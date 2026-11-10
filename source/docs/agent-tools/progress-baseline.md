# 官方 Agent/MCP：52 项保守进度基线

> 历史基线：以下数字描述 `fa1c623` 修改前状态。当前验收结果见 [progress-current.md](progress-current.md)，不要将本页13.5%当作分支现状。

基线日期：2026-10-25。目标 ID 和验收条件固定取自 [official-capabilities.md](research/official-capabilities.md) 的 52 行；逐工具差距见 [schema-parity.md](research/schema-parity.md)。本文件只评估官方 Agent/MCP 目标，不包含桌面面板、社区后端和额外诊断工具。

**当前为 A=7、P=35、U=6、?=4，共 52 项；严格已验收项为 7/52（13.5%），尚有 45 项不能计通过。** 这不是代码复用率，也不是“只完成了 13.5% 的工程量”：35 项已有不同程度的基础服务，需要补契约或完整行为；6 项缺少对应实现分支；4 项需核定原版语义。各项成本不同，不给 P 任意折半，也不由这个比例换算剩余工时。

## 口径和证据强度

| 状态 | 计入通过 | 定义 |
| --- | --- | --- |
| A | 是 | 本行所需官方参数可直接由当前入口消费，且本轮运行的既有测试有实际效果断言。只证明此行，不代表整个工具完成，更不代表已与原版 tools/call 逐字段对照。 |
| P | 否 | 已有服务或部分行为/测试，但官方契约、必要样本或完整边界缺失。源码存在不能升级为 A。 |
| U | 否 | 该行关键分支没有实现；相邻能力可复用也不算完成。 |
| ? | 否 | 原版语义尚未核定，当前无法给出完整验收判定。已有本地行为另写清楚，不把未知当实现为零。 |

A 按单个验收行判定：例如 `pseudo-class` 只传 `nodeId` 列举可以与官方兼容，但 `pseudoClass/isActive` 修改参数尚不兼容，所以 PSEUDO01 与 PSEUDO02 状态不同。`styles(nodeId)` 的核心读取可用，也不意味着过滤和默认值已完成。参数不匹配的行一律没有计 A。

本轮仅重跑 6 个既有测试，**CLI 3/3、Agent 3/3 通过，0 失败、0 跳过**。其中 C01、SET01、SHOT01 有实际 stdio MCP 结果；STYLES01、PSEUDO01、ACT02、ACT03 的效果断言在 Agent 测试，CLI 对应参数/转发路径另外核对源码，尚不称这四行已完成官方参数的独立 MCP 端到端回归。新目标后续应补这些回归。历史“476 个测试通过”不能换算为本清单 52 项通过。

原版本轮只运行 initialize/tools/list，**没有原版 tools/call 对照**。未公开的输出字段、错误文字、默认树语义和目录变化仍保留未知，不因本地测试成功被补成“已证实原版行为”。

## 逐 ID 状态

