# 注册事件目录与路由观察

Inspector 的“日志与事件 → 事件路由”子页通过目标进程的 `RoutedEventRegistry.Instance.GetAllRegistered()` 获取真实目录。目录包含事件身份、owner 类型、参数类型和 `RoutingStrategies`，不会扫描业务对象或调用业务 `ToString()`。

默认选择 Pointer、Keyboard、Focus、Control、Gesture、DragDrop 六组。可取消组，改选目录中的单个事件；组与单项取并集。组按已注册事件的名称及 owner 元数据分类，不声称是 Avalonia 官方定义的分组。目录刷新与捕获计时器会接入后续注册的事件。`ActivityStartRequest.EventSelection` 提供精确 `EventIds`、`Groups` 和 `IncludeAll`；旧 `EventName` 模糊筛选仍兼容，显式 `EventSelection` 优先。未指定选择与旧筛选时启用默认六组。

“保存为默认”显式保存当前事件身份与分组到 `%LOCALAPPDATA%/AvaloniaDevTool/event-selection.json`，下次连接恢复；当前会话仍可临时覆盖，并通过“默认六组”恢复初始选择。文件只含 `eventIds`、`groups`、`includeAll`，不含令牌、业务对象 ID 或日志。写入使用 UTF-8 临时文件原子替换，读写限制 64 KiB；配置损坏或不可读时提示并回退六组。目标尚未注册的已保存事件身份会明确列出为未应用，注册后需刷新并重新开始捕获。所有筛选修改都在下一次开始捕获时生效。

开始捕获后，一次实际 raise 对应一个 `EventRouteInfo.RouteId`。同一次事件的 Tunnel、Bubble 观察聚合到同一条记录，Direct 独立完成；嵌套 raise 使用不同身份，相同参数对象在完成后再次 raise 也分配新身份。事件源与路由节点通过弱对象注册表的 ID 定位，可从详情分别跳到源对象和实际接收节点。对象回收后，刷新显示失效状态，定位操作返回已有的对象失效提示。

## Handled 的观察边界

实现使用公开 `RoutedEvent.AddClassHandler(typeof(Interactive), ..., handledEventsToo: true)` 与 `RoutedEvent.RouteFinished`。

Avalonia 12.1.4 的 `EventRoute.RaiseEventImpl` 在进入每个接收节点时发布 `Raised`，随后执行该节点的实例处理器；类处理器也是 `Raised` 的订阅。因此每个节点的 `HandledObserved` 是诊断类处理器订阅被调用时的值，早于该节点的实例处理器，但可能晚于更早订阅的类处理器。`HandledChangedSincePreviousObservation` 仅表示相邻观察之间发生了变化，不能识别哪个实例处理器修改了状态，也不提供伪造的 handler 身份。`ReceiverObjectId` 是节点身份，旧活动条目的 `HandlerObjectId` 同样表示路由接收对象。

框架为 Tunnel 与 Bubble 分别发布 `RouteFinished`。聚合保留 `FinishedStrategies` 与各策略的 `PhaseFinishes`（完成时的 Handled 值及相邻观察区间变化），只在最后的已声明策略完成后设置 `IsFinished`；`FinalHandled` 是最近一次策略完成时观察到的值。抛出异常而未正常完成的路由保持未完成状态，达到活动 TTL 后显示 `Expired`。

官方实现：[EventRoute.cs](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Interactivity/EventRoute.cs)、[RoutedEvent.cs](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Interactivity/RoutedEvent.cs)、[Interactive.cs](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Interactivity/Interactive.cs)。

## 资源与生命周期

路由历史独立限制为 200 次事件、2 MiB UTF-8 JSON 预算、每次最多 128 个观察节点。超限淘汰最旧记录；单路由观察节点超限保留已观察片段并标记 `IsTruncated`。活动路由 TTL 为 30 秒，历史 TTL 为 300 秒，状态返回实际预算、淘汰和截断计数。计时器、快照和状态请求执行过期清理。

关联表为 `ConditionalWeakTable<RoutedEventArgs, ...>`，值只保留路由 ID、代次和重入栈；历史保存安全类型/名称文本及弱注册表 ID，不持有参数、源控件或接收控件。停止解除事件订阅，并将未完成路由标为 `Stopped`；清空增加捕获代次，清空前仍在运行的事件不能从后续节点重建旧路由。

旧活动历史的 500 条/2 MiB、单条 128 KiB 和通知队列 100 条/512 KiB 限额继续有效。路由更新复用原通知队列与同一个异步发送泵，不创建独立无界任务队列；慢连接可能丢失更新，可通过刷新快照恢复当前有界历史。

RPC：`events.catalog`、`events.routes`、`events.status`；通知：`events.routeUpdated`、`events.state`。该功能仅原生后端可用，外部只读后端保持禁用。
