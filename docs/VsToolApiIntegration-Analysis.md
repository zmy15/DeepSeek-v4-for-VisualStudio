# VS 工具 API 接入现状分析报告（VS2022 / VS2026）

> 日期：2026-08-24　范围：产品代码全量静态盘点 + 本轮运行时实测证据
> 关联：Handoff §五「用户反馈④ 缺少 VS 工具接入」的前置调查

---

## 一、接入面总览

| 能力域 | 主要 API / 通道 | 代表文件 | VS2022 | VS2026 |
|--------|----------------|----------|:------:|:------:|
| 包运行时 | AsyncPackage + `[ProvideOptionPage/Profile/ToolWindow/MenuResource]` | Package.cs | ✅ | ✅ |
| 新扩展运行时 | VSEXT `Extension`（in-proc，RequiresInProcessHosting） | DeepSeekExtension.cs | ➖ | ⚠️ 正式式✅/Exp❌ |
| Shell/UI | `SVsUIShell` `SVsShell` `SVsRunningDocumentTable` `SVsInvisibleEditorManager` `SVsOutputWindow` user32(Toast) | Package.cs, DiffViewer*, Events.cs | ✅ | ✅ |
| 命令/菜单 | `IMenuCommandService` + `.vsct`(Menus.ctmenu) + KeyBinding | ShowChatWindowCommand, InlineAiEditCommand, vsct | ✅ | ✅ |
| 自动化对象模型 | `EnvDTE.DTE`（SolutionBuild/MainWindow/Windows） | BuildService.cs, CodeActions.cs, Toast 处理 | ✅ | ✅ |
| 编辑器/文本（**最深**） | Editor 全栈：`ITextBuffer/ITextView/ITextEdit/ITextDocument`、Differencing、Tagging、Formatting、Operations；`IVsTextViewCreationListener`/`IWpfTextViewCreationListener`/`IViewTaggerProvider`/`AdornmentLayerDefinition`(MEF)；`SVsTextManager` | InlineAiEditCommand, GhostTextTaggerProvider, InlinePredictionManager, EditorDiffMarkerService, EditBufferApplier, OpenBufferCommitTarget, InlineDiffSession*, DiffViewer* | ✅ | ✅ |
| 构建 | `IVsSolutionBuildManager`（主）→ `DTE.SolutionBuild`（回退）→ CMake 命令行（vcvars/cmake 探测） | BuildService.cs | ✅ | ✅ |
| 诊断/错误 | 经构建输出解析 + 编辑器 ErrorTag（IdeContext 快照）；`SVsErrorList` 仅用于面板唤起 | GetErrorsTool.cs, IdeContextTracker.cs, BuildService.cs | ✅ | ✅ |
| 设置体系（经典） | DialogPage + ProvideOptionPage/Profile（本轮补全资源）→ 实例私有存储 | DeepSeekOptionsPage.cs, VSPackage.resx | ✅ | ✅ |
| 设置体系（新版） | `SVsUnifiedSettingsManager{E3684F31-…}` → `ISettingsReader/Writer`；声明式 SettingCategory（settingsRegistration.json） | UnifiedSettingsSync.cs, DeepSeekUnifiedSettings.cs | ❌ 无此服务(fail-open) | ⚠️ 正式✅闭环 / Exp❌ NotRegistered |
| 跨实例设置迁移 | RegLoadAppKey 只读挂载他实例 privateregistry.bin | SettingsMigration.cs | ✅(源) | ✅(目标，实测 16 项) |
| 工具调用旁路 | powershell.exe 子进程（终端）、git.exe 子进程（版本控制）、Windows.Graphics.Capture（截图）、Windows.Media.Ocr（OCR）、NPOI/PdfPig/PaddleOCR（解析） | RunInTerminalTool, GitTool, GraphicsCaptureService, OcrService, FileParserService | ✅ | ✅ |

