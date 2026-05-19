<div align="center">

> ⚠️ **本项目正在积极开发中，部分功能可能尚未完善，API 可能发生变动。**

# DeepSeek v4 for Visual Studio

**DeepSeek V4 · 深度思考 · 1M 上下文 · 多智能体协作 · Skills 技能系统 · MCP 协议 · 联网搜索 · OCR 图像识别**

*将 DeepSeek V4 大模型深度集成到 Visual Studio 2022+ 的全能 AI 编程助手*

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

它不只是聊天窗口，更是一套完整的 **AI 工作流系统**：

- **多智能体协作引擎** — 四种专用 Agent 自动分派、移交任务
- **Skills 技能引擎** — 用 Markdown 定义可复用的 AI 工作流
- **MCP 协议** — 接入任意工具生态，Function Calling 自动调用
- **1M Token 上下文** — 承载大型代码库，智能压缩不丢信息
- **三种编辑方法** — Patch / Insert / Create，四级匹配精准应用
- **RAG 检索增强** — 可插拔的知识库集成
- **双 OCR 引擎** — 读懂你的报错截图

---

## 能力一览

| 能力 | 说明 |
|------|------|
| 🧠 **DeepSeek V4** | 流式对话 · 深度思考 (Reasoning) · 双模型可选 (Pro / Flash) |
| 🤖 **多智能体系统** | Ask / Explore / Plan / Edit 四种 Agent，Handoff 自动协作 |
| 🔧 **MCP 协议** | 多服务器连接 · Function Calling · 工具白名单 · 持久化配置 |
| 📐 **Skills 技能** | 斜杠命令 · 项目/用户/内置三级 · YAML 前置元数据 |
| 📝 **三种编辑方法** | apply_patch / insert_edit_into_file / create_file，四级匹配 + Healing 修复 |
| 📚 **1M 上下文** | 900K Token 预算 · 上下文压缩 · 文件不再截断 |
| 🔍 **RAG 检索** | 可插拔提供者接口 · 智能缓存 · 自动注入对话上下文 · 🚧 内置向量库开发中 |
| 🌐 **联网搜索** | 百度千帆 (月1500次免费) + DuckDuckGo 双引擎 · 额度耗尽自动切换 |
| 📄 **文件解析** | 50+ 格式 · 代码/文档/PDF/Word/Excel 全支持 · 拖拽即解析 |
| 🖼️ **图像 OCR** | Windows 内置 · MCP 远程 OCR 双引擎 |
| 📊 **代码差异预览** | 编辑器内红绿 Diff 标记 · 确认/撤销/一键应用 |
| 💡 **Ghost Text 补全** | 行内灰色预测 · 上下文感知 · 可配置防抖延迟 |
| 💬 **聊天窗口** | WebView2 渲染 · Markdown/代码高亮 · 多会话持久化 · 计划实时展示 |
| ⚙️ **可视化配置** | Tools → Options 一站式设置 · 上下文/搜索/OCR 分类管理 |

---

## 多智能体协作系统

本扩展内置四种专用 Agent，通过 **Handoff（移交）** 机制自动协作，无需手动切换：

| Agent | 角色 | 能力 | 可移交至 |
|-------|------|------|----------|
| **Ask** 🤔 | 问答助手 | 代码解释、只读分析、知识问答 | Explore |
| **Explore** 🔍 | 探索者 | 代码库搜索、文件发现、结构分析、引用追踪 | Ask, Plan, Edit |
| **Plan** 📋 | 规划者 | 任务分解、方案设计、生成 plan.md | Edit, Explore |
| **Edit** ✏️ | 执行者 | 代码写入/删除、文件操作、编辑后诊断 | Explore, Ask |

### 典型协作流程

```
用户提问 → Ask (分析问题)
              ↓ 需要规划
           Plan (制定方案 → 生成 plan.md)
              ↓ Handoff
           Edit (执行修改 → 通知 Explore 探查文件)
              ↓ 完成后
           Ask (总结汇报)
```

每个 Agent 有独立的系统提示词、工具白名单和权限边界，确保安全可控。

---

## Skills 技能系统

> 🔥 这是本扩展区别于普通 AI 插件的核心特性。

### 什么是 Skill？

Skill 是一个 Markdown 文件 (`SKILL.md`)，用 YAML 前置元数据描述 **"何时触发、怎么做"**，AI 加载后即获得对应领域的专业工作指令。

