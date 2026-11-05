using System;
using System.Collections.Generic;
using RTree;

namespace SpatialQueriesDemo
{
    internal static class Program
    {
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        private static int Main(string[] args)
        {
            string command = args.Length==0 ? "all" : args[0];
            if (args.Length>1 || (command!="all" && command!="skills" && command!="moving" && command!="broadphase" && command!="rays" && command!="points" && command!="--help"))
            { Console.Error.WriteLine("Use all|skills|moving|broadphase|rays|points|--help"); return 1; }
            if (command=="--help")
            { Console.WriteLine("SpatialQueries Demo: all|skills|moving|broadphase|rays|points (default: all)"); return 0; }
            try
            {
                if (command=="all" || command=="skills") Skills();
                if (command=="all" || command=="moving") Moving();
                if (command=="all" || command=="broadphase") BroadPhase();
                if (command=="all" || command=="rays") Rays();
                if (command=="all" || command=="points") Points();
                Console.WriteLine("PASS: "+command);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void Skills()
        {
            IMutableBoundsIndex3<Unit> index = new RTreeIndex<Unit>();
            // Replace the preceding constructor with DynamicAabbTree<Unit>()
            // or LooseOctree<Unit>(world) without changing the targeting service.
            var units = new[] {
                new Unit(1,2,new Vector3d(3,0,4)),       // exactly on radius 5
                new Unit(2,1,new Vector3d(1,0,1)),       // ally
                new Unit(3,2,new Vector3d(4.8,0,4.8)),   // in box, outside circle
                new Unit(4,2,new Vector3d(1,0,1)),       // dead
                new Unit(5,2,new Vector3d(1,4,1)),       // above skill height
                new Unit(6,2,new Vector3d(5.2,0,0)),     // AABB overlaps, center outside
                new Unit(7,2,new Vector3d(2,0,1)) };
            units[3].Alive=false;
            var handles = new Dictionary<int,ProxyHandle>();
            foreach (Unit unit in units) handles.Add(unit.Id,index.Insert(unit.Bounds,unit));
            var service = new SkillTargeting(index);
            var targets = new List<Unit>(64); var context = new QueryContext();
            service.GroundCircle(new Vector3d(0,0,0),5,-1,1,1,targets,context);
            Check(TargetIds(targets)=="1,7","ground circle must reject allies/dead/high/outside-center targets");
            Console.WriteLine("skills: ground circle targets = "+TargetIds(targets));
            service.GroundSector(new Vector3d(0,0,0),5,-1,1,new Vector3d(1,0,0),45,1,targets,context);
            Check(TargetIds(targets)=="7","sector refines circle candidates");
            Console.WriteLine("skills: 90-degree sector targets = "+TargetIds(targets));
            units[6].Position=new Vector3d(30,0,0);
            index.Move(handles[7],units[6].Bounds); // Move may return false and still update bounds.
            service.GroundCircle(new Vector3d(0,0,0),5,-1,1,1,targets,context);
            Check(TargetIds(targets)=="1","moving unit leaves skill range");
            Console.WriteLine("skills: after movement = "+TargetIds(targets));
        }

        private static string TargetIds(List<Unit> units)
        {
            var ids=new List<int>(); foreach(Unit unit in units) ids.Add(unit.Id);
            ids.Sort(); return string.Join(",",ids); // Display/sorting is outside the targeting loop.
        }

        private static void Moving()
        {
            ProxyHandle[] packedHandles;
            var packed=RTreeIndex<int>.BulkLoad(new[] {
                new KeyValuePair<Bounds3,int>(new Bounds3(0,0,0,1,1,1),10),
                new KeyValuePair<Bounds3,int>(new Bounds3(2,0,0,3,1,1),10) },out packedHandles);
            packed.Rebuild();packed.Move(packedHandles[1],new Bounds3(8,0,0,9,1,1));
            Check(packed.Count==2 && packed.Remove(packedHandles[0]),"STR handles and repeated payloads");
            var world = new Bounds3(-16,-16,-16,16,16,16);
            var indexes = new IMutableBoundsIndex3<int>[] {new RTreeIndex<int>(),new DynamicAabbTree<int>(),new LooseOctree<int>(world)};
            foreach (IMutableBoundsIndex3<int> index in indexes)
            {
                ProxyHandle handle = index.Insert(new Bounds3(0,0,0,1,1,1),42);
                index.Move(handle,new Bounds3(30,0,0,31,1,1));
                Bounds3 current;
                Check(index.TryGetBounds(handle,out current) && current.MinX==30,"Move preserves a live handle");
                var results=new List<int>();
                index.Query(new Bounds3(29,-1,-1,32,2,2),results); // Interface convenience overload.
                Check(results.Count==1 && results[0]==42,"common interface query");
                Check(index.Remove(handle) && !index.Remove(handle),"stale removal returns false");
                index.Insert(new Bounds3(0,0,0,1,1,1),43);
                Check(!index.TryGetBounds(handle,out current),"reused slots do not revive stale handles");
                Console.WriteLine("moving: "+index.GetType().Name+" lifecycle passed");
            }
        }

        private static void BroadPhase()
        {
            IBroadPhase3<int> index = new DynamicAabbTree<int>(2,2);
            ProxyHandle a=index.Insert(new Bounds3(0,0,0,1,1,1),101);
            ProxyHandle b=index.Insert(new Bounds3(2,0,0,3,1,1),102);
            var pairs=new List<ProxyPair<int>>(); var context=new QueryContext();
            Check(index.QueryPairs(pairs,context)==0,"fat overlap alone is not a real box pair");
            bool reinserted=index.Move(b,new Bounds3(.9,0,0,1.9,1,1));
            Check(!reinserted && index.QueryPairs(pairs)==1,"contact begins inside unchanged fat bounds");
            Check(pairs[0].HandleA!=pairs[0].HandleB,"unordered pair has distinct identities");
            Console.WriteLine("broadphase: one AABB pair; run shape narrow phase on entities 101/102");
            index.Remove(a);
            Check(index.QueryPairs(pairs,context)==0,"removed proxies leave no cached contacts");
        }

        private static void Rays()
        {
            var ray=new Ray3(new Vector3d(0,0,0),new Vector3d(0,0,2));
            var boxes=new StaticBvh<int>(new[] {new KeyValuePair<Bounds3,int>(new Bounds3(-1,-1,2,1,1,3),5)});
            IRayQuery3<int> boxRays=boxes; RayHit<int> hit;
            Check(boxRays.RaycastClosest(ray,out hit) && hit.Parameter==1,"static BVH returns a box hit");
            boxes.Refit(new[] {new Bounds3(-1,-1,6,1,1,7)});
            Check(boxRays.RaycastClosest(ray,out hit) && hit.Parameter==3,"refit uses original source order");
            var mesh=new TriangleBvh<int>(new[] {
                new KeyValuePair<Triangle3,int>(Triangle(4),7),new KeyValuePair<Triangle3,int>(Triangle(8),7) });
            IRayQuery3<int> rays=mesh; var context=new QueryContext(); var hits=new List<RayHit<int>>();
            Check(rays.RaycastClosest(ray,out hit,context) && hit.Parameter==2,"triangle hit has true t");
            Check(ray.At(hit.Parameter).Z==4,"unnormalized direction means t differs from distance");
            Check(rays.RaycastAll(ray,hits)==2 && hits[0].Parameter==2 && hits[1].Parameter==4,"sorted hits preserve repeated payloads");
            mesh.Refit(new[] {Triangle(6),Triangle(10)});
            Check(rays.RaycastClosest(ray,out hit,context) && hit.Parameter==3,"triangle refit");
            var frustum=new Frustum3(new Plane3(new Vector3d(1,0,0),2),new Plane3(new Vector3d(-1,0,0),2),
                new Plane3(new Vector3d(0,1,0),2),new Plane3(new Vector3d(0,-1,0),2),
                new Plane3(new Vector3d(0,0,1),-5),new Plane3(new Vector3d(0,0,-1),7));
            IBoundsQuery3<int> volume=mesh;var candidates=new List<int>();
            Check(volume.QueryFrustum(frustum,candidates)==1,"frustum emits triangle-AABB candidates");
            Console.WriteLine("rays: box refit, exact triangle hits, repeated payloads and frustum passed");
        }

        private static Triangle3 Triangle(double z)
        { return new Triangle3(new Vector3d(-1,-1,z),new Vector3d(1,-1,z),new Vector3d(0,1,z)); }

        private static void Points()
        {
            var tree=new KdTree<int>(new[] {new KeyValuePair<Vector3d,int>(new Vector3d(1,0,0),10),
                new KeyValuePair<Vector3d,int>(new Vector3d(-1,0,0),20),new KeyValuePair<Vector3d,int>(new Vector3d(3,0,0),30)});
            IPointQuery3<int> index=tree;var neighbors=new List<Neighbor<int>>();var context=new QueryContext();
            Check(index.Radius(new Vector3d(0,0,0),1,neighbors)==2 && neighbors[0].Item==10 && neighbors[1].Item==20,"radius includes ties in source order");
            Check(index.KNearest(new Vector3d(0,0,0),1,neighbors,context)==1 && neighbors[0].Item==10,"k=1 returns exactly one tied point");
            tree.Rebuild(new[] {new KeyValuePair<Vector3d,int>(new Vector3d(0,0,0),99)});
            Check(index.KNearest(new Vector3d(0,0,0),5,neighbors)==1 && neighbors[0].Item==99,"k clamps to point count after rebuild");
            Console.WriteLine("points: radius, stable ties, exact k and rebuild passed");
        }
    }
}
