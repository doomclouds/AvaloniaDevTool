# 0.1.0 测试与实物验证

本轮为能力对象拆分、全解决方案分层、唯一MCP合同、本地/HTTP执行器、单exe与用户目录安装的验收。当前29项目、15生产；没有旧profile、类型转发SDK或平铺客户端API。下面旧批次均为历史记录，不与本轮计数累加。

## 构建和格式

完整Debug solution单线程构建已通过，0警告0错误。ReSharper检查369个编译/结构文件无待修改；Black26.10.1、Prettier3.9.10、PSScriptAnalyzer1.25.0验证通过，新增打包/安装/构建脚本纳入辅助格式范围。最后修复另按所属文件格式化。

## 功能回归与保留的失败证据

| 批次 | 结果 | 范围 |
| --- | --- | --- |
| Agent能力与生命周期 | 287/287通过 | 树、属性/绑定/覆盖、资源、输入、诊断；含新增能力清理隔离 |
| Protocol当前合同 | 30/30通过 | 源生成DTO与集合、RPC、客户端消费；含4项旧响应/旧通知跨重连隔离 |
| Previewer | 15/15通过 | 文档、项目上下文、重载、失败保留与渲染 |
| Remote底层SDK扩展 | 11/11通过 | 可选WebSocket能力，不是用户MCP远端入口 |
| Native最终完整回归 | 137/137通过；另1项Explicit未执行 | 真实UIA/OS、文件弹窗、输入、身份与清理；含10项游标到达确认规则 |
| CLI首批唯一目录全量 | 190项：185通过、5失败 | 冷启动握手、opaque资产期望、预览depth/取消及worker测试清理暴露问题，失败记录保留 |
| CLI首批修复精准回归 | 17/17通过 | 上述5处及协议探测、scope错误同步、新网关切换/图片 |
| CLI完整复核 | 193项：191通过、2失败 | 一项测试过度依赖SDK异常文字；一项真实picker刚出现时树暂不完整 |
| CLI最后精准复测 | 6/6通过 | 两项协议协商测试及4项实际文件读写失败流程；修复的是wire断言和等待期的只读瞬态重试 |
| FileDialog等待合成回归 | 新增2项red→green，相关3项通过 | 首次截断后完整、持续截断到期限仍拒绝；同session、0动作、慢RPC不被poll期限取消 |
| 安装脚本 | 隔离检查通过 | WhatIf、首次/幂等、版本保留、同版本不同内容拒绝、错误hash拒绝、锁定入口回滚、用户环境未修改 |

这些表项分别说明实际运行批次：没有把失败批次回写成0失败，也没有把精准重跑称为再次完整执行。原生早期并发桌面批次出现光标干扰与临时映像文件清理失败；改为独占验证、只读等待一次move到达、测试owned目录有界清理后，最终137项通过。

原始TRX位于`artifacts/test-results/0.1.0/`及本轮Temp专项目录。Native Explicit精确HWND复用压力测试未作为常规回归自动运行。

## 协议、媒体与真实进程

MCP2.3默认客户端先discover2026、探测超时后在同连接initialize；服务端可能已经依据_meta切到2026，导致初始化拒绝。0.1.0在生产统一配置明确2025-11-25，HTTP执行器同样使用该已验证合同；外部与内部hop不共用元数据。新增wire回归使用默认未pin客户端和延迟响应，hard-pin不支持版本明确拒绝；检查真实wire支持版本而非SDK异常消息排版。

网关真实stdio→HTTP→stdio验证固定目录、旧节点拒绝、失败预览保留原引用、断线不回本机/不重放、PNG解码后尺寸/颜色和元数据。图片不是文件路径，原始采集/自定义JSON不能被误当句柄重写。

可见演示通过本机Console附加AutomationSample PID13572，输入`HTTP MCP demo 0.1.0`、点击三次、提交并回读`Clicks=3`及提交文本，HTTP截图为840×645的实际多色PNG。演示程序已正常关闭以释放构建锁，证据在`artifacts/visible-console-demo/`。

## 单文件分发验收

打包工程与最终EXE分别验证。早期实物冷解包发现Windows对已提取整树MoveDirectory的AccessDenied；FirstChance三次确认，未通过延长重试掩盖。当前改为payload独占锁内直接提取、逐文件验证后最后写manifest commit marker；中断或不完整不启动，下一次修复。暖缓存临时读锁直接保留原IO错误，不误当损坏并删除正在使用的目录。

