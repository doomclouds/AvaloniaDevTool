# MCP 与命令行

本项目使用 ModelContextProtocol 2.3.0 提供 stdio 和内网 Streamable HTTP 两种 MCP 入口，连接自研 Agent SDK；不依赖原版 DeveloperTools 的运行进程或账户。默认 `agent` profile 提供 44 项工具：canonical 16、框架交互/截图 9、Windows native 8、文件弹窗 8、同连接原始采集 3。stdio 显式 `official` 保留 canonical 16 项兼容入口，`extended` 保留旧 62 项诊断扩展；HTTP 首版仅提供 agent。extended 与 canonical 是独立契约，不是兼容超集。

## 启动

先构建并运行接入 Agent 的目标。客户端直接启动构建产物：

```powershell
dotnet src/AvaloniaDevTool.Cli/bin/Debug/net10.0/AvaloniaDevTool.Cli.dll mcp
```

可指定初次读取时自动附加的目标管道：

```powershell
dotnet src/AvaloniaDevTool.Cli/bin/Debug/net10.0/AvaloniaDevTool.Cli.dll mcp --pipe avalonia-devtool-demo
```

等价的显式默认值为 `--transport stdio --profile agent`。`--pipe`、`--profile` 和 `--transport` 每项一次且需要非空值；`--listen IP`、`--port PORT` 只在 HTTP 模式下有效。stdio 标准输出只承载协议，运行日志写入标准错误；构建应在客户端启动前完成。

MCP 客户端进程配置示例：

```json
{
  "command": "dotnet",
  "args": [
    "C:/WorkSpace/ResearchProjects/AvaloniaDevTool/src/AvaloniaDevTool.Cli/bin/Debug/net10.0/AvaloniaDevTool.Cli.dll",
    "mcp"
  ]
}
```

路径应换为实际构建产物。示例不自动修改任何客户端配置。

## 内网 HTTP 与标准 Codex 配置

被测电脑 A 在完整 CLI 分发目录中运行以下命令，将 IP 替换为 A 的实际内网地址：

```powershell
dotnet AvaloniaDevTool.Cli.dll mcp --transport http --listen 192.168.1.50 --port 7443
```

Codex 电脑 B 使用与其他 MCP 相同的标准配置加载连接，无需 Token：

```toml
[mcp_servers.avalonia_testpc]
url = "http://192.168.1.50:7443/mcp"
```

HTTP 条目不设置 `command/args`；它连接 A 已运行的服务，不在 B 启动执行端。默认 HTTP 只监听 `127.0.0.1:7443`；内网须显式 `--listen`。`0.0.0.0` 只能作为绑定地址，URL 填 A 的可达 IP。`/mcp` 为标准 MCP，现有 `/diagnostics` WebSocket 是另一条内部 RPC 接口。

工具执行、应用发现、文件路径、预览构建、native worker 均在 A。MCP 连通后，通过下面的“发现、附加和节点”步骤选择 A 上的程序；应用须事先接入 SDK。

HTTP 采用独立逻辑会话和单桌面控制租约；发现应用不抢占目标，首次附加取得控制，detach 释放控制但保持 MCP 会话。十分钟无工具/协议活动且无活跃调用时失效，断线写操作不盲目重试。HTTP 首版选择 official/extended 会在监听前报 `unsupported_profile`。

完整启动、运行时依赖、应用关联和验证边界见[内网部署](architecture/lan-mcp-design.md)。

## 默认 agent 工具

| 组 | 数量与工具 |
| --- | --- |
| canonical | 16 项，完整目录见下表；agent 和显式 official 共用此合同 |
| 框架交互/截图 | 9 项：ui-input、ui-click、ui-key-chord、ui-drag、ui-text-selection、ui-select-item、ui-control-action、ui-wait、ui-screenshot |
| Windows native | 8 项：native-windows、native-tree、native-find、native-inspect、native-action、native-action-status、native-input、native-screenshot |
| 真实文件弹窗 | 8 项：file-dialogs、file-dialog-inspect、file-dialog-set-paths、file-dialog-set-filter、file-dialog-accept、file-dialog-cancel、file-dialog-overwrite、file-dialog-wait |
| 原始诊断采集 | 3 项：capture-start、capture-status、capture-stop |

