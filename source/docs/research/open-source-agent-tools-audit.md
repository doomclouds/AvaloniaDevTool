# 社区 Inspector 与 AgentTools 开源复用审计

> 历史研究：本文对应完整桌面产品基线 `main/fa1c623`，其中 GUI、外部后端、旧路线建议和阶段计数不作为当前分支交付范围或通过证据。保留它用于来源、许可和设计研究；当前官方 Agent/MCP 合同与原版证据边界见 [官方能力基准](../agent-tools/research/official-capabilities.md)，当前状态见 [实现状态](../implementation-status.md)。

审计日期：2026 年 10 月 25 日。范围为三个指定仓库的固定提交，依据实际许可证、项目依赖、服务分派、传输实现和测试源码；不以 README 的功能列表替代实现核验。本轮只读审计并编写文档，未将第三方代码或依赖加入产品，未执行这三个仓库的测试。 后续基于同一 XamlMcp 提交的真实兼容验证结果，见 [XamlMcp 12.1.4 验证](xamlmcp-compatibility-validation.md)。

**结论：控件树、属性、样式与绑定、输入、截图、资产、MCP 和跨进程会话已有可读、可复用的社区实现。它们是独立社区产品，不是官方闭源 DeveloperTools 的完整源码。后续应先评估这些后端的复用和适配，再决定哪些原版差异值得继续 REA。**

## 固定样本与维护线索

| 项目 | 审计提交 | 最近提交日期 | 实际许可证和目标 |
| --- | --- | --- | --- |
| wieslawsoltes/CDP | `349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84` | 2026-10-13 | MIT；版权告知为 2026 Wiesław Šoltés；默认 net10.0、Avalonia 12.0.5，另有 Avalonia 11 构建项目 |
| AvaloniaCommunity/Avalonia.Markup.Declarative | `24cfeebadc273cfdd76588e0ecef66c6cb0b6aab` | 2026-09-27 | MIT；版权告知为 2025 gritsenko, Avalonia UI；AgentTools 多目标 net8.0/net9.0/net10.0，核心引用 Avalonia 12.1.1 |
| trrahul/XamlMcp | `1718f946cb275a71e021de58f4caa552e04feaa8` | 2026-08-05 | Apache-2.0；项目元数据声明 2026 Rahul TR；Avalonia Agent 为 net8.0、编译下限 11.3.18，Server 为 net10.0 |

最近提交只说明本次样本的时间，不代表维护承诺或当前 CI 成功。文档中的“可复用”指存在具体实现和可评估依赖边界，不代表已在本项目的 Avalonia 12.1.4 上完成兼容验证。

许可证原文：[CDP MIT](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/LICENSE)、[Declarative MIT](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/LICENSE)、[XamlMcp Apache 2.0](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/LICENSE)。

## CDP

### 已核实的实现

这不是只有协议声明的库。它包含应用内 CDP 服务、HTTP/WebSocket 传输、独立 Inspector、CLI、MCP 桥接及多框架诊断模块。

| 部分 | 源码核验与复用价值 |
| --- | --- |
| 跨进程连接 | [核心 HTTP/WebSocket Server](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Chrome.DevTools.Protocol/Server/CdpServer.cs) 与 [WebSocket Client](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Chrome.DevTools.Protocol/Client/CdpService.cs) 实际实现目标发现和会话；Avalonia 侧的 [CdpServer](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Server/CdpServer.cs) 负责 TopLevel 目标与 UI Dispatcher 适配 |
| 控件、样式、覆盖层、输入、截图、日志 | Avalonia Domains 下确有 [DomDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/DomDomain.cs)、[CssDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/CssDomain.cs)、[OverlayDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/OverlayDomain.cs)、[InputDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/InputDomain.cs)、[PageDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/PageDomain.cs) 和 [LogDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/LogDomain.cs) 分派 |
| 桌面 Inspector | [ElementsViewModel](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/CDP.Inspector.Shared/ViewModels/ElementsViewModel.cs)、[EventsViewModel](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/CDP.Inspector.Shared/ViewModels/EventsViewModel.cs)、[PerformanceViewModel](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/CDP.Inspector.Shared/ViewModels/PerformanceViewModel.cs) 等是可读前端实现 |
| 真正的 MCP 桥 | [CLI Program](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/CDP.Inspector.CLI/Program.cs) 中有 mcp 命令、initialize、tools/list、tools/call 和工具调用映射；此提交握手返回固定 MCP 版本 2024-11-05，需要对当前客户端实测 |
| live UI Designer | [DesignerViewModel](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/CDP.Inspector.Shared/ViewModels/DesignerViewModel.cs) 通过 DOM 修改目标应用；[AvaloniaXamlMutationEngine](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Mutation/AvaloniaXamlMutationEngine.cs) 查找 AXAML 文件、修改并保存文档、更新 live 控件。该能力会写源码，不能直接作为本项目“临时可撤销属性编辑”的同义替换 |
| 测试 | [McpCliTests](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/tests/CDP.Inspector.CLI.Tests/McpCliTests.cs) 测试工具清单、初始化和调用分派，使用 mock CDP；另有 Avalonia、Inspector Shared/Controls、v11 等测试项目及 [Windows/Linux/macOS 测试矩阵](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/.github/workflows/test-suite.yml) |

