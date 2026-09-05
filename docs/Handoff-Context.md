# 项目交接报告 — 供新一轮对话使用

> 基线 `644068a` (v1.1.14) → HEAD，标签 `v1.2.0`，43 提交。
> 本文档为完整上下文快照，新对话可直接从此处继续。

---

## 一、项目概况

DeepSeek v4 for Visual Studio 扩展（VSIX），net472 + WPF + WebView2。
Phase 1.5 目标：可观测性 → IDE Context → Copilot 交互 → 评测驱动优化。

## 二、已完成（全部验证通过）

| 模块 | 关键文件 | 说明 |
|------|---------|------|
| P0 Telemetry | Models/TelemetryModels.cs, Services/Telemetry/AgentMetricsCollector.cs | 会话 JSON 自动导出至 %LocalAppData%\DeepSeekVS\telemetry\；TTFT/轮次/工具/失败四分类 |
| P1-A IDE Context | Models/IdeContextModels.cs, Services/IdeContext/IdeContextTracker.cs | 活动文件/选区/光标/符号/诊断 volatile 注入；fail-closed |
| P1-B Inline Edit | Services/InlineEdit/, Commands/InlineAiEditCommand.cs, View/InlineEdit/ | Ctrl+I → 指令条 → 单次 LLM → EditorDiffMarkerService 预览管线 |
| P2 Debugger | 抽屉 UI(BuildDetoxEmojisJs) + 数据面(context_debug 字段) | 含 Working Set Top-N |
| P2 ToolResult | Models/ToolResultModels.cs | ❌/⏱️ 约定唯一解析点 |
| P2 Timeout | Services/Tools/ToolTimeoutPolicy.cs | 分档超时 + LLM 超时可配置 |
| P3 Benchmark | Services/Benchmark/, benchmark/ | 报告生成器 + 标注脚本 + 规程 + schema |
| 上游合入 | feature/v1.1.15 全量 | 截图工具/PDF视觉/FIM回退 |
| 设置迁移 | Settings/SettingsMigration.cs | RegLoadAppKey 跨实例导入（已实测成功 16 项）|
| VSEXT 声明 | Settings/DeepSeekUnifiedSettings.cs | SettingCategory + 7 Setting.Boolean/Integer 进新设置 UI |
| VSEXT 构建系统 | DeepSeekExtension.cs + csproj/manifest 改造 | VSSDK+VSEXT 混合模式已验证 |

## 三、关键架构决策

1. **单一事实源**：`DeepSeekOptionsPage.Instance` — 所有设置读写经此
2. **前缀缓存保护**：IDE 态仅进 volatile 块，会话结束清除
3. **Emoji 渲染层替换**：BuildDetoxEmojisJs（MutationObserver），数据层字节不变
4. **❌/⏱️ 为解析契约**：ToolExecutionOutcome.Classify 唯一权威点
5. **工具豁免超时**：交互式工具由 IsInteractiveTool 跳过（哨兵测试锁定边界）
6. **PS 脚本纯 ASCII**：PS5.1 无 BOM 时按 ANSI 解析非 ASCII 导致语法错误

## 四、构建环境（重要！）

- 编译器：**必须用 VS2026 MSBuild**（Roslyn 支持 LangVersion 14）
- VS2022 MSBuild 的 csc 不支持 C#14 → CS1617
- dotnet SDK 9 也不支持 → 同样失败
- 工具链变通：`tools/build-vs26.ps1` 创建合并式 Sdks junction 视图绕过缺失的 .NET SDK resolver
- 测试运行：VS2022 vstest.console.exe 可正常执行 net472 测试 DLL

```powershell
# 标准构建+测试命令
powershell -File tools\build-vs26.ps1
```

## 五、待办事项（按优先级）

### 🔴 用户反馈四项（2026-08-23 晚，最高优先级）
1. **回答气泡下"重来/复制"按钮未合理对齐** — ⚠️ 排查更正：CSS 类名完好
   （早前 `.ln` 为 rg -r 标志造成的显示假象）。真正根因需在 Exp 实例用 DevTools
   检查渲染后的 action row DOM/CSS；静态分析已排除类名缺失。
2. **输入框+按钮区域比例失调** — 需重新设计输入区布局
3. **MCP 对话框符号残留** ✅ 已修复（9037acf）：✓/✕/⚙ 全部改为本地化文本，
   Test 结果按钮显示 OK/成功、Error/失败，Delete 按钮文本化。布局重排仍待做。
4. **缺少 VS 工具接入** — 新功能需求，待明确范围（调试器变量/编辑器操作/Git 等）

### Step2c：观察者桥接（SettingCategory → Instance 回写）
- SettingCategory 已声明于 `Settings/DeepSeekUnifiedSettings.cs`（7 项 Boolean/Integer）
- 已启用 `GenerateObserverClass = true` 并调用 `services.AddSettingsObservers()`
- **新发现阻塞**：源生成器未实际产出 `GeneralCategoryObserver.g.cs`（Clean+Build 后 obj 下无该文件）
  - 早前失败构建日志中曾出现该文件路径，说明生成器识别了标记但可能因后续错误中断
  - 需检查 Extensibility.Sdk 版本是否完整支持 GenerateObserverClass
  - 或改用方案 B：通过 `IVsNotifyUnifiedSettings` interop 订阅变化事件
  - 或方案 C：在消费点直接读取 Unified Settings 值（每次调用时同步，无需缓存）

### MCP 对话框主题适配
- `View/McpConfigDialog.xaml` 有 42 个硬编码色值
- 方案：构造时检测 `ThemeService.Instance.IsLight` 切换双调色板
- emoji 已全部清除 ✓

### 十次基线会话
- 实验实例已装好最新 VSIX 且 ApiKey 已迁移就位
- 正常使用后运行：`benchmark\invoke-benchmark.ps1 -ReportOnly`
- 用 v0 报告的失败三分类分布决定 B 层优化方向

### 其他
- 推送 origin master --tags
- MCP 对话框 XAML 资源字典化（42 硬编码色值 → DynamicResource）
- RAG 冻结中：待 cross_file 类 Context 失败集中时立项 BM25

## 六、关键文件索引

| 文件 | 用途 |
|------|------|
| docs/Roadmap-Gap-Analysis.md | 路线差距分析（Phase 1.5 起点） |
| docs/Phase1.5-Delivery-Report.md | Phase 1.5 完整交付报告 |
| docs/Phase1.5-BuildTest-Report.md | 构建&测试报告 |
| docs/A-Phase-Execution-Report.md | A 阶段执行报告 |
| docs/Settings-UnifiedIntegration-Feasibility.md | Unified Settings 可行性调查（含 §八 Step2 深度调查）|
| CHANGELOG.md | v1.2.0 更新日志 |
| benchmark/README.md | Benchmark 规程 |
| tools/build-vs26.ps1 | 可复现构建入口 |
| tools/de-emoji.ps1 | locale emoji 清扫脚本 |

## 七、技术约束备忘

| 约束 | 原因 |
|------|------|
| net472 无 Math.Clamp | 手动收敛 |
| PS 脚本纯 ASCII | PS5.1 ANSI 解析非 ASCII 破坏语法 |
| LangVersion 14 仅 VS2026 Roslyn 支持 | VS2022 csc 上限 C#13 |
| string.Contains(s,StringComparison) 走 span 重载 | 需 C#14 语义，SDK9 下编译失败 |
| Setting ID 必须小写字母开头纯字母数字 | VSEXT 源生成器校验 |
