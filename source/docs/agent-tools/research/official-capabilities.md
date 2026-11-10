# 官方 DevTools MCP 能力基准

核对日期：**2026-10-25（Asia/Shanghai）**。本文件定义官方目标和验收口径，不给本项目打完成率。联网证据在当日读取；页面自身更新日期、包发布日期和本机采集日期分别记录，不能互相替代。

## 目标与证据边界

本项目要对齐的是 **Avalonia DevTools MCP**，入口为本地 stdio 命令 `avdt mcp`，官方包为 `AvaloniaUI.DeveloperTools`；不是整个 Developer Tools 桌面产品，也不是所有 Avalonia AI 服务。[D1][D2]

官方 AI Tools 文档目前另有 Build MCP（文档、规则、迁移）、Charts MCP（图表生成）、Parcel MCP（打包签名）。它们是独立服务，不进入本次检查器分母。官网营销页仍写三个 MCP，AI Tools 文档已经列四个；以各服务的具体文档为准。[D2][D3][D4][D5][D6]

社区包 `Declarative.Avalonia.AgentTools` 的名称不能作为官方产品名；其工具、额外诊断和交互能力不自动成为本项目必须复刻的官方能力。[C1]

| 证据层 | 本轮已核实 | 不足以证明 |
| --- | --- | --- |
| 官方在线文档（D） | 当前公开工具名、用途、安装和运行前提 | 精确 JSON 输出格式、每个参数的运行时边界 |
| 官方发行说明（N） | 已发布版本和明确修复的 MCP 行为 | 其他未提及行为的逐项正确性 |
| 正常运行的 `initialize` / `tools/list`（R） | 2.2.3 初始会话的身份、参数 schema、默认值、枚举、注解 | 授权成功、目标连接成功、`tools/call` 成功 |
| 真实 `tools/call` 对照（尚缺） | 本文件没有这一级证据 | 不能把 schema 声明、工具存在或本项目测试通过写成原版运行行为确认 |

本轮唯一归档的原版采样为 [official-mcp-2.2.3-schema.json][R1]，2026-10-24T23:06:53.322625+00:00（本地 2026-10-25）。采集范围仅为 `initialize` 和 `tools/list`，没有目标附加、账户认证或授权修改。历史临时目录中的原始 schema 已不可用，后续应引用这份本轮归档，不能只引用旧摘要。

## 版本、平台和运行前提

| 项目 | 当前事实 | 依据与日期 |
| --- | --- | --- |
| 最新稳定工具 | `AvaloniaUI.DeveloperTools 2.2.3`，NuGet 发布于 2026-06-21；本轮读取时最新仍为该版本 | [N1]，2026-10-25 核对 |
| 目标桥包 | `AvaloniaUI.DiagnosticsSupport 2.2.3`，同日发布；包说明要求 Avalonia 11.2.0+ | [N2]，2026-10-25 核对 |
| 本机被采样身份 | `AvaloniaUI DeveloperTools`，`2.2.3+92df9d92acf0bbe8b2ffa96370c1bf848df81d27`；Windows x64 原生程序 SHA-256 为 `76a88bb2fd68d40a7b663a0f710c8bc18dc85bf9a2f732dc6f2f84c6ac0f9f08` | [R1]，2026-10-25 本地采样 |
| 工具宿主平台 | 官方安装文档列 Windows 10+、macOS 13+、Linux X11（glibc 2.27 / musl 1.2.2 兼容发行版），运行要求 .NET 6+ | [D7]，页面更新 2026-10-06 |
| 安装包区别 | .NET SDK 10+ 使用无平台后缀工具包；旧 SDK 使用 `.Windows` / `.macOS` / `.Linux`。通用工具包的 NuGet TFM 是 net10.0 | [D7][N1]，2026-10-25 核对 |
| 目标平台边界 | DiagnosticsSupport 文档声明兼容 Browser、Android、iOS；这不证明每项 MCP 工具和自动发现都在这些平台等价可用 | [D7][N2][D8]，2026-10-25 核对；MCP 跨平台矩阵未知 |
| MCP 前提 | 已安装 DevTools；官方文档要求有效 Plus 许可，Free / Community 不含 MCP | [D1]，页面更新 2026-10-06 |
| 授权环境变量 | 当前 MCP 文档列 `AVALONIA_TOOLS_LICENSE_KEY`；Avalonia 11.x 或更早列旧变量 `ACCELERATE_LICENSE_KEY` | [D1]，2026-10-25 核对 |
| 运行应用 | 安装 DiagnosticsSupport，并在启动时调用 `WithDeveloperTools()` 或 `AttachDeveloperTools()`；目标已经完成启动 | [D1]，2026-10-25 核对 |
| 独立 XAML 预览 | `attach-to-file` 不要求用户先运行 app，但项目仍需 DiagnosticsSupport；官方示例承诺修改 XAML 后可继续预览而不重新构建 | [D1]，2026-10-25 核对 |
| F12 | MCP 文档明确 F12 对 MCP 连接无效；安装说明中的 F12 是桌面 DevTools 入口，不能用来替代 MCP 前提 | [D1][D7]，2026-10-25 核对 |

