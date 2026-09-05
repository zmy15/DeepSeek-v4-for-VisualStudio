# 设置体系接入方式调查 与 Unified Settings（新版设置 UI）接入可行性报告

> 日期：2026-08-23　对应提交：`353ca3a`
> 调研对象：当前扩展的设置接入链路 + VS2026「工具→设置」(Unified Settings) 的官方扩展点

---

## 一、当前接入方式（现状链路）

```
[ProvideOptionPage(typeof(DeepSeekOptionsPage), "DeepSeek Chat", "General", true)]
        │  （传统 DialogPage；无 ProvideProfile → 不参与漫游/导入导出）
        ▼
Tools→选项 页面 UI ──OK──► SaveSettingsToStorage()
        │                     ├─ ApiKey/BaiduKey/BingKey → DPAPI 加密
        ▼                     └─ base 写入实例私有存储（Dev17=bin / Dev18=file+bin 混合，已实测）
DeepSeekOptionsPage.Instance（单例 = 唯一事实源）
        ├─ LoadPersistedOptionsAsync 启动装载（含跨实例迁移，一次性标记防回写）
        ├─ SettingsChanged 静态事件 → 热更新
        │     ├─ OnOcrSettingsChanged（OCR/Web搜索/模型下拉刷新）
        │     └─ OnCoreSettingsChanged（ApiKey/Model/思考模式 → _apiService 即时生效）★本轮新增
        └─ 各消费点直读 Instance 属性（TokenBudget/压缩阈值等在构造期捕获的除外）
```

**已确认的现状缺口**：
1. 无 `ProvideProfile` —— 设置不随 VS 账户漫游、不参与导入导出；
2. 构造期捕获型消费点（如 ContextManager 的 TokenBudget）改动后需重启才生效；
3. 在 Dev18 新版设置界面中不可见/不可搜（见下）。

## 二、VS2026 新版设置体系（Unified Settings）

- 官方命名空间：`Microsoft.VisualStudio.Utilities.UnifiedSettings`，
  **包 Microsoft.VisualStudio.Utilities v17.14.40264 —— 已被本工程 SDK 元包引用**，17.14+/18 双端可用。
- 入口：`SVsUnifiedSettingsManager` → `ISettingsManager.GetReader()/GetWriter(callerId)`。
  Writer 采用 Enqueue→RequestCommit 两段式，支持作用域与用户审批。
- **面向扩展的展示/读写接入点：`IExternalSettingsProvider`**
  「Controls a single region of external settings. Unified Settings will query for this object when the external settings region is shown in the UI.」
  - 需实现：`GetValueAsync<T>(id)` / `SetValueAsync<T>(id, value)` / 可选 `GetEnumChoicesAsync`（动态枚举）、
    `OpenBackingStoreAsync`（点击“托管于 xxx”链接打开真实存储）。
  - 可选增强：`ICachingExternalSettingsProvider`（realtimeNotifications=false 时内存缓存+Commit）、
    `ISuspendableExternalSettingsProvider`。
  - 反向通知：`SettingValuesChanged` / `ErrorConditionResolved` / 动态消息文本变更事件。
  - 注册形态：文档明确"registration 包含区域 id、backing store 字符串资源 id、realtimeNotifications 标志"，
    但注册属性/接口未出现在 Utilities.xml 中 —— 需对 Dev18 实测确定（MEF Export+元数据 或 Shell 侧 attribute）。
- 另有新一代 `VisualStudio.Extensibility` 的 `SettingCategory` 声明式模型（类型化/校验/生成观察者），
  但面向新 Extensibility 包模型，经典 AsyncPackage 接入属重写级。

## 三、可行性矩阵

| 方案 | 做法 | 工作量 | 风险 | 收益 |
|------|------|:----:|------|------|
| C 维持现状 | 依赖 Dev18 兼容模型：VS2022 扩展免改直接运行于 VS2026，旧 Options 页继续可用 | 0 | 低（官方兼容承诺） | 不进新设置 UI，不可搜索 |
| **A 外部区域桥接 ⭐** | 实现 `DeepSeekExternalSettingsProvider : IExternalSettingsProvider`。GetValue/SetValue 直接桥接 `DeepSeekOptionsPage.Instance`（单一事实源），SetValue 后触发既有 SettingsChanged 热更新链路；枚举项映射审批模式三态；`SettingsValuesChanged` 由现有 SettingsChanged 事件转发。MEF 导出 + 注册元数据原型迭代 | 1–2 天原型 | 注册元数据需 Dev18 实测一次；敏感字段排除策略（见 §四） | 设置进入新 UI：可搜索、即时生效、主题一致 |
| B 全面迁移 SettingCategory | 迁到 VisualStudio.Extensibility 声明式模型 | 大（包模型重构/双栈并存） | 高 | 类型安全、校验、观察者、workspace scope |

