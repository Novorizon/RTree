# RTree

An in-memory, three-dimensional R-tree for axis-aligned boxes and unique items.
The library preserves its existing public API and .NET Framework 4.5 project.
Its implementation uses managed arrays; it does not require unsafe code or
native memory.

```csharp
var tree = new RTree.RTree<string>();
var box = new RTree.Rectangle(new[] { 0.0, 0, 0 }, new[] { 1.0, 1, 1 });
tree.Add(box, "item");
var hits = tree.Intersects(box);
var nearest = tree.Nearest(new RTree.Point(2, 2, 2), double.PositiveInfinity);
tree.Delete(box, "item");
```

`Intersects` includes touching boundaries. `Contains(query)` returns indexed
boxes fully contained by the query. `Nearest(point, maximumDistance)` returns
all items tied at the closest distance within the inclusive limit; it does not
return every item in a radius. Positive infinity means no distance limit.
Result order is unspecified. The original query parameter names are preserved
for callers using named arguments.

Items must be non-null, unique according to their equality/hash implementation,
and retain stable equality and hash codes while indexed. Duplicate additions
throw `ArgumentException`. Equality/hash implementations must not reenter the
same tree. `Delete` requires the item's exact geometric bounds and returns
`false` for a missing item or mismatched bounds. Empty trees have `Count == 0`
and `getBounds() == null`. Bounds are returned as a copy.

Coordinates must be finite. Array bounds must have three coordinates and
`min[d] <= max[d]`; the six-scalar constructor accepts either corner order,
using its existing `(x1, y1, x2, y2, z1, z2)` parameter order. Null arguments,
non-finite coordinates, and negative/NaN nearest limits are rejected before
modifying the index. Geometric comparisons use double precision, with scaled
fallbacks for extremely small and large distances.

## Reusable, allocation-free query path

The existing APIs return a newly allocated list. New overloads clear and fill a
caller-owned `List<T>` and return the match count. A reusable `QueryContext`
holds traversal buffers and the nearest-node priority queue:

```csharp
var results = new System.Collections.Generic.List<string>(256);
var context = new RTree.QueryContext();
int count = tree.Intersects(box, results, context);
count = tree.Contains(box, results, context);
count = tree.Nearest(new RTree.Point(2, 2, 2), 100, results, context);
```

Once the result list and workspace have sufficient capacity, this path performs
no managed allocation per query. Capacity growth can allocate; reserve enough
result capacity and warm the context for the workload. Convenience overloads
that accept only the result list use a reusable thread-local workspace. Reuse
input `Point`/`Rectangle` objects too when measuring application allocations.

Use `new QueryContext(true)` to collect `NodesVisited`, `EntriesTested`, and
`ResultCount` for the latest query. Statistics are optional and disabled by
default. Entry tests count actual descendant geometry tests; a subtree fully
covered by the range query skips its remaining geometry tests while still
visiting nodes and emitting every result.

## Packed construction and node capacity

For a known dataset, build with STR packing instead of repeated insertion:

```csharp
var pairs = new[] {
    new System.Collections.Generic.KeyValuePair<RTree.Rectangle, string>(box, "item")
};
var packed = RTree.RTree<string>.BulkLoad(pairs, 32, 16);
packed.Rebuild(); // Repack an existing tree after accumulated updates.
```

The two capacity arguments are maximum and minimum entries per node. As in the
original constructor, maximum values below two use the default 10; an invalid
minimum uses half the maximum. The no-capacity `BulkLoad` overload also uses
10/5. STR detects varying center axes, so planar and linear datasets do not get
partitioned by constant coordinates. Every packed level remains balanced and
satisfies the minimum occupancy. Packed trees support subsequent adds/deletes.

Incremental construction uses an R*-style split: select the axis by summed
margins, then the partition by overlap and size. Surface/edge metrics handle
degenerate boxes. This is not a complete R*-tree implementation with forced
reinsertion. `Rebuild` compacts/reorders the node pool; item slots retain their
high-water capacity and are recycled on later additions.

## Concurrency and storage

Multiple threads can query the same instance concurrently. Each active query
must have its own result list and explicit context; default APIs use independent
thread-local scratch. Concurrent reuse of one explicit context is rejected.
Context statistics describe only its most recent query.

A reader/writer lock synchronizes all operations. Add/Delete/Rebuild exclude
readers and other writers; each query sees one consistent tree state. Rebuild
holds the write lock throughout packing. Separate API calls, such as Count and
a later query, are not one atomic snapshot. Reader throughput depends on result
sizes, tree shape, memory bandwidth, and lock contention.

Internally, each box is six inline doubles in a contiguous entry array. Nodes
use array indices, result items use direct slots, and only item-to-slot lookup
uses a dictionary. There are no per-entry Rectangle objects or coordinate
arrays in the index. Retired nodes/item slots are cleared and recycled. Nearest
search is best-first using box-distance lower bounds; ordinary distance
comparisons use squared values, with scaled extreme-value fallbacks.
Near-tie and radius-boundary checks preserve rounded distance semantics.

## Correctness checks

The dependency-free validation projects require .NET 8 and compile all library
source files. They do not change the legacy library's target framework. No NuGet
packages or network access are needed.

```sh
dotnet run --project validation/Correctness/Correctness.csproj -c Release
```

The suite covers the original deletion/data-loss and geometry regressions,
slot recycling/reference release, STR packing boundaries, Rebuild, caller-owned
buffers/statistics, zero-allocation warmed queries, eight concurrent readers,
and concurrent readers plus a writer. It also runs 168 fixed-seed randomized
scenarios across capacities 2/3/4/10 and uniform, point, nested, Z-only,
identical, planar, and linear data. Mixed updates are checked against independent
brute-force queries; every mutation checks bounds, depth, occupancy, item slots,
and the exact partition of pool IDs into live/free slots. Every scenario ends
with a shuffled complete drain. Use `-- --regressions-only` for a quick run.

The recorded optimized run passed 18 regression groups, 140,437 mutations,
100,188 differential query comparisons and 25,934,298 node invariant checks.
All library sources also compile with C# 5 language restrictions.

GitHub Actions runs the full suite on pushes and pull requests. The original
.NET Framework 4.5 project still needs its targeting pack; validation on .NET 8
and C# 5 compilation do not establish behavior on every legacy runtime.

## Performance checks

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/Performance/Performance.csproj -c Release \
  -p:OptimizedApi=true -- \
  --sizes 10000,50000 --queries 1000 --rounds 5 \
  --label optimized --json validation/Performance/results-optimized.json
```

The benchmark checks every measured query against a brute-force oracle outside
timing. Compare the same allocating API with its pre-optimization source first;
reused-buffer, STR/fanout, and concurrent workloads are reported separately.
Reports include allocations, retained-memory estimates, node visits, result
sizes and checksums. See [benchmark instructions and results](validation/Performance/README.md).
