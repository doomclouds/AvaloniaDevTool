# 官方 Agent/MCP schema 与本地实现差距

研究日期：2026-10-25。比较基线：`fa1c623a69073eab81a1e226ea05405825e76831`（研究开始时 `codex/agent-tools` 的 HEAD）。本文记录基线差距；后续实现必须重新验证，不能把本表当作实现后的现状。

结论：本地已有同名诊断能力，但官方调用方直接使用的参数契约存在多处不兼容。需要按官方的真实输入与用户可观察效果验收，不能用本地工具名总数计算完成率。官方成功执行结果的形状目前未知，不能用本地 DTO 替代官方结果证据。

## 证据身份、范围和清理

- 当前 `dotnet tool list --global`：包 `avaloniaui.developertools`，版本 `2.2.3`，命令 `avdt`。
- 本轮实际运行的文件：`C:/Users/10062/.dotnet/tools/.store/avaloniaui.developertools/2.2.3/avaloniaui.developertools.windows/2.2.3/tools/net6.0/any/tool-win-x64/AvaloniaUI.DeveloperTools.exe`。
- 文件大小：`55,258,952` 字节；SHA-256：`76a88bb2fd68d40a7b663a0f710c8bc18dc85bf9a2f732dc6f2f84c6ac0f9f08`。
- 本轮正常启动上述文件的 `mcp` 子命令，只发送 `initialize`、`notifications/initialized`、`tools/list`。没有 `tools/call`、目标附加、认证或授权界面操作，也没有启动 REA/Ghidra 冷导入。
- 原始响应与完整 schema：[official-mcp-2.2.3-schema.json](official-mcp-2.2.3-schema.json)。文件 SHA-256：`977822db586d02321e6583857af59a9de0f1efe4c465059e7abf71fe3e9acdab`；准确采集时间见其 `capturedAt`。
- 实际 `initialize.serverInfo.version`：`2.2.3+92df9d92acf0bbe8b2ffa96370c1bf848df81d27`；协议版本：`2025-06-18`；能力：`logging`、`tools.listChanged=true`。
- 本轮初始未附加会话实际声明 15 项工具，全部带 `execution.taskSupport=optional`。这个字段是服务声明，本轮没有验证异步任务执行。
- 自有 PID `29332` 在关闭 stdin 后正常退出，退出码 `0`；证据中的 `cleanup.ownedProcessExited=true`。未结束任何既有用户进程。
- 旧基线中 `docs/research/sources/mcp-tools-2.2.3-index.json` 和 `docs/research/native-deep-analysis.md` 记录了 2026-10-24 的同版本初始 15 项观察。其指向的正常 Temp 原始目录本轮不可见，因此重新采集，而不是把摘要冒充完整 schema。

工具声明证据只证明参数与注解被实际公布，不证明业务执行成功。当前目录没有 `detach`；官方公开文档包含该项，且服务声明目录可变，必须保留“初始目录”限定，不能断言安装版完全没有 `detach`。官网能力范围与文档版本单独见 [official-capabilities.md](official-capabilities.md)。

## 官方输入契约逐项记录

下表的 required、类型、枚举、默认值均来自本轮实际响应。`?` 表示非必填，不代表自动接受 JSON `null`；显式 nullable 会另列。schema 未给出范围的字段不擅自补上界限。

