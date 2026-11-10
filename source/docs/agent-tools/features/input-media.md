# 输入、资源与媒体

目标是 [官方 2.2.3 声明](../research/official-capabilities.md)中的 INPUT01–04、RES01–03、ASSET01、OPEN01 和 SHOT01。官方归档只有 `initialize` / `tools/list`，没有原版 `tools/call` 输出；下述返回类型和限制是本项目明确实现的行为，不能表述为原版字节级协议。

## 已实现路径

| 工具 | 实际路径与返回 |
| --- | --- |
| `input(nodeId, inputEvent, arg=null)` | 仅公开 `Click / KeyDown / KeyUp / Text`；映射目标端 `InputInspectionService`，TextInput、KeyDown、KeyUp 分别发对应 Avalonia 路由事件，Click 发 move/press/release，让控件自身触发业务行为。Text/Key 输入先实际聚焦。 |
| `resources(nodeId=0, onlySelf=false)` | 0/省略查询 Application 资源；指定节点按节点、父链、Application 顺序枚举真实字典、样式、合并与主题子字典，同时返回有来源的条目值。`onlySelf=true` 在目标端停止父链并跳过 Application，保留该节点拥有的字典、样式及子字典。不会按 provider 的显示名称猜 scope。 |
| `assets()` | 从目标进程已经加载且带 `!AvaloniaResources` 的程序集枚举 `avares://` URL，包含图片、字体、文本及其他二进制。最多 2000 项。 |
| `open-asset(assetUrl)` | 复用完整导出协议，以 64 KiB RPC 块读取并校验完整长度与 SHA-256；返回元信息和 MCP `EmbeddedResourceBlock` / `BlobResourceContents`，保留原始字节。字体、其他二进制及图片均是带准确 MIME 的 blob，不把字体当 MCP image。 |
| `screenshot(nodeId)` | 目标端 RenderTargetBitmap 抓取指定控件，返回原生 MCP PNG image；使用实际控件尺寸，拒绝空边界或超限截图。 |

所有调用使用会话串行 lease；nodeId 请求在 lease 内解码，资源返回的字典、键和值对象身份在同一 lease 内编码。切换连接后不能静默复用旧节点。

## 明确限制与错误

- 输入是 Avalonia framework 路由事件，不注入操作系统或 IME。按键 `arg` 使用命名 Avalonia `Key`（如 `Back`、`Enter`、`A`），当前不解释 `Ctrl+A`。Text 最多 2048 UTF-16 单元；空文字、非法键、不可聚焦、只读文本框、禁用、隐藏、脱离树和无效布局均报明确错误。
- 资源树最多 128 provider，条目最多 1000；值预览最长 1024 UTF-16 单元、键最长 256，截断显式标识。输出保留同名资源及来源，没有伪造合并后的唯一 effective 值；主题字典枚举不等于每个主题都当前生效。自定义资源 provider 无法通过公开 API 枚举时保留类型和 limitation。
- 完整 MCP 嵌入资产响应上限是 **16 MiB 原始字节**。超限明确报 `asset_download_limit`，不返回成功的部分文件，也不把旧的 1 MiB `AssetPreview` 当完整下载。底层导出仍有 100 MiB/session 双 reader/两分钟空闲上限；读写失败或取消后关闭 token。
- 资产只接受无 query/fragment/userinfo/自定义端口的 `avares://`，程序集必须已加载且实际包含目标资源；拒绝 `file://` 和未加载程序集，不能任意读磁盘。
- 截图最大边 2048、总像素 2×1024×1024、PNG 编码体 1 MiB。超限报错，不静默缩放。没有把 nodeId=0 擅自解释为窗口截图。

## 验证

`OfficialInputMediaMcpTests` 通过真实子进程 stdio MCP + headless Avalonia 目标验证：四输入分别生效，Back 的 down 改字/对应 up 路由独立计数，Click 触发 Button.Click；资源 Application/0/节点父链、自身、遮蔽同名、合并和主题；大于 1 MiB 文本、PNG、真实 Inter TTF 和任意二进制的完整长度/原始字节/hash/内容块类型；红蓝控件截图的像素、尺寸、空边界和上限。

`OfficialResourceScopeTests` 直接验证目标端 onlySelf 是遍历边界：节点故意命名为 `Application.Resources` 仍正确保留自身资源并排除真实 Application 和父资源；节点合并/主题/样式资源仍保留。

2026-10-25 最后验证：真实 stdio 专项 **5/5 通过**（含 JSON 数字 inputEvent=0 被拒绝且按钮未再次点击、1102 资源返回 1000 并明确截断）；目标端 scope 专项 **1/1**，相关输入/资源/资产已有回归 **34/34**。构建零警告零错误。

```powershell
dotnet test tests/AvaloniaDevTool.Cli.Tests/AvaloniaDevTool.Cli.Tests.csproj --artifacts-path "$env:TEMP/avdt-official-inputmedia-verify" --filter FullyQualifiedName~OfficialInputMediaMcpTests --no-restore --logger 'trx;LogFileName=official-input-media.trx' --results-directory "$env:TEMP/avdt-official-inputmedia-results" --verbosity minimal
```

TRX 在 `%TEMP%/avdt-official-inputmedia-results/official-input-media.trx`。16 MiB 资产响应上限是明确实现策略，本轮没有加入超过该上限的大型二进制 fixture；完整跨 1 MiB 预览边界的 1,049,516 字节资产已实测。

原版尚未知：键语法及修饰键、焦点和 IME 语义；资源覆盖解析/主题选择及输出结构；资产总量、二进制内容块与大小策略；截图 DPI、裁剪及内容块。上述差异保持“本项目明确行为 / 原版待对照”。
