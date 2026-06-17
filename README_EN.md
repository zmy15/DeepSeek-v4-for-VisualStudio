<div align="center">

> ⚠️ **Beta Stage** — Backup your project before use (Git commit or manual backup).

# DeepSeek v4 for Visual Studio

**An AI-powered coding assistant deeply integrated into Visual Studio 2022+ with DeepSeek V4**

[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![VS](https://img.shields.io/badge/VS-2022%2017.14%2B-purple)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)]()
[![Version](https://img.shields.io/badge/version-1.1.11-blue)]()

[中文](README.md)

</div>

---

## 📖 Overview

**DeepSeek v4 for Visual Studio**is a Visual Studio 2022+ extension that deeply embeds the DeepSeek V4 large language model into the IDE. It provides a native-grade chat experience through **WebView2**, supporting multi-agent collaboration, code editing, inline completions, terminal command execution, web search, OCR image recognition, file parsing, and MCP external tool integration.

**Core architecture** is built on .NET Framework 4.7.2 + WPF + WebView2, using Visual Studio SDK 17.14, compatible with Visual Studio 2022 17.14 and above.

---

## ✨ Core Features

| Feature | Description |
|---------|-------------|
| 🧠 **DeepSeek V4** | Streaming chat, Deep Reasoning, Pro/Flash dual model switching |
| 🤖 **Multi-Agent** | Five agents (Ask/Explore/Plan/Edit/Build) with Handoff auto-collaboration |
| 📐 **Skills System** | Markdown-defined (SKILL.md) reusable AI workflows, triggered by `/` slash commands |
| 🔧 **MCP Protocol** | Connect external tool servers (HTTP + stdio), auto Function Calling with classification injection |
| 📝 **Three Edit Methods** | apply_patch / insert_edit / create_file, Levenshtein 4-tier matching + Healing auto-repair |
| 📚 **1M Context** | 900K token budget, intelligent compression, files never truncated |
| 📊 **Code Diff Preview** | In-editor red/green diff markers (EditorDiffMarkerService), per-hunk confirm or apply all |
| 💡 **Ghost Text Completion** | Inline gray prediction text, context-aware, Tab to accept |
| 🌐 **Web Search** | Baidu Qianfan + DuckDuckGo dual engines, automatic fallback on quota exhaustion |
| 🖼️ **Image OCR** | Windows built-in / MCP remote dual engines |
| 📄 **File Parsing** | Drag-drop or paste 50+ formats (code/docs/PDF/Office/images), powered by NPOI + PdfPig |
| 🛡️ **Terminal Approval** | Pre-execution confirmation dialog (BlockAll / AllowAll / SmartBlock modes) |
| 🌍 **Internationalization** | Auto Chinese/English switching, `zh-CN.user.json` custom translation overrides |
| 🔄 **Resumable Streaming** | Auto-recovery on network interruption, seamless continuation of received content |
| 🧠 **AI Memory System** | Three-tier persistent memory (user/session/repo), AI-managed notes, auto-injected on new conversations |
| ⚡ **Prefix Cache** | Leverage DeepSeek Prefix Caching to optimize token consumption for repeated contexts |


---

## 📦 Installation

### Option 1: Download VSIX (Recommended)

Download the latest `.vsix` from [Releases](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases), close Visual Studio, then double-click to install.

### Option 2: Build from Source

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
# Open .slnx with VS 2022 → Build → F5 to launch experimental instance
```

| Build Dependency | Version Requirement |
|------------------|---------------------|
| Visual Studio | 2022 (17.14+) |
| .NET Framework | 4.7.2 SDK |
| VS SDK | VS Installer → "Visual Studio extension development" |
| Windows | 10/11 x64 |

---

## 🚀 Quick Start

1. **Get API Key**: [platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys)
2. **Open Settings**: `Tools → Options → DeepSeek Chat` → Paste API Key → Select model
3. **Open Chat Window**: `View → Other Windows → DeepSeek Chat` (or click toolbar button)

| Recommended Setting | Value | Notes |
|--------------------|-------|-------|
| Model | `deepseek-v4-pro` | Flagship model, strongest reasoning |
| Deep Reasoning | On, Reasoning Effort = `max` | Better results for complex tasks |
| Web Search | Baidu Qianfan | 1,500 free calls/month |
| OCR Engine | Windows Built-in | System built-in, zero dependency |
| Token Budget | 900000 | Fully utilize the 1M context window |

---

## 🤖 Multi-Agent Collaboration

Five agents automatically dispatch tasks via the **Handoff protocol** — no manual switching required:

```
User Question → Ask (analyze)
                  ↓
              Plan (plan, multi-file/complex tasks)
                  ↓
              Edit (execute code changes, step-by-step confirmation)
                  ↓
              Build (build verification + auto-repair, max 3 rounds)
                  ↓
              Ask (summarize & report)
```

| Agent | Class | Responsibility | Permissions |
|-------|-------|---------------|-------------|
| **Ask** | `AskAgent` | Q&A, code explanation, tech discussion | Read-only |
| **Explore** | `ExploreAgent` | Codebase search, file exploration, structure analysis | Read-only (sub-agent of Plan/Ask) |
| **Plan** | `PlanAgent` | Task decomposition, solution design, generates plan.md | Read-only + sub-agent calls |
| **Edit** | `EditAgent` | Code write/delete, file operations, terminal execution | Read-write |
| **Build** | `BuildAgent` | Build diagnostics, auto error repair | Read-write + build |

**Routing Mechanism**:
- AI automatically classifies user intent (Plan→Edit→Build→Ask)
- Users can explicitly specify: `@ask question`, `@plan task`, `@edit change`, `@build compile`
- Explore is not independently routed; it is invoked internally by Plan/Ask agents via `runSubagent`

**Handoff Protocol** (`AgentTypes.cs` — `HandoffRequest`):
- Source agent declares handoff intent (target agent, reason, task description)
- Supports `ChainBack` — target chains back to source after completion
- Supports `ForwardedMessages` — reuses message list on handoff to maximize Prefix Cache hits

---

## 📐 Skills System

Define reusable AI workflows with Markdown (`SKILL.md`). Skill file format:

```yaml
---
name: code-review
description: 'Review code quality, security, performance. Triggers: code review, check code quality, find bugs, security audit, PR review'
argument-hint: '[file or code]'
user-invocable: true
disable-model-invocation: false
---
# Code Review

## When to Use
- User requests code review or inspection
- Self-review before PR submission
- Finding potential bugs, security vulnerabilities, or performance issues

## Execution Steps
1. Read user-provided code or currently open file
2. Analyze: correctness, security, performance, maintainability
3. Sort findings by severity, provide fix recommendations
```

**Skill Sources** (priority high to low):
1. **Project-level**: `.github/skills/`, `.agents/skills/`, `.claude/skills/`
2. **User-level**: `%USERPROFILE%\.copilot\skills\`, `%USERPROFILE%\.agents\skills\`, `%USERPROFILE%\.claude\skills\`
3. **Built-in**: `BuiltInSkills/` shipped with the extension

**Three Trigger Methods**:
- User explicit invocation: type `/skillname`
- AI semantic auto-match: automatically loaded when question semantics match skill description
- Context inference: proactively suggested when conversation context accumulates enough to need a specific skill

---

## 🧠 AI Memory System

AI manages three-tier persistent memory via the `memory` tool:

| Scope | Path Prefix | Storage Location | Lifecycle |
|-------|------------|-----------------|-----------|
| **User Memory** | `/memories/` | `%LocalAppData%\DeepSeekVS\memories\user\` | Persists across all solutions |
| **Session Memory** | `/memories/session/` | `%LocalAppData%\DeepSeekVS\memories\session\` | Valid within current conversation |
| **Repo Memory** | `/memories/repo/` | `%LocalAppData%\DeepSeekVS\memories\repo\` | Valid within current solution |

**Supported Operations**: `view`, `create`, `str_replace`, `insert`, `delete`, `rename`

**Auto-Injection**: At the start of each new conversation, user memory and repo memory are automatically injected into the System Prompt. Available to all agents.

---

## 🔧 MCP Protocol Integration

Connect external tool servers via **Model Context Protocol**:
- **Transport Modes**: HTTP + stdio dual mode
- **Tool Classification**: Auto-classify read/write by prefix, inject into corresponding agents
- **Management UI**: `McpConfigDialog.xaml` — visual configuration

---

## 📝 Code Editing Capabilities

### Three Edit Methods

| Method | Tool Name | Use Case |
|--------|-----------|----------|
| **Exact Replace** | `replace_string_in_file` | Single-location changes |
| **Multi Replace** | `multi_replace_string_in_file` | Multiple changes in same file |
| **Patch Apply** | `apply_patch` | Complex cross-file changes |

### Four-Tier Matching + Healing Auto-Repair

1. **Exact Match** → 2. **Line-Level Match** → 3. **Levenshtein Fuzzy Match** → 4. **Healing Repair**

### Diff Preview

Before modification, red/green diff markers appear in-editor. Confirm per-hunk through `DiffViewerWindow`.

---

## 💡 Ghost Text Inline Completion

Powered by DeepSeek FIM API (`api.deepseek.com/beta/completions`):
- Gray prediction text in-editor (`GhostTextTagger`)
- Tab to accept, Esc to cancel
- `InlinePredictionManager` manages prediction lifecycle

---

## 🌐 Web Search & OCR

### Search Engines

| Engine | Provider | Free Quota | Highlights |
|--------|----------|-----------|------------|
| Baidu Qianfan | Baidu | 1,500/month | High Chinese search quality |
| DuckDuckGo | Free | Unlimited | Privacy-friendly |

### OCR Dual Engines

| Engine | Implementation | Dependency |
|--------|---------------|------------|
| Windows Built-in | System API | Windows 10/11 |
| MCP Remote | MCP Protocol | External OCR service |

---

## 🛡️ Terminal Approval Security

Three approval modes: **BlockAll** / **AllowAll** / **SmartBlock** (recommended)

---

## 🌍 Internationalization

- Auto Chinese/English switching, based on `LocalizationService` + `I18nMarkupExtension`
- Custom translations: copy `zh-CN.user.json.example` to `zh-CN.user.json`

---

## 🧪 Testing

| Test Type | Framework | Location |
|-----------|-----------|----------|
| Unit Tests | xUnit 2.9.3 + Moq 4.20.72 + FluentAssertions 6.12.2 | `Tests/Unit/` |
| Integration Tests | Same as above | `Tests/Integration/` |
| Code Coverage | Coverlet 6.0.4 → Cobertura | Auto-uploaded in CI |

---

## 🗺️ Roadmap

| Plan | Description | Priority |
|------|-------------|----------|
| **RAG Code Retrieval Enhancement** | Local vector DB (SQLite + embedding model), auto file indexing, BM25 + vector hybrid search, solution-level cross-project symbol indexing | 🔴 High |
| **Test Generation Skill** | Auto-generate xUnit tests based on `tdd` skill | 🟡 Medium |
| **Enhanced Git Integration** | PR description generation, auto commit message writing | 🟡 Medium |
| **Local Model Support** | Ollama / LM Studio offline inference | 🟢 Low |
| **Session Export** | Export conversations as Markdown / PDF / HTML | 🟢 Low |
| **More Built-in Skills** | `debug-analyzer`, `sql-optimizer`, `api-designer` and other out-of-the-box skills | 🟡 Medium |

> 💡 Suggestions and code contributions welcome via [Issues](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/issues)!

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

## 📄 License

[MIT License](LICENSE) © 2024 zmy15
