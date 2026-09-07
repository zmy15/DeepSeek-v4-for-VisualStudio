<div align="center">

> ⚠️ **Beta Stage** — Back up your project before use.

# DeepSeek v4 for Visual Studio

**An AI coding assistant deeply integrated with Visual Studio 2022+**

[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![VS](https://img.shields.io/badge/VS-2022%2017.14%2B-purple)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%2F%20ARM64-lightgrey)]()
[![Version](https://img.shields.io/badge/version-1.2.2-blue)]()

[简体中文](README.zh-CN.md)

</div>

## Overview

DeepSeek v4 for Visual Studio brings chat, code editing, project tools, and multimodal AI directly into Visual Studio. It supports streaming responses, long-context reasoning, agent workflows, reusable skills, MCP tool integration, and image/file understanding.

## Highlights

- **Chat and reasoning**: streaming chat, Deep Reasoning, Pro/Flash models, and long-context summarization.
- **Agents**: Ask, Explore, Plan, Edit, and Build modes with automatic handoff.
- **Code editing**: inline AI edit, patch-based editing, diff preview, and Ghost Text completion.
- **Project tools**: solution/file exploration, terminal commands, Git operations, and MCP external tools.
- **Multimodal input**: image understanding, screenshots, PDF reading, and OCR.
- **Safety**: terminal approval modes and persistent user/session/repository memory.

## Installation

Download the latest `.vsix` from [Releases](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases), close Visual Studio, and install it.

- **Full package**: Windows x64.
- **No-Local-OCR package**: Windows x64 and ARM64, with local OCR removed.

To build from source, use Visual Studio 2022 17.14+ with the `.NET Framework 4.7.2 SDK` and the `Visual Studio extension development` workload:

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
```

Open `.slnx`, build, then press `F5` to launch the experimental instance.

## Quick Start

1. Get an API key from [platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys).
2. Open `Tools → Options → DeepSeek Chat`, enter the API key, and select a model.
3. Open `View → Other Windows → DeepSeek Chat`.
4. Ask a question, select code and press `Ctrl+I` for inline editing, or use an agent command.

| Shortcut | Action |
|----------|--------|
| `Ctrl+Shift+D` | Open or focus the chat window |
| `Ctrl+I` | Inline AI edit |
| `Enter` | Send chat message |
| `Ctrl+Enter` | Insert a newline |

## Support

- **Usage questions and discussions**: [Discussions](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/discussions)
- **Bug reports and feature requests**: [Issues](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues)

## License

[MIT](LICENSE) © 2024 zmy15
