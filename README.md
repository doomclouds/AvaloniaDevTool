# AvaloniaDevTool

AvaloniaDevTool 是面向 Avalonia 桌面应用的独立运行时检查器。Native 模式使用本项目 Agent，Inspector 和 stdio MCP 共用诊断服务；Inspector 另已接入公开 NuGet XamlMcp 后端，可读取只安装上游 Agent 的独立目标。当前使用 .NET 10 和 Avalonia 12.1.4，包含检查器、应用侧 SDK、后端适配、CLI／MCP、独立预览宿主、常规测试应用和 Native AOT 消费样例。

## 当前能力

下表为 Native 模式。XamlMcp 模式只开放已经验证的共同只读能力，详见后端部分。

| 范围 | 已有实现与边界 |
| --- | --- |
| 连接与树 | 本机管道与可选 Native WebSocket；Visual／Logical／Merged 按需加载、直接子项游标分页、预算化递归／模板范围、折叠释放、实际 selector／模板父入口／元数据复制、搜索／定位／拾取；网络路径已验 Windows loopback，跨机待验 |
| 高亮与测量 | 根 DIP 四角、带符号 Margin／Padding、通用信息及 TextBlock／TextBox／Image 专用元数据、DIP／设备像素标尺及引导线；树与盒模型分区悬停恢复所选高亮，截图排除诊断层，盒模型语义和 clip 限制明确 |
| 属性与样式 | Avalonia 属性及显式开启的只读 CLR 属性、已知布局父级的附加属性筛选、分类／排序／草稿保留、官方 ColorPicker、四种内置画刷结构化草稿与实际 IImage／Geometry 预览；绑定 Source／DataContext／ResourceKey 导航、文化／格式／源更新时机及活动／错误筛选，缺失和不适用字段分开 |
| 资源 | 应用／样式／选中元素资源层级、嵌套聚合、分页／键／类型筛选与来源；原字典已有字符串键临时编辑及冲突保护，非字符串键用弱 ID 重读；不计算唯一胜出项 |
| 内嵌资产 | avares URI 复制、文本／图片元数据与 TTF／OTF 字体预览；完整导出按 64 KiB 分块校验 SHA-256，上限 100 MiB；预览、解码和文件写入各有独立预算，WOFF 尚不支持 |
| 日志与事件 | 结构化日志与可选 ILoggerProvider／自定义源；真实事件目录、可保存的六组默认选择、关联路由及源／节点跳转；Handled 仅表示类处理器观察区间 |
| 指标与断点 | 真实 Avalonia／System.Runtime 及自定义 Meter、tag、真实时间窗／帧聚合／P90、同步图与单图清空；独立 Source／接收节点事件条件、属性／事件断点、命中次数条件、日志和已附加调试器下的暂停请求 |
| 渲染诊断 | 框架自身的 Fps／DirtyRects／LayoutTimeGraph／RenderTimeGraph，可按窗口／全部根开启并跟随新根；停止恢复工具拥有的值，应用后改值保留并报告冲突 |
| 性能录制 | 真实 Avalonia Activity 的样式附加、激活器求值及资源查找，支持开始／停止／清空、微秒耗时和保留窗口分组；缺失结果保持未知，完整统计语义仍有限 |
| 元素交互 | Focus、BringIntoView、可撤销 Enable／Disable、伪类预览和 Popup／标准 Flyout light-dismiss 冻结恢复；菜单／ToolTip／transient 关闭另有边界；框架合成输入已通过 Agent／Client／MCP 接入，详见 [输入语义](docs/input.md) |
| 截图与层级 | 96 DPI 元素 PNG；VisualGeometry 几何视图与 CompositorOwnDrawing 自身绘制 PNG 分开采集，支持图像层投影、平移及深度筛选；effects／mask 等组合语义有显式限制 |
| CLR 对象 | 只读公共成员、DataContext、嵌套对象、IList／IDictionary 分页和弱对象 ID；Inspector 对象页支持面包屑、后退与循环提示，Client／MCP 共用后端 |
| 源文件预览 | MCP attach-to-file 管理独立进程，加载显式选择的已构建项目与 classful AXAML、App 资源／主题、输入／PNG、保存重载和错误保留；不自动构建或调用目标 Main |
| 协议监视 | 本地录制真实 Native RPC、请求／响应／通知关联、错误／取消／重连状态、可选脱敏正文与双重预算；不录制 MCP stdio 或原版私有协议 |
| MCP | 官方 C# MCP SDK stdio 服务；可连接管道或通过 attach-to-remote 使用 Native WebSocket，凭据从明确命名的环境变量读取；每个 Agent 的管道与网络入口共用单会话门 |

Inspector 现有十二主页面：属性、样式绑定、资源、资产、日志事件、交互截图、指标、断点、3D几何、对象、性能、协议；事件路由、高亮测量与渲染诊断位于对应子页。页面存在不代表原版同名模块的全部功能已经等价实现，范围和验证以对应文档为准。