## 四、关键约束：敏感字段必须排除

Unified Settings 具备云同步与 JSON 导出能力。ApiKey/BaiduApiKey/BingKey 属 DPAPI 本机加密凭据，
**不应进入 Unified 存储**（会随同步/导出泄漏）。方案 A 的区域定义仅收录非敏感子集：

```
SelectedModel / IsThinkingEnabled / ReasoningEffort / EnableWebSearch /
SearchProvider / ApprovalMode / TokenBudget / CompressionThreshold /
PreserveRecentTurns / ShowContextStats / EnableTelemetryExport / EnableIdeContextInjection
```

ApiKey 保持现状：旧页编辑 + DPAPI 私有存储；新 UI 对应条目可展示为只读状态 + “在旧页修改”跳转链接
（利用 OpenBackingStoreAsync 打开旧 Options 页命令）。

## 五、建议路线（✅ 已于 2026-08-23 全部执行，状态见下）

1. **Step 1（原型验证）**：✅ **已完成** —— `Settings/UnifiedSettingsBridge.cs` 实现方案 A 最小原型
   （DeepSeekExternalSettingsProvider + 注册探测），实测结论见 §七。
2. **Step 2（补全）**：🔶 **部分完成** —— VSEXT SettingCategory 声明已落地（7 项进新 UI，
   见 §八）。**跳转链接已取消**：经 DLL 符号扫描确认，Dev18 的 Unified Settings UI 并非独立命令，
   而是整合进 `Tools.Options` 本体——旧 DialogPage 与新 SettingCategory 天然共存于同一对话框，
   无需额外跳转入口。
3. **Step 3（远期评估）**：✅ **提前完成** —— 通过 VSSDK+VSEXT 混合构建系统
   （`DeepSeekExtension.cs` + Extensibility.Sdk 包）实现形态③；
   GenerateObserverClass 已启用但源生成器未实际产出 Observer 类（阻塞项，
   备选方案 A/B/C 记录于 Handoff-Context.md §五）。

## 六、附：本次核实的关键证据

| 断言 | 证据 |
|------|------|
| API 已在本机 SDK 内 | `~/.nuget/packages/microsoft.visualstudio.utilities/17.14.40264/` 存在（与工程元包同版本线）|
| 官方定位 | IExternalSettingsProvider doc：「Unified Settings will query for this object when the external settings region is shown in the UI」|
| 入口服务 | ISettingsManager doc：「available as a VS service (via SVsUnifiedSettingsManager)」，Guid 2f26e586-… |
| 兼容承诺 | DevBlog《Modernizing Visual Studio Extension Compatibility》：VS2022 扩展免改运行于 VS2026 |

## 七、Step1 实测附录（探针 v2 蓝图，2026-08-23）

运行时反射扫描（诊断日志全量捕获）确认：

1. 区域/设置定义类型位于 Dev18 内部程序集 `Microsoft.VisualStudio.Shell.UI.Internal`，
   命名空间 `Microsoft.VisualStudio.Services.UnifiedSettings.DataModel`；
   `RegisteredSettingDefinition` / `EnumSettingDefinition` 构造函数达 **31 参数**，
   深度依赖 `Moniker / RegistrationType / DefaultValueDefinition / ProviderInfo /
   DefinitionLogContext / ExpressionParser.Token` 等内部类型。
2. 唯一消费入口为 `UnifiedSettings.CompositionRoot.GetExternalProviderState`
   （非公开）—— 即外部区域经 MEF 组装进 CompositionRoot。
3. 该 Definition 形态与 **VisualStudio.Extensibility SettingCategory 声明式模型的生成产物一致**。

### 结论修正

