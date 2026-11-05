using System;
using System.Collections.Generic;
using System.Threading;

namespace RTree
{
    /// <summary>
    /// A mutable sparse loose octree. Each proxy occurs once. Insertion chooses
    /// children by box center and descends only while their loose bounds fit.
    /// Queries test the actual bounds, rather than returning loose-cell hits.
    /// Concurrent readers are supported; a writer excludes readers.
    /// </summary>
    public sealed class LooseOctree<T> : IMutableBoundsIndex3<T>, IRayQuery3<T>
    {
        private const int SplitThreshold = 8;

        private struct OctreeNode
        {
            internal Bounds3 Tight, Loose;
            internal int Parent, ParentOctant, Depth, FirstItem, ItemCount, ChildCount;
            internal bool Split, HasBounds;
        }

        private readonly ReaderWriterLockSlim sync =
            new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private readonly ProxyStore<T> proxies = new ProxyStore<T>();
        private readonly int maxDepth;
        private readonly double looseness;
        private readonly bool autoExpand;
        private Bounds3 worldBounds;
        private OctreeNode[] nodes = new OctreeNode[16];
        // Separate from cell metadata so rejected queries read only 48 bytes.
        // Bounds grow on writes; Rebuild removes slack left by moves/removals.
        private Aabb[] queryBounds = new Aabb[16];
        private int[] children = new int[16 * 8];
        private int[] freeNodes = new int[16];
        private int freeNodeCount, nextNode;
        private int[] owner = new int[16], nextItem = new int[16], previousItem = new int[16];

        public LooseOctree(Bounds3 worldBounds) : this(worldBounds, 16, 2.0, true) { }

        /// <param name="worldBounds">Initial tight root bounds.</param>
        /// <param name="maxDepth">Maximum number of subdivisions, between 0 and 32.</param>
        /// <param name="looseness">Loose cell size relative to its tight cell; at least 1.</param>
        /// <param name="autoExpand">Expand the world and rebuild when an object leaves it.</param>
        public LooseOctree(Bounds3 worldBounds, int maxDepth, double looseness, bool autoExpand)
        {
            if (maxDepth < 0 || maxDepth > 32) throw new ArgumentOutOfRangeException("maxDepth");
            if (double.IsNaN(looseness) || double.IsInfinity(looseness) || looseness < 1)
                throw new ArgumentOutOfRangeException("looseness");
            this.worldBounds = worldBounds;
            this.maxDepth = maxDepth;
            this.looseness = looseness;
            this.autoExpand = autoExpand;
            ResetNodes();
        }

        public int Count
        {
            get
            {
                sync.EnterReadLock();
                try { return proxies.Count; }
                finally { sync.ExitReadLock(); }
            }
        }

        public Bounds3 WorldBounds
        {
            get
            {
                sync.EnterReadLock();
                try { return worldBounds; }
                finally { sync.ExitReadLock(); }
            }
        }

        public ProxyHandle Insert(Bounds3 bounds, T item)
        {
            if ((object)item == null) throw new ArgumentNullException("item");
            sync.EnterWriteLock();
            try
            {
                bool expand = !worldBounds.Contains(bounds);
                if (expand && !autoExpand) throw new ArgumentOutOfRangeException("bounds", "Bounds lie outside the fixed world.");
                int slot = proxies.Allocate(bounds, item);
                EnsureItemCapacity(slot);
                if (expand)
                {
                    worldBounds = ExpandedWorld(bounds);
                    RebuildCore();
                }
                else Place(slot, 0);
                return proxies.Handle(slot);
            }
            finally { sync.ExitWriteLock(); }
        }

