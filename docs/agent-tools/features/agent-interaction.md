# 自研 Agent 交互工具

核对日期：2026-10-25。默认 `agent` profile 共 44 项工具：canonical 16、框架交互/截图 9、Windows native 8、文件弹窗 8、同连接原始采集 3。`--profile official` 保留 canonical 16，`--profile extended` 保留独立的 62 项诊断表面。新增工具的定义来自本项目，不能表述成官方公开工具合同。产品只提供操作和数据，业务关联与判断由调用方负责；[当前范围](../architecture/current-agent-scope.md) 定义明确排除项。

## 框架交互

先用 `attach-to-app` 选择目标，再以当前 `tree`/`search` 返回的数值 nodeId 操作。`ui-*` 和 canonical 共用 nodeId 会话域及 managed call gate；切换应用或预览内容 generation 改变后，重新读取节点。

| 工具 | 参数要点 | 实际语义与结果 |
| --- | --- | --- |
| `ui-input` | `nodeId`, `kind`, 可选 `text/key/modifiers/x/y/deltaX/deltaY/button` | Avalonia Text/KeyDown/KeyUp/PointerMove/PointerDown/PointerUp/Wheel/Click。坐标为节点局部坐标；明确 down/up 可组成持续指针序列。 |
| `ui-click` | `nodeId`, `clickCount=1`, `button=Left`, 可选局部坐标/修饰键 | 1–3 次真实路由 move/press/release，双击手势、右键菜单由控件处理；返回 InputResult 不代替业务断言。 |
| `ui-key-chord` | `nodeId`, `keys[]`, 可选 `modifiers[]` | 使用 Avalonia Key 名称，例如 `keys=[A], modifiers=[Control]`；依次按下，逆序释放，异常也尝试释放。不是把 `Ctrl+A` 当一个键名。 |
| `ui-drag` | `sourceNodeId`, `targetNodeId`, 可选起止局部坐标、`path[]`、`steps=12`、button/modifiers | 同一 TopLevel 中保持 pointer capture 并转换根坐标；waypoint 含 `nodeId/x/y`。步数和路径长度各最多 128；先验证整条路径，跨根拒绝。 |
| `ui-text-selection` | `nodeId`, `start=0`, `length=null`, `replacementText=null` | TextBox UTF-16 范围；省略 length 选到末尾，省略 replacementText 只选中，空替换删除选区。通过编辑管线替换并返回实际范围、文本长度和是否替换。 |
| `ui-select-item` | `nodeId`, `index` | 真实 SelectingItemsControl，例如 ComboBox/ListBox/TabControl；index 从 0 开始，-1 清空选择。返回实际选中项与索引。 |
| `ui-control-action` | `nodeId`, `action` | 实际 automation provider 的 Invoke/Toggle/Expand/Collapse；返回观察到的 toggle/expand 状态。不支持的 provider 明确报错。 |
| `ui-wait` | `query`, `condition=Exists`, `propertyName=Avalonia.Controls.TextBlock:Text`, `expectedValue`, `timeoutMs=10000`, `pollIntervalMs=100` | 在真实 search/props 上轮询 Exists/Absent/Equals/Contains。默认使用TextBox/TextBlock共享的明确文本属性ID；属性条件要求唯一匹配，每次轮询释放managed gate，超时报 `ui_wait_timeout`。 |
| `ui-screenshot` | 数值 `nodeId`，可选局部 DIP `x/y/width/height`、`scale=1` | 返回控件/区域 PNG image 与实际尺寸、区域、缩放和 DPI 元数据；详见 [PNG 截图](#png-截图)。 |

键和文本需可聚焦目标；只读文本替换、隐藏/禁用/脱离树节点、无效布局、越界坐标和 stale ID 都应明确失败。文本和替换每次最多 2048 UTF-16 单元。一次 pressed pointer 序列不能跨 TopLevel；结束、异常或释放会话时清理本会话的 capture，不接管其他输入设备的状态。

框架输入发送 Avalonia 路由事件，不注入 Windows 键鼠，不创建 IME composition，不传递文件或数据 DragDrop。`ui-select-item` 是选择控件状态，`ui-control-action` 是 provider 动作；需要验证用户手势本身时，应选择对应 pointer/key 工具并断言实际事件和状态。

实现入口：[`AgentTools.Interaction.cs`](../../../src/AvaloniaDevTool.Cli/Tools/Input/AgentTools.Interaction.cs)、[`AgentTools.Wait.cs`](../../../src/AvaloniaDevTool.Cli/Tools/Input/AgentTools.Wait.cs)、[`InputInspectionService.Interactions.cs`](../../../src/AvaloniaDevTool.Agent/Features/Input/InputInspectionService.Interactions.cs)。

## Windows 原生节点

native 工具只允许当前已附加的本地应用及可验证 owner 链窗口。ID 为 `native:<session>:<opaque-id>` 字符串，与 Avalonia 数值 nodeId 分离。无连接、remote transport、非 Windows、目标进程身份无法验证、过期 worker 或旧节点均明确失败。

| 工具 | 用法 |
| --- | --- |
| `native-windows` | 列出真实目标/owned 窗口及 PID、HWND、OwnerHandle、modal/enabled/visible 状态。 |
| `native-tree` | 可选 `rootNodeId`，默认 `maxDepth=6`, `maxNodes=500`；返回有界树、Truncated、VisitedNodes。 |
| `native-find` | 可按 automationId/name/className/controlType 查找，显式限制深度、访问节点和结果数量；检查截断标志。 |
| `native-inspect` | 读取当前节点的属性、值、选择/展开/切换状态及 supported patterns；密码值受保护。 |
| `native-action` | 根据实际 UIA pattern 执行 Invoke/SetValue/Select/AddToSelection/RemoveFromSelection/Expand/Collapse/Toggle/Focus/ScrollIntoView/Scroll/Close/Minimize/Maximize/Restore。 |
| `native-action-status` | 使用 operationId 查询先前 `pending` 动作是否 completed/failed；不能只看到 pending 就结束验收。 |
| `native-input` | `nodeId`, `kind`, 可选局部 x/y、text、keys/modifiers、targetNodeId/targetX/targetY、wheelDelta=120、dragSteps=12；使用真实 Windows SendInput。 |
| `native-screenshot` | native `nodeId`，可选节点局部物理像素 `x/y/width/height`、`maxPixels=8000000`；返回限定窗口表面的 PNG 和元数据，详见 [PNG 截图](#png-截图)。 |

`native-action` 可返回 `completed`、`pending`、`dispatched` 或 `failed`。最后业务状态由实际 UI 和文件证据决定。窗口关闭或替换后重新枚举；不得将相同标题的其他窗口视为原节点。

worker 以独立消息泵接收目标及 owned 进程的窗口销毁通知，将根窗口与有 HWND 节点的 epoch 组合校验；根过期时，其子节点即使没有单独收到通知也会被拒绝。通知异步送达，epoch 不能作为同步屏障；迟到通知可能使新 ID 保守失效。真实销毁/重建已验证旧根、旧子 ID 被拒绝，修复后完整 HWND 复用的旧 ID 拒绝尚未获得成功样本，详见[窗口身份研究](../research/native-window-identity.md)。

独立 `native-input` 路径支持限定节点局部坐标的 Click/DoubleClick/RightClick/Move/Wheel/Text/KeyChord/Drag。Text 为 1–4096 UTF-16 单元且不接受修饰键；KeyChord 为 1–8 个键；modifiers 仅 Ctrl/Alt/Shift；wheelDelta 非零且在 -12000 到 12000；Drag 必须有同根 targetNodeId，dragSteps 为 1–128。默认局部坐标为中心，不接受任意全桌面坐标。

执行前严格核对 foreground、PID/start time、owner 和节点，恢复最小化根并请求前台；实际前台不符、目标点被其他窗口遮挡、物理鼠标/修饰键正在按住、目标 offscreen/disabled 时拒绝。会离开目标输入范围的系统切换快捷键被拒绝。按压与释放放在同一个 SendInput 批次；部分投递失败时按返回count推算成功前缀，并尽力逐项释放仍按下的键/按钮，清理失败不遮蔽原异常。前缀是清理假设，OS再次拒绝时不能保证状态恢复。返回 `Injected/InputCount` 只证明系统接受输入，`ApplicationEffectVerified=false`；Unicode 注入不宣称 IME，drag 不宣称文件/数据 DragDrop。已有真实MCP正常输入和焦点效果证据，具体批次与未触发的OS失败场景见[测试记录](../../testing.md)。

实现入口：[`AgentTools.Native.cs`](../../../src/AvaloniaDevTool.Cli/Tools/Native/AgentTools.Native.cs)、[`AgentTools.NativeInput.cs`](../../../src/AvaloniaDevTool.Cli/Tools/Native/AgentTools.NativeInput.cs)、[`ToolSession.Native.cs`](../../../src/AvaloniaDevTool.Cli/Native/ToolSession.Native.cs)、[`NativeAutomationBackend.cs`](../../../src/AvaloniaDevTool.NativeAutomation.Worker/Sessions/NativeAutomationBackend.cs) 和 [`NativeAutomationBackend.Input.cs`](../../../src/AvaloniaDevTool.NativeAutomation.Worker/Input/NativeAutomationBackend.Input.cs)。worker 有界超时、回收和身份验证见[完整交互架构](../architecture/complete-ui-automation.md)。

## PNG 截图

两项截图工具均为只读，返回 MCP `image/png` ImageContentBlock 和 JSON TextContentBlock；调用方按内容块类型读取，不依赖块顺序。canonical `screenshot(nodeId)` 继续保留原合同，区域/缩放使用 `ui-screenshot`，原生窗口和 picker 使用 `native-screenshot`。

| 合同 | ui-screenshot | native-screenshot |
| --- | --- | --- |
| 身份 | 当前 canonical 数值 nodeId，必须是可见 TopLevel 中真实、已布局的 Control | 当前 native 字符串 nodeId，必须位于已附加目标/owner 窗口范围 |
| 区域 | x/y/width/height 全省略为完整控件，或四项全部提供；有限、非负起点、正尺寸，完全位于控件局部 bounds | 同样全省略或全部提供；节点局部物理像素，完全位于当前节点和根窗口表面；分数边界向外取整 |
| 缩放 | scale 为有限正数，默认 1 像素/DIP；输出尺寸为区域 DIP × scale 向上取整，渲染 DPI=96×scale，与桌面 DPI 无关 | 不提供 scale，直接返回物理像素 |
| 预算 | 输出边长最多 2048，总像素最多 2,097,152，完整 PNG 最多 1 MiB | 完整源窗口最多 16,000,000 像素；输出 maxPixels 默认 8,000,000、允许 1..16,000,000；完整 PNG 最多 16 MiB |
| 方法 | RenderTargetBitmap 渲染真实控件子树；区域从子树局部坐标选择，不是桌面像素或窗口装饰抓屏 | PrintWindow(PW_RENDERFULLCONTENT) 渲染限定根窗口后裁剪；不使用任意桌面区域，不激活/恢复窗口或改变焦点 |
| 元数据 | NodeId、Width、Height、Bounds、Scale、DpiX、DpiY、CoordinateSpace=control-local-dip | NodeId、Width、Height、ScreenBounds、CaptureMethod、Limitations、CoordinateSpace=physical-pixels、MaximumEncodedBytes |

ui 截图拒绝隐藏、脱离树、零尺寸、无效区域/缩放、过期 ID 和预算溢出；不支持的渲染器返回明确错误。它反映该控件子树的渲染，OS picker、原生嵌入内容或其他窗口不属于该子树。

native 截图拒绝不可见、最小化或 offscreen 节点；采集前后核对身份、生命周期与节点/窗口 bounds，变化时要求重新检查再试。每次使用不同初始填充执行两次 PrintWindow，仅当请求区域同位置保留两种对应填充值时判为未绘制，返回 `screenshot_not_rendered`；普通帧变化和合法黑色不按未绘制拒绝。PrintWindow 仍依赖目标渲染器，GPU、受保护、分层或嵌入内容可能黑屏或不完整；像素颜色不能证明全部内容正确呈现。无桌面抓屏回退，附加 Avalonia 控件优先使用 ui-screenshot。

已执行的截图专项包括：managed Headless 新增 7 项及既有 official/Agent/Protocol AOT 相关检查；真实 WinForms SDK 捕获 RGB 与动态文字并核对 foreground 不变；真实 stdio MCP 捕获实际 Avalonia 控件、原生主窗、CommonItemDialog 和路径框区域，以及无效/stale 拒绝。native 几何 70 项只证明坐标/预算算法，不算运行截图。实际主窗与 OS picker PNG 已人工查看清晰；这些专项不替代本轮完整回归，结果见 [测试说明](../../testing.md)。

截图不计算视觉差异、不维护基线，也不作业务判断。源码见 [AgentTools.Screenshot](../../../src/AvaloniaDevTool.Cli/Tools/Rendering/AgentTools.Screenshot.cs)、[AgentTools.NativeScreenshot](../../../src/AvaloniaDevTool.Cli/Tools/Native/AgentTools.NativeScreenshot.cs) 和 [native 捕获实现](../../../src/AvaloniaDevTool.NativeAutomation.Worker/Capture/NativeAutomationBackend.Screenshot.cs)。

## 同连接原始诊断采集

| 工具 | 参数和结果 |
| --- | --- |
| `capture-start` | metrics/profiler/activity 默认 true；logs=true、events=false、eventName=Click、minimumLevel=Warning；可选 meterNames/instrumentNames/profilerCategories；capacity=512（1..1024）、pollIntervalMs=1000（100..60000）。至少选择一个服务；activity 至少选 logs/events 之一。 |
| `capture-status(captureId)` | 读取原连接上本次采集的原始 metrics、profiler、activity 快照、状态、Completeness 与 Issues。 |
| `capture-stop(captureId)` | 只停止此 captureId 仍拥有的 listener；成功后快照冻结，重复读取/停止返回冻结结果。停止失败保留运行状态和错误，不伪装为已停止。 |

meterNames/instrumentNames 各最多 16 个非空模式，每项最多 128 字符；具体 Meter 筛选/数值语义见 [Metrics](../../metrics.md)。profilerCategories 可选 StyleAttach、StyleEvaluate、ResourceLookup（1..3 项）；该 Profiler 仅为样式/资源活动，完整 CPU 调用栈和分配剖析交由其他工具。

返回包含 CaptureId、Target（ConnectionId/Session/OwnershipScope）、State、StartedAt/StoppedAt、Metrics、Profiler、Activity、Completeness 与 Issues。Completeness 的 Scope 为 retained_samples，IsComplete/HasDataLoss/HasErrors/Frozen 只描述采集数据状态。原始精度文本、单位、ValueSemantics、未知 Result、保留窗口及淘汰/丢失计数保留，空样本不表示性能通过。

采集复用当前 ToolSession 的唯一目标诊断连接，不另开 extended 抢占连接。启动前检查所选服务，不接管已在运行的 listener；部分启动失败只撤销本次拥有的服务。目标切换、重连、外部重启或 mutation epoch 变化后旧 ID 不停止新采集。每个 MCP 会话最多保留 64 次采集。对应 capture 20 项 Headless/故障 RPC 专项已验证，统一回归结果单列。

调用方依据原始数据自行关联业务与分析性能；本工具不设性能阈值、业务断言或 CI 门禁。源码见 [AgentTools.Capture](../../../src/AvaloniaDevTool.Cli/Tools/Diagnostics/Capture/AgentTools.Capture.cs) 与 [ToolSession.Capture](../../../src/AvaloniaDevTool.Cli/Capture/ToolSession.Capture.cs)。

## 真实文件弹窗

文件工具使用 native session，不持有 managed gate。它们检查 Windows common item dialog 的窗口类和实际控件结构，操作 owned picker 的路径字段、文件类型、确认/取消及覆盖 prompt。它们不替换 StorageProvider，也不直接调用应用内部“打开/保存”方法。

| 工具 | 用法和注意点 |
| --- | --- |
| `file-dialogs` | 列出当前目标范围内支持的真实 picker。不要按桌面所有窗口的标题猜目标。 |
| `file-dialog-inspect(dialogId)` | 当前路径值、filter、确认/取消节点、owned prompts 和生命周期。关闭状态只描述 dialog。 |
| `file-dialog-set-paths(dialogId, paths[], kind)` | kind 为 OpenFile/OpenFiles/SaveFile/Folder；先验证绝对文件系统路径，再设置并逐字读回。不会自动接受。 |
| `file-dialog-set-filter(dialogId, filterName=null, filterIndex=null)` | 二者只能选一个；名称精确匹配，索引从 1 开始。选择后读回验证。 |
| `file-dialog-accept(dialogId)` | 操作 Open/Save/Select Folder；覆盖 prompt 仍保留，不能自动等同于保存成功。 |
| `file-dialog-cancel(dialogId)` | 取消指定 picker；随后检查应用文档和已有文件未变。 |
| `file-dialog-overwrite(dialogId, promptId, overwrite)` | 仅响应已检查的 owned Yes/No 覆盖 prompt；true 同意覆盖，false 拒绝。需要新鲜 prompt ID。 |
| `file-dialog-wait(dialogId=null, timeoutMs=10000)` | 省略 ID 等待一个唯一 picker 出现；指定 ID 等待原 picker 关闭或出现 owned prompt。超时/取消明确结束，不自动回答 prompt。 |

`timeoutMs` 是轮询预算，不是严格墙钟上限：已开始的一轮原生读取会按session自身的RPC操作超时完成，可能晚于预算返回成功或等待超时。内部轮询到期不取消在途RPC或销毁worker；调用者主动取消和RPC自身超时仍会使session失效并回收worker，需重新附加和发现ID。

文件类型列表的 `Expand` 最多发送一次。选项在可见性预算内未出现时，仅当动作已 `completed`、最新树中的 combo 仍为 `Collapsed`，且其直接子节点中存在唯一的 `AutomationId=DropDown`、支持 `Invoke` 的 Button，才回退 `Invoke` 一次并重新观察；动作仍 `pending` 或控件已展开时不会回退，也不会反复切换列表。该分支已有不创建窗口的合成 provider 进程证据，不能据此宣称所有 Windows picker 都已验证，详见[边界补证](../research/windows-boundary-coverage.md)。

路径只接受绝对 drive/UNC 文件系统路径；Open 必须已存在，Folder 必须是目录，Save 的父目录必须存在。拒绝相对路径、shell/URL、引号/通配符、设备前缀、ADS、控制字符、保留设备名和尾随空格/点。路径 preflight 有 10 秒期限；它验证路径，不证明打开权限或应用 I/O 成功。网络文件系统探测超时时不开始 UI 修改。

`SetPaths` 后若路径字段被外部改动，accept 拒绝，必须重新 inspect/set；存在 active owned prompt 时不能继续盲改 picker。多 picker 歧义、缺少唯一确认/取消或路径控件、树截断、filter 歧义/读回不符、非预期覆盖 prompt 和 stale ID 都明确失败。不同 Windows 版本、自定义/第三方 picker 不保证符合当前 `windows-common-item-dialog-v1` 结构。

返回状态包括 Open、ConfirmationRequired、AcceptedDialogClosed、CancelledDialogClosed、Closed/ActionPending；实际工具响应还应检查 LastAction。`ApplicationIoVerified=false` 是持续契约：选择器关闭后必须检查业务应用和磁盘。

## 研发工作流与证据边界

以下流程是通用能力的研发验证，业务状态核对由测试驱动执行，不是产品内置的场景或断言引擎。使用 [`AutomationSample`](../../../samples/AvaloniaDevTool.AutomationSample/README.md) 的真实桌面进程，传入唯一 `--pipe` 和临时 `--test-root`，可加 `--ready-file`/`--result-file`。不要在这个目标设置 fake StorageProvider 或 TopLevelProvider。

打开流程：managed 点击 FilesTab/OpenSingleButton → file-dialog-wait → inspect → set-paths(OpenFile) → accept → 等待应用 FileStatus/结果文件 → 对照 DocumentPath、DocumentEditor 和源文件。多选重复同样过程并核对全部路径与内容。目录选择核对 FolderPath，不能计为文档读取。

保存流程：修改 DocumentEditor → 触发 SaveAsButton → 等待真实 Save picker → set-paths(SaveFile) → accept → 若有覆盖 prompt，inspect 后显式 overwrite → 等待应用 Saved 状态 → 独立读取实际文件字节/长度/hash → 通过真实 Open 重新打开并核对 UI。选择路径与实际写入是不同阶段；比原文件更短的编辑内容必须验证旧尾部已截断。

取消流程：记录原文档、路径、已有文件 hash → 取消 picker 或拒绝覆盖后再取消 picker → 等待应用 Cancelled 状态 → 比较原文档和所有相关文件。只确认 dialog 不见了不足以证明无改动。

真实 MCP 工作流、源码指纹及各批次结果见[测试说明](../../testing.md)。框架 headless、合成 provider 和 native 底层测试只覆盖各自证据层，不能替代上述真实工作流。所有固定验收 ID 及待补证据位于[完整交互清单](../architecture/complete-ui-automation.md#固定功能验收清单)，不使用旧官方兼容验收百分比描述本目标。

工具行为适用于明确提供的控件/provider 和已验证 dialog profile，UAC/更高权限桌面及任意自绘 picker 不保证可操作。用户不做官方授权运行等价和 macOS/Linux native；IME 候选/组合输入及文件/数据 DragDrop 暂不做。业务判断、视觉比对/基线、性能阈值、发版门禁和 CI 均排除；完整 CPU 调用栈/内存分配剖析交由其他工具。这些不是当前待完成清单。实际 I/O 结果由调用方判断，不能由工具 `Delivered`/`Injected`/`completed` 回执推出业务成功。
