//   RTree.java
//   Java Spatial Index Library
//   Copyright (C) 2002 Infomatiq Limited
//   Copyright (C) 2008 Aled Morris aled@sourceforge.net
//  
//  This library is free software; you can redistribute it and/or
//  modify it under the terms of the GNU Lesser General Public
//  License as published by the Free Software Foundation; either
//  version 2.1 of the License, or (at your option) any later version.
//  
//  This library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
//  Lesser General Public License for more details.
//  
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

//  Ported to C# By Dror Gluska, April 9th, 2009
//  Updated by Boris Kudryashov, January 29, 2014
using System;
using System.Collections.Generic;
using System.Threading;

namespace RTree
{
    // Metadata and entries contain inline value boxes, never Rectangle objects.
    internal struct TreeNode
    {
        internal Aabb Bounds;
        internal int Count, Level;
        internal bool Live;
    }

    internal struct TreeEntry
    {
        internal Aabb Bounds;
        internal int Id;
        internal TreeEntry(Aabb bounds, int id) { Bounds = bounds; Id = id; }
    }

    /// <summary>
    /// A three-dimensional in-memory R-tree. Readers may query one instance
    /// concurrently; writers are serialized and exclude readers. Item equality
    /// and hash codes must remain stable while the item is indexed.
    /// </summary>
    public partial class RTree<T>
    {
        private const string version = "1.0b2p1";
        private const int DefaultCapacity = 10;
        internal int maxNodeEntries, minNodeEntries;
        private TreeNode[] nodes;
        private TreeEntry[] entries;
        private T[] itemValues;
        private bool[] itemLive;
        private Dictionary<T, int> itemIds = new Dictionary<T, int>();
        private int nextNodeId, nextItemId, rootNodeId, treeHeight = 1, msize;
        private Stack<int> freeNodeIds = new Stack<int>();
        private Stack<int> freeItemIds = new Stack<int>();
        private readonly ReaderWriterLockSlim sync = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

        // One writer owns these reusable split buffers. No per-entry geometry
        // objects or per-split comparison delegates are allocated.
        private TreeEntry[] splitEntries;
        private int[] splitOrder, bestOrder;
        private Aabb[] prefixBounds, suffixBounds;
        private SplitComparer splitComparer;
        // A single writer owns deletion scratch just as it owns split scratch.
        // Capacity is retained across calls; entries contain no item references.
        private readonly List<int> deletePath = new List<int>(8);
        private readonly List<int> deletePathEntries = new List<int>(8);
        private readonly List<OrphanEntry> deleteOrphans = new List<OrphanEntry>(16);

        public RTree() : this(DefaultCapacity, DefaultCapacity / 2) { }

        public RTree(int MaxNodeEntries, int MinNodeEntries)
        {
            maxNodeEntries = MaxNodeEntries < 2 ? DefaultCapacity : MaxNodeEntries;
            minNodeEntries = MinNodeEntries < 1 || MinNodeEntries > maxNodeEntries / 2
                ? maxNodeEntries / 2 : MinNodeEntries;
            int initial = Math.Min(16, int.MaxValue / maxNodeEntries);
            if (initial < 1 || maxNodeEntries == int.MaxValue)
                throw new ArgumentOutOfRangeException("MaxNodeEntries");
            nodes = new TreeNode[initial];
            entries = new TreeEntry[checked(initial * maxNodeEntries)];
            itemValues = new T[16];
            itemLive = new bool[16];
            splitEntries = new TreeEntry[checked(maxNodeEntries + 1)];
            splitOrder = new int[splitEntries.Length];
            bestOrder = new int[splitEntries.Length];
            prefixBounds = new Aabb[splitEntries.Length];
            suffixBounds = new Aabb[splitEntries.Length];
            splitComparer = new SplitComparer(splitEntries);
            rootNodeId = AllocateNode(1);
        }

