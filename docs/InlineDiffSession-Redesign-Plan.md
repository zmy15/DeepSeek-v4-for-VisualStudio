# InlineDiffSession 改造计划 v2

> 目标：将当前“先写入再预览”（write-then-preview）改造为“先准备、再预览、最后提交”（prepare-preview-commit），并确保直接代码操作、Agent 多文件编辑、已打开文档、未打开文件和新建文件使用同一套决策与提交语义。

---

## 一、设计结论

### 1.1 核心原则

1. 预览阶段不得修改真实 `ITextBuffer`、磁盘文件或项目结构。
2. AI 和编辑工具只能生成或更新暂存内容，不直接写盘。
3. 所有“保留”操作必须经过唯一的 `ProposalCommitCoordinator`。
4. Diff 视图使用冻结的基准缓冲区，不直接把可变化的 `sourceBuffer` 作为显示左侧。
5. 所有差异视图必须只读，避免用户通过 Diff Viewer 绕过提交入口。
6. 已打开文件、未打开文件和新文件分别使用不同的 Commit Target，但共享同一种 Proposal 模型。
7. 多文件“全部保留”必须先完成全量冲突预检，再执行任何写入。
8. UI 宿主与 Session 解耦，先验证稳定宿主，再逐步逼近 Copilot 式编辑器内体验。

### 1.2 目标流程

```text
AI / Code Action / Edit Tool
    ↓
生成 PreparedChangeSet
    ↓
写入 StagedEditWorkspace（仅 Agent 多步工具链）
    ↓
创建 InlineDiffSession
    ├─ baselineDisplayBuffer：冻结的原始内容
    ├─ proposalBuffer：建议内容
    ├─ IDifferenceBuffer：只读
    └─ IWpfDifferenceViewer：Inline
    ↓
用户选择
    ├─ 撤销：关闭 Session，真实文件始终未变
    └─ 保留：ProposalCommitCoordinator
               ↓
             全量预检
               ↓
             BackupService
               ↓
             OpenBufferTarget / FileTarget / NewFileTarget
               ↓
             保存、更新项目、验证
```

### 1.3 非目标

第一版不实现：

- 单个 Hunk 独立保留或撤销。
- 用户直接编辑 Diff Viewer 的右侧内容。
- 自动合并预览期间发生的外部修改。
- 跨文件共享一个 Visual Studio Undo Transaction。
- 完全复刻 GitHub Copilot 的内部编辑器宿主和动画。

---

## 二、需要先解决的架构问题

### 2.1 工具链必须拆成 Prepare 和 Commit

现有 `AbstractEditTool.ApplyAllEditsAsync()` 同时负责：

1. 构造最终内容。
2. 创建备份。
3. 写入磁盘。
4. 更新打开的 VS Buffer。
5. 验证结果。

这与 preview-before-commit 冲突。改造后必须拆成：

```csharp
Task<PreparedChangeBatch> PrepareChangeBatchAsync(...);

Task<BatchCommitResult> CommitPreparedBatchAsync(
    PreparedChangeBatch batch,
    CancellationToken cancellationToken);
```

`PrepareChangeBatchAsync()` 只计算，不写盘；`CommitPreparedBatchAsync()` 只能由 `ProposalCommitCoordinator` 在用户确认后调用。

### 2.2 Agent 多步编辑需要 Staged Workspace

Agent 可能连续调用多个工具，后续工具必须看到前一个工具产生的修改。如果第一步不写盘，第二步直接读取磁盘就会读到旧内容。

因此新增 `StagedEditWorkspace`：

```text
读取文件：
    优先读取 StagedEditWorkspace
    不存在时读取 Buffer 或磁盘，并登记 Baseline

写入文件：
    只更新 StagedEditWorkspace.CurrentContent
    不修改真实 Buffer 或磁盘

Agent 完成：
    BaselineContent vs CurrentContent
    → 生成多文件 PreparedChangeBatch
    → 显示 Diff
```

所有 Agent 编辑工具及其文件读取入口必须使用同一个 Workspace 实例，不能再直接调用 `File.ReadAllText()` / `File.WriteAllText()` 作为主路径。

### 2.3 只能有一个提交入口

以下行为不能同时存在：

- `ProposalApplier` 写入 `sourceBuffer`。
- EditAgent 在用户确认后再次调用工具写盘。

改造后：

```text
InlineDiffSession
    不负责写文件
    ↓
ProposalCommitCoordinator
    唯一提交入口
    ↓
IProposalCommitTarget
```

