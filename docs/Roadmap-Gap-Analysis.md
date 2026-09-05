# 阶段路线图对齐分析报告 —— 建议方案 vs 实际落地情况

> 版本基准：v1.1.14（commit `644068a`）
> 分析日期：2026-08-22
> 分析方法：对建议报告的 18 条主张逐条核查源码，标注证据文件与行号；所有结论均以当前代码为准，不依赖 README 描述。

---

## 0. 结论摘要（TL;DR）

1. **项目实际完成度显著高于报告的假设前提。** 报告按"基础插件刚完成、WinUI 流式问题待解、无 Patch 机制"的阶段给出建议，但代码库中：
   - 流式管线已是报告推荐的 **buffer → batch → render 解耦架构**（C# 60ms 门限批处理 + JS rAF 缓冲双端实现）；
   - **Patch 化编辑 + 文档版本预检**已完整落地（prepare → preflight → commit 三段式）；
   - **五 Agent + Handoff 协作已上线**——报告第十节"先别做 Planner/MultiAgent"的建议对本项目已经失效，问题从"要不要做"变成"如何收敛复杂度"。

2. **报告中最有价值的两条建议恰好落在当前真正的空白区：**
   - **IDE 实时上下文注入（第五节 ContextProvider）**——目前 Agent 对"用户正在看什么、选中什么、光标在哪、有什么报错"零感知，只能靠主动调工具；
   - **量化评测体系（第一/十六/十七节基线指标、Benchmark、失败三分类）**——完全缺失。

3. **因此优先级需要重排**：报告的第一优先级（Streaming）在本项目应降级为"验证 + 补指标"；原第三优先级（Context）升为最高。详见第 5 节修正路线图。

---

## 1. 关键前提修正：报告三个假设与现实的偏差

| # | 报告假设 | 代码库现实 | 影响 |
|---|---------|-----------|------|
| A1 | UI 为 WinUI，存在流式刷新性能问题，是第一优先级 | UI 为 **WPF + WebView2**，流式已是双端缓冲架构（见 §3.1） | 报告第一优先级基本已完成，降级为"补指标验证" |
| A2 | 编辑为整文件覆盖，缺 Patch 与版本检查 | 已有 6 种结构化编辑工具 + 四级匹配 + Healing + 快照版本 Preflight（见 §3.5/3.6） | 报告第八/九节为"维持性工作"，无需新建 |
| A3 | Agent Runtime 处于简单 Tool Loop，Planner/MultiAgent 应暂缓 | Ask/Explore/Plan/Edit/Edit.AutoSplit/Build 六个 Agent + Handoff + runSubagent + PlanBuildOutcomeReconciler 均已存在 | 该建议方向正确但对象错位；实际风险是 View 层编排过重（见 §3.2） |

**结论：不应照搬报告的执行顺序，应以第 5 节的重排路线为准。**

---

## 2. 十八条建议逐项对照总表

图例：✅ 已达成　🟡 部分达成 / 有残余缺口　❌ 未实现

