using System;
using System.Collections.Generic;
using System.Linq;
using RTree;

internal static partial class Program
{
    private sealed class UnhashablePayload
    {
        public override int GetHashCode() { throw new InvalidOperationException("Payload hash must not be used for handle identity"); }
        public override bool Equals(object value) { throw new InvalidOperationException("Payload equality must not be used for handle identity"); }
    }

    private static void UserApiChecks()
    {
        foreach (IMutableBoundsIndex3<int> index in new IMutableBoundsIndex3<int>[] {
            new RTreeIndex<int>(),new DynamicAabbTree<int>(),new LooseOctree<int>(new Bounds3(-20,-20,-20,20,20,20)) })
        {
            var random=new Random(714933);var live=new List<Live>();
            var results=new List<int>();var context=new QueryContext(true);
            for(int step=0;step<(quick?150:650);step++)
            {
                int operation=random.Next(100);
                if(live.Count==0||operation<40)
                {
                    Box box=RandomBox(random,8,3);
                    live.Add(new Live{Handle=index.Insert(box.Bounds,step%5),Box=box,Item=step%5});
                }
                else if(operation<80)
                {
                    Live item=live[random.Next(live.Count)];Box box=RandomBox(random,8,3);
                    index.Move(item.Handle,box.Bounds);item.Box=box;
                    Check(index.TryGetBounds(item.Handle,out Bounds3 actual)&&Same(actual,box),"common Move updates real bounds");
                }
                else
                {
                    int i=random.Next(live.Count);var old=live[i].Handle;
                    Check(index.Remove(old)&&!index.Remove(old),"common Remove stale contract");live.RemoveAt(i);
                    Check(!index.TryGetBounds(old,out _),"common stale bounds contract");
                    Throws<ArgumentException>(()=>index.Move(old,new Bounds3()),"common stale Move contract");
                }
                Check(index.Count==live.Count,"common live count");
                if(step%31==0)BoxChecks("mutable API",index,(IRayQuery3<int>)index,live.Select(e=>new Entry(e.Box,e.Item)).ToList(),3,step+91);
            }
            IBoundsQuery3<int> read=index;IRayQuery3<int> rays=(IRayQuery3<int>)index;
            var all=new Bounds3(-100,-100,-100,100,100,100);
            read.Query(all,results);Items(results,live.Select(e=>e.Item),"interface Query convenience");
            read.QuerySphere(new Sphere3(V(0,0,0),100),results);Items(results,live.Select(e=>e.Item),"interface sphere convenience");
            read.QueryFrustum(Frustum(new Box(-100,-100,-100,100,100,100)),results);Items(results,live.Select(e=>e.Item),"interface frustum convenience");
            var ray=new Ray3(V(-100,0,0),V(1,0,0));var hits=new List<RayHit<int>>();
            rays.RaycastAll(ray,hits);Check(rays.RaycastAny(ray,out _)==(hits.Count>0),"interface any convenience");
            Check(rays.RaycastClosest(ray,out RayHit<int> hit)==(hits.Count>0),"interface closest convenience");
            if(hits.Count>0)Check(Close(hit.Parameter,hits[0].Parameter),"interface sorted ray results");
            var foreign=new DynamicAabbTree<int>().Insert(new Bounds3(),4);
            Check(!index.Remove(foreign)&&!index.TryGetBounds(foreign,out _),"common foreign handle");
            Throws<ArgumentException>(()=>index.Move(foreign,all),"common foreign Move");
        }

        var source=Data(quick?30:160,71339,8,true);
        var packed=RTreeIndex<int>.BulkLoad(BoxSource(source),out ProxyHandle[] handles);
        Check(handles.Length==source.Count,"packed handles in source order");
        for(int i=0;i<source.Count;i++)Check(packed.TryGetBounds(handles[i],out Bounds3 b)&&Same(b,source[i].Box),"packed source identity");
        packed.Rebuild();
        BoxChecks("packed adapter",packed,packed,source,15,411);
        Box changed=source[0].Box.Shift(20,0,0);
        Check(packed.Move(handles[0],changed.Bounds),"packed handle remains movable after rebuild");
        source[0]=new Entry(changed,source[0].Item);
        BoxChecks("moved packed adapter",packed,packed,source,10,712);
        var output=new List<int>();var stats=new QueryContext(true);
        packed.Query(new Bounds3(-100,-100,-100,100,100,100),output,stats);
        Check(stats.ResultCount==source.Count&&stats.NodesVisited>0,"adapter forwards statistics");
        stats.Enter();
        try { Throws<InvalidOperationException>(()=>packed.Query(new Bounds3(),output,stats),"adapter guards complete active query"); }
        finally { stats.Exit(); }
        packed.Query(new Bounds3(),output,stats);
        var allBounds=new Bounds3(-100,-100,-100,100,100,100);
        ZeroAllocation("adapter statistics",()=>packed.Query(allBounds,output,stats));

        var payload=new UnhashablePayload();var repeated=new RTreeIndex<UnhashablePayload>();
        var first=repeated.Insert(new Bounds3(),payload);var second=repeated.Insert(new Bounds3(),payload);
        repeated.Move(first,new Bounds3(1,1,1,2,2,2));repeated.Rebuild();
        var same=new List<UnhashablePayload>();repeated.Query(allBounds,same);
        Check(same.Count==2&&ReferenceEquals(same[0],payload)&&ReferenceEquals(same[1],payload),"duplicate unhashable payloads retain separate identities");
        Check(repeated.Remove(first)&&repeated.Remove(second),"unhashable payload lifecycle");
        Throws<ArgumentNullException>(()=>repeated.Insert(new Bounds3(),null),"null payload rejected");
        Throws<ArgumentOutOfRangeException>(()=>new RTreeIndex<int>(1,1),"adapter maximum validation");
        Throws<ArgumentOutOfRangeException>(()=>new RTreeIndex<int>(8,5),"adapter minimum validation");

        IPointQuery3<int> points=new KdTree<int>(new[] {new KeyValuePair<Vector3d,int>(V(0,0,0),3)});
        var near=new List<Neighbor<int>>();
        Check(points.Radius(V(0,0,0),1,near)==1,"point radius convenience");
        Check(points.KNearest(V(0,0,0),1,near)==1&&points.KNearest(V(0,0,0),1,near,new QueryContext())==1,"unbounded nearest convenience");
        Check(points.KNearest(V(0,0,0),1,0,near)==1,"bounded nearest convenience");
        IBroadPhase3<int> broad=new DynamicAabbTree<int>();broad.Insert(new Bounds3(),1);broad.Insert(new Bounds3(),2);
        Check(broad.QueryPairs(new List<ProxyPair<int>>())==1,"broad phase interface convenience");
        IBoundsQuery3<int> missing=null;
        Throws<ArgumentNullException>(()=>missing.Query(new Bounds3(),output),"null index convenience");

        // Unique source IDs for the shared concurrency oracle.
        var unique=Data(80,1258,8);var concurrent=new RTreeIndex<int>();
        var concurrentHandles=unique.Select(e=>concurrent.Insert(e.Box.Bounds,e.Item)).ToArray();
        ReaderWriter("RTreeIndex",concurrent,()=>{
            for(int i=0;i<80;i++){concurrent.Move(concurrentHandles[i%unique.Count],unique[i%unique.Count].Box.Shift(i%2,0,0).Bounds);if(i%10==0)concurrent.Rebuild();}
        },allBounds,unique.Count);
    }
}