`InlineDiffSession.CommitAsync()` 只委托给 Coordinator，不自行创建第二套写入逻辑。

---

## 三、核心数据模型

### 3.1 `PreparedChangeSet`

**新文件**：`Models/PreparedChangeSet.cs`

```csharp
public enum ProposedFileOperation
{
    Modify,
    Add,
    Delete,
}

public enum ProposalSaveBehavior
{
    KeepDocumentDirty,
    SaveImmediately,
}

public sealed class PreparedChangeSet
{
    public string ChangeId { get; init; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; init; } = string.Empty;
    public ProposedFileOperation Operation { get; init; }

    public string BaselineText { get; init; } = string.Empty;
    public string BaselineHash { get; init; } = string.Empty;
    public DateTime? BaselineLastWriteTimeUtc { get; init; }

    public string ProposedText { get; init; } = string.Empty;
    public IReadOnlyList<ProposedTextChange> TextChanges { get; init; }
        = Array.Empty<ProposedTextChange>();

    public string ContentTypeName { get; init; } = "code";
    public ProposalSaveBehavior SaveBehavior { get; init; }
}
```

要求：

- `BaselineText` 必须来自生成 Proposal 时实际读取到的内容。
- 对已打开文档，应优先使用 `ITextBuffer.CurrentSnapshot`，不能用磁盘内容覆盖未保存编辑。
- `ProposedText` 必须是完整文档内容。
- 选区替换和光标插入先转换为 `ProposedTextChange`，再应用到 Baseline 得到完整 `ProposedText`。
- `BaselineHash` 用于未打开文件和“修改后又撤回到同样内容”的冲突判断。

### 3.2 `PreparedChangeBatch`

**新文件**：`Models/PreparedChangeBatch.cs`

```csharp
public sealed class PreparedChangeBatch
{
    public string BatchId { get; init; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<PreparedChangeSet> Changes { get; init; }
        = Array.Empty<PreparedChangeSet>();
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}
```

### 3.3 `StagedEditWorkspace`

**新文件**：`Services/Editing/StagedEditWorkspace.cs`

职责：

- 为一次 Agent 编辑会话保存所有文件的 Baseline 和当前暂存内容。
- 后续工具读取时返回暂存版本。
- 记录 Add / Modify / Delete。
- Agent 结束时生成 `PreparedChangeBatch`。
- 用户撤销后直接丢弃整个 Workspace。
- 用户保留后由 Coordinator 提交，Workspace 本身不写盘。

---

## 四、Diff Session 设计

### 4.1 Session 状态机

**新文件**：`Services/InlineDiffSession.cs`

```csharp
public enum InlineDiffSessionState
{
    Created,
    Showing,
    Applying,
    Committed,
    Dismissed,
    Conflicted,
    Failed,
    Disposed,
}
```

```csharp
public sealed class InlineDiffSession : IDisposable
{
    public string SessionId { get; }
    public PreparedChangeSet Change { get; }
    public IProposalCommitTarget CommitTarget { get; }

    // 真实提交目标，仅打开文档时存在
    public ITextBuffer? SourceBuffer { get; }
    public ITextSnapshot? SourceBaselineSnapshot { get; }

    // 只用于显示，均为冻结/临时 Buffer
    public ITextBuffer BaselineDisplayBuffer { get; }
    public ITextBuffer ProposalBuffer { get; }
    public IDifferenceBuffer DifferenceBuffer { get; }
    public IWpfDifferenceViewer Viewer { get; }

    public InlineDiffSessionState State { get; private set; }

    public Task<ApplyResult> CommitAsync(CancellationToken cancellationToken);
    public void Dismiss();
    public void Dispose();
}
```

设计要求：

- `CommitAsync()` 必须幂等，`Applying` 状态下禁止重复点击。
- `Dismiss()` 只能从 `Created` / `Showing` / `Conflicted` 进入 `Dismissed`。
- `Dispose()` 必须取消事件订阅并调用 `Viewer.Close()`。
- Session 不直接保存文件，不直接访问 `BackupService`。
- 事件使用标准 `EventHandler<TEventArgs>`，不使用 `OnCommitted` 命名。

### 4.2 为什么不直接 Diff `sourceBuffer`

Diff 左侧应显示创建 Proposal 时的冻结内容。若直接使用真实 `sourceBuffer`：

