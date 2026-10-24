# XamlMcp 在 Avalonia 12.1.4 上的后端兼容验证

验证日期：2026 年 10 月 25 日。环境为 Windows 11 build 26200、.NET SDK 10.0.400、Debug 配置、真实 Skia headless 渲染。固定仓库提交为 `1718f946cb275a71e021de58f4caa552e04feaa8`；远端 `v1.0.0-preview.3` 的解引用提交与它一致。

**结论：XamlMcp 的源码构建链和公开 NuGet 1.0.0-preview.3 二进制链都已实际连接 Avalonia 12.1.4 的独立目标进程。真实 MCP stdio → Server → token 认证 NamedPipe → Agent 路径通过 tree、props、styles、bindings、截图、输入、资产及断开重连验证。应优先评估直接 NuGet 后端适配，而不是为这些基础能力重新移植整套 SDK。**

本项目产品代码、用户 MCP 配置和全局工具列表均未修改。临时安装的公开 Server 使用专属 `--tool-path`，所有验证代码和构建修改都在系统临时克隆内。

## 结果

| 运行 | 实际配置 | 结果 |
| --- | --- | --- |
| 原始基线 | 源码 Server/Agent，原始 TestHost Avalonia 11.3.18，仅跳过可选 Windows Driver 打包载荷构建 | 原有 LiveMcpTests 1/1 通过，测试约 7 秒 |
| 12.1.4 首轮 | 将 TestHost、SampleApp 和测试项目的 Avalonia 参数切为 12.1.4 | 构建被 SampleApp 的 AXAML 编译绑定错误阻塞：AVLN2100，缺少 x:DataType |
| 12.1.4 fixture 调整后 | 仅给 SampleApp 根 Window 显式设 x:CompileBindings=False，保留其 11.x 反射绑定语义 | 原 LiveMcp 主体通过；本地新增重连断言错误地假定 counter 仍为早先文字，导致新增断言失败 |
| 12.1.4 源码最终验证 | 修正新增断言，比较 detach 前后当前 Text 值；Agent 和 MCP 业务源码不变 | LiveMcp 1/1 通过，含新增 detach／拒绝未附加读取／reattach／再次 detach |
| 12.1.4 针对性兼容测试 | Compat、BindingDiagnostics、Style、Screenshot、Resource、NodeRegistry、StreamAgentServer、JSON-RPC Loopback | 97/97 通过，无跳过，约 2 秒 |
| 12.1.4 公开 NuGet 二进制链 | TestHost 使用 XamlMcp.Avalonia 1.0.0-preview.3；stdio 启动本地 tool-path 下公开 XamlMcp.Server 1.0.0-preview.3 | 同一个扩展后的 LiveMcp 1/1 通过，约 7 秒 |

每个 LiveMcp 行是一次包含多个工具的端到端测试方法，不能把这些工具步骤或重复配置计为不同的独立测试。这里没有运行 XamlMcp 全解决方案或所有平台测试。

迁移编译中还出现 4 处 TextBox／AutoCompleteBox Watermark 弃用警告；没有将这些警告隐藏或当作后端故障，也没有为此修改 Agent。

## 实际链路与版本证据

采用上游 [LiveMcpTests](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/tests/XamlMcp.Tests/EndToEnd/LiveMcpTests.cs)，不是在进程内直接调用服务：

```text
MCP SDK McpClient
  → stdio transport
  → XamlMcp.Server
  → discovery + per-launch token authentication
  → current-user NamedPipe
  → 独立 dotnet TestHost.dll --headless
  → XamlMcp.Avalonia Agent
  → Avalonia 12.1.4 + Skia
```

公开包运行中，独立目标进程返回：

- Avalonia 实际载入版本：`12.1.4+2d79ba55fae1b1e71c69f93a668080c9029e6a31`。
- Agent ProductVersion：`1.0.0-preview.3+1718f946cb275a71e021de58f4caa552e04feaa8`。
- ProtocolVersion：`2`；FrameworkVersion：`12.1.4.0`。
- styles、bindings、input-raw、input-routed、assets、screenshot.nodeMap 等 capability 均返回 true，相关工具随后被实际调用。
- TestHost.deps.json 明确解析到 `Avalonia/12.1.4` 以及 `XamlMcp.Avalonia`、`XamlMcp.Agent.Hosting`、`XamlMcp.Protocol` 的 `1.0.0-preview.3` 包。
- 已下载 Agent 包的 nuspec 声明 repository commit 为同一个固定提交，并将 Hosting／Protocol 依赖锁定为同版精确范围。

