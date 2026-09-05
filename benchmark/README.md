# VS-Agent Benchmark v0 规程

> 原则（路线图 §22）：Benchmark 不是给模型打分，而是回答 **为什么失败**。
> 产出物驱动下一步：Context 失败多 → 优先补 Context；Host 失败多 → 修 VS Adapter；Model 失败多 → 调提示词/换模型。

## 一、环境冻结（每次跑分前逐项记录，缺一不可）

| 冻结项 | 取值来源 |
|--------|---------|
| Repository commit | fixture 仓库的固定 SHA（任务 setup 注入前） |
| VS Version | 实验实例 `帮助 → 关于` |
| Extension Version | VSIX 清单版本号 |
| Model | 设置页 SelectedModel |
| Model Parameters | 深度思考开关 / Reasoning Effort / Temperature |
| Prompt Version | 本仓库 `Services/AiPrompts.cs` 的 git SHA |
| Task Version | `tasks.json` 的 `task_schema_version` + 各任务 id |

任何一项变化 ⇒ 报告必须标注，禁止与历史结果直接对比。

## 二、任务集 v0：24 任务

| 类别 | 数量 | 会话内对应能力 |
|------|-----:|----------------|
| Compile Fix | 8 | BuildAgent / get_errors / 编辑工具闭环 |
| Inline Edit | 8 | Ctrl+I 指令条 → InlineDiffSession 预览 |
| Cross-file | 8 | 多文件检索 + apply_patch |

样例见 `tasks.sample.json`。扩充到 24 个时保持三类均衡。

## 三、执行规程

1. `git -C <fixture>` checkout 任务指定 commit → 打开实验实例。
2. 正常发起对话/Inline Edit（**不要**手动改代码替代 AI 操作）。
3. 会话结束等待 telemetry JSON 落盘：
   `%LocalAppData%\DeepSeekVS\telemetry\agent-session_*.json`
4. 在该 JSON 中补两字段（当前由运行方脚本或临时代码注入，
   后续 runner 自动化）：`task_category`、`task_id`；
   并人工标注失败分类（若 `failure_category` 为 `None`）：
   **Model / Context / Host**（System 仅工程故障）。
5. 每类任务至少 3 个 fixture 变体轮换，防止单仓库过拟合。

## 四、报告生成

调用 `Services/Benchmark/BenchmarkReportGenerator.cs`：

```csharp
var agg = BenchmarkReportGenerator.SummarizeDirectory(telemetryDir);
Console.WriteLine(BenchmarkReportGenerator.ToMarkdown(agg, $"v0 run {DateTime.Now:yyyyMMdd-HHmm}"));
```

输出包含：成功率、失败三分类分布、轮次/工具/TTFT/Token 均值、
按 Agent 与按 task_category 的分组统计 —— 直接对照 §22 的决策表行动。

## 五、记录字段 ↔ session JSON 映射

| 规程要求 | 字段 |
|----------|------|
| Success | `result == "success"` |
| Turns | `turn_count` |
| Tool Calls | `tool_call_count` |
| Input/Output Tokens | `input_tokens` / `output_tokens` |
| TTFT | `first_turn_ttft_ms` |
| Total Time | `duration_ms` |
| Human Intervention | （runner 记录）Accept 点击次数 / 手动纠偏次数 |
| Failure Category | `failure_category` |
| 为什么失败 | `context_debug` + `turns[].terminated_reason` + `tools[]` |