提交标记、8并发、中断/完整性/路径边界和真实目录锁等包装专项16/16通过。最终实物的SHA、内嵌文件、冷/热耗时、无PATH dotnet、stdio89/HTTP88、父进程退出回收及真实EXE隔离安装结果，以最终验证报告追加记录；中间快照不代表最终交付。

完整校验成本不隐藏：一次中间热校验曾耗时43秒，另一次24.6秒。MCP配置示例预留120秒启动时间，不以专项证明所有机器启动性能。物理LAN、防火墙、其他交互桌面以及未安装运行时的干净Windows环境需另外实测；本机HTTP和隐藏PATH dotnet不能等价替代这些证据。

## 最终实物记录

最终分发文件：`artifacts/package/win-x64/AvaloniaDevTool.exe`，版本0.1.0，214,722,986字节，SHA-256：`fcc906227a4ba558f167b64c34231e2596b6843f56b0cc72c6033f4c497ea860`。独立Packaging工程生成，目录只有这个EXE；payload为`2cd75a08fa56c2ede23275209072fbba8324b5355ccbb7c110debbeb5e999955`，861个文件，不含旧Client/NativeAutomation/Protocol兼容程序集。

真实EXE的两项网关E2E全部通过（22秒）：实际exe启动stdio网关和HTTP Console，检查本地/HTTP/返回本地、真实PNG尺寸/像素/元数据、旧节点、失败选择和断线不重放。最后bootstrap按同一源码重新发布，整个EXE SHA逐字节相同，因此该证据对应最终文件。

完整CLI批次与精准复测按testName取最新结果，193个唯一测试均有通过结果；这是一份跨批次只读汇总，不声称另外执行了一次完整193项：`artifacts/verification/cli-latest-0.1.0.json`。所有原失败TRX继续保留。

分发及安装字节校验报告：`artifacts/bundle-release-0.1.0/report.json`，版本/文件/SHA索引：`artifacts/verification/release-0.1.0.json`，编译输入：`artifacts/verification/source-0.1.0.json`。安装检查在隔离路径运行，未修改用户PATH或HOME；实际用户安装由安装脚本显式执行。

---

## 历史批次（2026-10-26及以前）

当前验证目标是 Windows 完整界面交互和真实原生文件流程。解决方案现有22项目：9个生产、3个样例、10个测试/支撑。常规测试包含 Agent、Cli、Protocol、Previewer、Remote 与新增 NativeAutomation；AotSmoke 和真实消费/fixture 的身份另外保留。

## ReSharper 格式化后续批次

2026-10-26，使用固定版本 ReSharper 和完整团队 DotSettings 格式化全仓库。最终集成 `-Check -IncludeAuxiliary` 通过：ReSharper 369 文件、0 待修改；辅助 Python、PowerShell、HTML/JSON 检查通过。338 个 C# 的 token、预处理分支和字符串运行值均等价，31 个 XML/AXAML/SVG/项目文件及 JSON 值等价；`.editorconfig` 的文件匹配范围扩展是明确的配置变化。

本批次 22 项目 Debug 构建零警告零错误，针对性回归 **99/99 通过**（CLI 70、Protocol 26、Agent 路由 3）。这不是全部桌面用例的再次执行，不与下面历史批次累加。完整设置、格式命令、哈希及清理状态见[代码格式约定](code-style.md)和[格式化验证记录](architecture/formatting-2026-10-26.json)。历史验证清单保留原始源码指纹。

## 本轮 HTTP MCP 与结构重构验证

2026-10-26，22 项目 Debug/Release 均 **0 警告、0 错误**。本轮完成领域目录、Agent 同步路由、共享 managed 上下文和内网 HTTP 宿主，保留 stdio 与原有能力。摘要、原始 TRX 哈希、381 个源码输入指纹和发布包哈希见[验证清单](architecture/verification-2026-10-26.json)。

| 验证批次 | 结果 | 解释 |
| --- | --- | --- |
| 完整 Debug 批次 | 652 项发现、651 项执行：643 通过、8 失败；1 项 Explicit 未执行 | 八项既有 OS 输入均被同一系统 PickerHost 前台阻挡，记录仍为失败 |
| 最终源码的环境过滤复核 | 排除上述八项后，643 通过、0 失败；1 项 Explicit 未执行 | 排除名单保留在验证清单的完整批次失败列表中；原八项未因此变为通过 |
| 前台环境恢复后的定向重测 | 8/8 通过 | PickerHost退出后，仅重跑原3项CLI与5项native失败用例；生产实现未变，历史批次不回写 |
| 新增及迁移关联 CLI 检查 | 69/69 通过 | 参数、managed 代际、控制租约、HTTP、真实 native 模态/文件/PNG |
| 完成审计补证 | 4/4 通过 | HTTP真实项目预览/资源/PNG/重载/回收；三类capture原始数据/损失/冻结/DELETE隔离 |
| Agent 同步路由专项 | 3/3 通过，包含在 Agent 的 285 项中 | 74 条同步路由、23 条无参忽略 payload、Ordinal/null fallback |

