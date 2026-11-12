# Excel 原始结构与快照

[知识库首页](README.md)

本项目用 Lumina 7.7.2 从客户端 SqPack 读取 Excel 资源。读取一张陌生表时，先确定构建号、语言和表类型，再按 EXH 的列编号和偏移解码 EXD。字段名和引用关系可以随后叠加。 [项目依赖锁定](../src/FFXIVPatchParser/packages.lock.json) · [Lumina Excel 读取接口](https://lumina.xiv.dev/api/Lumina.Excel.ExcelModule.html)

## 从 EXL 到 EXD

| 资源 | 内容 | 项目里的用法 |
| --- | --- | --- |
| `exd/root.exl` | 表名目录，记录表名及其 ID | 通过 `game.Excel.SheetNames` 枚举，包括 `custom/` 等子目录下的表。 [Lumina ExcelModule](https://github.com/NotAdam/Lumina/blob/8802d66a3cb3032be2fcf998bb10e864221bdb13/src/Lumina/Excel/ExcelModule.cs) |
| `exd/<表>.exh` | 列定义、数据页、语言、表类型（Variant）、声明行数 | 每个列定义含原始类型和字节偏移。列编号取定义在数组中的顺序，从 `0` 起算。 [EXH 读取](https://github.com/NotAdam/Lumina/blob/8802d66a3cb3032be2fcf998bb10e864221bdb13/src/Lumina/Data/Files/Excel/ExcelHeaderFile.cs) |
| `exd/<表>_<StartId>[_chs].exd` | 对应数据页的行索引和行数据 | 按 EXH 的 `DataPages` 找每页的 `StartId`，用列类型和偏移解码值。 [本地表格读取](../src/FFXIVPatchParser/LocalGameSource.cs) |

语言按表选择。EXH 声明 `ChineseSimplified` 时读取 `_chs.exd`，否则有 `None` 就读取无语言后缀的 `.exd`。两种语言均缺失时，快照将表记为 `skipped-language`，本地浏览报告该表不支持。导出器逐页读取 EXH 声明的资源以检查存在性并取哈希，本地浏览首次打开该表时逐页检查。排查缺行时先核对语言和页清单，区分空表与资源缺页。 [快照导出器](../src/FFXIVPatchParser/SnapshotExporter.cs) · [本地表格读取](../src/FFXIVPatchParser/LocalGameSource.cs)

| EXH Variant | Lumina 接口 | 记录标识 |
| --- | --- | --- |
| `Default` | `GetSheet<RawRow>(language, name)` | `(RowId, null)` |
| `Subrows` | `GetSubrowSheet<RawSubrow>(language, name)` | `(RowId, SubrowId)`，一个父行可以含多条记录 |

两种读取接口都用 `ReadColumn` 按列编号读值。子行读取时，项目从 `RowOffset - 2` 取 EXD 存储的 `UInt16` 子行 ID。枚举索引描述遍历位置，CSV 和快照对齐使用实际 ID。快照的 `rowCount` 统计父行，`recordCount` 统计导出的每条记录。 [快照导出器](../src/FFXIVPatchParser/SnapshotExporter.cs) · [本地按行读取](../src/FFXIVPatchParser/LocalGameSource.Tables.cs)

## 字段名与类型的来源

| 需要确认的信息 | 使用的证据 |
| --- | --- |
| 字节如何解码 | 该构建的 EXH 提供类型、顺序和偏移。`PackedBool` 还带位号，同一字节可存多个布尔列。 |
| 列叫什么 | 同构建的 CHS CSV 表头可提供 `Name`、`Icon` 等名称。未验证的 Raw 列用 `Column0`、`Column1` 等显示。 |
| 数值指向哪里 | CSV 中的 `Image`、目标表名，或经核对的 SaintCoinach Definitions 转换规则，提供图标与引用语义。 |

比较两个构建的原始类型和字节偏移时，各取一份对应构建的 SqPack 或 Raw schema。CSV 的类型行可能写 `Image` 或目标表名，读取物理类型时回到 EXH。匹配 CSV 与外部定义的检查见 [表格 Diff 与关联](table-diff.md#关联字段怎样验证)。 [原始列模型](../src/FFXIVPatchParser/TableDiff.cs) · [关联解析器](../src/FFXIVPatchParser/RelationResolver.cs)

### 参考工具从哪里读起

- **SaintCoinach** 按 EXH 中的类型和偏移读原值。`RelationalColumn` 用列索引在对应表的 Definitions JSON 里找字段名和 `converter`。 [原始列](https://github.com/xivapi/SaintCoinach/blob/0b22b78f97951bb66bfa78fb561aa4c7bf7af7cd/SaintCoinach/Ex/Column.cs) · [关系列](https://github.com/xivapi/SaintCoinach/blob/0b22b78f97951bb66bfa78fb561aa4c7bf7af7cd/SaintCoinach/Ex/Relational/RelationalColumn.cs)
- **Alpha** 把 Lumina Raw 读取与外部定义结合。核对的版本从 SaintCoinach `master` 或 EXDSchema `schemas/latest` 取定义。分析国服旧构建时，要另外对照该构建的列布局。 [SaintCoinachResolver](https://github.com/NotNite/Alpha/blob/d09167974abe2e5c1274cff8f022ca4037c8f59d/Alpha/Services/Excel/SaintCoinach/SaintCoinachResolver.cs) · [EXDSchemaResolver](https://github.com/NotNite/Alpha/blob/d09167974abe2e5c1274cff8f022ca4037c8f59d/Alpha/Services/Excel/ExdSchema/ExdSchemaResolver.cs)
- **HaselDebug** 使用 Lumina.Excel 的生成类型或插件自己的实验表类型。`ExcelTable` 通过公开属性显示字段，`RowRef` 类型提供引用目标。找不到可用的生成类型时，Raw 分支按列号显示 `RawRow`。 [ExcelTab](https://github.com/Haselnussbomber/HaselDebug/blob/6dd2f0a12dccf3f496c69d9ad96fbc89644f6178/HaselDebug/Tabs/Excel/ExcelTab.cs) · [ExcelTable](https://github.com/Haselnussbomber/HaselDebug/blob/6dd2f0a12dccf3f496c69d9ad96fbc89644f6178/HaselDebug/Tabs/Excel/ExcelTable.cs) · [ExcelSheetColumn](https://github.com/Haselnussbomber/HaselDebug/blob/6dd2f0a12dccf3f496c69d9ad96fbc89644f6178/HaselDebug/Tabs/Excel/ExcelSheetColumn.cs)

国服 7.58 的 `BNpcBase` 有 27 个 EXH 列。同构建 CSV 和 SaintCoinach 都为其中 14 列给出相同字段名。第 0 列是 `Behavior`，第 1 列是 `Battalion`。第 4 列 `Scale` 的 EXH 类型是 `Float32`，SaintCoinach 导出的类型名为 `single`。校验时需识别这个别名，其余 13 列仍显示 `ColumnN`。 [BNpcBase 定义](https://github.com/xivapi/SaintCoinach/blob/0b22b78f97951bb66bfa78fb561aa4c7bf7af7cd/SaintCoinach/Definitions/BNpcBase.json) · [字段校验](../src/FFXIVPatchParser/RelationResolver.cs)

同构建表的列数和编号对齐后，程序逐列校验类型。`ClassJob` 第 30 列在 EXH 中是字符串，CSV 却标为 `Item`，本地浏览只保留这一列的 Raw 名称。`Item` 和 `ENpcBase` 的模型字段则是 64 位打包值，CSV 用 `int64` 表示，EXH 用 `UInt64`。程序采用核对过的字段名，读取和显示仍保留 EXH 的原始类型。 [字段校验](../src/FFXIVPatchParser/RelationResolver.cs)

## Raw 快照如何保存数据

快照 v1 包含 `manifest.json`，以及每张表的 `sheets/<表>.schema.json` 和 `sheets/<表>.rows.jsonl.gz`。schema 记录语言、Variant、列编号、类型、偏移及来源 EXH/EXD 的 SHA256。行文件每行一条 JSON，字段为 `rowId`、`subrowId` 和按列顺序排列的 `values`。 [快照导出器](../src/FFXIVPatchParser/SnapshotExporter.cs) · [值序列化](../src/FFXIVPatchParser/SnapshotValue.cs)

| 值 | 快照表示 | 比较依据 |
| --- | --- | --- |
| SeString | `text` 保存可见文本，`raw` 保存原始 SeString 字节的 Base64 | `raw`。两个字符串可以显示相同文本，同时包含不同控制载荷。 |
| Float32 | `value` 供展示，`bits` 为八位十六进制 IEEE 754 位模式 | `bits`。可区分正负零、不同 NaN 位模式，并保留无穷。 |
| Int64、UInt64 | 十进制字符串 | 完整字符串。JavaScript `Number` 对大整数会舍入。 |
| 其他整数、布尔 | JSON 数字或布尔值 | 对应类型的原值。 |

`manifest.json` 记录构建号、Lumina 版本、各表状态、行数、压缩行文件 SHA256 和 Excel pack 指纹。导出前后检查 `0a*.win32.*` 的相对路径、长度和 SHA256。输入变化或表读取错误时 `complete` 为 `false`。主动筛表和语言跳过单独记在清单里，核对覆盖范围时同时查看表计数和状态。 [快照导出器](../src/FFXIVPatchParser/SnapshotExporter.cs) · [快照读取器](../src/FFXIVPatchParser/TableSources.cs)

## 基准样本

CHS 7.58，构建 `2026.11.03.0000.0000`。项目全量导出 7,912 张表，共 1,801,427 条记录。语言分布为 7,126 张 CHS 表、786 张无语言表。表类型分布为 7,780 张普通表、132 张子行表。该样本有 21 张空表、24 张超过 100 列的表，最宽的 `CharaMakeType` 有 3,536 列。此次导出错误和语言跳过均为 0。 [快照验证器](../scripts/Verify-Snapshot.py) · [CHS 7.58 提交](https://github.com/InfSein/ffxiv-datamining-mixed/commit/9e2c05c0ed8205d5397031152f62a34900355de4)

| 同构建 CSV 抽查 | 记录数 | 列数 | 结果 |
| --- | ---: | ---: | --- |
| `Stain` | 129 | 9 | 全表单元格一致 |
| `ClassJob` | 46 | 51 | 全表单元格一致 |
| `GilShopItem` | 16,157 | 10 | 全表单元格和子行 ID 一致 |

复验时按列编号和完整行键对齐这三张表。CSV 的字符串检查以显示文本为准，SeString 字节和浮点位模式可用快照自测复验。导出命令、全量校验及记录方式见 [验证与复现](verification.md)。 [快照验证器](../scripts/Verify-Snapshot.py) · [快照自测](../src/FFXIVPatchParser/SnapshotTests.cs)