| 报告章节 | 建议 | 状态 | 核心证据 |
|---------|------|:----:|---------|
| §1 冻结基线 v0 | 记录启动/TTFT/内存等指标 | ❌ | 无任何 TTFT/响应耗时埋点；仅有 `[Cache]` 每轮汇总日志（BaseAgent.cs:1554-1603）与 DeepSeekApiService 的 token 快照差值统计（DeepSeekApiService.cs:214-219） |
| §3 Streaming 解耦 | buffer→batch→render | ✅ | C# `BatchStreamingUpdate`（DeepSeekChatControl.xaml.cs:1416）；JS rAF `_flushStreamBuf`（ChatHtmlService.Js.cs:372-549） |
| §4 UI/Runtime 事件流解耦 | EventStream 共用 Runtime | 🟡 | 回调式事件已有（OnThinkingChunk/OnContentChunk，DeepSeekChatControl.Agent.cs:425-478），但 View 层直接编排路由/搜索/Handoff，且体量巨大 |
| §5 ContextProvider | ActiveDoc/Selection/Cursor/Diagnostics 注入 | ❌ | 全库无 IDE 实时态注入路径；仅 `includeSelected` 错误列表参数（GetErrorsTool.cs:48） |
| §6 不做全量 Solution Index | 先 Local Context | ✅ | RagService 仅 provider 注册架构，无内建索引（RagService.cs:22-59） |
| §7 Inline Edit（Ctrl+I） | 选区→指令→预览→接受 | ❌ | VSCommandTable.vsct 仅有 2 个开窗按钮；但 InlineDiffSession 基建已就绪（见 §3.4） |
| §8 Patch 化修改 | 拒绝整文件覆盖 | ✅ | apply_patch/replace/multi_replace 等工具族；结构化 TextChanges 从后向前应用（OpenBufferCommitTarget.cs:139-165） |
| §9 DocumentVersion 校验 | 应用前版本比对 | ✅ | `PreflightAsync` 快照版本号 + 全文 Ordinal 比对，不一致即拒绝（OpenBufferCommitTarget.cs:37-54） |
| §10 Runtime 护栏 | 取消/超时/重试/MaxIterations | ✅ | MaxToolCallRounds=200 可配置（DeepSeekOptionsPage.cs:333）；工具 60s 超时（BaseAgent.cs:1621-1670）；断点续传重试 4 次（BaseAgent.cs:847-997）；三类循环检测（同结果签名/连续错误轮/白名单拒绝轮，BaseAgent.cs:1080-1503） |
| §11 工具统一化 | ITool + ToolRegistry | ✅ | `BuiltInToolBase`：Name/GetDefinition()/ExecuteAsync/GetDisplayText/GetResultSummary（BuiltInToolBase.cs:16-35）；BuiltInToolService 注册表统一管理内置+MCP 工具 |
| §12 ToolResult 标准化 | {success,output,error,metadata} | 🟡 | 结果为纯字符串 + emoji 约定（如 `❌` 前缀判断，GetErrorsTool.cs:71）；有 ToolResultCompactor 做体积压缩，但无类型化 metadata |
| §13 Build→Diagnostics→Fix 闭环 | 编译修复循环 | ✅ | BuildAgent 工具集含 build_solution/get_errors/全套编辑工具（BuildAgent.cs:37-65），最多修复 3 次（新错误不计入）；PlanBuildOutcomeReconciler 负责结果核对 |
| §14 上下文优先级 P0-P5 | 不足时按级丢弃 | ❌ | 压缩为轮次驱动（85%/95% 阈值、保留最近 3 轮，ContextModels.cs:91-110），无内容分级丢弃 |
| §15 Context Debugger | UI 展示上下文构成 | ❌ | `ContextStats.GetDetailedReport()`（ContextModels.cs:46）仅内部使用，UI 无展示面板 |
| §16 自建 Benchmark | 20-50 个真实任务 | ❌ | 仅单元/集成测试（48 个测试文件），无端到端任务评测集 |
| §17 失败三分类 | Model/Context/Host | ❌ | 无分类记录机制 |
| §18 总路线图 | Freeze→Stream→Context→… | ⚠️ | 顺序需按本报告第 5 节重排 |

---

## 3. 详细差距分析

### 3.1 UX/UI 层 —— Streaming：报告第一优先级已被现有实现覆盖

**现状管线（与报告推荐架构逐环对应）：**

```text
SSE chunk (DeepSeekApiService)
    ↓ OnContentChunk / OnThinkingChunk 回调          ← Agent.cs:425-478
C# BatchStreamingUpdate                              ← xaml.cs:1416
    ├─ 60ms 最小推送间隔 (StreamBatchMinIntervalTicks, :1375)
    ├─ 推理增量 ≥50 字符门限 (:1447-1448)
    └─ 300ms 空闲定时器强制刷新 (:1466-1472)
    ↓ PostWebMessageAsString 非阻塞通道               ← ChatHtmlService.cs:143
JS _streamBuf + requestAnimationFrame 批量应用         ← ChatHtmlService.Js.cs:378-398
    ├─ 250ms 兜底定时器（防 rAF 不触发）
    ├─ textNode 直写（流式期间不做 Markdown 重渲染）     ← :528-541
    └─ streamEnd 单次 Markdown 渲染 + late-chunk 竞态防护 ← :400-413, 529-531
```