`ui-*` 操作真实 Avalonia 控件与路由输入。native 工具通过隔离 worker 读取 UIA、执行 provider pattern、受限 OS 输入或 PNG 采集；文件工具操作真实 OS picker，不替换 StorageProvider。capture 工具复用同一个目标诊断连接。产品提供操作与原始数据，业务关联和判断由调用方负责；范围见 [当前 Agent 范围](agent-tools/architecture/current-agent-scope.md)，详细合同见 [Agent 交互](agent-tools/features/agent-interaction.md)。

### canonical 兼容入口

`tools/list` 是当前服务的实际输入契约。下表概括官方入口；完整必填、类型、默认、枚举、注解及证据边界见 [官方能力基准](agent-tools/research/official-capabilities.md)。

| 工具 | 输入与本地行为 |
| --- | --- |
| `attach-to-app` | `id=0` 列出本地端点；使用返回的 id 附加真实应用 |
| `attach-to-file` | 必填 `xamlFilePath,xamlProjectPath`，`build=false`；解析项目并管理独立 Previewer |
| `detach` | 断开，释放临时诊断状态和自建预览 |
| `tree` | `nodeId=0,treeKind,depth=10`；三种树，1 为直接一层，-1 为完整子树 |
| `ancestors` | 必填 `nodeId`；本地返回 root 到自身的真实 Merged 父链 |
| `search` | `query,scopeNodeId=0,searchTemplates=false,limit=-1`；类型和 x:Name 部分匹配 |
| `props` | `nodeId,propertyNames=null`；真实注册属性、有效值、来源和名单过滤 |
| `set-prop` | `nodeId,propertyName,value:string,isXamlValue=false`；临时覆盖、字符串特殊值和 XAML 对象 |
| `styles` | `nodeId,propertyNames=null,includeDefaults=false`；真实样式帧、setter、绑定及可选默认值 |
| `pseudo-class` | `nodeId,pseudoClass=null,isActive=null`；列举或修改命名伪类 |
| `resources` | `nodeId=0,onlySelf=false`；应用或节点资源及来源 |
| `assets` | 无参数；枚举目标程序集内嵌资产 URL |
| `open-asset` | `assetUrl`；完整读取允许的 avares 资产，返回带 MIME 的原始字节 |
| `screenshot` | 必填 `nodeId`；指定元素 PNG，返回 MCP image 内容块 |
| `input` | `nodeId,inputEvent,arg=null`；Click、KeyDown、KeyUp、Text 四类框架输入 |
| `action` | `nodeId,action`；Focus、Enable、Disable、BringIntoView |

原版 2.2.3 归档初始目录是 15 项，没有 `detach`，但官方文档包含它且目录声明可动态变化。本项目按文档公开 16 项；这不是原版附加后目录已核实的结论。15 项的归档均无 outputSchema，原版真实返回 DTO、错误及部分边界尚未知，不宣称输出逐字段等价。

## 发现、附加和节点

客户端完成 `initialize/tools/list` 后，可按下面顺序调用：

```json
{"name":"attach-to-app","arguments":{}}
```

从返回应用列表取 `id` 再调用：

```json
{"name":"attach-to-app","arguments":{"id":1}}
```

这里的 1 只是示例，必须使用本次列表实际返回值。发现 ID 属于当前 MCP 服务映射，不是承诺的操作系统 PID。本地发现覆盖本工具管道前缀以及明确配置的 `--pipe`，端点可发现不等于已经成功附加。

随后通过 `tree/search` 得到当前节点。canonical 与 ui-* 的数值身份区分连接和预览内容代际，不能跨应用或 XAML 重载复用。Merged 合成节点只提供导航，实际属性、截图和输入须使用真实元素。Agent 同时接受一个诊断连接；MCP 附加不要求 F12。

native ID 是 `native:<session>:<opaque-id>` 字符串，由 native-windows/tree/find 返回；不可与数值 nodeId 混用。native 会话核对附加目标的 PID、UTC start time、窗口 owner 归属和 runtime ID；过期窗口应重新枚举。native/file-dialog 不取得 managed call gate，所以 managed 动作进入模态等待时仍可使用同一 MCP 处理其 owned 系统弹窗。UIA `pending/dispatched` 和 native-input `Injected` 都不是业务完成证明。

