# Unity Spatial Indexes

UPM 包 `com.novorizon.rtree`，Unity 2022.3+，公开程序集和命名空间保持 `RTree`。包含 RTree、DynamicAabbTree、StaticBvh、TriangleBvh、LooseOctree 和 KdTree；算法与旧 API 保持不变。


## 源码目录

```text
Runtime/
  Common/           # 公共接口、几何、精确三角形计算与查询工作区
  RTree/            # R-tree、适配器与兼容节点
  DynamicAabbTree/   # 动态 AABB 树
  StaticBvh/         # 静态 BVH
  TriangleBvh/       # 三角形 BVH
  LooseOctree/       # 松散八叉树
  KdTree/            # KD 树
  Grid/              # 独立格子索引归属；当前尚无算法实现
  RTree.asmdef
```

各类结构分别归档，仍编入一个 RTree 程序集。命名空间、公开接口和已有源码 .meta GUID 保持不变。Grid 目录的说明不代表已经新增格子算法。

## 安装

通过 Package Manager → Add package from git URL：

```text
https://github.com/Novorizon/RTree.git?path=/Packages/com.novorizon.rtree#codex/unity-upm-package
```

也可使用发行包 tgz。仅安装所选 UPM 包，不将整个仓库拖入 Assets。项目 asmdef 引用 `RTree`。不依赖 ECS、技能、渲染管线或 Framework；本包没有需要重复实现的单例、通用定时器或对象池。

## 查询

```csharp
using RTree;
using System.Collections.Generic;

IMutableBoundsIndex3<int> index = new RTreeIndex<int>();
var handle = index.Insert(new Bounds3(-1, -1, -1, 1, 1, 1), 42);
var results = new List<int>(64);
var workspace = new QueryContext();
index.QuerySphere(new Sphere3(new Vector3d(0, 0, 0), 3), results, workspace);
index.Move(handle, new Bounds3(10, 0, 10, 11, 1, 11));
index.Remove(handle);
```

Package Manager 的 Samples 提供 Spatial Queries 组件，导入后在场景对象上运行其组件菜单即可验证新增、移动、查询与删除。它只使用对象 ID，不携带角色、技能或阵营规则。

使用有限坐标，Bounds3 按 minX/minY/minZ/maxX/maxY/maxZ 排列。Unity Vector3 可通过显式读取 x/y/z 转为 Vector3d；查询半径和距离采用世界坐标单位。查询清空并填充调用方列表；一个 QueryContext 只服务一个并发中的查询。跨帧保存生命周期句柄，用 Move/Remove 更新或删除对象。

## 许可和验证

保留原始 LGPL-2.1-or-later 许可及源码版权，不改成 MIT。项目使用和分发时保留对应许可材料。

本轮通过 .NET Standard 2.1 编译、原有差分/随机和空间查询回归；这些检查不代表已完成 Unity Editor、IL2CPP 或设备验证。完整算法指南保留在仓库 docs/UserGuide.md 与 docs/SpatialQueries.md。
