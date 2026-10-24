# 测试与手工验证

测试覆盖协议、目标应用服务、Inspector、stdio MCP 和独立预览器。统计必须与实际执行的代码范围和配置对应；单个功能的隔离通过不等于扩展后的完整回归已经通过。

## 构建与运行

在仓库根目录执行：

```powershell
dotnet build AvaloniaDevTool.slnx
dotnet test AvaloniaDevTool.slnx
dotnet build AvaloniaDevTool.slnx --configuration Release
```

| 项目 | 验证范围 |
| --- | --- |
| Protocol.Tests | 分帧读取、请求关联、取消、断连、版本检查和帧长度边界 |
| Agent.Tests | 真实 Avalonia 控件、UI 线程调度、弱引用、编辑恢复、采集与服务边界 |
| Inspector.Tests | AXAML 页面、ViewModel、草稿保留、异步选择代数、管道和诊断页交互 |
| Cli.Tests | SDK stdio 客户端启动服务器、initialize、tools/list／tools/call 和真实 Agent 调用 |
| Previewer.Tests | classless／项目 XAML、程序集校验、文件监视、Window 生命周期、错误恢复与 Agent 接入 |
| Backends.XamlMcp.Tests | 公开 NuGet Server／Agent、独立目标进程、快照身份、只读映射和进程清理 |
| Remote.Tests | 真实 loopback Kestrel／ClientWebSocket、认证／Origin、共享会话与断开清理 |
| AotSmoke | 独立控制台验证程序，启动实际发布的 trimmed／Native AOT 消费目标；不计入 xUnit TRX 用例数 |

Headless 场景按测试选择绘图后端。像素和 PNG 断言来自启用 Skia 的测试，不能把仅加载 AXAML 或检查 ViewModel 的用例当作桌面视觉验收。测试使用自建指标、可控控件及临时文件，避免依赖系统恰好产生某个事件。

## Phase 8 验证范围

Phase 8 最终源码在 Windows 11、.NET SDK 10.0.400、Avalonia 12.1.4 上完成 **476／476 项通过、无跳过**。七份 `artifacts/phase8-verified-results/phase8-verified*.trx` 的 XML 合计 total／passed 均为 476、failed／notExecuted 均为 0，命令退出码 0。加入 AotSample／AotSmoke 后，全部 22 项目 Debug／Release 完整构建均退出码 0、0 警告、0 错误；普通构建不代表 Native AOT 严格发布通过。

| 项目 | 最终统一通过数 |
| --- | --- |
| Protocol.Tests | 26 |
| Agent.Tests | 269 |
| Inspector.Tests | 114 |
| Cli.Tests | 22 |
| Previewer.Tests | 15 |
| Backends.XamlMcp.Tests | 19 |
| Remote.Tests | 11 |
| 最终完整同批次 | 476 项通过，无跳过 |

最终命令：`dotnet test AvaloniaDevTool.slnx --no-restore --logger 'trx;LogFilePrefix=phase8-verified' --results-directory artifacts/phase8-verified-results -v minimal`；完整 Debug／Release 构建分别使用 `dotnet build AvaloniaDevTool.slnx --no-restore` 和 `dotnet build AvaloniaDevTool.slnx --no-restore --configuration Release`。下表新增方法与用例已纳入该统一批次，专项及历史批次不叠加。

