# Agent / MCP 范围与依赖审计

审计日期：2026-10-25。审计对象是 `codex/agent-tools` 分支收缩前的项目引用和源码调用，完整旧产品基线保存在 `main` 的 `fa1c623`。本文只决定依赖和删除边界；官方工具的名称、参数、返回值及行为仍以本分支的官方 Agent / MCP 规范为准，旧 GUI 功能目录不再作为交付清单。

结论：独立 Inspector 与其 XamlMcp 只读后端是可整体移除的生产子图；Previewer 是 `attach-to-file` 的实际运行时依赖，不能随 GUI 删除。Remote、Logging、Native 测试和消费样例各有独立服务端验证价值，不能因不在 CLI 的直接项目引用中就认定无关。

## 项目依赖和真实调用

下表记录收缩前的引用；行号对应旧基线，删除后的历史文件可从 `fa1c623:<path>` 查看。

| 调用方 | 依赖 | 证据和用途 |
| --- | --- | --- |
| `src/AvaloniaDevTool.Cli` | Client、Previewer | `AvaloniaDevTool.Cli.csproj:6-7`；Previewer 的 `ReferenceOutputAssembly=false` 只表示不作为编译程序集引用，仍然是构建和分发依赖。`CopyPreviewHost` / `PublishPreviewHost` 将整个宿主输出复制到 `previewer/`。 |
| CLI `ToolSession` | 具体 `DiagnosticsClient` | `ToolSession.cs:13` 直接构造客户端，MCP 不通过 `IInspectionBackend` 切换后端。 |
| CLI `attach-to-file` | Previewer 子进程 | `InspectorTools.Preview.cs:8-11` → `ToolSession.AttachFileAsync` → `OwnedPreview.StartAsync`；`ToolSession.cs:159` 定位 `previewer/AvaloniaDevTool.Previewer.dll`，之后启动 `dotnet` 并管理控制管道、超时、取消和退出。 |
| Previewer | Agent | `AvaloniaDevTool.Previewer.csproj:11`；预览生成真实 Avalonia 根，并通过本项目 Agent 暴露给 CLI。 |
| Agent、Client | Protocol | 各自 `csproj` 的直接引用；DTO、RPC 帧、会话和通知不能随任何界面删除。 |
| Inspector | Client、Backends.XamlMcp | `AvaloniaDevTool.Inspector.csproj:11-12`；`InspectorLaunchOptions.cs:76` 是 `new XamlMcpBackend(...)` 的唯一生产调用点。 |
| Backends.XamlMcp | Client、MCP SDK、XamlMcp.Protocol、下载的 XamlMcp.Server | `AvaloniaDevTool.Backends.XamlMcp.csproj:3-10`；用于 GUI 经公开上游服务读取共同只读能力。CLI、Agent、Previewer 没有调用该适配器。 |
| XamlMcpTarget 样例 | 上游 NuGet Agent | 仅为上述外部只读后端提供目标；被 Backend.Tests 与 Inspector.Tests 的项目引用和进程 fixture 使用，未被本项目 Agent/MCP 使用。 |
| Remote | Agent、ASP.NET Core | `AvaloniaDevTool.Remote.csproj:3-4`；提供现有 Native WebSocket 服务。CLI 通过 `DiagnosticsClient.Remote.cs` 消费协议，不要求在 CLI 直接引用服务端程序集。 |
| Logging | Agent、Microsoft.Extensions.Logging | `AvaloniaDevTool.Logging.csproj:3-4`；将真实 `ILogger` 记录写入 Agent 日志来源。Agent.Tests 和 Sample 是实际调用方。 |

## 可以删除的完整子图

以下目录可一起从专注 Agent / MCP 的分支移除，并同步删除解决方案项目项、旧 README 命令和文档链接；旧代码仍由 `main/fa1c623` 保存。

```text
src/AvaloniaDevTool.Inspector/
tests/AvaloniaDevTool.Inspector.Tests/
src/AvaloniaDevTool.Backends.XamlMcp/
tests/AvaloniaDevTool.Backends.XamlMcp.Tests/
samples/AvaloniaDevTool.XamlMcpTarget/
```

