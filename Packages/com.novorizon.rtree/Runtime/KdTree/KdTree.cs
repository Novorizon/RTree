using System;
using System.Collections.Generic;
using System.Threading;

namespace RTree
{
    /// <summary>
    /// A balanced, three-dimensional point KD-tree. Rebuild replaces the complete
    /// index atomically; queries can run concurrently with other queries. Points
    /// and payloads may repeat. Equal-distance results retain input order.
    /// </summary>
    public sealed class KdTree<T> : IPointQuery3<T>
    {
        private struct PointRecord
        {
            internal Vector3d Position;
            internal T Item;
            internal int SourceIndex;
        }

        private struct NodeRecord
        {
            internal Aabb Bounds;
            internal int Start, Count, Left, Right, MinSourceIndex;
        }

        private sealed class Snapshot
        {
            internal PointRecord[] Points;
            internal NodeRecord[] Nodes;
            internal int Root;
        }

        private sealed class AxisComparer : IComparer<PointRecord>
        {
            private readonly int axis;
            internal AxisComparer(int axis) { this.axis = axis; }

            public int Compare(PointRecord left, PointRecord right)
            {
                double a = Coordinate(left.Position, axis), b = Coordinate(right.Position, axis);
                int order = a.CompareTo(b);
                return order != 0 ? order : left.SourceIndex.CompareTo(right.SourceIndex);
            }
        }

        private sealed class Builder
        {
            private readonly PointRecord[] points;
            private readonly int leafCapacity;
            private readonly List<NodeRecord> nodes = new List<NodeRecord>();
            private static readonly AxisComparer[] comparers =
                { new AxisComparer(0), new AxisComparer(1), new AxisComparer(2) };

            internal Builder(PointRecord[] points, int leafCapacity)
            {
                this.points = points;
                this.leafCapacity = leafCapacity;
            }

            internal Snapshot Build()
            {
                int root = points.Length == 0 ? -1 : BuildNode(0, points.Length);
                return new Snapshot { Points = points, Nodes = nodes.ToArray(), Root = root };
            }

            private int BuildNode(int start, int count)
            {
                Vector3d first = points[start].Position;
                Aabb bounds = PointBounds(first);
                int minSourceIndex = points[start].SourceIndex;
                int end = start + count;
                for (int i = start + 1; i < end; i++)
                {
                    bounds = Aabb.Union(bounds, PointBounds(points[i].Position));
                    minSourceIndex = Math.Min(minSourceIndex, points[i].SourceIndex);
                }

                int id = nodes.Count;
                NodeRecord node = new NodeRecord { Bounds = bounds, Start = start, Count = count, Left = -1, Right = -1, MinSourceIndex = minSourceIndex };
                nodes.Add(node);
                if (count > leafCapacity)
                {
                    int axis = LargestAxis(bounds);
                    int middle = start + count / 2;
                    SelectMedian(start, end - 1, middle, comparers[axis]);
                    node.Left = BuildNode(start, middle - start);
                    node.Right = BuildNode(middle, end - middle);
                    node.Count = 0;
                    nodes[id] = node;
                }
                return id;
            }

            // Median-of-three selection avoids sorting whole subtrees. A bounded
            // partition budget falls back to introsort on adversarial inputs.
            private void SelectMedian(int first, int last, int middle, AxisComparer comparer)
            {
                int budget = 0;
                for (int size = last - first + 1; size > 1; size >>= 1) budget += 2;
                while (first < last)
                {
                    if (budget-- == 0)
                    {
                        Array.Sort(points, first, last - first + 1, comparer);
                        return;
                    }
                    int center = first + (last - first) / 2;
                    if (comparer.Compare(points[center], points[first]) < 0) Swap(points, first, center);
                    if (comparer.Compare(points[last], points[first]) < 0) Swap(points, first, last);
                    if (comparer.Compare(points[last], points[center]) < 0) Swap(points, center, last);
                    PointRecord pivot = points[center];
                    int left = first, right = last;
                    while (left <= right)
                    {
                        while (comparer.Compare(points[left], pivot) < 0) left++;
                        while (comparer.Compare(points[right], pivot) > 0) right--;
                        if (left > right) break;
                        Swap(points, left++, right--);
                    }
                    if (middle <= right) last = right;
                    else if (middle >= left) first = left;
                    else return;
                }
            }

            private static void Swap(PointRecord[] records, int left, int right)
            {
                PointRecord value = records[left]; records[left] = records[right]; records[right] = value;
            }

            private static int LargestAxis(Aabb bounds)
            {
                double x = bounds.MaxX - bounds.MinX, y = bounds.MaxY - bounds.MinY, z = bounds.MaxZ - bounds.MinZ;
                if (double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z))
                {
                    x = bounds.MaxX * 0.5 - bounds.MinX * 0.5;
                    y = bounds.MaxY * 0.5 - bounds.MinY * 0.5;
                    z = bounds.MaxZ * 0.5 - bounds.MinZ * 0.5;
                }
                return y > x ? (z > y ? 2 : 1) : (z > x ? 2 : 0);
            }
        }