```markdown
---
name: code-review
description: '审查代码质量、安全性、性能。Use when: code review, PR review, 代码审查'
argument-hint: '[文件路径或代码片段]'
user-invocable: true
---

# 代码审查

## 审查流程
1. 从正确性、安全性、性能、可维护性、最佳实践五个维度分析
2. 🔴 严重 → 🟡 中等 → 🟢 建议，按优先级列出问题
3. 为每个问题提供修复方案和代码示例
```

### 三级技能来源

| 级别 | 路径 | 适用场景 |
|------|------|----------|
| 📁 **项目级** | `.github/skills/` `.agents/skills/` `.claude/skills/` | 随项目版本管理，团队共享 |
| 👤 **用户级** | `~/.copilot/skills/` `~/.agents/skills/` | 个人偏好，跨项目通用 |
| 🏭 **内置级** | `BuiltInSkills/`（随扩展发布） | 开箱即用，如 `code-review` |

### 使用方式

在聊天窗口输入 `/` 触发斜杠命令自动补全，选择技能后 AI 加载对应工作流：

```text
/code-review  UserService.cs
/tdd          实现用户登录功能
/triage       #42 这个 Bug 应如何处理
```

---

## MCP 协议集成

通过 **Model Context Protocol (MCP)** 连接外部工具服务器，无限扩展 AI 能力：

- **多服务器同时连接**：每台服务器独立进程，互不干扰
- **自动 Function Calling**：AI 判断时机，自动调用 MCP 工具
- **工具白名单**：每个 Agent 可声明允许使用的工具，精细化权限控制
- **持久化配置**：`%LocalAppData%\DeepSeekVS\mcp_servers.json` 保存服务器列表
- **内置 OCR Server**：默认集成 PP-OCRv5（`uvx paddleocr-mcp`）
- **内部工具过滤**：OCR 等内部工具自动从 AI 可见列表中隐藏，避免误调用

配置入口：聊天窗口 → 🔌 MCP 按钮 → 添加/管理服务器。

---

## 1M 上下文与压缩

充分利用 DeepSeek V4 的 1M Token 上下文窗口：

### Token 预算管理

| 参数 | 值 | 说明 |
|------|-----|------|
| Token 上限 | 900K | 保留 100K 给输出 |
| 文件大小限制 | 无上限 | 不再截断文件内容 |
| 自动压缩阈值 | 85% | 达到上限时触发压缩 |

### 上下文压缩

当使用率超过 85% 时，自动压缩早期对话轮次：

- **保留最近 3 轮**完整对话
- **更早轮次**压缩为精简摘要，以 system 消息注入
- 支持 **LLM 摘要**和**规则提取**双模式
- 压缩摘要可被**再次压缩**（渐进式）
- 实时 `ContextStats` 可查询各维度 Token 分布

可在 `工具 → 选项 → DeepSeek Chat → Context Management` 中配置压缩参数。

---

## RAG 检索增强

> ⚠️ **规划中**：`IRagProvider` 接口和 `RagService` 注册/缓存基础设施已就位，内置本地向量库提供者正在开发中。当前可通过实现 `IRagProvider` 接入自定义 RAG 后端。

可插拔的 RAG (Retrieval-Augmented Generation) 集成，为 AI 提供项目知识库支持：

- **提供者接口 (`IRagProvider`)**：注册任意 RAG 后端
- **智能缓存**：Jaccard 相似度 ≥60% 的连续查询复用结果
- **自动注入**：检索结果在每轮对话前注入上下文
- **多提供者支持**：按名称切换活跃提供者
- **🚧 内置本地向量库**：基于 SQLite + 本地嵌入模型的零配置方案（规划中）

```csharp
// 注册自定义 RAG 提供者
var ragService = new RagService();
ragService.RegisterProvider(new MyCustomRagProvider());
ragService.SetActiveProvider("MyProvider");
ragService.IsEnabled = true;
```

