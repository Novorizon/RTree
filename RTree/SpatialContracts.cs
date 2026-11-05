using System;
using System.Collections.Generic;
using System.Threading;

namespace RTree
{
    /// <summary>Queries of real object bounds; payloads may occur more than once.</summary>
    public interface IBoundsQuery3<T>
    {
        int Count { get; }
        int Query(Bounds3 bounds, List<T> results, QueryContext context);
        int QuerySphere(Sphere3 sphere, List<T> results, QueryContext context);
        int QueryFrustum(Frustum3 frustum, List<T> results, QueryContext context);
    }

    /// <summary>Mutable object bounds identified by index-owned, generation-checked handles.</summary>
    public interface IMutableBoundsIndex3<T> : IBoundsQuery3<T>
    {
        ProxyHandle Insert(Bounds3 bounds, T item);
        /// <summary>Updates bounds; true means structural reinsertion, not success.</summary>
        bool Move(ProxyHandle handle, Bounds3 bounds);
        bool Remove(ProxyHandle handle);
        bool TryGetBounds(ProxyHandle handle, out Bounds3 bounds);
    }

    /// <summary>Unique unordered pairs whose current real AABBs overlap, before narrow phase.</summary>
    public interface IBroadPhase3<T> : IMutableBoundsIndex3<T>
    {
        int QueryPairs(List<ProxyPair<T>> results, QueryContext context);
    }

    /// <summary>
    /// Ray parameters use the supplied direction, without normalization.
    /// Box indexes return box hits; TriangleBvh returns exact triangle hits.
    /// </summary>
    public interface IRayQuery3<T>
    {
        bool RaycastAny(Ray3 ray, out RayHit<T> hit, QueryContext context);
        bool RaycastClosest(Ray3 ray, out RayHit<T> hit, QueryContext context);
        int RaycastAll(Ray3 ray, List<RayHit<T>> results, QueryContext context);
    }

    public interface IRayIntersector<T>
    {
        bool Intersect(T item, Ray3 ray, out double parameter);
    }

    public interface IPointQuery3<T>
    {
        int Count { get; }
        int Radius(Vector3d point, double maximumDistance, List<Neighbor<T>> results, QueryContext context);
        int KNearest(Vector3d point, int k, double maximumDistance, List<Neighbor<T>> results, QueryContext context);
    }

    public struct RayHit<T>
    {
        public readonly T Item;
        public readonly double Parameter;
        public RayHit(T item, double parameter) { Item = item; Parameter = parameter; }
    }

    public struct Neighbor<T>
    {
        public readonly T Item;
        public readonly double Distance;
        public Neighbor(T item, double distance) { Item = item; Distance = distance; }
    }

    /// <summary>A proxy identity, scoped to its index and invalidated on removal.</summary>
    public struct ProxyHandle : IEquatable<ProxyHandle>
    {
        public readonly int Slot;
        public readonly long Generation;
        public readonly long Owner;
        internal ProxyHandle(int slot, long generation, long owner)
        { Slot = slot; Generation = generation; Owner = owner; }
        /// <summary>Checks handle format only. Use TryGetBounds to check whether it is still live.</summary>
        public bool IsValid { get { return Owner > 0 && Slot >= 0 && Generation > 0; } }
        public bool Equals(ProxyHandle other)
        { return Slot == other.Slot && Generation == other.Generation && Owner == other.Owner; }
        public override bool Equals(object value) { return value is ProxyHandle && Equals((ProxyHandle)value); }
        public override int GetHashCode()
        { unchecked { return (Slot * 397 ^ Generation.GetHashCode()) * 397 ^ Owner.GetHashCode(); } }
        public static bool operator ==(ProxyHandle left, ProxyHandle right) { return left.Equals(right); }
        public static bool operator !=(ProxyHandle left, ProxyHandle right) { return !left.Equals(right); }
    }

    public struct ProxyPair<T>
    {
        public readonly ProxyHandle HandleA, HandleB;
        public readonly T ItemA, ItemB;
        public ProxyPair(ProxyHandle handleA, ProxyHandle handleB, T itemA, T itemB)
        { HandleA = handleA; HandleB = handleB; ItemA = itemA; ItemB = itemB; }
    }

    internal static class SpatialQueryWorkspaces
    {
        private static readonly ThreadLocal<QueryContext> workspaces =
            new ThreadLocal<QueryContext>(delegate { return new QueryContext(); });
        internal static QueryContext Get() { return workspaces.Value; }
        internal static void ValidateResults<T>(List<T> results, QueryContext context)
        {
            if (results == null) throw new ArgumentNullException("results");
            if (context == null) throw new ArgumentNullException("context");
        }
    }

    internal static class ProxyOwners
    {
        private static long next;
        internal static long Allocate()
        {
            long value = Interlocked.Increment(ref next);
            if (value <= 0) throw new InvalidOperationException("Proxy owner identifiers are exhausted.");
            return value;
        }
    }

    // The owning index provides synchronization. Query loops index payloads
    // directly; only writes can grow these arrays or recycle a generation.
    internal sealed class ProxyStore<T>
    {
        internal T[] Items = new T[16];
        internal Bounds3[] Bounds = new Bounds3[16];
        internal bool[] Live = new bool[16];
        internal long[] Generations = new long[16];
        internal int Next, Count;
        private readonly long owner = ProxyOwners.Allocate();
        private readonly Stack<int> free = new Stack<int>();

        internal int Allocate(Bounds3 bounds, T item)
        {
            if (ReferenceEquals(item, null)) throw new ArgumentNullException("item");
            int slot;
            if (free.Count != 0)
            {
                slot = free.Peek();
                if (Generations[slot] == long.MaxValue)
                    throw new InvalidOperationException("Proxy generation identifiers are exhausted.");
                free.Pop();
            }
            else
            {
                if (Next == Items.Length) Grow();
                slot = Next++;
            }
            Generations[slot]++;
            Items[slot] = item; Bounds[slot] = bounds; Live[slot] = true; Count++;
            return slot;
        }

        private void Grow()
        {
            int size = checked(Items.Length * 2);
            var items = new T[size]; var bounds = new Bounds3[size];
            var live = new bool[size]; var generations = new long[size];
            Array.Copy(Items, items, Next); Array.Copy(Bounds, bounds, Next);
            Array.Copy(Live, live, Next); Array.Copy(Generations, generations, Next);
            Items = items; Bounds = bounds; Live = live; Generations = generations;
        }

        internal void Release(int slot)
        {
            Items[slot] = default(T); Bounds[slot] = default(Bounds3);
            Live[slot] = false; Count--; free.Push(slot);
        }

        internal bool TryResolve(ProxyHandle handle, out int slot)
        {
            slot = handle.Slot;
            return handle.Owner == owner && slot >= 0 && slot < Next && Live[slot]
                && Generations[slot] == handle.Generation;
        }

        internal ProxyHandle Handle(int slot) { return new ProxyHandle(slot, Generations[slot], owner); }
    }
}
