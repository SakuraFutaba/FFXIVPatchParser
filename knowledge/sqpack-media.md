# SqPack 与媒体资源

[知识库首页](README.md)

本项目用 Lumina 7.7.2 从本地 SqPack 读取 TEX、MDL 和 SCD。选择补丁后，可以用 VCDIFF 的写入区间筛选目标版本中的媒体资源。本文使用国服 7.58 构建 `2026.11.03.0000.0000` 和 `0.0.0.30 → 0.0.0.31` 补丁复现。

## 容器类别与资源身份

`ffxiv`、`ex1` 等目录是 SqPack 仓库。容器文件名前两位十六进制数表示类别，例如 `0a` 是 Excel，`06` 是 UI，`03` 是过场。角色、场景和过场容器里可同时存放多种资源。 [SqPack 格式文档](https://xiv.dev/data-files/sqpack)

索引保存路径哈希和指向 `.dat` 的位置信息。项目可用已知路径计算哈希并查询索引。未知路径的资源可以通过合成地址打开，其中的偏移随容器重排变化。 [本地资源读取](../src/FFXIVPatchParser/LocalGameSource.cs)

| 表示方式 | 意义 |
| --- | --- |
| `ui/icon/000000/000001.tex` | 可按游戏内逻辑路径查询的资源 |
| `sqpack:<container>@<offset>#<hash>` | 项目生成的定位地址，保留容器、偏移和索引哈希 |
| `.datN` | 存储资源块的数据容器 |
| `.index` / `.index2` | 路径查找所需的索引文件 |

## 图标编号如何变成图片

图标查找按千位分目录，例如编号 1234 对应 `ui/icon/001000/001234.tex`。本地图标目录还会检查 `_hr1.tex` 高分辨率后缀和 `chs/` 本地化目录。用这些已知路径补充索引中的名字后，其他 UI 纹理仍可通过合成地址打开。 [图标和 UI 索引](../src/FFXIVPatchParser/LocalGameSource.cs)

本地图片页进入后自动按当前索引匹配已知 `.tex` 和 `.atex` 路径，覆盖 UI、角色、场景与特效等类别；目录默认展开 `ui/icon`。音频页同样自动列出已知 `.scd` 路径，默认展开 `music`。名称来自文件系统已有路径清单及当前构建的已验证缓存，目录筛选不逐个读取未知资源的 DAT 文件头。未命名资源仍在 SqPack 文件页按地址访问。目录后台按需加载，切换游戏源时重建，导入路径后再次进入页面会检查目录版本。 [媒体目录](../src/FFXIVPatchParser/LocalGameSource.LocalMediaCatalog.cs) · [图片目录加载](../src/FFXIVPatchParser.Desktop/SqPackBrowser.Images.cs)

历史图标专用目录在国服 7.58 读出 140,859 条路径，约 0.63 至 0.94 秒；这些数字仅描述旧版图标目录，不代表当前全类别目录的数量或耗时。

「图标浏览器」按可用 ID 去重。参考 Dalamud 的游戏图标查找顺序，国服浏览器先试无语言高清，再按顺序试简中高清、简中普通、无语言普通。列表只解码可见区域和少量预取项。缩略图保持长宽比，悬停显示 ID 和原图，单击进入图片页。国服简体构建 `2026.11.03.0000.0000` 的 WPF 探针从 140,859 条变体路径合并出 70,441 个 ID；测试用 128 × 64 图片缩成 64 × 32。 [Dalamud 图标浏览源码](https://github.com/goatcorp/Dalamud/blob/43b34118bcf7213e575ec39517d033191b1acf17/Dalamud/Interface/Internal/Windows/Data/Widgets/IconBrowserWidget.cs) · [Dalamud 图标查找顺序](https://github.com/goatcorp/Dalamud/blob/43b34118bcf7213e575ec39517d033191b1acf17/Dalamud/Interface/Textures/Internal/TextureManager.GamePath.cs)

国服简体 7.58、构建 `2026.11.03.0000.0000`，使用已有路径及已验证缓存的本项目实测：图片 634,165 条（含场景、角色、UI、特效），模型 186,170 条，音频 110,097 条。独立进程按图片→模型→音频顺序调用目录 API，耗时分别为 16.00、1.38、0.51 秒，再次读取图片约 2.91 秒。首次图片读取包含共享索引和名称缓存初始化；后续两项复用了缓存。上述数字仅覆盖该缓存样本，未计入 WPF 目录树构造与绘制。检查模型与音频完整扫描缓存仍为空，预先取消的调用抛出取消异常；此验证不代表未知资源已有完整类型分类。

TEX 解码后可按原始尺寸导出 PNG，保留透明通道。预览的缩放和棋盘背景只影响显示。离屏验证使用四个 BGRA 像素，往返 PNG 后 Alpha 0、128、255 都保持原值。 [PNG 导出与预览](../src/FFXIVPatchParser.Desktop/TexturePreview.xaml.cs)

### ATEX 特效贴图

在国服简体 `2026.11.03.0000.0000` 的文件系统中打开 `vfx/action/ab_2ast003/texture/app_grd_1h.atex`，也可在图片页直接输入该路径。这个样本在 SqPack 中用 Standard（类型 2）封装，解包后的纹理头与 TEX 结构兼容。实测首级纹理是 64 × 128 的 BC1（DXT1），格式值 `0x3420`，首级数据偏移 `0x50`。按路径和索引地址读取的像素一致，导出 PNG 后重新读取也得到相同的 BGRA 像素。 [Lumina 纹理头](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Files/TexFile.cs) · [本地纹理解包](../src/FFXIVPatchParser/LocalGameSource.cs)

本地图片页自动从已补全的路径中加入 ATEX。Patch 的图片页按写入区间筛选有路径的 ATEX，使用样本的实际容器位置构造写入区间后，自动和完整扫描均命中。真实 `0.0.0.30 → 0.0.0.31` 补丁样本的两次扫描都得到 15 张 UI TEX，ATEX 命中数为 0。 [补丁媒体索引筛选](../src/FFXIVPatchParser/LocalGameSource.KnownMedia.cs)

## Channeling 表与连线特效

`Channeling.File` 保存 AVFX 的文件名主体，完整的游戏内路径是 `vfx/channeling/eff/{File}.avfx`。这类资源绘制游戏对象之间的连线特效（tether）。Pictomancy 的 `AddChanneling` 使用该路径和起止对象，FFXIVClientStructs 把 `Tether.Id` 标为 `Channeling` 的行号，并记录目标对象 ID。 [Pictomancy 的路径与连线实现](https://github.com/sourpuh/ffxiv_pictomancy/blob/master/Pictomancy/VfxRenderer.cs#L15-L68) · [FFXIVClientStructs 的 Tether 结构](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/Character/VfxContainer.cs#L23-L29)

国服简体中文客户端 7.58 构建 `2026.11.03.0000.0000` 实测，`Channeling` 是无语言表。界面把行号放在第一列，因此第二列 `File` 对应 EXH 中索引为 0 的字符串字段。本次读取 `exd/Channeling.exh` 和 `exd/Channeling_0.exd`，为 432 行中 431 个非空值补齐路径，去重后用 Lumina 7.7.2 逐一从本地 SqPack 解包。404 个文件全部读出，且均以 AVFX 的 `XFVA` 文件头开头。 [本地表读取](../src/FFXIVPatchParser/LocalGameSource.cs) · [Lumina 按索引读取文件](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Category.cs) · [AVFX 格式](https://xiv.dev/game-data/visual-effects/avfx-files)

三个样本均位于该构建的 `ffxiv/080000.win32.dat0`，偏移为十六进制。实测范围是表值、路径和文件头，画面及具体战斗机制需在游戏内另行观察。

| 行号 | 游戏内逻辑路径 | 容器偏移 | 解包后字节数 |
| --- | --- | --- | --- |
| 4 | `vfx/channeling/eff/chn_wind001f.avfx` | `0xE0B5980` | 10,796 |
| 1、192 | `vfx/channeling/eff/chn_dark001f.avfx` | `0xE0B7580` | 10,068 |
| 13 | `vfx/channeling/eff/chn_tankmog1f.avfx` | `0xE0A9A80` | 15,432 |

## 模型依赖与 SCD 音轨

MDL 提供网格，外观和动作还依赖 IMC、材质、纹理、骨架及 PAP。真实客户端的身体、装备、怪物和头发四个 MDL 样本通过解码和离屏渲染检查。材质近似和动作采样的覆盖范围另有实测。 [模型、材质与动作](models-animation.md)

SCD 是音频容器，可能含多条音轨。程序按轨道读取编码，提取 Ogg Vorbis 或 MP3，或为 MS ADPCM 构造 WAV。实测样本覆盖 Ogg Vorbis 和 MS ADPCM。 [音轨读取](../src/FFXIVPatchParser/LocalGameSource.Audio.cs) · [Lumina ScdFile](https://github.com/NotAdam/Lumina/blob/8802d66a3cb3032be2fcf998bb10e864221bdb13/src/Lumina/Data/Files/ScdFile.cs)

本地音频页自动按已知 `.scd` 路径建立目录，并保留 BGM 曲名显示。选择目录树中的文件后才读取音轨。历史 BGM 专用目录在国服 7.58 简体中文客户端包含 1,287 条路径；旧版手动扫描 `cut`、`sound`、`music` 三类容器得到 102,427 条去重路径，其中包括匿名地址。这些数量不代表当前已知路径目录的覆盖范围。 [音频目录界面](../src/FFXIVPatchParser.Desktop/AudioPreview.xaml.cs)

音频合成地址按容器、偏移和索引哈希直接校验后读取，不需要先扫描容器。此前匿名地址依赖扫描缓存；若同一哈希已解析成已知路径，扫描缓存中只保留已知路径，匿名地址便无法打开。实测用标题曲 `music/ex2/bgm_ex2_system_title.scd` 的合成地址在新建数据源中读取，内容与逻辑路径完全一致，且没有填充容器扫描缓存；错误偏移和空哈希地址被拒绝。 [音频地址解析](../src/FFXIVPatchParser/LocalGameSource.Audio.cs)

Windows 解码失败时，桌面端可调用已有 FFmpeg 转 PCM。转码保留完整时长和采样率，切换歌曲或退出可取消转码。国服 7.58 实测 `music/ex4/bgm_ex4_wks_01.scd` 转码后为 318.898 秒。

Vorbis 的 `LOOPSTART`、`LOOPEND` 来自 Ogg 注释，单位是每声道的采样帧。SCD 音轨头中的 Ogg 循环值是字节位置。PCM 16-bit 和 MS ADPCM 使用 SCD 音轨头的循环字节位置，按声道对齐或 ADPCM 块换算为采样帧。单曲循环先播放前奏，随后反复缓冲 `[LOOPSTART, LOOPEND)` 内的 PCM。PCM 可直接使用循环队列，压缩音轨需要 PATH 中的 FFmpeg；缺少循环点或 FFmpeg 时使用整轨重复。 [vgmstream 的 SCD 实现](https://github.com/vgmstream/vgmstream/blob/master/src/meta/sqex_scd.c)

国服构建 `2026.11.03.0000.0000` 的 `music/ex2/bgm_ex2_system_title.scd` 有 44,100 Hz 采样率，循环点为 1,065,385 至 2,742,057。桌面验证中，从循环终点前定位后，播放位置回到循环起点附近并继续推进。
### 全局音乐播放与短音轨重播

2026-11-16，国服简中 `2026.11.03.0000.0000` 样本的 `sqpack:ffxiv/070000.win32.dat0@30a8cd00#c2d4378e002f5cd4.scd` 含 14,230 字节的 MS ADPCM 音轨。WPF 使用 FFmpeg 回退为 PCM，时长约 0.585 秒。音轨自然结束后，下一次播放用新的 `MediaPlayer` 打开同一缓存。暂停和定位继续使用当前实例。离屏回归覆盖短音效和约 62.178 秒的 Ogg Vorbis 标题曲，包括结束后重播、暂停定位、停止后重播、来源切换和单曲循环。旧版本的二次播放失败在同机离屏测试中未能稳定复现，具体触发条件待确认。 [播放生命周期](../src/FFXIVPatchParser.Desktop/AudioPreview.xaml.cs)

底部播放栏常驻，离开来源页后继续播放。来源页和底栏共用播放状态和进度。音频目录单击预览，双击文件或曲名可立即播放。列表图标在右侧打开队列、曲库和收藏浮窗，按 Esc 或点击浮窗外可关闭。随机播放在一轮内避免重复，上一首沿实际播放顺序返回。自然播放到队列末尾后，列表循环从头继续。单曲循环复用当前音轨。收藏按路径和音轨保存在 `tmp/music/favorites.json`。 [全局播放器](../src/FFXIVPatchParser.Desktop/GlobalMusicPlayer.xaml) · [播放顺序](../src/FFXIVPatchParser.Desktop/MusicPlaybackOrder.cs)

### OrchestrionPlugin 的曲名和文件路径

上游 `SongList.LoadMetadataSheet` 先取得游戏表 `BGM`，按 CSV 的 `ID` 找到对应 `BGM.RowId`，从该行 `File` 取 SCD 路径。`xiv_bgm_metadata.csv` 提供编号和时长。`LoadLangSheet` 再按相同 `ID` 从 `xiv_bgm_zh.csv` 等语言表取曲名、别名和场景。上游通常从社区表更新，失败时读取本地 CSV。 [OrchestrionPlugin 关联实现](https://github.com/perchbirdd/OrchestrionPlugin/blob/e1031563f03d0bd7ae833326e0ac9f05aa67a9bb/Orchestrion/Persistence/SongList.cs#L74-L135) · [中文曲名表](https://github.com/perchbirdd/OrchestrionPlugin/blob/e1031563f03d0bd7ae833326e0ac9f05aa67a9bb/Orchestrion/Data/xiv_bgm_zh.csv)

本项目用同一编号关联本地 `BGM` 与离线曲名资料。实测国服 7.58 曲库含 1,287 个路径。比如 `BGM #428` 的 `File` 是 `music/ex2/bgm_ex2_system_title.scd`，中文资料记为「红莲序曲*」。在音频目录或本地曲库输入「红莲序曲」、`Prelude - Long March Home` 或 `Stormblood title screen` 都能找到。目录优先显示中文曲名，鼠标悬停可看实际路径。同一文件关联多个 BGM ID 时，这些编号和名称都参与搜索。标题和场景来自社区标注，进度条使用本地音轨的实际解码时长。 [本地 BGM 读取](../src/FFXIVPatchParser/LocalGameSource.Audio.cs) · [离线关联](../src/FFXIVPatchParser.Desktop/MusicMetadata.cs) · [MIT 许可证](../src/FFXIVPatchParser.Desktop/Data/Orchestrion-LICENSE.txt)

2026-11-18 对照 OrchestrionPlugin 仓库发布的中文、日文和英文 CSV，与项目此前缓存的 CSV 逐条一致。用户提供的 Google 表与插件的在线来源相同，实时表本次连接超时。新版优先用本地 `Orchestrion.RowId → OrchestrionPath.RowId → File`，再用 BGM ID 对应的社区中文、日文、英文。两个表各自的行号空间独立，跨表按经过验证的 SCD 路径合并。国服 `Orchestrion #1` 的名称「雪风」可直接匹配 `music/ffxiv/orchestrion/bgm_orch_001.scd`。 [社区原始表](https://docs.google.com/spreadsheets/d/1s-xJjxqp6pwS7oewNy1aOQnr3gaJbewvIBbyYchZ6No/edit?gid=2053080530)


## 写入候选怎样筛选

手动完整扫描先按 ResultMD5 校验本地目标容器，以及补丁涉及的相关索引文件。然后校验差分 MD5 并读取 VCDIFF 指令，把目标写入区间与索引资源范围求交。资源范围用同一 `.dat` 中下一项的偏移近似，因此可能含填充字节。命中项直接从本地目标版本预览。 [候选扫描](../src/FFXIVPatchParser/PatchImageScanner.cs)

自动扫描调用 `ScanKnownPaths`，只返回可解析逻辑路径的图片、模型和音频候选。它先验证相关目标 `.index`（补丁含 `.index2` 时也验证）及差分 MD5，再用目标索引范围与 VCDIFF 写入区间求交。缺少目标 `.index` 摘要的容器会跳过，并在结果摘要中报告数量；索引摘要不匹配仍会报错。该模式不计算整个 DAT 的 MD5，也不逐个探测资源头，因此明确标记“本地数据容器尚未完整校验”。自动候选的预览读取本地资源，不能据此声称本地 DAT 与目标版本一致。 [自动候选入口](../src/FFXIVPatchParser/PatchImageScanner.cs)

路径来自已知图标、BGM 表、导入模型目录及模型命名模式。模型命名只针对写入区间命中的哈希，装备编号覆盖到 9999；不会因默认目录只覆盖较低编号而漏掉 `e6258`。路径还需匹配仓库、类别、DAT 和偏移；跨分片指向不同地址的哈希保留给手动扫描。相同偏移的多个索引哈希保留供路径解析。 [已知路径与索引求交](../src/FFXIVPatchParser/LocalGameSource.KnownMedia.cs) · [模型路径生成](../src/FFXIVPatchParser/LocalGameSource.ModelCatalog.cs)

2026-11-13 本项目实测：国服简体中文构建 `2026.11.03.0000.0000`，安装器补丁 `0.0.0.30 → 0.0.0.31`。自动扫描图片得到 11 个候选，用时 1.41 秒；模型得到 7 个候选，用时 2.79 秒，包含 `e6258` 的四个装备模型，并跳过 14 个缺少目标 index 摘要的容器；音频得到 0 个已知路径候选，用时 0.20 秒，跳过 7 个容器。计时从数据源初始化后开始，使用系统文件缓存。完整扫描仍找到 8 个模型和 1 个音频候选，分别用时 96.11 秒和 24.29 秒；完整图片扫描找到 15 个候选，用时 15.27 秒；自动图片与模型候选均包含在完整扫描结果中。缺失目标 index 摘要跳过、错误摘要拒绝和取消操作也通过验证。差异包含未知路径和未验证索引容器，不能将自动目录当作完整变更清单。

完整扫描的图片和音频也先按容器、写入范围过滤，再读资源头或解压 SCD 签名。受限范围的扫描结果不写入完整目录缓存，避免后续浏览遗漏未命中范围的资源。

ADD、RUN 和 COPY 写出的区间都参与筛选。只有从旧文件复制到相同绝对位置的 COPY 被排除。这样可把新增、更新或容器重排涉及的目标资源找出来。 [区间解析](../src/FFXIVPatchParser/VcdiffRanges.cs)

区间解析使用默认指令表，检查窗口、指令、地址和目标长度。自定义码表和二级压缩会被拒绝。完整还原还需要生成目标文件并校验生成内容。

| 本地验证 | 观测结果 | 范围边界 |
| --- | --- | --- |
| UI 图片候选扫描 | 15 个纹理候选 | UI 类别的本地目标资源 |
| 模型，`ffxiv/040000.win32.dat6` | 1 个候选，读出 1 个网格 | 单容器验证 |
| 模型，`ex5/020508.win32.dat0` | 0 个候选 | 单容器验证 |
| 图片，`ffxiv/060000.win32.dat3` | 0 个候选 | 单容器验证 |
| 音频，`ex3/030300.win32.dat1` | 0 个候选 | 单容器验证 |
| 音频候选扫描 | 1 个候选，读取为 MS ADPCM，导出 WAV 1,048,410 字节 | 本次音频范围内的目标资源 |
| 全部模型相关容器 | 8 个写入候选，抽查 2 个模型可解码 | 125.68 秒，其中索引筛选约 1.80 秒 |

这次唯一的音频候选来自 `cut` 过场类别。按文件类型筛选时也要检查其他可能存放该类型的容器。场景和角色容器里的纹理同理。

要做资源级 Hex Diff 或找出删除项，需要旧版与新版的配套索引和容器。先按资源身份对齐，再比较解压后的字节。若要判断画面或声音是否变化，还需处理纹理编码、模型依赖和音轨内容。 [国服 V3 补丁](cn-patches.md) · [复现实验与验证](verification.md)

## 待研究

- 提高图片和模型的路径覆盖率。记录只知道索引哈希的资源，查找可用于核对路径的游戏内引用或路径目录。
- 逐步覆盖所有图片和模型的解码。分别统计已读取的资源、能预览的资源和失败样本，找出缺少支持的 TEX 编码、MDL 布局及材质依赖。
