# Native HTTP/WebSocket 远程传输

> 范围说明：本页描述可选Agent WebSocket SDK扩展，不是用户MCP的第二条远端入口。0.1.0用户使用stdio网关、select-machine和远端Console；没有profile或attach-to-remote工具。文中Inspector界面与旧测试批次仅作历史资料，当前使用与验证见[MCP](mcp.md)和[测试说明](testing.md)。

当前 Codex 的完整双机接入使用 [Cli 的内网 HTTP MCP](architecture/lan-mcp-design.md)，端点为 `/mcp`，按当前要求无 Token。本页 `/diagnostics` 是可选的内部 RPC WebSocket，原有 Token/TLS 规则保持。扩展 MCP 的环境凭据入口见 [MCP](mcp.md)；旧 Inspector 遮罩输入和启动步骤可从 `fa1c623:docs/remote-inspector.md` 查看，当前分支不再提供该界面。

`AvaloniaDevTool.Remote` 为现有 `DiagnosticsAgent` 增加可选的 Kestrel HTTP/WebSocket 入口。网络连接继续使用本项目的 Native RPC、DTO、弱引用对象 ID、属性临时覆盖和推送；它不是 CDP、MCP 或官方 DeveloperTools CoreRPC 的兼容实现。

## 启动与连接

仅需要本机管道时，无须引用 Remote 项目，也不会在目标应用中引入 ASP.NET Core。需要网络入口的 CLR 桌面应用引用 `AvaloniaDevTool.Remote` 后，在 UI 线程创建 Agent：

```csharp
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Remote;

// Call Attach on the Avalonia UI thread. Keep both instances for the application lifetime.
await using var agent = DiagnosticsAgent.Attach(application);
var token = DiagnosticsRemoteServer.GenerateBearerToken();
await using var server = await DiagnosticsRemoteServer.StartAsync(agent,
    new DiagnosticsRemoteServerOptions { BearerToken = token });

// Transfer Endpoint and token separately through an application-controlled private channel.
// Endpoint contains only ws://127.0.0.1:<ephemeral-port>/diagnostics.
await using var client = new DiagnosticsClient();
await client.ConnectRemoteAsync(server.Endpoint, token, cancellationToken);
var roots = await client.GetRootsAsync(AvaloniaDevTool.Protocol.TreeKind.Visual, cancellationToken);
```

默认监听 `IPAddress.Loopback`，`Port = 0` 由操作系统分配可用端口，避免隐式监听 LAN。`Endpoint` 不包含 token。`GenerateBearerToken()` 使用 32 字节加密随机数，生成无填充的 base64url 字符串；调用方负责私密地交付和保存 token。不要把 token 拼接进 URL、输出到应用日志或保存到源代码。

`DiagnosticsClient.ConnectRemoteAsync(Uri, string, CancellationToken)` 使用 `Authorization: Bearer ...`，把建立 WebSocket 和 Native RPC 版本握手一起限制在 5 秒内，并支持调用方取消。URI 只接受 `ws`/`wss`，拒绝 userinfo、query 和 fragment；明文 `ws` 只接受字面量 loopback IP 地址并禁用代理。`localhost` 不属于明文例外，因为名称解析结果可以变化。客户端保留平台 TLS 证书验证，不自动降级，也没有跳过证书验证的开关。

## 显式 TLS 与远程部署

```csharp
await using var server = await DiagnosticsRemoteServer.StartAsync(agent,
    new DiagnosticsRemoteServerOptions
    {
        BearerToken = token,
        ListenAddress = localInterfaceAddress,
        Port = configuredPort,
        ServerCertificate = serverCertificate
    }, cancellationToken);
// Connect with a wss URI whose host matches the certificate and whose chain is trusted by the client.
```

非 loopback 绑定必须显式提供 `X509Certificate2`，否则在创建监听器之前失败。证书由调用方持有并在服务器结束后释放。Kestrel 配置源被清空，环境变量、appsettings、命令行和 launch profile 不会额外扩大绑定地址；服务器自身不注册日志 provider。此实现不会修改防火墙、系统证书信任、路由或 VPN 配置。

认证在 WebSocket upgrade 和 AgentSession 创建之前完成，服务器对 bearer token 的 SHA-256 摘要做固定时间比较。错误响应没有 token、目标对象或诊断数据。请求只接受 `/diagnostics`，含 query 的请求被拒绝。没有 HTTP discovery、匿名状态页、token URL 参数、cookie 登录或自动远程发现。

