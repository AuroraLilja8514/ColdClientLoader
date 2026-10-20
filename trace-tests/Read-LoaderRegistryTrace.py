"""Record the tested loader's Steam registry reads/writes without logging process environments."""
import csv
import json
import sys
from pathlib import Path

run = Path(sys.argv[1])
source_csv = Path(sys.argv[2])
launch = json.loads((run / "launch.json").read_text(encoding="utf-8-sig"))
root_pid = str(launch["RootProcessId"])
events = []
first_queries = {}
written = set()
with source_csv.open(encoding="utf-8-sig", newline="") as stream:
    for row in csv.DictReader(stream):
        if row["PID"] != root_pid or not row["Operation"].startswith("Reg"):
            continue
        path = row["Path"]
        if "\\software\\valve\\steam" not in path.casefold():
            continue
        item = {key: row[key] for key in ("Time of Day", "Operation", "Path", "Result", "Detail")}
        events.append(item)
        if (row["Operation"] == "RegQueryValue" and row["Result"] in {"SUCCESS", "NAME NOT FOUND"}
                and path not in written and path not in first_queries):
            first_queries[path] = item
        if row["Operation"] in {"RegSetValue", "RegDeleteValue"}:
            written.add(path)
report = {"RootPid": int(root_pid), "FirstValueReadsBeforeModification": first_queries, "Events": events}
(run / "loader-registry-trace.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"RegistryEvents": len(events), "FirstValueReadsBeforeModification": first_queries},
                 ensure_ascii=False, indent=2))
