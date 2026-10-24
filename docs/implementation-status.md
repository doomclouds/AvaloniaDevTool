# 实现完成度审计

当前项目是可运行的诊断工具子集，尚未达到原版 DeveloperTools 的全部工作流。Phase 8 新增直接子项游标分页、已知布局上下文附加属性筛选、弹层 light-dismiss 冻结恢复、从模板父复制到部件、绑定文化／格式／源更新时机，以及最小消费程序的 trimmed／真实 Native AOT 核心验证。严格无警告 AOT、完整弹层冻结、实例 handler 归因、跨主机／平台、Image／Drawing／shader／custom 画刷、Compositor 全效果／GPU、完整 Profiler 与 IDE 进程附加仍有缺口。

审计日期为 **2026 年 10 月 25 日**，范围是本次未提交工作树的 src、tests 与项目内研究文档。逐项基线为 [功能目录](research/developer-tools-feature-catalog.md) 的 **125 项公开能力/边界 + 16 项原生声明候选**。本文件没有改动原目录的 V1状态 列；该列仍是当时24项测试通过的历史基线。

## 判定与执行证据

| 代号 | 状态 | 判定 |
| --- | --- | --- |
| A | 已实装且自动化证实 | 当前自研行为有实现和明确自动化证明；不表示与原版所有参数、平台或内部算法等价。 |
| P | 部分实装 | 已有可用子集，但该行完整验收条件、GUI/MCP接入、语义或专项验证仍缺。 |
| R | 仅研究已说明 | 已掌握原版REA或具体开源实现线索，自研对应行为尚未实装。 |
| U | 待实现或未验证 | 当前工作树没有相应行为，或没有足够执行证据确认；出现面板/方法名字不算完成。 |
| B | 边界项不要求复制原版限制 | 原版明确限制或商业前提不是必须复制的功能；自研选择单独写明。 |

Phase 8 最终冻结批次为 `artifacts/phase8-verified-results/phase8-verified*.trx` 的七份统一产物，**476 项全部通过，无跳过**：Protocol 26、Agent 269、Inspector 114、CLI 22、Previewer 15、XamlMcp Backend 19、Remote 11。XML 合计 total／passed 均为 476，failed／notExecuted 均为 0，命令退出码 0；全部 22 项目 Debug／Release 完整构建均退出码 0、0 警告、0 错误。这是普通构建结果，不能代替 Native AOT 严格发布检查。

本轮新增 43 个测试方法见 T393–T435，Theory 展开后新增 58 个用例，均已纳入上述 476 项；AotSample／AotSmoke 已进入解决方案的 22 项目。首轮 `artifacts/phase8-final-results` 为 474 项、470 通过／4 失败，其中 Inspector 108／112；旧传输缺分页方法及刷新临时空选择清掉属性行的问题已分别修复，原断言和新增兼容回归转绿，该红批次仍保留为历史，不与最终批次或专项相加。

Phase 7 历史冻结批次为 `artifacts/phase7-desktop-results/phase7-desktop*.trx` 的七份统一产物，418 项全部通过、无跳过：Protocol 23、Agent 229、Inspector 103、CLI 18、Previewer 15、XamlMcp Backend 19、Remote 11；当时 Debug／Release 全部 20 项目构建均退出码 0、0 警告、0 错误。新增 43 个测试方法对应 T350–T392。`phase7-current` 的 418 项是指标整页滚动布局修正前的阶段绿；417 项是后续指标指针刷新修正前的阶段绿，416 项首轮有失败，均不代表 Phase 8 最终源码。

此前 Phase 6 冻结批次为 `artifacts/phase6-verified-results/phase6-verified*.trx` 的七份统一产物，**375 项全部通过，无跳过**：Protocol 17、Agent 207、Inspector 90、CLI 16、Previewer 15、XamlMcp Backend 19、Remote 11。随后 Debug／Release 全部 20 项目均为 0 警告、0 错误，退出码均为 0；不由专项结果相加推算总数，早先 phase6-test-results 中的红例不能作为当前批次。

此前 `artifacts/phase5-boundary-results` 的 323 项及当时 20 项目双配置零警告构建保留为历史基线。

319 项是上述属性异常边界修正之前的统一批次，保留在 `artifacts/phase5-verified-results`；其当时 20 项目 Debug／Release 构建零警告、零错误。

此前 `artifacts/phase4-final-results/phase4-final*.trx` 的六份 266 项及当时 18 项目零警告构建是 Phase 5 之前的历史基线。

此前 260 项是最后预算／宿主／显示修正之前的中间批次。更早的六份 `artifacts/phase3-final-layout-results/phase3-final*.trx` 为 214 项：Protocol 12、Agent 116、CLI 4、Inspector 50、Previewer 13、XamlMcp Backend 19；当时 15 项目 Debug／Release 零警告、零错误。这些与 157／116 项均保留为对应源码的历史结果，不与当前计数混合。

测试索引列出源码中的精确测试名称，供复查。测试用例数不是 141 条功能的完成百分比，也不将自动化范围扩大成所有 GUI/平台验收。此前另补三个可控竞态回归：新选择被旧树刷新覆盖、定位被树替换取消，以及新断点通知被旧 pull 快照覆盖；这三项和原间歇场景连续重放 10 轮通过。新增对象页另有真实管道下换控件、隐藏页和断连的旧响应保护测试。

运行证据范围：

- **桌面V1**：2026-10-24真实 Sample/Inspector 两个窗口的连接、拾取 BoundText、高亮、编辑、覆盖期间更新源、清除恢复最新绑定，以及目标退出后清理；已记在 [测试文档](testing.md)。
- **Headless自动化**：真实Avalonia控件、布局及命名管道；不等于用户桌面上的多DPI/平台视觉验收。
- **stdio自动化**：CLI.Tests通过官方SDK客户端启动服务器进程，真实initialize/tools/list/tools/call，再连到测试进程中的Agent；不是直接调用工具方法。
- **原版运行目录**：本机2.2.3只正常执行过初始initialize/tools/list，15项声明不证明受授权/目标状态影响的tools/call均执行成功。原版GUI只观察到Sign-In入口。
- **桌面V2**：F12启动连接、实际Meter图表及已知6样本统计、classless预览保存/错误保留/恢复均已观察；另已在可见的独立NuGet Agent目标上验证XamlMcp树/只读属性/绑定缺口/PNG/刷新失效/断开与进程清理。
- **桌面V3**：真实 Sample／Inspector 已观察 Merged 合成说明与模板 owner 跳转、BoundText 结构字段、DataContext 的 14 个业务成员与面包屑及 50／122 分页；Profiler 一次操作采到 55 分组／192 条活动并停止保留。11 个主标签、最大化后的分页按钮与 3D 左右分栏已复查，128 节点／12 层预算截断可见。该轮 Debug 的非最大化初始窗口标题、11 标签及底部状态均在工作区可见且保持连接；关闭 Inspector 后 Sample 恢复等待连接。该轮启动的 6 个进程最终均退出，6 份 stderr 日志均为 0 字节。此处不扩大为所有复杂集合、异常、回收或原版统计的桌面验收，详情见 [测试记录](testing.md)。
- **桌面V4**：真实已构建 Sample 的 MainWindow.axaml／x:Class 已预览，宿主与独立 Window、App 资源／绑定实际呈现。NativeStyleTarget 的合成快照实际为 3 层／2 张 PNG，蓝背景与白文字独立透视／正视可见。资源页查询 131 条、每页 50；Duplicate 的本地／merged／Dark 三个 owner 分开，实际嵌套项临时修改后 Reset 恢复。真实 ILogger 与 custom 两项日志显示 EventId 73／SampleOrder、Count 7、RequestId scope 和异常链，来源跳至 MainWindow 后仍保留模板。此为该次操作的具体观察，不证明全部 provider／组合效果；最后修正后的具体复验见下条。
- **V4 最终构建复验**：宿主中央 Previewer.WindowNotice 显示独立 Window 说明，Sample 设计预览显示“诊断由宿主提供”并提示使用宿主管道；Inspector 实际连接并列出宿主／文档两个根。日志页显示历史 2,097,152／通知 524,288 字节预算，深度输入的 0 与 64 清晰可见。另一次宿主合成捕获有 37 层／11 张 PNG，并明确显示 depth／PixelBudgetExceeded 限制，没有把缺失图像当作完整捕获。
- **V4 保存监视**：在最终预算补丁前的构建上，临时 Sample 源副本保存后加载次数从 1 到 2，文档标题实际改变；保存无效 `<Window` 时可见 xaml_load_failed 且保留旧文档，修正后第 3 次加载并清错误。
- **开源兼容与采用**：[XamlMcp验证](research/xamlmcp-compatibility-validation.md)包含源码及公开NuGet真实MCP链和97项针对性测试；[CDP验证](research/cdp-compatibility-validation.md)包含12.1.4编译、真实网络/像素/输入及清理钩子后的8项通过。当前产品已通过[IInspectionBackend只读适配](xamlmcp-backend.md)采用公开XamlMcp包；CDP尚未作为产品后端引入。
- **基础工作流专项**：[Merged 树](merged-tree.md) 的真实控件与命名管道测试覆盖模板组、插槽、重模板、内容替换和 Popup；[对象页](object-browser.md) 的 6 项 Inspector Headless 测试覆盖导航、分页、回收和旧响应；[绑定诊断](binding-diagnostics.md) 分别记录 Native、Inspector 与公开包映射测试。原版 Merged CIL 是静态语义证据，不能替代自研运行测试或原版桌面验收。
- **Profiler 专项**：11 项 Agent 测试有真实框架样式／class 激活／资源活动、主题分组、长资源键隔离及有界合成 Activity 压力；2 项 Inspector Headless 和真实 MCP stdio 用例通过。缺失资源的框架结果是未知，class 改变不是实际鼠标 hover，不能据此扩大为原版全部统计验收。细节见 [Profiler](profiler.md)。
- **Phase 4 模块专项**：[合成绘制](composition-capture.md) 的 Agent 测试使用真实 Skia compositor，区分自身 PNG、实际 server 顺序与重组误差；Inspector 5 项覆盖透视像素／clip／相机／范围／显示开关及真实管道刷新和释放。[资源](resources.md) 的 8 Agent／4 GUI 项覆盖查询、原 owner 编辑和旧响应；[日志](logging.md) 覆盖模板／参数／异常、实际 ILoggerFactory 与 GUI 跳转；[文件附加](preview-attach.md) 的 8 项真正 stdio 用例启动项目预览子进程。另有真实 MCP 查询／PNG／日志断言。它们不拼成新的完整回归计数，未执行的桌面／GPU／AOT 验收继续明确保留。
- **日志完整帧与背压**：最大 Unicode／转义文本的真实管道快照曾稳定触发 frame_too_large，LoggingBudgetTests 保留该红灯场景并验证修复后会话可继续使用。单条编码预算、pending＋history 合计 2 MiB、并发清空／重启及慢通知发送的 5 项专项通过；通知有独立 512 KiB／100 条上限与损失计数，不预序列化无界积压。
- **Phase 5 专项**：[Native WebSocket](remote-transport.md) 使用真实 loopback Kestrel／ClientWebSocket，覆盖认证、Origin、会话门、推送／清理、分片／Close／双向 RPC；不是跨机验收。[属性](properties.md) 覆盖默认 CLR getter 惰性、统一去重、实际编辑器、组状态与 PNG；[绑定导航](binding-diagnostics.md) 验证来源、真实资源键与过期命令；[资产](assets.md) 验证真实管道大于预览上限的完整导出、哈希／取消／原子写入、元数据与字体释放。MCP 与 Inspector 的新远程专项分别记录，不能由这些子集推算全量总数。
- **Phase 5 属性异常边界**：4 项真实管道回归先红后绿，覆盖 90 万中文字符的 getter／IImage.Size 错误、Message／GetBaseException 覆写、聚合及深层异常；错误摘要最多 2048 字符／8 层并检测引用循环，不展开递归聚合消息，失败响应后会话继续可用。
- **桌面V5**：独立 Sample 与 Inspector 已通过真实 loopback WS 连接；演示凭据在一次 QA 的 UIA 广泛读取中进入工具输出后已立即停服务撤销，复验使用轮换令牌并排除凭据字段。该临时令牌不是用户凭据，应用日志没有 token；视觉遮罩不构成 UIA／进程隔离。官方 ColorPicker popup 实际展开，颜色应用／恢复仍以自动化证据为准。Inter-Bold 实际家族／字形可见、原始 316100 字节，原生保存对话框导出系统 Temp 文件的磁盘 SHA-256 为 `790c108befe859dac2ddbd20af3fbb6917c601b3d544c8a05761519f3b5508fe`，与 UI 相同。BoundText Source 弱 ID 35 进入 16 成员对象页再返回原绑定，控件树选择始终为控件 4；Image.Source 的独立 128×64 棋盘 PNG 实际显示。URI 复制本轮未额外作桌面操作，只有窗口剪贴板自动化证据。初次与轮换复验的 4 个桌面进程最终均已退出，4 份 stderr 日志均为 0 字节。
- **Phase 6 自动化**：[覆盖层](overlays.md) 的 16 Agent／2 GUI 用例覆盖真实 Skia 像素与根几何；[树工作流](tree-workflows.md) 的 7 Agent／4 GUI 用例包括诊断集合排除、selector 末节点预算和 4 万以上 ID 分批释放的红转绿回归；[事件](events.md) 的 9 Agent／5 GUI 用例覆盖关联、TTL、UTF-8 预算、来源失效及默认选择保存／重连；[渲染诊断](renderer-diagnostics.md) 的 6 Agent／1 GUI 用例验证原生帧变化／恢复和五种框架 histogram、System.Runtime 实际样本。一个真实 stdio 场景再连通覆盖层、selector、renderer 和 route；这些均已纳入上述 375 项同批次。另有一个原生 PopupRoot 发现回归；Windows 已实际观察 Flyout 输入框与复选框焦点高亮、Escape 返回按钮、关闭后根恢复及断开清除，具体边界见 testing.md。

- **Phase 7 自动化**：四种内置画刷 3 Agent／3 GUI；时间窗／帧／间隔和同步图 6 Agent／4 GUI；源断点 8 Agent／3 GUI／1 Protocol／1 CLI；专用信息与分区 hover 5 Agent／1 GUI；协议监视 5 Protocol／2 GUI；另有 1 个真实 stdio 画刷／指标链。共 43 个新增方法已纳入上述 418 项，同参数递归路由、未变化断点刷新保持 Update、指标真实按下→刷新→释放均有确定性回归；不扩展为全部桌面／平台或原版私有算法等价。

- **Phase 7 桌面**：Windows 默认 1322×885 窗口的十二标签可见；Protocol 实际录制／停止／筛选与 JSON 详情、最终指标共享 UTC 轴／P90／单图清空／停止冻结，以及 Solid→Linear 应用／Clear 恢复已观察。盒模型数值和 Inspector 左 Margin 强调可见，但目标被前景窗口遮挡，未据此宣称 Windows 同侧像素已验；专用文字／图片信息本轮仅按自动化。断开后诊断层清除、背景恢复，两轮 4 个自有进程退出、4 份 stderr 为空，详见 [测试记录](testing.md)。

- **Phase 8 专项**：[上下文属性](contextual-properties.md) 3 Agent／1 GUI／1 CLI 方法验证直接视觉父白名单、未知保留、换父通知、隐藏草稿及完整树刷新保留行；[树工作流](tree-workflows.md) 6 Agent／4 GUI／2 CLI 方法验证 2305 子项续页、游标失效／release、从模板父复制到部件、owner 辅助入口、真实指针／剪贴板、错误 StyleKey 唯一匹配误报，以及旧会话能力探测／成功后不降级；[弹层冻结](popup-freeze.md) 12 Agent／1 GUI／1 CLI 方法展开为 19 用例，覆盖真实 Native／Overlay 输入、配置所有权及恢复，同时明确菜单／ToolTip／transient 仍可独立关闭；[绑定诊断](binding-diagnostics.md) 8 Agent／1 GUI 方法展开为 19 用例，覆盖文化／实际格式和四种更新时机。以上是专项范围，统一批次和 Windows 桌面范围另记。
- **Phase 8 AOT**：[独立消费样例](native-aot.md) 的 trimmed／真实 Native AOT 进程已通过管道 PID、三种树、首分页、上下文属性、弹层只读根状态、属性覆盖／通知／恢复、PNG、框架拾取与断连清理；Protocol 新增 3 个 metadata 方法且独立兼容分析零警告。默认严格 Native AOT 发布实际 exit 1，编译阶段 46 条 IL error；允许兼容警告的证据模式核心通过不能升级为整个 Agent 的无警告 AOT 兼容。
- **Phase 8 桌面**：Windows 默认 Inspector 的相关属性开关可见，选中 CultureFrench 时开／关／开实际为 173／182→182／182→173／182。Sample fr-FR／en-US 文本初始为 `12 345,67`／`12,345.67`，输入 `13000,5` 在失焦前保持旧文本、点击只读管道框后变为 `13 000,50`／`13,000.50`；Styles 实际显示 fr-FR、Configured、`{0:N2}`。标准 Native Flyout 在全部根冻结后外点及切换 Inspector 仍保持，全部恢复后再次外点实际关闭；不以零弹层状态代替关闭观察。H01 仍 P，本轮未手验 ContextMenu／ToolTip 等更多路径；2305 子项分页仍按自动化。GUI Disconnect 后 Sample 等待连接且无弹层／覆盖，核对 exe 后本轮 PID 16900／2268 均已停止，`artifacts/smoke-v8` 两份 stderr 均 0 字节。详见 [测试记录](testing.md)。

## 汇总

| 范围 | A | P | R | U | B | 合计 |
| --- | --- | --- | --- | --- | --- | --- |
| 公开能力/边界 | 77 | 38 | 0 | 5 | 5 | 125 |
| 原生声明候选 | 9 | 3 | 0 | 4 | 0 | 16 |
| 全部条目 | 86 | 41 | 0 | 9 | 5 | 141 |

