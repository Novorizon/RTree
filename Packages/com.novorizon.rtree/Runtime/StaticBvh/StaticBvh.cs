using System;
using System.Collections.Generic;
using System.Threading;

namespace RTree
{
    /// <summary>
    /// A compact binary BVH for mostly static bounds. Rebuild uses a binned
    /// surface-area heuristic; Refit preserves the original source order.
    /// Concurrent queries are supported. Intersectors must not reenter this tree.
    /// </summary>
    public sealed class StaticBvh<T> : IBoundsQuery3<T>, IRayQuery3<T>
    {
        private struct Node
        {
            internal Aabb Bounds;
            internal int Left, Right, Start, Count;
        }

        private readonly ReaderWriterLockSlim sync = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private readonly int leafCapacity;
        private Node[] nodes = new Node[0];
        private Aabb[] bounds = new Aabb[0];
        private T[] items = new T[0];
        private int[] order = new int[0];

        public StaticBvh(IEnumerable<KeyValuePair<Bounds3, T>> items) : this(items, 4) { }

        public StaticBvh(IEnumerable<KeyValuePair<Bounds3, T>> items, int leafCapacity)
        {
            if (leafCapacity < 1) throw new ArgumentOutOfRangeException("leafCapacity");
            this.leafCapacity = leafCapacity;
            Rebuild(items);
        }

        public int Count
        {
            get { sync.EnterReadLock(); try { return items.Length; } finally { sync.ExitReadLock(); } }
        }

        public int LeafCapacity { get { return leafCapacity; } }

        /// <summary>Atomically replaces all primitives, preserving enumeration order as source order.</summary>
        public void Rebuild(IEnumerable<KeyValuePair<Bounds3, T>> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var newBounds = new List<Aabb>();
            var newItems = new List<T>();
            foreach (KeyValuePair<Bounds3, T> pair in source)
            {
                ValidateBounds(pair.Key, "source");
                if ((object)pair.Value == null) throw new ArgumentException("Payloads cannot be null.", "source");
                newBounds.Add(pair.Key.Box);
                newItems.Add(pair.Value);
            }
            Aabb[] builtBounds = newBounds.ToArray();
            T[] builtItems = newItems.ToArray();
            Builder builder = new Builder(builtBounds, leafCapacity);
            builder.Build();
            sync.EnterWriteLock();
            try
            {
                bounds = builtBounds;
                items = builtItems;
                order = builder.Order;
                nodes = builder.Nodes;
            }
            finally { sync.ExitWriteLock(); }
        }

        /// <summary>Updates bounds in original source order without changing payloads or topology.</summary>
        public void Refit(IList<Bounds3> boundsInSourceOrder)
        {
            if (boundsInSourceOrder == null) throw new ArgumentNullException("boundsInSourceOrder");
            Aabb[] replacement = new Aabb[boundsInSourceOrder.Count];
            for (int i = 0; i < replacement.Length; i++)
            {
                Bounds3 box = boundsInSourceOrder[i];
                ValidateBounds(box, "boundsInSourceOrder");
                replacement[i] = box.Box;
            }
            sync.EnterWriteLock();
            try
            {
                if (replacement.Length != bounds.Length)
                    throw new ArgumentException("The bound count must equal Count in original source order.", "boundsInSourceOrder");
                // All validation and allocation precedes publication. Parent IDs
                // precede their children, so a reverse pass refits bottom-up.
                for (int n = nodes.Length - 1; n >= 0; n--)
                {
                    Node node = nodes[n];
                    if (node.Count != 0)
                    {
                        Aabb box = replacement[order[node.Start]];
                        for (int j = 1; j < node.Count; j++) box = Aabb.Union(box, replacement[order[node.Start + j]]);
                        node.Bounds = box;
                    }
                    else node.Bounds = Aabb.Union(nodes[node.Left].Bounds, nodes[node.Right].Bounds);
                    nodes[n] = node;
                }
                bounds = replacement;
            }
            finally { sync.ExitWriteLock(); }
        }

        public int Query(Bounds3 query, List<T> results) { return Query(query, results, SpatialQueryWorkspaces.Get()); }
        public int Query(Bounds3 query, List<T> results, QueryContext context)
        {
            ValidateBounds(query, "query");
            return QueryRange(query, default(Sphere3), null, 0, results, context);
        }

