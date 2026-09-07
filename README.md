<div align="center">

> **Beta Stage** — Back up your project before use.

# DeepSeek v4 for Visual Studio

**An AI coding assistant for Visual Studio 2022 and 2026**

[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![Visual Studio](https://img.shields.io/badge/Visual%20Studio-2022%20%7C%202026-purple)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64%20%2F%20ARM64-lightgrey)]()
[![Version](https://img.shields.io/badge/version-1.2.2-blue)]()

[简体中文](README.zh-CN.md)

</div>

## Overview

DeepSeek v4 for Visual Studio brings AI chat, code editing, solution-aware tools, terminal execution, and multimodal understanding directly into the IDE. It is designed for Visual Studio 2022 (17.14 or later) and Visual Studio 2026.

The extension combines a native-grade WebView2 chat experience with five cooperating agents, reusable Skills, MCP tool servers, Ghost Text completion, and persistent project memory.

## Project Highlights

| Highlight | Why it matters |
|---|---|
| **Five-agent workflow** | Ask, Explore, Plan, Edit, and Build collaborate through the Handoff protocol instead of forcing manual mode switching. |
| **IDE-native interaction** | Chat, inline editing, diff preview, build diagnostics, terminal commands, and Git operations stay inside Visual Studio. |
| **Reliable code changes** | Patch editing, four-tier matching, Healing repair, and per-hunk diff confirmation reduce accidental edits. |
| **Long-context reasoning** | A 900K token budget, Deep Reasoning, and automatic LLM summarization support large solutions. |
| **Multimodal understanding** | Images, screenshots, PDFs, webpage content, and OCR results can be reasoned about directly. |
| **Extensible tools** | Markdown-based Skills and MCP servers add custom workflows and external tools without changing the extension core. |
| **Safety controls** | Terminal approval modes and user/session/repository memory keep automation useful and controllable. |

## Core Features

- **Chat and reasoning**: streaming responses, Deep Reasoning, Pro/Flash models, resumable streaming, and Prefix Cache.
- **Code editing**: inline AI edit, exact/multi replacement, `apply_patch`, file creation, diff preview, and Ghost Text completion.
- **Project tools**: solution exploration, file parsing, terminal commands, Git operations, and build verification.
- **Skills system**: reusable workflows defined in `SKILL.md`, triggered by `/skillname` or semantic matching.
- **MCP integration**: connect HTTP and stdio tool servers and classify their tools for agents.
- **Multimodal AI**: image understanding, window screenshots, page-by-page PDF reading, web image reading, and OCR.
- **Search**: Baidu Qianfan and DuckDuckGo with automatic fallback.
- **Memory**: persistent user, session, and repository notes injected into new conversations.
- **Internationalization**: automatic Chinese/English UI switching with custom translation overrides.

## Requirements

| Component | Requirement |
|---|---|
| Visual Studio | **2022 (17.14+)** or **2026** |
| Operating system | Windows 10/11 |
| Runtime | .NET Framework 4.7.2 |
| Architectures | x64; ARM64 for the No-Local-OCR package |

## Installation

Download the latest `.vsix` from [Releases](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases), close Visual Studio, and install it.

- **Full package**: x64 only, includes local OCR.
- **No-Local-OCR package**: x64 and ARM64; local OCR is removed and other features remain available.

To build from source, install the **.NET Framework 4.7.2 SDK** and the **Visual Studio extension development** workload:

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
```

Open `.slnx` in Visual Studio, build the solution, and press `F5` to launch the experimental instance.

## Quick Start

1. Get an API key from [platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys).
2. Open `Tools > Options > DeepSeek Chat`, enter the API key, and select a model.
3. Open `View > Other Windows > DeepSeek Chat`.
4. Ask a question, select code and press `Ctrl+I` for inline editing, or dispatch an agent command.

Recommended starting settings:

| Setting | Value |
|---|---|
| Model | `deepseek-v4-pro` |
| Deep Reasoning | Enabled, effort `max` |
| Token budget | `900000` |
| Vision model | `deepseek-v4-flash-vision-exp` for image/PDF understanding |

| Shortcut | Action |
|---|---|
| `Ctrl+Shift+D` | Open or focus the chat window |
| `Ctrl+I` | Inline AI edit |
| `Enter` | Send chat message |
| `Ctrl+Enter` | Insert a newline |

## Agents and Skills

| Agent | Responsibility |
|---|---|
| **Ask** | Answer questions, explain code, and summarize results. |
| **Explore** | Search the repository and analyze project structure. |
| **Plan** | Decompose tasks, design changes, and coordinate subagents. |
| **Edit** | Apply code and file changes with confirmation and review. |
| **Build** | Run builds, inspect diagnostics, and repair errors. |

Use `@ask`, `@plan`, `@edit`, or `@build` to select an agent explicitly, or let Handoff route the task automatically. Skills are loaded from project-level directories, user-level directories, and built-in skills.

## Roadmap / TODO

| Item | Description | Priority |
|---|---|---|
| **RAG code retrieval** | Local vector storage, file indexing, BM25/vector hybrid search, and solution symbol indexing. | High |
| **Code knowledge graph** | AST-based relationships and semantic navigation across classes and methods. | Medium |
| **Test generation skill** | Generate xUnit tests from the built-in TDD workflow. | High |
| **GitHub integration** | PR descriptions, review assistance, and issue assignment. | Medium |
| **More built-in skills** | Debug analyzer, SQL optimizer, API design, and other workflows. | Medium |
| **Local model support** | Ollama and LM Studio offline inference. | Low |
| **Session export** | Export conversations as Markdown, PDF, or HTML. | Low |
| **More UI languages** | Japanese, Korean, and additional locales. | Low |

## Support

- **Usage questions and discussions**: [Discussions](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/discussions)
- **Bug reports and feature requests**: [Issues](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues)

## Contributors

<a href="https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=zmy15/DeepSeek-v4-for-VisualStudio" />
</a>

## Star History

<a href="https://www.star-history.com/?type=date&repos=zmy15%2FDeepSeek-v4-for-VisualStudio">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&theme=dark&legend=top-left" />
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&theme=light&legend=top-left" />
    <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&legend=top-left" />
  </picture>
</a>

## License

[MIT](LICENSE) © 2026 zmy15