上表保留原功能目录的 125＋16＝141 条映射。其中 5 个 B 是产品边界，单列说明是否沿用原版限制，不计作已完成功能。排除 B 后，**136 个真实功能项为 A 86、P 41、R 0、U 9**；A 占 86／136，约 **63%**。此前约 61% 的口径来自 86／141，将功能与边界混合作分母，属于历史口径，不与当前功能条目比率直接比较。条目粒度不同，此比率不表示原版全部参数、平台、内部算法或开发工作量的覆盖率；P／R／U 仍是剩余功能项。

## 逐项映射

Kxx是下方代码索引，Qxx是研究/复用证据，Txxx是精确测试索引。“—”表示没有该行的专项测试或新增运行证明。相邻功能的测试不能替代缺失行为。

### 连接与进程

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| C01 | 桌面应用接入 | A · 已实装且自动化证实 | K01 | T009, T134；桌面V1 | 本机单客户端管道、会话和版本握手已证实。 |
| C02 | F12 手势启动工具 | A · 已实装且自动化证实 | K02 | T074；桌面V2 | 配置、去抖和释放有回归；实际Sample中F12启动指定Inspector.exe并通过--pipe连接成功，其他平台未专项验收。 |
| C03 | 自定义应用名称 | A · 已实装且自动化证实 | K01,K02 | T090；Sample运行记录 | 显式名称和FriendlyName默认值有实现，握手显示真实目标；默认回退未独立成例。 |
| C04 | 启动时自动连接 | P · 部分实装 | K02,K14,K16 | T103 | Inspector --pipe及MCP首次调用可附加；没有原版平台AutoConnect策略。 |
| C05 | Runner 自动启动或 NoOp | P · 部分实装 | K02 | T074 | 配置为空不装手势；单Agent进程去重；跨进程启动竞争未测。 |
| C06 | 指定可执行文件或 Apple bundle | P · 部分实装 | K02 | 桌面V2 | Windows绝对exe路径和--pipe参数转发已实测；Apple bundle没有支持。 |
| C07 | HTTP 与命名管道 | A · 已实装且自动化证实 | K01,K31,K32,K33 | T135, T242, T244, T247, T256, T257, T260 | Windows loopback 的 HTTP upgrade／Native 二进制 WS 与管道已验证建立、编辑／通知、掉线清理与重连；共用单会话门，不等同 HTTP REST 或 MCP wire。 |
| C08 | 局域网及 VPN 连接 | P · 部分实装 | K31,K33,Q11 | T248, T249, T251 | SDK 已提供显式非 loopback＋TLS 配置并保留客户端信任验证；LAN／VPN／可信 TLS 跨主机及实际断网恢复尚未实测。 |
| C09 | 移动及 Browser 应用 | U · 待实现或未验证 | K01 | — | 没有Android/iOS/WASM部署和运行证据。 |
| C10 | WSL2 应用连宿主工具 | U · 待实现或未验证 | K01 | — | 没有WSL2目标到Windows的传输与重连证据。 |
| C11 | 连接 IDE 预览器 | U · 待实现或未验证 | K15,K28,Q02 | — | 已构建项目的独立设计宿主与 MCP 文件附加已有，但没有接入 IDE 自有设计进程／协议或其重建后的自动接续。 |
| C12 | 多应用独立窗口 | P · 部分实装 | K01,K14,K16 | T135 | 每Client隔离会话，但Agent单客户端；两应用并发同ID隔离尚未测试。 |
| C13 | Native AOT 目标支持 | P · 部分实装 | K59,Q21 | T393, T394, T395；AOT 真实消费进程 | 源生成 wire 和独立 trimmed／Native AOT 消费程序核心实跑通过；默认严格 Native AOT 发布实际失败，编译阶段 46 条 IL error，任意反射诊断／完整合成等未验证。 |
| C14 | 离线使用与激活状态 | B · 边界项不要求复制原版限制 | K01,K14 | T089 | 自研无商业激活依赖，不复制原版授权限制。 |

### Elements 树与导航

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| T01 | 合并逻辑与视觉树 | A · 已实装且自动化证实 | K01,K18 | T136, T140, T141, T173 | Merged 保留逻辑用户内容并聚合模板，内容不重不漏；Popup 当前呈现路径、内容替换、Visual／Logical 回归及 GUI 合成角色导航已测。 |
| T02 | 可见节点按需加载 | A · 已实装且自动化证实 | K01,K16 | T103, T013 | 按展开加载直接子节点，分支集合事件驱动失效及过期响应取消；150 ms 仅用于根发现／弱观察清理。 |
| T03 | 模板聚合为子节点 | A · 已实装且自动化证实 | K18,Q04 | T136, T137, T138, T139, T174 | TemplateGroup／Root／Part／ContentSlot、模板 owner 与内容弱 ID 已实装；重模板失效通知、旧 ID 拒绝和 GUI 过期选择清理已测，不推测 ItemsPresenter 项目插槽。 |
| T04 | 名称和类型搜索 | A · 已实装且自动化证实 | K03,K16 | T030, T097 | 目标端有界搜索未展开分支，父链定位已测。 |
| T05 | 焦点跟踪 | A · 已实装且自动化证实 | K03 | T075 | 真实GotFocus通知跟随，可关闭。 |
| T06 | 点击拾取定位 | A · 已实装且自动化证实 | K01,K07 | T017；桌面V1 | 命中/退出/高亮与阻止业务点击已测。 |
| T07 | 展开直接子节点 | A · 已实装且自动化证实 | K01,K16 | T103 | 直接子节点按需查询，一次展开仅取一层。 |
| T08 | 递归展开及含模板展开 | A · 已实装且自动化证实 | K42,K44,Q15 | T090, T323 | GUI 与 MCP 均有模板范围，GUI 节点／深度预算、让出调度、CTS 取消和保留部分结果有真实测试。 |
| T09 | 折叠节点 | A · 已实装且自动化证实 | K42,K44 | T316, T317, T319, T322, T324, T326 | 事件驱动观察、折叠释放和重新读取已测，属性观察独立；诊断层变化不触发树失效，累积超过 4 万 ID 按最多 2 万一批释放并检查会话／取消。 |
| T10 | 复制元素或选择器 | A · 已实装且自动化证实 | K43,K44 | T318, T320, T325, T348 | 真实 Avalonia parser／matcher 验证普通及模板 selector，报告唯一／歧义／截断；预算最后节点不误报 Unique，实际窗口剪贴板和会话元数据复制已测。 |
| T11 | 聚焦及滚动到元素 | A · 已实装且自动化证实 | K03,K44 | T033, T321 | 真实 Focus 与 ScrollViewer Offset、进入视口及滚动前后红色目标像素变化均有断言。 |
| T12 | 视觉失效与窗口 FPS 覆盖 | A · 已实装且自动化证实 | K47,Q17 | T341, T342, T343, T344, T345, T347, T348 | 四个公开原生 renderer 标志、窗口／全部根／新根范围和冲突恢复已测；真实帧像素变化、停止／断连恢复，不用工具计时器冒充 FPS。 |
| T13 | 枚举与强制伪类 | P · 部分实装 | K03,K16 | T034, T098 | 枚举/切换/恢复初始状态已测；覆盖期间自然状态重新计算/恢复仍有限。 |
| T14 | 上下文相关属性筛选 | A · 已实装且自动化证实 | K60,Q22 | T396, T397, T398, T399, T400 | 按直接视觉父布局过滤 Grid／Canvas／DockPanel／RelativePanel 已知属性，未知／继承及其他附加项保留；GUI 默认本地筛选并保留隐藏草稿、换父通知，Client／MCP 默认保持旧行为。 |

### 属性样式与绑定

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| P01 | Avalonia 属性与有效来源 | A · 已实装且自动化证实 | K04 | T009, T010；桌面V1 | GetDiagnostic有效值/来源，覆盖标记DiagnosticOverride。 |
| P02 | 纳入 CLR 属性并去重 | A · 已实装且自动化证实 | K34,K35,K40,Q12 | T262, T263, T268, T294, T296, T297 | 默认只读 Avalonia，显式 opt-in 才执行 CLR getter；同一 backing property 才去重，保留独立隐藏成员；错误有界且不执行异常覆写，未知对象不格式化。 |
| P03 | 按名称筛选、按名或值排序 | A · 已实装且自动化证实 | K35 | T272 | 支持名称／当前值文本升降序及分类排序，搜索／分类筛选和真实 PropertyList 选择、同 ID 行与脏草稿在刷新后保留。 |
| P04 | 按分类分组 | A · 已实装且自动化证实 | K34,K35 | T263, T272 | CategoryAttribute 或声明类型提供分类，可折叠组头、组对象和展开状态在重拉／实时更新后保留。 |
| P05 | 专用颜色与画刷编辑器 | P · 部分实装 | K51,K52,Q12 | T350, T351, T352, T353, T354, T355, T390 | 官方 ColorPicker 与 Solid／Linear／Radial／Conic 结构化草稿、色标／坐标／半径／角度／透明度／变换及实际像素、清除／断连恢复最新绑定已测；Image／Drawing／shader／custom、超限或不兼容画刷仍只读。 |
| P06 | 图像与 Geometry 预览 | P · 部分实装 | K34,K35,K40,Q12 | T265, T266, T267, T273, T295, T296, T297；桌面V5 | 已绘制实际 IImage／Geometry，验证尺寸／原点／weak／stale 与有界错误；桌面独立 PNG 已观察。取消／迟到响应和 Bitmap 释放的 GUI 专项尚未覆盖。 |
| P07 | 嵌套对象浏览 | A · 已实装且自动化证实 | K06,K14,K20 | T142, T143, T144, T145, T146, T147, T190 | Control → DataContext → 嵌套值、面包屑／后退、成员与集合分页、自身／祖先循环、对象回收和旧响应保护均已测；同一选择的进行中加载不重复执行 getter，显式刷新可重读。只读，不提供 CLR setter。 |
| P08 | 实时属性编辑与清除 | A · 已实装且自动化证实 | K04 | T010, T012, T103；桌面V1 | Animation可释放覆盖，清除/断开恢复最新绑定。 |
| P09 | 属性全部 setter 与优先级 | P · 部分实装 | K05 | T048 | 帧/优先级/setter已有；MatchesEffectiveValue值相等不证明唯一胜者。 |
| P10 | 活动及失效 setter 区分 | P · 部分实装 | K05,K36 | T093, T281 | 活动帧／运行绑定／明确错误已有派生筛选且不改原证据；公开 Values 不返回无值／无效 setter，仍不能分类这些缺失项或证明连续激活状态。 |
| P11 | 绑定 Source 与 Path | A · 已实装且自动化证实 | K19,K36,Q05 | T148, T149, T276, T277, T278 | Native Source／DataContext 可进入真实对象成员、嵌套／循环并返回原绑定，控件选择保留；缺源、坏路径、失效弱源和旧命令归属已测，观察不保证跨请求原子一致。 |
| P12 | Mode/Converter/Fallback 参数 | A · 已实装且自动化证实 | K19,Q05 | T148, T154, T155, T159, T164 | 已读取有效 Mode、Converter、Parameter、Fallback，Known／Null／Unknown／Unavailable 及具体 gap 不混淆；表达式或上游不支持时逐字段降级。 |
| P13 | 验证异常及内部异常 | A · 已实装且自动化证实 | K19 | T150, T151, T156, T160 | 转换／验证错误分类、实际异常类型和有界内部异常树已传输并展示；数量／深度／文本截断可见，不传活异常或 StackTrace。 |
| P14 | MultiBinding 表达式 | A · 已实装且自动化证实 | K19 | T153, T158, T160, T166 | 保留组合表达式、缓存当前值和逐子项路径／错误；界面可展开子项，健康父项不冒充同一失败，整体节点／文本预算有测试。 |

### 覆盖层与测量

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| O01 | 树悬停触发高亮 | A · 已实装且自动化证实 | K41,K50 | T314 | 树行 hover 接入真实覆盖层，预览不改变选择，过期移出不清掉较新 hover，最后移出恢复所选或清空。 |
| O02 | 应用内 hover 检查模式 | A · 已实装且自动化证实 | K01,K07 | T017；桌面V1 | 应用内预览、点击退出、状态通知已测，Esc有实现。 |
| O03 | Bounds 边界 | A · 已实装且自动化证实 | K41,Q14 | T298, T300, T301, T304, T305, T310, T312, T349 | 窗口根 DIP 四角、嵌套变换／中心原点和 clip 有 Skia 像素证据；OverlayTests popup fixture 为 owner-root 模式，T349 另测原生及嵌套 Flyout 根、焦点高亮及清理。Windows 已观察原生输入框／复选框信息高亮；非仿射不绘假 identity，截图排除诊断层。 |
| O04 | Margin 与 Padding 分区 | A · 已实装且自动化证实 | K41 | T299, T302, T306, T314 | 正负 Margin、非均匀／重叠 Padding 与 Border 内边框有像素／几何证据，只读实际 Avalonia 属性；显示声明值的舍入投影，不声称重建原 parent arrange slot。 |
| O05 | 元素信息浮层 | A · 已实装且自动化证实 | K55,Q14 | T377, T378, T379, T380, T381 | TextBlock／TextBox 实际框架布局、字体、Image 解码像素／DPI／DIP 与弱源 ID 已测；文本不传内容，密码跳过长度／布局，Inline run 字体、自定义源与 source URI 明示未知。 |
| O06 | 水平/垂直标尺 | A · 已实装且自动化证实 | K41 | T303, T314 | 根空间水平／垂直跨度、主次刻度与 DIP×RenderScaling 像素单位可复算；不把旋转后的 root 跨度混作 local 宽度。 |
| O07 | 边界延长引导线 | A · 已实装且自动化证实 | K41 | T300, T301, T303, T304, T314 | 引导线经实际变换角点延长至根视口，父偏移／尺寸／变换更新及视口裁剪有自动化；mask／effect 输出仍有限制。 |

### 路由事件

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| E01 | 选择事件及事件组 | A · 已实装且自动化证实 | K45,K46,Q16 | T329, T336, T337, T348 | 真实注册目录、单项／组并集、动态新增、实际 AXAML 选择并捕获均已测；六组是本项目按公开元数据分类。 |
| E02 | Tunnel/Bubble/Direct 路由 | A · 已实装且自动化证实 | K45,K46 | T327, T328, T330, T331, T334, T336, T348 | 一次 raise 以 RouteId 聚合传播顺序，分别记录 Tunnel／Bubble 完成和 Direct；嵌套／重复 args、异常未完成、TTL／清空代数有测试。 |
| E03 | 标出 Handled 节点 | P · 部分实装 | K45,K46 | T327, T336 | 展示类处理器观察值、策略完成值及相邻观察区间变化；观察早于该节点实例处理器，不能归因具体实例 handler。 |
| E04 | 事件节点跳回元素 | A · 已实装且自动化证实 | K45,K46 | T333, T336, T338 | 实际按钮分别跳源与接收节点；弱源 GC 后点击明确显示已失效并保留路由详情，不用旧数据冒充目标。 |
| E05 | 默认监听集配置 | A · 已实装且自动化证实 | K45,K46,K49 | T329, T339, T340 | 默认六组可覆盖并显式保存到有界 UTF-8 原子文件，重连恢复；缺失注册 ID、非法组／过大文件有提示与保护，修改在下一次捕获生效。 |
| E06 | 清空事件列表 | A · 已实装且自动化证实 | K08 | T004, T096 | 清空历史/丢弃计数后继续采集，GUI停止/清空已测。 |

### 断点

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| B01 | 对象属性断点 | A · 已实装且自动化证实 | K09 | T018, T019 | 指定对象/属性及只读属性观察已测。 |
| B02 | 源对象事件断点 | A · 已实装且自动化证实 | K54,Q19 | T365, T366, T367, T368, T369, T370, T371, T372, T373, T374, T375, T376, T391 | SourceObjectId 与旧 ObjectId 接收节点独立取交集，真实 Source、同 args 连续／嵌套／递归 Raise 的同步路由收尾、弱源回收、GUI 和 stdio 已测；不提供实例 handler 归因。 |
| B03 | 全局事件断点 | A · 已实装且自动化证实 | K09 | T101, T028 | 全局事件命中来源和解除监听已测。 |
| B04 | 路由链节点断点 | A · 已实装且自动化证实 | K09 | T022 | 可按接收者观察子源route，收尾去重并保留摘要。 |
| B05 | 启停、命中计数与条件 | A · 已实装且自动化证实 | K09,K16 | T018, T020, T100 | 六种比较/启停/重置及无效更新保留旧监听已测。 |
| B06 | 附加调试器时暂停 | P · 部分实装 | K09 | T024 | 未附加调试器保护已测；真实IDE附加后的Break交互未执行。 |
| B07 | 命中时记录日志 | A · 已实装且自动化证实 | K09 | T029 | 命中日志与同步重入抑制有实测。 |
| B08 | 命中一次自动移除 | A · 已实装且自动化证实 | K09 | T023 | 条件满足后一次移除并释放监听。 |

### 日志

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| L01 | 默认捕获警告和错误 | A · 已实装且自动化证实 | K08 | T001, T002 | Warning默认值及保留原/后来替换sink有实测。 |
| L02 | 按级别、消息、参数筛选 | A · 已实装且自动化证实 | K08,K26 | T001, T215, T223, T234 | 类别／消息模板或预览／同一参数名称和值按 AND 在入队前筛选；实际 GUI 与 stdio 捕获验证拒绝项不进入历史。 |
| L03 | 结构化参数与异常详情 | A · 已实装且自动化证实 | K26,K30 | T215, T216, T217, T218, T223, T235, T236, T237 | 保留原模板、类型／可用性参数、异常图、scope 和 EventId；跨字段 UTF-8 与完整快照预算有真实管道回归，缺失与截断明确，不保留业务对象。 |
| L04 | 从日志源跳回控件 | A · 已实装且自动化证实 | K26 | T219, T223, T224 | 后台日志来源经弱 ID 定位真实控件，实际按钮点击跳转与已回收来源提示、断连清理均已测。 |
| L05 | Microsoft ILoggerFactory 接入 | A · 已实装且自动化证实 | K27,Q09 | T216, T223 | 可选 DiagnosticsLoggerProvider 经真实 LoggerFactory 传递模板／事件／scope／异常；停止解除诊断订阅，应用 logger factory 和其他 provider 的所有权仍属调用者。 |
| L06 | 自定义 ILoggerObservable 适配 | P · 部分实装 | K26,K30,Q09 | T215, T219, T220, T221, T222, T238, T239 | 自研 IDiagnosticsLogSource 已有同步订阅、字节／数量界限、异常隔离、慢通知背压及释放；没有直接实现商业私有接口或提供现成 Serilog 等适配包。 |