| ID | 状态 | 现有证据与缺口 | 下一步验收 / 工作包 |
| --- | --- | --- | --- |
| C01 | A | E1 实际启动独立 stdio MCP，完成初始化/目录与未附加错误；无桌面操作依赖。 | 保留 E1 回归。 |
| C02 | P | 本轮原版完整 schema 已保存，但本地 attach、tree、input 等输入名、必填和默认值不一致；存在 15 个同名工具不代表契约匹配。 | W1：逐字段 schema 对照。 |
| C03 | ? | 原版初始 15 项、本地始终包含 detach；原版声明 listChanged，但尚无附加后的官方目录。 | W0：确认附加前后目录，再决定静态/动态行为。 |
| C04 | P | 自研 Agent 有显式接入入口，MCP 不靠 F12；未有桥缺失/未启动路径的完整诊断验收。 | W1：缺桥、缺启动调用、未发现目标的清晰诊断。 |
| C05 | P | Client 检查本地 ProtocolVersion 并抛 unsupported_version；未验证工具/桥包版本不匹配的实际触发和错误。 | W1/W0：建立版本不匹配样本；不复制未知官方文字。 |
| C06 | P | E3 实测文件更新后 nodeId 变化；没有覆盖旧 ID 继续操作时必定不命中新对象。 | W2：跨更新/会话的旧 ID 拒绝测试。 |
| C07 | P | Agent 有无效值/只读/enum 检查，E1 覆盖未附加错误；官方 inputEvent 契约缺失，props/set-prop/input 的 MCP 失败矩阵不完整。 | W1/W4/W5：错误不伪成功且保留诊断信息。 |
| C08 | P | 原版注解已归档；本地 attach-to-app 的 ReadOnly=true 与原版 false 不同，任务支持和破坏性注解没有完整对照测试。 | W1：声明元信息和真实效果分别验。 |
| APP01 | P | 本地 apps 能枚举管道，attach-to-app 必填 pipeName；官方 `{}` / `{id:0}` 不可直接执行发现路径。 | W1：统一 id 发现入口。 |
| APP02 | P | E2 用 pipeName 附加后可检查真实 Agent，但官方返回 id 到附加的链路不存在。 | W1：返回 id 附加并读目标独有节点。 |
| APP03 | P | ToolSession 能切换 Client 连接；没有两 app 的官方 id 选择及节点会话隔离测试。 | W1：双目标切换与旧节点失效。 |
| FILE01 | P | E3 真实预览已有，但当前必填 filePath/assemblyPath/projectPath，不接受两个官方必填名。 | W2：项目关联和路径契约适配。 |
| FILE02 | P | 现实现不构建并要求提供已存在程序集；缺 build=false 参数与项目产物解析。 | W2：由项目选择已有产物，明确歧义/缺产物错误。 |
| FILE03 | U | 没有 build 参数或先构建再附加的实现。测试夹具预先构建不算该分支。 | W2：build=true、失败输出、取消与不误连。 |
| FILE04 | P | E3 无用户预先运行 app，真实加载设计期资源和自定义控件；官方 attach-to-file 输入仍不兼容。 | W2：用两个官方路径复用此效果。 |
| FILE05 | P | E3 实测保存 XAML 后内容/ID 更新、错误恢复；入口仍是本地参数，旧 ID 拒绝边界未完备。 | W2：官方入口后的保存刷新与身份回归。 |
| DET01 | ? | E3 实测 detach 后自有 previewer 退出且 tree 返回错误；原版参数、目录时机和子进程处理未知。 | W0/W2：保留本地清理；核定官方契约。 |
| TREE01 | P | 支持 null 根与节点递归；官方 0 根目前会当成对象 ID。 | W3：省略/0/普通 ID 三路径。 |
| TREE02 | P | 三种算法和 Merged 模板数据已存在，E2 验模板层级；CLI 字段 kind 与官方 treeKind 不匹配。 | W3：显式三枚举的官方参数回归。 |
| TREE03 | P | 实现 depth=1/-1，但默认 1 与官方 10 不同；-1 有节点/层数预算。 | W3：默认10、1、-1及截断信号。 |
| TREE04 | P | E2 的节点可供 props/screenshot 消费；官方 root0/默认树/null 契约未齐。 | W3/W0：节点互操作与根/默认值。 |
| ANC01 | ? | Agent 已返回 root→target 真父链，既有父链测试存在；官方 schema 与官网方向不一致，自身包含规则未核。 | W0：先定成员/顺序，再补 MCP 根/中间/模板回归。 |
| SEARCH01 | P | query 匹配类型/Name 的代码已有；既有 MCP 样本主要按完整 Name，缺两种部分查询的官方参数效果矩阵。 | W3：类型和 Name 部分匹配。 |
| SEARCH02 | P | 底层 RootId 作用域已有；CLI 缺 scopeNodeId 及 0 根映射。 | W3：指定作用域排除外部同名节点。 |
| SEARCH03 | U | 没有 searchTemplates 参数/分支；Merged 树能表达模板不等于该开关已实现。 | W3：含模板样本验证 false/true 差异。 |
| SEARCH04 | P | 有 maxResults/maxVisited 与截断，但缺官方 limit，默认100而非-1。 | W3/W0：上限/-1与范围完整性。 |
| PROPS01 | P | E2/E3 确认真实属性和绑定/编辑值；尚缺同一官方调用矩阵中的样式值、绑定值、null 三类完整断言。 | W4：补有来源差异的样本。 |
| PROPS02 | P | propertyNames 过滤代码与既有过滤测试存在；未运行省略/null/名单的完整组合，数组内 null 语义待核。 | W4/W0：过滤边界回归。 |
| SET01 | A | E2 以 nodeId/propertyName/string value 实际调用，断言 Text 从绑定值变成 `MCP temporary edit`。 | 保留 E2；本行不包括特殊值/XAML。 |
| SET02 | P | PropertyValues 只对 Nullable<T> 的文字 null 转换；引用属性和修复场景未完整实现/验收。 | W4：nullable/reference null 分开验证。 |
| SET03 | U | 没有字符串 unset 分支；JSON null 清除本地覆盖不是官方 string unset。 | W4/W0：unset及来源/绑定恢复规则。 |
| SET04 | P | 普通文本转换已存在且 E2 有效果；isXamlValue 开关和完整 XAML 对象值未实现。 | W4：普通/XAML/非法 XAML 和类型检查。 |
| STYLES01 | A | E4 实测 Style 的 FontSize=31、真实活动帧 FontSize setter 和活绑定；CLI `styles(nodeId)` 输入可直接消费并转发。 | 补官方参数的 MCP 独立回归与模板样本，不将 STYLES02/03 勾选。 |
| STYLES02 | U | CLI/Client/Agent 无 propertyNames 的样式过滤分支。 | W3：过滤真实帧/setter/绑定。 |
| STYLES03 | U | 无 includeDefaults 参数，也未枚举默认属性值作为样式结果。 | W3：默认值开关的可观察差异。 |
| PSEUDO01 | A | E5 按目标类声明与当前状态枚举 `:probe`，断言 declared/active/override；CLI 仅给 nodeId 时走同一列表服务。 | 补 nodeId-only 的 MCP 独立回归；修改分支仍 P。 |
| PSEUDO02 | P | Agent 实际 set/reset 与状态断言已有；CLI name/enabled 不兼容 pseudoClass/isActive，未验命名伪类对样式效果。 | W5：官方字段与实际样式切换。 |
| PSEUDO03 | ? | 本地要求冒号且省略 enabled 时只列举；原版省略 isActive/空值/冒号规则未核。 | W0：分别记录所有输入形状。 |
| RES01 | P | Application 资源服务存在；官方省略可用但显式0被当作对象查找。 | W3：省略/0一致的 Application 域。 |
| RES02 | P | 能沿节点/祖先和 merged/theme 枚举资源；缺官方入口下遮蔽同名键的来源/范围完整断言。 | W3：三层同名键及主题/合并样本。 |
| RES03 | U | 无 onlySelf 参数或只保留节点自身范围的分支。 | W3：明确 true/false 的实际查询差异。 |
| ASSET01 | P | assets 能枚举已加载程序集且已有嵌入文本测试；缺图片+字体都得到可用 URL 的完整验收，2000项静默上限也需说明。 | W6：资产夹具与完整性/截断。 |
| OPEN01 | P | avares/text 读取服务已有；1MiB 预览不等于完整下载，字体 Base64 仍走 image block。 | W6/W0：多 MIME、原始字节完整性、媒体块类型。 |
| SHOT01 | A | E2 验 image/png 与 PNG 签名；E3 对目标 TextBox 截图做 Skia 解码、非空尺寸及多色真实文本像素断言。 | 保留 E2/E3；DPI/裁切/原版内容块仍待对照。 |
| INPUT01 | P | 既有真实 Click 事件测试，但 CLI 要 kind，官方 inputEvent 不可直接用。 | W5：Click + 真实 handler/command 次数。 |
| INPUT02 | P | KeyDown 框架事件服务已有且有文本编辑测试；缺 inputEvent/arg 映射。 | W5：官方 arg 到实际按键响应。 |
| INPUT03 | P | KeyUp 独立服务路径已有；缺官方字段与实际释放事件的 MCP 回归。 | W5：key-up handler断言，不能只看返回。 |
| INPUT04 | P | E3 使用本地 kind/text 后 props 验真实文字；官方 inputEvent/arg 不兼容。 | W5：Text + arg、中文和目标状态。 |
| ACT01 | P | E6 验 Focus 返回接受/拒绝；未断言 IsFocused、后续输入目标与焦点实际变化。 | W5：状态/后续输入断言。 |
| ACT02 | A | 官方 nodeId/action 参数名/枚举一致；E6 调 Enable 后即便绑定源=false仍断言 button.IsEnabled=true。 | 补该官方参数的独立 MCP 回归。 |
| ACT03 | A | 官方 nodeId/action 参数名/枚举一致；E6 调 Disable 后断言 button.IsEnabled=false。 | 补该官方参数的独立 MCP 回归。 |
| ACT04 | P | 已调用框架 BringIntoView；E6 只断言返回成功，没有离屏目标和 ScrollViewer offset/可见性断言。 | W5：真正滚动到离屏控件。 |

