#nullable enable
#if OPTIMIZED_API
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using RTree;

internal static partial class Program
{
    private static readonly string[] QueryNames = { "intersects", "contains", "nearest" };

    private static void RunAdvanced(Options options, Report report)
    {
        foreach (string scenario in Scenarios)
        foreach (int size in options.Sizes)
        {
            var data = Generate(scenario, size, options.Queries);
            var expected = QueryNames.Select(name => Scan(data, name)).ToArray();
            var pairs = data.Boxes.Select((box, item) => new KeyValuePair<Rectangle, int>(box, item)).ToArray();
            foreach (int capacity in options.Capacities.Distinct())
            foreach (bool bulk in options.BulkOnly ? new[] { true } : new[] { false, true })
            {
                string mode = bulk ? "STR" : "incremental";
                Func<RTree<int>> factory = () => BuildAtCapacity(pairs, capacity, bulk);
                var warm = factory();
                CheckAllItems(warm, size);
                foreach (string name in QueryNames) Query(warm, data, name);
                var builds = new List<(double, long)>();
                RTree<int> tree = warm;
                for (int round = 0; round < options.Rounds; round++)
                {
                    var sample = Measure(factory);
                    tree = sample.Value;
                    CheckAllItems(tree, size);
                    builds.Add((sample.Milliseconds, sample.AllocatedBytes));
                }
                AddRow(report, scenario, size, bulk ? "bulk-build" : "insert", "capacity-sweep", size, builds, size.ToString(CultureInfo.InvariantCulture));
                var buildRow = report.Results[report.Results.Count - 1];
                buildRow.BuildMode = mode;
                buildRow.NodeCapacity = capacity;
                buildRow.RetainedIndexBytes = EstimateRetainedBytes(factory);
                Console.WriteLine("  build=" + mode + " capacity=" + capacity + " retained-index-bytes~=" + buildRow.RetainedIndexBytes);

                for (int k = 0; k < QueryNames.Length; k++)
                {
                    string name = QueryNames[k];
                    var buffer = new List<int>(Math.Max(4, expected[k].Max(matches => matches.Count)));
                    var context = new QueryContext(false);
                    var samples = new List<(double, long)>();
                    long expectedTotal = expected[k].Sum(matches => (long)matches.Count);
                    ValidateReusable(tree, data, name, expected[k], buffer, context);
                    if (QueryReusableBatch(tree, data, name, buffer, context) != expectedTotal)
                        throw new InvalidOperationException("Reusable warm-up count differs: " + name);
                    for (int round = 0; round < options.Rounds; round++)
                    {
                        var sample = Measure(() => QueryReusableBatch(tree, data, name, buffer, context));
                        if (sample.Value != expectedTotal) throw new InvalidOperationException("Reusable query match count differs: " + name);
                        samples.Add((sample.Milliseconds, sample.AllocatedBytes));
                        ValidateReusable(tree, data, name, expected[k], buffer, context);
                    }
                    AddRow(report, scenario, size, name, "reused-buffer", options.Queries, samples, Checksum(expected[k]));
                    var row = report.Results[report.Results.Count - 1];
                    row.BuildMode = mode;
                    row.NodeCapacity = capacity;
                    SetStatistics(row, tree, data, name);
                    Console.WriteLine("  build=" + mode + " capacity=" + capacity + " mean-matches=" + row.MeanMatches?.ToString("F3", CultureInfo.InvariantCulture)
                        + " mean-nodes=" + row.MeanNodesVisited?.ToString("F3", CultureInfo.InvariantCulture)
                        + " mean-entry-tests=" + row.MeanEntriesTested?.ToString("F3", CultureInfo.InvariantCulture));
                }
            }

            // A fixed documented layout keeps worker-count comparisons comparable.
            var concurrentTree = BuildAtCapacity(pairs, 16, true);
            foreach (int workers in options.Workers)
            {
                var samples = new List<(double, long)>();
                string? checksum = null;
                for (int round = 0; round < options.Rounds; round++)
                {
                    var sample = MeasureConcurrent(concurrentTree, data, expected, workers, options.ConcurrentRepeats);
                    samples.Add((sample.Milliseconds, sample.AllocatedBytes));
                    string current = sample.Value.ToString("x16", CultureInfo.InvariantCulture);
                    if (checksum != null && current != checksum) throw new InvalidOperationException("Concurrent checksums differ between rounds");
                    checksum = current;
                }
                int operations = checked(options.Queries * QueryNames.Length * options.ConcurrentRepeats * workers);
                AddRow(report, scenario, size, "mixed-read-consume", "parallel-reused-buffer", operations, samples, checksum);
                var row = report.Results[report.Results.Count - 1];
                row.BuildMode = "STR";
                row.NodeCapacity = 16;
                row.Threads = workers;
                row.MeanMatches = expected.Sum(workload => workload.Sum(matches => (long)matches.Count)) / (double)(options.Queries * QueryNames.Length);
                Console.WriteLine("  workers=" + workers + " STR capacity=16; consumes and checks every query's order-independent ID checksum");
            }
        }
    }

    private static RTree<int> BuildAtCapacity(KeyValuePair<Rectangle, int>[] pairs, int capacity, bool bulk)
    {
        if (bulk) return RTree<int>.BulkLoad(pairs, capacity, Math.Max(1, capacity / 2));
        var tree = new RTree<int>(capacity, Math.Max(1, capacity / 2));
        foreach (var pair in pairs) tree.Add(pair.Key, pair.Value);
        return tree;
    }