DiagnosticsSupport 的 NuGet 当前实际框架资产列 net8.0、net10.0，而 README 仍说明使用 .NET Standard 2.0 兼容 API；“API 兼容性说明”不等于包包含 netstandard2.0 目标资产。[N2]

2.2.2 发行说明把 `AVALONIA_TOOLS_LICENSE_KEY` 的离线认证新增项标为 Enterprise；当前 MCP 文档则以 Plus key 配置该变量。两者可能讨论不同认证路径，本轮不推导所有订阅和离线状态的许可矩阵，也不测试认证。[N1][D1]

## 公开工具与精确输入声明

当前具体 MCP 文档公开 **16 项**；本轮 2.2.3 **未附加目标的初始目录声明 15 项**。唯一目录差异是 `detach`。服务声明 `tools.listChanged=true`，所以应保留“可能随会话变化、尚未确认”的状态；不能断言 2.2.3 永远没有 `detach`。[D1][R1]

下面每一行的工具用途来自当日读取的 [D1]；参数类型、必填标记、默认值和枚举来自本地采样 [R1]。`*` 为 schema 必填；`?` 表示可省略，不能理解为同时允许 JSON null。`string[]?` 在这份 schema 中实际允许数组或 null，数组成员也允许 string 或 null。

| 工具 | 精确输入（2.2.3 初始 schema） | 公开行为 / 返回内容类别 | 返回结构仍未知 |
| --- | --- | --- | --- |
| `attach-to-app` | `id?: integer = 0` | 发现 / 连接运行应用；多应用时供选择 | 应用列表字段、id 含义、单应用是否自动连接 |
| `attach-to-file` | `xamlFilePath*: string`, `xamlProjectPath*: string`, `build?: boolean = false` | 打开文件对应的预览器；`build` 表示启动前构建项目 | 路径解析、构建输出、会话和预览错误结构 |
| `detach` | **本次没有 schema** | 断开当前应用或预览器 | 参数、何时可发现、连接及进程清理细节 |
| `tree` | `nodeId?: integer = 0`, `treeKind?: string ∈ {Logical, Visual, Merged}`（schema default 为数字 `0`）, `depth?: integer = 10` | 根 / 子树；depth=1 为直接子项，-1 为完整子树 | 节点字段、稳定性、未加载和虚拟化节点规则 |
| `ancestors` | `nodeId*: integer` | 祖先链 | 链顺序、是否包含自身、字段 |
| `search` | `query*: string`, `scopeNodeId?: integer = 0`, `searchTemplates?: boolean = false`, `limit?: integer = -1` | 按类型或 x:Name 部分匹配并可限定范围 / 数量 | 大小写、排序、类型派生匹配、模板边界 |
| `props` | `nodeId*: integer`, `propertyNames?: array<string or null> or null = null` | 属性值；可按名称过滤 | 属性身份、值表示、来源、绑定和异常字段 |
| `set-prop` | `nodeId*: integer`, `propertyName*: string`, `value*: string`, `isXamlValue?: boolean = false` | 设置属性；特殊文字 `null` / `unset`；可传带 xmlns 的 XAML 值 | 转换范围、附加 / CLR 属性、优先级及绑定恢复 |
| `styles` | `nodeId*: integer`, `propertyNames?: array<string or null> or null = null`, `includeDefaults?: boolean = false` | 应用样式 / setter，可筛属性并包含默认值 | 生效 / 未生效 setter、模板、值帧表示 |
| `pseudo-class` | `nodeId*: integer`, `pseudoClass?: string or null = null`, `isActive?: boolean or null = null` | 省略名称列举；给名称激活 / 取消 | 有无冒号、isActive 省略的动作、返回状态 |
| `resources` | `nodeId?: integer = 0`, `onlySelf?: boolean = false` | 应用或节点资源；onlySelf 限节点自身 | 覆盖解析、主题字典、合并字典和返回层级 |
| `assets` | 无参数，object schema 的 properties 为空 | 列出内嵌图片、字体等资产 URL | URL 列表结构、程序集 / 资源范围 |
| `open-asset` | `assetUrl*: string` | 下载 `assets` 返回的 `avares://` 资产 | 二进制 / 文本内容块、MIME 和大小处理 |
| `screenshot` | `nodeId*: integer` | 指定元素的 PNG | MCP image / 文本 / 文件返回、像素尺寸、DPI 和裁切 |
| `input` | `nodeId*: integer`, `inputEvent*: string ∈ {Click, KeyDown, KeyUp, Text}`, `arg?: string or null = null` | 向节点发输入；Text / Key 通过 arg 给文字或键 | 键语法、修饰键、焦点、路由 / 原生输入差异 |
| `action` | `nodeId*: integer`, `action*: string ∈ {Focus, Enable, Disable, BringIntoView}` | 四种指定操作 | 成功结果、异步布局、不可用节点语义 |

