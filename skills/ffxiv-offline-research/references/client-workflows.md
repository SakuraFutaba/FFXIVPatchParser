# 追踪客户端调用链与验证资源

需要确认运行时语义时使用本页。已实测地址、资源和剩余问题见[客户端执行逻辑记录](../../../knowledge/client-runtime.md)。上游项目的职责和源码入口见[参考库](upstream.md)。

## 固定样本与定位函数

1. 从 TODO 选一个问题，把它改写成能检验的行为，例如「`TimelineIndex=0` 时会不会立即改变当前场景」。同时记下资源路径、对象／表行、游戏构建、语言和对照样本。用相同输入同时验证读取器和预览结果。
2. 在只读打开的本地安装中读取 `ffxivgame.ver`，用 `Get-FileHash -Algorithm SHA256` 记录 EXE 指纹，另记 PE ImageBase、COFF 时间戳和节表。两边各固定地址表的提交、版本和 Git blob；两个分支的文件一模一样时，也分别对照本地 EXE 查证。
3. 读取 YAML 全部 `globals`、`functions` 和 `classes`，按 TODO 抽取名字相关的项。`funcs` 是已命名成员地址，`vtbls` 是虚表地址与基类信息，`vfuncs` 是槽号。用结构声明看签名和字段用途。先查最接近现象的调用点，再查它怎样调下一个对象。
4. 从 VA 减去首选 ImageBase 得到 RVA。文件偏移为 `节的 PointerToRawData + RVA - 节的 VirtualAddress`，先确认目标区域有磁盘数据。用异常目录的起止 RVA 缩小查看范围，再以函数序言、直接调用和控制流校正边界。Windows 可用 `dumpbin /DISASM:NOBYTES /RANGE:<起始VA>,<结束VA> <EXE>`；也可在 IDA/Ghidra 中导入匹配版本的地址表，再看反编译结果和反汇编。[上游工具说明](https://github.com/aers/FFXIVClientStructs/tree/main/ida#readme)列出 `ffxiv_idarename.py` 的 IDA/Ghidra 支持及旧脚本的 IDA 版本限制。
5. 版本变动时，在本机构建 `.text` 匹配上游结构里的签名，遮蔽位移等易变字节。`E8` 直接调用解析为 `下一条指令地址 + 有符号 rel32`；RIP 相对的数据引用同样从下一条指令计算。虚表按 x64 的每槽 8 字节检查，兼顾次基类虚表或 `this` 调整。只在周围调用、字段或资源引用能独立支持时记录匹配。相同 RVA 差值只适合缩小局部搜索范围。
6. 没有符号时，从已知文件魔数、路径格式串、常量名或命令标签入手。查指向字串的表条目，读取同一项里的构造入口，再跟踪对象写入的虚表；用调用者、条件判断、失败回退和字段读写还原控制流。备选名称保留为候选。完整反汇编、临时脚本、日志和原始资源只放 `tmp/`，知识库记录必要的地址、伪代码和样本结果。

Microsoft 文档：[PE 格式](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format) · [DUMPBIN DISASM](https://learn.microsoft.com/en-us/cpp/build/reference/disasm?view=msvc-170) · [DUMPBIN RANGE](https://learn.microsoft.com/en-us/cpp/build/reference/range?view=msvc-170)。

## 按 TODO 选择验证链

| 要研究的行为 | 从哪里追 | 能区分不同解释的验证 |
| --- | --- | --- |
| MapEffect、阶段显隐与环境时间线 | 从区域的 LVB/LGB、SGB、管理表到 `ContentDirector` 与共享组 Play／Stop。把 `Index`、`LayoutId`、`State`、`TimelineIndex` 分开保存。按 BossMod 模块查事件值，按有时间戳的记录补事件顺序。 | 用同一场景的启动、停止和未知掩码各跑一次；检查跳转或重播时恢复的状态。只载入了哪些资源用 ResLogger2 确认。 |
| TMB、AVFX、武器光效与技能 | 从 `ActionTimelineManager` 到轨道和命令表（如 `C012`），再跟场景 `VfxObject`、资源实例、Apricot 调度器、时间线、发射器、粒子与提交。比较无条件轨道、条件轨道和不同目标。 | 对照同一特效的触发时刻、目标变换、挂点和粒子寿命。分别验证文件成功读取、实例正确更新和像素正确混合。 |
| 天空、天气、材质与 Alpha | 对照 ENVB、ShCd/ShPk、DXBC 与 CPU 提交。记录从资源变体到通道的选择、常量布局、输入语义、纹理和采样器，并复现深度及混合状态。 | 用离屏 D3D11/WARP 读回像素。用两层相交的发片或树叶检查 Alpha clip 的深度值，再用真正需要混合的材质对照。 |
| ULD 组件、动态状态和插值 | 先用 Lumina/Dalamud 确认组件、节点、切片和关键帧，再从 `AtkTimeline`、`AtkUldManager` 向上追具体 Addon 的标签选择和业务数据。 | 分别检查父子位置、枢轴、角度单位、初始可见性和标签跳转。选中间帧能区分线性和非线性的真实轨道，再用游戏内已知状态的截图核对。隐藏节点开关用于检查资源。 |
| 装备、人形、亚人与镜头 | 从模型路径、MDL→MTRL/IMC→贴图和 PAP→骨架入手。用 `CharacterBase`、`Human`、`Weapon` 以及路径实参定位资源分支；镜头分开检查投影近平面、视锥剔除、场景碰撞及相机位置。 | 同一动作至少核对两个体型或部位，比较骨名重映射和实际顶点位移。固定模型姿态改变相机距离和投影参数，判断切割来自投影还是几何。 |
| 路径恢复与表格定义 | 首先读 ResLogger2、EXD、SGB/TMB、MDL/MTRL、PAP、AVFX 的实际引用。缺少完整路径时，从客户端格式化调用或同族路径提取候选。 | 查目录、文件名和完整路径的哈希、分区及物理地址；有歧义时查内容和引用。按已命名资源净增量报告。EXD 定义按实际 EXH 列顺序和子行位置验证。 |
| SCD 音频和补丁 Diff | 音频从每种编码的 SCD 头、解码器和相应曲目表追起；补丁从清单校验和、实际写入区间到两个构建的索引和解包数据。 | 循环点统一为每声道采样帧再对照交界；补丁区分仅偏移变化和资源内容变化，按完整路径和真实字节报告。 |

每次把结论分为上游声明、本地反汇编和资源试验。字段名还不确定时写偏移和用途线索，语义确认后再补代码或表格定义。优先做可以独立回归的最小行为，把未验证分支和所需下一份样本写回 TODO。
