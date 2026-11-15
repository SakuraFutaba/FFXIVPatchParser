# SqPack 文件系统索引与预览

## 目录和地址

SqPack 的 `.index` 主表保存 64 位键，高 32 位是目录哈希，低 32 位是文件名哈希。`.index2` 保存 32 位完整路径哈希。两种索引都可以给出数据分片和偏移。地址低位的碰撞标志指向文本记录，必须先查文本表，才能拿到实际地址。 [Alpha 文件系统](https://github.com/NotNite/Alpha#features) · [XivAlexander 索引结构](https://github.com/Soreepeong/XivAlexander/blob/main/XivAlexanderCommon/Sqex/Sqpack.h)

目录遍历本地安装实际存在的资料片、资源类别、分块和 `.dat`。在同一索引分块内，两边只有一条记录指向同一地址时合并，并保留两种原始哈希。不能唯一配对的条目保留各自的哈希和地址。文本碰撞表里能校验的原路径直接用于目录。 [文件索引实现](../src/FFXIVPatchParser/LocalGameSource.FileSystem.cs)

目录以索引为全集。EXH 与分页信息、地图表、BGM 表、图标及模型路径目录用于补充名称。只有键命中才显示完整路径。如果目录可识别而文件名未知，条目显示在已知目录内，文件名用 `~哈希` 表示。其余目录用目录哈希标识，只有 `.index2` 的未知条目集中在完整路径哈希下。路径发现的覆盖率与能否按地址读取是两项独立的指标。 [目录构建和筛选](../src/FFXIVPatchParser.Desktop/SqPackFileTreeIndex.cs)

## 按需读文件

打开文件系统只读索引和路径来源需要的少量元数据。文件节点随目录展开创建。选中文件后，用索引位置打开该文件。Standard 容器条目只解压与 Hex 窗口相交的块，模型和纹理交给 Lumina 解包单个文件，上限为 256 MiB。右侧 Hex 偏移从解包数据的开头计数，标题显示的是压缩资源在 `.dat` 中的地址。 [分块读取](../src/FFXIVPatchParser/LocalGameSource.FileSystem.cs) · [文件预览](../src/FFXIVPatchParser.Desktop/SqPackBrowser.FileSystem.cs)

模型和纹理可依据容器条目类型预览，SCD 依据 `SEDBSSCF` 签名。可识别的 EXH／EXD 打开关联的整张表，显示当前表格语言和筛选规则。带路径的场景布局和游戏地图纹理可复用地图预览。其余条目显示 Hex。Patch 的同名页面显示补丁清单中的容器和文件差分，以及 VCDIFF 写入区间和校验结果。 [本地预览分流](../src/FFXIVPatchParser.Desktop/SqPackBrowser.FileSystem.cs) · [补丁 Hex](../src/FFXIVPatchParser.Desktop/HexDiffView.xaml.cs)

## 国服 7.58 样本

以国服简中 `2026.11.03.0000.0000` 安装为例，读取 12 类、73 个数据分片，得到 1,803,008 个可定位条目。本次目录使用的名称来源覆盖 302,840 条。副索引普通表少 62 个地址。计入副索引的 31 个碰撞指针对应的 62 条文本记录后，两份索引地址完全一致，全部条目可记录完整路径哈希。

复现时从每个分片抽取两个源文件，比较 Hex 前 512 字节及偏移 100 开始的窗口与 Lumina 整文件解包结果。146 个样本全部一致。追加验证包含跨 SCD 压缩块窗口、模型和纹理直接按地址打开，以及单独构造的副索引独有地址和多个别名。样本计数和名称覆盖随安装和本地目录变化。 [本地读取实现](../src/FFXIVPatchParser/LocalGameSource.FileSystem.cs)