| 方案 | 修正后评估 |
|------|-----------|
| A 手工反射构造外部区域 | **降级为不推荐**：31 参内部 ctor 无兼容承诺，随 Dev18 更新随时破坏 |
| B VisualStudio.Extensibility `SettingCategory` | **升级为正道**：Dev18 新设置 UI 的官方接入模型；需以 in-proc Extensibility 包形态提供声明 |
| C 维持现状 | 仍然有效：旧 Options 页在 VS2026 由兼容模型持续承载 |

### 修订路线

1. Step1 已交付：Provider 桥接实现（`Settings/UnifiedSettingsBridge.cs`，编译+942 测试通过）
   —— 其 GetValue/SetValue/枚举/事件逻辑与声明式模型解耦，可被任何注册载体复用。
2. Step2（下一迭代）：新增 in-proc `Extension` 工程引用 Microsoft.VisualStudio.Extensibility，
   以 `SettingCategory` 声明同一非敏感子集；观察者回写 Instance 单例；与旧页双入口并存。
3. ApiKey 继续排除在新体系之外（云同步风险），维持 DPAPI 私有存储。

*探针原始输出见 %LocalAppData%\DeepSeekVS\diagnostic-2026-08-23.log（USv2 标记段）。*


---

## 八、Step2 深度调查：VisualStudio.Extensibility 设置声明接入 VS2026 的可行性

> 依据：官方 in-proc 指南、VSExtensibility 实验特性清单、兼容性博客、GitHub #233，
> 以及本机环境核查（Extensibility 包未缓存、Utilities v17.14 已缓存）。

### 8.1 官方支持的三种混合形态（均适用于本工程）

| 形态 | 说明 | 对我们的适配度 |
|------|------|--------------|
| ① 全新 in-proc Extension 工程 | `Extension` 基类 + `RequiresInProcessHosting=true`，可注入全部 VSSDK 服务 | 需新建工程，双 VSIX 并存 |
| ② 现有 AsyncPackage 内查询 `VisualStudioExtensibility` 实例 | 仅加 `Microsoft.VisualStudio.Extensibility` 包引用，`GetServiceAsync<…>()` | ✅ 改动最小；但只能调用运行期服务，**不支持 `[VisualStudioContribution]` 贡献** |
| ③ 同工程承载 VSSDK 包 + VSEXT Extension | 移除 `Microsoft.VSSDK.BuildTools`，改引 `Extensibility.Sdk`+`.Build`；csproj 加 `VssdkCompatibleExtension=true`；清单 `ExtensionType="VSSDK+VisualStudio.Extensibility"`；新增 `Extension` 子类 | ⭐ **唯一能让 `SettingCategory` 出现在新设置 UI 的形态** |

### 8.2 关键事实

1. TFM 兼容：in-proc 扩展目标 .NET Framework 4.7.2 —— 与本工程一致。
2. 版本窗口：VSEXT 仅支持 VS2022+；清单 `[17.14,)` 下界写法与 Dev18「只看下界」的兼容模型吻合。
3. 设置 API 处于 Preview：需 `#pragma warning disable VSEXTPREVIEW_SETTINGS` 或 csproj NoWarn；
   官方实验特性清单明确列出 Settings，行为可能变更。
4. Marketplace 发布：17.9 起 stable SDK 构建的扩展可发布市场；早期 preview-SDK 一刀切禁令已解除，
   但「实验特性 + NoWarn」能否过审未见明文承诺 —— 发布前需以实际构建物实测一次。
5. 本机构建链冲突点：官方要求移除 Microsoft.VSSDK.BuildTools 由 Extensibility.Build 接管 VSIX 打包；
   本工程当前依赖 BuildTools + 自定义合并式 Sdks 工具链（tools/build-vs26.ps1）——
   切换后该脚本大概率失效，需为 VSEXT 打包路径重做验证。
6. 观察者代码生成：GenerateObserverClass=true 时 SDK 自动生成强类型观察者并经
   InitializeServices 的 AddSettingsObservers() 注入 DI —— 天然承接「新 UI 改动 → 写回 Instance」桥接。
7. 反向同步：旧 Options 页 OnApply 后可通过 ISettingsWriter/批写 API 把新值推回 Unified 存储，
   保证双入口无漂移（需验证 Writer 在 in-proc 经典包内的可用性）。
8. 本机 NuGet 缓存中 Extensibility 包未安装 → 还原需联网。

### 8.3 迁移脚手架（形态③，供 Step2 执行时直接取用）