        /// <summary>
        /// Updates actual bounds. Returns true when a proxy is structurally
        /// reinserted or the root is rebuilt, and false while its loose cell fits.
        /// An invalid, expired or foreign handle throws ArgumentException.
        /// </summary>
        public bool Move(ProxyHandle handle, Bounds3 bounds)
        {
            sync.EnterWriteLock();
            try
            {
                int slot;
                if (!proxies.TryResolve(handle, out slot)) throw new ArgumentException("The proxy handle is invalid.", "handle");
                bool expand = !worldBounds.Contains(bounds);
                if (expand && !autoExpand) throw new ArgumentOutOfRangeException("bounds", "Bounds lie outside the fixed world.");
                if (expand)
                {
                    proxies.Bounds[slot] = bounds;
                    worldBounds = ExpandedWorld(bounds);
                    RebuildCore();
                    return true;
                }
                if (nodes[owner[slot]].Loose.Contains(bounds))
                {
                    proxies.Bounds[slot] = bounds;
                    GrowBounds(owner[slot], bounds.Box);
                    return false;
                }
                int oldNode = owner[slot];
                Unlink(slot);
                Prune(oldNode);
                proxies.Bounds[slot] = bounds;
                Place(slot, 0);
                return true;
            }
            finally { sync.ExitWriteLock(); }
        }

        public bool Remove(ProxyHandle handle)
        {
            sync.EnterWriteLock();
            try
            {
                int slot;
                if (!proxies.TryResolve(handle, out slot)) return false;
                int oldNode = owner[slot];
                Unlink(slot);
                proxies.Release(slot);
                Prune(oldNode);
                return true;
            }
            finally { sync.ExitWriteLock(); }
        }

        public bool TryGetBounds(ProxyHandle handle, out Bounds3 bounds)
        {
            sync.EnterReadLock();
            try
            {
                int slot;
                if (proxies.TryResolve(handle, out slot))
                {
                    bounds = proxies.Bounds[slot];
                    return true;
                }
                bounds = default(Bounds3);
                return false;
            }
            finally { sync.ExitReadLock(); }
        }

        /// <summary>Repartitions all proxies without invalidating their handles.</summary>
        public void Rebuild()
        {
            sync.EnterWriteLock();
            try { RebuildCore(); }
            finally { sync.ExitWriteLock(); }
        }

        private void RebuildCore()
        {
            ResetNodes();
            for (int slot = 0; slot < proxies.Next; slot++)
                if (proxies.Live[slot]) Place(slot, 0);
        }

        private void ResetNodes()
        {
            nextNode = 0;
            freeNodeCount = 0;
            AllocateNode(worldBounds, -1, -1, 0);
        }

        private void EnsureItemCapacity(int slot)
        {
            if (slot < owner.Length) return;
            int capacity = Math.Max(checked(owner.Length * 2), checked(slot + 1));
            Array.Resize(ref owner, capacity);
            Array.Resize(ref nextItem, capacity);
            Array.Resize(ref previousItem, capacity);
        }

        private int AllocateNode(Bounds3 tight, int parent, int octant, int depth)
        {
            int id;
            if (freeNodeCount != 0) id = freeNodes[--freeNodeCount];
            else
            {
                id = nextNode++;
                if (id == nodes.Length)
                {
                    int capacity = checked(nodes.Length * 2);
                    Array.Resize(ref nodes, capacity);
                    Array.Resize(ref queryBounds, capacity);
                    Array.Resize(ref children, checked(capacity * 8));
                    Array.Resize(ref freeNodes, capacity);
                }
            }
            OctreeNode node = new OctreeNode();
            node.Tight = tight;
            node.Loose = LooseBounds(tight);
            // Rounding or saturation must never make a child escape its parent.
            if (parent >= 0) node.Loose = Intersection(node.Loose, nodes[parent].Loose);
            node.Parent = parent;
            node.ParentOctant = octant;
            node.Depth = depth;
            node.FirstItem = -1;
            nodes[id] = node;
            queryBounds[id] = default(Aabb);
            int offset = id * 8;
            for (int i = 0; i < 8; i++) children[offset + i] = -1;
            return id;
        }

