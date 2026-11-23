using System.Reflection.PortableExecutable;
using SteamKit2;

namespace ColdClientManager;

public sealed record SteamLaunchOption(string Executable, string Directory, string Arguments);

public static class SteamGameImport
{
    public static GameProfile ReadInstall(string folder)
    {
        folder = SyncEngine.Normalize(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("找不到游戏安装目录：" + folder);
        SyncEngine.RejectLinks(folder);
        var profile = new GameProfile { Name = Path.GetFileName(folder), WorkingDirectory = folder,
            UseColdClient = true, SyncAchievements = true, ReportSteamPlaytime = true, RecordGame = true };
        ColdClient.ReadSteamManifest(profile, folder);
        if (!uint.TryParse(profile.AppId, out uint id) || id == 0)
            throw new InvalidDataException("找不到对应的 Steam 安装记录。请选择 steamapps\\common 下的游戏目录，并保留其 appmanifest 清单。");
        return profile;
    }

    public static GameProfile? AlreadySaved(IEnumerable<GameProfile> profiles, GameProfile draft, string folder) =>
        profiles.FirstOrDefault(p => p.AppId == draft.AppId && p.SteamId == draft.SteamId
            && p.Executable.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    public static void Save(string dataRoot, List<GameProfile> profiles, GameProfile draft)
    {
        int index = profiles.FindIndex(p => p.Id == draft.Id);
        var updated = profiles.ToList();
        if (index < 0) updated.Add(draft); else updated[index] = draft;
        Storage.Write(Path.Combine(dataRoot, "profiles.json"), updated);
        profiles.Clear(); profiles.AddRange(updated);
    }

    public static bool NeedsPortableObs(GameProfile profile, string dataRoot)
    {
        string obs = ObsRecorder.DefaultObsDirectory(dataRoot);
        return profile.RecordGame && profile.ObsExecutable.Length == 0
            && (!File.Exists(Path.Combine(obs, "bin", "64bit", "obs64.exe")) || !File.Exists(Path.Combine(obs, "coldclientmanager-obs.txt")));
    }

    public static List<SteamLaunchOption> FindLaunchOptions(string folder, KeyValue? app)
    {
        var options = new List<SteamLaunchOption>();
        void Add(string path, string working, string arguments)
        {
            if (!options.Any(o => o.Executable.Equals(path, StringComparison.OrdinalIgnoreCase)
                && o.Directory.Equals(working, StringComparison.OrdinalIgnoreCase) && o.Arguments == arguments))
                options.Add(new(path, working, arguments));
        }
        foreach (var entry in app?["config"]["launch"].Children ?? [])
        {
            string[] platforms = (entry["config"]["oslist"].Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (platforms.Length > 0 && !platforms.Contains("windows", StringComparer.OrdinalIgnoreCase)) continue;
            string executable = entry["executable"].Value ?? "";
            if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            string path = SteamCloudSync.Under(folder, executable), working = SteamCloudSync.Under(folder, entry["workingdir"].Value ?? "");
            if (File.Exists(path) && Directory.Exists(working))
            {
                SyncEngine.RejectLinks(path); SyncEngine.RejectLinks(working);
                Add(path, working, entry["arguments"].Value ?? "");
            }
        }
        if (options.Count > 0)
        {
            // Unreal can publish a root bootstrapper while the packaged game owns the window and audio.
            var bootstrappers = options.Where(o => Path.GetDirectoryName(o.Executable)!.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bootstrappers.Length > 0)
                foreach (var project in Directory.EnumerateDirectories(folder).Where(d => !Path.GetFileName(d).Equals("Engine", StringComparison.OrdinalIgnoreCase)))
                    foreach (string platform in new[] { "Win64", "Win32" })
                    {
                        string binaries = Path.Combine(project, "Binaries", platform);
                        if (!Directory.Exists(binaries)) continue;
                        SyncEngine.RejectLinks(binaries);
                        foreach (string game in Directory.EnumerateFiles(binaries, "*-" + platform + "-Shipping.exe"))
                        {
                            SyncEngine.RejectLinks(game);
                            foreach (var entry in bootstrappers) Add(game, entry.Directory, entry.Arguments);
                        }
                    }
            return options;
        }

        var scan = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string path in Directory.EnumerateFiles(folder, "*.exe", scan).Order(StringComparer.OrdinalIgnoreCase))
        {
            var parts = Path.GetRelativePath(folder, path).Split(Path.DirectorySeparatorChar);
            if (parts.Any(s => s.Equals("Engine", StringComparison.OrdinalIgnoreCase) || s.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase)
                || s.Equals("redist", StringComparison.OrdinalIgnoreCase))) continue;
            string name = parts[^1];
            if (name.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CrashReportClient", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) continue;
            Add(path, folder, "");
        }
        return options;
    }

    static bool Is64Bit(string executable)
    {
        using var stream = File.OpenRead(executable);
        using var image = new PEReader(stream);
        var headers = image.PEHeaders;
        if (headers.CoffHeader.Machine == Machine.Amd64) return true;
        if (headers.CoffHeader.Machine != Machine.I386) throw new InvalidDataException("请选择 Windows x86 或 x64 游戏和启动器。");
        return headers.CorHeader is { } clr && clr.Flags.HasFlag(CorFlags.ILOnly)
            && (clr.Flags & (CorFlags.Requires32Bit | CorFlags.Prefers32Bit)) == 0;
    }

    public static void SetLaunch(GameProfile profile, SteamLaunchOption launch, IReadOnlyList<GameProfile> previous, string dataRoot)
    {
        profile.Executable = launch.Executable; profile.WorkingDirectory = launch.Directory; profile.Arguments = launch.Arguments;
        bool x64 = Is64Bit(profile.Executable);
        foreach (string source in previous.Where(p => p.UseColdClient).Select(p => p.LoaderSource).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string folder = Path.GetDirectoryName(source)!;
            // Source folders can contain both the x86 and x64 loaders.
            var available = new[] { source }.Concat(Directory.EnumerateFiles(folder, "steamclient_loader*.exe")).Distinct(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(Path.Combine(folder, "steamclient.dll")) || !File.Exists(Path.Combine(folder, "steamclient64.dll"))) continue;
            var match = available.FirstOrDefault(p => Is64Bit(p) == x64);
            if (match != null) { profile.LoaderSource = match; break; }
        }
        var recording = previous.FirstOrDefault(p => p.RecordGame);
        profile.RecordingDirectory = recording?.RecordingDirectory ?? "";
        profile.RecordingFormat = recording?.RecordingFormat ?? "mkv";
        if (File.Exists(recording?.ObsExecutable)) profile.ObsExecutable = recording.ObsExecutable;
        else if (!File.Exists(Path.Combine(ObsRecorder.DefaultObsDirectory(dataRoot), "coldclientmanager-obs.txt")))
        {
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit", "obs64.exe");
            if (File.Exists(installed)) profile.ObsExecutable = installed;
        }
    }

    public static void ValidateLaunch(GameProfile profile)
    {
        if (!File.Exists(profile.Executable) || !Directory.Exists(profile.WorkingDirectory))
            throw new IOException("请选择有效的游戏启动程序和运行目录。");
        if (profile.SyncAchievements && !profile.UseColdClient)
            throw new InvalidDataException("同步成就需要 ColdClientLoader。直接启动时请关闭自动同步成就。");
        if (profile.UseColdClient || profile.ReportSteamPlaytime || profile.UseSteamCloud)
        {
            if (!ulong.TryParse(profile.SteamId, out ulong account) || account == 0) throw new InvalidDataException("请填写存档账号 ID。");
            SteamSession.ValidateProfile(profile, account);
        }
        if (profile.UseColdClient)
        {
            if (!File.Exists(profile.LoaderSource)) throw new IOException("请在“启动器程序”中选择现有的 ColdClientLoader。");
            if (Is64Bit(profile.LoaderSource) != Is64Bit(profile.Executable)) throw new InvalidDataException("请选择与游戏位数相同的 ColdClientLoader。");
            string folder = Path.GetDirectoryName(profile.LoaderSource)!;
            if (!File.Exists(Path.Combine(folder, "steamclient.dll")) || !File.Exists(Path.Combine(folder, "steamclient64.dll")))
                throw new IOException("启动器目录需要包含 steamclient.dll 和 steamclient64.dll。");
        }
        if (profile.RecordGame && profile.ObsExecutable.Length > 0 && !File.Exists(profile.ObsExecutable))
            throw new FileNotFoundException("找不到选定的 OBS 程序。", profile.ObsExecutable);
    }

    static string? RelativeChild(string root, string path)
    {
        string part = Path.GetRelativePath(root, path);
        return part == "." || part == ".." || part.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(part) ? null : part;
    }

    public static string? PreviousTargetUser(IEnumerable<GameProfile> profiles, string userDirectory, string dataRoot)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in profiles.SelectMany(p => p.Rules))
        {
            if (!Path.IsPathFullyQualified(rule.LocalPath) || !Path.IsPathFullyQualified(rule.SharedPath)) continue;
            string? relative = RelativeChild(userDirectory, SyncEngine.Normalize(rule.LocalPath));
            if (relative == null) continue;
            string shared = SyncEngine.Normalize(rule.SharedPath);
            if (RelativeChild(dataRoot, shared) != null) continue;
            string suffix = Path.DirectorySeparatorChar + relative;
            if (!shared.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string root = Path.TrimEndingDirectorySeparator(shared[..^relative.Length]);
            if (!root.Equals(userDirectory, StringComparison.OrdinalIgnoreCase)) targets.Add(root);
        }
        return targets.Count == 1 ? targets.Single() : null;
    }

    public static SyncRule MakeRule(GameProfile profile, string dataRoot, string? targetUser, string local, string patterns)
    {
        var rule = new SyncRule { LocalPath = local, Patterns = patterns };
        string? relative = RelativeChild(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), local);
        if (targetUser != null && relative != null && RelativeChild(dataRoot, local) == null)
            rule.SharedPath = Path.Combine(targetUser, relative);
        else rule.SharedPath = Path.Combine(Storage.ProfileFolder(dataRoot, profile), "save-copy", rule.Id, Path.GetFileName(local));
        return rule;
    }

