# CPU 3D 查询索引

本库提供四种补充索引，并为原 RTree 接入共同的 bounds 和 ray 接口。
新用户从[用户指南](UserGuide.md)和[可运行 Demo](../examples/SpatialQueries/README.md)开始。
`RTreeIndex<T>` 提供 Bounds3 和句柄生命周期，支持 IMutableBoundsIndex3；原 RTree API 继续兼容。
所有实现使用 double 几何、托管连续数组和可复用查询上下文；GPU 用途见文末。

| 类型 | 适用数据 | 更新接口 | 查询语义 |
| --- | --- | --- | --- |
| `DynamicAabbTree<T>` | 移动物体与物理 broad phase | `Insert/Move/Remove` | 真实 AABB 查询、射线 box hit、候选 pair cache |
| `StaticBvh<T>` | 静态或可 refit 的对象 bounds | `Rebuild/Refit` | bounds 查询；默认 box ray hit，可提供精确图元回调 |
| `TriangleBvh<T>` | 静态三角网格 | `Rebuild/Refit` | bounds 查询产生 triangle AABB 候选；ray 返回真实三角形命中 |
| `LooseOctree<T>` | 有世界范围的场景管理 | `Insert/Move/Remove/Rebuild` | 唯一归属，无重复输出；按真实 AABB 过滤 |
| `KdTree<T>` | 点云、点最近邻 | `Rebuild` | 按距离排序的 `Radius` 与 `KNearest` |
| `RTree<T>` | 动态空间对象与范围查询 | 原 API、STR、`Rebuild` | 保留原 API，另支持共同 bounds/sphere/frustum/ray 接口 |

AABB tree 是 AABB 包围体的 BVH。动态 BVH 使用连续二叉节点、fat bounds、
受限高度平衡、降低包围面积的局部旋转与 refit；静态 BVH 使用 12-bin SAH，退化划分保持平衡。
KD-tree 按最大跨度轴选中位数，保留原输入顺序作为同距次序。

## 几何与共同查询

`Bounds3` 的六标量顺序是 **minX/minY/minZ/maxX/maxY/maxZ**，与旧 `Rectangle`
构造器不同。`Bounds3`、`Vector3d`、`Ray3`、`Sphere3`、`Plane3`、`Triangle3`
是值类型；`Frustum3` 是不可变六平面对象。坐标必须有限。

```csharp
var box = new RTree.Bounds3(-1, -1, -1, 1, 1, 1);
var tree = new RTree.DynamicAabbTree<int>();
RTree.ProxyHandle proxy = tree.Insert(box, 42);
var results = new System.Collections.Generic.List<int>(256);
var context = new RTree.QueryContext(true);

tree.Query(box, results, context);
tree.QuerySphere(new RTree.Sphere3(new RTree.Vector3d(0, 0, 0), 5), results, context);
tree.Move(proxy, new RTree.Bounds3(0, 0, 0, 2, 2, 2));
tree.Remove(proxy);
```

`IBoundsQuery3<T>` 定义 `Query/QuerySphere/QueryFrustum`，`IRayQuery3<T>` 定义
`RaycastAny/RaycastClosest/RaycastAll`。结果列表先清空再填充，顺序只在明确约定
时保证。所有 query 接口均可传入独占 `QueryContext`，便利重载复用线程局部上下文。

AABB 和 sphere 对真实对象 bounds 做包含边界的测试。Frustum 使用六个内向
平面的保守 AABB 剔除：`normal · position + offset >= 0` 为内部，结果可能含
通过平面测试但不与完整视锥精确相交的盒子。它用于可见性候选筛选。

非 null payload 可以重复，重复几何也可以重复。新索引按代理或源索引区分对象，
因此结果是带重数的序列。旧 RTree 仍保留其 payload 唯一性约定。

## 动态 AABB 与配对

```csharp
var tree = new RTree.DynamicAabbTree<string>(0.1, 2.0);
var handle = tree.Insert(new RTree.Bounds3(0, 0, 0, 1, 1, 1), "object");
bool reinserted = tree.Move(handle,
    new RTree.Bounds3(0.01, 0, 0, 1.01, 1, 1),
    new RTree.Vector3d(0.01, 0, 0));
var pairs = new System.Collections.Generic.List<RTree.ProxyPair<string>>();
tree.QueryPairs(pairs, new RTree.QueryContext());
```