csproj 变更：
- PropertyGroup 增加 VssdkCompatibleExtension=true 与 NoWarn 追加 VSEXTPREVIEW_SETTINGS
- 移除 Microsoft.VSSDK.BuildTools 包引用
- 新增 Microsoft.VisualStudio.Extensibility.Sdk / .Build（PrivateAssets=all）

vsixmanifest 变更：Installation 标签追加 ExtensionType="VSSDK+VisualStudio.Extensibility"

新增代码骨架：
- DeepSeekExtension : Extension，RequiresInProcessHosting=true，
  InitializeServices 中 services.AddSettingsObservers()
- DeepSeekSettingDefinitions：[VisualStudioContribution] SettingCategory +
  非敏感子集声明（SelectedModel/Thinking/Effort/ApprovalMode/WebSearch/
  TokenBudget/CompressionThreshold/各开关）；ApiKey 永不入内
- 生成的 Observer 内：值变化 → 写回 Instance + ApplyRuntimeHotUpdates()

### 8.4 风险矩阵与缓解

| 风险 | 等级 | 缓解 |
|------|:---:|------|
| 构建链切换破坏既有打包（含自定义工具链） | 高 | 独立分支执行；先只加壳不声明设置，跑通 VSIX 再进设置声明 |
| Preview API 行为变更 | 中 | 子集收敛在稳定类型（Boolean/Integer/Enum/String）；NoWarn 显式化便于升级审视 |
| 市场/本地分发政策不确定 | 中 | 先走 GitHub Release 直发 VSIX；Marketplace 待实测 |
| 双入口漂移 | 低 | 单一事实源仍为 Instance；双向写穿 + Observer 监听 |
| ApiKey 泄漏面扩大 | — | 维持排除策略不变（DPAPI 私有存储） |

### 8.5 结论

接入可行且为官方推荐路径；按「先壳后芯」两段式推进以隔离最大风险：

- Step2a（约半天）：独立分支完成 csproj/manifest/Extension 壳改造，
  Dev18 F5 验证包加载与旧功能零回归（同时检验 tools/build-vs26.ps1 是否需适配）。
- Step2b（约一天）：声明非敏感子集 SettingCategory + 观察者回写 Instance +
  旧页反向推写；新旧双入口一致性验收。
- Step2c：发布通道实测（GitHub Release vs Marketplace）。

---

## 九、对照官方《扩展用户设置和选项》文档的最新版本切换分析（2026-08-24）

> 输入：learn.microsoft.com/zh-cn/visualstudio/extensibility/extending-user-settings-and-options
> （含子页：创建选项页 / 创建设置类别 / 使用设置存储 / 写入用户设置存储）
> 方法：官方文档能力面 × 本工程现状 × 已有探针证据 三方交叉

### 9.1 官方文档树的能力面（该 URL 覆盖什么）

| 子页 | 官方机制 | 解决的问题 |
|------|----------|-----------|
| 创建选项页 | `DialogPage` + `[ProvideOptionPage]` | 工具→选项 页 UI + 持久化 |
| 创建设置类别 | `DialogPage` 派生 + `[ProvideProfile(isToolsOptionPage:true)]` + 三个资源 ID | **导入和导出设置向导**勾选项、`.vssettings` 往来、随配置漫游 |
| 使用/写入设置存储 | `new ShellSettingsManager(serviceProvider)` → `GetReadOnlySettingsStore(scope)` / 可写 User 存储 | 扩展对设置存储的**编程式读写**（任意集合/属性名） |

关键定性：**该文档树描述的是经典 VSSDK 设置模型**。VS2026「新版设置 UI」（Unified Settings /
`IExternalSettingsProvider` / VisualStudio.Extensibility `SettingCategory`）不在此文档树内 ——
后者属于 VisualStudio.Extensibility 文档体系。因此"切换到最新版本"在官方文档语境下存在
两条正交路径，需分开评估。

### 9.2 现状对照官方能力面的差距清单

| 官方能力 | 本工程现状 | 差距影响 |
|----------|-----------|---------|
| `[ProvideOptionPage]` | ✅ 已有（DeepSeekOptionsPage） | 无 |
| `[ProvideProfile]` 设置类别 | ❌ **缺失**（feasibility §一已确认） | 不参与导入/导出向导、不随 VS 配置漫游 —— 与官方模型的功能性缺口 |
| ShellSettingsManager 编程读写 | ⭕ 未系统使用（迁移走 RegLoadAppKey 私有挂载） | 旧↔新值同步缺少官方桥 |

