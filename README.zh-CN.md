<div align="center">

> ⚠️ **测试阶段** — 使用前请备份项目。

# DeepSeek v4 for Visual Studio

**深度集成到 Visual Studio 2022+ 的 AI 编程助手**

[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![VS](https://img.shields.io/badge/VS-2022%2017.14%2B-purple)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%2F%20ARM64-lightgrey)]()
[![Version](https://img.shields.io/badge/version-1.2.2-blue)]()

[English](README.md)

</div>

## 简介

DeepSeek v4 for Visual Studio 将聊天、代码编辑、项目工具和多模态 AI 直接带入 Visual Studio。它支持流式响应、长上下文推理、智能体工作流、可复用 Skills、MCP 工具集成以及图片和文件理解。

## 核心能力

- **聊天与推理**：流式对话、Deep Reasoning、Pro/Flash 双模型、长上下文摘要。
- **智能体**：Ask、Explore、Plan、Edit、Build 五种模式，支持自动协作。
- **代码编辑**：Inline AI Edit、补丁式修改、Diff 预览、Ghost Text 补全。
- **项目工具**：解决方案和文件浏览、终端命令、Git 操作、MCP 外部工具。
- **多模态输入**：图片理解、窗口截图、PDF 读取和 OCR。
- **安全与记忆**：终端审批模式，以及用户、会话、仓库三层持久记忆。

## 安装

从 [Releases](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases) 下载最新 `.vsix`，关闭 Visual Studio 后安装。

- **完整版**：Windows x64。
- **No-Local-OCR 版**：Windows x64 和 ARM64，不包含本地 OCR。

从源码编译需要 Visual Studio 2022 17.14+，并安装 `.NET Framework 4.7.2 SDK` 和 `Visual Studio 扩展开发` 工作负载：

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
```

用 Visual Studio 打开 `.slnx`，编译后按 `F5` 启动实验实例。

## 快速开始

1. 在 [platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys) 获取 API Key。
2. 打开 `工具 → 选项 → DeepSeek Chat`，填入 API Key 并选择模型。
3. 打开 `视图 → 其他窗口 → DeepSeek Chat`。
4. 直接提问；选中代码后按 `Ctrl+I` 进行内联编辑，或使用智能体命令。

| 快捷键 | 作用 |
|--------|------|
| `Ctrl+Shift+D` | 打开或聚焦聊天窗口 |
| `Ctrl+I` | 内联 AI 编辑 |
| `Enter` | 发送消息 |
| `Ctrl+Enter` | 插入换行 |

## 支持

- **使用方法和讨论**：[Discussions](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/discussions)
- **Bug 和功能建议**：[Issues](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues)

## 许可

[MIT](LICENSE) © 2024 zmy15
