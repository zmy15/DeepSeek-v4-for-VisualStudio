<div align="center">

> ⚠️ **This project is under active development. Some features may be incomplete and APIs are subject to change.**

# DeepSeek v4 for Visual Studio

**DeepSeek V4 · Deep Thinking · MCP Protocol · Skills System · Web Search · OCR · Multi-Agent Collaboration**

*A full-featured AI programming assistant that deeply integrates DeepSeek V4 into Visual Studio 2022*

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![VS](https://img.shields.io/badge/VS-2022%2017.14%2B-purple.svg)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet.svg)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green.svg)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey.svg)]()

[中文文档](README.md)

</div>

---

## What Is This?

The days of switching to a browser to ask AI are over.

**DeepSeek v4 for Visual Studio** embeds the DeepSeek V4 model directly into your editor. Select code, paste screenshots, drag in files — AI is right there, ready to respond.

It's more than just a chat window — it's a complete **AI workflow system**: a multi-agent collaboration engine that automatically dispatches tasks to the best-suited Agent, a Skills engine that lets you define reusable AI workflows, MCP protocol integration for connecting any tool ecosystem, and three OCR engines that can read your error screenshots.

---

## Feature Overview

```
🧠 DeepSeek V4          Streaming chat · Deep Thinking (Reasoning) · Dual model support
🤖 Multi-Agent System   Ask / Explore / Plan / Edit — four agents working together
🔧 MCP Protocol         Multi-server connectivity · Function Calling · Custom tool extension
📐 Skills System        Slash commands · Project/User/Built-in tiers · YAML frontmatter
🌐 Web Search           Baidu Qianfan + DuckDuckGo dual engines · Auto-fallback on quota exhaustion
📄 File Parsing         50+ formats · Code / Documents / PDF / Office — all supported
🔍 Image OCR            Windows Built-in · PaddleOCR · MCP OCR — three engines
📊 Diff Preview         In-editor red/green markers · Confirm/Undo · One-click apply
💡 Code Completion      Ghost text inline predictions · Context-aware · Configurable debounce
💬 Chat Window          WebView2 rendering · Markdown highlighting · Multi-session persistence
⚙️ Visual Settings      Tools → Options — one-stop configuration
```

---

## Multi-Agent System

The extension features four specialized Agents that automatically collaborate on complex tasks:

| Agent | Role | Capabilities |
|-------|------|-------------|
| **Ask** 🤔 | Q&A Assistant | Pure Q&A, code explanation, read-only analysis |
| **Explore** 🔍 | Explorer | Codebase search, file discovery, structure analysis |
| **Plan** 📋 | Planner | Task planning, solution design, forbids code modification |
| **Edit** ✏️ | Executor | Code modification, file operations, coordinates with Explore |

Agents support a **Handoff** mechanism — for example, Plan formulates a strategy and hands it off to Edit for execution; Edit dispatches Explore when it needs to discover files.

---

## Skills System

> This is the core feature that sets this extension apart from ordinary AI plugins.

### What Is a Skill?

A Skill is a Markdown file (`SKILL.md`) with YAML frontmatter that describes "when to trigger, how to execute":

```markdown
---
name: code-review
description: 'Review code quality, security, performance. Use when: code review, PR review'
argument-hint: '[file path or code]'
user-invocable: true
---

# Code Review

## Process
1. Analyze from five dimensions: correctness, security, performance, maintainability, best practices
2. 🔴 Critical → 🟡 Medium → 🟢 Suggestion — list issues by priority
3. Provide fix proposals and code examples for each issue
```

### Three Skill Tiers

| Tier | Path | Description |
|------|------|-------------|
| 📁 **Project** | `.github/skills/` `.agents/skills/` `.claude/skills/` | Version-controlled, shared by team |
| 👤 **User** | `~/.copilot/skills/` `~/.agents/skills/` | Personal preferences, cross-project |
| 🏭 **Built-in** | `BuiltInSkills/` (shipped with extension) | Ready out of the box, e.g., `code-review` |

### Usage

Type `/` in the chat window to trigger slash command autocompletion. Select a skill and the AI loads the corresponding workflow.

```text
/code-review  UserService.cs
```

---

## MCP Protocol Integration

Connect external tool servers via the **Model Context Protocol (MCP)** to expand AI capabilities:

- **Multi-server support**: Connect multiple MCP servers simultaneously, invoke tools on demand
- **Function Calling**: AI automatically determines when to call external tools
- **Tool whitelisting**: Each Agent declares which tools it's allowed to use
- **Persistent config**: MCP server configurations stored at `%LocalAppData%\DeepSeekVS\mcp_servers.json`
- **Built-in OCR server**: PP-OCRv5 integrated by default (via `uvx paddleocr-mcp`)

Configuration: Chat window → Click 🔌 MCP button → Add/Manage servers.

---

## Web Search

| Search Engine | Highlights |
|---------------|-----------|
| **Baidu Qianfan** | 1,500 free requests/month, auto-fallback when quota exhausted |
| **DuckDuckGo** | Completely free, no quota limits |

Search keywords are intelligently generated from conversation context, and results are automatically injected into the chat.

---

## Image OCR

Three engines for different scenarios:

| Engine | Accuracy | Setup |
|--------|----------|-------|
| **Windows Built-in** | Moderate | Zero config, ready out of the box |
| **PaddleOCR-Sharp** | ≥95% Chinese recognition | Auto-downloads ChineseV5 model |
| **MCP OCR** | Depends on server | Requires MCP OCR server setup |

Simply `Ctrl+V` paste an error screenshot, and the AI automatically recognizes the text and analyzes the issue.

---

## Installation

### Recommended: Download VSIX

1. [**Releases**](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases) → Download `DeepSeek_v4_for_VisualStudio.vsix`
2. Close Visual Studio → Double-click the `.vsix` → Install
3. Restart Visual Studio

### Advanced: Build from Source

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
# Open .slnx in VS 2022 → Ctrl+Shift+B to build → F5 to debug
```

**Prerequisites**:
- Visual Studio 2022 17.14+
- .NET Framework 4.7.2 SDK
- Visual Studio SDK (install via VS Installer)

---

## Quick Start

### ① Get an API Key

[platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys) → Create Key → Copy

### ② Configure

`Tools` → `Options` → `DeepSeek Chat` → Paste Key → Select model

| Setting | Recommended | Notes |
|---------|-------------|-------|
| API Key | Your key | Obtain from platform.deepseek.com |
| Selected Model | `deepseek-v4-pro` | Primary model |
| Enable Deep Thinking | ✅ On | Enable reasoning chain |
| Reasoning Effort | `high` | Reasoning depth (high / max) |
| Search Provider | `DuckDuckGo` | Free, no API key needed |
| OCR Engine | `PaddleOCR-Sharp` | Best Chinese recognition |
| Show Diff Markers | ✅ On | Preview code changes |
| Copilot Enable | ✅ On | Inline code completion |

### ③ Start Chatting

`View` → `Other Windows` → `DeepSeek Chat`, or click the 🧠 icon on the toolbar.

### ④ Common Operations

| Operation | How |
|-----------|-----|
| Ask about code | Type directly — AI reads currently open files |
| Parse file contents | Drag & drop files into chat window |
| OCR error screenshots | `Ctrl+V` paste screenshot, auto OCR |
| Search the web | Enable 🌐 Web Search toggle |
| Invoke a Skill | Type `/` and select a skill command |
| Configure MCP servers | Click 🔌 MCP button |
| Preview code changes | Enable Diff Markers, confirm before applying |
| Get code suggestions | Enable Copilot, suggestions appear as you type |

---

## Settings Reference

### API Settings
- **API Key**: DeepSeek platform API key
- **System Prompt**: Custom system prompt (optional, leave blank for default)

### Model Settings
- **Selected Model**: Choose which DeepSeek model to use
- **Enable Deep Thinking**: Toggle reasoning (chain-of-thought) mode
- **Reasoning Effort**: `high` for speed-quality balance, `max` for strongest reasoning

### Web Search
- **Enable Web Search**: Toggle web search on/off
- **Search Provider**: Baidu Qianfan / DuckDuckGo
- **Baidu API Key**: Baidu Qianfan key (optional, free tier available)

### Editor
- **Show Diff Markers in Editor**: Toggle in-editor code change markers

### OCR
- **OCR Engine**: Select OCR engine (Windows Built-in / PaddleOCR-Sharp / MCP)

### Code Completion
- **Enable Copilot**: Toggle inline code completion
- **Suggestion Interval**: Debounce time before triggering suggestions

---

## Project Structure

```
DeepSeek_v4_for_VisualStudio/
├── DeepSeek_v4_for_VisualStudioPackage.cs    VS extension entry point (AsyncPackage)
├── source.extension.vsixmanifest             VSIX manifest
├── VSCommandTable.vsct                       Menu/toolbar command table
│
├── Commands/
│   └── ShowChatWindowCommand.cs              Window command
│
├── Models/
│   ├── DeepSeekModels.cs                     API request/response · Streaming · Function Calling
│   ├── AgentModels.cs                        Agent data models
│   ├── AgentTypes.cs                         Agent type enums
│   ├── McpTypes.cs                           MCP JSON-RPC 2.0 protocol types
│   ├── SkillDefinition.cs                    Skill definition · Source enum · Discovery results
│   ├── SkillSuggestionItem.cs                Slash command autocomplete items
│   ├── ConversationTree.cs                   Conversation tree data structure
│   ├── ContextModels.cs                      Context models
│   ├── RagModels.cs                          RAG retrieval-augmented generation models
│   └── ToolCallAccumulator.cs                Tool call accumulator
│
├── Services/
│   ├── DeepSeekApiService.cs                 API communication (streaming + thinking mode)
│   ├── AgentDispatcher.cs                    ★ Multi-agent dispatch center
│   ├── SkillService.cs                       ★ Skills discovery/parsing/caching/events
│   ├── McpManagerService.cs                  MCP multi-server management & tool aggregation
│   ├── McpStdioClient.cs                     stdio transport client
│   ├── McpConfigStore.cs                     MCP config JSON persistence
│   ├── WebSearchService.cs                   Baidu Qianfan + DuckDuckGo search
│   ├── FileParserService.cs                  50+ file format parsing
│   ├── OcrService.cs                         Windows/PaddleOCR/MCP three engines
│   ├── ChatHtmlService.cs                    WebView2 HTML templates
│   ├── ChatPersistenceService.cs             Chat history local persistence
│   ├── ContextCompressorService.cs           Context compression (token budget management)
│   ├── ConversationContextManager.cs         Conversation context builder
│   ├── CodeDiffService.cs                    Code difference computation
│   ├── DiffViewerService.cs                  Diff visualization & markers
│   ├── EditorDiffMarkerService.cs            Editor inline markers
│   ├── RagService.cs                         RAG retrieval-augmented generation
│   └── AiPrompts.cs                          Centralized prompt management
│   │
│   └── Agents/
│       ├── AskAgent.cs                       Ask agent
│       ├── ExploreAgent.cs                   Explore agent
│       ├── PlanAgent.cs                      Plan agent
│       └── EditAgent.cs                      Edit agent
│
├── Settings/
│   ├── DeepSeekOptionsPage.cs                Tools→Options configuration page
│   └── DownloadLinkEditor.cs                 UI editor
│
├── CodeCompletion/
│   ├── InlinePredictionManager.cs            Inline prediction manager
│   ├── GhostTextTagger.cs                    Ghost text tagger
│   ├── GhostTextTaggerProvider.cs            Ghost text provider
│   └── CommandFilter.cs                      Command filter
│
├── View/
│   ├── DeepSeekChatWindowPane.cs             VS ToolWindow pane
│   ├── DeepSeekChatControl.xaml/.cs          WPF main control
│   ├── DeepSeekChatControl.Events.cs         Event handling (partial class)
│   ├── DeepSeekChatControl.Messaging.cs      Message send/receive (partial class)
│   ├── DeepSeekChatControl.Rendering.cs      UI rendering (partial class)
│   ├── DeepSeekChatControl.Sessions.cs       Session management (partial class)
│   ├── DeepSeekChatControl.Clipboard.cs      Clipboard OCR (partial class)
│   ├── DeepSeekChatControl.Agent.cs          Agent interaction (partial class)
│   ├── DeepSeekChatControl.CodeActions.cs    Code actions (partial class)
│   ├── DeepSeekChatControl.Search.cs         Search features (partial class)
│   ├── DeepSeekChatControl.Skills.cs         Skills system (partial class)
│   ├── DiffPreviewAdornment.cs               Diff preview adornment
│   ├── DiffViewerWindow.xaml/.cs             Diff viewer window
│   └── McpConfigDialog.xaml/.cs              MCP configuration dialog
│
├── Utils/
│   ├── Logger.cs                             Logging utility
│   └── StringExtensions.cs                   String extensions
│
└── Resources/                                Icons & style resources
```

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET Framework 4.7.2 · WPF |
| VS SDK | Microsoft.VisualStudio.SDK 17.14 |
| Chat UI | WebView2 (Chromium) |
| Markdown | Markdig 1.1.3 |
| Document Parsing | NPOI 2.8.0 · PdfPig 0.1.14 |
| OCR | Windows.Media.Ocr · PaddleOCR 3.0.1 · OpenCvSharp 4.10 |
| Serialization | System.Text.Json |
| MCP | JSON-RPC 2.0 over stdio |

---

## Contributing

Issues and Pull Requests are welcome.

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'feat: add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Create a Pull Request

---

## Acknowledgments

- [DeepSeek](https://www.deepseek.com/) — Powerful AI model support
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — Excellent OCR engine
- [Markdig](https://github.com/xoofx/markdig) — Fast Markdown parser

---

## License

This project is open-sourced under the [MIT License](LICENSE).

Copyright (c) 2024 zmy15
