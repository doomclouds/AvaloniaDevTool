# 原生双击与窗口焦点修正

2026-10-25。原断言保持：原生双击需要实际产生按钮业务计数和DoubleTapped；窗口切换需要文本进入指定编辑框，不能仅凭SendInput返回数量判定业务成功。

## 已取得的故障证据

双击真实失败样本 `avdt-native-input-failures/20261025-023814-380-1f275365edc24350bcf8bea5ce3f0146.json` 显示：预期Clicks=4，实际停在2；Root tunnel和按钮事件记录里只有此前的framework两次点击，没有任何native down/up，最后发生focus-lost。这排除了“只吞第二次业务Click”的解释，但该样本没有注入后的物理光标和窗口命中快照，不能唯一确定操作系统层原因。

后续核对INPUT结构x64大小40、union偏移8均正确；归一化公式与FlaUI差异约1像素，不足以解释整批原生事件未到本窗口。本机200%缩放下，unaware的桌面指标1500×938与physical/PMv2的3000×1876不同，存在必须消除的坐标域不确定性；成功probe中实际落点与目标一致，**没有把DPI宣称为原始失败样本已经证实的根因**。

焦点另有独立证据：最初51/80误拒来自raw foreground HWND是同窗口child，规范化GA_ROOT后解决该类误判；后续仍有3/80落在previous的另一根窗口，调用顺序显示最终root拒绝发生在目标元素SetFocus之前。仅前移SetFocus还不能消除全部异步落地延迟。

## 公共路径修正

### 指针落点确认

- 先独立投递起始Move，立即读取真实物理光标位置。
- 未抵达目标（误差超过1像素）时拒绝，不发送按钮或按键；光标可能已移动，错误消息明确说明这一点。
- 再检查前台exact root、进程身份、物理按压状态和路径遮挡，然后投递原按下/释放批次。
- 双击的原四个按钮事件保留，没有增加发送sleep；InputCount累计真实插入的Move与后续事件。
- NativeCoordinateSpace在输入和坐标API调用期间明确设置physical线程DPI上下文，并在成功/异常后恢复。Unaware/System/PM/PMv2四种起始context的归一化与还原均有定向测试。

受控ClipCursor回归先验证目标点确实被排除，要求input_not_arrived且业务值不变；finally恢复原clip，随后正常Click必须实际生效。这证明了旧路径缺少落点确认时的防错缺口，不伪装成原失败的post-cursor观测。

### 键盘焦点事务

- 一次SetForegroundWindow后，对请求的键盘元素执行一次合法UIA SetFocus。
- barrier后最多750ms观察exact root和HasKeyboardFocus落实，不重复夺前台或重复SetFocus。
- 第三根窗口抢占、身份/启动时间变化、owner禁用或物理修饰键/鼠标按住时立即拒绝。
- 保留注入前最后复检，不把同PID、owned关系或API返回true直接视为成功。

定向测试包括80次双窗口Ctrl+A和中文/emoji文本读回，以及第三owned root抢占时拒绝且两个编辑框均不被误写。

## 诊断与验证口径

WaitText先检查MCP IsError，再在fixture仍存活时保存fresh observed、pointer trace和光标/窗口信息；不再在解析错误结果时提前丢失诊断。对应模拟错误单测只证明证据保全机制，明确不作为真实桌面证据。

临时worker探针和环境开关已清理。真实MCP十轮同点双击、独立DoubleTapped、Clip防错/恢复、DPI恢复与原生测试分别保留。最终是否全部通过，以同一源码指纹的 [测试记录](../../testing.md) 和本轮TRX为准，不能把不同版本的专项相加成全绿。

## 一手资料

- [Win32 MOUSEINPUT结构、absolute与virtual-desktop坐标](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput)
- [UIA物理坐标与屏幕缩放](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-screenscaling)
- [线程DPI上下文设置及恢复](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddpiawarenesscontext)
- [跨输入队列窗口激活与barrier](https://devblogs.microsoft.com/oldnewthing/20161118-00/?p=94745)

当前修正解决的是输入定位/焦点确认路径；业务完成仍需读取实际控件状态或文件I/O结果，不改变NativeInputResult.ApplicationEffectVerified=false的含义。