外部 XamlMcp 后端不是官方 Agent 实现依赖。它的 `SnapshotCacheTests`、`BindingDetailsMappingTests` 和独立进程集成测试只验证这层适配器的快照句柄、上游 gap 映射和只读会话。随着适配器整体退出范围，这些测试一并退役；不能将退役描述为 Native 兼容行为已经获得等价覆盖。

删除后可进一步精简的 GUI 专用代码：

| 路径或符号 | 处理条件 |
| --- | --- |
| `src/AvaloniaDevTool.Client/IInspectionBackend.cs` | 生产接口调用方仅 Inspector。移除 GUI 与外部后端后可删，同时将 `DiagnosticsClient : IInspectionBackend` 改为直接实现 `IAsyncDisposable`。 |
| `src/AvaloniaDevTool.Client/DiagnosticsClient.Backend.cs` | `BackendId`、`DisplayName`、`HasStableObjectIds`、`HasPushUpdates`、`LastNotice` 是 GUI 共同后端元数据；CLI 无调用，可随接口删除。 |
| `samples/AvaloniaDevTool.Sample/SampleInspectorLocator.cs` | 只定位被删除的 Inspector EXE；删除时同时移除 `App.axaml.cs:35` 的 `InspectorExecutablePath` 赋值，并改写样例中的 Inspector 操作提示。 |

`DiagnosticsClient.ProtocolTrace` 不应仅凭 GUI 消失而整块删除：`tests/AvaloniaDevTool.Protocol.Tests/RpcTraceTests.cs` 仍验证有界、脱敏和关联语义。是否继续分发该观测能力由官方 Agent / MCP 规范决定。

## 必须保留的执行链和测试

| 路径 | 保留原因及独特覆盖 |
| --- | --- |
| `src/AvaloniaDevTool.Agent/`、`Client/`、`Protocol/`、`Cli/` | 当前 MCP 的目标内服务、传输、客户端与 stdio 服务执行链。应按官方合同修正或精简具体能力，不能按旧 GUI 所属面板删除整个服务。 |
| `src/AvaloniaDevTool.Previewer/` | `attach-to-file` 实际启动的宿主，不是 Inspector GUI。它负责设计上下文、程序集装载、源文件监视和预览生命周期。 |
| `tests/AvaloniaDevTool.Cli.Tests/` | 真实 SDK stdio `initialize/tools/list/tools/call` 和目标 Agent 的联通验证；保留官方工具合同相关测试，已有扩展工具用例在确定最终工具表后调整。 |
| `tests/AvaloniaDevTool.Previewer.Tests/` | 15 个现有方法验证加载错误保留、原子保存与去抖、停止监视、程序集身份和版本拒绝、源文件预算、真实 PNG 与根更新；CLI 的端到端用例不能替代这些边界。 |
| `tests/AvaloniaDevTool.PreviewFixture/` | CLI 预览的真实已构建应用，提供 App 资源、x:Class、设计 guard 和阻塞启动。`Cli.Tests.csproj:13,19` 引用；不是可删除的演示应用。 |
| `tests/AvaloniaDevTool.PreviewFixture.Controls/` | 控件库与应用程序集分离的预览 fixture。主 fixture 的项目引用和 `Cli.Tests.csproj:22` 使用它，验证独立 `applicationAssemblyPath`。 |
| `tests/AvaloniaDevTool.Agent.Tests/` | 真实 Avalonia 控件、命名管道、UI 线程、弱引用、恢复、预算及异常隔离。测试服务本身，无需 Inspector 才有价值。 |
| `tests/AvaloniaDevTool.Protocol.Tests/` | 帧限制、取消、断开、WebSocket stream、RPC trace、源生成 JSON 元数据等跨工具基础保证。 |
| `tests/Fixtures/asset-long-utf8.txt`、Agent.Tests 的 `Assets/` | 真实资产与 UTF-8 分块/预算输入；CLI.Tests 的 `AvaloniaResource` 直接引用通用 fixture。 |