Phase 8 最终统一批次为 **476／476 项通过、无跳过**，七份 TRX 位于 `artifacts/phase8-verified-results`；全部 22 项目 Debug／Release 构建均退出码 0、零警告、零错误。本轮新增直接子项分页、上下文附加属性、弹层冻结、从模板父复制到部件及绑定文化／格式／源更新时机，共 43 个测试方法、58 个展开用例，均已进入该统一批次。首轮 474 项的 4 例失败和 Phase 7 的 418 项保留为对应源码历史。

Windows 默认窗口已复验相关属性开关、fr-FR／en-US 实际格式和 LostFocus 写源、绑定文化／来源／格式字段，以及标准 Native Flyout 冻结外点保持和恢复后外点关闭；断开与本轮两个自有进程清理完成。2305 子项分页和其他弹层的更广范围仍按各自自动化及限制记录，详见 [测试记录](docs/testing.md)。

能力目录中的 136 个真实功能项为 A 86、P 41、U 9，约 63% 的功能条目已有自动化证实；另有 5 个 B 产品边界单列，不作为已完成功能计数。这个条目口径不等于原版全部参数、平台或工作量的覆盖率。

最小消费程序的 trimmed 与真实 Native AOT 核心均已通过独立进程检查；默认严格 Native AOT 发布仍实际失败，编译阶段有 46 条 IL error，因此 C13 保持部分实现。详情见 [AOT 消费验证](docs/native-aot.md)、[完成度](docs/implementation-status.md) 与 [测试记录](docs/testing.md)。

## 快速运行

需要 .NET 10 SDK 和桌面环境。在仓库根目录执行：

```powershell
dotnet build AvaloniaDevTool.slnx
dotnet test AvaloniaDevTool.slnx
```

打开两个终端。先运行测试应用：

```powershell
dotnet run --project samples/AvaloniaDevTool.Sample -- --pipe avalonia-devtool-demo
```

再运行检查器：

```powershell
dotnet run --project src/AvaloniaDevTool.Inspector -- --pipe avalonia-devtool-demo
```

示例默认 Debug 配置并启用 Agent；Release 示例不启动 Agent。省略 --pipe 时，管道名为 avalonia-devtool-{进程 ID}，窗口内可复制。Windows 可发现使用本工具前缀的管道；其他平台需手动输入，当前桌面验收基线为 Windows。

完整构建后，Sample 会寻找本仓库已构建的 Inspector；在目标窗口按 F12 可打开并连接 Native 检查器。自己的应用如需快捷启动，可在 DiagnosticsAgentOptions 中配置 InspectorExecutablePath 或 InspectorLauncher。

连接后可选择树节点或开启拾取，再使用各诊断页。属性修改通过临时覆盖生效，清除或断开后恢复原绑定、样式和本地值；直接属性只读。资源编辑有独立的冲突检查，不会覆盖应用后来写入的新值。

### MCP

先断开 GUI 对同一 Agent 的连接，再启动已构建的 stdio 服务器：

```powershell
dotnet src/AvaloniaDevTool.Cli/bin/Debug/net10.0/AvaloniaDevTool.Cli.dll mcp --pipe avalonia-devtool-demo
```

标准输出只承载 MCP。工具名和输入参数以 tools/list 为准，接入方式见 [MCP 与命令行](docs/mcp.md)。GUI 与 MCP 当前不能同时共享同一个目标连接。

### Native WebSocket

可选 `AvaloniaDevTool.Remote` 项目为现有 Agent 增加 HTTP upgrade／二进制 WebSocket 入口，默认只监听 loopback。Sample 加 `--remote` 可启动演示；Inspector 可输入 endpoint 和遮罩令牌，或使用 `--endpoint`／`--token-env`。MCP `attach-to-remote` 的 tokenEnvironmentVariable 同样只指定服务器进程中的环境变量名称，令牌不进入 URL 或工具参数。

非 loopback 必须显式配置 TLS 证书，客户端保留系统证书验证。当前实际自动化是 Windows loopback；LAN／VPN、可信 TLS 跨主机、WSL2、移动和浏览器尚未实测。配置与认证、Origin、释放和平台边界见 [远程传输](docs/remote-transport.md)。

令牌遮罩用于视觉显示，UIA／进程内读取仍须按敏感凭据处理。启动参数、遮罩输入和实际桌面证据见 [远程 Inspector](docs/remote-inspector.md)。

### XamlMcp 只读后端

先运行只引用公开 NuGet Agent 的独立目标：

```powershell
dotnet run --project samples/AvaloniaDevTool.XamlMcpTarget -- --enable-inspection
```

目标窗口显示 PID。再启动外部模式，把示例 PID 换成当前值：