### 资源

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| R01 | 应用级资源 provider 树 | P · 部分实装 | K10,K25 | T203, T206, T209, T212 | 应用／局部／style／merged／theme、共享与循环和遍历预算均有显式状态；非字典自定义 provider 或不公开的 style 仍不可枚举。 |
| R02 | 查看选中 scope 的条目 | P · 部分实装 | K25 | T203, T204, T210, T211 | 每页 50／最多 200、HasMore／扫描截断分开，条目保留实际 owner；不计算所有 provider 的资源解析胜出结果，跨页不冻结资源。 |
| R03 | Include Nested 聚合 | P · 部分实装 | K25 | T203, T206, T211, T212 | 已聚合所选字典的 merged／theme 并保留重复键、原路径／scope；自定义不可枚举 provider 显式说明，不合并所有父级作用域或计算唯一胜出值。 |
| R04 | 按 key/type 查找和排序 | P · 部分实装 | K25 | T204, T205, T207, T213 | key／type 筛选、Key／Type／Source 排序与分页已测，非字符串键以弱 ID 重读且不执行任意 ToString；复杂键没有通用比较／全资源解析排序语义。 |
| R05 | 修改已有运行时资源 | A · 已实装且自动化证实 | K10,K16 | T050, T051, T094 | 现有字符串键编辑、DynamicResource刷新、冲突保留及回滚已测。 |
| R06 | 局部控件资源边界 | B · 边界项不要求复制原版限制 | K10 | T052 | 支持节点局部/祖先scope，不复制原版GUI排除局部资源的规则。 |
| R07 | 新增/源码持久化边界 | B · 边界项不要求复制原版限制 | K10 | T050 | 已有条目仅临时编辑／回滚；目前新增资源／源码保存未实现。B 仅说明不照搬原版限制，不计这些能力已完成。 |

### 资产

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| A01 | 按程序集列出嵌入资产 | A · 已实装且自动化证实 | K11 | T053 | 列已加载AvaloniaResources程序集及URI元数据；最多2000。 |
| A02 | 按名称/路径筛选 | P · 部分实装 | K11,K37 | T095, T292 | 元数据按 URI／程序集本地筛选，选中才取预览；列表仍有 2000 项上限，大目录分页及筛选性能没有专项验收。 |
| A03 | 复制 avares URI | A · 已实装且自动化证实 | K37 | T053, T292 | 实际窗口剪贴板复制当前 URI，内容与所选资产一致；URI 打开与界面入口均有自动化。 |
| A04 | 导出资产 | A · 已实装且自动化证实 | K37,Q13 | T260, T282, T283, T284, T285, T286, T292 | 真实管道／远程 stdio 按 64 KiB 导出完整大文件，长度／SHA-256 双验；取消／写失败／断连／过期关闭流，文件同目录临时写后显式原子覆盖，GUI 取消／进度已测。 |
| A05 | 图片/字体/文本预览 | P · 部分实装 | K37,K38,Q13 | T288, T289, T290, T291, T293 | UTF-8 文本、PNG 元数据／实际像素与 Inter TTF 字节家族渲染和释放已测；实现支持有界图片头及 TTF／OTF，WOFF／WOFF2 明确不支持，不能泛化所有格式。 |
| A06 | 按需取内容及大小限制 | A · 已实装且自动化证实 | K37,K38 | T283, T286, T287, T288, T289, T291 | 1 MiB 字节预览、3 MiB 转义文本、GUI 64 Ki 字符、16 Mi 解码像素与 100 MiB 导出分开限制；未知总长度／截断与非 seek／超限有明确状态及帧预算测试。 |

### 指标

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| M01 | Counter 和 UpDownCounter | A · 已实装且自动化证实 | K12 | T063, T070 | 原始计数测量与语义标签已测，不冒充生命周期累计值。 |
| M02 | Histogram 与 P50/P90/P95 | A · 已实装且自动化证实 | K53,Q18 | T356, T357, T358, T359, T360, T361, T362, T363, T364, T390 | 对真实时间窗内仍保留原始观测计算 nearest_rank P50／P90／P95，GUI 展示 P90；Histogram 帧均值不替代原始分位，不宣称原版私有算法等价。 |
| M03 | 动态 provider 与 instrument | A · 已实装且自动化证实 | K12 | T062 | 已有/迟建仪表与稳定tag序列已测。 |
| M04 | Avalonia/Runtime及自定义源 | A · 已实装且自动化证实 | K12,K48,Q17 | T062, T073, T346 | 真实 Avalonia 五种 ms histogram 与 System.Runtime 样本已产生，复用真实 meter／保留缓存 IsEnabled，并报告版本／形状支持；自定义源回归继续保留。 |
| M05 | 轮询与显示帧间隔 | A · 已实装且自动化证实 | K53,Q18 | T356, T357, T358, T359, T360, T361, T362, T363, T364, T390 | GUI／RPC／MCP 配置轮询、通知、显示帧间隔；实际 DispatcherTimer 活动与图表刷新快慢有断言，不由配置回显代替生效证明。 |
| M06 | 帧聚合与历史窗口 | A · 已实装且自动化证实 | K53,Q18 | T356, T357, T358, T359, T360, T361, T362, T363, T364, T390 | 真实 UTC 时间窗／精确左边界／静默过期、停止冻结、公共采集原点帧与 raw 模式已测；Histogram 均值、其余帧末值，容量与时间损失分开，非完整全历史分布。 |
| M07 | Gauge 支持范围 | B · 边界项不要求复制原版限制 | K12 | T066 | 自研支持ObservableGauge有实测，不复制原版文档未支持限制。 |

### Profiler

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| F01 | 开始/停止及按类型聚合 | A · 已实装且自动化证实 | K21,K22,Q06 | T175, T176, T178, T181, T182, T184, T187, T188 | 独立 ActivityListener、三类别筛选、微秒耗时、retained_samples 分组、开始／停止／清空／重录已接 Agent／Client／GUI／MCP；结果固定、旧代活动拒绝、并发停止和断连释放已测。 |
| F02 | Style Matching 统计 | P · 部分实装 | K21,K22 | T175, T176 | 真实 AttachingStyle 的 SelectorResult、匹配／不匹配／未知与 NeverThisType 快拒样本已采；复杂选择器降级，窗口统计尚未证明等价原版 Attempts／Matches／FastReject 口径。 |
| F03 | Style Activators 统计 | P · 部分实装 | K21,K22 | T175 | 真实 EvaluatingStyle 活动和 class 添加／删除触发的 active／inactive 已测，区别于初始样式附加；未做实际 hover 与完整重评估计数验收。 |
| F04 | Resource Lookup 统计 | P · 部分实装 | K21,K22 | T175, T181, T182, T184, T186, T189 | 真实资源键、主题分组与耗时已测，长键／分隔符不会因显示截断混为同组；成功为 true。实际缺失资源的框架活动无失败 tag，保留未知，不能报告已证实失败次数。 |

### 3D composition 对照

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| D01 | 选定视觉子树快照 | P · 部分实装 | K13,K23,Q07 | T191, T192, T193, T197, T198, T202 | 几何模式保留；新 CompositorOwnDrawing 采真实 server 子树与独立自身 PNG，弱 ObjectId／捕获 ID 分开。effects／mask／复杂 clip／GPU／AOT 及精确组合承诺仍有限。 |
| D02 | 层平面与 Z-order | P · 部分实装 | K23,K24 | T192, T199, T200 | 采实际 ChildIndex／ZIndex 并验证基础重组像素、透视贴图和矩形祖先裁剪；3D 视图仍自研 Depth 分层，平面命中不等于透明像素或全部合成效果遮挡。 |
| D03 | 旋转、平移、缩放、复位 | A · 已实装且自动化证实 | K24 | T201 | 合成绘制模式通过真实指针验证旋转、独立右键平移、滚轮缩放、复位与正视、点击选择；不扩大成所有触控／平台手势验收。 |
| D04 | 渐变与边框开关 | A · 已实装且自动化证实 | K24 | T202, T232 | 真实 Skia 像素验证边框／渐变开关生效，场景快照不变；真实目标 Background 保持原值，显示设置只作用于工具端。 |
| D05 | 层间距与层范围 | A · 已实装且自动化证实 | K24 | T200 | 合成视图层间距改变投影角点，最小／最大深度筛选只保留指定层；这些参数不重新写入目标或重采图像。 |
| D06 | 模板/Application 不适用 | B · 边界项不要求复制原版限制 | K13 | T059, T060 | 以Control/有效Bounds为边界，不复制原版模板入口限制。 |

### MCP 接口

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| X01 | attach-to-app | A · 已实装且自动化证实 | K14 | T090 | 标准stdio显式pipe附加真实Agent；非原版id参数兼容。 |
| X02 | attach-to-file | A · 已实装且自动化证实 | K28,Q10 | T225, T226, T227, T228, T229, T230, T231, T233 | 真实 stdio 启动预览子进程，已构建项目／classful AXAML、独立控件库与 App 资源、输入／PNG、保存错误／恢复和 owned 清理已测；不自动构建或附加任意 IDE 设计进程。 |
| X03 | detach | A · 已实装且自动化证实 | K14 | T090 | stdio detach恢复最新绑定；每服务器单目标。 |
| X04 | tree | A · 已实装且自动化证实 | K14,K18,K61,Q15 | T089, T090, T401, T402, T403, T406, T408, T434, T435 | Visual／Logical／Merged 与递归预算保留；直接子项 1–2000 游标分页、总数／HasMore、分支变更／release 失效及真实 stdio 已测；旧 Agent 首次缺方法可显式本地分页，成功原生会话不再降级。单分支超过 20,000 明确失败，不代表虚拟化全量或跨快照原子一致。 |
| X05 | ancestors | A · 已实装且自动化证实 | K03,K14,K18 | T030, T032, T136 | 实际父链用于 GUI 定位；MCP 同 API，Merged 模板组／用户内容路径与子节点关系一致。 |
| X06 | search | A · 已实装且自动化证实 | K03,K14 | T030, T090 | stdio搜索、稳定句柄及后端遍历预算已测。 |
| X07 | screenshot | A · 已实装且自动化证实 | K03,K14 | T035, T090 | 实际96DPI PNG/native ImageContentBlock，超限明确失败。 |
| X08 | props | A · 已实装且自动化证实 | K04,K14 | T090 | 类型/来源/错误及完整Id；复杂对象另走object。 |
| X09 | set-prop | P · 部分实装 | K04,K14 | T090 | 设置/JSON null清除已测；无isXamlValue，非原版null/unset/XAML契约全兼容。 |
| X10 | styles | P · 部分实装 | K05,K14,K19 | T048, T090, T148, T153 | 真实帧、setter、结构化绑定和字段缺口已传出；完整胜者判定和所有框架表达式覆盖仍有限。 |
| X11 | pseudo-class | A · 已实装且自动化证实 | K03,K14 | T034 | 枚举/设置/reset底层已测，MCP直通；自然状态并发边界见T13。 |
| X12 | resources | P · 部分实装 | K10,K14,K25 | T052, T210, T234 | 旧接口保留，新增 resource-tree／query／entry 已用真实 stdio 验证分页／来源／弱键重读；完整自定义 provider／胜出语义仍有限。 |
| X13 | assets | A · 已实装且自动化证实 | K11,K14 | T053 | 列表只元数据，限定已加载程序集和2000项。 |
| X14 | open-asset | P · 部分实装 | K11,K37 | T053, T260, T287, T290 | 验证已加载程序集的实际 avares、1 MiB 预览及明确总长度／截断；完整导出独立工具已测，仍未覆盖全部媒体格式，也不绑定上一 assets 快照。 |
| X15 | input | P · 部分实装 | K17,K14 | T040, T038, T043, T185 | 8 类 Agent 输入与真实 stdio 文本／键盘／按钮单击已测；OS 输入、IME、完整拖放不在当前语义内。 |
| X16 | action | P · 部分实装 | K03,K14 | T033 | Focus/Enable/Disable底层已测；BringIntoView实际滚动及stdio action专项未证实。 |

### 工具壳与诊断

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| S01 | 工具主题与欢迎页偏好 | U · 待实现或未验证 | K16 | — | 固定主题/界面，无欢迎偏好和持久化设置。 |
| S02 | 最后窗口关闭时退出 | P · 部分实装 | K16 | T102 | 单窗口使用默认桌面lifetime关闭退出；Hide/Show/重开状态机未测。 |
| S03 | TopMost 模式 | U · 待实现或未验证 | K16 | — | 没有Topmost开关。 |
| S04 | 快捷键绑定 | P · 部分实装 | K02,K16 | T074 | Agent手势可配/拾取Esc已有，工具壳重绑UI和冲突检测缺失。 |
| S05 | 搜索结果及工具页导航 | P · 部分实装 | K03,K16 | T097 | 搜索结果点击定位已测；前后结果/工具键盘导航缺失。 |
| S06 | 隐藏面板、重置布局 | P · 部分实装 | K16 | T092 | 有分隔条/最小尺寸；面板隐藏/保存/重置未实现。 |
| S07 | Protocol Monitor | A · 已实装且自动化证实 | K57,K58,Q20 | T383, T384, T385, T386, T387, T388, T389 | 第十二页录制真实 Native RPC、关联／过滤／错误、停止／清空／重连；默认关闭正文，显式开启后脱敏且有界。非抓包、非 MCP stdio、非原版私有 wire。 |
| S08 | HTTP端口与监听配置 | P · 部分实装 | K31,K33,Q11 | T248, T249, T259 | 可选 SDK 提供监听地址／端口／TLS／Origin，Inspector endpoint／遮罩凭据和环境变量启动已测；GUI 监听配置、跨机信任和真实部署仍未验。 |
| S09 | 清空当前列表/刷新 | P · 部分实装 | K08,K09,K12,K16 | T004, T096 | 树/活动有独立刷新清空；Metrics 已有单图／全部 clear，但无当前工具上下文统一命令。 |
| S10 | 工具内部诊断日志 | P · 部分实装 | K01,K14,K16 | T133 | RPC code、LastError、状态/stderr已有；统一阶段/关联ID内部诊断缺失。 |

### 原生声明候选

| ID | 能力或边界 | 状态 | 当前代码/研究 | 测试/运行证据 | 已完成范围与剩余差异 |
| --- | --- | --- | --- | --- | --- |
| H01 | FreezePopups 弹层冻结入口 | P · 部分实装 | K62,Q23 | T410–T423 | 普通 Popup／标准 Flyout 的 Native／Overlay light-dismiss 可冻结并按所有权恢复；新弹层／根、绑定改写、卸载、断连及弱回收已测。ContextMenu 自有 handler、ToolTip 与 transient 服务仍可直接关闭，不能声称全部弹层自动关闭均冻结。 |
| H02 | 从模板父节点复制选择器 | A · 已实装且自动化证实 | K18,K43,K44,K61,Q15 | T173, T320, T325, T404, T405, T407, T409, T433 | 按 Classic 语义独立复制 owner 经 /template/ 到当前部件的真实路径，owner 自身 selector 明确为辅助入口；同一实例分别匹配、真实剪贴板和 RPC 中换选取消已测。共享 builder 直接 Match 所选目标，拒绝另一 StyleKey 对象唯一匹配的误报；自定义命名空间与不支持状态保留。 |
| H03 | 绑定 Culture/Parameter | A · 已实装且自动化证实 | K19,K63,Q05 | T148, T153, T159, T424–T427, T429–T432 | ConverterParameter 与保留的 ConverterCulture／来源分别展示；普通／Compiled／Multi／Template 真实转换与 ConvertBack、空配置、子文化及旧 JSON 已测；未知／自定义文化 getter 与上游缺字段明确降级，不用诊断线程文化补值。 |
| H04 | TargetNull/StringFormat/UpdateSourceTrigger | A · 已实装且自动化证实 | K19,K63,Q05 | T148, T154, T159, T164, T424, T426, T427, T428, T432 | TargetNull 与 null／Unset 原有证据保留；普通／编译绑定实际规范化格式、Multi 格式包装器，以及 Default／PropertyChanged／LostFocus／Explicit 真实写源时机已测；不适用表达式明示 NotApplicable，不声称恢复被框架忽略的原始声明。 |
| H05 | ResourceKey 和 Source 导航状态 | A · 已实装且自动化证实 | K19,K36,Q05 | T274, T275, T276, T277, T279, T280, T281 | Native 真实 ResourceKey／host 与 Source 分开；字符串键保留多个 owner 候选，非字符串弱键打开对象，缺失／截断／foreign／expired 明确不可用。无 SourceVersion，不推断原子观察或 winner。 |
| H06 | 协议录制、参数与响应视图 | A · 已实装且自动化证实 | K57,K58,Q20 | T383, T384, T385, T386, T387, T388, T389 | 真实收发路径提供参数／响应 JSON 投影、实际帧字节与单调耗时；双向相同 ID、取消迟回、类型解码失败、正文／历史预算和清空关联边界均有测试。 |
| H07 | 生成 MCPB 及编辑器安装入口 | U · 待实现或未验证 | Q02 | — | 只有原版名字线索；无自研MCPB/编辑器安装。 |
| H08 | CompactMode | U · 待实现或未验证 | K16 | — | 无CompactMode设置/小屏专项。 |
| H09 | 更新状态与变更日志入口 | U · 待实现或未验证 | K16 | — | 没有更新检查或变更日志视图。 |
| H10 | 欢迎页应用发现与接入片段 | P · 部分实装 | K02,K14,K16 | T089 | 管道发现和接入文档已有；缺欢迎页/三类失败精确分类。 |
| H11 | 布局盒模型分区交互 | A · 已实装且自动化证实 | K55,K56,Q14 | T377, T378, T379, T380, T381, T382 | 真实 BoxModel 指针经过 Margin／Border／Padding／Content 与单边时只绘对应分区；旋转／缩放像素、快速切换／旧移出／换选／清空／隐藏恢复／断连已测，面板只读。 |
| H12 | 资产列表/网格切换 | U · 待实现或未验证 | K11,K16 | — | 只有资产列表，无网格切换及状态保留验证。 |
| H13 | 断点重置命令 | A · 已实装且自动化证实 | K09,K16 | T020, T100 | ResetHitCount保留定义，UI有命令且更新测试证实。 |
| H14 | 事件全停与恢复默认 | P · 部分实装 | K45,K46,K49 | T331, T339 | Stop 解除监听且不再收新路由，默认六组入口／保存恢复已有；全停→恢复六组→重启后仅捕获预期集合的完整 GUI 路径尚无专项。 |
| H15 | 指标图清空与序列同步 | A · 已实装且自动化证实 | K53,Q18 | T356, T357, T358, T359, T360, T361, T362, T363, T364, T390, T392 | 多图共用 UTC 窗口横轴、按时间定位；单图／全部 Clear 保留采集与身份、连续序号及停止后清空；旧 pull／通知不得复活数据，不以样本序伸缩横轴。 |
| H16 | Composition Refresh | A · 已实装且自动化证实 | K23,K24 | T202 | 真实管道验证目标由 Red 改 Green 后再次捕获得到 Green PNG，旧 scene 已释放；断连再释放新 scene，不复用旧图像。 |