完整批次分项：Agent 285 通过，CLI 184 通过/3 失败，Native 122 通过/5 失败/1 Explicit 未执行，Previewer 15、Protocol 26、Remote 11 通过。最终环境过滤复核仍覆盖六个测试项目。HTTP 最后的取消回调异常路径整理发生在完整批次之后；最终源码由过滤复核与发布烟测确认，两个批次分别保留身份。

原生输入的阻挡还通过独立 Computer Use 复现：向本任务自建 fixture 的正常点击也被 PickerHost 覆盖而拒绝。没有关闭归属不明窗口或弱化前台/进程校验。Native worker 九份代码与重构前逐段一致，只有位置改变。随后 PickerHost 退出，原八项在相同生产代码上定向复测8/8通过。该重测与原完整批次分开记录；加上环境过滤确认和4项补证，分批覆盖655个不同用例通过，另1项Explicit未执行，不声称这是单次整批结果。详见[当前限制](architecture/limitations.md)。

## HTTP、Codex、WSL 与分发证据

- 真实 HTTP SDK 客户端无 Token 连接，发现44项工具；跨请求附加/读写/PNG、双客户端 busy、detach、DELETE、旧节点与生命周期均验证。
- Windows 真实 HTTP 闭环在 managed 调用停留于 MessageBoxW 时，通过同会话 native 应答，并完成 UTF-8 打开、保存、独立磁盘字节/hash、重新打开及 PNG。
- 实际 Codex CLI 0.164.0 通过隔离配置对生产宿主与发布宿主发现44项工具。没有创建模型 thread/turn、没有模型调用、没有修改用户配置。
- WSL Ubuntu-24.04 的标准库 Python 客户端经 mirrored localhost 访问 Windows 生产宿主通过；Windows 客户端到非回环接口也通过。两条路径覆盖发现/附加、树/搜索/属性、PNG 和真实 native 窗口。
- WSL 的另一条非回环路径 initialize 成功、tools/list 200，但正文读取超时；原因未确定，未修改系统网络。它仍为未通过场景。独立两台物理电脑的 LAN 尚未现场验收。
- Windows x64 framework-dependent 发布成功，包含 Previewer/worker/deps/runtimeconfig/第三方许可；发布目录通过同一 HTTP 烟测。交付 ZIP 约15.5 MiB，保留在 `artifacts/releases/AvaloniaDevTool-win-x64-lan-mcp.zip`。
- WSL 没有额外安装 .NET SDK、第三方 Python 包或容器。临时源码探针已移除，独立探针数据/进程已清理；共享产物最初约10.8 GiB。任意目录/逐文件/单文件删除仍受自动审查拒绝（仅返回 blocked by policy）；独立审核允许的项目原生 dotnet clean 已按 Debug、Release、win-x64 清理 MSBuild 声明产物，没有改写 Clean target 或注入 FileWrites。当前 %TEMP%/AvaloniaDevTool-lan-development 仍有约611 MiB 未登记的副本、发布目录和结果，少量截图/工作记录也保留；清单见验证记录。测试应用、宿主和worker进程均已结束，未删除用户数据或全局缓存。

完成审计后仅补充测试和兼容的夹具访问，生产实现未变；这些4项单列，不回填原完整批次的652项计数。

HTTP 复现使用[标准库烟测脚本](../tests/scripts/Test-McpHttp.py)，被测机的启动与 Codex 标准配置见[内网部署](architecture/lan-mcp-design.md)。系统输入、框架 TextBox 临时覆盖和虚拟网络的限制见[当前限制](architecture/limitations.md)。

## 2026-10-25 历史基线

