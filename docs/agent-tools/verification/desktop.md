# Windows 真实桌面 MCP 冒烟验证

2026-10-25 08:25:27–08:25:30（Asia/Shanghai）完成，16 项检查全部通过。使用真实 Windows 桌面 Sample 与本项目 official CLI 的 stdio MCP；生产、测试源码和构建工件均未修改。本次结果补充统一构建与测试记录，不将 Headless 测试当作桌面证据。

原始证据：[result.json](../../../artifacts/agent-tools-2026-10-25/desktop/result.json)、[stdio.jsonl](../../../artifacts/agent-tools-2026-10-25/desktop/stdio.jsonl)、[驱动脚本](../../../artifacts/agent-tools-2026-10-25/desktop/driver.py.txt)、[自有进程退出复核](../../../artifacts/agent-tools-2026-10-25/desktop/cleanup-verification.json)。最终执行包含 57 条客户端消息，CLI 和 Sample 的 stderr 均为空。

## 环境与进程

- Windows 10.0.26100，Avalonia 12.1.4.0，桌面会话 ID 7。
- 使用统一验收工件 `C:\Users\10062\AppData\Local\Temp\avdt-agent-acceptance\bin\<Project>\debug`，未重新构建。
- Sample：PID `29248`，主窗 HWND `8192076`，窗口标题 `Avalonia 测试工作台`，原生窗口类 `Avalonia-0b661ee7-4c6a-47fd-92f7-077eec6331c5`。
- official CLI：PID `20748`，由 `dotnet AvaloniaDevTool.Cli.dll mcp --profile official --pipe <唯一管道>` 启动。
- 管道：`avalonia-devtool-desktop-smoke-d1d1c6950f7a4e099529e9d571e89615`。
- 进程启动采用 `SW_HIDE`、`CREATE_NO_WINDOW`；驱动只枚举并隐藏自有 Sample PID 的原生窗口。协议交互不使用用户桌面点按。
- 最终 CLI 关闭 stdin 后正常退出，代码 `0`。检查完成后由驱动终止自有 Sample，记录的退出代码 `1` 来自清理操作。最终运行及三次驱动准备运行的全部 8 个自有 PID 均确认已退出；未操作用户进程或窗口。

工件 SHA-256：

| 文件 | SHA-256 |
| --- | --- |
| `AvaloniaDevTool.Sample.dll` | `5530f92be0ed997ff41972bbe40835b0e4f1cb635c80ac5089ef4e55a8ca5d73` |
| `AvaloniaDevTool.Cli.dll` | `51ce5eb56e4897df01c393f980372fbe025452170a4e0ec6ef036a6ba68f17a7` |
| `driver.py.txt` | `d61d30defb1e74759c51f43ced3a23e9b58f2ae24db38a34320ceeb17675a474` |

## 步骤与真实结果

1. 启动 Sample 的真实 `StartWithClassicDesktopLifetime` 入口，并通过原生 HWND 确认实际桌面主窗。当前 [App.axaml.cs](../../../samples/AvaloniaDevTool.Sample/App.axaml.cs) 只设置 `PipeName`、`ApplicationName` 和 `LogSources`，没有配置 `TopLevelProvider`；Agent 从桌面 lifetime 的 `Windows` 获取根节点。
2. 进行 MCP `initialize`（协议版本 `2025-06-18`）与 `notifications/initialized`，`tools/list` 返回当前 official surface 的 16 个工具，与 `OfficialTools.ToolNames` 一致。
3. 先调用无参 `attach-to-app` 发现唯一管道，再使用返回的应用 ID 连接。会话报告的进程号等于实际 Sample PID `29248`，应用名为 `Avalonia 测试工作台`。
4. `tree(depth: 1)` 返回真实根 `SampleMainWindow`，类型 `AvaloniaDevTool.Sample.MainWindow`，初始节点 ID `4294967297`。通过 `search` 定位 `UserNameInput`、`GreetingText`、`PreviewButton`、`StatusText` 和 `BoxModelTarget`，没有注入测试根提供器。
5. `props` 读回用户名 `小陌`、问候语 `你好，小陌。这个文本由 UserName 绑定生成。`。使用从属性结果取得的完整 ID `Avalonia.Controls.Border:Background` 调用 `set-prop`，将 `BoxModelTarget` 背景从 `#ffeaf0ff` 改为 `#ff1a7f37`；再次读取显示 `valueSource: DiagnosticOverride`。
6. 对 `UserNameInput` 发送 `End` 的 KeyDown/KeyUp，再发送 Text `桌面冒烟`。控件文本变为 `小陌桌面冒烟`，独立绑定目标 `GreetingText` 同步变为 `你好，小陌桌面冒烟。这个文本由 UserName 绑定生成。`。随后发送 `Back` 的 KeyDown/KeyUp，文本和问候语都真实删去末尾的 `烟`。
7. 对 `PreviewButton` 发送 Click，`StatusText` 实际变为 `预览按钮收到点击，当前模式：标准模式，进度：64%。`，证明应用 Click handler 执行。检查依据为业务状态读回，未仅凭 `delivered: true` 判定成功。
8. `screenshot` 返回实际目标的 `image/png`。`BoxModelTarget` 图片为 `360×130`，背景像素 `(6,6)` 从 RGBA `(234,240,255,255)` 变为 `(26,127,55,255)`；主窗图片为 `1180×850`，已人工查看输入后的用户名、问候语及底部点击状态。
9. 调用 `detach`。管道重建期间重新发现应用，共两次发现调用，使用新返回的应用 ID `2` 重新连接；不复用旧发现 ID。进程仍为 `29248`，会话 ID 从 `355ed03a4745496eb2ea3d88282da2a7` 变为 `3cb8ba4d0eb94198b279e482ce5e645e`。
10. 重新 `search` 获取新会话节点 ID，再读取背景：恢复为 `#ffeaf0ff`、`LocalValue`，截图 SHA-256 与修改前完全相同。真实 TextInput 已写入的应用用户名仍为 `小陌桌面冒`，不会被 detach 回滚；驱动随后通过真实 Back 输入清理新增文字，确认文本和问候语恢复基线后再次 detach，最后清理自有进程。