`PreviewAttachTests.cs` 的 8 个现有方法还独立验证：classful 项目资源/自定义控件/输入/像素与保存、Window 根及 MCP 正常退出、缺少构建和不存在的类、Style/ResourceDictionary 设计转换、分离的控件库和 App、阻塞启动超时和取消、外部应用不归本会话杀进程、宿主异常退出时释放阻塞子进程。收缩分支必须继续保留这些行为，或把官方合同要求的行为迁移到新的等价真实进程测试。

## 样例和 Native 验证不能按目录名清除

| 路径 | 判断 |
| --- | --- |
| `samples/AvaloniaDevTool.Sample/` | 保留为当前真实桌面诊断目标，可重写为更小的 Agent/MCP 测试样例。它承载多窗口、Flyout、绑定文化、资源、资产、真实 ILogger 与 Meter；不要求继续提供 Inspector 启动和操作文案。 |
| `samples/AvaloniaDevTool.Sample/Previews/PreviewCard.axaml` | `Previewer.Tests.csproj:15-16` 将其复制为测试资源；`PreviewIntegrationTests.cs:247` 实际验证资源、样式、绑定和设计尺寸。若要删样例目录，先把该 fixture 迁移到测试工程，保持断言。 |
| `samples/AvaloniaDevTool.AotSample/` | 最小真实消费应用，验证 Agent 在裁剪和 Native AOT 下的可用范围。保留有助于防止纯 JIT 测试掩盖目标内反射问题。 |
| `tests/AvaloniaDevTool.AotSmoke/` | 实际启动消费 EXE，通过真实 Client 验证三种树、直接子项分页、popup-freeze 状态、上下文属性、临时属性与通知、PNG、拾取、断开恢复和重新连接；不是 GUI 测试。 |
| `tests/scripts/Test-NativeAot.ps1` | 发布 AotSample 并运行 AotSmoke；严格模式与允许兼容警告模式有不同证据含义。若官方范围不承诺 AOT，可单独标记实验验证，但不能把允许警告的 smoke 通过写成完整 AOT 支持。 |
| `tests/AvaloniaDevTool.Protocol.Tests/ProtocolJsonAotTests.cs` | DTO、集合及嵌套错误的源生成元数据和 JSON 格式保证，普通序列化测试不能替代。 |

## Remote、Logging 及扩展能力的待确认边界

这些内容有实际调用，不能认定是可独立删除的 GUI 残留；同时，存在调用和测试不代表它们已属于官方 MCP 合同。

| 内容 | 已证实的当前调用 | 官方规范确认后的动作 |
| --- | --- | --- |
| `src/AvaloniaDevTool.Remote/`、`tests/AvaloniaDevTool.Remote.Tests/` | CLI `InspectorTools.Remote.cs:9-11` 注册 `attach-to-remote`；`ToolSession.cs:90-104` 调 `ConnectRemoteAsync`；`Cli.Tests/RemoteMcpTests.cs:32` 启动真实服务，Sample/App.Remote.cs 亦使用。11 个既有用例包含认证/Origin、共享单会话 gate、分片、关闭与重连、TLS、取消和地址限制。 | 若官方 MCP 不公开远程附加，可移除该扩展入口；是否保留 Remote 作为 Agent 传输要按官方 Agent 连接规范判断。若决定整个远程能力退役，相关服务/Client partial/DTO/测试成组处理，不保留失效测试引用。 |
| `src/AvaloniaDevTool.Logging/` | `Agent.Tests/StructuredLoggingTests.cs:61` 用真实 LoggerFactory 验证原模板、EventId、scope、level 和异常；Sample/SampleDiagnostics.cs 使用 provider。 | 若官方 Agent 要采集 Microsoft.Extensions.Logging，必须保留或提供等价适配。若官方只要求 Avalonia 日志，可单独移除该 provider，但不能一并删除 Agent 的 ActivityService、日志来源、结构化快照和背压测试。 |
| `src/AvaloniaDevTool.Agent/DiagnosticsAgent.Launcher.cs` 与 Options 的 `LaunchGesture` / `InspectorExecutablePath` / `InspectorLauncher` | 构造/释放 Agent 时安装/卸载；`Agent.Tests/NavigationIntegrationTests.cs:15-34` 独立验证 F12 范围、去抖和释放。 | 先确认官方 Agent 是否承担工具启动。若仅由 CLI 主动附加，可让该 GUI launcher 能力及其独特测试退出范围；不可将它误计为被别的测试覆盖。 |
| Agent 的 Metrics、Profiler、Breakpoints、Composition、Overlay、Renderer、PopupFreeze、runtime object、资源/资产扩展 | 当前 `InspectorTools.*.cs` 大多直接公开相应 MCP 工具；Agent 和 CLI 测试均有服务行为证明。 | 以官方工具表和目标内服务合同划定最终范围。不要因旧 GUI 对应面板删除底层服务，也不要因为源码存在而继续对外公开官方没有的额外工具。 |

