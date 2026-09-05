# A 阶段执行报告 —— 冒烟、构建固化与基线准备

> 日期：2026-08-22　提交范围：`8dade23..b418f06`
> 对应计划：A 层四项（冒烟走查 / 基线会话 / v0 报告 / 构建脚本固化）

---

## 一、执行结果总览

| 项 | 状态 | 结果 |
|----|:----:|------|
| A4 构建脚本固化 | ✅ | `tools/build-vs26.ps1` 端到端验证：主工程+测试工程构建成功，vstest **942/942 通过**（ExitCode=0） |
| A1 冒烟 · VSIX 安装 | ✅ | 静默安装至实验实例 Exit=0；同时落位 VS2026(`18.0_ba3bb658Exp`) 与 VS2022(`17.0_fc76f596Exp`) 两套 Exp hive |
| A1 冒烟 · 包加载 | ✅ | 实验实例拉起 + `/command` 打开工具窗口：初始化 **Step 1–10 全部 OK**（含新增的 `Step 10: InlineAiEditCommand registered OK`）；Pane 构造→StartControl→窗口显示全链路 OK；**诊断日志 0 错误 0 异常** |
| A2 十次基线会话 | ⏸ | 无法无人化执行：需要配置 API Key 并由人工在聊天窗发送消息触发 Agent 流。遥测目录当前不存在 —— 符合设计（仅开窗不产生会话噪音） |
| A3 第一份 v0 报告 | ⏸ | 脚本就绪（`benchmark\invoke-benchmark.ps1 -ReportOnly` 对空目录优雅提示）；待 A2 数据 |

## 二、A1 冒烟证据（实验实例 diagnostic 日志摘录）

```text
23:44:07 [DeepSeek Init] Step 9/9: Commands registered OK
23:44:07 [DeepSeek Init] Step 10: InlineAiEditCommand registered OK   ← P1-B 新命令注册成功
23:44:08 [DeepSeek Cmd] Execute: menu item clicked...
23:44:09 [DeepSeek Pane] Constructor: DeepSeekChatControl created OK
23:44:09 [DeepSeek Pane] OnCreate: StartControl completed OK          ← WebView2 聊天界面初始化完成
23:44:10 [DeepSeek Init] Auto-show: tool window shown OK
错误扫描: 0 条
```

## 三、测试情况分析

### 3.1 自动化验证已覆盖的层级
```
源码编译（含上游合并）→ 单元回归 942/942 → VSIX 打包 → 静默安装 → 真实进程包初始化 → 工具窗 UI 容器创建
```
至此 Phase 1.5 全部代码路径中，除"编辑器内按键交互"与"LLM 会话流"外均已获得真实环境验证。

### 3.2 从冒烟数据得到的观察
1. **初始化耗时 ≈ 2.4 s**（Step1 23:44:07.821 → tool window shown 23:44:10.394）。其中 `StartControl` 占 0.55 s。该值可作为后续基线报告中"扩展冷启动分量"的参考锚点。
2. **双 Hive 安装行为**：`VSIXInstaller /rootSuffix:Exp` 按 machine 上全部 VS 实例枚举安装 —— 对用户友好（两套 IDE 都可测），但也意味着卸载需逐实例执行。
3. **遥测目录缺席即正确性信号**：窗口打开不写任何 session JSON，验证了"会话级指标以真实 Agent 往返为粒度"的设计边界。

### 3.3 无法自动化项的原因与移交方式
| 项 | 原因 | 移交方式 |
|----|------|---------|
| Ctrl+I 指令条交互 | 需要编辑器焦点与真实选区 | 打开任意解决方案 → 选中代码 → Ctrl+I；观察指令条出现/Enter 后 diff 预览/Accept 写入 |
| Context 抽屉视觉检查 | 需聊天窗渲染后目视 | 发送任一 @ask 消息后看右上角抽屉是否出现并可展开 |
| 十次基线会话 | 需真实 LLM 往返 | 正常使用即可；结束后运行 `-ReportOnly` |

## 四、给下一阶段的输入

1. 构建入口统一为 `tools\build-vs26.ps1`（CI 若具备完整组件可绕过合并视图逻辑，脚本内已留参数化空间）。
2. 首批基线数据回来后，优先核对三个数字：**TTFT 中位数、平均工具调用数、cache 命中率** —— 分别对应 Streaming/Agent 循环/上下文结构三条优化线的开关。
3. `capture_window` 建议实测一次超时表现（默认 60s 档），必要时入豁免清单或独立档位。

*对应提交：`b418f06`（构建脚本）、`3a86908`(上游合并)、`d2f6db0`（标注/报告脚本）。*
