using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ColdClientManager;

public sealed record FileActivity(string Path, int ProcessId, bool Created, bool Written, bool Directory);
public sealed class DiscoveryResult
{
    public List<FileActivity> Files { get; set; } = [];
    public List<string> Processes { get; set; } = [];
    public int EventsLost { get; set; }
    public int IncompleteOperations { get; set; }
    public int UnknownProcessOperations { get; set; }
}
public sealed record SaveCandidate(string Path, string Reason, int FileCount, string Examples, string Patterns);

public static class CandidateFinder
{
    static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase) {
        "cache", "caches", "gpucache", "shadercache", "shader_cache", "dxcache", "glcache", "d3dscache", "logs", "crashdumps", "temp", "tmp"
    };
    static readonly HashSet<string> SaveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".sav", ".save", ".savegame", ".dat", ".slot", ".profile" };
    public static List<SaveCandidate> Find(DiscoveryResult result)
    {
        var files = result.Files.Where(f => !f.Directory && (f.Created || f.Written) && Path.IsPathFullyQualified(f.Path))
            .Where(f => !f.Path.Split('\\', '/').Any(segment => Noise.Contains(segment)))
            .Where(f => !new[] { ".dll", ".exe", ".log", ".etl", ".pdb", ".dmp" }.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase))
            .GroupBy(f => Path.GetDirectoryName(f.Path)!, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(int Rank, SaveCandidate Candidate)>();
        foreach (var group in files)
        {
            var paths = group.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
            bool saveName = group.Key.Split('\\', '/').Any(s => s.Contains("save", StringComparison.OrdinalIgnoreCase) || s.Equals("remote", StringComparison.OrdinalIgnoreCase));
            bool saveExtension = paths.Any(p => SaveExtensions.Contains(Path.GetExtension(p)));
            string reason = saveName ? "路径含存档标记；游戏进程树实际写入" : saveExtension ? "存在常见存档扩展名；游戏进程树实际写入" : "游戏进程树实际写入，需人工判断用途";
            string[] extensions = paths.Select(p => Path.GetExtension(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string patterns = extensions.All(e => SaveExtensions.Contains(e)) ? string.Join(';', extensions.Select(e => "*" + e)) : "*";
            candidates.Add((saveName ? 2 : saveExtension ? 1 : 0, new(group.Key, reason, paths.Count, string.Join(", ", paths.Take(4).Select(Path.GetFileName)), patterns)));
        }
        var merged = new List<(int Rank, SaveCandidate Candidate)>();
        foreach (var entry in candidates.OrderBy(c => c.Candidate.Path.Length))
        {
            int parent = merged.FindIndex(c => entry.Candidate.Path.StartsWith(c.Candidate.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (parent < 0) { merged.Add(entry); continue; }
            var previous = merged[parent];
            string patterns = previous.Candidate.Patterns == "*" || entry.Candidate.Patterns == "*" ? "*"
                : string.Join(';', (previous.Candidate.Patterns + ";" + entry.Candidate.Patterns).Split(';').Distinct(StringComparer.OrdinalIgnoreCase));
            merged[parent] = (Math.Max(previous.Rank, entry.Rank), previous.Candidate with {
                FileCount = previous.Candidate.FileCount + entry.Candidate.FileCount,
                Examples = previous.Candidate.Examples + ", " + entry.Candidate.Examples, Patterns = patterns });
        }
        return merged.OrderByDescending(c => c.Rank).ThenBy(c => c.Candidate.Path).Select(c => c.Candidate).ToList();
    }
}

public static class EtwCollector
{
    sealed record Pending(string Path, int Pid, string Operation, int Options);
    public static void Run(string folder)
    {
        string errorPath = Path.Combine(folder, "error.txt");
        try
        {
            int ownerId = Storage.Read<int>(Path.Combine(folder, "owner.json"));
            using var owner = Process.GetProcessById(ownerId);
            using var session = new TraceEventSession("ColdClientManager-" + Guid.NewGuid().ToString("N"));
            session.StopOnDispose = true;
            var result = new DiscoveryResult();
            var active = new HashSet<int>();
            var pending = new Dictionary<ulong, Pending>();
            var files = new Dictionary<string, FileActivity>(StringComparer.OrdinalIgnoreCase);
            object gate = new();
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
                | KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit);
            var source = session.Source;
            source.Kernel.ProcessStart += e => {
                lock (gate)
                {
                    if (!active.Contains(e.ParentID)) return;
                    active.Add(e.ProcessID);
                    result.Processes.Add($"{e.ProcessID} ← {e.ParentID} {e.ImageFileName}");
                }
            };
            source.Kernel.ProcessStop += e => { lock (gate) active.Remove(e.ProcessID); };
            void Begin(int pid, ulong irp, string path, string op, int options)
            {
                lock (gate)
                {
                    if (pid < 0) { result.UnknownProcessOperations++; return; }
                    if (!active.Contains(pid)) return;
                    if (pending.Remove(irp)) result.IncompleteOperations++;
                    pending[irp] = new(path, pid, op, options);
                }
            }
            source.Kernel.FileIOCreate += e => Begin(e.ProcessID, e.IrpPtr, e.FileName, "Create", (int)e.CreateOptions);
            source.Kernel.FileIOWrite += e => Begin(e.ProcessID, e.IrpPtr, e.FileName, "Write", 0);
            source.Kernel.FileIOOperationEnd += e => {
                lock (gate)
                {
                    if (!pending.Remove(e.IrpPtr, out var operation) || e.NtStatus < 0 || string.IsNullOrEmpty(operation.Path)) return;
                    bool created = operation.Operation == "Create" && e.ExtraInfo == 2;
                    bool written = operation.Operation == "Write";
                    if (!created && !written) return;
                    string key = operation.Path;
                    files.TryGetValue(key, out var previous);
                    files[key] = new(operation.Path, operation.Pid, created || previous?.Created == true,
                        written || previous?.Written == true, (operation.Options & 1) != 0 || previous?.Directory == true);
                }
            };
            var processing = Task.Run(() => source.Process());
            File.WriteAllText(Path.Combine(folder, "ready"), session.SessionName);
            bool attached = false;
            while (!File.Exists(Path.Combine(folder, "stop")) && !owner.HasExited)
            {
                if (processing.IsCompleted) { processing.GetAwaiter().GetResult(); throw new IOException("ETW 会话意外结束。"); }
                if (!attached && File.Exists(Path.Combine(folder, "root.json")))
                {
                    int root = Storage.Read<int>(Path.Combine(folder, "root.json"));
                    lock (gate) { active.Add(root); result.Processes.Add(root + " 根进程"); }
                    File.WriteAllText(Path.Combine(folder, "attached"), "");
                    attached = true;
                }
                Thread.Sleep(80);
            }
            // Let the realtime buffer deliver the final child exit / writes before stopping.
            Thread.Sleep(1200);
            result.EventsLost = session.EventsLost;
            session.Stop();
            processing.GetAwaiter().GetResult();
            result.IncompleteOperations += pending.Count;
            result.Files = files.Values.OrderBy(f => f.Path).ToList();
            Storage.Write(Path.Combine(folder, "result.json"), result);
        }
        catch (Exception ex) { File.WriteAllText(errorPath, ex.ToString()); }
    }
}

public sealed class DiscoveryCapture : IAsyncDisposable
{
    readonly Process helper;
    public string Folder { get; }
    DiscoveryCapture(string folder, Process helper) { Folder = folder; this.helper = helper; }
    public static async Task<DiscoveryCapture> StartAsync(string dataRoot, bool elevated = true)
    {
        string folder = Path.Combine(dataRoot, "captures", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        Storage.Write(Path.Combine(folder, "owner.json"), Environment.ProcessId);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--collect"); info.ArgumentList.Add(folder);
        if (elevated) info.Verb = "runas";
        var capture = new DiscoveryCapture(folder, Process.Start(info)!);
        try { await capture.WaitFor("ready"); return capture; }
        catch { await capture.DisposeAsync(); throw; }
    }
    async Task WaitFor(string marker)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!File.Exists(Path.Combine(Folder, marker)))
        {
            if (File.Exists(Path.Combine(Folder, "error.txt"))) throw new IOException(File.ReadAllText(Path.Combine(Folder, "error.txt")));
            if (helper.HasExited) throw new IOException("采集器已退出，请检查权限或安全软件提示。");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("采集器未就绪，请检查 UAC 或安全软件提示。");
            await Task.Delay(100);
        }
    }
    public async Task AttachAsync(int pid)
    {
        Storage.Write(Path.Combine(Folder, "root.json"), pid);
        await WaitFor("attached");
    }
    public async Task<DiscoveryResult> FinishAsync()
    {
        File.WriteAllText(Path.Combine(Folder, "stop"), "");
        await helper.WaitForExitAsync();
        if (File.Exists(Path.Combine(Folder, "error.txt"))) throw new IOException(File.ReadAllText(Path.Combine(Folder, "error.txt")));
        return Storage.Read<DiscoveryResult>(Path.Combine(Folder, "result.json"));
    }
    public async ValueTask DisposeAsync()
    {
        if (!helper.HasExited) { File.WriteAllText(Path.Combine(Folder, "stop"), ""); await helper.WaitForExitAsync(); }
        helper.Dispose();
    }
}