## 图片证据

| 图片 | 尺寸 | 字节数 | SHA-256 |
| --- | --- | ---: | --- |
| [主窗基线](../../../artifacts/agent-tools-2026-10-25/desktop/main-baseline.png) | 1180×850 | 85443 | `e00717720541fd5b942fcf445cccbcd676517f9f06dd29e040c75e5e6cda9f3a` |
| [目标基线](../../../artifacts/agent-tools-2026-10-25/desktop/box-baseline.png) | 360×130 | 3063 | `a55dd52badde6f254830e12937b16c644d36b75be4e300d7a11c7fdbed7caf6f` |
| [目标属性覆盖](../../../artifacts/agent-tools-2026-10-25/desktop/box-override.png) | 360×130 | 3309 | `fef409fdd5aab3871fe7db96ebb93205f5c3de3c3eddda24086cdb740cd58bd8` |
| [输入后的主窗](../../../artifacts/agent-tools-2026-10-25/desktop/main-input-state.png) | 1180×850 | 87681 | `f20c41cce96ceea625ffacd092498611c283f360334d7a215478474b059aa726` |
| [断开恢复后的目标](../../../artifacts/agent-tools-2026-10-25/desktop/box-after-detach.png) | 360×130 | 3063 | `a55dd52badde6f254830e12937b16c644d36b75be4e300d7a11c7fdbed7caf6f` |

![输入后的真实桌面 Sample 控件内容](../../../artifacts/agent-tools-2026-10-25/desktop/main-input-state.png)

## 范围与限制

- 本次验证的是实际 Win32 桌面进程中的 Avalonia 控件、绑定、应用 handler 和渲染；`input` 使用独立 session pointer 与 Avalonia routed events，不是操作系统原生键鼠注入，未测试原生 OS 输入或 file picker。
- `screenshot` 是指定控件的 `RenderTargetBitmap` 内容，96 DPI，不是桌面屏幕截图。原生窗口已隐藏，Avalonia 的逻辑窗口仍处于显示状态；因此本次不声称测试了用户实际可见桌面的遮挡、命中测试或显示缩放。截图边界仍受单边 2048、总像素 2,097,152、PNG 1 MiB 限制。
- detach 恢复 session 所有的诊断属性覆盖；TextInput 造成的 TwoWay 绑定源和应用业务状态变化属于应用自身状态，须由业务操作还原或随自有样例退出清理。
- 保留 [attempt-01](../../../artifacts/agent-tools-2026-10-25/desktop/attempt-01/result.json)、[attempt-02](../../../artifacts/agent-tools-2026-10-25/desktop/attempt-02/result.json)、[attempt-03](../../../artifacts/agent-tools-2026-10-25/desktop/attempt-03/result.json) 的准备运行记录：分别修正驱动预期工具数量、同名附加属性的选择方式、以及 detach 后立即复用旧发现 ID 的时序。只调整了系统临时目录中的驱动，未修改产品实现。
