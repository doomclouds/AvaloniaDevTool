# 官方 Agent/MCP 的社区证据与验收边界

核实日期：2026-10-25。检索对象为 Avalonia 官方文档、AvaloniaUI/AvaloniaPro 的公开 issue/discussion、已归档的 AvaloniaUI.DeveloperTools 仓库，以及社区项目的一手文档。这里只记录公开证据和由此提出的验收建议；本轮没有运行官方付费 MCP 的 `tools/call`，没有登录、修改账户或发送消息。

## 结论

- 官方当前 DevTools MCP 文档列出 **16 个工具，包含 `detach`，没有 `sources`**。本仓库旧的 2.2.3 未附加会话记录只列出 15 个，不能用初始 `tools/list` 推断 `detach` 永远不存在，也不能把社区 CDP 的 Sources domain 算进官方分母。[官方工具表](https://docs.avaloniaui.net/tools/developer-tools/mcp#available-tools)
- `initialize`、`tools/list`、发现应用、返回 `isError=false` 均不足以证明已附加；原始反馈分别出现许可拒绝、静默返回应用列表、预览器已连上但没有可用控件树。至少应取得目标身份、非空且符合样例的树，再验证一个只读工具和一个行为变化。[#23](https://github.com/AvaloniaUI/AvaloniaPro/issues/23)、[#179](https://github.com/AvaloniaUI/AvaloniaPro/issues/179)、[#157](https://github.com/AvaloniaUI/AvaloniaPro/issues/157)
- `attach-to-file` 是独立的预览器工作流，不能用 `attach-to-app` 成功替代。它应覆盖项目解析、初次构建、后续 AXAML 更新及清理。维护者明确说预览模式不支持 dialogs。[#19](https://github.com/AvaloniaUI/AvaloniaPro/discussions/19)、[#53](https://github.com/AvaloniaUI/AvaloniaPro/issues/53#issuecomment-4235777853)
- **官方目前不支持移动应用 MCP**：维护者 maxkatz6 在 2026-10-12 明确答复，移动支持是开放的 Feature。Android/iOS 社区绕行方案不能计入官方支持范围。[#212](https://github.com/AvaloniaUI/AvaloniaPro/issues/212)

## 证据等级与适用时间

| 等级 | 含义 | 如何使用 |
| --- | --- | --- |
| 官方规格 | 当前官方文档、正式发布说明 | 定义支持范围；仍需本项目运行验证 |
| 维护者确认 | 官方仓库中标为 Member 的维护者答复 | 解释边界或已承认缺陷；不能自动证明修复已发布 |
| 用户实测报告 | 原作者给出环境、调用与日志 | 选择回归场景；不能当成本机复现或所有版本普遍事实 |
| 社区实现文档 | 社区项目作者维护的 README、代码或文档 | 参考实现和适配候选；不扩大官方验收分母 |

issue 的 open/closed 是 2026-10-25 读取到的状态。closed 只能证明工单状态；除非有明确发布版本和重新执行证据，不写成“当前版本已验证修复”。

## 官方社区中的实际问题

| 证据 | 日期、环境、层级 | 核实结果与验收影响 |
| --- | --- | --- |
| [#179：`processId` 与 `id` 不一致](https://github.com/AvaloniaUI/AvaloniaPro/issues/179) | 2026-09-03；avdt/DiagnosticsSupport 2.2.3，Avalonia 12.1.1，.NET 10，Windows 11；用户报告，09-04 维护者确认；open | schema 定义 `id`，旧 description 残留 `processId`。`{"processId":12345}` 可能静默退回列表，日志仍显示成功；`{"id":12345}` 才附加。维护者确认描述需要修正，也要求客户端遵守 schema。10-09 另一用户在 Linux 2.2.3 确认 `id` 用法。验收须区分“列表成功”与 `connectedClient`。 |
| [#157：预览附加后不能读树](https://github.com/AvaloniaUI/AvaloniaPro/issues/157) | 2026-07-24；Avalonia 12.1.0、Windows，Codex/Opencode；用户报告；open | 新建 `avalonia.mvvm` 项目后，运行态附加可用，文件预览失败。日志有空树及 `no connection was pending`，另一次约 60 秒后取消。正文版本栏写 `v2.3.3`，但日志明确服务器为 `2.2.3+92df9d92…`；保留这个不一致，不将其写成 2.3.3 发布证据。未找到维护者确认根因。 |
| [#19：beta2 的附加超时](https://github.com/AvaloniaUI/AvaloniaPro/discussions/19) | 2026-02-07；avdt/DiagnosticsSupport 2.2.0-beta2，Avalonia 11.2.0、.NET 10、Windows 11；用户报告及维护者答复 | 用户能枚举 app，却在两种 attach 上超时。03-08 maxkatz6 宣布 beta3 改善多层稳定性，要求同时更新 tool 与 DiagnosticsSupport；`AVALONIA_PREVIEWER_EXPERIMENTAL=false` 切旧预览器，`AVALONIA_TOOLS_LOG_LEVEL=debug` 增加日志。没有原作者确认 beta3 已解决，不能外推当前版本仍失败或已修复。 |
| [#23：列工具成功但调用许可失败](https://github.com/AvaloniaUI/AvaloniaPro/issues/23) | 2026-03-07；2.2.0-beta2、Windows 11、Claude Code；用户报告及维护者答复；closed | 用户声称有付费订阅和本机 ticket，但每次调用被拒绝。维护者指出 Community 层不包含 MCP，可显式设置当时的 `ACCELERATE_LICENSE_KEY` 而不依赖 GUI session。05-30 维护者说 2.2.1/2.2.2 改善了错误信息后关闭；不是原作者付费状态或修复运行结果的独立证明。 |
| [#100：`Nullable<bool>` 转换失败](https://github.com/AvaloniaUI/AvaloniaPro/issues/100) | 2026-05-18；Avalonia 12.0.3、DiagnosticsSupport 2.2.1、Windows；用户报告及维护者确认；closed | `ToggleButton.IsChecked` 写字符串 `False/false/True/1/0` 失败，JSON bool 又不符合字符串 schema；`null`、`unset` 可用。05-27 maxkatz6 承诺下一次更新修复并关闭，但没有给出确切发布版本。必须加入可空 bool 的三态读回、清除及非法输入回归。 |
| [#53：新开的子窗口不在树中](https://github.com/AvaloniaUI/AvaloniaPro/issues/53) | 2026-04-12；avdt/DiagnosticsSupport 2.2.0、Avalonia 11.3.9、macOS、Claude Code；用户报告及维护者答复；closed | 维护者说明运行态应跟踪 `IClassicDesktopStyleApplicationLifetime.Windows`，文件预览不支持 dialogs。原作者随后说移除同时加载的 legacy DevTools/Diagnostics 后恢复并关闭。验收要覆盖附加后 `Show()`/`ShowDialog()`、关闭后根节点移除，不能据首帖判定官方始终只支持 MainWindow。 |
| [#212：移动支持](https://github.com/AvaloniaUI/AvaloniaPro/issues/212) | 2026-10-09；2.2.3、Avalonia 12.1.4、Android 16 emulator、Kubuntu、Trial；用户报告，10-12 维护者确认；open Feature | 用户通过端口转发能让目标连接 listener，却因无 pending connection 被关闭。维护者明确表示移动应用目前不受 DevTools MCP 支持。官方 GUI 的移动附加能力不能直接外推给 MCP；当前官方对标从移动分母排除，产品如另做支持应标为扩展。 |
| [#210：Android `MainViewFactory` 空树](https://github.com/AvaloniaUI/AvaloniaPro/issues/210) | 2026-10-09；2.2.3、Avalonia.Android 12.1.4、.NET 11 RC；用户报告；open | 推荐模板 lifetime 的 `tree/search` 返回空。作者随后纠正首帖的 MainView workaround：它会让 Activity 重开显示空白，不应照搬；新的反射补私有字段仅为 Debug 绕行。本项目不能把此私有内部方案列为官方验收要求或已支持平台。 |

补充：[Discussion #211](https://github.com/AvaloniaUI/AvaloniaPro/discussions/211) 是上述 Android 用户的完整使用记录，报告同机 Linux 桌面上的 attach/search/props/set-prop/input/screenshot 正常；Android 需要伪造主机发现管道、改写进程 ID、反射内部字段，随后转为 #212。它提供了可信的用户操作与日志线索，但没有维护者确认这些绕行方案受支持。本轮没有重复执行该用户的测试。

## 官方仓库和发布记录的边界

[AvaloniaUI.DeveloperTools](https://github.com/AvaloniaUI/AvaloniaUI.DeveloperTools) 已于 2025-11-05 归档；公开 [releases](https://github.com/AvaloniaUI/AvaloniaUI.DeveloperTools/releases) 当前最高为 2.1.0，不能当作现有 2.2.3 能力上限。2.1.0 的正式说明再次要求同时升级工具与 DiagnosticsSupport，并说明 .NET 10 可使用无平台后缀包名。其余 GUI 的 profiler、metrics、layout 等条目不能仅凭发布记录加入 Agent/MCP 分母。

[AvaloniaPro README](https://github.com/AvaloniaUI/AvaloniaPro) 说明该仓库是商业工具及控件的公开反馈入口，不包含 Pro 工具源代码。因此此处 issue/discussion 是直接反馈来源，但 issue 里用户的反编译推测仍属于用户推测。

## 工具清单与尚缺的原始行为证据

当前官方文档在 2026-10-06 更新，列出以下工具；这里只记录名称及边界，完整参数以固定版本 `tools/list` 为准。[DevTools MCP](https://docs.avaloniaui.net/tools/developer-tools/mcp)

| 工具组 | 官方文档工具 |
| --- | --- |
| 连接 | `attach-to-app`、`attach-to-file`、`detach` |
| 检查 | `tree`、`ancestors`、`search`、`screenshot` |
| 属性和样式 | `props`、`set-prop`、`styles`、`pseudo-class` |
| 资源和资产 | `resources`、`assets`、`open-asset` |
| 交互 | `input`、`action` |

本轮没有在官方原始帖子里找到 `isXamlValue` 的完整示例，也没有找到足以定案的 `input`/`action` 每个枚举值的行为回归记录。仓库已有 [2.2.3 schema 索引](../../research/sources/mcp-tools-2.2.3-index.json) 记录 `set-prop.isXamlValue`，属于 2026-10-24 的历史 schema 捕获，不是本轮重新调用结果。旧 [原生分析记录](../../research/native-deep-analysis.md) 另记 `input=Click/KeyDown/KeyUp/Text`、`action=Focus/Enable/Disable/BringIntoView`；实现和验收前应重新捕获完整 schema，避免把旧记录当最新运行。

`sources`、任意磁盘源文件浏览、源码改写，不在当前官方工具表。官方 `assets` 返回嵌入资源 URL，`open-asset` 读取该 URL；`resources` 是应用或节点作用域的运行时资源。这三者不是同一个范围。当前没有找到官方 MCP `set-resource` 或 `save-source` 的证据，不应据 GUI 文档或第三方功能补出这些官方能力。

许可的当前官方规格是 Plus key；Free/Community 不包含 MCP。Avalonia 12 使用 `AVALONIA_TOOLS_LICENSE_KEY`，11.x 及更早使用 `ACCELERATE_LICENSE_KEY`。#19 中 2026-02-26 MikeCodesDotNET 确认当时 Business trial 包含 MCP。试用、Community、正式 key 应按其证据日期分别记录，不能用商业方案改名推定所有历史许可一致。[当前官方许可说明](https://docs.avaloniaui.net/tools/developer-tools/mcp#setting-your-license-key)、[试用答复](https://github.com/AvaloniaUI/AvaloniaPro/discussions/19)

## 社区实现的复用价值

以下均是社区产品的作者文档，不是官方 Avalonia MCP 的验收条目。本轮只核对公开说明，没有重新运行它们。

| 项目和原始来源 | 能用于解决的实现问题 | 保留的边界 |
| --- | --- | --- |
| [trrahul/XamlMcp README](https://github.com/trrahul/XamlMcp) | 现有树/属性/资源/资产/输入/action/截图工具、快照目标、变更后等待与 observation digest，可帮助设计稳定的行为验收 | 自有协议和工具；不能用它声称官方 `isXamlValue` 语义已证明。独立 AXAML previewer 是否等价需单独验证。 |
| [wieslawsoltes/CDP README](https://github.com/wieslawsoltes/CDP) | Avalonia raw input、DOM/CSS、截图、多个 window target；Sources domain 提供工作区文件导航与内容读取 | Sources 是该社区协议功能，不是官方 MCP `sources`。其原生 MCP 暴露的是 `dom_query/evaluate/screenshot/tap/input_text/clear_text/scroll` 等另一套工具名。 |
| [Avalonia.Markup.Declarative AgentTools](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/master/docs/agent-tools.md) | 文档明确区分 UI Automation actions 与真实 keyboard/text 输入管线，并提供“操作后等待、读回和截图”的可参考实践 | 交互为显式启用的社区能力；这不证明官方 `input` 也是同一实现，更不能把全部社区工具并入官方清单。 |

## 可复现的验收建议

以下是由证据提出的**待执行测试**，不是本轮通过记录。建议用独立测试应用、临时项目副本和真实 stdio MCP 会话输出 JSON/PNG 证据，固定 Avalonia、tool、DiagnosticsSupport、OS、客户端与 schema hash。

| 场景 | 最小操作 | 合格证据与能发现的问题 |
| --- | --- | --- |
| A01 发现与真正附加 | 同时运行两个测试 app；无参数列应用；用 schema 的 `id` 附加指定 PID；随后读树 | 明确区分 availableClients 与 connectedClient；树含目标独有 Name。错误参数应清晰失败，不能以空参数成功列表伪装附加。对应 #179。 |
| A02 许可层次 | 分别以缺 key、无效 key、有效许可配置启动；各执行 initialize/list/attach/tree | 保存 `tools/call` 的 isError 与文本，不能只看进程或 list；不记录真实 key。对应 #23。独立实现无需复制官方商业授权，只需明确外部官方对照为何受阻。 |
| A03 文件预览全链路 | 临时复制带自定义控件、资源引用的 AXAML 项目；不运行 app；首次需要时 build；attach file→tree→screenshot；改一处布局后再次读树/截图 | 有目标节点、可解码非空 PNG、新旧画面或属性的可解释差异；明确 build=false 的已有产物条件。失败时保留预览进程 exit、连接日志及错误，不把空树当成功。对应 #19/#157。 |
| A04 预览生命周期 | 同一进程多次附加不同文件；测试 detach、取消、无效 AXAML、缺项目文件；结束会话 | 旧进程和句柄清理、失效节点清晰报错、恢复有效预览；重新捕获附加前后 tools/list，确认 detach 是否动态出现。此为补齐版本差异的测试建议。 |
| A05 `set-prop` 类型与清除 | 对 string、enum、Thickness、Brush、`bool?` 逐一写值并 props 读回；bool? 做 true/false/null/unset；非法值再读旧值 | 值的实际类型与结果正确，unset 恢复优先级来源，失败无半写入。`null` 与 unset 分开。对应 #100；关闭工单不替代此回归。 |
| A06 `isXamlValue` | 固定 schema 后，同一 Brush 属性分别用普通文本和 `isXamlValue=true` 的完整带命名空间 XAML 值；另传非法 XAML | 先通过官方对照确认可接受 XAML 形状，再断言解析对象及读回；错误可诊断。现有社区帖子不足以定义所有 Binding/markup-extension 支持，不能先列为已完成。 |
| A07 `input` 的实际事件 | 在有事件计数器的 Button、TextBox 上依 schema 分别发 Click、KeyDown、KeyUp、Text；输入中文；增加禁用控件和失焦场景 | 断言真实 handler、命令计数、焦点及文本结果；按键不能仅以改 Text 属性冒充。保存调用前后树/props。枚举以该版本 schema 为准。 |
| A08 `action` | 依 schema 对 Focus、Enable、Disable、BringIntoView 分别调用，目标放在可滚动的屏外区域 | props/焦点与 ScrollViewer offset 确有变化；disabled 状态及不支持节点得到明确结果；不能只有成功字符串。 |
| A09 资源与资产 | 应用/窗口/控件各定义同名 key；另有 merged/theme 资源；嵌入小 PNG、字体、文本和二进制资源；枚举再逐个 open | resources 的 scope/onlySelf 与预期来源相符；assets URL 能返回原始字节，MIME/长度可核对；不存在的 URL 清晰失败；不把任意磁盘文件算嵌入资产。 |
| A10 窗口和树范围 | 附加后打开 Show 与 ShowDialog 子窗口、Popup；在各树模式及 scope 搜索；关闭再读取 | 根及后代与实际生命周期一致；旧节点失效；文件预览不要求显示运行时 dialogs。对应 #53。 |
| A11 平台报告 | 至少在实际执行过的 Windows/Linux/macOS 桌面组合记录结果 | 跨平台声明须逐平台实测。Android/iOS 依 #212 标记官方不支持；不能因 GUI 支持或社区代理成功而加进完成率。 |

完成率应分别呈现“官方规格项”“已实现”“本次运行通过”“因官方许可/平台无法对照”，并给每项证据文件。用户报告、反编译推测、历史 schema 或本项目 mock 通过，都不能单独填入“官方对照运行通过”。
