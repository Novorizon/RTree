# 可运行 Demo

仓库根目录使用 .NET 8 SDK：

```sh
dotnet run --project examples/SpatialQueries/SpatialQueries.csproj -c Release -- all
```

无第三方包；通过 ProjectReference 引用 `RTree.Net8.csproj`。`all` 运行全部场景并检查
预期结果。其他命令：

| 参数 | 示例内容 |
| --- | --- |
| `skills` | RTree 技能圆形/扇形，阵营、死亡、中心距离、高度、移动后更新 |
| `moving` | 通过 IMutableBoundsIndex3 切换 RTreeIndex / Dynamic AABB / Octree，句柄更新和失效 |
| `broadphase` | Dynamic AABB 的 fat 内移动、新配对、移除后缓存更新 |
| `rays` | Static BVH Refit、Triangle BVH 精确交点、非单位方向、排序、重复 payload、视锥 |
| `points` | KD Radius / KNearest、同距稳定顺序、Rebuild |

每组成功打印说明，最后打印 `PASS: all`。失败打印错误并返回退出码 1。
`--help` 显示命令；技能中心规则见 [SkillTargeting.cs](SkillTargeting.cs)，全部启动逻辑见
[Program.cs](Program.cs)。显示和断言会创建临时对象；Demo 输出不是性能基准。

技能约定 Y 为高度、XZ 为地面；角度使用扇形半角，因此传 45 表示总角度 90°。
边界查询只是候选筛选，阵营、目标中心和视线等是应用策略。技能过滤组件拥有复用缓冲，
每个活动调用需要独立组件实例/工作区；示例不宣称是完整技能系统或物理引擎。

完整接口与选型：[用户指南](../../docs/UserGuide.md)。