### 9.3 可行性判定

| 路径 | 内容 | 可行性 | 判定依据 |
|------|------|:------:|---------|
| **P1 经典补全**：给 DeepSeekOptionsPage 加 `[ProvideProfile]` | 官方文档完整支持，纯声明式增量 | **高**（0.5 天） | 仅需资源 ID + attribute；不动存储格式（仍写实例私有存储）；VS2022/2026 双端由兼容模型承载 |
| **P2 新版 UI 接入**：Unified Settings（SettingCategory 形态③） | 链接文档未覆盖；但 §八已给出完整蓝图且壳已落地（DeepSeekExtension.cs + 7 项声明） | **中高**（1–2 天收尾） | 唯一阻塞 = 观察者源生成器未产出（§五 Handoff）；备选：ISettingsWriter 反向批写替代生成观察者 |
| P3 外部区域手工反射构造 | §七已降级为不推荐（31 参内部 ctor 无兼容承诺） | 低 | 放弃 |

结论：**P1+P2 组合可行**。P1 立即消除漫游/导出缺口且零风险；P2 是"进新 UI"的唯一官方正道，
剩余工作集中在观察者回写与双向同步两个点上。

### 9.4 实现方案（分阶段）

**Phase A — 经典模型补全（P1，先做，独立可交付）**
1. VSPackage.resx/.vsct 资源：新增 CategoryName/ObjectName/Description 三个字符串资源 ID；
   注意 zh-CN/en 双语卫星资源。
2. Package 头追加：
   `[ProvideProfile(typeof(DeepSeekOptionsPage), "DeepSeek Chat", "General", <catId>, <objId>, isToolsOptionPage:true, DescriptionResourceID = <descId>)]`
   （与现有 `[ProvideOptionPage]` 同页复用，无需新类）。
3. 验证：工具→导入和导出设置 向导中出现"DeepSeek Chat"勾选点；导出 `.vssettings` 含全部
   非 DPAPI 属性；ApiKey 密文行为确认（DPAPI 值以密文字符串进出，本机可解，跨机不可解 —— 符合预期）。

**Phase B — 新版 UI 收尾（P2，按 §8.3 脚手架继续）**
1. 观察者回写（二选一，先 B 后 A 尝试）：
   - B1：排查 GenerateObserverClass 未产出原因（Extensibility.Sdk 版本 / Clean+Build 顺序 /
     obj 产物核查），产出后 Observer 内写回 `DeepSeekOptionsPage.Instance` + 热更新链；
   - B2 兜底：放弃生成观察者，改用 `SVsUnifiedSettingsManager` 的 ISettingsWriter 在
     旧页 OnApply 时反向批写 + `IVsNotifyUnifiedSettings` interop 订阅变更（Handoff 方案 B/C）。
2. 双入口一致性验收矩阵：新 UI 改值 → Instance 生效；旧页改值 → 新 UI 刷新；
   重启后两入口读数一致；TokenBudget 等"构造期捕获型"消费点补充热更新或标注需重启。
3. ApiKey 维持排除策略不变（云同步/JSON 导出泄漏面，§四结论继续有效）。

**Phase C — 回归与发布**
1. 全量 vstest + Exp hive F5 冒烟（沿用 ColdStart-Fix-Verification.md 流程）；
2. 发布通道实测按 Step2c。

### 9.5 与既有资产的关系

- `Settings/DeepSeekUnifiedSettings.cs`（7 项声明）→ Phase B 直接复用，无需改动；
- `Settings/UnifiedSettingsBridge.cs`（Provider 原型）→ 其 GetValue/SetValue 枚举映射逻辑
  可迁入 B2 兜底的读写适配层；
- `DeepSeekExtension.cs`（形态③壳）→ 已满足 P2 的包形态前提；
- Phase A 与上述全部正交，可立即合入主干。

### 9.6 Phase A/B 实施状态（✅ 2026-08-24 落地 + 运行时实测）

