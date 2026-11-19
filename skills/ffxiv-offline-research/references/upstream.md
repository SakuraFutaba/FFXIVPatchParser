# 按研究问题选择上游项目

从表中选择与问题最接近的入口。沿调用点查实际读取、选择和失败回退，再对照本地构建。插件里通过已运行客户端得到的数据，离线工具需要另找资源入口或明确留为运行时输入。

| 要核对的内容 | 源码入口与本地验证 |
| --- | --- |
| SqPack 索引、碰撞、按需预览 | [Lumina](https://github.com/NotAdam/Lumina) 核对 index、index2 与文件读取；[Alpha](https://github.com/NotNite/Alpha) 和 [FFXIV Explorer 文件浏览器](https://github.com/goaaats/ffxiv-explorer-fork/blob/develop/src/main/java/com/fragmenterworks/ffxivextract/gui/FileManagerWindow.java) 参考目录、未知名称、按类型预览和 Hex 回退；[ResLogger2](https://rl2.perchbird.dev/Downloads) 提供路径候选。用同一定位符核对实际地址、解包字节和文件类型。 |
| 补丁链、EXD 和地图对象 | [Thaliak](https://github.com/CrystallineTools/Thaliak) 查版本和补丁链；[Sapphire](https://github.com/SapphireServer/Sapphire/tree/master/src) 查旧版本的数据读取、事件对象和区域；[SaintCoinach](https://github.com/xivapi/SaintCoinach) 查表格定义及关联格式。用目标版本的容器、校验值和 EXH 复核，区分旧客户端行为与当前定义。 |
| 地图、天气和战斗事件 | [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs/tree/main/ida)、[国服版](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs)、[BossMod 旧版同步入口](https://github.com/awgil/ffxiv_bossmod/blob/ee5b04a4ab63fb4313d29dbbb671f029f74752b7/BossMod/Framework/WorldStateGameSync.cs)与[战斗模块](https://github.com/awgil/ffxiv_bossmod/tree/master/BossMod/Modules)。从 `OnMapEffect`、`OID` 和事件条件查具体副本，再用国服表和 LVB/SGB 确认槽位、NPC 对应和绑定。机制识别代码不能替代游戏渲染逻辑。[Physis ENVB](https://github.com/redstrate/Physis/blob/main/src/envb.rs) 和 [Ktisis](https://github.com/ktisis-tools/Ktisis) 用于查环境数据、时间和天气控制。 |
| AVFX、TMB 和着色器 | [VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor)、[AVFXTools](https://github.com/0ceal0t/AVFXTools)、[Penumbra.GameData](https://github.com/Ottermandias/Penumbra.GameData)。对照区块长度、列表是累计还是局部、纹理引用和命令字段。着色器从变体和通道选择追到常量、阶段输入和渲染状态，用本地资源与离屏读回核对。 |
| 装备、骨架与动作 | [Anamnesis 动作说明](https://github.com/imchillin/Anamnesis/wiki/Animation-FAQ)和源码用于查 ActionTimeline ID、角色和武器姿态；[Penumbra](https://github.com/xivdev/Penumbra) 用于查部件、材质变体和外观替换；[Godbert 的怪物视图](https://github.com/xivapi/SaintCoinach/blob/master/Godbert/ViewModels/MonstersViewModel.cs)用于查共享 PAP 的 Idle 槽、Havok 采样和 FBX 导出。用真实 SKLB、PAP 和 MDL 核对骨名、顶点位移和动作时长。 |
| 图标、ULD 与运行时 UI | [Dalamud 数据窗口](https://github.com/goatcorp/Dalamud/tree/master/Dalamud/Interface/Internal/Windows/Data/Widgets) 用于查图标、ULD 资源、部件、主题和时间线；[Lumina UldFile](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Files/UldFile.cs)用于解析拓扑、切片和关键帧；[AtkTimeline](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Component/GUI/AtkTimeline.cs) 用于查运行时标签和父子时间。分别核对资源初始状态和游戏内指定状态。 |
| SCD 循环点和曲名 | [vgmstream](https://github.com/vgmstream/vgmstream) 用于查 SCD 的编码分支和循环点换算。对照 [OrchestrionPlugin 的 `SongList`](https://github.com/perchbirdd/OrchestrionPlugin/blob/e1031563f03d0bd7ae833326e0ac9f05aa67a9bb/Orchestrion/Persistence/SongList.cs) 从 `BGM.RowId` 到 `File` 再关联 CSV 的 `ID`；本地 Orchestrion 行与 BGM ID 用实际 SCD 路径交汇。曲名优先级为游戏表直接关联、社区中文、日文、英文。 |

## 桌面交互参考

- 文件树、`Ctrl+P`、Hex 偏移浮层和清空搜索后的定位行为参考 [VS Code](https://code.visualstudio.com/docs/editing/getting-started/userinterface)。目录固定重点看 [StickyScrollController](https://github.com/microsoft/vscode/blob/main/src/vs/base/browser/ui/tree/abstractTree.ts)，按可见节点寻找父级，点击固定项应作用于同一目录。图标参考有授权说明的开源图标集，例如 [Codicons](https://github.com/microsoft/vscode-codicons)，复用项目的图标资源和按钮样式。
- 播放器从 Spotify、网易云音乐与用户参考图学习布局，重点检查正在播放、暂停、静音、循环、收藏和悬停各状态；队列独立悬浮。按[项目播放器](../../../src/FFXIVPatchParser.Desktop/GlobalMusicPlayer.xaml)保持曲目、时间和进度一致，并用实际曲目检查衔接。
- 浏览图片和图标时用虚拟列表或按需加载缩略图。长边等比缩放，悬停展示编号和原图，点击定位图片页。验收除初始画面外，还要滚到列表末端、清空筛选、打开浮层，比较页面内容有没有被滚动条或临时提示挤动。

可复现的实验和构建范围保存在 [项目知识库](../../../knowledge/README.md)。
