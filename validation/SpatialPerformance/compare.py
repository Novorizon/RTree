"""Compare SpatialPerformance JSON reports after checking workload equivalence."""
import csv
import json
import sys
from pathlib import Path


def rows(report):
    result = {}
    for row in report["Results"]:
        key = tuple(row[name] for name in
                    ("Profile", "Items", "Workload", "Implementation", "Operations"))
        if key in result:
            raise ValueError(f"Duplicate workload: {key}")
        result[key] = row
    return result


def compare(before_path, after_path, output_path):
    before = json.loads(Path(before_path).read_text())
    after = json.loads(Path(after_path).read_text())
    for name in ("Framework", "Processor", "Architecture", "LogicalProcessors",
                 "ServerGC", "TieredCompilation", "Seed", "Queries", "Rounds", "K"):
        if before[name] != after[name]:
            raise ValueError(f"Environment/workload differs: {name}")
    old_rows, new_rows = rows(before), rows(after)
    if old_rows.keys() != new_rows.keys():
        raise ValueError("Reports do not contain the same workloads/operation counts")
    fields = ["profile", "items", "workload", "implementation", "operations",
              "before_us", "after_us", "speedup", "before_allocated_B", "after_allocated_B",
              "before_nodes", "after_nodes", "before_primitive_tests", "after_primitive_tests"]
    with Path(output_path).open("w", newline="") as output:
        writer = csv.writer(output, lineterminator="\n")
        writer.writerow(fields)
        for key in sorted(old_rows):
            old, new = old_rows[key], new_rows[key]
            for name in ("Checksum", "MeanMatches", "ApiCallsPerOperation", "Rounds"):
                if old[name] != new[name]:
                    raise ValueError(f"Result semantics differ for {key}: {name}")
            old_us, new_us = old["MicrosecondsPerOperation"], new["MicrosecondsPerOperation"]
            writer.writerow([*key, old_us, new_us, old_us / new_us if new_us else "",
                             old["AllocatedBytesPerOperation"], new["AllocatedBytesPerOperation"],
                             old["MeanNodesVisited"], new["MeanNodesVisited"],
                             old["MeanPrimitiveTests"], new["MeanPrimitiveTests"]])
    print(f"PASS: {len(old_rows)} equivalent measured workloads -> {output_path}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("Usage: python3 compare.py before.json after.json comparison.csv")
    compare(*sys.argv[1:])
