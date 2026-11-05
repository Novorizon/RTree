# 空间索引用户指南

面向技能范围、附近对象、场景射线和物理宽检测。所有示例使用 `using RTree;`，
坐标为 double；游戏 Demo 约定 **Y 向上、XZ 为地面**。核心库不绑定坐标朝向、实体系统或物理引擎。

## 先选择用途

| 需求 | 使用类型 | 更新方式 | 结果含义 |
| --- | --- | --- | --- |
| 范围查询频繁的实体、技能、感知 | `RTreeIndex<T>` | 句柄 Insert/Move/Remove；STR BulkLoad/Rebuild | 真实对象 AABB 命中 |
| 大量移动对象、固定或可扩张世界 | `LooseOctree<T>` | 同样的句柄接口；Rebuild | 真实对象 AABB 命中 |
| 刚体宽检测、移动碰撞体 | `DynamicAabbTree<T>` | 同样的句柄接口；额外位移预测 | AABB 查询、候选配对 |
| 静态对象、建筑包围体 | `StaticBvh<T>` | 按源顺序 Refit，或 Rebuild | AABB 命中；可接精确射线回调 |
| 静态三角网格 | `TriangleBvh<T>` | 按源顺序 Refit，或 Rebuild | 范围是三角形 AABB 候选，射线是真实三角形命中 |
| 点云、路点、点最近邻 | `KdTree<T>` | Rebuild | 排序的点半径/精确 k 个近邻 |

范围查询多时可先用 RTree；每帧更新很多而查询较少时比较 Octree。物理宽检测选
Dynamic AABB；已有游戏物理引擎时先评估其 Overlap API，避免重复维护实体边界。
性能应统计整帧更新、查询、结果过滤与配对成本。已有[基准](../validation/SpatialPerformance/OPTIMIZATION.md)
是指定分布的测量，RTree 原接口的数字不直接代表新适配层的速度。

## 构建和运行

安装 .NET 8 SDK，在仓库根执行：

```sh
dotnet run --project examples/SpatialQueries/SpatialQueries.csproj -c Release -- all
```

不需要第三方包或 GPU。Demo 通过 ProjectReference 使用真正的 .NET 8 库项目，
不把库源码复制进示例程序集。应用项目可引用 `RTree/RTree.Net8.csproj`。
原 `RTree/RTree.csproj` 仍用于 .NET Framework 4.5，构建它需要相应 targeting pack；
源码保持 C# 5。这里未提供 Unity 专用包或宣称特定 Unity 版本已验证。

独立运行 `skills`、`moving`、`broadphase`、`rays` 或 `points`，替换上面命令的 `all`。
Demo 会检查预期结果，失败返回非零；CI 运行全部 Demo。

## 按能力依赖接口

| 接口 | 操作 | 实现 |
| --- | --- | --- |
| `IBoundsQuery3<T>` | Count、Query、QuerySphere、QueryFrustum | 六种对象树（包括原 RTree） |
| `IMutableBoundsIndex3<T>` | 上述查询＋Insert、Move、Remove、TryGetBounds | RTreeIndex、Dynamic AABB、Loose Octree |
| `IRayQuery3<T>` | RaycastAny、RaycastClosest、RaycastAll | 所有对象树 |
| `IBroadPhase3<T>` | 可变对象＋QueryPairs | Dynamic AABB |
| `IPointQuery3<T>` | Count、Radius、KNearest | KD-tree |

点近邻与对象包围体分开；静态树不承诺逐对象 Move。不要为未支持的能力做强制转换。
共同查询都有接受 `QueryContext` 的接口方法，也有省略 context 的扩展方法。
扩展方法需要 `using RTree;`；高频循环推荐显式复用工作区。

## 统一对象生命周期

```csharp
using System.Collections.Generic;
using RTree;

IMutableBoundsIndex3<int> targets = new RTreeIndex<int>();
// 按负载换成 new DynamicAabbTree<int>() 或 new LooseOctree<int>(world)。
var handle = targets.Insert(new Bounds3(0, 0, 0, 1, 2, 1), 1001);
targets.Move(handle, new Bounds3(4, 0, 0, 5, 2, 1));

var hits = new List<int>(128);
var context = new QueryContext();
targets.QuerySphere(new Sphere3(new Vector3d(0, 0, 0), 10), hits, context);
targets.Remove(handle);
```

