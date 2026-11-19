# 地图目录与场景

「SqPack → 地图」从已安装的客户端读取区域目录、静态 3D 场景和二维地图纹理。区域目录只读取 Excel 表与 SqPack 内存索引，选中区域后才打开布局、模型或纹理。

## 区域目录的关联

Godbert 用 `TerritoryType.Bg` 列出区域。Hyperborea 用同一字段检查 `bg/{Bg}.lvb`，并复用 ECommons 的地域选择表。本项目用这些数据在本地建立搜索目录。 [Godbert 区域视图](https://github.com/xivapi/SaintCoinach/blob/master/Godbert/ViewModels/TerritoryViewModel.cs) · [Hyperborea 路径检查](https://github.com/kawaii/Hyperborea/blob/main/Hyperborea/Utils.cs#L221) · [ECommons 区域选择器](https://github.com/NightmareXIV/ECommons/blob/e6be8f0fd7786a9e1781db2e71cb5b9146f04980/ECommons/ImGuiMethods/TerritorySelection/TerritorySelector.cs)

| 信息 | 读取方式 |
| --- | --- |
| 地点、区域、地域 | `TerritoryType` 的三个地名引用分别读取 `PlaceName` 的简体中文文本。 |
| 副本或任务 | 优先用 `TerritoryType` 引用的 `ContentFinderCondition.Name`，缺名时尝试 `QuestBattle → Quest.Name`。 |
| 二维地图 | `TerritoryType.Map → Map.Id`。例如代号 `s1t1/00` 对应 `ui/map/s1t1/00/s1t100_m.tex`，缺少 `_m.tex` 时再试 `_s.tex`。 |
| 区域用途 | `TerritoryType.TerritoryIntendedUse`。界面显示用途名和原始编号。 |

项目核对 EXH 的列类型、顺序和偏移后才使用这些字段。附加表缺失或布局变化时，目录仍可从已验证的 `TerritoryType.Bg` 建立，受影响的名称或地图纹理留空。 [本地地图目录](../src/FFXIVPatchParser/LocalGameSource.MapCatalog.cs) · [SaintCoinach 地图纹理路径](https://github.com/xivapi/SaintCoinach/blob/master/SaintCoinach/Xiv/Map.cs#L127)

国服简中构建 `2026.11.03.0000.0000` 的 `TerritoryIntendedUse` 有 43 列，包含压缩布尔位、一个 UInt16 和三个 UInt8。区域用途的已知枚举是行 ID，例如 0 是城镇，1 是野外，10 是讨伐战。该表列的业务含义仍待验证，内置定义按实际字节偏移和位号显示，例如 `Flags05Bit0`。同批内置定义也补充了地图目录和天气已经使用的字段。 [区域用途定义](../src/FFXIVPatchParser/Definitions/TerritoryIntendedUse.json) · [上游用途枚举](https://ffxiv.wildwolf.dev/api/FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse.html)

## 静态 3D 场景

场景代号定位到包含 `level/` 的区域目录。地形从 `bgplate/terrain.tera` 读取板块清单和各块的位置，再加载 `bgplate/0000.mdl` 等网格。背景从 `level/bg.lgb`、`planmap.lgb` 和 `planevent.lgb` 读取实例，遇到 `.sgb` 共享组时递归展开，并组合父子变换。实测一部分有效背景的 BG `IsVisible` 为 0，因此预览保留这些实例，跳过节庆层。 [场景解析](../src/FFXIVPatchParser/LocalGameSource.TerritoryScenes.cs) · [Godbert 帮助](https://github.com/ufx/SaintCoinach/blob/master/Godbert/Views/Help3DView.xaml#L11-L69)

预览从 MDL 读取网格和 UV，再按 MTRL 的采样器绑定选取基色贴图。Godbert 的背景材质用 `0x1E6FEF9C` 标记 `g_SamplerColorMap0`，本项目用同一绑定查找基色。纹理优先解码最长边不超过 256 像素的 mip，同一路径的网格、材质和贴图各读一次。 [Godbert 背景材质](https://github.com/xivapi/SaintCoinach/blob/master/SaintCoinach.Graphics.Viewer/Content/BgMaterial.cs) · [地图纹理解码](../src/FFXIVPatchParser/LocalGameSource.TerritoryTextures.cs)

首批显示一小块地形，其余地形和背景随后分批提交。场景默认用 LOD 1，大场景优先用 LOD 2，资源没有对应网格时回退到较高细节。预览优先保留地形，背景按层交错加载，最多提交 100 万个三角面和 6,000 个实例。WPF 在后台合并使用同一贴图的网格，最近两个场景保留在内存中。当前支持静态基色贴图。游戏的多层材质混合、水面着色、天气、任务阶段和特效需要另外解析。二维地图显示原始纹理，客户端的地图标记由游戏叠加。

### 双击聚焦场景实例

在 3D 场景双击物体，预览会按命中网格的顶点范围找回原始模型实例，聚焦该实例的世界包围盒。场景仍按材质合批，模型名和 LGB 图层显示在视口。国服简中 `2026.11.03.0000.0000` 的利姆萨·罗敏萨 `s1t1` 实测分别选中 `bgplate/0000` 地形和 `s1t0_b0_snt2` 小型道具。将同一样本的两个实例相隔 300 个单位并放入同一批次，再选第二实例，镜头支点与其中心一致。平移会同步平移支点，原地转向后仍围绕同一支点。 [视口命中与模型实例](../src/FFXIVPatchParser.Desktop/MapPreview.Scene.cs) · [相机](../src/FFXIVPatchParser.Desktop/PreviewCameraController.cs)

## Hyperborea 的预设坐标

`LoadZone` 用区域 ID 查 `TerritoryType.Bg`，再以这个场景路径为键找 `ZoneInfo`。查找顺序是用户覆盖配置、插件内置 `data.yaml`。坐标来自匹配条目的 `Spawn.X/Y/Z`。插件在开启设置位置且找到坐标时应用它，天气等阶段预设则来自同一条目的 `Phases`。公开仓库的内置文件只有绝巴哈的示例，坐标为 `(0, 0, 0)`。 [查找预设](https://github.com/kawaii/Hyperborea/blob/main/Hyperborea/Utils.cs#L45-L73) · [应用坐标](https://github.com/kawaii/Hyperborea/blob/main/Hyperborea/Utils.cs#L318-L325) · [内置数据](https://raw.githubusercontent.com/kawaii/Hyperborea/main/Hyperborea/data.yaml)

游戏数据里，`Level` 有世界坐标 X、Y、Z 和朝向。国服 7.58 的 `Level #3693860` 属于下层甲板 `TerritoryType #129`，`Type=12`、`Object=8` 指向 `Aetheryte #8`，坐标约为 `(-84.00, 20.78, 0.03)`。SaintCoinach 将 `Aetheryte` 第 11 到 14 列标为 `Level` 引用。在本地抽查的 7 个编号均未直接命中 `Level.RowId`。现有样本确认了水晶的地标坐标，实际入场落点还需按具体入口与游戏状态验证。 [Aetheryte 字段定义](https://github.com/xivapi/SaintCoinach/blob/master/SaintCoinach/Definitions/Aetheryte.json)

## Simulant 与副本出生圈

Simulant 现行实现从区域的 `level/planmap.lgb` 查出生圈。SharedGroup 引用的特效路径为 `bgcommon/world/lvd/shared/for_vfx/sgvf_w_lvd_b0095.sgb`。用 SharedGroup 的实例 ID 匹配 EObj 的 `BoundInstanceId`，即可取到 EObj 的世界坐标 `Transform.Translation`。Simulant 在没有阶段覆盖坐标时使用这个锚点。Simulant 的 ACT 接口交换了后两轴，本项目直接使用 SqPack 的 X、Y、Z，Y 为高度。 [入口识别源码](https://github.com/MnFeN/Simulant/blob/91a82b61458dbe1b766e6adbc526631729bd7a4f/Simulant/Core/Zone/EntranceBarrier.cs#L16-L57) · [回放对照](https://github.com/MnFeN/Simulant/blob/91a82b61458dbe1b766e6adbc526631729bd7a4f/docs/NetworkEntities.md#L67-L98)

国服 7.58 本地读取验证结果如下。近零的高度已取整。

| 区域 ID | 副本 | 出生圈中心 X、Y、Z |
| ---: | --- | --- |
| 887 | 亚历山大绝境战 | (100, 0, 115) |
| 1122 | 欧米茄绝境验证战 | (105, 0, 115) |
| 1238 | 伊甸绝境战 | (100, 0, 115) |
| 1363 | 妖星乱舞绝境战 | (100.4117, 0, 115.7509) |

`ZoneInitPacket` 有用于镜头和场景加载的 XYZ，也有指向 `planmap.lgb` 实例的 `PopRangeId`。`SpawnPlayerPacket` 的公共生成数据另有角色位置。服务器可在进图初始化时下发坐标和范围标识，离线预览能独立读取的是客户端出生圈。正式副本如何在范围内选定每个玩家的精确落点仍需单独验证。 [进图初始化消息](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/Network/ZoneInitPacket.cs) · [玩家生成消息](https://github.com/aers/FFXIVClientStructs/blob/aad0de66f0a6b49f257d224499dd685ceba567da/FFXIVClientStructs/FFXIV/Client/Game/Network/SpawnPackets.cs#L33-L105)

地图预览在本次打开期间按区域 ID 与场景路径记住最后的镜头位置、朝向和旋转中心。切换区域后返回，或在二维地图与三维场景之间切换时，优先恢复该会话视角；不同区域即使共用场景路径，也分别记忆。更换游戏数据源或重启应用会清空会话记忆。首次进入区域时使用用户保存的世界坐标。场景只有一个出生圈时，以出生圈上方 1.65 米作为默认镜头位置。没有唯一出生圈时使用自动取景。区域 1363 的四块地形在 Y=-1024，合并横向范围约 0.125 米。镜头取景跳过这类占位地形，1363 会在出生圈附近初始化，重置视角也围绕出生圈取景。 [出生圈读取](../src/FFXIVPatchParser/LocalGameSource.TerritoryEntrances.cs) · [预览取景](../src/FFXIVPatchParser.Desktop/MapPreview.Scene.cs)

「取当前视角」捕获精确的 XYZ、水平转角、俯仰角和旋转中心。保存时采用捕获的数据，坐标框按三位小数展示，手动修改某一轴时旋转中心同步平移。旧 JSON 里的纯 XYZ 坐标继续使用默认朝向。会话视角不会写入起点文件；「回到起点」和镜头重置仍立即生效，离开区域后会记住操作后的视角。用国服 `2026.11.03.0000.0000` 的地图预览复测，可在 3D 场景中转动、捕获并保存，切换区域再返回后恢复同一视角。 [起点存取](../src/FFXIVPatchParser.Desktop/MapPreview.StartPosition.cs)

环绕中心与朝向独立保存。方向键和右键拖动只改变朝向，左键环绕对当前镜头相对于中心的位置施加增量旋转。原地转向后开始环绕，镜头由当前状态继续运动。滚轮按与旋转中心的距离计算推进量，沿当前朝向平滑前后移动，保留旋转中心与朝向；原地转向后也不会朝偏离视轴的旧中心侧移。「地图总览」重新取景，「距离 20」从当前旋转中心外 20 个世界单位朝中心取景。 [镜头控制](../src/FFXIVPatchParser.Desktop/PreviewCameraController.cs)

## 国服实测

使用国服 7.58 构建 `2026.11.03.0000.0000`。1,134 条区域记录对应 596 个场景路径，1,006 条记录带二维地图纹理，6 条缺少可用的中文地名并显示内部代号。进入「SqPack → 地图」后搜索区域 ID 即可复现。

- `128` 利姆萨·罗敏萨上层甲板关联 `Map #11`。可切换到二维视图查看 2048 × 2048 纹理。
- `134` 中拉诺西亚可用于观察大地图逐批显示地形与背景。
- `177` 后桅旅店用于检查小地图的完整加载，以及切换回来时复用内存缓存。
- `171` 泽梅尔要塞从副本表取得「山中战线泽梅尔要塞」。`225` 从任务链回退得到「牢狱之影」。

带贴图版的 WPF 离屏测试，计时从选择区域到提交可显示的网格。目录加载约 316 毫秒。

| 区域 | 首批网格 | 加载结束 | 已加载实例 | 三角面 |
| --- | ---: | ---: | ---: | ---: |
| 上层甲板 | 555 毫秒 | 2.09 秒 | 6,000 | 388,517 |
| 中拉诺西亚 | 92 毫秒 | 1.95 秒 | 3,873 | 594,438 |
| 后桅旅店 | 33 毫秒 | 254 毫秒 | 85 | 34,287 |

旧版白模的两个大场景均接近 200 万三角面。切回仍在缓存中的后桅旅店约 30 毫秒。这些数字记录网格提交时间，受磁盘缓存和机器性能影响。镜头测试覆盖地图和模型的前进、后退、转向、横移、双键前进和焦点清理。

## 天气环境与天空渲染的边界

国服 7.58 构建 `2026.11.03.0000.0000`、简体中文样本中，LVB 的 General 段给出天气槽与 EnvSpace；EnvSpace 指向 `.envb` 环境文件。地图信息现在显示这两类数据，并可跳转 `Weather` 行。读取 596 个不同场景路径均通过文件签名、长度、偏移和数量检查；这是文件解析验证，不是游戏内切换天气的画面对照。 [环境读取](../src/FFXIVPatchParser/LocalGameSource.TerritoryEnvironment.cs) · [地图信息](../src/FFXIVPatchParser.Desktop/MapPreview.Scene.cs)

本地天气槽是 **32 个 little-endian ushort**，偏移以 General 起点为基准。部分槽带 `0x8000` 标志，界面保留完整十六进制值，低 15 位只用于查天气名称。标志的运行时含义尚未验证，带标志的槽不能直接视为可用天气。比如区域 1122 的 `0x8002` 可关联晴朗名称，但对应 ENVB 没有 owner 2；区域 1363 的 `0x0002` 则有 owner 2。当前 Physis 的 `scn.rs` 按 32 个字节读取，且偏移处理与这些国服样本不同，因此本项目没有直接采用该实现。 [Physis 场景结构](https://github.com/redstrate/Physis/blob/main/src/scn.rs)

| 区域样本 | LVB 天气槽（不列零填充） | 环境资源末级文件名 |
| --- | --- | --- |
| 128 上层甲板 | 1 碧空、2 晴朗、3 阴云、4 薄雾、7 小雨、15 小雪 | `genv_s1t1.envb` |
| 134 中拉诺西亚 | 1、2、3、4、5 微风、7、15 | `genv_s1f1.envb` |
| 1122 时空狭缝 | `0x8002`、77、78、79、89、174、175、176 | `genv_z3oz.envb` |
| 1363 西格玛幻境4 | 2、77、78、79、89、174、175、176 | `genv_z3oy.envb` |

本地 `genv_s1t1.envb` 为 22,687 字节，ENVS 版本 6，包含 6 个 owner，与未带标志的天气槽一致；`genv_s1f1.envb` 为 29,083 字节，包含 7 个。对照 Physis 的轨道定义，上层甲板的碧空包含 15 个云关键帧，小雨包含 6 个云关键帧并新增雨轨道，小雪有雪轨道。两个文件还分别包含雾、雨、雪或风的 AVFX 路径。这里验证的是不同天气的数据确实不同，尚未渲染其动画。 [Physis ENVB](https://github.com/redstrate/Physis/blob/main/src/envb.rs) · [环境轨道](https://github.com/redstrate/Physis/blob/main/src/envs/mod.rs) · [云与雨雪参数](https://github.com/redstrate/Physis/blob/main/src/envs/environment.rs)

动态天空涉及昼夜时间、天气过渡、环境空间、云层、雾、光照与粒子。`EnvScene` 还持有 cubemap array。上层甲板的 ENVB 云轨道在 03:00 给出两组可复核的数据。碧空主／副云编号为 0／2，漫反射 RGBA 为 `(6, 10, 15, 255)`。小雨为 8／11 和 `(26, 31, 38, 255)`，两者强度均为 1。该颜色属于云轨道，天空背景还需要独立采样。 [客户端 EnvManager](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Environment/EnvManager.cs) · [EnvScene](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/EnvScene.cs)

在该构建的 SqPack 中实际读到下列资源。

| 资源 | 实测结构或引用 |
| --- | --- |
| `bgcommon/nature/sky/texture/sky_001.tex` | 8×32×32 体积纹理，6 个 mip，A8R8G8B8 |
| `bgcommon/nature/cloud/texture/cloud_002.tex`、`cloud_008.tex` | 1024×1024 DXT5，云轨道编号至贴图的映射待验证 |
| `shader/sm5/posteffect/Sky.shcd`、`Sky2.shcd`、`shader/sm5/shpk/cloud.shpk` | 已从本地索引读取，仍需核对着色器绑定和渲染顺序 |
| 区域 770 天之御柱的 `e3c1_u1_sky01.mdl` | `e3c1/level/bg.lgb` 的 `BG_001` 层包含此实例。模型可读取 1 个网格，材质 `e3c1_u1_sky01a.mtrl` 使用 `bguvscroll.shpk` 和 `e3c1_u1_sky01_d.tex` |

复现时可用「SqPack → 文件系统」搜索上述路径。从区域 770 的 `bg/ex2/02_est_e3/cnt/e3c1/level/bg.lgb` 可追到静态天空网格。一般地图的天空预览支持选择天气和昼夜时间。日光采用真实颜色轨道，天空可选择实验性体积纹理或 ENVB 颜色渐变。还需对比同区域、同天气和同游戏时间的画面，确认游戏端的天空、云和天气粒子合成。 [布局读取](../src/FFXIVPatchParser/LocalGameSource.TerritoryScenes.cs) · [文件类型说明](https://github.com/0ceal0t/Dalamud-VFXEditor)

对两个 ShCd 的 DXBC 读取 `RDEF` 资源表，实测 `Sky.shcd` 的 `t0` 是体积纹理，`Sky2.shcd` 使用 `t0` 和 `t1` 两份体积纹理。两者的 `b0`、`b1` 分别是 64 字节的 `cProjInv`、`cViewInv`，`b2` 是 96 字节的 `cSkyParam`。下一步可用独立的 Direct3D 11 视口绑定这组资源，核对 `cSkyParam` 的字段以及天气和时间怎样选中相应纹理，再与现有静态场景合成。 [原生着色器验证方法](shaders.md#预览与游戏渲染)

## MapEffect 参数与机制状态

FFXIVClientStructs 将 MapEffect 消息中的两个 16 位字段命名为 `State` 和 `TimelineIndex`，分别位于偏移 `0x04` 和 `0x06`。偏移 `0x08` 的字节 `Index` 选择当前 ContentDirector 的管理槽。该列表的客户端结构有 128 个槽，实际个数另以 `ItemCount` 记录。BossMod 旧源码称两个字段为 `State1` 和 `State2`，合并成 `low | (high << 16)` 后传给模块。例如 `0x00020001` 可拆成 `State=0x0001` 和 `TimelineIndex=0x0002`。 [消息字段](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/Network/MapEffectPacket.cs) · [ContentDirector 定义](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/InstanceContent/ContentDirector.cs#L46-L74) · [BossMod 合并逻辑](https://github.com/awgil/ffxiv_bossmod/blob/ee5b04a4ab63fb4313d29dbbb671f029f74752b7/BossMod/Framework/WorldStateGameSync.cs#L1159)

- **P1S Intemperance** 用 index 0–23 标识八个方位各三层的方块，`0x00020001`、`0x00800040`、`0x20001000` 分别标记红、蓝、紫。 [P1S Intemperance](https://github.com/awgil/ffxiv_bossmod/blob/ee5b04a4ab63fb4313d29dbbb671f029f74752b7/BossMod.Modules/Endwalker/Savage/P1SErichthonios/Intemperance.cs)
- **P7S Agdistis** 用 index 0 的 `0x00020001` 和 `0x02000100` 标识准备切换到三平台和大平台。桥使用 index 1、2、3、6，`0x00020001` 出现，`0x00800004` 消失。 [P7S 场地逻辑](https://github.com/awgil/ffxiv_bossmod/blob/ee5b04a4ab63fb4313d29dbbb671f029f74752b7/BossMod.Modules/Endwalker/Savage/P7SAgdistis/Border.cs)
- **P10S Pandaemonium** 用 index 2、3 区分右桥和左桥，`0x00020001` 启用，`0x00080004` 关闭。 [P10S 场地逻辑](https://github.com/awgil/ffxiv_bossmod/blob/ee5b04a4ab63fb4313d29dbbb671f029f74752b7/BossMod.Modules/Endwalker/Savage/P10SPandaemonium/Border.cs)

离线预览需要按区域把事件映射到布局实例、共享组时间线或特效，并保留事件顺序。可为单个标志位提供位移按钮，像 `0x00020001` 这样的组合状态需要保留原值。桥和平台可先用布局状态验证，含粒子的效果可在 AVFX 解析后接入。本轮核对了源码和本地文件，游戏内切换后的画面仍待对照。

## 下层甲板的交互环境预览

区域 129 的实际场景为 `bg/ffxiv/sea_s1/twn/s1t2/level/s1t2.lvb`，引用 `genv_s1t2.envb`。静态布局没有路径包含 sky 或 cloud 的模型。该 ENVB 有 1、2、3、4、7、15 六种天气。用带本地天气图标的下拉框或两侧箭头切换，右键可跳到对应的 `Weather` 行。0–24 时的滑条调整环境时间。带标志或缺少轨道的天气回退到默认光照。

ENVB v6 的轨道 0、2、13 分别读取日光、云漫反射、雾的时间关键帧；颜色为四个字节与独立浮点强度，预览在一天范围内循环线性插值。129 的正午碧空雾色为 `(35,109,178)/255`，小雨为 `(53,62,76)/255`，薄雾为 `(173,192,204)/255`。本地验证六种天气的各轨道可读取，并覆盖单关键帧与午夜首尾插值。 [轨道解析](../src/FFXIVPatchParser/LocalGameSource.EnvironmentTimeline.cs) · [环境交互和穹顶](../src/FFXIVPatchParser.Desktop/MapPreview.Environment.cs)

穹顶和场景共用朝向与视场角，自动取景的范围只计算场景几何。「实验性游戏天空纹理」打开时，程序从 ENVB 天气子段尾部读取候选编号。在上下层甲板的环境样本中，碧空、晴朗、其余四种天气的值分别为 273、274、3。三个本地 `sky_NNN.tex` 均为 8 × 32 × 24 的 A8R8G8B8 体积纹理。深度切片从暗到亮再到暗，符合昼夜序列。可在区域 129 依次选择碧空、晴朗，再拖动时间滑条查看。

当前用候选编号读取真实纹理，并假定 24 层对应 24 小时，按镜头世界方向估算纹理取样。编号映射、原生 `cSkyParam`、日光方向和天气过渡仍需与客户端画面对照。关闭实验纹理或所选天气的纹理缺失时，穹顶以实际云色和雾色显示渐变。 [体积纹理读取](../src/FFXIVPatchParser/LocalGameSource.SkyTexture.cs) · [预览采样](../src/FFXIVPatchParser.Desktop/MapPreview.Environment.cs) · [Ktisis SkyId 字段](https://github.com/ktisis-tools/Ktisis/blob/main/Ktisis/Structs/Env/EnvState.cs)

## 已验证的 MapEffect 共享组绑定

国服 7.58 本地样本：读取 `ContentDirectorManagedSG` 全部 5,654 条子行，将第一列与 `bg/planmap/planevent.lgb` 顶层共享组的 `InstanceId` 连接。以下路径均位于 `bg/ex4/01_nvt_n5/shared/for_bg/`。这是资源绑定实测，不代表已经执行游戏内状态或动画。

| 场地样本 | 管理表行.子行 | LayoutId | SGB 文件 |
| --- | --- | --- | --- |
| P7 场地（区域 1086） | 96.1 / 97.1 | 9325059 | `sgbg_n5r7_a0_gmc02.sgb` |
| P7 场地（区域 1086） | 96.2 / 97.2 | 9325370 | `sgbg_n5r7_a0_gmc02.sgb` |
| P7 场地（区域 1086） | 96.3 / 97.3 | 9325369 | `sgbg_n5r7_a0_gmc02.sgb` |
| P7 场地（区域 1086） | 97.6 | 9321250 | `sgbg_n5r7_a0_gmc03.sgb` |
| P10 场地（区域 1150） | 132.2 / 133.2 | 9754055 | `sgbg_n5ra_a1_gmc05.sgb` |
| P10 场地（区域 1150） | 132.3 / 133.3 | 9754054 | `sgbg_n5ra_a1_gmc02.sgb` |

上述子行索引与 BossMod P7S、P10S 的桥索引一致。同一场景可被多个管理表行引用，选择具体行仍需要 Director 上下文。区域 129 的三个布局文件共扫描到 1,557 个顶层共享组，与完整管理表没有匹配；该结果不覆盖其他事件系统和嵌套共享组。

地图详情的「MapEffect 资源绑定」会按当前游戏数据执行上述连接，允许选择匹配目标并复制实例、来源表行与 SGB 路径。可选时间线与 ACT 回放见下文。[绑定读取](../src/FFXIVPatchParser/LocalGameSource.MapEffects.cs) · [绑定界面](../src/FFXIVPatchParser.Desktop/MapPreview.MapEffects.cs)

区域 1326 登天格斗场还匹配到 `ContentDirectorManagedSG #227.22`，对应的 `LayoutId` 为 `12117802`。当当前 Director 采用管理行 227 时，消息的 `Index=22` 选中这条管理项，再用 `12117802` 匹配 `planmap.lgb` 的同号实例，得到 `sgbg_x6rc_a1_gmc12.sgb`。状态位和时间线字段描述如何改变选中的实例。离线界面核对的是管理表与布局的静态关联，尚缺运行时 Director 所选管理行和状态执行结果。

国服 7.58（`2026.11.03.0000.0000`）静态核对了客户端的 `ApplyMapEffect` 与 `PlayMapEffectTimeline`。后者逐位检查 16 位掩码，在有效槽上启动尚未播放的置位轨道，停止仍在播放的清零轨道。高 16 位为零时，前者跳过立即播放；非零、目标已加载且管理项就绪时优先应用高位掩码，调用失败才尝试低位。低位 `State` 会写入有效管理项。因此不能将高低两部分合并为单一轨道编号，也不能把高位为零解释为隐藏所有模型。[ContentDirector 定义](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/InstanceContent/ContentDirector.cs) · [共享组时间线定义](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/LayoutEngine/Group/SharedGroupLayoutInstance.cs)

两份上游地址表在核对时内容一致；国服 EXE 中的函数位置仍需另行核对。见[固定构建、地址对照和 Apricot 入口](client-runtime.md#已复查的入口)。

在这个构建的 SGB1/SCN1，时间线目录和共享组设置分别位于场景数据起点的相对偏移字段 `+0x10` 和 `+0x20`。目录项长 `0x2C`，每项有自己的 ID 和相对名称偏移。共享组设置从 `+5` 开始的 16 个字节按槽保存时间线 ID，零表示空槽。区域 1363 实测有 36 个场地管理绑定；`sgbg_z3oy_a7_flo2a.sgb` 的根实例为 `12357148`，包含两块背景模型，掩码 `0x0001`、`0x0004`、`0x0010` 分别选择 `bg_on`、`bg_off`、`bg_missing`。[共享组时间线读取](../src/FFXIVPatchParser/LocalGameSource.SharedGroupTimelines.cs)

地图页可读取 ACT 原始网络日志。区域行 01 确定区域，257 行提供事件时间、Director、索引与两个状态字段。回放最近一次含事件的入场记录，可暂停、跳转并回溯。当前实现将唯一匹配实例的独立 `bg_on` 和 `bg_off` 请求近似为整组背景几何的显隐；轨道的逐子对象动画、粒子、碰撞与音效仍需逐项执行。实测本场 ResLogger2 日志不含 ACT 事件；单独使用它无法恢复阶段时刻。[ACT 日志格式](https://github.com/OverlayPlugin/cactbot/blob/main/docs/LogGuide.md) · [回放实现](../src/FFXIVPatchParser.Desktop/MapPreview.Recording.cs)

## 妖星乱舞绝境战的本地加载记录

国服简中构建 `2026.11.03.0000.0000` 的这次 ResLogger2 记录有 216,712 行。第 112 行加载 `bg/ex2/05_zon_z3/ome/z3oy/level/z3oy.lvb`，第 214,954 行加载另一场景 `s1ti.lvb`。截取两者之间的记录，按不区分大小写的区域代号 `z3oy` 去重得到 430 条路径，包括 6 个 LGB、68 个 SGB、76 个 MDL、65 个 MTRL、122 个 TEX、37 个 AVFX 和 28 个 TMB。这份按名称得到的清单只覆盖直接带区域代号的资源。日志还出现了共用的 `bg.shpk` 和 `bguvscroll.shpk`。

在同一安装中打开这些 SGB、MTRL、TMB 和 AVFX，扫描以零结尾的完整资源路径，再用 SqPack 索引核对文件存在性，得到 885 对不同的来源与目标。例如 `z3oy_boss_gimmick04.tmb` 同时引用 `z3oy_b0_g04c0c.avfx` 和 `z3oy_b0_g04t0c.avfx`。地板共享组 `sgbg_z3oy_a7_flo2a.sgb` 引用两块背景网格、六个特效和开关音效。该日志只记录加载顺序，缺少战斗时间戳、Director 的事件参数、阶段切换点与实例世界位置。场地事件的播放还要读取共享组和动作时间线的指令，并与相应 Director 事件关联。

地图预览使用 MDL 顶点法线；非均匀缩放的实例按逆转置矩阵变换法线。较小布局优先取 LOD 0 和最高 512 像素的纹理。选择有 ENVB 环境轨道的天气后，「播放昼夜」从滑块当前位置按艾欧泽亚时间连续采样日光、云色和雾色。地图可用 D3D11 执行单纹理天空像素通道。AVFX 局部预览可从战斗资源栏打开，场景内的空间绑定和效果合成仍待接入。 [场景绘制](../src/FFXIVPatchParser.Desktop/MapPreview.Scene.cs) · [原生天空](shaders.md#地图单纹理天空的原生像素通道) · [AVFX 预览](sqpack-media.md#avfx-调度与粒子预览)

区域 1363 的静态布局有 590 个模型实例（含地形）。以 768 个布局实例作为高精度分档阈值后，桌面预览从 51 个实例、约 132 万个绘制三角面增至 107 个实例、约 232 万个绘制三角面。两次运行均达到预览预算，绘制三角面还包含透明裁切产生的细分。

复现界面关联时，选择区域 1363，展开「战斗时间线与特效资源」，读取这场战斗的 ResLogger2 逐行路径清单。选择 `z3oy_boss_gimmick04.tmb` 即可查看它首次出现的日志行与本地文件中的 AVFX 引用。资源按当前 LVB 的加载区间和区域代号筛选，引用逐项核对当前 SqPack。路径与明细可选中复制。

国服 7.58 的 `z3oy_boss_gimmick04.tmb` 有 10 块。沿 `TMAL → TMAC → TMTR → C012` 的 ID 引用读取，核对 C012 的起始帧和相对路径，可定位第 0 帧的 `z3oy_b0_g04c0c.avfx` 和第 28 帧的 `z3oy_b0_g04t0c.avfx`。两条路径均已由本地资源索引验证。当前读取范围为轨道偏移为零且无 Lua 条件的 C012，帧号相对该动作时间线；映射到整场战斗时还需要动作的起始事件。C012 另含绑定点、缩放、旋转、平移等参数。[TMB 触发读取](../src/FFXIVPatchParser/LocalGameSource.TmbAvfx.cs) · [VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor)