        public int QuerySphere(Sphere3 sphere, List<T> results) { return QuerySphere(sphere, results, SpatialQueryWorkspaces.Get()); }
        public int QuerySphere(Sphere3 sphere, List<T> results, QueryContext context)
        {
            return QueryRange(default(Bounds3), sphere, null, 1, results, context);
        }

        public int QueryFrustum(Frustum3 frustum, List<T> results) { return QueryFrustum(frustum, results, SpatialQueryWorkspaces.Get()); }
        public int QueryFrustum(Frustum3 frustum, List<T> results, QueryContext context)
        {
            if (frustum == null) throw new ArgumentNullException("frustum");
            return QueryRange(default(Bounds3), default(Sphere3), frustum, 2, results, context);
        }

        private int QueryRange(Bounds3 query, Sphere3 sphere, Frustum3 frustum, int kind, List<T> results, QueryContext context)
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
                    if (nodes.Length == 0) return 0;
                    bool statistics = context.CollectStatistics;
                    context.Push(0);
                    while (context.stackCount != 0)
                    {
                        int pending = context.Pop();
                        bool accepted = pending < 0;
                        if (accepted) pending = ~pending;
                        Node node = nodes[pending];
                        if (statistics) context.NodesVisited++;
                        if (!accepted)
                        {
                            if (statistics) context.EntriesTested++;
                            if (!Matches(node.Bounds, query, sphere, frustum, kind)) continue;
                            accepted = kind == 0 && query.Box.Contains(node.Bounds);
                        }
                        if (node.Count == 0)
                        {
                            context.Push(accepted ? ~node.Right : node.Right);
                            context.Push(accepted ? ~node.Left : node.Left);
                        }
                        else
                        {
                            for (int i = 0; i < node.Count; i++)
                            {
                                int id = order[node.Start + i];
                                if (statistics && !accepted) { context.EntriesTested++; context.PrimitiveTests++; }
                                if (accepted || Matches(bounds[id], query, sphere, frustum, kind)) results.Add(items[id]);
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

        private static bool Matches(Aabb box, Bounds3 query, Sphere3 sphere, Frustum3 frustum, int kind)
        {
            if (kind == 0) return box.Intersects(query.Box);
            Bounds3 publicBox = Bounds3.FromAabb(box);
            return kind == 1 ? SpatialMath.Intersects(publicBox, sphere) : frustum.Intersects(publicBox);
        }

        public bool RaycastAny(Ray3 ray, out RayHit<T> hit) { return RaycastAny(ray, out hit, SpatialQueryWorkspaces.Get()); }
        public bool RaycastAny(Ray3 ray, out RayHit<T> hit, QueryContext context) { return RaycastFirst(ray, null, false, out hit, context); }
        public bool RaycastAny(Ray3 ray, IRayIntersector<T> intersector, out RayHit<T> hit) { return RaycastAny(ray, intersector, out hit, SpatialQueryWorkspaces.Get()); }
        public bool RaycastAny(Ray3 ray, IRayIntersector<T> intersector, out RayHit<T> hit, QueryContext context)
        {
            if (intersector == null) throw new ArgumentNullException("intersector");
            return RaycastFirst(ray, intersector, false, out hit, context);
        }

        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit) { return RaycastClosest(ray, out hit, SpatialQueryWorkspaces.Get()); }
        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit, QueryContext context) { return RaycastFirst(ray, null, true, out hit, context); }
        public bool RaycastClosest(Ray3 ray, IRayIntersector<T> intersector, out RayHit<T> hit) { return RaycastClosest(ray, intersector, out hit, SpatialQueryWorkspaces.Get()); }
        public bool RaycastClosest(Ray3 ray, IRayIntersector<T> intersector, out RayHit<T> hit, QueryContext context)
        {
            if (intersector == null) throw new ArgumentNullException("intersector");
            return RaycastFirst(ray, intersector, true, out hit, context);
        }

        private bool RaycastFirst(Ray3 ray, IRayIntersector<T> intersector, bool closest, out RayHit<T> hit, QueryContext context)
        {
            ray.Validate();
            if (context == null) throw new ArgumentNullException("context");
            hit = default(RayHit<T>);
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    double near, far;
                    if (nodes.Length == 0) return false;
                    bool statistics = context.CollectStatistics;
                    if (statistics) context.EntriesTested++;
                    if (!ray.IntersectsUnchecked(nodes[0].Bounds, out near, out far)) return false;
                    bool found = false;
                    double best = ray.TMax;
                    context.Push(0, new DistanceKey(near, 4));
                    while (context.heapCount != 0)
                    {
                        QueryContext.HeapEntry entry = context.PopNearest();
                        if (entry.Distance.CompareTo(new DistanceKey(best, 4)) > 0) break;
                        Node node = nodes[entry.NodeId];
                        if (statistics) context.NodesVisited++;
                        if (node.Count == 0)
                        {
                            if (statistics) context.EntriesTested += 2;
                            if (ray.IntersectsUnchecked(nodes[node.Left].Bounds, out near, out far) && near <= best)
                                context.Push(node.Left, new DistanceKey(near, 4));
                            if (ray.IntersectsUnchecked(nodes[node.Right].Bounds, out near, out far) && near <= best)
                                context.Push(node.Right, new DistanceKey(near, 4));
                            continue;
                        }
                        for (int i = 0; i < node.Count; i++)
                        {
                            int id = order[node.Start + i];
                            if (statistics) { context.EntriesTested++; if (intersector == null) context.PrimitiveTests++; }
                            if (!ray.IntersectsUnchecked(bounds[id], out near, out far) || near > best) continue;
                            double parameter = near;
                            if (intersector != null)
                            {
                                if (statistics) context.PrimitiveTests++;
                                if (!intersector.Intersect(items[id], ray, out parameter) || !ValidParameter(parameter, ray)) continue;
                            }
                            if (!ValidParameter(parameter, ray) || parameter > best) continue;
                            best = parameter;
                            hit = new RayHit<T>(items[id], parameter);
                            found = true;
                            if (!closest) { context.ResultCount = 1; return true; }
                        }
                    }
                    context.ResultCount = found ? 1 : 0;
                    return found;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        public int RaycastAll(Ray3 ray, List<RayHit<T>> results) { return RaycastAll(ray, results, SpatialQueryWorkspaces.Get()); }
        public int RaycastAll(Ray3 ray, List<RayHit<T>> results, QueryContext context) { return RaycastAllCore(ray, null, results, context); }
        public int RaycastAll(Ray3 ray, IRayIntersector<T> intersector, List<RayHit<T>> results) { return RaycastAll(ray, intersector, results, SpatialQueryWorkspaces.Get()); }
        public int RaycastAll(Ray3 ray, IRayIntersector<T> intersector, List<RayHit<T>> results, QueryContext context)
        {
            if (intersector == null) throw new ArgumentNullException("intersector");
            return RaycastAllCore(ray, intersector, results, context);
        }

        private int RaycastAllCore(Ray3 ray, IRayIntersector<T> intersector, List<RayHit<T>> results, QueryContext context)
        {
            ray.Validate();
            if (results == null) throw new ArgumentNullException("results");
            if (context == null) throw new ArgumentNullException("context");
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    results.Clear();
                    if (nodes.Length == 0) return 0;
                    bool statistics = context.CollectStatistics;
                    context.Push(0);
                    while (context.stackCount != 0)
                    {
                        Node node = nodes[context.Pop()];
                        double near, far;
                        if (statistics) { context.NodesVisited++; context.EntriesTested++; }
                        if (!ray.IntersectsUnchecked(node.Bounds, out near, out far)) continue;
                        if (node.Count == 0)
                        {
                            context.Push(node.Right);
                            context.Push(node.Left);
                        }
                        else
                        {
                            for (int i = 0; i < node.Count; i++)
                            {
                                int id = order[node.Start + i];
                                if (statistics) { context.EntriesTested++; if (intersector == null) context.PrimitiveTests++; }
                                if (!ray.IntersectsUnchecked(bounds[id], out near, out far)) continue;
                                double parameter = near;
                                if (intersector != null)
                                {
                                    if (statistics) context.PrimitiveTests++;
                                    if (!intersector.Intersect(items[id], ray, out parameter) || !ValidParameter(parameter, ray)) continue;
                                }
                                if (ValidParameter(parameter, ray)) results.Add(new RayHit<T>(items[id], parameter));
                            }
                        }
                    }
                    SortHits(results);
                    context.ResultCount = results.Count;
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        private static bool ValidParameter(double parameter, Ray3 ray)
        {
            return !double.IsNaN(parameter) && !double.IsInfinity(parameter) && parameter >= ray.TMin && parameter <= ray.TMax;
        }

        // In-place heapsort avoids comparer/delegate allocations in hot queries.
        private static void SortHits(List<RayHit<T>> results)
        {
            for (int i = results.Count / 2 - 1; i >= 0; i--) SiftHits(results, i, results.Count);
            for (int end = results.Count - 1; end > 0; end--)
            {
                RayHit<T> first = results[0]; results[0] = results[end]; results[end] = first;
                SiftHits(results, 0, end);
            }
        }

        private static void SiftHits(List<RayHit<T>> results, int root, int count)
        {
            RayHit<T> value = results[root];
            while (root < count / 2)
            {
                int child = root * 2 + 1;
                if (child + 1 < count && results[child].Parameter < results[child + 1].Parameter) child++;
                if (value.Parameter >= results[child].Parameter) break;
                results[root] = results[child]; root = child;
            }
            results[root] = value;
        }

        private static void ValidateBounds(Bounds3 box, string name)
        {
            if (!Finite(box.MinX) || !Finite(box.MinY) || !Finite(box.MinZ) || !Finite(box.MaxX) || !Finite(box.MaxY) || !Finite(box.MaxZ) ||
                box.MinX > box.MaxX || box.MinY > box.MaxY || box.MinZ > box.MaxZ)
                throw new ArgumentException("Bounds must have finite ordered coordinates.", name);
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        private sealed class Builder
        {
            private const int BinCount = 12;
            private readonly Aabb[] source;
            private readonly int capacity;
            private readonly int[] binCounts = new int[BinCount];
            private readonly Aabb[] binBounds = new Aabb[BinCount];
            private readonly int[] rightCounts = new int[BinCount];
            private readonly Aabb[] rightBounds = new Aabb[BinCount];
            private int nodeCount;
            internal Node[] Nodes;
            internal int[] Order;

            internal Builder(Aabb[] source, int capacity)
            {
                this.source = source; this.capacity = capacity;
                Order = new int[source.Length];
                for (int i = 0; i < Order.Length; i++) Order[i] = i;
                Nodes = new Node[source.Length == 0 ? 0 : checked(source.Length * 2 - 1)];
            }

            internal void Build()
            {
                if (Order.Length != 0) BuildNode(0, Order.Length, 0);
                if (nodeCount != Nodes.Length) Array.Resize(ref Nodes, nodeCount);
            }

            private int BuildNode(int start, int count, int depth)
            {
                int id = nodeCount++;
                Aabb box = source[Order[start]];
                Aabb centers = CenterBox(box);
                for (int i = 1; i < count; i++)
                {
                    Aabb next = source[Order[start + i]];
                    box = Aabb.Union(box, next);
                    centers = Aabb.Union(centers, CenterBox(next));
                }
                Node node = new Node(); node.Bounds = box;
                if (count <= capacity)
                {
                    node.Start = start; node.Count = count; Nodes[id] = node; return id;
                }
                int axis = 0, split = -1;
                bool hasSplit = depth < 64 && FindSplit(start, count, box, centers, out axis, out split);
                if (!hasSplit) { axis = WidestAxis(centers); split = -1; }
                int middle = split < 0 ? start : Partition(start, count, axis, split, centers);
                if (middle == start || middle == start + count)
                {
                    middle = start + count / 2;
                    SelectMedian(start, start + count - 1, middle, axis);
                }
                node.Left = BuildNode(start, middle - start, depth + 1);
                node.Right = BuildNode(middle, start + count - middle, depth + 1);
                Nodes[id] = node;
                return id;
            }

            private bool FindSplit(int start, int count, Aabb parent, Aabb centers, out int bestAxis, out int bestSplit)
            {
                bestAxis = 0; bestSplit = -1;
                double bestCost = double.PositiveInfinity;
                double scale = Math.Max(Math.Max(Math.Abs(parent.MinX), Math.Abs(parent.MaxX)),
                    Math.Max(Math.Max(Math.Abs(parent.MinY), Math.Abs(parent.MaxY)), Math.Max(Math.Abs(parent.MinZ), Math.Abs(parent.MaxZ))));
                if (scale == 0) scale = 1;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (centers.Min(axis) == centers.Max(axis)) continue;
                    Array.Clear(binCounts, 0, BinCount);
                    for (int i = 0; i < count; i++)
                    {
                        Aabb primitive = source[Order[start + i]];
                        int bin = Bin(primitive.Center(axis), centers.Min(axis), centers.Max(axis));
                        binBounds[bin] = binCounts[bin]++ == 0 ? primitive : Aabb.Union(binBounds[bin], primitive);
                    }
                    int suffixCount = 0; Aabb suffixBox = default(Aabb);
                    for (int i = BinCount - 1; i > 0; i--)
                    {
                        if (binCounts[i] != 0) suffixBox = suffixCount == 0 ? binBounds[i] : Aabb.Union(suffixBox, binBounds[i]);
                        suffixCount += binCounts[i]; rightCounts[i] = suffixCount; rightBounds[i] = suffixBox;
                    }
                    int prefixCount = 0; Aabb prefixBox = default(Aabb);
                    for (int i = 0; i < BinCount - 1; i++)
                    {
                        if (binCounts[i] != 0) prefixBox = prefixCount == 0 ? binBounds[i] : Aabb.Union(prefixBox, binBounds[i]);
                        prefixCount += binCounts[i];
                        if (prefixCount == 0 || rightCounts[i + 1] == 0) continue;
                        double cost = Area(prefixBox, scale) * prefixCount + Area(rightBounds[i + 1], scale) * rightCounts[i + 1];
                        if (cost < bestCost)
                        {
                            bestCost = cost; bestAxis = axis; bestSplit = i;
                        }
                    }
                }
                return bestSplit >= 0;
            }

            private int Partition(int start, int count, int axis, int split, Aabb centers)
            {
                int left = start, right = start + count - 1;
                while (left <= right)
                {
                    if (Bin(source[Order[left]].Center(axis), centers.Min(axis), centers.Max(axis)) <= split) left++;
                    else { int value = Order[left]; Order[left] = Order[right]; Order[right--] = value; }
                }
                return left;
            }

            private void SelectMedian(int left, int right, int middle, int axis)
            {
                // Median partition guarantees balanced fallback children,
                // including the case where all centroids coincide.
                while (left < right)
                {
                    double pivot = source[Order[left + (right - left) / 2]].Center(axis);
                    int i = left, j = right;
                    while (i <= j)
                    {
                        while (i <= right && source[Order[i]].Center(axis) < pivot) i++;
                        while (j >= left && source[Order[j]].Center(axis) > pivot) j--;
                        if (i <= j) { int value = Order[i]; Order[i++] = Order[j]; Order[j--] = value; }
                    }
                    if (middle <= j) right = j;
                    else if (middle >= i) left = i;
                    else return;
                }
            }

            private static int Bin(double center, double minimum, double maximum)
            {
                double scale = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
                double value = scale == 0 ? 0 : (center / scale - minimum / scale) / (maximum / scale - minimum / scale);
                int bin = (int)(value * BinCount);
                return bin < 0 ? 0 : bin >= BinCount ? BinCount - 1 : bin;
            }

            private static double Area(Aabb box, double scale)
            {
                double x = box.MaxX / scale - box.MinX / scale;
                double y = box.MaxY / scale - box.MinY / scale;
                double z = box.MaxZ / scale - box.MinZ / scale;
                return x * y + x * z + y * z;
            }

            private static Aabb CenterBox(Aabb box)
            {
                double x = box.Center(0), y = box.Center(1), z = box.Center(2);
                return new Aabb(x, y, z, x, y, z);
            }

            private static int WidestAxis(Aabb box)
            {
                double x = box.MaxX * 0.5 - box.MinX * 0.5;
                double y = box.MaxY * 0.5 - box.MinY * 0.5;
                double z = box.MaxZ * 0.5 - box.MinZ * 0.5;
                return x >= y && x >= z ? 0 : y >= z ? 1 : 2;
            }
        }
    }
}