| 专项 | 新增测试方法 | Theory 展开用例 | 证据范围 |
| --- | --- | --- | --- |
| Protocol JSON／AOT metadata | 3 Protocol | 3 | 公开 record 四种 wire 形状、Web／string enum 合同、envelope／嵌套错误 |
| 上下文附加属性 | 3 Agent＋1 Inspector＋1 CLI | 5 | 四类直接视觉父布局、未知保留、换父通知、真实复选框与隐藏草稿、stdio 旧默认／显式参数 |
| 直接子项分页／模板父入口 | 6 Agent＋4 Inspector＋2 CLI | 12 | 2305 项完整续页、游标变更／release、上限、真实按钮／剪贴板、模板父到部件路径、目标自身匹配，以及会话能力探测／成功后不降级 |
| 弹层冻结 | 12 Agent＋1 Inspector＋1 CLI | 19 | Native／Overlay 真实外部输入、所有权、绑定／同值改写、嵌套／新根、卸载、断连与弱回收；保留独立关闭限制 |
| 绑定文化／格式／源更新 | 8 Agent＋1 Inspector | 19 | 普通／Compiled／Multi／Template 文化与实际转换／格式、四种 TextBox 源更新时机、五种字段状态与 AXAML 子表达式 |
| 本轮新增源码合计 | 43 | 58 | 精确方法名见 [T393–T435](implementation-status.md#测试名称索引)；此表不是完整回归通过数 |

上下文属性专项 5／5 通过；绑定专项 Agent 与既有 BindingDetails 共 27／27、GUI 9／9、XamlMcp 映射 13／13 通过。这些隔离批次保留各自范围，不能再次相加。分页及弹层专项的准确合同见 [树工作流](tree-workflows.md) 与 [弹层冻结](popup-freeze.md)，文化与上游缺口见 [绑定诊断](binding-diagnostics.md)。

首轮 `artifacts/phase8-final-results/phase8-final*.trx` 的七份 XML 合计 474 项、470 通过／4 失败、无跳过，Inspector 为 108／112，其余六项目均通过。三个旧竞态因测试传输未实现新分页方法而留下占位节点、未继续高亮；修复为会话级未知／支持／旧版缺失能力，只在未知时遇明确缺方法错误回退，完整本地分页检查已加载前缀，原生成功后不再降级。三个旧断言保持并转绿，另新增 T434／T435 确认只探测一次及成功后不降级。上下文用例则复现完整刷新先清空选择导致属性行／草稿丢失，改为按 Native 稳定 ID 保留行至新树定位确定，并强化同一真实刷新断言后红转绿。该 474 项红批次不代表修复后源码的最终结果。

最小消费程序已分别完成 trimmed／JIT 与真实 Native AOT 发布后的独立进程 smoke，覆盖实际管道握手 PID、Visual／Logical／Merged、首分页、上下文属性、弹层无活动弹窗时的只读根状态、属性读取／覆盖／通知／恢复、520×280 PNG、框架路由拾取和断连清理。Native AOT 目标报告动态代码支持／已编译均 False；trimmed 目标均 True。Smoke 不是 xUnit 测试，不并入上表或 TRX 总数。

两次消费程序发布均使用保留所有诊断的 `-AllowCompatibilityWarnings` 证据模式；默认严格 Native AOT 发布实际 exit 1，编译阶段 46 条 IL error。Native AOT／trimmed 的编译与发布分析输出分别合计 87／85 条警告行，同源位置可能重复，不能解释为独立缺陷数。Protocol 独立兼容分析零警告、零错误及专项 26／26 通过，不能据此声明整个 Agent 无警告 AOT 兼容。复现命令、实际进程与产物哈希见 [AOT 消费验证](native-aot.md)。

### Phase 8 最终桌面观察与清理

本轮使用实际 Windows Sample／Inspector 默认窗口，观察范围如下；Headless 指针、真实 stdio 与 AOT 消费进程仍各自保留证据层级。

- 上下文属性：默认 Inspector 中“仅相关附加属性”复选框可见。选中 CultureFrench 时，开启为 173／182 属性，关闭后 182／182，再开启回到 173／182。“分页与模板导航”折叠入口可见，但本轮桌面没有实际翻完 2305 子项，该范围仍由自动化证明。
- 文化与写源：Sample 第 12 区实际显示 fr-FR 的 `12 345,67` 与 en-US 的 `12,345.67`。聚焦 LostFocus 输入并改为 `13000,5` 时，两份文本仍保留旧值；点击只读管道框转移焦点后，分别变为 `13 000,50` 与 `13,000.50`，证明本次真实焦点转移后的写源与格式差异。
- 绑定字段：Inspector 样式绑定页可滚动到实际 `ConverterCulture=fr-FR`、文化来源 `Configured`、`StringFormat={0:N2}`。该 OneWay TextBlock 显示的有效触发器为 PropertyChanged，只记录字段证据，不据此称该目标会写源；实际 LostFocus 行为由上条输入区操作证明。
- 弹层冻结：先对全部配置根开启保持，再打开标准 Native Flyout，实际点击弹层外部后仍开；切换到 Inspector 时仍记录 Native Popup／Flyout #61 已抑制外部点击／失焦。点击“全部恢复”后弹层曾仍开，随后在 Sample 再次外点，实际观察到 Flyout 关闭。关闭结论来自最后的外点操作和画面，不来自恢复时零弹层的状态值。H01 继续为 P，本轮未手验 ContextMenu、ToolTip 或 transient 的更多关闭路径。
- 清理：GUI Disconnect 后 Sample 显示等待连接，弹层和诊断覆盖均消失。核对可执行文件路径后，本轮自有 PID 16900／2268 均已停止；`artifacts/smoke-v8/sample.stderr.log` 与 `inspector.stderr.log` 均为 0 字节。最终桌面观察后未再修改源码，普通 22 项目双配置零警告构建与 Native AOT 严格发布的 46 条 IL error 继续分别记录。

## Phase 7 历史冻结范围

Phase 7 最终源码在 Windows 11、.NET SDK 10.0.400、Avalonia 12.1.4 上完成 **418／418 项，无跳过**。七份 `artifacts/phase7-desktop-results/phase7-desktop*.trx` 的 XML 合计 total／passed 为 418、failed／notExecuted 为 0，命令退出码为 0；随后 Debug／Release 全部 20 项目均退出码 0、0 警告、0 错误。

| 项目 | 通过数 |
| --- | --- |
| Protocol.Tests | 23 |
| Agent.Tests | 229 |
| Inspector.Tests | 103 |
| Cli.Tests | 18 |
| Previewer.Tests | 15 |
| Backends.XamlMcp.Tests | 19 |
| Remote.Tests | 11 |
| 最终完整同批次 | 418 项通过，无跳过 |

最终命令：`dotnet test AvaloniaDevTool.slnx --no-restore --logger 'trx;LogFilePrefix=phase7-desktop' --results-directory artifacts/phase7-desktop-results -v minimal`，随后分别完整构建 Debug 和 Release。

本轮新增 43 个测试方法（T350–T392）：画刷 3 Agent＋3 Inspector，指标 6 Agent＋4 Inspector，源断点 8 Agent＋3 Inspector＋1 Protocol＋1 CLI，覆盖层 5 Agent＋1 Inspector，协议监视 5 Protocol＋2 Inspector，以及 1 CLI 真实 stdio 画刷／指标链。方法数不等于 Theory 展开后的用例数，不与历史通过数拼接。

首轮 `artifacts/phase7-final-results` 有 416 项，其中 Inspector 99／101：既有 11 主标签断言更新为 12 并增加协议页断言；源断点页快照 Clear 触发 TwoWay 选择暂空、禁用 Update 并取消指针命令，改为按 ID 原位同步且新增确定性回归。后续 `artifacts/phase7-verified-results` 的 417 项阶段回归全过，再由真实指针回归发现指标刷新重建 Clear 按钮会吞掉点击，已按 ID 保留图行、选择与命令。该回归先红（按钮被替换、点击 0、样本未清）后绿，最终已纳入上述 418 项。之后桌面发现默认窗口中统计标签被固定图区域挤出，MetricsView 改为整页滚动并保留 280 px 统计区，专项 5／5 通过；该布局源码已重新执行上述 418 项和双配置构建。`phase7-current` 的 418 项、416／417 和 Phase 6 的 375 均是历史，不代表最新源码。

- 画刷：真实管道与 Skia 验证 Solid／Linear／Radial／Conic 字段／像素、非法输入、原实例不变与最新绑定恢复；真实 AXAML 验证色标、类型、坐标／半径／角度草稿和刷新保留。P05 仍是部分实现，未支持画刷保持只读。
- 指标：实际 Meter 与可控时间验证真实窗口、静默过期、停止冻结、帧／原始分位、容量分离和单图清空；真实定时器验证轮询／通知／显示频率，GUI 验证 P90、公共时间轴及旧 pull／通知隔离；另有真实 MouseDown→强制快照刷新→MouseUp，验证按钮身份及服务端清空结果。
- 断点：独立 Source／receiver、旧 ObjectId 合同、同参数连续／嵌套／递归 Raise、源弱回收，以及真实页面和 stdio 定义／更新／禁用／计数／一次性移除。
- 覆盖层：TextBlock／TextBox 实际框架布局、密码跳过、Bitmap 像素／DPI／DIP 与未知源边界；真实旋转／缩放分区像素和 BoxModel 指针／快速切区／隐藏恢复／清空／断连。
- 协议：真实双向管道相同 ID、错误／通知、取消迟回、类型解码失败、Unicode JSON 与双重预算；真实页面录制握手／失败 RPC、过滤／停止／清空和默认元数据／重连关联。正文默认关闭，Native 本地录制不扩展为 MCP stdio 或原版私有协议。

专项细节见 [属性](properties.md)、[指标](metrics.md)、[断点](breakpoints.md)、[覆盖层](overlays.md)、[协议监视](protocol-monitor.md)。本轮自动化及下述桌面观察不扩展为多 DPI、跨平台、实例 handler 归因、原版私有算法或属性预览取消／Bitmap 释放专项已经完成。

## Phase 6 历史冻结范围

2026 年 10 月 25 日，Phase 6 在 Windows 11、.NET SDK 10.0.400、Avalonia 12.1.4 上实际完整执行解决方案测试：

| 项目 | 通过数 |
| --- | --- |
| Protocol.Tests | 17 |
| Agent.Tests | 207 |
| Inspector.Tests | 90 |
| Cli.Tests | 16 |
| Previewer.Tests | 15 |
| Backends.XamlMcp.Tests | 19 |
| Remote.Tests | 11 |
| 最终完整同批次 | 375 项通过，无跳过 |

最终 375 项对应源码的 Debug 和 Release 全部 20 项目构建均为 0 警告、0 错误、退出码 0。七份 TRX 在 artifacts/phase6-verified-results，前缀 phase6-verified；XML 合计 total／passed 均为 375，failed／notExecuted 均为 0，已包含选择命令状态、显示列宽及原生 popup 发现修正。前一阶段 374 项产物在 artifacts/phase6-final-results；374、323、319、266 及更早结果是历史，不与隔离计数相加。Inspector 使用真实 Skia，但只有实际像素断言证明像素行为，VM 或加载 AXAML 不能替代桌面验收。

最终命令：`dotnet test AvaloniaDevTool.slnx --no-restore --logger 'trx;LogFilePrefix=phase6-verified' --results-directory artifacts/phase6-verified-results -v minimal`，随后分别完整构建 Debug 和 Release。

### 真实桌面与进程证据

- Native：Sample 中按 F12 实际打开并连接 Inspector；既有拾取、属性修改和绑定恢复链继续保留。
- 指标：sample.duration 的 6 个已知合成样本实测 mean=34.5、P50=18、P95=95，并观察图表更新；这是保留窗口统计，不是完整 Profiler。
- Previewer：可见独立宿主保存后自动完成第 2 次加载；无效 AXAML 保留旧画面，修正后第 3 次恢复。
- XamlMcp：真实可见目标仅接入公开 NuGet Agent，不引用本项目 Agent；经 Inspector 外部模式读取树、只读属性和带缺口的样式信息，取得选中元素 1064×49 的原始 PNG。
- 外部快照：刷新后清除旧选择和截图，未沿用旧句柄；detach 后本实例 ServerLauncher 退出且无后代，目标应用仍运行。最后仅关闭本轮自行启动的窗口和进程。

本次 V3 桌面专项使用本机真实 Sample 和 Inspector：

- Merged：选择“模板 · Template”显示合成组说明，禁用对象读取；跳转模板所属控件后恢复真实对象成员。BoundText 搜索定位保留真实 ID。
- 绑定：BoundText 显示一个真实 BindingExpression，字段表可见 SampleViewModel 来源、DataContext、BoundMessage 路径和 OneWay；未提供的 RelativeSource 声明明确显示缺口。
- 对象：实际从控件进入 DataContext，显示业务 SampleViewModel 的 14 个成员及面包屑；50／122 成员分页亦已观察。复杂集合／错误／弱回收由自动化覆盖，不把它们写为桌面已验。
- Profiler：Sample 按钮实际创建／移除控件、切换类、查找已有和缺失资源；第一轮观察到 55 分组、192 条真实框架活动、微秒统计与无回调错误，停止后保留窗口。此数字只描述该次操作，不是固定性能基准。
- 布局：主 11 标签收紧后同一行，连接后隐藏已禁用的发现列表；最大化窗口中对象页分页完整可见。3D 画布和层列表左右分栏无重叠，128 节点／12 层预算截断明确显示；仍为 VisualGeometry，不是逐层位图。
- 初始窗口：追加工作区位置约束后，重新启动并连接的非最大化 Inspector 标题、全部标签和底部状态栏完整可见。随后从界面关闭 Inspector，Sample 显示“等待 Inspector 连接”，再关闭该 Sample；本轮自行启动的窗口均已清理，日志无 stderr 输出。

2026 年 10 月 24 日的基础桌面链路还验证过 BoundText 拾取定位、高亮、覆盖期间更新绑定源、清除恢复最新值，以及目标退出后清空会话。未在上面列出的高级交互仍按下方矩阵继续专项验收，不能由测试总数替代。

### V4 合成、项目预览与日志预算

- 合成：Native Sample 的 StyleTarget 返回 3 个真实合成节点、2 张自身绘制 PNG；背景与文字分别显示，透视和正视均已观察。矩形、路径、相机及预算的严格像素／交互证明来自 CompositionInspectionTests 和 CompositionSceneTests；效果、mask 和 GPU 导入表面未获完整证明。
- 资源：窗口作用域 IncludeNested 实际扫描 3 个作用域、131 项并按 50 项分页；Duplicate 过滤保留 3 个不同 owner（本地／Merged／Dark），桌面实际修改 Merged 条目后 Reset 恢复，来源字典保持独立。
- 日志：Sample 实际 ILoggerProvider 与 custom source 各发一条记录，观察到原模板、Count=7、EventId=73、scope 和异常链；Source 参数明确不可格式化，来源按钮定位 MainWindow，选择和模板在刷新后保留。
- 项目预览：使用真实 Sample 的已构建 DLL、项目文件和 x:Class Window 预览，App 资源／绑定实际显示。随后对系统临时目录的项目／源副本保存标题变更，观察第 2 次加载；无效文件显示 xaml_load_failed 并保留原 Window，修正后第 3 次加载且错误清除，未改动仓库 AXAML 来做错误注入。
- 状态观察：曾从 UIA 读到旧加载计数，后续自行更新；新旧原生版本及有／无诊断连接的三轮检查均未复现赋值故障，真实 Text 读取正常。因此未据此修改产品逻辑，只为 Window 标题与宿主计数同步补了回归断言。
- 字节预算：48 条中文／转义／多参数日志先在真实 Client 得到 frame_too_large，修复后可读快照并继续查询 roots；600 条受阻通知的自动化证明历史不等发送、字节／数量有界且最多一个 sender 在飞。最终桌面显示 history 2,097,152 字节与 notification 524,288 字节上限。
- 两处真实问题已修：日志 pull 清空列表导致选择与详情消失，改为复用序号对应的 entry；Window 根的预览宿主中央空白，增加独立窗口提示。设计模式 Sample 提示使用预览宿主管道，避免复制业务默认管道。
- 最终构建复验：宿主独立窗口提示、Sample 设计模式状态／管道提示、Inspector 同时列出宿主与文档两根均已实际观察；深度输入 0／64 清楚可见。采集较大宿主时返回 37 层、11 张 PNG，并明确报告深度截断及 PixelBudgetExceeded；不把部分图像误记为全树无遗漏。
- 清理：本轮自行启动的 Sample、Inspector、原项目／临时副本 Previewer 和最终复验进程均已退出，六份 stderr 日志为空。临时源副本作为证据保留，未把试验性错误写入仓库源文件。

### V5 远程、属性与资产

- 认证传输：独立 Sample 与 Inspector 通过 Windows 回环 WebSocket 连接，地址无令牌、输入框视觉遮罩；CLI 使用环境凭据完成真实 stdio 远程附加、属性编辑／恢复和大于 1 MiB 的完整资产导出。HTTP 认证、Origin、TLS 拒绝、会话竞争、分片和断连请求取消由 Remote／Protocol 测试覆盖。没有 LAN／VPN、跨主机可信 TLS、WSL2 或移动部署证明。
- 属性：官方 ColorPicker 弹出面板已在桌面展开；颜色草稿／应用／最新绑定恢复、分组、排序、CLR 显式开启主要由真实 AXAML 和管道测试证明，未扩大为所有复杂画刷桌面已验。独立 Image.Source 实际显示 128×64 棋盘 PNG，不是控件或资产页截图。
- 绑定：BoundText 的 Source 弱 ID 打开 SampleViewModel 对象，来源上下文和返回原绑定可见；树选择保持原控件。ResourceKey 多作用域候选／非字符串键、expired／foreign 禁用由导航回归覆盖，不把候选当最终胜出资源。
- 资产：真实 Inter-Bold 字体显示家族 Inter 和实际字形，316,100 字节；通过原生保存框导出到本轮系统临时目录，磁盘 SHA-256 与 UI 同为 790c108befe859dac2ddbd20af3fbb6917c601b3d544c8a05761519f3b5508fe。URI 复制有真实剪贴板自动化，桌面未额外覆盖原剪贴板。
- 两项产品修复：同一对象的树刷新保留属性行、草稿和分类状态；巨大异常摘要限 2048 字符／8 层、引用去环，不执行 Message／GetBaseException override。4 项真实 RPC 先红后绿，错误后 roots 仍可读。
- 测试修正：MCP 新用例改用原有 assetUrl 参数；同名属性按 DeclaringType 区分。旧 Merged 用例仅等待新根引用会过早读取占位子项，固定该窗口复现后等待真实 Owner，核心断言保留，生产未为此修改。
- 调试输出曾误带一枚本轮生成的临时令牌，相关回环服务立即关闭并轮换；非用户凭据、未写应用日志。后续输出过滤令牌控件，所有本轮令牌在对应进程退出后失效。

### Phase 6 最终桌面观察

- 覆盖层：真实 Sample 的 BoxModelTarget 已显示 Margin、Padding、通用信息、水平／垂直标尺和延长线，页面／快照的 RenderScaling 为 2；目标 12° 旋转时，覆盖形态跟随旋转。这是该根缩放下的实际可见观察，多屏和其他 DPI 组合仍独立验收。
- 渲染器：真实 Sample 窗口的原生 Fps 覆盖实际绘制，Stop 后消失；其余三个标志及冲突／断连恢复的严格证明仍来自 RendererDiagnosticsTests，不由状态复选框代替像素证据。
- 事件：实际在事件路由页选择记录并跳转到源对象。Handled 继续按 Raised 类处理器观察区间解释；本轮桌面没有实例 handler 归因证明。
- Popup 边界：OverlayTests 的 popup fixture 设置 OverlayPopups=true，实际使用 owner Window／PopupOverlayLayer，不是独立原生 PopupRoot。新增 Native_flyout_roots_follow_focus_and_are_removed_without_observing_unrelated_windows 用局部 OverlayPopups=false 验证真实原生及嵌套 Flyout、无关窗口排除和关闭清理。根发现改为公开 OpenedPopups，解决 Flyout 私有 Popup 不在逻辑树中的漏根，已纳入最终 375 项。
- 原生 Flyout 桌面：FollowFocus／Highlight 开启并显示信息层，打开后实际标签为 TextBox #PopupTextInput、260×32 DIP、root(13,45.5)、scale 2；点击复选框后实际标签变为 CheckBox、112×32 DIP、root(13,89.5)、scale 2。Escape 返回 PopupButton，根列表恢复一个窗口；不据此宣称 Tooltip 或其他平台已验。
- 界面与清理：ShowOverlayCommand 在有效选择后启用、断开后禁用，展开参数“每批 32”完整可见；相应命令状态已有真实 AXAML 回归。GUI 断开后 Sample 显示等待连接，诊断高亮及信息层消失。本轮两个自有进程 33504／12808 核对绝对路径后均已停止，sample-verified.stderr.log／inspector-verified.stderr.log 均为 0 字节，未写真实用户事件默认配置。

### Phase 7 最终桌面观察与清理

以下是 Windows 的具体操作记录。最终 Inspector 默认窗口为 1322×885，十二个主标签全部可见；这不是多 DPI／跨平台验收。

- 协议：在最后指标布局修正前、相同 Protocol 源码的构建上，真实录制 170 条、279,510 字节，Stop 后历史保留；按 `metrics.start` 筛选只剩一条请求，参数与响应页都实际显示有效 JSON。行内请求关联、耗时与发送／接收字节均可见；该次数字只是操作样例，不是性能基准。
- 指标：最终布局实际发现 System.Runtime／Avalonia 共 28 个仪表、32 个序列，图共用 UTC 横轴。整页可滚到统计标签并看见 P90 数值；真实点击“清空此图”后状态报告序列 #1 已清空，该图从新 2 点继续，其他图保留。Stop 后时间窗与图冻结。发现数量来自该次目标活动，不是固定目录。
- 画刷：BoxModelTarget 的 Background 从 Solid 切换 Linear，草稿显示红蓝预览；点击“应用”后 Sample 的真实 Border 呈现红蓝渐变且属性来源显示 DiagnosticOverride。点击“清除”后回到 Solid 和原始颜色。
- 盒模型：最终桌面显示 Margin 24／12／36／18、Border 3、Padding 22／14／16／10、Content 316×100 DIP、RenderScaling 2；点击左侧 Margin 后 Inspector 分区强调可见。Sample 被前景 Inspector 遮挡，因此本轮没有证明 Windows 目标侧同一区域的实际像素；该路径的旋转／scale 1.5 和真实指针像素证明来自自动化。文字／图片专用信息本轮没有额外手动桌面操作，仍按自动化范围界定。
- 清理：GUI Disconnect 后 Sample 显示等待连接、诊断层消失且背景恢复原色；核对可执行文件后，本轮启动的两轮 Sample／Inspector 共 4 个进程均已停止，4 份本轮 smoke-v7 stderr 日志均为 0 字节。没有写入真实用户事件默认配置。

### 异步竞态历史

此前修复了刷新旧选择覆盖新选择、刷新中取消／丢失定位，以及较旧断点 pull 快照覆盖新 push 命中或复活已移除定义。两个 SelectionRefreshRaceTests 和一个 BreakpointSnapshotRaceTests 使用受控延迟复现顺序；原 4 个场景各连续 10 轮通过，已纳入当前 Inspector 回归。

本次第一次统一测试 212 项中，对象页 getter 次数断言失败（预期 1、实际 2）。查明是真实产品重复请求：选择流程等待高亮时激活对象页，页面和选择尾部分别发出成员读取。保留原断言，并新增 RuntimeSelectionLoadRaceTests 受控延迟测试，旧实现稳定复现，按请求代数修复后连续 6 次通过。Inspector 全套 50 项另连续 3 轮通过；最终扩展后的统一批次为上述 214 项全过。

## 验证环境

终端一：

```powershell
dotnet run --project samples/AvaloniaDevTool.Sample -- --pipe avalonia-devtool-demo
```

终端二：

```powershell
dotnet run --project src/AvaloniaDevTool.Inspector -- --pipe avalonia-devtool-demo
```

Sample 的 Agent 仅在默认 Debug 配置启用。样例包含命名控件、绑定源更新、动态节点、第二窗口和 Flyout，并有样式／资源、验证错误、日志、图片资产和测试 Meter。第 12 区提供同源 decimal 的 CultureFrench／CultureEnglish（fr-FR／en-US、N2）与 LostFocus 输入，可在样式绑定页复查文化、来源、规范化 StringFormat 和失焦写源。

GUI 与 MCP 共享同一个单客户端 Agent；验证 MCP 前先从 GUI 断开：

```powershell
dotnet src/AvaloniaDevTool.Cli/bin/Debug/net10.0/AvaloniaDevTool.Cli.dll mcp --pipe avalonia-devtool-demo
```

预览器的启动、源文件和窗口错误状态见 [预览文档](previewer.md)。可对预览管道使用同一 Inspector 或 MCP。

## 桌面与端到端验收矩阵

| 范围 | 操作 | 预期与准确边界 |
| --- | --- | --- |
| 连接 | 相同管道名连接、断开、关闭目标、重连 | 应用／PID／Session 正确；旧缓存、订阅和覆盖释放；新会话不复用旧句柄 |
| 外部后端 | 连接仅有上游 NuGet Agent 的目标，刷新快照并 detach | 共同读取只读；gaps 明确；旧选择／截图清除，Server 退出但目标保持运行 |
| 树 | 切换 Visual／Logical，逐层展开、搜索折叠分支 | 搜索报告预算和截断；选择结果按父链定位；两种树不冒充 Merged 树 |
| 直接子项分页 | 展开宽分支、加载更多、定位后续页，变更分支后续读旧游标 | 追加保留选择；游标失效明确提示；超过 20,000 子项失败，不把前缀当完整结果 |
| 模板父入口 | 同一模板部件分别从模板父复制到部件和复制模板所属控件 selector | 主入口为 owner /template/ part 路径并匹配当前部件，辅助入口只匹配 owner；等待中换选不复制旧结果，其他对象唯一匹配不能验证当前目标 |
| 动态与多窗口 | 增删节点、打开第二窗口及普通 Popup | 根与已请求父节点失效后重拉；移除对象不会误指向另一对象 |
| 拾取与焦点 | 移动／点击拾取、Esc、切换焦点 | 高亮正确；拾取不触发业务点击；退出后无残余模式 |
| 属性 | 编辑 Width／Margin／Background 和绑定 Text，再清除／断开 | DiagnosticOverride 生效，最新底层绑定值恢复；直接属性仍只读 |
| 草稿与错误 | 未应用草稿期间触发实时通知，输入非法值 | 草稿与行容器保留；错误可见，目标原值不变 |
| 上下文属性 | 选择 Grid／Canvas 子项、切换相关筛选、同对象换父 | 仅已知直接视觉父消费规则过滤；未知保留；隐藏草稿往返及换父后仍保留 |
| 样式绑定 | 选择 StyleTarget，切换 emphasized class，触发 ValidationInput 错误 | 帧、活动状态、表达式和验证信息有真实差异；不把相等值当作唯一获胜 setter |
| 绑定文化与时机 | 检查普通／编译及组合绑定、改变文化和 TextBox 焦点／显式更新 | 配置文化与诊断线程分开，实际格式和四种源更新行为可区分；不适用字段与缺失分开 |
| 资源 | 修改 SampleAccentBrush／SampleResourceText，再让应用主动改写同键 | DynamicResource 变化；恢复遇冲突保留应用新值，明确报告 |
| 资产 | 打开 sample.png、文本与 SVG | 小型图片／文本按真实格式预览；SVG 为源文本；超 1 MiB 请求失败 |
| 日志事件 | 开始捕获，发送 Warning／Information 和后台日志，选择 Click／* | level、来源、route／Handled 正确；历史有界，停止不再捕获 |
| 元素动作与伪类 | Focus、BringIntoView、启停和伪类切换／恢复 | 启停不破坏绑定；伪类可恢复，真实输入仍可重新计算状态 |
| 弹层冻结 | Native／Overlay Popup／Flyout 冻结、外点、恢复／断连和业务主动关闭 | 普通 light-dismiss 抑制并恢复；ContextMenu／ToolTip／transient 独立关闭保持明确限制，已关闭弹层不重开 |
| 截图 | 截取普通控件，再尝试零尺寸、超边长或高熵图片 | PNG／尺寸正确；96 DPI，边长／像素／编码大小超限明确失败，无静默缩小 |
| 3D几何 | 选择含布局偏移／旋转的控件，调整层距与视角 | 使用真实矩阵和 VisualGeometry 层；不显示虚构的逐层像素 |
| 指标 | Meter 过滤为 AvaloniaDevTool.Sample，生成测试数据 | Counter 是原始 +1，Histogram 是已知合成样本；百分位仅对保留窗口 |
| 断点 | 完整 Owner:Member、命中计数条件、禁用、重置与一次性删除 | 事件一条路由只计一次；无调试器的 Suspend 明确拒绝；真实暂停另行人工验证 |
| CLR 对象 | GUI 对象页或 MCP object 从 Control.DataContext 进入成员、集合和循环 | 公共只读成员、分页／错误／截断明确；临时对象可失效；不提供 CLR setter |
| 框架输入 | MCP input 向 TextBox 发文本／按键，向 Button 发 Click | 由框架实际处理；Delivered 不等于必有业务效果；禁用／隐藏／只读条件明确拒绝 |
| 预览器 | classless／已构建项目、Window 与控件库，保存合法／非法源文件 | App 资源、x:Class 与源程序集上下文已实现；成功重建，失败保留；现有 IDE 设计进程附加仍未实现 |

普通 Popup、原生及嵌套 Flyout 已有 Headless 覆盖，Windows 原生 Flyout 焦点高亮已有上述实际观察；Tooltip、其他操作系统、GPU 专用控件及不同渲染后端尚未完成专项桌面验证。完整 Profiler 语义、效果／mask／GPU 表面的精确合成、任意框架／IDE 设计进程仍不在当前通过范围中；已实现的真实自身绘制 PNG 和已构建项目预览不能扩大为这些承诺。

## 隔离与证据

关闭正在运行的本项目桌面进程后再重建共享输出，避免 exe／DLL 文件锁。需要并行或隔离某个测试时，使用独立临时 artifacts 目录：

```powershell
$testArtifacts = Join-Path $env:TEMP "avdt-agent-tests"
dotnet test tests/AvaloniaDevTool.Agent.Tests/AvaloniaDevTool.Agent.Tests.csproj --artifacts-path $testArtifacts --filter FullyQualifiedName~RuntimeInspectionTests
```

服务预算和错误语义见 [指标](metrics.md)、[断点](breakpoints.md)、[对象浏览](object-browser.md)、[输入](input.md) 与 [几何](layout-3d.md)。[MCP 文档](mcp.md) 区分真实协议端到端测试和直接调用 handler；外部模式的启动与验证见 [XamlMcp 后端](xamlmcp-backend.md)、[Inspector 双后端](xamlmcp-inspector.md)。

[XamlMcp 兼容验证](research/xamlmcp-compatibility-validation.md) 和 [CDP 兼容验证](research/cdp-compatibility-validation.md) 记录各自固定版本的实际试验，与本项目阶段统一回归分别计数。[源码复用审计](research/open-source-agent-tools-audit.md) 中存在源码或测试不自动证明适配；已实际采用的 XamlMcp 后端另有产品集成和桌面证据。
