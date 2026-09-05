using Xunit;

// BackupService（静态会话/备份根目录）、StagedEditWorkspace 磁盘备份等共享进程级静态状态，
// 且现有测试（BackupServiceTests / DeleteFileCommitTargetTests）直接操作备份目录。
// xUnit 默认并行执行不同测试类会引入竞态（如 BaseDirOverride 被并发覆盖、备份计数断言抖动），
// 故程序集级关闭并行，保证测试确定性。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