        /// <summary>Adds a unique non-null item and copies its geometric bounds.</summary>
        public void Add(Rectangle r, T item)
        {
            if (r == null) throw new ArgumentNullException("r");
            if (ReferenceEquals(item, null)) throw new ArgumentNullException("item");
            Aabb bounds = Aabb.FromRectangle(r);
            AddValue(bounds, item);
        }

        internal void AddValue(Aabb bounds, T item)
        {
            if (ReferenceEquals(item, null)) throw new ArgumentNullException("item");
            sync.EnterWriteLock();
            try
            {
                if (itemIds.ContainsKey(item)) throw new ArgumentException("The item is already indexed.", "item");
                if (msize == int.MaxValue) throw new InvalidOperationException("The index is full.");
                int id = AllocateItem();
                try { itemIds.Add(item, id); }
                catch { ReleaseItem(id); throw; }
                itemValues[id] = item;
                itemLive[id] = true;
                try { InsertRoot(new TreeEntry(bounds, id), 1); }
                catch
                {
                    itemIds.Remove(item);
                    ReleaseItem(id);
                    throw;
                }
                msize++;
            }
            finally { sync.ExitWriteLock(); }
        }

        /// <summary>Deletes a matching item and exact box, or returns false.</summary>
        public bool Delete(Rectangle r, T item)
        {
            if (r == null) throw new ArgumentNullException("r");
            if (ReferenceEquals(item, null)) throw new ArgumentNullException("item");
            Aabb bounds = Aabb.FromRectangle(r);
            return DeleteValue(bounds, item);
        }

        internal bool DeleteValue(Aabb bounds, T item)
        {
            if (ReferenceEquals(item, null)) throw new ArgumentNullException("item");
            sync.EnterWriteLock();
            try
            {
                int itemId;
                if (!itemIds.TryGetValue(item, out itemId)) return false;
                var path = deletePath;
                var pathEntries = deletePathEntries;
                path.Clear(); pathEntries.Clear(); deleteOrphans.Clear();
                int leaf, index;
                if (!FindLeaf(rootNodeId, bounds, itemId, path, pathEntries, out leaf, out index)) return false;

                // Invoke user equality before any structural changes.
                itemIds.Remove(item);
                RemoveEntry(leaf, index);
                var orphanEntries = deleteOrphans;
                for (int depth = path.Count - 1; depth >= 0; depth--)
                {
                    int parent = path[depth], parentEntry = pathEntries[depth];
                    if (nodes[leaf].Count < minNodeEntries)
                    {
                        int offset = leaf * maxNodeEntries;
                        for (int i = 0; i < nodes[leaf].Count; i++)
                            orphanEntries.Add(new OrphanEntry(entries[offset + i], nodes[leaf].Level));
                        RemoveEntry(parent, parentEntry);
                        ReleaseNode(leaf);
                    }
                    else
                    {
                        int slot = parent * maxNodeEntries + parentEntry;
                        entries[slot].Bounds = nodes[leaf].Bounds;
                        Recalculate(parent);
                    }
                    leaf = parent;
                }

                // Upper-level orphan entries must be restored before lower-level
                // ones, so all dependent leaves retain the same depth.
                orphanEntries.Sort(OrphanComparer.Instance);
                for (int i = 0; i < orphanEntries.Count; i++)
                    InsertRoot(orphanEntries[i].Entry, orphanEntries[i].Level);
                CollapseRoot();
                ReleaseItem(itemId);
                msize--;
                return true;
            }
            finally
            {
                deletePath.Clear(); deletePathEntries.Clear(); deleteOrphans.Clear();
                sync.ExitWriteLock();
            }
        }

        public int Count
        {
            get { sync.EnterReadLock(); try { return msize; } finally { sync.ExitReadLock(); } }
        }

        public Rectangle getBounds()
        {
            sync.EnterReadLock();
            try { return nodes[rootNodeId].Count == 0 ? null : nodes[rootNodeId].Bounds.ToRectangle(); }
            finally { sync.ExitReadLock(); }
        }

        public int getRootNodeId()
        {
            sync.EnterReadLock();
            try { return rootNodeId; }
            finally { sync.ExitReadLock(); }
        }

        public string getVersion() { return "RTree-" + version; }

        /// <summary>Builds a packed, balanced tree using three-dimensional STR ordering.</summary>
        public static RTree<T> BulkLoad(IEnumerable<KeyValuePair<Rectangle, T>> items)
        {
            return BulkLoad(items, DefaultCapacity, DefaultCapacity / 2);
        }

        public static RTree<T> BulkLoad(IEnumerable<KeyValuePair<Rectangle, T>> items, int maxNodeEntries, int minNodeEntries)
        {
            if (items == null) throw new ArgumentNullException("items");
            var tree = new RTree<T>(maxNodeEntries, minNodeEntries);
            tree.sync.EnterWriteLock();
            try
            {
                var records = new List<TreeEntry>();
                foreach (var pair in items)
                {
                    if (pair.Key == null) throw new ArgumentNullException("items", "An item has null bounds.");
                    if (ReferenceEquals(pair.Value, null)) throw new ArgumentNullException("items", "An item is null.");
                    Aabb box = Aabb.FromRectangle(pair.Key);
                    int id = tree.AllocateItem();
                    tree.itemIds.Add(pair.Value, id);
                    tree.itemValues[id] = pair.Value;
                    tree.itemLive[id] = true;
                    records.Add(new TreeEntry(box, id));
                }
                tree.msize = records.Count;
                tree.Pack(records.ToArray());
                return tree;
            }
            finally { tree.sync.ExitWriteLock(); }
        }

        /// <summary>Repacks existing items with STR ordering without changing their identities.</summary>
        public void Rebuild()
        {
            sync.EnterWriteLock();
            try
            {
                var records = new TreeEntry[msize];
                int position = 0;
                for (int id = 0; id < nextNodeId; id++)
                {
                    if (!nodes[id].Live || nodes[id].Level != 1) continue;
                    Array.Copy(entries, id * maxNodeEntries, records, position, nodes[id].Count);
                    position += nodes[id].Count;
                }
                var packed = new RTree<T>(maxNodeEntries, minNodeEntries);
                packed.Pack(records);
                // Publish only after a complete build, while holding the write lock.
                nodes = packed.nodes; entries = packed.entries;
                rootNodeId = packed.rootNodeId; treeHeight = packed.treeHeight;
                nextNodeId = packed.nextNodeId; freeNodeIds = packed.freeNodeIds;
            }
            finally { sync.ExitWriteLock(); }
        }

        private int AllocateItem()
        {
            if (freeItemIds.Count != 0) return freeItemIds.Pop();
            if (nextItemId == itemValues.Length)
            {
                int capacity = Grow(itemValues.Length, checked(nextItemId + 1), int.MaxValue);
                var values = new T[capacity]; var live = new bool[capacity];
                Array.Copy(itemValues, values, itemValues.Length);
                Array.Copy(itemLive, live, itemLive.Length);
                itemValues = values; itemLive = live;
            }
            return nextItemId++;
        }

        private void ReleaseItem(int id)
        {
            itemValues[id] = default(T); itemLive[id] = false; freeItemIds.Push(id);
        }

        private int AllocateNode(int level)
        {
            int id;
            if (freeNodeIds.Count != 0) id = freeNodeIds.Pop();
            else
            {
                EnsureNodeCapacity(checked(nextNodeId + 1));
                id = nextNodeId++;
            }
            nodes[id] = new TreeNode { Level = level, Live = true };
            return id;
        }

        private void EnsureNodeCapacity(int required)
        {
            if (required <= nodes.Length) return;
            int capacity = Grow(nodes.Length, required, int.MaxValue / maxNodeEntries);
            var newNodes = new TreeNode[capacity];
            var newEntries = new TreeEntry[checked(capacity * maxNodeEntries)];
            Array.Copy(nodes, newNodes, nodes.Length);
            Array.Copy(entries, newEntries, entries.Length);
            nodes = newNodes; entries = newEntries;
        }