```powershell
$targetPid = 12345
$xamlMcpServer = (Resolve-Path src/AvaloniaDevTool.Inspector/bin/Debug/net10.0/Upstream/XamlMcpServer/XamlMcp.Server.dll).Path
dotnet run --project src/AvaloniaDevTool.Inspector -- --backend xamlmcp --target $targetPid --server $xamlMcpServer
```

构建固定下载并携带 XamlMcp.Server 1.0.0-preview.3；目标使用同版本 XamlMcp.Avalonia Agent。Inspector 经 IInspectionBackend 读取树、只读属性、样式／绑定摘要、祖先和 PNG。它不要求目标引用本项目 Agent，也不复制上游认证管道。

外部树来自有界快照而非实时推送；刷新会使旧句柄失效，并清除旧选择与截图。缺失的 owner、setter 匹配或绑定字段保持 Unknown／null；Native 的编辑、拾取、资源、日志、指标、断点和 3D 等高级能力在此模式禁用。当前本项目 CLI MCP 仍连接 Native Agent，不会随 GUI 模式自动切换。

详情见 [后端与快照语义](docs/xamlmcp-backend.md)、[Inspector 外部模式](docs/xamlmcp-inspector.md)。本轮实际适配和桌面验证固定在公开 1.0.0-preview.3 与 Avalonia 12.1.4，不泛化为所有上游或框架版本。

### XAML 文件附加与独立预览

MCP `attach-to-file` 接收现有 AXAML、项目和已构建程序集的绝对路径；控件库源码可另选启动应用程序集。它初始化实际 App 资源、保留 `x:Class`，再把预览作为普通诊断会话使用。文件保存后重载，错误时保留旧视图；detach 和 MCP 退出会清理自建进程。它不附加任意 IDE 设计进程，也不猜测构建输出。调用参数与项目边界见 [文件附加](docs/preview-attach.md)。

原有独立 classless 模式仍可直接运行：

```powershell
$previewXaml = (Resolve-Path samples/AvaloniaDevTool.Sample/Previews/PreviewCard.axaml).Path
dotnet run --project src/AvaloniaDevTool.Previewer -- --xaml $previewXaml --pipe avalonia-devtool-preview
```

然后用 Inspector 或 MCP 连接 avalonia-devtool-preview。预览器的 Agent 在 Debug 和 Release 均启用。支持范围、可选程序集和设计模式限制见 [预览器](docs/previewer.md)。

## 项目结构

| 项目 | 职责 |
| --- | --- |
| src/AvaloniaDevTool.Protocol | DTO、方法名、4 MiB 长度限制的内部 RPC 帧 |
| src/AvaloniaDevTool.Client | 会话、请求、通知以及各诊断域的调用 API |
| src/AvaloniaDevTool.Agent | 应用内树、属性、样式、资源、观察、交互与采集服务 |
| src/AvaloniaDevTool.Logging | 可选 Microsoft.Extensions.Logging provider；核心 Agent 不依赖该日志框架 |
| src/AvaloniaDevTool.Remote | 可选 Kestrel Native WebSocket 入口；核心 Agent 不依赖 ASP.NET Core |
| src/AvaloniaDevTool.Backends.XamlMcp | 经公开 Server／Agent 读取共同能力的只读适配器 |
| src/AvaloniaDevTool.Inspector | 十二页 Avalonia 桌面检查器 |
| src/AvaloniaDevTool.Cli | 独立 stdio MCP 入口 |
| src/AvaloniaDevTool.Previewer | 已构建项目与 classless XAML 文件预览进程 |
| samples/AvaloniaDevTool.Sample | 绑定、动态控件、多窗口、弹层及预览文件 |
| samples/AvaloniaDevTool.AotSample | 直接消费 Agent 的最小 Avalonia 样例，供 trimmed／真实 Native AOT 发布验证 |
| samples/AvaloniaDevTool.XamlMcpTarget | 仅接入上游 NuGet Agent 的独立可见／Headless 目标 |
| tests/AvaloniaDevTool.AotSmoke | 独立 Client 验证进程，连接实际发布目标并检查核心树／属性／截图／拾取与清理 |
| tests/AvaloniaDevTool.Protocol.Tests | 协议、会话和消息边界 |
| tests/AvaloniaDevTool.Remote.Tests | 真实 loopback Kestrel／ClientWebSocket、认证、Origin、TLS 拒绝与共享会话生命周期 |
| tests/AvaloniaDevTool.Agent.Tests | 应用侧真实 Avalonia／Skia Headless 服务测试 |
| tests/AvaloniaDevTool.Inspector.Tests | AXAML、ViewModel 与真实管道集成 |
| tests/AvaloniaDevTool.Cli.Tests | 真正 stdio MCP 客户端的跨进程调用 |
| tests/AvaloniaDevTool.Previewer.Tests | 预览加载、文件监视、重载、错误及 Agent 接入 |
| tests/AvaloniaDevTool.PreviewFixture 与 .Controls | 文件附加测试实际构建的 classful 应用与独立引用控件库 |
| tests/AvaloniaDevTool.Backends.XamlMcp.Tests | 快照缓存、公开 Server 和独立 NuGet 目标进程的集成验证 |

## 在自己的应用中接入

引用 Agent 项目，在桌面应用初始化时创建，并在退出时释放：

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using AvaloniaDevTool.Agent;

public partial class App : Application
{
#if DEBUG
    private DiagnosticsAgent? _diagnostics;
#endif

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
#if DEBUG
            _diagnostics = DiagnosticsAgent.Attach(this, new DiagnosticsAgentOptions
            {
                ApplicationName = "My Avalonia App",
                PipeName = $"avalonia-devtool-{Environment.ProcessId}"
            });
            desktop.Exit += (_, _) => _diagnostics?.Dispose();
#endif
        }
        base.OnFrameworkInitializationCompleted();
    }
}
```

Agent 在目标进程监听命名管道，连接限制为当前用户。连接超时 5 秒，单次请求超时 15 秒；取消等待不能强制中止目标 getter 或同步指标回调。框架版本兼容性、帧预算和释放职责见 [架构](docs/architecture.md)。

## 文档与研究入口

- [架构与协议](docs/architecture.md)、[测试与手工验证](docs/testing.md)。
- [MCP](docs/mcp.md)、[指标](docs/metrics.md)、[协议监视](docs/protocol-monitor.md)、[Profiler](docs/profiler.md)、[断点](docs/breakpoints.md)、[输入](docs/input.md)。
- [Native WebSocket 传输](docs/remote-transport.md)、[AOT 消费验证](docs/native-aot.md)、[属性与编辑器](docs/properties.md)、[上下文附加属性](docs/contextual-properties.md)、[资产预览与导出](docs/assets.md)。
- [树工作流与 selector](docs/tree-workflows.md)、[高亮与测量](docs/overlays.md)、[事件路由](docs/events.md)、[原生渲染诊断](docs/renderer-diagnostics.md)。
- [资源查询](docs/resources.md)、[结构化日志与接入](docs/logging.md)、[Compositor 自身绘制采集](docs/composition-capture.md)。
- [Merged 模板树](docs/merged-tree.md)、[结构化绑定诊断](docs/binding-diagnostics.md)、[弹层冻结](docs/popup-freeze.md)。
- [只读对象浏览](docs/object-browser.md)、[3D 几何](docs/layout-3d.md)、[文件附加](docs/preview-attach.md)、[独立预览器](docs/previewer.md)。
- [XamlMcp 后端](docs/xamlmcp-backend.md)、[Inspector 双后端](docs/xamlmcp-inspector.md)。
- [原版功能目录](docs/research/developer-tools-feature-catalog.md)：公开能力、前提和验收条件；V1 状态列是历史基线。
- [2.2.3 静态分析](docs/research/developer-tools-2.2.3.md)、[诊断服务深挖](docs/research/diagnostic-services-deep-analysis.md)、[原生深挖](docs/research/native-deep-analysis.md)。
- [开源复用核查](docs/research/open-source-reuse-audit.md)、[社区 AgentTools 审计](docs/research/open-source-agent-tools-audit.md)。
- [XamlMcp 兼容实证](docs/research/xamlmcp-compatibility-validation.md)、[CDP 兼容实证](docs/research/cdp-compatibility-validation.md)。

开源复用已从审计进入实际产品集成：XamlMcp 公开 NuGet Server／Agent 的只读共同后端已接入 Inspector，并有独立进程和真实桌面证据；构建保留上游许可与归属文件。CDP 已完成限定范围的兼容试验，尚未作为产品后端接入；ClassicDiagnostics 和 Declarative AgentTools 的复用仍需按模块评估。各项目的能力、许可与版本边界见对应审计和验证文档。

当前仍有严格无警告 AOT、跨主机／VPN／WSL2 验证、移动／Browser、多客户端共享、Image／Drawing／shader／custom 画刷编辑、属性预览取消清理专项、CLR 编辑、Profiler 完整统计、Compositor 效果／遮罩／GPU 与 IDE 进程附加等缺口。弹层冻结保留 ContextMenu／ToolTip／transient 的独立关闭路径；上下文属性只过滤已知布局白名单。专用信息只覆盖 TextBlock／TextBox／Image，文本不传内容、密码跳过长度与布局，自定义图像与来源 URI 明示未知；Handled 观察不提供实例 handler 归因；资源 winner 和全部媒体格式也未覆盖。剩余范围见 [实现完成度](docs/implementation-status.md)；内部 wire 保持本项目独立协议。
