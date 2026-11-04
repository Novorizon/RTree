#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using RTree;

internal static class Program
{
    private const int Seed = 180731;
    private static long consumed;
    private static readonly Bounds3 World = new Bounds3(-2048, -2048, -2048, 2048, 2048, 2048);

    private sealed class Options
    {
        internal int[] Sizes = { 10000, 100000 };
        internal string[] Profiles = { "uniform", "cluster", "mixedsize", "planar" };
        internal HashSet<string> Workloads = new HashSet<string>(new[] { "range", "box-ray", "triangle-ray", "kd", "update" }, StringComparer.Ordinal);
        internal int Queries = 128, Rounds = 3, Updates = 1000, K = 16;
        internal string? Json;
    }

    private sealed class Dataset
    {
        internal string Profile = "";
        internal Bounds3[] Boxes = Array.Empty<Bounds3>();
        internal Rectangle[] Rectangles = Array.Empty<Rectangle>();
        internal Vector3d[] Points = Array.Empty<Vector3d>();
        internal Triangle3[] Triangles = Array.Empty<Triangle3>();
        internal KeyValuePair<Bounds3, int>[] BoxItems = Array.Empty<KeyValuePair<Bounds3, int>>();
        internal KeyValuePair<Rectangle, int>[] RectangleItems = Array.Empty<KeyValuePair<Rectangle, int>>();
        internal KeyValuePair<Vector3d, int>[] PointItems = Array.Empty<KeyValuePair<Vector3d, int>>();
        internal KeyValuePair<Triangle3, int>[] TriangleItems = Array.Empty<KeyValuePair<Triangle3, int>>();
        internal Bounds3[] Ranges = Array.Empty<Bounds3>();
        internal Sphere3[] Spheres = Array.Empty<Sphere3>();
        internal Frustum3[] Frusta = Array.Empty<Frustum3>();
        internal Vector3d[] QueryPoints = Array.Empty<Vector3d>();
        internal Ray3[] Rays = Array.Empty<Ray3>();
        internal int[] UpdateIds = Array.Empty<int>();
        internal Bounds3[] Replacements = Array.Empty<Bounds3>();
        internal Rectangle[] ReplacementRectangles = Array.Empty<Rectangle>();
        internal Bounds3[] UpdatedBoxes = Array.Empty<Bounds3>();
    }

    private sealed class Row
    {
        public string Profile { get; set; } = "";
        public int Items { get; set; }
        public string Workload { get; set; } = "";
        public string Implementation { get; set; } = "";
        public int Operations { get; set; }
        public int Rounds { get; set; }
        public int ApiCallsPerOperation { get; set; } = 1;
        public double MedianMilliseconds { get; set; }
        public double MicrosecondsPerOperation { get; set; }
        public double OperationsPerSecond { get; set; }
        public double AllocatedBytesPerOperation { get; set; }
        public double? MeanMatches { get; set; }
        public double? MeanNodesVisited { get; set; }
        public double? MeanEntriesTested { get; set; }
        public double? MeanPrimitiveTests { get; set; }
        public double? MeanStructuralChanges { get; set; }
        public string Checksum { get; set; } = "";
    }

    private sealed class Report
    {
        public string Framework { get; set; } = RuntimeInformation.FrameworkDescription;
        public string OperatingSystem { get; set; } = RuntimeInformation.OSDescription;
        public string Architecture { get; set; } = RuntimeInformation.ProcessArchitecture.ToString();
        public string Processor { get; set; } = ProcessorName();
        public int LogicalProcessors { get; set; } = Environment.ProcessorCount;
        public bool ServerGC { get; set; } = GCSettings.IsServerGC;
        public string TieredCompilation { get; set; } = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default";
        public int Seed { get; set; } = Program.Seed;
        public int Queries { get; set; }
        public int Rounds { get; set; }
        public int K { get; set; }
        public string Methodology { get; set; } = "Fixed seed; independent brute-force geometry and point-distance oracles; complete result-ID/multiplicity and ray/neighbor-distance checks before and after each measured round. Build/input/oracle/check/statistics are outside query timing. Each index materializes the same exact results into preallocated reusable Lists with one QueryContext; no per-query sorting is added unless required by the API. A full warm-up precedes timing. Median elapsed time and median per-thread allocated bytes across rounds; full GC before each sample outside timing. Diagnostic node/primitive counts use a separate untimed pass. Build operations count indexed objects; update operations count moved objects (RTree uses two API calls: Delete+Add). Updates use a fresh index each round and precreated old/new bounds; static structures do not appear in update rows. Box and precise triangle raycasts are different workloads. KD radius and sorted kNN use a separate point dataset and materializing scan baselines. No concurrency or GPU claims. Shared-host timings are not universal rankings.";
        public List<Row> Results { get; set; } = new List<Row>();
    }

    private readonly struct Sample
    {
        internal readonly double Milliseconds;
        internal readonly long Bytes;
        internal Sample(double milliseconds, long bytes) { Milliseconds = milliseconds; Bytes = bytes; }
    }

    private sealed class BoxIndex
    {
        internal string Name = "";
        internal IBoundsQuery3<int> Bounds = null!;
        internal IRayQuery3<int> Rays = null!;
        internal RTree<int>? Rtree;
        internal DynamicAabbTree<int>? Dynamic;
        internal LooseOctree<int>? Octree;
        internal ProxyHandle[] Handles = Array.Empty<ProxyHandle>();
        internal bool Mutable { get { return Rtree != null || Dynamic != null || Octree != null; } }

