# 自研 Agent / MCP 测试与验证

当前验证目标是 Windows 完整界面交互和真实原生文件流程。解决方案现有22项目：9个生产、3个样例、10个测试/支撑。常规测试包含 Agent、Cli、Protocol、Previewer、Remote 与新增 NativeAutomation；AotSmoke 和真实消费/fixture 的身份另外保留。

最新能力验证共580项：**579通过、0失败、1项Explicit未执行**。分项：Agent282、CLI118、Native127通过/1未执行、Previewer15、Protocol26、Remote11。22项目Debug/Release零警告零错误，340个输入无漂移，测试自有运行进程已清理。证据见 [capabilities-final](agent-tools/verification/2026-10-25-capabilities-final.json)，原始6份TRX在 `artifacts/agent-tools-ui-2026-10-25/capabilities-final`。本轮以[当前能力边界](agent-tools/architecture/current-agent-scope.md)为准，不包含业务判断、CI等已排除事项。

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
| Cli.Tests | 生产CLI子进程 initialize/tools/list/tools/call，默认agent39和显式official16/extended62、会话身份、媒体/预览和完整交互编排 |
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
