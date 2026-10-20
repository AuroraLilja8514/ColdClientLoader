"""Extract a captured root's process lifetimes and file operations, then verify fixture truth.

Usage: python Analyze-Capture.py <run-directory> [--extract-only]
Ground truth is used only after extraction, never to select descendants or file events.
"""
import bisect
import csv
import json
import re
import sys
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


class Lifetimes:
    def __init__(self, events):
        self.nodes = []
        self.by_pid = defaultdict(list)
        active = {}
        self.unmatched_stops = 0
        self.reused_pids = set()
        for event in sorted(events, key=lambda item: item["time"]):
            pid = event["pid"]
            if event["type"] == "start":
                if pid in active:
                    raise ValueError(f"Overlapping process lifetimes for PID {pid}")
                if self.by_pid[pid]:
                    self.reused_pids.add(pid)
                node = dict(event, end=None, parent=None, tree=False)
                node["id"] = f'{pid}@{event["time"]}'
                self.nodes.append(node)
                self.by_pid[pid].append(node)
                active[pid] = node
            elif pid in active:
                node = active.pop(pid)
                if event.get("key") and node.get("key") != event["key"]:
                    raise ValueError(f"Process key mismatch for PID {pid}")
                node["end"] = event["time"]
            else:
                self.unmatched_stops += 1  # Processes already running when tracing began.
        self.starts = {pid: [node["time"] for node in nodes] for pid, nodes in self.by_pid.items()}
        for node in self.nodes:
            parent = self.at(node["parentPid"], node["time"])
            if parent:
                node["parent"] = parent["id"]

    def at(self, pid, time):
        nodes = self.by_pid.get(pid)
        if not nodes:
            return None
        index = bisect.bisect_right(self.starts[pid], time) - 1
        if index < 0:
            return None
        node = nodes[index]
        return node if node["end"] is None or time < node["end"] else None

    def select_tree(self, root_pid, expected_start):
        candidates = self.by_pid[root_pid]
        root = min(candidates, key=lambda node: abs(node["time"] - expected_start))
        # Trace start and OS creation timestamps can differ slightly, but not by a second.
        if abs(root["time"] - expected_start) > 10_000_000:
            raise ValueError("Root start does not match the captured launch")
        root["tree"] = True
        for node in self.nodes:
            if node["parent"] and any(parent["id"] == node["parent"] and parent["tree"]
                                      for parent in self.nodes):
                node["tree"] = True
        return root


def time_of_day(value):
    match = re.fullmatch(r"(\d+):(\d+):(\d+)\.(\d+)", value)
    hour, minute, second = map(int, match.groups()[:3])
    fraction = int(match[4].ljust(7, "0"))
    return ((hour * 60 + minute) * 60 + second) * 10_000_000 + fraction