## 代码与研究索引

下列索引是当前实现位置，不是从名字推断行为。每行状态以对应方法体、DTO和测试断言综合判断。

| 索引 | 文件 | 检查到的行为/边界 |
| --- | --- | --- |
| K01 | [AgentSession.cs](../src/AvaloniaDevTool.Agent/AgentSession.cs) | GetRoots/GetChildren、对象观察、Handle、断连清理；配套DiagnosticsAgent和Protocol.RpcPeer。 |
| K02 | [DiagnosticsAgent.Launcher.cs](../src/AvaloniaDevTool.Agent/DiagnosticsAgent.Launcher.cs) | InitializeLauncher/DisposeLauncher；DiagnosticsAgentOptions.LaunchGesture/InspectorExecutablePath。 |
| K03 | [AgentSession.Exploration.cs](../src/AvaloniaDevTool.Agent/AgentSession.Exploration.cs) | 搜索、GetAncestors、ElementAction、伪类及PNG；Focus跟随见AgentSession.Focus.cs。 |
| K04 | [AgentSession.cs](../src/AvaloniaDevTool.Agent/AgentSession.cs) | GetProperties/SetProperty/ClearProperty；PropertyValues文本转换，GUI PropertyRow保留草稿。 |
| K05 | [Avalonia12DiagnosticsAdapter.cs](../src/AvaloniaDevTool.Agent/Avalonia12DiagnosticsAdapter.cs) | Inspect、GetValueStoreDiagnostic、BindingDiagnosticInfo；版本特定PrivateApi反射边界。 |
| K06 | [RuntimeInspectionService.cs](../src/AvaloniaDevTool.Agent/RuntimeInspectionService.cs) | GetMembers/GetItems、弱对象值、分页、逐项异常；Client.Runtime和CLI object接入。 |
| K07 | [HighlightAdorner.cs](../src/AvaloniaDevTool.Agent/HighlightAdorner.cs) | Bounds绘制；AgentSession的拾取、高亮和Esc模式。 |
| K08 | [ActivityService.cs](../src/AvaloniaDevTool.Agent/ActivityService.cs) | Avalonia ILogSink组合、路由事件观察、历史/背压/Start/Stop/Clear。 |
| K09 | [BreakpointService.cs](../src/AvaloniaDevTool.Agent/BreakpointService.cs) | Create/Update/Remove、计数条件、route收尾、日志和Debugger保护；GUI Breakpoints分部。 |
| K10 | [ResourceInspectionService.cs](../src/AvaloniaDevTool.Agent/ResourceInspectionService.cs) | GetTrees/GetEntries/Set/Reset/Dispose；字典/主题/Styles/节点scope及冲突回滚。 |
| K11 | [AssetInspectionService.cs](../src/AvaloniaDevTool.Agent/AssetInspectionService.cs) | List/Open、已加载程序集、avares/1MiB正文；Inspector AssetsView及选择后预览。 |
| K12 | [MetricsService.cs](../src/AvaloniaDevTool.Agent/MetricsService.cs) | MeterListener、Observable轮询、原始样本及保留窗统计；Inspector Metrics分部与MetricLineChart。 |
| K13 | [LayoutInspectionService.cs](../src/AvaloniaDevTool.Agent/LayoutInspectionService.cs) | VisualGeometry；Inspector LayerSceneProjection/LayerSceneControl及MainWindowViewModel.Layout。 |
| K14 | [InspectorTools.cs](../src/AvaloniaDevTool.Cli/InspectorTools.cs) | 官方MCP SDK工具映射；配套ToolSession及Runtime/Metrics/Breakpoints/Input分部。 |
| K15 | [PreviewSession.cs](../src/AvaloniaDevTool.Previewer/PreviewSession.cs) | classless／已构建项目文件预览核心，配套 Loader／AssemblyScope／Watcher／Window；MCP 已由 ToolSession 管理该宿主，仍不等同 IDE 现有设计进程附加。 |
| K16 | [MainWindow.axaml](../src/AvaloniaDevTool.Inspector/MainWindow.axaml) | 十二个工具 tab 及窗口壳；功能以相应 ViewModel 行为而非 TabItem 名称判定。 |
| K17 | [InputInspectionService.cs](../src/AvaloniaDevTool.Agent/InputInspectionService.cs) | Send及会话独立Pointer；Client.Input、CLI InspectorTools.Input。 |
| K18 | [MergedTree.cs](../src/AvaloniaDevTool.Agent/MergedTree.cs) | 合并父子关系、模板组／内容插槽、弱合成对象、重模板与脱离检测；Inspector MainWindowViewModel.Merged 导航、CLI tree 模板范围。 |
| K19 | [BindingDetailsReader.cs](../src/AvaloniaDevTool.Agent/BindingDetailsReader.cs) | BindingDetailsContracts、逐字段兼容探测及预算；Inspector BindingDiagnosticView／BindingDetailsView；XamlBindingDetailsMapper 保留上游证据。 |
| K20 | [MainWindowViewModel.Runtime.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.Runtime.cs) | RuntimeView／RuntimeRowViewModel、按需加载、面包屑、分页、循环和请求代数／会话保护；没有 CLR setter。 |
| K21 | [ProfilerService.cs](../src/AvaloniaDevTool.Agent/ProfilerService.cs) | ActivityListener、开始／停止／清空、有界样本／标签／分组／in-flight、代数保护；Client.Profiler、Inspector ProfilerView 与 CLI InspectorTools.Profiler 共用。 |
| K22 | [Avalonia12ActivityAdapter.cs](../src/AvaloniaDevTool.Agent/Avalonia12ActivityAdapter.cs) | Avalonia 12 活动源初始化兼容边界与常见框架选择器描述；不修改 IsEnabled，不调用任意业务 ToString。 |
| K23 | [CompositionInspectionService.cs](../src/AvaloniaDevTool.Agent/CompositionInspectionService.cs) | 单会话异步捕获；Avalonia12CompositionAdapter 负责真实 server 层级／自身 RenderCore PNG、clip 元数据与完整帧预算。 |
| K24 | [CompositionSceneControl.cs](../src/AvaloniaDevTool.Inspector/CompositionSceneControl.cs) | CompositionScene 图像所有权、透视映射、祖先 clip、相机／平移／范围和命中；MainWindowViewModel.Composition 负责采集代数与释放。 |
| K25 | [ResourceInspectionService.cs](../src/AvaloniaDevTool.Agent/ResourceInspectionService.cs) | ResourceQueryContracts 的树／条目／弱键重读、查询预算与来源；Inspector ResourcesView 分页／筛选／原 owner 编辑；CLI Resources 分部。 |
| K26 | [LogSnapshots.cs](../src/AvaloniaDevTool.Agent/LogSnapshots.cs) | 结构化日志快照与安全有界预览；DiagnosticsLogSource 订阅，ActivityService 入队前筛选；ActivityView 日志详情／来源跳转。 |
| K27 | [DiagnosticsLoggerProvider.cs](../src/AvaloniaDevTool.Logging/DiagnosticsLoggerProvider.cs) | 可选 Microsoft ILoggerProvider／ISupportExternalScope，经公共 API 提供原模板、结构状态、EventId 和有界 scope。 |
| K28 | [ToolSession.cs](../src/AvaloniaDevTool.Cli/ToolSession.cs) | attach-to-file 控制管道、ready／PID 验证、预览进程所有权和清理；入口 InspectorTools.Preview。 |
| K29 | [Program.cs](../src/AvaloniaDevTool.Previewer/Program.cs) | 目标 AppBuilder／SetupWithoutStarting 与设计模式守卫；PreviewDocumentLoader 保留 x:Class、项目 URI 和官方预览转换。 |
| K30 | [AgentSession.LogNotifications.cs](../src/AvaloniaDevTool.Agent/AgentSession.LogNotifications.cs) | 等待实际 RPC 写入的异步通知适配；有界 DTO 引用队列、单 pump、独立损失及停止／清空／释放代数由 ActivityService 管理。 |
| K31 | [DiagnosticsRemoteServer.cs](../src/AvaloniaDevTool.Remote/DiagnosticsRemoteServer.cs) | 可选 Kestrel、upgrade 前 token／Origin／会话门、监听及连接释放；Options 显式配置地址／端口／证书。 |
| K32 | [RpcWebSocketStream.cs](../src/AvaloniaDevTool.Protocol/RpcWebSocketStream.cs) | 二进制按需分片流、消息上限、Close；RpcPeer 分离 reader／handler，并限制入站请求及并发发送。 |
| K33 | [DiagnosticsClient.Remote.cs](../src/AvaloniaDevTool.Client/DiagnosticsClient.Remote.cs) | Native WS URI／凭据约束、默认 TLS 验证和 5 秒连接握手；Inspector／CLI 复用同一 Client。 |
| K34 | [PropertyPresentationAdapter.cs](../src/AvaloniaDevTool.Agent/PropertyPresentationAdapter.cs) | CLR 显式读取、backing property 去重、分类／编辑器元数据；AgentSession.Properties 负责 weak＋version 属性值预览。 |
| K35 | [PropertyValueEditor.axaml.cs](../src/AvaloniaDevTool.Inspector/PropertyValueEditor.axaml.cs) | 官方 ColorPicker 与各类型 Draft 编辑；PropertyRowViewModel／MainWindowViewModel.Properties 维护排序、组状态、行身份与预览释放。 |
| K36 | [MainWindowViewModel.BindingNavigation.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.BindingNavigation.cs) | Source／DataContext／ResourceKey 导航、原绑定上下文、DTO／Session 请求归属；资源候选保持真实 owner，筛选不修改原始诊断。 |
| K37 | [AssetExportService.cs](../src/AvaloniaDevTool.Agent/AssetExportService.cs) | 会话 export token、按序 64 KiB 读取、哈希／TTL／限额；Client.Assets 原子文件与 Inspector AssetsView 复制／保存／取消。 |
| K38 | [AssetMetadataReader.cs](../src/AvaloniaDevTool.Agent/AssetMetadataReader.cs) | 有界图像头／DPI／像素预算与字体识别；MainWindowViewModel.Assets 用实际字节注册字体集合、限制文本和释放预览。 |
| K39 | [InspectorLaunchOptions.cs](../src/AvaloniaDevTool.Inspector/InspectorLaunchOptions.cs) | --endpoint／--token-env 只读命名环境变量，拒绝后端混用；MainWindow 令牌遮罩与 Native WS 连接入口。 |
| K40 | [PropertyErrors.cs](../src/AvaloniaDevTool.Agent/PropertyErrors.cs) | 属性／预览错误的 2048 字符摘要、8 层／引用循环界限、自定义异常 getter 不可用与聚合不展开。 |
| K41 | [OverlayService.cs](../src/AvaloniaDevTool.Agent/OverlayService.cs) | 根尺寸诊断表面、弱目标与 33 ms 跟踪；OverlayGeometry 生成 RootDips 四角／有符号区域／clip／标尺，不写业务布局值。 |
| K42 | [AgentSession.TreeWorkflows.cs](../src/AvaloniaDevTool.Agent/AgentSession.TreeWorkflows.cs) | 分树型弱分支观察、事件变更、诊断子树过滤及 observe／release；属性观察独立。 |
| K43 | [ElementSelectorBuilder.cs](../src/AvaloniaDevTool.Agent/ElementSelectorBuilder.cs) | 实际 Avalonia parser／matcher、模板路径、匹配／唯一／预算及不支持状态；元数据使用真实会话弱身份。 |
| K44 | [MainWindowViewModel.TreeWorkflows.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.TreeWorkflows.cs) | GUI 递归／模板范围／取消、缓存折叠／分批释放及当前窗口剪贴板 selector／元数据。 |
| K45 | [ActivityService.EventRoutes.cs](../src/AvaloniaDevTool.Agent/ActivityService.EventRoutes.cs) | 真实注册目录、弱 args 关联、策略完成／区间观察、200 次／2 MiB／TTL 与原有通知泵。 |
| K46 | [MainWindowViewModel.EventRoutes.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.EventRoutes.cs) | EventRoutesView 组／单项选择、真实路径跳转、失效提示、明示 Handled 限制；保留路由选择与详情。 |
| K47 | [RendererDiagnosticsService.cs](../src/AvaloniaDevTool.Agent/RendererDiagnosticsService.cs) | 四个真实 TopLevel 标志、窗口／全部／新根、原值／所有权／同步冲突恢复；Inspector RendererDiagnosticsView 与 CLI 同服务。 |
| K48 | [Avalonia12MetricsAdapter.cs](../src/AvaloniaDevTool.Agent/Avalonia12MetricsAdapter.cs) | 精确 12.1.4 真实 Meter／histogram 初始化、复用及显式支持状态；不改 cached IsEnabled，不造假源。 |
| K49 | [EventSelectionDefaultsStore.cs](../src/AvaloniaDevTool.Inspector/EventSelectionDefaultsStore.cs) | 客户端显式保存／载入当前事件选择，64 KiB UTF-8 原子文件，仅 ID／组／IncludeAll，无连接凭据。 |
| K50 | [MainWindowViewModel.Overlay.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.Overlay.cs) | OverlayView 参数、所选／悬停恢复、取消／代数与当前快照；MainWindow 树行指针转发。 |
| K51 | [BrushValues.cs](../src/AvaloniaDevTool.Agent/BrushValues.cs) · [AgentSession.Brushes.cs](../src/AvaloniaDevTool.Agent/AgentSession.Brushes.cs) | 四种内置画刷 DTO／校验／新实例，结构化 RPC 继续使用可释放属性覆盖。 |
| K52 | [BrushEditorViewModel.cs](../src/AvaloniaDevTool.Inspector/BrushEditorViewModel.cs) · [BrushEditorView.axaml](../src/AvaloniaDevTool.Inspector/BrushEditorView.axaml) | 类型约束、色标／坐标／半径／角度草稿与本地预览，显式 Apply 及刷新保留。 |
| K53 | [MetricsService.cs](../src/AvaloniaDevTool.Agent/MetricsService.cs) · [MainWindowViewModel.Metrics.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.Metrics.cs) | TimeProvider 时间窗、公共原点帧、原始分位／clear，GUI 同步横轴、按 ID 保留图行／按钮与异步修订保护。 |
| K54 | [BreakpointService.cs](../src/AvaloniaDevTool.Agent/BreakpointService.cs) · [BreakpointContracts.cs](../src/AvaloniaDevTool.Protocol/BreakpointContracts.cs) | 独立 Source／receiver 条件、弱源快照与按真实阶段次数收尾，兼容旧定义。 |
| K55 | [OverlayControlInformation.cs](../src/AvaloniaDevTool.Agent/OverlayControlInformation.cs) · [OverlayGeometry.cs](../src/AvaloniaDevTool.Agent/OverlayGeometry.cs) | 精确框架文字／图像元数据，密码跳过、未知状态与分区几何。 |
| K56 | [BoxModelView.cs](../src/AvaloniaDevTool.Inspector/BoxModelView.cs) · [MainWindowViewModel.Overlay.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.Overlay.cs) | 只读盒模型真实指针侧区联动、序列化选项恢复与选择／hover 代数。 |
| K57 | [RpcTrace.cs](../src/AvaloniaDevTool.Protocol/RpcTrace.cs) · [RpcPeer.cs](../src/AvaloniaDevTool.Protocol/RpcPeer.cs) | 实际帧观察、连接／方向／请求关联、调用终态、有界脱敏 JSON 和历史预算。 |
| K58 | [MainWindowViewModel.ProtocolMonitor.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.ProtocolMonitor.cs) · [ProtocolMonitorView.axaml](../src/AvaloniaDevTool.Inspector/ProtocolMonitorView.axaml) | 本地录制按钮、默认元数据模式、过滤／详情和 200 ms 快照；XamlMcp 禁用。 |
| K59 | [ProtocolJsonContext.cs](../src/AvaloniaDevTool.Protocol/ProtocolJsonContext.cs) · [AotSample](../samples/AvaloniaDevTool.AotSample/AvaloniaDevTool.AotSample.csproj) · [Test-NativeAot.ps1](../tests/scripts/Test-NativeAot.ps1) | 显式 wire metadata、AOT resolver 与真实 trimmed／Native AOT 消费进程；AotSmoke 使用独立 Client 检查并清理自有进程。 |
| K60 | [ContextualProperties.cs](../src/AvaloniaDevTool.Agent/ContextualProperties.cs) · [MainWindowViewModel.Properties.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.Properties.cs) | 直接视觉父白名单、可选 DTO context、换父失效；GUI 本地筛选保留隐藏行及草稿，props 显式参数保持旧默认。 |
| K61 | [AgentSession.TreePaging.cs](../src/AvaloniaDevTool.Agent/AgentSession.TreePaging.cs) · [MainWindowViewModel.TreePaging.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.TreePaging.cs) · [InspectorTools.TreeWorkflows.cs](../src/AvaloniaDevTool.Cli/InspectorTools.TreeWorkflows.cs) | 直接子项弱观察游标、边界／失效、GUI 页追加与定位、会话级三态能力／旧读取前缀校验、从模板父到部件路径及 owner 辅助入口、等待后的选择／树／会话检查和 MCP 分页。 |
| K62 | [PopupFreezeService.cs](../src/AvaloniaDevTool.Agent/PopupFreezeService.cs) · [MainWindowViewModel.PopupFreeze.cs](../src/AvaloniaDevTool.Inspector/MainWindowViewModel.PopupFreeze.cs) | 公开 light-dismiss 当前值覆盖、弱根／弹层、即时新弹层接管、配置所有权／冲突恢复，GUI／MCP 共用服务。 |
| K63 | [BindingDetailsReader.cs](../src/AvaloniaDevTool.Agent/BindingDetailsReader.cs) · [BindingDetailsContracts.cs](../src/AvaloniaDevTool.Protocol/BindingDetailsContracts.cs) · [BindingDetailsView.axaml](../src/AvaloniaDevTool.Inspector/BindingDetailsView.axaml) | 保留的文化配置及来源、实际格式／触发器、NotApplicable 和父／子表达式呈现；外部映射保留字段缺口。 |
| Q01 | [diagnostic-services-deep-analysis.md](../docs/research/diagnostic-services-deep-analysis.md) | 原版SDK服务的静态证据与未恢复边界。 |
| Q02 | [native-deep-analysis.md](../docs/research/native-deep-analysis.md) | 原版MCP初始工具目录、UI声明候选、嵌入预览器CIL；原生UI/3D方法仍未还原。 |
| Q03 | [open-source-agent-tools-audit.md](../docs/research/open-source-agent-tools-audit.md) | CDP/Declarative/XamlMcp源码与许可审计；另见open-source-reuse-audit.md的Classic/官方核查。 |
| Q04 | [merged-tree.md](merged-tree.md) | 固定原版 DLL 哈希／MVID 的精选 REA CIL 静态语义；自研模板模型、运行测试及不同证据层级。 |
| Q05 | [binding-diagnostics.md](binding-diagnostics.md) | 公开 XamlMcp 模块复用选择、实际 API／引用形状探针、原生兼容边界与专项结果。 |
| Q06 | [profiler.md](profiler.md) | 框架开源活动源、独立 ActivityListener 采用、窗口统计、未知结果与具体专项证据。 |
| Q07 | [composition-capture.md](composition-capture.md) | 公共 Avalonia 12.1.4 精确提交源码、own drawing／全子树边界、真实 Skia 像素与资源生命周期证据。 |
| Q08 | [resources.md](resources.md) | 公开资源 provider API 与开源模块参考；查询／分页／来源语义和不可枚举／非 winner 限制。 |
| Q09 | [logging.md](logging.md) | Microsoft 公共日志 provider API、自研可订阅源、DTO 可用性、安全预算与执行证据。 |
| Q10 | [preview-attach.md](preview-attach.md) | Avalonia 官方 designer／runtime loader 依据、已构建项目的实际 stdio 附加及进程生命周期。 |
| Q11 | [remote-transport.md](remote-transport.md) | .NET 公共 WS／Kestrel 依据、Native RPC 共用会话门与真实 Windows loopback 证据；未测部署单列。 |
| Q12 | [properties.md](properties.md) | 默认 getter 惰性、统一 CLR／Avalonia、ColorPicker／四种内置画刷草稿与真实属性值预览专项。 |
| Q13 | [assets.md](assets.md) | 预览／解码／完整分块导出的独立预算、媒体可用性、哈希／原子写入和执行证据。 |
| Q14 | [overlays.md](overlays.md) | 公开变换／布局／Adorner 源码依据，真实 Skia 像素、DIP／屏幕像素、专用信息、box 分区 hover 语义及弱生命周期。 |
| Q15 | [tree-workflows.md](tree-workflows.md) | 事件观察／释放、递归与实际 selector 验证、预算与 clipboard／BringIntoView 的执行范围。 |
| Q16 | [events.md](events.md) | 公开 EventRoute／Raised／RouteFinished 顺序、关联与 Handled 观察界限、默认存储及资源预算。 |
| Q17 | [renderer-diagnostics.md](renderer-diagnostics.md) | 公开 renderer 标志、真实像素／恢复、精确框架 meter 源与 cached switch 保留证据。 |

