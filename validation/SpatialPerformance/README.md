# 3D spatial query performance

This dependency-free .NET 8 console project compiles `../../RTree/*.cs`. It
compares indexes only where their query semantics match. It does not change the
library target framework or require a GPU.

Run from the repository root on an otherwise idle machine:

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/SpatialPerformance/SpatialPerformance.csproj -c Release -- \
  --sizes 10000,100000 --profiles uniform,cluster,mixedsize,planar \
  --queries 128 --rounds 3 --updates 1000 --k 16 \
  --json validation/SpatialPerformance/results-current.json
```

A small functional smoke run is different from a performance measurement:

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/SpatialPerformance/SpatialPerformance.csproj -c Release -- \
  --sizes 200 --profiles uniform,cluster,mixedsize,planar \
  --queries 12 --rounds 1 --updates 24
```

For a focused 100k-object comparison, select workloads explicitly. Sphere and
frustum are optional additional workloads; the default set is
`range,box-ray,triangle-ray,kd,update`.

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/SpatialPerformance/SpatialPerformance.csproj -c Release -- \
  --sizes 100000 --profiles uniform --queries 512 --rounds 3 \
  --workloads range,box-ray,kd --json /tmp/spatial-100k.json
```

| Workload | Compared implementations | Result semantics |
| --- | --- | --- |
| `range` | RTree STR, dynamic AABB tree, static SAH BVH, loose octree, scan | Every real object AABB intersecting the query AABB |
| `sphere` | Same box indexes and scan | Every real AABB intersecting an inclusive radius-35 sphere |
| `frustum` | Same box indexes and scan | Box candidates inside an orthographic six-plane frustum |
| `box-ray` | Same box indexes and scan | Separate closest-hit and all-hit rows for real AABBs |
| `triangle-ray` | Triangle SAH BVH and triangle scan | Separate closest-hit and all-hit rows using precise, two-sided triangle intersection |
| `kd` | Point KD-tree and point scans | Separate radius-35 and k-nearest queries; both return sorted neighbors |
| `update` | RTree Delete+Add, dynamic AABB Move, loose octree Move | Change a real object's bounds while retaining its payload |

Box-ray times do not represent precise triangle intersection times. Point KD
times do not represent indexing extended objects. Static BVH refit and rebuild
are different operations from proxy insertion/movement, so they are not included
in the dynamic movement comparison. Build rows are also separated into
`build-box`, `build-triangle`, and `build-point`.

The four deterministic profiles use seed `180731` plus fixed profile/size
offsets:

- `uniform`: object centers uniformly distributed in a 1000-unit cube.
- `cluster`: centers in four 130-unit cubes, testing overlapping subtrees and
  concentrated output.
- `mixedsize`: a uniform distribution with 2% of objects enlarged 20 times on
  each axis, testing uneven object sizes.
- `planar`: centers and zero-thickness object bounds on Z=0, testing degenerate
  spatial distributions.

Ordinary box half-extents are independently uniform in `[0.25,3]`. A fixed
octree world `[-2048,2048]^3` contains every generated object and movement;
root expansion is not part of these movement rows. Defaults for all tree
capacities and dynamic fat margins are retained. Object payloads are unique
integer IDs; validation compares multiplicities rather than discarding repeats.

Four out of five query centers are sampled from existing object centers; every
fifth is an independent uniform position. Range queries have half-extent 35.
Half the rays target a query center with an unnormalized oblique direction; half
are vertical offset rays, so workloads include hits and misses. Triangles are
nondegenerate XY triangles centered on the same points. KD kNN uses an unbounded
distance limit and configurable `--k` (default 16). Movement updates a
deterministic sample of up to `--updates` distinct objects: half receive a small
offset (at most 0.025 per axis); the remainder receive offsets of at most 8 or 80.
Planar objects stay on Z=0.

Input conversion, geometry objects, fixed result capacity, contexts, oracle
generation, validation, and diagnostics are outside query timing. Every query
materializes its full result into a reused `List<T>`; every active query owns a
context. The scan baseline also sorts all ray hits by parameter and both KD
result types by distance/source ID to match the API. Its bounded top-k selection
does not sort all N points. Untimed oracles use complete point sorting, scalar
ray slabs, independent Moller-Trumbore triangle intersections, AABB intervals,
and squared sphere-to-box distances. The frustum oracle uses direct AABB
intervals rather than the library plane implementation. The frustum workload
therefore measures orthographic culling, not every possible perspective scene.

Before and after **each** measured query round, all query result IDs and
multiplicities are checked. Ray parameters and neighbor distances are checked
too, using relative/absolute tolerance `1e-9`. Equal-distance closest ray hits
may select any eligible source primitive; KD tie order follows original input
order. The checksum comes from the independent oracle, and is printed only
after verification succeeds. Checksums should match between implementations of
the same workload/profile/size. Ray closest and all rows share an oracle checksum
for the complete hit set; validation of closest rows checks the minimum hit.

Each movement round starts from a freshly constructed index, built outside
timing. Full range results are independently checked before/after movements.
In addition, a query at every moved object's new bounds is compared against a
precomputed complete scan result, including objects that the workload queries
would miss. Old and replacement legacy Rectangles are created before timing.
Movement allocation therefore includes any first-use internal growth after
construction, but excludes driver handle tables and input geometry.

One full warm-up precedes measurement. Every sample starts after full GC outside
timing. Output reports median elapsed time and median
`GC.GetAllocatedBytesForCurrentThread()` across the configured rounds. Query
allocation should be read separately from build/movement allocation; a warmed
query can report 0 B while its index still consumes memory. Build allocations
include index construction and its input enumeration, but exclude precreated
input arrays and the update-only handle table. Allocation is cumulative
allocated bytes, **not retained index memory**.

Time per build operation is time per input object. Time per update operation is
time per **moved object**, so RTree uses two calls (`Delete` and `Add`) for one
logical move. JSON records `ApiCallsPerOperation` and the mean fraction of moves
that structurally reinsert or rebuild. Build mode, initial world extent, fat
margins, and distribution affect this fraction.

Mean match/node/entry/primitive-test counts come from a separate untimed pass with
statistics enabled. A primitive test means a real box, triangle, or point tested,
rather than every internal-node bound test. Entries tested also includes bound
traversal tests as recorded by each provider. The scan tests all N primitives;
its traversal-node field is empty. JSON also records CPU, OS, runtime, GC mode,
tiered compilation, seed, k, and round count. Low query counts are useful for
smoke tests; increase `--queries` and use at least three rounds for conclusions.
Run the old RTree benchmark separately when comparing the historical RTree
implementation. These single-thread measurements do not imply concurrent-reader
or GPU throughput. Shared-host results describe these workloads, not a universal
ranking of spatial trees.


## Recorded CPU implementation comparison

The checked-in [report](results-current.json) records 336 measured rows at
10,000 and 100,000 objects across uniform, clustered, mixed-size and planar
profiles. It used .NET 8.0.33, Debian 13, AMD EPYC 9V74 with four available
logical processors, workstation GC and `DOTNET_TieredCompilation=0`. Each query
workload uses 64 fixed queries and three measured rounds; movements use 200
objects per round. Workloads ran sequentially, including range, sphere,
frustum, box rays, precise triangle rays, point queries and updates. All measured
results and update validation passed the independent oracles.

Representative 100,000-object **uniform** results are median microseconds per
query or moved object. These are specific work/return counts, not universal tree
rankings. Static structures have no dynamic movement row.

| Box index | Range µs | Closest box ray µs | Move object µs |
| --- | ---: | ---: | ---: |
| rtree-str | 6.063 | 10.520 | 20.309 |
| dynamic-aabb | 47.600 | 72.309 | 2.645 |
| static-sah-bvh | 8.444 | 8.104 | — |
| loose-octree | 41.568 | 51.670 | 0.987 |

Uniform range queries return 35.5 real bounds on average. RTree's STR layout is
particularly effective for this query workload; the dynamic tree and octree
trade some query cost for much cheaper object movement in this sample. The
RTree movement path starts from tightly packed STR storage and includes its
first pool growth and reinsertion costs; this is not a steady-state allocation
claim for every future Delete/Add. Clustered octree movement can also incur pool
growth, shown separately in the JSON. Dynamic pair-cache discovery stays lazy
until QueryPairs/CachedPairCount is requested; pair enumeration is validated in
the correctness suite and is not timed by this matrix.

Other uniform 100,000-object workloads:

| Query | Index µs | Materializing scan µs |
| --- | ---: | ---: |
| Closest triangle ray | 12.865 | 1856.403 |
| Point radius | 5.572 | 162.987 |
| Point kNN (k=16) | 11.230 | 165.600 |

All warmed query rows in this recorded matrix allocate 0 B/op. This applies to
the measured ordinary geometry, pre-sized output buffers and warmed workspaces.
Triangle3 uses BigInteger exact fallback for difficult boundary, nearly parallel
or extreme-range predicates; such fallback can allocate, as can first buffer
and pool growth. It is tested separately rather than hidden in the fast-path
zero-allocation claim. Build/movement allocations remain in the report.

The clustered profile returns thousands of bounds and the planar profile
changes pruning behavior; inspect those rows before selecting a default index.
The scan and index must emit the same outputs before their timing is compared.
64 queries and three batches do not establish per-query p95/p99. No GPU or
concurrent throughput measurement is claimed by this single-thread matrix.

Reproduce the recorded run with:

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/SpatialPerformance/SpatialPerformance.csproj -c Release -- \
  --sizes 10000,100000 --profiles uniform,cluster,mixedsize,planar \
  --queries 64 --rounds 3 --updates 200 --k 16 \
  --workloads range,sphere,frustum,box-ray,triangle-ray,kd,update \
  --json validation/SpatialPerformance/results-current.json
```
