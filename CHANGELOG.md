# 更新日志

## [Unreleased]

### 修复
- **VS2026 新版设置界面**：
  - 新增 `SettingCategory`（"DeepSeek Chat 设置"）并保留旧选项页；旧页是
    DeepSeek/百度/Bing API Key 的安全配置入口，三类密钥不进入 Unified Settings 的
    云同步/导出面
  - API Key 存储迁移到微软官方 `SVsCredentialStorageService` / Visual Studio keychain：
    旧 DPAPI 密文自动读取并迁移；保存成功后清空 DialogPage 设置存储中的 Key，避免继续
    出现在导入/导出面。凭据服务不可用时自动回退 DPAPI，不丢已有 Key
  - keychain 写入后立即读回校验，三把 Key 均确认持久化成功后才清除旧 DPAPI 备份，
    修复首次迁移启动时偶发读到空 Key 的问题
  - API Key 改为双持久化：优先读写 Visual Studio Credential Storage，同时保留
    DPAPI 加密备份，避免 keychain 瞬时故障导致密钥丢失
  - 新版设置页新增“API 密钥配置”只读指引，指向
    `工具 → 选项 → DeepSeek Chat → General`
  - 新版设置页元数据接入 VSEXT `string-resources.json` 本地化，随 VS 语言切换
  - 修复旧 Options 页外观设置描述语言切换后不刷新的问题
  - 修复 x64 完整版 OCR 引擎下拉只剩 Windows 内置的问题：恢复
    `PaddleOCR-Sharp` 本地 OCR 选项与运行时映射；ARM64/No-Local-OCR 变体生成脚本
    会继续移除该选项
  - `DeepSeekUnifiedSettings`（新版 "DeepSeek Chat 设置"）与 `UnifiedSettingsSync`
    双向同步桥恢复保留；旧页改动仍推送到新 UI，新 UI 改动仍回写 `Instance` 并热更新。
  - 新版设置类别从 7 项扩展到 32 项非敏感设置：补充系统提示词、模型/推理强度/
    搜索引擎/OCR/语言/审批模式等枚举，代码补全、上下文压缩、Agent 防护阈值与界面布局；
    新 UI 修改会同时保存回旧 DialogPage 存储，重启后不丢失
  - 恢复主题切换设置：新版设置页与旧 Options 页均可选择“跟随 Visual Studio / 深色 /
    浅色”，修改后立即刷新 WPF 与 WebView2 主题并持久化
  - 修复新 UI 修改模型/深度思考/推理强度后聊天窗口不跟随的问题：热更新回调改为以
    设置存储为准，并反向刷新聊天窗口控件；聊天窗口内修改这些控件也会持久化并推回新 UI
  - 修复联网搜索开关与新设置不同步的问题：Unified Settings 的“联网搜索”现在会实时
    点亮/关闭聊天窗口按钮；按钮与搜索引擎下拉修改也会写回设置并推送到新 UI
  - 修复 API Key 配置入口在 VS2026 中消失的问题：旧选项页不再被 Unified Settings 隐藏
  - 移除已降级的 MEF 外部区域原型 `UnifiedSettingsBridge`（含注册探针 `RunConsumptionScan`），
    避免在新版设置界面额外注册一个外部设置区域。
- **空白实例启动卡死（根因修复）**：
  - `UnifiedSettingsRegistrationProbe` 全域反射扫描曾同步运行在 UI 线程
    （实测热实例冻结 UI 18.7 秒，冷/空白实例可达分钟级）——现仅 Debug 构建
    保留并移入后台线程延迟执行，Release 构建完全剔除
  - 设置迁移拆分两阶段：`RegLoadAppKey` 挂载探测（慢 IO）移至后台线程，
    DialogPage 回填/保存留主线程；新增当前 hive 自排除与单 hive 3 秒超时兜底，
    不再探测本实例正在使用的活动 privateregistry.bin
  - 自动弹出聊天窗口改为标记门控：仅在用户显式打开过后（写入
    `%LocalAppData%\DeepSeekVS\chat-window-opened.flag`）的后续会话恢复弹出；
    空白/未使用实例启动保持安静，由工具栏 / 菜单 / Ctrl+Shift+D 主动触发
- 加载链路各阶段（GetDialogPage / 迁移 / 总耗时）补充计时诊断日志
- **发送按钮样式统一**：新增 `AccentButton` 样式（圆角/悬停/按下/禁用态与整体按钮语言一致），
  发送与停止按钮改用该样式，不再使用系统默认模板
- **重新生成 / 复制按钮尺寸统一**：重试、复制、编辑消息按钮改为本地化文本标签，
  并以相同 `min-width/min-height/padding/font-size` 渲染，深浅主题下均严格等宽等高

### 变更
- **i18n 缺口修复与阶段二启动**：
  - 补齐 12 个被引用但未定义的资源键（消除运行时 "[key]" 字面量泄漏）
  - 同步 en/zh 键集 3 处差异 → 双语各 1558 键完全一致
  - 阶段二批次1：ListDir/FileSearch/Grep/Terminal 工具输出抽取为资源键；
    新增 CJK 字面量扫描器（tools/check-cjk-strings.ps1）与存量基线防回流
- **设置体系接入新版 UI（Phase A/B 落地）**：
  - Phase A：`[ProvideProfile]` 注册资源补全（VSPackage.resx 16001-16003），
    设置加入 导入和导出向导 与 VS 配置漫游
  - Phase B：新增 `Settings/UnifiedSettingsSync.cs` 双向同步桥 ——
    旧选项页 OnApply 后经 `ISettingsWriter` 批写 Unified 存储；
    订阅存储变更回写 Instance 并触发热更新；服务键经实测修正为
    `{E3684F31-344E-42EA-9047-B620FDC7AC25}`；服务缺失时 fail-open 降级
  - 已知边界：Unified Settings 引擎对新声明 moniker 的可见性需新版设置 UI 首次枚举后生效，
    桥内置 120s 轮询与旧页 Apply 重试自愈（详见 docs/Settings-UnifiedIntegration-Feasibility.md §9.6）
