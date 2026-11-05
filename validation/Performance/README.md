# Reproducible performance checks

Run from the repository root with .NET 8:

```sh
dotnet run --project validation/Performance/Performance.csproj -c Release \
  -p:OptimizedApi=true -- \
  --sizes 10000,50000 --queries 200 --rounds 3 \
  --label current --json validation/Performance/results-current.json
```

This project compiles the selected library source files, also used by the legacy .NET
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
  --label current --json validation/Performance/results-current.json
```

The report records the tiered-compilation environment setting. Use the same
setting and parameters for both source revisions.

To compare another revision, extract all of its library `.cs` files into a
separate directory, then pass that directory when building:

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
uses the initial correctness repair at `e63063ea1b594a806a74001cfc66d5c6ff262b42`,
before the pooled-storage/query optimizations. Both runs used .NET 8.0.33, Debian 13,
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

## Measuring the query and storage optimizations

The historical JSON reports above remain unchanged. To measure the new storage
and query implementation, compile with `-p:OptimizedApi=true`. Compatibility
rows still invoke the same allocating `List<T>`-return API as the earlier
revision, making those before/after comparisons meaningful. Diagnostic query
statistics are collected separately, outside the timed region. Compatibility
insertion rows also record an untimed retained-index byte estimate, so both
source revisions can be compared for storage footprint with the same helper.

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/Performance/Performance.csproj -c Release \
  -p:OptimizedApi=true -- \
  --sizes 10000,50000 --queries 1000 --rounds 5 \
  --label optimized --json validation/Performance/results-optimized.json
```

The pre-optimization source revision is `e63063ea1b594a806a74001cfc66d5c6ff262b42`.
Extract its four library sources to a separate directory and build this same
benchmark with `-p:OptimizedApi=false -p:SourceDirectory=/path/to/those/sources`.
Use identical runtime settings, sizes, queries and rounds, with a distinct
label/output filename. Run source revisions sequentially and rebuild when
changing the source directory. The newer benchmark source remains the same
for both runs; only the library source changes.

The advanced measurements require the optimized source and API:

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/Performance/Performance.csproj -c Release \
  -p:OptimizedApi=true -- \
  --advanced-only --sizes 50000 --queries 1000 --rounds 5 \
  --capacities 10,8,16,32,64 --workers 1,2,4 --concurrent-repeats 4 \
  --label optimized-advanced \
  --json validation/Performance/results-optimized-advanced.json
```

`--advanced` appends the advanced workloads to compatibility measurements;
`--advanced-only` skips compatibility rows. These options measure:

- Incremental insertion and STR bulk construction at capacities 8/16/32/64,
  with capacity 10 also included in the default sweep as an anchor. An explicit
  `--capacities` list replaces that default. Build rows
  include allocation and an untimed `GC.GetTotalMemory` estimate of retained
  index bytes, excluding pre-existing caller-owned input boxes/pairs.
- All three query types on each tree using a caller-owned list and
  `QueryContext`. Both are warmed across the full query set before timing.
  Every query's exact IDs are checked against the independent oracle before
  and after each measured round, outside timing. The timed loop consumes total
  match counts and does not allocate a per-query result array. These rows use
  `Implementation="reused-buffer"` and must be compared separately from the
  compatibility API rows.
- Untimed mean node visits, entry tests and returned matches per query. These
  distinguish pruning/tree-shape changes from work proportional to output size.
- Concurrent readers of the same STR tree at fixed capacity 16. Each worker
  owns a list/context, and lock/runtime bookkeeping is warmed on the actual
  worker before timing. Thread construction and warm-up are excluded; start-gate
  scheduling is included. The timed workload consumes and checks every query's
  order-independent ID checksum and count, rather than timing discarded output.
  It mixes intersection, containment and nearest calls. Worker counts and total
  operation counts are explicit. Thread allocations are summed from per-thread
  counters. Parallel throughput does not promise linear scaling.

New JSON rows include `LatencyBatchSamples` and p50/p95/p99 **of round-level
batch mean latency**. With five rounds, these summarize only five batches;
they are not individual-query tail latency or an online service's P99.
`AllocatedBytesPerOperation` excludes input/context/buffer setup. Zero is only
expected after sufficient buffer and workspace warm-up; allocating result API
rows deliberately continue to include result storage.

For a short functional check, use `--advanced-only --sizes 100 --queries 8
--rounds 1 --capacities 8,16 --workers 1,2 --concurrent-repeats 1`. Larger sizes,
including 100,000 or 1,000,000 items, are supported through `--sizes`; keep query
counts and the capacity sweep bounded because independent oracle work is
linear in items times queries and is repeated for each distribution.

`--bulk-only` selects advanced-only measurements and omits incremental
construction. It still validates query results against one independently
computed oracle per dataset and runs the configured reader counts. For a
bounded million-item smoke workload:

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/Performance/Performance.csproj -c Release \
  -p:OptimizedApi=true -- \
  --bulk-only --sizes 1000000 --queries 32 --rounds 3 \
  --capacities 16 --workers 1 --concurrent-repeats 1 \
  --label million-str --json validation/Performance/results-million-str.json
```


