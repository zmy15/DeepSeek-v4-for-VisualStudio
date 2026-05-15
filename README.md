<div align="center">

> ⚠️ **本项目正在积极开发中，部分功能可能尚未完善，API 可能发生变动。**

# DeepSeek v4 for Visual Studio

**DeepSeek V4 · 深度思考 · MCP 协议 · Skills 技能系统 · 联网搜索 · OCR 图像识别 · 多智能体协作**

*将 DeepSeek V4 大模型深度集成到 Visual Studio 2022 的全能 AI 编程助手*

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![VS](https://img.shields.io/badge/VS-2022%2017.14%2B-purple.svg)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet.svg)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green.svg)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey.svg)]()

[English](README_EN.md)

</div>

---

## 这是什么？

**DeepSeek v4 for Visual Studio** 把 DeepSeek V4 模型直接嵌入你的编辑器。选中代码、粘贴截图、拖入文件——AI 就在旁边，随时响应。

它不只是聊天窗口，更是一套完整的 **AI 工作流系统**：多智能体协作引擎自动分派任务给最适合的 Agent，Skills 技能引擎让你定义可复用的 AI 工作流，MCP 协议让你接入任意工具生态，三大 OCR 引擎能读懂你的报错截图。

---

## 能力一览

```
🧠 DeepSeek V4          流式对话 · 深度思考 (Reasoning) · 双模型可选
🤖 多智能体系统          Ask / Explore / Plan / Edit 四种 Agent 自动协作
🔧 MCP 协议             多服务器连接 · Function Calling · 自定义工具扩展
📐 Skills 技能系统       斜杠命令 · 项目/用户/内置三级 · YAML 前置元数据
🌐 联网搜索              百度千帆 + DuckDuckGo 双引擎 · 额度耗尽自动切换
📄 文件解析              50+ 格式 · 代码/文档/PDF/Office 全支持
🔍 图像 OCR              Windows 内置 · PaddleOCR · MCP OCR 三引擎
📊 代码差异预览          编辑器内红绿标记 · 确认/撤销 · 一键应用
💡 代码补全              Ghost Text 行内预测 · 上下文感知 · 可配置延迟
💬 聊天窗口              WebView2 渲染 · Markdown 高亮 · 多会话持久化
⚙️ 可视化配置            Tools → Options 一站式设置
```

---

## 多智能体系统

本扩展内置四种专用 Agent，自动协作处理复杂任务：

| Agent | 角色 | 能力 |
|-------|------|------|
| **Ask** 🤔 | 问答助手 | 纯问答、代码解释、只读分析 |
| **Explore** 🔍 | 探索者 | 代码库搜索、文件发现、结构分析 |
| **Plan** 📋 | 规划者 | 任务规划、方案设计、禁止修改代码 |
| **Edit** ✏️ | 执行者 | 代码修改、文件操作、协调 Explore 发现文件 |

Agent 之间支持 **Handoff（移交）** 机制——例如 Plan 制定方案后自动移交给 Edit 执行，Edit 需要查找文件时调度 Explore 协助。

---

## Skills 技能系统

> 这是本扩展区别于普通 AI 插件的核心特性。

### 什么是 Skill？

Skill 就是一个 Markdown 文件 (`SKILL.md`)，用 YAML 前置元数据描述"何时触发、怎么做"：

```markdown
---
name: code-review
description: '审查代码质量、安全性、性能。Use when: code review, PR review, 代码审查'
argument-hint: '[file path or code]'
user-invocable: true
---

# 代码审查

## 流程
1. 从正确性、安全性、性能、可维护性、最佳实践五个维度分析
2. 🔴 严重 → 🟡 中等 → 🟢 建议  按优先级列出问题
3. 为每个问题提供修复方案和代码示例
```

### 三级技能来源

| 级别 | 路径 | 说明 |
|------|------|------|
| 📁 **项目级** | `.github/skills/` `.agents/skills/` `.claude/skills/` | 随项目版本管理，团队共享 |
| 👤 **用户级** | `~/.copilot/skills/` `~/.agents/skills/` | 个人偏好，跨项目通用 |
| 🏭 **内置级** | `BuiltInSkills/`（随扩展发布） | 开箱即用，如 `code-review` |

### 使用方式

在聊天窗口输入 `/` 即可触发斜杠命令自动补全，选择技能后 AI 会加载对应的工作流指令。

```text
/code-review  UserService.cs
```

---

## MCP 协议集成

通过 **Model Context Protocol (MCP)** 协议连接外部工具服务器，扩展 AI 能力边界：

- **多服务器支持**：同时连接多个 MCP Server，按需调用工具
- **Function Calling**：AI 自动判断何时调用外部工具
- **工具白名单**：每个 Agent 可声明允许使用的工具列表
- **持久化配置**：MCP 服务器配置存储在 `%LocalAppData%\DeepSeekVS\mcp_servers.json`
- **内置 OCR Server**：默认集成 PP-OCRv5（通过 `uvx paddleocr-mcp`）

配置入口：聊天窗口 → 点击 🔌 MCP 按钮 → 添加/管理服务器。

---

## 联网搜索

| 搜索引擎 | 特点 |
|----------|------|
| **百度千帆** | 每月 1500 次免费请求，额度耗尽自动切换 |
| **DuckDuckGo** | 完全免费，无额度限制 |

搜索关键词基于对话上下文智能生成，结果自动注入聊天上下文。

---

## 图像 OCR

三种引擎满足不同场景：

| 引擎 | 精度 | 配置 |
|------|------|------|
| **Windows 内置** | 一般 | 零配置，开箱即用 |
| **PaddleOCR-Sharp** | ≥95% 中文识别率 | 自动下载 ChineseV5 模型 |
| **MCP OCR** | 取决于服务端 | 需配置 MCP OCR 服务器 |

直接 `Ctrl+V` 粘贴报错截图，AI 自动识别文字并分析问题。

---

## 安装

### 推荐：下载 VSIX 安装

1. [**Releases**](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases) → 下载 `DeepSeek_v4_for_VisualStudio.vsix`
2. 关闭 Visual Studio → 双击 `.vsix` → 安装
3. 重启 Visual Studio

### 进阶：源码编译

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
# 用 VS 2022 打开 .slnx → Ctrl+Shift+B 编译 → F5 调试
```

**编译要求**：
- Visual Studio 2022 17.14+
- .NET Framework 4.7.2 SDK
- Visual Studio SDK（通过 VS Installer 安装）

---

## 快速上手

### ① 获取 API Key

[platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys) → 创建 Key → 复制

### ② 配置

`工具` → `选项` → `DeepSeek Chat` → 粘贴 Key → 选模型

| 设置项 | 推荐值 | 说明 |
|--------|--------|------|
| API Key | 你的密钥 | 从 platform.deepseek.com 获取 |
| Selected Model | `deepseek-v4-pro` | 主模型 |
| Enable Deep Thinking | ✅ 开启 | 启用推理链 |
| Reasoning Effort | `high` | 推理深度（high / max） |
| Search Provider | `DuckDuckGo` | 免费无需 Key，国内无法使用 |
| OCR Engine | `PaddleOCR-Sharp` | 中文识别最佳 |
| Show Diff Markers | ✅ 开启 | 代码修改预览 |
| Copilot Enable | ✅ 开启 | 行内代码补全 |

### ③ 开始对话

`视图` → `其他窗口` → `DeepSeek Chat`，或者点击工具栏 🧠 图标。

### ④ 常用操作

| 操作 | 方式 |
|------|------|
| 问代码问题 | 直接输入，AI 可读取当前打开的文件 |
| 解析文件内容 | 拖拽文件到聊天窗口 |
| 截图识别报错 | `Ctrl+V` 粘贴截图，自动 OCR |
| 联网查最新资料 | 勾选 🌐 联网搜索 |
| 调用 Skill | 输入 `/` 选择技能命令 |
| 配置 MCP 服务器 | 点击 🔌 MCP 按钮 |
| 预览代码修改 | 开启 Diff Markers，确认后应用 |
| 自动补全建议 | 启用心跳模式，输入时自动提示 |

---

## 配置项详解

### API 设置
- **API Key**：DeepSeek 平台 API 密钥
- **System Prompt**：自定义系统提示词（可选，留空使用默认）

### 模型设置
- **Selected Model**：选择使用的 DeepSeek 模型
- **Enable Deep Thinking**：启用深度思考（Reasoning）模式
- **Reasoning Effort**：推理深度，`high` 平衡速度与质量，`max` 最强推理

### 联网搜索
- **Enable Web Search**：开关联网搜索
- **Search Provider**：百度千帆 / DuckDuckGo
- **Baidu API Key**：百度千帆密钥（可选，每日1500次免费额度）

### 编辑器
- **Show Diff Markers in Editor**：是否在编辑器中显示代码修改标记

### OCR
- **OCR Engine**：选择 OCR 引擎（Windows 内置 / PaddleOCR-Sharp / MCP）

### 代码补全
- **Enable Copilot**：开关行内代码补全
- **Suggestion Interval**：输入停顿多久后触发补全建议

---

## 项目结构

```
DeepSeek_v4_for_VisualStudio/
├── DeepSeek_v4_for_VisualStudioPackage.cs    VS 扩展入口 (AsyncPackage)
├── source.extension.vsixmanifest             VSIX 清单
├── VSCommandTable.vsct                       菜单/工具栏命令表
│
├── Commands/
│   └── ShowChatWindowCommand.cs              窗口命令
│
├── Models/
│   ├── DeepSeekModels.cs                     API 请求/响应 · 流式 · Function Calling
│   ├── AgentModels.cs                        智能体数据模型
│   ├── AgentTypes.cs                         智能体类型枚举
│   ├── McpTypes.cs                           MCP JSON-RPC 2.0 协议类型
│   ├── SkillDefinition.cs                    Skill 定义 · 来源枚举 · 发现结果
│   ├── SkillSuggestionItem.cs                斜杠命令自动补全项
│   ├── ConversationTree.cs                   对话树数据结构
│   ├── ContextModels.cs                      上下文模型
│   ├── RagModels.cs                          RAG 检索增强模型
│   └── ToolCallAccumulator.cs                工具调用累加器
│
├── Services/
│   ├── DeepSeekApiService.cs                 API 通信（流式 + 思考模式）
│   ├── AgentDispatcher.cs                    ★ 多智能体调度中心
│   ├── SkillService.cs                       ★ Skills 发现/解析/缓存/事件
│   ├── McpManagerService.cs                  MCP 多服务器管理 & 工具聚合
│   ├── McpStdioClient.cs                     stdio 传输客户端
│   ├── McpConfigStore.cs                     MCP 配置 JSON 持久化
│   ├── WebSearchService.cs                   百度千帆 + DuckDuckGo 搜索
│   ├── FileParserService.cs                  50+ 文件格式解析
│   ├── OcrService.cs                         Windows/PaddleOCR/MCP 三引擎
│   ├── ChatHtmlService.cs                    WebView2 HTML 模板
│   ├── ChatPersistenceService.cs             聊天记录本地持久化
│   ├── ContextCompressorService.cs           上下文压缩（Token 预算管理）
│   ├── ConversationContextManager.cs          对话上下文构建
│   ├── CodeDiffService.cs                    代码差异计算
│   ├── DiffViewerService.cs                  差异可视化 & 标记
│   ├── EditorDiffMarkerService.cs            编辑器行内标记
│   ├── RagService.cs                         RAG 检索增强
│   └── AiPrompts.cs                          Prompt 集中管理
│   │
│   └── Agents/
│       ├── AskAgent.cs                       Ask 智能体
│       ├── ExploreAgent.cs                   Explore 智能体
│       ├── PlanAgent.cs                      Plan 智能体
│       └── EditAgent.cs                      Edit 智能体
│
├── Settings/
│   ├── DeepSeekOptionsPage.cs                Tools→Options 配置页
│   └── DownloadLinkEditor.cs                 UI 编辑器
│
├── CodeCompletion/
│   ├── InlinePredictionManager.cs            行内预测管理器
│   ├── GhostTextTagger.cs                    Ghost Text 标记器
│   ├── GhostTextTaggerProvider.cs            Ghost Text 提供者
│   └── CommandFilter.cs                      命令过滤器
│
├── View/
│   ├── DeepSeekChatWindowPane.cs             VS ToolWindow 面板
│   ├── DeepSeekChatControl.xaml/.cs          WPF 主控件
│   ├── DeepSeekChatControl.Events.cs         事件处理（分部类）
│   ├── DeepSeekChatControl.Messaging.cs      消息收发（分部类）
│   ├── DeepSeekChatControl.Rendering.cs      界面渲染（分部类）
│   ├── DeepSeekChatControl.Sessions.cs       会话管理（分部类）
│   ├── DeepSeekChatControl.Clipboard.cs      剪贴板 OCR（分部类）
│   ├── DeepSeekChatControl.Agent.cs          智能体交互（分部类）
│   ├── DeepSeekChatControl.CodeActions.cs    代码操作（分部类）
│   ├── DeepSeekChatControl.Search.cs         搜索功能（分部类）
│   ├── DeepSeekChatControl.Skills.cs         技能系统（分部类）
│   ├── DiffPreviewAdornment.cs               差异预览装饰器
│   ├── DiffViewerWindow.xaml/.cs             差异查看器窗口
│   └── McpConfigDialog.xaml/.cs              MCP 配置对话框
│
├── Utils/
│   ├── Logger.cs                             日志工具
│   └── StringExtensions.cs                   字符串扩展
│
└── Resources/                                图标/样式资源
```

---

## 技术栈

| 层 | 选型 |
|---|------|
| 运行时 | .NET Framework 4.7.2 · WPF |
| VS SDK | Microsoft.VisualStudio.SDK 17.14 |
| 聊天 UI | WebView2 (Chromium) |
| Markdown | Markdig 1.1.3 |
| 文档解析 | NPOI 2.8.0 · PdfPig 0.1.14 |
| OCR | Windows.Media.Ocr · PaddleOCR 3.0.1 · OpenCvSharp 4.10 |
| 序列化 | System.Text.Json |
| MCP | JSON-RPC 2.0 over stdio |

---

## 开发

### 环境

- Visual Studio 2022 v17.14+
- .NET Framework 4.7.2 SDK
- **Visual Studio Extension Development** 工作负载

### 调试

`F5` → 启动实验性 VS 实例 → 扩展自动加载

### 打包

```powershell
msbuild DeepSeek_v4_for_VisualStudio.csproj /p:Configuration=Release
# → bin/Release/net472/DeepSeek_v4_for_VisualStudio.vsix
```

---

## 常见问题

<details>
<summary><b>找不到聊天窗口？</b></summary>

重启 VS → `视图` → `其他窗口` → `DeepSeek Chat`。检查 `扩展` → `管理扩展` 是否已启用。
</details>

<details>
<summary><b>API Key 无效？</b></summary>

从 [platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys) 重新获取，确认有余额。配置路径：`工具` → `选项` → `DeepSeek Chat`。
</details>

<details>
<summary><b>OCR 中文不准？</b></summary>

`工具` → `选项` → `DeepSeek Chat` → OCR Settings → 切换到 `PaddleOCR`。模型随 NuGet 包自动部署。
</details>

<details>
<summary><b>怎么创建自定义 Skill？</b></summary>

在项目的 `.github/skills/my-skill/SKILL.md` 创建文件，写入 YAML 前置元数据 + Markdown 指令。在聊天窗口输入 `/my-skill` 即可调用。详见上方 [Skills 技能系统](#skills-技能系统)。
</details>

<details>
<summary><b>怎么接入 MCP 服务器？</b></summary>

点击聊天窗口 🔌 按钮 → 添加服务器配置（名称、启动命令、参数）→ 保存后自动连接加载工具。
</details>

<details>
<summary><b>WebView2 报错？</b></summary>

扩展已内置 x64 Runtime。如仍有问题：[下载 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。
</details>

---

## 贡献

1. Fork → 创建分支 → 修改 → Push → 提交 PR
2. Commit 格式使用 [Conventional Commits](https://www.conventionalcommits.org/)：`feat:` / `fix:` / `docs:` / `refactor:` / `chore:`

---

## 测试

本扩展包含 **86 个 xUnit 测试**，覆盖模型序列化、补丁解析、上下文管理、API 流式响应等核心路径。

### 运行测试

```powershell
# 运行所有测试
dotnet test DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj

# 带覆盖率报告
dotnet test DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj `
    /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura
```

### 测试技术栈

| 组件 | 版本 | 用途 |
|------|------|------|
| xUnit | 2.9.x | 测试框架 |
| Moq | 4.20.x | Mock 框架 |
| FluentAssertions | 6.12.x | 断言库 |
| coverlet | 6.0.x | 代码覆盖率 |

### 测试结构

```
DeepSeek_v4_for_VisualStudio.Tests/
├── Unit/
│   ├── Models/       # 序列化、枚举、工具调用解析
│   ├── Services/     # 补丁解析、4级匹配、上下文管理
│   └── Utils/        # 字符串扩展
├── Integration/       # API 流式响应、持久化、Agent 调度
├── TestData/          # 测试 JSON/技能文件
└── Fixtures/          # DI 容器 fixture
```

---

## 致谢

- [DeepSeek](https://www.deepseek.com/) — 强大的 AI 模型支持
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — 出色的 OCR 引擎
- [Markdig](https://github.com/xoofx/markdig) — 快速的 Markdown 解析器

---

## 许可证

[MIT](LICENSE) © [zmy15](https://github.com/zmy15)

---

<div align="center">

**Issues** · [github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues)

</div>