        private readonly int leafCapacity;
        private readonly ReaderWriterLockSlim sync = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private Snapshot snapshot;

        public KdTree(IEnumerable<KeyValuePair<Vector3d, T>> points) : this(points, 8) { }

        public KdTree(IEnumerable<KeyValuePair<Vector3d, T>> points, int leafCapacity)
        {
            if (leafCapacity < 1) throw new ArgumentOutOfRangeException("leafCapacity");
            this.leafCapacity = leafCapacity;
            snapshot = BuildSnapshot(points);
        }

        public int Count
        {
            get
            {
                sync.EnterReadLock();
                try { return snapshot.Points.Length; }
                finally { sync.ExitReadLock(); }
            }
        }

        /// <summary>Validates and builds privately before publishing the new index.</summary>
        public void Rebuild(IEnumerable<KeyValuePair<Vector3d, T>> points)
        {
            Snapshot replacement = BuildSnapshot(points);
            sync.EnterWriteLock();
            try { snapshot = replacement; }
            finally { sync.ExitWriteLock(); }
        }

        private Snapshot BuildSnapshot(IEnumerable<KeyValuePair<Vector3d, T>> points)
        {
            if (points == null) throw new ArgumentNullException("points");
            List<PointRecord> records = new List<PointRecord>();
            foreach (KeyValuePair<Vector3d, T> point in points)
            {
                if (ReferenceEquals(point.Value, null)) throw new ArgumentException("Point payloads cannot be null.", "points");
                records.Add(new PointRecord { Position = point.Key, Item = point.Value, SourceIndex = records.Count });
            }
            return new Builder(records.ToArray(), leafCapacity).Build();
        }

        public int Radius(Vector3d point, double maxDistance, List<Neighbor<T>> results)
        {
            return Radius(point, maxDistance, results, SpatialQueryWorkspaces.Get());
        }

