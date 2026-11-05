using System;
using System.Collections.Generic;
using System.Threading;

namespace RTree
{
    /// <summary>
    /// Handle-based RTree adapter using Bounds3, with the same mutable/query
    /// contracts as DynamicAabbTree and LooseOctree. Duplicate payloads are allowed.
    /// The original RTree API remains available independently.
    /// </summary>
    public sealed class RTreeIndex<T> : IMutableBoundsIndex3<T>, IRayQuery3<T>
    {
        private sealed class Workspace
        {
            internal readonly List<ProxyHandle> Items = new List<ProxyHandle>();
            internal readonly List<RayHit<ProxyHandle>> Hits = new List<RayHit<ProxyHandle>>();
            internal readonly QueryContext Fast = new QueryContext();
            internal readonly QueryContext Statistics = new QueryContext(true);
        }

        private readonly ReaderWriterLockSlim sync = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private readonly ProxyStore<T> proxies = new ProxyStore<T>();
        private readonly ThreadLocal<Workspace> workspaces = new ThreadLocal<Workspace>(delegate { return new Workspace(); });
        private readonly RTree<ProxyHandle> tree;

        public RTreeIndex() : this(10, 5) { }

        public RTreeIndex(int maxNodeEntries, int minNodeEntries)
        {
            ValidateCapacity(maxNodeEntries, minNodeEntries);
            tree = new RTree<ProxyHandle>(maxNodeEntries, minNodeEntries);
        }

        /// <summary>Builds STR storage and returns live handles in input order.</summary>
        public static RTreeIndex<T> BulkLoad(IEnumerable<KeyValuePair<Bounds3, T>> items, out ProxyHandle[] handles)
        { return new RTreeIndex<T>(items, 10, 5, out handles); }

        public static RTreeIndex<T> BulkLoad(IEnumerable<KeyValuePair<Bounds3, T>> items,
            int maxNodeEntries, int minNodeEntries, out ProxyHandle[] handles)
        { return new RTreeIndex<T>(items, maxNodeEntries, minNodeEntries, out handles); }

        private RTreeIndex(IEnumerable<KeyValuePair<Bounds3, T>> items, int maxNodeEntries, int minNodeEntries, out ProxyHandle[] handles)
        {
            if (items == null) throw new ArgumentNullException("items");
            ValidateCapacity(maxNodeEntries, minNodeEntries);
            var sourceHandles = new List<ProxyHandle>();
            tree = RTree<ProxyHandle>.BulkLoad(Pack(items, sourceHandles), maxNodeEntries, minNodeEntries);
            handles = sourceHandles.ToArray();
        }

        private static void ValidateCapacity(int maximum, int minimum)
        {
            if (maximum < 2) throw new ArgumentOutOfRangeException("maxNodeEntries");
            if (minimum < 1 || minimum > maximum / 2) throw new ArgumentOutOfRangeException("minNodeEntries");
        }

        private IEnumerable<KeyValuePair<Rectangle, ProxyHandle>> Pack(IEnumerable<KeyValuePair<Bounds3, T>> items, List<ProxyHandle> handles)
        {
            foreach (KeyValuePair<Bounds3, T> item in items)
            {
                int slot = proxies.Allocate(item.Key, item.Value);
                handles.Add(proxies.Handle(slot));
                yield return new KeyValuePair<Rectangle, ProxyHandle>(item.Key.ToRectangle(), proxies.Handle(slot));
            }
        }

        public int Count
        {
            get { sync.EnterReadLock(); try { return proxies.Count; } finally { sync.ExitReadLock(); } }
        }

        public ProxyHandle Insert(Bounds3 bounds, T item)
        {
            if ((object)item == null) throw new ArgumentNullException("item");
            sync.EnterWriteLock();
            try
            {
                int slot = proxies.Allocate(bounds, item);
                ProxyHandle handle = proxies.Handle(slot);
                try { tree.AddValue(bounds.Box, handle); }
                catch { proxies.Release(slot); throw; }
                return handle;
            }
            finally { sync.ExitWriteLock(); }
        }

        /// <summary>True if different bounds were removed/reinserted; invalid handles throw.</summary>
        public bool Move(ProxyHandle handle, Bounds3 bounds)
        {
            sync.EnterWriteLock();
            try
            {
                int slot = Resolve(handle);
                Bounds3 previous = proxies.Bounds[slot];
                if (previous.Equals(bounds)) return false;
                if (!tree.DeleteValue(previous.Box, handle)) throw new InvalidOperationException("The proxy is missing from its tree.");
                tree.AddValue(bounds.Box, handle);
                proxies.Bounds[slot] = bounds;
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
                if (!tree.DeleteValue(proxies.Bounds[slot].Box, handle)) throw new InvalidOperationException("The proxy is missing from its tree.");
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
                bounds = proxies.Bounds[slot]; return true;
            }
            finally { sync.ExitReadLock(); }
        }

