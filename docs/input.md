# 框架合成输入

应用侧 `input.send` 支持 Text、KeyDown、KeyUp、PointerMove、PointerDown、PointerUp、Wheel、Click。服务直接向选中的业务 Control 派发 Avalonia 路由事件，Source 为该控件；文本与按键先请求框架焦点。它不调用 OS 全局输入、不使用系统鼠标设备，不直接设置 TextBox.Text，也不直接执行 ICommand。

## 输入语义

InputResult.Delivered 表示事件已派发；Handled 是路由事件的实际处理状态。它不保证发生某个特定业务效果，例如非文本控件可能不处理 TextInput，右键不等于一定弹出系统菜单。Click 按顺序派发 PointerMoved、PointerPressed、PointerReleased；Button 的业务 Click 由框架自身的真实事件处理与 hit-test 产生。

PointerDown／PointerUp 必须在同一个目标上使用同一个按钮配对。每个会话只支持一个按住的按钮，新的 down 或 click 不会静默覆盖现有序列。会话拥有独立 Pointer，使用公开 Pointer.GetNextFreeId 分配 ID；基类 PlatformCapture 是空实现，不接触 MouseDevice 或 OS 捕获。按下时建立该指针自己的捕获，抬起或 Click 完成后释放；Dispose 调用 Pointer.Dispose，正常框架 CaptureLost 路径会清理按下状态。

X、Y 为目标控件的局部坐标，省略时使用中心点；通过 TranslatePoint 映射到所在 TopLevel，再构造公开 PointerEventArgs。坐标必须有限且位于目标局部 Bounds 内。本版不支持通过越界坐标模拟拖出控件，也不实现完整原生拖放、双击识别、系统热键、IME 组合输入或原始设备 hit-test 流。Wheel 的 DeltaX、DeltaY 必须有限。

Text 必须非空，最长 2048 个 UTF-16 code units。KeyDown／KeyUp 使用 Avalonia Key 的命名值，拒绝数值字符串和组合快捷键文本；修饰键通过独立 Modifiers 列表提供，可使用 Alt、Control、Shift、Meta 或独立 None。重复、未知或混合 None 的列表会失败。按钮支持 Left、Right、Middle、XButton1、XButton2，拒绝 None 和数值名字。

键盘事件不自动转换成文本字符，输入字母应发送 Text。文本和按键要求控件能够获得框架焦点。只读 TextBox 拒绝 Text；Key 仍由控件处理，因此只读框可接受选择等操作，但不会把 Back 伪装为成功修改文本。

## 前置条件与错误

目标必须是已附加、有效启用、有效可见且宽高为有限正值的业务 Control。已失效的句柄、分离控件、禁用或隐藏祖先、非法尺寸、非有限或越界坐标均返回明确 RPC 错误。诊断覆盖层不是输入目标。

| 错误 | 含义 |
| --- | --- |
| invalid_input | 名称、参数组合或文本为空 |
| input_limit | 文本超过上限 |
| unsupported_input | 未实现的事件类型 |
| unsupported_object | 根不是业务 Control |
| object_expired | 句柄未知或已回收 |
| detached_object | 目标没有附加视觉树／TopLevel |
| disabled_target / hidden_target | 自身或祖先禁用／隐藏 |
| invalid_layout | 目标没有有效尺寸 |
| invalid_coordinates | 指针坐标或滚轮 delta 无效 |
| unsupported_transform | 局部位置无法映射到 TopLevel |
| focus_failed | 键盘目标不能获得焦点 |
| readonly_target | 向只读 TextBox 发送 Text |
| input_state | 指针序列未配对或按钮／目标不一致 |
| input_failed | 控件输入处理器抛出异常 |

服务创建、Send 和 Dispose 均在 UI 线程调用。AgentSession 应复用已有 UI 调度，并在断开时 Dispose 服务，释放独立指针捕获。

## 验证

InputInspectionTests 使用项目现有的 Skia Headless 环境，验证真实 TextBox 的文本编辑与按键、Control 修饰键选择、Button 的业务 Click 次数、指针配对与释放、变换后的局部坐标、Wheel delta 与处理状态、修饰键、独立指针不替换 Headless 设备指针捕获、只读／禁用／隐藏条件，以及名称、大小、坐标与失效对象错误。

实现依据为本机 Avalonia 12.1.4 的实际公开构造器与类行为，未引用原版 DeveloperTools 的实现代码。产品 Agent 不引用 Avalonia.Headless；只有测试使用它。