## 本轮 A 的实际运行证据

下列方法均在本轮所列过滤运行中通过。源码行位置用于定位具体断言；TRX 是本轮执行证据。`E4/E5/E6` 是 Agent 级实效证据，不冒充原版执行，也不冒充已经完成额外的 MCP 回归。

| 引用 | 实际通过的方法 | 断言位置 / 用途 |
| --- | --- | --- |
| E1 | `McpIntegrationTests.Stdio_server_initializes_advertises_tools_and_explains_missing_attachment` | `tests/AvaloniaDevTool.Cli.Tests/McpIntegrationTests.cs:24`；ListTools、工具声明、未附加 props 的 IsError/not_connected。用于 C01。 |
| E2 | `McpIntegrationTests.Stdio_tools_inspect_edit_capture_and_detach_a_real_agent` | 同文件 `:36`；SET01 官方字段调用 `:72`，实际 Text 变化 `:74`；PNG MIME `:99`、签名 `:100`。用于 SET01/SHOT01。 |
| E3 | `PreviewAttachTests.Stdio_attach_previews_built_classful_project_resources_custom_controls_input_pixels_and_live_saves` | `tests/AvaloniaDevTool.Cli.Tests/PreviewAttachTests.cs:21`；目标截图 `:43`，真实文本像素 `:49`；新节点ID `:53`；detach后错误 `:62`。用于 SHOT01；预览官方参数不一致所以 FILE 项仍 P。 |
| E4 | `InspectionIntegrationTests.Style_snapshot_reports_style_frames_and_live_binding_description` | `tests/AvaloniaDevTool.Agent.Tests/InspectionIntegrationTests.cs:16`；目标 FontSize `:28`、活动帧真实setter `:30`、活绑定 `:31`。用于 STYLES01。 |
| E5 | `ExplorationIntegrationTests.Pseudo_class_changes_are_enumerable_resettable_and_reverted_on_disconnect` | `tests/AvaloniaDevTool.Agent.Tests/ExplorationIntegrationTests.cs:109`；真实节点的 declared/active `:117`，并测试改动与断开恢复。用于 PSEUDO01。 |
| E6 | `ExplorationIntegrationTests.Element_actions_preserve_enabled_binding_and_focus_returns_actual_result` | 同文件 `:84`；Disable 后 `:94`；Enable 后改绑定源仍启用 `:98`。用于 ACT02/ACT03。Focus/BringIntoView 仅结果断言，不足以令 ACT01/ACT04 为 A。 |