该历史能力验证共580项：**579通过、0失败、1项Explicit未执行**。分项：Agent282、CLI118、Native127通过/1未执行、Previewer15、Protocol26、Remote11。22项目Debug/Release零警告零错误，340个输入无漂移，测试自有运行进程已清理。证据见 [capabilities-final](agent-tools/verification/2026-10-25-capabilities-final.json)，原始6份TRX在 `artifacts/agent-tools-ui-2026-10-25/capabilities-final`。该历史批次以[当前能力边界](agent-tools/architecture/current-agent-scope.md)为准，不包含业务判断、CI等已排除事项。

## 修复阶段历史批次（417项）

核对日期：2026-10-25。双击/焦点修复阶段的Debug统一批次417/417通过，0失败0跳过；22项目Debug/Release构建均零警告零错误。该历史批次的源码指纹、逐工程TRX和DLL哈希见 [修复阶段结果](agent-tools/verification/2026-10-25-native-fixed.json)。原始6份TRX在本机 `artifacts/agent-tools-ui-2026-10-25/native-fixed`；当前结果以本页开头的agent-ui-final批次为准。

修复前历史整批为 **413项：411通过、2失败、0跳过**。该版TRX、哈希、失败方法和构建时源码指纹见 [2026-10-25-ui.json](agent-tools/verification/2026-10-25-ui.json)，本机原始TRX在 `artifacts/agent-tools-ui-2026-10-25/verification-round`，不代表后续417项或当前579项执行结果。

两项失败为同点原生双击业务计数与80次目标窗口切换（77次成功、3次安全拒绝）。实际文件MCP大流程和普通完整UI大流程已通过。后续诊断用例与源码修改另记专项；不能合并不同源码结果冒充413项全绿。

| 批次 | 已知结果 | 不能推出的结论 |
| --- | --- | --- |
| AgentInteractionMcpTests | 新真实 stdio 交互专项7/7通过，包含工具表面、双击/右键、组合键/选区、选择/provider、拖动、旧节点和等待 | headless目标不等于全部 Windows 桌面场景 |
| NativeAutomation.Tests | 修复阶段24/24通过，含UIA、scope、关闭竞态、80次激活/文本、第三root抢占、Clip和DPI恢复；边界补证36/36，此前44/44；该历史批次54通过/1失败/1未执行；当前127通过、1项Explicit未执行 | 其中16条路径预检是子集，不可重复相加；业务效果须用对应读回证据 |
| 原生文件 Controller 桌面批次 | 真实 AutomationSample 与平台 StorageProvider 的9个场景通过，核对应用结果及实际文件内容/长度/hash | 直接Controller API不是MCP tools/call验收 |
| NativeFileDialogMcpTests | 最新统一批次包含真实文件/普通UI、十轮同点双击/手势和错误证据保全单测，全部通过 | 错误保全单测的假输入不是真实UI样本；不把成功推导到未覆盖provider/输入设备 |
| 构建/发布 | 当前22项目Debug/Release均0警告0错误；linux-x64 framework-dependent交叉发布通过 | Release仅构建，Debug跑测试；未实际Linux运行 |

原生文件Controller证据位于本机 `%TEMP%/avdt-native-dialog-validation/acceptance.jsonl` 和对应 app-result JSON。原生底层测试与文件预检同属 NativeAutomation.Tests；专项、Theory展开行、桌面检查点与Fact方法不可直接累加成一个总数。

历史agent-ui-final批次为477通过、1失败、1项Explicit未执行，原失败项独立复测80/80通过；记录未被改写。最新579通过/0失败来自当前独立完整批次，不与历史结果拼接。

## 构建与常规测试

从仓库根目录执行，构建和结果隔离到临时目录。具有成功依赖的命令分次运行：

```powershell
dotnet build AvaloniaDevTool.slnx -c Debug --artifacts-path "$env:TEMP/avdt-complete-ui-verification"
```

```powershell
dotnet build AvaloniaDevTool.slnx -c Release --artifacts-path "$env:TEMP/avdt-complete-ui-verification"
```

```powershell
dotnet test AvaloniaDevTool.slnx -c Debug -m:1 --artifacts-path "$env:TEMP/avdt-complete-ui-verification" --no-build --no-restore --logger trx --results-directory "$env:TEMP/avdt-complete-ui-results"
```

在真实Windows交互桌面执行原生专项；无法使用交互桌面时的skip保留为未验证。不同测试可能启动真实窗口，避免多个进程同时向同一桌面注入输入。

```powershell
dotnet test tests/AvaloniaDevTool.NativeAutomation.Tests/AvaloniaDevTool.NativeAutomation.Tests.csproj --artifacts-path "$env:TEMP/avdt-native-verification" --logger trx --results-directory "$env:TEMP/avdt-native-results"
```

