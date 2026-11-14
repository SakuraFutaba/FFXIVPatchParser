# 国服 V3 补丁

[知识库首页](README.md)

国服 V3 补丁以 ZIP 分发。ZIP 中的 `patch_delta_direct.dat` 将游戏文件路径映射到 VCDIFF 差分。安装器把差分应用到对应旧文件，生成目标 SqPack 容器和索引。EXD、TEX 和模型可从生成的容器中读取。

本文使用 2026-11-12 查询的目录和国服 7.58 样本。样本构建为 `2026.11.03.0000.0000`，补丁数据版本为 `0.0.0.31`。

## 追踪入口与版本身份

最初从启动器「保留补丁包」设置追到国服下载与安装代码。默认保留目录是 `%APPDATA%/XIVLauncherCN/patches/v3/<包名>`。启动器读出更新路径后，逐跳取得包文件清单。 [设置界面](https://github.com/AtmoOmen/FFXIVQuickLauncher/blob/65972f758b47615f321d4f3da509e3ade3b14489/src/XIVLauncher/Windows/SettingsWindow.xaml#L429) · [GameUpdater](https://github.com/AtmoOmen/FFXIVQuickLauncher/blob/65972f758b47615f321d4f3da509e3ade3b14489/src/XIVLauncher.GamePatchV3/Update/GameUpdater.cs) · [GamePatchMetadataClient](https://github.com/AtmoOmen/FFXIVQuickLauncher/blob/65972f758b47615f321d4f3da509e3ade3b14489/src/XIVLauncher.GamePatchV3/GamePatchMetadataClient.cs)

| 标识 | 用途 | 样本 |
| --- | --- | --- |
| 数据版本 | 补丁图的节点 | `0.0.0.31` |
| 游戏构建号 | 精确匹配客户端与历史数据 | `2026.11.03.0000.0000` |
| 展示版本 | 面向玩家的版本名称 | `7.58` |
| `versionView` | 关联构建号与展示版本 | `2026.11.03.0000.0000_7.58` |

官方目录 `ver2.dat` 的 `areas` 中，区域 `id == "0"` 的 `must` 决定更新目标。样本中 `min` 为 `0.0.0.19`，`must` 和 `max` 均为 `0.0.0.31`。启动器沿 `from → to` 图做广度优先搜索，以补丁跳数最少为目标。 [官方 ver2.dat](https://v3launcher.jijiagames.com/v3launcher/build/ver2data/100001900/8847/-1/ver2.dat)

```mermaid
flowchart LR
  A["ver2.dat 与构建映射"] --> B["每跳 FileList"]
  B --> C["一个或多个 ZIP"]
  C --> D["路径与三层文件哈希"]
  D --> E["匹配旧文件并应用 VCDIFF"]
  E --> F["目标 SqPack 与索引"]
  F --> G["内部资源读取与比较"]
```

## 分卷与校验

一次更新可能对应多个 ZIP。读取 `DeltaPathSubItem` 后要在所有分卷中定位条目，才能判断更新包是否齐全。 [补丁读取](../src/FFXIVPatchParser/PatchArchive.cs) · [补丁目录](../src/FFXIVPatchParser/PatchCatalog.cs)

| 依据 | 核对对象 |
| --- | --- |
| HTTP Range 探测 | 当次资源可读，服务端报告的总长度与清单一致 |
| ZIP 大小与外部 MD5 | 完整下载的 ZIP 与清单一致 |
| `DeltaPathSubItem` | 目标游戏路径对应的差分条目 |
| `DeltaMD5SubItem` | 从 ZIP 解压出的 VCDIFF 字节 |
| `OriginMD5SubItem` | VCDIFF 要求的旧文件 |
| `ResultMD5SubItem` | 差分输出或本地目标文件 |

2026-11-12 的目录包含 24 条更新、33 个包文件。全部通过可访问性和 Range 长度探测，清单大小合计 26,072,453,676 字节。本次完整下载的是 `.30 → .31`，大小 58,179,787 字节，MD5 为 `A4C947EAABA40778B23AEE08BDE6EED5`，校验通过。三个 Excel 差分通过 DeltaMD5，当前对应 Excel 文件均匹配 ResultMD5。

补丁目录缓存稳定的资源路径，每次下载时再生成签名 URL。下载先写 `.partial`，校验大小和 MD5 后改为正式文件名。已有完整文件通过校验时直接复用。 [下载实现](../src/FFXIVPatchParser/PatchCatalog.cs) · [上游 CDN 签名](https://github.com/AtmoOmen/FFXIVQuickLauncher/blob/65972f758b47615f321d4f3da509e3ade3b14489/src/XIVLauncher.GamePatchV3/CDNLinkSigner.cs)

## 历史补丁需要匹配的旧文件

早期检查的五个 ZIP 包含 `.19 → .20`、`.20 → .21`、`.23 → .25`、`.25 → .26`、`.27 → .29`，合计 1,860,145,418 字节。其中九个 Excel 差分合计 12,254,867 字节，9/9 通过差分 MD5。当前 Excel 文件却没有匹配这九条记录中的任何对应 OriginMD5 或 ResultMD5。

VCDIFF 用旧文件正向构造目标，被删除或覆盖的旧字节可能没有保存在差分里。要复现历史更新，先找齐匹配 `OriginMD5SubItem` 的容器及配套索引，再按版本顺序应用差分。 [VCDIFF 格式规范](https://www.rfc-editor.org/rfc/rfc3284)

上游安装器用 xdelta3 把差分写到临时文件，校验结果后替换旧文件。合并失败时，安装器可下载完整的最新目标文件来完成客户端更新。 [GamePatchInstaller](https://github.com/AtmoOmen/FFXIVQuickLauncher/blob/65972f758b47615f321d4f3da509e3ade3b14489/src/XIVLauncher.GamePatchV3/Update/GamePatchInstaller.cs) · [xdelta3 桥接](https://github.com/AtmoOmen/FFXIVQuickLauncher/blob/65972f758b47615f321d4f3da509e3ade3b14489/src/XIVLauncher.XdeltaShim/src/xdelta_bridge.c)

当前表格 Diff 使用按构建匹配的历史 CHS CSV 比较导出值。媒体 Diff 从补丁里读写入区间，在本地目标容器中筛选候选。完整的 VCDIFF 历史还原尚未实现。 [表格 Diff 与关联](table-diff.md) · [SqPack 与媒体](sqpack-media.md) · [复现实验与验证](verification.md)

## 容器级分段 Hex 查看

[分段读取器](../src/FFXIVPatchParser/PatchHexSession.cs)对每条 `PatchResource` 提供三种来源：解压并校验 DeltaMD5 后的 VCDIFF 原始字节、通过 ResultMD5 的目标文件字节，以及通过 OriginMD5 的旧文件字节。选中资源后即可查看差分和写入区间。程序随后自动校验当前游戏目录中的目标文件。新旧对照使用相同的文件偏移，文件末尾以外留空。

写入区间来自 ADD、RUN 与非原偏移 COPY 指令，相邻区间会合并。它表示差分如何构造目标，不保证区间内每个字节都改变：容器重排和复制也会命中。只有实际取得两侧经验证字节时，才能判断同一偏移是否不同。没有旧文件时旧字节保持未知；若清单 OriginMD5 是空文件摘要，则旧长度可直接确定为 0。 [指令读取](../src/FFXIVPatchParser/VcdiffRanges.cs)

此实现不反向推导被覆盖的旧值，也不复制或重建整个 DAT。VCDIFF 使用 `tmp/` 中随会话关闭删除的临时文件；文件摘要和解压采用 64 KiB 缓冲，每页最多读取 64 KiB。Hex 分析为单个窗口的指令和地址节合计设置 32 MiB 上限，并限制最多一百万个合并区间；无法解析的格式或超限样本仍保留原始 VCDIFF 查看。读取、摘要计算和区间解析支持取消。

2026-11-13 本项目实测：国服中文 7.58（构建 `2026.11.03.0000.0000`）对应 `.30 → .31` 补丁的 47 个资源全部通过 DeltaMD5 和区间解析，共得到 9,341 个合并写入区间；其中 Excel 的 DAT、index、index2 三个本地文件通过 ResultMD5，且成功读取首个写入区间。合成样本验证了旧新同偏移读取、EOF 截断、错误摘要拒绝、缺少旧版、空旧文件、目录选择、路径越界拒绝、取消和临时文件清理。旧版真实客户端尚未用于此功能验证，因此旧新对照的结论来自合成样本。

后续可按文件类型提供结构注释。对 `ffxiv_dx11.exe` 一类 PE 文件，可先标注文件偏移对应的节，只在代码节中尝试反汇编。对于数据节，保留十六进制和结构说明。