        /// <summary>Returns every point within the inclusive radius, ordered by distance and input order.</summary>
        public int Radius(Vector3d point, double maxDistance, List<Neighbor<T>> results, QueryContext context)
        {
            ValidateResults(results, context);
            DistanceKey limit = DistanceKey.Limit(maxDistance);
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    results.Clear();
                    Snapshot data = snapshot;
                    if (data.Root < 0) return 0;
                    if (data.Nodes[data.Root].Bounds.Distance(point.X, point.Y, point.Z).CompareTo(limit) > 0) return 0;
                    context.Push(data.Root);
                    while (context.stackCount != 0)
                    {
                        NodeRecord node = data.Nodes[context.Pop()];
                        if (context.CollectStatistics) context.NodesVisited++;
                        if (node.Left < 0)
                        {
                            int end = node.Start + node.Count;
                            for (int i = node.Start; i < end; i++)
                            {
                                if (context.CollectStatistics) { context.EntriesTested++; context.PrimitiveTests++; }
                                DistanceKey distance = PointDistance(data.Points[i].Position, point);
                                if (distance.CompareTo(limit) > 0) continue;
                                context.EnsureCandidates(context.candidateCount + 1);
                                int candidate = context.candidateCount++;
                                context.candidates[candidate] = i;
                                context.candidateDistances[candidate] = distance;
                            }
                        }
                        else
                        {
                            if (data.Nodes[node.Left].Bounds.Distance(point.X, point.Y, point.Z).CompareTo(limit) <= 0)
                                context.Push(node.Left);
                            if (data.Nodes[node.Right].Bounds.Distance(point.X, point.Y, point.Z).CompareTo(limit) <= 0)
                                context.Push(node.Right);
                        }
                    }
                    SortCandidates(context, data.Points);
                    return WriteResults(context, data.Points, results);
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        public int KNearest(Vector3d point, int k, double maxDistance, List<Neighbor<T>> results)
        {
            return KNearest(point, k, maxDistance, results, SpatialQueryWorkspaces.Get());
        }

        /// <summary>Returns up to k points within the inclusive distance limit.</summary>
        public int KNearest(Vector3d point, int k, double maxDistance, List<Neighbor<T>> results, QueryContext context)
        {
            if (k < 0) throw new ArgumentOutOfRangeException("k");
            ValidateResults(results, context);
            DistanceKey limit = DistanceKey.Limit(maxDistance);
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    results.Clear();
                    Snapshot data = snapshot;
                    if (k == 0 || data.Root < 0) return 0;
                    k = Math.Min(k, data.Points.Length);
                    context.EnsureCandidates(k);
                    DistanceKey rootDistance = data.Nodes[data.Root].Bounds.Distance(point.X, point.Y, point.Z);
                    if (rootDistance.CompareTo(limit) > 0) return 0;
                    context.Push(data.Root, rootDistance);
                    while (context.heapCount != 0)
                    {
                        QueryContext.HeapEntry entry = context.PopNearest();
                        DistanceKey threshold = context.candidateCount == k ? context.candidateDistances[0] : limit;
                        int order = entry.Distance.CompareTo(threshold);
                        if (order > 0) break;
                        NodeRecord node = data.Nodes[entry.NodeId];
                        if (context.candidateCount == k && order == 0 &&
                            node.MinSourceIndex >= data.Points[context.candidates[0]].SourceIndex) continue;
                        if (context.CollectStatistics) context.NodesVisited++;
                        if (node.Left < 0)
                        {
                            int end = node.Start + node.Count;
                            for (int i = node.Start; i < end; i++)
                            {
                                if (context.CollectStatistics) { context.EntriesTested++; context.PrimitiveTests++; }
                                DistanceKey distance = PointDistance(data.Points[i].Position, point);
                                if (distance.CompareTo(limit) <= 0) OfferCandidate(context, data.Points, i, distance, k);
                            }
                        }
                        else
                        {
                            PushNode(data, node.Left, point, threshold, context, context.candidateCount == k);
                            PushNode(data, node.Right, point, threshold, context, context.candidateCount == k);
                        }
                    }
                    SortCandidates(context, data.Points);
                    return WriteResults(context, data.Points, results);
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        private static void ValidateResults(List<Neighbor<T>> results, QueryContext context)
        {
            if (results == null) throw new ArgumentNullException("results");
            if (context == null) throw new ArgumentNullException("context");
        }

        private static void PushNode(Snapshot data, int nodeId, Vector3d point, DistanceKey threshold, QueryContext context, bool full)
        {
            NodeRecord node = data.Nodes[nodeId];
            DistanceKey distance = node.Bounds.Distance(point.X, point.Y, point.Z);
            int order = distance.CompareTo(threshold);
            // Equal distances remain eligible only when this subtree can still
            // contribute an earlier input than the worst retained candidate.
            if (order < 0 || (order == 0 && (!full || node.MinSourceIndex < data.Points[context.candidates[0]].SourceIndex)))
                context.Push(nodeId, distance);
        }

        private static void OfferCandidate(QueryContext context, PointRecord[] points, int pointId, DistanceKey distance, int k)
        {
            if (context.candidateCount < k)
            {
                int child = context.candidateCount++;
                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (CompareCandidate(points, context.candidates[parent], context.candidateDistances[parent], pointId, distance) >= 0) break;
                    context.candidates[child] = context.candidates[parent];
                    context.candidateDistances[child] = context.candidateDistances[parent];
                    child = parent;
                }
                context.candidates[child] = pointId;
                context.candidateDistances[child] = distance;
            }
            else if (CompareCandidate(points, pointId, distance, context.candidates[0], context.candidateDistances[0]) < 0)
            {
                context.candidates[0] = pointId;
                context.candidateDistances[0] = distance;
                SiftDown(context, points, 0, context.candidateCount);
            }
        }

        private static int CompareCandidate(PointRecord[] points, int left, DistanceKey leftDistance, int right, DistanceKey rightDistance)
        {
            int order = leftDistance.CompareTo(rightDistance);
            return order != 0 ? order : points[left].SourceIndex.CompareTo(points[right].SourceIndex);
        }

        private static void SiftDown(QueryContext context, PointRecord[] points, int parent, int count)
        {
            int pointId = context.candidates[parent];
            DistanceKey distance = context.candidateDistances[parent];
            while (parent < count / 2)
            {
                int child = parent * 2 + 1;
                if (child + 1 < count && CompareCandidate(points, context.candidates[child], context.candidateDistances[child],
                    context.candidates[child + 1], context.candidateDistances[child + 1]) < 0) child++;
                if (CompareCandidate(points, pointId, distance, context.candidates[child], context.candidateDistances[child]) >= 0) break;
                context.candidates[parent] = context.candidates[child];
                context.candidateDistances[parent] = context.candidateDistances[child];
                parent = child;
            }
            context.candidates[parent] = pointId;
            context.candidateDistances[parent] = distance;
        }

        private static void SortCandidates(QueryContext context, PointRecord[] points)
        {
            int count = context.candidateCount;
            for (int i = count / 2 - 1; i >= 0; i--) SiftDown(context, points, i, count);
            for (int end = count - 1; end > 0; end--)
            {
                int pointId = context.candidates[0];
                DistanceKey distance = context.candidateDistances[0];
                context.candidates[0] = context.candidates[end];
                context.candidateDistances[0] = context.candidateDistances[end];
                context.candidates[end] = pointId;
                context.candidateDistances[end] = distance;
                SiftDown(context, points, 0, end);
            }
        }

        private static int WriteResults(QueryContext context, PointRecord[] points, List<Neighbor<T>> results)
        {
            int count = context.candidateCount;
            for (int i = 0; i < count; i++)
                results.Add(new Neighbor<T>(points[context.candidates[i]].Item, SpatialMath.DistanceFromKey(context.candidateDistances[i])));
            context.ResultCount = count;
            return count;
        }

        private static double Coordinate(Vector3d point, int axis) { return axis == 0 ? point.X : axis == 1 ? point.Y : point.Z; }

        private static Aabb PointBounds(Vector3d point) { return new Aabb(point.X, point.Y, point.Z, point.X, point.Y, point.Z); }

        private static DistanceKey PointDistance(Vector3d point, Vector3d query) { return PointBounds(point).Distance(query.X, query.Y, query.Z); }
    }
}
