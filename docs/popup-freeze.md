# 弹层冻结

完成状态：**H01 P（部分实现）**。普通 Popup / 标准 Flyout 的 light-dismiss 冻结与恢复已实现；ContextMenu、ToolTip、transient Flyout 的独立自动关闭仍有下述公开 API 边界。

Inspector → 探索 → 弹层冻结，可对全部配置根或单个配置根抑制弹层的自动 light-dismiss。MCP 提供 `popup-freeze`、`popup-freeze-set`、`popup-freeze-resume`；Client 对应 `GetPopupFreezeAsync`、`SetPopupFreezeAsync`、`ResumePopupsAsync`。

冻结使用 Avalonia 公开 `Popup.IsLightDismissEnabled` 和 `SetCurrentValue`，抑制 Popup 自身对点击弹层外部、窗口失活、平台失焦和非客户区点击的关闭处理。正常输入和焦点移动继续运行。恢复仅还原本会话仍拥有的设置；断开连接、配置根移除和会话释放也执行恢复。已经关闭的弹层不会重新打开。

## 范围与状态

`PopupFreezeSetRequest(IsFrozen: true, RootId: null)` 冻结所有 `DiagnosticsAgentOptions.TopLevelProvider` 配置根及后续新增配置根。指定 `RootId` 只影响该配置根。配置根的 ID 可从 `PopupFreezeStatusDto.Roots` 读取。原生 `PopupRoot` 是呈现根，不额外扩大配置根范围；嵌套弹层通过各层公开 `OpenedPopups` 递归发现。

新打开的弹层通过公开 `Popup.IsOpenProperty.Changed` 在打开调用期间接管，不需要等待 150ms 刷新。弱引用保存配置根和弹层。关闭的弹层立即解绑并移出状态；服务停止或断开时释放全局订阅。

状态返回各配置根的冻结选择，以及当前弹层的宿主模式、实际 `IsLightDismissEnabled`、工具所有权和行为边界。应用原本禁用 light-dismiss 的弹层保持原值，不计作工具覆盖。应用后续配置改写会放弃工具所有权并记录冲突；不会在下一次刷新重新覆盖。

## 各类弹层的实际语义

| 类型 | Native / Overlay | 冻结效果 | 保留的关闭路径 |
| --- | --- | --- | --- |
| 普通 `Popup` | 均支持 | 抑制外部点击和窗口失焦等 light-dismiss | `Close()`、`IsOpen = false`、目标卸载 |
| 标准 `Flyout` | 均支持 | 通过公开 `Flyout.Popup` 对应的底层 `Popup` 抑制 light-dismiss | `Hide()`、`IsOpen = false`、业务代码关闭、目标卸载 |
| `ContextMenu` | 均可发现，菜单交互关闭受限 | 通过公开 `OpenedPopups` 发现底层弹层并抑制 Popup 的 light-dismiss | 菜单自身 `DefaultMenuInteractionHandler` 的根点击 / 失焦 / 非客户区处理仍可关闭；Overlay 外部点击仍会关闭。`Close()`、菜单项选择、Escape、目标卸载继续生效 |
| `ToolTip` | 均可发现，生命周期支持受限 | 它原本就没有启用 light-dismiss；状态明确返回 `ToolTipService` 边界 | 内部服务直接写 `ToolTip.IsOpen`，指针移出、点击、计时及应用关闭继续生效 |
| transient `Flyout` | 均可发现，移出关闭受限 | 底层 light-dismiss 仍被抑制 | `TransientWithDismissOnPointerMoveAway` 的内部 `HideCore(false)` 继续生效 |

ContextMenu 自带的交互处理器、ToolTip 服务和 transient Flyout 的内部服务关闭没有公开的“自动关闭原因”标记。用内部 `Popup.Closing` 一律取消会同时拦截应用主动关闭、破坏所有者状态或目标卸载清理，因此此实现保留这些路径并在 Inspector/MCP 中说明限制。原生菜单的外部点击不会经过菜单自己的 `PopupRoot`，Overlay 菜单则共享所有者根，二者真实输入测试有不同的结果。

