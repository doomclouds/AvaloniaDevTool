# Native 窗口与节点的生命周期身份

2026-10-25。NATIVE05 要求旧 native ID 不得在窗口关闭、替换或进程变化后重定向到新对象。已取得防护前旧根 ID 重定向的真实红样本；当前销毁 epoch 防护已验证普通销毁/重建及父销毁后的旧根/旧子拒绝，修复后完整 HWND 复用尚未获得成功样本。以下区分公开合同、代码路径和实验边界。

## 公开合同与防护前风险

Windows 明确说明 HWND 可回收，`IsWindow` 成功后目标也可能马上销毁或变成另一个窗口，见 [IsWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iswindow)。UI Automation 的 [RuntimeId](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.getruntimeid?view=windowsdesktop-10.0) 只保证当前桌面中的唯一性，并允许随时间复用；[CompareElements](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation-compareelements) 的返回定义也是比较 runtime identifiers，并不提供额外的窗口代际令牌。

防护前 worker 用 PID/start time、HWND、根 RuntimeId、元素 RuntimeId 和父链核对作用域。`Register` 的缓存键不含生命周期 epoch，同键会给旧 native ID 换入 fresh COM wrapper。若这些键跨对象寿命复用，这一操作存在主动重绑旧 ID 的风险。下文实际复用实验同时检查了旧代理实时读取、fresh 枚举后的再次读取和 generation 状态，不能只比较缓存 RuntimeId。