| Q18 | [metrics.md](metrics.md) | 真实时间窗、nearest-rank 原始分位、帧末／均值、同步图与实际间隔专项范围。 |
| Q19 | [breakpoints.md](breakpoints.md) | Source 与 receiver 独立合同、公开 RouteFinished 聚合与同参数递归／生命周期证据。 |
| Q20 | [protocol-monitor.md](protocol-monitor.md) | Native 本地观察、取消／迟回／解码失败、JSON 脱敏／预算及不复制原版 wire 的边界。 |
| Q21 | [native-aot.md](native-aot.md) | 本机工具链、真实 trimmed／Native AOT 消费进程、严格发布失败与各项未验反射边界。 |
| Q22 | [contextual-properties.md](contextual-properties.md) | Avalonia 12.1.4 四类父布局公开消费规则、未知保留与 GUI／MCP 专项证据。 |
| Q23 | [popup-freeze.md](popup-freeze.md) | Classic 公开目标、Avalonia 公开关闭路径／所有权 API，真实 Native／Overlay 输入矩阵和不能拦截的内部服务关闭。 |

## 测试名称索引

这些是源码名称索引；执行口径见上文。Theory可能展开多例，方法数不等于已运行用例数。InputInspectionTests在测试fixture先生成真实Skia Headless帧，让Button释放时走真实hit-test；产品服务没有直接执行Button命令。

| 索引 | 测试源文件和精确方法名 |
| --- | --- |
| T001 | [ActivityIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ActivityIntegrationTests.cs) · Log_capture_preserves_existing_sink_and_applies_capture_level |
| T002 | [ActivityIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ActivityIntegrationTests.cs) · Stopping_capture_does_not_replace_a_new_application_sink |
| T003 | [ActivityIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ActivityIntegrationTests.cs) · Background_log_flood_is_bounded_and_notifications_run_on_ui_thread |
| T004 | [ActivityIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ActivityIntegrationTests.cs) · Clear_keeps_capture_running_and_resets_buffer_and_loss_count |
| T005 | [ActivityIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ActivityIntegrationTests.cs) · Routed_events_include_route_nodes_final_handled_state_and_source_filter |
| T006 | [ActivityIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ActivityIntegrationTests.cs) · Event_name_filter_does_not_capture_unrelated_events |
| T007 | [ActivityIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ActivityIntegrationTests.cs) · Invalid_settings_leave_existing_capture_running |
| T008 | [ActivityIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ActivityIntegrationTests.cs) · Pending_log_entries_do_not_keep_source_objects_alive |
| T009 | [AgentIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AgentIntegrationTests.cs) · Named_pipe_session_exposes_roots_children_and_property_values |
| T010 | [AgentIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AgentIntegrationTests.cs) · Diagnostic_override_preserves_live_binding_and_clear_reveals_latest_value |
| T011 | [AgentIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AgentIntegrationTests.cs) · Invalid_and_readonly_edits_return_errors_without_changing_target |
| T012 | [AgentIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AgentIntegrationTests.cs) · Disconnect_removes_overrides_and_new_connection_has_a_new_session |
| T013 | [AgentIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AgentIntegrationTests.cs) · Changing_materialized_children_invalidates_their_parent |
| T017 | [AgentIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AgentIntegrationTests.cs) · Picking_uses_real_pointer_input_highlights_selection_and_does_not_click_button |
| T018 | [BreakpointIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointIntegrationTests.cs) · Property_breakpoints_apply_all_hit_count_comparisons_and_keep_summary |
| T019 | [BreakpointIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointIntegrationTests.cs) · Readonly_properties_can_be_observed_with_complete_owner_id |
| T020 | [BreakpointIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointIntegrationTests.cs) · Update_can_enable_disable_reset_and_replace_member_without_stale_listener |
| T022 | [BreakpointIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointIntegrationTests.cs) · Routed_event_is_counted_once_after_tunnel_and_bubble_complete |
| T023 | [BreakpointIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointIntegrationTests.cs) · One_shot_removes_listener_only_after_comparison_matches |
| T024 | [BreakpointIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointIntegrationTests.cs) · Suspension_requires_attached_debugger_and_disabled_configuration_cannot_be_enabled_without_one |
| T028 | [BreakpointIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointIntegrationTests.cs) · Dispose_releases_global_routed_event_subscription |
| T029 | [BreakpointIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointIntegrationTests.cs) · Logging_observer_reentrancy_does_not_create_breakpoint_feedback_loop |
| T030 | [ExplorationIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ExplorationIntegrationTests.cs) · Search_preserves_duplicate_names_and_ancestor_paths_identify_real_objects |
| T032 | [ExplorationIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ExplorationIntegrationTests.cs) · Paths_reject_expired_ids_and_live_objects_detached_from_tree |
| T033 | [ExplorationIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ExplorationIntegrationTests.cs) · Element_actions_preserve_enabled_binding_and_focus_returns_actual_result |
| T034 | [ExplorationIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ExplorationIntegrationTests.cs) · Pseudo_class_changes_are_enumerable_resettable_and_reverted_on_disconnect |
| T035 | [ExplorationIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ExplorationIntegrationTests.cs) · Screenshot_returns_real_png_with_exact_arranged_dimensions |
| T038 | [InputInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/InputInspectionTests.cs) · Text_and_key_events_use_real_textbox_editing_and_modifier_behavior |
| T040 | [InputInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/InputInspectionTests.cs) · Click_raises_pointer_events_and_exactly_one_business_click |
| T043 | [InputInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/InputInspectionTests.cs) · Pointer_and_wheel_preserve_local_coordinates_after_real_visual_transform |
| T048 | [InspectionIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/InspectionIntegrationTests.cs) · Style_snapshot_reports_style_frames_and_live_binding_description |
| T049 | [InspectionIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/InspectionIntegrationTests.cs) · Invalid_binding_exposes_error_state_without_replacing_the_binding |
| T050 | [InspectionIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/InspectionIntegrationTests.cs) · Resource_edit_updates_dynamic_resource_and_dispose_restores_original |
| T051 | [InspectionIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/InspectionIntegrationTests.cs) · Resource_reset_preserves_a_newer_application_write |
| T052 | [InspectionIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/InspectionIntegrationTests.cs) · Resource_dictionary_scope_preserves_theme_and_merged_structure |
| T053 | [InspectionIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/InspectionIntegrationTests.cs) · Asset_preview_reads_embedded_text_and_rejects_other_uri_schemes |
| T054 | [LayoutInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/LayoutInspectionTests.cs) · Snapshot_preserves_parent_links_stable_ids_and_nested_layout_offsets |
| T055 | [LayoutInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/LayoutInspectionTests.cs) · Snapshot_uses_real_scale_rotation_and_translation_in_root_coordinates |
| T057 | [LayoutInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/LayoutInspectionTests.cs) · Depth_and_node_limits_report_only_actual_omissions |
| T059 | [LayoutInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/LayoutInspectionTests.cs) · Invalid_limits_expired_objects_and_unsupported_objects_fail_explicitly |
| T060 | [LayoutInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/LayoutInspectionTests.cs) · Detached_and_zero_sized_roots_are_rejected |
| T062 | [MetricsIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsIntegrationTests.cs) · Existing_and_new_instruments_are_filtered_and_tags_form_stable_series |
| T063 | [MetricsIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsIntegrationTests.cs) · Every_supported_numeric_type_is_captured_with_an_exact_raw_representation |
| T064 | [MetricsIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsIntegrationTests.cs) · Sample_history_and_notification_backlog_have_independent_bounds |
| T066 | [MetricsIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsIntegrationTests.cs) · Observable_gauges_are_polled_and_notifications_are_delivered_on_ui_thread |
| T067 | [MetricsIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsIntegrationTests.cs) · Stop_and_dispose_silence_callbacks_and_restart_creates_a_fresh_capture |
| T070 | [MetricsIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsIntegrationTests.cs) · Notification_batches_are_bounded_and_observable_counters_keep_reported_semantics |
| T073 | [MetricsIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsIntegrationTests.cs) · Named_pipe_metrics_api_publishes_samples_and_stop_retains_a_queryable_snapshot |
| T074 | [NavigationIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/NavigationIntegrationTests.cs) · Configured_launch_gesture_is_scoped_debounced_and_released |
| T075 | [NavigationIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/NavigationIntegrationTests.cs) · Focus_following_selects_real_focus_and_can_be_stopped |
| T076 | [RuntimeInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RuntimeInspectionTests.cs) · Control_datacontext_and_nested_cycles_keep_stable_weak_object_ids |
| T077 | [RuntimeInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RuntimeInspectionTests.cs) · Members_are_deduplicated_by_kind_and_name_and_keep_declaring_owner |
| T078 | [RuntimeInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RuntimeInspectionTests.cs) · Getter_errors_are_isolated_and_indexers_static_and_private_getters_are_excluded |
| T083 | [RuntimeInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RuntimeInspectionTests.cs) · Lists_page_null_simple_complex_and_self_references |
| T085 | [RuntimeInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RuntimeInspectionTests.cs) · Arbitrary_enumerable_is_explicitly_unsupported_without_starting_enumeration |
| T089 | [McpIntegrationTests.cs](../tests/AvaloniaDevTool.Cli.Tests/McpIntegrationTests.cs) · Stdio_server_initializes_advertises_tools_and_explains_missing_attachment |
| T090 | [McpIntegrationTests.cs](../tests/AvaloniaDevTool.Cli.Tests/McpIntegrationTests.cs) · Stdio_tools_inspect_edit_capture_and_detach_a_real_agent |
| T092 | [AdvancedInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AdvancedInspectorTests.cs) · Advanced_tabs_preserve_original_property_controls_and_expose_tools |
| T093 | [AdvancedInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AdvancedInspectorTests.cs) · Style_diagnostics_are_loaded_when_styles_tab_is_selected |
| T094 | [AdvancedInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AdvancedInspectorTests.cs) · Resource_edit_updates_target_and_conflict_keeps_application_newer_value |
| T095 | [AdvancedInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AdvancedInspectorTests.cs) · Asset_text_preview_is_loaded_and_disconnect_clears_it |
| T096 | [AdvancedInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AdvancedInspectorTests.cs) · Activity_tab_receives_log_notifications_and_can_stop_and_clear |
| T097 | [AdvancedInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AdvancedInspectorTests.cs) · Search_result_locates_unloaded_target_through_ancestor_path |
| T098 | [AdvancedInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AdvancedInspectorTests.cs) · Pseudo_class_ui_can_temporarily_toggle_and_restore_pointerover |
| T099 | [AdvancedInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AdvancedInspectorTests.cs) · Metrics_tab_shows_raw_histogram_samples_and_retained_window_statistics |
| T100 | [AdvancedInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AdvancedInspectorTests.cs) · Breakpoint_ui_tracks_count_conditions_and_can_disable_and_remove_definition |
| T101 | [AdvancedInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AdvancedInspectorTests.cs) · One_shot_global_event_breakpoint_hit_can_locate_source_control |
| T102 | [InspectorIntegrationTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/InspectorIntegrationTests.cs) · Window_markup_exposes_connection_tree_and_property_controls |
| T103 | [InspectorIntegrationTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/InspectorIntegrationTests.cs) · Inspector_connects_loads_children_and_edits_then_restores_binding |
| T104 | [InspectorIntegrationTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/InspectorIntegrationTests.cs) · Live_updates_preserve_dirty_draft_and_existing_property_row |
| T106 | [LayerSceneIntegrationTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/LayerSceneIntegrationTests.cs) · Geometry_tab_fetches_snapshot_and_pointer_selection_links_table_and_tree_without_recapturing |
| T108 | [LayerSceneProjectionTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/LayerSceneProjectionTests.cs) · Rotation_preserves_length_and_zoom_scales_about_viewport_center |
| T109 | [LayerSceneProjectionTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/LayerSceneProjectionTests.cs) · Polygon_hit_uses_frontmost_drawn_layer_and_ignores_outside_points |
| T133 | [TransportTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/TransportTests.cs) · ObserverFailureDoesNotTerminateTransport |
| T134 | [TransportTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/TransportTests.cs) · VersionMismatchIsRejectedAndClientRemainsDisconnected |
| T135 | [TransportTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/TransportTests.cs) · ClientReportsRemoteDisconnectAndCanEstablishAnotherSession |
| T136 | [MergedTreeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MergedTreeTests.cs) · Template_groups_preserve_user_content_once_and_slots_reference_its_logical_path |
| T137 | [MergedTreeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MergedTreeTests.cs) · Retemplating_invalidates_old_groups_and_old_template_objects_and_notifies_expanded_owner |
| T138 | [MergedTreeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MergedTreeTests.cs) · Empty_templates_standalone_presenters_and_search_limits_are_explicit |
| T139 | [MergedTreeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MergedTreeTests.cs) · Retained_synthetic_group_does_not_keep_owner_or_template_root_alive |
| T140 | [MergedTreeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MergedTreeTests.cs) · Replacing_content_expires_old_slot_and_preserves_current_content_without_duplicate_nodes |
| T141 | [MergedTreeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MergedTreeTests.cs) · Popup_content_is_reachable_once_under_its_current_presentation_root |
| T142 | [RuntimeBrowserTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RuntimeBrowserTests.cs) · Control_datacontext_nested_cycle_and_getter_errors_are_readonly_and_navigable |
| T143 | [RuntimeBrowserTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RuntimeBrowserTests.cs) · List_and_dictionary_pages_keep_indices_keys_nulls_and_navigation_offsets |
| T144 | [RuntimeBrowserTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RuntimeBrowserTests.cs) · Enumerable_only_and_expired_temporary_values_do_not_appear_as_empty_or_null |
| T145 | [RuntimeBrowserTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RuntimeBrowserTests.cs) · Real_agent_late_response_cannot_replace_a_new_selection_or_a_hidden_page |
| T146 | [RuntimeBrowserTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RuntimeBrowserTests.cs) · Disconnect_and_dispose_clear_paths_rows_and_disable_retained_commands |
| T147 | [RuntimeBrowserTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RuntimeBrowserTests.cs) · Markup_exposes_tenth_native_tab_and_external_backend_cannot_read_runtime |
| T148 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Healthy_binding_reports_effective_mode_options_and_inherited_data_context |
| T149 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Missing_data_context_and_bad_path_have_distinct_evidence_and_live_activity |
| T150 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Converter_failure_retains_inner_exception_and_does_not_invent_a_path_failure |
| T151 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Validation_failure_keeps_validation_category_and_actual_exception |
| T152 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Template_binding_reports_parent_property_and_retained_mode |
| T153 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Multi_binding_keeps_child_expression_tree_and_child_failures |
| T154 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Private_shape_can_be_disabled_and_missing_privateapi_members_degrade_per_field |
| T155 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Throwing_getters_and_display_are_isolated_without_losing_other_fields |
| T156 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Exception_depth_count_text_and_surrogate_boundaries_are_bounded |
| T157 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Source_registration_and_snapshot_do_not_keep_business_objects_alive |
| T158 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Composite_children_and_serialized_response_stay_bounded |
| T159 | [BindingDiagnosticsViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingDiagnosticsViewTests.cs) · Fields_distinguish_null_unknown_unavailable_and_weak_read_only_source |
| T160 | [BindingDiagnosticsViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingDiagnosticsViewTests.cs) · Axaml_renders_field_table_and_expandable_children_with_error_tree |
| T161 | [BindingDiagnosticsViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingDiagnosticsViewTests.cs) · Legacy_binding_keeps_explicit_unavailable_notice_without_fabricating_details |
| T162 | [BindingDiagnosticsViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingDiagnosticsViewTests.cs) · Child_and_error_presentation_are_bounded_and_show_truncation |
| T163 | [BindingDetailsMappingTests.cs](../tests/AvaloniaDevTool.Backends.XamlMcp.Tests/BindingDetailsMappingTests.cs) · Known_values_and_observed_null_preserve_types_without_native_object_handles |
| T164 | [BindingDetailsMappingTests.cs](../tests/AvaloniaDevTool.Backends.XamlMcp.Tests/BindingDetailsMappingTests.cs) · Missing_fields_distinguish_unknown_from_explicit_upstream_gaps |
| T165 | [BindingDetailsMappingTests.cs](../tests/AvaloniaDevTool.Backends.XamlMcp.Tests/BindingDetailsMappingTests.cs) · Independent_source_type_gap_is_preserved_without_claiming_source_is_unavailable |
| T166 | [BindingDetailsMappingTests.cs](../tests/AvaloniaDevTool.Backends.XamlMcp.Tests/BindingDetailsMappingTests.cs) · Child_tree_errors_and_upstream_gaps_remain_structured_without_duplicate_top_level_rows |
| T167 | [BindingDetailsMappingTests.cs](../tests/AvaloniaDevTool.Backends.XamlMcp.Tests/BindingDetailsMappingTests.cs) · Only_activity_status_can_determine_running_state |
| T168 | [BindingDetailsMappingTests.cs](../tests/AvaloniaDevTool.Backends.XamlMcp.Tests/BindingDetailsMappingTests.cs) · Unknown_data_context_and_foreign_supplier_ids_do_not_fabricate_native_references |
| T169 | [BindingDetailsMappingTests.cs](../tests/AvaloniaDevTool.Backends.XamlMcp.Tests/BindingDetailsMappingTests.cs) · Text_children_depth_and_total_node_limits_produce_visible_truncation |
| T170 | [BindingDetailsMappingTests.cs](../tests/AvaloniaDevTool.Backends.XamlMcp.Tests/BindingDetailsMappingTests.cs) · Global_gaps_survive_even_without_binding_rows |
| T171 | [BindingDetailsMappingTests.cs](../tests/AvaloniaDevTool.Backends.XamlMcp.Tests/BindingDetailsMappingTests.cs) · Whole_tree_text_budget_keeps_adversarial_escaped_json_below_protocol_limit |
| T172 | [BackendIntegrationTests.cs](../tests/AvaloniaDevTool.Backends.XamlMcp.Tests/BackendIntegrationTests.cs) · Published_packages_drive_tree_properties_binding_png_and_detach_through_adapter |
| T173 | [MergedTreeInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/MergedTreeInspectorTests.cs) · Merged_groups_and_slots_explain_their_roles_and_navigate_to_real_controls |
| T174 | [MergedTreeInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/MergedTreeInspectorTests.cs) · Retemplating_clears_expired_group_selection_and_preserves_a_newer_real_selection |
| T175 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · Late_start_captures_real_framework_style_attach_class_evaluation_and_resource_lookups |
| T176 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · History_pending_group_and_inflight_limits_have_independent_accounting |
| T177 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · Category_and_substring_filters_are_applied_to_final_tags |
| T178 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · Cross_thread_callbacks_are_batched_on_UI_thread_and_stop_clear_restart_ignore_old_spans |
| T179 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · Business_tag_objects_are_never_formatted_or_retained_and_text_is_bounded |
| T180 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · Invalid_settings_preserve_capture_and_worst_case_snapshot_fits_protocol_frame |
| T181 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · Typed_client_controls_real_framework_capture_and_disconnect_disposes_its_listener |
| T182 | [ProfilerViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/ProfilerViewTests.cs) · Profiler_commands_show_real_lookup_samples_clear_and_reset_on_disconnect |
| T183 | [ProfilerViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/ProfilerViewTests.cs) · Foreign_backend_has_a_disabled_profiler_page_and_commands |
| T184 | [McpIntegrationTests.cs](../tests/AvaloniaDevTool.Cli.Tests/McpIntegrationTests.cs) · Stdio_profiler_captures_real_framework_resource_lookups_and_cleans_up |
| T185 | [McpIntegrationTests.cs](../tests/AvaloniaDevTool.Cli.Tests/McpIntegrationTests.cs) · Stdio_input_tools_route_text_keys_and_real_button_clicks |
| T186 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · Real_framework_resource_groups_include_the_requested_theme_variant |
| T187 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · Concurrent_stop_and_restart_do_not_commit_stale_background_operations |
| T188 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · Hierarchical_parent_activities_have_distinct_bounded_inflight_identifiers |
| T189 | [ProfilerIntegrationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ProfilerIntegrationTests.cs) · Long_string_keys_and_theme_delimiters_keep_distinct_group_identities |
| T190 | [RuntimeSelectionLoadRaceTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RuntimeSelectionLoadRaceTests.cs) · Activating_runtime_during_pending_selection_reads_once_and_explicit_refresh_still_reads_again |

