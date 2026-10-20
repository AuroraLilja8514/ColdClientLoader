using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ColdClientManager;

public static class ColdClient
{
    public static void ReadSteamManifest(GameProfile profile)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(profile.Executable)!);
        while (directory.Parent != null && !directory.Name.Equals("common", StringComparison.OrdinalIgnoreCase)) directory = directory.Parent;
        if (directory.Parent == null || !directory.Parent.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return;
        string gameFolder = Path.GetRelativePath(directory.FullName, profile.Executable).Split(Path.DirectorySeparatorChar)[0];
        foreach (string file in Directory.EnumerateFiles(directory.Parent.FullName, "appmanifest_*.acf"))
        {
            var fields = Regex.Matches(File.ReadAllText(file), "\"([^\"]+)\"\\s+\"([^\"]*)\"")
                .GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Groups[2].Value, StringComparer.OrdinalIgnoreCase);
            if (!fields.TryGetValue("installdir", out string? folder) || !folder.Equals(gameFolder, StringComparison.OrdinalIgnoreCase)) continue;
            if (fields.TryGetValue("appid", out string? appId)) profile.AppId = appId;
            if (fields.TryGetValue("LastOwner", out string? owner)) profile.SteamId = owner;
            if (fields.TryGetValue("name", out string? name)) profile.Name = name;
            break;
        }
    }

    public static string Prepare(GameProfile profile, string dataRoot)
    {
        if (!uint.TryParse(profile.AppId, out uint appId) || appId == 0 || !ulong.TryParse(profile.SteamId, out ulong steamId) || steamId == 0)
            throw new InvalidDataException("ColdClientLoader 模式需要有效的 AppID 和 SteamID（应与原存档使用的账号一致）。");
        if (new[] { profile.Executable, profile.WorkingDirectory, profile.Arguments }.Any(s => s.Contains('\r') || s.Contains('\n')))
            throw new InvalidDataException("启动路径和参数不能包含换行。");
        string source = SyncEngine.Normalize(profile.LoaderSource);
        if (!File.Exists(source)) throw new FileNotFoundException("请选择现有的 ColdClientLoader 程序。", source);
        string destination = Path.Combine(Storage.ProfileFolder(dataRoot, profile), "loader");
        if (SyncEngine.Normalize(Path.GetDirectoryName(source)!) == SyncEngine.Normalize(destination)) throw new IOException("请选择原始组件目录中的启动器。");
        Directory.CreateDirectory(destination);
        string loader = Path.Combine(destination, Path.GetFileName(source));
        File.Copy(source, loader, true);
        foreach (string name in new[] { "steamclient.dll", "steamclient64.dll", "GameOverlayRenderer.dll", "GameOverlayRenderer64.dll" })
        {
            string component = Path.Combine(Path.GetDirectoryName(source)!, name);
            if (name.StartsWith("GameOverlay") && !File.Exists(component)) continue;
            File.Copy(component, Path.Combine(destination, name), true);
        }
        File.WriteAllText(Path.Combine(destination, "ColdClientLoader.ini"), $"""
            [SteamClient]
            Exe={SyncEngine.Normalize(profile.Executable)}
            ExeRunDir={SyncEngine.Normalize(profile.WorkingDirectory)}
            ExeCommandLine={profile.Arguments}
            AppId={profile.AppId}
            SteamClientDll=steamclient.dll
            SteamClient64Dll=steamclient64.dll
            [Persistence]
            Mode=0
            """);
        Directory.CreateDirectory(Path.Combine(destination, "steam_settings"));
        File.WriteAllText(Path.Combine(destination, "steam_settings", "configs.user.ini"), $"""
            [user::general]
            account_steamid={profile.SteamId}
            [user::saves]
            local_save_path=./emu-data
            """);
        return loader;
    }
}

public sealed class LoaderRegistry : IDisposable
{
    public sealed record Value(string Key, string Name, bool Exists, RegistryValueKind Kind, JsonElement Data);
    static readonly (string Key, string[] Names)[] Targets = [
        (@"Software\Valve\Steam", ["SourceModInstallPath", "SteamPath", "SteamExe", "RunningAppID"]),
        (@"Software\Valve\Steam\ActiveProcess", ["pid", "SteamClientDll", "SteamClientDll64", "ActiveUser", "Universe"])
    ];
    readonly List<Value> values;
    readonly string recovery;

    public static void CheckOtherClients()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name = process.ProcessName;
                if (name.Equals("steam", StringComparison.OrdinalIgnoreCase) || name.StartsWith("steamclient_loader", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("请先退出正在运行的 Steam / ColdClientLoader，再使用此启动模式。");
            }
        }
    }

    public LoaderRegistry(string dataRoot)
    {
        CheckOtherClients();
        recovery = Path.Combine(dataRoot, "loader-registry-recovery.json");
        if (File.Exists(recovery)) throw new IOException("上次启动未完成注册表恢复。请先使用“恢复启动器环境”。");
        values = Capture();
        // Check the same access the loader needs before starting it (otherwise it opens a modal error dialog).
        foreach (var target in Targets) { using var writable = Registry.CurrentUser.CreateSubKey(target.Key); }
        Storage.Write(recovery, values);
    }
    public static List<Value> Capture()
    {
        var result = new List<Value>();
        foreach (var target in Targets)
        {
            using var key = Registry.CurrentUser.OpenSubKey(target.Key);
            foreach (string name in target.Names)
            {
                bool exists = key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
                result.Add(new(target.Key, name, exists, exists ? key!.GetValueKind(name) : RegistryValueKind.None,
                    JsonSerializer.SerializeToElement(exists ? key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null)));
            }
        }
        return result;
    }
    static void Restore(List<Value> values)
    {
        foreach (var target in values)
        {
            // The recovery file can only address this fixed set of Steam client values.
            if (!Targets.Any(t => t.Key == target.Key && t.Names.Contains(target.Name))) throw new InvalidDataException("无效的启动器恢复记录。");
            using (var existing = Registry.CurrentUser.OpenSubKey(target.Key))
            {
                bool exists = existing?.GetValueNames().Contains(target.Name, StringComparer.OrdinalIgnoreCase) == true;
                if (!exists && !target.Exists) continue;
                if (exists && target.Exists && existing!.GetValueKind(target.Name) == target.Kind
                    && JsonSerializer.SerializeToElement(existing.GetValue(target.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)).GetRawText() == target.Data.GetRawText()) continue;
            }
            using var key = Registry.CurrentUser.CreateSubKey(target.Key);
            if (!target.Exists) { key.DeleteValue(target.Name, false); continue; }
            object data = target.Kind switch {
                RegistryValueKind.String or RegistryValueKind.ExpandString => target.Data.GetString()!,
                RegistryValueKind.DWord => target.Data.GetInt32(), RegistryValueKind.QWord => target.Data.GetInt64(),
                RegistryValueKind.MultiString => target.Data.Deserialize<string[]>()!,
                RegistryValueKind.Binary or RegistryValueKind.None => target.Data.GetBytesFromBase64(),
                _ => throw new InvalidDataException("不支持的注册表值类型。")
            };
            key.SetValue(target.Name, data, target.Kind);
        }
    }
    public static void Recover(string dataRoot)
    {
        CheckOtherClients();
        string path = Path.Combine(dataRoot, "loader-registry-recovery.json");
        Restore(Storage.Read<List<Value>>(path));
        File.Delete(path);
    }
    public void Dispose()
    {
        CheckOtherClients();
        Restore(values);
        File.Delete(recovery);
    }
}
