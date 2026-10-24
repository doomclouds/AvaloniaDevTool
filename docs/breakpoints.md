# 属性与事件断点

断点服务在目标应用的会话内观察 Avalonia 属性变化或路由事件，按命中次数执行日志或暂停动作，并向检查器发送命中记录。断开会话时释放全部监听器和待发送记录；已有属性、绑定和业务事件处理保持原来的语义。

默认只记录并可输出日志，`Suspend=false`。暂停使用 `Debugger.Break()`，必须已有调试器附加。创建暂停断点、启用暂停断点和实际执行暂停前均检查 `Debugger.IsAttached`，避免弹出未附加调试器的 JIT 窗口。

## 配置与标识

`BreakpointSpec` 的核心字段为：

| 字段 | 含义 |
| --- | --- |
| Kind | Property 或 Event |
| MemberId | 完整 owner-qualified 标识，如 `Avalonia.Controls.TextBlock:Text`、`Avalonia.Controls.Button:Click` |
| ObjectId | 属性的目标对象，或事件路由经过的接收节点；空值表示不限制该条件，保留已有调用语义 |
| SourceObjectId | 仅事件：要求本次 RaiseEvent 的实际 Source 等于该对象；空值表示全部源对象，与 ObjectId 独立组合 |
| Enabled | 是否监听和累计命中 |
| Comparison / CompareTo | 对累计 HitCount 做 Always、LessThan、LessThanOrEqual、Equal、GreaterThan、GreaterThanOrEqual 比较 |
| LogMessage | 条件满足时向 Avalonia 日志管道输出 Information 日志 |
| RemoveOnHit | 条件满足后移除本断点 |
| Suspend | 条件满足后请求在触发对象的 Dispatcher 线程暂停 |

比较的是**累计命中次数**，不是属性值，也不执行任意 C# 条件表达式。CompareTo 不得为负。属性 ID 可直接取自属性面板 DTO 的 Id；短名称 `Text`、`Click` 或 `Button.Click` 会明确失败，避免同名成员歧义。

属性观察允许 readonly 和 direct 属性，因为服务只监听变化，不尝试改写它们。目标必须是 AvaloniaObject；事件的指定目标还必须是 Interactive。省略 ObjectId 时，属性 owner 必须在已加载程序集里唯一解析，成员必须已经能从公开注册表查询。

事件指定 ObjectId 的含义是“事件经过该 Interactive 接收者时监听”。B02 源对象事件断点使用新增的 SourceObjectId；B04 接收节点条件继续使用 ObjectId，旧客户端和旧定义无需迁移。命中记录里的 ObjectId 仍指向本次实际 Source，因此父容器上的接收节点断点可以报告子控件发出的事件。

两个条件取交集：只设置 SourceObjectId 时观察该源的整个事件路由；只设置 ObjectId 时观察所有经过该节点的源；两个均为空时全局观察该事件。SourceObjectId 必须指向存活的 Interactive；将其用于属性断点返回 invalid_request。Avalonia 的 EventRoute.RaiseEvent 会覆盖预先填写的 RoutedEventArgs.Source，因此源条件使用实际发起 RaiseEvent 的对象，并在本次路由首次观察时保存弱引用快照。

## 协议与客户端

| 方法 | 请求／结果 |
| --- | --- |
| `breakpoint.create` | CreateBreakpointRequest → BreakpointInfo |
| `breakpoint.update` | UpdateBreakpointRequest → BreakpointInfo |
| `breakpoint.remove` | RemoveBreakpointRequest → OperationResult |
| `breakpoint.list` | 当前 BreakpointInfo 列表 |
| `breakpoint.history` | BreakpointHistory：最近命中和 DroppedCount |
| `breakpoint.hit` | BreakpointHit 通知 |
| `breakpoint.changed` | BreakpointChangedNotification 通知 |

Client 对应 API 为 `CreateBreakpointAsync`、`UpdateBreakpointAsync`、`RemoveBreakpointAsync`、`GetBreakpointsAsync`、`GetBreakpointHistoryAsync`，并提供 `BreakpointHit` 和 `BreakpointsChanged` 事件。

检查器断点页分别显示“事件 Source 对象 ID”和“目标／接收节点 ID”，可把树中所选对象填入源条件；留空源 ID 表示全部源，取消目标限制表示全部接收节点。定义列表分别回显两个条件，命中导航后更新仍保留原接收节点；重复拉取同一定义不会覆盖正在编辑的草稿。断开连接会清空对象 ID 草稿。

MCP `breakpoint-create` 追加可选 `sourceObjectId` 参数，`objectId` 保持原含义；`breakpoint-update` 的完整 definition 同样支持 sourceObjectId。`breakpoints` 返回定义或 history，历史的 objectId 可定位源对象。例如父面板 ID 为 10、按钮 ID 为 11 时，`kind=Event, memberId=Avalonia.Controls.Button:Click, objectId=10, sourceObjectId=11` 只观察按钮 11 经过面板 10 的 Click。

更新使用完整新定义，可修改成员、对象、条件和开关；默认保留命中计数，ResetHitCount 可清零。新配置验证和监听建立成功后，才释放旧监听器。无效更新不会悄悄删除原断点。

未附加调试器时，创建 Suspend=true 的定义返回 `debugger_not_attached`。更新允许保存 Enabled=false 的暂停配置，这样已有暂停断点可随时被安全禁用；尝试启用时仍会拒绝。已建立配置之后调试器退出，命中仍可被记录，但暂停前的再次检查会跳过 Break；命中准备时已知调试器退出时，记录的 SuspendSkipped=true。