| T191 | [CompositionInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/CompositionInspectionTests.cs) · Own_drawing_images_do_not_repeat_child_paints_or_ancestor_backgrounds |
| T192 | [CompositionInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/CompositionInspectionTests.cs) · Hierarchical_transform_opacity_clip_and_z_order_recompose_to_normal_screenshot_pixels |
| T193 | [CompositionInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/CompositionInspectionTests.cs) · Overflow_bounds_transparency_and_unmapped_compositor_nodes_are_real_content |
| T194 | [CompositionInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/CompositionInspectionTests.cs) · Node_depth_pixel_byte_and_wire_budgets_report_real_omissions_and_exclude_highlight |
| T195 | [CompositionInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/CompositionInspectionTests.cs) · Empty_nodes_are_retained_and_unsupported_effects_are_explicit |
| T196 | [CompositionInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/CompositionInspectionTests.cs) · Geometry_paths_rounded_bounds_and_unavailable_stream_paths_preserve_clip_metadata |
| T197 | [CompositionInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/CompositionInspectionTests.cs) · Invalid_request_detachment_cancellation_and_session_disposal_release_capture |
| T198 | [CompositionInspectionTests.cs](../tests/AvaloniaDevTool.Agent.Tests/CompositionInspectionTests.cs) · Typed_client_capture_crosses_real_named_pipe_and_keeps_the_UI_responsive |
| T199 | [CompositionSceneTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/CompositionSceneTests.cs) · Projective_image_mapping_preserves_four_corners_and_real_skia_pixel_regions |
| T200 | [CompositionSceneTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/CompositionSceneTests.cs) · Scene_control_draws_real_images_clips_parent_chain_and_rejects_oversized_png_headers |
| T201 | [CompositionSceneTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/CompositionSceneTests.cs) · Camera_rotation_pan_zoom_reset_and_hit_selection_are_independent |
| T202 | [CompositionSceneTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/CompositionSceneTests.cs) · Composition_page_captures_real_agent_images_and_disposes_them_on_disconnect |
| T203 | [ResourceQueryTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ResourceQueryTests.cs) · Nested_query_keeps_duplicate_keys_original_owners_themes_and_style_scopes |
| T204 | [ResourceQueryTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ResourceQueryTests.cs) · Pagination_filter_sort_and_budgets_are_explicit_and_bounded |
| T205 | [ResourceQueryTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ResourceQueryTests.cs) · Nonstring_keys_use_weak_identity_and_read_never_calls_user_tostring |
| T206 | [ResourceQueryTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ResourceQueryTests.cs) · Shared_custom_and_cyclic_provider_graphs_are_visible_and_stop_at_budget |
| T207 | [ResourceQueryTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ResourceQueryTests.cs) · Inspection_metadata_and_ids_do_not_keep_unowned_dictionary_or_keys_alive |
| T208 | [ResourceQueryTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ResourceQueryTests.cs) · Removed_local_key_is_not_recreated_by_reset_or_redirected_to_a_nested_dictionary |
| T209 | [ResourceQueryTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ResourceQueryTests.cs) · Large_unicode_tree_and_entry_pages_stay_below_the_json_frame_limit |
| T210 | [ResourceQueryTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ResourceQueryTests.cs) · Agent_client_resource_queries_round_trip_utf8_pages_and_original_owner_edits |
| T211 | [ResourceQueryViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/ResourceQueryViewTests.cs) · Resource_pages_filters_and_nested_edits_use_the_original_dictionary |
| T212 | [ResourceQueryViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/ResourceQueryViewTests.cs) · Axaml_exposes_nested_filter_sort_page_controls_and_provider_provenance |
| T213 | [ResourceQueryViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/ResourceQueryViewTests.cs) · Late_page_after_filter_or_dictionary_change_cannot_replace_current_rows |
| T214 | [ResourceQueryViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/ResourceQueryViewTests.cs) · Late_page_after_disconnect_does_not_restore_resource_state |
| T215 | [StructuredLoggingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/StructuredLoggingTests.cs) · Agent_and_client_preserve_raw_template_typed_values_and_filter_before_history |
| T216 | [StructuredLoggingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/StructuredLoggingTests.cs) · Real_logger_factory_maps_original_format_event_id_scope_level_and_exception |
| T217 | [StructuredLoggingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/StructuredLoggingTests.cs) · Snapshot_limits_parameters_templates_and_safe_exception_graphs_without_business_formatting |
| T218 | [StructuredLoggingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/StructuredLoggingTests.cs) · Custom_exception_properties_are_not_executed_and_parameter_availability_remains_precise |
| T219 | [StructuredLoggingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/StructuredLoggingTests.cs) · Queued_and_historical_logs_do_not_retain_sources_arguments_or_exceptions |
| T220 | [StructuredLoggingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/StructuredLoggingTests.cs) · Custom_source_subscription_and_capture_stop_with_disposal |
| T221 | [StructuredLoggingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/StructuredLoggingTests.cs) · Reentrant_previous_sink_and_notification_logging_do_not_recurse_or_fill_history |
| T222 | [StructuredLoggingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/StructuredLoggingTests.cs) · Nested_capture_wrappers_forward_to_original_sink_and_restore_after_out_of_order_stop |
| T223 | [LogDetailsTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/LogDetailsTests.cs) · Actual_activity_view_filters_structured_logger_entries_and_jumps_to_background_source |
| T224 | [LogDetailsTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/LogDetailsTests.cs) · Expired_weak_log_source_reports_expiry_from_actual_jump_button_and_keeps_details |
| T225 | [PreviewAttachTests.cs](../tests/AvaloniaDevTool.Cli.Tests/PreviewAttachTests.cs) · Stdio_attach_previews_built_classful_project_resources_custom_controls_input_pixels_and_live_saves |
| T226 | [PreviewAttachTests.cs](../tests/AvaloniaDevTool.Cli.Tests/PreviewAttachTests.cs) · Window_root_uses_the_real_designer_and_mcp_shutdown_releases_its_process |
| T227 | [PreviewAttachTests.cs](../tests/AvaloniaDevTool.Cli.Tests/PreviewAttachTests.cs) · Missing_build_and_absent_class_fail_clearly_and_leave_no_attachment |
| T228 | [PreviewAttachTests.cs](../tests/AvaloniaDevTool.Cli.Tests/PreviewAttachTests.cs) · Style_and_resource_dictionary_use_the_official_design_preview_conversion |
| T229 | [PreviewAttachTests.cs](../tests/AvaloniaDevTool.Cli.Tests/PreviewAttachTests.cs) · Startup_timeout_and_cancellation_kill_the_owned_blocked_child |
| T230 | [PreviewAttachTests.cs](../tests/AvaloniaDevTool.Cli.Tests/PreviewAttachTests.cs) · Attaching_to_an_external_app_never_gives_the_session_permission_to_kill_it |
| T231 | [PreviewAttachTests.cs](../tests/AvaloniaDevTool.Cli.Tests/PreviewAttachTests.cs) · Abrupt_owner_exit_releases_a_child_even_while_the_target_builder_is_blocked |
| T232 | [CompositionSceneTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/CompositionSceneTests.cs) · Border_and_gradient_switches_change_only_the_scene_pixels |
| T233 | [PreviewAttachTests.cs](../tests/AvaloniaDevTool.Cli.Tests/PreviewAttachTests.cs) · A_library_project_source_can_use_a_distinct_built_application_for_App_resources |
| T234 | [McpIntegrationTests.cs](../tests/AvaloniaDevTool.Cli.Tests/McpIntegrationTests.cs) · Stdio_extended_resources_composition_and_structured_logs_preserve_real_evidence |
| T235 | [LoggingBudgetTests.cs](../tests/AvaloniaDevTool.Agent.Tests/LoggingBudgetTests.cs) · Unicode_and_escaped_log_history_fits_real_rpc_snapshot_and_keeps_session_usable |
| T236 | [LoggingBudgetTests.cs](../tests/AvaloniaDevTool.Agent.Tests/LoggingBudgetTests.cs) · Maximum_scopes_and_exception_graph_share_one_encoded_text_budget_with_explicit_gaps |
| T237 | [LoggingBudgetTests.cs](../tests/AvaloniaDevTool.Agent.Tests/LoggingBudgetTests.cs) · Ordinary_32_scalar_parameters_remain_complete_under_the_entry_budget |
| T238 | [LoggingBudgetTests.cs](../tests/AvaloniaDevTool.Agent.Tests/LoggingBudgetTests.cs) · Concurrent_producers_and_clear_restart_keep_combined_byte_accounting_bounded |
| T239 | [LoggingBudgetTests.cs](../tests/AvaloniaDevTool.Agent.Tests/LoggingBudgetTests.cs) · Slow_notification_sender_stays_bounded_while_snapshots_remain_readable_and_recovers_in_order |
| T240 | [PreviewIntegrationTests.cs](../tests/AvaloniaDevTool.Previewer.Tests/PreviewIntegrationTests.cs) · Window_root_has_a_visible_host_notice_and_closing_the_document_keeps_the_host_open |
| T241 | [PreviewIntegrationTests.cs](../tests/AvaloniaDevTool.Previewer.Tests/PreviewIntegrationTests.cs) · Preview_window_agent_exposes_reloaded_document_through_the_existing_pipe_protocol |

