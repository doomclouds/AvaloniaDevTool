# 自动化能力与研发验证边界

核对日期：2026-10-25。当前产品范围以 [当前 Agent 范围](current-agent-scope.md) 为准；构建和回归结果见 [实现状态](../../implementation-status.md) 与 [测试说明](../../testing.md)。本文定义通用能力的验证方法，不定义业务验收产品。

Agent 提供操作和数据。调用方根据控件状态、截图、文件与诊断数据建立业务关联，决定操作是否达到业务目标。研发使用代表性业务流程验证工具是否可用，不把这些流程、断言或报告格式变成产品接口。

## 产品能力和责任划分

| 产品提供 | 调用方负责 |
| --- | --- |
| 附加目标、读取控件树/属性/状态、修改属性与通用界面操作 | 选择业务目标，解释业务状态，决定流程及失败处理 |
| Windows 原生节点、限定目标的 OS 输入、真实打开/保存/目录/覆盖弹窗操作 | 解释文件选择后的应用结果，核对实际读写是否符合业务要求 |
| 控件、窗口与区域 PNG 及尺寸/坐标/采集方式等元数据 | 解释画面；需要时自行使用图像分析工具 |
| 同连接采集原始 metrics、样式/资源 Profiler、logs/events，保留采集身份、范围、预算、损失与错误 | 关联业务操作，选择统计方法，解释性能或诊断含义 |
| 有界等待、明确错误、会话/节点身份校验和本会话资源清理 | 编排跨步骤流程，设置业务预期，形成自己的判断或报告 |

`ui-wait` 的属性匹配是通用观察条件。它不识别“订单成功”“文件已正确保存”等业务概念。`Injected`、`pending`、`dispatched`、`completed` 和 `AcceptedDialogClosed` 描述输入、动作或弹窗状态，均不能单独证明应用业务成功。

默认 `agent` 提供 44 项：canonical 16、`ui-*` 9、`native-*` 8、`file-dialog-*` 8、`capture-*` 3。`ui-screenshot` 与 `native-screenshot` 已有控件/窗口/区域 PNG 的分层专项证据；official 16、extended 62 保持独立合同。实际工具和参数以对应构建的 `tools/list` 为准，专项不提前替代本轮最终统一回归。

## 通用能力的完成标准

| 能力 | 可核对标准 |
| --- | --- |
| 连接与观察 | 真实 stdio MCP 可附加目标并读取树、属性和状态；目标切换、预览更新或断连后拒绝旧 ID；截断、不可用和错误显式返回。 |
| 框架操作 | 真实控件收到相应键、文本、指针或 provider 操作；合法操作后的通用状态可读，非法作用域或参数在写入前拒绝；异常后收束本会话按压/capture。 |
| Windows 原生操作 | 只操作已附加进程及可验证 owner 范围；真实 UIA/OS 路径与合成 provider 证据分别记录；pending 动作可查询，worker 可取消、超时并回收。 |
| 真实文件弹窗 | 不替换 StorageProvider；通过真实公共文件弹窗选择路径、类型、确认/取消和覆盖；歧义、截断、读回不符及 stale ID 明确失败。工具仍返回 `ApplicationIoVerified=false`。 |
| PNG | 返回可解码的真实 PNG 与实际尺寸/区域/坐标空间；区域、缩放、像素/字节预算有明确合同；原生窗口采集报告方式与受限表面，不把空白或缺失内容解释为正常业务画面。 |
| 原始诊断采集 | 同一个 ToolSession/目标诊断连接开始、读取和停止；原始值、单位、类别、身份、保留窗口、损失和错误不被隐藏；停止后冻结；不接管已有采集，旧 captureId 不影响新目标。 |

单个目标 Agent 只接受一个诊断连接。默认 `capture-start/status/stop` 复用当前连接；不能另开 `extended` 连接抢占正在执行交互的目标。`official`、默认 `agent` 与 `extended` 是不同工具表面，不能相加计算完成率。

## 研发脚本验证矩阵

下表是研发验证应覆盖的维度，现有测试入口是核对来源。它不是新增场景语言、脚本执行器或业务断言引擎；也不表示本次文档编辑运行了这些验证。