- **还原点体系修复与加固**：
  - 修复 delete_file 提案的 RollbackAsync 向 RestoreFromBackup 传空路径导致
    批量回滚时已删除文件无法恢复的 P0 缺陷（缓存原始路径，回滚还原至原位）
  - 编辑会话生命周期挂钩：EditAgent 计划流结束调用 EndSession 回收空备份目录
  - 新增保留期策略：启动后台清扫超过 14 天的历史备份会话目录
    （跳过活跃会话；BackupService.BaseDirOverride 支持测试注入）
- **VS 工具接入（反馈④ 首批两项落地）**：
  - 调试器断点快照：中断态自动捕获栈帧函数/位置/局部变量注入消息上下文
    （只读有界、逐项容错、无编辑器视图亦可独立注入）
  - get_errors 合并 Error List 实时结构化条目（全量枚举上限 200），
    构建输出为空时仍可返回 IDE 分析器诊断
  - 移除零使用的 Microsoft.VisualStudio.Workspace 包引用（VSIX 瘦身）
- **快捷键映射补全**：新增全局 `Ctrl+Shift+D` 打开/聚焦聊天窗口
  （`Ctrl+I` 行内编辑保持不变；均可经 工具 → 选项 → 环境 → 键盘 调整）
- **彻底移除 emoji**：产品源码（C#/XAML/内置 HTML/CSS/JS）与中英文本地化资源中的全部
  emoji / 装饰符号清除或替换为文本；工具结果的 `❌`/`⏱️`/`⛔` 前缀契约迁移为
  `Error:` / `Timeout:` / `[BLOCKED]` 文本标记（`ToolExecutionOutcome.Classify`
  唯一解析点同步更新）；WebView2 渲染层 Emoji→Fluent 兜底映射保留用于模型输出

## [1.2.0] — 2026-08-23（Phase 1.5：Context & Evaluation Driven Refinement）

> 基线 `644068a` (v1.1.14) → 本版本共 **34 个提交**；含上游 `feature/v1.1.15` 全量合入。
> 详细报告：`docs/Phase1.5-Delivery-Report.md`、`docs/Phase1.5-BuildTest-Report.md`

### 新增
- **会话遥测**：每次 Agent 会话自动导出 JSON 指标
  （TTFT / 轮次 / 工具调用明细与耗时 / Token / Cache 命中率 / 失败四分类 /
  终止原因 / context_debug 上下文构成快照）至 `%LocalAppData%\DeepSeekVS\telemetry\`
- **IDE 实时上下文注入**：每条消息自动携带活动文件、光标、选区、光标符号、
  当前文件错误/警告摘要（volatile 注入，不影响 Prefix Cache；可关闭）
- **编辑器 Inline Edit**：选区 → Ctrl+I / 右键菜单 → 指令条 → 单次 LLM 直改 →
  复用 InlineDiffSession 预览管线（Accept/Reject/Esc 取消/原地重试）
- **Context Debugger 抽屉**：聊天窗右上角折叠面板，展示 Token 预算占用、
  注入块标志（IDE/Search/RAG）、Working Set Top-N、活动文件/选区/符号/诊断计数
- **审批模式条目去 emoji**，底部审批行纳入主题适配
- **工具超时分档**：memory 10s / 诊断类 20s / 抓取 45s / 默认 60s；
  新增 LLM 请求超时设置项（默认 300s）
- **跨实例设置迁移**：新 hive 无 ApiKey 时自动从同机其他 VS 实例导入
  （RegLoadAppKey 只读挂载 + 类型前缀解码；一次性执行防覆盖）
- **设置热更新**：选项页保存后 ApiKey/模型/思考模式即时生效，无需重启
- **Benchmark 脚手架**：报告生成器（C#）、标注/报告脚本（PS）、24 任务规程与 schema

### 变更
- 主题默认跟随 IDE，并支持在设置中强制深色/浅色；Auto 模式保留 VSColorTheme 实时订阅
- 工具行图标全部替换为 Segoe Fluent Icons 字形；聊天内容渲染层将
  Emoji 映射为 Fluent 图标或移除（发给模型的数据字节不变）
- 会话历史交互对齐 Copilot：时钟按钮打开历史浮层（当前会话钉顶 +
  相对时间 + 逐条删除），替代常驻下拉框
- 输入区结构修正：上下文按钮移出文本框悬浮位，内边距规范化

### 合入上游 (feature/v1.1.15)
- `capture_window` 窗口截图工具（GraphicsCaptureService）
- PDF 直传视觉模型（PdfRenderService）
- `fetch_webpage` 图片直传视觉 + 递归抓取/长度修复
- FIM 补全在视觉模型下回退 deepseek-v4-flash

### 质量
- 单元测试 942 个全绿（真实 VS2022 vstest 运行）
- VSIX 整包构建通过（VS2026 Roslyn + 合并式 Sdks 工具链，
  见 `tools/build-vs26.ps1`）

### 已知事项
- Unified Settings（新设置 UI）接入完成可行性调研与 Provider 原型，
  正式注册推荐采用 VisualStudio.Extensibility SettingCategory 声明式模型
  （详见 `docs/Settings-UnifiedIntegration-Feasibility.md` §七）
- RAG 冻结中：待 Benchmark cross_file 类 Context 失败数据触发 BM25 立项
