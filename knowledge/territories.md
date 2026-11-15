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

## 静态 3D 场景

场景代号定位到包含 `level/` 的区域目录。地形从 `bgplate/terrain.tera` 读取板块清单和各块的位置，再加载 `bgplate/0000.mdl` 等网格。背景从 `level/bg.lgb`、`planmap.lgb` 和 `planevent.lgb` 读取实例，遇到 `.sgb` 共享组时递归展开，并组合父子变换。实测一部分有效背景的 BG `IsVisible` 为 0，因此预览保留这些实例，跳过节庆层。 [场景解析](../src/FFXIVPatchParser/LocalGameSource.TerritoryScenes.cs) · [Godbert 帮助](https://github.com/ufx/SaintCoinach/blob/master/Godbert/Views/Help3DView.xaml#L11-L69)

预览从 MDL 读取网格和 UV，再按 MTRL 的采样器绑定选取基色贴图。Godbert 的背景材质用 `0x1E6FEF9C` 标记 `g_SamplerColorMap0`，本项目用同一绑定查找基色。纹理优先解码最长边不超过 256 像素的 mip，同一路径的网格、材质和贴图各读一次。 [Godbert 背景材质](https://github.com/xivapi/SaintCoinach/blob/master/SaintCoinach.Graphics.Viewer/Content/BgMaterial.cs) · [地图纹理解码](../src/FFXIVPatchParser/LocalGameSource.TerritoryTextures.cs)

首批显示一小块地形，其余地形和背景随后分批提交。场景默认用 LOD 1，大场景优先用 LOD 2，资源没有对应网格时回退到较高细节。预览优先保留地形，背景按层交错加载，最多提交 100 万个三角面和 6,000 个实例。WPF 在后台合并使用同一贴图的网格，最近两个场景保留在内存中。当前支持静态基色贴图。游戏的多层材质混合、水面着色、天气、任务阶段和特效需要另外解析。二维地图显示原始纹理，客户端的地图标记由游戏叠加。

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

地图预览优先使用用户保存的世界坐标。场景只有一个出生圈时，以出生圈上方 1.65 米作为默认镜头位置。没有唯一出生圈时使用自动取景。区域 1363 的四块地形在 Y=-1024，合并横向范围约 0.125 米。镜头取景跳过这类占位地形，1363 会在出生圈附近初始化，重置视角也围绕出生圈取景。 [出生圈读取](../src/FFXIVPatchParser/LocalGameSource.TerritoryEntrances.cs) · [预览取景](../src/FFXIVPatchParser.Desktop/MapPreview.Scene.cs)

「取当前视角」捕获精确的 XYZ、水平转角、俯仰角和旋转中心。保存时采用捕获的数据，坐标框按三位小数展示，手动修改某一轴时旋转中心同步平移。旧 JSON 里的纯 XYZ 坐标继续使用默认朝向。用国服 `2026.11.03.0000.0000` 的地图预览复测，可在 3D 场景中转动、捕获并保存，切换区域再返回后恢复同一视角。 [起点存取](../src/FFXIVPatchParser.Desktop/MapPreview.StartPosition.cs)

环绕中心与朝向独立保存。方向键和右键拖动只改变朝向，左键环绕对当前镜头相对于中心的位置施加增量旋转。原地转向后开始环绕，镜头由当前状态继续运动。滚轮以旋转中心为基准改变目标距离，每帧按实际经过时间逼近目标。「地图总览」重新取景，「距离 20」从当前旋转中心外 20 个世界单位朝中心取景。 [镜头控制](../src/FFXIVPatchParser.Desktop/PreviewCameraController.cs)

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