- 用户或其他扩展修改 Buffer 后，Diff 左侧会变化。
- Viewer 可能通过可编辑投影间接修改真实 Buffer。
- 预览内容与冲突检测使用的 Baseline 可能不一致。

因此使用：

```text
SourceBuffer             → 仅作为提交目标和冲突检测目标
BaselineDisplayBuffer    → BaselineText 的冻结副本
ProposalBuffer           → ProposedText 的冻结副本
```

---

## 五、DiffViewerService 改造

`Services/DiffViewerService.cs` 从“不改动”调整为“必须改动”。

新增：

```csharp
public DiffViewerHandle CreateReadOnlyPreview(
    string baselineText,
    string proposedText,
    string contentType,
    DifferenceViewMode viewMode = DifferenceViewMode.Inline);
```

返回：

```csharp
public sealed class DiffViewerHandle : IDisposable
{
    public ITextBuffer BaselineBuffer { get; }
    public ITextBuffer ProposalBuffer { get; }
    public IDifferenceBuffer DifferenceBuffer { get; }
    public IWpfDifferenceViewer Viewer { get; }
}
```

创建 Difference Buffer 时必须显式只读：

```csharp
var differenceBuffer = _bufferFactory.CreateDifferenceBuffer(
    baselineBuffer,
    proposalBuffer,
    diffOptions,
    disableEditing: true,
    wrapLeftBuffer: true,
    wrapRightBuffer: true);
```

其他要求：

- 保留现有临时 `ITextDocument` fallback，用于兼容第三方 Margin。
- 临时文件、`ITextDocument` 和 Viewer 的生命周期统一由 `DiffViewerHandle` 管理。
- 关闭宿主时必须调用 `DiffViewerHandle.Dispose()`，不能只关闭 WPF Window。
- 保留原字符串 API 作为兼容包装，但内部委托给新 API。

---

## 六、提交架构

### 6.1 `IProposalCommitTarget`

**新文件**：`Services/Editing/IProposalCommitTarget.cs`

```csharp
public interface IProposalCommitTarget
{
    Task<PreflightResult> PreflightAsync(
        PreparedChangeSet change,
        CancellationToken cancellationToken);

    Task<ApplyResult> CommitAsync(
        PreparedChangeSet change,
        CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
```

实现：

| Target | 场景 | 提交方式 |
|---|---|---|
| `OpenBufferCommitTarget` | 文档已打开 | `ITextEdit` + `ITextUndoTransaction` |
| `FileCommitTarget` | 文档未打开 | 备份 + 保留编码/换行的文件写入 |
| `NewFileCommitTarget` | 新文件 | 接受后才创建文件并加入项目 |
| `DeleteFileCommitTarget` | 删除文件 | 接受后备份并删除 |

### 6.2 Open Buffer 提交

提交前：

1. 当前 Snapshot 与 Baseline Snapshot 相同：允许。
2. Version 不同但文本 Hash 相同：允许。
3. 文本不同：返回 Conflict，不自动覆盖。

提交时：

```csharp
var history = undoRegistry.RegisterHistory(sourceBuffer);
using var transaction = history.CreateTransaction("Apply AI Edit");
using var edit = sourceBuffer.CreateEdit();

// 优先应用结构化 TextChanges；仅兼容路径使用整文件 Replace。
ApplyChangesFromEndToStart(edit, change.TextChanges);

ITextSnapshot appliedSnapshot = edit.Apply();
transaction.Complete();
```

要求：

- 检查 `edit.Apply()` 是否成功。
- 仅在成功后调用 `transaction.Complete()`。
- `KeepDocumentDirty` 不调用 Save。
- `SaveImmediately` 通过 `ITextDocument.Save()` 显式保存。
- 整文件 Replace 仅作为 fallback，不能标注为“从后向前批量应用”。

### 6.3 文件提交

提交前重新读取文件并计算 Hash。Hash 与 Baseline 不一致时返回 Conflict。

提交时：

1. `BackupService.CreateBackup()`。
2. 使用现有编码感知写入逻辑。
3. 写入临时文件后替换目标文件，或继续使用备份保护的可靠写入路径。
4. 验证最终内容。
5. 成功后清理备份，失败则恢复。

### 6.4 `ProposalCommitCoordinator`

**新文件**：`Services/Editing/ProposalCommitCoordinator.cs`

职责：

- 所有单文件和批量“保留”的唯一入口。
- 先对整个 Batch 执行 Preflight。
- 任一文件冲突时，默认一个都不提交。
- Preflight 全部成功后才开始 Backup 和 Commit。
- 中途失败时对已提交目标执行 best-effort rollback。
- 返回逐文件结果，不静默部分成功。

