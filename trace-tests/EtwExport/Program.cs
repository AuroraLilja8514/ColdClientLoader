using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

if (args.Length != 2) throw new ArgumentException("Usage: EtwExport <capture.etl> <events.jsonl>");
using var output = new StreamWriter(args[1], false, new UTF8Encoding(false));
using var source = new ETWTraceEventSource(args[0]);
var pending = new Dictionary<ulong, Dictionary<string, object>>();
int overlaps = 0, unknownProcess = 0, completed = 0;
void Emit(object value) => output.WriteLine(JsonSerializer.Serialize(value));

void ProcessEvent(ProcessTraceData data, string kind) => Emit(new {
    Type = kind, Time = data.TimeStampRelativeMSec, Pid = data.ProcessID,
    ParentPid = data.ParentID, Key = data.UniqueProcessKey.ToString("x"),
    Image = data.ImageFileName
});

void Begin(TraceEvent data, string operation, ulong irp, ulong fileObject, string path,
    int? options = null, int? disposition = null, int? infoClass = null) {
    if (pending.Remove(irp, out var previous)) {
        previous["CompletionMissing"] = true;
        Emit(previous);
        overlaps++;
    }
    if (data.ProcessID < 0) unknownProcess++;
    pending[irp] = new Dictionary<string, object> {
        ["Type"] = "file", ["Operation"] = operation,
        ["Time"] = data.TimeStampRelativeMSec, ["Pid"] = data.ProcessID,
        ["Tid"] = data.ThreadID, ["Irp"] = irp.ToString("x"),
        ["FileObject"] = fileObject.ToString("x"), ["Path"] = path,
        ["Options"] = options, ["Disposition"] = disposition, ["InfoClass"] = infoClass
    };
}

source.Kernel.ProcessStart += data => ProcessEvent(data, "process-start");
source.Kernel.ProcessStop += data => ProcessEvent(data, "process-stop");
source.Kernel.ProcessDCStart += data => ProcessEvent(data, "process-rundown-start");
source.Kernel.ProcessDCStop += data => ProcessEvent(data, "process-rundown-stop");
source.Kernel.FileIOCreate += data => Begin(data, "OpenOrCreate", data.IrpPtr,
    data.FileObject, data.FileName, (int)data.CreateOptions, (int)data.CreateDisposition);
source.Kernel.FileIOWrite += data => Begin(data, "Write", data.IrpPtr, data.FileObject, data.FileName);
source.Kernel.FileIORename += data => Begin(data, "Rename", data.IrpPtr, data.FileObject,
    data.FileName, infoClass: data.InfoClass);
source.Kernel.FileIODelete += data => Begin(data, "Delete", data.IrpPtr, data.FileObject,
    data.FileName, infoClass: data.InfoClass);
source.Kernel.FileIOSetInfo += data => Begin(data, "SetInfo", data.IrpPtr, data.FileObject,
    data.FileName, infoClass: data.InfoClass);
source.Kernel.FileIOOperationEnd += data => {
    if (!pending.Remove(data.IrpPtr, out var operation)) return;
    operation["EndTime"] = data.TimeStampRelativeMSec;
    operation["Status"] = data.NtStatus;
    operation["ExtraInfo"] = data.ExtraInfo;
    Emit(operation);
    completed++;
};

Emit(new { Type = "metadata", SessionStartUtc = source.SessionStartTime.ToUniversalTime().ToString("o") });
source.Process();
foreach (var operation in pending.Values) {
    operation["CompletionMissing"] = true;
    Emit(operation);
}
Emit(new { Type = "statistics", EventsLost = source.EventsLost, CompletedOperations = completed,
    IncompleteOperations = pending.Count, OverlappingIrps = overlaps, UnknownProcessOperations = unknownProcess });