## 删除 GUI 测试时的覆盖归属

原 Inspector.Tests 的 GUI AXAML、草稿、图表、相机、鼠标命中、对话框、剪贴板、面包屑、异步选择和 UI 旧响应竞态，属于被移除的产品；它们没有自动变成 CLI 覆盖，也不需要为已退役界面保留测试。

仍保留的服务行为分别由以下测试族证明：

| 被移除的 GUI 场景 | 保留的服务端验证 |
| --- | --- |
| 属性/画刷编辑、绑定详情与来源、资源查询/冲突 | `Agent.Tests/PropertyPresentationTests.cs`、`BrushEditorRpcTests.cs`、`BindingDetailsTests.cs`、`BindingCultureTests.cs`、`NavigationIntegrationTests.cs`、`ResourceQueryTests.cs`，以及 CLI 的对应 stdio 用例。 |
| 弱对象导航、分页、选择定位、Merged 树、游标 | `RuntimeInspectionTests.cs`、`MergedTreeTests.cs`、`TreeWorkflowTests.cs`、`TreePagingTests.cs`；CLI `TreePagingMcpTests.cs` 等。GUI 的旧选择响应保护随着 GUI 退役，不宣称服务测试覆盖它。 |
| 日志/事件/断点、历史与预算 | `StructuredLoggingTests.cs`、`LoggingBudgetTests.cs`、`EventRouteTests.cs`、`BreakpointIntegrationTests.cs`、`BreakpointSourceTests.cs`；CLI `BreakpointSourceMcpTests.cs`。GUI 本地事件默认值文件存储不在服务测试内。 |
| 几何/合成、渲染器、覆盖层与弹层 | `LayoutInspectionTests.cs`、`CompositionInspectionTests.cs`、`RendererDiagnosticsTests.cs`、`OverlayTests.cs`、`PopupFreezeTests.cs`、`PopupRootDiscoveryTests.cs`。GUI 场景相机和画布交互随界面退役，Agent 像素与坐标验证仍保留。 |
| Metrics/Profiler 和资产 | `MetricsIntegrationTests.cs`、`MetricsWindowTests.cs`、`ProfilerIntegrationTests.cs`、`AssetPreviewTests.cs`、`AssetExportTests.cs`。图表同步/按钮竞态与字体预览注册为 GUI 独特行为，不能计入保留的服务能力。 |

删除前最后检查应按行为归属完成，而不是比较测试总数。旧 `476` 项包含 Inspector `114` 与 XamlMcp Backend `19`；分支删去这些产品后必须重新构建和运行余下项目，不能将旧总数继续作为当前验证结果。

## 文档收缩建议

完整历史已在 `main/fa1c623`，本分支不需要再复制一套 `archive/`。仅保留对官方 Agent / MCP 的实现、验收与许可仍有直接意义的材料，并让当前入口指向 `docs/agent-tools/`。