```csharp
public Task<BatchApplyResult> CommitBatchAsync(
    PreparedChangeBatch batch,
    CancellationToken cancellationToken);
```

---

## 七、Session Manager

**新文件**：`Services/InlineDiffSessionManager.cs`

活动文档使用 `ITextBuffer` 作为主键：

```csharp
private readonly Dictionary<ITextBuffer, InlineDiffSession> _activeByBuffer;
```

未打开文件使用规范化绝对路径：

```csharp
private readonly Dictionary<string, PreparedChangeSet> _pendingByPath;
```

原因：

- 未保存文档可能没有路径。
- 文件可能被重命名。
- 同一个 Buffer 可能有多个 View。
- 路径只能作为文件身份，不能代替 Buffer 身份。

公开 API：

```csharp
Task<InlineDiffSession> CreateSessionAsync(
    PreparedChangeSet change,
    CancellationToken cancellationToken);

bool TryGetSession(ITextBuffer buffer, out InlineDiffSession session);
Task<BatchApplyResult> AcceptAllAsync(CancellationToken cancellationToken);
void DismissAll();
```

单文档已有 Session 时必须明确策略：

- 默认拒绝新 Session 并提示用户先处理旧 Proposal。
- Agent 同一 Batch 内的后续修改应更新 `StagedEditWorkspace`，而不是创建多个 Session。

所有 Manager UI 操作固定在 VS UI 线程执行。

---

## 八、UI 宿主

### 8.1 先增加 Phase 0 技术验证

在正式重构前做一个最小 POC，验证：

1. 只读 Difference Buffer 可正常显示。
2. `IWpfDifferenceViewer.VisualElement` 可在目标宿主中正确测量、聚焦和滚动。
3. 关闭宿主后 Viewer、临时文件和事件全部释放。
4. FileEncoding 等第三方扩展不会导致 Viewer 创建失败。
5. VS 深色/浅色主题正常。

### 8.2 宿主抽象

```csharp
public interface IDiffHost
{
    void Show(InlineDiffSession session);
    void Activate();
    void Close();
}
```

实现顺序：

1. `FloatingWindowDiffHost`：复用现有 `DiffViewerWindow`，作为第一阶段稳定兜底。
2. `ToolWindowDiffHost` 或 `DocumentTabDiffHost`：作为正式可用宿主。
3. `EditorOverlayDiffHost`：后续尝试接近 Copilot 的编辑器内体验。

`InlineDiffHostControl` 只负责布局，不决定挂载位置：

```text
Grid
├─ 工具栏：上一处 / 下一处 / Inline / SideBySide
├─ IWpfDifferenceViewer.VisualElement
└─ 状态栏：文件名、差异统计、冲突状态、保留 / 撤销
```

现有 `DiffPreviewAdornment` 暂时只作为状态工具条，不承载完整 Viewer。

---

## 九、调用链改造

### 9.1 Direct Code Action

`DeepSeekChatControl.CodeActions.cs`：

- 不再执行 `selection.Text = newCode` 或立即 `ITextEdit.Replace()`。
- 捕获当前 `ITextSnapshot` 和选区。
- 根据操作生成结构化 `ProposedTextChange`。
- 将 TextChange 应用到 Baseline，生成完整 `ProposedText`。
- 创建 Session。
- 用户接受后按原行为选择 Keep Dirty 或 Save Immediately。

### 9.2 TerminalWindowHelper

拆分：

```text
PrepareCodeChangeAsync()
    → PreparedChangeSet

CommitCodeChangeAsync()
    → 仅供 ProposalCommitCoordinator 调用
```

旧 `WriteCodeToFileAsync()` 保留兼容入口，但新的 preview 流程不能先调用它。

`RegisterPendingDiff` 的语义改为“登记尚未提交的 Proposal”，不再表示“文件已写入后的通知”。

### 9.3 EditAgent

移除：

- `SuppressDiffPreview`。
- “工具已写盘，最后再显示 Diff”的路径。

新增：

```text
创建 StagedEditWorkspace
    ↓
所有编辑工具读取/写入 StagedEditWorkspace
    ↓
Agent 完成后生成 PreparedChangeBatch
    ↓
创建多文件 Session
    ↓
用户保留
    ↓
ProposalCommitCoordinator.CommitBatchAsync()
```

