# 表格 Diff 与关联

[知识库首页](README.md)

历史 CSV 用于比较两个 CHS 构建导出的表格。Raw 快照用于比较各自 SqPack 的物理列结构、SeString 字节和浮点位模式。复现 Diff 时先记录数据来源、旧新构建号及选表范围。 [Excel 原始结构与快照](excel.md) · [表格数据源](../src/FFXIVPatchParser/TableSources.cs)

## 补丁范围怎样匹配历史表格

1. 从选中的 ZIP 取得 `from` 和 `to` 数据版本。分别在国服 `ver2.dat` 的 `packages` 中按 `to` 查对应条目，从 `versionView` 取下划线之前的完整游戏构建号。 [国服补丁](cn-patches.md) · [版本匹配](../src/FFXIVPatchParser/DataminingSource.cs)
2. 在 `InfSein/ffxiv-datamining-mixed` 的 `chs` 提交正文中精确匹配两个构建号，得到两个提交 SHA。现有程序查询 `chs` 最近 100 条提交。显示标题和提交日期便于人读，完整构建号用来区分同一版本名下的热修。 [版本匹配](../src/FFXIVPatchParser/DataminingSource.cs) · [上游提交约定](https://github.com/InfSein/ffxiv-datamining-mixed/blob/master/README.md)
3. 读取两个固定提交的 `chs` 递归 Git tree，按 CSV 路径取并集。只在一侧出现的路径标记为新增或删除，两侧路径的 blob SHA 不同则标记为修改。GitHub 返回 `truncated` 时停止，避免从缺失目录推导删除。 [历史 CSV 目录](../src/FFXIVPatchParser/DataminingSource.cs) · [变化表清单](../src/FFXIVPatchParser/TableSources.cs)
4. 打开变化表时按需下载 CSV，读取前用 Git blob SHA-1 校验内容。计算包含 `blob <字节数>` 和一个 NUL 组成的头部，以及完整文件字节。清单中的 SHA 固定内容，补丁 ZIP 提供选定的版本范围。 [CSV 缓存与校验](../src/FFXIVPatchParser/DataminingSource.cs)

历史 CSV 由 `InfSein/dumpcsv` 从客户端 SqPack 导出。该工具基于 `thewakingsands/dumpcsv` 修改，底层通过 SaintCoinach 读取 Excel。上游 `npm run unpack:chs` 生成 `chs/`。CSV 的前三条记录依次为列编号、字段名、类型，`key` 列保存 `RowId` 或 `RowId.SubrowId`。解析器按带引号的 CSV 读取，保留字段内的逗号、引号和换行。历史 CSV 比较覆盖导出的列定义和文本值。原始偏移及完整 SeString 载荷需从对应构建的 SqPack 取得。 [上游导出说明](https://github.com/InfSein/ffxiv-datamining-mixed/blob/master/README.md) · [InfSein/dumpcsv](https://github.com/InfSein/dumpcsv) · [thewakingsands/dumpcsv](https://github.com/thewakingsands/dumpcsv) · [CSV 解析器](../src/FFXIVPatchParser/TableSources.cs)

| 补丁数据版本 | 完整构建号 | CHS 样本提交 |
| --- | --- | --- |
| `0.0.0.20` | `2026.05.01.0000.0000` | [7.5#hf1 · 01ae401](https://github.com/InfSein/ffxiv-datamining-mixed/commit/01ae40195cede1f5ac61ed57cfcac62c20a90c0c) |
| `0.0.0.21` | `2026.05.25.0000.0000` | [7.51 · 51ee0ea](https://github.com/InfSein/ffxiv-datamining-mixed/commit/51ee0ea4819fb08fb4451d4bb0ced66f16bc2331) |
| `0.0.0.25` | `2026.07.16.0001.0000` | [7.55 · 28f1b3b](https://github.com/InfSein/ffxiv-datamining-mixed/commit/28f1b3b5b6f6c224960d62a496c99fd147cb83f0) |
| `0.0.0.26` | `2026.08.05.0000.0000` | [7.55#hf1 · d22f1a3](https://github.com/InfSein/ffxiv-datamining-mixed/commit/d22f1a3d129a70bf8b11a778fe124d9ddd3a56b1) |
| `0.0.0.29` | `2026.09.15.0000.0000` | [7.56#hf2 · bbb112c](https://github.com/InfSein/ffxiv-datamining-mixed/commit/bbb112c374d27ae032bdfdaa277295879c5a834d) |
| `0.0.0.30` | `2026.10.06.0000.0000` | [7.57 · 187a6a6](https://github.com/InfSein/ffxiv-datamining-mixed/commit/187a6a6d18aa0b40652bade7a433ca7526a3049f) |
| `0.0.0.31` | `2026.11.03.0000.0000` | [7.58 · 9e2c05c](https://github.com/InfSein/ffxiv-datamining-mixed/commit/9e2c05c0ed8205d5397031152f62a34900355de4) |

这些映射曾用官方 `versionView` 与提交正文核对。`0.0.0.30 → 0.0.0.31` 的集成验证列出 91 张变化表。抽查的 `Action` 为 16 条修改、51,485 条未变化、71 列，`ActionTransient` 为 2 条修改、51,499 条未变化、1 列。两张抽查表均无新增或删除行。91 是文件清单层的计数，行变化由打开各表后计算。 [变化表清单](../src/FFXIVPatchParser/TableSources.cs) · [行列比较](../src/FFXIVPatchParser/TableDiff.cs) · [验证与复现](verification.md)

## 从变化表到变化单元格

| 层级 | 对齐与判断 |
| --- | --- |
| 表 | CSV 比较 Git blob 指纹。Raw 快照比较压缩行文件 SHA256 与 schema 文件 SHA256 组成的指纹。比较要求两侧格式相同。只要任一侧是部分导出的 Raw 快照，两侧导出的表集合就必须相同。 |
| 行 | 对齐 `(RowId, SubrowId)`。只有一侧存在的记录分别记为新增或删除。 |
| 列 | 按列编号对齐。列有无、名称、类型或偏移变化会标记为结构变化。编号在版本之间重新分配时，结合两侧 schema 追字段含义。 |
| 单元格 | Raw 按规范值比较，SeString 用原始字节，Float32 用位模式。CSV 按导出字段的文本比较。两侧都有的行出现列结构变化或值变化时计入修改行。 |

同一张表可以在文件清单中变化，打开后只见格式变化或空表结构变化。Raw schema 也含来源文件哈希。遇到这种情况，查看列结构与行结果，确认变化发生的层级。 [变化表清单](../src/FFXIVPatchParser/TableSources.cs) · [行列比较](../src/FFXIVPatchParser/TableDiff.cs) · [Diff 界面](../src/FFXIVPatchParser.Desktop/MainWindow.xaml.cs)

Diff 用红色展示旧值、绿色展示新值。列类型为 `String` 或 `str` 时，值内高亮按 Unicode 文本元素切分，保留组合字符。算法先去掉公共前后缀，再对中间做最长公共子序列。中间两段的长度乘积超过 `250,000` 时，将中间区整体高亮。数值和布尔变化使用整格底色。若 SeString 字节有变化而显示文本相同，整格仍标记变化。列筛选以原始展示值为准，同列匹配任一侧，多列条件同时满足。 [字符比较](../src/FFXIVPatchParser/TextDiff.cs) · [单元格展示与筛选](../src/FFXIVPatchParser/CellPresentation.cs) · [Diff 界面](../src/FFXIVPatchParser.Desktop/MainWindow.xaml.cs)

## 关联字段怎样验证

1. 本地 SqPack 用 `ffxivgame.ver`，Raw 快照用清单中的 `gameVersion`。找到同一构建的 CHS CSV 后，只解析前三条记录来取得语义表头。对照列数、编号和可核对的原始类型，例如 `str` 对应 `String`，`bit&01` 对应 `PackedBool0`。CSV 的 `Image` 或表名属于逻辑类型，引用解析时还要检查当前单元格能否读成 `UInt32`。 [本地数据源](../src/FFXIVPatchParser/LocalGameSource.Tables.cs) · [关联解析器](../src/FFXIVPatchParser/RelationResolver.cs)
2. CSV 类型 `Image` 可生成图标规则，类型名与源中的表名一致时可生成直接引用。其他规则由 SaintCoinach Definitions 补充，按同一列编号和字段名匹配。手动导入可从本地表格的“导入定义…”选择目录。单独解释匿名 Raw 列还可用代码中的显式绑定，绑定同时校验来源标签、列结构哈希和定义文件哈希。 [定义读取与绑定](../src/FFXIVPatchParser/SaintCoinachDefinitions.cs) · [关联解析器](../src/FFXIVPatchParser/RelationResolver.cs)
3. 解析器处理 `link`、`multiref`、`complexlink` 和 `icon`。复杂引用按 `when` 条件选择候选表，可用目标字段作为键，再按 `project` 或目标表 `defaultColumn` 取显示值。未声明 `defaultColumn` 时可用已验证的 `Name`。一组子行或目标键匹配多条记录时保留原值。递归解析检查重复单元格并限制深度。 [关联解析器](../src/FFXIVPatchParser/RelationResolver.cs)
4. Diff 的旧侧和新侧各用自己的数据源。关联值用于显示、导航和图标预览，差异计算和筛选仍用原值。关联未完成时可切换 Raw，已验证的字段名仍可显示。绿色新侧支持按表名、行号、子行号和列定位本地表格。 [Diff 关联界面](../src/FFXIVPatchParser.Desktop/MainWindow.Relations.cs) · [本地关联界面](../src/FFXIVPatchParser.Desktop/SqPackBrowser.Relations.cs)

CHS 7.58 的本地抽查验证了 `Action` 对 `ActionCategory` 的行号引用，以及 Icon 字段到本地纹理的解析。Icon 路径形如 `ui/icon/<千位分组>/<六位编号>.tex`。合成用例单独检查了关联显示保留单元格规范值。需要从表关系追到资源路径时，可继续看 [SqPack 媒体](sqpack-media.md) 和 [模型目录](model-catalog.md)。 [关联解析器](../src/FFXIVPatchParser/RelationResolver.cs)

悬浮窗用 `目标表#行号` 显示引用，例如 `ModelChara#5336`。目标有可用名称时追加名称，有已验证的图标列时按 100% 原始像素显示图标，点击可打开本地图片页。Diff 的旧、新侧分别从各自的表数据取名称和图标编号，纹理从本地 SqPack 读取。 [关联解析器](../src/FFXIVPatchParser/RelationResolver.cs) · [Diff 关联界面](../src/FFXIVPatchParser.Desktop/MainWindow.Relations.cs)

本地表格的关联模式也在引用名称前显示目标图标，例如 `AozAction` 中「千针刺」的 `Action` 引用。`Action` 的 Icon 列只显示图标，单击后可看原始编号。表格默认按表头定宽，右上角的「按内容调整列宽」以当前页各列的最宽单元格为准。 [本地表格展示](../src/FFXIVPatchParser.Desktop/SqPackBrowser.TablePresentation.cs)

`ReplaceAction` 的 `Action` 直接连到 `Action` 表。`Param1` 根据 `Type1` 选择目标，例如类型 2 连到 `Status`，类型 3 连到 `Trait`。国服 7.58 的 `1.0`、`1.5`、`1.7` 子行分别可查到 `Action#28`「钢铁信念」、`Status#3827`「祈告剑预备」和 `Trait#565`「安魂祈祷精通」。三个目标各有自己的 Icon 字段。 [ReplaceAction 定义](https://github.com/xivapi/SaintCoinach/blob/0b22b78f97951bb66bfa78fb561aa4c7bf7af7cd/SaintCoinach/Definitions/ReplaceAction.json)

`ModelChara` 行描述模型，没有自己的名称列。同一模型可被战斗 NPC、宠物或坐骑等多条记录引用。本地悬浮窗优先用当前来源行对应的名称，也可列出本地模型目录中找到的多个名称。历史 Diff 按各自版本解析 `ModelChara#行号`。 [名称到模型的来源链](model-catalog.md)

## 按行查找和缓存

- 本地普通行引用用 `TryGetRow` 直接读取目标行。子行按父行号定位，最多解码两条以判断引用是否唯一。按其他字段查键时读整张目标表。历史 CSV 和 Raw 快照的整表结果可按 `RowId` 建索引。 [本地按行读取](../src/FFXIVPatchParser/LocalGameSource.Tables.cs) · [关联解析器](../src/FFXIVPatchParser/RelationResolver.cs)
- CSV 按提交和表名缓存。共享下载限制为四个并发请求，界面等待取消后已启动的下载继续。解析表头也要先取得并校验完整文件，因而大表的首次关联可能先显示原值。 [CSV 缓存与校验](../src/FFXIVPatchParser/DataminingSource.cs)
- `tmp/definitions/saintcoinach-upstream` 可缓存 SaintCoinach 固定提交的 811 份定义，手动导入的定义放在同级 `saintcoinach` 目录。完整缓存就绪后，缺少定义的表也无需逐个请求 GitHub。上游随附的 `game.ver` 为 `2024.06.18.0000.0000`。国服当前表仍用同构建 CHS CSV 核对列号和字段名，再按列合并转换规则。 [固定版 Definitions](https://github.com/xivapi/SaintCoinach/tree/0b22b78f97951bb66bfa78fb561aa4c7bf7af7cd/SaintCoinach/Definitions) · [定义加载](../src/FFXIVPatchParser/SaintCoinachDefinitions.cs)
- 关联解析器按数据源维护最近使用的 32 个读取结果，区分整表和按行结果。单次读取最多等待 5 秒，单次解析累计有 12 秒读取预算。失败的读取项通常等待 15 秒再试。定义与行数据各自缓存。 [关联解析器](../src/FFXIVPatchParser/RelationResolver.cs)

## 故障诊断

| 现象 | 排查步骤 |
| --- | --- |
| 找不到准确构建 | 核对 `from`、`to` 对应的官方 `versionView`、提交正文里的完整构建号和最近 100 条的查询范围。两端各自匹配成功后再比较。 |
| 提示目录不完整或 GitHub 请求失败 | 区分递归 tree 的 `truncated` 与 API 限流。固定版本目录和已校验 CSV 可复用。 |
| CSV 或 Raw 快照校验失败 | CSV 移走报错的单表缓存后从固定提交重取。Raw 重新校验快照清单和行文件，必要时从原构建重新导出。 |
| 两份快照无法比较 | 检查 `complete`、格式版本和两份快照的表集合。主动选表时使用同一选表清单。 |
| 表头仍是 `ColumnN` 或引用只显示数字 | 核对构建匹配、原始列类型、定义列编号与名称。若已找到目标行，再核对目标行是否唯一及默认显示列是否可解析。 |
| 关联读取超时 | 看状态栏区分版本语义元数据、关联目标读取和定义读取。CSV 下载可在界面等待结束后继续，缓存就绪后重新打开该表或单元格。 |

具体复现步骤见 [验证与复现](verification.md)。 [数据源校验](../src/FFXIVPatchParser/TableSources.cs) · [版本匹配与下载](../src/FFXIVPatchParser/DataminingSource.cs) · [关联状态](../src/FFXIVPatchParser/RelationResolver.cs)
