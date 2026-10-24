# 上下文附加属性

Inspector 默认开启“仅相关附加属性”。已知规则仅针对直接视觉父布局实际消费的附加属性：Grid 的 Row／Column／RowSpan／ColumnSpan，Canvas 的 Left／Top／Right／Bottom，DockPanel.Dock，以及 RelativePanel 的相对定位／对齐属性。模板部件按真实视觉父布局判断，不使用 TemplatedParent 代替父布局。

未知自定义附加属性、继承型配置、ToolTip、Automation、ScrollViewer 模板设置和 Grid.IsSharedSizeScope 不做猜测过滤。关闭开关可看所有注册属性；已知不适用的行会解释要求的父类型及当前父类型。过滤不修改目标值，不禁止显式设置当前布局不消费的属性。

Agent 的 `PropertiesRequest.Contextual` 默认 false，旧 `GetPropertiesAsync(id)` 和 MCP `props` 保持原行为；显式 true 才在目标端过滤。属性 `Presentation.Context` 是可选元数据，表示已知规则和是否适用；null 表示此工具没有限定该属性用途，不能解释成不存在该属性。CLR getter 仍只有 IncludeClr 明确开启才读取。

Inspector 请求完整元数据并在本地筛选，隐藏的属性行保留身份和草稿。因此开关往返不会丢失尚未应用的 Canvas.Left 等值。选中对象移动到另一个父布局时，Agent 每 150 ms 检查已请求对象的弱父身份，发送属性失效通知；界面重读元数据后调整显示。重建树时，同一稳定 ID 若移入未展开分支，会保留检查行直到新树定位完成，避免临时选空销毁草稿；只有确定失效或选择其他对象才清理。该检查不轮询整棵树，也不保留强父对象快照。异步属性响应继续受当前对象、CLR 模式和请求序号约束。

MCP `props` 新增 `contextual`、`includeClr` 参数，均默认 false，原有 propertyNames 筛选保留。Native AOT JSON 元数据包含新增结构；完整 AOT 兼容边界另见 [AOT 记录](native-aot.md)。

规则来自固定 Avalonia 12.1.4 的公开 [Grid](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/Grid.cs)、[Canvas](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/Canvas.cs)、[DockPanel](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/DockPanel.cs) 和 [RelativePanel](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/RelativePanel.cs) 布局实现。这里实现的是本项目的白名单规则，不宣称恢复原版的全部上下文分类器。

验证包括 3 项真实 Agent 管道测试：四种父布局／未知属性与旧默认行为、同一控件换父及分离通知、模板部件视觉父判断；1 项真实 MainWindow 复选框 pointer 测试：隐藏草稿往返、换父后自动更新和保留草稿；1 项 SDK stdio MCP 测试：旧默认、显式过滤及 CLR opt-in 不提前执行 getter。专项 5／5 通过。完整同批次结果见 [测试记录](testing.md)。