```powershell
dotnet test tests/AvaloniaDevTool.Cli.Tests/AvaloniaDevTool.Cli.Tests.csproj --filter FullyQualifiedName~AgentInteractionMcpTests --artifacts-path "$env:TEMP/avdt-stdio-interaction" --logger trx --results-directory "$env:TEMP/avdt-stdio-interaction-results"
```

```powershell
dotnet test tests/AvaloniaDevTool.Cli.Tests/AvaloniaDevTool.Cli.Tests.csproj --filter FullyQualifiedName~NativeFileDialogMcpTests --artifacts-path "$env:TEMP/avdt-native-mcp" --logger trx --results-directory "$env:TEMP/avdt-native-mcp-results"
```

测试命令是复现入口，不表示本页已执行所有命令。最终记录区分Debug测试与仅Release构建；源码变化后只在新改动、失败或未解决疑点存在时扩大验证。

## 各层的独特断言

| 工程/目标 | 验证职责 |
| --- | --- |
| Agent.Tests | 实际Avalonia/Skia、UI线程、属性/绑定、资源范围、输入路由/capture和服务清理；InteractionInspectionTests覆盖新增输入合同 |
| Cli.Tests | 生产CLI子进程 initialize/tools/list/tools/call，默认agent44和stdio显式official16/extended62，新增HTTP agent44、会话身份、媒体/预览和完整交互编排 |
| NativeAutomation.Tests/Fixture | 独立Windows目标、UIA真实读回、owned modal、scope/start time/stale、worker有界失败/重附加、OS输入和路径preflight |
| Protocol.Tests | 内部帧、分片/取消/断开、trace、源生成JSON及新输入DTO |
| Previewer.Tests、PreviewFixture/Controls | 真实XAML、App上下文、独立库、重载/监视/错误保留和PNG |
| Remote.Tests | loopback认证、Origin、TLS拒绝、共享会话门和清理；不扩大为跨主机native能力 |
| AutomationSample | 真实ClassicDesktopLifetime多窗口/模态/弹层、命名控件、真实StorageProvider读写、结果JSON和退出释放Agent |
| Sample、AotSample、AotSmoke | 其他SDK消费回归和实际AOT发布消费链；Sample默认仅Debug启用Agent，AutomationSample在Release也启用 |

canonical兼容专项继续由OfficialConnection/Tree/Properties/InputMedia/Preview测试维护，原版schema只支持声明对照；不以canonical已有通过数抵扣新完整UI清单。

## 完整界面和文件验收