        private void Link(int slot, int nodeId)
        {
            int first = nodes[nodeId].FirstItem;
            owner[slot] = nodeId;
            previousItem[slot] = -1;
            nextItem[slot] = first;
            if (first >= 0) previousItem[first] = slot;
            nodes[nodeId].FirstItem = slot;
            nodes[nodeId].ItemCount++;
            GrowBounds(nodeId, proxies.Bounds[slot].Box);
        }

        private void GrowBounds(int nodeId, Aabb bounds)
        {
            while (nodeId >= 0)
            {
                if (nodes[nodeId].HasBounds)
                {
                    if (queryBounds[nodeId].Contains(bounds)) return;
                    queryBounds[nodeId] = Aabb.Union(queryBounds[nodeId], bounds);
                }
                else
                {
                    queryBounds[nodeId] = bounds;
                    nodes[nodeId].HasBounds = true;
                }
                nodeId = nodes[nodeId].Parent;
            }
        }

        private void Unlink(int slot)
        {
            int nodeId = owner[slot], previous = previousItem[slot], next = nextItem[slot];
            if (previous >= 0) nextItem[previous] = next;
            else nodes[nodeId].FirstItem = next;
            if (next >= 0) previousItem[next] = previous;
            nodes[nodeId].ItemCount--;
            owner[slot] = nextItem[slot] = previousItem[slot] = -1;
        }

        private void Place(int slot, int nodeId)
        {
            while (true)
            {
                if (!nodes[nodeId].Split && nodes[nodeId].ItemCount >= SplitThreshold && CanSplit(nodeId))
                {
                    nodes[nodeId].Split = true;
                    Redistribute(nodeId);
                }
                if (!nodes[nodeId].Split)
                {
                    Link(slot, nodeId);
                    return;
                }
                Bounds3 childTight, childLoose;
                int octant;
                if (!FindChild(nodeId, proxies.Bounds[slot], out octant, out childTight, out childLoose))
                {
                    Link(slot, nodeId);
                    return;
                }
                int child = children[nodeId * 8 + octant];
                if (child < 0)
                {
                    child = AllocateNode(childTight, nodeId, octant, nodes[nodeId].Depth + 1);
                    children[nodeId * 8 + octant] = child;
                    nodes[nodeId].ChildCount++;
                }
                nodeId = child;
            }
        }

        private void Redistribute(int nodeId)
        {
            int slot = nodes[nodeId].FirstItem;
            while (slot >= 0)
            {
                int next = nextItem[slot];
                int octant;
                Bounds3 childTight, childLoose;
                if (FindChild(nodeId, proxies.Bounds[slot], out octant, out childTight, out childLoose))
                {
                    int child = children[nodeId * 8 + octant];
                    if (child < 0)
                    {
                        child = AllocateNode(childTight, nodeId, octant, nodes[nodeId].Depth + 1);
                        children[nodeId * 8 + octant] = child;
                        nodes[nodeId].ChildCount++;
                    }
                    Unlink(slot);
                    Place(slot, child);
                }
                slot = next;
            }
        }

        private bool CanSplit(int nodeId)
        {
            OctreeNode node = nodes[nodeId];
            if (node.Depth >= maxDepth) return false;
            Bounds3 tight = node.Tight;
            double x = Midpoint(tight.MinX, tight.MaxX), y = Midpoint(tight.MinY, tight.MaxY), z = Midpoint(tight.MinZ, tight.MaxZ);
            return (x > tight.MinX && x < tight.MaxX) ||
                   (y > tight.MinY && y < tight.MaxY) ||
                   (z > tight.MinZ && z < tight.MaxZ);
        }

