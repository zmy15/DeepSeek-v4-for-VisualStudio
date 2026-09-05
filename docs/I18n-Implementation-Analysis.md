# i18n（国际化/本地化）实现现状完整分析报告

> 日期：2026-08-24　范围：LocalizationService / 资源文件 / XAML·属性标注 / 引用完整性 /
> 硬编码残留 / 双语键集一致性 / 已知缺陷与路线

---

## 一、架构总览

### 1.1 运行时链路

```
启动: InitializeAsync Step2 Initialize(null)（后台线程自动检测）
      └─ Step7 主线程 CaptureVsUiLanguage() + Initialize() 二次细化
用户发送消息 ──► View.CaptureFromActiveView() 等 UI 事件
                    │
                    ▼
LocalizationService.Instance[key] / Format(key, args…)
                    │
        三层字典叠加（OrdinalIgnoreCase）:
          ① zh-CN.json            ← 默认兜底（isFallback=true 只填空位）
          ② {Current}.json        ← 当前语言覆盖（zh-CN 时跳过）
          ③ {Current}.user.json   ← 用户自定义覆盖（可选文件）
                    │
        缺键回退: "[key]" 字面量暴露给调用方
```

### 1.2 关键机制

| 机制 | 实现 | 评价 |
|------|------|------|
| 语言检测 | VS UI 文化（主线程捕获 `_vsUiLanguageName`）→ `GetUserDefaultUILanguage` → 兜底 **en** | ✅ 设计正确：避免后台线程误报系统语言导致非中文用户收到中文 |
| 热切换 | `SetLanguage` → `Reload()` → `LanguageChanged` 事件 | ✅ |
| XAML | `I18nExtension`（MarkupExtension，自动订阅 LanguageChanged 刷新） | ✅ 但 View 中仅 **7 处**使用（主聊天 UI 在 WebView2，不走 XAML） |
| 选项页 | `LocalizedCategoryAttribute` 等 ×33 处；反射改写 CategoryAttribute 内部字段实现切换后刷新 | ✅ 巧妙但依赖内部实现细节 |
| 嵌入资源回退 | 默认语言可从 `.Resources.Locales.zh-CN.json` 嵌入资源加载 | ✅ CI/测试友好 |
| 跨语言读取 | `GetValueForLocale(key, locale)`（不切换当前语言） | ✅ 用于 System Prompt 双语选择 |
| 用户覆盖 | `{lang}.user.json`（附 example 模板） | ✅ 差异化亮点 |

### 1.3 资源规模（实测）

| 文件 | 键数 |
|------|-----:|
| en.json | **1539** |
| zh-CN.json | **1538** |
| 代码引用的键（去重） | **928** |

---

## 二、双语一致性与引用完整性（自动化审计）

### 2.1 en ↔ zh-CN 键集差异：3 处不同步

| 键 | en | zh-CN |
|----|:--:|:-----:|
| `chat.html.handoffButtonTitle` | ✅ | ❌ |
| `agent.log.planReuseAlignment` | ✅ | ❌ |
| `agent.log.planExploreDone` | ❌ | ✅ |

影响：缺失侧回退到另一语言的值（叠加逻辑保证有值），但会造成混语言显示。

### 2.2 引用但未定义（Missing）：**12 个** 🔴

以下键被代码直接引用，两个 locale 均未定义 → **运行时向用户/模型输出 `[key]` 字面量**：

```
agent.build.handoffAskPrompt
agent.log.buildReconciledStepResult
agent.log.buildReconciledSteps
agent.log.codeMemoryUpdated
agent.log.editAutoSplit
agent.log.editExplicitRouteSkipHandoff
agent.log.patchApplied
edit.healingHeaderOriginalReplace
edit.healingInstructionReplace
tool.readFile.param.maxLines
tool.symbolSearch.desc
plan.md.section? （见 keys-used 全量比对）
```

### 2.3 定义但未引用（Orphan）：623 个 🟡

按前缀分布：tool 226 / settings 79 / agent 76 / status 33 / chat 30 …

⚠️ **注意失真**：`tool.*.desc/param.*` 等由 GetDefinition 动态拼装键名经反射引用，
静态扫描必然误报。剔除 tool.*（226）后真实孤儿约 **397 个**，仍值得清理，
但需先排除 `settings.*`、`mcp.dialog.*` 等潜在动态拼接族。

---

## 三、最大的结构性问题：硬编码中文绕过本地化体系

实测数据：

- 产品代码（不含测试）**184 / 184 个 .cs 文件全部包含中文字符（100%）**
- 含中文字符串字面量的行约 **2688 行**

典型分布：