### 必须保留的边界

- **模块名字不等于完整实现。** [DomDebuggerDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/DomDebuggerDomain.cs) 的 setDOMBreakpoint、setEventListenerBreakpoint 等分支在本提交中直接返回空对象；已实现的监听器枚举使用反射读取 Interactive 的处理器。
- [DebuggerDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/DebuggerDomain.cs) 使用 Jint 和脚本 URL/行号断点，不应当作原版 DeveloperTools 的 CLR 属性／事件断点实现。
- [PerformanceDomain](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Domains/PerformanceDomain.cs) 实际采集进程 CPU、GC、分配和 UI 相关数据，不等同原版 Meter/Activity 工具或完整 CPU profiler。仓库另有独立 Profiling 包，本轮没有验证其与原版 profiler 的完整等价性。
- 本次扫描 src 内 C#、AXAML、TypeScript、HTML 及 docs Markdown，未找到专门的 LayerTree/3D 图层透视视图实现；不能据此断言其他分支或全网没有该能力。
- live Designer 与 XAML 源码修改已有实现；本次未找到像官方预览器那样、针对任意现有项目启动专用 AXAML previewer 进程的同等实现。
- [核心包](https://github.com/wieslawsoltes/CDP/blob/349d829e8c50f0f3c6f7d5560dfbfd6a9f5f9f84/src/Avalonia.Diagnostics.Cdp/Avalonia.Diagnostics.Cdp.csproj) 引入 Jint、Roslyn、SQLite、XAML Compiler 和集成项目，整体依赖明显多于当前 Agent。不能把它视作无成本替换的小包。
- 两个 samples 有官方 DiagnosticsSupport 的开发期包引用；本次检查的 CDP 核心项目没有该依赖，不能将 samples 的调试配置误当作 CDP 功能依赖官方闭源工具。

### 对当前项目的建议

优先单独验证 **Chrome.DevTools.Avalonia + CDP Client** 后端适配，或参考 DOM/CSS/Overlay 和 Inspector 面板设计；不应为评估基本能力直接引入整个大型 Inspector 解决方案。当前项目是自定义管道 DTO，CDP 是另一套网络协议，需要明确的后端适配层。迁移前应验证 Avalonia 12.1.4、关闭连接清理、属性修改语义、source mutation 范围及 MCP 握手。

## Avalonia.Markup.Declarative AgentTools

### 已核实的实现

项目主体是 C# 声明式 UI 作者层，AgentTools 是独立的诊断与 MCP 包。[agent-tools 文档](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/docs/agent-tools.md) 对运行方式和工具说明很完整，且本次检查的源码中确实存在对应实现。

| 部分 | 源码核验与复用价值 |
| --- | --- |
| 真正的 MCP 服务 | [AgentInspectorServer](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Declarative.Avalonia.AgentTools/AgentInspectorServer.cs) 使用 ASP.NET Core WebApplication、MCP SDK、MapMcp；在应用进程中监听 loopback HTTP |
| 包边界 | [AgentTools csproj](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Declarative.Avalonia.AgentTools/Declarative.Avalonia.AgentTools.csproj) 依赖 ModelContextProtocol.AspNetCore 1.4.0 与 Declarative 核心；核心 Diagnostics 不引用 MCP/web 栈 |
| 工具清单 | [InspectionTools](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Declarative.Avalonia.AgentTools/Tools/InspectionTools.cs) 有 22 个带 MCP 属性的工具；[InteractionTools](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Declarative.Avalonia.AgentTools/Tools/InteractionTools.cs) 有 18 个。包括窗口／控件／区域截图、截图比较、布局、属性来源、DataContext、hit-test、等待、日志和指针／触摸／命令操作 |
| 截图和差分 | [ControlScreenshotService](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Avalonia.Markup.Declarative/Diagnostics/ControlScreenshotService.cs) 提供 PNG、BGRA、独立子树或窗口上下文截图，并对初始空帧做有限重试；[ScreenshotStore](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Avalonia.Markup.Declarative/Diagnostics/ScreenshotStore.cs) 管理截图比较 |
| 布局、命中与属性来源 | [LayoutAuditor](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Avalonia.Markup.Declarative/Diagnostics/LayoutAuditor.cs)、[HitTester](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Avalonia.Markup.Declarative/Diagnostics/HitTester.cs)、[PropertySourceInspector](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Avalonia.Markup.Declarative/Diagnostics/PropertySourceInspector.cs) 是直接的算法参考 |
| 日志与错误 | [AppLogSink](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Avalonia.Markup.Declarative/Diagnostics/AppLogSink.cs)、BindingErrorSink、RuntimeErrorSink 与 ring buffer 提供错误和输出观察 |
| 测试 | [AgentToolsSmokeTests](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/24cfeebadc273cfdd76588e0ecef66c6cb0b6aab/src/Declarative.Avalonia.AgentTools.Tests/AgentToolsSmokeTests.cs) 以及 Diagnostics.Tests 覆盖真控件／Skia headless 场景；该测试项目明确直接驱动工具 handler，未启动实际 HTTP endpoint，不能当作 live HTTP 端到端证明 |

### 对当前项目的建议和边界

- 最适合优先参考截图稳定化、像素比较、布局审计、命中可操作性、DataContext 与输出观察。它们的算法已有源码，不必通过官方闭源 SDK 反编译来获得同类能力。
- 直接依赖 AgentTools 会同时引入 Declarative 核心、源码生成器及 ASP.NET Core/MCP 栈。当前 Inspector 仍使用 AXAML，没有必须整体转为 C# Declarative 的需求；应先判断需要的是公共 Diagnostics 类还是整套 authoring 层。
- 窗口／控件名定位中有通用 Avalonia 路径，但 active components、源码 tagging、ViewBase 等功能带有 Declarative 语义，不能直接宣称可定位所有 AXAML 源码。
- MCP 客户端确实跨进程通过 HTTP 调用应用，但这里的 **MCP server 在目标应用内部**，并不是当前项目的独立 Inspector Client + NamedPipe Agent 架构。
- 文档中的 dotnet watch 热重载流程是代码编辑—热重载—观察回路；截图与 render stats 不应被称为独立 AXAML 预览器或完整 profiler。
- 本次扫描 AgentTools、核心 Diagnostics 与 agent-tools 文档，未找到独立 3D 图层视图、属性／事件断点或 Meter/Activity 采集服务；“breakpoint” 的少数出现是 UI 响应宽度或调试暂停说明。

## XamlMcp

### 已核实的实现

该项目与当前项目的“进程内 Agent + 外部工具 + 跨进程协议”最接近，但它主要提供 MCP、CLI 和自动化能力，没有本项目这种九页桌面检查器。

| 部分 | 源码核验与复用价值 |
| --- | --- |
| 真正的 MCP Server | [McpHostRunner](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Server/Cli/McpHostRunner.cs) 注册 MCP SDK stdio transport 和 AppTools、InspectionTools、InteractionTools；不是只有 csproj 描述 |
| Agent 与跨进程管道 | [DesktopAgentServer](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Agent.Hosting/DesktopAgentServer.cs) 启动 current-user named pipe，创建每次启动独立的 discovery 信息与 token；[AgentClient](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Server/AgentClient.cs) 连接并认证 |
| 兼容和协议版本 | [ProtocolInfo](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Protocol/ProtocolInfo.cs) 的协议版本为 2；[AvaloniaCompat](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Avalonia/Compat/AvaloniaCompat.cs)、[StyleDiagnosticsCompat](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Avalonia/Compat/StyleDiagnosticsCompat.cs)、BindingDiagnosticsCompat 把 runtime reflection 隔离并暴露 capability/gap |
| 弱引用与失效 | [通用 NodeRegistry](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Agent.Hosting/NodeRegistry.cs) 保留四个有限快照命名空间；[Avalonia NodeRegistry](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Avalonia/Nodes/NodeRegistry.cs) 还检查顶层根和虚拟化 container identity，比仅检查对象是否 GC 更完整 |
| 样式与绑定 | [StyleService](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Avalonia/Values/StyleService.cs)、[BindingDiagnosticsService](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Avalonia/Values/BindingDiagnosticsService.cs) 提供结构化诊断，并明确 binding mode、raw exception 等不可得字段 |
| 资源、资产、截图、输入 | Values、Assets、Interaction、Observation 目录有完整服务实现；[InspectionTools](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Server/Tools/InspectionTools.cs) 真实分派 tree、search、ancestors、props、styles、bindings、failures、resources、assets、wait-for、hit-test、screenshot、open-asset |
| 自动化行为 | [InteractionTools](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Server/Tools/InteractionTools.cs) 分派 set-prop、pseudo-class、input、action；automation pattern 与 command slot 发现可帮助避免把“RaiseEvent”误称为真实 OS 输入 |
| 真端到端测试 | [LiveMcpTests](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/tests/XamlMcp.Tests/EndToEnd/LiveMcpTests.cs) 使用 SDK McpClient，经 stdio Server、命名管道到独立 TestHost；[测试项目](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/tests/XamlMcp.Tests/XamlMcp.Tests.csproj) 支持 AvaloniaTestVersion 覆盖，默认 11.3.18，release consumer 脚本包含 12.1.0 |

### 对当前项目的建议和边界

- **最高优先级参考或适配候选**：Agent.Hosting 的认证发现与会话生命周期、NodeRegistry 虚拟化失效、compat probing、binding diagnostics、public MCP 工具与真实 E2E 测试。
- 直接引用包仍需设计后端适配：它使用 `NodeRef(snapshotId, nodeId)`、带能力协商的 JSON-RPC 2.0（项目协议版本 2）和自己的 transport；当前项目的 long objectId、自定义帧和 DTO 不能直接互通。
- Avalonia Agent 的 csproj 编译下限为 11.3.18，文档和测试矩阵声称支持 12.x；本次未在 12.1.4 运行其测试，所以不能把“支持 12.x”当作本项目版本已验证。
- `resources` 的已检查服务是资源查询和主题／host chain 解析，工具面没有独立 set-resource；不能据此声称涵盖当前项目的可恢复资源字典编辑。
- 对 Avalonia/Server/Protocol 的这次定向扫描未发现原版式 3D 图层、独立 AXAML previewer、属性／事件断点管理或 Meter/Activity profiler。WPF 文件里的 Visual3D 是 WPF 树类型，不是 3D 图层可视化。
- Server csproj 中 `BuildLocalPreview` 是预发布 NuGet 打包／manifest 开关，不是 UI 预览器。该文件有过时的“stub”注释，但实际 McpHostRunner 已实现，不能用注释否定代码事实。

## 建议的复用决策

1. 保留当前 Inspector 前端，将 backend 访问抽成可替换接口；用一个最小独立验证程序分别评估 XamlMcp Agent 和 CDP Agent。先证明树／属性／绑定／截图／断连工作，再决定依赖或移植哪些模块。
2. 对算法型功能优先读源码：Declarative 的布局审计和截图差分、XamlMcp 的虚拟化失效与兼容层，不需要继续逆向同类公开 API。
3. 不把第三方整个工具的行为直接带入：CDP Designer 可写 AXAML；XamlMcp 使用快照命名空间；Declarative AgentTools 使用应用内 HTTP host。它们与当前可撤销、本机单会话的编辑语义不同。
4. 若决定复制或修改源码，应建立第三方归属清单。MIT 要随副本或重要部分保留原 copyright 与许可告知；Apache-2.0 要保留许可证与相关告知，修改文件显著标注变更，并在所用版本包含 NOTICE 时按其要求保留。三个当前克隆中本次仅找到 LICENSE，未据此推导未来版本或依赖包的 NOTICE 义务不存在。
5. 继续 REA 应限于原版特有且确有必要还原的差异：Compositor 逐层位图与精确 3D 组织、预览器进程／编译／传输链、原版特定 profiler 和交互细节。独立自研不要求复制官方 CoreRPC wire format，也无需为已有公开实现的基本诊断服务继续消耗同等逆向成本。若只需要独立实现而非原版行为等价，3D 与预览也可以依据 Avalonia 公开源码自行设计，并不强制需要 REA。

## 本次未做的事

未执行第三方仓库测试、未验证其已发布 NuGet 与上述提交逐字一致、未审计所有间接依赖的许可证、未迁移任何第三方产品代码。原始克隆保存在系统临时目录 `C:\Users\10062\AppData\Local\Temp\avdt-open-source-audit-e91f75f3c56240ac9777241b861bbaa6`，不参与本项目构建。

