# 客户端执行逻辑：地址核对与样本

本文记录对国服 `ffxiv_dx11.exe` 的离线静态核对。函数名和结构先取自上游声明，再对照本机构建的调用点、字段读写和资源。下表可用于复查同一构建；换构建后重新定位。操作步骤见[离线研究技能](../skills/ffxiv-offline-research/SKILL.md)。

## 样本身份与地址来源

- 客户端：国服简中 7.58，`ffxivgame.ver` 为 `2026.11.03.0000.0000`。EXE 为 x64 PE32+，首选映像基址 `0x140000000`，COFF 时间戳 `0x6AE8C849`，大小 51,961,600 字节。SHA-256：`52042b60631cf2c48851b3aaee36e9ec4fedd6d7f2c68f0e9a8c3befb0af9f98`。
- 地址表：核对日期 2026-11-18。[国际服 7.58 地址表](https://github.com/aers/FFXIVClientStructs/blob/1aaea81f65dd7fbd2e8302fdf947aa077fbf4ff6/ida/data.yml)与当时[国服分支的同一文件](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs/blob/1aaea81f65dd7fbd2e8302fdf947aa077fbf4ff6/ida/data.yml)的 Git blob 均为 `4b347341068ebb25476566579c634c27fee53f25`。该文件在国服仓库中并未给出另外一套国服地址。
- 通读地址表时按 YAML 结构读取全部 3,987 个类、321 个独立函数和 148 个全局条目。按地图、着色器、AVFX、TMB／PAP、ULD、镜头、资源和音频筛出 609 个成员函数、259 张虚表和 167 个虚函数槽作为待查索引。下列语义来自其中已逐段核对的分支；索引中的其余条目仍是线索。
- 本地验证工具为 Microsoft DUMPBIN 和读取 PE 节表、`.pdata` 异常目录的探针。样本有 173,151 个有效的起始 RVA 记录，用来缩小反汇编范围。一个函数可能有多个记录，叶函数也可能没有记录。用调用点的 `E8 rel32` 或完整入口确认边界，结合交叉引用和参数使用还原控制流。

## 已复查的入口

| 对象／函数 | 上游首选 VA | 国服样本 RVA | 本地核对依据 |
| --- | ---: | ---: | --- |
| `ContentDirector::ApplyMapEffect` | `0x140B755A0` | `0xB733D0` | 签名、管理槽查询、有效管理项的状态字段写入 |
| `ContentDirector::PlayMapEffectTimeline` | `0x140B75410` | `0xB73240` | 调用关系与 16 位 Play／Stop 循环 |
| `Scene::VfxObject::Create` | `0x140798B70` | `0x798D80` | 唯一签名调用点 `0x7290C7`；分配 `0x390` 字节、构造、传入路径 |
| `Scene::VfxObject::Update` | `0x14045C850` | `0x45D650` | 唯一签名调用点 `0x75D6AB`；资源实例和可见性相关字段 |
| `ActionTimelineManager::PreloadActionTmb` | `0x1418CA630` | `0x18C7560` | 函数签名，读取 Key，空值提前返回 |
| `ActionTimelineManager::AddLayoutTimelineObject` | `0x1418CC410` | `0x18C9340` | 函数签名与存放布局关联的对象 |
| `ActionTimelineManager::InsertTimelineGroup` | `0x1418CE510` | `0x18CB440` | 函数签名与时间线注册调用 |
| `AtkTimeline::PlayAnimation` | `0x1406AE080` | `0x6AE3F0` | 唯一签名调用点，标签查找与帧时间换算 |
| `AtkTimeline::UpdateChildTimelines` | `0x1406AE650` | `0x6AE9C0` | 唯一入口签名，节点和组件树遍历 |
| `AtkTimeline::GetInterpolatedValue` | `0x1406AE930` | `0x6AECA0` | 唯一签名调用点，关键帧搜索与分支 |

上游结构：[ContentDirector](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/InstanceContent/ContentDirector.cs) · [VfxObject](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/VfxObject.cs) · [ActionTimelineManager](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/System/Scheduler/ActionTimelineManager.cs) · [AtkTimeline](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Component/GUI/AtkTimeline.cs)。这些是可更新的结构声明；精确的地址来源以上面的固定提交为准。

## MapEffect、场景和天空

- `Index` 先查当前 Director 的管理项。加载就绪时，高 16 位 `TimelineIndex` 非零才尝试立即执行；优先执行高位掩码，失败才尝试低位。低 16 位 `State` 保存进有效管理项。共享组执行端逐位启动或停止 16 个槽。`LayoutId` 用于定位实例，和位掩码不是同一个编号空间。区域 1363 的布局绑定、完整参数和复现步骤见 [地图目录与场景](territories.md#已验证的-mapeffect-共享组绑定)。
- 资源引用、场景对象状态和时间戳需要分别取得。ResLogger2 记录能列出加载依赖；回放仍需带时间戳的 MapEffect 事件，以及当时的 Director 和共享组状态。本项目已有回放只执行已识别的轨道，未识别槽继续显示原始值。
- 本机构建从 `cSkyParam` 名称的使用点（RVA `0x36091D`）追到写入 `0x60` 字节的 CPU 提交点（RVA `0x360959`）。DXBC 反射和 D3D11 WARP 读回已有独立验证。该结果只覆盖[已实现的单纹理天空通道](shaders.md#地图单纹理天空的原生像素通道)，不能用于推定全场景渲染顺序或其他天空变体的常量。

## TMB 命令与 AVFX 实例

**加载。** `PreloadActionTmb` 先取输入结构的 `Key`，空字符串走返回分支；非空则把 Key 和资源类别 `0x746D62` 传给 `0x18C2990`，更新加载状态。`ActionTimelinePreloadInfo.Index` 的用途来自上游结构，当前反汇编所查到的预加载入口读取的是 Key。

**命令切入点。** `.rdata` 中 `C012` 字符串（RVA `0x22E7CF0`）被一张命令表引用。该项的类型编号为 9，分配大小为 `0x188`，工厂（RVA `0x18C3CE0`）初始化命令对象并设置虚表（RVA `0x2325580`）。虚表第 16 槽（从 0 开始）的入口为 `0x1912B30`，在实例链为空且满足该分支的标志条件时调用 `0x1913680`。

`0x1913680` 中能看到两条实例初始化路径。其中类型 9 的分支使用命令数据的相对字符串偏移，尝试从当前对象取上下文，必要时从父对象补取；关联对象存在时还会走额外的配置分支。最后将实例链入命令对象。这里确认了字符串、父对象回退与实例生命周期之间的关系。各命令字段如何选择武器、身体和骨骼挂点，仍需用成对资源和调用者验证。现有[离线 TMB 读取](../src/FFXIVPatchParser/LocalGameSource.TmbAvfx.cs)只返回无条件轨道的帧号与路径，例如 `z3oy_boss_gimmick04.tmb` 的第 0、28 帧。

**场景物件与粒子。** `VfxObject::Create` 构造物件、加入列表，再交给路径入口 `0x45B570`。`VfxObject::Update` 读取 `this+0x2A0` 的资源实例：为空时置等待标志，已就绪且标志允许时，把场景变换和速度等传到 `0x38FF70`。这是配置入口，单凭名字不能判定它是粒子每帧推进函数。另在 AVFX 读取入口 `0x3BB090` 验证了 `0x41564658`，对应文件首四字节 `XFVA`。

沿 Apricot 类型继续定位，已用基类构造和写入虚表确认 `Timeline` 和 `PointEmitter` 的构造入口在 `0x3BDB10` 和 `0x3DC9E0`，后者虚表位于 `0x215F8D0`。下一步检查其覆盖的虚函数由谁调用、用哪个时间源，再跟到粒子提交和渲染状态。仅凭构造函数不能确定推进或绘制槽。用同一个 AVFX 比较场景播放、父对象移动及模型挂点三种样本，再判断能否共用一条离线执行链。 [本地 AVFX/TMB 样本](sqpack-media.md#avfx-调度与粒子预览)

## ULD 时间线

- `PlayAnimation` 按请求标签选择动画。主时间线资源没有标签集时转查 `LabelResource`。这里的帧时间换算使用 `1/30`（常量 RVA `0x211BE08`）。`UpdateChildTimelines` 遍历当前节点、组件内部节点及同层节点，把父时间传给子节点后推进当前动画。
- `GetInterpolatedValue` 先检查激活片段、属性位和键类型。它以 `ParentFrameTime × 30`（常量 RVA `0x211BF70`）定位两侧关键帧。早于第一键时返回无值；没有后一键或 `InterpMode=0` 时返回前值。模式 `1` 按区间比例线性插值，模式 `2` 用左键的两个参数决定区间内的速度。
- 当前 [ULD 预览代码](../src/FFXIVPatchParser.Desktop/UldBrowser.Timeline.cs)按每秒 30 帧推进统一帧位置。已接入下述模式 2 的分段速度公式，标签驱动和子节点时钟仍待接入。比如 `jobhudblm0.uld` 的普通与简易分支包含多套元素状态。要复现游戏截图，应先确定运行时选择的分支、标签和值，再核对纹理、透明度和节点变换。[布局样本](uld.md#黑魔法师元素量谱)

### 插值模式 2

从 `GetInterpolatedValue` 的分派表（RVA `0x28B9EF0`）跟到双浮点和单浮点处理函数（`0x6E25F0`、`0x6E26A0`），两者调用同一个计算函数 `0x6EEA60`。它按区间内的分段速度求累计位移。以下等价写法适用于已核对的 `0 ≤ a, b ≤ T` 范围：

- `T` 是两键帧号之差除以 30。左键的两个浮点字段各乘 `T` 得 `a`、`b`；[Lumina](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Parsing/Uld/Keyframes.cs) 称这两字段为 `Acceleration`、`Deceleration`。`Δ` 为右值减左值，`t` 为距左键的秒数。
- 当 `a ≤ b` 时，时间 `(0, a, b, T)` 对应速度 `(0, v, v, 0)`，其中 `v = 2Δ / (T + b − a)`。当 `a > b` 时，时间 `(0, b, a, T)` 对应速度 `(v, pv, pv, v)`，其中 `p = (a − b) / T`，`v = 2Δ / (T × (1 + p²))`。
- 相邻速度之间线性变化。从 0 积分到 `t`，加到左键值；时长为零的段跳过。字节键处理函数 `0x6E26E0` 以无符号字节读入并向零截断输出。其余键类型和异常参数仍须逐项核对。

实读 `ui/uld/BasketBall.uld`：时间线 #15 的 X 坐标在 30→48 帧从 −32 到 −28，两个参数均为 0。按还原的计算逻辑，第 39 帧是 −29，线性插值则是 −30，可以用来做回归。`ActionBar.uld` 时间线 #6 的缩放关键帧也是模式 2，但该段的参数是 1 和 0，结果恰好为线性，单看该段不能区分两种模式。这里核对的是资源参数和本机机器码，尚未与游戏的逐帧画面比较。

## 模型动作与未知路径

在本机 `.rdata` 找到路径格式串后，继续检查引用格式串的代码是否实际调用同一个格式化入口（RVA `0x5FA20`）。结果如下：

| 资源族 | 国服样本位置 | 从实参可确认的行为 |
| --- | ---: | --- |
| 亚人 PAP | 入口 `0x44B1A0` | 常规分支为 `chara/demihuman/d%04d/animation/%s/pap/%s.pap`，两处 `%s` 传同一个键。模式值为 1 时改用 `animation/f0000/%s.pap`。 |
| 武器 PAP | 格式串引用 `0x43840D`、`0x44CBC2`、`0x44CC06` | 实际引用 `chara/weapon/w%04d/animation/%s/pap/%s.pap`。需进一步确认各调用者如何选键和骨架。 |
| 人形 TMB | 入口 `0x443710` | 以对象中的 16 位 ID 和调用者提供的键格式化 `chara/human/c%04d/animation/%s/timeline/%s.tmb`。此包装函数把目录参数固定为 `dummy`；不能推广到所有 TMB。 |

这些分支能提供跨版本候选，但无法单靠模板推出全部路径。把得到的键和编号代入后，须用目标构建的路径哈希、物理地址和资源引用验收。有碰撞时保留未知身份直到取得独立证据。[模型与动作样本](models-animation.md#亚人与人形动作样本) · [SqPack 路径覆盖](sqpack-filesystem.md)
