# CPU 空间查询优化记录

优化前为 `b41cdc2`，优化后为本目录配套源码。2026-11-05 在同一环境顺序运行，保留原始 JSON 与逐工作负载 CSV。

## 变更与代价

- Dynamic AABB：包围盒与维护元数据分离；以包围面积选择局部交换，并将子树高度差限制在 4，避免纯高度平衡破坏空间聚集。叶子 fat bounds、句柄和 pair cache 语义保持兼容。构建和结构更新需要更多代价计算。
- Loose Octree：独立数组保存保守的子树内容边界，查询先读取内容边界，拒绝后无需读取单元元数据。在单元内移动时按需扩张祖先边界，不重插。每个已分配节点增加 48 字节；边界积累的空隙通过 Rebuild 收紧。
- Dynamic、Static BVH 和 Octree：范围完全包含子树时省略后续几何谓词。
- 共享几何：普通视锥平面使用有误差界的直接计算，极端数值仍回退到缩放算法；射线在查询入口校验一次，内部保留原有稳健 slab 算法。
- KD-tree 算法维持既有实现，本轮继续验证其正确性和性能。全部实现仍为托管数组。

## 配对方法

.NET 8.0.33 / Debian 13 / AMD EPYC 9V74，4 个可用逻辑处理器，workstation GC，`DOTNET_TieredCompilation=0`。100,000 对象，uniform/cluster/mixedsize/planar 四种分布，每类 256 次查询、5 轮中位数。构建、预热、GC、暴力 oracle 和统计采集在查询计时之外；结果写入同样的复用 List/QueryContext。每轮前后核对完整 ID、多重性、距离和命中参数。动态 pair cache 未启用。

查询矩阵每版 168 条测量；`compare.py` 确认相同操作数、输出 checksum 和命中数。普通几何的热查询全部 0 B/op；这不包含首次扩容与三角形疑难数值的精确回退。共享环境的批中位数不能作为单次 p95/p99，小幅耗时变化应结合节点访问量判断。

## 均匀分布，微秒/查询

| 索引 | 范围：前 → 后 | 最近 box 射线：前 → 后 | 视锥：前 → 后 |
| --- | ---: | ---: | ---: |
| RTree STR | 4.26 → 4.03 | 10.31 → 10.18 | 35.33 → 23.91 |
| Dynamic AABB | 45.58 → 27.89 | 75.57 → 34.26 | 100.44 → 57.32 |
| Static SAH BVH | 6.44 → 6.35 | 11.66 → 9.59 | 37.64 → 29.67 |
| Loose Octree | 28.64 → 10.58 | 51.87 → 8.56 | 101.86 → 30.45 |

均匀范围查询平均命中 37.19 个真实盒子；Dynamic 的访问节点从 662.99 降至 315.87，Octree 从 422.95 降至 126.58。Octree 最近射线从 148.80 降至 26.27 个访问节点。节点访问量的下降说明收益包含裁剪改善；二叉树与多叉树的单节点工作量不同，不能仅凭节点数直接比较速度。

## 当前排序与分布差异

范围查询四种分布均为 RTree STR → Static SAH BVH → Loose Octree → Dynamic AABB。下表列出最近 box 射线中位数；Static 和 Octree 已进入相近的性能区间，差距小的项目不应视为稳定名次。

| 分布 | RTree STR | Static SAH BVH | Loose Octree | Dynamic AABB |
| --- | ---: | ---: | ---: | ---: |
| uniform | 10.18 | 9.59 | 8.56 | 34.26 |
| cluster | 3.54 | 2.18 | 2.24 | 4.47 |
| mixedsize | 4.14 | 3.22 | 3.97 | 9.81 |
| planar | 2.32 | 1.89 | 2.25 | 4.84 |

这是当前实现和参数在这些查询分布中的比较。box 射线、精确 triangle 射线和点 kNN 分组测量，不混为同一排名。

## 稳态移动与取舍

每个采样交替正向/反向移动 1000 对象，共 2000 次逻辑 Move。下表为微秒/对象：

| 分布 | RTree：前 → 后 | Dynamic AABB：前 → 后 | Loose Octree：前 → 后 |
| --- | ---: | ---: | ---: |
| uniform | 3.252 → 3.206 | 1.724 → 2.199 | 0.582 → 0.665 |
| cluster | 3.303 → 3.226 | 1.721 → 2.207 | 0.783 → 0.884 |
| mixedsize | 3.142 → 3.419 | 1.652 → 2.264 | 0.546 → 0.514 |
| planar | 3.199 → 3.352 | 1.808 → 2.312 | 0.825 → 0.882 |

Dynamic 在该移动混合中约慢 28–37%，换取更好的查询裁剪；其中 50% 的 Move 结构重插，全部热 Move 为 0 B。Octree 的变化依分布不同，均匀分布约慢 14%；热 Move 同样 0 B。RTree 每个逻辑 Move 为 Delete + Add，仍有约 12–36 B/op 的内部增长分配。

四种分布的稳态移动排序均为 Loose Octree → Dynamic AABB → RTree。本轮没有测 pair cache 启用后的移动吞吐，也不把冷更新中的首次数组增长当作稳态成本。

## 正确性

- 原 RTree：18 组回归、168 个随机场景，140,437 次变更、100,188 次查询对照和 25,934,298 次节点不变量检查通过。
- SpatialCorrectness：14 组、88,114 条断言通过，增加有序/倒序/嵌套动态对象、单元内移动越过旧内容边界、完整子树包含、节点复用、平面缩放及抵消回归。并发与普通热查询零分配检查通过。
- 独立结构审查：2,663,958 条断言通过，核对父子指针、无环、精确高度、限制高度差和父边界并集；额外动态缓存/随机移动探针 1,468,114 条断言通过。
- 全库 C# 5 编译通过；实际测试运行时为 .NET 8，未验证 .NET Framework 4.5 运行时。

## 复测

从基线提交导出源文件，然后分别运行，切换源码时重新构建：

```sh
mkdir -p /tmp/rtree-before
git archive b41cdc2bc55b2a7e5d5baa13d17c02f130606a80 RTree | tar -x -C /tmp/rtree-before
DOTNET_TieredCompilation=0 dotnet run \
  --project validation/SpatialPerformance/SpatialPerformance.csproj -c Release \
  -p:SourceDirectory=/tmp/rtree-before/RTree -- \
  --sizes 100000 --profiles uniform,cluster,mixedsize,planar \
  --queries 256 --rounds 5 --updates 200 --k 16 \
  --workloads range,sphere,frustum,box-ray,triangle-ray,kd,update \
  --json /tmp/before.json
```

对优化后源码省略 `-p:SourceDirectory`，输出 `/tmp/after.json`，再运行：

```sh
python3 validation/SpatialPerformance/compare.py /tmp/before.json /tmp/after.json /tmp/comparison.csv
```

稳态移动用 `--workloads steady-update --updates 1000`，其余规模、查询数和轮数相同。两次完整往返预热后，在同一索引交替移动 1000 个对象，分别计时、计时外校验；RTree 的一个逻辑 Move 为 Delete + Add 两次 API 调用。

原始数据：

- [优化前查询与冷更新](results-before-spatial-optimization.json)
- [优化后查询与冷更新](results-after-spatial-optimization.json)
- [查询与冷更新对比](results-spatial-comparison.csv)
- [优化前稳态移动](results-before-spatial-steady.json)
- [优化后稳态移动](results-after-spatial-steady.json)
- [稳态移动对比](results-spatial-steady-comparison.csv)