| 官方工具 | required | 完整可见输入与默认值 | 注解 |
| --- | --- | --- | --- |
| `props` | `nodeId` | `nodeId: integer`；`propertyNames?: array|null = null`，items `string|null` | readOnly=true，destructive=false |
| `input` | `nodeId,inputEvent` | `nodeId: integer`；`inputEvent: string`，枚举 `Click,KeyDown,KeyUp,Text`；`arg?: string|null = null`，用于文本或按键 | readOnly=false，destructive=true |
| `ancestors` | `nodeId` | `nodeId: integer`；描述 `Gets parent chain from root.` | readOnly=true，destructive=false |
| `set-prop` | `nodeId,propertyName,value` | `nodeId: integer`；`propertyName: string`；`value: string`；`isXamlValue?: boolean = false`，描述为带 xmlns 的 XAML；特殊值说明是字符串 `'null'`、`'unset'` | readOnly=false，destructive=true |
| `styles` | `nodeId` | `nodeId: integer`；`propertyNames?: array|null = null`，items `string|null`；`includeDefaults?: boolean = false` | readOnly=true，destructive=false |
| `screenshot` | `nodeId` | `nodeId: integer`；描述称捕获 PNG | readOnly=true，destructive=false |
| `assets` | 无 | 无字段；描述称图片、字体等嵌入资产，返回供 `open-asset` 使用的 URL | readOnly=true，destructive=false |
| `open-asset` | `assetUrl` | `assetUrl: string`，描述 `avares://...`；工具描述为下载资产 | readOnly=true，destructive=false |
| `attach-to-file` | `xamlFilePath,xamlProjectPath` | 两者 `string`；`build?: boolean = false`，启动 previewer 前构建项目 | 未给 annotations |
| `resources` | 无 | `nodeId?: integer = 0`，省略为 Application；`onlySelf?: boolean = false`，只读取节点自身资源 | readOnly=true，destructive=false |
| `action` | `nodeId,action` | `nodeId: integer`；`action: string`，枚举 `Focus,Enable,Disable,BringIntoView` | readOnly=false，destructive=true |
| `attach-to-app` | 无 | `id?: integer = 0`；字段说明称省略时列举应用；工具描述另称未给 processId 且多个应用时列举 | readOnly=false，destructive=false |
| `tree` | 无 | `nodeId?: integer = 0`；`treeKind?: string`，枚举 `Logical,Visual,Merged`，**schema default 为数值 `0`**；`depth?: integer = 10`，`1` 为直接子节点，`-1` 为完整子树 | readOnly=true，destructive=false |
| `search` | `query` | `query: string`，类型名或 x:Name 的部分匹配；`scopeNodeId?: integer = 0`；`searchTemplates?: boolean = false`；`limit?: integer = -1` | readOnly=true，destructive=false |
| `pseudo-class` | `nodeId` | `nodeId: integer`；`pseudoClass?: string|null = null`；`isActive?: boolean|null = null`；省略 pseudoClass 时列举；示例名称 `pointerover,pressed,focus,disabled,selected` | readOnly=false，destructive=true |

### 不应自行抹平的契约歧义

1. `tree.nodeId` 类型为 integer、默认 `0`，工具描述却写 `Null nodeId returns roots`。本轮没有调用以验证 JSON null 或省略/0 的精确分派。
2. `tree.treeKind` 是字符串枚举却给数值默认 `0`。不能据枚举显示顺序猜测这个数字在运行时代表 Logical 还是 Visual；验收应分别检查省略参数与显式字符串。
3. `ancestors` 的 schema 说“from root”；官网对顺序的文字说明与之不同。必须以实际链顺序做验收，暂不宣称官方包含/排除目标节点或顺序已确认。
4. `set-prop.value` 明确是 string。文案的 `null`/`unset` 是字符串特殊值，不能据此宣称官方接受 JSON null，更不能把两个字符串合并成同一撤销动作。
5. `pseudo-class` 文案示例无冒号；本轮没有执行确认无冒号与带冒号的接受规则。兼容实现应覆盖文档形式，并独立验证状态效果。

## 本地实际调用链与差距

本地签名来自 `src/AvaloniaDevTool.Cli/InspectorTools*.cs`；效果继续追踪到 `ToolSession`、`DiagnosticsClient`、`AgentSession` 与专用服务。下面是源码证据，不将其描述为本轮已运行的业务验证。