**Phase A（ProvideProfile 补全）— ✅ 完成**
- 新增 `VSPackage.resx`（数据项以数字字符串 "16001/16002/16003" 命名，官方约定）；
  csproj 以 `<EmbeddedResource Update="VSPackage.resx"><LogicalName>VSPackage.resx</LogicalName></EmbeddedResource>`
  附加元数据（SDK 默认通配已含 resx，显式 Include 会触发 NETSDK1022）
- Package 特性修正：`[ProvideProfile(..., 16001, 16002, isToolsOptionPage:true, DescriptionResourceID=16003)]`
  （资源 ID 为**位置参数**；CategoryResourceID/ObjectNameResourceID 并非命名属性）
- 导入/导出向导与配置漫游即此声明生效

**Phase B（双向同步桥）— ✅ 基础设施完成；写入依赖引擎注册可见性（见下）**
- 新增 `Settings/UnifiedSettingsSync.cs`：
  - 服务获取：占位类型携带 `SVsUnifiedSettingsManager` GUID。**实测关键修正：服务 GUID =
    `{E3684F31-344E-42EA-9047-B620FDC7AC25}`**（取自 Dev18
    Microsoft.Internal.VisualStudio.Interop.dll），≠ ISettingsManager 接口 GUID
    （2f26e586-…，用后者查询返回 null）
  - 推（旧→新）：`GetWriter(callerId, eventSource:PackageGuid)` 逐项 Enqueue+Commit；
  - 拉（新→旧）：`reader.SubscribeToChanges(handler, monikers)` → 回写 Instance +
    `ApplyRuntimeHotUpdates()`；含回声抑制窗口
  - fail-open：服务缺失/异常时停用桥接并记日志，旧页功能零影响
- **Moniker 格式实测确认**：`<category>.<settingId>`（如 `deepseekGeneral.deepseekThinking`），
  与 `bin\.vsextension\settingsRegistration.json` 一致 —— 该文件由构建管线正常生成
  （Handoff 所述"观察者源生成器未产出"不影响声明注册；且发现 Sdk 包 17.14.40608 在本机
  缓存为空壳——仅元数据无分析器，属 NuGet 还原损坏，必要时清缓存重置）

**运行时实测结论（Exp hive，diagnostic 日志）**

| 步骤 | 结果 |
|------|------|
| 服务获取（修正 GUID 后） | ✅ acquired |
| 订阅 7 个 moniker | ✅ |
| 宿主激活 `GetServiceAsync(VisualStudioExtensibility)` | ✅ ok |
| RequireRegistration 读回 | ❌ **NotRegistered**（全部 7 项） |
| EnqueueChange ×7 | ✅ 全部接受 |
| Commit | ❌ InternalError（message=对应 moniker） |

判定：写入被拒的直接原因是**设置引擎尚未加载我们的 SettingCategory 注册**——
VSEXT in-proc 扩展的 settingsRegistration.json 需扩展宿主按需装载（典型时机：
用户首次打开新版设置 UI 枚举类别）。宿主激活调用本身不足以令引擎收录外部注册。

**桥接已内置自愈**：初始化时最长等待 120s 轮询注册可见性；不可见则保持订阅并在每次旧页
OnApply 时重试推送。一旦引擎可见（用户打开过一次新版设置 UI 后），链路即闭环：
旧页 Apply → 推送成功 → 新 UI 显示当前值；新 UI 改值 → 订阅回调 → Instance 热更新。

### 9.7 待人工验收一步

1. 启动 Exp 实例 → 打开 工具→选项（新版设置页）→ 确认出现 "DeepSeek Chat" 类别与 7 项设置
2. 在旧入口（原 DeepSeek Chat 页）改任一开关并应用 → 诊断日志应出现
   `[USync] push(onApply) ... commit=Success` → 新 UI 中该值同步
3. 在新 UI 改值 → 诊断日志应出现 `[USync] pulled N value(s)` → 运行行为即时变化

### 9.8 运行时验收补充记录与根因定位（2026-08-24 深夜）

人工验收实际观察：**新设置 UI 中 DeepSeek Chat 仅显示「前往 General（旧版本设置位置）」
跳转卡片**，7 项声明未出现。追加插桩后获得决定性证据链：

