<div align="center">

> ⚠️ **测试阶段** — 使用前请备份项目（Git 提交或手动备份）。

# DeepSeek v4 for Visual Studio

**将 DeepSeek V4 深度集成到 Visual Studio 2022+ 的 AI 编程助手**

[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![VS](https://img.shields.io/badge/VS-2022%2017.14%2B-purple)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)]()
[![Version](https://img.shields.io/badge/version-1.1.10-blue)]()

[English](README_EN.md)

</div>

---

## 📖 简介

**DeepSeek v4 for Visual Studio**是一款将 DeepSeek V4 大语言模型深度嵌入 Visual Studio 2022+ 的 AI 编程助手扩展。它通过 **WebView2** 提供原生级聊天体验，并支持多智能体协作、代码编辑、行内补全、终端命令执行、联网搜索、OCR 图像识别、文件解析以及 MCP 外部工具集成。

**核心架构**基于 .NET Framework 4.7.2 + WPF + WebView2，使用 Visual Studio SDK 17.14 构建，兼容 Visual Studio 2022 17.14 及以上版本。

---

## ✨ 核心特性

| 特性 | 说明 |
|------|------|
| 🧠 **DeepSeek V4** | 流式对话、深度思考 (Reasoning)、Pro/Flash 双模型切换 |
| 🤖 **多智能体** | Ask / Explore / Plan / Edit / Build 五种 Agent，Handoff 自动协作 |
| 📐 **Skills 技能** | 用 Markdown (SKILL.md) 定义可复用 AI 工作流，`/` 斜杠命令触发 |
| 🔧 **MCP 协议** | 连接外部工具服务器（HTTP + stdio），Function Calling 自动调用与分类注入 |
| 📝 **三种编辑方法** | apply_patch / insert_edit / create_file，Levenshtein 四级匹配 + Healing 自动修复 |
| 📚 **1M 上下文** | 900K Token 预算，智能压缩，文件不截断 |
| 📊 **代码差异预览** | 编辑器内红绿 Diff 标记（EditorDiffMarkerService），逐块确认或一键全部应用 |
| 💡 **Ghost Text 补全** | 行内灰色预测文本，上下文感知，Tab 接受 |
| 🌐 **联网搜索** | 百度千帆 + DuckDuckGo 双引擎，额度耗尽自动切换 |
| 🖼️ **图像 OCR** | PaddleOCR-Sharp 本地 / Windows 内置 / MCP 远程三引擎 |
| 📄 **文件解析** | 拖拽或粘贴 50+ 格式（代码/文档/PDF/Office/图片），基于 NPOI + PdfPig |
| 🛡️ **终端审批** | 命令执行前弹窗确认（BlockAll / AllowAll / SmartBlock 三模式） |
| 🌍 **国际化** | 中英文自动切换，支持 `zh-CN.user.json` 自定义翻译覆盖 |
| 🔄 **断点续传** | 网络中断自动恢复，已接收内容无缝衔接 |
| 🧠 **AI 记忆系统** | 三层持久化记忆（用户/会话/仓库），AI 自主管理笔记，新对话自动注入 |
| ⚡ **Prefix Cache** | 利用 DeepSeek Prefix Caching 优化重复上下文的 Token 消耗 |

---

## 📦 安装

### 方式一：下载 VSIX（推荐）

从 [Releases](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases) 下载最新 `.vsix`，关闭 Visual Studio 后双击安装。

### 方式二：从源码编译

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
# 用 VS 2022 打开 .slnx → 编译 → F5 启动实验实例
```

| 编译依赖 | 版本要求 |
|----------|----------|
| Visual Studio | 2022 (17.14+) |
| .NET Framework | 4.7.2 SDK |
| VS SDK | VS Installer → "Visual Studio 扩展开发" |
| Windows | 10/11 x64 |

---

## 🚀 快速开始

1. **获取 API Key**：[platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys)
2. **打开设置**：`工具 → 选项 → DeepSeek Chat` → 粘贴 API Key → 选择模型
3. **打开聊天窗口**：`视图 → 其他窗口 → DeepSeek Chat`（或点击工具栏按钮）

| 推荐设置 | 值 | 说明 |
|----------|-----|------|
| 模型 | `deepseek-v4-pro` | 旗舰模型，推理能力最强 |
| 深度思考 | 开启，Reasoning Effort = `max` | 复杂任务效果更佳 |
| 联网搜索 | 百度千帆 | 每月 1500 次免费额度 |
| OCR 引擎 | PaddleOCR-Sharp | 本地离线识别，无网络依赖 |
| Token Budget | 900000 | 充分利用 1M 上下文窗口 |

---

## 🤖 多智能体协作

五种 Agent 通过 **Handoff 协议**自动分派任务，无需手动切换：

```
用户提问 → Ask (分析)
              ↓
          Plan (规划，多文件/复杂任务)
              ↓
          Edit (执行代码修改，逐步骤确认)
              ↓
          Build (编译验证 + 自动修复，最多 3 轮)
              ↓
          Ask (总结汇报)
```

| Agent | 类 | 职责 | 权限 |
|-------|-----|------|------|
| **Ask** | `AskAgent` | 问答、代码解释、技术讨论 | 只读 |
| **Explore** | `ExploreAgent` | 代码库搜索、文件探索、结构分析 | 只读（Plan/Ask 的子 Agent） |
| **Plan** | `PlanAgent` | 任务分解、方案设计、生成 plan.md | 只读 + 子 Agent 调用 |
| **Edit** | `EditAgent` | 代码写入/删除、文件操作、终端执行 | 读写 |
| **Build** | `BuildAgent` | 编译诊断、错误自动修复 | 读写 + 编译 |

**路由机制**：
- AI 自动根据用户意图分类（Plan→Edit→Build→Ask）
- 用户可显式指定：`@ask 问题`、`@plan 任务`、`@edit 修改`、`@build 编译`
- Explore 不独立路由，由 Plan/Ask Agent 通过 `runSubagent` 内部调用

**Handoff 协议**（`AgentTypes.cs` — `HandoffRequest`）：
- 源 Agent 声明移交意图（目标 Agent、原因、任务描述）
- 支持 `ChainBack` — 目标完成后再链回源 Agent
- 支持 `ForwardedMessages` — 移交时复用消息列表以最大化 Prefix Cache 命中

---

## 📐 Skills 技能系统

用 Markdown (`SKILL.md`) 定义可复用的 AI 工作流。技能文件格式：

```yaml
---
name: code-review
description: '审查代码质量、安全、性能。触发场景：代码审查、检查代码质量、找 bug、安全审计、PR review'
argument-hint: '[file or code]'
user-invocable: true
disable-model-invocation: false
---
# Code Review

## 使用时机
- 用户请求代码审查或检查
- PR 提交前的自审
- 寻找潜在 bug、安全漏洞或性能问题

## 执行步骤
1. 读取用户提供的代码或当前打开的文件
2. 分析：正确性、安全性、性能、可维护性
3. 按严重程度排序发现问题，提供修复建议
```

**技能来源**（优先级从高到低）：
1. **项目级**：`.github/skills/`、`.agents/skills/`、`.claude/skills/`
2. **用户级**：`%USERPROFILE%\.copilot\skills\`、`%USERPROFILE%\.agents\skills\`、`%USERPROFILE%\.claude\skills\`
3. **内置级**：随扩展发布的 `BuiltInSkills/`

**三种触发方式**：
- 用户显式调用：输入 `/skillname`
- AI 语义自动匹配：问题语义匹配技能描述时自动加载
- 上下文推断：对话上下文积累到需要特定技能时主动建议

---

## 🧠 AI 记忆系统

AI 通过 `memory` 工具管理三层持久化记忆：

| 作用域 | 路径前缀 | 存储位置 | 生命周期 |
|--------|---------|---------|---------|
| **用户记忆** | `/memories/` | `%LocalAppData%\DeepSeekVS\memories\user\` | 跨所有解决方案持久化 |
| **会话记忆** | `/memories/session/` | `%LocalAppData%\DeepSeekVS\memories\session\` | 当前对话内有效 |
| **仓库记忆** | `/memories/repo/` | `%LocalAppData%\DeepSeekVS\memories\repo\` | 当前解决方案内有效 |

**支持的操作**：`view`、`create`、`str_replace`、`insert`、`delete`、`rename`

**自动注入**：新对话开始时，用户记忆和仓库记忆自动注入 System Prompt，所有 Agent 均可使用。

---

## 🔧 MCP 协议集成

支持通过 **Model Context Protocol** 连接外部工具服务器：
- **传输方式**：HTTP + stdio 双模式
- **工具分类**：自动根据前缀判断读写属性，注入对应 Agent
- **管理界面**：`McpConfigDialog.xaml` — 可视化配置

---

## 📝 代码编辑能力

### 三种编辑方法

| 方法 | 工具名 | 适用场景 |
|------|--------|---------|
| **精确替换** | `replace_string_in_file` | 单处修改 |
| **多处替换** | `multi_replace_string_in_file` | 同文件多处修改 |
| **补丁应用** | `apply_patch` | 复杂跨文件修改 |

### 四级匹配 + Healing 自动修复

1. **精确匹配** → 2. **行级匹配** → 3. **Levenshtein 模糊匹配** → 4. **Healing 修复**

### Diff 预览

修改前编辑器内显示红绿 Diff 标记，通过 `DiffViewerWindow` 逐条确认。

---

## 💡 Ghost Text 行内补全

基于 DeepSeek FIM API（`api.deepseek.com/beta/completions`）：
- 编辑器内灰色预测文本（`GhostTextTagger`）
- Tab 接受，Esc 取消
- `InlinePredictionManager` 管理预测生命周期

---

## 🌐 联网搜索与 OCR

### 搜索引擎

| 引擎 | 提供方 | 免费额度 | 特点 |
|------|--------|---------|------|
| 百度千帆 | Baidu | 1500 次/月 | 中文搜索质量高 |
| DuckDuckGo | 免费 | 无限制 | 隐私友好 |

### OCR 三引擎

| 引擎 | 实现 | 依赖 |
|------|------|------|
| PaddleOCR-Sharp | 本地离线 | Sdcb.PaddleOCR + OpenCvSharp |
| Windows 内置 | 系统 API | Windows 10/11 |
| MCP 远程 | MCP 协议 | 外部 OCR 服务 |

---

## 🛡️ 终端审批安全

三种审批模式：**BlockAll**（全拦截）/ **AllowAll**（全放行）/ **SmartBlock**（智能拦截，推荐）

---

## 🌍 国际化

- 中英文自动切换，基于 `LocalizationService` + `I18nMarkupExtension`
- 自定义翻译：复制 `zh-CN.user.json.example` 为 `zh-CN.user.json`

---

## 🧪 测试

| 测试类型 | 框架 | 位置 |
|---------|------|------|
| 单元测试 | xUnit 2.9.3 + Moq 4.20.72 + FluentAssertions 6.12.2 | `Tests/Unit/` |
| 集成测试 | 同上 | `Tests/Integration/` |
| 代码覆盖率 | Coverlet 6.0.4 → Cobertura | CI 自动上传 |

---

## 🗺️ 路线图

| 计划项 | 说明 | 优先级 |
|--------|------|--------|
| **RAG 代码检索增强** | 本地向量库（SQLite + 嵌入模型）、文件自动索引、BM25+向量混合检索、解决方案级跨项目符号索引 | 🔴 高 |
| **测试生成 Skill** | 基于 `tdd` 技能自动生成 xUnit 测试 | 🟡 中 |
| **Git 增强集成** | PR 描述生成、Commit Message 自动撰写 | 🟡 中 |
| **本地模型支持** | Ollama / LM Studio 离线推理 | 🟢 低 |
| **会话导出** | 对话导出为 Markdown / PDF / HTML | 🟢 低 |
| **更多内置 Skills** | `debug-analyzer`、`sql-optimizer`、`api-designer` 等开箱即用技能 | 🟡 中 |
| **Token 用量面板** | API 调用次数、Token 消耗、缓存命中率可视化统计 | 🟢 低 |

> 💡 欢迎通过 [Issues](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues) 提出建议或贡献代码！

---

## 👥 贡献者

<a href="https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=zmy15/DeepSeek-v4-for-VisualStudio" />
</a>

---

## 📈 Star 趋势

<a href="https://www.star-history.com/?type=date&repos=zmy15%2FDeepSeek-v4-for-VisualStudio">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&theme=dark&legend=top-left" />
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&legend=top-left" />
    <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&legend=top-left" />
  </picture>
</a>

---

## 📄 开源协议

[MIT License](LICENSE) © 2024 zmy15