        private static int Grow(int current, int required, int limit)
        {
            if (required > limit) throw new InvalidOperationException("The index has reached its storage limit.");
            long doubled = (long)current * 2;
            return (int)Math.Min(limit, Math.Max(required, doubled));
        }

        private void ReleaseNode(int id)
        {
            Array.Clear(entries, id * maxNodeEntries, maxNodeEntries);
            nodes[id] = default(TreeNode);
            freeNodeIds.Push(id);
        }

        private void AddEntry(int nodeId, TreeEntry entry)
        {
            int count = nodes[nodeId].Count;
            entries[nodeId * maxNodeEntries + count] = entry;
            nodes[nodeId].Bounds = count == 0 ? entry.Bounds : Aabb.Union(nodes[nodeId].Bounds, entry.Bounds);
            nodes[nodeId].Count = count + 1;
        }

        private void RemoveEntry(int nodeId, int index)
        {
            int last = --nodes[nodeId].Count;
            int offset = nodeId * maxNodeEntries;
            if (index != last) entries[offset + index] = entries[offset + last];
            entries[offset + last] = default(TreeEntry);
            Recalculate(nodeId);
        }

        private void Recalculate(int nodeId)
        {
            int count = nodes[nodeId].Count;
            if (count == 0) { nodes[nodeId].Bounds = default(Aabb); return; }
            int offset = nodeId * maxNodeEntries;
            Aabb bounds = entries[offset].Bounds;
            for (int i = 1; i < count; i++) bounds = Aabb.Union(bounds, entries[offset + i].Bounds);
            nodes[nodeId].Bounds = bounds;
        }

        private void InsertRoot(TreeEntry entry, int level)
        {
            if (nodes[rootNodeId].Count == 0)
            {
                nodes[rootNodeId].Level = level;
                treeHeight = level;
            }
            int sibling = Insert(rootNodeId, entry, level);
            if (sibling < 0) return;
            int oldRoot = rootNodeId;
            rootNodeId = AllocateNode(++treeHeight);
            AddEntry(rootNodeId, new TreeEntry(nodes[oldRoot].Bounds, oldRoot));
            AddEntry(rootNodeId, new TreeEntry(nodes[sibling].Bounds, sibling));
        }

        private int Insert(int nodeId, TreeEntry entry, int level)
        {
            if (nodes[nodeId].Level > level)
            {
                int chosen = ChooseEntry(nodeId, entry.Bounds);
                int child = entries[nodeId * maxNodeEntries + chosen].Id;
                int sibling = Insert(child, entry, level);
                entries[nodeId * maxNodeEntries + chosen].Bounds = nodes[child].Bounds;
                Recalculate(nodeId);
                if (sibling < 0) return -1;
                entry = new TreeEntry(nodes[sibling].Bounds, sibling);
            }
            else if (nodes[nodeId].Level != level)
                throw new InvalidOperationException("An orphan entry has an invalid level.");
            if (nodes[nodeId].Count < maxNodeEntries) { AddEntry(nodeId, entry); return -1; }
            return Split(nodeId, entry);
        }

        private int ChooseEntry(int nodeId, Aabb addition)
        {
            int offset = nodeId * maxNodeEntries, chosen = 0;
            Aabb best = entries[offset].Bounds;
            double bestGrowth = best.Growth(addition);
            for (int i = 1; i < nodes[nodeId].Count; i++)
            {
                Aabb candidate = entries[offset + i].Bounds;
                double growth = candidate.Growth(addition);
                int comparison = growth.CompareTo(bestGrowth);
                if (comparison == 0) comparison = CompareGrowthTies(candidate, best, addition);
                if (comparison == 0)
                    comparison = nodes[entries[offset + i].Id].Count.CompareTo(nodes[entries[offset + chosen].Id].Count);
                if (comparison < 0) { chosen = i; best = candidate; bestGrowth = growth; }
            }
            return chosen;
        }