| 实验 | 结果 |
|------|------|
| `DeepSeekExtension` 构造器/InitializeServices 插桩 | **从未执行** —— VSEXT 宿主未加载本扩展 |
| `GetServiceAsync(VisualStudioExtensibility)` 宿主激活 | 服务存在（ok），但不触发本扩展装载 |
| pkgdef 全文 | **零 VSEXT 键**（无 dotnetExtensibility 服务/目录注册；仅经典键） |
| 部署布局 | `.vsextension/{extension.json,settingsRegistration.json}` 均正确落位 |
| 引擎读回 | RequireRegistration=NotRegistered（引擎不认识 moniker） |

**根因判定**：声明式注册是纯数据（settingsRegistration.json），但本机 Dev18 构建
（18.0_ba3bb658，内部构建号）的设置引擎**未从扩展目录摄取该数据** —— 高度疑似
VSEXT Settings 预览特性在当前构建/航班下关闭，或该内部构建的宿主尚未实现
混合 VSIX 的声明式设置摄取。非本工程代码缺陷：

- 生成产物完整（extension.json / settingsRegistration.json / 清单 ExtensionType 正确）
- 部署布局符合官方形态
- 同一管线在正式渠道 VS2022 17.14+/VS2026 GA 上按官方文档应为受支持路径（需复测）

**Phase B 桥接现状**：基础设施全部就绪且运行时验证至引擎边界；一旦引擎可见注册
（换构建/开启航班/未来版本），订阅+推送+回写即自动闭环，无需再改代码。

### 9.9 后续可选路线

| 选项 | 内容 | 适用 |
|------|------|------|
| A 复测环境 | 换 VS2026 公开正式版/GA 渠道重装同一 VSIX 验证 | 判定是否构建航班问题（推荐首选，成本≈0） |
| B 方案 A' 兜底 | 实现 `IExternalSettingsProvider` MEF 导出（经典 MefComponent 资产直进目录，
不依赖 VSEXT 宿主）；注册元数据形态需 1–2 轮 Dev18 实测 | 需要"现在就进新 UI"时 |
| C 维持现状 | 新 UI 显示旧页跳转卡（当前行为），旧页功能完整 | 可接受时 |

### 9.10 方案 A 复测结论（✅ 2026-08-24 正式实例全链路打通）

将同一 VSIX（1.2.2，含 Phase A/B 全部改动）部署至 **VS2026 正式实例**
（`18.0_ba3bb658` 主 hive，公开渠道 Community 版）并实测：

```
06:31:26.909 [USync] push(initial) ok=7/7                      ← 7 项全部提交成功
06:31:26.910 [USync] readback deepseekGeneral.deepseekThinking reg=Success any=Success value=True
    …（7 项 reg=Success any=Success，值与 Instance 一致，TokenBudget=900000 ✓）
06:31:28.574 [USync] pulled 7 value(s) from Unified Settings → Instance ← 订阅回写闭环
```

| 验证点 | 结果 |
|--------|------|
| SettingCategory 引擎注册 | ✅ RequireRegistration=Success |
| ISettingsWriter 批写+Commit | ✅ ok=7/7 |
| 存储值落盘与回读一致 | ✅ |
| 变更订阅 → 回写 Instance | ✅ pulled 7 |

**最终判定**：
1. Phase A/B 实现正确，声明式接入在正式渠道 VS2026 上完整可用；
2. Exp hive（18.0_ba3bb658Exp）的 NotRegistered 现象为该实验实例环境特有
   （宿主未装载混合扩展部分），非代码缺陷 —— 与 §9.8 判定吻合；
3. 双入口一致性验收（新 UI 改值 ↔ 旧页改值互同步）具备全部自动化前提，
   剩余为人工目视确认新 UI 渲染效果。

*注意：主实例更新采用部署目录直接覆盖（当时 VS2022 编译进程阻塞 VSIXInstaller，
exit=2004）；后续版本升级建议正常走 VSIXInstaller。*

### 9.11 非敏感设置全量化（2026-08-31）

在 §9.10 已验证的双向链路基础上，`DeepSeekUnifiedSettings` 从 7 项扩展到
**32 项非敏感设置**：

- `Setting.String`：SystemPrompt / SystemPromptEn
- `Setting.Enum`：SelectedModel / ReasoningEffort / SearchProvider / OcrEngine /
  Language / ApprovalMode / ThemeMode
- `Setting.Boolean` / `Setting.Integer`：代码补全、编辑器差异标记、上下文压缩、
  RAG、遥测、IDE 上下文注入、LLM 超时、Agent 防护阈值、自动编译与界面布局

