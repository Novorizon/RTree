# Unity UPM 整理验证

2026-11-05，包 com.novorizon.rtree 1.0.0-preview.2，目标 Unity 2022.3。原有十九个算法源码仅移动到 Packages/com.novorizon.rtree/Runtime，不重写算法、不改公共命名空间、几何规则、查询和句柄行为。原 .NET 项目及所有回归工具引用该唯一来源。

已运行：

- .NET Standard 2.1 Release 编译：零警告、零错误。
- 原 RTree 回归：18 组；168 个随机场景；140437 次变更、100188 次差分查询比较，全部通过。
- 空间索引回归：15 组，94160 条断言，全部通过。
- 原命令行所有查询示例：通过。
- UPM Sample 作为外部消费者，引用独立标准库程序集：通过新增、移动、球查询和删除流程。
- UPM 元数据、程序集边界与发行检查：通过。原 LGPL-2.1-or-later 许可保留。

执行命令为 dotnet run --project validation/Correctness -c Release、dotnet run --project validation/SpatialCorrectness -c Release、dotnet run --project validation/UnitySample -c Release；制作包运行 python3 scripts/package.py --output /tmp/com.novorizon.rtree-1.0.0-preview.2.tgz。

没有 Unity Editor 或设备；Unity 原生导入、IL2CPP 和项目侧坐标适配仍需实际验收。测试与示例的命中规则验证不属于目标设备性能结论。

目录整理复验：现有六种树各自放入独立目录，公共几何、接口与查询类型放入 Common。十九个 C# 文件及各自 .meta 字节均保持不变，新目录获得独立稳定 GUID。SDK 递归编译与旧项目显式引用路径同步更新，打包检查拒绝运行时源码重新散落到根目录。
