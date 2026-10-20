using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;

string role = args[0];
string runRoot = Path.GetFullPath(args[1]);
int parentId = int.Parse(args[2]);
string dataRoot = Path.Combine(runRoot, "data");
string controlRoot = Path.Combine(runRoot, "control");
int processId = Environment.ProcessId;
var operations = new List<object>();
var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

void Record(string operation, string path, string destination = null) => operations.Add(new {
    Operation = operation, Path = path, Destination = destination,
    AtUtc = DateTime.UtcNow.ToString("o")
});

void MakeDirectory(string path) {
    Directory.CreateDirectory(path);
    Record("CreateDirectory", path);
}

void MakeFile(string path) {
    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) {
        stream.Write(Encoding.UTF8.GetBytes(role + ":" + processId));
        stream.Flush(true);
    }
    Record("CreateFile", path);
}

Process Spawn(string childRole) {
    var info = new ProcessStartInfo(Environment.ProcessPath) {
        UseShellExecute = false, CreateNoWindow = true
    };
    info.ArgumentList.Add(childRole);
    info.ArgumentList.Add(runRoot);
    info.ArgumentList.Add(processId.ToString());
    return Process.Start(info);
}

try {
    switch (role) {
        case "root":
            MakeDirectory(Path.Combine(dataRoot, "tree", "empty-directory"));
            MakeFile(Path.Combine(dataRoot, "tree", "root.sav"));
            File.WriteAllText(Path.Combine(controlRoot, "broker-request"), "save");
            Spawn("branch-a");
            Spawn("branch-b");
            // Deliberately exit before either branch and its descendants finish.
            break;
        case "branch-a":
            Thread.Sleep(250);
            MakeFile(Path.Combine(dataRoot, "tree", "branch-a.sav"));
            Spawn("grandchild");
            var workers = new List<Process>();
            for (int index = 0; index < 12; index++) workers.Add(Spawn("quick-" + index));
            foreach (var worker in workers) {
                worker.WaitForExit();
                if (worker.ExitCode != 0) throw new Exception("Worker failed");
            }
            break;
        case "branch-b":
            MakeDirectory(Path.Combine(dataRoot, "tree", "中文目录, with spaces"));
            MakeFile(Path.Combine(dataRoot, "tree", "中文目录, with spaces", "进度, 01.sav"));
            string temporary = Path.Combine(dataRoot, "tree", "atomic.tmp");
            string final = Path.Combine(dataRoot, "tree", "atomic.sav");
            MakeFile(temporary);
            File.Move(temporary, final);
            Record("Rename", temporary, final);
            string deleted = Path.Combine(dataRoot, "tree", "created-and-deleted.tmp");
            MakeFile(deleted);
            File.Delete(deleted);
            Record("Delete", deleted);
            string existing = Path.Combine(dataRoot, "tree", "existing.sav");
            using (var stream = new FileStream(existing, FileMode.Open, FileAccess.Write)) {
                stream.WriteByte(42);
                stream.Flush(true);
            }
            Record("WriteExisting", existing);
            string mapped = Path.Combine(dataRoot, "tree", "mapped-existing.sav");
            using (var mapping = MemoryMappedFile.CreateFromFile(mapped, FileMode.Open))
            using (var view = mapping.CreateViewAccessor()) {
                view.Write(0, (byte)77);
                view.Flush();
            }
            Record("MappedWriteExisting", mapped);
            // A failed creation must not be reported as a newly created file.
            string failed = Path.Combine(dataRoot, "tree", "missing-parent", "failed.sav");
            try { File.WriteAllText(failed, "unreachable"); }
            catch (DirectoryNotFoundException) { Record("FailedCreate", failed); }
            break;
        case "grandchild":
            Thread.Sleep(1000);
            MakeDirectory(Path.Combine(dataRoot, "tree", "late-child"));
            MakeFile(Path.Combine(dataRoot, "tree", "late-child", "after-root-exit.sav"));
            break;
        case "noise":
            MakeFile(Path.Combine(dataRoot, "noise", "same-image-unrelated.sav"));
            break;
        case "broker":
            var watch = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(controlRoot, "broker-request"))) {
                if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("No broker request");
                Thread.Sleep(20);
            }
            MakeFile(Path.Combine(dataRoot, "broker", "delegated-outside-tree.sav"));
            break;
        default:
            if (!role.StartsWith("quick-")) throw new ArgumentException("Unknown fixture role");
            MakeFile(Path.Combine(dataRoot, "tree", role + ".sav"));
            break;
    }
}
finally {
    File.WriteAllText(Path.Combine(runRoot, "truth", role + "-" + processId + ".json"),
        JsonSerializer.Serialize(new {
            Role = role, ProcessId = processId, ParentProcessId = parentId,
            StartUtc = started.ToString("o"), FinishedWorkUtc = DateTime.UtcNow.ToString("o"),
            Operations = operations
        }));
}
