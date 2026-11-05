using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RTree;

// Deliberately uses separate, elementary brute-force geometry below. No query
// oracle calls the library's intersection/distance helpers or a second index.
internal static partial class Program
{
    private static int assertions;
    private static bool quick;
    private static int Main(string[] args)
    {
        quick = Array.IndexOf(args, "--quick") >= 0;
        int failures = 0;
        var total = Stopwatch.StartNew();
        Run("geometry and boundary queries", Boundaries, ref failures);
        Run("randomized box indexes and RTree adapter", RandomBoxes, ref failures);
        Run("dynamic AABB handles, updates and pairs", DynamicUpdates, ref failures);
        Run("dynamic AABB ordered, nested and moving objects", DynamicAdversarial, ref failures);
        Run("loose octree ownership, expansion and updates", OctreeUpdates, ref failures);
        Run("subtree pruning and movement outside cached content bounds", PruningAndMovement, ref failures);
        Run("scaled and cancelling frustum planes", FrustumPrecision, ref failures);
        Run("static BVH refit/rebuild", StaticUpdates, ref failures);
        Run("triangle BVH exact ray queries", Triangles, ref failures);
        Run("triangle precision regressions", TrianglePrecision, ref failures);
        Run("two-level scene query integration", SceneIntegration, ref failures);
        Run("KD radius, k nearest, ties and rebuild", Points, ref failures);
        Run("concurrent reads and writers", ConcurrentQueries, ref failures);
        Run("warmed reusable queries allocate zero bytes", Allocations, ref failures);
        Run("public mutable interfaces and RTreeIndex adapter", UserApiChecks, ref failures);
        Console.WriteLine("{0}: {1:N0} assertions, {2} failed groups, {3:F2}s ({4})",
            failures == 0 ? "PASS" : "FAIL", assertions, failures, total.Elapsed.TotalSeconds,
            quick ? "quick" : "full");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test, ref int failures)
    {
        var timer = Stopwatch.StartNew();
        try { test(); Console.WriteLine("PASS {0} ({1:F2}s)", name, timer.Elapsed.TotalSeconds); }
        catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL {0}: {1}", name, ex); }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException(label + ": expected " + typeof(T).Name);
    }

    private readonly struct Box
    {
        internal readonly double X0, Y0, Z0, X1, Y1, Z1;
        internal Box(double x0, double y0, double z0, double x1, double y1, double z1)
        { X0 = x0; Y0 = y0; Z0 = z0; X1 = x1; Y1 = y1; Z1 = z1; }
        internal Bounds3 Bounds => new Bounds3(X0, Y0, Z0, X1, Y1, Z1);
        internal Box Shift(double x, double y, double z) => new Box(X0 + x, Y0 + y, Z0 + z, X1 + x, Y1 + y, Z1 + z);
        internal Vector3d Center => V((X0 + X1) / 2, (Y0 + Y1) / 2, (Z0 + Z1) / 2);
    }

    private readonly struct Entry
    {
        internal readonly Box Box;
        internal readonly int Item;
        internal Entry(Box box, int item) { Box = box; Item = item; }
    }

    private readonly struct Ray
    {
        internal readonly Vector3d O, D;
        internal readonly double Min, Max;
        internal Ray(Vector3d origin, Vector3d direction, double min = 0, double max = double.PositiveInfinity)
        { O = origin; D = direction; Min = min; Max = max; }
        internal Ray3 Value => new Ray3(O, D, Min, Max);
    }

    private readonly struct Hit
    {
        internal readonly int Item;
        internal readonly double T;
        internal Hit(int item, double t) { Item = item; T = t; }
    }

    private readonly struct Tri
    {
        internal readonly Vector3d A, B, C;
        internal readonly int Item;
        internal Tri(Vector3d a, Vector3d b, Vector3d c, int item) { A = a; B = b; C = c; Item = item; }
        internal Triangle3 Value => new Triangle3(A, B, C);
        internal Box Box => new Box(Math.Min(A.X, Math.Min(B.X, C.X)), Math.Min(A.Y, Math.Min(B.Y, C.Y)),
            Math.Min(A.Z, Math.Min(B.Z, C.Z)), Math.Max(A.X, Math.Max(B.X, C.X)),
            Math.Max(A.Y, Math.Max(B.Y, C.Y)), Math.Max(A.Z, Math.Max(B.Z, C.Z)));
    }

