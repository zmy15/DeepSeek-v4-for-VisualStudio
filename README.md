<div align="center">

> ⚠️ **测试阶段** — 使用前请备份项目（Git 提交或手动备份）。

# DeepSeek v4 for Visual Studio

**将 DeepSeek V4 深度集成到 Visual Studio 2022+ 的 AI 编程助手**

[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![VS](https://img.shields.io/badge/VS-2022%2017.14%2B-purple)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%2F%20ARM64-lightgrey)]()
[![Version](https://img.shields.io/badge/version-1.2.2-blue)]()

[English](README_EN.md)

</div>

---

## 📖 简介

**DeepSeek v4 for Visual Studio**是一款将 DeepSeek V4 大语言模型深度嵌入 Visual Studio 2022+ 的 AI 编程助手扩展。它通过 **WebView2** 提供原生级聊天体验，并支持多智能体协作、代码编辑、行内补全、终端命令执行、联网搜索、OCR 图像识别、多模态图像理解、文件解析以及 MCP 外部工具集成。

**核心架构**基于 .NET Framework 4.7.2 + WPF + WebView2，使用 Visual Studio SDK 17.14 构建，兼容 Visual Studio 2022 17.14 及以上版本。

---

## ✨ 核心特性

| 特性 | 说明 |
|------|------|
| 🧠 **DeepSeek V4** | 流式对话、深度思考 (Reasoning)、Pro/Flash 双模型、断点续传、Prefix Cache |
| 👁️ **多模态视觉** | `deepseek-v4-flash-vision-exp` 视觉模型：图片理解、窗口截图分析、PDF 逐页直读、网页图片直读 |
| 🤖 **多智能体** | Ask / Explore / Plan / Edit / Build 五种 Agent，Handoff 自动协作，实时计划监控，VS 构建集成 |
| 📐 **Skills 技能** | Markdown (SKILL.md) 定义可复用 AI 工作流，`/` 斜杠命令触发 |
| 🔧 **MCP 协议** | 连接外部工具服务器（HTTP + stdio），自动分类注入对应 Agent |
| 📝 **代码编辑** | replace / apply_patch / create_file 等 5 种编辑工具，Levenshtein 四级匹配 + Healing 修复，Diff 预览，Ghost Text 行内补全 |
| 📚 **1M 上下文** | 900K Token 预算，使用率达 85% 自动 LLM 摘要压缩，文件不截断 |
| 🌐 **联网搜索** | 百度千帆 + DuckDuckGo 双引擎，额度耗尽自动切换 |
| 🖼️ **图像 OCR** | PaddleOCR-Sharp 本地 / Windows 内置 / MCP 远程三引擎 |
| 📄 **文件解析** | 拖拽或粘贴 50+ 格式（代码/文档/PDF/Office/图片），基于 NPOI + PdfPig |
| 🛡️ **终端审批** | 命令执行前弹窗确认（BlockAll / AllowAll / SmartBlock 三模式） |
| 🧠 **AI 记忆系统** | 三层持久化记忆（用户/会话/仓库），AI 自主管理笔记，新对话自动注入 |
| 🔀 **Git 集成** | status / diff / log / add / commit / branch / checkout / pull / stash / reset 等 12 种操作 |
| 🌍 **国际化** | 中英文自动切换，支持 `zh-CN.user.json` 自定义翻译覆盖 |


---

## 📦 安装

### 方式一：下载 VSIX（推荐）

从 [Releases](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases) 下载最新 `.vsix`，关闭 Visual Studio 后双击安装。

> **架构支持**：
> - **完整版（含本地 OCR）**：仅支持 **x64**（PaddleOCR / OpenCvSharp 原生库暂无 ARM64 版本）。
> - **No-Local-OCR 版**：同时支持 **x64** 与 **ARM64**（含 ARM64 版 Visual Studio 2022 17.14+ / 2026）。
>
> ARM64 设备请下载 **No-Local-OCR** 版本（不含本地 OCR，其余功能完整）。

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
| Windows | 10/11 x64 / ARM64 |

---

## 🚀 快速开始

1. **获取 API Key**：[platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys)
2. **打开设置**：`工具 → 选项 → DeepSeek Chat` → 粘贴 API Key → 选择模型
3. **打开聊天窗口**：`视图 → 其他窗口 → DeepSeek Chat`（或点击工具栏按钮）

### 快捷键

| 快捷键 | 作用 |
|--------|------|
| `Ctrl+Shift+D` | 打开 / 聚焦 DeepSeek Chat 窗口（全局） |
| `Ctrl+I` | 编辑器内 Inline AI Edit（选区 → 指令 → 预览） |
| `Enter` | 发送消息（聊天输入框内） |
| `Ctrl+Enter` | 输入框内换行 |

> 如与现有方案冲突，可在 `工具 → 选项 → 环境 → 键盘` 中搜索 "DeepSeek" 自行调整。

| 推荐设置 | 值 | 说明 |
|----------|-----|------|
| 模型 | `deepseek-v4-pro` | 旗舰模型，推理能力最强 |
| 深度思考 | 开启，Reasoning Effort = `max` | 复杂任务效果更佳 |
| 联网搜索 | 百度千帆 | 每月 1500 次免费额度 |
| OCR 引擎 | PaddleOCR-Sharp | 本地离线识别，无网络依赖 |
| Token Budget | 900000 | 充分利用 1M 上下文窗口 |

> 💡 需要「看图」时（图片理解 / 截图分析 / PDF 直读），把模型切换到 `deepseek-v4-flash-vision-exp`，详见下文「多模态图像理解」。

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

### 五种编辑工具

| 工具 | 适用场景 |
|------|---------|
| `replace_string_in_file` | 单处精确替换（含上下文定位） |
| `multi_replace_string_in_file` | 同文件多处同时修改 |
| `apply_patch` | 复杂跨文件修改（`*** Begin Patch` 格式） |
| `create_file` | 创建新文件 |
| `delete_file` | 删除文件 |
| `create_directory` | 创建目录结构 |

> 💡 `insert_edit` 功能已合并到 `apply_patch` 中，通过 `@@` 上下文标记定位插入位置。

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

> 💡 OCR 只负责**提取文字**；若要**理解**图片 / 截图 / PDF 的语义内容（结合布局、图表、上下文），请切换模型到 `deepseek-v4-flash-vision-exp`（见下方「多模态图像理解」）。

### 多模态图像理解（deepseek-v4-flash-vision-exp）

将模型切换为 `deepseek-v4-flash-vision-exp` 后，AI 可以直接「看懂」图像，而不只是提取文字：

| 能力 | 说明 |
|------|------|
| 🖼️ **图片理解** | 拖拽 / 粘贴图片附件时直传视觉模型，识别内容、描述画面、解答图片相关问题 |
| 📸 **窗口截图分析** | `capture_window` 工具截取指定窗口，AI 直接查看截图进行界面 / 报错诊断 |
| 📄 **PDF 逐页直读** | PDF 自动逐页渲染为图片直传（最多 20 页），排版 / 表格 / 公式也能看懂，无需先 OCR |
| 🌐 **网页图片直读** | 联网搜索 / 抓取网页时，页面图片 URL 直接喂给视觉模型理解 |

> ⚠️ 视觉模型不支持行内 FIM 补全，启用时会自动回退到 `deepseek-v4-flash` 完成补全。

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

> ✅ 473+ 测试全部通过，5 个 Agent 100% 覆盖，26 个测试文件涵盖 Models / Services / Integration

---

## 🗺️ 路线图

| 计划项 | 说明 | 优先级 |
|--------|------|--------|
| **RAG 代码检索增强** | 本地向量库（SQLite + 嵌入模型）、文件自动索引、BM25+向量混合检索、解决方案级符号索引 | 🔴 高 |
| **项目代码知识图谱** | 基于代码 AST 的符号关系图，类/方法/接口依赖可视化，语义级代码导航与理解 | 🟡 中 |
| **测试生成 Skill** | 基于 `tdd` 技能自动生成 xUnit 测试 | 🔴 高 |
| **GitHub PR/Issue 深度集成** | PR 描述生成、Review 辅助、Issue 自动分派 | 🟡 中 |
| **更多内置 Skills** | `debug-analyzer`、`sql-optimizer`、`api-designer` 等开箱即用技能 | 🟡 中 |
| **本地模型支持** | Ollama / LM Studio 离线推理 | 🟢 低 |
| **会话导出** | 对话导出为 Markdown / PDF / HTML | 🟢 低 |
| **多语言扩展** | 日语、韩语等更多 UI 语言支持 | 🟢 低 |

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