    public static List<SyncRule> KnownRules(GameProfile profile, KeyValue ufs, string dataRoot, string? targetUser)
    {
        if (ufs["savefiles"].Children.Count == 0 && (ufs["quota"].AsUnsignedLong() == 0 || ufs["maxnumfiles"].AsUnsignedInteger() == 0)) return [];
        var rules = new List<SyncRule>();
        foreach (var group in SteamCloudSync.ReadLocations(ufs, profile, dataRoot).GroupBy(l => l.LocalPath, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Select(l => l.Recursive).Distinct().Count() != 1 || group.Any(l => l.Pattern.Contains(';')))
                throw new InvalidDataException("该存档目录的筛选条件无法用一条目录规则表示，可使用 Steam 云存档或启动游戏查找。");
            var patterns = group.Select(l => l.Pattern).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!group.First().Recursive && patterns.All(p => p.IndexOfAny(['*', '?']) < 0))
            {
                foreach (var name in patterns)
                {
                    var file = MakeRule(profile, dataRoot, targetUser, SteamCloudSync.Under(group.Key, name), "*");
                    file.SingleFile = true; rules.Add(file);
                }
            }
            else
            {
                if (group.Any(l => l.CloudPrefix.StartsWith('%') && l.CloudPrefix.EndsWith('%') && l.Pattern is "*" or "*.*"))
                    throw new InvalidDataException("Steam 给出的规则会包含整个根目录，请通过存档查找确认该游戏的文件。");
                var rule = MakeRule(profile, dataRoot, targetUser, group.Key, string.Join(';', patterns));
                rule.Recursive = group.First().Recursive; rule.SkipSteamCloudMarker = true; rules.Add(rule);
            }
        }
        SyncEngine.Validate(new GameProfile { Id = profile.Id, Rules = rules }, checkLinks: false);
        return rules;
    }
}