MEF 导出面（5 类）：`IViewTaggerProvider`、`IWpfTextViewCreationListener`、`IVsTextViewCreationListener`、`AdornmentLayerDefinition`、`IExternalSettingsProvider`（原型保留）。

---

## 二、双版本差异与已知边界（实测）

| 差异点 | Dev17 (VS2022) | Dev18 (VS2026) | 应对 |
|--------|----------------|----------------|------|
| DialogPage 存储位置 | bin 私有存储 | file+bin 混合 | LoadSettingsFromStorage 已加固捕获 InvalidCastException 回退默认值 |
| Unified Settings 服务 | 不存在 → 同步桥 fail-open 停用 | 正式实例可用；Exp 宿主不装载混合扩展部分 → moniker NotRegistered | 桥内置 120s 轮询 + 旧页 Apply 重试自愈；文档化于 feasibility §9.8–9.10 |
| privateregistry 迁移源 | 作为来源被枚举（含活动 hive 锁定快速失败） | 同左 | 自排除本 hive + 3s 超时兜底 |
| Roslyn/LangVersion | csc 上限 C#13 | 支持 C#14 | 构建固定走 tools/build-vs26.ps1（合并 Sdks 视图） |
| 安装器阻塞 | cl.exe/MSBuild 运行时 exit=2004 | 同左 | 静默安装前需无编译进程；或部署目录覆盖式更新（本次主实例采用） |

## 三、与「VS 工具接入」需求相关的缺口（反馈④落地候选）

> **状态更新（2026-08-24）**：以下 1、2 两项已落地实现；5 已执行瘦身。

1. ~~**调试器变量接入**~~ ✅ 已实现
   `IdeContextTracker.TryCaptureDebuggerFrame`：断点中断态捕获当前栈帧函数/位置/局部变量
   （只读、有界截断、逐项容错），随消息发送注入 volatile 块；无编辑器视图亦可独立注入。
2. ~~**错误列表结构化读取**~~ ✅ 已实现
   `IBuildService.GetAllErrorsAsync`：SVsErrorList → IVsTaskList.EnumTaskItems 全量枚举
   （上限 200），get_errors 输出合并 "Live Error List (structured)" 段；
   构建输出为空时亦可返回 IDE 分析器诊断。
3. **VS 集成终端**：维持外部 powershell.exe 现状（集成终端写交互式会话复杂度高，收益中）。
4. **Git 对象模型**：维持 git.exe CLI（跨版本稳）；仅当需要 HEAD 变更事件订阅时再评估。
5. ~~**Workspace 包零使用**~~ ✅ 已移除引用（VSIX 瘦身）。

## 四、风险提示

| 项 | 说明 |
|----|------|
| Preview API | `VSEXTPREVIEW_SETTINGS` 已显式 NoWarn；升级 SDK 需复核 |
| 内部类型依赖 | UnifiedSettings 服务 GUID 取自 Dev18 内部 interop（实测锁定），理论上随大版本变动需复查 |
| 双运行时并存 | AsyncPackage 与 VSEXT Extension 并存依赖 `ExtensionType="VSSDK+VisualStudio.Extensibility"` 清单标记与 Extensibility.Build 打包行为——勿回退 VSSDK.BuildTools 单打模式 |
| UI 线程纪律 | 本次启动卡死教训：任何新增 VS 服务调用须明确线程模型并计入启动预算（计时日志已在位） |

## 五、结论

- **编辑器/构建/设置三大域已深度接入且双版本验证**；编辑器栈是本项目差异化能力的基石，保持现状。
- **新设置体系在正式渠道 VS2026 已完整打通**；实验实例宿主装载问题属上游环境特性，桥接自愈机制就位。
- 反馈④的三个候选（调试器变量、Error List 结构化、集成终端）中，建议优先落地
  「调试器只读快照注入」（复用现有上下文管线、改动集中）与「Error List 结构化读取」（直接提升 get_errors 质量）。
