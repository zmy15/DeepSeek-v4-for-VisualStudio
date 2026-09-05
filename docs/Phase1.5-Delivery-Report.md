# Phase 1.5 交付报告 —— Context & Evaluation Driven Refinement

> 基线：`644068a`（v1.1.14）→ HEAD：`d2f6db0`，共 **14 个提交**
> 分析依据：`docs/Roadmap-Gap-Analysis.md`（路线图差距分析）
> 执行纪律：能力冻结 + 缺口补齐 —— Streaming / Patch / Version Preflight / Build-Fix /
> Retry / Loop Detection / Tool Registry / Multi-Agent 全程未动核心逻辑

---

## 一、28 步计划逐项映射

### P0 可观测性（序号 01–06）✅

| 步骤 | 交付物 | 提交 | 验证 |
|------|--------|------|------|
| 01 冻结基线 | 差距分析报告入库 | `d2c39ba` | 文档 |
| 02 会话指标模型 | `Models/TelemetryModels.cs`（Turn/ToolCall/Session + Model/Context/Host/System 四分类） | `42a72e9` | 单测 |
| 03 TTFT 测量 | 流式回调包装，首轮 token 计时 | `55fd1fc` | 单测 |
| 04 轮次/工具指标 | BeginTurn/EndTurn(usage+cache)、每工具耗时成败、3 处断点续传计数、4 类终止原因标记 | `55fd1fc` | 单测 |
| 05 Session JSON 导出 | `Services/Telemetry/AgentMetricsCollector.cs` → `%LocalAppData%\DeepSeekVS\telemetry\agent-session_*.json`（保留最新 100 个） | `42a72e9` | 单测 |
| 06 失败分类 schema | 枚举 + View 三分支完成态标注（未捕获异常→System；其余留待人工标注） | `55fd1fc` | 单测 |

### P1-A IDE Context（序号 07–13）✅

| 步骤 | 交付物 | 提交 |
|------|--------|------|
| 07 IdeContextSnapshot | `Models/IdeContextModels.cs`（含 ToPromptBlock 格式化器：<4KB、选区 40 行/2000 字符帽、诊断错误优先展示 6 条） | `665cd38` |
| 08 ActiveDocument 追踪 | ITextDocument.FilePath | `b3e41c9` |
| 09 Selection 追踪 | 选区文本 + 起止行号 | `b3e41c9` |
| 10 Caret/Symbol 追踪 | 光标行列 + 标识符启发式提取（非语义解析） | `b3e41c9` |
| 11 Diagnostics 摘要 | IErrorProviderFactory squiggle（≤50 条，仅当前文件；深度查询仍走 get_errors） | `b3e41c9` |
| 12 volatile 注入 | `ConversationContextManager.SetIdeContext`（token 记账同 RAG），置于易变块最前 | `21255f8` |
| 13 feature flags | `EnableIdeContextInjection` 设置项（默认开）+ 双语字符串 | `21255f8` |

关键约束兑现：
- **前缀缓存零破坏** —— IDE 态只进 volatile 块；会话结束在 finally 中清除（`c8f7a80`），防过期快照泄漏到普通聊天轮次
- **fail-closed** —— 捕获异常置空而非沿用旧值
- **每轮单次捕获** —— UI 线程一次性拍照，Agent 循环内零 VS API 调用

### P1-B Inline Edit（序号 14–19）✅

| 步骤 | 交付物 | 说明 |
|------|--------|------|
| 14 编辑器命令 | `cmdidInlineAiEdit`：编辑器右键菜单 + Ctrl+I（限定文本编辑器作用域，不影响全局增量搜索） | `3eb6795` |
| 15 选区捕获 | IVsTextManager→IWpfTextView 模式；**提交时重新捕获基线**（输入期间用户可能改动） | 同上 |
| 16 指令条 | 纯 C# WPF 无边框窗体（DPI 感知锚定选区上方）：Ready/Busy/Error 三态、占位符叠加层、失焦自动关闭 | 同上 |
| 17 接入预览管线 | `EditorDiffMarkerService.CreateInlineDiffPreview` + 结构化 ProposedTextChange —— **零重复造轮子** | 同上 |
| 18 Accept/Reject/Retry | Accept/Reject 由既有 Diff 宿主提供；失败时指令条原地重试（SetError 后继续等待提交） | 同上 |
| 19 取消 | Ready 态 Esc 关闭；Busy 态 Esc 触发 CancelRequested → 取消 LLM 调用 | 同上 |

第一版刻意**不走 Agent 工具循环**（报告 §14 边界）：选区 + 前后各 60 行上下文 → 单次 LLM 调用 → 围栏剥离 → 预览。使 Inline Edit 本身的体验可被独立评测。

### P2 打磨（序号 20–22）✅

