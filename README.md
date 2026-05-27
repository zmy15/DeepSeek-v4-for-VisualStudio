<div align="center">

> ⚠️ **测试阶段** — 使用前请备份项目（Git 提交或手动备份）。

# DeepSeek v4 for Visual Studio

**将 DeepSeek V4 深度集成到 Visual Studio 2022+ 的 AI 编程助手**

[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![VS](https://img.shields.io/badge/VS-2022%2017.14%2B-purple)]()
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet)]()
[![DeepSeek](https://img.shields.io/badge/DeepSeek-V4-green)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)]()
[![Version](https://img.shields.io/badge/version-1.1.5-blue)]()

[English](README_EN.md)

</div>

---

## 简介

**DeepSeek v4 for Visual Studio** 将 DeepSeek V4 模型嵌入 Visual Studio 2022，提供聊天、代码编辑、代码补全等 AI 辅助编程能力。

---

## 核心特性

| 特性 | 说明 |
|------|------|
| 🧠 **DeepSeek V4** | 流式对话、深度思考 (Reasoning)、Pro/Flash 双模型 |
| 🤖 **多智能体** | Ask / Explore / Plan / Edit / Build 五种 Agent，Handoff 自动协作 |
| 📐 **Skills 技能** | 用 Markdown 定义可复用 AI 工作流，`/` 斜杠命令触发 |
| 🔧 **MCP 协议** | 连接外部工具服务器，Function Calling 自动调用 |
| 📝 **三种编辑方法** | apply_patch / insert_edit / create_file，四级匹配 + Healing 自动修复 |
| 📚 **1M 上下文** | 900K Token 预算，智能压缩，文件不截断 |
| 📊 **代码差异预览** | 编辑器内红绿 Diff 标记，逐条确认或一键应用 |
| 💡 **Ghost Text 补全** | 行内灰色预测，上下文感知，Tab 接受 |
| 🌐 **联网搜索** | 百度千帆 + DuckDuckGo 双引擎，额度耗尽自动切换 |
| 🖼️ **图像 OCR** | Windows 内置 / MCP 远程双引擎 |
| 📄 **文件解析** | 拖拽或粘贴 50+ 格式（代码/文档/PDF/Office/图片） |
| 🛡️ **终端审批** | 命令执行前弹窗确认，保障安全 |
| 🌐 **国际化** | 中英文自动切换，支持自定义翻译 |
| 🔄 **断点续传** | 网络中断自动恢复，已接收内容无缝衔接 |
| 🧠 **AI 记忆系统** | 三层持久化记忆（用户/会话/仓库），AI 自主管理笔记 |

---

## 安装

**下载 VSIX**：从 [Releases](https://github.com/zmy15/DeepSeek-v4-for-VisualStudio/releases) 下载 `.vsix`，关闭 VS 后双击安装。

**从源码编译**：

```powershell
git clone https://github.com/zmy15/DeepSeek-v4-for-VisualStudio.git
# VS 2022 打开 .slnx → 编译 → F5 启动实验实例
```

| 编译依赖 | 版本要求 |
|----------|----------|
| Visual Studio | 2022 (17.14+) |
| .NET Framework | 4.7.2 SDK |
| VS SDK | VS Installer → "Visual Studio 扩展开发" |
| Windows | 10/11 x64 |

---

## 快速开始

1. **获取 API Key**：[platform.deepseek.com/api_keys](https://platform.deepseek.com/api_keys)
2. **配置**：`工具 → 选项 → DeepSeek Chat` → 粘贴 API Key → 选择模型
3. **打开聊天窗口**：`视图 → 其他窗口 → DeepSeek Chat`

| 推荐设置 | 值 |
|----------|-----|
| 模型 | `deepseek-v4-pro` |
| 深度思考 | 开启，Reasoning Effort = `high` |
| 搜索 | 百度千帆（月 1500 次免费） |
| OCR | Windows 内置 / MCP 远程 |
| Token Budget | 900000 |

---

## 多智能体协作

五种 Agent 通过 Handoff 自动分派任务，无需手动切换：

```
Ask (分析) → Plan (规划) → Edit (执行) → Build (编译修复) → Ask (汇报)
```

| Agent | 职责 |
|-------|------|
| **Ask** | 问答、代码解释、只读分析 |
| **Explore** | 代码库搜索、结构分析 |
| **Plan** | 任务分解、方案设计、生成 plan.md |
| **Edit** | 代码写入/删除、文件操作 |
| **Build** | 编译诊断、自动修复（最多 3 轮） |

---

## Skills 技能

用 Markdown 定义可复用的 AI 工作流。在聊天中输入 `/` 触发：

```text
/code-review  UserService.cs
/tdd          实现用户登录功能
```

技能来源：**项目级** (`.github/skills/`) → **用户级** (`~/.copilot/skills/`) → **内置级** (随扩展发布)。

---

## 记忆系统

AI 通过 `memory` 工具管理三层持久化记忆，记住你的偏好、项目约定和对话上下文：

| 作用域 | 路径前缀 | 存储位置 | 生命周期 |
|--------|---------|---------|---------|
| **用户记忆** | `/memories/` | `%LocalAppData%\DeepSeekVS\memories\user\` | 跨所有解决方案持久化 |
| **会话记忆** | `/memories/session/` | `%LocalAppData%\DeepSeekVS\memories\session\` | 当前对话内有效 |
| **仓库记忆** | `/memories/repo/` | `%LocalAppData%\DeepSeekVS\memories\repo\` | 当前解决方案内有效 |

**支持的操作**：`view`、`create`、`str_replace`、`insert`、`delete`、`rename`

**自动注入**：新对话开始时，用户记忆和仓库记忆自动注入 System Prompt，AI 开箱即知你的偏好和项目约定。

所有 Agent（Ask / Explore / Plan / Edit / Build）均可使用记忆工具。

---

## 🗺️ 路线图

| 计划项 | 说明 | 优先级 |
|--------|------|--------|
| **内置本地向量库** | SQLite + 本地嵌入模型，零配置代码索引和语义检索 | 🔴 高 |
| **文件自动索引** | 打开项目时自动构建向量索引 | 🔴 高 |
| **混合检索策略** | BM25 关键词 + 向量语义混合检索 | 🟡 中 |
| **测试生成 Skill** | 基于 `tdd` 技能自动生成 xUnit 测试 | 🟡 中 |
| **Git 增强集成** | PR 描述生成、Commit Message 自动撰写 | 🟡 中 |
| **解决方案级索引** | 跨项目符号索引和引用追踪 | 🟡 中 |
| **本地模型支持** | Ollama / LM Studio 离线推理 | 🟢 低 |
| **更多内置 Skills** | `debug-analyzer`、`sql-optimizer` 等 | 🟡 中 |
| **会话导出** | 对话导出为 Markdown / PDF | 🟢 低 |

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

## 开源协议

[MIT License](LICENSE)