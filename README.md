# RTree

An in-memory, three-dimensional R-tree for axis-aligned boxes and unique items.
The library retains its existing .NET Framework 4.5 project and public API.

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
all items tied at the closest distance, if that distance is within the inclusive
limit; it does not return every item in a radius. Positive infinity means no
distance limit. Result order is unspecified.

Items must be non-null, unique according to their equality/hash implementation,
and retain stable equality and hash codes while indexed. Duplicate additions
throw `ArgumentException`. `Delete` requires the item's exact geometric bounds
and returns `false` for a missing item or mismatched bounds. An empty tree has
`Count == 0` and `getBounds() == null`. Bounds are returned as a copy.

Coordinates must be finite. Array bounds must have three coordinates and
`min[d] <= max[d]`; the six-scalar constructor accepts either corner order,
using its existing `(x1, y1, x2, y2, z1, z2)` parameter order. Null arguments,
non-finite coordinates, and negative/NaN nearest limits are rejected before
modifying the index. As with other floating-point spatial indexes, geometric
distance comparisons use double precision. Access to one tree instance must be
externally synchronized.

Insertion uses three-dimensional volume growth. Surface and edge growth break
ties for planar, linear, or point data. Retired node IDs are reused after their
old map entries are removed; root contraction also releases obsolete nodes.

## Correctness checks

The dependency-free validation projects require .NET 8 and compile the same
four source files as the legacy library project. They do not change its target
framework. No NuGet packages or network access are required to run the checks.

```sh
dotnet run --project validation/Correctness/Correctness.csproj -c Release
```

The suite includes deterministic regressions for node recycling and deletion
data loss, root bounds, failed additions, equality/hashing, derived boxes,
invalid arguments, nearest ties/limits, extremely large and small distances,
and item-ID wraparound. It also runs 168 deterministic randomized scenarios
with capacities 2/3/4/10 and seven distributions: uniform, points, nested,
Z-only, identical, planar, and linear. Mixed additions and deletions are checked
against independent brute-force queries; every mutation validates bounds,
levels, occupancy, live IDs, free IDs and both item dictionaries. Each scenario
ends by draining the tree completely. Use `-- --regressions-only` for a quick run.

The recorded full run passed 12 regression groups, 139,247 mutations and 93,787
differential query comparisons, with 37,046,885 node invariant checks. The four
library sources also compile with C# 5 language restrictions. These results
cover the recorded deterministic workloads, rather than all possible inputs.

GitHub Actions runs the full correctness suite on pushes and pull requests.
The original .NET Framework 4.5 project still requires its targeting pack;
the portable validation run does not establish compatibility with every legacy
runtime or substitute for testing in a deployment environment.

## Performance checks

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/Performance/Performance.csproj -c Release -- \
  --sizes 10000,50000 --queries 1000 --rounds 5 \
  --label fixed --json validation/Performance/results-fixed.json
```

The benchmark measures insertion, intersection, containment, nearest queries,
and deletion/reinsertion on uniform 3D, Z-only, and planar data. All measured
query results are validated against a brute-force oracle outside the timed
region. Reports include medians, allocation per operation, environment details,
and checksums. See [the benchmark documentation](validation/Performance/README.md)
for baseline comparison instructions and workload limitations.