| 维度 | 代表性脚本/测试动作 | 只证明的通用能力与证据 |
| --- | --- | --- |
| stdio 与观察 | 真实 initialize/tools/list/tools/call，附加、读取、切目标、断开 | 工具合同、连接和旧 ID 边界；参考 [连接](../features/connection.md) 及 CLI 测试。 |
| 控件交互 | 文本选区/替换、组合键、多击/右键、选择、滚动、拖动、Popup/菜单 | 路由事件、provider 状态、树/属性变化及错误恢复；[AgentInteractionMcpTests](../../../tests/AvaloniaDevTool.Cli.Tests/AgentInteractionMcpTests.cs)、[边界测试](../../../tests/AvaloniaDevTool.Cli.Tests/AgentInteractionBoundaryMcpTests.cs)。 |
| 原生及模态并发 | managed 操作仍在等待时，读取并操作 owned 原生窗口；查询 pending 动作 | 独立 native 通道、managed gate 和 worker 生命周期；[NativeManagedGateMcpTests](../../../tests/AvaloniaDevTool.Cli.Tests/NativeManagedGateMcpTests.cs)、[原生所有权测试](../../../tests/AvaloniaDevTool.Cli.Tests/NativeSessionOwnershipMcpTests.cs)。 |
| 打开/保存 | 真实 AutomationSample + StorageProvider；打开、编辑、另存、重新打开、覆盖/拒绝、取消、占用/权限失败及恢复 | 真实 picker 可操作，应用结果与独立文件字节可供脚本核对；[NativeFileDialogMcpTests](../../../tests/AvaloniaDevTool.Cli.Tests/NativeFileDialogMcpTests.cs)、[弹窗边界测试](../../../tests/AvaloniaDevTool.Cli.Tests/NativeFileDialogBoundaryMcpTests.cs)。样例业务结果只验证该次工具链路。 |
| 原始采集 | 开始后触发真实 Meter、Avalonia 样式/资源活动和日志/事件；读取、停止、重读；切目标及外部采集冲突 | 原始数据、精度、冻结、所有权及损失/错误报告；[AgentCaptureMcpTests](../../../tests/AvaloniaDevTool.Cli.Tests/AgentCaptureMcpTests.cs) 和 Agent 层 metrics/profiler/activity 测试。 |
| PNG | 捕获控件及局部区域，检查真实像素、缩放和无效预算；捕获限定原生窗口/节点区域 | [AgentScreenshotMcpTests](../../../tests/AvaloniaDevTool.Cli.Tests/AgentScreenshotMcpTests.cs) 的真实渲染及 [NativeScreenshotMcpTests](../../../tests/AvaloniaDevTool.Cli.Tests/NativeScreenshotMcpTests.cs) 的真实 stdio/Windows 主窗与公共文件弹窗证据；SDK/几何单测/人工查看各自单列，不替代统一回归。 |
| 故障与清理 | stale ID、双 picker 歧义、树截断、取消、超时、目标/宿主退出、输入部分失败 | 拒绝、保留原错误与资源回收；真实 OS、Headless、合成 provider/sender 各自标明证据层，不能互相替代。 |

研发脚本可以在自有临时目录保存请求/响应、目标身份、树/属性、PNG、原始诊断快照和文件字节/hash，以定位失败并复现测试。脚本按业务标签或时间关联这些数据，属于脚本逻辑。产品不承诺统一业务证据包、业务报告 schema 或验收退出码。

脚本中发起打开/保存的 managed 调用可能被模态对话框阻塞，驱动需要同时处理 native/file-dialog 通道，再收束原调用。此并发规则来自实际工具执行模型，不要求产品提供 begin/await 场景语言。

## 原始数据的解释边界

- Metrics 只采目标实际发布的 .NET Meter；保留 RawValue、数值类型、单位、ValueSemantics、CaptureId 和损失计数。Counter 增量不自动变成累计值或速率，窗口分位只覆盖保留样本。
- Profiler 指样式附加/求值与资源查找活动。耗时是启用诊断后的观测，未知 Result 保持未知；它不提供完整 CPU 调用栈或内存分配剖析。
- Logs/events 返回实际记录及有界状态。无记录、采集错误、容量丢弃和未订阅是不同状态，不能用空数据推导业务或性能通过。
- 截图只返回像素与元数据；不计算视觉差异、维护视觉基线或作视觉通过判断。

ui PNG 使用控件局部 DIP 与显式 scale，输出边长最多 2048、总像素最多 2,097,152、PNG 最多 1 MiB。native PNG 使用节点局部物理像素，源窗口最多 16,000,000 像素、结果默认 8,000,000（可设到 16,000,000）、PNG 最多 16 MiB。native 只取 PrintWindow 窗口表面、不改变焦点；未绘制区域可拒绝，GPU/受保护等内容仍可能不完整。完整参数与渲染边界见 [PNG 合同](../features/agent-interaction.md#png-截图)。

详细服务合同见 [Metrics](../../metrics.md)、[Profiler](../../profiler.md) 和 [Agent 交互](../features/agent-interaction.md)。这些服务资料中的历史 GUI 说明和旧批次不扩展当前产品范围。

## 研究保留与退出范围

官方工具名/schema、已采用实现的来源与许可、Windows provider/窗口身份限制，以及历史失败样本仍有研究价值。官方 16 项基准用于定位兼容差异，不能推导官方授权运行等价已验证。原版授权运行对照、macOS/Linux 原生后端、完整 CPU/分配剖析、IME 候选/组合输入与文件/数据 DragDrop 均按 [当前范围](current-agent-scope.md#明确排除) 处理。

旧版本文提出的 run-scenario、版本化业务场景、业务断言、性能阈值、视觉基线、发版门禁和 CI 平台已退出产品范围。当前没有 run-scenario 入口；本轮试写的场景/验收源码和测试已移出产品，不作为待完成功能。此前的 477/1/1 整批及 80 项独立复测是历史证据，不能写成本轮结果。