    private static Vector3d V(double x, double y, double z) => new Vector3d(x, y, z);
    private static Vector3d Sub(Vector3d a, Vector3d b) => V(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Vector3d Cross(Vector3d a, Vector3d b) => V(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static bool Close(double a, double b) => a == b || Math.Abs(a - b) <= 2e-10 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
    private static double Coord(Vector3d v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static bool Overlap(Box a, Box b) => !(a.X1 < b.X0 || b.X1 < a.X0 || a.Y1 < b.Y0 || b.Y1 < a.Y0 || a.Z1 < b.Z0 || b.Z1 < a.Z0);
    private static double Distance(Vector3d a, Vector3d b)
    {
        double x = Math.Abs(a.X - b.X), y = Math.Abs(a.Y - b.Y), z = Math.Abs(a.Z - b.Z);
        double scale = Math.Max(x, Math.Max(y, z));
        if (scale == 0 || double.IsInfinity(scale)) return scale;
        x /= scale; y /= scale; z /= scale;
        return scale * Math.Sqrt(x * x + y * y + z * z);
    }

    private static bool SphereOverlap(Box b, Vector3d p, double radius)
    {
        var nearest = V(Math.Max(b.X0, Math.Min(b.X1, p.X)), Math.Max(b.Y0, Math.Min(b.Y1, p.Y)), Math.Max(b.Z0, Math.Min(b.Z1, p.Z)));
        return Distance(nearest, p) <= radius;
    }

    private static bool RayBox(Box b, Ray r, out double t)
    {
        double enter = r.Min, exit = r.Max;
        for (int axis = 0; axis != 3; ++axis)
        {
            double low = axis == 0 ? b.X0 : axis == 1 ? b.Y0 : b.Z0;
            double high = axis == 0 ? b.X1 : axis == 1 ? b.Y1 : b.Z1;
            double o = Coord(r.O, axis), d = Coord(r.D, axis);
            if (d == 0) { if (o < low || o > high) { t = 0; return false; } continue; }
            double a = DifferenceQuotient(low, o, d), c = DifferenceQuotient(high, o, d);
            if (a > c) { double tmp = a; a = c; c = tmp; }
            enter = Math.Max(enter, a); exit = Math.Min(exit, c);
            if (enter > exit) { t = 0; return false; }
        }
        t = enter;
        return true;
    }

    private static double DifferenceQuotient(double a,double b,double denominator)
    {
        double difference=a-b;
        return double.IsInfinity(difference)?a/denominator-b/denominator:difference/denominator;
    }

    private static bool RayTriangle(Tri triangle, Ray ray, out double t)
    {
        // Double-sided Moller-Trumbore, computed without library operations.
        Vector3d e1 = Sub(triangle.B, triangle.A), e2 = Sub(triangle.C, triangle.A);
        double scale = Math.Max(MaxAbs(e1), MaxAbs(e2));
        if (scale == 0) { t = 0; return false; }
        double directionScale = MaxAbs(ray.D);
        e1 = Divide(e1, scale); e2 = Divide(e2, scale);
        Vector3d direction = Divide(ray.D, directionScale);
        Vector3d p = Cross(direction, e2);
        double determinant = Dot(e1, p);
        if (determinant == 0) { t = 0; return false; }
        Vector3d offset = Divide(Sub(ray.O, triangle.A), scale);
        double u = Dot(offset, p) / determinant;
        Vector3d q = Cross(offset, e1);
        double v = Dot(direction, q) / determinant;
        t = (Dot(e2, q) / determinant) * scale / directionScale;
        return u >= 0 && v >= 0 && u + v <= 1 && t >= ray.Min && t <= ray.Max;
    }

    private static double MaxAbs(Vector3d v) => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));
    private static Vector3d Divide(Vector3d v,double scale) => V(v.X/scale,v.Y/scale,v.Z/scale);

    private static Frustum3 Frustum(Box box) => new Frustum3(
        new Plane3(V(1, 0, 0), -box.X0), new Plane3(V(-1, 0, 0), box.X1),
        new Plane3(V(0, 1, 0), -box.Y0), new Plane3(V(0, -1, 0), box.Y1),
        new Plane3(V(0, 0, 1), -box.Z0), new Plane3(V(0, 0, -1), box.Z1));

    private static bool PassesPlanes(Box box,(Vector3d Normal,double Offset)[] planes)
    {
        // Independent corner enumeration for conservative frustum culling.
        foreach(var plane in planes)
        {
            bool anyInside=false;
            for(int vertex=0;vertex<8;vertex++)
            {
                double x=(vertex&1)==0?box.X0:box.X1,y=(vertex&2)==0?box.Y0:box.Y1,z=(vertex&4)==0?box.Z0:box.Z1;
                if(plane.Normal.X*x+plane.Normal.Y*y+plane.Normal.Z*z+plane.Offset>=0)anyInside=true;
            }
            if(!anyInside)return false;
        }
        return true;
    }

    private static List<KeyValuePair<Bounds3, int>> BoxSource(List<Entry> entries) => entries.Select(x => new KeyValuePair<Bounds3, int>(x.Box.Bounds, x.Item)).ToList();
    private static List<KeyValuePair<Triangle3, int>> TriSource(List<Tri> entries) => entries.Select(x => new KeyValuePair<Triangle3, int>(x.Value, x.Item)).ToList();
    private static void Items(List<int> actual, IEnumerable<int> expected, string label)
    {
        int[] a = actual.OrderBy(x => x).ToArray(), e = expected.OrderBy(x => x).ToArray();
        Check(a.SequenceEqual(e), label + ": expected multiset [" + string.Join(",", e.Take(12)) + "] (" + e.Length + "), actual (" + a.Length + ")");
    }

    private static void BoxChecks(string label, IBoundsQuery3<int> tree, IRayQuery3<int> rays, List<Entry> entries, int rounds, int seed)
    {
        Check(tree.Count == entries.Count, label + " Count");
        var random = new Random(seed);
        var results = new List<int>(entries.Count + 1);
        var context = new QueryContext(true);
        double scale = entries.Count == 0 ? 1 : entries.Max(e => new[] { Math.Abs(e.Box.X0), Math.Abs(e.Box.Y0), Math.Abs(e.Box.Z0), Math.Abs(e.Box.X1), Math.Abs(e.Box.Y1), Math.Abs(e.Box.Z1) }.Max()) * 1.2;
        if (scale == 0) scale = 1;
        for (int i = 0; i < rounds; ++i)
        {
            Box query = RandomBox(random, scale, scale * (i % 7 == 0 ? 1 : .15));
            if (i == 0) query = new Box(-scale * 5, -scale * 5, -scale * 5, scale * 5, scale * 5, scale * 5);
            results.Add(int.MinValue);
            int count = tree.Query(query.Bounds, results, context);
            Check(count == results.Count, label + " Query return count");
            Items(results, entries.Where(e => Overlap(e.Box, query)).Select(e => e.Item), label + " AABB " + i);
            Vector3d center = query.Center;
            double radius = i % 13 == 0 ? 0 : random.NextDouble() * scale * .7;
            count = tree.QuerySphere(new Sphere3(center, radius), results, context);
            Check(count == results.Count, label + " Sphere return count");
            Items(results, entries.Where(e => SphereOverlap(e.Box, center, radius)).Select(e => e.Item), label + " sphere " + i);
            count = tree.QueryFrustum(Frustum(query), results, context);
            Check(count == results.Count, label + " Frustum return count");
            Items(results, entries.Where(e => Overlap(e.Box, query)).Select(e => e.Item), label + " frustum " + i);
            if (rays != null)
            {
                Vector3d origin = V(RandomSigned(random) * scale, RandomSigned(random) * scale, RandomSigned(random) * scale);
                Vector3d direction = i % 3 == 0 ? V(2, 0, 0) : i % 3 == 1 ? V(0, -3, 0) : V(RandomSigned(random), RandomSigned(random), RandomSigned(random));
                if (i % 9 == 0 && entries.Count > 0) origin = entries[i % entries.Count].Box.Center;
                Ray ray = new Ray(origin, direction, i % 11 == 0 ? .25 * scale : 0, i % 5 == 0 ? 3 * scale : double.PositiveInfinity);
                var expected = new List<Hit>();
                foreach (Entry e in entries) if (RayBox(e.Box, ray, out double parameter)) expected.Add(new Hit(e.Item, parameter));
                RayChecks(label + " ray " + i, rays, ray, expected, context);
            }
        }
    }

    private static void RayChecks(string label, IRayQuery3<int> tree, Ray ray, List<Hit> expected, QueryContext context)
    {
        var hits = new List<RayHit<int>> { default(RayHit<int>) };
        int count = tree.RaycastAll(ray.Value, hits, context);
        Check(count == hits.Count && hits.Count == expected.Count, label + " all count");
        for (int i = 1; i < hits.Count; i++) Check(hits[i - 1].Parameter <= hits[i].Parameter, label + " sorted parameters");
        var actual = hits.OrderBy(h => h.Item).ThenBy(h => h.Parameter).ToArray();
        var wanted = expected.OrderBy(h => h.Item).ThenBy(h => h.T).ToArray();
        for (int i = 0; i < actual.Length; ++i)
            Check(actual[i].Item == wanted[i].Item && Close(actual[i].Parameter, wanted[i].T), label + " all item/parameter " + i);
        bool any = tree.RaycastAny(ray.Value, out RayHit<int> anyHit, context);
        Check(any == (expected.Count != 0), label + " any presence");
        if (any) Check(expected.Any(h => h.Item == anyHit.Item && Close(h.T, anyHit.Parameter)), label + " any valid hit");
        bool closest = tree.RaycastClosest(ray.Value, out RayHit<int> closestHit, context);
        Check(closest == any, label + " closest presence");
        if (closest)
        {
            double minimum = expected.Min(h => h.T);
            Check(Close(closestHit.Parameter, minimum), label + " closest parameter");
            Check(expected.Any(h => h.Item == closestHit.Item && Close(h.T, minimum)), label + " closest valid item");
        }
    }

    private static double RandomSigned(Random random) => random.NextDouble() * 2 - 1;
    private static Box RandomBox(Random random, double spread, double maxSize)
    {
        double x = RandomSigned(random) * spread, y = RandomSigned(random) * spread, z = RandomSigned(random) * spread;
        return new Box(x, y, z, x + random.NextDouble() * maxSize, y + random.NextDouble() * maxSize, z + random.NextDouble() * maxSize);
    }

    private static List<Entry> Data(int count, int seed, double scale = 100, bool duplicates = false)
    {
        var random = new Random(seed);
        var entries = new List<Entry>();
        for (int i = 0; i < count; i++) entries.Add(new Entry(RandomBox(random, scale, scale * .12), duplicates ? i % 13 : i));
        return entries;
    }

    private static void Boundaries()
    {
        Throws<ArgumentException>(() => new Ray3(V(0, 0, 0), V(0, 0, 0)), "zero ray direction");
        Throws<ArgumentOutOfRangeException>(() => new Sphere3(V(0, 0, 0), -1), "negative sphere radius");
        var data = new List<Entry> {
            new Entry(new Box(0,0,0,1,1,1),0), new Entry(new Box(1,1,1,1,1,1),1),
            new Entry(new Box(-2,-2,-2,-1,-1,-1),2), new Entry(new Box(0,-1,0,0,1,0),3),
            new Entry(new Box(2,-1,-1,4,1,1),4) };
        var dynamic = new DynamicAabbTree<int>();
        var octree = new LooseOctree<int>(new Box(-10,-10,-10,10,10,10).Bounds);
        foreach (Entry e in data) { dynamic.Insert(e.Box.Bounds, e.Item); octree.Insert(e.Box.Bounds, e.Item); }
        var bvh = new StaticBvh<int>(BoxSource(data), 1);
        var trees = new (string Name, IBoundsQuery3<int> Box, IRayQuery3<int> Ray)[] {
            ("dynamic",dynamic,dynamic), ("octree",octree,octree), ("bvh",bvh,bvh) };
        var queries = new[] { new Box(1,1,1,1,1,1), new Box(1,-4,-4,2,4,4), new Box(-1,-1,-1,0,0,0), new Box(100,100,100,101,101,101) };
        var rays = new[] { new Ray(V(-4,0,0),V(2,0,0)), new Ray(V(0,0,0),V(1,0,0)), new Ray(V(1,1,1),V(0,-2,0)),
            new Ray(V(0,0,-2),V(-0.0,0,1)), new Ray(V(-4,0,0),V(2,0,0),2.5,3), new Ray(V(8,0,0),V(-2,0,0),1,2), new Ray(V(-4,3,0),V(2,0,0)) };
        foreach (var tree in trees)
        {
            var actual = new List<int>(); var ctx = new QueryContext();
            foreach (Box query in queries)
            {
                tree.Box.Query(query.Bounds, actual, ctx);
                Items(actual, data.Where(e => Overlap(e.Box, query)).Select(e => e.Item), tree.Name + " touching boxes");
                tree.Box.QueryFrustum(Frustum(query), actual, ctx);
                Items(actual, data.Where(e => Overlap(e.Box, query)).Select(e => e.Item), tree.Name + " touching planes");
            }
            tree.Box.QuerySphere(new Sphere3(V(2,1,1),1),actual,ctx);
            Items(actual,data.Where(e=>SphereOverlap(e.Box,V(2,1,1),1)).Select(e=>e.Item),tree.Name+" sphere tangent");
            var planes=new[]{(V(2,0,0),6.0),(V(-1,0,0),3.0),(V(0,3,0),9.0),(V(0,-1,0),3.0),(V(0,0,1),3.0),(V(1,-2,-3),0.0)};
            var slanted=new Frustum3(new Plane3(planes[0].Item1,planes[0].Item2),new Plane3(planes[1].Item1,planes[1].Item2),
                new Plane3(planes[2].Item1,planes[2].Item2),new Plane3(planes[3].Item1,planes[3].Item2),
                new Plane3(planes[4].Item1,planes[4].Item2),new Plane3(planes[5].Item1,planes[5].Item2));
            tree.Box.QueryFrustum(slanted,actual,ctx);
            Items(actual,data.Where(e=>PassesPlanes(e.Box,planes)).Select(e=>e.Item),tree.Name+" oblique nonunit frustum planes");
            foreach (Ray ray in rays)
            {
                var expected = new List<Hit>();
                foreach (Entry e in data) if (RayBox(e.Box,ray,out double t)) expected.Add(new Hit(e.Item,t));
                RayChecks(tree.Name+" boundary ray",tree.Ray,ray,expected,ctx);
            }
            Throws<ArgumentException>(()=>tree.Ray.RaycastAny(default(Ray3),out _,ctx),tree.Name+" default ray rejected");
        }
        // Empty trees exercise root sentinels, result clearing, and context release.
        var empty = new StaticBvh<int>(Array.Empty<KeyValuePair<Bounds3,int>>());
        BoxChecks("empty",empty,empty,new List<Entry>(),3,17);
        // Finite input values may have an overflowing coordinate difference.
        // These hits still have finite ray parameters and cannot be discarded.
        var hugeData=new List<Entry>{new Entry(new Box(5e307,-1,-1,1e308,1,1),1),new Entry(new Box(-1e308,-1,-1,-5e307,1,1),2)};
        var hugeBvh=new StaticBvh<int>(BoxSource(hugeData),1);
        foreach(Ray ray in new[]{new Ray(V(-1e308,0,0),V(1e308,0,0)),new Ray(V(1e308,0,0),V(-1e308,0,0))})
        {
            var expected=new List<Hit>();foreach(Entry e in hugeData)if(RayBox(e.Box,ray,out double t))expected.Add(new Hit(e.Item,t));
            RayChecks("overflowing subtraction ray",hugeBvh,ray,expected,new QueryContext());
        }
    }

    private static void RandomBoxes()
    {
        foreach (double scale in new[] { 1e-6, 1.0, 1e6 })
        {
            List<Entry> data = Data(quick ? 70 : 240, 3471, scale, true);
            var dynamic = new DynamicAabbTree<int>(scale*.01,2);
            var octree = new LooseOctree<int>(new Box(-scale*3,-scale*3,-scale*3,scale*3,scale*3,scale*3).Bounds, 10,2,true);
            foreach (Entry e in data) { dynamic.Insert(e.Box.Bounds,e.Item); octree.Insert(e.Box.Bounds,e.Item); }
            var bvh = new StaticBvh<int>(BoxSource(data),4);
            int rounds = quick ? 15 : 60;
            BoxChecks("dynamic "+scale,dynamic,dynamic,data,rounds,554);
            BoxChecks("octree "+scale,octree,octree,data,rounds,554);
            BoxChecks("bvh "+scale,bvh,bvh,data,rounds,554);
        }
        // RTree's legacy item contract requires unique values.
        foreach (double scale in new[] { 1e-6, 1.0, 1e6 })
        {
            List<Entry> unique = Data(quick ? 70 : 240, 3471, scale);
            var rtree = new RTree.RTree<int>();
            foreach (Entry e in unique) rtree.Add(new Rectangle(e.Box.X0,e.Box.Y0,e.Box.X1,e.Box.Y1,e.Box.Z0,e.Box.Z1),e.Item);
            BoxChecks("RTree adapter "+scale,rtree,rtree,unique,quick ? 15 : 60,554);
        }
    }

    private static void PruningAndMovement()
    {
        var octree = new LooseOctree<int>(new Bounds3(-1024,-1024,-1024,1024,1024,1024),12,2,false);
        var dynamic = new DynamicAabbTree<int>();
        var data = new List<Entry>();
        ProxyHandle moving = default;
        for (int i = 0; i < 256; i++)
        {
            double x = (i % 16) * .01, y = (i / 16) * .01;
            var box = new Box(x,y,0,x+.001,y+.001,.001);
            data.Add(new Entry(box,i));
            var handle = octree.Insert(box.Bounds,i);
            if (i == 0) moving = handle;
            dynamic.Insert(box.Bounds,i);
        }
        var results = new List<int>(); var context = new QueryContext(true);
        octree.Query(new Bounds3(900,900,900,901,901,901),results,context);
        Check(results.Count == 0 && context.NodesVisited == 1,"content bounds reject empty world regions at root");
        var all = new Bounds3(-2048,-2048,-2048,2048,2048,2048);
        var bvh = new StaticBvh<int>(BoxSource(data));
        foreach (IBoundsQuery3<int> index in new IBoundsQuery3<int>[] {octree,dynamic,bvh})
        {
            index.Query(all,results,context);
            Items(results,Enumerable.Range(0,256),"complete subtree inclusion preserves all items");
            Check(context.PrimitiveTests == 0,"contained subtree avoids primitive predicates");
        }
        // This stays inside the original loose owner but leaves its original
        // content bound. Repeating it in both directions detects stale bounds.
        foreach (double x in new[] {-.001,0.0,-.002,0.0})
        {
            var box = new Box(x,0,0,x+.001,.001,.001);
            Check(!octree.Move(moving,box.Bounds),"small move retains loose owner");
            data[0] = new Entry(box,0);
            octree.Query(box.Bounds,results,context);
            Items(results,data.Where(e=>Overlap(e.Box,box)).Select(e=>e.Item),"moved object expands content bounds through ancestors");
            var ray = new Ray(V(x,.0005,-1),V(0,0,1));
            var expected = new List<Hit>();
            foreach (Entry e in data) if (RayBox(e.Box,ray,out double t)) expected.Add(new Hit(e.Item,t));
            RayChecks("ray after in-cell move",octree,ray,expected,context);
        }
        Check(octree.Remove(moving),"remove moved proxy"); data.RemoveAt(0);
        octree.Rebuild();
        BoxChecks("rebuilt content bounds",octree,octree,data,8,91214);
        octree.Insert(new Bounds3(-800,-800,-800,-799,-799,-799),999);
        octree.Query(new Bounds3(-801,-801,-801,-798,-798,-798),results,context);
        Items(results,new[] {999},"recycled nodes reset content bounds");
        // Large late insertions must not hide the small leaves below them.
        dynamic.Insert(new Bounds3(-1000,-1000,-1000,1000,1000,1000),1000);
        dynamic.Query(new Bounds3(.02,.02,0,.021,.021,.001),results,context);
        Items(results,data.Where(e=>Overlap(e.Box,new Box(.02,.02,0,.021,.021,.001))).Select(e=>e.Item).Append(1000),"large sibling insertion preserves small leaves");
    }

    private static void FrustumPrecision()
    {
        foreach (double scale in new[] {1e-300,1.0,1e300})
        {
            var volume = new Frustum3(new Plane3(V(scale,0,0),scale),new Plane3(V(-scale,0,0),scale),
                new Plane3(V(0,scale,0),scale),new Plane3(V(0,-scale,0),scale),
                new Plane3(V(0,0,scale),scale),new Plane3(V(0,0,-scale),scale));
            Check(volume.Intersects(new Bounds3(-.5,-.5,-.5,.5,.5,.5)),"scaled frustum inside");
            Check(volume.Intersects(new Bounds3(1,-1,-1,2,1,1)),"scaled frustum touching plane");
            Check(!volume.Intersects(new Bounds3(2,-1,-1,3,1,1)),"scaled frustum outside");
            // Dot-product cancellation at a non-axis-aligned plane must retain
            // touching points, even when large products cancel exactly.
            var oblique = new Frustum3(new Plane3(V(scale,-scale,0),0),volume.GetPlane(1),
                volume.GetPlane(2),volume.GetPlane(3),volume.GetPlane(4),volume.GetPlane(5));
            Check(oblique.Intersects(new Bounds3(.5,.5,0,.5,.5,0)),"cancelling plane keeps contact");
            Check(!oblique.Intersects(new Bounds3(0,.5,0,0,.5,0)),"cancelling plane rejects clear outside");
        }
    }

    private sealed class Live
    {
        internal ProxyHandle Handle;
        internal Box Box;
        internal int Item;
    }

    private static void DynamicUpdates()
    {
        var tree = new DynamicAabbTree<int>(2,2);
        var foreign = new DynamicAabbTree<int>();
        ProxyHandle a = tree.Insert(new Box(0,0,0,1,1,1).Bounds,7);
        ProxyHandle b = tree.Insert(new Box(2,0,0,3,1,1).Bounds,7);
        var pairs = new List<ProxyPair<int>>(); var context = new QueryContext();
        tree.QueryPairs(pairs,context); Check(pairs.Count==0,"fat candidates must filter nonoverlapping true boxes");
        Check(!tree.Move(b,new Box(.9,0,0,1.9,1,1).Bounds),"inside fat box Move must avoid reinsertion");
        tree.QueryPairs(pairs,context); Check(pairs.Count==1,"moving within fat bounds must discover a new true contact");
        Check((pairs[0].HandleA.Equals(a)&&pairs[0].HandleB.Equals(b))||(pairs[0].HandleA.Equals(b)&&pairs[0].HandleB.Equals(a)),"pair identity with repeated payload");
        Check(!tree.Move(b,new Box(2,0,0,3,1,1).Bounds),"inside fat bounds noncontact move");
        tree.QueryPairs(pairs,context); Check(pairs.Count==0,"pair cache must remove separated true contact");
        Check(!foreign.Remove(a),"foreign tree cannot remove handle");
        Check(!foreign.TryGetBounds(a,out _),"foreign tree cannot resolve handle");
        Throws<ArgumentException>(()=>foreign.Move(a,new Box(0,0,0,1,1,1).Bounds),"foreign Move");
        Check(tree.Remove(a),"remove live handle"); Check(!tree.Remove(a),"remove stale handle");
        ProxyHandle replacement=tree.Insert(new Box(4,0,0,5,1,1).Bounds,8);
        Check(!replacement.Equals(a),"reused handle slot needs new generation");
        Check(!tree.TryGetBounds(a,out _),"stale handle cannot resolve reused slot");
        Check(tree.Remove(replacement)&&tree.Remove(b)&&tree.Count==0,"empty after removals");

        var random = new Random(47199); var live = new List<Live>();
        int rounds=quick?120:500;
        for(int step=0;step<rounds;step++)
        {
            int operation=random.Next(100);
            if(live.Count==0||operation<40)
            {
                Box box=RandomBox(random,8,3);
                live.Add(new Live{Handle=tree.Insert(box.Bounds,step%9),Box=box,Item=step%9});
            }
            else if(operation<80)
            {
                Live e=live[random.Next(live.Count)]; Box box=operation%2==0?e.Box.Shift(RandomSigned(random)*.05,0,0):RandomBox(random,8,3);
                tree.Move(e.Handle,box.Bounds,V(box.X0-e.Box.X0,box.Y0-e.Box.Y0,box.Z0-e.Box.Z0)); e.Box=box;
                Check(tree.TryGetBounds(e.Handle,out Bounds3 found)&&Same(found,box),"GetBounds tracks true Move bounds");
            }
            else
            {
                int index=random.Next(live.Count); ProxyHandle old=live[index].Handle;
                Check(tree.Remove(old),"random remove");live.RemoveAt(index);
                Check(!tree.TryGetBounds(old,out _),"random stale handle");
            }
            if(step%7==0)
            {
                PairChecks(tree,live,context,pairs);
                BoxChecks("dynamic updates "+step,tree,tree,live.Select(e=>new Entry(e.Box,e.Item)).ToList(),2,step+93);
            }
        }
        PairChecks(tree,live,context,pairs);
    }

    private static void DynamicAdversarial()
    {
        int count = quick ? 256 : 4096;
        foreach (string profile in new[] {"sorted","reverse","nested"})
        {
            var tree = new DynamicAabbTree<int>(0,0);
            var handles = new ProxyHandle[count]; var boxes = new Box[count];
            for (int i = 0; i < count; i++)
            {
                double x = profile == "reverse" ? count-i : i;
                boxes[i] = profile == "nested" ? new Box(-x,-x,-x,x+.25,x+.25,x+.25) : new Box(x,0,0,x+.25,0,0);
                handles[i] = tree.Insert(boxes[i].Bounds,i);
            }
            var buffer = new List<int>(); var context = new QueryContext(true);
            var probe = new Box(count/2.0,0,0,count/2.0,0,0);
            tree.Query(probe.Bounds,buffer,context);
            Items(buffer,Enumerable.Range(0,count).Where(i=>Overlap(boxes[i],probe)),"ordered/nested insertion query");
            if (profile != "nested") Check(context.NodesVisited < count/2,"ordered line inserts retain useful pruning");
            for (int i = 0; i < count; i += 3)
            {
                boxes[i] = boxes[i].Shift(count*2,1,0);
                tree.Move(handles[i],boxes[i].Bounds);
            }
            for (int i = 0; i < count; i += Math.Max(1,count/16))
            {
                tree.Query(boxes[i].Bounds,buffer,context);
                Items(buffer,Enumerable.Range(0,count).Where(j=>Overlap(boxes[j],boxes[i])),"ordered/nested move preserves descendants");
            }
            for (int i = count-1; i >= 0; i--) Check(tree.Remove(handles[i]),"ordered/nested drain");
            Check(tree.Count == 0,"drained dynamic root");
            tree.Insert(new Bounds3(0,0,0,1,1,1),999);
            tree.Query(new Bounds3(0,0,0,1,1,1),buffer,context);
            Items(buffer,new[] {999},"dynamic pools reused after drain");
        }
    }

    private static bool Same(Bounds3 bounds,Box b) => bounds.MinX==b.X0&&bounds.MinY==b.Y0&&bounds.MinZ==b.Z0&&bounds.MaxX==b.X1&&bounds.MaxY==b.Y1&&bounds.MaxZ==b.Z1;
    private static void PairChecks(DynamicAabbTree<int> tree,List<Live> live,QueryContext context,List<ProxyPair<int>> pairs)
    {
        var expected=new HashSet<(int,int)>();
        for(int i=0;i<live.Count;i++)for(int j=i+1;j<live.Count;j++)if(Overlap(live[i].Box,live[j].Box))expected.Add((i,j));
        int count=tree.QueryPairs(pairs,context);Check(count==pairs.Count,"pair return count");
        var actual=new HashSet<(int,int)>();
        foreach(var pair in pairs)
        {
            int a=live.FindIndex(e=>e.Handle.Equals(pair.HandleA)),b=live.FindIndex(e=>e.Handle.Equals(pair.HandleB));
            Check(a>=0&&b>=0&&a!=b,"pair contains live distinct handles");
            Check(pair.ItemA==live[a].Item&&pair.ItemB==live[b].Item,"pair payload matches handle");
            if(a>b)(a,b)=(b,a);
            Check(actual.Add((a,b)),"pair not emitted twice");
        }
        Check(actual.SetEquals(expected),"all true overlap pairs after move/remove");
    }

    private static void OctreeUpdates()
    {
        var world=new Box(-1,-1,-1,1,1,1);
        var tree=new LooseOctree<int>(world.Bounds,8,2,true);
        var live=new List<Live>();
        foreach(Box box in new[] {new Box(0,0,0,0,0,0),new Box(-1,-1,-1,1,1,1),new Box(-.1,-.1,-.1,.1,.1,.1),new Box(1,1,1,1,1,1)})
            live.Add(new Live{Handle=tree.Insert(box.Bounds,3),Box=box,Item=3});
        for(int i=0;i<35;i++)live.Add(new Live{Handle=tree.Insert(new Box(.2,.2,.2,.2,.2,.2).Bounds,4),Box=new Box(.2,.2,.2,.2,.2,.2),Item=4});
        Box huge=new Box(-1e6,-4,-3,2e6,3,2);live.Add(new Live{Handle=tree.Insert(huge.Bounds,9),Box=huge,Item=9});
        foreach(Live e in live)Check(tree.TryGetBounds(e.Handle,out Bounds3 found)&&Same(found,e.Box),"octree expansion preserves handles");
        Check(SameOrContains(tree.WorldBounds,huge),"expanded WorldBounds contains giant inserted box");
        tree.Rebuild();
        BoxChecks("octree coincident/giant",tree,tree,live.Select(e=>new Entry(e.Box,e.Item)).ToList(),12,892);
        var random=new Random(6672);
        for(int step=0;step<(quick?80:260);step++)
        {
            if(step%4==0)
            {
                Box box=RandomBox(random,Math.Pow(2,step%18),10);
                live.Add(new Live{Handle=tree.Insert(box.Bounds,step%7),Box=box,Item=step%7});
            }
            else if(step%4<3&&live.Count>0)
            {
                Live e=live[random.Next(live.Count)];Box box=RandomBox(random,Math.Pow(2,step%18),2);
                tree.Move(e.Handle,box.Bounds);e.Box=box;
            }
            else if(live.Count>0)
            {
                int index=random.Next(live.Count);var handle=live[index].Handle;
                Check(tree.Remove(handle)&&!tree.Remove(handle),"octree remove/generation");live.RemoveAt(index);
            }
            if(step%17==0){tree.Rebuild();BoxChecks("octree mutations",tree,tree,live.Select(e=>new Entry(e.Box,e.Item)).ToList(),3,step+98);}
        }
        var fixedTree=new LooseOctree<int>(world.Bounds,4,1.5,false);
        ProxyHandle fixedHandle=fixedTree.Insert(new Box(0,0,0,.5,.5,.5).Bounds,1);
        Throws<ArgumentOutOfRangeException>(()=>fixedTree.Insert(huge.Bounds,2),"fixed octree outworld insert");
        Throws<ArgumentOutOfRangeException>(()=>fixedTree.Move(fixedHandle,huge.Bounds),"fixed octree outworld move");
        Check(fixedTree.Count==1&&fixedTree.TryGetBounds(fixedHandle,out Bounds3 fixedFound)&&Same(fixedFound,new Box(0,0,0,.5,.5,.5)),"failed fixed world writes leave state unchanged");
        Check(!fixedTree.Remove(live[0].Handle)&&!fixedTree.TryGetBounds(live[0].Handle,out _),"octree handle ownership");
        Throws<ArgumentException>(()=>fixedTree.Move(live[0].Handle,world.Bounds),"octree foreign Move");
        foreach(double coordinate in new[]{0.0,double.Epsilon,1e-300})
        {
            var tiny=new LooseOctree<int>(new Box(0,0,0,0,0,0).Bounds,8,2,true);
            var tinyData=new List<Entry>{new Entry(new Box(0,0,0,0,0,0),1),new Entry(new Box(coordinate,0,0,coordinate,0,0),2)};
            var tinyHandles=tinyData.Select(e=>tiny.Insert(e.Box.Bounds,e.Item)).ToList();
            tiny.Rebuild();
            var found=new List<int>();tiny.Query(new Box(0,0,0,coordinate,0,0).Bounds,found,new QueryContext());
            Items(found,new[]{1,2},"octree degenerate/subnormal world");
            for(int i=0;i<tinyHandles.Count;i++)Check(tiny.TryGetBounds(tinyHandles[i],out Bounds3 b)&&Same(b,tinyData[i].Box),"tiny world preserves handles/bounds");
        }
    }

    private static bool SameOrContains(Bounds3 bounds,Box b) => bounds.MinX<=b.X0&&bounds.MinY<=b.Y0&&bounds.MinZ<=b.Z0&&bounds.MaxX>=b.X1&&bounds.MaxY>=b.Y1&&bounds.MaxZ>=b.Z1;

    private static void StaticUpdates()
    {
        List<Entry> data=Data(quick?60:190,8897,20,true);
        var tree=new StaticBvh<int>(BoxSource(data),3);
        for(int iteration=0;iteration<5;iteration++)
        {
            data=data.Select((e,i)=>new Entry(e.Box.Shift((i%5-2)*10,iteration,-iteration),e.Item)).ToList();
            tree.Refit(data.Select(e=>e.Box.Bounds).ToList());
            BoxChecks("static refit "+iteration,tree,tree,data,quick?5:15,iteration+81);
        }
        Throws<ArgumentException>(()=>tree.Refit(Array.Empty<Bounds3>()),"refit length mismatch");
        BoxChecks("static invalid refit atomicity",tree,tree,data,3,89);
        data=Data(37,992,2,true);tree.Rebuild(BoxSource(data));BoxChecks("static rebuild",tree,tree,data,12,174);
        tree.Rebuild(Array.Empty<KeyValuePair<Bounds3,int>>());BoxChecks("static rebuilt empty",tree,tree,new List<Entry>(),3,13);
    }

    private static void Triangles()
    {
        // Triangle 1 has the nearest box (z=1), but its exact hit is z=7.4;
        // triangle 2 hits at z=3 and must win closest traversal.
        var data=new List<Tri> {
            new Tri(V(-1,-1,1),V(1,-1,9),V(0,1,9),1),
            new Tri(V(-1,-1,3),V(1,-1,3),V(0,1,3),2),
            new Tri(V(0,0,2),V(0,0,2),V(0,0,2),3),
            new Tri(V(-1,0,4),V(0,0,4),V(1,0,4),4),
            new Tri(V(5,5,1),V(7,5,1),V(5,7,1),5) };
        var tree=new TriangleBvh<int>(TriSource(data),1);
        var context=new QueryContext(true);
        var rays=new List<Ray>{new Ray(V(0,0,0),V(0,0,1)),new Ray(V(0,0,10),V(0,0,-2)),new Ray(V(5.5,5.5,0),V(0,0,2)),
            new Ray(V(5,5,0),V(0,0,1)),new Ray(V(6,6,0),V(0,0,1)),new Ray(V(0,0,3),V(0,0,1)),new Ray(V(0,0,0),V(1,0,0)),new Ray(V(0,0,0),V(0,0,1),3.1,6)};
        foreach(Ray ray in rays)TriangleRayChecks(tree,data,ray,context);
        Check(tree.RaycastClosest(rays[0].Value,out RayHit<int> nearest,context)&&nearest.Item==2&&Close(nearest.Parameter,3),"closest must compare primitive hits, not bbox entry");
        var random=new Random(27611);
        for(int i=0;i<(quick?80:260);i++)
        {
            Vector3d p=V(RandomSigned(random)*20,RandomSigned(random)*20,RandomSigned(random)*20);
            data.Add(new Tri(p,V(p.X+random.NextDouble()*3,p.Y+random.NextDouble()*3,p.Z+random.NextDouble()*3),V(p.X+random.NextDouble()*3,p.Y-random.NextDouble()*3,p.Z+random.NextDouble()*3),i%17));
        }
        tree.Rebuild(TriSource(data));
        for(int i=0;i<(quick?40:180);i++)
        {
            Tri target=data[random.Next(data.Count)];
            Vector3d center=V((target.A.X+target.B.X+target.C.X)/3,(target.A.Y+target.B.Y+target.C.Y)/3,(target.A.Z+target.B.Z+target.C.Z)/3);
            Vector3d origin=V(center.X+RandomSigned(random)*4,center.Y+RandomSigned(random)*4,center.Z-5);
            TriangleRayChecks(tree,data,new Ray(origin,Sub(center,origin)),context);
        }
        data=data.Select(t=>new Tri(V(t.A.X+100,t.A.Y,t.A.Z),V(t.B.X+100,t.B.Y,t.B.Z),V(t.C.X+100,t.C.Y,t.C.Z),t.Item)).ToList();
        tree.Refit(data.Select(t=>t.Value).ToList());
        foreach(Tri t in data.Take(20))TriangleRayChecks(tree,data,new Ray(V(t.A.X,t.A.Y,t.A.Z-10),V(0,0,1)),context);
        Throws<ArgumentException>(()=>tree.Refit(Array.Empty<Triangle3>()),"triangle refit length mismatch");
        TriangleRayChecks(tree,data,new Ray(V(100,0,-50),V(0,0,1)),context);
        tree.Rebuild(Array.Empty<KeyValuePair<Triangle3,int>>());
        TriangleRayChecks(tree,new List<Tri>(),rays[0],context);
        foreach(double scale in new[]{1e-200,1e-150,1e150,1e200})
        {
            var scaled=new List<Tri>{new Tri(V(0,0,2),V(scale,0,2),V(0,scale,2),1),
                new Tri(V(0,0,4),V(scale,0,4),V(0,scale,4),2)};
            tree.Rebuild(TriSource(scaled));
            TriangleRayChecks(tree,scaled,new Ray(V(scale*.1,scale*.1,0),V(0,0,1)),context);
            TriangleRayChecks(tree,scaled,new Ray(V(scale*.7,scale*.7,0),V(0,0,1)),context);
        }
    }

    private static void TriangleRayChecks(TriangleBvh<int> tree,List<Tri> triangles,Ray ray,QueryContext context)
    {
        var expected=new List<Hit>();
        foreach(Tri triangle in triangles)if(RayTriangle(triangle,ray,out double t))expected.Add(new Hit(triangle.Item,t));
        RayChecks("triangle exact",tree,ray,expected,context);
    }

    private static void TrianglePrecision()
    {
        // Analytic answers, independent of the randomized Moller oracle: the
        // first two triangles lie in z = ray.Direction.Z, so their hit is t=1.
        // Their transverse and longitudinal scales differ by 400 exponents.
        var cases=new[] {
            ("tiny face at huge depth",new Tri(V(0,0,1e200),V(1e-200,0,1e200),V(0,1e-200,1e200),1),
                new Ray(V(1e-201,1e-201,0),V(0,0,1e200)),true),
            ("huge face at tiny depth",new Tri(V(0,0,1e-200),V(1e200,0,1e-200),V(0,1e200,1e-200),2),
                new Ray(V(1,1,0),V(0,0,1e-200)),true),
            // AB=(10,62,0), AC=2*AB exactly: this triangle has no area.
            ("integer collinear vertices",new Tri(V(24,-28,1),V(34,34,1),V(44,96,1),3),
                new Ray(V(29,3,0),V(0,0,1)),false),
            // Origin=A and direction=B-A: the entire ray lies in the plane.
            ("coplanar ray along edge",new Tri(V(82,43,7),V(24,-24,-23),V(42,-30,-93),4),
                new Ray(V(82,43,7),V(-58,-67,-30)),false) };
        foreach(var test in cases)
        {
            bool actual=test.Item2.Value.Intersects(test.Item3.Value,out double parameter);
            Check(actual==test.Item4,test.Item1+" direct triangle presence");
            if(actual)Check(Close(parameter,1),test.Item1+" direct triangle t=1");
            var tree=new TriangleBvh<int>(TriSource(new List<Tri>{test.Item2}),1);
            var expected=test.Item4?new List<Hit>{new Hit(test.Item2.Item,1)}:new List<Hit>();
            RayChecks(test.Item1,tree,test.Item3,expected,new QueryContext());
        }
    }

    private static void SceneIntegration()
    {
        var a=new Tri(V(-3,-3,0),V(3,-3,0),V(0,3,0),11);
        var b=new Tri(V(0,0,0),V(1,0,0),V(0,1,0),22);
        var meshes=new[]{new TriangleBvh<int>(TriSource(new List<Tri>{a})),new TriangleBvh<int>(TriSource(new List<Tri>{b}))};
        var translations=new[]{V(0,0,10),V(0,0,5)};
        var outer=new DynamicAabbTree<int>();
        outer.Insert(a.Box.Shift(0,0,10).Bounds,0);
        outer.Insert(b.Box.Shift(0,0,5).Bounds,1);
        var world=new Ray(V(.9,.9,0),V(0,0,2));
        var context=new QueryContext();
        Check(outer.RaycastClosest(world.Value,out RayHit<int> boxHit,context)&&boxHit.Item==1&&Close(boxHit.Parameter,2.5),
            "scene outer bbox closest is mesh B at t=2.5");
        var candidates=new List<RayHit<int>>();outer.RaycastAll(world.Value,candidates,context);
        Check(candidates.Count==2&&candidates[0].Item==1&&candidates[1].Item==0,"scene visits both meshes in bbox ray order");
        int closestMesh=-1,closestPrimitive=-1;double closest=double.PositiveInfinity;
        foreach(RayHit<int> candidate in candidates)
        {
            int mesh=candidate.Item;
            // Translation changes only the origin. Keeping direction (0,0,2)
            // makes every local hit parameter directly comparable in world t.
            var local=new Ray3(Sub(world.O,translations[mesh]),world.D,world.Min,world.Max);
            bool hit=meshes[mesh].RaycastClosest(local,out RayHit<int> primitive,context);
            if(mesh==1)Check(!hit,"scene mesh B bbox hit must fail exact triangle test");
            if(hit&&primitive.Parameter<closest){closest=primitive.Parameter;closestMesh=mesh;closestPrimitive=primitive.Item;}
        }
        Check(closestMesh==0&&closestPrimitive==11&&Close(closest,5),"scene exact closest is mesh A at world t=5");
        Check(Close(world.O.Z+world.D.Z*closest,10),"scene nonunit world ray reaches mesh A at z=10");
    }

    private static void Points()
    {
        var points=new List<KeyValuePair<Vector3d,int>> {
            new KeyValuePair<Vector3d,int>(V(1,0,0),10),new KeyValuePair<Vector3d,int>(V(-1,0,0),11),
            new KeyValuePair<Vector3d,int>(V(0,1,0),12),new KeyValuePair<Vector3d,int>(V(0,-1,0),13),
            new KeyValuePair<Vector3d,int>(V(0,0,0),14),new KeyValuePair<Vector3d,int>(V(0,0,0),14) };
        var tree=new KdTree<int>(points,1);var results=new List<Neighbor<int>>();var context=new QueryContext(true);
        foreach(int k in new[]{0,1,2,3,6,100})PointChecks(tree,points,V(0,0,0),1,k,results,context);
        PointChecks(tree,points,V(0,0,0),0,100,results,context);
        Throws<ArgumentOutOfRangeException>(()=>tree.KNearest(V(0,0,0),-1,1,results,context),"negative k");
        Throws<ArgumentOutOfRangeException>(()=>tree.Radius(V(0,0,0),-1,results,context),"negative radius");
        Throws<ArgumentOutOfRangeException>(()=>tree.KNearest(V(0,0,0),1,double.NaN,results,context),"NaN distance");
        var random=new Random(315871);
        foreach(double scale in new[]{1e-150,1.0,1e150})
        {
            points.Clear();
            for(int i=0;i<(quick?90:350);i++)points.Add(new KeyValuePair<Vector3d,int>(i%9==0?V(0,0,0):V(RandomSigned(random)*scale,RandomSigned(random)*scale,RandomSigned(random)*scale),i%23));
            tree.Rebuild(points);
            for(int i=0;i<(quick?25:100);i++)
            {
                Vector3d center=i%7==0?V(0,0,0):V(RandomSigned(random)*scale,RandomSigned(random)*scale,RandomSigned(random)*scale);
                double radius=i%8==0?double.PositiveInfinity:random.NextDouble()*scale*2;
                PointChecks(tree,points,center,radius,i%13==0?points.Count+9:random.Next(0,40),results,context);
            }
        }
        tree.Rebuild(Array.Empty<KeyValuePair<Vector3d,int>>());PointChecks(tree,new List<KeyValuePair<Vector3d,int>>(),V(0,0,0),double.PositiveInfinity,10,results,context);
    }

    private static void PointChecks(KdTree<int> tree,List<KeyValuePair<Vector3d,int>> points,Vector3d center,double radius,int k,List<Neighbor<int>> actual,QueryContext context)
    {
        var expected=points.Select((p,i)=>new{p.Value,Index=i,Distance=Distance(p.Key,center)}).Where(p=>p.Distance<=radius).OrderBy(p=>p.Distance).ThenBy(p=>p.Index).ToArray();
        actual.Add(default(Neighbor<int>));int count=tree.Radius(center,radius,actual,context);
        Check(count==actual.Count&&count==expected.Length,"KD radius count and clearing");
        for(int i=0;i<count;i++)Check(actual[i].Item==expected[i].Value&&CloseDistance(actual[i].Distance,expected[i].Distance),"KD radius exact distance/source tie order "+i);
        count=tree.KNearest(center,k,radius,actual,context);
        Check(count==actual.Count&&count==Math.Min(k,expected.Length),"KD exact k");
        for(int i=0;i<count;i++)Check(actual[i].Item==expected[i].Value&&CloseDistance(actual[i].Distance,expected[i].Distance),"KD nearest distance/source tie order "+i);
        Check(tree.Count==points.Count,"KD Count");
    }

    private static bool CloseDistance(double a,double b) => a==b || Math.Abs(a-b)<=Math.Max(Math.Abs(a),Math.Abs(b))*2e-12;

    private static void ConcurrentQueries()
    {
        List<Entry> data=Data(100,857,20);
        var dynamic=new DynamicAabbTree<int>();var handles=new List<ProxyHandle>();
        var octree=new LooseOctree<int>(new Box(-100,-100,-100,100,100,100).Bounds);
        var octHandles=new List<ProxyHandle>();
        foreach(Entry e in data){handles.Add(dynamic.Insert(e.Box.Bounds,e.Item));octHandles.Add(octree.Insert(e.Box.Bounds,e.Item));}
        var bvh=new StaticBvh<int>(BoxSource(data));var all=new Box(-1000,-1000,-1000,1000,1000,1000).Bounds;
        ReaderWriter("dynamic",dynamic,()=>{for(int i=0;i<80;i++)dynamic.Move(handles[i%handles.Count],data[i%data.Count].Box.Shift(i%2,0,0).Bounds);},all,data.Count);
        ReaderWriter("octree",octree,()=>{for(int i=0;i<80;i++)octree.Move(octHandles[i%octHandles.Count],data[i%data.Count].Box.Shift(i%2,0,0).Bounds);},all,data.Count);
        ReaderWriter("static",bvh,()=>{for(int i=0;i<25;i++)bvh.Refit(data.Select(e=>e.Box.Shift(i%2,0,0).Bounds).ToList());},all,data.Count);
        var triData=new List<Tri>{new Tri(V(-1,-1,1),V(1,-1,1),V(0,1,1),1),new Tri(V(-1,-1,2),V(1,-1,2),V(0,1,2),2)};
        var triangles=new TriangleBvh<int>(TriSource(triData));
        ReaderWriter("triangle",triangles,()=>{for(int i=0;i<25;i++)triangles.Refit(triData.Select(t=>t.Value).ToList());},all,triData.Count);
        var points=data.Select(e=>new KeyValuePair<Vector3d,int>(e.Box.Center,e.Item)).ToList();var kd=new KdTree<int>(points);
        var tasks=new List<Task>();using var gate=new Barrier(5);
        for(int worker=0;worker<4;worker++)tasks.Add(Task.Factory.StartNew(()=>{
            gate.SignalAndWait();
            var context=new QueryContext();var hits=new List<Neighbor<int>>();
            for(int i=0;i<80;i++)
            {
                int n=kd.KNearest(V(0,0,0),200,double.PositiveInfinity,hits,context);
                if(n!=data.Count||hits.Select(h=>h.Item).Distinct().Count()!=data.Count)throw new InvalidOperationException("concurrent KD result");
            }
        },TaskCreationOptions.LongRunning));
        tasks.Add(Task.Factory.StartNew(()=>{gate.SignalAndWait();for(int i=0;i<25;i++)kd.Rebuild(points);},TaskCreationOptions.LongRunning));
        Task.WaitAll(tasks.ToArray());Check(true,"concurrent KD readers/rebuild");
    }

    private static void ReaderWriter(string label,IBoundsQuery3<int> tree,Action writer,Bounds3 all,int expectedCount)
    {
        var tasks=new List<Task>();using var gate=new Barrier(5);
        for(int worker=0;worker<4;worker++)tasks.Add(Task.Factory.StartNew(()=>{
            gate.SignalAndWait();
            var context=new QueryContext();var actual=new List<int>();
            for(int i=0;i<100;i++)
            {
                int n=tree.Query(all,actual,context);
                if(n!=expectedCount||actual.Count!=expectedCount||actual.Distinct().Count()!=expectedCount)throw new InvalidOperationException(label+" concurrent result");
            }
        },TaskCreationOptions.LongRunning));
        tasks.Add(Task.Factory.StartNew(()=>{gate.SignalAndWait();writer();},TaskCreationOptions.LongRunning));Task.WaitAll(tasks.ToArray());Check(true,label+" concurrent readers/writer");
    }

    private static void Allocations()
    {
        List<Entry> data=Data(150,431,20);
        var dynamic=new DynamicAabbTree<int>();var octree=new LooseOctree<int>(new Box(-100,-100,-100,100,100,100).Bounds);
        var rtree=new RTree.RTree<int>();var adapter=new RTreeIndex<int>();
        foreach(Entry e in data){dynamic.Insert(e.Box.Bounds,e.Item);octree.Insert(e.Box.Bounds,e.Item);adapter.Insert(e.Box.Bounds,e.Item);rtree.Add(new Rectangle(e.Box.X0,e.Box.Y0,e.Box.X1,e.Box.Y1,e.Box.Z0,e.Box.Z1),e.Item);}
        var bvh=new StaticBvh<int>(BoxSource(data));
        var triangles=new TriangleBvh<int>(data.Select(e=>new KeyValuePair<Triangle3,int>(new Triangle3(V(e.Box.X0,e.Box.Y0,e.Box.Z0),V(e.Box.X1,e.Box.Y0,e.Box.Z1),V(e.Box.X0,e.Box.Y1,e.Box.Z1)),e.Item)).ToList());
        var query=new Box(-100,-100,-100,100,100,100).Bounds;
        foreach(var entry in new (string,IBoundsQuery3<int>,IRayQuery3<int>)[]{("dynamic",dynamic,dynamic),("octree",octree,octree),("bvh",bvh,bvh),("triangle",triangles,triangles),("RTree",rtree,rtree),("RTreeIndex",adapter,adapter)})
        {
            var results=new List<int>(data.Count);var hits=new List<RayHit<int>>(data.Count);var context=new QueryContext();
            var sphere=new Sphere3(V(0,0,0),100);var frustum=Frustum(new Box(-100,-100,-100,100,100,100));var ray=new Ray3(V(-100,0,0),V(1,0,0));
            Action action=()=>{entry.Item2.Query(query,results,context);entry.Item2.QuerySphere(sphere,results,context);entry.Item2.QueryFrustum(frustum,results,context);entry.Item3.RaycastAll(ray,hits,context);entry.Item3.RaycastAny(ray,out _,context);entry.Item3.RaycastClosest(ray,out _,context);};
            ZeroAllocation(entry.Item1,action);
        }
        var pairResults=new List<ProxyPair<int>>();var pairContext=new QueryContext();
        ZeroAllocation("dynamic pair cache",()=>dynamic.QueryPairs(pairResults,pairContext));
        var kd=new KdTree<int>(data.Select(e=>new KeyValuePair<Vector3d,int>(e.Box.Center,e.Item)).ToList());
        var neighbors=new List<Neighbor<int>>(data.Count);var kdContext=new QueryContext();
        ZeroAllocation("KD",()=>{kd.Radius(V(0,0,0),100,neighbors,kdContext);kd.KNearest(V(0,0,0),15,100,neighbors,kdContext);});
    }

    private static void ZeroAllocation(string label,Action action)
    {
        for(int i=0;i<80;i++)action();
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<256;i++)action();
        long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(bytes==0,label+" warmed reusable queries allocated "+bytes+" bytes");
    }
}
