# 「创建还原点」实现调查与分析报告

> 日期：2026-08-24　范围：编辑链路全部备份/回滚代码 + 本机实测数据
> 结论速览：已实现**文件级还原点**（BackupService，双路径触发），整体设计清晰、
> 测试覆盖良好（30 项）；但存在 **1 个删除回滚失效的 P0 级缺陷** 与 3 项机制性缺口。

---

## 一、总体架构：两套并行的"还原"体系

| | A. BackupService 文件级还原点 | B. StagedEditWorkspace 基线/Diff 撤销 |
|--|--|--|
| 定位 | 编辑工具写盘前的**安全网**（事务性回滚） | 用户可见的 **Diff 预览与逐块撤销**（InlineDiff 体验） |
| 存储 | `%LOCALAPPDATA%\DeepSeekVS\backups\{yyyyMMdd_HHmmss}\{sha256(path)[12]}\{filename}` | 内存 Baseline（StagedEditWorkspace 登记） |
| 触发 | 工具修改文件前自动 | Agent 流开始时创建 Workspace，WriteFile 登记 Baseline |
| 还原 | RestoreFromBackup / RollbackAll | EditorDiffMarkerService.UndoAllChanges / 逐块 Accept-Reject |
| 关系 | **Workspace 存在时不启用**（走 buffer/Baseline）；Workspace 为 null 的静态降级路径才落盘+备份 | 优先通道 |

关键约束（EditAgent.cs L664 注释）：`StagedEditWorkspace` 必须在 AI 工具循环**之前**
初始化——否则工具编辑绕过 Baseline 登记直接落盘，diff 预览无数据。

## 二、BackupService 机制详解

### 2.1 会话模型（进程级单会话）
```
BeginSession()   创建 {timestamp} 目录（幂等：已有活跃会话则复用）
CreateBackup(f)  首次接触某文件时复制原文件 → {session}/{hash12}/{filename}
                 （自动惰性 BeginSession；文件不存在返回 null）
EndSession()     目录为空则删除（产品代码当前无调用点，见问题③）
```

### 2.2 事务流（以 ApplyPatchTool 静态降级路径为例）
```
首触文件 ──► CreateBackup（登记 backups[filePath]）
逐 hunk 应用 ──► 任一失败 ──► RollbackAll(backups) 全量恢复
              └─ 全部成功 ──► CleanupBackup 逐个销毁备份
任何异常 ──► catch 内 RollbackAll ──► rethrow
```
提案-提交路径（FileCommitTarget / DeleteFileCommitTarget）：Preflight（含 SHA256
外部修改校验）→ Commit 时备份 → 写入 → 回读校验 → 成功 Cleanup / 失败 Restore。

## 三、触发路径矩阵

| 入口 | 备份时机 | 还原时机 | 走向 |
|------|----------|----------|------|
| apply_patch（无 Workspace） | 首 hunk 前 | 部分 hunk 失败 / 异常 → 全量回滚；全成功 → 销毁 | A |
| replace/multi_replace/insert_edit/create_file | 写入前 | 同上模式 | A |
| 提案批量提交（InlineDiff 预览确认） | 各 Target.Commit 内 | 单文件失败自恢复；用户 Reject → 不提交 | A |
| delete_file 提案 | 删除前 | Commit 异常自恢复；**RollbackAsync 存在缺陷（见④-P0）** | A |
| 已打开文档（buffer 模式） | 不备份（走 Baseline + textbuffer 编辑器撤销栈） | VS Ctrl+Z / UndoAllChanges | B |

## 四、发现的问题（按严重度）

### 🔴 P0-1 删除文件的 RollbackAsync 静默失效（真实数据丢失风险）
`DeleteFileCommitTarget.RollbackAsync`（L86）：
```csharp
BackupService.RestoreFromBackup("", _backupPath);   // filePath 传空串！
```
`RestoreFromBackup` 内部 `File.Copy(backupPath, "", …)` 抛 ArgumentException →
被捕获后仅记录「请手动恢复」。**后果**：批量提案中先删除文件 A（Commit 成功、
磁盘上文件已消失），后续任一文件失败触发整批回滚时，A 无法被恢复——
违背「Rollback」语义且用户无感知。根因：`RollbackAsync(ct)` 签名不含原始路径，
而实现又未缓存 `change.FilePath`。
对照：`FileCommitTarget.RollbackAsync` 为显式 no-op（成功提交后备份已清理，
无可回滚物），语义自洽。

