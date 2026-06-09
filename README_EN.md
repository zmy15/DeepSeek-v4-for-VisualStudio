<div align="center">

> ⚠️ **Beta Stage** — Please back up your project before use (Git commit or manual backup).

# DeepSeek v4 for Visual Studio

**AI-powered coding assistant deeply integrating DeepSeek V4 into Visual Studio 2022+**

[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![VS](https://img.shields.io/badge/VS-2022%2017.14%2B-purple)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)]()
[![Version](https://img.shields.io/badge/version-1.1.10-blue)]()

[中文](README.md)

</div>

---

## Overview

**DeepSeek v4 for Visual Studio** embeds the DeepSeek V4 model into Visual Studio 2022, providing AI-assisted chat, code editing, code completion, and more.

---

## Core Features

| Feature | Description |
|---------|-------------|
| 🧠 **DeepSeek V4** | Streaming chat, Deep Thinking (Reasoning), Pro/Flash dual models |
| 🤖 **Multi-Agent** | Ask / Explore / Plan / Edit / Build — five agents with automatic Handoff collaboration |
| 📐 **Skills** | Define reusable AI workflows via Markdown, triggered with `/` slash commands |
| 🔧 **MCP Protocol** | Connect to external tool servers, automatic Function Calling |
| 📝 **Three Editing Methods** | apply_patch / insert_edit / create_file, four-level matching + Healing auto-fix |
| 📚 **1M Context** | 900K token budget, intelligent compression, no file truncation |
| 📊 **Code Diff Preview** | Red/green diff markers in editor, per-hunk confirm or apply all |
| 💡 **Ghost Text Completion** | Inline grey predictions, context-aware, Tab to accept |
| 🌐 **Web Search** | Baidu Qianfan + DuckDuckGo dual engine, auto fallback on quota exhaustion |
| 🖼️ **Image OCR** | Windows built-in / MCP remote — dual engines |
| 📄 **File Parsing** | Drag & drop 50+ formats (code/docs/PDF/Office/images) |
| 🛡️ **Terminal Approval** | Confirmation popup before command execution for security |
| 🌐 **i18n** | Auto-switch between Chinese and English, custom translations supported |
| 🔄 **Stream Resume** | Auto-recover from network interruptions, seamless continuation |

---

## Installation

**Download VSIX**: Get `.vsix` from [Releases](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases), close VS, double-click to install.

**Build from Source**:

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
# Open .slnx with VS 2022 → Build → F5 to launch experimental instance
```

| Build Dependency | Version |
|------------------|---------|
| Visual Studio | 2022 (17.14+) |
| .NET Framework | 4.7.2 SDK |
| VS SDK | VS Installer → "Visual Studio extension development" |
| Windows | 10/11 x64 |

---

## Quick Start

1. **Get API Key**: [platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys)
2. **Configure**: `Tools → Options → DeepSeek Chat` → Paste API Key → Select model
3. **Open Chat**: `View → Other Windows → DeepSeek Chat`

| Recommended Settings | Value |
|----------------------|-------|
| Model | `deepseek-v4-pro` |
| Deep Thinking | On, Reasoning Effort = `high` |
| Search | Baidu Qianfan (1500 free/month) |
| OCR | Windows built-in / MCP remote |
| Token Budget | 900000 |

---

## Multi-Agent Collaboration

Five agents collaborate via Handoff — no manual switching required:

```
Ask (Analyze) → Plan (Design) → Edit (Execute) → Build (Fix) → Ask (Report)
```

| Agent | Role |
|-------|------|
| **Ask** | Q&A, code explanation, read-only analysis |
| **Explore** | Codebase search, structure analysis |
| **Plan** | Task decomposition, solution design, plan.md generation |
| **Edit** | Code write/delete, file operations |
| **Build** | Build diagnostics, auto-fix (up to 3 rounds) |

---

## Skills

Define reusable AI workflows with Markdown. Type `/` in chat to trigger:

```text
/code-review  UserService.cs
/tdd          Implement user login feature
```

Skill sources: **Project** (`.github/skills/`) → **User** (`~/.copilot/skills/`) → **Built-in** (bundled with extension).

---

## 🗺️ Roadmap

| Plan | Description | Priority |
|------|-------------|----------|
| **Built-in Local Vector DB** | SQLite + local embedding model, zero-config code indexing & semantic search | 🔴 High |
| **Auto File Indexing** | Automatically build vector index when opening project | 🔴 High |
| **Hybrid Retrieval** | BM25 keyword + vector semantic hybrid search | 🟡 Medium |
| **Test Generation Skill** | Auto-generate xUnit tests via `tdd` skill | 🟡 Medium |
| **Enhanced Git Integration** | PR description generation, auto commit messages | 🟡 Medium |
| **Solution-Level Indexing** | Cross-project symbol indexing and reference tracking | 🟡 Medium |
| **Local Model Support** | Ollama / LM Studio offline inference | 🟢 Low |
| **More Built-in Skills** | `debug-analyzer`, `sql-optimizer`, etc. | 🟡 Medium |
| **Session Export** | Export conversations as Markdown / PDF | 🟢 Low |

> 💡 Suggestions welcome via [Issues](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues)!

---

## 👥 Contributors

<a href="https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=zmy15/DeepSeek-v4-for-VisualStudio" />
</a>

---

## 📈 Star History

<a href="https://www.star-history.com/?type=date&repos=zmy15%2FDeepSeek-v4-for-VisualStudio">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&theme=dark&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&legend=top-left" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=zmy15/DeepSeek-v4-for-VisualStudio&type=date&legend=top-left" />
 </picture>
</a>

---

## License

[MIT License](LICENSE)
