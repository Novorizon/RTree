# Spatial Queries

导入 Sample，将 SpatialQueryExample 添加到场景对象，通过组件菜单运行 Run Spatial Query Example。初始球查询命中 1 项，移动后为 0，最后删除句柄。示例不依赖 ECS、技能或 Framework。

坐标使用 double 精度 Vector3d；接入 Transform 时显式转换 position 的 x/y/z。查询清空并填充调用方结果列表，QueryContext 不可跨并发查询共享。
