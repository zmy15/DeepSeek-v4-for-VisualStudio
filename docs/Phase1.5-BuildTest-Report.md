# Phase 1.5 构建 & 测试报告（真实 VS 工具链）

> 日期：2026-08-22　提交：`b1b8eb1`（基线 `644068a` 起 18 个提交）
> 结论先行：**VSIX 整包构建成功，910/910 单元测试全部通过**。

---

## 一、测试环境

| 项 | 值 |
|----|----|
| 编译器 | VS2026 Community 18.7.3 自带 Roslyn（支持 `LangVersion 14`） |
| 构建引擎 | VS2026 MSBuild + 合并式 Sdks 目录（见 §二） |
| 测试运行器 | VS2022 Pro 17.14.35 `vstest.console.exe` |
| 目标框架 | net472（主工程与测试工程一致） |
| 产物 | `bin\Debug\net472\DeepSeek_v4_for_VisualStudio.vsix`（约 910 MB，含 PaddleOCR/OpenCvSharp 原生依赖） |

## 二、工具链障碍与解法

本机此前无法整包构建的根因与处置：

| 障碍 | 根因 | 解法 |
|------|------|------|
| dotnet CLI 报 CS1617 | SDK9 Roslyn 不认识字面量 `LangVersion 14` | 改用 VS2026 MSBuild 的 Roslyn |
| VS2022 MSBuild 同样 CS1617 | 17.14 的 csc 仅支持到 C#13 | 同上 |
| VS2026 MSBuild 报 MSB4236 找不到 Microsoft.NET.Sdk | 该实例未安装 .NET SDK 解析器组件 | 设 `MSBuildSDKsPath` 指向合并视图：对 `dotnet\sdk\9.0.315\Sdks` 逐目录建 junction，并合成三个 workload 定位器桩（AutoImport.props / WorkloadManifest.targets 等空项目文件）。编译器仍为 VS2026，仅借用目标文件 |

> ⚠️ 复现命令需带环境变量 `$env:MSBuildSDKsPath = <合并视图>`。该视图在 `%TEMP%`，重启丢失后可由脚本重建（后续可固化为 `benchmark/build-vs26.ps1`）。
> 正式开发建议：在 VS2026 安装器勾选 ".NET desktop development" 工作负载以获得原生解析器，届时无需任何变通。

## 三、真实构建暴露并已修复的问题（提交 `b1b8eb1`）

独立 harness 无法覆盖、只有整包编译能暴露 —— 全部位于 Phase 1.5 新增代码：

| # | 文件 | 问题 → 修复 |
|---|------|------------|
| 1 | Utils/IsExternalInitShim.cs（新增） | net472 无 `IsExternalInit`，所有 `{ get; init; }` 编译失败 → 按惯例加程序集内垫片 |
| 2 | View/InlineEdit/InlineEditBarWindow.cs | 缺 `using System.Threading.Tasks`（TCS 不可见） |
| 3 | Services/ConversationContextManager.cs | Context 探针用 `[JsonIgnore]` 但缺 `System.Text.Json.Serialization` using |
| 4 | Services/IdeContext/IdeContextTracker.cs | API 名错误：实际为 `GetErrorTagger(buffer)`；且 `SimpleTagger.GetTags` 返回快照已解析的 `ITagSpan<ErrorTag>`，无需再做 GetSpans 映射 |
| 5 | Commands/InlineAiEditCommand.cs | `IWpfTextViewLine` 无 `Bounds` 属性 → 改用 `Left`/`Top` |
| 6 | Services/Benchmark/BenchmarkReportGenerator.cs | net472 字典无 `GetValueOrDefault` 扩展 → TryGetValue 模式 |
| 7 | VSCommandTable.vsct | `IDG_VS_CTXT_CODEWIN` 符号不存在 → 自建组挂到 `IDM_VS_CTXT_CODEWIN`；KeyBinding 的 editor GUID 需声明为 GuidSymbol |

## 四、结果

### 4.1 构建

```
DeepSeek_v4_for_VisualStudio.dll      2.1 MB   ✅
DeepSeek_v4_for_VisualStudio.vsix     ~910 MB  ✅
ExitCode=0（"已成功生成"）
警告 369 条 —— 全部为存量 VSTHRD010/CA 类告警，非本次新增引入
```

### 4.2 单元测试（vstest.console 实测）

```
测试总数: 910    通过: 910    失败: 0    总时长: 4.16 s
```

Phase 1.5 新增测试类在真实运行中的执行情况：

| 测试类 | 用例数 |
|--------|-------:|
| AgentMetricsCollectorTests | 22 |
| IdeContextModelsTests（含符号提取 9 例） | 31 |
| InlineEditServiceTests | 7 |
| ToolTimeoutPolicyTests | 8 |
| ToolResultModelsTests | 7 |
| BenchmarkReportGeneratorTests | 4 |
| **小计** | **79** |

> 注：仓库 README 所写 "473+" 为历史数字；当前套件实际为 910。

## 五、遗留事项

1. **F5 冒烟**（人工）：安装 VSIX 至实验实例 → 验证 Ctrl+I 指令条 / Context 抽屉 / 遥测落盘三件套
2. 正式开发机补装 VS2026 的 .NET 桌面工作负载，消除 MSBuildSDKsPath 变通
3. 把合并视图重建逻辑固化为脚本并入 CI（CI 若已有完整组件则无需）

*本报告对应提交 `b1b8eb1`；构建日志存于 %TEMP%\opencode\build-main12.log / vstest.log。*