| 步骤 | 交付物 | 提交 |
|------|--------|------|
| 20 Context Debugger 数据面 | `context_debug` 字段随每个会话 JSON 导出（token 占用/注入块标志/IDE 快照摘要）；`[ContextDebug]` 一行日志 | `dbf1913` |
| 20 Context Debugger UI | 聊天窗口右上角折叠抽屉（JS 懒创建，PostWebMessageAsString 新增 contextDebug 类型） | `f0dcece` |
| 21 ToolResult 结构化 | `ToolExecutionOutcome`/`ToolResultKind`：❌/⏱️ emoji 约定的唯一权威解析点；对外字符串契约零变化 | `d0f7fdd` |
| 22 Timeout 分档 | `ToolTimeoutPolicy`（memory 10s/诊断类 20s/抓取 45s/默认 60s）；交互式工具豁免边界以哨兵测试固化；`LlmTimeoutSeconds` 设置（默认 300s=原硬编码值，钳位 30–3600）接入 HttpClient.Timeout | `e5a4c57` |

### P3 Benchmark（序号 23–28）🔶 离线件完成

| 步骤 | 交付物 | 提交 |
|------|--------|------|
| 23 冻结规程 | `benchmark/README.md`：环境冻结清单（repo commit/VS 版本/扩展版本/模型及参数/Prompt SHA/Task schema 版本）、24 任务三类均衡、字段↔session JSON 映射表 | `309e343` |
| 24–26 任务清单 | `benchmark/tasks.sample.json`（schema + CompileFix/InlineEdit/CrossFile 各 2 示例，扩充至 24 按此格式） | `309e343` |
| 27 runner | `benchmark/invoke-benchmark.ps1` 标注模式（最新未标记会话打 task_category/task_id）；C# 报告生成器 `BenchmarkReportGenerator` | `309e343` + `d2f6db0` |
| 28 失败报告 | Markdown 报告：成功率、失败三分类分布、轮次/工具/TTFT/时长均值、按 Agent 与按任务类别分组 —— 直接对照 §22 决策表 | 同上 |
| — 任务标注字段 | `AgentSessionMetrics.task_category/task_id` + `collector.SetTaskInfo()` | `309e343` |

---

## 二、验证矩阵

| 层级 | 方式 | 结果 |
|------|------|------|
| C# 纯逻辑（遥测/IDE 格式化器/InlineEdit 解析/超时策略/工具分类/Benchmark 聚合） | 独立 harness 单测（xUnit+FluentAssertions） | **70/70 通过** |
| Context Debugger 渲染逻辑 | Node DOM 桩实测（完整载荷 + 空载荷断言） | ✅ |
| Benchmark 脚本 | 合成遥测目录端到端（标注命中 + 聚合数字核对） | ✅ |
| 本地化完整性 | 代码引用键 ↔ zh-CN/en 文件 15/15 双向核对 | ✅ |
| 接线一致性 | Metrics 属性/分类器引用/命令 ID 对齐/零 TODO 残留扫描 | ✅ |
| **整包编译** | **⚠️ 待 VS IDE F5 验证**（本机 dotnet SDK 9 Roslyn 不支持项目 LangVersion 14 的 span 重载解析） | 待办 |

---

## 三、设计决策记录

1. **遥测永不侵入主流程** —— Collector 所有方法吞异常仅记日志；`Context?.Metrics` 空值短路，设置关闭时零开销。
2. **IDE Context = 快速定位，Tool = 深度查询** —— 快照只带计数与 ≤6 条摘要；完整错误仍由 get_errors 提供（职责不重叠）。
3. **结果分类唯一权威点** —— emoji 约定解析从调用点收敛到 `ToolExecutionOutcome.Classify`，未来 Benchmark/UI 汇总不再各自实现。
4. **豁免边界文档化** —— read/edit/build/terminal 类工具因审批弹窗与内部控时而豁免超时是既有设计；策略类以哨兵测试防止误收编。
5. **PS 脚本纯 ASCII** —— PS 5.1 将无 BOM 脚本按 ANSI 解析，非 ASCII 字符破坏字面量导致整段语法错（已在源码注释警示）。

---

## 四、移交清单（环境绑定）

| # | 事项 | 说明 |
|---|------|------|
| 1 | F5 整包编译验证 | 唯一无法离线完成的关卡 |
| 2 | 10 次基线会话 | 正常使用后检查 `%LocalAppData%\DeepSeekVS\telemetry\` |
| 3 | 第一份 v0 报告 | `.\benchmark\invoke-benchmark.ps1 -ReportOnly` |
| 4 | fixture 仓库准备 | 按 README §三 规程；runner 自动化（fixture 注入 + 自动标注）待下一迭代 |

## 五、已知限制与演进点

- Context Debugger 抽屉为英文固定文案（调试工具，暂不进 i18n）
- 符号提取为词法启发式，非 Roslyn 语义解析 —— Case B 验收以"包含标识符"为准
- Benchmark runner 未自动化 fixture 注入；task_category 目前靠脚本标注
- RAG 维持冻结：仅当 v0 报告中 cross_file 类目出现大量 `"找不到远程 symbol"` 型 Context 失败时立项 BM25（不做 Vector DB）

*生成于 Phase 1.5 收尾；行号/哈希以本报告所列提交为准。*
