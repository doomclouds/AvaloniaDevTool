# AvaloniaDevTool 0.1.0

AvaloniaDevTool 为 Avalonia 应用提供控件检查、临时调试修改、界面交互、Windows 原生窗口与文件弹窗、PNG、原始诊断采集和 XAML 文件预览。目标应用引用 Agent SDK；调用方负责业务流程、业务判断和测试断言。

**一个 MCP 配置，默认本机，指定 IP 切换执行电脑。** `mcp` 是 Codex 启动的 stdio 网关；`serve` 是被测电脑运行的 HTTP 执行 Console。两者复用同一套能力和执行工具。普通终端不提供 `tree/click` 等直接业务子命令。

## 安装与单 exe 分发

独立的 [Packaging 工程](src/Packaging/AvaloniaDevTool.Packaging/AvaloniaDevTool.Packaging.csproj) 将控制台、Previewer、Windows worker 的自包含发布资产嵌入 `AvaloniaDevTool.exe`。运行时校验、缓存解包并按需启动隔离子进程，不要求用户分别部署内部 DLL。

```powershell
pwsh -File src/Packaging/AvaloniaDevTool.Packaging/Build-Bundle.ps1 -Version 0.1.0
pwsh -File tools/Install-AvaloniaDevTool.ps1 -PackagePath .\artifacts\package\win-x64\AvaloniaDevTool.exe -Version 0.1.0
```

安装目录为 `%USERPROFILE%\.local\share\avalonia-devtool\versions\<版本>`；稳定入口为 `%USERPROFILE%\.local\bin\AvaloniaDevTool.exe`。脚本更新用户级 PATH 和 `AVALONIA_DEVTOOL_HOME`，保留旧版本，校验失败或入口锁定时不会切换到损坏的命令。已有终端或 Codex 可能需要重新启动以读取新环境。

完整命令、校验和回滚行为见 [打包与安装](docs/packaging-and-installation.md)。单 exe 指工具分发文件；运行时仍会释放内部资产并启动多个进程。工具运行自包含，用户工程评估和构建仍需要 .NET SDK。

## Codex 只配置一次

将路径换成实际用户目录：

```toml
[mcp_servers.avalonia]
command = 'C:\Users\a\.local\bin\AvaloniaDevTool.exe'
args = ["mcp"]
startup_timeout_sec = 120
```

0.1.0 只提供一套当前能力合同；网关在统一执行工具目录上增加 `select-machine`。选择机器不改变外部工具目录：

```text
select-machine {}                           默认／切回本机
select-machine {"ip":"192.168.1.50"}        选择远端 Console
attach-to-app {}                            发现所选电脑的应用
attach-to-app {"id":<本次发现返回的id>}       附加应用
```

选择机器后重新发现和附加应用。旧节点、弹窗、采集等句柄不能跨执行会话使用；远端失联不回退本机，也不自动重发可能已经执行的动作。

## 被测电脑启动 Console

在目标用户的交互桌面中运行：

```cmd
AvaloniaDevTool.exe serve
```

`serve` 默认监听 IPv4 接口的 `7443/mcp`；需要指定网卡时使用 `--listen IP`。当前为无 Token 内网模式。目标应用、Console 和原生 worker 应处于同一可交互用户桌面。详见 [本地与远端使用](docs/mcp.md) 和 [双机执行关系](docs/architecture/lan-mcp-design.md)。

本机也能验证 HTTP 路径：启动一个 Console，调用 `select-machine {"ip":"127.0.0.1"}`。显式提供回环 IP 仍使用 HttpExecutor；本地和 HTTP 测试顺序运行以避免争用目标。

## 能力域

| 能力域 | 主要能力 |
| --- | --- |
| 目标与会话 | 发现、附加、切换、断开及控制范围 |
| 结构与对象检查 | 树、搜索、祖先、分页、对象下钻 |
| 属性与样式 | 属性、绑定、样式、伪类、临时覆盖和恢复 |
| 资源与资产 | 运行资源查询与编辑、嵌入资产查看与导出 |
| 框架交互 | 文本、键鼠、拖动、选择、控件动作和条件等待 |
| 原生自动化 | 原生窗口、UIA、OS 输入和异步动作状态 |
| 文件选择对话 | 路径、过滤器、确认、取消与覆盖提示 |
| 图像与视觉诊断 | managed/native PNG、布局、合成、覆盖层与弹层冻结 |
| 运行诊断 | 日志、事件、Metrics、Profiler、断点与原始采集 |
| XAML 预览 | 项目上下文、构建、文件预览、重载与失败恢复 |

图像随 MCP 结果返回，不以远端磁盘路径代替。文件路径始终属于当前执行电脑；文件弹窗关闭、输入投递或原生动作回执不自动证明应用业务成功。

## 应用接入

引用 [Agent SDK](src/SDK/AvaloniaDevTool.Agent/AvaloniaDevTool.Agent.csproj)，在 Avalonia 初始化完成后调用并保存实例：

```csharp
_diagnostics = DiagnosticsAgent.Attach(this, new DiagnosticsAgentOptions
{
    ApplicationName = "My Avalonia App"
});
```

在应用退出时释放。外部附加的应用不会被 detach 终止；工具只清理自己的临时修改、监听、自建预览与 worker。

`attach-to-file` 用于 XAML 设计预览，按需启动内部 Previewer。仅操作已经运行的应用时无需启动预览器。预览目标继续复用普通检查、交互和采集能力，见 [预览说明](docs/agent-tools/features/preview.md)。

## 源码与验证

解决方案统一按 `Contracts / Domain / Application / Infrastructure / SDK / Hosts / Extensions / Packaging` 分层。能力对象拥有自己的状态与生命周期，调用方直接使用当前能力 API；未发版的旧 SDK 壳、类型转发、平铺 API 和 profile 已移除。JSON 源生成所需的 partial 保留。

```powershell
dotnet build AvaloniaDevTool.slnx -m:1
```

- [架构与类图](docs/architecture.md)、[项目依赖清单](docs/architecture/project-inventory.md)、[重构记录](docs/architecture/refactoring-blueprint.md)。
- [MCP 使用](docs/mcp.md)、[工具扩展](docs/architecture/extending-tools.md)、[运行契约](docs/architecture/runtime-contracts.md)。
- [测试记录](docs/testing.md)、[实现状态](docs/implementation-status.md)、[当前能力边界](docs/agent-tools/architecture/current-agent-scope.md)。

Windows native 通过执行电脑上的本地 worker 完成。可选 Agent WebSocket 接入是另一条内部传输，其 native 限制不影响统一网关的 HTTP 执行链。

当前不提供业务脚本语言、断言引擎、视觉比对、性能阈值或发布门禁。脚本可作为标准 MCP 客户端编排工具，结果和证据解释由调用方负责。
