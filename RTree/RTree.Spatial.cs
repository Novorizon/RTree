using System;
using System.Collections.Generic;

namespace RTree
{
    /// <summary>Common 3D bounds/ray adapters, retaining all original RTree APIs.</summary>
    public partial class RTree<T> : IBoundsQuery3<T>, IRayQuery3<T>
    {
        public int Query(Bounds3 bounds, List<T> results, QueryContext context)
        { return QueryRangeValue(bounds.Box, results, context, false); }
        public int QuerySphere(Sphere3 sphere, List<T> results, QueryContext context)
        { return QuerySpatial(default(Bounds3), sphere, null, 1, results, context); }
        public int QueryFrustum(Frustum3 frustum, List<T> results, QueryContext context)
        {
            if (frustum == null) throw new ArgumentNullException("frustum");
            return QuerySpatial(default(Bounds3), default(Sphere3), frustum, 2, results, context);
        }
        public int Query(Bounds3 bounds, List<T> results) { return Query(bounds, results, SpatialQueryWorkspaces.Get()); }
        public int QuerySphere(Sphere3 sphere, List<T> results) { return QuerySphere(sphere, results, SpatialQueryWorkspaces.Get()); }
        public int QueryFrustum(Frustum3 frustum, List<T> results) { return QueryFrustum(frustum, results, SpatialQueryWorkspaces.Get()); }

        private static bool SpatialMatches(Aabb box, Bounds3 bounds, Sphere3 sphere, Frustum3 frustum, int kind)
        {
            if (kind == 0) return box.Intersects(bounds.Box);
            Bounds3 value = Bounds3.FromAabb(box);
            return kind == 1 ? SpatialMath.Intersects(value, sphere) : frustum.Intersects(value);
        }

        private int QuerySpatial(Bounds3 bounds, Sphere3 sphere, Frustum3 frustum, int kind, List<T> results, QueryContext context)
        {
            SpatialQueryWorkspaces.ValidateResults(results, context);
            context.Enter();
            try
            {
                sync.EnterReadLock();
                try
                {
                    results.Clear();
                    if (nodes[rootNodeId].Count == 0) return 0;
                    context.Push(rootNodeId);
                    bool statistics = context.CollectStatistics;
                    while (context.stackCount != 0)
                    {
                        int nodeId = context.Pop();
                        TreeNode node = nodes[nodeId];
                        if (statistics) { context.NodesVisited++; context.EntriesTested++; }
                        if (!SpatialMatches(node.Bounds, bounds, sphere, frustum, kind)) continue;
                        int offset = nodeId * maxNodeEntries;
                        for (int i = 0; i < node.Count; i++)
                        {
                            TreeEntry entry = entries[offset + i];
                            if (node.Level != 1) context.Push(entry.Id);
                            else
                            {
                                if (statistics) { context.EntriesTested++; context.PrimitiveTests++; }
                                if (SpatialMatches(entry.Bounds, bounds, sphere, frustum, kind)) results.Add(itemValues[entry.Id]);
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

        public bool RaycastAny(Ray3 ray, out RayHit<T> hit, QueryContext context)
        { return RaycastOne(ray, false, out hit, context); }
        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit, QueryContext context)
        { return RaycastOne(ray, true, out hit, context); }
        public bool RaycastAny(Ray3 ray, out RayHit<T> hit)
        { return RaycastAny(ray, out hit, SpatialQueryWorkspaces.Get()); }
        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit)
        { return RaycastClosest(ray, out hit, SpatialQueryWorkspaces.Get()); }

        private bool RaycastOne(Ray3 ray, bool closest, out RayHit<T> hit, QueryContext context)
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
                    if (nodes[rootNodeId].Count == 0) return false;
                    double near, far;
                    if (!ray.IntersectsUnchecked(nodes[rootNodeId].Bounds, out near, out far)) return false;
                    context.Push(rootNodeId, new DistanceKey(near, 4));
                    bool found = false, statistics = context.CollectStatistics;
                    double maximum = ray.TMax;
                    while (context.heapCount != 0)
                    {
                        QueryContext.HeapEntry pending = context.PopNearest();
                        if (pending.Distance.value > maximum) break;
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
                            if (!ray.IntersectsUnchecked(entry.Bounds, out near, out far) || near > maximum) continue;
                            if (node.Level != 1) context.Push(entry.Id, new DistanceKey(near, 4));
                            else
                            {
                                if (!found || near < maximum) { maximum = near; hit = new RayHit<T>(itemValues[entry.Id], near); found = true; }
                                if (!closest) { context.ResultCount = 1; return true; }
                            }
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
                    results.Clear();
                    if (nodes[rootNodeId].Count == 0) return 0;
                    context.Push(rootNodeId);
                    bool statistics = context.CollectStatistics;
                    while (context.stackCount != 0)
                    {
                        int id = context.Pop(); TreeNode node = nodes[id]; double near, far;
                        if (statistics) { context.NodesVisited++; context.EntriesTested++; }
                        if (!ray.IntersectsUnchecked(node.Bounds, out near, out far)) continue;
                        int offset = id * maxNodeEntries;
                        for (int i = 0; i < node.Count; i++)
                        {
                            TreeEntry entry = entries[offset + i];
                            if (node.Level != 1) context.Push(entry.Id);
                            else
                            {
                                if (statistics) { context.EntriesTested++; context.PrimitiveTests++; }
                                if (ray.IntersectsUnchecked(entry.Bounds, out near, out far)) results.Add(new RayHit<T>(itemValues[entry.Id], near));
                            }
                        }
                    }
                    SpatialResultSort.RayHits(results);
                    context.ResultCount = results.Count;
                    return results.Count;
                }
                finally { sync.ExitReadLock(); }
            }
            finally { context.Exit(); }
        }
    }

    internal static class SpatialResultSort
    {
        internal static void RayHits<T>(List<RayHit<T>> hits)
        {
            for (int i = hits.Count / 2 - 1; i >= 0; i--) Sift(hits, i, hits.Count);
            for (int end = hits.Count - 1; end > 0; end--)
            {
                RayHit<T> value = hits[0]; hits[0] = hits[end]; hits[end] = value;
                Sift(hits, 0, end);
            }
        }
        private static void Sift<T>(List<RayHit<T>> hits, int root, int count)
        {
            RayHit<T> value = hits[root];
            while (root < count / 2)
            {
                int child = root * 2 + 1;
                if (child + 1 < count && hits[child + 1].Parameter > hits[child].Parameter) child++;
                if (value.Parameter >= hits[child].Parameter) break;
                hits[root] = hits[child]; root = child;
            }
            hits[root] = value;
        }
    }
}
