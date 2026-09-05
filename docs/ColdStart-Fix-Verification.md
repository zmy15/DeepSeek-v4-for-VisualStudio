# 空白实例启动卡死修复 — 验证测试报告

> 日期：2026-08-24
> 关联：`docs/Handoff-Context.md` 用户反馈、CHANGELOG `[Unreleased]`
> 修复提交范围：本次工作区未提交变更（Package.cs / SettingsMigration.cs / ShowChatWindowCommand.cs）

---

## 一、验证结论

| # | 验证项 | 结果 | 证据 |
|---|--------|:----:|------|
| 1 | 单元回归（含 9 项新增迁移测试） | ✅ | **951/951 通过**（vstest 真实运行，7.55s） |
| 2 | VSIX 打包 + 标准静默安装 | ✅ | `VSIXInstaller /quiet /rootSuffix:Exp` → **Exit=0**（197s，双 Exp hive 落位） |
| 3 | 冷启动安静性（无标记实例） | ✅ | 纯启动 100s：**零扩展活动**（包不加载）、devenv 全程 `Responding=True` |
| 4 | 显式触发全链路耗时 | ✅ | 包初始化 ~170ms；`GetDialogPage OK in 112ms`；**加载链总计 115ms**；窗口 ~1.2s 可用 |
| 5 | 反射探针后台化 | ✅ | `[USv2] background probe start/done in 10620ms` —— 同一 10.6s 扫描已脱离 UI 线程，且延迟 10s 执行 |
| 6 | 显式打开 → 标记写入 | ✅ | `chat-window-opened.flag` 于 Execute 成功时落盘（02:58:14） |
| 7 | 迁移阶段短路 | ✅ | 已迁移 hive 上 `migrated=True` 直接跳过探测（两阶段完整路径由单元测试覆盖） |

**核心对比（修复前 vs 修复后，同一加载链）：**

```
修复前（diagnostic-2026-08-24.log 00:01 会话，热实例）：
  GetDialogPage → Probe.Run + RunConsumptionScan（UI 线程同步）
  00:01:20.497 [USv2] 契约所在程序集
  …… 18.7 秒 UI 线程冻结（VS 整体无响应）……
  00:01:39.232 [USv2] 注册入口候选

修复后（02:58 会话，Exp hive 全新部署）：
  02:58:14.080 Loading persisted options...
  02:58:14.194 GetDialogPage OK in 112ms
  02:58:14.195 Persisted options loaded OK in 115ms   ← 链路总耗时 115ms
  02:58:14.854 ShowToolWindowAsync completed OK        ← 窗口可用
  02:58:23.876 [USv2] background probe start           ← 探针在 UI 线程之外、延迟 10s
  02:58:34.498 background probe done in 10620ms        ← Release 构建中此扫描完全不存在
```

## 二、测试过程记录（标准流程）

### 2.1 构建与单元测试
```powershell
powershell -File tools\build-vs26.ps1     # 主工程 + 测试工程 + vstest
# 结果：VSIX 构建成功；测试总数 951，通过 951，失败 0
```

新增 `DeepSeek_v4_for_VisualStudio.Tests\Unit\Settings\SettingsMigrationTests.cs`（9 项，
临时目录隔离，不触碰真实实例 hive）：

| 测试 | 覆盖点 |
|------|--------|
| Enumerate_ExcludesExpHives_AndMissingBins | Exp 过滤 / 缺 bin 排除 |
| Enumerate_ExcludesOwnHiveByName | 当前 hive 自排除（不再挂载活动注册表） |
| Enumerate_OrdersByLastWriteTime_Descending | 最新来源优先排序 |
| WithTimeout_FastFunc_ReturnsValue | 正常路径 |
| WithTimeout_SlowFunc_AbandonsAndReturnsDefault | 3s 超时兜底（防异常锁定 hive 拖住链路） |
| Probe_NonexistentBaseDir_ReturnsNullWithoutThrow | 容错 |
| Probe_AllCandidatesExcluded_ReturnsNullQuickly | 全排除短路 |
| Probe_InvalidHiveContent_GracefullySkipsAndReturnsNull | RegLoadAppKey 失败优雅跳过 |
| Probe_EmptyBaseDir_ReturnsNull | 空根目录 |

为可测试性做的等价重构：`EnumerateCandidateBins` / `WithTimeoutAsync` 提取为 internal；
`ProbeBestSourceAsync` 增加 `baseDirOverride`（仅测试注入）。