        private bool FindChild(int nodeId, Bounds3 bounds, out int octant, out Bounds3 tight, out Bounds3 loose)
        {
            Bounds3 parent = nodes[nodeId].Tight;
            double x = Midpoint(parent.MinX, parent.MaxX), y = Midpoint(parent.MinY, parent.MaxY), z = Midpoint(parent.MinZ, parent.MaxZ);
            bool splitX = x > parent.MinX && x < parent.MaxX;
            bool splitY = y > parent.MinY && y < parent.MaxY;
            bool splitZ = z > parent.MinZ && z < parent.MaxZ;
            bool highX = splitX && Midpoint(bounds.MinX, bounds.MaxX) >= x;
            bool highY = splitY && Midpoint(bounds.MinY, bounds.MaxY) >= y;
            bool highZ = splitZ && Midpoint(bounds.MinZ, bounds.MaxZ) >= z;
            octant = (highX ? 1 : 0) | (highY ? 2 : 0) | (highZ ? 4 : 0);
            tight = new Bounds3(highX ? x : parent.MinX, highY ? y : parent.MinY, highZ ? z : parent.MinZ,
                splitX && !highX ? x : parent.MaxX, splitY && !highY ? y : parent.MaxY, splitZ && !highZ ? z : parent.MaxZ);
            loose = Intersection(LooseBounds(tight), nodes[nodeId].Loose);
            return loose.Contains(bounds);
        }

        private void Prune(int nodeId)
        {
            while (nodeId != 0 && nodes[nodeId].ItemCount == 0 && nodes[nodeId].ChildCount == 0)
            {
                int parent = nodes[nodeId].Parent;
                children[parent * 8 + nodes[nodeId].ParentOctant] = -1;
                nodes[parent].ChildCount--;
                nodes[nodeId] = default(OctreeNode);
                freeNodes[freeNodeCount++] = nodeId;
                nodeId = parent;
            }
        }

        private Bounds3 LooseBounds(Bounds3 tight)
        {
            double factor = looseness - 1;
            double x = (tight.MaxX * 0.5 - tight.MinX * 0.5) * factor;
            double y = (tight.MaxY * 0.5 - tight.MinY * 0.5) * factor;
            double z = (tight.MaxZ * 0.5 - tight.MinZ * 0.5) * factor;
            return new Bounds3(Subtract(tight.MinX, x), Subtract(tight.MinY, y), Subtract(tight.MinZ, z),
                Add(tight.MaxX, x), Add(tight.MaxY, y), Add(tight.MaxZ, z));
        }

        private Bounds3 ExpandedWorld(Bounds3 bounds)
        {
            Bounds3 union = Bounds3.Union(worldBounds, bounds);
            // Reserve an additional half-extent on each side to avoid rebuilding
            // for every small movement across the previous world boundary.
            double x = union.MaxX * 0.5 - union.MinX * 0.5;
            double y = union.MaxY * 0.5 - union.MinY * 0.5;
            double z = union.MaxZ * 0.5 - union.MinZ * 0.5;
            return new Bounds3(Subtract(union.MinX, x), Subtract(union.MinY, y), Subtract(union.MinZ, z),
                Add(union.MaxX, x), Add(union.MaxY, y), Add(union.MaxZ, z));
        }

        private static double Midpoint(double minimum, double maximum)
        {
            return minimum == maximum ? minimum : minimum * 0.5 + maximum * 0.5;
        }

        private static double Add(double value, double margin)
        {
            double result = value + margin;
            return double.IsPositiveInfinity(result) ? double.MaxValue : result;
        }

        private static double Subtract(double value, double margin)
        {
            double result = value - margin;
            return double.IsNegativeInfinity(result) ? -double.MaxValue : result;
        }

        private static Bounds3 Intersection(Bounds3 left, Bounds3 right)
        {
            return new Bounds3(Math.Max(left.MinX, right.MinX), Math.Max(left.MinY, right.MinY), Math.Max(left.MinZ, right.MinZ),
                Math.Min(left.MaxX, right.MaxX), Math.Min(left.MaxY, right.MaxY), Math.Min(left.MaxZ, right.MaxZ));
        }

        public int Query(Bounds3 bounds, List<T> results) { return Query(bounds, results, SpatialQueryWorkspaces.Get()); }
        public int QuerySphere(Sphere3 sphere, List<T> results) { return QuerySphere(sphere, results, SpatialQueryWorkspaces.Get()); }
        public int QueryFrustum(Frustum3 frustum, List<T> results) { return QueryFrustum(frustum, results, SpatialQueryWorkspaces.Get()); }

