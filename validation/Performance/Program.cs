#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using RTree;

internal static partial class Program
{
    private const int Seed = 712367;
    private static readonly string[] Scenarios = { "uniform-3d", "z-only", "planar-xy" };

    private sealed class Options
    {
        internal int[] Sizes = { 10000, 50000 };
        internal int Queries = 200;
        internal int Rounds = 3;
        internal string Label = "current";
        internal string? JsonPath;
        internal bool AllowMutationFailure;
        internal bool Advanced;
        internal bool AdvancedOnly;
        internal int[] Capacities = { 10, 8, 16, 32, 64 };
        internal int[] Workers = { 1, 2, 4 };
        internal int ConcurrentRepeats = 4;
        internal bool BulkOnly;
    }

    private sealed class Dataset
    {
        internal Rectangle[] Boxes = Array.Empty<Rectangle>();
        internal Rectangle[] Ranges = Array.Empty<Rectangle>();
        internal Point[] Points = Array.Empty<Point>();
        internal double[] Radii = Array.Empty<double>();
        internal int[] MutationIds = Array.Empty<int>();
        internal Rectangle[] Replacements = Array.Empty<Rectangle>();
    }

    private sealed class Sample<T>
    {
        internal T Value = default!;
        internal double Milliseconds;
        internal long AllocatedBytes;
    }

    private sealed class Row
    {
        public string Scenario { get; set; } = "";
        public int Items { get; set; }
        public string Workload { get; set; } = "";
        public string Implementation { get; set; } = "";
        public int Operations { get; set; }
        public int CompletedRounds { get; set; }
        public double? MedianMilliseconds { get; set; }
        public double? MicrosecondsPerOperation { get; set; }
        public double? OperationsPerSecond { get; set; }
        public double? AllocatedBytesPerOperation { get; set; }
        public string? Checksum { get; set; }
        public string? Error { get; set; }
        public string? BuildMode { get; set; }
        public int NodeCapacity { get; set; } = 10;
        public int Threads { get; set; } = 1;
        public double? MeanMatches { get; set; }
        public double? MeanNodesVisited { get; set; }
        public double? MeanEntriesTested { get; set; }
        public long? RetainedIndexBytes { get; set; }
        public int LatencyBatchSamples { get; set; }
        public double? P50BatchMeanMicroseconds { get; set; }
        public double? P95BatchMeanMicroseconds { get; set; }
        public double? P99BatchMeanMicroseconds { get; set; }
    }