本轮运行命令（均为 `dotnet test <project> --no-restore --filter <filter> --logger "trx;LogFileName=<file>" --results-directory <dir> --verbosity quiet`）：

- CLI project：`tests/AvaloniaDevTool.Cli.Tests/AvaloniaDevTool.Cli.Tests.csproj`；filter 为上述 E1、E2、E3 三个完整方法名的 `FullyQualifiedName~...`，使用 `|` 连接。
- Agent project：`tests/AvaloniaDevTool.Agent.Tests/AvaloniaDevTool.Agent.Tests.csproj`；filter 为上述 E4、E5、E6 三个方法名，方式相同。
- 结果目录：`C:/Users/10062/AppData/Local/Temp/avdt-official-baseline-20261025/`。
- `official-baseline-cli.trx`：3/3 passed；SHA-256 `9808135de4a4784566bfef63a2eafdf9bbafd48a2af407a4f0e94b08b588d431`。
- `official-baseline-agent.trx`：3/3 passed；SHA-256 `a9344ba3a0c7edb75f31153f8bb5906cfb55e53d2f49e6e271a2435c36506220`。

本轮没有新增生产代码或测试。测试输出位于系统临时目录，其可用性不作为项目运行依赖。

## 16 个工具中哪些需要实质修改

**已确定需要实质修改的 10 个**：`attach-to-app`、`attach-to-file`、`tree`、`search`、`set-prop`、`styles`、`pseudo-class`、`resources`、`input`、`open-asset`。