### 2.2 E2E 冒烟（标准插件流程）

```powershell
# 1) 静默安装（A-Phase 同款命令；枚举本机全部 VS 实例的 Exp hive）
VSIXInstaller.exe /quiet /rootSuffix:Exp bin\Debug\net472\DeepSeek_v4_for_VisualStudio.vsix
#    Exit=0；实际落位 <hive>\Extensions\<随机目录>\（18.0_ba3bb658Exp 与 17.0_fc76f596Exp 各一份）

# 2) 场景 A —— 纯冷启动（无触发）
devenv /rootsuffix Exp /log          # PID 由 Start-Process -PassThru 记录
#    t+100s：诊断日志零新增；进程存活且 Responding=True → 安静启动成立

# 3) 场景 B —— 显式触发（DTE 自动化，规避 /command 规范名歧义）
$dte = [Marshal]::GetActiveObject("VisualStudio.DTE.18.0")
$dte.ExecuteCommand("DeepSeek_v4_for_VisualStudio.ShowChatWindowToolbar")   # 不带前导点
#    完整链路时间线见第一节对比；标记文件写入成功
```

### 2.3 过程中的教训（供后续冒烟脚本固化参考）

1. **禁止手动拷贝文件到 `<hive>\Extensions\`**：会与 VSIXInstaller 的随机目录部署形成
   并行残留，导致扩展无法注册（本轮"包永不加载 / 命令不存在"的直接原因）。一律走
   VSIXInstaller。
2. **`/command` 的规范名不含 vsct 中 `LocCanonicalName` 的前导点**：
   - ❌ `.DeepSeek_v4_for_VisualStudio.ShowChatWindowToolbar` → 弹"无效的命令行"模态框
   - ✅ `DeepSeek_v4_for_VisualStudio.ShowChatWindowToolbar`
   - 自动化场景建议改用 DTE `ExecuteCommand`（错误可捕获、不弹阻塞对话框）
3. `VSIXInstaller` 是 GUI 程序：PowerShell 必须用 `Start-Process -Wait` 取 ExitCode（`&` 不等待）。
4. 全新 rootsuffix（如 `BlankTest`）首启会进入 VS 原生首次运行体验，无法无人值守走完；
   空白语义的验证以"无标记 + 无布局恢复"的既有 Exp hive 等价覆盖。

## 三、已知边界与后续观察

| 项 | 说明 |
|----|------|
| 自动弹出恢复路径 | 标记存在时仍需包先被加载（VS 原生窗口布局恢复或用户触发）；无人值守下不可单独达。设计上作为兜底，主恢复通道是 VS 自身的工具窗布局还原 |
| USv2 Debug 探针 | Debug 构建 10s 后台执行一次 ~10.6s 扫描（本轮实测），不影响 UI；Release 零成本 |
| 真正全新 hive 的完整冷启动 | 受 VS 首次运行向导限制未能自动化，建议人工 F5 一次目视确认（预期：启动流畅、无窗口自动弹出、点击工具栏后秒开） |
| 迁移真实写路径 | ApplyProbedValues 的 SaveSettingsToStorage 需 VS 存储服务，单测覆盖属性回填逻辑；端到端已在 00:01 会话由旧代码路径间接验证（16 项迁移成功） |

## 五、实机交付后补充记录（2026-08-24 03:09 会话）

- 用户实测会话：`Ctrl+Shift+D`/工具栏触发打开成功（Execute 于命令注册后立即命中，
  系用户排队输入所致）；标记文件由显式打开写入（03:09:23）；
  `Auto-show (marker present)` 门控正确识别并空转（窗口已显示，仅聚焦）
- USv2 Debug 探针于 init+10s 后台运行 8.8s 完成，UI 无感知
- 用户随后切换推理强度（max→high）/联网搜索开关均正常工作；关闭窗口 Dispose 干净
  （会话保存、SolutionEvents 注销、控件释放，无错误日志）
- 复启观察：标记存在但无布局恢复触发时包保持安静 —— 与 §三"自动弹出为兜底路径"结论一致

## 四、清理记录

- [x] 测试用 devenv 进程按 PID 终止（48724 / 46988 / 42924 / 41724 等）
- [x] `chat-window-opened.flag` 删除（恢复测试前全局状态）
- [x] `BlankTest` hive 删除
- [x] 手动拷贝残留目录（两个 Exp hive 下 `Extensions\DeepSeekChat\`）删除
- [x] 新构建 VSIX 保留部署于两套 Exp hive（供人工复验）