请求含 `Origin` 时，默认拒绝。`AllowedOrigins` 可显式指定精确 HTTP(S) authority，未列出的 Origin 返回 403；无 Origin 的 .NET 原生客户端正常连接。Origin 只作为额外限制，不能代替 bearer 认证。浏览器 WebSocket API 无法设置此 Authorization header，因此当前入口没有提供浏览器登录桥；不能据此声称 WASM Agent 或浏览器 Inspector 已实现。

## 会话与资源边界

同一个 Agent 的 NamedPipe 与所有网络入口共用一个会话门。网络入口先保留会话再 upgrade，已有会话时返回 HTTP 409；竞争的管道连接关闭后不会替换或释放已有网络会话。NamedPipe 的默认行为仍为 current-user-only、本地、单活动客户端。

`DiagnosticsAgent.ServeConnectionAsync(Stream, CancellationToken)` 接管可读写流。factory 重载在调用 factory 前取得同一会话门，允许宿主在拒绝繁忙连接后保留 HTTP 响应能力。会话结束时在 UI 线程释放原有 AgentSession，包括属性／资源覆盖、订阅、拾取和服务计时器；该连接清理完毕之前不会允许新会话进入。服务器释放会取消其活动网络连接并关闭监听器；Agent 释放也会结束活动连接。

`RpcWebSocketStream` 按需读取二进制片段，消息边界不改变 Native RPC 记录边界。现有记录是 4 字节小端长度加 UTF-8 JSON，JSON 负载上限 4 MiB；WebSocket 消息上限为 4 MiB + 4 字节，以允许单消息携带完整记录。客户端和服务器接受分片二进制消息，拒绝 Text 和无效／超长记录。读取不累积整个 WebSocket 消息，写入直接 await 发送，没有额外 WebSocket 发送队列或压缩。

RPC 读取循环与串行请求处理器分离，因此等待请求期间仍能接收另一方向的响应、推送和断连。入站待处理请求队列最多 64 条，出站发送最多接受 128 个同时进行的写入，只有当前写入会创建最终 UTF-8 帧缓冲；超过限制明确失败。现有调用仍有 15 秒请求超时。收到正常 Close、断开的 socket 或本地主动取消后，挂起请求失败并取消正在执行的处理器，不等待业务请求自己超时。

## 已验证与限制

2026-10-25，Windows、.NET 10、Avalonia 12.1.4 下的测试使用真实 loopback Kestrel 与 `ClientWebSocket`，不是内存 HTTP TestServer。测试覆盖 Native typed Client 的控件树／属性读取、临时编辑、推送、断连恢复与新会话重连；未认证、错误 token、不允许的 Origin、NamedPipe 与网络会话竞争；分片二进制、Text／错误长度帧、服务器释放与监听器关闭；调用方取消和未受信任 TLS 证书拒绝。协议测试另覆盖分片消息大小上限、Close、双向嵌套请求和客户端断连时取消未完成处理器。Agent 原有管道回归测试保留。

这些证据仅证明当前机器的 loopback CLR 路径。LAN／VPN 跨机器、WSL2 网络、Linux／macOS、可信 TLS 跨主机连接、代理和实际断网恢复尚未实测。Remote host 依赖 ASP.NET Core，不能在浏览器进程中启动；目标平台的 Avalonia 渲染、输入、composition、反射和 profiler capability 不因增加 WebSocket 而自动补齐，也不应将某个平台不支持的能力显示为可用。

## 设计依据

本轮优先参考已有公开实现审计：[CDP／XamlMcp 审计](research/open-source-agent-tools-audit.md)。CDP 的 HTTP/WebSocket 和 XamlMcp 的 current-user 管道认证提供架构参照，但它们的协议和对象身份不同，本实现没有复制商业 transport 或声称协议互通，也没有复制第三方源码。

宿主生命周期、upgrade 前认证、Origin 和二进制消息限制依据 [.NET 10 WebSocket 文档](https://learn.microsoft.com/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0) 与 [Kestrel WebSocket 安全说明](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/security-considerations?view=aspnetcore-10.0#websocket-security)。Context7 已解析官方 `/dotnet/aspnetcore.docs`，查询服务本轮返回 fetch failed，随后通过 Microsoft Learn 工具读取官方文档。