| 域 | 示例 |
|----|------|
| Agent 日志/提示 | `[EditAgent] 步骤X「…」由 AI 声明完成`、`(由步骤N的AI输出自动标记完成)`、`连续 N 轮…已自动终止` |
| 工具输出 | `终端输出 (退出码: 0): …`、`目录: …`、`Error: 工具执行异常: …`、`未知操作: …` |
| 编辑服务消息 | `写入后校验失败：磁盘内容与预期不一致`、`无法创建备份文件`、`目标文件已不存在。` |
| View 提示 | `没有打开的活动文档`、`已添加 N 个项目文件到上下文`、`修改`/`新建`/`删除` |
| 模型提示词片段 | EditAgent 步骤自检、PlanAgent 发现阶段说明等大段中文 |

**影响判定**：
- 英文用户的核心对话可用（主要 UI 骨架已本地化），但 Agent 日志、思考气泡、
  工具结果摘要、异常信息将大量出现中文 → 「English」承诺只兑现了表层；
- 反向无虞：中文用户不受影响。

**根因**：项目由中文作者快速迭代，i18n 抽取从未系统性执行；
LocalizationService 体系本身质量良好，缺的是「把字面量搬进资源」的工程投入。

---

## 四、新接入面的 i18n 缺口（本轮 Phase A/B 引入或暴露）

| 项 | 说明 | 严重度 |
|----|------|:---:|
| VSEXT SettingCategory 标题硬编码中文 | `DeepSeekUnifiedSettings.cs` 中 "深度思考模式" 等 7 项 title/Description 及类别标题 "DeepSeek Chat 设置" 为中文字面量。Setting API 为声明式静态属性，Preview 阶段不支持资源化 → 英文环境的新设置 UI 将显示中文 | 中 |
| ProvideProfile 资源仅中性英文 | VSPackage.resx 16001–16003 为英文字符串，无 zh-CN 卫星资源 → 中文用户的导入/导出向导显示英文 | 低 |
| get_errors 新增段 | "Live Error List (structured)" 为英文字面量（有意为之：模型面向文本，规避 locale 轮换） | 信息 |
| 本报告同源教训 | PowerShell 自动化脚本内联 CJK 被 GBK 解析损坏（PR 标题/测试文件事故）→ 一切脚本保持 ASCII 或带 BOM | 流程 |

---

## 五、已知缺陷清单（可直接修复）

| # | 缺陷 | 修复 | 成本 |
|---|------|------|:---:|
| D1 | 12 个 missing key（运行时 `[key]` 泄漏） | 按 key 语义补 en+zh 双语条目 | 1h |
| D2 | 双语键集 3 处不同步 | 互译补齐 | 0.5h |
| D3 | orphan 清理（先剔除动态族） | 删除或标记 Reserved | 2h |
| D4 | VSEXT 类别/条目标题本地化受限 | 短期：接受中文；长期：待 Setting API 支持资源化 | — |
| D5 | ProvideProfile 卫星资源（zh-CN resx） | 新增 `VSPackage.zh-Hans.resx` | 1h |

---

## 六、改进路线建议

### 阶段一（止血，≈半天）—— ✅ 已于 2026-08-24 完成
D1（12 个缺失键）与 D2（3 处双语差异）已全部补齐：
en/zh 键数 **1551/1551 完全一致**，代码引用缺失键清零；
校验脚本固化于 `Temp\verify-i18n.ps1` 模式（JSON 解析 + 键集比对 + 引用完整性三查）。

### 阶段二（硬编码治理，分域推进）
按用户可见频率排序抽取：
1. `get_errors / apply_patch / replace_*` 等高频工具输出（模型+用户双消费）
2. Agent 思考气泡日志（FormatLogForThinking 白名单域）
3. 异常消息（catch 分支的用户可读部分）
每抽取一批即跑全量回归；新增代码强制走 L["…"]（可加 analyzer/CI 扫描禁止
产品代码裸 CJK 字面量，白名单目录显式豁免）。

### 阶段三（体验补全）
- VSEXT 标题随 CurrentLanguage 运行时切换的可行性调研（当前 Preview API 限制记录在案）
- ProvideProfile 中文卫星资源
- 语言粒度扩展（如 zh-TW/en-GB）仅需 SupportedLanguages + json 副本

## 七、结论

- **框架层质量高**：三层叠加、热切换、VS 语言跟随、用户覆盖、嵌入回退等设计成熟；
- **内容层欠账大**：100% 产品文件存在硬编码中文（约 2688 行字面量），en 本地化覆盖率
  远低于表面键数所暗示的水平；
- **即时行动**：修复 D1/D2（12 缺失键 + 3 键集差异）消除可见缺陷；
- **中期行动**：以「域」为单位分批抽取硬编码中文，并以 CI 规则防止回流。

---
*证据生成方式：rg 键引用扫描 ×928 / locale 键集 ×1539+1538 / Compare-Object 差异 /
CJK 字面量行计数 ×2688 / LocalizationService・I18nExtension・LocalizedAttributes 源码通读。*