15 项均未声明 `outputSchema`。因此本表有意只定义“输出内容类别”，不能声称已经掌握原版返回 DTO 或字节级兼容协议。[R1]

注解也属于契约：9 项检查工具 `props`、`ancestors`、`styles`、`screenshot`、`assets`、`open-asset`、`resources`、`tree`、`search` 声明只读 / 非破坏性；`input`、`set-prop`、`action`、`pseudo-class` 声明非只读 / 破坏性；`attach-to-app` 声明非只读 / 非破坏性；`attach-to-file` 未给 annotations。15 项均有 `execution.taskSupport=optional`。这些是服务提示，不能替代行为或安全验证。[R1]

## 已知差异与不可擅自填补的语义

| 编号 | 证据差异（均在 2026-10-25 核对） | 验收处理 |
| --- | --- | --- |
| U01 | [D1] 有 `detach`，[R1] 初始目录没有 | 文档目标保留它；调用和动态目录仍待原版有目标会话验证 |
| U02 | `tree` 文案说 null 返回 roots，但 schema 的 nodeId 是不可空 integer、默认 0 | 基础客户端用省略 / 0；JSON null 是否接受单独标未知 |
| U03 | `treeKind` 是字符串枚举，schema default 却是数字 0 | 显式三种枚举是确定契约；省略时选哪种仍待调用 |
| U04 | [D1] 的 ancestors 描述方向是节点到根，[R1] 写从根开始 | 确认链成员与实际顺序前，不能把任一方向当原版已证实 |
| U05 | `set-prop.value` schema 是 string；文字特殊值与 JSON null 是不同输入 | 已知目标为字符串 `null` / `unset`；两者对绑定 / local value 的区别待对照 |
| U06 | `attach-to-app` 工具总描述使用 processId 字样，真正参数名却是 `id`，参数说明称 app identifier | 必须接收 `id`；不据文案把它当操作系统 PID |
| U07 | `pseudoClass` schema 例子无冒号，网页示例使用 `:pointerover` | 接受哪种拼写、是否规范化仍待调用；不能凭名称推定 |
| U08 | `attach-to-file.build=false` 明确公开，但官网没有说明自动选择配置 / TFM / output 路径算法 | 必须支持显式 build 开关；构建和预览上下文不得仅因工具同名就算相符 |

本轮初始化指令另外要求：tree / search 得到的 nodeId 用于后续工具；附加 XAML 修改后应重新获取树 / 搜索，不继续使用旧节点身份；运行 app 优先 attach-to-app，隔离预览 XAML 用 attach-to-file。它没有给出跨树、跨会话或对象回收时 nodeId 的全部生命周期。[R1]

## 可统计的验收项

下表是后续实现和测试的**固定目标条目**，当前全部未在本文件判定项目完成情况。`D` / `R` / `N` 对应上述证据层；“待对照”意味着应记录原版实测结果，不能用自定义行为填空。每行是一个验收单元，不能按一个同名工具全部勾选。每个工具至少要同时通过输入契约、核心行为和相关边界。