`Bounds3` 参数顺序是 **minXYZ、maxXYZ**，边界必须有限、有序。默认值表示原点零体积盒。
Insert 返回的句柄属于该索引；保存在实体 ID→句柄表中。Move 无需传入旧边界。
**Move 返回的是是否发生结构重插，不是成功与否**：返回 false 时真实边界仍已更新，
RTreeIndex 在边界完全相同时返回 false。无效/已删除/其他索引的句柄传给 Move 会抛
`ArgumentException`；Remove、TryGetBounds 对这些句柄返回 false。

`handle.IsValid` 只检查格式，删除后可能仍为 true；用 TryGetBounds 判断是否存活。
代理槽位复用不会使旧句柄重新生效。payload 必须非 null，可以重复，不参与句柄身份比较。
一个实体有多个碰撞盒时会返回多个 payload，按游戏实体 ID 去重由应用完成。
payload 的位置字段改变后也要调用 Move，索引不会追踪用户对象的属性变化。

`RTreeIndex<T>` 是原 RTree 的适配层，保留读写锁、直接槽位和复用查询缓冲；有额外
句柄存储、同步与结果映射成本。原 `RTree<T>` API 继续保留，原有 T 唯一性约束不变。
适配层的写入使用内部值边界入口，无需每次移动创建 Rectangle/坐标数组。

## 批量建立 RTree，仍可移动

```csharp
var source = new[] {
    new KeyValuePair<Bounds3, int>(new Bounds3(0, 0, 0, 1, 1, 1), 1001),
    new KeyValuePair<Bounds3, int>(new Bounds3(2, 0, 0, 3, 1, 1), 1002)
};
ProxyHandle[] handles;
var targets = RTreeIndex<int>.BulkLoad(source, out handles);
targets.Move(handles[0], new Bounds3(10, 0, 0, 11, 1, 1));
targets.Rebuild(); // 重新 STR 打包，live handles 仍有效。
```

handles 与输入元素顺序一一对应；查询结果顺序不保证。可指定节点容量，默认 10/5：
`BulkLoad(source, 16, 8, out handles)`。最大容量至少 2，最小容量在 1..max/2 内；
新适配层对错误参数抛异常，原 API 的历史容量归一化行为继续保留。

## 技能：先取候选，再做玩法判断

| 形状 | 候选查询 | 玩法精筛 |
| --- | --- | --- |
| 3D 球 | QuerySphere | 按目标中心/碰撞体约定精筛 |
| 地面圆形/圆柱 | 覆盖水平半径和高度的 Query | 水平中心距离、中心高度 |
| 地面扇形 | 同上 | 再判断与朝向的夹角 |
| 线段/胶囊、定向盒 | 先查保守 AABB | 再做线段/胶囊或 OBB 相交 |

QuerySphere 判断球与目标 AABB 相交，可能命中中心在范围外的目标。地面技能若忽略
高差，不能直接把 3D 球距离当作水平距离。`examples/SpatialQueries/SkillTargeting.cs`
演示中心判定、阵营、死亡、角度和高度过滤；这些规则留在应用层，不写进树的核心接口。
示例采用普通游戏坐标，库的极端 double 数值保障不自动延伸到示例玩法计算。

`skills` Demo 的预期输出：圆形命中 `1,7`，90°扇形命中 `7`，目标 7 移走后圆形只命中 `1`。
它特意加入 AABB 命中但中心超范围、同阵营、死亡、超高和扇形外目标。

## 物理宽检测

```csharp
IBroadPhase3<int> broadPhase = new DynamicAabbTree<int>(0.1, 2);
var a = broadPhase.Insert(new Bounds3(0, 0, 0, 1, 1, 1), 1);
var b = broadPhase.Insert(new Bounds3(0.9, 0, 0, 1.9, 1, 1), 2);
var pairs = new List<ProxyPair<int>>(128);
broadPhase.QueryPairs(pairs, new QueryContext());
// 用 pairs 的实体 ID、句柄做碰撞层过滤和形状窄检测。
```