这正是报告第三节要求的"token 到达频率与 UI 渲染频率完全解耦"。**无需再动架构。**

**残余缺口（小）：**
- 无 TTFT / 首 token 延迟 / 更新次数统计（并入 §5 P0-A 指标项）；
- 刷新间隔硬编码，不可配置（低优先）；
- 报告提及的"WinUI 问题"在当前栈上不存在，相关专项可关闭。

### 3.2 Runtime 层 —— 解耦程度：回调已有，View 过重

**已达成的部分：**
- Agent → UI 通过 `context.OnThinkingChunk / OnContentChunk / OnPlanUpdated / RequestPermissionAsync` 等回调交互，Runtime 不直接操作 DOM；
- 工具调用通知、状态文本均有独立消息通道（streamStatus）。

**未达成的部分（报告第四节目标形态）：**
- 无统一的 EventStream 抽象（Message/ToolCall/ToolResult/Thinking/Error/Completed 六类事件）；
- View 层六个 partial 类合计约 600KB（Events.cs 109KB、Agent.cs 93KB、RetryEdit.cs 64KB），直接承担意图路由、联网搜索注入、Skill 加载、Handoff 确认等编排逻辑；
- BaseAgent 直接引用 LocalizationService（Runtime 感知了本地化这一表现层关切）。

**处置建议：与报告"不要大规模重构"的原则一致，暂不做整体事件流改造。** 仅在新功能开发时遵守"新增逻辑进 Services、View 只做渲染"的纪律，并在 P3 阶段视 Benchmark 结果决定是否值得抽 EventStream。

### 3.3 Context 层 —— 当前最大真实空白

**已有的良好基础：**

| 能力 | 实现 |
|------|------|
| Working Set 追踪 | `ActiveFileTracker` 记录工具读写的文件，生成摘要块（ActiveFileTracker.cs） |
| 读写字节点 | ReadFileTool/BaseAgent 写入后调用 ObserveRead/ObserveWrite（ReadFileTool.cs:225 等） |
| 三层消息结构 | 稳定前缀 [0..3] + 历史 + 动态块（压缩摘要/记忆/语言守卫）+ 易变块（Working Set/搜索/RAG）（ConversationContextManager.cs:577, 625-650, 750-777） |
| 前缀缓存保护 | dynamicBlock 冻结快照防每轮漂移（:138-154）；缓存窗口 FindCacheWindowStart |
| 自动压缩 | 85% 触发 / 95% 激进阈值，保留最近 3 轮（CompressionConfig） |

**缺失的部分（对照报告第五/十四/十五节）：**

1. **IDE 实时态零注入** —— 用户打开的文件、选区、光标所在函数、Error List 现状都不会自动进入模型输入。Agent 回答"当前文件"类问题时必须先调 read_file/get_errors，多一轮工具调用 = 多一次延迟 + 多一份 token。
2. **无 P0-P5 分级丢弃** —— 压缩按轮次从旧到新，不看内容价值；长会话中早期关键决策可能被压掉，而低价值的搜索结果反而占预算。
3. **无 Context Debugger** —— 出现坏回答时无法快速判定是"模型不行"还是"上下文给错了"（报告第十七节的失败三分类也因此无从落地）。

**注意约束：IDE 态属于易变信息，必须走 volatile 块（user 消息前），不得写入稳定前缀，否则会击穿 DeepSeek Prefix Cache 命中率——现有三层结构已经为此预留了位置。**

### 3.4 Inline Edit —— 基建完备，只差入口

- `InlineDiffSession` prepare-preview-commit 改造（docs/InlineDiffSession-Redesign-Plan.md v2）已实施：StagedEditWorkspace（27KB）、ProposalCommitCoordinator、三种 CommitTarget（OpenBuffer/File/NewFile）、冻结基线 Diff、批量全量预检、回滚；
- CodeActions 中聊天侧已能触发 "preview-then-commit" 内联 Diff（DeepSeekChatControl.CodeActions.cs:304）。