前九个有明确的官方参数或行为分支缺口；open-asset 有完整读取/媒体内容块问题，不是增加测试就能覆盖。不要把它们统称为“只改名字”。其中 build、XAML 对象赋值、默认样式值、onlySelf/模板搜索需要真实行为实现。

其余六个 `ancestors`、`props`、`assets`、`screenshot`、`action`、`detach` 当前没有据本轮证据确定的全部生产代码修改清单，但仍需要补验收/核语义；绝不等于六个工具都已完成。特别是 ancestors/detach 的原版语义、action 的实际焦点/滚动、props 的 null/过滤、assets 的字体图片和规模边界仍未关闭。

## 剩余工作包、依赖和风险

| 工作包 | 内容 / 相关 ID | 依赖 | 主要风险与验收门槛 |
| --- | --- | --- | --- |
| W0 原版语义与可对照边界 | C03、DET01、ANC01、PSEUDO03 及其他行附带的未知值/返回语义 | 有合法可附加的原版目标会话；当前没有原版 tools/call 证据 | 若对照受许可/环境限制，明确保留未知；不自行补造默认模式、链顺序或错误包。 |
| W1 官方契约与会话入口 | C02/04/05/07/08、APP01..03；id发现/附加、schema/注解、版本/错误；保留已有服务复用 | 先定本地 id→目标映射和会话边界 | 列表返回成功不等于真正附加；双app切换和旧ID不得串用。 |
| W2 项目构建与预览全链路 | FILE01..05、C06、DET01本地部分；官方路径→项目/TFM/产物→构建→自有进程→保存刷新 | W1会话；明确项目产物选择；复用现有previewer | 多TFM/配置/引用库、build失败/取消、旧ID、退出清理；现有预览服务可复用，但官方入口缺失。 |
| W3 树/搜索/样式/资源读取 | TREE01..04、SEARCH01..04、STYLES02/03、RES01..03 | W1入口；模板边界和预算策略明确 | 默认10/-1完整性、模板开关真实生效、默认值与资源遮蔽的来源正确，不能只在DTO层装字段。 |
| W4 属性转换与写入语义 | PROPS01/02、SET02..04；补引用null、unset、XAML及错误 | W0特殊值/恢复规则；W1错误包装 | 绑定/优先级保留、XAML对象类型与引用上下文、失败不能半写；复用现有可撤销覆盖不等于自动等价官方。 |
| W5 输入、伪类和操作效果 | PSEUDO02、INPUT01..04、ACT01/04；补A项独立MCP回归 | W1参数适配；W0空值/冒号/键语法 | 实际事件/命令/文本、焦点与滚动必须有状态断言，不能只验Delivered/Succeeded。 |
| W6 媒体与资产 | ASSET01、OPEN01、SHOT01扩展边界 | W1目标；多媒体嵌入夹具 | 原始字节、字体不能作为image块、MIME、截断/完整下载与大资产资源预算。 |
| W7 官方52项回归收口 | 把现有局部断言与上述缺口形成逐ID可复现的MCP测试，更新本表 | W1..W6；W0未关闭项继续标? | 一次运行覆盖哪些ID必须有映射；不以旧总测试数或声明工具数量代替通过率。 |

可先并行推进 W2/W3/W4/W5/W6，但它们都需要 W1 的官方参数和会话约定；W0 的不确定性应在实现前记录，以免把自研选择误写成官方兼容。工作量判断以这些尚未关闭的包和 45 个未通过条目为依据，不给没有证据的小时数或日数。