## Recorded pooled-storage/query optimization results

These reports compare the correctness-repaired revision `e63063ea` with the
pooled implementation. The original broken upstream version is only used in
the earlier historical comparison. All runs here used .NET 8.0.33, Debian 13,
AMD EPYC 9V74 with four available logical processors, workstation GC, and
`DOTNET_TieredCompilation=0`; workloads ran sequentially. Every read-query
checksum agrees across source revisions, and every measured updated-tree result
agrees with its independent oracle.

[Before](results-before-optimization.json) and [optimized](results-optimized.json)
use the same existing List-return API, capacity 10, sizes 10k/50k, 1,000 queries
per workload, and five measured rounds. The 50,000-item medians below are
microseconds per call, **before → optimized**. Delete+reinsert counts two calls.

| Distribution | Add | Intersects | Contains | Nearest | Delete+reinsert |
| --- | ---: | ---: | ---: | ---: | ---: |
| uniform-3d | 2.370 → 1.970 | 11.947 → 4.605 | 14.339 → 4.404 | 30.605 → 7.054 | 3.619 → 2.543 |
| z-only | 1.889 → 1.591 | 12.575 → 3.506 | 11.226 → 3.449 | 50.806 → 2.818 | 1.907 → 1.632 |
| planar-xy | 2.981 → 2.145 | 30.525 → 6.651 | 39.107 → 6.819 | 28.585 → 3.495 | 5.469 → 2.264 |

The compatible API's query improvement is about 2.6–18.0 times in these
workloads. Incremental build and update times also improve. Estimated retained
index memory at 50k is about 13.6 MB before versus 7.0 MB after (approximately
49% less), excluding pre-existing caller input. The estimates use full-GC heap
deltas and are not exact allocator accounting. The revised R*-style writer can
allocate more temporary bytes during split sorting, despite retaining less
memory; the reports include this tradeoff. The warmed caller-buffer query path
allocates zero bytes per call in every recorded advanced workload.

[Advanced results](results-optimized-advanced.json) use 50k items, 500 queries per
workload, three rounds, capacities 10/8/16/32/64, both incremental/STR builds,
and separate caller-owned result buffers. This is a different API path and
matrix from the compatible comparison above. For STR capacity 16:

| Distribution | Intersects µs | Contains µs | Nearest µs | Mean Intersects matches | Mean nearest matches |
| --- | ---: | ---: | ---: | ---: | ---: |
| uniform-3d | 2.066 | 2.258 | 3.872 | 11.74 | 0.80 |
| z-only | 1.659 | 1.728 | 1.950 | 124.57 | 24.77 |
| planar-xy | 3.582 | 3.074 | 2.352 | 190.20 | 0.97 |

The best capacity varies by workload: the sweep does not establish a universal
replacement for the backward-compatible default 10. Packed nodes generally
reduce visits in these samples; means of nodes/entry tests are in the JSON.

The advanced mixed-read benchmark uses STR capacity 16, consumes all returned
IDs to verify an order-independent checksum, and excludes worker startup and
warm-up. Aggregate throughput is queries/second, not a latency percentile:

| Distribution | One reader | Two readers | Four readers |
| --- | ---: | ---: | ---: |
| uniform-3d | 395,342 | 751,763 | 1,322,978 |
| z-only | 639,780 | 1,385,650 | 2,053,942 |
| planar-xy | 344,624 | 647,989 | 1,093,094 |

Four-reader throughput improves about 3.2–3.3 times in this host/workload. It
includes output checksum work. Writers and Rebuild hold an exclusive lock, so
these read-only rates do not represent read/write latency under contention.

[Million-item results](results-million-str.json) use STR capacity 16, 32 fixed
queries per workload and three measured rounds; they are a larger-scale check,
not an individual-query tail-latency study. Every query still has a brute-force
oracle. All three indexes retain approximately 92.7 MiB (excluding caller input).

| Distribution | Bulk build ms | Intersects µs | Contains µs | Nearest µs | Mean Intersects matches |
| --- | ---: | ---: | ---: | ---: | ---: |
| uniform-3d | 771.2 | 28.639 | 24.767 | 9.492 | 240.88 |
| z-only | 368.9 | 117.946 | 90.369 | 35.624 | 2513.75 |
| planar-xy | 514.1 | 166.952 | 155.057 | 7.550 | 3808.66 |

Range-query latency grows with the number of returned items; the Z-only and
planar million-item queries emit thousands of items. All warmed reusable and
parallel rows in the advanced and million-item runs report 0 B/op. Input
construction, tree construction, context growth and result-list growth can
allocate and are not covered by that zero-allocation claim.

Reproduce the million-item packed-only matrix with:

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/Performance/Performance.csproj -c Release \
  -p:OptimizedApi=true -- \
  --bulk-only --sizes 1000000 --queries 32 --rounds 3 \
  --capacities 16 --workers 1,2,4 --concurrent-repeats 2 \
  --label million-str --json validation/Performance/results-million-str.json
```
