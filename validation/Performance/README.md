# Reproducible performance checks

Run from the repository root with .NET 8:

```sh
dotnet run --project validation/Performance/Performance.csproj -c Release -- \
  --sizes 10000,50000 --queries 200 --rounds 3 \
  --label fixed --json validation/Performance/results-fixed.json
```

This project compiles the same four library source files as the legacy .NET
Framework project. It has no external package dependencies. It does not change
the library's target framework. Use `--help` for the small set of parameters.
For a quick functional check, use `--sizes 100 --queries 8 --rounds 1`.

The benchmark uses the default node capacity (10), seed `712367`, and three
distributions: independent X/Y/Z coordinates, fixed X/Y with varying Z, and
varying X/Y with every box's Z bounds equal to zero. The planar queries span
Z `[-1, 1]` and nearest points have Z zero. Per-scenario seed offsets are `0`,
`1`, and `2`, so adding the planar distribution preserves the first two datasets.
Each size and distribution includes insertion, intersection, containment,
nearest-neighbor queries, and deletion followed by reinsertion of 10% of items
(capped at 5,000 pairs). A mutation row reports time per individual `Delete` or
`Add` call, so one pair counts as two operations. Nearest-neighbor queries return
all tied nearest items within their upper distance limit; they are not radius
range queries.

Data generation and an independent brute-force oracle run before measurement.
Tree and linear-scan queries both materialize result lists. Every measured result
is checked against the oracle as a sorted set, outside the timed region. After
each mutation round, all item IDs and all three query types are checked again.
Reported checksums are independent of traversal order and must agree between
the tree and scan rows for the same workload.

One full warm-up runs before the measured rounds. Each sample starts after a
full garbage collection, outside timing. Rows report median wall-clock time and
median allocation per operation; allocation uses
`GC.GetAllocatedBytesForCurrentThread`. These are single-threaded measurements
in one process. The report records runtime, OS, CPU model, logical CPU count, GC
mode, seed, and source directory. Run on an otherwise idle machine and treat
shared-host results as workload-specific observations.

For a longer run with tiered-compilation changes removed from the samples:

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/Performance/Performance.csproj -c Release -- \
  --sizes 10000,50000 --queries 1000 --rounds 5 \
  --label fixed --json validation/Performance/results-fixed.json
```

The report records the tiered-compilation environment setting. Use the same
setting and parameters for both source revisions.

To compare another revision, extract its `Node.cs`, `Point.cs`, `Rectangle.cs`,
and `RTree.cs` into a separate directory, then pass that directory when building:

```sh
dotnet run --project validation/Performance/Performance.csproj -c Release \
  -p:SourceDirectory=/absolute/path/to/baseline-sources -- \
  --sizes 10000,50000 --queries 200 --rounds 3 \
  --label baseline --allow-mutation-failure \
  --json validation/Performance/results-baseline.json
```

The baseline sources stay outside the repository. Do not use `--no-build` when
switching source revisions: the source selection is a build-time property.
The JSON report records the directory that was actually compiled.

The original revision can throw while deleting or reinserting. Such rows are
marked `FAILED` and have no performance value. `--allow-mutation-failure` permits
that known baseline failure while keeping its other measurements; the default
exits unsuccessfully on any mutation failure. Query mismatches always fail the
run, regardless of that option.

## Recorded comparison

The checked-in [baseline report](results-baseline.json) uses upstream commit
`a75b92389200c6a39fd78b359daf9e01b034f2fb`; the [fixed report](results-fixed.json)
uses the repaired source in this change. Both runs used .NET 8.0.33, Debian 13,
an AMD EPYC 9V74 host with four available logical processors, workstation GC,
`DOTNET_TieredCompilation=0`, the same seeds, 1,000 queries per query workload,
and five measured rounds. Runs were sequential. Medians below are microseconds
per API call at 50,000 items, shown as **original → fixed**.

| Distribution | Add | Intersects | Contains | Nearest |
| --- | ---: | ---: | ---: | ---: |
| Uniform 3D | 1.271 → 2.334 | 16.186 → 11.871 | 16.574 → 13.555 | 28.169 → 29.462 |
| Z-only | 0.938 → 1.352 | 702.224 → 8.960 | 901.796 → 9.292 | 776.401 → 47.405 |
| Planar XY | 1.266 → 2.887 | 22.346 → 31.681 | 22.113 → 32.488 | 15.893 → 27.857 |

The original failed all six deletion/reinsertion workloads (both sizes across
three distributions) with a duplicate-node-key exception, so it has no valid
mutation timing. The fixed version passed every round; its 50,000-item mutation
medians were 3.515 µs/call for uniform 3D, 1.843 µs/call for Z-only, and
5.190 µs/call for planar XY. A pair consists of two calls.

Z-only queries improve by about 16–97 times in this run. Uniform intersection
and containment queries improve by about 1.2–1.4 times. The report also records
regressions: insertion takes about 1.4–2.3 times as long, planar queries take
about 1.4–1.8 times as long, and uniform nearest queries are about 5% slower.
The corrected geometry and split rules change both insertion work and tree
shape; this result does not establish a universal speedup.

At 50,000 items the fixed range queries are about 10–39 times faster than the
measured linear scans; fixed nearest queries are about 7–11 times faster.
Allocation per operation and 10,000-item results are in the JSON reports. All
read-query checksums agree between original, fixed and linear scan. Every fixed
post-mutation query also agrees with its independently rebuilt oracle.
