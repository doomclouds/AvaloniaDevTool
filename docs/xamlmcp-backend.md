# XamlMcp 开源后端

项目现有 Native Agent 保留完整诊断与可撤销编辑能力。新增 `AvaloniaDevTool.Backends.XamlMcp` 使用官方 MCP C# SDK 2.3.0 启动公开 `XamlMcp.Server 1.0.0-preview.3`，连接只接入上游 NuGet Agent 的独立目标。适配器不实现或复制上游认证管道协议。

## 边界

| 能力 | Native | XamlMcp 适配器 |
| --- | --- | --- |
| 共同只读接口 | IInspectionBackend | IInspectionBackend |
| BackendId | native | xamlmcp |
| 对象身份 | 会话内稳定弱对象 ID | 每次根刷新重建本地句柄；保留上游 snapshotId/nodeId |
| 树读取 | 应用侧请求子项 | depth 24、默认 2048 节点的有界预取；展开读取本地缓存 |
| 截断 | Native 具体请求规则 | LastNotice 说明截断，截断分支报 snapshot_truncated，不伪装为空叶子 |
| 更新 | 有 properties/tree 推送 | HasPushUpdates=false，显式刷新 |
| 属性编辑 | 可撤销诊断覆盖 | CanEdit=false、CanClear=false，不静默转换为上游 SetValue/ClearValue |
| 样式／绑定 | Native 版本适配 | 映射上游结构，保留 provenance 和 gaps；无匹配证据的 setter 显示未知 |
| 祖先路径 | Visual／Logical | Visual 调上游 ancestors；Logical 仅用当前 logical 快照父关系 |
| PNG | Native 截图 | 保留上游原始 PNG 字节，不转 JPEG、不缩放 |
| 高级功能 | 资源编辑、活动、指标、断点、3D 等 | 共同接口之外保留 Native，未映射能力不返回假成功 |

`IInspectionBackend` 位于 Client，只有已验证的共同只读方法。Native 的高级方法仍由 DiagnosticsClient 提供。

## 可复现依赖

Backend 项目固定引用 MCP SDK 2.3.0、XamlMcp.Protocol 1.0.0-preview.3，并以 PackageDownload 精确下载 XamlMcp.Server 1.0.0-preview.3。构建／发布时将公开工具载荷复制到：

```text
<output>/Upstream/XamlMcpServer/XamlMcp.Server.dll
```

`XamlMcpServerLocation.BundledAssemblyPath` 返回该位置。没有硬编码某台机器的 NuGet 缓存或系统临时克隆目录，没有安装全局工具，也不写用户 MCP 配置。首次 restore 需要能访问所配置的 NuGet feed；运行阶段不自动下载安装。

调用方可以明确配置自己的公开 Server 路径：

```csharp
var backend = new XamlMcpBackend(new XamlMcpBackendOptions
{
    ServerCommand = "dotnet",
    ServerArguments = [absoluteServerAssemblyPath],
    DiscoveryDirectory = optionalIsolatedDiscoveryDirectory
});
```

ServerCommand 与每项参数分别交给 SDK stdio transport，不拼接 shell 命令。默认使用受控系统环境，并可传入独立 discovery 目录；SDK 负责进程标准流和关闭。

## 目标与连接

独立样例不引用本项目 Agent：

```powershell
dotnet run --project samples/AvaloniaDevTool.XamlMcpTarget
```

Debug 接入上游公开 NuGet Agent，窗口和 stdout 显示 PID。Release 需要显式 `--enable-inspection`。headless 自动化使用真实 Skia：

```text
--headless --enable-inspection --discovery-dir <owned-directory> --ready-file <new-file>
```

ready 文件只包含 PID、endpoint 与版本，不包含 token 或 pipe name。

```csharp
var applications = await backend.ListApplicationsAsync();
await backend.ConnectAsync("pid:12345");
// 或使用 list-apps 返回的 opaque instanceId:
await backend.ConnectAsync("instance:" + instanceId);
```

目录只暴露清洗后的 Avalonia 应用信息。上游 Server 自己读取 discovery 和认证，适配器只调用 list-apps／attach。连接后 Session.ProtocolVersion 保留上游版本 2，不伪装成本机协议 1。

## 快照与缓存

每个 GetRootsAsync 会请求新的上游 raw tree、生成新本地 long 句柄并使先前句柄失效。缓存记录完整 snapshotId 和 nodeId，只有两者同时匹配才可向上游查询。重复的 nodeId 出现在不同 snapshot 时不会混用。句柄计数在适配器实例的重连之间也不重置。

GUI 应尊重 HasStableObjectIds=false：刷新根后清空旧选中状态。旧句柄报 snapshot_expired；树类型不一致报 tree_scope_mismatch；有未物化子项的分支报 snapshot_truncated。缓存上限可通过 MaxSnapshotNodes 配置为 1..10000，SnapshotDepth 为 0..24。

这不是目标端懒加载。对于超过边界的大树，应在界面显示 LastNotice，调整快照限制并重新选择。Logical 祖先只从当前完整已物化路径得出，适配器没有把上游仅有的 visual ancestors 改名为 logical。

## 诊断映射

XamlMcp 仅提供短类型名或 owner 名时保持其原值，不补造 CLR 完整类型。属性 ValueDto 的 text／summary 保留为显示文本，截断值会标明。

StyleSnapshot.Adapter 明确来自 XamlMcp 及公开包版本。setter owner／有效值匹配不可得时，DeclaringType 保持 Unknown、MatchesEffectiveValue 为 null。上游没有有效 binding priority 时显示 Unknown；错误状态不能确认 IsRunning 时为 null。binding mode、原始异常及其他缺口保留在 Limitations，不能用推测填充。

截图保持上游 PNG bytes；其含义是控件软件重绘，GPU／原生互操作内容不保证捕获。此接口关闭额外 node map，避免截图意外生成一个新树快照。

## 生命周期与错误

操作串行，默认 15 秒超时。Disconnect 会先 detach，再释放 SDK MCP client 和该实例启动的 Server。服务进程异常结束会更新 ConnectionChanged；目标应用关闭通常在下一次读请求时发现，因为此后端没有推送／心跳。

取消或超时后要求重连以重新确认状态。stale-node、stale-snapshot、agent-disconnected、app-unresponsive、outcome-indeterminate 等上游错误保留区别，不把每种失败都解释成空结果。

## 验证

```powershell
dotnet test tests/AvaloniaDevTool.Backends.XamlMcp.Tests/AvaloniaDevTool.Backends.XamlMcp.Tests.csproj
```

新增 6 项测试：4 项缓存语义测试；2 项真实进程集成测试。后者使用 PackageDownload 的公开 Server、SDK 2.3、独立 NuGet Agent／Avalonia 12.1.4 headless 目标，经过 adapter 验证树、props、健康／错误 binding、styles、PNG、Visual/Logical 祖先、pid／instance 连接、旧快照失效、只读／无 push 标记、截断分支错误、detach 与 Server 退出。

测试仅清理自身启动的目标与 SDK Server。此前直接针对上游公开二进制的兼容实证见 [兼容验证](research/xamlmcp-compatibility-validation.md)。本次没有将上游实现源码复制到本项目；只有原始许可证与归属文本随构建分发。