## 真实原生文件流程

以 [AutomationSample](../samples/AvaloniaDevTool.AutomationSample/README.md) 为目标，managed 操作打开 FilesTab 和对应按钮；file-dialog-wait/inspect 获得当前 picker，set-paths 只填写并读回，accept 才确认；有覆盖 prompt 时 inspect 后显式 file-dialog-overwrite。cancel、拒绝覆盖和等待均有独立结果，过期 prompt 不复用。

研发脚本在选择器关闭后检查应用 DocumentEditor/DocumentPath/FileStatus，再独立读取磁盘长度、内容/hash，并重新用真实 Open 打开保存结果。选中路径不等于写入；短内容覆盖长文件核对截断，取消核对原文档和原文件未变。这些判断属于驱动脚本，文件工具持续返回 `ApplicationIoVerified=false`。

## 属性、输入和媒体边界

`set-prop.value` 必须是 JSON 字符串。`"null"` 对可空/引用属性设置真正 null；`"unset"` 释放本会话覆盖并恢复底层绑定/值。JSON null 是参数错误，不代替这两个字符串。`isXamlValue=true` 使用完整 xmlns 的真实 runtime XAML loader，转换失败保留原覆盖；动态 XAML 在 Native AOT 下不可用。名称有歧义时使用 `props` 返回的 owner-qualified ID。

本地 `styles.includeDefaults=true` 增加 metadata 默认值帧；`pseudo-class` 接受有无冒号，名称省略时列举，指定名称而省略 isActive 时读取现状。上述选择已有本地定义，原版默认/省略参数细节仍待对照。详见 [属性、样式与操作](agent-tools/features/properties.md)。

`input` 派发 Avalonia 框架事件，不是 OS 注入或 IME。`arg` 用于文字或命名 Key，当前不解释 `Ctrl+A` 这样的组合语法。`resources` 保留同名资源和来源，不计算一个伪造的唯一胜出值；`onlySelf` 是目标端范围边界。完整资产响应最多 16 MiB 原始字节，字体和任意二进制使用 blob 资源块；截图超边长、像素或 PNG 预算明确失败。详见 [输入、资源与媒体](agent-tools/features/input-media.md)。

## PNG 与同连接原始采集

| 工具 | 参数与坐标 | 返回与预算 |
| --- | --- | --- |
| `ui-screenshot` | 数值 `nodeId`；可选 `x/y/width/height` 必须全部省略或全部提供，使用控件局部 DIP；`scale=1` 是输出像素/DIP | PNG image + JSON 元数据 `NodeId/Width/Height/Bounds/Scale/DpiX/DpiY/CoordinateSpace`；输出边长最多 2048、总像素最多 2,097,152、PNG 最多 1 MiB。 |
| `native-screenshot` | native 字符串 `nodeId`；可选四元区域为节点局部物理像素；`maxPixels=8000000`，允许 1..16000000 | PNG image + JSON 元数据 `NodeId/Width/Height/ScreenBounds/CaptureMethod/Limitations/CoordinateSpace/MaximumEncodedBytes`；完整源窗口最多 16,000,000 像素、PNG 最多 16 MiB。 |
| `capture-start` | `metrics/profiler/activity=true`；`logs=true,events=false,eventName=Click,minimumLevel=Warning`；可选 `meterNames/instrumentNames/profilerCategories`；`capacity=512,pollIntervalMs=1000` | 返回 captureId、目标连接身份、状态及原始 metrics/profiler/activity 快照；capacity 1..1024，pollIntervalMs 100..60000。 |
| `capture-status` / `capture-stop` | 必填当前 MCP/目标连接创建的 `captureId` | 返回原始快照、Completeness 与 Issues；停止成功后冻结。旧 ID 不停止新目标或外部重启的采集，不接管已有 listener。 |

ui 截图使用 Avalonia RenderTargetBitmap 渲染真实控件子树，像素尺寸向上取整，DPI 为 `96*scale`，不随桌面 DPI 隐式缩放。native 截图使用 `PrintWindow(PW_RENDERFULLCONTENT)` 的限定窗口表面，区域向外取整并必须位于当前节点和源窗口内；不激活、恢复窗口，不回退到桌面抓屏。隐藏、最小化、offscreen、过期身份或采集中移动/缩放明确失败。