| 工具 | 本地基线实际输入/调用链 | 明确差距与可测试要求 |
| --- | --- | --- |
| `attach-to-app` | `InspectorTools.AttachToApp(string pipeName)` → `ToolSession.AttachAsync` → `DiagnosticsClient.ConnectAsync`，直接连接本地命名管道；另有本地扩展 `apps` 返回管道名字符串 | 官方 `{}` 和 `{id:...}` 不符合本地必填 `pipeName`。需统一发现/附加入口，省略 id 列举可选择标识，再用该 id 真正附加；不能只改字段名字。当前 `[ReadOnly=true]` 也与官方 readOnly=false 不同。应用列表/选择后的返回结构官方未知。 |
| `attach-to-file` | `InspectorTools.Preview.cs` 必填 `filePath,assemblyPath,projectPath`；`headless=false,watch=true,applicationAssemblyPath=null,applicationType=null,timeoutSeconds=30`。`ToolSession.AttachFileAsync` 要求所有文件已存在，启动自有 previewer；明确不构建项目、不运行 Main | 官方只需 `xamlFilePath,xamlProjectPath`，可指定 `build=true`。当前缺少官方字段与构建行为，且强制预先提供程序集路径。验收必须从项目解析正确产物，证明 build=true 真正构建、build=false 的产物选择/缺失错误，以及附加后查询与文件变更生效。 |
| `tree` | `nodeId=null,kind=Logical,depth=1,maxNodes=500,includeTemplates=true`；CLI 通过 Client 根/子页递归，支持三个树种；`depth=-1` 仍有 64 层、最多 2000 节点限制，并返回 `Truncated`；`nodeId=0` 被当作对象 id 查询祖先 | 字段 `kind` 不兼容 `treeKind`；缺省深度 1 与官方声明 10 不同；根哨兵 0 不同；默认 500 个节点可能截断官方 -1 用例。验收显式 Logical/Visual/Merged、省略/0 根、深度 1/10/-1、模板层级；保留明确限额/截断信号，不能把有上限遍历宣称无限全树。 |
| `props` | `nodeId`，`propertyNames=null,contextual=false,includeClr=false`；Client `GetPropertiesAsync(new PropertiesRequest(...))` → Agent 注册属性诊断；CLI 按 `Name` 或 owner-qualified `Id` 精确筛选 | 常用官方字段已有。需证明未给过滤器与多属性过滤效果；官方 items 可 null，本地过滤器处理需明确。官方返回形状未知，本地是含类型、优先级、编辑性与值预览的 `PropertyInfoDto[]`，不能据此声明线格式兼容。 |
| `set-prop` | `(nodeId,propertyName,string? value)`，没有 isXamlValue；CLI 先读属性，要求名称或 Id 唯一；JSON null 调 `ClearPropertyAsync`，字符串走 `PropertyValues.Parse`；Agent 用 `BindingPriority.Animation` 的可释放覆盖保护原绑定 | **缺 XAML 对象值、字符串特殊 null/unset 语义**。字符串属性收到 `"null"`/`"unset"` 会成为文字；nullable 值类型的 `"null"` 才被 parser 转成 null，引用类型未统一；`"unset"` 没有分支。验收分别覆盖 reference null、nullable null、unset、普通字面值、带 xmlns 的 XAML、错误 XAML、绑定存在时的修改/恢复。官方究竟清除本地值还是其他优先级需要真实结果/状态证据。 |
| `styles` | 仅 `nodeId`；Client `GetStylesAsync` → `Avalonia12DiagnosticsAdapter.Inspect`。读取 AppliedFrames、绑定与校验错误；未实现 includeDefaults 分支，也未枚举默认属性值 | 缺 `propertyNames`、`includeDefaults`。需证明过滤作用于真实帧/setter/绑定，并用只有默认值的属性证明 includeDefaults=false/true 的结果差异，不能只增加 schema 字段。 |
| `ancestors` | `nodeId,kind=Logical` → Agent `GetAncestors`，达到根后 `Reverse()`；返回 root→target 且包含 target，Merged 走独立 parent 模型 | 官方只有 nodeId；本地默认树与目标可达性、包含 target、输出次序尚不能与官方实际结果比较。验收普通子节点、模板节点、失效节点，先明确链顺序。 |
| `search` | `query,kind=Logical,rootId=null,maxResults=100,maxVisited=3000`；Agent BFS，匹配完整类型名、Name、部分预览文本；结果/访问范围分别限制 1..500、1..20000 | 缺 `scopeNodeId,searchTemplates,limit`；默认 100 与官方 -1 不同。当前默认 Logical 不等价于已验证的“模板不搜索”，没有 searchTemplates 控制路径。验收类型/Name 部分匹配、作用域隔离、模板标志开关、limit 正值与 -1，另检查有界遍历的完整性/截断信息。 |
| `input` | 必填 `nodeId,kind`；可选 text/key/modifiers/x/y/deltaX/deltaY/button；Client → `InputInspectionService.Send`；支持 Text/KeyDown/KeyUp/Click 与扩展指针/滚轮，Click 真实派发 move/press/release | 官方必填 `inputEvent` 和可选 `arg` 不符合本地字段。应映射 Text→text、键事件→key，不能只接受参数后丢弃。验收 TextBox 实际文本、Back/方向键等按键效果、Button Click 回调次数、无效 arg 和目标状态；不能用 Delivered=true 替代实际效果。 |
| `action` | 必填 `nodeId,action`，枚举能力相同；Agent Focus 返回是否接受，BringIntoView 调框架方法，Enable/Disable 复用可撤销属性覆盖 | 官方输入字段可对齐；输出与持久性尚未知。验收焦点、滚动位置、有效禁用状态及失败可见性；本地 `ElementActionResult.Succeeded=false` 是否应产生 MCP error 需明确，不可假定官方错误格式。 |
| `pseudo-class` | `nodeId,name=null,enabled=null,reset=false`；Client 的 list/set/reset → Agent；`ValidatePseudoClass` 要求 `:` 前缀，严格比较名称并维护可恢复覆盖 | 官方字段为 `pseudoClass,isActive`；文案使用无冒号示例。需统一接受官方输入并改变真实伪类/样式；列举、省略 isActive、true/false 各路径单独验收。额外 reset 可以保留，但不能替代官方字段。 |
| `screenshot` | 必填 nodeId；Client → Agent `CaptureElement` → `RenderTargetBitmap` → PNG；CLI `ImageContentBlock.FromBytes(...,"image/png")` | 参数与格式意图吻合；官方实际内容块、大小与错误未知。本地要求可见 top-level、正布局，2048 边长、2,097,152 像素、编码后 1 MiB；应验 PNG 签名、尺寸、已知像素与 headless/预览情况，超限不能伪造成功截图。 |
| `resources` | `nodeId=null,dictionaryId=null`；不传字典时返回 `ResourceDictionaryInfo[]`，传字典时返回 entries；Agent `GetTrees` → `GetTree` 总先加入 Application 与 styles，再沿选中元素 Parent 链读取资源 | 官方 `nodeId=0` 不是本地 null；缺 `onlySelf`，现链路无法排除 Application/祖先。验收 Application 默认域、自身与祖先的同名键隔离、onlySelf 开关以及作用域明确的结果。只删除输出里某些名称不等价于真实资源域查询。 |
| `assets` | 无输入；Agent 枚举当前 AppDomain 已加载程序集的 `!AvaloniaResources`，最多 2000 项，返回 Uri/AssemblyName/Extension | 官方语义意图相近，实际数据结构未知。验收图片/字体等嵌入 URL，并把返回 URL 原样传给 open-asset 成功读取；超 2000 时当前返回无显式截断标志，不能据清单长度宣称全量。 |
| `open-asset` | assetUrl → Agent `AssetInspectionService.Open`；仅允许已加载目标程序集 avares；预览读最多 1 MiB。CLI 返回一段序列化 AssetPreview 文本，Base64 存在时另加 ImageContentBlock | 官方描述为“下载”，本地为受限预览，无法据声明证明全量/部分输出一致。应验 PNG/JPEG/SVG、文本、字体、一般二进制及大资产；核对完整性、MIME 和 content block 类型。注意本地 Base64 也可能是字体，CLI 当前统一构造 image block，不能把字体成功读到 Base64 当作正确的 MCP 媒体输出。 |
| `detach`（官网项） | 本地始终公布；调用 `ToolSession.DetachAsync`，断开 Client、清理自有 previewer，返回字符串 `Disconnected` | 原版初始 schema 没有此项；官方安装版附加后的动态目录与结果需要有权限的真实目标才能确认。自研仍须验断连、临时修改恢复、previewer 退出、重复 detach、重附加；没有运行证据时只记“已实现路径”。 |