        internal int Move(Dataset data)
        {
            int structural = 0;
            for (int i = 0; i < data.UpdateIds.Length; i++)
            {
                int id = data.UpdateIds[i];
                if (Dynamic != null)
                {
                    if (Dynamic.Move(Handles[id], data.Replacements[i])) structural++;
                }
                else if (Octree != null)
                {
                    if (Octree.Move(Handles[id], data.Replacements[i])) structural++;
                }
                else
                {
                    if (!Rtree!.Delete(data.Rectangles[id], id)) throw new InvalidOperationException("RTree update could not delete item " + id);
                    Rtree.Add(data.ReplacementRectangles[i], id);
                    structural++;
                }
            }
            return structural;
        }
    }

    private sealed class QueryCase
    {
        internal Action<int, QueryContext> Fill = null!;
        internal Func<int> Count = null!;
        internal Action<int> Verify = null!;
        internal string Checksum = "";
        internal bool HasTraversalStatistics;
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--help"))
            {
                Console.WriteLine("SpatialPerformance --sizes 10000,100000 --profiles uniform,cluster,mixedsize,planar --queries 128 --rounds 3 --updates 1000 --k 16 --workloads range,box-ray,triangle-ray,kd,update --json results.json");
                Console.WriteLine("Additional optional workloads: sphere,frustum. Release build required. Set DOTNET_TieredCompilation=0 for stable warmed measurements.");
                return 0;
            }
#if DEBUG
            throw new InvalidOperationException("Use a Release build (-c Release) for performance measurements.");
#endif
            Options options = Parse(args);
            var report = new Report { Queries = options.Queries, Rounds = options.Rounds, K = options.K };
            Console.WriteLine(report.Framework + " | " + report.OperatingSystem + " | " + report.Architecture);
            Console.WriteLine(report.Processor + " | CPUs=" + report.LogicalProcessors + " | serverGC=" + report.ServerGC + " | tiered=" + report.TieredCompilation);
            Console.WriteLine("seed=" + Seed + " queries=" + options.Queries + " rounds=" + options.Rounds + " k=" + options.K);
            Console.WriteLine("profile,items,workload,implementation,operations,median_ms,us_per_op,allocated_B_per_op,mean_matches,mean_nodes,mean_entries_tested,mean_primitive_tests,checksum");
            foreach (string profile in options.Profiles)
            foreach (int size in options.Sizes)
            {
                Dataset data = Generate(profile, size, options);
                BenchmarkDataset(report, data, options);
                WriteJson(report, options.Json);
            }
            WriteJson(report, options.Json);
            GC.KeepAlive(consumed);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            return 1;
        }
    }

    private static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (i + 1 == args.Length) throw new ArgumentException("Missing value for " + name);
            string value = args[++i];
            switch (name)
            {
                case "--sizes": options.Sizes = IntList(value); break;
                case "--profiles": options.Profiles = value.Split(','); break;
                case "--queries": options.Queries = Positive(value); break;
                case "--rounds": options.Rounds = Positive(value); break;
                case "--updates": options.Updates = Positive(value); break;
                case "--k": options.K = Positive(value); break;
                case "--workloads": options.Workloads = new HashSet<string>(value.Split(','), StringComparer.Ordinal); break;
                case "--json": options.Json = Path.GetFullPath(value); break;
                default: throw new ArgumentException("Unknown option " + name);
            }
        }
        foreach (string profile in options.Profiles)
            if (!new[] { "uniform", "cluster", "mixedsize", "planar" }.Contains(profile)) throw new ArgumentException("Unknown profile " + profile);
        foreach (string workload in options.Workloads)
            if (!new[] { "range", "box-ray", "triangle-ray", "kd", "update", "sphere", "frustum" }.Contains(workload)) throw new ArgumentException("Unknown workload " + workload);
        if (options.Profiles.Length == 0 || options.Workloads.Count == 0) throw new ArgumentException("Select at least one profile and workload.");
        return options;
    }

    private static int Positive(string value)
    {
        int result = int.Parse(value, CultureInfo.InvariantCulture);
        if (result < 1) throw new ArgumentOutOfRangeException(nameof(value), "Values must be positive.");
        return result;
    }

    private static int[] IntList(string value) { return value.Split(',').Select(Positive).ToArray(); }

    private static Dataset Generate(string profile, int size, Options options)
    {
        var random = new Random(Seed + size + Array.IndexOf(new[] { "uniform", "cluster", "mixedsize", "planar" }, profile) * 193);
        var data = new Dataset { Profile = profile, Boxes = new Bounds3[size], Rectangles = new Rectangle[size], Points = new Vector3d[size], Triangles = new Triangle3[size],
            BoxItems = new KeyValuePair<Bounds3, int>[size], RectangleItems = new KeyValuePair<Rectangle, int>[size], PointItems = new KeyValuePair<Vector3d, int>[size], TriangleItems = new KeyValuePair<Triangle3, int>[size] };
        for (int i = 0; i < size; i++)
        {
            Vector3d point = RandomPoint(random, profile, i);
            double hx = 0.25 + random.NextDouble() * 2.75, hy = 0.25 + random.NextDouble() * 2.75, hz = 0.25 + random.NextDouble() * 2.75;
            if (profile == "mixedsize" && i % 50 == 0) { hx *= 20; hy *= 20; hz *= 20; }
            if (profile == "planar") hz = 0;
            Bounds3 box = new Bounds3(point.X - hx, point.Y - hy, point.Z - hz, point.X + hx, point.Y + hy, point.Z + hz);
            var triangle = new Triangle3(new Vector3d(point.X - hx, point.Y - hy, point.Z), new Vector3d(point.X + hx, point.Y - hy, point.Z), new Vector3d(point.X, point.Y + hy, point.Z));
            data.Points[i] = point; data.Boxes[i] = box; data.Rectangles[i] = Legacy(box); data.Triangles[i] = triangle;
            data.BoxItems[i] = new KeyValuePair<Bounds3, int>(box, i);
            data.RectangleItems[i] = new KeyValuePair<Rectangle, int>(data.Rectangles[i], i);
            data.PointItems[i] = new KeyValuePair<Vector3d, int>(point, i);
            data.TriangleItems[i] = new KeyValuePair<Triangle3, int>(triangle, i);
        }
        int count = options.Queries;
        data.Ranges = new Bounds3[count]; data.Spheres = new Sphere3[count]; data.Frusta = new Frustum3[count];
        data.QueryPoints = new Vector3d[count]; data.Rays = new Ray3[count];
        for (int q = 0; q < count; q++)
        {
            Vector3d point = q % 5 == 0 ? RandomPoint(random, "uniform", q) : data.Points[random.Next(size)];
            const double radius = 35;
            data.QueryPoints[q] = point;
            data.Ranges[q] = new Bounds3(point.X - radius, point.Y - radius, point.Z - radius, point.X + radius, point.Y + radius, point.Z + radius);
            data.Spheres[q] = new Sphere3(point, radius);
            data.Frusta[q] = BoxFrustum(data.Ranges[q]);
            if (q % 2 == 0)
            {
                var origin = new Vector3d(point.X + 70, point.Y - 45, point.Z + 700);
                data.Rays[q] = new Ray3(origin, new Vector3d(-70, 45, -700), 0, 2);
            }
            else data.Rays[q] = new Ray3(new Vector3d(point.X + 17, point.Y - 13, 800), new Vector3d(0, 0, -1), 0, 1600);
        }
        int updates = Math.Min(size, options.Updates);
        int[] ids = Enumerable.Range(0, size).ToArray();
        data.UpdateIds = new int[updates]; data.Replacements = new Bounds3[updates]; data.ReplacementRectangles = new Rectangle[updates];
        data.UpdatedBoxes = (Bounds3[])data.Boxes.Clone();
        for (int i = 0; i < updates; i++)
        {
            int j = random.Next(i, size), id = ids[j]; ids[j] = ids[i]; ids[i] = id;
            double scale = i % 2 == 0 ? 0.025 : i % 4 == 1 ? 8 : 80;
            double dx = Signed(random) * scale, dy = Signed(random) * scale, dz = profile == "planar" ? 0 : Signed(random) * scale;
            Bounds3 b = data.Boxes[id];
            var replacement = new Bounds3(b.MinX + dx, b.MinY + dy, b.MinZ + dz, b.MaxX + dx, b.MaxY + dy, b.MaxZ + dz);
            data.UpdateIds[i] = id; data.Replacements[i] = replacement; data.ReplacementRectangles[i] = Legacy(replacement); data.UpdatedBoxes[id] = replacement;
        }
        return data;
    }

    private static Vector3d RandomPoint(Random random, string profile, int i)
    {
        if (profile == "cluster")
        {
            int cluster = i % 4;
            return new Vector3d((cluster % 2 == 0 ? -250 : 250) + Signed(random) * 65,
                (cluster / 2 == 0 ? -250 : 250) + Signed(random) * 65, (cluster % 2 == 0 ? -150 : 150) + Signed(random) * 65);
        }
        return new Vector3d(Signed(random) * 500, Signed(random) * 500, profile == "planar" ? 0 : Signed(random) * 500);
    }

    private static double Signed(Random random) { return random.NextDouble() * 2 - 1; }
    private static Rectangle Legacy(Bounds3 b) { return new Rectangle(b.MinX, b.MinY, b.MaxX, b.MaxY, b.MinZ, b.MaxZ); }
    private static Frustum3 BoxFrustum(Bounds3 b)
    {
        return new Frustum3(new Plane3(new Vector3d(1, 0, 0), -b.MinX), new Plane3(new Vector3d(-1, 0, 0), b.MaxX),
            new Plane3(new Vector3d(0, 1, 0), -b.MinY), new Plane3(new Vector3d(0, -1, 0), b.MaxY),
            new Plane3(new Vector3d(0, 0, 1), -b.MinZ), new Plane3(new Vector3d(0, 0, -1), b.MaxZ));
    }

    private static BoxIndex BuildBoxes(string name, Dataset data, bool handles)
    {
        if (name == "rtree-str")
        {
            RTree<int> tree = RTree<int>.BulkLoad(data.RectangleItems);
            return new BoxIndex { Name = name, Bounds = tree, Rays = tree, Rtree = tree };
        }
        if (name == "static-sah-bvh")
        {
            var tree = new StaticBvh<int>(data.BoxItems);
            return new BoxIndex { Name = name, Bounds = tree, Rays = tree };
        }
        var result = new BoxIndex { Name = name, Handles = handles ? new ProxyHandle[data.Boxes.Length] : Array.Empty<ProxyHandle>() };
        if (name == "dynamic-aabb")
        {
            var tree = new DynamicAabbTree<int>(); result.Dynamic = tree; result.Bounds = tree; result.Rays = tree;
            for (int i = 0; i < data.Boxes.Length; i++)
            {
                ProxyHandle handle = tree.Insert(data.Boxes[i], i);
                if (handles) result.Handles[i] = handle;
            }
        }
        else
        {
            var tree = new LooseOctree<int>(World); result.Octree = tree; result.Bounds = tree; result.Rays = tree;
            for (int i = 0; i < data.Boxes.Length; i++)
            {
                ProxyHandle handle = tree.Insert(data.Boxes[i], i);
                if (handles) result.Handles[i] = handle;
            }
        }
        return result;
    }

    private static void BenchmarkDataset(Report report, Dataset data, Options options)
    {
        var expected = new Dictionary<string, int[][]>();
        foreach (string kind in new[] { "range", "sphere", "frustum" })
            if (options.Workloads.Contains(kind)) expected[kind] = BoxOracle(data, data.Boxes, kind);
        RayHit<int>[][]? boxHits = options.Workloads.Contains("box-ray") ? RayOracle(data, false) : null;
        int[][]? updateBefore = options.Workloads.Contains("update") ? (expected.TryGetValue("range", out int[][]? ranges) ? ranges : BoxOracle(data, data.Boxes, "range")) : null;
        int[][]? updateAfter = options.Workloads.Contains("update") ? BoxOracle(data, data.UpdatedBoxes, "range") : null;
        int[][]? movedOracle = options.Workloads.Contains("update") ? MovedOracle(data) : null;
        bool boxWork = expected.Count != 0 || boxHits != null || options.Workloads.Contains("update");
        if (boxWork)
        foreach (string name in new[] { "rtree-str", "dynamic-aabb", "static-sah-bvh", "loose-octree" })
        {
            BoxIndex index = MeasureBuild(report, data, name, options.Rounds, () => BuildBoxes(name, data, false), b => ValidateAll(b.Bounds, data.Boxes.Length));
            foreach (KeyValuePair<string, int[][]> work in expected)
            {
                BenchmarkQuery(report, data, work.Key, name, options, IdCase(data, index.Bounds, work.Key, work.Value));
                if (name == "rtree-str") BenchmarkQuery(report, data, work.Key, "brute-force", options, IdCase(data, null, work.Key, work.Value));
            }
            if (boxHits != null)
            {
                BenchmarkQuery(report, data, "box-ray-all", name, options, RayCase(data, index.Rays, false, false, boxHits));
                BenchmarkQuery(report, data, "box-ray-closest", name, options, RayCase(data, index.Rays, false, true, boxHits));
                if (name == "rtree-str")
                {
                    BenchmarkQuery(report, data, "box-ray-all", "brute-force", options, RayCase(data, null, false, false, boxHits));
                    BenchmarkQuery(report, data, "box-ray-closest", "brute-force", options, RayCase(data, null, false, true, boxHits));
                }
            }
            if (options.Workloads.Contains("update") && index.Mutable) BenchmarkUpdates(report, data, name, options, updateBefore!, updateAfter!, movedOracle!);
        }
        if (options.Workloads.Contains("triangle-ray"))
        {
            RayHit<int>[][] hits = RayOracle(data, true);
            TriangleBvh<int> tree = MeasureBuild(report, data, "triangle-sah-bvh", options.Rounds,
                () => new TriangleBvh<int>(data.TriangleItems), b => { if (b.Count != data.Boxes.Length) throw new InvalidOperationException("Triangle build count mismatch"); }, "build-triangle");
            foreach (bool closest in new[] { false, true })
            {
                string kind = closest ? "triangle-ray-closest" : "triangle-ray-all";
                BenchmarkQuery(report, data, kind, "triangle-sah-bvh", options, RayCase(data, tree, true, closest, hits));
                BenchmarkQuery(report, data, kind, "brute-force-triangle", options, RayCase(data, null, true, closest, hits));
            }
        }
        if (options.Workloads.Contains("kd")) BenchmarkPoints(report, data, options);
    }

    private static T MeasureBuild<T>(Report report, Dataset data, string name, int rounds, Func<T> build, Action<T> verify, string workload = "build-box")
    {
        T warm = build(); verify(warm);
        var samples = new List<Sample>(rounds);
        T last = warm;
        for (int round = 0; round < rounds; round++)
        {
            FullGc();
            long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            last = build();
            long elapsed = Stopwatch.GetTimestamp() - start, bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            samples.Add(new Sample(elapsed * 1000.0 / Stopwatch.Frequency, bytes));
            verify(last);
        }
        AddRow(report, data, workload, name, data.Boxes.Length, samples, data.Boxes.Length.ToString(CultureInfo.InvariantCulture));
        GC.KeepAlive(warm);
        return last;
    }

    private static void ValidateAll(IBoundsQuery3<int> tree, int count)
    {
        var results = new List<int>(count);
        tree.Query(World, results, new QueryContext()); results.Sort();
        if (tree.Count != count || results.Count != count) throw new InvalidOperationException("Build count mismatch");
        for (int i = 0; i < count; i++) if (results[i] != i) throw new InvalidOperationException("Build lost or repeated ID " + i);
    }

    private static void BenchmarkQuery(Report report, Dataset data, string workload, string name, Options options, QueryCase query)
    {
        var context = new QueryContext();
        VerifyQueries(query, context, options.Queries);
        for (int q = 0; q < options.Queries; q++) query.Fill(q, context);
        var samples = new List<Sample>(options.Rounds);
        for (int round = 0; round < options.Rounds; round++)
        {
            VerifyQueries(query, context, options.Queries);
            FullGc();
            long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp(), matches = 0;
            for (int q = 0; q < options.Queries; q++) { query.Fill(q, context); matches += query.Count(); }
            long elapsed = Stopwatch.GetTimestamp() - start, bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            consumed ^= matches;
            samples.Add(new Sample(elapsed * 1000.0 / Stopwatch.Frequency, bytes));
            VerifyQueries(query, context, options.Queries);
        }
        var statistics = new QueryContext(true);
        double nodes = 0, entries = 0, primitives = 0, totalMatches = 0;
        for (int q = 0; q < options.Queries; q++)
        {
            query.Fill(q, statistics); query.Verify(q);
            nodes += statistics.NodesVisited; entries += statistics.EntriesTested; primitives += statistics.PrimitiveTests; totalMatches += query.Count();
        }
        Row row = AddRow(report, data, workload, name, options.Queries, samples, query.Checksum, print: false);
        row.MeanMatches = totalMatches / options.Queries;
        if (query.HasTraversalStatistics) { row.MeanNodesVisited = nodes / options.Queries; row.MeanEntriesTested = entries / options.Queries; row.MeanPrimitiveTests = primitives / options.Queries; }
        else { row.MeanEntriesTested = data.Boxes.Length; row.MeanPrimitiveTests = data.Boxes.Length; }
        Print(row);
    }

    private static void VerifyQueries(QueryCase query, QueryContext context, int count)
    {
        for (int q = 0; q < count; q++) { query.Fill(q, context); query.Verify(q); }
    }

    private static QueryCase IdCase(Dataset data, IBoundsQuery3<int>? tree, string kind, int[][] expected, Bounds3[]? boxes = null)
    {
        Bounds3[] source = boxes ?? data.Boxes;
        var buffer = new List<int>(source.Length);
        return new QueryCase
        {
            Fill = (q, context) =>
            {
                buffer.Clear();
                if (tree == null)
                {
                    for (int i = 0; i < source.Length; i++) if (Matches(source[i], data, q, kind)) buffer.Add(i);
                }
                else if (kind == "range") tree.Query(data.Ranges[q], buffer, context);
                else if (kind == "sphere") tree.QuerySphere(data.Spheres[q], buffer, context);
                else tree.QueryFrustum(data.Frusta[q], buffer, context);
            },
            Count = () => buffer.Count,
            Verify = q => CheckIds(buffer, expected[q], kind + " query " + q),
            Checksum = HashIds(expected), HasTraversalStatistics = tree != null
        };
    }

    private static int[][] BoxOracle(Dataset data, Bounds3[] boxes, string kind)
    {
        var result = new int[data.Ranges.Length][];
        for (int q = 0; q < result.Length; q++)
        {
            var found = new List<int>();
            for (int i = 0; i < boxes.Length; i++) if (Matches(boxes[i], data, q, kind)) found.Add(i);
            result[q] = found.ToArray();
        }
        return result;
    }

    private static bool Matches(Bounds3 b, Dataset data, int q, string kind)
    {
        // The frustum workload deliberately uses an orthographic box frustum;
        // the independent oracle is an interval test, not the library plane code.
        return kind == "sphere" ? SphereBox(data.QueryPoints[q], 35, b) : BoxesOverlap(data.Ranges[q], b);
    }

    private static bool BoxesOverlap(Bounds3 a, Bounds3 b)
    {
        return a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY && a.MinZ <= b.MaxZ && a.MaxZ >= b.MinZ;
    }

    private static bool SphereBox(Vector3d center, double radius, Bounds3 b)
    {
        double dx = Math.Max(Math.Max(b.MinX - center.X, center.X - b.MaxX), 0);
        double dy = Math.Max(Math.Max(b.MinY - center.Y, center.Y - b.MaxY), 0);
        double dz = Math.Max(Math.Max(b.MinZ - center.Z, center.Z - b.MaxZ), 0);
        return dx * dx + dy * dy + dz * dz <= radius * radius;
    }

    private static void CheckIds(List<int> actual, int[] expected, string label)
    {
        actual.Sort();
        if (actual.Count != expected.Length) throw new InvalidOperationException(label + ": count " + actual.Count + " expected " + expected.Length);
        for (int i = 0; i < expected.Length; i++) if (actual[i] != expected[i]) throw new InvalidOperationException(label + ": result ID/multiplicity mismatch");
    }

    private static QueryCase RayCase(Dataset data, IRayQuery3<int>? tree, bool triangles, bool closest, RayHit<int>[][] expected)
    {
        var buffer = new List<RayHit<int>>(data.Boxes.Length);
        bool found = false;
        RayHit<int> closestHit = default;
        return new QueryCase
        {
            Fill = (q, context) =>
            {
                buffer.Clear(); found = false;
                Ray3 ray = data.Rays[q];
                if (tree != null)
                {
                    if (closest) found = tree.RaycastClosest(ray, out closestHit, context);
                    else tree.RaycastAll(ray, buffer, context);
                    return;
                }
                double best = double.PositiveInfinity;
                for (int i = 0; i < data.Boxes.Length; i++)
                {
                    double parameter;
                    bool hit = triangles ? TriangleRay(data.Triangles[i], ray, out parameter) : BoxRay(data.Boxes[i], ray, out parameter);
                    if (!hit) continue;
                    if (!closest) buffer.Add(new RayHit<int>(i, parameter));
                    else if (!found || parameter < best)
                    {
                        found = true; best = parameter; closestHit = new RayHit<int>(i, parameter);
                    }
                }
                // Match the providers' ascending-parameter RaycastAll contract.
                if (!closest) SortBuffer(buffer, RayParameterComparer.Instance);
            },
            Count = () => closest ? (found ? 1 : 0) : buffer.Count,
            Verify = q =>
            {
                RayHit<int>[] wanted = expected[q];
                if (!closest)
                {
                    for (int i = 1; i < buffer.Count; i++)
                        if (buffer[i - 1].Parameter > buffer[i].Parameter) throw new InvalidOperationException("Ray-all results are not sorted by parameter");
                    buffer.Sort(RayIdComparer.Instance);
                    if (buffer.Count != wanted.Length) throw new InvalidOperationException("Ray-all query " + q + ": count mismatch");
                    for (int i = 0; i < wanted.Length; i++)
                        if (buffer[i].Item != wanted[i].Item || !Close(buffer[i].Parameter, wanted[i].Parameter)) throw new InvalidOperationException("Ray-all query " + q + ": ID/parameter mismatch");
                    return;
                }
                if (found != (wanted.Length != 0)) throw new InvalidOperationException("Closest-ray query " + q + ": presence mismatch");
                if (!found) return;
                double minimum = wanted.Min(h => h.Parameter);
                bool validItem = false;
                foreach (RayHit<int> hit in wanted)
                    if (hit.Item == closestHit.Item && Close(hit.Parameter, closestHit.Parameter)) { validItem = true; break; }
                // Any equal-distance source primitive is valid for a closest hit.
                if (!validItem || !Close(closestHit.Parameter, minimum)) throw new InvalidOperationException("Closest-ray query " + q + ": nearest hit mismatch");
            },
            Checksum = HashRays(expected), HasTraversalStatistics = tree != null
        };
    }

    private sealed class RayIdComparer : IComparer<RayHit<int>>
    {
        internal static readonly RayIdComparer Instance = new RayIdComparer();
        public int Compare(RayHit<int> a, RayHit<int> b) { return a.Item.CompareTo(b.Item); }
    }

    private sealed class RayParameterComparer : IComparer<RayHit<int>>
    {
        internal static readonly RayParameterComparer Instance = new RayParameterComparer();
        public int Compare(RayHit<int> a, RayHit<int> b)
        {
            int order = a.Parameter.CompareTo(b.Parameter);
            return order != 0 ? order : a.Item.CompareTo(b.Item);
        }
    }

    private static RayHit<int>[][] RayOracle(Dataset data, bool triangles)
    {
        var result = new RayHit<int>[data.Rays.Length][];
        for (int q = 0; q < result.Length; q++)
        {
            var found = new List<RayHit<int>>();
            for (int i = 0; i < data.Boxes.Length; i++)
            {
                double parameter;
                bool hit = triangles ? TriangleRay(data.Triangles[i], data.Rays[q], out parameter) : BoxRay(data.Boxes[i], data.Rays[q], out parameter);
                if (hit) found.Add(new RayHit<int>(i, parameter));
            }
            result[q] = found.ToArray(); // Source IDs are already in ascending order.
        }
        return result;
    }

    private static bool BoxRay(Bounds3 box, Ray3 ray, out double parameter)
    {
        double low = ray.TMin, high = ray.TMax;
        if (!Slab(ray.Origin.X, ray.Direction.X, box.MinX, box.MaxX, ref low, ref high) ||
            !Slab(ray.Origin.Y, ray.Direction.Y, box.MinY, box.MaxY, ref low, ref high) ||
            !Slab(ray.Origin.Z, ray.Direction.Z, box.MinZ, box.MaxZ, ref low, ref high)) { parameter = 0; return false; }
        parameter = low; return true;
    }

    private static bool Slab(double origin, double direction, double minimum, double maximum, ref double low, ref double high)
    {
        if (direction == 0) return origin >= minimum && origin <= maximum;
        double a = (minimum - origin) / direction, b = (maximum - origin) / direction;
        if (a > b) { double swap = a; a = b; b = swap; }
        low = Math.Max(low, a); high = Math.Min(high, b);
        return low <= high;
    }

    private static bool TriangleRay(Triangle3 triangle, Ray3 ray, out double parameter)
    {
        // Independent scalar Moller-Trumbore oracle, without library geometry helpers.
        double ux = triangle.B.X - triangle.A.X, uy = triangle.B.Y - triangle.A.Y, uz = triangle.B.Z - triangle.A.Z;
        double vx = triangle.C.X - triangle.A.X, vy = triangle.C.Y - triangle.A.Y, vz = triangle.C.Z - triangle.A.Z;
        double px = ray.Direction.Y * vz - ray.Direction.Z * vy;
        double py = ray.Direction.Z * vx - ray.Direction.X * vz;
        double pz = ray.Direction.X * vy - ray.Direction.Y * vx;
        double determinant = ux * px + uy * py + uz * pz;
        parameter = 0;
        if (determinant == 0) return false;
        double tx = ray.Origin.X - triangle.A.X, ty = ray.Origin.Y - triangle.A.Y, tz = ray.Origin.Z - triangle.A.Z;
        double inverse = 1 / determinant;
        double u = (tx * px + ty * py + tz * pz) * inverse;
        if (u < 0 || u > 1) return false;
        double qx = ty * uz - tz * uy, qy = tz * ux - tx * uz, qz = tx * uy - ty * ux;
        double v = (ray.Direction.X * qx + ray.Direction.Y * qy + ray.Direction.Z * qz) * inverse;
        if (v < 0 || u + v > 1) return false;
        parameter = (vx * qx + vy * qy + vz * qz) * inverse;
        return parameter >= ray.TMin && parameter <= ray.TMax;
    }

    private static void BenchmarkPoints(Report report, Dataset data, Options options)
    {
        KdTree<int> tree = MeasureBuild(report, data, "point-kdtree", options.Rounds, () => new KdTree<int>(data.PointItems),
            b => { if (b.Count != data.Points.Length) throw new InvalidOperationException("KD build count mismatch"); }, "build-point");
        foreach (bool nearest in new[] { false, true })
        {
            Neighbor<int>[][] expected = PointOracle(data, nearest, options.K);
            string kind = nearest ? "point-knn-k" + options.K : "point-radius";
            BenchmarkQuery(report, data, kind, "point-kdtree", options, PointCase(data, tree, nearest, options.K, expected));
            BenchmarkQuery(report, data, kind, nearest ? "brute-force-topk" : "brute-force-sorted", options, PointCase(data, null, nearest, options.K, expected));
        }
    }

    private static QueryCase PointCase(Dataset data, IPointQuery3<int>? tree, bool nearest, int k, Neighbor<int>[][] expected)
    {
        var buffer = new List<Neighbor<int>>(nearest ? Math.Min(data.Points.Length, k) : data.Points.Length);
        return new QueryCase
        {
            Fill = (q, context) =>
            {
                buffer.Clear();
                Vector3d point = data.QueryPoints[q];
                if (tree != null)
                {
                    if (nearest) tree.KNearest(point, k, double.PositiveInfinity, buffer, context);
                    else tree.Radius(point, 35, buffer, context);
                    return;
                }
                if (nearest) ScanNearest(data.Points, point, k, buffer);
                else
                {
                    for (int i = 0; i < data.Points.Length; i++)
                    {
                        double distance2 = SquaredDistance(data.Points[i], point);
                        if (distance2 <= 35 * 35) buffer.Add(new Neighbor<int>(i, Math.Sqrt(distance2)));
                    }
                    // Radius API guarantees distance/input-order sorting too.
                    SortBuffer(buffer, NeighborDistanceComparer.Instance);
                }
            },
            Count = () => buffer.Count,
            Verify = q =>
            {
                Neighbor<int>[] wanted = expected[q];
                if (buffer.Count != wanted.Length) throw new InvalidOperationException("Point query " + q + ": count mismatch");
                for (int i = 0; i < wanted.Length; i++)
                    if (buffer[i].Item != wanted[i].Item || !Close(buffer[i].Distance, wanted[i].Distance)) throw new InvalidOperationException("Point query " + q + ": sorted neighbor ID/distance mismatch");
            },
            Checksum = HashNeighbors(expected), HasTraversalStatistics = tree != null
        };
    }

    private static void ScanNearest(Vector3d[] points, Vector3d query, int k, List<Neighbor<int>> buffer)
    {
        k = Math.Min(points.Length, k);
        // Bounded sorted top-k: no n-element result array, no full sort, no allocations.
        for (int i = 0; i < points.Length; i++)
        {
            double distance2 = SquaredDistance(points[i], query);
            // Keep squared distances until selection completes, so pruning does
            // not round-trip sqrt and accidentally exclude equal candidates.
            if (buffer.Count == k && distance2 > buffer[k - 1].Distance) continue;
            var candidate = new Neighbor<int>(i, distance2);
            int position = buffer.Count;
            while (position > 0 && NeighborDistanceComparer.Instance.Compare(candidate, buffer[position - 1]) < 0) position--;
            if (position >= k) continue;
            if (buffer.Count < k) buffer.Add(candidate);
            for (int j = buffer.Count - 1; j > position; j--) buffer[j] = buffer[j - 1];
            buffer[position] = candidate;
        }
        for (int i = 0; i < buffer.Count; i++) buffer[i] = new Neighbor<int>(buffer[i].Item, Math.Sqrt(buffer[i].Distance));
    }

    private sealed class NeighborDistanceComparer : IComparer<Neighbor<int>>
    {
        internal static readonly NeighborDistanceComparer Instance = new NeighborDistanceComparer();
        public int Compare(Neighbor<int> a, Neighbor<int> b)
        {
            int order = a.Distance.CompareTo(b.Distance);
            return order != 0 ? order : a.Item.CompareTo(b.Item);
        }
    }

    private static Neighbor<int>[][] PointOracle(Dataset data, bool nearest, int k)
    {
        var result = new Neighbor<int>[data.QueryPoints.Length][];
        for (int q = 0; q < result.Length; q++)
        {
            var all = new List<Neighbor<int>>(data.Points.Length);
            for (int i = 0; i < data.Points.Length; i++)
            {
                double distance2 = SquaredDistance(data.Points[i], data.QueryPoints[q]);
                if (nearest || distance2 <= 35 * 35) all.Add(new Neighbor<int>(i, Math.Sqrt(distance2)));
            }
            all.Sort(NeighborDistanceComparer.Instance);
            result[q] = nearest ? all.Take(k).ToArray() : all.ToArray();
        }
        return result;
    }

    private static double SquaredDistance(Vector3d a, Vector3d b)
    {
        double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
        return x * x + y * y + z * z;
    }

    private static void SortBuffer<T>(List<T> buffer, IComparer<T> comparer)
    {
        // List.Sort(IComparer<T>) creates a Comparison<T> adapter on some
        // runtimes. An in-place heapsort keeps the scan baseline allocation-free.
        for (int root = buffer.Count / 2 - 1; root >= 0; root--) Sift(buffer, root, buffer.Count, comparer);
        for (int end = buffer.Count - 1; end > 0; end--)
        {
            T swap = buffer[0]; buffer[0] = buffer[end]; buffer[end] = swap;
            Sift(buffer, 0, end, comparer);
        }
    }

    private static void Sift<T>(List<T> buffer, int root, int count, IComparer<T> comparer)
    {
        T value = buffer[root];
        while (root < count / 2)
        {
            int child = root * 2 + 1;
            if (child + 1 < count && comparer.Compare(buffer[child], buffer[child + 1]) < 0) child++;
            if (comparer.Compare(value, buffer[child]) >= 0) break;
            buffer[root] = buffer[child]; root = child;
        }
        buffer[root] = value;
    }

    private static void BenchmarkUpdates(Report report, Dataset data, string name, Options options, int[][] before, int[][] after, int[][] movedOracle)
    {
        BoxIndex warm = BuildBoxes(name, data, true);
        VerifyQueries(IdCase(data, warm.Bounds, "range", before), new QueryContext(), options.Queries);
        warm.Move(data);
        VerifyQueries(IdCase(data, warm.Bounds, "range", after), new QueryContext(), options.Queries);
        VerifyMovedBounds(warm, data, movedOracle);
        var samples = new List<Sample>(options.Rounds);
        long structures = 0;
        for (int round = 0; round < options.Rounds; round++)
        {
            BoxIndex index = BuildBoxes(name, data, true);
            VerifyQueries(IdCase(data, index.Bounds, "range", before), new QueryContext(), options.Queries);
            FullGc();
            long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            int changed = index.Move(data);
            long elapsed = Stopwatch.GetTimestamp() - start, bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            samples.Add(new Sample(elapsed * 1000.0 / Stopwatch.Frequency, bytes)); structures += changed;
            VerifyQueries(IdCase(data, index.Bounds, "range", after), new QueryContext(), options.Queries);
            VerifyMovedBounds(index, data, movedOracle);
        }
        Row row = AddRow(report, data, "move-object", name, data.UpdateIds.Length, samples, HashIds(after), print: false);
        row.ApiCallsPerOperation = name == "rtree-str" ? 2 : 1;
        row.MeanStructuralChanges = structures / (double)(options.Rounds * data.UpdateIds.Length);
        Print(row);
        GC.KeepAlive(warm);
    }

    private static int[][] MovedOracle(Dataset data)
    {
        var result = new int[data.UpdateIds.Length][];
        for (int q = 0; q < result.Length; q++)
        {
            Bounds3 target = data.UpdatedBoxes[data.UpdateIds[q]];
            var wanted = new List<int>();
            for (int i = 0; i < data.UpdatedBoxes.Length; i++) if (BoxesOverlap(target, data.UpdatedBoxes[i])) wanted.Add(i);
            result[q] = wanted.ToArray();
        }
        return result;
    }

    private static void VerifyMovedBounds(BoxIndex index, Dataset data, int[][] movedOracle)
    {
        if (index.Bounds.Count != data.Boxes.Length) throw new InvalidOperationException("Move changed index count");
        var buffer = new List<int>(data.Boxes.Length);
        var context = new QueryContext();
        // Every moved object is checked at its new location, even if random
        // workload queries happen not to cover it. Full query results are checked.
        for (int q = 0; q < data.UpdateIds.Length; q++)
        {
            int id = data.UpdateIds[q];
            Bounds3 target = data.UpdatedBoxes[id];
            index.Bounds.Query(target, buffer, context);
            CheckIds(buffer, movedOracle[q], "Move verification ID " + id);
        }
    }

    private static bool Close(double a, double b) { return Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b))); }

    private static Row AddRow(Report report, Dataset data, string workload, string name, int operations, List<Sample> samples, string checksum, bool print = true)
    {
        double elapsed = Median(samples.Select(s => s.Milliseconds));
        var row = new Row { Profile = data.Profile, Items = data.Boxes.Length, Workload = workload, Implementation = name, Operations = operations,
            Rounds = samples.Count, MedianMilliseconds = elapsed, MicrosecondsPerOperation = elapsed * 1000 / operations,
            OperationsPerSecond = elapsed == 0 ? 0 : operations * 1000 / elapsed,
            AllocatedBytesPerOperation = Median(samples.Select(s => (double)s.Bytes)) / operations, Checksum = checksum };
        report.Results.Add(row);
        if (print) Print(row);
        return row;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.OrderBy(v => v).ToArray();
        return ordered.Length % 2 != 0 ? ordered[ordered.Length / 2] : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
    }

    private static void Print(Row row)
    {
        Console.WriteLine(string.Join(",", row.Profile, row.Items, row.Workload, row.Implementation, row.Operations,
            F(row.MedianMilliseconds), F(row.MicrosecondsPerOperation), F(row.AllocatedBytesPerOperation),
            F(row.MeanMatches), F(row.MeanNodesVisited), F(row.MeanEntriesTested), F(row.MeanPrimitiveTests), row.Checksum));
    }

    private static string F(double? value) { return value.HasValue ? value.Value.ToString("0.######", CultureInfo.InvariantCulture) : ""; }

    private static void FullGc() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private static void WriteJson(Report report, string? path)
    {
        if (path == null) return;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ProcessorName()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
                if (line.StartsWith("model name", StringComparison.Ordinal)) return line.Substring(line.IndexOf(':') + 1).Trim();
        }
        catch (IOException) { }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    private static ulong Mix(ulong hash, ulong value) { unchecked { return (hash ^ value) * 1099511628211UL; } }

    private static string HashIds(int[][] values)
    {
        ulong hash = 14695981039346656037UL;
        foreach (int[] query in values)
        {
            hash = Mix(hash, (ulong)query.Length);
            foreach (int value in query) hash = Mix(hash, (ulong)value);
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static string HashRays(RayHit<int>[][] values)
    {
        ulong hash = 14695981039346656037UL;
        foreach (RayHit<int>[] query in values)
        {
            hash = Mix(hash, (ulong)query.Length);
            foreach (RayHit<int> value in query) { hash = Mix(hash, (ulong)value.Item); hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(value.Parameter)); }
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static string HashNeighbors(Neighbor<int>[][] values)
    {
        ulong hash = 14695981039346656037UL;
        foreach (Neighbor<int>[] query in values)
        {
            hash = Mix(hash, (ulong)query.Length);
            foreach (Neighbor<int> value in query) { hash = Mix(hash, (ulong)value.Item); hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(value.Distance)); }
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