配对无重复、无自身配对，输出当前真实 AABB 的重叠候选。fat 候选缓存首次查询时启用，
以后持续维护；内存随 fat 重叠候选对增长。它不是完整物理引擎，也不是 CCD/运动轨迹碰撞。
高速物体需要保守 swept bounds 和连续碰撞检测。Demo 验证在 fat bounds 内移动、
Move 返回 false 时仍能产生新的真实配对。

## 静态树、射线和精确命中

StaticBvh 用 `IEnumerable<KeyValuePair<Bounds3,T>>` 构造；TriangleBvh 用
`IEnumerable<KeyValuePair<Triangle3,T>>`。Refit 必须按**原输入顺序**提供同数量几何，
它保留拓扑；大幅变形或数量变化时 Rebuild。

```csharp
IRayQuery3<int> mesh = new TriangleBvh<int>(new[] {
    new KeyValuePair<Triangle3, int>(new Triangle3(
        new Vector3d(-1, -1, 4), new Vector3d(1, -1, 4), new Vector3d(0, 1, 4)), 7)
});
var ray = new Ray3(new Vector3d(0, 0, 0), new Vector3d(0, 0, 2));
RayHit<int> hit;
if (mesh.RaycastClosest(ray, out hit)) {
    var point = ray.At(hit.Parameter); // Parameter=2，世界 Z=4。
}
```

Any 返回任一命中；Closest 返回最小参数命中；All 按参数升序，同参数的顺序不保证。
Ray3 不归一化方向，Parameter 只有在单位方向下才是世界距离，TMin/TMax 是包含边界的参数区间。
普通对象树返回盒命中；TriangleBvh 的射线精确测试三角形，但其范围查询仍是 AABB 候选。
其他精确图元使用 StaticBvh 的 `IRayIntersector<T>` 重载，契约见[几何细节](SpatialQueries.md)。

## 点半径和 kNN

```csharp
IPointQuery3<int> points = new KdTree<int>(new[] {
    new KeyValuePair<Vector3d, int>(new Vector3d(1, 0, 0), 1),
    new KeyValuePair<Vector3d, int>(new Vector3d(-1, 0, 0), 2)
});
var neighbors = new List<Neighbor<int>>(16);
points.Radius(new Vector3d(0, 0, 0), 2, neighbors);
points.KNearest(new Vector3d(0, 0, 0), 1, neighbors); // 默认距离上限 Infinity。
```

Radius 返回半径内全部点；KNearest 返回恰好 min(k, 合格点数) 个，可以显式传距离上限。
结果按距离、原始输入次序排序。KD k=1 不扩展全部同距项；原 RTree.Nearest 返回全部
并列最近项，不能把它当作半径查询或 exact-k 查询。

## 结果、线程与工作区

所有结果 List 都由查询**清空后写入**，返回 int 是结果数；不要依赖范围结果顺序。
列表与 QueryContext 每个活动查询独占。省略 context 的重载复用线程工作区，嵌套回调
查询应显式使用不同工作区。SkillTargeting 示例还拥有内部候选缓冲，不能跨活动调用共享。

各索引支持并发读，写入/refit/rebuild 与读取同步；RTreeIndex 的 Move 覆盖整个删除和插入，
读者不会看到中间缺失状态。payload 可变字段的读写由应用同步，索引锁不自动保护游戏状态。
Count 与后续 Query 是两个独立操作，不形成一个快照。

预留 List 容量并预热后，普通几何查询可 0 B 分配；新 RTreeIndex 的查询也有零分配回归。
首次扩容、写入、重建和三角形极端/边界精确回退可能分配。需要诊断时用
`new QueryContext(true)` 读取 NodesVisited、EntriesTested、PrimitiveTests、ResultCount。

详细几何语义、极值、坐标约定、两层网格查询与 GPU 边界见 [SpatialQueries.md](SpatialQueries.md)。