def procmon_events(run):
    processes, files = [], []
    previous, day = 0, 0
    with (run / "events.csv").open(encoding="utf-8-sig", newline="") as stream:
        for row in csv.DictReader(stream):
            clock = time_of_day(row["Time of Day"])
            if previous - clock > 12 * 3600 * 10_000_000:
                day += 24 * 3600 * 10_000_000
            previous = clock
            time, pid = day + clock, int(row["PID"])
            operation, detail = row["Operation"], row["Detail"]
            if operation == "Process Start":
                parent = int(re.search(r"(?:^|, )Parent PID: (\d+)", detail)[1])
                processes.append(dict(type="start", time=time, pid=pid, parentPid=parent,
                                      image=row["Process Name"]))
            elif operation == "Process Exit":
                processes.append(dict(type="stop", time=time, pid=pid))
            elif operation in {"CreateFile", "WriteFile", "SetRenameInformationFile",
                               "SetDispositionInformationEx", "SetDispositionInformationFile",
                               "CreateFileMapping"}:
                if operation == "CreateFile":
                    kind = "OpenOrCreate"
                elif operation == "SetRenameInformationFile":
                    kind = "Rename"
                elif operation.startswith("SetDisposition"):
                    kind = "Delete"
                elif operation == "WriteFile":
                    kind = "Write"
                else:
                    kind = "Mapping"
                item = dict(time=time, pid=pid, operation=kind, path=row["Path"],
                            success=row["Result"] == "SUCCESS", result=row["Result"])
                if kind == "OpenOrCreate":
                    disposition = re.search(r"(?:^|, )Disposition: ([^,]+)", detail)
                    open_result = re.search(r"(?:^|, )OpenResult: ([^,]+)", detail)
                    item["disposition"] = disposition[1] if disposition else None
                    item["openResult"] = open_result[1] if open_result else None
                    if not item["success"]:
                        item["creationOutcome"] = "Failed"
                    elif item["openResult"] == "Created" or item["disposition"] == "Create":
                        item["creationOutcome"] = "Created"
                    elif item["openResult"] or item["disposition"] in {"Open", "Overwrite"}:
                        item["creationOutcome"] = "NotCreated"
                    else:
                        # Observed for impersonated OpenIf operations in the real game trace.
                        item["creationOutcome"] = "Unknown"
                    item["created"] = item["creationOutcome"] == "Created"
                    item["directory"] = bool(re.search(r"Options: (?:[^,]*, )?Directory(?:,|$)", detail))
                elif kind == "Rename":
                    item["destination"] = detail.split("FileName: ", 1)[1]
                files.append(item)
    return processes, files, {}


def etw_events(run):
    processes, files, metadata = [], [], {}
    with (run / "events.jsonl").open(encoding="utf-8") as stream:
        for line in stream:
            row = json.loads(line)
            if row["Type"] == "metadata":
                metadata.update(row)
            elif row["Type"] == "statistics":
                metadata.update(row)
            elif row["Type"] in {"process-start", "process-stop"}:
                processes.append(dict(type="start" if row["Type"] == "process-start" else "stop",
                                      time=round(row["Time"] * 10_000), pid=row["Pid"],
                                      parentPid=row["ParentPid"], key=row["Key"], image=row["Image"]))
            elif row["Type"] == "file":
                success = "Status" in row and row["Status"] >= 0
                item = dict(time=round(row["Time"] * 10_000), pid=row["Pid"],
                            operation=row["Operation"], path=row["Path"], success=success,
                            status=row.get("Status"), extraInfo=row.get("ExtraInfo"),
                            irp=row["Irp"], fileObject=row["FileObject"])
                if row["Operation"] == "OpenOrCreate":
                    item["created"] = success and row.get("ExtraInfo") == 2  # FILE_CREATED
                    item["directory"] = bool(row["Options"] & 1)  # FILE_DIRECTORY_FILE
                    item["disposition"] = row["Disposition"]
                files.append(item)
    return processes, files, metadata


def verify_pid_reuse():
    def start(pid, parent, time):
        return dict(type="start", pid=pid, parentPid=parent, time=time)
    def stop(pid, time):
        return dict(type="stop", pid=pid, time=time)
    timeline = Lifetimes([start(10, 1, 1), start(20, 10, 2), stop(10, 3),
                          start(30, 20, 4), start(10, 1, 5), start(40, 10, 6),
                          stop(20, 7), start(20, 1, 8), start(50, 20, 9)])
    timeline.select_tree(10, 1)
    assert {node["pid"] for node in timeline.nodes if node["tree"]} == {10, 20, 30}
    assert timeline.at(10, 6)["tree"] is False
    assert timeline.at(20, 10)["tree"] is False
    assert timeline.at(30, 10)["tree"] is True
    return {"Passed": True, "Scope": "Synthetic lifecycle records, not actual OS PID reuse"}


