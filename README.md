# FFXIVPatchParser

FF14 国服 V3 补丁检查、Raw Excel 快照与表格 Diff 桌面工具。

目前支持：

- 只读 SqPack，导出简体中文及无语言 Excel 表，包括普通行、子行、空表与宽表。
- 按 EXH 保留列类型、偏移和原始值；SeString 保存可读文本与原始字节。
- 查询官方补丁目录，探测包文件可访问性，下载并校验最新 ZIP。
- 检查补丁路径映射、Excel 差分校验值及 SqPack 资源分类。
- WPF 桌面界面：补丁变化文件、红绿表格 Diff、官方补丁目录和下载。

补丁 VCDIFF 的完整还原尚未实现；当前 UI 的资源清单是文件 / SqPack 容器级，不是容器内每个模型或贴图的清单。

## 环境

桌面发布版支持 Windows x64，自带 .NET 运行时，无需安装 SDK 或 PowerShell 7。源码构建需要 .NET 10 SDK，构建和发布脚本兼容 Windows PowerShell 5.1 / PowerShell 7；补丁研究脚本仍需 PowerShell 7。Lumina 固定为 7.7.2，传递依赖记录在 packages.lock.json。

所有下载、缓存、构建产物、快照和本地测试数据放在项目的 tmp/ 下，并被 Git 忽略。请通过 scripts/Invoke-Dotnet.ps1 运行 .NET 命令，使 NuGet 与临时目录也位于项目内。

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

- **变化资源**：选择国服 V3 ZIP；同一更新的多个分卷可以一起选。支持路径搜索、类别筛选、前后 MD5 与差分文件信息。缺失分卷会明确标记。
- **表格 Diff**：加载补丁后自动匹配 CHS 历史版本；“数据源 / 设置”中可重新匹配，或选择旧、新两份 Raw 快照的 manifest.json。左侧显示表文件变化；选择一张表后按 RowId / SubrowId 和列编号比较。
- **补丁目录**：显示官方元数据列出的所有更新、分卷数、大小与可访问性；支持刷新和下载所选更新。下载校验大小与 MD5，已有且校验通过的包会复用。

Diff 中旧行为红色、新行为绿色，变化单元格使用底色，仅字符串类型的单元格内具体增删字符使用更深红绿高亮；数字和布尔字段保留整格高亮，不进行字符比较；新增和删除行也保留 + / − 标记。支持只看变化列；点击列头（包括 Row / Subrow）输入关键词筛选，匹配该列旧值或新值，多列条件同时满足，支持清除此列或清除全部筛选。底部完整值可选择复制，字符串同样显示字符级高亮。每页 100 条变化记录、40 列，分页覆盖全部内容，不截断宽表。中文及组合 Unicode 字符保持完整，支持多处字符增删；超长文本按公共前后缀高亮中间差异以限制计算量。表列表采用单行紧凑布局，状态通过悬停查看。结构变化在列标题中标注；只改变 CSV 格式或文件结构、没有行值变化的表会显示说明。

历史 CHS 数据来自 [InfSein/ffxiv-datamining-mixed](https://github.com/InfSein/ffxiv-datamining-mixed)，使用官方构建号精确匹配提交，通过两个完整 CHS Git tree 找到变化表，按需下载 CSV 并校验 Git blob 哈希。它是历史数据对照，**不是从所选 ZIP 直接还原的表格**。无法准确匹配的版本会报错，需改用两份本地 Raw 快照。CSV 无法保留 Raw 快照中的全部 SeString 原始信息，两种格式不能混比。

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