        public int Query(Bounds3 bounds, List<T> results, QueryContext context)
        {
            return QueryCore(0, bounds, default(Sphere3), default(Frustum3), results, context);
        }

        public int QuerySphere(Sphere3 sphere, List<T> results, QueryContext context)
        {
            return QueryCore(1, default(Bounds3), sphere, default(Frustum3), results, context);
        }

        public int QueryFrustum(Frustum3 frustum, List<T> results, QueryContext context)
        {
            if (frustum == null) throw new ArgumentNullException("frustum");
            return QueryCore(2, default(Bounds3), default(Sphere3), frustum, results, context);
        }

        private int QueryCore(int kind, Bounds3 bounds, Sphere3 sphere, Frustum3 frustum, List<T> results, QueryContext context)
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
                    if (proxies.Count == 0) return 0;
                    bool statistics = context.CollectStatistics;
                    context.Push(0);
                    while (context.stackCount != 0)
                    {
                        int nodeId = context.Pop();
                        bool accepted = nodeId < 0;
                        if (accepted) nodeId = ~nodeId;
                        if (statistics) context.NodesVisited++;
                        if (!accepted)
                        {
                            if (statistics) context.EntriesTested++;
                            if (!Matches(kind, bounds, sphere, frustum, Bounds3.FromAabb(queryBounds[nodeId]))) continue;
                            accepted = kind == 0 && bounds.Box.Contains(queryBounds[nodeId]);
                        }
                        OctreeNode node = nodes[nodeId];
                        for (int slot = node.FirstItem; slot >= 0; slot = nextItem[slot])
                        {
                            if (statistics && !accepted) { context.EntriesTested++; context.PrimitiveTests++; }
                            if (accepted || Matches(kind, bounds, sphere, frustum, proxies.Bounds[slot])) results.Add(proxies.Items[slot]);
                        }
                        int offset = nodeId * 8;
                        for (int i = 0; i < 8; i++)
                            if (children[offset + i] >= 0) context.Push(accepted ? ~children[offset + i] : children[offset + i]);
                    }
                    context.ResultCount = results.Count;
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        private static bool Matches(int kind, Bounds3 bounds, Sphere3 sphere, Frustum3 frustum, Bounds3 target)
        {
            if (kind == 0) return bounds.Intersects(target);
            if (kind == 1) return SpatialMath.Intersects(target, sphere);
            return frustum.Intersects(target);
        }

        public bool RaycastAny(Ray3 ray, out RayHit<T> hit) { return RaycastAny(ray, out hit, SpatialQueryWorkspaces.Get()); }
        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit) { return RaycastClosest(ray, out hit, SpatialQueryWorkspaces.Get()); }
        public int RaycastAll(Ray3 ray, List<RayHit<T>> results) { return RaycastAll(ray, results, SpatialQueryWorkspaces.Get()); }