| T242 | [RemoteIntegrationTests.cs](../tests/AvaloniaDevTool.Remote.Tests/RemoteIntegrationTests.cs) · Real_websocket_typed_client_inspects_edits_receives_push_restores_and_reconnects |
| T243 | [RemoteIntegrationTests.cs](../tests/AvaloniaDevTool.Remote.Tests/RemoteIntegrationTests.cs) · Authentication_and_origin_fail_before_session_creation_and_never_expose_token |
| T244 | [RemoteIntegrationTests.cs](../tests/AvaloniaDevTool.Remote.Tests/RemoteIntegrationTests.cs) · Pipe_and_remote_share_a_single_gate_and_rejected_transport_does_not_dispose_active_session |
| T245 | [RemoteIntegrationTests.cs](../tests/AvaloniaDevTool.Remote.Tests/RemoteIntegrationTests.cs) · Text_and_invalid_rpc_frames_close_connection_then_allow_clean_reconnect |
| T246 | [RemoteIntegrationTests.cs](../tests/AvaloniaDevTool.Remote.Tests/RemoteIntegrationTests.cs) · Fragmented_binary_messages_preserve_native_rpc_framing |
| T247 | [RemoteIntegrationTests.cs](../tests/AvaloniaDevTool.Remote.Tests/RemoteIntegrationTests.cs) · Server_disposal_disconnects_clients_releases_session_and_closes_listener |
| T248 | [RemoteIntegrationTests.cs](../tests/AvaloniaDevTool.Remote.Tests/RemoteIntegrationTests.cs) · Weak_tokens_and_plaintext_nonloopback_listeners_are_rejected_before_listening |
| T249 | [RemoteIntegrationTests.cs](../tests/AvaloniaDevTool.Remote.Tests/RemoteIntegrationTests.cs) · Untrusted_tls_certificate_is_rejected_without_an_insecure_fallback |
| T250 | [RemoteIntegrationTests.cs](../tests/AvaloniaDevTool.Remote.Tests/RemoteIntegrationTests.cs) · Caller_cancellation_stops_a_stalled_websocket_upgrade |
| T251 | [RemoteIntegrationTests.cs](../tests/AvaloniaDevTool.Remote.Tests/RemoteIntegrationTests.cs) · Insecure_remote_addresses_credential_urls_are_rejected |
| T252 | [WebSocketStreamTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/WebSocketStreamTests.cs) · Fragments_and_separate_messages_are_read_as_one_rpc_byte_stream |
| T253 | [WebSocketStreamTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/WebSocketStreamTests.cs) · Fragmented_oversized_websocket_message_is_rejected_without_accumulating_a_message_buffer |
| T254 | [WebSocketStreamTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/WebSocketStreamTests.cs) · Close_frame_returns_end_of_stream |
| T255 | [WebSocketStreamTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/WebSocketStreamTests.cs) · Bidirectional_rpc_can_receive_an_answer_while_its_request_handler_is_pending |
| T256 | [TransportTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/TransportTests.cs) · Client_disconnect_cancels_inflight_server_handler_without_waiting_for_a_reply |
| T257 | [RemoteInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RemoteInspectorTests.cs) · Remote_viewmodel_connects_edits_restores_and_reconnects_without_exposing_token |
| T258 | [RemoteInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RemoteInspectorTests.cs) · Empty_or_wrong_remote_token_can_be_corrected_and_the_input_is_masked |
| T259 | [RemoteInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RemoteInspectorTests.cs) · Launch_options_read_only_the_named_token_environment_and_reject_ambiguous_backends |
| T260 | [RemoteMcpTests.cs](../tests/AvaloniaDevTool.Cli.Tests/RemoteMcpTests.cs) · Stdio_remote_attach_uses_environment_credentials_and_exports_full_asset |
| T261 | [RemoteMcpTests.cs](../tests/AvaloniaDevTool.Cli.Tests/RemoteMcpTests.cs) · Missing_remote_token_and_insecure_urls_fail_without_echoing_credentials |
| T262 | [PropertyPresentationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PropertyPresentationTests.cs) · Default_property_request_does_not_execute_CLR_getters |
| T263 | [PropertyPresentationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PropertyPresentationTests.cs) · Unified_properties_keep_hidden_CLR_members_and_safely_describe_opaque_values |
| T264 | [PropertyPresentationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PropertyPresentationTests.cs) · Editor_metadata_distinguishes_color_flags_and_unsupported_brushes |
| T265 | [PropertyPresentationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PropertyPresentationTests.cs) · Property_preview_draws_actual_image_and_geometry_pixels_and_rejects_stale_handles |
| T266 | [PropertyPresentationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PropertyPresentationTests.cs) · Empty_image_reports_not_available |
| T267 | [PropertyPresentationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PropertyPresentationTests.cs) · Property_preview_cache_does_not_root_replaced_values |
| T268 | [PropertyEditorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/PropertyEditorTests.cs) · CLR_checkbox_explicitly_enables_getters_and_keeps_default_selection_lazy |
| T269 | [PropertyEditorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/PropertyEditorTests.cs) · Solid_brush_uses_color_picker_without_mutating_brush_until_apply |
| T270 | [PropertyEditorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/PropertyEditorTests.cs) · Color_picker_changes_only_draft_and_clear_restores_latest_bound_color |
| T271 | [PropertyEditorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/PropertyEditorTests.cs) · Boolean_numeric_enum_and_flags_AXAML_controls_share_unapplied_drafts |
| T272 | [PropertyEditorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/PropertyEditorTests.cs) · Sorting_filtering_and_external_updates_keep_selected_row_and_dirty_draft |
| T273 | [PropertyEditorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/PropertyEditorTests.cs) · Property_preview_command_loads_image_from_property_value |
| T274 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Dynamic_resource_reports_actual_key_host_and_explicit_private_shape_gaps |
| T275 | [BindingDetailsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingDetailsTests.cs) · Resource_key_budget_never_turns_a_truncated_preview_into_a_string_identity |
| T276 | [BindingNavigationTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingNavigationTests.cs) · Axaml_source_opens_public_members_nested_cycle_and_returns_to_original_binding_without_reselecting |
| T277 | [BindingNavigationTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingNavigationTests.cs) · Expired_source_keeps_origin_and_error_without_old_rows_and_refresh_can_recover_only_by_new_observation |
| T278 | [BindingNavigationTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingNavigationTests.cs) · Real_source_reentrancy_selection_page_disconnect_and_reconnect_invalidate_old_navigation |
| T279 | [BindingNavigationTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingNavigationTests.cs) · Dynamic_resource_uses_real_key_and_preserves_multiple_scope_provenances_and_nested_owner |
| T280 | [BindingNavigationTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingNavigationTests.cs) · Non_string_resource_key_opens_actual_key_object_without_parsing_display_text |
| T281 | [BindingNavigationTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingNavigationTests.cs) · Foreign_handles_and_missing_key_facts_stay_disabled_and_filters_do_not_mutate_raw_evidence |
| T282 | [AssetExportTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AssetExportTests.cs) · Disconnect_invalidates_export_tokens_and_new_session_can_export |
| T283 | [AssetExportTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AssetExportTests.cs) · Full_asset_export_crosses_preview_limit_and_preserves_bytes_hash_and_progress |
| T284 | [AssetExportTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AssetExportTests.cs) · Cancel_and_destination_error_close_readers_and_atomic_file_export_retains_old_file |
| T285 | [AssetExportTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AssetExportTests.cs) · Export_validates_owned_uris_and_disconnect_style_dispose_releases_streams |
| T286 | [AssetExportTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AssetExportTests.cs) · Sequential_nonseek_readers_expire_enforce_cap_and_recover_from_errors |
| T287 | [AssetPreviewTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AssetPreviewTests.cs) · Escaped_text_preview_stays_within_rpc_frame_budget |
| T288 | [AssetPreviewTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AssetPreviewTests.cs) · Text_preview_reports_total_and_preserves_complete_utf8_characters_at_boundary |
| T289 | [AssetPreviewTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AssetPreviewTests.cs) · Png_dimensions_dpi_and_decoded_memory_are_separate_from_encoded_size |
| T290 | [AssetPreviewTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AssetPreviewTests.cs) · Font_preview_uses_official_embedded_font_bytes_and_invalid_font_is_never_text |
| T291 | [AssetPreviewTests.cs](../tests/AvaloniaDevTool.Agent.Tests/AssetPreviewTests.cs) · Unknown_length_stream_does_not_report_preview_size_as_full_size |
| T292 | [AssetPreviewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AssetPreviewTests.cs) · Asset_view_copies_uri_cancels_picker_and_exports_full_file_with_visible_status |
| T293 | [AssetPreviewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/AssetPreviewTests.cs) · Font_preview_renders_actual_asset_family_and_releases_registration_when_cleared |

| T294 | [PropertyPresentationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PropertyPresentationTests.cs) · CLR_getter_failure_is_bounded_without_losing_the_RPC_session |
| T295 | [PropertyPresentationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PropertyPresentationTests.cs) · Image_preview_failure_is_bounded_without_losing_the_RPC_session |
| T296 | [PropertyPresentationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PropertyPresentationTests.cs) · Property_failures_do_not_execute_custom_exception_message_or_base_exception_overrides |
| T297 | [PropertyPresentationTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PropertyPresentationTests.cs) · Aggregate_and_deep_inner_exception_failures_have_bounded_explicit_summaries |

