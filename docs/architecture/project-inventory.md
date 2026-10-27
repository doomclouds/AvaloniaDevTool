# 0.1.0 项目清单与直接依赖

解决方案共29项目：15个生产项目、3个消费样例、11个测试/支撑项目。下表从当前csproj提取直接编译引用；不包含运行资产关系。所有项目和tools/build/docs/根配置均已加入.slnx。

| 项目 | 层 | 直接编译项目引用 |
| --- | --- | --- |
| [Application](../../src/Application/AvaloniaDevTool.Application/AvaloniaDevTool.Application.csproj) | Application | Domain、Contracts |
| [Contracts](../../src/Contracts/AvaloniaDevTool.Contracts/AvaloniaDevTool.Contracts.csproj) | Contracts | 无 |
| [Domain](../../src/Domain/AvaloniaDevTool.Domain/AvaloniaDevTool.Domain.csproj) | Domain | Contracts |
| [Logging](../../src/Extensions/AvaloniaDevTool.Logging/AvaloniaDevTool.Logging.csproj) | Extensions | Agent |
| [Remote](../../src/Extensions/AvaloniaDevTool.Remote/AvaloniaDevTool.Remote.csproj) | Extensions | Agent |
| [Cli](../../src/Hosts/AvaloniaDevTool.Cli/AvaloniaDevTool.Cli.csproj) | Hosts | Infrastructure.Mcp、Infrastructure.Execution |
| [NativeAutomation.Worker](../../src/Hosts/AvaloniaDevTool.NativeAutomation.Worker/AvaloniaDevTool.NativeAutomation.Worker.csproj) | Hosts | Infrastructure.Windows |
| [Previewer](../../src/Hosts/AvaloniaDevTool.Previewer/AvaloniaDevTool.Previewer.csproj) | Hosts | Agent |
| [Infrastructure.Avalonia](../../src/Infrastructure/AvaloniaDevTool.Infrastructure.Avalonia/AvaloniaDevTool.Infrastructure.Avalonia.csproj) | Infrastructure | Infrastructure.Rpc |
| [Infrastructure.Execution](../../src/Infrastructure/AvaloniaDevTool.Infrastructure.Execution/AvaloniaDevTool.Infrastructure.Execution.csproj) | Infrastructure | Infrastructure.Rpc、Domain |
| [Infrastructure.Mcp](../../src/Infrastructure/AvaloniaDevTool.Infrastructure.Mcp/AvaloniaDevTool.Infrastructure.Mcp.csproj) | Infrastructure | Infrastructure.Execution、Application |
| [Infrastructure.Rpc](../../src/Infrastructure/AvaloniaDevTool.Infrastructure.Rpc/AvaloniaDevTool.Infrastructure.Rpc.csproj) | Infrastructure | Contracts |
| [Infrastructure.Windows](../../src/Infrastructure/AvaloniaDevTool.Infrastructure.Windows/AvaloniaDevTool.Infrastructure.Windows.csproj) | Infrastructure | Contracts |
| [Packaging](../../src/Packaging/AvaloniaDevTool.Packaging/AvaloniaDevTool.Packaging.csproj) | Packaging | 无 |
| [Agent](../../src/SDK/AvaloniaDevTool.Agent/AvaloniaDevTool.Agent.csproj) | SDK | Infrastructure.Avalonia |
## 依赖约束

Contracts不依赖框架；Domain与Application不依赖MCP/执行实现。RPC使用真实Infrastructure.Rpc程序集名。Agent SDK只保留真实接入入口；未发布的Client/NativeAutomation SDK壳和全部类型转发已删除，消费者直接引用实现或中立契约。

控制台不编译引用Avalonia/UIA。Worker Host仅Program入口，FlaUI/Win32在Infrastructure.Windows；Previewer在独立设计进程挂载Agent。控制台的子进程引用使用ReferenceOutputAssembly=false，不能混作普通编译引用。

Packaging工程没有生产项目编译引用；脚本独立发布各角色的完整自包含目录、生成清单并嵌入bootstrap，产生唯一EXE。单文件关系是分发关系，不把平台和进程边界合并。

## 样例和测试

Sample、AutomationSample和AotSample保留。Agent/Cli/Protocol/Previewer/Remote/NativeAutomation/Packaging共7个测试框架项目；PreviewFixture、PreviewFixture.Controls、NativeAutomation.Fixture、AotSmoke是4个测试或消费支撑项目，不进入普通分发。

所有ProjectReference和Compile/Link路径随物理迁移更新。历史纯兼容profile/schema断言随对应功能删除，实际能力、像素、恢复、会话、原生输入与生命周期测试继续保留。当前验证见[测试说明](../testing.md)。

[架构与类图](../architecture.md) · [迁移记录](refactoring-blueprint.md) · [打包安装](../packaging-and-installation.md)
