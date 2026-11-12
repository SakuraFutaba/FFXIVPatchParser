# FFXIVPatchParser

FF14 国服 V3 补丁检查、Raw Excel 快照与表格 Diff 桌面工具。

[FF14 解包知识库](knowledge/README.md)记录补丁、SqPack、表格、模型、动作和音频的格式与复现实验。

目前支持：

- 只读 SqPack，导出简体中文及无语言 Excel 表，包括普通行、子行、空表与宽表。
- 按 EXH 保留列类型、偏移和原始值；SeString 保存可读文本与原始字节。
- 查询官方补丁目录，探测包文件可访问性，下载并校验最新 ZIP。
- 检查补丁路径映射、Excel 差分校验值及 SqPack 资源分类。
- WPF 桌面界面包含补丁工作区、红绿表格 Diff 和图片 Diff。补丁工作区集成官方目录、下载和变化资源。
- 直接浏览本地 SqPack 的 Excel 表格、图片、模型和音频。表格可切换 Raw 和关联视图，无需先导出快照。

补丁 VCDIFF 的完整还原尚未实现；“变化资源”仍是文件 / SqPack 容器级清单。图片浏览的支持范围见下文。

## 环境

桌面发布版支持 Windows x64，自带 .NET 运行时，无需安装 SDK 或 PowerShell 7。源码构建需要 .NET 10 SDK，构建和发布脚本兼容 Windows PowerShell 5.1 / PowerShell 7；补丁研究脚本仍需 PowerShell 7。Lumina 固定为 7.7.2，传递依赖记录在 packages.lock.json。

所有下载、缓存、构建产物、快照和本地测试数据放在项目的 tmp/ 下，并被 Git 忽略。请通过 scripts/Invoke-Dotnet.ps1 运行 .NET 命令，使 NuGet 与临时目录也位于项目内。

默认构建从 NuGet 使用固定版本的 Lumina。开发时可以显式选择 XIVLauncherCN 的开发程序集，MSBuild 通过 `DalamudLibPath` 查找 DLL，默认取 `$(APPDATA)/XIVLauncherCN/addon/Hooks/dev`。可传入 `-p:DalamudLibPath=...` 覆盖目录。独立程序会将需要的 DLL 复制到输出目录。

```powershell
./scripts/Invoke-Dotnet.ps1 build src/FFXIVPatchParser.Desktop '-p:UseDalamudLibraries=true'
```

## 启动桌面界面

**直接使用 EXE：** 打开 tmp/publish/win-x64/FFXIVPatchParser.Desktop.exe。移动或分发程序时复制整个 win-x64 文件夹，保留旁边的 DLL 和运行时文件；不要只复制 EXE。在源码仓库内运行时复用项目 tmp，独立运行时使用程序目录下的 tmp。

**从源码重新发布：**

```powershell
./scripts/Publish-Desktop.ps1
```