        public bool RaycastAny(Ray3 ray, out RayHit<T> hit, QueryContext context)
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
                    if (proxies.Count == 0) return false;
                    bool statistics = context.CollectStatistics;
                    context.Push(0);
                    while (context.stackCount != 0)
                    {
                        int nodeId = context.Pop();
                        if (statistics) { context.NodesVisited++; context.EntriesTested++; }
                        double near, far;
                        if (!ray.IntersectsUnchecked(queryBounds[nodeId], out near, out far)) continue;
                        OctreeNode node = nodes[nodeId];
                        for (int slot = node.FirstItem; slot >= 0; slot = nextItem[slot])
                        {
                            if (statistics) { context.EntriesTested++; context.PrimitiveTests++; }
                            if (!ray.IntersectsUnchecked(proxies.Bounds[slot], out near, out far)) continue;
                            hit = new RayHit<T>(proxies.Items[slot], near);
                            context.ResultCount = 1;
                            return true;
                        }
                        int offset = nodeId * 8;
                        for (int i = 0; i < 8; i++)
                            if (children[offset + i] >= 0) context.Push(children[offset + i]);
                    }
                    return false;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit, QueryContext context)
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
                    if (proxies.Count == 0) return false;
                    bool statistics = context.CollectStatistics, found = false;
                    if (statistics) context.EntriesTested++;
                    if (!ray.IntersectsUnchecked(queryBounds[0], out near, out far)) return false;
                    double closest = ray.TMax;
                    context.Push(0, new DistanceKey(near, 4));
                    while (context.heapCount != 0)
                    {
                        QueryContext.HeapEntry entry = context.PopNearest();
                        if (entry.Distance.value > closest) break;
                        int nodeId = entry.NodeId;
                        OctreeNode node = nodes[nodeId];
                        if (statistics) context.NodesVisited++;
                        for (int slot = node.FirstItem; slot >= 0; slot = nextItem[slot])
                        {
                            if (statistics) { context.EntriesTested++; context.PrimitiveTests++; }
                            if (!ray.IntersectsUnchecked(proxies.Bounds[slot], out near, out far) || near > closest) continue;
                            found = true;
                            closest = near;
                            hit = new RayHit<T>(proxies.Items[slot], near);
                        }
                        int offset = nodeId * 8;
                        for (int i = 0; i < 8; i++)
                        {
                            int child = children[offset + i];
                            if (child < 0) continue;
                            if (statistics) context.EntriesTested++;
                            if (ray.IntersectsUnchecked(queryBounds[child], out near, out far) && near <= closest)
                                context.Push(child, new DistanceKey(near, 4));
                        }
                    }
                    context.ResultCount = found ? 1 : 0;
                    return found;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        public int RaycastAll(Ray3 ray, List<RayHit<T>> results, QueryContext context)
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
                    if (proxies.Count == 0) return 0;
                    bool statistics = context.CollectStatistics;
                    context.Push(0);
                    while (context.stackCount != 0)
                    {
                        int nodeId = context.Pop();
                        if (statistics) { context.NodesVisited++; context.EntriesTested++; }
                        double near, far;
                        if (!ray.IntersectsUnchecked(queryBounds[nodeId], out near, out far)) continue;
                        OctreeNode node = nodes[nodeId];
                        for (int slot = node.FirstItem; slot >= 0; slot = nextItem[slot])
                        {
                            if (statistics) { context.EntriesTested++; context.PrimitiveTests++; }
                            if (ray.IntersectsUnchecked(proxies.Bounds[slot], out near, out far)) results.Add(new RayHit<T>(proxies.Items[slot], near));
                        }
                        int offset = nodeId * 8;
                        for (int i = 0; i < 8; i++)
                            if (children[offset + i] >= 0) context.Push(children[offset + i]);
                    }
                    SortHits(results);
                    context.ResultCount = results.Count;
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        // In-place heapsort has bounded stack use and needs no comparer/delegate
        // allocation, including when called from allocation-sensitive ray loops.
        private static void SortHits(List<RayHit<T>> hits)
        {
            for (int start = hits.Count / 2 - 1; start >= 0; start--) SiftHits(hits, start, hits.Count);
            for (int end = hits.Count - 1; end > 0; end--)
            {
                RayHit<T> first = hits[0];
                hits[0] = hits[end];
                hits[end] = first;
                SiftHits(hits, 0, end);
            }
        }

        private static void SiftHits(List<RayHit<T>> hits, int root, int count)
        {
            RayHit<T> value = hits[root];
            while (root < count / 2)
            {
                int child = root * 2 + 1;
                if (child + 1 < count && hits[child].Parameter < hits[child + 1].Parameter) child++;
                if (value.Parameter >= hits[child].Parameter) break;
                hits[root] = hits[child];
                root = child;
            }
            hits[root] = value;
        }
    }
}
