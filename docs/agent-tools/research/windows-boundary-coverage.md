# Windows 边界与生命周期补证

2026-10-25。此轮在417项基线上补充已有安全分支的证据，并修复新发现的错误分类/读取时序问题；专项通过不替代当前源码的统一回归。

## 新增验证入口

| 测试 | 实际证据层与结果要求 |
| --- | --- |
| AgentInteractionBoundaryMcpTests | 19项实际stdio MCP + Headless：完整39工具集合、只读/隐藏/脱离树/零尺寸/坐标保护、cross-root拖动零部分事件、异常capture/pressed清理与恢复、菜单/Popup/Flyout动作后从树消失 |
| NativeWorkerLifecycleTests | 无GUI进程测试：inflight取消/超时实际worker PID退出，target保留；真实host异常退出时刻意继续持有stdin管道，排除EOF后验证真实watchdog回收 |
| NativePatternCoverageTests | 6项真实WinForms/UIA：外部无owner进程排除、密码Value=null与pattern集合、Scroll TopIndex、窗口状态与Win32双读回、受控pending完成/失败、9个修饰键真实OS释放 |
| NativeSessionOwnershipMcpTests | 2项真实stdio MCP + 两个自有Avalonia应用：A→B→A拒绝旧native ID的读取/写入/关闭，原窗口/内容不变；detach/target关闭后实际worker退出，同client可重新attach |
| NativeFileDialogBoundaryMcpTests | 7项真实stdio MCP + OS picker：等待超时、SetPaths后的外部修改拒绝、stale overwrite prompt、缺省.txt与重开、读写占用/ACL拒绝及恢复 |
| NativePickerProfileTests | 真实MCP打开OS picker后用独立native session验证：depth2/23节点时已匹配完整footer但Truncated=true，低预算Inspect/SetPaths拒绝且窗口/filename不变，默认预算仍能操作/取消 |
| NativeManagedGateMcpTests | 真实同步MessageBoxW保留原Click/RPC栈与managed gate；原click和queued props确实未完成期间，native仍能读取/关闭owned窗口，随后两者完成 |
| NativeFileFilterTimingTests | 合成provider、真实进程：3300ms RPC不被内部可见性预算误取消；没有选项时超时但worker仍存活可inspect/cancel；Expand已completed但仍Collapsed时仅Invoke唯一直接DropDown一次并验证选择。它不创建窗口，不是UIA真实界面证据 |
| NativeFileDialogAmbiguityTests | SDK/controller + 真实UIA worker：同PID两个独立STA/owner的真实CommonItemDialog时等待返回ambiguous_file_dialog；指定DialogId修改/取消一个，另一个窗口身份、文件名和类型不变 |
| NativeInputBatchTests | 4项模拟sender测试，含32个失败前缀：按返回count推算尚按下项、反序逐项释放，清理拒绝/异常不遮蔽原错误，正常事件/计数不变。不是OS部分发送失败的复现 |
| NativeWindowLifetimeMonitorTests | hook安装/释放和可控事件源单测：已送达销毁永久使旧stamp失效、迟到通知保守失效、owned进程订阅与PID变化、hook/消息泵故障时拒绝操作 |
| NativeHwndReuseTests | 真实Win32/UIA的确定性销毁/重建：观察根销毁epoch，按根与节点组合拒绝旧根/旧子ID；完整原HWND复用实验为Explicit入口，后续用户请求的独立Explicit已1/1通过，完整原HWND确实复用，旧ID被拒绝；不改写先前整批结果 |

## 真实 I/O 失败的控制方法

AutomationSample的可选 `--io-gate DIRECTORY` 仅用于验收：真实StorageProvider返回后、真实OpenRead/OpenWrite之前，驱动可通过arm/entered/release协议建立确定时序，最多等待30秒。没有参数或arm文件时不改变正常行为；没有替换StorageProvider、跳过picker或注入假异常。

占用测试由驱动对自有Temp文件持FileShare.None后释放gate，应用真实打开失败；ACL测试只更改自有Temp文件的DACL，明确不读写SACL/Owner或系统目录。finally从原始Access-only安全描述符恢复并核对SDDL，成功/失败都保留原文档及文件断言。初次fixture错误和恢复证据保留，不把测试未成功施加权限限制算成应用失败通过。

