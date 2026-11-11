"""Validate an exported snapshot; optionally compare selected SaintCoinach-style CSVs."""
import argparse
import base64
import csv
import gzip
import hashlib
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("snapshot", type=Path)
parser.add_argument("--reference", type=Path)
parser.add_argument("--output", type=Path)
args = parser.parse_args()
root = args.snapshot.resolve()
manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
assert manifest["complete"] and manifest["inputUnchanged"], "Snapshot is incomplete or source changed"
checked_rows = 0
reference_results = []
variants = {}
wide_sheets = []
empty_sheets = []
for index, sheet in enumerate(manifest["sheets"]):
    if sheet["status"] != "exported":
        continue
    schema = json.loads((root / sheet["schemaFile"]).read_text(encoding="utf-8"))
    columns = schema["columns"]
    assert len(columns) == sheet["columns"]
    data_path = root / sheet["rowsFile"]
    assert hashlib.file_digest(data_path.open("rb"), "sha256").hexdigest().upper() == sheet["sha256"]
    variants[sheet["variant"]] = variants.get(sheet["variant"], 0) + 1
    if len(columns) > 100:
        wide_sheets.append({"name": sheet["name"], "columns": len(columns)})
    if sheet["recordCount"] == 0:
        empty_sheets.append(sheet["name"])
    reference_path = args.reference / (sheet["name"] + ".csv") if args.reference else None
    reference = None
    if reference_path and reference_path.is_file():
        with reference_path.open(encoding="utf-8-sig", newline="") as file:
            reader = csv.reader(file)
            header, names, types = next(reader), next(reader), next(reader)
            assert header[0] == "key"
            assert header[1:] == list(map(str, range(len(columns))))
            reference = {row[0]: row[1:] for row in reader if row}
    ids = set()
    parent_ids = set()
    with gzip.open(data_path, "rt", encoding="utf-8") as file:
        for line in file:
            row = json.loads(line)
            key = (row["rowId"], row["subrowId"])
            assert key not in ids, (sheet["name"], "duplicate identity", key)
            ids.add(key)
            parent_ids.add(key[0])
            assert len(row["values"]) == len(columns), (sheet["name"], key, "column count")
            if sheet["variant"] == "Default":
                assert row["subrowId"] is None
            else:
                assert isinstance(row["subrowId"], int)
            for value, column in zip(row["values"], columns):
                if column["type"] == "String":
                    assert isinstance(value["text"], str)
                    base64.b64decode(value["raw"], validate=True)
                if column["type"] in ("Int64", "UInt64"):
                    assert isinstance(value, str)
                    int(value)
            if reference is not None:
                csv_key = str(key[0]) if key[1] is None else f"{key[0]}.{key[1]}"
                expected = reference.pop(csv_key)
                actual = []
                for value, column in zip(row["values"], columns):
                    if column["type"] == "String":
                        actual.append(value["text"])
                    elif column["type"] == "Float32":
                        actual.append(str(value["value"]))
                    else:
                        actual.append(str(value))
                assert actual == expected, (sheet["name"], key, actual, expected)
    assert len(ids) == sheet["recordCount"], (sheet["name"], "record count")
    assert len(parent_ids) == sheet["rowCount"], (sheet["name"], "row count")
    checked_rows += len(ids)
    if reference is not None:
        assert not reference, (sheet["name"], "reference-only rows", len(reference))
        reference_results.append({"sheet": sheet["name"], "rows": len(ids), "columns": len(columns), "allCellsMatch": True})
    if (index + 1) % 1500 == 0:
        print(f"Validated {index + 1} sheets; {checked_rows:,} records", flush=True)
assert checked_rows == manifest["recordCount"]
if args.reference:
    expected_references = {p.stem for p in args.reference.glob("*.csv")}
    assert expected_references == {entry["sheet"] for entry in reference_results}, "Reference file was not compared"
result = {
    "snapshot": root.name, "sheets": manifest["exportedSheetCount"], "records": checked_rows,
    "variants": variants, "emptySheets": empty_sheets,
    "wideSheetCount": len(wide_sheets), "widestSheets": sorted(wide_sheets, key=lambda x: -x["columns"])[:5],
    "references": reference_results, "passed": True
}
if args.output:
    target = args.output.resolve()
    project_tmp = Path(__file__).resolve().parents[1] / "tmp"
    assert target.is_relative_to(project_tmp), "Validation output must be inside project tmp"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(result, ensure_ascii=False, indent=2))