## 返回值、MIME 和错误边界

本轮官方 15 项工具均没有 `outputSchema`。原版没有执行 `tools/call`，因此不能精确比较下列行为：JSON 与文本布局、content/structuredContent、是否含嵌入资源、真实 MIME、isError 与 JSON-RPC error 的分工、失败错误码、未附加/失效节点/无效值的文字内容。官方 schema 只为 screenshot 声明 PNG 意图，为 assets/open-asset 声明 URL/下载意图。

本地代码可明确确认：

- 普通工具直接返回 DTO/字符串，经 MCP SDK 封装；本地已有 MCP 测试使用文本块反序列化这些 DTO，但本轮没有重跑这些测试。
- `screenshot` 显式返回 `image/png` 的 `ImageContentBlock`。Agent 的 `ElementScreenshot` 中 PNG 为 Base64；这不是官方输出结构的证据。
- `open-asset` 显式返回 `CallToolResult.Content`：先一个 `TextContentBlock`（AssetPreview 元数据 JSON，Base64 置 null），可选另一个 ImageContentBlock；文本/字体/二进制等须分别检查，不能只验文件扩展名。
- `ToolSession.GetAsync` 对未附加抛 `McpException("not_connected: ...")`；tree/tree-children 等通过 `WithTreeErrorsAsync` 将 RpcException.Code 放进 MCP 异常文字。
- 多数其他工具直接让 RpcException 穿透，未统一转换；本地协议 `RpcPeer` 传输 `{code,message}`，但这并不证明最终 MCP 错误文本总保留该 code。应实际验不同失败路径的 `isError`、文字、诊断码，避免“所有错误已统一”的虚假声明。
- 本地 attach-file 对路径、缺文件、超时使用显式 McpException，典型前缀 `invalid_path,file_not_found,preview_timeout`；本地 set-prop 的已知 Agent 错误包括 `readonly_property,unsupported_type,conversion_failed,invalid_value,unsupported_property`，且名称未唯一时 CLI 抛 `ambiguous_property`。

