# 全面测试报告 —— v1.2.2 设置体系接入 + 启动修复 + i18n 治理 + 还原点加固

> 日期：2026-08-24　分支：`feature/v1.2.2-settings-and-fixes`
> 基线：upstream/master (`49b83ff`) → HEAD（81 提交，146 文件，+12083/-4400 行）

---

## 一、测试结果总览

| # | 测试项 | 结果 | 详情 |
|---|--------|:----:|------|
| 1 | 单元回归 | ✅ **983/983** | 含新增 SettingsMigrationTests(9)、IdeContextModelsTests 调试器帧(+4)、DeleteFileCommitTargetTests(+4) |
| 2 | VSIX 构建 | ✅ | 913.1 MB；`.vsextension/`、zh-Hans 卫星 DLL 均正确生成 |
| 3 | 冷启动安静性 | ✅ | 无标记实例纯启动：包不加载、零日志增量、VS 全程响应 |
| 4 | 显式触发全链路 | ✅ | GetDialogPage 50ms；加载链 51ms；push(initial) ok=7/7；窗口 <1s 可用 |
| 5 | UnifiedSettings 注册可见性 | ✅ | `registered form found after 0s: deepseekGeneral.deepseekThinking → Success` |
| 6 | 自动弹窗标记门控 | ✅ | 标记存在时恢复弹出；空白实例保持安静 |
| 7 | CJK 防回流扫描器 | ✅ | No new violations（基线 91 文件） |
| 8 | en/zh 键集一致性 | ✅ | **en = zh = 1564**，missing keys = 0 |
| 9 | JSON 完整性 | ✅ | en.json / zh-CN.json 双文件 ConvertFrom-Json 通过 |

## 二、启动性能对比

| 指标 | 修复前 | 修复后 | 变化 |
|------|--------|--------|------|
| 加载链总耗时 | **18.7s UI 冻结** | **51ms** | **-99.7%** |
| GetDialogPage | 未单独计时 | 50–112ms | 正常范围 |
| 窗口可用时间 | 分钟级（冷实例永久卡死） | <1s | 质变 |
| USv2 探针 | UI 线程同步扫描 | 后台线程延迟执行 / Release 剔除 | 消除 |

## 三、各批次提交清单

### 批次 A：i18n 阶段一止血（`74d9323`）
- 补齐 12 个缺失资源键（消除 `[key]` 泄漏）
- 同步 en/zh 键集 3 处差异
- 新增 tool.symbolSearch.desc、tool.readFile.param.maxLines 等

### 批次 B：还原点 P0 缺陷修复（`5e7f581`）
- DeleteFileCommitTarget.RollbackAsync 空路径修复
- 新增 DeleteFileCommitTargetTests 4 项（含 P0 回归用例）

### 批次 C：还原点 P1 加固（`04649e9`）
- EditAgent 流结束挂钩 EndSession（空目录回收）
- CleanupExpiredSessions(14d) 启动清扫 + baseDirOverride 参数化
- BackupServiceTests 追加保留期测试 2 项

### 批次 D：CI 工作流修复（`3469817`）
- sync-no-paddleocr 重写为 sed 内联移除（消除静态补丁过期问题）
- 删除过期 paddleocr-remove.patch 文件

### 批次 E：ProvideProfile 卫星资源 + CI 扫描器（`7ae3e6f`）
- VSPackage.zh-Hans.resx 中文卫星资源
- CI workflow 追加 CJK 字面量扫描步

## 四、运行时实测证据（Exp 实例最新部署）

```
15:16:00 [DeepSeek Init] All steps completed successfully
15:16:00 [DeepSeek Init] Auto-show (marker present)
15:16:00 [DeepSeek Init] GetDialogPage OK in 50ms
15:16:00 [DeepSeek Init] Persisted options loaded OK in 51ms
15:16:00 [USync] ISettingsManager acquired
15:16:01 [USync] extensibility host activation: ok
15:16:01 [USync] subscribed to 7 setting monikers
15:16:01 [USync] registered form found after 0s: deepseekGeneral.deepseekThinking → Success
15:16:01 [USync] push(initial) ok=7/7
```

关键改善：
- **注册可见性从 NotRegistered → Success**（Exp 实例重新部署后引擎目录已刷新）
- **推送 7/7 成功**（此前因 NotRegistered 全部 InternalError）

## 五、遗留事项

| 优先级 | 项 | 说明 |
|:---:|----|------|
| P2 | Agents 域日志本地化 | FormatLogForThinking 白名单耦合需专项设计（~179 行 CJK） |
| P2 | 还原点管理器 UI | 工具窗列出 sessions→files→一键还原 |
| P2 | 新建文件回滚语义 | backups 字典增加 CreatedNew 标记 |
| 信息 | VSEXT Preview API | SettingCategory 标题运行时切换受 Preview API 限制，已记录 |
| 信息 | ProvideProfile zh-Hans 卫星资源 | MSBuild 已自动生成卫星 DLL；实际效果待中文环境 VS 目视确认 |

## 六、已知限制与注意事项

1. **构建工具链锁定**：必须使用 tools/build-vs26.ps1（VS2026 MSBuild），VS2022 csc 不支持 LangVersion 14
2. **CJK 扫描器基线**：91 文件存量在 baseline 中，新增文件自动触发违规报告（-Enforce 阻断）
3. **PowerShell 自动化规范**：脚本保持纯 ASCII 或带 BOM（GBK 解析事故教训 ×3）
4. **部署方式**：开发阶段用部署目录覆盖；正式升级走 VSIXInstaller（编译进程阻塞时会 exit=2004）