| ID | 工具 / 范围 | 验收条件 | 证据 |
| --- | --- | --- | --- |
| C01 | MCP 宿主 | 独立进程走 stdio，完成 initialize 与 tools/list；工具调用不依赖桌面窗口操作 | [D1]、[R1] |
| C02 | 工具声明 | 逐项核对上述 15 项的名字、必填、类型、默认、enum；detach 单列，不拿额外工具数量抵扣缺项 | [R1]；[D1] |
| C03 | 会话目录 | 明确记录未附加 / 已附加的 tools/list 差异和 listChanged；原版何时出现 detach 待对照 | [R1]、U01 |
| C04 | 目标准备 | 验证桥包和启动调用的缺失路径；MCP 连接不要求用户按 F12 | [D1] |
| C05 | 版本诊断 | DevTools / DiagnosticsSupport 不匹配时给有用诊断；原版消息和触发条件待对照 | [N1]（2.2.1） |
| C06 | 节点生命周期 | XAML 内容更新后重新 tree/search；旧节点不得静默操作另一个对象，原版具体错误待对照 | [R1] 初始化指令 |
| C07 | 参数失败 | 错误 enum / 无效参数能向 agent 报告，覆盖 set-prop、props、input；不要求凭空复制未知错误字符串 | [N1]（2.2.1） |
| C08 | 元信息 | 核对只读 / 破坏性提示和任务支持；声明成功不计工具行为成功 | [R1] |
| APP01 | attach-to-app | 省略 id / 默认 0 的发现路径；多 app 的列表和选择行为真实可用 | [D1]、[R1] |
| APP02 | attach-to-app | 用返回的 app id 附加真实运行目标，后续检查确实来自该 app | [D1]、[R1] |
| APP03 | attach-to-app | 两个 app 间按 id 选择 / 切换并检查节点会话边界；id 不预设为 PID | [D9]、[R1]、U06 |
| FILE01 | attach-to-file | 接受且正确关联 xamlFilePath、xamlProjectPath，两个官方必填名均保留 | [R1] |
| FILE02 | attach-to-file | 省略 build / false 时不主动构建；从现有项目上下文启动预览，实际失败边界待对照 | [R1] |
| FILE03 | attach-to-file | build=true 先构建再启动预览；构建失败不能伪装成已连接 | [R1] |
| FILE04 | attach-to-file | 没有用户预先运行的 app 也能预览项目内文件，使用真实设计期项目上下文 | [D1] |
| FILE05 | attach-to-file | 连接保持时修改 XAML 可更新预览 / 截图，无需每次重建；更新后重取 nodeId | [D1]、[R1] |
| DET01 | detach | 当前应用 / 预览器断开后会话不可继续误用；参数、广告时机、子进程处置待原版对照 | [D1]、U01 |
| TREE01 | tree | 省略 / 0 返回 roots；普通 nodeId 返回该节点子项 | [D1]、[R1]、U02 |
| TREE02 | tree | Logical、Visual、Merged 三种显式模式确有不同的树算法 | [D9]、[R1] |
| TREE03 | tree | depth 默认 10、1 直接子项、-1 完整子树；不是接收参数后忽略 | [R1] |
| TREE04 | tree | tree 返回的节点能被 props / screenshot / action 等消费；默认树模式及 null 输入单列待对照 | [R1]、U02、U03 |
| ANC01 | ancestors | 返回真实父链；根节点 / 中间节点均覆盖，自身包含规则和顺序待对照 | [D1]、[R1]、U04 |
| SEARCH01 | search | 同时按类型、x:Name 部分匹配；不只支持一种查询 | [D1]、[R1] |
| SEARCH02 | search | scopeNodeId=0 / 省略搜索全范围，指定节点确实限制范围 | [R1] |
| SEARCH03 | search | searchTemplates=false / true 确实影响模板搜索；含模板控件样本验证 | [R1] |
| SEARCH04 | search | limit 默认 -1、显式上限生效；默认 / 排序 / 大小写仍待对照 | [R1] |
| PROPS01 | props | 必填 nodeId 返回实际属性值；有样式值、绑定值、null 值样本 | [D1]、[R1] |
| PROPS02 | props | propertyNames 省略 / null 取全量，显式列表过滤；数组内 null 行为待对照 | [R1] |
| SET01 | set-prop | 正确消费 nodeId、propertyName、string value；设置后 props 和 UI 可观察到变化 | [D1]、[R1] |
| SET02 | set-prop | 字符串 `null` 在可空属性生效；覆盖 2.2.2 修复场景 | [R1]、[N1]（2.2.2） |
| SET03 | set-prop | 字符串 `unset` 清除值；绑定 / 样式恢复的具体规则须记录对照 | [D1]、[R1]、U05 |
| SET04 | set-prop | isXamlValue=false 普通值转换；true 接受含 xmlns 的 XAML 对象值，不能忽略此开关 | [R1]；[D9] |
| STYLES01 | styles | 返回真实应用样式和 setter，含模板 / 样式提供的值 | [D1]、[R1] |
| STYLES02 | styles | propertyNames 的省略 / null / 名单过滤均有行为 | [R1] |
| STYLES03 | styles | includeDefaults=false / true 确实改变默认值结果 | [R1] |
| PSEUDO01 | pseudo-class | 省略 pseudoClass 列出该节点可用伪类，而非固定通用列表 | [D1]、[R1] |
| PSEUDO02 | pseudo-class | isActive=true / false 实际改变命名伪类并影响样式 | [D1]、[R1] |
| PSEUDO03 | pseudo-class | 可空参数 / 省略 isActive / 有无冒号的行为明确记录，待原版对照 | [R1]、U07 |
| RES01 | resources | 省略 nodeId / 0 返回 Application 资源 | [D1]、[R1] |
| RES02 | resources | 指定节点时实际按该 scope 查询，含遮蔽同名资源样本 | [D1]、[R1] |
| RES03 | resources | onlySelf=false / true 的范围区别真实生效；继承 / 合并 / 主题细节待对照 | [R1] |
| ASSET01 | assets | 无参列出目标应用内嵌资产，图片和字体都得到可用 URL | [D1]、[R1] |
| OPEN01 | open-asset | assets 生成的 avares URL 能取回原始资产；内容格式及错误待对照 | [D1]、[R1] |
| SHOT01 | screenshot | 必填 nodeId 抓取该元素 PNG，验证图像确为目标节点内容；尺寸 / DPI / MCP 内容块待对照 | [D1]、[R1] |
| INPUT01 | input | Click 确实触发目标控件点击行为，覆盖官方 2.2.1 修复的场景 | [R1]、[N1]（2.2.1） |
| INPUT02 | input | KeyDown + arg 发出按键按下；可观察目标响应，键格式待对照 | [R1] |
| INPUT03 | input | KeyUp + arg 发出按键释放；不能只映射 KeyDown | [R1] |
| INPUT04 | input | Text + arg 使可编辑控件接收文字；焦点、组合和路由语义待对照 | [R1] |
| ACT01 | action | Focus 实际改变焦点，后续输入作用于预期控件 | [D9]、[R1] |
| ACT02 | action | Enable 实际启用目标 | [D9]、[R1] |
| ACT03 | action | Disable 实际禁用目标 | [D9]、[R1] |
| ACT04 | action | BringIntoView 使滚动容器中的离屏目标可见 | [D9]、[R1] |