**唯一缺口：编辑器内的命令入口不存在。** VSCommandTable.vsct 仅注册了两个打开聊天窗口的按钮，没有任何编辑器右键命令或 Ctrl+I 绑定。也就是说报告第七节的完整流程"选区→Ctrl+I→指令→diff 预览→Accept/Reject"中，前半段（选区捕获+指令输入）和后半段（预览+提交）的实现都已存在，缺的是把两者串起来的那条编辑器命令和一个轻量输入 UI。**这是投入产出比极高的一项。**

### 3.5 Patch 与版本安全 —— 达成，列为维护项

- 六种编辑工具 + `EditStringMatcher` 四级匹配（精确→行级→Levenshtein→Healing 修复）；
- 结构化 `ProposedTextChange` 按 offset 从后向前应用，避免位置漂移（OpenBufferCommitTarget.cs:139-165）；
- 版本校验双保险：快照 VersionNumber + 全文 Ordinal 比较，版本不同但内容相同仍放行（避免误报），否则 `ConflictLevel.ContentChanged` 拒绝并要求重新生成——语义正是报告第九节要求的 Reject→Re-read→Re-generate；
- FileCommitTarget 另有 BackupService 兜底。

**无行动项，仅需在 Benchmark 中持续验证冲突拒绝路径的真实触发率。**

### 3.6 Runtime 护栏 —— 达成，两处小档位空缺

已具备：轮次上限（200，可配置）、工具超时（60s）、全程取消令牌 + 停止按钮、流断点续传（4 次重试 + 部分内容回注）、三种循环终止检测。

小缺口：
1. `GetToolTimeout` 的 switch 只有一个 default 臂（BaseAgent.cs:2831-2837）——分档位骨架在，但 build_solution 这类长任务与 list_dir 这类快操作共用 60s；
2. LLM 单次调用超时未暴露为设置项（目前靠 HttpClient 默认 + 重试兜底）。

### 3.7 工具系统与结果标准化 —— 形式统一，语义未标准化

- 形式上已是报告第十一节的 `ITool` 形态（BuiltInToolBase + 注册表 + 每 Agent 白名单拦截 + MCP 工具同池注册）；
- 但结果仍是**自由字符串**：错误靠 `❌` 前缀约定、摘要靠各工具自己实现的 GetResultSummary 正则提取（如 GetErrorsTool.cs:75-80 用正则数错误个数）。报告第十二节的结构化 `{success, output, error, metadata}` 未落地。
- 影响：LLM 侧上下文尚可用（有 Compactor 控量），但 UI 汇总、失败统计、Benchmark 度量都缺乏机器可读字段——这是 P3 评测体系的前置依赖之一。

### 3.8 Build→Fix 闭环 —— 已存在，进入打磨期

BuildAgent 已实现报告第十三节的目标闭环（get_errors 定位 → read/grep/read_file 确认 → 编辑修复 → build_solution 验证，≤3 轮，新错误不计入限制）。打磨方向：
- 修复前将编译错误的精确 span（文件+行列）注入上下文，减少 Agent 二次定位；
- PlanBuildOutcomeReconciler 的核对结果更显著地呈现给用户；
- 在 Benchmark 中单列 Compile Fix 类目跟踪成功率。

### 3.9 Reliability 层 —— 评测体系整体缺失（报告第一/十六/十七节）

这是当前与报告差距最大的一层：

- **无基线指标**：启动时间、TTFT、完整响应耗时、CPU/内存均未采集。现有基础可复用：`[Cache]` 每轮命中率汇总日志（BaseAgent.cs:1006, 1554-1603）、DeepSeekApiService 的累计 token 快照差值接口（:214-219）、DiagnosticLog；
- **无任务 Benchmark**：473+ 单元测试保障的是"组件正确"，无法回答"修一个真实编译错误平均要几轮、成功率多少"；
- **无失败三分类**：Model/Context/Host Failure 无法区分，优化就是盲人摸象。

### 3.10 一处战略张力需要拍板：RAG

README 路线图将 RAG 代码检索增强列为 🔴高优先，而报告第六节明确警告"先别做全量索引"。两者并不矛盾，折中方案：
- RagService 已是 provider 架构，**保持接口不动**；
- 是否投入索引实现，改由 Benchmark 数据触发：若 Compile Fix/Cross-file Change 类目因"找不到远处代码"导致的 Context Failure 占比高，再启动 BM25 轻检索；在此之前不投。