`Move` 的返回值表示结构是否重插，`false` 时真实 bounds 仍已更新。
fat bounds 的固定余量和位移预测减少结构更新；它们不改变普通查询的真实
bounds 过滤。句柄包含所属索引、槽位和 generation，跨索引和移除后的旧句柄
不会误指向新对象。`Remove` 对无效句柄返回 false，`Move` 对其抛参数异常。

首次 `QueryPairs` 或读取 `CachedPairCount` 时懒建 fat 候选缓存，之后持续维护。
物体在 fat bounds 内开始真实重叠时，原候选仍在缓存中，`QueryPairs` 会重新
检查真实 bounds。输出为唯一、无自配对的真实 AABB 重叠 pair。启用缓存后，
稠密场景的存储随 fat 候选对数增长；纯查询使用者在启用前不付这个缓存成本。

## 静态 BVH、refit 与真实射线

`StaticBvh<T>` 接受 `IEnumerable<KeyValuePair<Bounds3,T>>`；`TriangleBvh<T>`
接受 `IEnumerable<KeyValuePair<Triangle3,T>>`。可指定叶容量，默认 4。
`Rebuild` 创建新结构后在写锁下发布。`Refit` 输入必须按最初源顺序排列，数量
等于 Count；它保留对象身份与节点拓扑，只更新 bounds。

```csharp
var triangles = new[] {
    new System.Collections.Generic.KeyValuePair<RTree.Triangle3, int>(
        new RTree.Triangle3(new RTree.Vector3d(-1, -1, 2),
                            new RTree.Vector3d(1, -1, 2),
                            new RTree.Vector3d(0, 1, 2)), 7)
};
var mesh = new RTree.TriangleBvh<int>(triangles);
var ray = new RTree.Ray3(new RTree.Vector3d(0, 0, 0), new RTree.Vector3d(0, 0, 2));
RTree.RayHit<int> hit;
bool found = mesh.RaycastClosest(ray, out hit, new RTree.QueryContext());
// found == true, hit.Item == 7, hit.Parameter == 1.
```

Ray 保留输入方向，不自动归一化；`TMin/TMax` 是包含边界的 **参数**区间。
方向必须非零，TMin 必须有限且非负，TMax 可为正无穷。`Parameter` 只有方向为
单位向量时才等于世界距离。命中参数需要能用有限 double 表示。box ray 从内部
出发时，入口参数为 TMin。`RaycastAll` 按参数升序，相同参数之间不保证顺序。

三角形相交是双面、包含边界的查询，退化三角形和共面射线不产生孤立命中。
普通内点使用误差界快速路径；边界、近乎平行、下溢或极端尺度采用精确 dyadic
谓词和正确舍入的参数转换。精确回退使用 `System.Numerics.BigInteger`，可能
分配内存，普通热查询零分配不包含这些回退或缓冲扩容。

`StaticBvh<T>` 的 `IRayIntersector<T>` 重载可接入其他窄相图元。
回调必须返回真实参数；返回 NaN、Infinity 或区间外参数会被忽略。closest 使用
真实交点收紧上界，不能用最近包围盒替代最近图元。回调不得重入同一索引或
活动 context，嵌套其他查询时使用独立 context。

两层场景可以将动态 AABB 树用于场景对象，TriangleBvh 用于各对象的局部网格。
先获得对象候选，再把射线转换到局部空间，并保持同一个参数 t；非均匀缩放后
不应擅自归一化局部方向。每级使用独立工作区，最终以真实图元参数决定最近项。

## Loose octree

```csharp
var world = new RTree.Bounds3(-1024, -1024, -1024, 1024, 1024, 1024);
var tree = new RTree.LooseOctree<int>(world, 16, 2.0, true);
var handle = tree.Insert(new RTree.Bounds3(0, 0, 0, 1, 1, 1), 3);
tree.Move(handle, new RTree.Bounds3(2048, 0, 0, 2049, 1, 1));
// 自动扩展根并重建节点归属，handle 仍有效。
```