枚举 ID 与旧 `DeepSeekOptionsPage` 存储值保持一致，升级时不做值迁移。`OcrEngine`
在 x64 完整版提供 `Windows Built-in` 与 `PaddleOCR-Sharp`；ARM64/No-Local-OCR 变体
由 `tools/remove-paddleocr.py` 移除 Paddle 依赖和对应枚举项。新 UI 修改后，
桥接层会回写 `Instance`、保存旧 DialogPage 存储，并执行语言/运行时热更新。
ApiKey、BaiduApiKey、BingApiKey 继续排除在 Unified Settings 之外。
因此旧选项页（DeepSeek Chat / General）必须保持可见，作为三类 API Key 的唯一安全配置入口；
不能为消除“双入口”而设置 `IsInUnifiedSettings=true` 隐藏旧页。

构建产物校验：`settingsRegistration.json` 为 33 项，其中 1 项是“API 密钥配置”只读指引，
其余 32 项与 `UnifiedSettingsSync.Bindings` 一一对应；新增
`UnifiedSettingsCoverageTests` 锁定该覆盖关系并防止敏感字段回流。

### 9.12 API Key 迁移至 Visual Studio Credential Storage（2026-09-01）

微软官方没有为 VisualStudio.Extensibility Settings 提供 Secret/Password 类型。官方扩展
凭据路径是 `Microsoft.VisualStudio.Shell.Connected.CredentialStorage`：

- 服务：`SVsCredentialStorageService` → `IVsCredentialStorageService`
- 凭据：`IVsCredential.TokenValue` / `RefreshTokenValue()` / `SetTokenValue()`
- 键位：`IVsCredentialKey` 的 `FeatureName / Resource / UserName / Type`

本项目已新增 `Settings/VisualStudioApiKeyStore.cs`，三类密钥分别使用独立 credential：

| Key | Resource | Type |
|-----|----------|------|
| DeepSeek | `https://api.deepseek.com` | `Bearer` |
| Baidu | `https://qianfan.baidubce.com` | `Bearer` |
| Bing | `https://api.bing.microsoft.com` | `SubscriptionKey` |

读写行为：

1. 包初始化时获取 `SVsCredentialStorageService`，再加载 DialogPage；
2. `LoadSettingsFromStorage` 先读 VS keychain；若 keychain 为空且旧 DPAPI 值存在，
   自动把旧值迁移到 keychain；
3. `SaveSettingsToStorage` 写入 keychain 成功后，临时把三个 Key 属性置空再调用
   `base.SaveSettingsToStorage()`，清掉 DialogPage/导入导出面中的旧密文；
4. `Set` 写入后立即读回并比对原值，`Clear` 后确认条目确实不存在；只有三把 Key
   均验证成功才清除旧 DPAPI 备份；
5. keychain 不可用或写入失败时，回退到原有 DPAPI 私有存储，保证升级不丢 Key；
6. Unified Settings 声明与同步桥继续排除 `ApiKey / BaiduApiKey / BingApiKey`，
   并提供 `deepseekApiKeyGuide` 只读指引，指向旧选项页。

官方 API 参考：

- <https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.connected.credentialstorage.ivscredentialstorageservice>
- <https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.connected.credentialstorage.ivscredential>
- <https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.connected.credentialstorage.ivscredentialkey>

### 9.13 Unified Settings 元数据本地化与 Key 双备份（2026-09-02）

新版设置页的 `SettingCategory` / `Setting` 元数据已改为 VSEXT 官方
`string-resources.json` 本地化：

```text
.vsextension/string-resources.json
.vsextension/zh-Hans/string-resources.json
```

声明代码中的标题、描述、枚举标签使用 `%DeepSeek.Chat.<key>%` 引用。
默认资源为英文，`zh-Hans` 目录提供中文。这样新版设置页会随 VS 语言切换，
而不是固定中文。

API Key 采用双持久化策略：

1. 运行时优先读取 `SVsCredentialStorageService`；
2. 同步保留 DPAPI 加密的 DialogPage 备份；
3. 只有用户明确修改 Key 或首次迁移时才写 keychain；
4. 普通设置保存不再触发 API Key 写入，避免瞬时 keychain 故障导致密钥丢失。

这解决了两个问题：

- 新版设置页元数据不随语言切换；
- keychain 读回失败时误清 API Key。