    private static int QueryReusable(RTree<int> tree, Dataset data, string name, int query, List<int> buffer, QueryContext context)
    {
        if (name == "intersects") return tree.Intersects(data.Ranges[query], buffer, context);
        if (name == "contains") return tree.Contains(data.Ranges[query], buffer, context);
        return tree.Nearest(data.Points[query], data.Radii[query], buffer, context);
    }

    private static long QueryReusableBatch(RTree<int> tree, Dataset data, string name, List<int> buffer, QueryContext context)
    {
        long matches = 0;
        for (int query = 0; query < data.Ranges.Length; query++) matches += QueryReusable(tree, data, name, query, buffer, context);
        return matches;
    }

    private static void ValidateReusable(RTree<int> tree, Dataset data, string name, List<int>[] expected, List<int> buffer, QueryContext context)
    {
        for (int query = 0; query < data.Ranges.Length; query++)
        {
            int count = QueryReusable(tree, data, name, query, buffer, context);
            if (count != buffer.Count) throw new InvalidOperationException("Reusable API returned inconsistent count: " + name);
            var sorted = buffer.ToArray();
            Array.Sort(sorted);
            if (!sorted.SequenceEqual(expected[query])) throw new InvalidOperationException("Wrong reusable query IDs for " + name + " query " + query);
        }
    }

    private static void SetStatistics(Row row, RTree<int> tree, Dataset data, string name)
    {
        var context = new QueryContext(true);
        var buffer = new List<int>();
        long nodes = 0, entries = 0, matches = 0;
        for (int query = 0; query < data.Ranges.Length; query++)
        {
            QueryReusable(tree, data, name, query, buffer, context);
            if (context.ResultCount != buffer.Count) throw new InvalidOperationException("Diagnostic result count disagrees with output");
            nodes += context.NodesVisited;
            entries += context.EntriesTested;
            matches += buffer.Count;
        }
        row.MeanMatches = matches / (double)data.Ranges.Length;
        row.MeanNodesVisited = nodes / (double)data.Ranges.Length;
        row.MeanEntriesTested = entries / (double)data.Ranges.Length;
    }

    private static Sample<ulong> MeasureConcurrent(RTree<int> tree, Dataset data, List<int>[][] expected, int workers, int repeats)
    {
        var contexts = new QueryContext[workers];
        var buffers = new List<int>[workers];
        var hashes = expected.Select(workload => workload.Select(ChecksumOne).ToArray()).ToArray();
        int largest = expected.Max(workload => workload.Max(matches => matches.Count));
        for (int worker = 0; worker < workers; worker++)
        {
            contexts[worker] = new QueryContext(false);
            buffers[worker] = new List<int>(Math.Max(4, largest));
            for (int k = 0; k < QueryNames.Length; k++) ValidateReusable(tree, data, QueryNames[k], expected[k], buffers[worker], contexts[worker]);
        }
        using (var ready = new CountdownEvent(workers))
        using (var done = new CountdownEvent(workers))
        using (var start = new ManualResetEventSlim(false))
        {
            var threads = new Thread[workers];
            var errors = new Exception?[workers];
            var allocations = new long[workers];
            var checksums = new ulong[workers];
            for (int worker = 0; worker < workers; worker++)
            {
                int own = worker;
                threads[worker] = new Thread(() =>
                {
                    // ReaderWriterLockSlim and other runtime helpers can keep
                    // thread-local bookkeeping. Warm those on the actual worker.
                    try
                    {
                        for (int k = 0; k < QueryNames.Length; k++)
                            QueryReusable(tree, data, QueryNames[k], 0, buffers[own], contexts[own]);
                    }
                    catch (Exception error) { errors[own] = error; }
                    ready.Signal();
                    start.Wait();
                    if (errors[own] != null) { done.Signal(); return; }
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    try
                    {
                        ulong consumed = 0;
                        for (int repeat = 0; repeat < repeats; repeat++)
                        for (int query = 0; query < data.Ranges.Length; query++)
                        for (int k = 0; k < QueryNames.Length; k++)
                        {
                            int count = QueryReusable(tree, data, QueryNames[k], query, buffers[own], contexts[own]);
                            ulong hash = ChecksumOne(buffers[own]);
                            if (count != expected[k][query].Count || hash != hashes[k][query])
                                throw new InvalidOperationException("Concurrent query disagrees with independent oracle");
                            consumed = unchecked(consumed + (hash ^ (ulong)(query * 3 + k + 1)));
                        }
                        checksums[own] = consumed;
                    }
                    catch (Exception error) { errors[own] = error; }
                    finally
                    {
                        allocations[own] = GC.GetAllocatedBytesForCurrentThread() - before;
                        done.Signal();
                    }
                });
                threads[worker].IsBackground = true;
                threads[worker].Start();
            }
            ready.Wait();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long begun = Stopwatch.GetTimestamp();
            start.Set();
            done.Wait();
            long ended = Stopwatch.GetTimestamp();
            foreach (var thread in threads) thread.Join();
            foreach (var error in errors) if (error != null) throw new InvalidOperationException("Concurrent reader failed", error);
            ulong sum = 0;
            foreach (ulong checksum in checksums) sum = unchecked(sum + checksum);
            return new Sample<ulong>
            {
                Value = sum,
                Milliseconds = (ended - begun) * 1000.0 / Stopwatch.Frequency,
                AllocatedBytes = allocations.Sum()
            };
        }
    }

    private static ulong ChecksumOne(List<int> matches)
    {
        ulong sum = 0;
        foreach (int id in matches)
        {
            ulong value = (ulong)(id + 1);
            value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111ebUL);
            sum = unchecked(sum + (value ^ (value >> 31)));
        }
        return sum ^ (ulong)matches.Count;
    }
}
#endif