        private static int CompareGrowthTies(Aabb left, Aabb right, Aabb addition)
        {
            int comparison = left.SurfaceGrowth(addition).CompareTo(right.SurfaceGrowth(addition));
            if (comparison != 0) return comparison;
            comparison = left.MarginGrowth(addition).CompareTo(right.MarginGrowth(addition));
            if (comparison != 0) return comparison;
            comparison = left.Volume().CompareTo(right.Volume());
            if (comparison != 0) return comparison;
            comparison = left.Surface().CompareTo(right.Surface());
            if (comparison != 0) return comparison;
            return left.Margin().CompareTo(right.Margin());
        }

        private int Split(int nodeId, TreeEntry addition)
        {
            int total = maxNodeEntries + 1, offset = nodeId * maxNodeEntries;
            Array.Copy(entries, offset, splitEntries, 0, maxNodeEntries);
            splitEntries[maxNodeEntries] = addition;
            int chosenAxis = 0;
            double minimumMargin = double.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                double sum = 0;
                for (int order = 0; order < 2; order++)
                {
                    PrepareSplit(axis, order != 0, total);
                    for (int count = minNodeEntries; count <= total - minNodeEntries; count++)
                        sum += prefixBounds[count - 1].Margin() + suffixBounds[count].Margin();
                }
                if (sum < minimumMargin) { minimumMargin = sum; chosenAxis = axis; }
            }

            bool found = false;
            int bestCount = minNodeEntries, bestBalance = int.MaxValue;
            double bestOverlap = 0, bestOverlapSurface = 0, bestOverlapMargin = 0;
            double bestVolume = 0, bestSurface = 0, bestMargin = 0;
            for (int order = 0; order < 2; order++)
            {
                PrepareSplit(chosenAxis, order != 0, total);
                for (int count = minNodeEntries; count <= total - minNodeEntries; count++)
                {
                    Aabb left = prefixBounds[count - 1], right = suffixBounds[count];
                    double overlap = left.OverlapVolume(right), overlapSurface = left.OverlapSurface(right), overlapMargin = left.OverlapMargin(right);
                    double volume = left.Volume() + right.Volume(), surface = left.Surface() + right.Surface(), margin = left.Margin() + right.Margin();
                    int balance = Math.Abs(total - 2 * count), comparison = -1;
                    if (found)
                    {
                        comparison = overlap.CompareTo(bestOverlap);
                        if (comparison == 0) comparison = overlapSurface.CompareTo(bestOverlapSurface);
                        if (comparison == 0) comparison = overlapMargin.CompareTo(bestOverlapMargin);
                        if (comparison == 0) comparison = volume.CompareTo(bestVolume);
                        if (comparison == 0) comparison = surface.CompareTo(bestSurface);
                        if (comparison == 0) comparison = margin.CompareTo(bestMargin);
                        if (comparison == 0) comparison = balance.CompareTo(bestBalance);
                    }
                    if (comparison < 0)
                    {
                        found = true; bestCount = count; bestBalance = balance;
                        bestOverlap = overlap; bestOverlapSurface = overlapSurface; bestOverlapMargin = overlapMargin;
                        bestVolume = volume; bestSurface = surface; bestMargin = margin;
                        Array.Copy(splitOrder, bestOrder, total);
                    }
                }
            }
            int sibling = AllocateNode(nodes[nodeId].Level);
            Array.Clear(entries, nodeId * maxNodeEntries, maxNodeEntries);
            nodes[nodeId].Count = 0;
            nodes[nodeId].Bounds = default(Aabb);
            for (int i = 0; i < bestCount; i++) AddEntry(nodeId, splitEntries[bestOrder[i]]);
            for (int i = bestCount; i < total; i++) AddEntry(sibling, splitEntries[bestOrder[i]]);
            return sibling;
        }