发布采用 [.NET 自包含目录部署](https://learn.microsoft.com/dotnet/core/deploying/)，输出、运行时包和 NuGet 缓存均在项目 tmp 下。发布包不包含本地游戏数据或调试符号。

**开发时从项目目录运行：**

```powershell
./scripts/Start-Desktop.ps1
# 或直接使用通用 .NET 包装脚本
./scripts/Invoke-Dotnet.ps1 run --project src/FFXIVPatchParser.Desktop
```

- **补丁工作区**可在补丁目录与变化资源之间切换。目录列出官方更新、分卷数、大小和可访问性，可下载所选更新或查看本地已有的补丁。程序校验文件大小与 MD5，通过校验的分卷会复用。也可直接打开国服 V3 ZIP，同一更新的多个分卷可以一起选。变化资源支持路径搜索、类别筛选和查看前后 MD5、ZIP 条目与差分文件信息。资源仓库一列显示 `ffxiv`、`ex1` 等 SqPack 仓库目录。
- **表格 Diff** 在加载补丁后自动匹配 CHS 历史构建。顶部显示游戏版本区间，左侧列出有变化的表。选择一张表后按 RowId、SubrowId 和列编号比较。
- **本地 SqPack** 和 **Diff** 分别是可折叠的导航组，各有表格、图片、模型、音频入口。本地表格直接读取 CHS 或无语言数据，按行列分页并支持筛选。本地图片可按图标编号查找纹理，也可输入游戏内 `.tex` 路径直接打开。
- **设置**管理游戏目录及已有补丁所在的目录。支持游戏根目录、`game` 或 `sqpack`。设置存放在被忽略的 `tmp/ui/` 中，也可通过 `FFXIV_GAME_PATH` 指定游戏目录。自定义补丁目录供程序查找和打开已有 ZIP，新下载仍保存到 `tmp/patches/`。

表格 Diff、本地 SqPack 和图片 Diff 的表名与路径使用靠左、缩进紧凑的目录树。搜索保留匹配项的父目录，点击叶子时按完整路径读取。可拖动表格 Diff 中间的分隔条调整目录宽度。

### 图片、模型与音频

**图片 Diff** 是与“表格 Diff”并列的页面，也可从补丁的“变化资源”进入。它与“本地 SqPack”共用游戏目录。分析时校验补丁目标 UI 索引、数据容器及差分的 MD5，解析 VCDIFF 写入区间，列出相交的纹理并预览本地目标图像。本地文件必须匹配所选补丁的目标版本。列表包含新增、更新或容器重排的候选，暂不支持逐张图片的精确新旧比较和删除项。未解析出原始路径的 UI 图片以容器、偏移和哈希标识，仍可查看。分析只读游戏容器，无需安装补丁或复制容器。

“本地图片”和“图片 Diff”共用预览器，新打开的图片默认以 100% 显示。滚轮以光标位置为中心缩放，按住鼠标左键拖动平移。可一键适应窗口或回到 100%，并在棋盘、深色和浅色背景之间切换。尺寸、缩放比例和预览信息显示在底部。“保存 PNG…”导出原始分辨率和 Alpha 透明通道。

「本地 SqPack → 模型」默认从本地 Excel 表和 SqPack 索引建立分类目录，包括宠物、召唤兽、坐骑、时尚配饰、战斗 NPC 和事件 NPC。可切换按模型编号分组，也可显式扫描容器查找未列入表的模型。输入游戏内 `.mdl` 路径可直接打开。选中条目后才读取网格、IMC 材质变体及纹理。视图支持旋转、平移、缩放和重置。模型缺少原始路径时，索引扫描会显示容器、偏移和哈希。

模型目录将 `Companion`、`Mount`、`Ornament`、`BNpcBase` 和 `ENpcBase` 的模型引用连到 `ModelChara`。怪物路径里的 `m####` 是模型族编号，`b####` 是 MDL 身体子模型编号。`ModelChara` 的变体对应 IMC 子集，具体材质版本由 IMC 解析。SKLB 可以走共享骨架路径。人形 NPC 的完整外观需要组合装备和自定义数据，当前目录只列出可验证的单个模型资源。

战斗 NPC 的名称用 Anamnesis 的社区映射连接 `BNpcBase` 与本地 `BNpcName`，事件 NPC 的名称优先来自本地 `ENpcResident`。可在模型详情跳到来源行、可用的名称行和 `ModelChara`。例如 `BNpcBase #5` 的名称是 `BNpcName #408`「婴猴」，模型是 `ModelChara #31` 对应的 `m0004b0001`。社区英文别名仅在英文单数名精确匹配且简中结果唯一时翻译。FFCafé 查询失败时可复用缓存或通过 InfSein 固定版本的 `en` 和 `chs` 两表按行号对照。社区映射及翻译缓存在 `tmp/npc-names`。

动作面板显示已验证存在的 `.pap` 候选和动作槽。已知怪物本体会优先选中 `resident/monster.pap` 的 Idle。点击「Godbert…」选已安装的目录，然后用「播放」预览。桌面程序通过 32 位子进程调用 Godbert 的 `hkAnimationInterop.dll`，解码后按模型骨骼名称蒙皮。播放支持已验证的怪物本体及相应的 PAP 和四权重蒙皮布局。FBX 动作导出和其他模型的播放仍需进一步支持。Godbert 的导出将网格、骨架与收集到的 PAP 交给 FBX 导出器。

「本地 SqPack → 音频」可从 BGM 列表选择或输入游戏内 `.scd` 路径。可切换音轨、播放、暂停、定位和导出。Windows 解码失败时，程序可使用 PATH 中已有的 FFmpeg 转换为 PCM，回退试听最长五分钟。导出保留完整音轨。

「模型 Diff」和「音频 Diff」从通过校验的本地补丁目标版本预览写入候选。程序使用 VCDIFF 写入区间筛选相关 SqPack 容器中的资源。可以选择单个容器缩短扫描时间。容器重排可能产生额外候选，删除项和旧版本内容需要旧版数据。

### 表格比较与关联

Diff 中旧行为红色、新行为绿色，变化单元格使用底色，仅字符串类型的单元格内具体增删字符使用更深红绿高亮；数字和布尔字段保留整格高亮，不进行字符比较；新增和删除行也保留 + / − 标记。支持只看变化列；点击列头（包括 Row / Subrow）输入关键词筛选，匹配该列旧值或新值，多列条件同时满足，支持清除此列或清除全部筛选。点击单元格，在附近的浮窗中查看和复制完整旧、新值，字符串同样显示字符级高亮；点击外部或按 Esc 关闭。每页 100 条变化记录、40 列，分页覆盖全部内容，不截断宽表。中文及组合 Unicode 字符保持完整，支持多处字符增删；超长文本按公共前后缀高亮中间差异以限制计算量。表列表采用单行紧凑布局，状态通过悬停查看。结构变化在列标题中标注；只改变 CSV 格式或文件结构、没有行值变化的表会显示说明。

本地表格和表格 Diff 默认显示关联值。切换到 Raw 后，单元格显示原值，表头继续显示已验证的字段名。单击单元格打开浮窗，再点同一格关闭，点另一格切换。浮窗可查看完整值、跳到目标行或打开 Icon 纹理。程序按同一游戏构建匹配 CHS CSV，并核对列数、列编号和原始列类型。SaintCoinach Definitions 补充同列编号、同字段名的转换规则。可在本地表格中点击“导入定义…”选择定义目录。Diff 的旧、新引用分别用各自版本的数据解析，筛选与差异判断以原始值为准。

SqPack 的 EXH 记录列的顺序、原始类型和字节偏移，EXD 保存行值。本地浏览和 Raw 快照使用 Lumina 直接读取这些结构。`Name`、`Icon` 等字段名和跨表引用规则由外部定义提供。SaintCoinach 从 EXH 读取原始列布局，由关系定义提供名称和逻辑类型。Alpha 将 Lumina Raw 读取和 SaintCoinach 或 EXDSchema 的定义结合。HaselDebug 的 Raw 表按序号显示列，命名表使用由 schema 生成的类型。要从两个构建分别读取原始类型和偏移，需要各自构建的 SqPack。历史 CSV 的类型行可能包含 `Image` 或目标表名等逻辑类型。

本地表格按字段类型调整列宽。关联视图用蓝色粗体显示可跳转的引用名称，例如“魔法”。点开浮窗可看到 `2 → ActionCategory · 魔法` 及原始值。目标没有可读名称时，单元格显示原始编号。经验证的 Icon 字段在编号旁加载本地缩略图。

本地表的常规引用按行号从 SqPack 读取，关联名称所需的版本化 CSV 只解析表头。CSV 首次按需下载，完整文件经校验后可从 `tmp/ui/datamining/` 复用，定义缓存位于 `tmp/definitions/saintcoinach/`。等待读取超时的单元格先显示原值，后台下载完成后可重新读取。状态栏会标明失败的表和读取环节，区分本地数据读取、历史数据下载及字段定义。

在 Diff 的绿色新侧右键，可按表名、RowId、SubrowId 和列定位本地 SqPack 单元格。选择“打开关联记录 / Icon 图片”可跳到本地引用行或预览本地图标，需先连接游戏目录或已有保存的目录。

历史对比的行和单元格值来自两个构建的完整 CHS 表格 CSV。程序用官方构建号精确匹配提交，通过两个完整 CHS Git tree 找到变化表，按需下载 CSV 并校验 Git blob 哈希。所选 ZIP 提供版本标识，表格还原尚未实现。无法准确匹配构建时，界面会显示原因。CSV 无法保留 Raw 快照中的全部 SeString 原始信息。可用下文的命令另行导出本地 Raw 快照。

上游 CSV 由 `InfSein/dumpcsv` 从客户端 SqPack 导出。该工具由 `thewakingsands/dumpcsv` 分支修改而来，底层通过 SaintCoinach 读取 Excel 资源。

Raw 快照不依赖字段定义，因此支持未知表与未命名列。Raw 比较保留 SeString 字节、浮点位模式、64 位整数精度及真实子行 ID；部分导出的快照要求表集合一致，避免把未导出的表误判为删除。

启动时读取已缓存的补丁目录和本地最新补丁，并自动匹配历史 CHS 表索引；不自动批量下载补丁。所有操作只读游戏文件，不执行补丁安装。HTTP 可访问性探测不等同于完整包校验，下载后才检查 MD5。GitHub 未认证 API 有访问频率限制，已下载的固定版本数据可复用。

## 导出快照

设置 FFXIV_GAME_PATH 为本机游戏根目录，也可直接传 --game；路径无需写入仓库配置。

```powershell
./scripts/Invoke-Dotnet.ps1 restore src/FFXIVPatchParser --locked-mode
./scripts/Invoke-Dotnet.ps1 run --project src/FFXIVPatchParser -- snapshot --game $env:FFXIV_GAME_PATH --output tmp/snapshots/current
```

只导出指定表时追加 --sheets Stain,ClassJob,GilShopItem。输出目录必须为空，避免混入另一版本的数据。

## 补丁列表与下载

```powershell
# 查询全部官方条目和包文件可访问性
./scripts/Get-CnPatchCatalog.ps1

# 同时下载目标版本的最后一跳补丁，检查大小与 MD5
./scripts/Get-CnPatchCatalog.ps1 -DownloadLatest

# 只读检查下载包；GameRoot 用于比较当前文件与补丁的基线/目标哈希
./scripts/Inspect-CnPatch.ps1 -PatchRoot ./tmp/patches -GameRoot $env:FFXIV_GAME_PATH -OutputPath ./tmp/patches/latest-inspection.json
```

目录结果在 tmp/patches/catalog.json；最新包在 tmp/patches/patch-<from>-<to>/。下载不执行安装。

## 验证

```powershell
./scripts/Invoke-Dotnet.ps1 run --project src/FFXIVPatchParser -- self-test
# 可选，Python 3.11+；校验全部快照文件、行标识与列数
python ./scripts/Verify-Snapshot.py tmp/snapshots/current --output tmp/validation/current.json
```

可提交文件不包含本机用户名或绝对路径。快照清单记录源文件的相对路径与哈希；本地专用脚本可放入被忽略的 tmp/ 或 local/。

## 参考资料

- 国服 V3 补丁元数据、下载及安装流程。 [Dalamud 国服分支](https://github.com/Dalamud-DailyRoutines/Dalamud) · [DailyRoutines 本地化](https://github.com/Dalamud-DailyRoutines/DailyRoutines.Localizations) · [XIVLauncher CN 实现](https://github.com/AtmoOmen/FFXIVQuickLauncher/tree/65972f758b47615f321d4f3da509e3ade3b14489) · [官方版本目录](https://v3launcher.jijiagames.com/v3launcher/build/ver2data/100001900/8847/-1/ver2.dat)
- SqPack、EXH、EXD 和纹理读取。 [Lumina 7.7.2](https://www.nuget.org/packages/Lumina/7.7.2) · [Lumina 源码](https://github.com/NotAdam/Lumina) · [XIV Dev SqPack Wiki](https://xiv.dev/data-files/sqpack) · [SaintCoinach 原始列](https://github.com/xivapi/SaintCoinach/blob/master/SaintCoinach/Ex/Column.cs) · [关系列](https://github.com/xivapi/SaintCoinach/blob/master/SaintCoinach/Ex/Relational/RelationalColumn.cs) · [Definitions](https://github.com/xivapi/SaintCoinach/tree/master/SaintCoinach/Definitions)
- Excel 浏览、字段定义和图片预览交互。 [Alpha ExcelService](https://github.com/NotNite/Alpha/blob/d09167974abe2e5c1274cff8f022ca4037c8f59d/Alpha/Services/Excel/ExcelService.cs) · [EXDSchema](https://github.com/xivdev/EXDSchema) · [HaselDebug Excel](https://github.com/Haselnussbomber/HaselDebug/tree/main/HaselDebug/Tabs/Excel) · [TexTools UI](https://github.com/TexTools/FFXIV_TexTools_UI)
- 模型材质、骨骼和动作文件。 [TexTools Framework](https://github.com/TexTools/xivModdingFramework) · [Godbert](https://github.com/xivapi/SaintCoinach/tree/master/Godbert)
- 模型目录和名称映射。 [TexTools 模型缓存](https://github.com/TexTools/xivModdingFramework/blob/master/xivModdingFramework/Cache/XivCache.cs) · [TexTools 同伴模型](https://github.com/TexTools/xivModdingFramework/blob/master/xivModdingFramework/Items/Categories/Companions.cs) · [Godbert 怪物视图](https://github.com/xivapi/SaintCoinach/blob/master/Godbert/ViewModels/MonstersViewModel.cs) · [Anamnesis NPC 名称](https://github.com/imchillin/Anamnesis/blob/e180a0f124cb45db5bf56b283b41b8920a7e4c2e/Anamnesis/Data/NpcNames.json) · [FFCafé 字符串数据库](https://strings-legacy.ffcafe.cn/)
- SCD 音频提取与可选试听转换。 [Lumina ScdFile](https://github.com/NotAdam/Lumina/blob/8802d66a3cb3032be2fcf998bb10e864221bdb13/src/Lumina/Data/Files/ScdFile.cs) · [FFmpeg](https://ffmpeg.org/)
- CHS 历史数据及导出链。 [InfSein/ffxiv-datamining-mixed](https://github.com/InfSein/ffxiv-datamining-mixed) · [InfSein/dumpcsv](https://github.com/InfSein/dumpcsv) · [thewakingsands/dumpcsv](https://github.com/thewakingsands/dumpcsv) · [xivapi/SaintCoinach](https://github.com/xivapi/SaintCoinach)
- 二进制差分及桌面发布。 [RFC 3284 VCDIFF](https://www.rfc-editor.org/rfc/rfc3284) · [xdelta3](https://github.com/jmacd/xdelta) · [.NET 自包含发布](https://learn.microsoft.com/dotnet/core/deploying/)
