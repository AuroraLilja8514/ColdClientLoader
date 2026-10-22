using System.IO.Enumeration;
using System.Security.Cryptography;

namespace ColdClientManager;

public enum SyncDirection { Both, TargetToLocal, LocalToTarget }

public sealed class SyncItem
{
    public string RuleId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string? LocalHash { get; set; }
    public string? SharedHash { get; set; }
    // Equal, Local, Shared, Conflict. Conflicts require an explicit choice.
    public string Action { get; set; } = "";
}

public sealed class SyncPlan
{
    public SyncDirection Direction { get; set; }
    public List<SyncItem> Items { get; set; } = [];
    public int Changes => Items.Count(i => i.Action != "Equal");
}

public static class SyncEngine
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)));
    static bool ContainsPath(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void Validate(GameProfile profile)
    {
        if (!Guid.TryParseExact(profile.Id, "N", out _) || profile.Rules.Any(r => !Guid.TryParseExact(r.Id, "N", out _))
            || profile.Rules.Select(r => r.Id).Distinct().Count() != profile.Rules.Count)
            throw new InvalidDataException("无效的游戏或同步规则标识。");
        var paths = new List<string>();
        foreach (var rule in profile.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.LocalPath) || string.IsNullOrWhiteSpace(rule.SharedPath))
                throw new InvalidDataException("请填写本机路径和对应的同步路径。");
            if (!rule.SingleFile && (string.IsNullOrWhiteSpace(rule.Patterns)
                || rule.Patterns.Split(';').Any(p => p.Trim().Length == 0 || p.Contains('/') || p.Contains('\\') || p.Contains(':'))))
                throw new InvalidDataException("文件筛选应为文件名，例如 *.sav;*.dat，不要包含目录。");
            foreach (string input in new[] { rule.LocalPath, rule.SharedPath })
            {
                string path = Normalize(input);
                var broad = new[] { Path.GetPathRoot(path)!, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
                if (broad.Any(p => p.Length > 0 && path.Equals(Normalize(p), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("请选择具体游戏的存档路径，不能同步整个磁盘、用户目录或系统目录。");
                if (paths.Any(p => ContainsPath(p, path) || ContainsPath(path, p)))
                    throw new InvalidDataException("同步路径不能相同、互相包含或与其他规则重叠。");
                RejectLinks(path);
                paths.Add(path);
            }
        }
    }

    static void RejectLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("同步路径中包含链接或联接点，请选择它指向的实际存档目录：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException)
            {
                if (!Directory.Exists(Path.GetPathRoot(path))) throw new IOException("磁盘不可用：" + path);
            }
        }
    }

    static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static Dictionary<string, string> Snapshot(SyncRule rule, string input)
    {
        string root = Normalize(input);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(root); }
        catch (FileNotFoundException) { return result; }
        catch (DirectoryNotFoundException)
        {
            if (!Directory.Exists(Path.GetPathRoot(root))) throw new IOException("磁盘不可用：" + root);
            return result;
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不能同步链接：" + root);
        if (rule.SingleFile)
        {
            if ((attributes & FileAttributes.Directory) != 0) throw new IOException("单文件规则指向了文件夹：" + root);
            result[""] = Hash(root);
            return result;
        }
        if ((attributes & FileAttributes.Directory) == 0) throw new IOException("目录规则指向了文件：" + root);
        string[] patterns = rule.Patterns.Split(';', StringSplitOptions.TrimEntries);
        void Walk(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("存档目录包含链接，请改为更精确的规则：" + path);
                if ((attr & FileAttributes.Directory) != 0) Walk(path);
                else if (patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, Path.GetFileName(path), true)))
                    result[Path.GetRelativePath(root, path)] = Hash(path);
            }
        }
        Walk(root);
        return result;
    }

    static string StatePath(GameProfile profile, string dataRoot) => Path.Combine(Storage.ProfileFolder(dataRoot, profile), "baseline.json");
    static string StateKey(SyncRule rule, string relative) => rule.Id + "|" + Normalize(rule.LocalPath).ToUpperInvariant()
        + "|" + Normalize(rule.SharedPath).ToUpperInvariant() + "|" + rule.SingleFile + "|" + rule.Patterns.ToUpperInvariant() + "|" + relative;
    public static SyncPlan Plan(GameProfile profile, string dataRoot, SyncDirection direction = SyncDirection.Both)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        Validate(profile);
        string statePath = StatePath(profile, dataRoot);
        var baseline = File.Exists(statePath) ? new Dictionary<string, string>(Storage.Read<Dictionary<string, string>>(statePath), StringComparer.OrdinalIgnoreCase) : [];
        var plan = new SyncPlan { Direction = direction };
        foreach (var rule in profile.Rules)
        {
            var local = Snapshot(rule, rule.LocalPath);
            var shared = Snapshot(rule, rule.SharedPath);
            IEnumerable<string> paths = direction switch {
                SyncDirection.TargetToLocal => shared.Keys,
                SyncDirection.LocalToTarget => local.Keys,
                _ => local.Keys.Union(shared.Keys, StringComparer.OrdinalIgnoreCase)
            };
            foreach (string relative in paths.Order(StringComparer.OrdinalIgnoreCase))
            {
                local.TryGetValue(relative, out string? l);
                shared.TryGetValue(relative, out string? s);
                baseline.TryGetValue(StateKey(rule, relative), out string? previous);
                string action = l == s ? "Equal" : direction == SyncDirection.TargetToLocal ? "Shared"
                    : direction == SyncDirection.LocalToTarget ? "Local" : l == null ? "Shared" : s == null ? "Local"
                    : l == previous ? "Shared" : s == previous ? "Local" : "Conflict";
                plan.Items.Add(new SyncItem { RuleId = rule.Id, RelativePath = relative, LocalHash = l, SharedHash = s, Action = action });
            }
        }
        return plan;
    }

    static string ItemPath(SyncRule rule, string root, string relative)
    {
        root = Normalize(root);
        if (rule.SingleFile)
        {
            if (relative.Length != 0) throw new InvalidDataException("无效的单文件同步项。");
            return root;
        }
        string full = Normalize(Path.Combine(root, relative));
        if (relative.Length == 0 || !ContainsPath(root, full) || full == root) throw new InvalidDataException("同步项超出选定目录。");
        return full;
    }

    public static string Apply(GameProfile profile, string dataRoot, SyncPlan plan)
    {
        // Rebuild from the filesystem: a saved/elevated request must not authorize stale or arbitrary paths.
        var current = Plan(profile, dataRoot, plan.Direction);
        if (current.Items.Count != plan.Items.Count || current.Items.Where((item, index) =>
            item.RuleId != plan.Items[index].RuleId || item.RelativePath != plan.Items[index].RelativePath
            || item.LocalHash != plan.Items[index].LocalHash || item.SharedHash != plan.Items[index].SharedHash).Any())
            throw new IOException("存档在预览后发生了变化，请重新同步。");
        for (int i = 0; i < current.Items.Count; i++)
        {
            var action = plan.Items[i].Action;
            if (current.Items[i].Action == "Conflict" ? action is not ("Local" or "Shared") : action != current.Items[i].Action)
                throw new InvalidDataException("请先解决所有存档冲突。");
        }
        string backupRoot = Path.Combine(Storage.ProfileFolder(dataRoot, profile), "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var item in plan.Items.Where(i => i.Action != "Equal"))
        {
            var rule = profile.Rules.Single(r => r.Id == item.RuleId);
            bool push = item.Action == "Local";
            string source = ItemPath(rule, push ? rule.LocalPath : rule.SharedPath, item.RelativePath);
            string target = ItemPath(rule, push ? rule.SharedPath : rule.LocalPath, item.RelativePath);
            string expected = (push ? item.LocalHash : item.SharedHash)!;
            string? targetExpected = push ? item.SharedHash : item.LocalHash;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".ccm-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                if (Hash(temporary) != expected) throw new IOException("复制期间存档发生变化：" + source);
                if (targetExpected != null)
                {
                    if (Hash(target) != targetExpected) throw new IOException("目标存档发生变化：" + target);
                    string backup = Path.Combine(backupRoot, rule.Id, push ? "shared" : "local", rule.SingleFile ? Path.GetFileName(target) : item.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    // ReplaceFile requires its backup on the same volume. App data and the other
                    // system's saves commonly live on different drives, so copy and verify first.
                    File.Copy(target, backup, false);
                    if (Hash(backup) != targetExpected || Hash(target) != targetExpected)
                        throw new IOException("备份期间目标存档发生变化：" + target);
                    File.Replace(temporary, target, null);
                }
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var next = Plan(profile, dataRoot, plan.Direction);
        if (next.Items.Any(i => i.Action != "Equal")) throw new IOException("同步期间文件再次变化；已保留备份，请重新同步。");
        Storage.Write(StatePath(profile, dataRoot), next.Items.ToDictionary(i => StateKey(profile.Rules.Single(r => r.Id == i.RuleId), i.RelativePath), i => i.LocalHash!, StringComparer.OrdinalIgnoreCase));
        return backupRoot;
    }
}
