using System.IO.Compression;
using System.IO.Enumeration;
using System.Net.Http;
using System.Security.Cryptography;
using SteamKit2;
using SteamKit2.Internal;

namespace ColdClientManager;

public sealed record CloudSaveLocation(string LocalPath, string CloudPrefix, string Pattern, bool Recursive, uint Platforms);
public sealed record CloudSaveFile(string Name, string LocalPath, CCloud_AppFileInfo? Remote, uint Platforms);

public sealed class SteamCloudSync : IDisposable
{
    readonly Cloud service;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    readonly uint appId;
    readonly string dataRoot;
    readonly ulong quota;
    readonly uint maxFiles;
    readonly CCloud_GetAppFileChangelist_Response remote;
    readonly Dictionary<string, CloudSaveFile> files = new(StringComparer.OrdinalIgnoreCase);
    public GameProfile Profile { get; }
    public SyncPlan Plan { get; private set; } = new();
    public int Downloads { get; private set; }
    public IReadOnlyDictionary<string, CloudSaveFile> Files => files;

    SteamCloudSync(GameProfile profile, string dataRoot, Cloud service, KeyValue ufs, CCloud_GetAppFileChangelist_Response remote)
    {
        this.service = service; this.dataRoot = dataRoot; this.remote = remote;
        appId = uint.Parse(profile.AppId); quota = ufs["quota"].AsUnsignedLong(); maxFiles = ufs["maxnumfiles"].AsUnsignedInteger();
        Profile = new GameProfile { Id = profile.Id, Name = profile.Name, AppId = profile.AppId, SteamId = profile.SteamId, UseSteamCloud = true };
    }

    static async Task<T> Response<T>(AsyncJob<SteamUnifiedMessages.ServiceMethodResponse<T>> job) where T : class, ProtoBuf.IExtensible, new()
    {
        var response = await job.ToTask().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (response.Result != EResult.OK) throw SteamConnection.Failure(response.Result, $"Steam 云存档请求失败：{response.Result}。");
        return response.Body;
    }

    public static Task<SteamCloudSync> CreateAsync(GameProfile profile, string dataRoot, SteamSession steam) =>
        steam.RunConnectedAsync(connection => CreateAsync(profile, dataRoot, connection));