        private void PrepareSplit(int axis, bool upper, int total)
        {
            for (int i = 0; i < total; i++) splitOrder[i] = i;
            splitComparer.Axis = axis; splitComparer.Upper = upper;
            Array.Sort(splitOrder, 0, total, splitComparer);
            Aabb bounds = splitEntries[splitOrder[0]].Bounds;
            prefixBounds[0] = bounds;
            for (int i = 1; i < total; i++) { bounds = Aabb.Union(bounds, splitEntries[splitOrder[i]].Bounds); prefixBounds[i] = bounds; }
            bounds = splitEntries[splitOrder[total - 1]].Bounds;
            suffixBounds[total - 1] = bounds;
            for (int i = total - 2; i >= 0; i--) { bounds = Aabb.Union(bounds, splitEntries[splitOrder[i]].Bounds); suffixBounds[i] = bounds; }
        }

        private bool FindLeaf(int nodeId, Aabb bounds, int itemId, List<int> path, List<int> pathEntries, out int leaf, out int index)
        {
            int offset = nodeId * maxNodeEntries;
            for (int i = 0; i < nodes[nodeId].Count; i++)
            {
                TreeEntry entry = entries[offset + i];
                if (nodes[nodeId].Level == 1)
                {
                    if (entry.Id == itemId && entry.Bounds.EqualsBounds(bounds)) { leaf = nodeId; index = i; return true; }
                }
                else if (entry.Bounds.Contains(bounds))
                {
                    path.Add(nodeId); pathEntries.Add(i);
                    if (FindLeaf(entry.Id, bounds, itemId, path, pathEntries, out leaf, out index)) return true;
                    path.RemoveAt(path.Count - 1); pathEntries.RemoveAt(pathEntries.Count - 1);
                }
            }
            leaf = index = -1;
            return false;
        }

        private void CollapseRoot()
        {
            while (nodes[rootNodeId].Level > 1 && nodes[rootNodeId].Count == 1)
            {
                int retired = rootNodeId;
                rootNodeId = entries[retired * maxNodeEntries].Id;
                ReleaseNode(retired);
            }
            if (nodes[rootNodeId].Count == 0) { nodes[rootNodeId].Level = 1; nodes[rootNodeId].Bounds = default(Aabb); }
            treeHeight = nodes[rootNodeId].Level;
        }

        private void Pack(TreeEntry[] records)
        {
            // Called only on an unpublished builder (BulkLoad or Rebuild).
            Array.Clear(nodes, 0, nodes.Length); Array.Clear(entries, 0, entries.Length);
            freeNodeIds.Clear(); nextNodeId = 0;
            if (records.Length == 0) { rootNodeId = AllocateNode(1); treeHeight = 1; return; }
            int needed = 0, count = records.Length;
            do { count = (count - 1) / maxNodeEntries + 1; needed = checked(needed + count); } while (count > 1);
            EnsureNodeCapacity(needed);
            int level = 1;
            while (true)
            {
                int groups = (records.Length - 1) / maxNodeEntries + 1;
                int[] groupSizes = new int[groups];
                int basic = records.Length / groups, extra = records.Length % groups;
                for (int i = 0; i < groups; i++) groupSizes[i] = basic + (i < extra ? 1 : 0);
                OrderStr(records, groupSizes);
                var parents = new TreeEntry[groups];
                int cursor = 0;
                for (int group = 0; group < groups; group++)
                {
                    int nodeId = AllocateNode(level);
                    for (int i = 0; i < groupSizes[group]; i++) AddEntry(nodeId, records[cursor++]);
                    parents[group] = new TreeEntry(nodes[nodeId].Bounds, nodeId);
                }
                if (groups == 1) { rootNodeId = parents[0].Id; treeHeight = level; return; }
                records = parents; level++;
            }
        }