构造参数依次是初始 world bounds、最大深度 0–32、looseness（至少 1）、
是否自动扩容。默认 16/2/true。跨子单元的大对象留在合适祖先，代理只出现一次。
当前 loose cell 足以容纳新真实 bounds 时，Move 更新数据并在必要时扩张祖先的
查询内容边界，无需重插。查询按子树内容边界裁剪，而非整个 loose cell。
内容边界只随写入扩张，保持保守包含；移动和删除积累的空隙可通过 Rebuild 收紧。
这以每个已分配节点额外 48 字节的边界数组换取更好的裁剪，移动仍可避免结构更新。
关闭自动扩容时，越界 Insert/Move 抛异常且不改变索引。Rebuild 保留句柄。

## 点 KD-tree

```csharp
var source = new[] {
    new System.Collections.Generic.KeyValuePair<RTree.Vector3d, int>(new RTree.Vector3d(0, 0, 0), 0),
    new System.Collections.Generic.KeyValuePair<RTree.Vector3d, int>(new RTree.Vector3d(1, 0, 0), 1)
};
var kd = new RTree.KdTree<int>(source);
var neighbors = new System.Collections.Generic.List<RTree.Neighbor<int>>(16);
kd.KNearest(new RTree.Vector3d(0, 0, 0), 16, double.PositiveInfinity, neighbors, new RTree.QueryContext());
kd.Radius(new RTree.Vector3d(0, 0, 0), 1, neighbors, new RTree.QueryContext());
```

默认叶容量 8，支持完整 Rebuild。Radius 返回全部范围内点；KNearest 返回恰好
min(k, 范围内点数) 个，不自动扩展第 k 距离的全部并列项。k=0 返回空，负数抛异常。
两者都按距离升序，距离相同时按源输入顺序排序；重复点与 payload 都保留。
这与旧 RTree 的“全部并列最近项”是不同契约，不能直接混作同一性能工作负载。
极端距离使用缩放键保持排序；输出的 double Distance 在超出可表示范围时可能
为 Infinity，这不表示内部把所有这些点视为同一个距离。

## 同步、统计与测试

每个索引允许并发读，写入/refit/rebuild 受读写锁保护。私有 builder 的完成结果
原子发布。调用者持有的 payload 不会因此被冻结。结果列表和 context 不可由
同时活动的查询共享；过期句柄、错误 refit 数量和无效参数都有回归测试。

`QueryContext(true)` 记录节点访问、bounds 检查和真实叶谓词检查。后者对 box
索引是实际对象 AABB，对精确 triangle ray 是三角形，对 KD 是点距离测试。
统计应与命中量一起看，不能把返回数千对象的查询直接和只返回一个对象比较。

```sh
dotnet run --project validation/Correctness/Correctness.csproj -c Release
dotnet run --project validation/SpatialCorrectness/SpatialCorrectness.csproj -c Release
```

两套测试都有独立暴力 oracle。额外性能矩阵见
[SpatialPerformance](../validation/SpatialPerformance/README.md)，将 box、triangle、
point 和动态更新分开比较，并把候选生成与精确命中区分开。

## GPU 做什么

GPU 的主要价值是并行吞吐：

1. 批量建树：并行生成 Morton 编码、排序并构建 LBVH/HLBVH，服务频繁的大批量重建。
2. 批量遍历：同时处理大量射线、视锥剔除或物体候选对。支持的硬件 ray-tracing
   接口还可使用专用加速结构和三角形求交单元。
3. 批量点查询：许多查询点一起做近邻或半径搜索，评估树遍历、网格或暴力分块方案。

数据常驻 GPU、输入和结果能够批处理时，才容易摊薄传输、提交与同步成本。
单次编辑器点选或少量 CPU 侧查询可能更适合当前 CPU 索引。GPU 后端需要单独
计量上传、构建、查询、读回和端到端延迟，并制定 float/double 精度契约；CPU
的 BigInteger 精确回退不能直接移植为 shader。这个版本提供 CPU 实现和比较基线。