def analyze(run, extract_only=False):
    state = read_json(run / "state.json")
    if state["Status"] != "Captured" or not state["CaptureStopped"]:
        raise ValueError("Capture did not finish successfully")
    backend = state["Backend"]
    processes, files, metadata = procmon_events(run) if backend == "Procmon" else etw_events(run)
    root_start = datetime.fromisoformat(state["RootStartUtc"])
    if backend == "Procmon":
        expected_start = time_of_day(root_start.astimezone().strftime("%H:%M:%S.%f"))
    else:
        expected_start = round((root_start - datetime.fromisoformat(metadata["SessionStartUtc"])).total_seconds()
                               * 10_000_000)
    timeline = Lifetimes(processes)
    root = timeline.select_tree(state["RootProcessId"], expected_start)
    selected = []
    for item in files:
        node = timeline.at(item["pid"], item["time"])
        if node and node["tree"]:
            selected.append(dict(item, process=node["id"]))
    write_json(run / "extracted-tree.json", [node for node in timeline.nodes if node["tree"]])
    write_json(run / "extracted-files.json", selected)
    write_json(run / "created-paths.json", [item for item in selected if item.get("created")])

    if extract_only:
        summary = {
            "Backend": backend, "RootPid": root["pid"],
            "TreeLifetimes": sum(node["tree"] for node in timeline.nodes),
            "NewFileEvents": sum(bool(item.get("created")) and not item["directory"] for item in selected),
            "NewDirectoryEvents": sum(bool(item.get("created")) and item["directory"] for item in selected),
            "SuccessfulWriteEvents": sum(item["operation"] == "Write" and item["success"] for item in selected),
            "UnknownCreationOutcomeEvents": sum(item.get("creationOutcome") == "Unknown" for item in selected),
            "TreeLifetimesWithoutExit": [node["id"] for node in timeline.nodes if node["tree"] and node["end"] is None],
            "OperationCounts": dict(Counter(item["operation"] for item in selected)),
            "EtwStatistics": metadata if backend == "ETW" else None
        }
        write_json(run / "extraction-summary.json", summary)
        print(json.dumps(summary, ensure_ascii=False, indent=2))
        return

    # Only the following verification uses workload expectations and the known data folder.
    truth = read_json(run / "ground-truth.json")
    inside = [entry for entry in truth if entry["Role"] not in {"broker", "noise"}]
    outside = [entry for entry in truth if entry["Role"] in {"broker", "noise"}]
    selected_pids = {node["pid"] for node in timeline.nodes if node["tree"]}
    tree_data = str(run / "data" / "tree") + "\\"
    observed = [item for item in selected if item["path"].casefold().startswith(tree_data.casefold())]
    expected_files = {operation["Path"].casefold() for entry in inside for operation in entry["Operations"]
                      if operation["Operation"] == "CreateFile"}
    expected_dirs = {operation["Path"].casefold() for entry in inside for operation in entry["Operations"]
                     if operation["Operation"] == "CreateDirectory"}
    created_files = {item["path"].casefold() for item in observed if item.get("created") and not item["directory"]}
    created_dirs = {item["path"].casefold() for item in observed if item.get("created") and item["directory"]}
    missing_roles = [entry["Role"] for entry in inside if entry["ProcessId"] not in selected_pids]
    outside_included = [entry["Role"] for entry in outside if entry["ProcessId"] in selected_pids]
    file_missing, file_extra = sorted(expected_files - created_files), sorted(created_files - expected_files)
    dir_missing, dir_extra = sorted(expected_dirs - created_dirs), sorted(created_dirs - expected_dirs)
    checks = {}
    for name, role, operation in [("Rename", "branch-b", "Rename"), ("Delete", "branch-b", "Delete"),
                                  ("ExistingWrite", "branch-b", "WriteExisting"),
                                  ("MappedWrite", "branch-b", "MappedWriteExisting"),
                                  ("FailedCreate", "branch-b", "FailedCreate")]:
        entry = next(entry for entry in truth if entry["Role"] == role)
        expected = next(item for item in entry["Operations"] if item["Operation"] == operation)
        matches = [item for item in observed if item["path"].casefold() == expected["Path"].casefold()]
        all_matches = [item for item in files if item["path"].casefold() == expected["Path"].casefold()]
        checks[name] = dict(Path=expected["Path"], ProcessEvents=matches,
                            AllAttributedPids=sorted({item["pid"] for item in all_matches}),
                            AllOperations=dict(Counter(item["operation"] for item in all_matches)))
    late_path = next(op["Path"] for entry in truth if entry["Role"] == "grandchild"
                     for op in entry["Operations"] if op["Operation"] == "CreateFile")
    late_created = any(item.get("created") and item["path"] == late_path and item["time"] > root["end"]
                       for item in observed)
    def case_has(name, operation):
        return any(item["operation"] == operation and item["success"]
                   for item in checks[name]["ProcessEvents"])

    broker = next(entry for entry in outside if entry["Role"] == "broker")
    broker_path = broker["Operations"][0]["Path"]
    case_results = {
        "ShortLivedChildrenFound": sum(entry["Role"].startswith("quick-") and entry["ProcessId"] in selected_pids
                                       for entry in inside),
        "OutsideSameImageProcessesExcluded": not outside_included,
        "DelegatedOutsideTreeCreationVisibleInFullTrace": any(item.get("created") and item["path"] == broker_path
                                                               for item in files),
        "DelegatedOutsideTreeCreationExcludedFromTree": not any(item["path"] == broker_path for item in selected),
        "RenameOperationCaptured": case_has("Rename", "Rename"),
        "RenameDestinationExtracted": any(item.get("destination") for item in checks["Rename"]["ProcessEvents"]),
        "DeleteOperationCaptured": case_has("Delete", "Delete"),
        "CreatedThenDeletedFileAbsentAtEnd": not Path(checks["Delete"]["Path"]).exists(),
        "ExistingFileWriteCaptured": case_has("ExistingWrite", "Write"),
        "ExistingFileNotReportedAsNew": not any(item.get("created") for item in checks["ExistingWrite"]["ProcessEvents"]),
        "MappedFileActuallyChanged": Path(checks["MappedWrite"]["Path"]).read_bytes()[0] == 77,
        "MappedWriteAttributedToTree": case_has("MappedWrite", "Write"),
        "MappedFileOpenCaptured": case_has("MappedWrite", "OpenOrCreate"),
        "FailedCreateCapturedAndNotReportedAsNew": bool(checks["FailedCreate"]["ProcessEvents"]) and
            not any(item["success"] or item.get("created") for item in checks["FailedCreate"]["ProcessEvents"])
    }
    summary = {
        "Backend": backend, "Status": "Verified" if not any((missing_roles, outside_included, file_missing,
                                                               file_extra, dir_missing, dir_extra)) else "Mismatch",
        "RootPid": root["pid"], "TreeLifetimes": sum(node["tree"] for node in timeline.nodes),
        "ExpectedFixtureTreeProcesses": len(inside), "ObservedFixtureTreeProcesses": len(inside) - len(missing_roles),
        "MissingRoles": missing_roles, "OutsideRolesIncorrectlyIncluded": outside_included,
        "ExpectedNewFiles": len(expected_files), "ObservedNewFiles": len(created_files),
        "ExpectedNewDirectories": len(expected_dirs), "ObservedNewDirectories": len(created_dirs),
        "MissingFiles": file_missing, "ExtraFiles": file_extra, "MissingDirectories": dir_missing,
        "ExtraDirectories": dir_extra, "CreationAfterRootExitCaptured": late_created,
        "UnicodePathPreserved": any("进度" in item["path"] and item.get("created") for item in observed),
        "TreeDataOperationCounts": dict(Counter(item["operation"] for item in observed)),
        "RealReusedPidLifetimes": {str(pid): timeline.by_pid[pid] for pid in sorted(timeline.reused_pids)},
        "EtwStatistics": metadata if backend == "ETW" else None,
        "PidReuseSyntheticTest": verify_pid_reuse(), "CaseResults": case_results, "Cases": checks
    }
    write_json(run / "verification.json", summary)
    print(json.dumps({key: value for key, value in summary.items() if key != "Cases"}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    analyze(Path(sys.argv[1]).resolve(), extract_only="--extract-only" in sys.argv[2:])