固定验收ID和目标结果见 [完整UI自动化清单](agent-tools/architecture/complete-ui-automation.md#固定功能验收清单)，工具语义见 [Agent交互](agent-tools/features/agent-interaction.md)，稳定控件/启动参数见 [AutomationSample](../samples/AvaloniaDevTool.AutomationSample/README.md)。

每个用例记录前置数据、工具调用、可等待条件、实际状态和失败证据。真实UI应观察文本/业务计数、焦点、选择、toggle/expand、滚动偏移、drag结果、多窗口owner、模态禁用和弹层关闭。InputResult.Delivered、NativeInputResult.Injected、UIA completed/pending/dispatched都不是业务断言。

文件验收使用独立临时目录和真实OS picker，不替换StorageProvider/TopLevelProvider。单选/多选/文件夹/另存新文件/真实覆盖Yes和No/四类取消/类型过滤/中文空格路径/错误路径/占用权限及非法UTF-8分别断言。保存后独立读取磁盘字节、长度和SHA256，并再次通过真实Open读回；短内容覆盖长文件必须确认旧尾部截断。取消对比原文档、路径及旧文件hash。Controller持续声明ApplicationIoVerified=false，应用结果JSON也不能替代独立磁盘读取。

并发验收使用同一MCP进程：managed动作停留在真实模态等待期间，native仍能枚举和操作owned窗口；节点数值/字符串域不可混用，进程PID+start time和owner不符必须失败。worker超时/取消应有界回收并可重新附加；清理只终止测试拥有的进程。

## 发布与平台验证

worker固定net10.0-windows7.0，由net10.0 facade隔离。项目引用使用SkipGetTargetFrameworkProperties，避免重复TFM属性造成worker并行实例竞态。BuildNativeWorker按宿主和RID选择，非Windows RID不携带worker；Agent/Cli第三方许可文件TargetPath各自命名，避免publish冲突。

linux-x64 framework-dependent交叉发布已通过，可按以下命令复现；没有实际Linux运行、图形或输入验证：

```powershell
dotnet publish src/AvaloniaDevTool.Cli/AvaloniaDevTool.Cli.csproj -c Release -r linux-x64 --self-contained false --artifacts-path "$env:TEMP/avdt-linux-publish"
```

Windows native只支持本机交互桌面及已附加应用的owned窗口，remote明确拒绝。framework/OS输入、IME、文件DragDrop、跨平台渲染和严格Native AOT均分别定界。`tests/scripts/Test-NativeAot.ps1` 与AotSmoke负责实际发布消费；历史兼容警告模式不等于严格零警告AOT。

## 历史基线

较早canonical阶段统一368项及当时17项目构建结果见 [历史验证索引](agent-tools/verification/2026-10-25.json) 和 [源码清单](agent-tools/verification/source-manifest.json)；原始TRX位于Git忽略的 `artifacts/agent-tools-2026-10-25/complete`。它们绑定较早源码，不是新39工具后的总数。[旧桌面16检查](agent-tools/verification/desktop.md)没有原生file picker或OS输入，也不能扩展为本次完整目标已过。

[历史官方兼容进度](agent-tools/progress-current.md)中的48/52只描述官方公开要求的本地验收口径；原版许可运行对照仍deferred，不对自研工具复制Community产品限制。更早 `main/fa1c623` 的476项/约63%属于完整GUI历史，退役Inspector/XamlMcp测试不会自动转移为CLI覆盖。当前交付口径由 [实现状态](implementation-status.md) 汇总，最新统一批次以本页开头的验证记录为准。

## 本轮窗口身份与文件类型修正

真实HWND复用红样本、销毁epoch防护和父/子组合失效说明见 [窗口身份研究](agent-tools/research/native-window-identity.md)。普通destroy/recreate、真实根销毁通知及旧根/旧子ID拒绝已进入本批；该整批时尚无修复后完整复用成功样本；后续独立Explicit已1/1通过，原未运行结果不改写。

此前中间批次有1项真实保存类型下拉file_filter_timeout，结果见 [历史失败记录](agent-tools/verification/2026-10-25-window-lifetime-intermediate.json)。修正保留一次Expand，仅在它已完成、combo仍Collapsed且唯一直接DropDown支持Invoke时回退一次；9项合成provider正/负用例与真实默认扩展名、保存后重开流程分别验证。真实键鼠回归要求用户也暂停操作桌面；整批窗口切换失败和无源码变更的独立复测均保留，复测通过不证明鼠标干扰是唯一原因。它不证明初始Expand未展开的唯一OS原因，也没有用改断言或增加sleep隐藏失败。

VSTest/xUnit对Explicit可能给单项NotExecuted、汇总notExecuted却为0；本项目验证JSON按逐项outcome计算并保留rawCounters。长时实验的显式入口及预算失败记录见研究文档，正常回归不自动反复循环系统句柄。

## 最终基础能力批次

默认agent 44项：canonical16 + ui9 + native8 + file8 + capture3。截图与采集合同见[交互能力](agent-tools/features/agent-interaction.md)。ui-screenshot使用控件本地DIP，native-screenshot使用节点本地物理像素，返回PNG与坐标/尺寸/限制。真实Headless/Skia、WinForms SDK与stdio MCP→实际Avalonia窗口/OS文件弹窗的截图分别核对实际像素；70条几何用例只证明逻辑边界。

原始诊断只提供metrics、样式/资源活动和日志/事件。样例业务流程断言仅存在于测试，不提供产品业务断言、场景运行器、视觉/性能门禁或CI。官方许可对照、跨平台native、完整CPU/内存剖析、IME候选和文件/数据DragDrop均按用户决定排除。

唯一Explicit实验为修复后完整HWND循环复用；统一批次当时未运行，不能改写成整批通过。随后按用户要求独立执行1/1通过，见[显式验证](agent-tools/verification/2026-10-25-hwnd-reuse-verified.json)。红样本、实际根销毁/旧节点拒绝与异步通知局限保留在[窗口身份研究](agent-tools/research/native-window-identity.md)。常规键鼠回归也已完整通过。

独立显式跟进：完整句柄593860在32,766轮、61,173ms后复用，旧根/子ID在fresh枚举前后读取及后续写入/Focus均被拒绝，新内容保持。源码无变更、产物一致、自有进程残留0。独立结果与常规579项分别记录，不虚构成一次580项整批；其动作顺序和未覆盖分支见研究文档。