    internal static async Task<SteamCloudSync> CreateAsync(GameProfile profile, string dataRoot, SteamConnection steam)
    {
        uint appId = SteamSession.ValidateProfile(profile, steam.SteamId);
        var ufs = (await steam.GetAppInfoAsync(appId).ConfigureAwait(false))["ufs"];
        if (ufs["quota"].AsUnsignedLong() == 0 || ufs["maxnumfiles"].AsUnsignedInteger() == 0)
            throw new InvalidOperationException("该游戏没有可用的 Steam 云存档配置，请使用目录同步。");
        var service = steam.Client.GetHandler<SteamUnifiedMessages>()!.CreateService<Cloud>();
        var remote = await Response(service.GetAppFileChangelist(new() { appid = appId, synced_change_number = 0 })).ConfigureAwait(false);
        if (remote.is_only_delta) throw new IOException("Steam 没有返回完整云文件列表，已停止同步。");
        var sync = new SteamCloudSync(profile, dataRoot, service, ufs, remote);
        try
        {
            var locations = ReadLocations(ufs, profile, dataRoot);
            var selected = SelectFiles(locations, remote);
            string cache = Path.Combine(Storage.ProfileFolder(dataRoot, profile), "cloud-cache");
            foreach (var file in selected.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                string id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file.Name.ToUpperInvariant())))[..32].ToLowerInvariant();
                var rule = new SyncRule { Id = id, LocalPath = file.LocalPath, SharedPath = Path.Combine(cache, id, "file"), SingleFile = true };
                sync.files.Add(id, file); sync.Profile.Rules.Add(rule);
            }
            SyncEngine.Validate(sync.Profile);
            foreach (var rule in sync.Profile.Rules)
            {
                var file = sync.files[rule.Id];
                // The cache is a snapshot of Steam, never evidence that an earlier upload succeeded.
                if (File.Exists(rule.SharedPath)) File.Delete(rule.SharedPath);
                if (file.Remote != null)
                {
                    await sync.DownloadAsync(file, rule.SharedPath).ConfigureAwait(false);
                    sync.Downloads++;
                }
            }
            sync.Plan = SyncEngine.Plan(sync.Profile, dataRoot);
            return sync;
        }
        catch { sync.Dispose(); throw; }
    }

    public static string Under(string root, string relative)
    {
        relative = relative.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Steam 提供的相对路径无效或超出所选目录：" + relative);
        root = SyncEngine.Normalize(root);
        string path = SyncEngine.Normalize(Path.Combine(root, relative));
        if (path != root && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Steam 提供的路径超出所选目录。");
        return path;
    }

    static string CanonicalRoot(string? root) => root?.ToLowerInvariant() switch {
        "gameinstall" => "GameInstall", "steamuserdata" => "SteamUserData", "winmydocuments" => "WinMyDocuments",
        "winappdatalocal" => "WinAppDataLocal", "winappdatalocallow" => "WinAppDataLocalLow", "winappdataroaming" => "WinAppDataRoaming",
        "winsavedgames" => "WinSavedGames", "winprogramdata" => "WinProgramData", _ => throw new InvalidDataException("尚不支持该 Steam 云存档根路径：" + root)
    };

    internal static string InstallDirectory(GameProfile profile)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(SyncEngine.Normalize(profile.Executable))!); directory.Parent != null; directory = directory.Parent)
            if (directory.Parent.Name.Equals("common", StringComparison.OrdinalIgnoreCase) && directory.Parent.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true)
                return directory.FullName;
        return SyncEngine.Normalize(string.IsNullOrWhiteSpace(profile.WorkingDirectory) ? Path.GetDirectoryName(profile.Executable)! : profile.WorkingDirectory);
    }

    public static List<CloudSaveLocation> ReadLocations(KeyValue ufs, GameProfile profile, string dataRoot)
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string remote = Path.Combine(AchievementSync.LoaderDirectory(profile, dataRoot), "emu-data", profile.AppId, "remote");
        string Root(string root) => root switch {
            "GameInstall" => InstallDirectory(profile), "SteamUserData" when profile.UseColdClient => remote,
            "WinMyDocuments" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WinAppDataLocal" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinAppDataLocalLow" => Path.Combine(user, "AppData", "LocalLow"),
            "WinAppDataRoaming" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinSavedGames" => SavedGamesDirectory(),
            "WinProgramData" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            _ => throw new InvalidOperationException("Steam API 云存档需要使用 ColdClientLoader 启动。")
        };
        string Expand(string? value) => (value ?? "").Replace("{64BitSteamID}", profile.SteamId)
            .Replace("{Steam3AccountID}", ((uint)ulong.Parse(profile.SteamId)).ToString()).Replace('\\', '/');
        var result = new List<CloudSaveLocation>();
        foreach (var rule in ufs["savefiles"].Children)
        {
            var platforms = rule["platforms"].Children;
            if (platforms.Count > 0 && !platforms.Any(p => p.Value?.Equals("Windows", StringComparison.OrdinalIgnoreCase) == true || p.Value?.Equals("all", StringComparison.OrdinalIgnoreCase) == true)) continue;
            string root = CanonicalRoot(rule["root"].AsString()), localRoot = root;
            string path = Expand(rule["path"].AsString()).Trim('/');
            if (path == ".") path = "";
            string addPath = "";
            foreach (var replacement in ufs["rootoverrides"].Children)
            {
                bool windows = string.Equals(replacement["os"].AsString(), "Windows", StringComparison.OrdinalIgnoreCase);
                bool applies = replacement["oscompare"].AsString() == "!=" ? !windows : windows;
                if (!applies || !string.Equals(replacement["root"].AsString(), root, StringComparison.OrdinalIgnoreCase)) continue;
                localRoot = CanonicalRoot(replacement["useinstead"].AsString()); addPath = Expand(replacement["addpath"].AsString());
            }
            string? pattern = rule["pattern"].AsString();
            if (string.IsNullOrEmpty(pattern) || pattern.IndexOfAny(['/', '\\', ':']) >= 0) throw new InvalidDataException("Steam 云存档文件筛选无效。");
            string prefix = root == "SteamUserData" ? path : "%" + root + "%" + path;
            result.Add(new(Under(Under(Root(localRoot), addPath), path), prefix, pattern, rule["recursive"].AsBoolean(), platforms.Count == 0 || platforms.Any(p => p.Value?.Equals("all", StringComparison.OrdinalIgnoreCase) == true) ? uint.MaxValue : 1));
        }
        if (ufs["savefiles"].Children.Count == 0)
        {
            if (!profile.UseColdClient) throw new InvalidOperationException("该游戏使用 Steam API 云存档，请选择 ColdClientLoader 启动方式。");
            result.Add(new(remote, "", "*", true, uint.MaxValue));
        }
        if (result.Count == 0) throw new InvalidOperationException("该游戏没有适用于 Windows 的 Steam 云存档规则。");
        return result;
    }

    static string SavedGamesDirectory()
    {
        var id = new Guid("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
        int result = SHGetKnownFolderPath(id, 0x4000 /* KF_FLAG_DONT_VERIFY: the first download may create this folder */, IntPtr.Zero, out IntPtr path);
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(path)!; }
        finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(path); }
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static string FileName(CCloud_AppFileInfo file, CCloud_GetAppFileChangelist_Response list)
    {
        if (!file.ShouldSerializepath_prefix_index()) return file.file_name.Replace('\\', '/');
        if (file.path_prefix_index >= list.path_prefixes.Count) throw new InvalidDataException("Steam 云文件路径索引无效。");
        string prefix = list.path_prefixes[(int)file.path_prefix_index].Replace('\\', '/');
        return prefix.TrimEnd('/') + (prefix.Length == 0 || prefix.EndsWith('%') ? "" : "/") + file.file_name.Replace('\\', '/');
    }

    public static List<CloudSaveFile> SelectFiles(List<CloudSaveLocation> locations, CCloud_GetAppFileChangelist_Response remote)
    {
        var selected = new Dictionary<string, CloudSaveFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in remote.files.Where(f => f.persist_state == ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted && (f.platforms_to_sync & 1) != 0))
        {
            string name = FileName(file, remote);
            foreach (var location in locations.OrderByDescending(l => l.CloudPrefix.Length))
            {
                string prefix = location.CloudPrefix.Length == 0 || location.CloudPrefix.EndsWith('%') ? location.CloudPrefix : location.CloudPrefix + "/";
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = name[prefix.Length..];
                string local = Under(location.LocalPath, relative);
                if (local == SyncEngine.Normalize(location.LocalPath) || (!location.Recursive && relative.Contains('/')) || !FileSystemName.MatchesSimpleExpression(location.Pattern, Path.GetFileName(local), true)) continue;
                if (!selected.TryAdd(local, new(name, local, file, location.Platforms))) throw new InvalidDataException("多个 Steam 云文件映射到了同一个本地存档。");
                break;
            }
        }
        foreach (var location in locations)
        {
            if (!Directory.Exists(location.LocalPath)) continue;
            void Walk(string directory)
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("云存档路径包含链接：" + directory);
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attr = File.GetAttributes(path);
                    if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("云存档路径包含链接：" + path);
                    if ((attr & FileAttributes.Directory) != 0) { if (location.Recursive) Walk(path); }
                    else if (FileSystemName.MatchesSimpleExpression(location.Pattern, Path.GetFileName(path), true) && !Path.GetFileName(path).Equals("steam_autocloud.vdf", StringComparison.OrdinalIgnoreCase))
                    {
                        string relative = Path.GetRelativePath(location.LocalPath, path).Replace('\\', '/');
                        string name = location.CloudPrefix + (location.CloudPrefix.Length == 0 || location.CloudPrefix.EndsWith('%') ? "" : "/") + relative;
                        selected.TryAdd(path, new(name, path, null, location.Platforms));
                    }
                }
            }
            Walk(location.LocalPath);
        }
        return selected.Values.ToList();
    }

    public static void ReadDownload(Stream input, string destination, CCloud_ClientFileDownload_Response info)
    {
        if (info.encrypted || info.is_explicit_delete) throw new InvalidDataException("Steam 云文件已删除或使用了不支持的加密格式。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temporary))
            {
                if (info.file_size != info.raw_file_size)
                {
                    using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
                    if (zip.Entries.Count != 1) throw new InvalidDataException("Steam 云文件压缩包必须只包含一个文件。");
                    using var contents = zip.Entries[0].Open(); contents.CopyTo(output);
                }
                else input.CopyTo(output);
                output.Flush(true);
            }
            using (var file = File.OpenRead(temporary))
                if (file.Length != info.raw_file_size || !SHA1.HashData(file).AsSpan().SequenceEqual(info.sha_file)) throw new InvalidDataException("Steam 云文件长度或 SHA-1 校验失败。");
            File.Move(temporary, destination, true);
            File.SetLastWriteTimeUtc(destination, DateTimeOffset.FromUnixTimeSeconds(checked((long)info.time_stamp)).UtcDateTime);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static Uri Url(bool https, string host, string path) => new((https ? "https://" : "http://") + host + path);
    static void Headers(HttpRequestMessage request, IEnumerable<(string Name, string Value)> headers)
    {
        foreach (var header in headers)
            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                if (request.Content == null || !request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value))
                    throw new InvalidDataException("Steam 云文件请求头无效：" + header.Name);
            }
    }

    async Task DownloadAsync(CloudSaveFile file, string destination)
    {
        var info = await Response(service.ClientFileDownload(new() { appid = appId, filename = file.Name })).ConfigureAwait(false);
        if (!info.sha_file.AsSpan().SequenceEqual(file.Remote!.sha_file)) throw new IOException("Steam 云存档在读取期间发生变化，请重新同步。");
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(info.use_https, info.url_host, info.url_path));
        Headers(request, info.request_headers.Select(h => (h.name, h.value)));
        using var response = await http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        ReadDownload(input, destination, info);
    }

    async Task UploadAsync(SyncRule rule, ulong batchId)
    {
        var file = files[rule.Id];
        using var input = new FileStream(rule.SharedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] sha = SHA1.HashData(input); input.Position = 0;
        var info = await Response(service.ClientBeginFileUpload(new() { appid = appId, filename = file.Name, file_size = checked((uint)input.Length),
            raw_file_size = checked((uint)input.Length), file_sha = sha, time_stamp = checked((ulong)new DateTimeOffset(File.GetLastWriteTimeUtc(rule.LocalPath)).ToUnixTimeSeconds()),
            platforms_to_sync = file.Remote?.platforms_to_sync ?? file.Platforms, upload_batch_id = batchId, can_encrypt = false })).ConfigureAwait(false);
        if (info.encrypt_file) throw new InvalidDataException("Steam 要求使用不支持的云文件加密方式。");
        foreach (var block in info.block_requests)
        {
            if (block.block_offset > (ulong)input.Length || block.block_length > (ulong)input.Length - block.block_offset)
                throw new InvalidDataException("Steam 云上传块超出存档长度。");
            byte[] bytes = new byte[checked((int)block.block_length)];
            input.Position = checked((long)block.block_offset); input.ReadExactly(bytes);
            using var request = new HttpRequestMessage(block.http_method switch { 3 => HttpMethod.Post, 4 => HttpMethod.Put, _ => throw new InvalidDataException("不支持的 Steam 云上传方法：" + block.http_method) },
                Url(block.use_https, block.url_host, block.url_path));
            request.Content = new ByteArrayContent(block.explicit_body_data is { Length: > 0 } ? block.explicit_body_data : bytes);
            Headers(request, block.request_headers.Select(h => (h.name, h.value)));
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        if (info.block_requests.Count > 0)
        {
            var committed = await Response(service.ClientCommitFileUpload(new() { appid = appId, filename = file.Name, file_sha = sha, transfer_succeeded = true })).ConfigureAwait(false);
            if (!committed.file_committed) throw new IOException("Steam 未确认云文件上传：" + file.Name);
        }
    }

    public async Task<string> ApplyAsync()
    {
        var current = await Response(service.GetAppFileChangelist(new() { appid = appId, synced_change_number = 0 })).ConfigureAwait(false);
        if (current.current_change_number != remote.current_change_number) throw new IOException("Steam 云存档在预览后发生变化，请重新同步。");
        var uploads = Plan.Items.Where(i => i.Action == "Local").Select(i => Profile.Rules.Single(r => r.Id == i.RuleId)).ToList();
        ulong size = (ulong)remote.files.Where(f => f.persist_state == ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted).Sum(f => (long)f.raw_file_size);
        foreach (var rule in uploads) size = checked(size - (files[rule.Id].Remote?.raw_file_size ?? 0) + (ulong)new FileInfo(rule.LocalPath).Length);
        if (size > quota || remote.files.Count(f => f.persist_state == ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted) + uploads.Count(r => files[r.Id].Remote == null) > maxFiles)
            throw new IOException("存档超过该游戏的 Steam 云容量或文件数量限制。");
        string backup = SyncEngine.Apply(Profile, dataRoot, Plan, false);
        if (uploads.Count > 0)
        {
            var request = new CCloud_BeginAppUploadBatch_Request { appid = appId, machine_name = "ColdClientManager" };
            request.files_to_upload.AddRange(uploads.Select(r => files[r.Id].Name));
            var batch = await Response(service.BeginAppUploadBatch(request)).ConfigureAwait(false);
            bool success = false;
            try
            {
                foreach (var rule in uploads) await UploadAsync(rule, batch.batch_id).ConfigureAwait(false);
                success = true;
            }
            finally
            {
                await Response(service.CompleteAppUploadBatchBlocking(new() { appid = appId, batch_id = batch.batch_id, batch_eresult = (uint)(success ? EResult.OK : EResult.Fail) })).ConfigureAwait(false);
            }
            var confirmed = await Response(service.GetAppFileChangelist(new() { appid = appId, synced_change_number = 0 })).ConfigureAwait(false);
            foreach (var rule in uploads)
            {
                var stored = confirmed.files.SingleOrDefault(f => FileName(f, confirmed) == files[rule.Id].Name && f.persist_state == ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted);
                using var local = File.OpenRead(rule.LocalPath);
                if (stored == null || !SHA1.HashData(local).AsSpan().SequenceEqual(stored.sha_file)) throw new IOException("Steam 云存档上传后的校验失败，请重新同步。");
            }
        }
        var equal = SyncEngine.Plan(Profile, dataRoot);
        if (equal.Changes != 0) throw new IOException("本机存档在云同步期间发生变化，请重新同步。");
        SyncEngine.Apply(Profile, dataRoot, equal);
        return backup;
    }

    public void Dispose() => http.Dispose();
}