PrintWindow 依赖目标渲染器。未绘制请求区域会被检测并报错，但 GPU、受保护、分层或嵌入内容仍可能黑屏或不完整；颜色正常不证明完整渲染。截图只提供 PNG，不作视觉比对、基线或业务判断。参数、错误和已验证证据见 [PNG 截图](agent-tools/features/agent-interaction.md#png-截图)。

capture 只输出保留样本及损失/错误，`Completeness` 描述数据采集状态，不表示业务或性能通过。Meter 原始精度/单位/值语义、样式/资源 Profiler 的未知结果、logs/events 容量信息继续保留。调用方自行建立业务关联；不另开 extended 连接抢占目标。详见 [原始诊断采集](agent-tools/features/agent-interaction.md#同连接原始诊断采集)。

## XAML 文件附加

```json
{
  "name": "attach-to-file",
  "arguments": {
    "xamlFilePath": "C:/Projects/MyApp/Views/MainView.axaml",
    "xamlProjectPath": "C:/Projects/MyApp/MyApp.csproj",
    "build": true
  }
}
```

路径必须是存在的绝对路径，源文件在所选项目目录内。默认/false 不主动构建；true 使用所选 TFM 实际构建，失败不伪装为附加成功。CLI 用真实 MSBuild 评估 TargetPath，不能猜测某次外部构建参数或任意输出目录。

Previewer 是必要依赖，加载实际 App 资源、主题、自定义类型和 `x:Class`，不调用目标 Main。保存所选 XAML 后重载，错误保留上次成功视图；成功更新后重新 tree/search。detach、切换、取消和退出清理本会话拥有的预览，外部附加应用不被终止。框架/运行时、项目启动、多 TFM 策略和异常退出限制见 [文件预览](agent-tools/features/preview.md)。

## extended 扩展

```powershell
dotnet src/AvaloniaDevTool.Cli/bin/Debug/net10.0/AvaloniaDevTool.Cli.dll mcp --profile extended
```

旧目录包含远程附加、分页和 selector、画刷编辑、资源写入与查询、资产导出、日志事件、指标、Profiler、断点、覆盖层、渲染诊断、弹层冻结、几何/合成及 CLR 读取等。它们不进入官方 16 项验收分母，工具名和参数以该 profile 的 `tools/list` 为准。

尤其注意同名工具差异：旧 `attach-to-app` 接受 `pipeName`，旧 `attach-to-file` 接受显式程序集/项目路径，旧 `set-prop` 的 JSON null 表示清覆盖，树和搜索还有旧预算参数。不能把 official 调用字典直接交给 extended。保留的扩展文档已标识旧桌面基线；其中 Inspector 操作仅在 `main/fa1c623` 可用。

## 验证与范围

CLI 测试使用真实 SDK stdio 子进程，执行 initialize、tools/list、tools/call 并连接真实 Avalonia Agent。专项验证工具行为不等于最终统一回归，当前记录见 [测试](testing.md) 和 [实现状态](implementation-status.md)。

框架 stdio、Windows native 底层、文件路径预检、真实 picker→应用 I/O、同连接 capture 和 PNG 已有对应专项证据。PNG 包括真实控件、WinForms SDK、实际 stdio MCP 的原生主窗与 CommonItemDialog/路径框区域；几何单测仅验证坐标和预算。专项通过不冒充本轮整套通过，统一结果由 [实现状态](implementation-status.md) 和 [测试说明](testing.md) 登记。

native 只支持已附加本机 Windows 应用及 owned 窗口，拒绝 remote transport。用户不做 macOS/Linux 原生后端或原版授权运行等价对照；IME 候选/组合输入、系统文件/数据 DragDrop 暂不做。完整 CPU 调用栈/内存分配剖析交由其他工具；本项目不做业务断言、视觉比对/基线、性能阈值、发版门禁或 CI。原版 tools/call、动态目录及默认语义资料保留为独立研究，不列为本轮待完成项。独立 Inspector 和外部 XamlMcp 后端历史位于 `main/fa1c623`。