---

## 4. 修正后的优先级路线图

```text
P0  量化地基（先于一切优化）
 ├── A. 基线指标采集 v0        —— 复用 [Cache] 日志 + 新增 TTFT/轮次/工具调用计数，
 │                                输出到会话级 JSON，形成 VS-Agent v0 基线
 └── B. 失败三分类标注规范      —— 在诊断日志中增加 category 字段（model/context/host），
                                   人工复盘即可，先不求自动分类

P1  Copilot 式体验补全（最高用户价值）
 ├── A. IDE 实时态上下文注入 v1 —— ActiveDocument + Selection + 光标符号 + Diagnostics 概要，
 │                                注入 volatile 块（严守前缀缓存约束）；带开关设置项
 └── B. 编辑器 Inline Edit 命令 —— Ctrl+I / 右键菜单 → 浮动指令条 → 复用 InlineDiffSession
                                   preview-commit → Accept/Reject/Retry

P2  打磨与标准化
 ├── A. Context Debugger 面板   —— 复用 ContextStats.GetDetailedReport + ActiveFileTracker
 │                                数据，聊天侧边栏展示构成与 token 占用
 ├── B. 结构化 ToolResult       —— 内部类型化 {success,error,metadata}，对外仍输出字符串，
 │                                向后兼容渐进迁移
 └── C. 超时档位补全            —— GetToolTimeout 按 build/terminal/search 分档；LLM 超时入设置页

P3  评测驱动优化
 ├── A. 任务 Benchmark v0      —— 20~30 个真实任务（Compile Fix / Inline Edit / Cross-file
 │                                各占 1/3），记录成功率/轮次/token/人工干预
 └── B. 依据数据决定            —— RAG 是否立项；View 层是否值得抽 EventStream
```

各项验收标准：

| 项 | 验收标准 |
|----|---------|
| P0-A | 连续 10 次典型会话可导出含 TTFT、轮次、cache 命中率、token 明细的 JSON |
| P0-B | 任一失败案例可在日志中唯一归类到 model/context/host 之一 |
| P1-A | 提问"我选中的这段代码有什么问题"，Agent 首轮即携带选区内容，无需调用 read_file |
| P1-B | 选区代码 → Ctrl+I → "加注释" → diff 预览 → Accept 全程 ≤3 次交互，Esc 可取消 |
| P2-A | Debugger 显示的 token 数与 API 用量偏差 <10% |
| P3-A | Benchmark 报告能按类目给出成功率与平均轮次，连续两周可比 |

---

## 5. 与建议报告的最终对齐声明

| 报告建议 | 本项目采纳情况 |
|---------|--------------|
| §1 冻结基线 | 采纳，提前至 P0（原报告也要求最先做） |
| §3 Streaming 专项 | 架构已完成，转为指标验证项 |
| §4 EventStream 大解耦 | **缓行**——遵循"不大规模重构"原则，由 P3 数据决定 |
| §5 ContextProvider | 采纳，升为最高工程优先级（P1-A） |
| §6 不做全量索引 | 采纳，RAG 立项与否交由 Benchmark 触发 |
| §7 Inline Edit | 采纳，基建已备，仅补命令入口（P1-B） |
| §8/§9 Patch + 版本校验 | 已达成，转入回归验证 |
| §10 Runtime 最小护栏 | 已超额达成（多 Agent 已上线），仅补超时档位 |
| §11/§12 工具统一与结果标准化 | §11 已达成；§12 列入 P2-B |
| §13 Build→Fix 闭环 | 已达成，进入打磨期 |
| §14 P0-P5 分级丢弃 | 并入 P1-A 后续迭代（先注入，后分级） |
| §15 Context Debugger | 采纳（P2-A），依赖 P1-A 的数据面 |
| §16/§17 Benchmark 与失败分类 | 采纳，P0-B 定规范、P3-A 落地执行 |

---

*本报告基于静态代码核查得出，未运行实验实例做动态验证；涉及行号以 v1.1.14（commit `644068a`）为准，后续提交可能使行号漂移。*
