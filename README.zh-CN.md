<div align="center">

> **测试阶段** — 使用前请备份项目。

# DeepSeek v4 for Visual Studio

**面向 Visual Studio 2022 和 2026 的 AI 编程助手**

[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![Visual Studio](https://img.shields.io/badge/Visual%20Studio-2022%20%7C%202026-purple)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%2F%20ARM64-lightgrey)]()
[![Version](https://img.shields.io/badge/version-1.2.2-blue)]()

[English](README.md)

</div>

## 简介

DeepSeek v4 for Visual Studio 将 AI 聊天、代码编辑、解决方案工具、终端执行和多模态理解直接带入 IDE。扩展支持 **Visual Studio 2022（17.14 及以上）** 和 **Visual Studio 2026**。

它通过 WebView2 提供原生级聊天体验，并结合五个协作 Agent、可复用 Skills、MCP 工具服务器、Ghost Text 补全和持久化项目记忆。

## 项目亮点

| 亮点 | 价值 |
|---|---|
| **五 Agent 工作流** | Ask、Explore、Plan、Edit、Build 通过 Handoff 自动协作，不需要频繁手动切换模式。 |
| **IDE 原生交互** | 聊天、内联编辑、Diff 预览、构建诊断、终端命令和 Git 操作都在 Visual Studio 内完成。 |
| **可靠的代码修改** | 补丁式编辑、四级匹配、Healing 修复和逐块 Diff 确认，降低误改风险。 |
| **长上下文推理** | 900K Token 预算、Deep Reasoning 和自动摘要，适合大型解决方案。 |
| **多模态理解** | 可以直接理解图片、截图、PDF、网页内容和 OCR 结果。 |
| **可扩展工具** | Markdown Skills 和 MCP 服务器可以在不修改扩展核心的情况下增加工作流和外部工具。 |
| **安全可控** | 终端审批模式和用户、会话、仓库三层记忆，让自动化既可用也可控。 |

## 核心功能

- **聊天与推理**：流式响应、Deep Reasoning、Pro/Flash 双模型、断点续传和 Prefix Cache。
- **代码编辑**：Inline AI Edit、精确/多处替换、`apply_patch`、创建文件、Diff 预览和 Ghost Text 补全。
- **项目工具**：解决方案浏览、文件解析、终端命令、Git 操作和构建验证。
- **Skills 系统**：通过 `SKILL.md` 定义工作流，可用 `/skillname` 或语义匹配触发。
- **MCP 集成**：连接 HTTP 和 stdio 工具服务器，并自动分类注入到 Agent。
- **多模态 AI**：图片理解、窗口截图、PDF 逐页读取、网页图片读取和 OCR。
- **联网搜索**：百度千帆和 DuckDuckGo，额度耗尽自动切换。
- **记忆系统**：持久化的用户、会话、仓库记忆，新对话自动注入。
- **国际化**：中英文界面自动切换，支持自定义翻译覆盖。

## 环境要求

| 组件 | 要求 |
|---|---|
| Visual Studio | **2022（17.14+）** 或 **2026** |
| 操作系统 | Windows 10/11 |
| 运行时 | .NET Framework 4.7.2 |
| 架构 | x64；No-Local-OCR 版支持 ARM64 |

## 安装

从 [Releases](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases) 下载最新 `.vsix`，关闭 Visual Studio 后安装。

- **完整版**：仅 x64，包含本地 OCR。
- **No-Local-OCR 版**：x64 和 ARM64；不包含本地 OCR，其余功能可用。

从源码编译时，请安装 **.NET Framework 4.7.2 SDK** 和 **Visual Studio 扩展开发** 工作负载：

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
```

用 Visual Studio 打开 `.slnx`，编译解决方案后按 `F5` 启动实验实例。

## 快速开始

1. 在 [platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys) 获取 API Key。
2. 打开 `工具 > 选项 > DeepSeek Chat`，填入 API Key 并选择模型。
3. 打开 `视图 > 其他窗口 > DeepSeek Chat`。
4. 直接提问；选中代码后按 `Ctrl+I` 进行内联编辑，或使用 Agent 命令。

推荐初始设置：

| 设置 | 值 |
|---|---|
| 模型 | `deepseek-v4-pro` |
| Deep Reasoning | 开启，Effort 为 `max` |
| Token 预算 | `900000` |
| 视觉模型 | 需要理解图片或 PDF 时使用 `deepseek-v4-flash-vision-exp` |

| 快捷键 | 作用 |
|---|---|
| `Ctrl+Shift+D` | 打开或聚焦聊天窗口 |
| `Ctrl+I` | 内联 AI 编辑 |
| `Enter` | 发送消息 |
| `Ctrl+Enter` | 插入换行 |

## Agent 与 Skills

| Agent | 职责 |
|---|---|
| **Ask** | 回答问题、解释代码、总结结果。 |
| **Explore** | 搜索仓库和分析项目结构。 |
| **Plan** | 拆解任务、设计变更并协调子 Agent。 |
| **Edit** | 应用代码和文件修改，支持确认与审查。 |
| **Build** | 执行构建、分析诊断并修复错误。 |

可以使用 `@ask`、`@plan`、`@edit`、`@build` 显式选择 Agent，也可以让 Handoff 自动路由。Skills 会从项目级目录、用户级目录和内置目录加载。

## TODO / 路线图

| 计划 | 说明 | 优先级 |
|---|---|---|
| **RAG 代码检索** | 本地向量存储、文件索引、BM25/向量混合搜索和解决方案符号索引。 | 高 |
| **代码知识图谱** | 基于 AST 的类和方法关系图，提供语义级导航。 | 中 |
| **测试生成 Skill** | 基于内置 TDD 工作流生成 xUnit 测试。 | 高 |
| **GitHub 集成** | PR 描述生成、Review 辅助和 Issue 分派。 | 中 |
| **更多内置 Skills** | Debug 分析、SQL 优化、API 设计等工作流。 | 中 |
| **本地模型支持** | Ollama 和 LM Studio 离线推理。 | 低 |
| **会话导出** | 导出为 Markdown、PDF 或 HTML。 | 低 |
| **更多界面语言** | 日语、韩语等语言支持。 | 低 |

## 支持

- **使用方法和讨论**：[Discussions](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/discussions)
- **Bug 和功能建议**：[Issues](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues)

## 贡献者

<a href="https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=zmy15/DeepSeek-v4-for-VisualStudio" />
</a>

## Star 趋势

<a href="https://www.star-history.com/?type=date&repos=zmy15%2FDeepSeek-v4-for-VisualStudio">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&theme=dark&legend=top-left" />
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&theme=light&legend=top-left" />
    <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&legend=top-left" />
  </picture>
</a>

## 开源协议

[MIT](LICENSE) © 2026 zmy15