没有只靠“测试项目参数变成 12”判断目标进程版本；原始 TestHost 原本固定 11.3.18，本轮也将它真实切换并记录了独立进程的载入版本。

## 实际覆盖的功能

LiveMcp 主体中已经执行并断言：

- MCP tools/list 与公共工具面一致，list-apps 不泄露 discovery token 或 pipe name，attach 返回能力。
- 名称 locator、search、tree、ancestors，以及版本化 snapshot/node 引用。
- props 读取、set-prop 写入与回读、styles 和 resources 查询。
- bindings 的健康状态、missing-source、path-error、converter-error 分类，以及 mutation 关联的 failures cursor。
- input 点击、ICommand action、pseudo-class 切换，以及 wait-for。
- screenshot 通过 MCP ImageContentBlock 返回真实 PNG；同次截图的 node map 能继续用于命中和后续操作。
- assets／open-asset 读取独立 TestHost 的嵌入文本资产。
- 本地新增的 detach 后读取被拒绝；再次 attach 后可读取目标仍保留的当前值；最终 detach 成功。

97 项针对性测试补充了兼容层、真实 Skia 截图、绑定错误、资源查找、弱引用／虚拟化节点有效性、认证流和 JSON-RPC 行为。测试源入口分别在 [Compat](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/tests/XamlMcp.Tests/Compat/StyleDiagnosticsCompatTests.cs)、[Values](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/tests/XamlMcp.Tests/Values/BindingDiagnosticsServiceTests.cs)、[Hosting](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/tests/XamlMcp.Tests/Hosting/NodeRegistryTests.cs) 和 [JsonRpc](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/tests/XamlMcp.Tests/JsonRpc/LoopbackTests.cs)。

## 临时修改与复现命令

原始克隆：

```text
C:\Users\10062\AppData\Local\Temp\avdt-open-source-audit-e91f75f3c56240ac9777241b861bbaa6\XamlMcp
```

先读 [Directory.Build.targets](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/Directory.Build.targets) 与 [Server csproj](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Server/XamlMcp.Server.csproj) 后，做了以下局部测试修改：

1. 为 Server 的 BuildDriverPayload AfterBuild 目标增加 `SkipDriverPayload` 条件。该目标构建 FlaUI Windows Driver，与本次 Avalonia 后端验证无关。没有调用 pack、publish 或 MCP 注册命令。
2. TestHost 和 SampleApp 的 Avalonia 引用改为 `$(AvaloniaTestVersion)`，缺省仍为 11.3.18；Agent 自身的 11.3.18 编译下限没有变。
3. SampleApp 根 Window 显式 `x:CompileBindings="False"`。
4. TestHost 启动时输出载入 Avalonia 版本；本地 LiveMcp 扩展只记录版本并补 detach／reattach 断言。
5. 公开包用例中，将 TestHost 的 Agent ProjectReference 改为 `PackageReference XamlMcp.Avalonia 1.0.0-preview.3`；MCP test client 的 Server 路径改为允许从本地验证环境变量指定公开工具 DLL。

源码用例命令核心：

```powershell
dotnet test tests/XamlMcp.Tests/XamlMcp.Tests.csproj `
  -p:AvaloniaTestVersion=12.1.4 -p:SkipDriverPayload=true `
  --filter FullyQualifiedName~XamlMcp.Tests.EndToEnd.LiveMcpTests
```

针对性筛选：

```text
FullyQualifiedName~XamlMcp.Tests.Compat
|FullyQualifiedName~XamlMcp.Tests.Values.BindingDiagnosticsServiceTests
|FullyQualifiedName~XamlMcp.Tests.Values.StyleServiceTests
|FullyQualifiedName~XamlMcp.Tests.Values.ScreenshotServiceTests
|FullyQualifiedName~XamlMcp.Tests.Values.ResourceServiceTests
|FullyQualifiedName~XamlMcp.Tests.Hosting.NodeRegistryTests
|FullyQualifiedName~XamlMcp.Tests.Hosting.StreamAgentServerTests
|FullyQualifiedName~XamlMcp.Tests.JsonRpc.LoopbackTests
```

公开包用例的附加准备：

```powershell
dotnet tool install --tool-path published-tools XamlMcp.Server --version 1.0.0-preview.3
```

随后由本地 `Run-Validation.ps1` 设置专属的公开 Server DLL 路径、运行同一个 MCP 测试。具体 argv、环境验证参数、stdout/stderr、TRX 和进程清单均保存在 `validation-records`，不是以当前全局 PATH 中的其他 xamlmcp 代替。

## 证据位置与进程清理