| T298 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Default_overlay_draws_only_bounds_and_never_changes_the_application_control |
| T299 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Margin_and_padding_have_distinct_real_pixels_outside_and_inside_the_border_box |
| T300 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Nested_translation_rotation_scaling_and_center_origin_align_actual_overlay_pixels |
| T301 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Ancestor_geometry_clip_and_root_viewport_clip_the_source_overlay_pixels |
| T302 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Negative_margin_and_overlapping_padding_are_drawn_and_reported_with_their_signed_meaning |
| T303 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Options_update_without_reselection_and_rulers_distinguish_root_DIP_from_device_pixels |
| T304 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Mutable_transforms_resize_parent_offsets_and_visibility_are_refreshed_without_target_property_writes |
| T305 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Non_affine_transforms_report_a_limitation_and_draw_no_fake_identity_bounds |
| T306 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Unsupported_padding_does_not_invoke_an_arbitrary_CLR_getter |
| T307 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Root_cross_window_clear_detach_and_window_close_reuse_the_surface_without_retaining_targets |
| T308 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Clear_and_detach_allow_selected_control_to_be_collected_while_the_service_remains_alive |
| T309 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Nested_suspensions_hide_only_the_overlay_and_clear_does_not_resurrect_it |
| T310 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Popup_uses_its_actual_presentation_root_and_topmost_surface_then_clears_when_closed |
| T311 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Closing_the_initial_root_itself_clears_selection_and_releases_the_surface |
| T312 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Typed_client_configures_live_overlay_while_screenshots_search_and_layout_exclude_it |
| T313 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Public_overlay_service_rejects_wrong_threads_expired_ids_detached_controls_and_diagnostic_controls |
| T314 | [OverlayViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/OverlayViewTests.cs) · Options_apply_to_real_overlay_and_hover_restores_selection_without_changing_layout |
| T315 | [OverlayViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/OverlayViewTests.cs) · Overlay_controls_bind_preferences_and_disable_without_a_native_connection |
| T316 | [TreeWorkflowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreeWorkflowTests.cs) · Expanded_branches_use_real_collection_events_and_release_stops_notifications |
| T317 | [TreeWorkflowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreeWorkflowTests.cs) · Diagnostic_overlay_changes_do_not_invalidate_an_observed_layer_but_application_children_do |
| T318 | [TreeWorkflowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreeWorkflowTests.cs) · Selector_validation_keeps_the_last_unvisited_node_in_the_truncation_budget |
| T319 | [TreeWorkflowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreeWorkflowTests.cs) · Removing_a_root_releases_its_tree_observations_without_releasing_property_observers |
| T320 | [TreeWorkflowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreeWorkflowTests.cs) · Selector_uses_Avalonia_parser_and_reports_ambiguity_templates_and_unsupported_identifiers |
| T321 | [TreeWorkflowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreeWorkflowTests.cs) · BringIntoView_changes_scroll_offset_and_moves_target_into_the_viewport |
| T322 | [TreeWorkflowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreeWorkflowTests.cs) · Active_tree_observation_does_not_keep_a_detached_control_alive |
| T323 | [TreeWorkflowInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/TreeWorkflowInspectorTests.cs) · Recursive_expansion_has_real_node_depth_and_template_budgets_and_can_cancel |
| T324 | [TreeWorkflowInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/TreeWorkflowInspectorTests.cs) · Collapsed_subtree_releases_backend_observations_and_reexpands_with_fresh_children |
| T325 | [TreeWorkflowInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/TreeWorkflowInspectorTests.cs) · Window_commands_copy_actual_selector_and_session_scoped_metadata_to_the_platform_clipboard |
| T326 | [TreeWorkflowInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/TreeWorkflowInspectorTests.cs) · Large_accumulated_tree_release_is_batched_with_the_real_agent_request_limit |
| T327 | [EventRouteTests.cs](../tests/AvaloniaDevTool.Agent.Tests/EventRouteTests.cs) · Real_tunnel_and_bubble_share_correlation_and_observe_handled_without_attributing_a_handler |
| T328 | [EventRouteTests.cs](../tests/AvaloniaDevTool.Agent.Tests/EventRouteTests.cs) · Direct_events_reused_args_and_child_reentrancy_have_distinct_finished_correlations |
| T329 | [EventRouteTests.cs](../tests/AvaloniaDevTool.Agent.Tests/EventRouteTests.cs) · Exact_catalog_selection_and_late_registration_are_applied_to_real_events |
| T330 | [EventRouteTests.cs](../tests/AvaloniaDevTool.Agent.Tests/EventRouteTests.cs) · Recursively_raising_same_direct_args_keeps_inner_and_outer_correlations_separate |
| T331 | [EventRouteTests.cs](../tests/AvaloniaDevTool.Agent.Tests/EventRouteTests.cs) · Clear_inside_instance_handler_cannot_resurrect_old_route_and_stop_keeps_history |
| T332 | [EventRouteTests.cs](../tests/AvaloniaDevTool.Agent.Tests/EventRouteTests.cs) · Route_history_has_count_byte_and_per_route_limits |
| T333 | [EventRouteTests.cs](../tests/AvaloniaDevTool.Agent.Tests/EventRouteTests.cs) · Route_history_and_correlations_do_not_retain_source_receiver_or_event_args |
| T334 | [EventRouteTests.cs](../tests/AvaloniaDevTool.Agent.Tests/EventRouteTests.cs) · Unfinished_routes_expire_without_inventing_completion_and_history_expires_after_ttl |
| T335 | [EventRouteTests.cs](../tests/AvaloniaDevTool.Agent.Tests/EventRouteTests.cs) · Large_real_routes_are_evicted_by_utf8_json_budget_before_count_capacity |
| T336 | [EventRoutesViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/EventRoutesViewTests.cs) · Actual_event_page_selects_catalog_captures_route_and_navigates_source_and_receiver |
| T337 | [EventRoutesViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/EventRoutesViewTests.cs) · Foreign_backend_event_view_and_its_commands_are_disabled |
| T338 | [EventRoutesViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/EventRoutesViewTests.cs) · Expired_event_source_actual_jump_button_reports_expiry_and_preserves_route_details |
| T339 | [EventRoutesViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/EventRoutesViewTests.cs) · Actual_save_defaults_button_restores_overrides_on_reconnect_and_reports_missing_registered_identities |
| T340 | [EventRoutesViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/EventRoutesViewTests.cs) · Defaults_store_rejects_invalid_groups_and_oversized_files_without_replacing_saved_selection |
| T341 | [RendererDiagnosticsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RendererDiagnosticsTests.cs) · Window_scope_preserves_application_flags_tracks_new_roots_and_restores_removed_roots |
| T342 | [RendererDiagnosticsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RendererDiagnosticsTests.cs) · Application_changes_release_ownership_and_are_not_overwritten_on_stop_or_dispose |
| T343 | [RendererDiagnosticsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RendererDiagnosticsTests.cs) · Synchronous_application_rewrite_is_detected_and_invalid_targets_do_not_mutate_flags |
| T344 | [RendererDiagnosticsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RendererDiagnosticsTests.cs) · Native_renderer_overlay_changes_actual_frame_pixels_and_stop_restores_the_frame |
| T345 | [RendererDiagnosticsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RendererDiagnosticsTests.cs) · Named_pipe_disconnect_restores_owned_flags |
| T346 | [RendererDiagnosticsTests.cs](../tests/AvaloniaDevTool.Agent.Tests/RendererDiagnosticsTests.cs) · Framework_meter_reports_real_layout_render_and_runtime_measurements |
| T347 | [RendererDiagnosticsTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/RendererDiagnosticsTests.cs) · Renderer_view_uses_real_window_flags_and_resets_on_disconnect |
| T348 | [McpIntegrationTests.cs](../tests/AvaloniaDevTool.Cli.Tests/McpIntegrationTests.cs) · Stdio_overlay_selector_renderer_and_correlated_events_use_actual_agent_services |
| T349 | [PopupRootDiscoveryTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupRootDiscoveryTests.cs) · Native_flyout_roots_follow_focus_and_are_removed_without_observing_unrelated_windows |
| T350 | [BrushEditorRpcTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BrushEditorRpcTests.cs) · Gradient_RPC_changes_real_pixels_and_clear_restores_latest_binding_without_mutating_original |
| T351 | [BrushEditorRpcTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BrushEditorRpcTests.cs) · All_four_brush_kinds_round_trip_real_fields_and_create_independent_objects |
| T352 | [BrushEditorRpcTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BrushEditorRpcTests.cs) · Invalid_gradient_data_and_opaque_brushes_are_rejected_without_replacing_current_value |
| T353 | [BrushEditorViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BrushEditorViewTests.cs) · Actual_AXAML_edits_stops_and_coordinates_as_draft_and_apply_replaces_brush |
| T354 | [BrushEditorViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BrushEditorViewTests.cs) · Brush_kind_switch_exposes_radial_and_conic_fields_and_preserves_stop_drafts |
| T355 | [BrushEditorViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BrushEditorViewTests.cs) · Stop_removal_keeps_two_stops_and_invalid_color_stays_in_unapplied_draft |
| T356 | [MetricsWindowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsWindowTests.cs) · Time_window_keeps_its_exact_boundary_and_stop_freezes_time_and_statistics |
| T357 | [MetricsWindowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsWindowTests.cs) · Frames_align_across_series_use_histogram_mean_and_counter_last_without_changing_raw_percentiles |
| T358 | [MetricsWindowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsWindowTests.cs) · Disabling_frame_aggregation_preserves_each_raw_point_at_its_actual_timestamp |
| T359 | [MetricsWindowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsWindowTests.cs) · Clearing_one_series_removes_its_history_frames_and_pending_without_restarting_the_other_series |
| T360 | [MetricsWindowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsWindowTests.cs) · Expiration_and_capacity_are_independent_and_invalid_window_settings_preserve_the_capture |
| T361 | [MetricsWindowTests.cs](../tests/AvaloniaDevTool.Agent.Tests/MetricsWindowTests.cs) · Configured_poll_and_notification_intervals_change_actual_observable_and_delivery_activity |
| T362 | [MetricsViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/MetricsViewTests.cs) · Actual_metric_controls_configure_capture_show_p90_synchronized_charts_and_clear_only_one_graph |
| T363 | [MetricsViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/MetricsViewTests.cs) · Display_interval_delays_actual_chart_refresh_and_raw_toggle_survives_the_start_command |
| T364 | [MetricsViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/MetricsViewTests.cs) · Older_pull_cannot_replace_new_notification_or_clear_and_foreign_capture_cannot_resurrect_old_data |
| T365 | [BreakpointSourceTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointSourceTests.cs) · Source_and_receiver_are_independent_filters_and_legacy_object_id_keeps_receiver_semantics |
| T366 | [BreakpointSourceTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointSourceTests.cs) · Reusing_args_for_sequential_raises_is_counted_synchronously_once_per_raise |
| T367 | [BreakpointSourceTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointSourceTests.cs) · Nested_raises_with_distinct_args_keep_each_actual_source_and_final_handled |
| T368 | [BreakpointSourceTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointSourceTests.cs) · Recursively_raising_same_direct_args_keeps_outer_source_snapshot_and_separate_count |
| T369 | [BreakpointSourceTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointSourceTests.cs) · Recursively_reusing_routed_args_counts_actual_phase_finishes_despite_inner_route_mutation |
| T370 | [BreakpointSourceTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointSourceTests.cs) · Source_filters_support_hit_conditions_once_disable_enable_and_replacement |
| T371 | [BreakpointSourceTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointSourceTests.cs) · Collected_source_expires_a_global_receiver_breakpoint_without_retaining_source_or_history |
| T372 | [BreakpointSourceTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BreakpointSourceTests.cs) · Invalid_source_definition_is_rejected_and_failed_update_keeps_old_receiver_observation |
| T373 | [BreakpointSourceViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BreakpointSourceViewTests.cs) · Actual_breakpoint_page_creates_independent_source_and_receiver_filters_and_keeps_receiver_on_hit_navigation |
| T374 | [BreakpointSourceViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BreakpointSourceViewTests.cs) · Actual_breakpoint_page_supports_global_source_and_rejects_invalid_source_id |
| T375 | [BreakpointContractTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/BreakpointContractTests.cs) · Legacy_event_definition_keeps_object_id_as_receiver_and_new_source_filter_roundtrips |
| T376 | [BreakpointSourceMcpTests.cs](../tests/AvaloniaDevTool.Cli.Tests/BreakpointSourceMcpTests.cs) · Stdio_source_and_receiver_breakpoints_roundtrip_update_disable_count_and_remove_on_a_real_agent |
| T377 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Text_information_uses_actual_TextBlock_layout_without_returning_contents |
| T378 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · TextBox_reports_its_real_presenter_layout_and_password_skips_text_metadata |
| T379 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Bitmap_information_distinguishes_decoded_pixels_DPI_DIP_and_actual_source_identity |
| T380 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Custom_image_source_and_custom_text_layout_do_not_execute_arbitrary_getters_or_ToString |
| T381 | [OverlayTests.cs](../tests/AvaloniaDevTool.Agent.Tests/OverlayTests.cs) · Box_region_emphasis_draws_only_matching_real_transformed_pixels_and_restores_options |
| T382 | [OverlayViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/OverlayViewTests.cs) · Box_model_real_pointer_regions_restore_preferences_selection_and_ignore_stale_exit |
| T383 | [RpcTraceTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/RpcTraceTests.cs) · A_received_frame_is_not_a_success_when_typed_result_decoding_fails |
| T384 | [RpcTraceTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/RpcTraceTests.cs) · Actual_duplex_frames_correlate_opposite_ids_errors_notifications_and_redact_payloads |
| T385 | [RpcTraceTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/RpcTraceTests.cs) · Caller_cancellation_and_late_response_preserve_identity_and_do_not_poison_next_call |
| T386 | [RpcTraceTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/RpcTraceTests.cs) · Connection_ids_stop_and_clear_define_explicit_correlation_boundaries |
| T387 | [RpcTraceTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/RpcTraceTests.cs) · Unicode_payload_projection_and_history_are_bounded_and_valid_json |
| T388 | [ProtocolMonitorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/ProtocolMonitorTests.cs) · Actual_monitor_controls_record_rpc_payloads_errors_filter_stop_and_clear |
| T389 | [ProtocolMonitorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/ProtocolMonitorTests.cs) · Default_metadata_capture_preserves_history_and_separates_reconnected_id_spaces |
| T390 | [Phase7McpTests.cs](../tests/AvaloniaDevTool.Cli.Tests/Phase7McpTests.cs) · Stdio_metrics_configuration_frames_and_clear_and_structured_brush_edit_use_actual_agent_services |
| T391 | [BreakpointSourceViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BreakpointSourceViewTests.cs) · Refreshing_an_unchanged_definition_keeps_selection_and_update_command_enabled |
| T392 | [MetricsPointerRefreshTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/MetricsPointerRefreshTests.cs) · Refresh_between_real_pointer_press_and_release_preserves_chart_button_and_clears_the_series |
| T393 | [ProtocolJsonAotTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/ProtocolJsonAotTests.cs) · EveryProtocolRecordAndItsWireCollectionsHaveGeneratedMetadata |
| T394 | [ProtocolJsonAotTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/ProtocolJsonAotTests.cs) · GeneratedMetadataPreservesWebWireFormatAndStringEnums |
| T395 | [ProtocolJsonAotTests.cs](../tests/AvaloniaDevTool.Protocol.Tests/ProtocolJsonAotTests.cs) · GeneratedMetadataRoundTripsAnRpcEnvelopeAndNestedError |
| T396 | [ContextualPropertyTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ContextualPropertyTests.cs) · Real_layout_parents_filter_only_known_properties_and_preserve_legacy_and_unknown_members |
| T397 | [ContextualPropertyTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ContextualPropertyTests.cs) · Moving_the_same_control_updates_context_metadata_and_notifies_without_a_value_change |
| T398 | [ContextualPropertyTests.cs](../tests/AvaloniaDevTool.Agent.Tests/ContextualPropertyTests.cs) · Template_parts_use_the_actual_visual_layout_parent_instead_of_the_template_owner |
| T399 | [ContextualPropertyViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/ContextualPropertyViewTests.cs) · Actual_context_checkbox_preserves_hidden_drafts_and_tracks_reparenting |
| T400 | [ContextualPropertyMcpTests.cs](../tests/AvaloniaDevTool.Cli.Tests/ContextualPropertyMcpTests.cs) · Stdio_props_context_filter_and_explicit_CLR_opt_in_preserve_legacy_defaults |
| T401 | [TreePagingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreePagingTests.cs) · Direct_children_beyond_two_thousand_are_reachable_with_bounded_pages_and_stable_ids |
| T402 | [TreePagingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreePagingTests.cs) · Cursors_reject_branch_changes_even_when_original_order_is_restored_and_release_discards_state |
| T403 | [TreePagingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreePagingTests.cs) · Oversized_branches_fail_explicitly_instead_of_silently_returning_a_prefix |
| T404 | [TreePagingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreePagingTests.cs) · Template_parent_and_part_have_distinct_real_matching_selectors_and_merged_page_identity |
| T405 | [TreePagingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreePagingTests.cs) · Custom_template_owner_requires_namespace_mapping_for_both_selector_entries |
| T406 | [TreePagingInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/TreePagingInspectorTests.cs) · Actual_load_more_button_appends_pages_and_search_locates_a_later_page_without_losing_selection_on_fold |
| T407 | [TreePagingInspectorTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/TreePagingInspectorTests.cs) · Template_parent_copy_and_navigation_are_independent_reachable_pointer_actions_with_real_clipboard |
| T408 | [TreePagingMcpTests.cs](../tests/AvaloniaDevTool.Cli.Tests/TreePagingMcpTests.cs) · Stdio_direct_child_pages_expose_large_branches_continuations_changes_and_recursive_truncation |
| T409 | [TreePagingMcpTests.cs](../tests/AvaloniaDevTool.Cli.Tests/TreePagingMcpTests.cs) · Stdio_template_parent_selector_is_independent_from_the_matching_part_path |
| T410 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Real_outside_pointer_input_is_suppressed_then_resumes_for_overlay_and_native_popups |
| T411 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Bound_configuration_remains_live_and_target_detachment_is_not_cancelled |
| T412 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Scope_includes_new_nested_popups_but_excludes_other_roots_and_application_close_never_reopens |
| T413 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Application_configuration_changes_including_same_effective_value_are_preserved_on_resume_and_scope_removal |
| T414 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Flyout_freezes_outside_clicks_and_context_menu_reports_its_overlay_interaction_handler_boundary |
| T415 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Tooltip_service_pointer_dismissal_is_reported_as_limited_and_never_resurrected |
| T416 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Frozen_context_menu_keeps_keyboard_escape_and_business_item_click_interactions |
| T417 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Named_pipe_disconnect_restores_owned_values_and_never_reopens_closed_popups |
| T418 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Closed_popups_are_not_rooted_by_the_freeze_service |
| T419 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Closing_the_owner_window_closes_its_popups_and_removes_frozen_scope_without_reopening |
| T420 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · Transient_flyout_move_away_is_an_explicit_limit_and_does_not_restore_a_closed_flyout |
| T421 | [PopupFreezeTests.cs](../tests/AvaloniaDevTool.Agent.Tests/PopupFreezeTests.cs) · All_root_freeze_follows_new_roots_and_can_resume_one_root_without_affecting_another |
| T422 | [PopupFreezeViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/PopupFreezeViewTests.cs) · Visible_freeze_and_resume_controls_change_real_popup_behavior_and_reset_on_disconnect |
| T423 | [PopupFreezeMcpTests.cs](../tests/AvaloniaDevTool.Cli.Tests/PopupFreezeMcpTests.cs) · Stdio_popup_freeze_tools_apply_read_and_resume_real_popup_state |
| T424 | [BindingCultureTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingCultureTests.cs) · Ordinary_and_compiled_bindings_use_retained_culture_for_format_and_convert_back |
| T425 | [BindingCultureTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingCultureTests.cs) · Unconfigured_culture_stays_null_when_diagnostic_thread_culture_changes |
| T426 | [BindingCultureTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingCultureTests.cs) · Multi_binding_reports_actual_format_wrapper_and_independent_child_cultures |
| T427 | [BindingCultureTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingCultureTests.cs) · Template_binding_preserves_configured_culture_without_claiming_a_format_option |
| T428 | [BindingCultureTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingCultureTests.cs) · Two_way_text_box_source_updates_follow_real_trigger_for_ordinary_and_compiled_bindings |
| T429 | [BindingCultureTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingCultureTests.cs) · Unsupported_shapes_and_custom_culture_getters_do_not_fabricate_culture |
| T430 | [BindingCultureTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingCultureTests.cs) · Resource_expression_marks_converter_culture_not_applicable |
| T431 | [BindingCultureTests.cs](../tests/AvaloniaDevTool.Agent.Tests/BindingCultureTests.cs) · Optional_culture_fields_round_trip_and_legacy_wire_payload_keeps_them_missing |
| T432 | [BindingDiagnosticsViewTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/BindingDiagnosticsViewTests.cs) · Culture_field_state_is_not_reinterpreted |
| T433 | [TreePagingTests.cs](../tests/AvaloniaDevTool.Agent.Tests/TreePagingTests.cs) · A_unique_match_on_another_style_key_never_validates_the_selected_element |
| T434 | [SelectionRefreshRaceTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/SelectionRefreshRaceTests.cs) · Legacy_tree_transport_falls_back_once_and_keeps_every_local_page_reachable |
| T435 | [SelectionRefreshRaceTests.cs](../tests/AvaloniaDevTool.Inspector.Tests/SelectionRefreshRaceTests.cs) · A_successful_native_paging_session_never_downgrades_a_later_unsupported_request |

## 不能用相邻功能替代的领域

1. **日志／Meter不替代Profiler统计**：独立 ProfilerService 已用真实框架 ActivityListener 捕获三类活动；其保留窗口、未知资源结果和常见选择器描述仍不能替代原版全部计数语义。日志 ActivityService 与 Meter 面板仍是各自独立服务。
2. **自身绘制不等于全部合成结果**：VisualGeometry 仍是矩形几何；CompositorOwnDrawing 已有真实 server 层级和逐节点 PNG。它们与带效果／遮罩／缓存／组透明度的完整重组必须分开；Skia CPU 像素与 3D 平面命中不证明 GPU 或全部遮挡语义。
3. **文件附加不等于IDE进程附加**：MCP 已能管理真实已构建项目预览，保留 x:Class、App 资源和实际输入／PNG。调用者仍需先构建并显式选择产物，且没有附加 IDE 现有设计进程；X02 与 C11 的状态不同。
4. **只读对象导航不是CLR编辑**：runtime.members/items 已接 Inspector 对象页，面包屑、长循环、分页和对象回收均有专项测试；CLR setter、私有成员和任意 IEnumerable 枚举仍不提供。
5. **弱来源导航不等于原子业务快照**：Source／ResourceKey 已有实际导航与失效状态，协议没有 SourceVersion；候选资源不代表 winner。Culture 按已验证形状读取保留配置，原始 ElementName／RelativeSource 声明及上游未给字段仍有明确缺口，不能补成假值。
6. **断点source与receiver要分开**：当前事件ObjectId表示经过的Interactive接收者；直接当“只匹配事件源”会误报B02完成。
7. **编辑器和导出按实际范围验收**：ColorPicker、实际属性 PNG 与完整资产导出已有；复杂画刷、取消清理专项、WOFF、原生文件对话框平台表现和持久化工具壳仍独立验收。

## 文档口径仍需随统一回归校正

功能目录的 V1 列和 24 项记录是历史基线，本审计不重写它们。最近完整冻结为 Phase 8 的 476 项及 22 项目双配置构建；首轮 474 项的失败、Phase 7 的 418 项及更早 375、374、323、319、266、260、214 项保留对应源码的历史含义。专项不与完整批次叠加，普通零警告构建不代表 Native AOT 严格发布通过。历史文字不能否定当前源码，也不能用于声称未验收的完整功能已完成。

## 原生与开源复用边界

原版桌面x64的两个不同900秒Ghidra配置均未完成整体分析。不能把134,360条unwind/funclet记录称为C#方法，也不能把编译绑定名称当作业务方法伪码。3D展示、原生MCP/预览器编排和Profiler UI聚合仍未取得原生业务伪码；已从同一EXE提取的三个预览器CIL则有独立哈希、MVID及587方法的REA成员证据，两者不得混淆。

[开源复用核查](research/open-source-reuse-audit.md) 与 [社区后端审计](research/open-source-agent-tools-audit.md) 已说明：

- ClassicDiagnostics与旧官方工具可参考专用编辑器、CLR对象导航、树订阅及盒模型；偏进程内且涉及Avalonia内部API，不是跨进程后端的即插即用替换。
- XamlMcp已有真实Server/Agent、弱句柄、兼容层与端到端测试；其token发现、协议版本2和快照命名空间与当前管道DTO不同。绑定诊断本身也有明确gap。
- CDP已有HTTP/WebSocket、Inspector与MCP；部分DomDebugger分支为空返回，Jint脚本断点和CPU/GC性能不能当成原版CLR事件断点或三类Profiler。Designer写AXAML的行为不能默认带入当前“临时可撤销编辑”契约。
- Declarative AgentTools是另一种应用内HTTP/诊断集成，可复用具体算法或适配；不能仅凭MCP/诊断模块命名宣称全部后端适配完成。

源码存在、兼容验证与产品采用是三个独立证据层级。当前已实际接入XamlMcp公开Server/Protocol/Agent包并保留其身份、能力缺口和快照作用域，复用范围仅为共同只读能力；其他社区模块仍是候选或参考。对应许可与归属文本随构建输出保留。

## 剩余工作的依赖顺序

| 顺序 | 下一目标 | 先决条件和可交付验收 |
| --- | --- | --- |
| 1 | 开源后端兼容对照与首个采用已完成 | XamlMcp公开包只读后端已接入，CDP在12.1.4真实目标验证并记录成本/清理缺口。后续按剩余功能评估模块复用，不将上游写值/写源码直接等同可撤销编辑。 |
| 2 | 保持共同只读后端合同与统一回归 | Phase 8 最终 476 项统一 TRX 和 22 项目双配置零警告构建已完成；XamlMcp 缺失能力继续禁用，新增工作保持合同回归。 |
| 3 | 对象图、绑定与资源来源导航已有 | Source／DataContext 导航、文化配置、实际格式与四种源更新时机已测；继续原始声明字段、无值 setter 与跨请求一致性边界。 |
| 4 | 树分页与独立模板父入口已有 | 分支观察／释放、递归模板范围、游标分页、从模板父到部件路径、目标自身实际匹配及 BringIntoView 已验；继续复杂命名空间表达与更广虚拟化场景。 |
| 5 | 已构建项目文件附加已接入 | MCP 真实 stdio 已覆盖 classful AXAML、AppBuilder／App 资源、设计 guard、依赖程序集、输入／PNG、保存与 owned 清理；继续验证更广项目／平台，程序集变化需重启，IDE 自有进程附加另列。 |
| 6 | Profiler 已有真实数据，继续准确统计 | 已采用框架 ActivitySource 并实装三类别、录制／停止／清空、保留窗口分组与 GUI／MCP；继续验收选择器覆盖、hover 重评估与资源失败证据。Metrics 已有真实时间窗／聚合／UI 参数与同步图，继续完整 Runtime／CPU／GC 分析，不把 raw delta 改称累计值。 |
| 7 | 高亮／事件及既有编辑已扩展 | 盒模型投影／标尺／引导线、关联路由／默认保存与源跳转已有；专用文字／图片信息和布局分区 hover 已有；继续实例 handler 归因和恢复默认完整 GUI 路径。未支持画刷、预览取消清理、媒体格式、资源 winner 与日志适配仍独立推进。 |
| 8 | 原生渲染诊断与合成绘制已有 | 四个 native overlay、实际帧恢复、真实 Avalonia／Runtime 数据和独立合成 PNG 已验；继续效果／mask／组透明度／GPU／AOT 与完整时窗统计。Protocol Monitor 已有真实 Native 录制，继续快捷键／布局／主题等工具壳。 |
| 9 | Native WS 已接入，继续真实部署 | Windows loopback、认证／Origin、共享 gate、掉线恢复和 GUI／MCP 路径已测；继续可信 TLS 跨主机、LAN／VPN、WSL2 与其他 OS。浏览器授权桥、移动／WASM 和多客户端不由传输命名补全。 |

接下来继续按工作流补缺，先复用已验证的兼容层和成熟模块，再对公开实现与现有证据不能解释的差异做定向 REA。目标仍未完成，A 状态与阶段性绿灯不能代替其余条目的验收。