        private int Resolve(ProxyHandle handle)
        {
            int slot;
            if (!proxies.TryResolve(handle, out slot)) throw new ArgumentException("The proxy handle is not live in this index.", "handle");
            return slot;
        }

        /// <summary>Repacks tree nodes without invalidating live proxy handles.</summary>
        public void Rebuild()
        { sync.EnterWriteLock(); try { tree.Rebuild(); } finally { sync.ExitWriteLock(); } }

        public int Query(Bounds3 bounds, List<T> results)
        { return Query(bounds, results, SpatialQueryWorkspaces.Get()); }
        public int Query(Bounds3 bounds, List<T> results, QueryContext context)
        { return QueryVolume(bounds, default(Sphere3), null, 0, results, context); }
        public int QuerySphere(Sphere3 sphere, List<T> results)
        { return QuerySphere(sphere, results, SpatialQueryWorkspaces.Get()); }
        public int QuerySphere(Sphere3 sphere, List<T> results, QueryContext context)
        { return QueryVolume(default(Bounds3), sphere, null, 1, results, context); }
        public int QueryFrustum(Frustum3 frustum, List<T> results)
        { return QueryFrustum(frustum, results, SpatialQueryWorkspaces.Get()); }
        public int QueryFrustum(Frustum3 frustum, List<T> results, QueryContext context)
        {
            if (frustum == null) throw new ArgumentNullException("frustum");
            return QueryVolume(default(Bounds3), default(Sphere3), frustum, 2, results, context);
        }

        private int QueryVolume(Bounds3 bounds, Sphere3 sphere, Frustum3 frustum, int kind, List<T> results, QueryContext context)
        {
            SpatialQueryWorkspaces.ValidateResults(results, context);
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    Workspace workspace = workspaces.Value;
                    QueryContext inner = context.CollectStatistics ? workspace.Statistics : workspace.Fast;
                    if (kind == 0) tree.Query(bounds, workspace.Items, inner);
                    else if (kind == 1) tree.QuerySphere(sphere, workspace.Items, inner);
                    else tree.QueryFrustum(frustum, workspace.Items, inner);
                    results.Clear();
                    foreach (ProxyHandle handle in workspace.Items) results.Add(proxies.Items[handle.Slot]);
                    CopyStatistics(inner, context);
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        public bool RaycastAny(Ray3 ray, out RayHit<T> hit)
        { return RaycastAny(ray, out hit, SpatialQueryWorkspaces.Get()); }
        public bool RaycastAny(Ray3 ray, out RayHit<T> hit, QueryContext context)
        { return RaycastOne(ray, false, out hit, context); }
        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit)
        { return RaycastClosest(ray, out hit, SpatialQueryWorkspaces.Get()); }
        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit, QueryContext context)
        { return RaycastOne(ray, true, out hit, context); }

        private bool RaycastOne(Ray3 ray, bool closest, out RayHit<T> hit, QueryContext context)
        {
            ray.Validate();
            if (context == null) throw new ArgumentNullException("context");
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    Workspace workspace = workspaces.Value;
                    QueryContext inner = context.CollectStatistics ? workspace.Statistics : workspace.Fast;
                    RayHit<ProxyHandle> candidate;
                    bool found = closest ? tree.RaycastClosest(ray, out candidate, inner) : tree.RaycastAny(ray, out candidate, inner);
                    hit = found ? new RayHit<T>(proxies.Items[candidate.Item.Slot], candidate.Parameter) : default(RayHit<T>);
                    CopyStatistics(inner, context);
                    return found;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        public int RaycastAll(Ray3 ray, List<RayHit<T>> results)
        { return RaycastAll(ray, results, SpatialQueryWorkspaces.Get()); }
        public int RaycastAll(Ray3 ray, List<RayHit<T>> results, QueryContext context)
        {
            ray.Validate(); SpatialQueryWorkspaces.ValidateResults(results, context);
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    Workspace workspace = workspaces.Value;
                    QueryContext inner = context.CollectStatistics ? workspace.Statistics : workspace.Fast;
                    tree.RaycastAll(ray, workspace.Hits, inner);
                    results.Clear();
                    foreach (RayHit<ProxyHandle> candidate in workspace.Hits)
                        results.Add(new RayHit<T>(proxies.Items[candidate.Item.Slot], candidate.Parameter));
                    CopyStatistics(inner, context);
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }

        private static void CopyStatistics(QueryContext source, QueryContext target)
        {
            target.NodesVisited = source.NodesVisited;
            target.EntriesTested = source.EntriesTested;
            target.PrimitiveTests = source.PrimitiveTests;
            target.ResultCount = source.ResultCount;
        }
    }
}
