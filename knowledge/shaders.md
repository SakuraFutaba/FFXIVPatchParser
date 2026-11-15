# Shader 资源、模型材质与预览

## 模型怎样引用着色器

SqPack 将 `shader` 划归 `05` 类别。模型 `.mdl` 记录网格使用的材质槽和材质名，具体 `.mtrl` 还要结合模型所在目录与材质变体定位。材质记录着色器包名、纹理路径、采样器与纹理槽的映射、材质键和常量覆盖。Windows DX11 客户端通过 `shader/sm5/shpk/<包名>` 读取 `.shpk`，所以 `chara/`、`bg/` 和 `bgcommon/` 下的材质都可以引用独立 `shader` 类别中的包。 [SqPack 类别](../src/FFXIVPatchParser/LocalGameSource.ModelPaths.cs) · [项目材质读取](../src/FFXIVPatchParser/LocalGameSource.Models.Materials.cs) · [Penumbra 的 MTRL 读取](https://raw.githubusercontent.com/Ottermandias/Penumbra.GameData/main/Files/Materials/MtrlFile.cs)

SHPK 包含各阶段的编译程序、资源声明、材质常量字段与默认字节、系统键、场景键和材质键。游戏按系统、场景、材质和子视图键的组合选择节点，再由渲染通道选择节点中的程序索引。MTRL 的材质键和常量覆盖包里的默认值。包级资源声明汇总了多个变体需要的资源，包含骨骼矩阵、光照缓冲和其他运行时资源。要确定一个通道的实际绑定，需要读取选定程序的资源表。 [Penumbra 的 SHPK 读取](https://github.com/Ottermandias/Penumbra.GameData/blob/main/Files/ShpkFile.cs#L120-L218) · [节点与通道结构](https://github.com/Ottermandias/Penumbra.GameData/blob/main/Files/ShpkFile.cs#L827-L872)

`.shcd` 是独立的已编译着色器资源。模型材质的包名解析走 `.shpk`，当前读取器针对这条依赖。 [VFXEditor 支持的文件类型](https://github.com/0ceal0t/Dalamud-VFXEditor#supported-file-types) · [项目 SHPK 读取](../src/FFXIVPatchParser/LocalGameSource.Shaders.cs)

## 本地复现

在简体中文客户端构建 `2026.11.03.0000.0000` 中，用 `ReadModelPreview` 读取 `chara/human/c0101/obj/hair/h0001/model/c0101h0001_hir.mdl`。其 `material/v0001/mt_c0101h0001_hir_a.mtrl` 同时引用 `hair.shpk` 和 `c0101h0001_hir_norm.tex`。人脸模型 `chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl` 则按材质槽分别引用 `skin.shpk`、`iris.shpk`、`hair.shpk` 等包。 [项目模型读取](../src/FFXIVPatchParser/LocalGameSource.Models.cs)

以同一安装的 SqPack 调用 `ReadShaderPackage("bg.shpk")`，再用返回的 `Path` 调用一次。两次取得同一份包级元数据。对下表的 11 个包重复验证，文件头均为 `ShPk`、版本 `0x0E01`、目标 `DX11`，首个顶点程序的正文带 `DXBC` 标记，程序版本为 `vs_5_0`。使用不存在的包名会报告缺失。此处的程序数和节点数来自包头。 [项目 SHPK 读取](../src/FFXIVPatchParser/LocalGameSource.Shaders.cs)

| 包名 | 顶点程序 | 像素程序 | 节点 | 材质键 | 采样器 | 纹理声明 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `bg.shpk` | 56 | 3850 | 9244 | 4 | 12 | 15 |
| `bgprop.shpk` | 70 | 138 | 558 | 1 | 10 | 14 |
| `bguvscroll.shpk` | 22 | 202 | 206 | 2 | 12 | 15 |
| `bgcolorchange.shpk` | 9 | 12 | 13 | 1 | 7 | 7 |
| `character.shpk` | 176 | 1038 | 12560 | 4 | 15 | 30 |
| `characterlegacy.shpk` | 64 | 1740 | 3136 | 4 | 13 | 25 |
| `hair.shpk` | 192 | 200 | 1664 | 4 | 11 | 22 |
| `iris.shpk` | 72 | 96 | 288 | 3 | 10 | 22 |
| `skin.shpk` | 72 | 384 | 704 | 3 | 13 | 30 |
| `water.shpk` | 19 | 777 | 1728 | 6 | 17 | 19 |
| `river.shpk` | 4 | 79 | 144 | 7 | 13 | 13 |

`bg.shpk` 的解压数据为 90,101,224 字节。读取器从数据中取出包级声明和材质参数默认值，按路径缓存，供引用同一个包的材质复用。 [项目 SHPK 读取](../src/FFXIVPatchParser/LocalGameSource.Shaders.cs)

## Alpha 在场景材质中的用法

在同一构建读取格里达尼亚旧街（`TerritoryType#133`）的场景材质。`bg/ffxiv/fst_f1/twn/common/material/f1t0_p1_trea3_h.mtrl` 使用 `bg.shpk`，材质键 `0xA9A3EE25` 覆盖为 `0x72AAA9AE`，对应 `ApplyAlphaTestOn`。这张树叶颜色纹理包含 45,181 个 Alpha 为 0 的像素和 4,412 个 Alpha 介于 0 与 255 之间的像素。场景中使用 `bgprop.shpk` 的 `f1t0_p1_trea3_b.mtrl` 有同一键值。四个已检查的场景包 `bg`、`bgprop`、`bguvscroll` 和 `bgcolorchange` 都把该键默认设为 `0x5D146A23`，把 `g_AlphaThreshold`（`0x29AC0223`）默认设为 `0.0`。项目先查材质覆盖，再看包默认值。树叶材质和 `TerritoryType#1369` 的 `bg/ffxiv/fst_f1/dun/f1de/material/f1de_se_stk01a.mtrl` 都覆盖为阈值 `0.5`。 [Meddle 着色器符号](https://github.com/PassiveModding/Meddle/blob/main/Meddle/Meddle.Formats/Constants/Names.cs) · [项目 Alpha 材质解析](../src/FFXIVPatchParser/LocalGameSource.MaterialAlpha.cs)

Alpha test 按阈值舍弃片元，适合树叶的镂空边界。Alpha blending 按透明度混合前景与背景，仍需处理深度和绘制顺序。TexTools 将 MTRL 低 16 位标志的 `0x10` 标为 `EnableTranslucency`。同一张颜色贴图的 Alpha 也可能供着色器作为遮罩，具体用途取决于包、材质键和选定的通道。预览根据包和材质的元数据识别已验证的场景镂空材质。 [场景纹理采样器](../src/FFXIVPatchParser/LocalGameSource.TerritoryTextures.cs) · [TexTools 材质标志](https://github.com/TexTools/xivModdingFramework/blob/master/xivModdingFramework/Materials/DataContainers/XivMtrl.cs#L37-L45)

## 角色着色器的发片遮罩

在国服简中 `2026.11.03.0000.0000` 打开希瓦模型 `chara/monster/m0640/obj/body/b0001/model/m0640b0001.mdl`，选中使用 `characterlegacy.shpk` 的材质。其基色和法线贴图的 Alpha 都是 255，发片遮罩存于法线贴图的蓝色通道。该通道有 93,366 个零值和 77,805 个中间值。材质将 `g_AlphaThreshold` 覆盖为 `0.5`，两个材质的标志分别为 `0x0D` 和 `0x1D`。 [材质和采样器读取](../src/FFXIVPatchParser/LocalGameSource.MaterialAlpha.cs)

Penumbra 的 `ProcessCharacterNormalOperation` 也读取法线蓝色作为角色材质不透明度。它对 `hair.shpk` 使用不同通道，皮肤法线的 Alpha 则可用于光照和湿润效果。当前预览只在 `character.shpk` 和 `characterlegacy.shpk` 有基色和可读法线采样器时应用这一规则，避免用错其他材质的通道。原始贴图仍可查看。 [Penumbra 材质导出](https://github.com/xivdev/Penumbra/blob/testing/Penumbra/Import/Models/Export/MaterialExporter.cs)

模型和地图共用 UV 遮罩裁切。模型预览中，新顶点保存原三角形的重心坐标，播放动作时先蒙皮原顶点，再更新裁切边界。希瓦样本由 23,386 增至 26,425 个顶点，完整覆盖的三角形直接复用。`m0001` 的法线蓝色全为 255，修复前后离屏图逐像素一致。发丝轮廓仍用 64 格遮罩近似，半透明区域受 WPF 混合限制。 [预览裁切与重心映射](../src/FFXIVPatchParser.Desktop/PreviewAlphaGeometry.cs) · [模型预览](../src/FFXIVPatchParser.Desktop/ModelPreview.xaml.cs)

## 预览与游戏渲染

WPF 的 `GeometryModel3D` 提供漫反射、高光和自发光材质。`ShaderEffect` 提供单个 PS 2.0 或 PS 3.0 像素程序作为位图效果，无法挂接本地包的 DX11 顶点程序或 Shader Model 5 像素程序。 [Microsoft WPF 三维材质](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/3-d-graphics-overview) · [Microsoft WPF ShaderEffect](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.effects.shadereffect)

地图预览将 `Mask` 材质的基色 Alpha 按阈值转成 UV 区域，再裁出覆盖区的几何。剩余表面按不透明材质绘制和写入深度，孔洞可以看到后方表面。两张相互交叉的镂空面在离屏 WPF 测试中交换绘制顺序，输出逐像素相同。场景默认按最多 16 格近似遮罩宽高，细枝轮廓会简化。`Blend` 材质仍由 WPF 混合，深度和排序限制继续适用。 [裁切实现](../src/FFXIVPatchParser.Desktop/PreviewAlphaGeometry.cs) · [场景提交](../src/FFXIVPatchParser.Desktop/MapPreview.Scene.cs)

用国服 `2026.11.03.0000.0000` 打开中拉诺西亚（`TerritoryType#134`）的 3D 场景。样本有 3,873 个实例，原始 LOD 提交前约 59 万三角面，裁切后实际提交约 201 万三角面，本机两次载入用时 2.2 秒和 3.2 秒。地图底部按实际提交量计数。该样本可用于评估独立渲染器改用逐像素裁剪后的视口开销。 [地图预览取样说明](territories.md)

用 D3D11 WARP 创建设备，取得功能级别 `11_0`。分别从上表各包取首个顶点程序（跳过包内 8 字节顶点附加头）和首个像素程序，按包内偏移与长度将 DXBC 传给 `CreateVertexShader` 和 `CreatePixelShader`，22 次调用均返回 `S_OK`。这项实测验证了样本字节码可以创建 Direct3D 11 着色器对象。 [Microsoft CreateVertexShader](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11device-createvertexshader) · [Microsoft CreatePixelShader](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11device-createpixelshader)

复用游戏程序仍需为选定节点和通道构造顶点输入布局，绑定对应的纹理、采样状态、骨骼和常量缓冲，再按游戏所需格式组织深度、混合、渲染目标与多通道顺序。`bg.shpk` 声明了视空间位置、法线和高光等资源，`character.shpk` 还声明了当前与上一帧骨骼矩阵以及光照和 G-buffer 输入。原始程序的绘制、通道绑定及与游戏画面的比对仍待验证。 [Penumbra 的单程序资源解析](https://github.com/Ottermandias/Penumbra.GameData/blob/main/Files/ShpkFile.cs#L733-L805)
