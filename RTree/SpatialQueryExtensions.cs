using System;
using System.Collections.Generic;

namespace RTree
{
    /// <summary>Convenience overloads for interface-typed indexes. Explicit contexts remain available.</summary>
    public static class SpatialQueryExtensions
    {
        public static int Query<T>(this IBoundsQuery3<T> index, Bounds3 bounds, List<T> results)
        { Require(index); return index.Query(bounds, results, SpatialQueryWorkspaces.Get()); }

        public static int QuerySphere<T>(this IBoundsQuery3<T> index, Sphere3 sphere, List<T> results)
        { Require(index); return index.QuerySphere(sphere, results, SpatialQueryWorkspaces.Get()); }

        public static int QueryFrustum<T>(this IBoundsQuery3<T> index, Frustum3 frustum, List<T> results)
        { Require(index); return index.QueryFrustum(frustum, results, SpatialQueryWorkspaces.Get()); }

        public static bool RaycastAny<T>(this IRayQuery3<T> index, Ray3 ray, out RayHit<T> hit)
        { Require(index); return index.RaycastAny(ray, out hit, SpatialQueryWorkspaces.Get()); }

        public static bool RaycastClosest<T>(this IRayQuery3<T> index, Ray3 ray, out RayHit<T> hit)
        { Require(index); return index.RaycastClosest(ray, out hit, SpatialQueryWorkspaces.Get()); }

        public static int RaycastAll<T>(this IRayQuery3<T> index, Ray3 ray, List<RayHit<T>> results)
        { Require(index); return index.RaycastAll(ray, results, SpatialQueryWorkspaces.Get()); }

        public static int Radius<T>(this IPointQuery3<T> index, Vector3d point, double maximumDistance, List<Neighbor<T>> results)
        { Require(index); return index.Radius(point, maximumDistance, results, SpatialQueryWorkspaces.Get()); }

        public static int KNearest<T>(this IPointQuery3<T> index, Vector3d point, int k, double maximumDistance, List<Neighbor<T>> results)
        { Require(index); return index.KNearest(point, k, maximumDistance, results, SpatialQueryWorkspaces.Get()); }

        public static int KNearest<T>(this IPointQuery3<T> index, Vector3d point, int k, List<Neighbor<T>> results)
        { Require(index); return index.KNearest(point, k, double.PositiveInfinity, results, SpatialQueryWorkspaces.Get()); }

        public static int KNearest<T>(this IPointQuery3<T> index, Vector3d point, int k, List<Neighbor<T>> results, QueryContext context)
        { Require(index); return index.KNearest(point, k, double.PositiveInfinity, results, context); }

        public static int QueryPairs<T>(this IBroadPhase3<T> index, List<ProxyPair<T>> results)
        { Require(index); return index.QueryPairs(results, SpatialQueryWorkspaces.Get()); }

        private static void Require(object index)
        { if (index == null) throw new ArgumentNullException("index"); }
    }
}