统计规则：工具级分母为文档目标 16 项，声明级另报初始 15 项的匹配情况；本表 52 条是细化验收条目。条目状态建议使用“未实现 / 已实现未验证 / 部分通过 / 已验证 / 原版语义未知”。同名工具或 schema 相同只能计声明相符，真实行为需测试证据；未知不能计通过，也不能悄悄删除来提高比例。额外社区工具、桌面面板、3D、profiler、断点、设计器等不进入该分母。

## 与 MCP 有关的官方发布记录

| 版本 / 发布时间 | 对本目标有用的变化 | 依据 |
| --- | --- | --- |
| 2.2.0-beta2 / 2026-01-23 | 2026-01-27 发布博文介绍 MCP；列三种树、属性 / XAML 值、伪类、资源资产、截图和四种 action | [N1][D9] |
| 2.2.0 / 2026-04-07 | MCP 正式可用；兼容 Avalonia 12，增加日志级别控制 | [N1] |
| 2.2.1 / 2026-04-14 | enum 校验、错误信息、版本不匹配诊断；修正 Plus / Indie 认证、Click 未发事件、set-prop / props / input 校验 | [N1] |
| 2.2.2 / 2026-06-02 | 修正 set-prop 可空值；其余多为桌面 / 连接可靠性变化，不据此新增工具 | [N1] |
| 2.2.3 / 2026-06-21 | 登录帮助和 macOS F12 修复；没有声明新增 MCP 工具 | [N1] |