> 📋 详见 [路线图 / TODO](#-路线图--todo) 中的 RAG 相关计划。

---

## 联网搜索

| 搜索引擎 | 特点 | 额度 |
|----------|------|------|
| **百度千帆** | 中文搜索结果优质 | 每月 1500 次免费，超额自动切换 |
| **DuckDuckGo** | 完全免费，隐私保护 | 国内不可用 |

- 基于对话上下文**智能生成**搜索关键词
- 搜索结果**自动注入**聊天上下文
- 百度额度耗尽时**无缝切换**到 DuckDuckGo

---

## 文件解析

支持拖拽或粘贴 **50+ 种文件格式**，自动提取文本内容：

| 类别 | 格式 |
|------|------|
| 代码 | `.cs` `.py` `.java` `.js` `.ts` `.go` `.rs` `.cpp` `.c` `.h` `.swift` `.kt` `.rb` `.php` `.sql` `.html` `.css` `.xml` `.json` `.yaml` `.toml` `.proto` 等 |
| 文档 | `.txt` `.md` `.rst` `.log` `.csv` |
| Office | `.doc` `.docx` `.xls` `.xlsx` |
| PDF | `.pdf`（UglyToad.PdfPig 解析） |
| 图片 | `.png` `.jpg` `.jpeg` `.bmp` `.gif` `.tiff` `.webp` → 自动 OCR |

**操作方式**：直接从文件管理器拖拽文件到聊天窗口，或 `Ctrl+V` 粘贴。

---

## 图像 OCR

两种 OCR 引擎满足不同场景：

| 引擎 | 中文识别率 | 配置难度 | 适用场景 |
|------|-----------|----------|----------|
| **Windows 内置** | 一般 | 零配置 | 英文截图、快速查看 |
| **MCP OCR** | 取决于服务端 | 需配置服务器 | 中文/高精度 OCR（推荐） |

> 💡 直接 `Ctrl+V` 粘贴报错截图，AI 自动识别文字并分析问题，无需手动输入错误信息。

---

## 代码差异预览

AI 修改代码后，在编辑器中以 **红色（删除）/ 绿色（新增）** 标记差异：

- **实时预览**：修改前预览所有变更
- **逐条确认**：Accept / Undo 每个 Diff 块
- **一键全部应用**：确认无误后一次性接受所有修改
- **编辑后诊断**：自动检查新引入的编译错误

通过 `DeepSeekOptionsPage` 可开关此功能。

---

## Ghost Text 代码补全

编辑器内的灰色幽灵文本预测，类似 GitHub Copilot：

- **上下文感知**：利用当前文件内容和光标位置
- **防抖延迟**：可配置触发间隔，避免频繁请求
- **缓存机制**：LRU 缓存 10 个最近补全结果
- **非侵入式**：灰色文本，Tab 接受，Esc 取消

在 `工具 → 选项 → DeepSeek Chat` 中启用和配置。

---

## 安装

### 推荐：下载 VSIX 安装包

1. [**Releases**](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases) → 下载 `DeepSeek_v4_for_VisualStudio.vsix`
2. 关闭所有 Visual Studio 实例
3. 双击 `.vsix` 文件 → 按提示安装
4. 重启 Visual Studio

### 进阶：从源码编译

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
# 用 VS 2022 打开 .slnx → Ctrl+Shift+B 编译 → F5 启动实验实例
```

**编译环境要求**：

| 组件 | 版本 |
|------|------|
| Visual Studio | 2022 (17.14+) |
| .NET Framework SDK | 4.7.2 |
| Visual Studio SDK | 通过 VS Installer → 修改 → 勾选 "Visual Studio 扩展开发" |
| Windows | 10/11 x64 |

---

## 🌐 国际化 (i18n) / 多语言支持

DeepSeek v4 for Visual Studio 支持**中文和英文**界面，自动跟随系统语言，也可手动切换。

### 自动语言检测

插件启动时自动检测 Windows 系统 UI 语言：
- **中文系统** → 显示中文界面和提示词
- **英文/其他系统** → 显示英文界面和提示词

### 手动切换语言

1. 打开 `工具 → 选项 → DeepSeek Chat`
2. 找到 **Language / 语言** 分类
3. 在 **界面语言 / Language** 下拉框中选择：
   - `auto` — 自动检测系统语言（默认）
   - `zh-CN` — 强制使用中文
   - `en` — 强制使用英文
4. 点击"确定"，界面立即生效

### 自定义翻译

你可以创建自定义翻译文件来覆盖默认翻译：

1. 在扩展安装目录的 `Resources\Locales\` 下创建 `zh-CN.user.json` 或 `en.user.json`
2. 写入你想要覆盖的键值对，例如：

```json
{
  "ui.welcomeMessage": "你好！欢迎使用自定义欢迎语！\n开始提问吧！"
}
```

3. 重启 Visual Studio 或切换语言后生效

### 覆盖范围

i18n 覆盖以下内容：
- **界面文字** — 工具窗口标题、按钮标签、对话框
- **AI 提示词** — 系统提示词、技能提示词、Agent 路由提示词
- **输出信息** — 欢迎语、错误提示、API 状态消息
- **设置页面** — 所有选项的显示名称和描述

---

## 快速上手

### ① 获取 API Key

访问 [platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys) → 创建 API Key → 复制。

### ② 配置扩展

`工具` → `选项` → `DeepSeek Chat` → 粘贴 API Key → 选择模型。

| 设置项 | 推荐值 | 说明 |
|--------|--------|------|
| API Key | 你的密钥 | 从 platform.deepseek.com 获取 |
| Selected Model | `deepseek-v4-pro` | Pro 模型推理能力更强 |
| Enable Deep Thinking | ✅ 开启 | 让模型展示推理过程 |
| Reasoning Effort | `high` | 推理深度（high / max） |
| Search Provider | `百度千帆` | 国内推荐，月1500次免费 |
| OCR Engine | `Windows Built-in` | 零配置即可使用 |
| Show Diff Markers | ✅ 开启 | 修改前预览差异 |
| Copilot Enable | ✅ 开启 | 行内代码补全 |
| Token Budget | `900000` | 1M 上下文上限 |
| Auto Compression | ✅ 开启 | Token 超限时自动压缩 |

### ③ 打开聊天窗口

`视图` → `其他窗口` → `DeepSeek Chat`，或点击工具栏图标。

### ④ 常用操作速查

| 操作 | 方式 |
|------|------|
| 代码问答 | 直接输入问题，AI 自动读取当前打开的文件 |
| 文件内容解析 | 从文件管理器拖拽文件到聊天窗口 |
| 截图识别报错 | `Ctrl+V` 粘贴截图，自动 OCR 识别 |
| 联网查资料 | 勾选聊天窗口的 🌐 联网搜索 |
| 调用技能 | 输入 `/` 选择斜杠命令 |
| 管理 MCP 服务器 | 聊天窗口 → 点击 🔌 MCP 按钮 |
| 切换 Agent | 聊天窗口顶部的 Agent 选择器 |
| 多会话管理 | 左侧会话列表 → 新建/切换/删除 |

---

## 架构概览

```
┌─────────────────────────────────────────────────────────┐
│                   Visual Studio 2022                     │
│  ┌─────────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │  Chat Window │  │  Diff Viewer  │  │  Ghost Text   │  │
│  │  (WebView2)  │  │  (Adornment)  │  │  (Tagger)     │  │
│  └──────┬───────┘  └──────┬───────┘  └───────┬───────┘  │
│         │                 │                   │          │
│  ┌──────┴─────────────────┴───────────────────┴───────┐  │
│  │                 AgentDispatcher                    │  │
│  │         (中央路由 · Handoff 管理)                    │  │
│  └──────┬──────┬──────┬──────┬────────────────────────┘  │
│         │      │      │      │                           │
│    ┌────┴─┐ ┌─┴───┐ ┌┴───┐ ┌┴────┐                      │
│    │ Ask  │ │Expl.│ │Plan│ │Edit │                      │
│    └──────┘ └─────┘ └────┘ └─────┘                      │
│                                                          │
│  ┌───────────────────────────────────────────────────┐  │
│  │                  服务层                             │  │
│  │  DeepSeekApi │ SkillService │ McpManager │ OCR     │  │
│  │  FileParser  │ EditPatch    │ WebSearch  │ RAG     │  │
│  │  ContextMgr  │ Compressor   │ DiffMarker │ ChatPst │  │
│  └───────────────────────────────────────────────────┘  │
│                                                          │
│  ┌───────────────────────────────────────────────────┐  │
│  │                  外部服务                           │  │
│  │  api.deepseek.com  │  MCP Servers  │  Search APIs  │  │
│  └───────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
```

### 核心服务

| 服务 | 职责 |
|------|------|
| `AgentDispatcher` | 多 Agent 中央路由，Handoff 协调，工作流编排 |
| `DeepSeekApiService` | DeepSeek API 调用，流式响应，Thinking/Reasoning 控制 |
| `SkillService` | Skill 发现、加载、YAML 解析、斜杠命令补全 |
| `McpManagerService` | MCP 服务器生命周期管理，工具聚合与调用 |
| `EditPatchService` | 三种编辑方法解析、四级匹配、Healing 修复、诊断检查 |
| `ContextCompressorService` | 上下文压缩，LLM/规则双模式摘要 |
| `RagService` | RAG 提供者注册、激活、检索结果注入 |
| `ConversationContextManager` | 对话上下文构建，Token 预算管理，消息修剪 |
| `WebSearchService` | 双引擎搜索，自动切换，关键词智能生成 |
| `OcrService` | 三引擎 OCR 统一接口 |
| `FileParserService` | 50+ 格式文件文本提取 |
| `ChatHtmlService` | WebView2 HTML/CSS/JS 生成，Markdown 渲染 |
| `CodeDiffService` | 代码差异计算与编辑器标记 |
| `ChatPersistenceService` | 多会话持久化存储 |

---

## 开发指南

### 项目结构

```
DeepSeek_v4_for_VisualStudio/
├── Models/                  # 数据模型
│   ├── AgentModels.cs       # Agent 任务计划模型
│   ├── AgentTypes.cs        # Agent 类型枚举与定义
│   ├── ContextModels.cs     # 上下文统计与压缩模型
│   ├── DeepSeekModels.cs    # DeepSeek API 请求/响应模型
│   ├── EditPatchModels.cs   # 编辑补丁模型
│   ├── McpTypes.cs          # MCP 协议类型
│   ├── RagModels.cs         # RAG 检索模型
│   ├── SkillDefinition.cs   # 技能定义模型
│   └── TreeModels.cs        # 文件树模型
├── Services/                # 业务服务
│   ├── Agents/              # Agent 实现
│   │   ├── BaseAgent.cs     # Agent 基类
│   │   ├── AskAgent.cs      # 问答 Agent
│   │   ├── ExploreAgent.cs  # 探索 Agent
│   │   ├── PlanAgent.cs     # 规划 Agent
│   │   └── EditAgent.cs     # 编辑 Agent
│   ├── AgentDispatcher.cs   # Agent 调度器
│   ├── ChatHtmlService.cs   # 聊天 HTML 渲染
│   ├── CodeDiffService.cs   # 代码差异服务
│   ├── ContextCompressorService.cs  # 上下文压缩
│   ├── ConversationContextManager.cs # 对话上下文管理
│   ├── DeepSeekApiService.cs # API 服务
│   ├── EditPatchService.cs  # 编辑补丁服务
│   ├── FileParserService.cs # 文件解析
│   ├── McpManagerService.cs # MCP 管理
│   ├── OcrService.cs        # OCR 服务
│   ├── RagService.cs        # RAG 服务
│   ├── SkillService.cs      # 技能服务
│   └── WebSearchService.cs  # 搜索服务
├── View/                    # UI 视图
│   └── DeepSeekChatControl* # 聊天窗口控件 (WebView2)
├── CodeCompletion/          # 代码补全
│   ├── GhostTextTagger.cs   # 幽灵文本标记
│   └── InlinePredictionManager.cs  # 内联预测管理
├── Commands/                # VS 命令
├── Settings/                # 选项页
├── ToolWindows/             # 工具窗口
└── Utils/                   # 工具类
```

### 调试

1. 在 VS 2022 中打开 `.slnx`
2. 设置为 Debug 配置
3. `F5` 启动实验实例 (Experimental Instance)
4. 在实验实例中打开/创建项目 → `视图 → 其他窗口 → DeepSeek Chat`

### 测试

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
## 常见问题

<details>
<summary><b>Q: 为什么聊天窗口显示空白？</b></summary>

确保已安装 **WebView2 运行时**。VS 2022 通常自带，如缺失请从 [developer.microsoft.com/microsoft-edge/webview2](https://developer.microsoft.com/microsoft-edge/webview2) 下载。
</details>

<details>
<summary><b>Q: API 调用失败，提示 401？</b></summary>

检查 API Key 是否正确：`工具 → 选项 → DeepSeek Chat → API Key`。确保 Key 来自 [platform.deepseek.com](https://platform.deepseek.com)，且账户有余额。
</details>

<details>
<summary><b>Q: OCR 识别中文不准？</b></summary>

配置 MCP OCR 服务器（如 paddleocr-mcp）以获得高精度中文 OCR。`工具 → 选项 → DeepSeek Chat → MCP 配置`。
</details>

<details>
<summary><b>Q: 百度搜索无法使用？</b></summary>

百度千帆需要配置 API Key（在选项页 Search 分类中）。如果没有百度 Key，可以切换到 DuckDuckGo（完全免费），但国内可能访问较慢。
</details>

<details>
<summary><b>Q: 如何添加自定义 Skill？</b></summary>

在项目根目录创建 `.github/skills/` 文件夹，放入 `SKILL.md` 文件（格式见 [Skills 技能系统](#skills-技能系统)）。重启聊天窗口即可发现。
</details>

<details>
<summary><b>Q: 扩展与 GitHub Copilot 冲突吗？</b></summary>

不冲突。本扩展的 Ghost Text 补全独立于 GitHub Copilot，可以与 Copilot 同时使用。如需关闭本扩展的补全，在选项页中取消勾选 "Copilot Enable"。
</details>

---

## 🗺️ 路线图 / TODO

以下功能已在架构中预留接口或基础设施，正在规划或开发中：

### 🔍 RAG 检索增强生成

> 当前状态：`IRagProvider` 接口和 `RagService` 注册/缓存基础设施已就位，但尚无内置提供者实现。

| 计划项 | 说明 | 优先级 |
|--------|------|--------|
| **内置本地向量库提供者** | 基于 SQLite + 本地嵌入模型（如 `all-MiniLM-L6-v2`），实现开箱即用的项目级代码索引和语义检索 | 🔴 高 |
| **文件自动索引** | 打开项目时自动扫描代码文件并构建向量索引，无需手动配置 | 🔴 高 |
| **嵌入模型配置 UI** | 在选项页中提供嵌入模型选择（本地 / API），支持 DeepSeek Embedding API | 🟡 中 |
| **混合检索策略** | BM25 关键词 + 向量语义混合检索，提升代码搜索精度 | 🟡 中 |
| **增量索引更新** | 文件变更时自动增量更新索引，避免全量重建 | 🟢 低 |

### 🧪 测试与质量

| 计划项 | 说明 | 优先级 |
|--------|------|--------|
| **单元测试生成 Skill** | 基于 `tdd` 技能，自动为选中代码生成 xUnit 单元测试 | 🟡 中 |
| **集成测试扩展** | 增加对 Agent Handoff 流程、MCP 工具调用链的集成测试覆盖 | 🟡 中 |
| **UI 自动化测试** | WebView2 聊天窗口的自动化回归测试 | 🟢 低 |

### 🔧 工具与集成

| 计划项 | 说明 | 优先级 |
|--------|------|--------|
| **Git 增强集成** | PR 描述生成、Commit Message 自动撰写、代码审查评论 | 🟡 中 |
| **解决方案级代码索引** | 跨项目的符号索引和引用追踪，支持大型解决方案 | 🟡 中 |
| **自定义 MCP 服务器模板** | 提供常用 MCP 服务器的一键部署模板（如数据库查询、API 文档） | 🟢 低 |
| **本地模型支持** | 支持通过 Ollama / LM Studio 等本地推理后端，实现离线使用 | 🟢 低 |

### 🎨 用户体验

| 计划项 | 说明 | 优先级 |
|--------|------|--------|
| **多语言国际化 (i18n)** | 聊天窗口和选项页的英文/中文双语界面切换 | 🟢 低 |
| **代码镜头 (Code Lens)** | 编辑器内嵌的 AI 操作入口（解释代码、生成测试、查找引用） | 🟢 低 |
| **更多内置 Skills** | 如 `debug-analyzer`、`api-designer`、`sql-optimizer` 等专业工作流 | 🟡 中 |
| **会话导出** | 支持导出对话为 Markdown / PDF，便于分享和归档 | 🟢 低 |

> 💡 欢迎通过 [Issues](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues) 提出功能建议或贡献代码！

---

## 致谢

- [DeepSeek](https://www.deepseek.com/) — 强大的 AI 模型支持
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — 出色的 OCR 引擎（可通过 MCP 协议接入）
- [Markdig](https://github.com/xoofx/markdig) — 快速的 Markdown 解析器

---

## 许可证

本项目基于 [MIT License](LICENSE) 开源。

---

<div align="center">

**⭐ 如果这个项目对你有帮助，请给一个 Star！**

[![GitHub stars](https://img.shields.io/github/stars/zmy15/DeepSeek-v4-for-VisualStudio?style=social)](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio)