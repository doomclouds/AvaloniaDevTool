# AvaloniaDevTool

AvaloniaDevTool 提供自研 Avalonia 应用内 Agent 和独立 stdio MCP，当前使用 .NET 10、Avalonia 12.1.4。默认 `agent` profile 提供 **44 项工具**：canonical 16、`ui-*` 9、`native-*` 8、`file-dialog-*` 8、`capture-*` 3。能力包括通用界面操作、控件树/属性/状态、真实 Windows 文件弹窗、控件/窗口/区域 PNG，以及原始 metrics、样式/资源 Profiler、logs/events。

产品提供操作和数据，调用方负责业务关联、业务判断及自己的流程编排。代表性业务脚本只用于研发验证；不提供业务断言引擎、视觉比对、性能阈值、发版门禁或 CI 平台。当前边界见 [Agent 范围](docs/agent-tools/architecture/current-agent-scope.md)，实现与验证结果见 [实现状态](docs/implementation-status.md) 和 [测试记录](docs/testing.md)，工具数和测试数均不表示产品完成率。

## 快速运行

需要 .NET 10 SDK；可见 Sample 和预览窗口还需要桌面环境。在仓库根目录构建：

```powershell
dotnet build AvaloniaDevTool.slnx
```

先在一个终端运行真实测试目标：

```powershell
dotnet run --project samples/AvaloniaDevTool.AutomationSample -- --pipe avalonia-devtool-demo
```

AutomationSample 是通用能力的研发验证目标，Debug/Release 均启用本项目 Agent，覆盖实际多窗口、模态、弹层、选择、滚动、拖动和真实平台 StorageProvider 文件读写。可选 `--test-root`、`--ready-file`、`--result-file` 便于隔离测试数据和核对内容/hash；详情见[样例说明](samples/AvaloniaDevTool.AutomationSample/README.md)。再由 MCP 客户端启动已构建的服务：

```powershell
dotnet src/AvaloniaDevTool.Cli/bin/Debug/net10.0/AvaloniaDevTool.Cli.dll mcp --pipe avalonia-devtool-demo
```

省略 `--profile` 等同 `--profile agent`。标准输出只承载 MCP，日志写入标准错误；客户端宜直接运行 DLL，避免 `dotnet run` 的构建输出混入 stdio。省略 `--pipe` 时，先调用 `attach-to-app {}` 发现本地目标，再用返回的 `id` 附加；该 ID 不是约定的操作系统 PID。MCP 附加不需要 F12，单个 Agent 同时接受一个诊断连接。详细启动配置和调用示例见 [MCP](docs/mcp.md)。

## 工具与预览

| 范围 | 默认 agent 工具 |
| --- | --- |
| 会话 | `attach-to-app`、`attach-to-file`、`detach` |
| 树和搜索 | `tree`、`ancestors`、`search` |
| 属性和样式 | `props`、`set-prop`、`styles`、`pseudo-class` |
| 资源和媒体 | `resources`、`assets`、`open-asset`、`screenshot` |
| 交互 | `input`、`action` |
| 框架交互与截图 9 项 | `ui-input`、`ui-click`、`ui-key-chord`、`ui-drag`、`ui-text-selection`、`ui-select-item`、`ui-control-action`、`ui-wait`、`ui-screenshot` |
| Windows 原生 8 项 | `native-windows`、`native-tree`、`native-find`、`native-inspect`、`native-action`、`native-action-status`、`native-input`、`native-screenshot` |
| 文件弹窗 8 项 | `file-dialogs`、`file-dialog-inspect`、`file-dialog-set-paths`、`file-dialog-set-filter`、`file-dialog-accept`、`file-dialog-cancel`、`file-dialog-overwrite`、`file-dialog-wait` |
| 同连接原始采集 3 项 | `capture-start`、`capture-status`、`capture-stop` |

前五行是 canonical 16，显式 `--profile official` 仅注册这组兼容工具。`ui-*` 使用当前 Avalonia 数值节点；native/file-dialog 使用独立字符串 ID，仅操作已附加本地进程及 owned 窗口。native 不占 managed 调用 gate，允许 managed 调用等待模态时继续处理系统弹窗。详情见 [Agent 交互](docs/agent-tools/features/agent-interaction.md)。

`ui-screenshot` 捕获控件及局部 DIP 区域，可指定输出缩放；`native-screenshot` 捕获限定窗口/节点的物理像素区域，使用 PrintWindow，不激活窗口或替换为桌面截图。两者返回 PNG image 和元数据，原生 GPU/受保护等表面可能不完整。`capture-*` 复用当前目标连接，返回原始诊断快照、损失和错误，停止后冻结，不评价业务或性能是否通过。

`attach-to-file` 接收 `xamlFilePath`、`xamlProjectPath` 和可选 `build=false`。它从真实 MSBuild 项目上下文解析产物，`build=true` 先构建，随后启动本项目 Previewer；保存 XAML 后可重载，更新后应重新 `tree/search` 获取节点。Previewer 是该工具的必要运行和分发依赖，不能随 GUI 删除。项目选择、进程清理和支持边界见 [文件预览](docs/agent-tools/features/preview.md)。

旧诊断扩展保留在显式 `--profile extended`，工具目录为旧 **62 项**。它与 official 是两套独立契约，**不是官方工具的兼容超集**；同名工具的参数和行为可能不同，例如旧 `set-prop` 使用 JSON null 清覆盖，官方入口使用字符串 `"null"` / `"unset"`，文件附加参数也不同。Remote、日志、指标、Profiler、断点、几何等保留的服务能力不计入官方 16 项分母，使用前以该 profile 的 `tools/list` 为准。

## 在自己的应用中接入

核心接入点是 `AvaloniaDevTool.Agent`。源码消费时引用该项目，在 Avalonia 初始化完成后附加，并在应用退出时释放：

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

本项目 Agent 不要求安装原版 DeveloperTools。默认管道限制当前用户；对象访问进入目标 UI 线程，临时诊断覆盖在断开时释放。可选 `AvaloniaDevTool.Logging` 接入 Microsoft.Extensions.Logging，`AvaloniaDevTool.Remote` 提供扩展 Native WebSocket 传输，核心 Agent 不依赖这两者。内部协议、对象身份与释放职责见 [架构](docs/architecture.md)。

## 项目结构

解决方案当前包含 **22 个项目**：9 个生产项目、3 个消费样例、10 个测试或测试支撑项目。

| 项目 | 职责 |
| --- | --- |
| `src/AvaloniaDevTool.Agent` | 目标内 SDK、Avalonia 对象访问与诊断服务 |
| `src/AvaloniaDevTool.Client` | 类型化客户端、连接、请求与通知 |
| `src/AvaloniaDevTool.Protocol` | DTO、内部 RPC、JSON 源生成元数据 |
| `src/AvaloniaDevTool.Cli` | 独立 stdio MCP，默认 agent / 显式 official、extended profile |
| `src/AvaloniaDevTool.NativeAutomation` | net10.0 跨平台 facade、native 会话和真实文件弹窗状态机 |
| `src/AvaloniaDevTool.NativeAutomation.Worker` | net10.0-windows7.0 隔离进程，Windows UIA3 和 OS 输入 |
| `src/AvaloniaDevTool.Previewer` | 文件附加所需的独立设计期预览宿主 |
| `src/AvaloniaDevTool.Logging`、`src/AvaloniaDevTool.Remote` | 可选日志适配和远程传输扩展 |
| `samples/AvaloniaDevTool.AutomationSample` | 桌面交互、真实 OS picker 和 UTF-8 文档读写的研发验证目标 |
| `samples/AvaloniaDevTool.Sample`、`samples/AvaloniaDevTool.AotSample` | 绑定/资源等 SDK 回归目标和最小 AOT 消费目标；Sample 默认仅 Debug 启用 Agent |
| `tests/AvaloniaDevTool.Agent.Tests`、`.Cli.Tests`、`.Protocol.Tests` | 目标内服务、真实 stdio MCP 和协议回归 |
| `tests/AvaloniaDevTool.Previewer.Tests`、`.Remote.Tests` | 预览宿主与远程传输回归 |
| `tests/AvaloniaDevTool.PreviewFixture`、`.PreviewFixture.Controls` | 真实 classful 预览应用和独立控件库 |
| `tests/AvaloniaDevTool.AotSmoke` | 独立 Client 对实际发布目标的消费验证 |
| `tests/AvaloniaDevTool.NativeAutomation.Tests`、`.NativeAutomation.Fixture` | Windows native 底层、路径预检、owned modal、OS 输入与进程隔离目标 |

## 验证和文档

[测试说明](docs/testing.md) 区分专项、统一批次、真实进程、像素断言和桌面观察；专项通过不能代替最终统一回归。已有真实控件、WinForms/UIA、CommonItemDialog 和 stdio MCP 的 PNG 证据；native 几何单测只验证坐标与预算，不算运行截图验证。历史批次不挪作本轮结果。

Windows native 仅支持本机交互桌面及可验证 owner 范围，拒绝 remote transport；非 Windows RID 不打包 Windows worker。macOS/Linux 原生后端、IME 候选/组合输入和系统文件/数据 DragDrop 不在本轮范围。完整 CPU 调用栈与内存分配剖析交由其他工具；本项目 Profiler 仅采样式/资源活动。

用户不做原版 DeveloperTools 授权运行等价对照；它不列为当前待完成项，也不是自研 Agent 的运行前置条件。官方 schema/运行语义资料保留为兼容研究。独立 Inspector GUI、XamlMcp 只读后端及专属测试已经退出本分支，历史桌面基线位于 `main/fa1c623`。

- [官方能力与证据](docs/agent-tools/research/official-capabilities.md)、[输入声明差距的历史基线](docs/agent-tools/research/schema-parity.md)、[范围与依赖](docs/agent-tools/architecture/scope-and-dependencies.md)。
- [树与搜索](docs/agent-tools/tree-search.md)、[属性、样式和操作](docs/agent-tools/features/properties.md)、[输入、资源和媒体](docs/agent-tools/features/input-media.md)、[文件预览](docs/agent-tools/features/preview.md)。
- [实现状态](docs/implementation-status.md)、[测试](docs/testing.md)、[架构](docs/architecture.md)、[MCP](docs/mcp.md)。
- [当前 Agent 范围](docs/agent-tools/architecture/current-agent-scope.md)、[自动化能力与研发验证](docs/agent-tools/architecture/automation-and-acceptance.md)。
- [完整 UI 自动化与固定验收清单](docs/agent-tools/architecture/complete-ui-automation.md)、[Agent 交互工具](docs/agent-tools/features/agent-interaction.md)、[连接与兼容契约](docs/agent-tools/features/connection.md)。
- 保留的扩展服务说明包括 [日志](docs/logging.md)、[远程传输](docs/remote-transport.md)、[Native AOT](docs/native-aot.md)、[资源](docs/resources.md)、[资产](docs/assets.md)；这些文档的旧 GUI 操作和历史测试证据均有范围标识。
