# ULD 界面资源与静态合成

ULD 保存界面节点、组件定义、纹理资源、切片坐标和动画时间线。Excel 表保存文字、调色和业务数据，例如 Addon、Lobby、UIColor。运行中的界面还需要游戏逻辑设置图标、文字、可见性和数值。

## 上游源码说明

[Dalamud 的 UldWidget](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Interface/Internal/Windows/Data/Widgets/UldWidget.cs)提供纹理资源、主题预览、Parts 切片和时间线字段浏览。ULD 名称来自游戏进程中的签名扫描，不是 Excel 表目录。离线工具不能直接照搬该发现机制。

主题资源位于 `ui/uld/img01/` 至 `img05/`。资源的 `ThemeSupportBitmask` 标记主题支持；缺失主题资源时使用原始路径。切片通过 `TextureId` 找纹理，按 `U/V/W/H` 裁剪。

[Lumina UldFile](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Files/UldFile.cs)读取 Assets、Parts、Components、Timelines 和 WidgetData。[UldRoot](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Parsing/Uld/UldRoot.cs)定义节点父子关系、坐标、缩放、旋转、透明度及可见性；[NodeData](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Parsing/Uld/NodeData.cs)定义图片、文本、九宫格和组件引用等节点。

## 本项目实测

样本为国服 7.58、构建 `2026.11.03.0000.0000` 的 `ui/uld/ActionBar.uld` 和 `ui/uld/actioncontents.uld`，游戏目录为简体中文客户端。ULD 本身没有单独的语言选择。

复现：连接该构建的游戏目录，打开 ULD 界面页，程序自动读取 `ui/uld/ActionBar.uld`。通过下拉菜单切换资源，或输入路径后按 Enter。查看资源、切片、时间线、节点与组件，再比较勾选和取消“显示隐藏节点”的静态布局。

- 6 个纹理资源、290 个切片、6 个组件、23 条时间线，主布局有 20 个节点。
- 主布局根节点尺寸为 624 × 72。Lumina 7.7.2 的 `Visible` 读取原始标记的 `0x80`，导致对该根节点的观察有误。新版读取原始标记的 `0x10` 并默认按该标记显示。 [AtkResNode 标记](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Component/GUI/AtkResNode.cs)
- 资源包含 `IconA_ChargeIcon.tex`、`IconA_Frame.tex`、`IconA_Recast.tex`、`IconA_Recast2.tex`、`DragTargetA.tex`、`ActionBar.tex`。资源支持主题的位掩码并不一致。
- 独立 WPF 探针成功加载 6 个纹理，展开主布局组件后绘制 123 个图片节点，另有 77 个文本、九宫格或动态节点未渲染。渲染结果可辨认出 12 格快捷栏；显示隐藏节点会叠加本应由运行时选择的状态，因此高亮可能重叠。
- `actioncontents.uld` 包含 5 个组件、64 条时间线。节点通过 `TimelineId` 关联同一 ULD 中的时间线。两个样本都包含分段的缩放和透明度轨道；部分片段只有标签或颜色轨道。

数量来自直接调用本项目使用的 Lumina 读取结果；尚未逐帧与游戏内动画画面比对。

## 当前预览范围

ULD 浏览器提供主题与原版纹理对照、切片预览、路径复制及结构字段浏览。静态合成会展开组件引用并应用父子坐标、缩放、旋转和透明度。打开「显示隐藏节点」可检查尚未激活的布局。 [数据入口](../src/FFXIVPatchParser/LocalGameSource.Uld.cs) · [ULD 控件](../src/FFXIVPatchParser.Desktop/UldBrowser.xaml.cs)

文件下拉只列出当前 `ui` 容器索引中已有经过验证名称的 ULD，并补入实际存在的 ActionBar 样本；它不是所有 ULD 的完整清单。可用上一项、下一项遍历，也可直接输入尚未列出的名称。该目录仅在页面首次显示后建立，不读取未知文件头。

布局预览按初始图片边界居中，拖动时间线时保留同一个视图位置。「节点边框和 ID」默认关闭，打开后可检查各节点范围。当前布局或组件包含可预览的轨道时，可以拖动滑块或用左右按钮逐帧查看。「恢复初始帧」会清除对节点位置、旋转、缩放和透明度的动画取样。无可预览轨道时，时间线保留原位并禁用。

当前按 `FrameData.StartFrame/EndFrame` 选择片段，对位移、旋转、缩放和透明度按帧取样。阶跃关键帧保持前值，其余使用线性插值；尚未模拟原生的加减速曲线、标签跳转和运行时事件。颜色轨道、文本表引用、动态技能图标、九宫格及裁切规则仍可在结构页检查。 [Lumina 关键帧结构](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Parsing/Uld/Keyframes.cs)

### 黑魔法师元素量谱

国服简中构建 `2026.11.03.0000.0000` 的 `ui/uld/jobhudblm0.uld` 有 4 个纹理资源、43 个切片、11 个组件、97 条时间线。主布局根节点 #1 下，普通量谱是 #2 分支，简易量谱是 #33 分支。预览可以分别查看两条分支，或切到完整主布局检查所有节点。 [游戏运行时结构](https://ffxiv.wildwolf.dev/api/FFXIVClientStructs.FFXIV.Client.UI.AddonJobHudBLM0.html)

节点初始旋转用度，例如冰晶节点 #15、#16、#17 分别为 −64°、−44°、−26°。时间线 Rotation 关键帧用弧度，例如时间线 #9 从约 −1.571 过渡到 0。节点保存相对父节点的 X/Y、枢轴、缩放和初始可见标记。量谱的元素、层数和计时由游戏运行时数据选择，原始主布局与任一游戏截图的状态可能不同。 [节点和时间线读取](../src/FFXIVPatchParser.Desktop/UldBrowser.Timeline.cs)