    private sealed class Report
    {
        public string Label { get; set; } = "";
        public string SourceDirectory { get; set; } = "";
        public string Framework { get; set; } = "";
        public string OperatingSystem { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Processor { get; set; } = "";
        public int LogicalProcessors { get; set; }
        public bool ServerGC { get; set; }
        public string TieredCompilation { get; set; } = "";
        public int Seed { get; set; }
        public int Rounds { get; set; }
        public int QueriesPerWorkload { get; set; }
        public string Methodology { get; set; } = "";
        public List<Row> Results { get; set; } = new List<Row>();
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--help"))
            {
                Console.WriteLine("Performance [--sizes 10000,50000] [--queries 200] [--rounds 3] [--label current] [--json path] [--allow-mutation-failure] [--advanced|--advanced-only|--bulk-only] [--capacities 10,8,16,32,64] [--workers 1,2,4] [--concurrent-repeats 4]");
                Console.WriteLine("Build with -p:SourceDirectory=/absolute/path/to/original/sources to compare an unmodified baseline.");
                Console.WriteLine("Build with -p:OptimizedApi=true to enable reusable buffers, STR, capacity sweep and parallel-reader measurements.");
                return 0;
            }
#if DEBUG
            throw new InvalidOperationException("Performance measurements require a Release build (-c Release).");
#endif
            var options = Parse(args);
#if !OPTIMIZED_API
            if (options.Advanced || options.AdvancedOnly)
                throw new ArgumentException("Advanced measurements require -p:OptimizedApi=true and the optimized source revision.");
#endif
            var report = new Report
            {
                Label = options.Label,
                SourceDirectory = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .First(a => a.Key == "RTreeSourceDirectory").Value ?? "",
                Framework = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Processor = ProcessorName(),
                LogicalProcessors = Environment.ProcessorCount,
                ServerGC = GCSettings.IsServerGC,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
                Seed = Seed,
                Rounds = options.Rounds,
                QueriesPerWorkload = options.Queries,
                Methodology = "Deterministic datasets and independent brute-force oracles; one full warm-up; median of measured rounds; full GC before each sample outside timing. Compatibility rows use identical List-return APIs and materialize results for both tree and scan. Reusable-buffer rows are a separate workload: buffers/context are prewarmed, all result IDs checked before and after each round outside timing, and total match counts are consumed inside timing. Diagnostic node/entry/match statistics are collected in a separate untimed pass. Allocation uses GC.GetAllocatedBytesForCurrentThread. Batch latency quantiles describe round-level mean latency, NOT individual-call or service P99; sample count is recorded. Mutation counts Delete and Add as separate API calls. Advanced parallel rows exclude thread setup and warm-up, include gate/scheduling overhead, use one buffer/context per thread, and verify per-query counts plus independent preflight ID checks. Build/data/oracle/verification are excluded from timing. Shared-host measurements are workload-specific."
            };
            Console.WriteLine("label=" + report.Label + " source=" + Path.GetFullPath(report.SourceDirectory));
            Console.WriteLine(report.Framework + " | " + report.OperatingSystem + " | " + report.Architecture);
            Console.WriteLine(report.Processor + " | logical-processors=" + report.LogicalProcessors + " | server-gc=" + report.ServerGC);
            Console.WriteLine("tiered-compilation=" + report.TieredCompilation);
            Console.WriteLine("seed=" + Seed + " rounds=" + options.Rounds + " queries-per-workload=" + options.Queries);
            Console.WriteLine("scenario,items,workload,implementation,operations,rounds,median_ms,us_per_op,ops_per_s,allocated_bytes_per_op,checksum,status");

            bool mutationFailed = false;
            if (!options.AdvancedOnly)
            foreach (string scenario in Scenarios)
            {
                foreach (int size in options.Sizes)
                {
                    var data = Generate(scenario, size, options.Queries);
                    var intersects = Scan(data, "intersects");
                    var contains = Scan(data, "contains");
                    var nearest = Scan(data, "nearest");
                    WarmUp(data);

                    var insertSamples = new List<Sample<RTree<int>>>();
                    var querySamples = new Dictionary<string, List<Sample<List<int>[]>>>();
                    foreach (string name in new[] { "intersects", "contains", "nearest" })
                    {
                        querySamples[name + "/tree"] = new List<Sample<List<int>[]>>();
                        querySamples[name + "/scan"] = new List<Sample<List<int>[]>>();
                    }

                    for (int round = 0; round < options.Rounds; round++)
                    {
                        var insertion = Measure(() => Build(data.Boxes));
                        CheckAllItems(insertion.Value, size);
                        insertSamples.Add(insertion);
                        foreach (string name in new[] { "intersects", "contains", "nearest" })
                        {
                            var expected = name == "intersects" ? intersects : name == "contains" ? contains : nearest;
                            // Alternate scan/tree timing order to reduce systematic order bias.
                            if (round % 2 == 0)
                            {
                                AddQuerySample(querySamples[name + "/tree"], () => Query(insertion.Value, data, name), expected, name);
                                AddQuerySample(querySamples[name + "/scan"], () => Scan(data, name), expected, name);
                            }
                            else
                            {
                                AddQuerySample(querySamples[name + "/scan"], () => Scan(data, name), expected, name);
                                AddQuerySample(querySamples[name + "/tree"], () => Query(insertion.Value, data, name), expected, name);
                            }
                        }
                    }

                    AddRow(report, scenario, size, "insert", "tree", size, insertSamples.Select(s => (s.Milliseconds, s.AllocatedBytes)).ToList(), size.ToString(CultureInfo.InvariantCulture));
                    report.Results[report.Results.Count - 1].RetainedIndexBytes = EstimateRetainedBytes(() => Build(data.Boxes));
                    foreach (string name in new[] { "intersects", "contains", "nearest" })
                    {
                        foreach (string implementation in new[] { "tree", "scan" })
                        {
                            var samples = querySamples[name + "/" + implementation];
                            AddRow(report, scenario, size, name, implementation, options.Queries,
                                samples.Select(s => (s.Milliseconds, s.AllocatedBytes)).ToList(), Checksum(samples[0].Value));
                            report.Results[report.Results.Count - 1].MeanMatches = samples[0].Value.Average(list => list.Count);
#if OPTIMIZED_API
                            if (implementation == "tree")
                                SetStatistics(report.Results[report.Results.Count - 1], insertSamples[insertSamples.Count - 1].Value, data, name);
#endif
                        }
                    }

                    var mutatedBoxes = (Rectangle[])data.Boxes.Clone();
                    for (int i = 0; i < data.MutationIds.Length; i++) mutatedBoxes[data.MutationIds[i]] = data.Replacements[i];
                    var mutatedData = new Dataset { Boxes = mutatedBoxes, Ranges = data.Ranges, Points = data.Points, Radii = data.Radii };
                    var mutatedExpected = new[] { Scan(mutatedData, "intersects"), Scan(mutatedData, "contains"), Scan(mutatedData, "nearest") };
                    var mutationSamples = new List<(double, long)>();
                    string? mutationError = null;
                    string? mutationChecksum = null;
                    for (int round = 0; round < options.Rounds; round++)
                    {
                        var tree = Build(data.Boxes);
                        try
                        {
                            var mutation = Measure(() => Mutate(tree, data));
                            CheckAllItems(tree, size);
                            for (int k = 0; k < 3; k++)
                            {
                                string name = k == 0 ? "intersects" : k == 1 ? "contains" : "nearest";
                                var actual = Query(tree, data, name);
                                Check(actual, mutatedExpected[k], "after mutation " + name);
                                if (k == 0) mutationChecksum = Checksum(actual);
                            }
                            mutationSamples.Add((mutation.Milliseconds, mutation.AllocatedBytes));
                        }
                        catch (Exception e)
                        {
                            mutationError = e.GetType().Name + ": " + e.Message;
                            mutationFailed = true;
                            break;
                        }
                    }
                    if (mutationError == null)
                        AddRow(report, scenario, size, "delete+reinsert", "tree", 2 * data.MutationIds.Length, mutationSamples, mutationChecksum);
                    else
                    {
                        var row = new Row { Scenario = scenario, Items = size, Workload = "delete+reinsert", Implementation = "tree", Operations = 2 * data.MutationIds.Length, CompletedRounds = mutationSamples.Count, Error = mutationError };
                        report.Results.Add(row);
                        Print(row);
                    }
                }
            }

#if OPTIMIZED_API
            if (options.Advanced || options.AdvancedOnly) RunAdvanced(options, report);
#endif

            if (options.JsonPath != null)
            {
                string path = Path.GetFullPath(options.JsonPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
                Console.WriteLine("json=" + path);
            }
            if (mutationFailed)
            {
                Console.WriteLine("Mutation failed; no mutation performance value is reported for failed workloads.");
                return options.AllowMutationFailure ? 0 : 1;
            }
            Console.WriteLine("PASS: every measured query and post-mutation query matched the brute-force oracle.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (name == "--allow-mutation-failure") { options.AllowMutationFailure = true; continue; }
            if (name == "--advanced") { options.Advanced = true; continue; }
            if (name == "--advanced-only") { options.AdvancedOnly = true; continue; }
            if (name == "--bulk-only") { options.BulkOnly = true; options.AdvancedOnly = true; continue; }
            if (++i >= args.Length) throw new ArgumentException("Missing value for " + name);
            switch (name)
            {
                case "--sizes": options.Sizes = args[i].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--queries": options.Queries = int.Parse(args[i], CultureInfo.InvariantCulture); break;
                case "--rounds": options.Rounds = int.Parse(args[i], CultureInfo.InvariantCulture); break;
                case "--label": options.Label = args[i]; break;
                case "--json": options.JsonPath = args[i]; break;
                case "--capacities": options.Capacities = args[i].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--workers": options.Workers = args[i].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--concurrent-repeats": options.ConcurrentRepeats = int.Parse(args[i], CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException("Unknown argument: " + name);
            }
        }
        if (options.Sizes.Length == 0 || options.Sizes.Any(n => n < 100) || options.Queries < 1 || options.Rounds < 1)
            throw new ArgumentException("Sizes must be at least 100; queries and rounds must be positive.");
        if (options.Capacities.Length == 0 || options.Capacities.Any(n => n < 2) || options.Workers.Length == 0 || options.Workers.Any(n => n < 1) || options.ConcurrentRepeats < 1)
            throw new ArgumentException("Capacities must be at least 2; workers and concurrent repeats must be positive.");
        return options;
    }

    private static Dataset Generate(string scenario, int count, int queries)
    {
        // Keep the original two distributions' seeds stable when adding scenarios.
        int scenarioSeed = scenario == "z-only" ? 1 : scenario == "planar-xy" ? 2 : 0;
        var random = new Random(Seed + count + scenarioSeed);
        var data = new Dataset { Boxes = new Rectangle[count], Ranges = new Rectangle[queries], Points = new Point[queries], Radii = new double[queries] };
        for (int i = 0; i < count; i++) data.Boxes[i] = RandomBox(random, scenario);
        for (int q = 0; q < queries; q++)
        {
            double x = random.NextDouble() * 1000, y = random.NextDouble() * 1000, z = random.NextDouble() * 1000;
            if (scenario == "z-only")
            {
                data.Ranges[q] = new Rectangle(-0.1, -0.1, 1.1, 1.1, z - 1, z + 1);
                data.Points[q] = new Point(0.5, 0.5, z);
            }
            else if (scenario == "planar-xy")
            {
                data.Ranges[q] = new Rectangle(x - 30, y - 30, x + 30, y + 30, -1, 1);
                data.Points[q] = new Point(x, y, 0);
            }
            else
            {
                data.Ranges[q] = new Rectangle(x - 30, y - 30, x + 30, y + 30, z - 30, z + 30);
                data.Points[q] = new Point(x, y, z);
            }
            data.Radii[q] = q % 5 == 0 ? 0 : double.PositiveInfinity;
        }
        int mutationCount = Math.Min(count / 10, 5000);
        var permutation = Enumerable.Range(0, count).ToArray();
        for (int i = count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            int temp = permutation[i]; permutation[i] = permutation[j]; permutation[j] = temp;
        }
        data.MutationIds = permutation.Take(mutationCount).ToArray();
        data.Replacements = new Rectangle[mutationCount];
        for (int i = 0; i < mutationCount; i++) data.Replacements[i] = RandomBox(random, scenario);
        return data;
    }

    private static Rectangle RandomBox(Random random, string scenario)
    {
        double x = random.NextDouble() * 1000, y = random.NextDouble() * 1000, z = random.NextDouble() * 1000;
        if (scenario == "z-only") return new Rectangle(0, 0, 1, 1, z, z + 0.25 + random.NextDouble() * 0.5);
        if (scenario == "planar-xy") return new Rectangle(x, y, x + 1 + random.NextDouble() * 4, y + 1 + random.NextDouble() * 4, 0, 0);
        return new Rectangle(x, y, x + 1 + random.NextDouble() * 4, y + 1 + random.NextDouble() * 4, z, z + 1 + random.NextDouble() * 4);
    }

    private static RTree<int> Build(Rectangle[] boxes)
    {
        var tree = new RTree<int>();
        for (int i = 0; i < boxes.Length; i++) tree.Add(boxes[i], i);
        return tree;
    }

    private static int Mutate(RTree<int> tree, Dataset data)
    {
        for (int i = 0; i < data.MutationIds.Length; i++)
        {
            int id = data.MutationIds[i];
            if (!tree.Delete(data.Boxes[id], id)) throw new InvalidOperationException("Delete returned false for existing item " + id);
            tree.Add(data.Replacements[i], id);
        }
        return tree.Count;
    }

    private static void WarmUp(Dataset data)
    {
        var tree = Build(data.Boxes);
        Query(tree, data, "intersects");
        Query(tree, data, "contains");
        Query(tree, data, "nearest");
        // Baseline deletion is known to throw; preserve the measured failure separately.
        try
        {
            int limit = Math.Min(32, data.MutationIds.Length);
            for (int i = 0; i < limit; i++)
            {
                int id = data.MutationIds[i];
                tree.Delete(data.Boxes[id], id);
                tree.Add(data.Replacements[i], id);
            }
        }
        catch (ArgumentException) { }
    }

    private static List<int>[] Query(RTree<int> tree, Dataset data, string name)
    {
        var results = new List<int>[data.Ranges.Length];
        for (int i = 0; i < results.Length; i++)
            results[i] = name == "intersects" ? tree.Intersects(data.Ranges[i]) : name == "contains" ? tree.Contains(data.Ranges[i]) : tree.Nearest(data.Points[i], data.Radii[i]);
        return results;
    }

    private static List<int>[] Scan(Dataset data, string name)
    {
        return name == "intersects" ? ScanIntersects(data) : name == "contains" ? ScanContains(data) : ScanNearest(data);
    }

    private static List<int>[] ScanIntersects(Dataset data)
    {
        var results = new List<int>[data.Ranges.Length];
        for (int q = 0; q < results.Length; q++)
        {
            var matches = new List<int>();
            var range = data.Ranges[q];
            for (int id = 0; id < data.Boxes.Length; id++)
            {
                var box = data.Boxes[id];
                if (box.min[0] <= range.max[0] && box.max[0] >= range.min[0]
                    && box.min[1] <= range.max[1] && box.max[1] >= range.min[1]
                    && box.min[2] <= range.max[2] && box.max[2] >= range.min[2]) matches.Add(id);
            }
            results[q] = matches;
        }
        return results;
    }

    private static List<int>[] ScanContains(Dataset data)
    {
        var results = new List<int>[data.Ranges.Length];
        for (int q = 0; q < results.Length; q++)
        {
            var matches = new List<int>();
            var range = data.Ranges[q];
            for (int id = 0; id < data.Boxes.Length; id++)
            {
                var box = data.Boxes[id];
                if (box.min[0] >= range.min[0] && box.max[0] <= range.max[0]
                    && box.min[1] >= range.min[1] && box.max[1] <= range.max[1]
                    && box.min[2] >= range.min[2] && box.max[2] <= range.max[2]) matches.Add(id);
            }
            results[q] = matches;
        }
        return results;
    }

    private static List<int>[] ScanNearest(Dataset data)
    {
        var results = new List<int>[data.Ranges.Length];
        for (int q = 0; q < results.Length; q++)
        {
            var matches = new List<int>();
            double nearest = data.Radii[q];
            var point = data.Points[q];
            for (int id = 0; id < data.Boxes.Length; id++)
            {
                var box = data.Boxes[id];
                double distanceSquared = 0;
                for (int dimension = 0; dimension < 3; dimension++)
                {
                    double gap = Math.Max(box.min[dimension], point.coordinates[dimension]) - Math.Min(box.max[dimension], point.coordinates[dimension]);
                    if (gap > 0) distanceSquared += gap * gap;
                }
                double distance = Math.Sqrt(distanceSquared);
                if (distance < nearest) { nearest = distance; matches.Clear(); }
                if (distance <= nearest) matches.Add(id);
            }
            results[q] = matches;
        }
        return results;
    }

    private static Sample<T> Measure<T>(Func<T> work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        T result = work();
        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return new Sample<T> { Value = result, Milliseconds = elapsed * 1000.0 / Stopwatch.Frequency, AllocatedBytes = allocatedBytes };
    }

    private static void AddQuerySample(List<Sample<List<int>[]>> samples, Func<List<int>[]> work, List<int>[] expected, string name)
    {
        var sample = Measure(work);
        Check(sample.Value, expected, name);
        samples.Add(sample);
    }

    private static void Check(List<int>[] actual, List<int>[] expected, string name)
    {
        if (actual.Length != expected.Length) throw new InvalidOperationException("Query count mismatch: " + name);
        for (int i = 0; i < actual.Length; i++)
        {
            // Oracle and scan enumerate unique item IDs in sorted order.
            var sorted = actual[i].ToArray();
            Array.Sort(sorted);
            if (!sorted.SequenceEqual(expected[i])) throw new InvalidOperationException("Wrong results for " + name + " query " + i);
        }
    }

    private static void CheckAllItems(RTree<int> tree, int expected)
    {
        if (tree.Count != expected) throw new InvalidOperationException("Wrong tree Count.");
        var all = tree.Intersects(new Rectangle(-1, -1, 2000, 2000, -1, 2000));
        all.Sort();
        if (!all.SequenceEqual(Enumerable.Range(0, expected))) throw new InvalidOperationException("Tree lost, duplicated or added an item.");
    }

    private static string Checksum(List<int>[] results)
    {
        ulong checksum = 1469598103934665603;
        for (int q = 0; q < results.Length; q++)
        {
            ulong sum = 0;
            foreach (int id in results[q])
            {
                ulong value = (ulong)(id + 1);
                value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL);
                value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111ebUL);
                sum = unchecked(sum + (value ^ (value >> 31)));
            }
            checksum = unchecked((checksum ^ sum ^ (ulong)results[q].Count) * 1099511628211);
        }
        return checksum.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static void AddRow(Report report, string scenario, int size, string workload, string implementation, int operations, List<(double Milliseconds, long Bytes)> samples, string? checksum)
    {
        samples.Sort((a, b) => a.Milliseconds.CompareTo(b.Milliseconds));
        int middle = samples.Count / 2;
        double milliseconds = samples.Count % 2 == 1 ? samples[middle].Milliseconds : (samples[middle - 1].Milliseconds + samples[middle].Milliseconds) / 2;
        var allocations = samples.Select(s => s.Bytes).OrderBy(b => b).ToArray();
        double bytes = allocations.Length % 2 == 1 ? allocations[middle] : (allocations[middle - 1] + (double)allocations[middle]) / 2;
        var row = new Row
        {
            Scenario = scenario, Items = size, Workload = workload, Implementation = implementation,
            Operations = operations, CompletedRounds = samples.Count, MedianMilliseconds = milliseconds,
            MicrosecondsPerOperation = milliseconds * 1000 / operations,
            OperationsPerSecond = operations * 1000 / milliseconds,
            AllocatedBytesPerOperation = bytes / operations, Checksum = checksum
        };
        SetBatchQuantiles(row, samples.Select(s => s.Milliseconds * 1000 / operations).ToArray());
        report.Results.Add(row);
        Print(row);
    }

    private static void Print(Row row)
    {
        Console.WriteLine(string.Join(",", row.Scenario, row.Items, row.Workload, row.Implementation, row.Operations, row.CompletedRounds,
            Number(row.MedianMilliseconds), Number(row.MicrosecondsPerOperation), Number(row.OperationsPerSecond),
            Number(row.AllocatedBytesPerOperation), row.Checksum ?? "", row.Error == null ? "ok" : "FAILED: " + row.Error.Replace(',', ';')));
    }

    private static string Number(double? value) => value.HasValue ? value.Value.ToString("F3", CultureInfo.InvariantCulture) : "";

    private static void SetBatchQuantiles(Row row, double[] batchMeans)
    {
        Array.Sort(batchMeans);
        row.LatencyBatchSamples = batchMeans.Length;
        row.P50BatchMeanMicroseconds = Quantile(batchMeans, 0.50);
        row.P95BatchMeanMicroseconds = Quantile(batchMeans, 0.95);
        row.P99BatchMeanMicroseconds = Quantile(batchMeans, 0.99);
    }

    private static long EstimateRetainedBytes(Func<RTree<int>> factory)
    {
        // An isolated, untimed estimate. Caller-owned input boxes already exist.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        var tree = factory();
        long after = GC.GetTotalMemory(true);
        GC.KeepAlive(tree);
        return Math.Max(0, after - before);
    }

    private static double Quantile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0) return double.NaN;
        double index = fraction * (sorted.Length - 1);
        int low = (int)index;
        int high = Math.Min(low + 1, sorted.Length - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (index - low);
    }

    private static string ProcessorName()
    {
        if (File.Exists("/proc/cpuinfo"))
        {
            string? model = File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal));
            if (model != null) return model.Substring(model.IndexOf(':') + 1).Trim();
        }
        return "unknown";
    }
}