针对菜单还核对了公开 `IMenuInteractionHandler.Attach/Detach(MenuBase)`：现有实例的处理器只能通过 `protected internal` 只读 `MenuBase.InteractionHandler` 或 `internal IMenu.InteractionHandler` 获取；公开注入构造函数只适用于新建菜单。`DetachCore` 会一起移除键盘、菜单项点击、访问键、子菜单移动和单选组处理，不能在保留原菜单交互的前提下直接脱离。即使新建 `DefaultMenuInteractionHandler` 子类，原生平台失焦处理 `TopLevelLostPlatformFocus` 也是不可覆写的私有方法。实现因此没有禁用整个菜单处理器，也没有替换应用菜单实例。

配置恢复通过公开 `GetDiagnostic(...).IsOverriddenCurrentValue` 复核所有权。普通属性 setter、绑定更新和清除值都能撤销当前值覆盖，包含应用写入同一有效值 `false` 的情况。应用再次以 `SetCurrentValue(false)` 写入完全相同的当前值覆盖不产生可区分的公开身份或变更事件；这种情况无法与工具覆盖区分，恢复可能回到原值。应用需要明确接管配置时可使用属性 setter、`SetValue`、绑定更新或先关闭弹层。

## 公开源码依据

- [ClassicDiagnostics.MainWindow.ToggleFreezePopups / HandlePopupClosing](https://github.com/Sylinko/ClassicDiagnostics.Avalonia/blob/9199c26495ed8708204c6caa4a6d2ec62093fbd1/src/Views/Shell/MainWindow.axaml.cs)：通过内部 `Closing` 取消关闭；此处只复用其交互目标，未复制该实现。
- [Avalonia 12.1.4 Popup](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Controls/Primitives/Popup.cs)：`CloseCore` 与 `WindowDeactivated`、`WindowLostFocus`、`PointerPressedDismissOverlay`、`ListenForNonClientClick` 的区别，以及公开 `OpenedPopups`。
- [PopupFlyoutBase](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Controls/Flyouts/PopupFlyoutBase.cs)、[ContextMenu](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Controls/ContextMenu.cs)、[DefaultMenuInteractionHandler](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Controls/Platform/DefaultMenuInteractionHandler.cs)：拥有者关闭、菜单独立根输入 / 失焦、键盘处理与 transient 自动关闭路径。
- [IMenuInteractionHandler](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Controls/Platform/IMenuInteractionHandler.cs)、[MenuBase](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Controls/MenuBase.cs)、[IMenu](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Controls/IMenu.cs)：公开 Attach/Detach 的粒度、现有处理器的可访问范围与只读属性。
- [ToolTipService](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Controls/ToolTipService.cs)、[ToolTip](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Controls/ToolTip.cs)：指针 / 计时关闭直接改变 `ToolTip.IsOpen`，不依赖 `IsLightDismissEnabled`。
- [AvaloniaPropertyValue.IsOverriddenCurrentValue](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Diagnostics/AvaloniaPropertyValue.cs)：公开当前值覆盖身份诊断。

本功能仅使用已核对的公开源码与公开运行时 API，不依赖商业包逆向或生产反射。

## 验证

专项测试包含原生和 Overlay 的真实外部指针输入、已接线平台焦点丢失回调、标准 Flyout / ContextMenu 的主动关闭、ToolTip 的真实指针生命周期、新嵌套弹层即时接管、跨配置根隔离、同值配置改写、绑定继续更新、目标卸载、断开恢复、弱引用回收，以及 Inspector 可见按钮和真实 stdio MCP 往返。

```powershell
dotnet test tests/AvaloniaDevTool.Agent.Tests/AvaloniaDevTool.Agent.Tests.csproj --filter FullyQualifiedName~PopupFreezeTests
dotnet test tests/AvaloniaDevTool.Inspector.Tests/AvaloniaDevTool.Inspector.Tests.csproj --filter FullyQualifiedName~PopupFreezeViewTests
dotnet test tests/AvaloniaDevTool.Cli.Tests/AvaloniaDevTool.Cli.Tests.csproj --filter FullyQualifiedName~PopupFreezeMcpTests
```