如果 Agent 后续工具依赖前一步修改，必须从 Workspace 读取暂存内容。

### 9.4 EditorDiffMarkerService

保留为兼容门面，但内部委托给：

- `InlineDiffSessionManager`
- `IDiffHost`
- `ProposalCommitCoordinator`

旧 API 可暂时保留，但 `originalContent` 不能被静默忽略。若传入的 original 与当前 Buffer 不一致，必须返回冲突或创建基于 original 的冻结 Proposal，不能直接覆盖未保存编辑。

---

## 十、受影响文件

### 10.1 新建

| 文件 | 说明 |
|---|---|
| `Models/PreparedChangeSet.cs` | 单文件 Proposal |
| `Models/PreparedChangeBatch.cs` | 多文件 Proposal |
| `Services/Editing/StagedEditWorkspace.cs` | Agent 内存暂存文件系统 |
| `Services/Editing/IProposalCommitTarget.cs` | 提交目标接口 |
| `Services/Editing/OpenBufferCommitTarget.cs` | 已打开文档提交 |
| `Services/Editing/FileCommitTarget.cs` | 未打开文件提交 |
| `Services/Editing/NewFileCommitTarget.cs` | 新文件提交 |
| `Services/Editing/ProposalCommitCoordinator.cs` | 唯一提交协调器 |
| `Services/InlineDiffSession.cs` | Session 状态与 Viewer 生命周期 |
| `Services/InlineDiffSessionManager.cs` | Session 管理 |
| `View/InlineDiffHostControl.xaml/.cs` | 可复用 Diff UI |
| `View/Hosts/IDiffHost.cs` | 宿主抽象 |

### 10.2 必须修改

| 文件 | 主要改动 |
|---|---|
| `Services/DiffViewerService.cs` | 新增只读 Diff API 和 `DiffViewerHandle` |
| `Services/EditTools/AbstractEditTool.cs` | Prepare / Commit 分离 |
| 各具体 Edit Tool | 改为读取/更新 Staged Workspace |
| `Services/Agents/EditAgent.cs` | 使用 Workspace，用户确认后统一提交 |
| `ToolWindows/TerminalWindowHelper.cs` | Prepare 与 Commit 分离 |
| `Services/EditorDiffMarkerService.cs` | 改为兼容门面 |
| `View/DeepSeekChatControl.CodeActions.cs` | 选区/全文变更先生成 Proposal |
| `View/DeepSeekChatControl.Events.cs` | 异步批量接受与结果展示 |
| `View/DiffViewerWindow.xaml/.cs` | 包装 `InlineDiffHostControl`，正确 Dispose Viewer |
| `View/DiffPreviewAdornment.cs` | 适配 Session 状态事件 |
| `CodeCompletion/DiffPreviewAdornmentFactory.cs` | 适配新 Manager |

### 10.3 原则上不修改

| 文件 | 说明 |
|---|---|
| `Services/BackupService.cs` | 保留备份能力，由 Coordinator 调用 |
| `Services/CodeDiffService.cs` | 可继续用于统计 |
| `Settings/DeepSeekOptionsPage.cs` | 现有 Diff 设置保持兼容 |

---

## 十一、实施阶段

### Phase 0：API 与宿主 POC

- 验证只读 `IDifferenceBuffer`。
- 验证真实 VS 环境中的 Viewer 创建、关闭和第三方扩展兼容性。
- 确定第一版正式宿主。

完成门槛：没有稳定宿主前，不进入大规模调用链重构。

### Phase 1：Proposal 模型与直接代码操作

- 实现 `PreparedChangeSet` / `PreparedChangeBatch`。
- 实现选区、光标插入和全文替换的 Proposal Builder。
- Direct Code Action 改为 preview-before-commit。

### Phase 2：DiffViewerService 与 Session

- 实现 `DiffViewerHandle`。
- 显式禁用 Diff 编辑。
- 实现 Session 状态机、Manager 和 Dispose。
- 先使用 Floating Window Host 验证完整流程。

### Phase 3：Commit Target 与 Coordinator

- 实现 Open Buffer / File / New File Target。
- 明确 Save Behavior。
- 实现冲突预检和 Batch 提交。
- 接入 BackupService。

### Phase 4：Agent Staged Workspace

- 编辑工具读取/写入 Workspace。
- `AbstractEditTool` 拆分 Prepare / Commit。
- Agent 最终生成 Batch，不提前写盘。
- 处理同文件连续多次编辑。

### Phase 5：正式 UI 宿主