保留旧 COM wrapper 并执行实时 getter 能避免主动替换，且能利用 provider 的失效信号，但不是所有 provider 的通用生命周期合同。微软 [ProxySimple.cs](https://raw.githubusercontent.com/dotnet/wpf/main/src/Microsoft.DotNet.Wpf/src/UIAutomation/UIAutomationClientSideProviders/MS/Internal/AutomationProxies/ProxySimple.cs) 中的 UIA2 代理保存 HWND，并用它查询实时矩形和进程属性；该源码不能直接证明本机 UIA3 的行为。明确的 provider 失效保证见 [UiaDisconnectProvider](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationcoreapi/nf-uiautomationcoreapi-uiadisconnectprovider)：成功断开后旧请求返回 UIA_E_ELEMENTNOTAVAILABLE。

## 未采用的推断捷径

- 增加标题、AutomationId、类名或矩形只能帮助诊断。这些值可能变化或重复，不能作为窗口实例的唯一身份。
- [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook) 的 out-of-context 通知异步送达，安装线程必须有消息泵。worker 的 Console.ReadLine 主循环不能代替消息泵，当前防护使用下述独立线程；hook 安装确认或普通线程消息 marker 都不能被当作清空全部 WinEvent 的同步屏障。
- 跨进程 SetProp 会改变目标窗口的属性表，受 UIPI 约束，且有窗口销毁前移除属性的生命周期责任，见 [SetProp](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setpropw)。本轮没有为通过测试向目标写入身份 cookie。

## 实验约束与首次结果

`NativeHwndReuseFixture` 仅创建、读取、销毁本 fixture PID 和 UI 线程拥有的真实窗口。首次记录根 HWND、EDIT HWND、PID/start time 和 generation 文本，随后销毁；只有完整原始根 HWND 再次出现才算实际复用，低16位相同或普通 destroy/recreate 不算。

首次预算60秒的探测执行16,370轮、创建32,742个真实窗口，耗时60,004ms；原根11016066，结束时根1083840386，没有完整 HWND 相等。Strict 实验按要求失败于 budget_exhausted，没有 skip 或计为通过；因此未执行复用后的旧代理对照。该结果证明首次预算内没有获得样本，不证明无法复用。

独立确定性的 destroy/recreate 测试已经通过：实际窗口从 generation:1 换为 generation:2，fresh 根/子 ID 不同，原根/子 ID 在销毁后以及 fresh 枚举后均被拒绝。它验证关闭和普通替换，不补造实际 HWND 循环复用。

## 实际复用故障

随后仅循环根窗口的真实实验在32,766轮、110,512ms后获得完整 HWND 28512516再次出现；PID/start time不变，generation:1变为generation:32767。原根和新根的实际UIA RuntimeId均为42.28512516。更关键的是，销毁前保存的原始FlaUI root proxy没有重新FromHandle，也读取了新名称；旧native根ID在fresh枚举前已指向新窗口，枚举后仍返回新窗口，replacement又复用了相同native ID。

原EDIT没有复用HWND，旧EDIT proxy和旧native EDIT ID均失效，说明不能把根的错误泛化为所有旧对象都仍然有效。该次私有Value诊断的反射失败不影响此前已记录的RuntimeId/Name；public native EDIT的generation内容另有记录。

真实红证据及原始console/JSON哈希见 [hwnd-reuse-red](../verification/2026-10-25-hwnd-reuse-red.json)。初次红跑没有TRX；源码快照是在诊断和测试增强后取得，不能冒充精确的红跑构建指纹。

## 窗口销毁代际防护

新增独立MTA消息泵，按目标及owned窗口的PID订阅EVENT_OBJECT_DESTROY，回调只推进已跟踪HWND的epoch，不读取已销毁句柄或调用UIA。枚举根时在FromHandle之前capture根stamp；注册有HWND的节点时在读取其RuntimeId之前capture节点stamp。缓存键包含根及节点的HWND/epoch和RuntimeId，Resolve、遍历出栈、读取完成和输入投递前均校验生命周期。已观察到销毁的旧epoch不能重新变为当前；确认stale的ID也永久退役，新的枚举分配新ID。监视器故障时明确失败，不能退回未监视身份。

WinEvent仍异步送达：这防止已观察销毁后的旧ID重定向，不是事件同步屏障，也不能原子锁住目标窗口直到操作结束。迟到destroy可能保守地使刚获取的新ID失效，需要重新发现。无HWND虚拟元素仍依赖provider的当前请求和失效信号，不能从本次Win32根样本推导所有provider的实例身份都已解决。

Windows [EVENT_OBJECT_DESTROY说明](https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants) 要求客户端在父对象销毁时把全部子对象视为销毁，不能期待每个子窗口都有独立事件。实测根Stamp已推进，而EDIT单独Stamp可能没有推进。因此backend每次先校验根，再校验有HWND节点自身的stamp：根过期即使该节点没有单独通知也必须拒绝。确定性测试保留两者原始epoch观察，并断言public旧根/旧子ID在销毁后及fresh枚举后均失效；不伪造子事件，也不要求EDIT独立epoch必须推进。monitor单测另行覆盖已送达销毁、迟到通知、PID变化、hook/消息泵故障及释放。

首个修复后180秒实验只完成51,903轮、涉及两个低位slot，没有出现完整原HWND；它按budget_exhausted失败，没有执行后续旧ID断言。另一次普通destroy/recreate在修正后通过。后续优化只减少fixture非客户区创建开销、保持完整HWND相等和旧ID拒绝断言；是否实际复用并通过，以后续TRX为准。

无边框fixture的另一次180秒实验和最多32个隐藏blocker的有界实验也没有获得完整复用，分别按时间/资源预算失败。它们不是产品功能断言失败，也不是修复已通过的证据，原始失败保留。本轮不继续追加长时间循环。

## 显式长时间实验入口

实际复用的分配顺序不由公开API保证。Strict实验保留原始目标和全部拒绝断言，标记xUnit `Explicit=true`，不让普通回归默认进行不稳定的句柄循环；未运行必须单列，不能算成通过。按[xUnit官方RunSettings](https://xunit.net/docs/config-runsettings)可显式执行：

```powershell
dotnet test tests/AvaloniaDevTool.NativeAutomation.Tests/AvaloniaDevTool.NativeAutomation.Tests.csproj --filter Category=NativeHwndReuse --logger trx -- xUnit.Explicit=only
```

预算耗尽仍会失败，不自动skip或替换目标。截至579项统一批次，已取得红样本但修复后尚无完整复用成功样本；该历史状态随后由下方独立显式实验补证。普通关闭、重建和真实父销毁通知验证仍分别保留。

## 2026-10-25独立显式验证：通过

使用与579项常规回归相同的源码指纹和Debug产物，没有修改断言或生产代码。显式用例1/1通过；32,766轮、61,173ms后完整根HWND 593860再次出现，PID 15712及start time不变，generation:1变为generation:32767。原根/新根UIA RuntimeId均为42.593860；原始UIA代理仍读取新窗口名称，说明只保留旧proxy不足。

工具的旧根和旧EDIT节点在fresh枚举前后都返回stale_node，replacement分配新节点ID。随后旧EDIT SetValue和旧根Focus均被拒绝，fixture原生文本与新节点读回都保持generation:32767。0 blockers残留、峰值2个自有窗口；fixture和worker均已退出。TRX、二进制哈希及原始观察见[显式通过记录](../verification/2026-10-25-hwnd-reuse-verified.json)。

该记录不改写此前整批579通过/1未执行。实测范围是同PID的根HWND复用，子HWND没有复用。写入/Focus在旧ID先经读取退役后发起，不能独立证明复用后的第一次请求就是写入这一分支。异步通知非原子屏障、实际PID循环及OS部分输入故障等限制也没有据此消除。