## 来源索引

所有联网来源读取于 **2026-10-25**。下列 URL 指向具体规范 / 包 / 博文，不以搜索摘要或营销标题替代具体工具契约。官网未公开的行为在上文保持未知。

| 编号 | 精确来源 | 发布 / 更新日期 | 用途 |
| --- | --- | --- | --- |
| D1 | [DevTools MCP](https://docs.avaloniaui.net/tools/developer-tools/mcp) | 页面更新 2026-10-06 | 16 工具、许可、桥包、预览器、F12 |
| D2 | [AI tools](https://docs.avaloniaui.net/tools/ai-tools/) | 页面更新 2026-10-06 | 四类独立 MCP 产品 |
| D3 | [Avalonia AI](https://avaloniaui.net/ai) | 页面未给可靠发布日期 | 营销页仍为三类且工具列举不全 |
| D4 | [Build MCP](https://docs.avaloniaui.net/tools/ai-tools/build-mcp) | 本轮不以未记录页面更新时间作版本依据 | 文档 / 迁移服务的范围 |
| D5 | [Charts MCP](https://docs.avaloniaui.net/tools/ai-tools/charts-mcp) | 本轮不以未记录页面更新时间作版本依据 | 图表生成服务的范围 |
| D6 | [Parcel MCP](https://docs.avaloniaui.net/tools/parcel/mcp) | 本轮不以未记录页面更新时间作版本依据 | 打包 / 签名服务的范围 |
| D7 | [Installing the Avalonia Plus developer tools](https://docs.avaloniaui.net/tools/developer-tools/installation) | 页面更新 2026-10-06 | 系统要求、SDK / 包区别、桥包安装 |
| D8 | [Attaching applications](https://docs.avaloniaui.net/tools/developer-tools/attaching-applications) | 页面更新 2026-10-06 | Browser / Mobile / WSL2 的一般 DevTools 连接边界 |
| D9 | [Avalonia DevTools MCP Server](https://avaloniaui.net/blog/avalonia-devtools-mcp-server) | 发布 2026-01-27 | 首发行为范围和四种 action |
| N1 | [AvaloniaUI.DeveloperTools 2.2.3 / Release Notes](https://www.nuget.org/packages/AvaloniaUI.DeveloperTools/2.2.3#release-notes-body-tab) | 包发布 2026-06-21 | 最新稳定版、各版 MCP 修复 |
| N2 | [AvaloniaUI.DiagnosticsSupport 2.2.3](https://www.nuget.org/packages/AvaloniaUI.DiagnosticsSupport/2.2.3) | 包发布 2026-06-21 | 桥包版本、实际 TFM 与 README 说明区别 |
| R1 | [本轮官方进程 initialize / tools/list](official-mcp-2.2.3-schema.json) | 2026-10-25 本地采样 | 完整 15 工具输入 schema 和初始化指令；非 tools/call |
| C1 | [AvaloniaCommunity AgentTools 文档](https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/master/docs/agent-tools.md) | 本轮用于名称归属，未以移动分支固定实现版本 | 独立社区产品名称边界 |

[D1]: https://docs.avaloniaui.net/tools/developer-tools/mcp
[D2]: https://docs.avaloniaui.net/tools/ai-tools/
[D3]: https://avaloniaui.net/ai
[D4]: https://docs.avaloniaui.net/tools/ai-tools/build-mcp
[D5]: https://docs.avaloniaui.net/tools/ai-tools/charts-mcp
[D6]: https://docs.avaloniaui.net/tools/parcel/mcp
[D7]: https://docs.avaloniaui.net/tools/developer-tools/installation
[D8]: https://docs.avaloniaui.net/tools/developer-tools/attaching-applications
[D9]: https://avaloniaui.net/blog/avalonia-devtools-mcp-server
[N1]: https://www.nuget.org/packages/AvaloniaUI.DeveloperTools/2.2.3#release-notes-body-tab
[N2]: https://www.nuget.org/packages/AvaloniaUI.DiagnosticsSupport/2.2.3
[R1]: official-mcp-2.2.3-schema.json
[C1]: https://github.com/AvaloniaCommunity/Avalonia.Markup.Declarative/blob/master/docs/agent-tools.md