- 抽取 `InlineDiffHostControl`。
- 实现选定的 Tool Window 或 Document Tab Host。
- 保留 Floating Window fallback。
- 适配全局保留/撤销和状态栏。

### Phase 6：测试、迁移与清理

- 删除旧 write-then-preview 主路径。
- 保留必要的兼容 API，并标记过期。
- 更新日志、资源和文档。
- 完成回归测试和 Experimental Instance 验证。

原计划中“调用方改造 3 天、工具层不变”的估算不再适用。Agent Staged Workspace 和工具 Prepare/Commit 分离应单独估时。

---

## 十二、测试策略

### 12.1 单元测试

通过以下抽象避免在普通测试进程中直接创建 VS WPF Viewer：

- `IDiffViewerFactory`
- `IProposalCommitTarget`
- `IDiffHost`
- `IUiThreadDispatcher`
- `IFileContentStore`

覆盖：

- Session 状态迁移和幂等。
- 选区替换生成完整 ProposedText。
- 冲突检测。
- Batch 全量 Preflight。
- 任一冲突时零提交。
- Agent 连续编辑读取暂存内容。
- Reject 后 Workspace 和真实文件均不变。

### 12.2 VS 集成测试

在 Experimental Instance 验证：

- `IWpfDifferenceViewer` 创建和关闭。
- Inline / SideBySide 切换。
- Undo Transaction。
- `ITextDocument.Save()`。
- 文档关闭、重命名和分屏。
- 深色/浅色主题。
- FileEncoding 等第三方扩展兼容性。

### 12.3 端到端场景

1. 已打开文件全文替换，保留后一步 Ctrl+Z。
2. 已打开文件选区替换，不覆盖选区外内容。
3. 文档存在未保存修改，Proposal 基于当前 Buffer。
4. 预览期间用户修改文档，提交返回 Conflict。
5. 未打开文件接受后写盘。
6. 新文件拒绝后磁盘和项目中均不存在该文件。
7. 多文件 Accept All 中有一个冲突，所有文件均不提交。
8. Agent 对同一文件连续编辑，后一步能看到前一步暂存结果。
9. 关闭 Diff Host 后无 Viewer、事件和临时文件泄漏。

---

## 十三、验收标准

1. 预览阶段真实 Buffer、磁盘和项目结构均不变化。
2. Diff Viewer 左右两侧不可编辑。
3. 选区、插入和全文替换都生成完整、正确的 ProposedText。
4. 用户撤销只关闭 Session，不执行反向文件写入。
5. 用户保留只经过 `ProposalCommitCoordinator`。
6. 已打开文档提交支持一步 Ctrl+Z。
7. Save Behavior 与调用场景一致，不依赖“自动落盘”假设。
8. 未打开文件、新文件和删除文件具备明确提交与撤销语义。
9. 多文件提交先全量预检，冲突时零提交。
10. Agent 连续工具调用共享 Staged Workspace，不提前写盘。
11. Session 关闭后 Viewer、事件、临时文件全部释放。
12. Floating Window fallback 始终可用。
13. 所有现有测试通过，并新增 Proposal、Commit 和 VS Viewer 测试。

---

## 十四、最终架构

```text
                    ┌──────────────────────┐
                    │ Code Action / Agent  │
                    └──────────┬───────────┘
                               │
                     PrepareChangeBatch
                               │
                    ┌──────────▼───────────┐
                    │ PreparedChangeBatch  │
                    └──────────┬───────────┘
                               │
                    ┌──────────▼───────────┐
                    │ InlineDiffSession(s) │
                    │ read-only preview    │
                    └───────┬────────┬─────┘
                            │        │
                         Dismiss   Accept
                            │        │
                            │   Preflight All
                            │        │
                            │   Backup + Commit
                            │        │
                            │  ┌─────▼──────────────┐
                            │  │ Commit Target      │
                            │  ├────────────────────┤
                            │  │ Open Buffer        │
                            │  │ Existing File      │
                            │  │ New/Delete File    │
                            │  └────────────────────┘
                            │
                       No real changes
```

核心边界：

- `PreparedChangeSet` 描述“想改什么”。
- `StagedEditWorkspace` 让 Agent 在不写盘的情况下连续工作。
- `InlineDiffSession` 负责“展示和收集决策”。
- `ProposalCommitCoordinator` 负责“唯一、可验证、可回滚地提交”。
- `DiffViewerService` 负责“创建只读的 VS 原生 Diff Viewer”。