        private static void OrderStr(TreeEntry[] records, int[] groupSizes)
        {
            if (groupSizes.Length <= 1) return;
            // Constant axes carry no spatial ordering information. Excluding
            // them prevents STR from partitioning line/plane data by arbitrary
            // tie-breaking item IDs before reaching the varying coordinates.
            double[] low = new double[3], high = new double[3], spread = new double[3];
            int[] axes = new int[3];
            for (int axis = 0; axis < 3; axis++) low[axis] = high[axis] = records[0].Bounds.Center(axis);
            for (int i = 1; i < records.Length; i++)
                for (int axis = 0; axis < 3; axis++)
                {
                    double center = records[i].Bounds.Center(axis);
                    low[axis] = Math.Min(low[axis], center);
                    high[axis] = Math.Max(high[axis], center);
                }
            int dimensions = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                spread[axis] = high[axis] - low[axis];
                if (spread[axis] == 0) continue;
                int insertion = dimensions;
                while (insertion > 0 && spread[axes[insertion - 1]] < spread[axis])
                {
                    axes[insertion] = axes[insertion - 1];
                    insertion--;
                }
                axes[insertion] = axis;
                dimensions++;
            }
            if (dimensions != 0) OrderStrRange(records, groupSizes, 0, groupSizes.Length, 0, axes, 0, dimensions);
        }

        private static void OrderStrRange(TreeEntry[] records, int[] groupSizes, int firstGroup, int groups,
            int entryStart, int[] axes, int axisIndex, int remainingDimensions)
        {
            if (groups <= 1) return;
            int entryCount = Sum(groupSizes, firstGroup, groups);
            int axis = axes[axisIndex];
            Array.Sort(records, entryStart, entryCount, axis == 0 ? CenterComparer.X : axis == 1 ? CenterComparer.Y : CenterComparer.Z);
            if (remainingDimensions == 1) return;
            int slices = Math.Max(1, (int)Math.Ceiling(Math.Pow(groups, 1.0 / remainingDimensions)));
            int groupStart = firstGroup;
            for (int slice = 0; slice < slices; slice++)
            {
                int sliceGroups = groups / slices + (slice < groups % slices ? 1 : 0);
                OrderStrRange(records, groupSizes, groupStart, sliceGroups, entryStart, axes, axisIndex + 1, remainingDimensions - 1);
                entryStart += Sum(groupSizes, groupStart, sliceGroups);
                groupStart += sliceGroups;
            }
        }

        private static int Sum(int[] values, int start, int count)
        {
            int sum = 0;
            for (int i = 0; i < count; i++) sum += values[start + i];
            return sum;
        }

        private struct OrphanEntry
        {
            internal TreeEntry Entry;
            internal int Level;
            internal OrphanEntry(TreeEntry entry, int level) { Entry = entry; Level = level; }
        }

        private sealed class OrphanComparer : IComparer<OrphanEntry>
        {
            internal static readonly OrphanComparer Instance = new OrphanComparer();
            public int Compare(OrphanEntry left, OrphanEntry right) { return right.Level.CompareTo(left.Level); }
        }

        private sealed class SplitComparer : IComparer<int>
        {
            private readonly TreeEntry[] source;
            internal int Axis;
            internal bool Upper;
            internal SplitComparer(TreeEntry[] source) { this.source = source; }
            public int Compare(int left, int right)
            {
                Aabb a = source[left].Bounds, b = source[right].Bounds;
                int comparison = (Upper ? a.Max(Axis) : a.Min(Axis)).CompareTo(Upper ? b.Max(Axis) : b.Min(Axis));
                if (comparison != 0) return comparison;
                comparison = (Upper ? a.Min(Axis) : a.Max(Axis)).CompareTo(Upper ? b.Min(Axis) : b.Max(Axis));
                return comparison != 0 ? comparison : left.CompareTo(right);
            }
        }

        private sealed class CenterComparer : IComparer<TreeEntry>
        {
            internal static readonly CenterComparer X = new CenterComparer(0), Y = new CenterComparer(1), Z = new CenterComparer(2);
            private readonly int axis;
            private CenterComparer(int axis) { this.axis = axis; }
            public int Compare(TreeEntry left, TreeEntry right)
            {
                int comparison = left.Bounds.Center(axis).CompareTo(right.Bounds.Center(axis));
                if (comparison != 0) return comparison;
                comparison = left.Bounds.Min(axis).CompareTo(right.Bounds.Min(axis));
                return comparison != 0 ? comparison : left.Id.CompareTo(right.Id);
            }
        }
    }
}
