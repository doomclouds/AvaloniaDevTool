# Avalonia 测试工作台

用于验证诊断客户端与应用内 Agent 的连接、控件树、属性编辑、点击拾取、样式绑定、资源、资产、日志事件、指标和断点。

```powershell
dotnet run --project samples/AvaloniaDevTool.Sample -- --pipe avdt-sample
```

使用 Debug 配置运行，Agent 会监听 `avdt-sample`。未指定参数时，管道名称为 `avalonia-devtool-{进程 ID}`，窗口顶部可以复制。

由 MCP 客户端连接当前应用并调用诊断工具。使用自定义管道名时，应将该管道名配置给 MCP 服务端；底层诊断客户端也可直接使用窗口顶部复制的管道名。

## 验证步骤

1. 通过诊断客户端连接当前应用，查询 Visual 和 Logical 控件树。
2. 选中 `UserNameInput`、`PreviewButton` 或 `ProgressSlider`，修改属性并观察目标控件。
3. 对 `BoundText.Text` 应用诊断修改，点击“更新绑定源”，再清除修改，检查绑定值和来源。
4. 开启点击拾取，点击输入框、按钮或模板子元素，确认选择和高亮跟随；断开连接后高亮应移除。
5. 添加、移除动态节点并打开第二窗口，检查根列表、子节点和过期句柄处理。
6. 打开 Flyout，观察弹出层的拾取与树结构；弹出层能力取决于首版 Agent 的根枚举范围。
7. 查询 `StyleTarget` 的样式绑定，切换 `emphasized` class 后重新查询，观察活动值帧和 setter；`ValidationInput` 可输入负数或非整数，验证校验错误。
8. 通过资源诊断接口修改 Application.Resources 内的 `SampleAccentBrush`、`SampleResourceText`；目标 `DynamicResource` 立即更新。若再点击“应用主动更新资源”，诊断客户端恢复时应报告冲突并保留应用的新值。
9. 查询 `AvaloniaDevTool.Sample` 的资产，分别读取 `sample.json`、`sample.svg` 源码和 `sample.png` 图片。
10. 通过日志事件诊断接口开始捕获后发送 Warning、Information 或后台日志；最低级别影响日志筛选。事件 ID 用于断点时必须完整填写 `Avalonia.Controls.Button:Click`，活动捕获则可用 `Click` 子串。
11. 通过指标诊断接口以 `AvaloniaDevTool.Sample` 筛选 Meter 并开始，点击“生成一组测试指标”；观察 Counter 原始增量、Histogram 合成样本与 ObservableGauge。统计仅覆盖当前保留窗口。
12. 通过断点诊断接口选择所选对象的属性或完整事件 ID，设置累计命中次数条件。暂停默认关闭，开启前需要附加调试器；这不是 CLR 调试器。
13. 通过树搜索定位命名控件，再请求焦点、滚动到控件、切换并恢复伪类或获取所选控件 PNG 快照。

Release 配置不会启用 Agent。此项目不依赖官方 `AvaloniaUI.DiagnosticsSupport`。