常见错误还包括 `invalid_member`、`property_not_found`、`event_not_found`、`object_expired`、`unsupported_object`、`breakpoint_not_found` 和 `breakpoint_limit`。每个会话最多允许 128 条定义。

## 命中顺序

每次观察到有效变化时先累计 HitCount，并记录 ConditionMet。无论条件是否满足，历史和通知都保留这一次观察，便于界面展示真实计数。

条件满足后：

1. RemoveOnHit 如已启用，移除并解除监听。
2. LogMessage 如已启用，写入 `DevTool.Breakpoints` 区域的 Information 日志。
3. Suspend 如已启用且调试器仍在，进入触发对象的 Dispatcher 执行 Break。

日志遵循应用现有 sink 的 IsEnabled 过滤；即使 Information 日志被应用过滤，断点自己的历史和通知仍然存在。服务只在自身写日志期间抑制同步重入，避免诊断日志引起新的断点递归；它不能修复第三方日志 sink 内部自行产生的无限递归。

暂停辅助方法隐藏自身调试帧。事件断点在公开 RouteFinished 通知中同步收尾，保留 RaiseEvent 尚未返回的调用栈；暂停位置仍是诊断收尾回调。SuspendSkipped 是准备命中记录时的状态信息，不是“已成功暂停”的运行证明。

## 事件聚合与生命周期

事件监听接收 Direct、Tunnel 和 Bubble，并接收已经 Handled 的事件。使用公开 Raised／RouteFinished 订阅跟踪一次 Raise 的节点和阶段，只有完成全部阶段且同时满足源／接收节点条件时才增加 HitCount。Tunnel＋Bubble 算一次，Direct 或单阶段事件也算一次。同一 RoutedEventArgs 连续 Raise、不同参数嵌套 Raise，以及同一参数递归 Raise 各自独立计数。源快照不会被内层 Raise 改写；同参数递归可能改变外层参数的 Route，因此按真实阶段收尾次数完成聚合，不根据可变的 args.Route 提前结束外层观察。

摘要记录最后阶段 RouteFinished 回调时的 Handled；所有普通实例处理器已经执行，后注册的其它 RouteFinished 订阅者若继续改写 Handled，无法提前归因。服务不修改 Source、Route 或 Handled，重复使用可变参数引起的应用事件语义仍由 Avalonia 决定。

每个定义最多积累 200 个待收尾观察，另有最多 200 个参数键用于有界清理。目标、源条件、来源快照和首次接收节点均使用弱引用。事件参数只在路由期间短暂保留；正常结束立即释放，处理器抛出等原因导致缺失 RouteFinished 时由 Dispatcher Background 清理并计入丢弃。移除、更新和 Dispose 会取消清理回调并清空集合。非 UI 线程路由不进入这条 UI 路由聚合通路，并计入丢弃统计。递归超限会丢弃该参数的未完成观察，避免把内层误归到外层。

属性目标使用弱引用解除订阅；全局属性和事件观察持有可释放的订阅。List 会把已被 GC 的指定目标、接收节点或源条件标记为 ObjectExpired 并停止监听，继续更新到过期对象则返回 object_expired。历史里保存对象 ID 和文本摘要，不持有原控件或 RoutedEventArgs。

服务最多保留最近 200 条命中记录。命中通知以 100 ms 周期、每轮最多 50 条发送，待通知队列同样限制为 200 条。高负载下只保证最近记录；DroppedCount 包括历史淘汰和被安全限额拒绝的观察，不是业务事件总数。属性新值／事件摘要最多保留约 1024 个字符。

## 验证

`BreakpointIntegrationTests` 与 `BreakpointSourceTests` 共 20 项真实 Avalonia Headless 服务测试，覆盖：

- 六种命中计数条件和新值摘要。
- readonly 属性及严格的 owner-qualified ID。
- 启用、禁用、计数清零、替换成员及移除旧监听。
- 无效更新保留原断点。
- Tunnel／Bubble 路由完成后只计一次，并保留最终 Handled。
- 条件满足后一次性移除。
- 未附加调试器的暂停拒绝和安全禁用。
- 200 条历史限制及丢弃统计。
- 目标和历史不阻止控件 GC，过期状态可查询。
- Dispose 后全局属性和路由事件订阅解除，服务本身可被 GC。
- 断点日志的同步重入抑制。
- Source 全局／指定源、receiver 全局／指定接收节点的独立组合，旧 ObjectId 语义。
- 实际 Source 覆盖预设参数、同参数连续 Raise、不同参数嵌套及同参数递归 Direct／Tunnel＋Bubble。
- 源条件更新、禁用、计数比较、一次性移除、源 GC 及无效更新原观察保留。

另有 1 项协议旧定义兼容回归、3 项真实 BreakpointsView 页面回归和 1 项真实 Agent＋stdio MCP 回归，覆盖创建、双条件回显、源切换、命中导航、全局模式、无效输入、禁用／启用、归零和一次性移除。同定义刷新期间保持 ListBox 选择与更新命令可用；按 ID 原位同步避免清空列表取消用户正在按压的更新按钮，确定性回归已先红后绿。已有断点页面和快照竞争回归继续保留。

2026 年 10 月 25 日在 Windows、.NET 10 和 Avalonia 12.1.4 上进行专项验证。测试不执行真实 Debugger.Break；附加 IDE 调试器后的交互暂停仍需专门人工验证。实现依据公开 [EventRoute.cs](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Interactivity/EventRoute.cs)、[RoutedEvent.cs](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Interactivity/RoutedEvent.cs) 和 [RoutedEventArgs.cs](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Interactivity/RoutedEventArgs.cs)，不依赖私有反射或应用预期值代替真实路由。

