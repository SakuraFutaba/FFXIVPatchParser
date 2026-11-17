# SqPack 浏览与补丁 Diff 路线图

目标是在一个工作区浏览本地 SqPack 的全部索引条目，并在打开补丁后先看到影响范围，再追到资源内容和游戏对象的具体变化。已知格式提供专用预览。路径未知的条目仍可通过索引、哈希和容器位置打开，未识别格式提供字节视图与导出。

当前「SqPack → 文件系统」已接入本地 `.index` 和 `.index2`，可预览 Excel、图片、模型、地图、音频及解包后的 Hex。Patch 用补丁清单、索引和 VCDIFF 写入区间定位目标资源候选。后续扩展路径恢复，再接入新旧内容确认与分类型比较。 [文件系统索引与预览](knowledge/sqpack-filesystem.md) · [SqPack 与媒体](knowledge/sqpack-media.md) · [表格 Diff 与关联](knowledge/table-diff.md)

## 1. 建立统一资源目录

- [x] 接入 `.index`、`.index2` 及文本碰撞项。按路径、哈希和数据分片搜索，按类别和目录展示已知路径及未知条目。文件选择后才读取内容并打开内嵌预览。
- [x] 实现按构建、类别和分片统计路径覆盖率。支持导入 ResLogger2 与本地清单，按当前索引、哈希及物理地址确认候选，保存来源、索引摘要和已验证结果。
- [x] 分批提取标准资源前 1 MiB 的完整 ASCII 路径引用，支持取消和从上次完成的批次继续。已确认的路径用于文件系统，以及原有图片、模型、音频和 Patch 扫描范围内的名称回填。
- [ ] **找齐所有资源路径。** 国服 `2026.11.03.0000.0000` 样本已补到 1,793,872 条，剩余 9,136 条。优先从 `bg`、`chara`、`bgcommon` 和 `vfx` 的结构化引用补全，并查证同一索引地址的 CRC 碰撞候选。构建更新后重新校验上一版候选，并从修改过的表和资源中提取新引用。 [覆盖率与验证方法](knowledge/sqpack-filesystem.md#国服-758-样本)
- [ ] **按已命名的资源扩展浏览分类。** 路径覆盖完整后，根据实际扩展名和目录分布补全图片、模型分类。图片可从图标、界面图集、地图、场景、角色和特效贴图分组，模型可从角色、装备、武器、怪物、家具和场景分组。保留原始目录和精确路径跳转。
- [ ] 增加原始解包文件导出，覆盖大文件分块写出、取消和校验。

验收时按索引统计可枚举、已命名、可读取和可专用预览的条目。覆盖已知路径、未知路径、`.index2` 独有条目及同哈希冲突，核对搜索与十六进制视图定位到同一资源。

## 2. 从写入候选确认内容变化

- [ ] 复用 `PatchArchive` 和 `PatchImageScanner` 的分卷清单、校验和区间筛选，将候选扫描扩展到所有受影响的索引和容器。关联 VCDIFF 写入区间、旧新索引差异及新增或移除的容器。先展示带证据的候选，再按需确认内容。
- [ ] 支持提供匹配的新旧只读客户端。缺目标容器时，研究按正确的补丁链在 `tmp/` 还原所需文件，应用前校验旧容器，完成后核对清单中的目标校验值。缓存以构建、容器和校验值为键，避免本地版本变化后复用过期结果。
- [ ] 优先用经验证的路径配对，再用同索引范围内可唯一匹配的哈希信息配对。比较解包内容的摘要，确认新增、删除、内容修改或位置变化。缺少旧数据、存在冲突或还原失败时保留证据及待确认原因。有相同内容且路径变更时提示可能迁移，要求额外证据才能确认改名。

以国服 `7.57 → 7.58`（`0.0.0.30 → 0.0.0.31`）建立基线，使用两端版本、语言和校验值匹配的数据。抽查内容真正变化、仅重排、删除和缺旧数据的条目，再与两端完整枚举比对。记录冷缓存、热缓存的首批候选时间、确认耗时、读取字节数与峰值内存，随后再设性能目标。 [复现实验与验证](knowledge/verification.md)

## 3. 按资源格式提供预览与 Diff

- [ ] **Excel 和纹理。** 沿用 `RowId`、`SubrowId` 和原始列值比较，布局变动时标出字段对齐依据，保留原始列视图。纹理复用原分辨率和透明背景控制，再增加双侧同步缩放、通道和 mip 选择、尺寸及格式变化、像素差异定位。
- [ ] **模型、材质、骨骼与动作。** 对照 LOD、网格、材质槽、IMC 变体、骨架层级和 PAP 动作槽。提供同步相机及同一动作时刻对照，展示实际解析的材质、纹理和 shader 引用。以透明、裁切、染色和多材质样本检查预览。缺依赖时让几何和结构信息仍可打开。
- [ ] **场景和地图。** 从现有地图与静态场景预览扩展 LGB/SGB 的实例树、模型与资源引用、变换和分组。支持按对象叠加新旧变化，标注可验证的地图标记、传送与事件对象。区分本地布局实例和外部来源补充的信息。 [地图目录与场景](knowledge/territories.md)
- [ ] **MapEffect 预览。** 为副本逐个验证 index 和 32 位 state 到 LGB／SGB 实例、桥梁、平台和时间线的映射。保留原始十六进制，也可分高低 16 位编辑。明确定义为单一标志位时提供 `<<`、`>>`，组合状态保留完整数值。先用 P7S 或 P10S 的布局切换验证，涉及 AVFX 的部分接入特效解析与播放后再实现。 [BossMod 样本与参数](knowledge/territories.md#mapeffect-参数与机制状态)
- [ ] **时间线控制。** 实现时间线加载和预览后，在地图的 MapEffect 资源绑定区增加 `TimelineIndex` 和 `State` 控件。按选中的绑定切换时间线和状态，展示原始参数，并用已确认的实例验证对应的场景变化。
- [ ] **音频、UI、特效和着色器。** 先比较 SCD 音轨参数、循环点及音轨内容，再补 ULD 节点树、贴图引用和版面预览。为 AVFX 和 ShCd 建立结构与依赖视图，沿用现有 SHPK 包读取器扩展变体、通道和字节码 Diff。 [Channeling 表与连线特效](knowledge/sqpack-media.md#channeling-表与连线特效) · [Shader 资源与预览](knowledge/shaders.md)
- [ ] **其他格式。** 用目录实际扫描结果维护按构建统计的格式清单，依使用频率补字体、文本、配置、脚本及其余可识别资源。区分可解析结构与可完整预览，保留对未知区块和解析错误的原始字节查看。

每种格式先用有明确已知差异的两侧样本验收，再加入一个只有容器位置变化或无内容变化的样本。结构差异和预览都可回查到对应资源及原始内容。

## 4. 把资源变化追到游戏对象

- [ ] 在 `RelationResolver`、模型目录和地图目录上补资源依赖查询。先做 Excel 行 → 图标／模型／音频、区域 → 场景实例、模型 → 材质 → 纹理，再为每条关系支持反查。记录字段或文件引用、构建和语言，避免不同版本中的相同 ID 被直接视作同一关系。
- [ ] 对照 Sapphire 的 EXD 结构、事件对象与 NPC 工具，为区域、事件、NPC 和动作补充可验证的字段语义。先在本地国服样本确认列、类型、行 ID 和引用方向，再显示给用户。服务端脚本推导、社区映射和客户端原始字段分别携带来源。
- [ ] 在补丁工作区展示新增、删除、内容变化、仅位置变化和待确认的资源数量。支持从游戏对象跳到变化的字段、资源和证据，也可从资源反查影响的表行和场景实例。相同资源影响多个对象时保留各自引用，并在汇总中按资源去重。

验收样例覆盖「图标变了但物品行值相同」「NPC 更换模型」「场景实例移动但模型文件内容相同」。每条结论能打开两端来源，缺失证据时显示已有的一侧和缺少的数据。

## 5. 改进场景镜头与材质预览

- [ ] **场景编辑镜头。** 已将环绕中心与自由观察的镜头姿态分开，并支持场景总览和键盘飞行。下一步支持以场景中选定的物体为中心聚焦和环绕。用宽阔地形和小型道具验收选点、聚焦、平移和环绕，确认原地转向后仍沿用选定的中心。 [Blender 视图导航偏好](https://docs.blender.org/manual/en/latest/editors/preferences/navigation.html#orbit-around-selection) · [Unity Scene 视图导航](https://docs.unity3d.com/Manual/SceneViewNavigation.html) · [UE 视口操作](https://dev.epicgames.com/documentation/en-us/unreal-engine/viewport-controls-in-unreal-engine)
- [ ] **第三人称地图预览。** 先用可见标记代表人物，在地图中保存独立的位置与朝向。移动以镜头朝向的水平投影为参考，鼠标拖动和方向键围绕人物转动，镜头目标距离默认为 20 个世界单位。接着为人物接入地面检测和胶囊碰撞，为镜头加独立的球形扫掠，遇墙时缩短距离，离开障碍后平滑恢复目标距离。最后接入可选人物模型、骨骼以及站立和移动动作。先验收实际位移、过墙拐角的镜头回缩与恢复，再检查人物朝向和动作切换。 [UE Spring Arm 组件](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/Engine/USpringArmComponent?lang=en-US)
- [ ] **Direct3D 11 裁剪基线。** 用自写像素着色器对两张互相交叉的镂空面执行 `clip(alpha - threshold)`。通过裁剪的片元写入颜色和深度，镂空处能看到后方表面。交换绘制顺序并从两侧观察，覆盖前后遮挡和交叉边缘，再用国服 `2026.11.03.0000.0000` 的格里达尼亚旧街树叶与废弃墓地贴花复验。保留基线截图供后续对照。 [Microsoft HLSL `clip`](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-clip) · [已验证的场景材质](knowledge/shaders.md#alpha-在场景材质中的用法)
- [ ] **接入 SqPack 原生着色器。** 以上述裁剪样本为起点，按 MTRL 覆盖值和系统、场景、子视图键选定 SHPK 节点和通道。用顶点输入签名校验布局，按单程序资源声明绑定纹理、采样器、材质与场景常量，并设置渲染目标格式、深度、混合与剔除状态。现有 WARP 实测验证了 11 个包各自的首个顶点程序及首个像素程序可创建，下一步验收是在独立渲染器实际绘出一个样本，并核对裁剪与前后遮挡。需要中间缓冲的通道按依赖逐步接入，尚未实现的材质沿用简化预览。 [本项目 SHPK 实测](knowledge/shaders.md#预览与游戏渲染) · [单程序资源解析](https://github.com/Ottermandias/Penumbra.GameData/blob/main/Files/ShpkFile.cs#L733-L805) · [Microsoft 输入布局](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11device-createinputlayout)
- [ ] **动态天空与天气。** 已读取 LVB 天气槽和环境资源。下一步以同一场景的碧空和小雨为样本，解析 ENVB 的昼夜、云层、雾与光照轨道，核对 `Sky.shcd` 的体积纹理采样和常量，再以同天气、同一游戏时间的截图验收。确认云层、雨雪粒子和过渡后逐步加入天气预览。 [天气和天空资源](knowledge/territories.md)

## 6. 全局音乐播放器

- [x] 底部播放器跨页面保留播放，支持上一首、下一首、播放／暂停、进度、音量、随机、列表循环、单曲循环和收藏。音频目录中双击曲目开始播放，来源页和底栏控制同一音轨。
- [x] 底部播放栏常驻。播放队列、曲库和收藏通过右侧列表图标在浮窗中打开，页面布局保持稳定。
- [x] 按需加载本地曲库。用 `BGM` 表把行 ID 连到 `.scd`，再以 OrchestrionPlugin 离线资料显示曲名和场景。可在播放前搜索、加入队列。进度和时长来自实际解码后的音轨。 [OrchestrionPlugin 曲目资料](https://github.com/perchbirdd/OrchestrionPlugin/tree/main/Orchestrion/Data) · [本地 BGM 路径读取](src/FFXIVPatchParser/LocalGameSource.Audio.cs)
- [ ] 扩展长曲、循环点和更多 SCD 编码的播放验证。目前使用 FFmpeg 回退的试听最多五分钟，导出保留完整音轨。

## 上游参考

- **FFXIV Explorer fork** 的 `FileManagerWindow` 用目录树、索引位置和哈希组织资源，按签名或容器内容类型调用预览，提供 Raw Hex。源码有纹理、模型、EXH/EXL、SCD 和 PAP 视图。AVFX、SGB 分支只解析对象，ShCd、ShPk 和 ULD 的视图调用在所查 `develop` 源码中已注释。Unofficial 1.9.1 的发布说明记录了索引编号、同索引冲突处理、已知路径的 `.index2` 目录和索引缓存。这些行为可作为目录和预览设计的参考，实际兼容范围由本项目样本验证。 [Explorer 文件浏览器源码](https://raw.githubusercontent.com/goaaats/ffxiv-explorer-fork/develop/src/main/java/com/fragmenterworks/ffxivextract/gui/FileManagerWindow.java) · [Explorer 发布说明](https://github.com/goaaats/ffxiv-explorer-fork/releases)
- **Sapphire** 的 `master` README 将目标定为国际服 3.3（`2016.07.05.0000.0001`）。`src/world` 包含世界服务的区域、副本、住宅和任务战等对象，`src/lobby` 是大厅服务，`src/scripts` 存放游戏脚本。`src/common` 中的 `ExdData` 按类型延迟读取及缓存工作表，内部选择英文。`src/tools` 有 EXD 结构生成、事件对象、NPC 和导航数据工具。旧版字段含义和对象关系需要用当前构建的 EXH、EXD 和资源引用复核。 [Sapphire README](https://github.com/SapphireServer/Sapphire#client-version) · [源码目录](https://github.com/SapphireServer/Sapphire/tree/master/src) · [ExdData](https://github.com/SapphireServer/Sapphire/blob/master/src/common/Exd/ExdData.h) · [区域对象](https://github.com/SapphireServer/Sapphire/tree/master/src/world/Territory)