## 官方能力验收分母建议

以“官方工具 + 有区别的参数/状态/效果”建立清单。初始 15 项与官网 detach 共构成工具范围，但完成度应在真实验收项上统计，并在报告中区分：

1. 输入契约被实际公布；
2. 本地调用已贯穿 Client/Agent；
3. 通过带真实状态断言的 MCP 端到端测试；
4. 受许可/环境限制而尚未与原版实际效果比较的部分。

优先验收项是：发现→id 附加、项目→构建/预览、tree 省略值与三树/深度、search 作用域/模板/limit、set-prop 普通/null/unset/XAML、styles 筛选/默认值、resources onlySelf、input arg 到真实控件行为、媒体 MIME/完整性、失效 ID/未附加/错误值、detach/重附加与清理。不得将拓展工具计入官方分母，也不得因为“同名工具存在”就标记完成。

## 本地源码证据入口

- CLI：`src/AvaloniaDevTool.Cli/InspectorTools.cs`、`InspectorTools.Input.cs`、`InspectorTools.Preview.cs`、`InspectorTools.TreeWorkflows.cs`、`ToolSession.cs`。
- Client：`src/AvaloniaDevTool.Client/DiagnosticsClient.cs`、`DiagnosticsClient.Exploration.cs`、`DiagnosticsClient.Inspection.cs`、`DiagnosticsClient.Input.cs`。
- Agent：`src/AvaloniaDevTool.Agent/AgentSession.cs`、`AgentSession.Exploration.cs`、`Avalonia12DiagnosticsAdapter.cs`、`PropertyValues.cs`、`ResourceInspectionService.cs`、`AssetInspectionService.cs`、`InputInspectionService.cs`。
- 既有验证入口（本轮只阅读，未执行）：`tests/AvaloniaDevTool.Cli.Tests/McpIntegrationTests.cs`、`PreviewAttachTests.cs`、`TreePagingMcpTests.cs`。已有断言主要使用本地参数名，不能替代官方契约验收。
