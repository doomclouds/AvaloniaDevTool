# 已验证范围与当前限制

本页只列本轮确认的限制，不把历史专项或本机虚拟网络结果当成任意环境保证。完整结果见[测试说明](../testing.md)。

## 内网与 WSL

HTTP MCP 已在本机、Windows 的非回环接口、真实 Codex CLI 和 WSL Ubuntu 的 mirrored localhost 路径验证。WSL 客户端未安装 .NET SDK或第三方 Python 包，使用仓库的[标准库烟测脚本](../../tests/scripts/Test-McpHttp.py)。

独立两台物理电脑的 LAN 尚未现场验收。当前 WSL 到 Windows vEthernet 的另一条路径中，initialize 成功，tools/list 返回 200 / chunked SSE，但十二秒内没有收到正文。改用增量 SSE 解析后结果相同，独立非 MCP 固定正文端点也超时；不同程序可能受不同网络规则影响，原因未被确定。没有据此修改防火墙、VPN或系统网络。

## 系统输入需要可用的交互前台

全量回归中的八项既有 OS 输入测试被同一系统 PickerHost 窗口阻挡，返回 `foreground_changed`。独立 Computer Use 点击本任务自建的 WinForms fixture 也被该系统窗口遮挡而拒绝，不能把故障归咎于新 HTTP 层。

该窗口归属未确认，未关闭它或放宽前台范围检查。Native worker 的九个文件与重构前 Git 版本逐段一致，只有源码目录改变。UIA provider 动作、原生模态回答、文件打开/保存及 PNG 的 HTTP 专项已通过，但不能替代这些 OS 输入场景。

部署时执行宿主、worker 与被测应用需要处于同一个可交互用户桌面；锁屏、其他前台窗口阻挡、权限完整性差异和远程桌面状态会影响 OS 输入。后续该系统窗口退出，保持生产代码不变定向复测原八项，全部通过；原失败批次保留为历史。部署仍需可用交互前台，验证细节见[测试说明](../testing.md)，不以绕过目标归属校验换取通过。

## Avalonia TextBox 的初始本地值覆盖

Avalonia 12.1.4、FluentTheme 下，已经显示并完成模板化的 TextBox，如果 Text 是初始 LocalValue 字符串，添加 Animation 优先级覆盖后仅 Dispose lease，覆盖值可能继续生效。该现象在没有 Agent、HTTP 或任何客户端时可以直接复现。

独立探针比较了 SetValue lease、常量 Observable Bind lease、CoerceValue，以及发布 UnsetValue 再 Dispose。后两种替代没有安全恢复初值，未用无条件写回或 ClearValue 破坏原绑定/应用后续修改。OneWay 原绑定继续更新、覆盖期间应用后写 LocalValue 的探针场景可恢复；未模板化 TextBox 的行为也不同。TwoWay 文本绑定还需注意模板回写对源数据的影响，不能泛称任何绑定状态均可无副作用恢复。

另一个“释放后短暂添加并立即释放当前同值 Animation lease”的候选，虽然通过初始 Local、后写与替换等部分场景，但未通过 TwoWay 源保护和外部 Animation 完整矩阵，因此未采用。

固定版本源码显示，Fluent TextPresenter 的 TwoWay TemplateBinding 回写 SetCurrentValue，与有效值保留 Animation 优先级相关。实现保持原有可逆覆盖算法，没有在结构重构中加入未经验证的 workaround。HTTP 清理测试使用被动 TextBlock 并保留原值恢复断言。

本限制只涉及诊断属性临时覆盖的这类控件状态；`ui-input` 等正常业务输入本来就会修改应用状态，二者不是同一个恢复合同。源码参考：[Fluent TextBox](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Themes.Fluent/Controls/TextBox.xaml#L152-L154)、[TemplateBinding](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Data/TemplateBindingExpression.cs#L56-L65)、[EffectiveValue](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/PropertyStore/EffectiveValue.cs#L71-L100)。

## 其他未扩大范围

- 核心 Agent/Protocol 的源生成序列化检查与 CLI 的动态 MCP schema 是不同路径；目录和路由重构不自动证明严格零警告 Native AOT。
- 桌面租约只协调本 HTTP 宿主内的逻辑会话，不提供跨多个独立宿主进程或其他自动化程序的全局互斥。
- Windows native 后端没有扩展到 macOS/Linux；WSL 本轮作为远程客户端，不作为 Windows worker 执行端。
- 临时断线不代表写操作未执行。点击、保存、确认等不自动重试，需先读取应用与文件状态。
- 取消结束等待，不强行终止已经进入目标 UI 线程的同步 getter 或应用事件处理器；目标内最终清理受 UI 线程可调度性约束。
