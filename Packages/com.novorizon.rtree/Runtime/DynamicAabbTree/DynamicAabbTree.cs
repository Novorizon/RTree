using System;
using System.Collections.Generic;
using System.Threading;

namespace RTree
{
    /// <summary>
    /// Dynamic binary bounding-volume hierarchy for moving axis-aligned boxes.
    /// Leaves use expanded bounds to avoid reinsertion for small movements;
    /// queries and reported pairs always test the current, unexpanded bounds.
    /// </summary>
    public sealed class DynamicAabbTree<T> : IBroadPhase3<T>, IRayQuery3<T>
    {
        // A little height slack permits spatial rotations that strict AVL
        // balance forbids, while still bounding adversarial insertion depth.
        private const int MaximumImbalance = 4;
        private struct Node
        {
            internal int Parent, Left, Right, Height, Slot, NextFree;
        }

        private readonly ReaderWriterLockSlim sync = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private readonly ProxyStore<T> proxies = new ProxyStore<T>();
        private readonly double fatMargin;
        private readonly double displacementMultiplier;
        // Traversal reads bounds first; rejected nodes need no maintenance data.
        private Node[] nodes = new Node[16];
        private Bounds3[] nodeBounds = new Bounds3[16];
        private int[] nodeBySlot = new int[16];
        private int nextNode;
        private int freeNode = -1;
        private int root = -1;

        // A pair remains a candidate until a fat bound is replaced or a proxy
        // is removed. Actual boxes may begin touching inside unchanged fat
        // bounds, so dropping non-touching candidates would lose future pairs.
        private readonly HashSet<long> candidatePairs = new HashSet<long>();
        private volatile bool pairsEnabled;
        private HashSet<int>[] neighbors = new HashSet<int>[16];
        private int[] writerStack = new int[32];
        private int writerStackCount;

        public DynamicAabbTree() : this(0.1, 2) { }