## 生产修正

1. 真实custom UIA provider抛E_ACCESSDENIED后，.NET可能转换成UnauthorizedAccessException。worker现保留access_denied分类，而不落到泛化native_error。
2. 文件profile原来只在结构不匹配时关注Truncated，可能在部分树已包含footer时继续操作。现在任何匹配但截断的picker均fail-closed；默认深度32/节点5000，显式预算便于调用方诊断。已用真实匹配profile且截断的树验证拒绝而非仅构造假数据。
3. Expand最多触发一次，随后轮询选项；可见性预算到期且仍无选项时，只有动作已completed、最新combo状态仍Collapsed，且存在唯一直接Button子节点、AutomationId=DropDown并支持Invoke，才回退Invoke一次并重置观察预算。pending或已展开时不回退，不按标题猜按钮。合成provider记录恰好一次Expand、一次DropDown Invoke和一次Select并验证读回；这不是该provider行为的真实Windows UIA复现。可见性轮询预算与原生RPC自身超时分离，避免内部短token取消一个仍在执行的请求并销毁共享worker/旧ID。此前独立临时副本恢复旧linked-token机制，同2项进程测试0/2，修正后2/2；该对照不证明历史真实UI瞬态的唯一原因，最新统一结果另见测试说明。
4. 同类问题也存在于WaitForDialog/WaitForClosed：内部等待期限原先直接取消正在执行的原生RPC。现在期限只决定是否继续轮询；在途请求保留NativeAutomationSession自己的操作超时，故总耗时可能超过轮询期限。调用者主动取消和RPC自身超时仍按session规则失效并回收worker。合成provider将一次windows请求延迟500ms、轮询期限设100ms，旧实现2/2失败，修正后两种等待均超时且同worker/ID仍可检查和取消；没有在真实UI上人为制造provider延迟。
5. 批次输入出错后，按SendInput返回数量推算成功前缀中仍按下的键/按钮，反序逐项尝试释放；cleanup再次拒绝或抛异常时继续其他释放，并保留原输入错误。Win32只返回数量，不承诺逐项成功索引，前缀是最佳努力清理假设；OS再次拒绝时无法保证恢复按压状态。模拟sender验证这些分支，真实OS正常输入仍由统一桌面回归核对。
6. 独立消息泵按PID接收窗口销毁并推进epoch，注册、读取和输入前校验根与节点的生命周期组合；父根过期时拒绝全部旧子ID，不能要求每个EDIT单独收到销毁事件。确定性真实销毁/重建已验证该分支；异步通知不是同步屏障，后续独立Explicit已验证完整根HWND复用后的旧ID读取及后续动作拒绝，详见窗口身份研究。

路径SetValue立即读回不一致也有合成provider测试：第一次写入被provider改变后返回file_dialog_readback_mismatch，没有Invoke接受、磁盘文件未创建；第二次精确读回成功且仍可取消。它补充了controller安全分支，真实picker外部后改名拒绝属于另一项已有桌面证据，不混为同一个场景。

## 仍保留的边界

真实多picker已在SDK/controller层补证；立即读回不一致和输入清理算法有合成provider/sender证据。后续实际HWND复用取得了旧根ID重定向的红样本，并已加入销毁epoch防护和真实父销毁/重建验证，见[窗口身份研究](native-window-identity.md)。后续独立Explicit在32,766轮后获得完整复用并通过，原始UIA proxy仍跨代读取而本工具旧ID拒绝；实际PID循环、强制OS部分SendInput插入失败及cleanup再次被OS拒绝后的真实按压状态仍未实测。

跨平台原生backend、IME composition、系统文件/数据DragDrop、任意自绘picker以及通用性能/视觉发版门禁也不由本轮推导完成。场景、同会话采集和发版报告的下一阶段设计见[验收架构](../architecture/automation-and-acceptance.md)，其中命令和格式仍未实现。统一结果和源码指纹以 [测试说明](../../testing.md) 的最新批次为准。
