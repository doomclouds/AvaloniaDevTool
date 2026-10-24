# Avalonia 测试工作台

用于验证独立 Inspector 与应用内 Agent 的连接、控件树、属性编辑、点击拾取、样式绑定、资源、资产、日志事件、指标和断点。

```powershell
dotnet run --project samples/AvaloniaDevTool.Sample -- --pipe avdt-sample
```

使用 Debug 配置运行，Agent 会监听 `avdt-sample`。未指定参数时，管道名称为 `avalonia-devtool-{进程 ID}`，窗口顶部可以复制。

完整构建后，可在样例中按 F12 启动检查器并传入本应用管道名；也可单独运行 Inspector 手动连接。

## 验证步骤

1. 在 Inspector 中输入相同管道名称并连接，展开 Visual 和 Logical 控件树。
2. 选中 `UserNameInput`、`PreviewButton` 或 `ProgressSlider`，修改属性并观察目标控件。
3. 对 `BoundText.Text` 应用诊断修改，点击“更新绑定源”，再清除修改，检查绑定值和来源。
4. 开启点击拾取，点击输入框、按钮或模板子元素，确认选择和高亮跟随；断开连接后高亮应移除。
5. 添加、移除动态节点并打开第二窗口，检查根列表、子节点和过期句柄处理。
6. 打开 Flyout，观察弹出层的拾取与树结构；弹出层能力取决于首版 Agent 的根枚举范围。
7. 拾取 `StyleTarget`，切到“样式绑定”，切换 `emphasized` class 后刷新，观察活动值帧和 setter；`ValidationInput` 可输入负数或非整数，验证校验错误展示。
8. 在“资源”中修改 Application.Resources 内的 `SampleAccentBrush`、`SampleResourceText`；目标 `DynamicResource` 立即更新。若再点击“应用主动更新资源”，检查器恢复时应报告冲突并保留应用的新值。
9. 在“资产”中筛选 `AvaloniaDevTool.Sample`，分别预览 `sample.json`、`sample.svg` 源码和 `sample.png` 图片。
10. 在“日志事件”开始捕获后发送 Warning、Information 或后台日志；最低级别影响日志筛选。事件 ID 用于断点时必须完整填写 `Avalonia.Controls.Button:Click`，活动面板则可用 `Click` 子串。
11. 在“指标”以 `AvaloniaDevTool.Sample` 筛选 Meter 并开始，点击“生成一组测试指标”；观察 Counter 原始增量、Histogram 合成样本与 ObservableGauge。统计仅覆盖当前保留窗口。
12. 在“断点”中选择所选对象的属性或完整事件 ID，设置累计命中次数条件。暂停默认关闭，开启前需要附加调试器；这不是 CLR 调试器。
13. 通过树搜索定位命名控件；“交互截图”支持请求焦点、滚动到控件、伪类切换恢复及所选控件 PNG 快照。

Release 配置不会启用 Agent。此项目不依赖官方 `AvaloniaUI.DiagnosticsSupport`。