        public DynamicAabbTree(double fatMargin, double displacementMultiplier)
        {
            if (double.IsNaN(fatMargin) || double.IsInfinity(fatMargin) || fatMargin < 0)
                throw new ArgumentOutOfRangeException("fatMargin");
            if (double.IsNaN(displacementMultiplier) || double.IsInfinity(displacementMultiplier) || displacementMultiplier < 0)
                throw new ArgumentOutOfRangeException("displacementMultiplier");
            this.fatMargin = fatMargin;
            this.displacementMultiplier = displacementMultiplier;
            for (int i = 0; i < nodeBySlot.Length; i++) nodeBySlot[i] = -1;
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

        /// <summary>
        /// Number of fat-bound candidates before actual box filtering. Reading
        /// this property enables the same persistent cache as QueryPairs.
        /// </summary>
        public int CachedPairCount
        {
            get
            {
                EnsurePairCache();
                sync.EnterReadLock();
                try { return candidatePairs.Count; }
                finally { sync.ExitReadLock(); }
            }
        }

        public ProxyHandle Insert(Bounds3 bounds, T item)
        {
            if ((object)item == null) throw new ArgumentNullException("item");
            sync.EnterWriteLock();
            try
            {
                int slot = proxies.Allocate(bounds, item);
                EnsureSlotCapacity(slot);
                int leaf = AllocateNode();
                nodeBounds[leaf] = bounds.Expanded(fatMargin, default(Vector3d), displacementMultiplier);
                nodes[leaf].Slot = slot;
                nodeBySlot[slot] = leaf;
                InsertLeaf(leaf);
                if (pairsEnabled) CachePairs(leaf);
                return proxies.Handle(slot);
            }
            finally { sync.ExitWriteLock(); }
        }

        /// <summary>Updates a proxy, returning true only if its fat bound was replaced.</summary>
        public bool Move(ProxyHandle handle, Bounds3 bounds)
        {
            return Move(handle, bounds, default(Vector3d));
        }

        public bool Move(ProxyHandle handle, Bounds3 bounds, Vector3d displacement)
        {
            sync.EnterWriteLock();
            try
            {
                int slot;
                if (!proxies.TryResolve(handle, out slot)) throw new ArgumentException("The proxy handle is not live in this tree.", "handle");
                int leaf = nodeBySlot[slot];
                proxies.Bounds[slot] = bounds;
                if (nodeBounds[leaf].Contains(bounds)) return false;
                if (pairsEnabled) RemovePairs(slot);
                RemoveLeaf(leaf);
                nodeBounds[leaf] = bounds.Expanded(fatMargin, displacement, displacementMultiplier);
                InsertLeaf(leaf);
                if (pairsEnabled) CachePairs(leaf);
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
                if (pairsEnabled) RemovePairs(slot);
                int leaf = nodeBySlot[slot];
                RemoveLeaf(leaf);
                ReleaseNode(leaf);
                nodeBySlot[slot] = -1;
                proxies.Release(slot);
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
                if (!proxies.TryResolve(handle, out slot)) { bounds = default(Bounds3); return false; }
                bounds = proxies.Bounds[slot];
                return true;
            }
            finally { sync.ExitReadLock(); }
        }

        public int Query(Bounds3 bounds, List<T> results)
        {
            return Query(bounds, results, SpatialQueryWorkspaces.Get());
        }

        public int Query(Bounds3 bounds, List<T> results, QueryContext context)
        {
            return QueryVolume(bounds, default(Sphere3), default(Frustum3), 0, results, context);
        }

        public int QuerySphere(Sphere3 sphere, List<T> results)
        {
            return QuerySphere(sphere, results, SpatialQueryWorkspaces.Get());
        }

        public int QuerySphere(Sphere3 sphere, List<T> results, QueryContext context)
        {
            return QueryVolume(default(Bounds3), sphere, default(Frustum3), 1, results, context);
        }

        public int QueryFrustum(Frustum3 frustum, List<T> results)
        {
            return QueryFrustum(frustum, results, SpatialQueryWorkspaces.Get());
        }

        public int QueryFrustum(Frustum3 frustum, List<T> results, QueryContext context)
        {
            if (frustum == null) throw new ArgumentNullException("frustum");
            return QueryVolume(default(Bounds3), default(Sphere3), frustum, 2, results, context);
        }

        private int QueryVolume(Bounds3 bounds, Sphere3 sphere, Frustum3 frustum, int kind, List<T> results, QueryContext context)
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
                    if (root < 0) return 0;
                    bool statistics = context.CollectStatistics;
                    context.Push(root);
                    while (context.stackCount != 0)
                    {
                        int id = context.Pop();
                        bool accepted = id < 0;
                        if (accepted) id = ~id;
                        if (statistics) context.NodesVisited++;
                        if (!accepted)
                        {
                            if (statistics) context.EntriesTested++;
                            if (!IntersectsVolume(nodeBounds[id], bounds, sphere, frustum, kind)) continue;
                            accepted = kind == 0 && bounds.Contains(nodeBounds[id]);
                        }
                        Node node = nodes[id];
                        if (node.Left < 0)
                        {
                            if (statistics && !accepted) { context.EntriesTested++; context.PrimitiveTests++; }
                            if (accepted || IntersectsVolume(proxies.Bounds[node.Slot], bounds, sphere, frustum, kind))
                                results.Add(proxies.Items[node.Slot]);
                        }
                        else { context.Push(accepted ? ~node.Left : node.Left); context.Push(accepted ? ~node.Right : node.Right); }
                    }
                    context.ResultCount = results.Count;
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        private static bool IntersectsVolume(Bounds3 candidate, Bounds3 bounds, Sphere3 sphere, Frustum3 frustum, int kind)
        {
            if (kind == 0) return candidate.Intersects(bounds);
            if (kind == 1) return SpatialMath.Intersects(candidate, sphere);
            return frustum.Intersects(candidate);
        }

        public bool RaycastAny(Ray3 ray, out RayHit<T> hit)
        {
            return RaycastAny(ray, out hit, SpatialQueryWorkspaces.Get());
        }

        public bool RaycastAny(Ray3 ray, out RayHit<T> hit, QueryContext context)
        {
            ray.Validate();
            if (context == null) throw new ArgumentNullException("context");
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    hit = default(RayHit<T>);
                    if (root < 0) return false;
                    bool statistics = context.CollectStatistics;
                    context.Push(root);
                    while (context.stackCount != 0)
                    {
                        int id = context.Pop();
                        if (statistics) { context.NodesVisited++; context.EntriesTested++; }
                        double near, far;
                        if (!ray.IntersectsUnchecked(nodeBounds[id], out near, out far)) continue;
                        Node node = nodes[id];
                        if (node.Left < 0)
                        {
                            if (statistics) { context.EntriesTested++; context.PrimitiveTests++; }
                            if (!ray.IntersectsUnchecked(proxies.Bounds[node.Slot], out near, out far)) continue;
                            hit = new RayHit<T>(proxies.Items[node.Slot], near);
                            context.ResultCount = 1;
                            return true;
                        }
                        context.Push(node.Left); context.Push(node.Right);
                    }
                    return false;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit)
        {
            return RaycastClosest(ray, out hit, SpatialQueryWorkspaces.Get());
        }

        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit, QueryContext context)
        {
            ray.Validate();
            if (context == null) throw new ArgumentNullException("context");
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    hit = default(RayHit<T>);
                    if (root < 0) return false;
                    bool statistics = context.CollectStatistics;
                    double near, far;
                    if (statistics) context.EntriesTested++;
                    if (!ray.IntersectsUnchecked(nodeBounds[root], out near, out far)) return false;
                    context.Push(root, new DistanceKey(near, 4));
                    bool found = false;
                    double closest = ray.TMax;
                    while (context.heapCount != 0)
                    {
                        QueryContext.HeapEntry pending = context.PopNearest();
                        if (pending.Distance.value > closest) break;
                        Node node = nodes[pending.NodeId];
                        if (statistics) context.NodesVisited++;
                        if (node.Left < 0)
                        {
                            if (statistics) { context.EntriesTested++; context.PrimitiveTests++; }
                            if (ray.IntersectsUnchecked(proxies.Bounds[node.Slot], out near, out far) && near <= closest)
                            {
                                closest = near;
                                hit = new RayHit<T>(proxies.Items[node.Slot], near);
                                found = true;
                            }
                        }
                        else
                        {
                            if (statistics) context.EntriesTested += 2;
                            if (ray.IntersectsUnchecked(nodeBounds[node.Left], out near, out far) && near <= closest)
                                context.Push(node.Left, new DistanceKey(near, 4));
                            if (ray.IntersectsUnchecked(nodeBounds[node.Right], out near, out far) && near <= closest)
                                context.Push(node.Right, new DistanceKey(near, 4));
                        }
                    }
                    context.ResultCount = found ? 1 : 0;
                    return found;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        public int RaycastAll(Ray3 ray, List<RayHit<T>> results)
        {
            return RaycastAll(ray, results, SpatialQueryWorkspaces.Get());
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
                    if (root < 0) return 0;
                    bool statistics = context.CollectStatistics;
                    context.Push(root);
                    while (context.stackCount != 0)
                    {
                        int id = context.Pop();
                        if (statistics) { context.NodesVisited++; context.EntriesTested++; }
                        double near, far;
                        if (!ray.IntersectsUnchecked(nodeBounds[id], out near, out far)) continue;
                        Node node = nodes[id];
                        if (node.Left < 0)
                        {
                            if (statistics) { context.EntriesTested++; context.PrimitiveTests++; }
                            if (ray.IntersectsUnchecked(proxies.Bounds[node.Slot], out near, out far))
                                results.Add(new RayHit<T>(proxies.Items[node.Slot], near));
                        }
                        else { context.Push(node.Left); context.Push(node.Right); }
                    }
                    SortHits(results);
                    context.ResultCount = results.Count;
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        public int QueryPairs(List<ProxyPair<T>> results)
        {
            return QueryPairs(results, SpatialQueryWorkspaces.Get());
        }

        /// <summary>
        /// Clears and fills results with unordered pairs of intersecting actual
        /// boxes. The first call builds the persistent fat-bound candidate
        /// cache; subsequent insertions, removals and reinserts maintain it.
        /// </summary>
        public int QueryPairs(List<ProxyPair<T>> results, QueryContext context)
        {
            if (results == null) throw new ArgumentNullException("results");
            if (context == null) throw new ArgumentNullException("context");
            context.Enter();
            try
            {
                EnsurePairCache();
                sync.EnterReadLock();
                try
                {
                    results.Clear();
                    bool statistics = context.CollectStatistics;
                    foreach (long key in candidatePairs)
                    {
                        int left = (int)(key >> 32), right = (int)(uint)key;
                        if (statistics) { context.EntriesTested++; context.PrimitiveTests++; }
                        if (proxies.Bounds[left].Intersects(proxies.Bounds[right]))
                            results.Add(new ProxyPair<T>(proxies.Handle(left), proxies.Handle(right), proxies.Items[left], proxies.Items[right]));
                    }
                    context.ResultCount = results.Count;
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        private static void SortHits(List<RayHit<T>> hits)
        {
            // In-place heapsort avoids comparer/delegate allocations, including
            // the hidden Comparison<T> adapter used by some List.Sort runtimes.
            int count = hits.Count;
            for (int i = count / 2 - 1; i >= 0; i--) SiftHit(hits, i, count);
            for (int end = count - 1; end > 0; end--)
            {
                RayHit<T> swap = hits[0]; hits[0] = hits[end]; hits[end] = swap;
                SiftHit(hits, 0, end);
            }
        }

        private static void SiftHit(List<RayHit<T>> hits, int index, int count)
        {
            RayHit<T> value = hits[index];
            while (index < count / 2)
            {
                int child = index * 2 + 1;
                if (child + 1 < count && hits[child + 1].Parameter > hits[child].Parameter) child++;
                if (value.Parameter >= hits[child].Parameter) break;
                hits[index] = hits[child];
                index = child;
            }
            hits[index] = value;
        }

        private void EnsureSlotCapacity(int slot)
        {
            if (slot < nodeBySlot.Length) return;
            int previous = nodeBySlot.Length;
            int size = previous;
            while (slot >= size) size = checked(size * 2);
            Array.Resize(ref nodeBySlot, size);
            Array.Resize(ref neighbors, size);
            for (int i = previous; i < size; i++) nodeBySlot[i] = -1;
        }

        private int AllocateNode()
        {
            int id;
            if (freeNode >= 0) { id = freeNode; freeNode = nodes[id].NextFree; }
            else
            {
                if (nextNode == nodes.Length)
                {
                    int capacity = checked(nodes.Length * 2);
                    Array.Resize(ref nodes, capacity);
                    Array.Resize(ref nodeBounds, capacity);
                }
                id = nextNode++;
            }
            nodes[id] = new Node { Parent = -1, Left = -1, Right = -1, Height = 0, Slot = -1, NextFree = -1 };
            return id;
        }

        private void ReleaseNode(int id)
        {
            nodes[id] = new Node { Parent = -1, Left = -1, Right = -1, Height = -1, Slot = -1, NextFree = freeNode };
            freeNode = id;
        }

        private static Bounds3 Union(Bounds3 left, Bounds3 right)
        {
            return Bounds3.FromAabb(Aabb.Union(left.Box, right.Box));
        }

        private void Refit(int id)
        {
            int left = nodes[id].Left, right = nodes[id].Right;
            nodeBounds[id] = Union(nodeBounds[left], nodeBounds[right]);
            nodes[id].Height = 1 + Math.Max(nodes[left].Height, nodes[right].Height);
        }

        private void InsertLeaf(int leaf)
        {
            if (root < 0) { root = leaf; nodes[leaf].Parent = -1; return; }
            Bounds3 bounds = nodeBounds[leaf];
            int sibling = root;
            while (nodes[sibling].Left >= 0)
            {
                int left = nodes[sibling].Left, right = nodes[sibling].Right;
                Aabb box = nodeBounds[sibling].Box;
                double growth = box.SurfaceGrowth(bounds.Box);
                double parentCost = 2 * (box.Surface() + growth);
                double inheritedCost = 2 * growth;
                double leftCost = DescentCost(left, bounds) + inheritedCost;
                double rightCost = DescentCost(right, bounds) + inheritedCost;
                // A large object can be a better sibling of a whole subtree.
                // Forcing descent to a leaf creates broad overlapping branches.
                if (nodes[sibling].Height <= MaximumImbalance && parentCost < leftCost && parentCost < rightCost) break;
                if (leftCost == rightCost) sibling = nodes[left].Height < nodes[right].Height ? left : right;
                else sibling = leftCost < rightCost ? left : right;
            }
            int oldParent = nodes[sibling].Parent;
            int parent = AllocateNode();
            nodes[parent].Parent = oldParent;
            nodes[parent].Left = sibling;
            nodes[parent].Right = leaf;
            nodes[sibling].Parent = parent;
            nodes[leaf].Parent = parent;
            Refit(parent);
            if (oldParent < 0) root = parent;
            else if (nodes[oldParent].Left == sibling) nodes[oldParent].Left = parent;
            else nodes[oldParent].Right = parent;
            RefitAncestors(parent, true);
        }

        private double DescentCost(int node, Bounds3 bounds)
        {
            Aabb box = nodeBounds[node].Box;
            return nodes[node].Left < 0 ? box.Surface() + box.SurfaceGrowth(bounds.Box) : box.SurfaceGrowth(bounds.Box);
        }

        private void RemoveLeaf(int leaf)
        {
            if (leaf == root) { root = -1; nodes[leaf].Parent = -1; return; }
            int parent = nodes[leaf].Parent;
            int grandparent = nodes[parent].Parent;
            int sibling = nodes[parent].Left == leaf ? nodes[parent].Right : nodes[parent].Left;
            if (grandparent < 0) { root = sibling; nodes[sibling].Parent = -1; }
            else
            {
                if (nodes[grandparent].Left == parent) nodes[grandparent].Left = sibling;
                else nodes[grandparent].Right = sibling;
                nodes[sibling].Parent = grandparent;
            }
            ReleaseNode(parent);
            nodes[leaf].Parent = -1;
            if (grandparent >= 0) RefitAncestors(grandparent, false);
        }

        private void RefitAncestors(int node, bool improve)
        {
            while (node >= 0)
            {
                Refit(node);
                int previous = node;
                node = Balance(node);
                if (improve || node != previous) ImproveArea(node);
                node = nodes[node].Parent;
            }
        }

        // Height rotations bound traversal depth even for sorted insertions.
        // The taller grandchild remains outside the demoted node, minimizing
        // the new height; every changed ancestor is then refitted exactly.
        private int Balance(int a)
        {
            if (nodes[a].Left < 0 || nodes[a].Height < 2) return a;
            int b = nodes[a].Left, c = nodes[a].Right;
            int difference = nodes[c].Height - nodes[b].Height;
            if (difference > MaximumImbalance)
            {
                int f = nodes[c].Left, g = nodes[c].Right;
                ReplaceAtParent(a, c);
                nodes[c].Left = a;
                nodes[a].Parent = c;
                if (nodes[f].Height > nodes[g].Height ||
                    (nodes[f].Height == nodes[g].Height &&
                     Union(nodeBounds[b], nodeBounds[g]).Box.Surface() < Union(nodeBounds[b], nodeBounds[f]).Box.Surface()))
                {
                    nodes[c].Right = f; nodes[f].Parent = c;
                    nodes[a].Right = g; nodes[g].Parent = a;
                }
                else
                {
                    nodes[c].Right = g; nodes[g].Parent = c;
                    nodes[a].Right = f; nodes[f].Parent = a;
                }
                Refit(a); Refit(c);
                return c;
            }
            if (difference < -MaximumImbalance)
            {
                int d = nodes[b].Left, e = nodes[b].Right;
                ReplaceAtParent(a, b);
                nodes[b].Left = a;
                nodes[a].Parent = b;
                if (nodes[d].Height > nodes[e].Height ||
                    (nodes[d].Height == nodes[e].Height &&
                     Union(nodeBounds[c], nodeBounds[e]).Box.Surface() < Union(nodeBounds[c], nodeBounds[d]).Box.Surface()))
                {
                    nodes[b].Right = d; nodes[d].Parent = b;
                    nodes[a].Left = e; nodes[e].Parent = a;
                }
                else
                {
                    nodes[b].Right = e; nodes[e].Parent = b;
                    nodes[a].Left = d; nodes[d].Parent = a;
                }
                Refit(a); Refit(b);
                return b;
            }
            return a;
        }

        // Exchange an aunt with a grandchild when that reduces the changed
        // branch's area. The top bound is unchanged. Height constraints retain
        // the bounded imbalance without imposing an ordering on BVH leaves.
        private void ImproveArea(int a)
        {
            int b = nodes[a].Left, c = nodes[a].Right;
            if (b < 0) return;
            int branch = -1, inside = -1;
            double best = 0;
            if (nodes[b].Left >= 0)
            {
                ConsiderRotation(a, b, c, nodes[b].Left, nodes[b].Right, ref branch, ref inside, ref best);
                ConsiderRotation(a, b, c, nodes[b].Right, nodes[b].Left, ref branch, ref inside, ref best);
            }
            if (nodes[c].Left >= 0)
            {
                ConsiderRotation(a, c, b, nodes[c].Left, nodes[c].Right, ref branch, ref inside, ref best);
                ConsiderRotation(a, c, b, nodes[c].Right, nodes[c].Left, ref branch, ref inside, ref best);
            }
            if (branch < 0) return;
            int outside = branch == b ? c : b;
            if (nodes[branch].Left == inside) nodes[branch].Left = outside;
            else nodes[branch].Right = outside;
            if (branch == b) nodes[a].Right = inside;
            else nodes[a].Left = inside;
            nodes[outside].Parent = branch;
            nodes[inside].Parent = a;
            Refit(branch); Refit(a);
        }

        private void ConsiderRotation(int a, int branch, int outside, int inside, int retained,
            ref int bestBranch, ref int bestInside, ref double best)
        {
            int branchHeight = 1 + Math.Max(nodes[outside].Height, nodes[retained].Height);
            if (Math.Abs(nodes[outside].Height - nodes[retained].Height) > MaximumImbalance ||
                Math.Abs(branchHeight - nodes[inside].Height) > MaximumImbalance ||
                1 + Math.Max(branchHeight, nodes[inside].Height) > nodes[a].Height) return;
            Aabb previous = nodeBounds[branch].Box;
            Aabb replacement = Aabb.Union(nodeBounds[outside].Box, nodeBounds[retained].Box);
            double oldArea = previous.Surface(), newArea = replacement.Surface();
            double gain = oldArea - newArea;
            if ((oldArea == 0 && newArea == 0) || double.IsNaN(gain)) gain = previous.Margin() - replacement.Margin();
            if (gain > best) { best = gain; bestBranch = branch; bestInside = inside; }
        }

        private void ReplaceAtParent(int oldChild, int newChild)
        {
            int parent = nodes[oldChild].Parent;
            nodes[newChild].Parent = parent;
            if (parent < 0) root = newChild;
            else if (nodes[parent].Left == oldChild) nodes[parent].Left = newChild;
            else nodes[parent].Right = newChild;
        }

        private static long PairKey(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        private void EnsurePairCache()
        {
            if (pairsEnabled) return;
            sync.EnterWriteLock();
            try
            {
                if (pairsEnabled) return;
                for (int slot = 0; slot < proxies.Next; slot++)
                    if (proxies.Live[slot]) CachePairs(nodeBySlot[slot]);
                pairsEnabled = true;
            }
            finally { sync.ExitWriteLock(); }
        }

        private void AddPair(int a, int b)
        {
            if (!candidatePairs.Add(PairKey(a, b))) return;
            if (neighbors[a] == null) neighbors[a] = new HashSet<int>();
            if (neighbors[b] == null) neighbors[b] = new HashSet<int>();
            neighbors[a].Add(b); neighbors[b].Add(a);
        }

        private void RemovePairs(int slot)
        {
            HashSet<int> adjacent = neighbors[slot];
            if (adjacent == null) return;
            foreach (int other in adjacent)
            {
                candidatePairs.Remove(PairKey(slot, other));
                neighbors[other].Remove(slot);
            }
            adjacent.Clear();
        }

        private void CachePairs(int leaf)
        {
            writerStackCount = 0;
            PushWriter(root);
            Bounds3 bounds = nodeBounds[leaf];
            int slot = nodes[leaf].Slot;
            while (writerStackCount != 0)
            {
                int id = writerStack[--writerStackCount];
                if (id == leaf || !nodeBounds[id].Intersects(bounds)) continue;
                if (nodes[id].Left < 0) AddPair(slot, nodes[id].Slot);
                else { PushWriter(nodes[id].Left); PushWriter(nodes[id].Right); }
            }
        }

        private void PushWriter(int id)
        {
            if (writerStackCount == writerStack.Length) Array.Resize(ref writerStack, checked(writerStack.Length * 2));
            writerStack[writerStackCount++] = id;
        }
    }
}
