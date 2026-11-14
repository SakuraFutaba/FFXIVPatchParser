# 复现实验与验证

[知识库首页](README.md)

复现解包问题时先记录游戏构建和语言，再选一张表或一个资源做验证。一次实验回答一个问题，例如「能否读取真实子行编号」或「Idle 是否真的改变了网格」。

## 固定实验输入

| 记录项 | 示例 | 用来确认 |
| --- | --- | --- |
| 游戏构建和语言 | `2026.11.03.0000.0000`、简体中文 | 读取的 SqPack、字段定义和参考数据来自哪一版 |
| 补丁数据版本及分卷 | `0.0.0.30 → 0.0.0.31` | ZIP 是否属于同一次更新，所需分卷是否齐备 |
| 历史数据提交 | CHS 7.58 的 `9e2c05c` | CSV 内容和列定义能否重复取得 |
| 样本身份 | `Action #3`、`MapMarker 4.50`、`m0001b0001` | 每次检查是否指向同一条记录或同一个模型 |
| 读取方式和缓存 | 本地 EXD、首次下载 CSV、读取已缓存 CSV | 耗时来自磁盘读取、解码、查表还是网络 |

## 从小样本开始

### 核对补丁清单

用 PowerShell 7 在项目根目录执行。`FFXIV_GAME_PATH` 指向含 `game` 子目录的游戏安装目录。以下示例检查一个 ZIP。脚本读取 ZIP 映射并校验 Excel 差分的 MD5，同时把本地 Excel 文件的 MD5 与补丁记录的旧值、目标值对照。遇到跨分卷映射时，用桌面端同时打开所需 ZIP。 [补丁检查脚本](../scripts/Inspect-CnPatch.ps1)

```powershell
./scripts/Inspect-CnPatch.ps1 `
  -PatchRoot ./tmp/patches/patch-0.0.0.30-0.0.0.31 `
  -GameRoot $env:FFXIV_GAME_PATH `
  -OutputPath ./tmp/research/patch-inspection.json
```

先查看 `.dat` 与 `.index` 属于哪个仓库和类别。要判断具体图片或模型是否值得检查，再用[目标资源候选](sqpack-media.md)缩小范围。清单中的 47 个变化文件、历史 CSV 中的 91 张变化表和 15 个写入纹理候选分别来自三种检查。它们在同一次 `0.0.0.30 → 0.0.0.31` 实验中出现，数量各有含义。

### 导出一组有代表性的表

下面的快照同时包含普通行、子行、文本和数值。每次导出选择新的空目录，便于对照清单和原始值。`snapshot` 接受安装目录、`game` 目录或 `sqpack` 目录。[导出入口](../src/FFXIVPatchParser/Program.cs) · [快照校验脚本](../scripts/Verify-Snapshot.py)

```powershell
./scripts/Invoke-Dotnet.ps1 run --project src/FFXIVPatchParser -- `
  snapshot --game $env:FFXIV_GAME_PATH --sheets 'Action,Item,GilShopItem,Stain' `
  --output tmp/snapshots/verification

python ./scripts/Verify-Snapshot.py tmp/snapshots/verification `
  --output tmp/validation/verification.json
```

在国服 7.58 上复跑以上命令，共导出 4 张表、120,588 条记录，其中 `GilShopItem` 为子行表。快照校验通过，导出前后输入文件的指纹一致。

需要覆盖特殊情况时，加一张空表和 `CharaMakeType` 这样的宽表。空表用 EXH 核对列定义。国服 7.58 的 `CharaMakeType` 有 3,536 列，可通过横向滚动检查字段定义匹配后是否保留完整列序。 [Excel 表格与 Raw 快照](excel.md)

### 检查资源预览

| 资源 | 可以验证的现象 | 为什么这样测 |
| --- | --- | --- |
| 图标 PNG | 比较导出前后的尺寸和 BGRA 像素，样本包含透明、半透明和不透明像素。 | 视口缩放不会改变导出的原始像素。 |
| 模型和材质 | 打开典雅方格阳伞的 `m6001b0003`，记录 IMC 子集和解析出的 `MaterialId`。 | 模型 ID、身体编号和材质版本各自可核对。 |
| 怪物 Idle | 从 `resident/monster.pap` 取同一动作槽的两帧，比较蒙皮后的顶点位置。 | 能读取槽名和时长只说明解析了元数据。顶点变化才证明动作驱动了预览。 |
| SCD 音频 | 提取一条 Ogg 和一条 ADPCM 音轨，用 `ffprobe` 检查编码、采样率和声道，再检查播放器能否打开。 | 提取成功与 Windows 能否解码是两个可分别定位的问题。 |

## 保存结果

把原始日志、下载和实验输出放到 `tmp/`。知识库记录可公开的输入版本、最小样本、观测结果与源码位置。下次修改实现时，用同一个样本复验。需要比较速度时，分别记录首次读取、磁盘缓存命中和内存缓存命中的耗时。

日常改动可运行内置自检，覆盖行号与子行编号、64 位整数、浮点位模式、SeString 原始字节、CHS、空行和输出目录边界。[自检源码](../src/FFXIVPatchParser/SnapshotTests.cs)

```powershell
./scripts/Invoke-Dotnet.ps1 run --project src/FFXIVPatchParser -- self-test
```