系统临时克隆内的记录：

| 路径 | 内容 |
| --- | --- |
| validation-records/baseline-11.3.18 | 原基线的命令、日志、TRX、PID |
| validation-records/matrix-12.1.4-live | 首次 fixture 构建错误 |
| validation-records/matrix-12.1.4-live-adjusted | 首次本地扩展断言错误和实际版本 |
| validation-records/matrix-12.1.4-live-final | 源码最终 LiveMcp 通过、版本与 capability |
| validation-records/matrix-12.1.4-compat | 97 项兼容测试的 TRX |
| validation-records/nuget-12.1.4-live | 公开包最终通过、实际 package 依赖和目标版本 |
| validation-records/final-fixture-build.patch | 最终 fixture／构建差异 |
| validation-records/final-validation-extension.patch | 版本记录和断开重连的本地验证扩展 |
| validation-records/cleanup-check.json | 完成后匹配本克隆路径的自启 dotnet/TestHost/Server 进程数为 0 |

每次 LiveMcp 包括 test runner、vstest、testhost、独立目标和 Server 共五个记录进程。外层 wrapper 在异常时清理自己启动的进程树，并仅对命令行匹配该临时克隆的目标／Server 做兜底清理。没有结束用户其他应用。临时工具目录保留作为可复查产物，没有全局安装。

## 适配建议

**优先直接使用一致版本的 NuGet 后端，再做接口适配。** 本轮公开二进制已经通过核心链路，尚无证据支持为了同一套基础读取功能先复制整个上游 SDK。

建议把现有 Inspector 的 Client 依赖抽为 `IInspectionBackend`，至少分开：

| 接口边界 | XamlMcp 映射与注意点 |
| --- | --- |
| 发现、连接、能力 | 复用 AppCatalog／AgentSession／AgentClient 及 capability 描述；凭据只在后端处理，不放入 UI 的应用列表 |
| 对象引用 | 原生是 `NodeRef(snapshotId,nodeId)`，不能直接丢失 snapshotId 后当成当前项目 long objectId；适配层需要会话与快照作用域映射 |
| 树、属性、样式、绑定 | 对结构化 DTO 做映射；保留 degraded/gaps，不把 binding mode 或 raw exception 的缺口填成推测值 |
| 更新观察 | XamlMcp 具有 mutation digest／observed snapshot／wait-for；不能直接当作当前 Agent 的 properties.changed 推送，需要明确轮询／订阅策略 |
| 截图和资产 | 支持 MCP 图片块及同次 capture node map；映射图像数据时保留该截图的 snapshot 引用 |
| 错误和取消 | 保留 stale-snapshot、stale-node、app-unresponsive、outcome-indeterminate 等差异；超时后不能一律声称没有修改 |
| 属性编辑语义 | 上游 PropertyService 使用 SetValue／ClearValue，本项目使用可释放 Animation 诊断覆盖；两者不等价。若要求清除后恢复原本本地绑定，应保留本项目覆盖服务或专门扩展上游，而非静默替换 |
| 当前独有扩展 | 可恢复资源字典编辑、日志事件／断点／Meter 面板及 VisualGeometry 等应单列 capability，不能因为换后端而继续展示不可用动作 |

最值得直接依赖或研究的模块是 [Agent.Hosting](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Agent.Hosting/DesktopAgentServer.cs)、[Avalonia Compat](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Avalonia/Compat/AvaloniaCompat.cs)、[NodeRegistry](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Avalonia/Nodes/NodeRegistry.cs)、[BindingDiagnosticsService](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Avalonia/Values/BindingDiagnosticsService.cs) 和 [LiveMcp 测试](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/tests/XamlMcp.Tests/EndToEnd/LiveMcpTests.cs)。只有当需要保持当前专有编辑语义或增加上游不存在的服务时，再评估小范围 source port／扩展。

## 许可证与验证限制

源仓库和公开包均声明 Apache-2.0，nuspec 归属为 Rahul TR、2026。使用包应保留其许可证与归属信息；若复制修改源码，应保留原有告知、显著记录修改，并检查所用版本及依赖的 NOTICE。此次没有把第三方源码加入本项目。许可证原文见 [LICENSE](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/LICENSE)。

已验证 Windows 上的 Debug／Skia headless 场景，不等同真实桌面所有输入与 GPU 渲染行为。未验证 AOT／trim、macOS／Linux、Android／MAUI、WPF／WinUI、可选 OS Driver，也未执行全部上游测试、发布或推送。公开包是 preview.3；本次兼容结论只绑定该版本、固定提交和上述环境。