### 🟡 P1-1 会话生命周期缺失
- 产品代码**没有任何 `EndSession()` 调用点**（仅测试使用）；
- `_currentSessionDir` 进程存活期间持续复用 → 不同解决方案/项目的备份混入同一
  时间戳目录，无法按工作区归档；
- 空时间戳目录仅在 EndSession 清理 → 随进程运行缓慢累积空目录。

### 🟡 P1-2 无保留期策略
失败残留（Restore/Cleanup 抛异常时「备份文件已保留」）与非空会话目录**永久留存**，
无启动清扫、无容量上限。（本机当前 0 个会话目录 = 健康样本，但机制缺失。）

### 🟡 P1-3 用户侧无「还原点管理」入口
还原完全是内部自动行为；用户无法浏览历史备份/手动挑选恢复——这正是 README
「⚠️ 测试阶段，使用前请自行 Git 提交或手动备份」警告存在的根本原因。

### 🟢 P2-1 新建文件缺少「还原为不存在」语义
create_file 场景 `CreateBackup` 返回 null（原文件不存在），写入失败时无从把
半成品文件回退到「不存在」状态（当前靠 create 流程自身的异常顺序保证未写盘）。

### 🟢 P2-2 双轨概念重叠
BackupService（A）与 StagedEditWorkspace.Baseline（B）职责相邻，新贡献者易混淆
「为什么有时有备份有时没有」；建议长期收敛为一套（见 §五）。

## 五、改进路线建议（状态：P0/P1 已于 2026-08-24 落地 ✅）

| 优先级 | 项 | 状态 | 落地说明 |
|:---:|----|:---:|----------|
| P0 | RollbackAsync 修复 | ✅ | DeleteFileCommitTarget 缓存 `_originalPath`，回滚还原至原路径；新增 4 项提交/回滚单测（含 P0 回归用例） |
| P1 | 会话生命周期挂钩 | ✅ | EditAgent 计划流 finally 调用 `BackupService.EndSession()`（空目录回收） |
| P1 | 保留期策略 | ✅ | `CleanupExpiredSessions(14d)` 启动后台清扫；跳过活跃会话；`BaseDirOverride` 支持测试注入 |
| P2 | 还原点管理器 UI | ⏳ 未立项 | 工具窗列出 sessions→files→一键还原 |
| P2 | 新建文件回滚语义 | ⏳ 未立项 | backups 字典增加 CreatedNew 标记，回滚时删除 |
| 远期 | 双轨合一 | ⏳ | 以 StagedEditWorkspace.Baseline 统一承载 |

落地后测试基线：**983/983 通过**（新增 DeleteFileCommitTargetTests 4 项 + 保留期清扫 2 项）。

## 六、测试覆盖现状

`BackupServiceTests`（574 行 / 30 项）覆盖优秀：目录布局、幂等、哈希子目录、
覆盖语义、恢复/清理/回滚全链路、特殊字符与长路径、并发不同文件、空/大文件。
**缺口**：
1. `DeleteFileCommitTarget.RollbackAsync` 无用例（恰为 P0 缺陷所在）；
2. `EndSession` 的产品接线无验证（因产品未接线）；
3. 并发同文件双备份竞争未测（当前 overwrite:true 语义下可接受）。

---
*证据索引：BackupService.cs 全文；ApplyPatchTool.cs L150–235；FileCommitTarget.cs /
DeleteFileCommitTarget.cs 全文；ProposalCommitCoordinator.cs 编排注释；
BackupServiceTests.cs 30 项契约；EditAgent.cs L664 Workspace 先行约束。*
