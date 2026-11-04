using System;
using System.Collections.Generic;
using System.Threading;

namespace RTree
{
    /// <summary>
    /// Static SAH BVH of triangles. Range queries return triangle-bound
    /// candidates; ray queries perform two-sided triangle intersection tests.
    /// Equal payloads remain distinct source primitives.
    /// </summary>
    public sealed class TriangleBvh<T> : IBoundsQuery3<T>, IRayQuery3<T>
    {
        private sealed class Workspace
        {
            internal readonly List<int> Indices = new List<int>();
            internal readonly List<RayHit<int>> Hits = new List<RayHit<int>>();
        }

        private sealed class Intersector : IRayIntersector<int>
        {
            private readonly TriangleBvh<T> owner;
            internal Intersector(TriangleBvh<T> owner) { this.owner = owner; }
            public bool Intersect(int index, Ray3 ray, out double parameter)
            {
                return owner.triangles[index].Intersects(ray, out parameter);
            }
        }

        private static readonly ThreadLocal<Workspace> workspaces =
            new ThreadLocal<Workspace>(delegate { return new Workspace(); });
        private readonly ReaderWriterLockSlim sync = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private readonly int leafCapacity;
        private readonly Intersector intersector;
        private Triangle3[] triangles = new Triangle3[0];
        private T[] items = new T[0];
        private StaticBvh<int> index;

        public TriangleBvh(IEnumerable<KeyValuePair<Triangle3, T>> items) : this(items, 4) { }

        public TriangleBvh(IEnumerable<KeyValuePair<Triangle3, T>> items, int leafCapacity)
        {
            if (leafCapacity < 1) throw new ArgumentOutOfRangeException("leafCapacity");
            this.leafCapacity = leafCapacity;
            intersector = new Intersector(this);
            Rebuild(items);
        }

        public int Count
        {
            get { sync.EnterReadLock(); try { return items.Length; } finally { sync.ExitReadLock(); } }
        }

        public int LeafCapacity { get { return leafCapacity; } }

        public void Rebuild(IEnumerable<KeyValuePair<Triangle3, T>> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var newTriangles = new List<Triangle3>();
            var newItems = new List<T>();
            var indexedBounds = new List<KeyValuePair<Bounds3, int>>();
            foreach (KeyValuePair<Triangle3, T> pair in source)
            {
                if ((object)pair.Value == null) throw new ArgumentException("Payloads cannot be null.", "source");
                // Triangle3.Bounds and the child constructor validate the full
                // geometry before publication, including non-finite vertices.
                indexedBounds.Add(new KeyValuePair<Bounds3, int>(pair.Key.Bounds, newTriangles.Count));
                newTriangles.Add(pair.Key);
                newItems.Add(pair.Value);
            }
            Triangle3[] builtTriangles = newTriangles.ToArray();
            T[] builtItems = newItems.ToArray();
            var builtIndex = new StaticBvh<int>(indexedBounds, leafCapacity);
            sync.EnterWriteLock();
            try
            {
                triangles = builtTriangles;
                items = builtItems;
                index = builtIndex;
            }
            finally { sync.ExitWriteLock(); }
        }

        /// <summary>Updates triangles in original source order, retaining payloads and BVH topology.</summary>
        public void Refit(IList<Triangle3> trianglesInSourceOrder)
        {
            if (trianglesInSourceOrder == null) throw new ArgumentNullException("trianglesInSourceOrder");
            Triangle3[] replacement = new Triangle3[trianglesInSourceOrder.Count];
            Bounds3[] newBounds = new Bounds3[replacement.Length];
            for (int i = 0; i < replacement.Length; i++)
            {
                replacement[i] = trianglesInSourceOrder[i];
                newBounds[i] = replacement[i].Bounds;
            }
            sync.EnterWriteLock();
            try
            {
                if (replacement.Length != triangles.Length)
                    throw new ArgumentException("The triangle count must equal Count in original source order.", "trianglesInSourceOrder");
                index.Refit(newBounds);
                triangles = replacement;
            }
            finally { sync.ExitWriteLock(); }
        }

        public int Query(Bounds3 query, List<T> results) { return Query(query, results, SpatialQueryWorkspaces.Get()); }
        public int Query(Bounds3 query, List<T> results, QueryContext context)
        {
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
            sync.EnterReadLock();
            try
            {
                Workspace workspace = workspaces.Value;
                results.Clear();
                try
                {
                    if (kind == 0) index.Query(query, workspace.Indices, context);
                    else if (kind == 1) index.QuerySphere(sphere, workspace.Indices, context);
                    else index.QueryFrustum(frustum, workspace.Indices, context);
                    for (int i = 0; i < workspace.Indices.Count; i++) results.Add(items[workspace.Indices[i]]);
                    return results.Count;
                }
                finally { workspace.Indices.Clear(); }
            }
            finally { sync.ExitReadLock(); }
        }

        public bool RaycastAny(Ray3 ray, out RayHit<T> hit) { return RaycastAny(ray, out hit, SpatialQueryWorkspaces.Get()); }
        public bool RaycastAny(Ray3 ray, out RayHit<T> hit, QueryContext context)
        {
            sync.EnterReadLock();
            try
            {
                RayHit<int> indexedHit;
                bool found = index.RaycastAny(ray, intersector, out indexedHit, context);
                hit = found ? new RayHit<T>(items[indexedHit.Item], indexedHit.Parameter) : default(RayHit<T>);
                return found;
            }
            finally { sync.ExitReadLock(); }
        }

        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit) { return RaycastClosest(ray, out hit, SpatialQueryWorkspaces.Get()); }
        public bool RaycastClosest(Ray3 ray, out RayHit<T> hit, QueryContext context)
        {
            sync.EnterReadLock();
            try
            {
                RayHit<int> indexedHit;
                bool found = index.RaycastClosest(ray, intersector, out indexedHit, context);
                hit = found ? new RayHit<T>(items[indexedHit.Item], indexedHit.Parameter) : default(RayHit<T>);
                return found;
            }
            finally { sync.ExitReadLock(); }
        }

        public int RaycastAll(Ray3 ray, List<RayHit<T>> results) { return RaycastAll(ray, results, SpatialQueryWorkspaces.Get()); }
        public int RaycastAll(Ray3 ray, List<RayHit<T>> results, QueryContext context)
        {
            if (results == null) throw new ArgumentNullException("results");
            if (context == null) throw new ArgumentNullException("context");
            sync.EnterReadLock();
            try
            {
                Workspace workspace = workspaces.Value;
                results.Clear();
                try
                {
                    index.RaycastAll(ray, intersector, workspace.Hits, context);
                    for (int i = 0; i < workspace.Hits.Count; i++)
                    {
                        RayHit<int> indexedHit = workspace.Hits[i];
                        results.Add(new RayHit<T>(items[indexedHit.Item], indexedHit.Parameter));
                    }
                    return results.Count;
                }
                finally { workspace.Hits.Clear(); }
            }
            finally { sync.ExitReadLock(); }
        }
    }
}