| 旧路径 | 本分支处理 |
| --- | --- |
| `docs/xamlmcp-backend.md`、`docs/xamlmcp-inspector.md`、`docs/remote-inspector.md`、`docs/protocol-monitor.md` | 随独立 Inspector/外部只读后端删除；仍可从旧基线阅读。Protocol trace 若保留，后续只写其服务端行为。 |
| `docs/implementation-status.md`、`docs/research/developer-tools-feature-catalog.md` | 旧 125+16（141 项）GUI/产品目录及 476 测试阶段审计退出本分支交付入口；用官方 MCP 工具/Agent 服务合同的独立差异矩阵替代，不再沿用其完成百分比和编号。 |
| `README.md`、`docs/architecture.md`、`docs/testing.md`、`docs/mcp.md` | 重写为 Agent/MCP 安装、协议、真实 target/stdio/attach-to-file 验证；删 GUI 启动、双后端和历史阶段总数。 |
| `docs/preview-attach.md`、`docs/previewer.md`、`docs/native-aot.md`、`docs/remote-transport.md`、`docs/logging.md` | 根据最终保留行为提炼到 Agent/MCP 文档；保留真实限制和可复现命令。没有官方合同支持的扩展，明确分离或删除。 |
| `docs/properties.md`、`binding-diagnostics.md`、`tree-workflows.md`、`merged-tree.md`、`resources.md`、`assets.md`、`input.md` 等服务说明 | 保留需要的协议、目标内行为、预算和测试证据；删除 GUI 交互说明。是否保留对应服务仍由官方合同决定。 |
| `docs/layout-3d.md`、`composition-capture.md`、`metrics.md`、`profiler.md`、`breakpoints.md`、`overlays.md`、`renderer-diagnostics.md`、`popup-freeze.md`、`events.md`、`object-browser.md`、`contextual-properties.md` | 不以面板名字判定去留。必要服务内容随 Agent 合同迁移；GUI 展示/相机/图表/保存对话框验收退回旧基线。 |
| `docs/research/developer-tools-2.2.3.md`、`native-deep-analysis.md`、`diagnostic-services-deep-analysis.md` 及 `sources/developer-tools-source-index.json`、`native-ui-candidates.json` | 静态/原生 GUI 研究留在历史基线；只抽取对官方 Agent 和预览编排有直接证明价值的证据到新合同文档，保留原始版本、哈希与边界。 |
| `docs/research/sources/mcp-tools-2.2.3-index.json` | 官方 MCP 工具声明证据，保留或迁移到新证据目录；`tools/list` 声明不等于调用行为验证。 |
| `docs/research/open-source-agent-tools-audit.md`、`open-source-reuse-audit.md`、`xamlmcp-compatibility-validation.md`、`cdp-compatibility-validation.md` | 外部后端产品路线和试验留在旧基线；已采用算法的来源、许可和差异提炼到新的复用/许可清单，避免删除归属证据。 |

## 必须保留或迁移的许可告知

- `src/AvaloniaDevTool.Agent/BindingDetailsReader.cs:1-2` 的 XamlMcp 改编来源仍然有效。保留 Agent 下 `ThirdParty/THIRD-PARTY-NOTICES.md`、`XamlMcp-LICENSE.txt` 和 `AvaloniaDevTool.Agent.csproj` 对 `ThirdParty/*` 的 build/publish 复制规则。删除整个外部后端不影响 Agent 内改编代码的归属要求。
- CLI 仍引用 `ModelContextProtocol` 2.3.0。收缩前仓库中对应完整许可证仅位于 `src/AvaloniaDevTool.Backends.XamlMcp/ThirdParty/McpSdk-2.3.0-LICENSE.txt`；删除 backend 目录前，应将这一份原文迁移到 CLI 的第三方目录，并随 CLI 构建/发布输出保留。该原文包含 Apache 2.0 转许可及保留 MIT 贡献告知，不能只保留其中一种。
- `McpSdk-1.4.1-LICENSE.txt` 对应被下载的外部 XamlMcp.Server 独立运行目录；不再分发该上游 Server 后可连同其专用告知退役。
- 目前 Agent 已确认的源码改编是 XamlMcp 绑定诊断；审计没有将 CDP/Declarative/ClassicDiagnostics 的 MIT 候选研究误报成已复制代码。以后若采用其代码或重要部分，要随具体副本保留版权、完整许可及修改来源，不能用删除研究文档代替归属清理。

## 审计验证范围

本次执行了全仓 `.csproj` ProjectReference 搜索、具体构造/调用点搜索、fixture 输入与测试方法检查、许可文件和复制规则检查；没有修改产品源码，也没有执行构建/测试。依赖审计支持上述删除计划，实际删除后的正确性需要本分支重新构建和运行保留的测试工程确认。
