using System;
using System.Collections.Generic;
using System.Threading;

namespace RTree
{
    public partial class RTree<T>
    {
        private static readonly ThreadLocal<QueryContext> queryWorkspace =
            new ThreadLocal<QueryContext>(delegate { return new QueryContext(); });

        public List<T> Intersects(Rectangle r)
        {
            var results = new List<T>();
            Intersects(r, results, queryWorkspace.Value);
            return results;
        }

        public List<T> Contains(Rectangle r)
        {
            var results = new List<T>();
            Contains(r, results, queryWorkspace.Value);
            return results;
        }

        /// <summary>Returns every tied nearest item within the inclusive limit.</summary>
        public List<T> Nearest(Point p, double furthestDistance)
        {
            var results = new List<T>();
            Nearest(p, furthestDistance, results, queryWorkspace.Value);
            return results;
        }

        /// <summary>Clears and fills results, returning the number of matches.</summary>
        public int Intersects(Rectangle rectangle, List<T> results)
        {
            return Intersects(rectangle, results, queryWorkspace.Value);
        }

        public int Intersects(Rectangle rectangle, List<T> results, QueryContext context)
        {
            return QueryRange(rectangle, results, context, false);
        }

        /// <summary>Clears and fills results with boxes contained by rectangle.</summary>
        public int Contains(Rectangle rectangle, List<T> results)
        {
            return Contains(rectangle, results, queryWorkspace.Value);
        }

        public int Contains(Rectangle rectangle, List<T> results, QueryContext context)
        {
            return QueryRange(rectangle, results, context, true);
        }

        private int QueryRange(Rectangle rectangle, List<T> results, QueryContext context, bool containedOnly)
        {
            if (rectangle == null) throw new ArgumentNullException("rectangle");
            return QueryRangeValue(Aabb.FromRectangle(rectangle), results, context, containedOnly);
        }

        private int QueryRangeValue(Aabb query, List<T> results, QueryContext context, bool containedOnly)
        {
            if (results == null) throw new ArgumentNullException("results");
            if (context == null) throw new ArgumentNullException("context");
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    results.Clear();
                    if (nodes[rootNodeId].Count == 0 || !query.Intersects(nodes[rootNodeId].Bounds)) return 0;
                    bool statistics = context.CollectStatistics;
                    // A complemented ID marks a subtree fully covered by the
                    // query, whose remaining geometric tests can be skipped.
                    context.Push(query.Contains(nodes[rootNodeId].Bounds) ? ~rootNodeId : rootNodeId);
                    while (context.stackCount != 0)
                    {
                        int encoded = context.Pop();
                        bool accepted = encoded < 0;
                        int nodeId = accepted ? ~encoded : encoded;
                        TreeNode node = nodes[nodeId];
                        if (statistics) context.NodesVisited++;
                        int offset = nodeId * maxNodeEntries;
                        if (node.Level == 1)
                        {
                            for (int i = 0; i < node.Count; i++)
                            {
                                TreeEntry entry = entries[offset + i];
                                if (!accepted)
                                {
                                    if (statistics) { context.EntriesTested++; context.PrimitiveTests++; }
                                    if (containedOnly ? !query.Contains(entry.Bounds) : !query.Intersects(entry.Bounds)) continue;
                                }
                                results.Add(itemValues[entry.Id]);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < node.Count; i++)
                            {
                                TreeEntry entry = entries[offset + i];
                                if (accepted) context.Push(~entry.Id);
                                else
                                {
                                    if (statistics) context.EntriesTested++;
                                    if (!query.Intersects(entry.Bounds)) continue;
                                    context.Push(query.Contains(entry.Bounds) ? ~entry.Id : entry.Id);
                                }
                            }
                        }
                    }
                    context.ResultCount = results.Count;
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        public int Nearest(Point point, double maximumDistance, List<T> results)
        {
            return Nearest(point, maximumDistance, results, queryWorkspace.Value);
        }

        /// <summary>
        /// Clears and fills results with all tied nearest items. Uses a reusable
        /// best-first node heap and squared-distance comparisons on the fast path.
        /// </summary>
        public int Nearest(Point point, double maximumDistance, List<T> results, QueryContext context)
        {
            if (point == null) throw new ArgumentNullException("point");
            if (double.IsNaN(maximumDistance) || maximumDistance < 0)
                throw new ArgumentOutOfRangeException("maximumDistance");
            if (results == null) throw new ArgumentNullException("results");
            if (context == null) throw new ArgumentNullException("context");
            double x = point.coordinates[0], y = point.coordinates[1], z = point.coordinates[2];
            DistanceKey limit = DistanceKey.Limit(maximumDistance);
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    results.Clear();
                    if (nodes[rootNodeId].Count == 0) return 0;
                    bool statistics = context.CollectStatistics;
                    DistanceKey initial = nodes[rootNodeId].Bounds.Distance(x, y, z);
                    if (initial.CompareTo(limit) > 0) return 0;
                    context.Push(rootNodeId, initial);
                    while (context.heapCount != 0)
                    {
                        QueryContext.HeapEntry pending = context.PopNearest();
                        if (pending.Distance.CompareTo(limit) > 0) break;
                        TreeNode node = nodes[pending.NodeId];
                        if (statistics) context.NodesVisited++;
                        int offset = pending.NodeId * maxNodeEntries;
                        for (int i = 0; i < node.Count; i++)
                        {
                            TreeEntry entry = entries[offset + i];
                            if (statistics)
                            {
                                context.EntriesTested++;
                                if (node.Level == 1) context.PrimitiveTests++;
                            }
                            DistanceKey distance = entry.Bounds.Distance(x, y, z);
                            int comparison = distance.CompareTo(limit);
                            if (comparison > 0) continue;
                            if (node.Level == 1)
                            {
                                if (comparison < 0)
                                {
                                    limit = distance;
                                    results.Clear();
                                }
                                results.Add(itemValues[entry.Id]);
                            }
                            else context.Push(entry.Id, distance);
                        }
                    }
                    context.ResultCount = results.Count;
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }
    }
}
