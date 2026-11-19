---
name: ffxiv-offline-research
description: 研究 FFXIVPatchParser 的 SqPack、EXD、地图、模型、音频、时间线、特效与着色器。用在需要核对国服与国际服客户端结构、从本地样本验证格式语义、恢复未知路径或把已证实的行为接入离线预览时。
---

# FFXIV 离线资源研究

1. 从项目的 [知识库](../../knowledge/README.md) 和相关源码找已有实现，选一个能稳定复现的区域、对象或资源，再选一个不同构建或场景作对照。记录构建、语言、路径或表行、操作步骤和结果。探针、日志、下载和原始资源放在 `tmp/`。
2. 按 [项目用过的参考库](references/upstream.md) 查上游实现。从调用点追到解析器和实际读取点，核对字节序、版本分支、索引空间、默认值和失败回退。将上游声明、国服实测和尚未覆盖的行为分别记录。
3. 格式和源码无法解释运行时行为时，按 [客户端定位与验证流程](references/client-workflows.md) 核对本机 EXE；已有样本和结论先查 [客户端执行逻辑记录](../../knowledge/client-runtime.md)。实现时把已验证的规则做成独立解析或渲染逻辑，再用本地样本回归。
4. 交付时更新对应知识库页与 TODO，只标记实际验证过的功能。桌面发布按项目协作指南使用 `tmp/publish/win-x64`。

## 客户端函数与着色器

- 用 [国际服地址表](https://github.com/aers/FFXIVClientStructs/blob/main/ida/data.yml) 和 [国服分支](https://github.com/Dalamud-DailyRoutines/FFXIVClientStructs) 建立待查函数清单。固定上游提交，核对本机 `ffxivgame.ver`、PE 标识和 SHA-256。两份表相同也要在本机验证 RVA。
- 签名只是入口线索。结合 `.pdata`、直接调用、虚表、格式字符串和字段读写确定函数和实际分支。资源引用、加载成功、对象更新、最终渲染分别验收。
- MapEffect 重点跟踪消息分发、`ContentDirector.ApplyMapEffect`、`PlayMapEffectTimeline`、共享组时间线 Play/Stop，再查 LGB/SGB 中 LayoutId 与槽位如何对应。对照 BossMod 的事件值及本地绑定。路径加载日志可定位依赖，事件时刻和参数用带时间戳的记录核对。
- 着色器先按 ShCd/ShPk 包内长度提取 DXBC，读 RDEF 和输入签名，再核对每个通道的常量、纹理、采样器、混合和深度状态。离屏 D3D11/WARP 验证实际绘制和读回结果。创建成功只能验证字节码可加载。

## 路径和表的验收

- ResLogger2、EXD 字符串、MDL/MTRL、PAP、SGB/TMB、AVFX 引用与同目录命名规律用于生成候选。用分区、目录 CRC、文件名 CRC、完整路径 CRC 与物理地址核验。有同址候选时保留冲突并寻找结构引用、文件头和运行日志。
- 报告当前构建导入前后的已知条目差值。清单匹配次数可能包含已命名条目。索引更新后用新索引复核历史候选。
- EXD 字段须核对实际 EXH 变体、字段位移、类型及行／子行布局。确认用途后补 SaintCoinach 兼容定义。验证跨表引用时，用可复现的已知行、边界值和绑定方向。
