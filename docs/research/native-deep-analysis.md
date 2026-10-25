# DeveloperTools 2.2.3 桌面主程序深挖

> 历史研究：本文对应完整桌面产品基线 `main/fa1c623`，其中 GUI、外部后端、旧路线建议和阶段计数不作为当前分支交付范围或通过证据。保留它用于来源、许可和设计研究；当前官方 Agent/MCP 合同与原版证据边界见 [官方能力基准](../agent-tools/research/official-capabilities.md)，当前状态见 [实现状态](../implementation-status.md)。

2026 年 10 月 24 日继续分析本机 Windows x64 发布产物。此次补充了 PE 导入、导出、资源的 REA 证据，实际启动 MCP 服务读取工具目录，并从原生字节提取编译绑定名称线索。桌面主程序的方法体与 3D 展示算法尚未还原。

## 目标身份和证据强度

目标为安装包中的 tool-win-x64/AvaloniaUI.DeveloperTools.exe，版本报告为 2.2.3+92df9d92acf0bbe8b2ffa96370c1bf848df81d27，55,258,952 字节。

~~~text
SHA-256
76a88bb2fd68d40a7b663a0f710c8bc18dc85bf9a2f732dc6f2f84c6ac0f9f08
~~~

| 证据 | 可以说明 | 不能说明 |
| --- | --- | --- |
| REA 离线 PE 解析 | 该哈希文件中的导入、导出、资源目录及精确范围 | 对应代码已经运行、某个业务功能可用 |
| 正常启动 MCP 后的 initialize / tools/list | 本机 2.2.3 服务实际报告的身份、工具名、参数 schema 和注解 | 已成功附加应用、已执行工具、全部授权前提已满足 |
| 原生字节中的名称和编译绑定候选 | 文件包含相应模块、UI 模型及属性名称线索 | 已解析原生类型元数据、已恢复 C#、已证实控制流或界面状态 |

没有操作登录或授权界面，没有修改授权检查。此次原版 GUI 正常启动后进入 AvaloniaUI DeveloperTools - Sign-In，仅记录入口，未操作认证对话框；本轮自启的 GUI 与应用 MCP 进程均已结束。

## PE 证据

| 范围 | REA 结果 | Evidence ID |
| --- | --- | --- |
| 普通导入与延迟导入 | 26 个 DLL、497 个导入函数、0 个延迟导入；目录覆盖 complete | ev_93dc87e5c267f61ecbb88deab956b235bf69ee1a78458c48e2084523d77c4541 |
| 导出目录 | 39 个导出，覆盖 complete | ev_23bd7ceb24a2fe570d7aee9977af93d002e54abfeb3342372a3ea8184caf9f6b |
| PE 资源 | 9 个资源，检查 22 个目录项，覆盖 complete | ev_3441a2660a19ecb60e980d03cb8dec3f2403f397abb0999180b5025a00aaf64c |

导入包括 Windows 窗口、COM、网络、密码学、文件和运行时相关接口。这些主要反映运行时与平台依赖，不能直接映射为 DevTools 面板的功能清单。

导出以 Brotli 压缩 API 为主，另有 DotNetRuntimeDebugHeader。没有导出可以直接作为 Elements、MCP、预览器或 3D 业务方法的名字入口；这仅是导出目录范围内的结论。PE 资源为图标、图标组、版本和 manifest，并未从该资源目录取得 AXAML 文本。

主程序没有普通 PE/CLI 元数据，托管启动器的 .NET 6 目标框架不能用于推断该原生程序的目标框架。原始字节中存在 RTR 头候选：文件偏移 48971344，版本字段 16.0；另一个位于代码区的同名字节序列是明显假阳性。此版本不在当前 REA 内置 NativeAOT RTR 9.1 恢复器的支持边界内，不能称已恢复类型和方法表。

辅助 PE 字节诊断还读取到 134,360 条 x64 exception-directory RUNTIME_FUNCTION 记录，起点均位于可执行节。这是 unwind／funclet 记录数，不能称为恢复出的 C# 方法数；它说明整幅原生代码的自动分析范围很大，后续应优先选择有证据锚点的路径。

## 实际 MCP 工具目录

通过正常启动该原生程序的 mcp 子命令建立 stdio MCP 会话，成功执行 initialize 和 tools/list。服务报告：

~~~text
name: AvaloniaUI DeveloperTools
version: 2.2.3+92df9d92acf0bbe8b2ffa96370c1bf848df81d27
capabilities: logging; tools.listChanged
~~~

本次未附加目标的初始目录实际包含 15 项工具。以下参数及默认值来自该运行中的服务所返回 schema：

| 工具 | 参数与细节 | 服务注解 |
| --- | --- | --- |
| attach-to-app | id 默认 0；描述称省略时列出可用应用 | 非只读、非破坏性 |
| attach-to-file | 必填 xamlFilePath、xamlProjectPath；build=false | 未声明只读注解 |
| tree | nodeId=0 表示根；支持 Logical / Visual / Merged；depth=10，-1 为全子树 | 只读 |
| ancestors | 必填 nodeId；返回从根开始的父链 | 只读 |
| search | 必填 query；scopeNodeId=0、searchTemplates=false、limit=-1 | 只读 |
| props | 必填 nodeId；可选 propertyNames | 只读 |
| set-prop | 必填节点、属性名和值文本；描述支持 null / unset；isXamlValue=false | 修改 |
| styles | 必填节点；可选属性名；includeDefaults=false | 只读 |
| resources | nodeId=0 为 Application；onlySelf=false | 只读 |
| assets | 无参数；描述称返回嵌入资产 URL | 只读 |
| open-asset | 必填 assetUrl，描述使用 avares:// URL | 只读 |
| screenshot | 必填 nodeId；描述称 PNG 输出 | 只读 |
| input | 事件枚举 Click / KeyDown / KeyUp / Text；可选 arg | 修改 |
| action | Focus / Enable / Disable / BringIntoView | 修改 |
| pseudo-class | 必填节点；可选 pseudoClass、可空 isActive；省略伪类时列举 | 修改 |

这 15 项工具都报告 execution.taskSupport=optional。注解是服务声明，不是安全性或执行效果的验证。

两个需要保留的版本细节：

1. 本次初始目录没有 detach。服务同时声明 tools.listChanged=true，尚不能排除附加后动态增加工具；若最新在线文档描述该项，需要保留版本与会话状态的区别，不能直接断言此版本完全不支持。
2. treeKind 的 schema 声明为字符串枚举，却给出数值默认值 0。本轮没有调用它验证省略参数时的实际处理，调用方宜显式传入枚举字符串。

原始目录保存于研究临时目录中的 app-tools.json，SHA-256 为 2b8a317abdd4e21692064db8d16301eb41fa1d3f0cea63560e23beaeb931c837，记录时间 2026-10-24T15:28:01.8559859Z。这些是 MCP 实际观察，不是 REA 原生反编译结果。

## 编译绑定名称提供的桌面结构线索

对该哈希原生文件的可打印 ASCII 和 UTF-16LE 字节运行做有限规则扫描，保留原始文件偏移。共得到 2,686 条与选定关键词相关的字面值候选；其中 765 条含 AvaloniaUI.DeveloperTools，再按编译绑定名称形状抽取出 366 条 Getter／Setter 候选，涉及 54 个 owner 名称。

这是针对名称形状的辅助扫描，既不是完整元数据解析，也不是源代码重建。原始片段可能带编码边界字符；以下只列完整匹配的名称。

| UI 模型声明线索 | 出现的属性／命令 | 后续调查方向 |
| --- | --- | --- |
| ElementsNavToolbarViewModel | AvailableTrees、CurrentTree、IsInspecting、TrackFocused | 树切换、拾取、焦点跟随 |
| ElementsNodeControlViewModel | CopySelectorCommand、CopySelectorFromTemplateParentCommand、FreezePopups、FocusNodeCommand、BringIntoViewCommand、OpenCompositionCommand、TopLevelDebugOverlays | 节点菜单、模板相对 selector、弹层保持、调试覆盖层 |
| ElementsNodeViewModelBase | ExpandCommand、CollapseCommand、ExpandRecursivelyCommand、PseudoClasses | 递归展开与伪类显示 |
| ElementsContentDetailsViewModel | Expression、Frames、Layout、SelectedTab、SettersMode、ShowInactiveFrames | 绑定／值帧／布局详情及失效帧显示 |
| BindingExpressionViewModel | Path、Source、Converter、ConverterCulture、ConverterParameter、FallbackValue、TargetNullValue、StringFormat、Mode、UpdateSourceTrigger、NestedBindings、Validation、NavigateSourceCommand | 结构化绑定详情与源对象定位 |
| ElementsLayoutViewModel | MarginThickness、BorderThickness、PaddingThickness、ContentWidth、ContentHeight 及各自 Highlighted 字段 | 盒模型可视化 |
| ElementsContentCompositionViewModel | Layers、RefreshCommand | 合成层数据页；相机、层间距或渲染算法仍未知 |
| RoutedEventFiredViewModel | ChainLinks、Handled、Source、HighlightEventCommand、SetSourceBreakpointCommand | 传播链、源对象和事件断点 |
| RoutedEventsViewModel | EnableDefaultCommand、DisableAllCommand、AddEventListenerCommand、SetGlobalBreakpointCommand、EventsFilter | 默认监听集、批量关闭、全局断点与过滤 |
| DebuggerBreakpointViewModel | CriteriaKind、CriteriaHitCount、SuspendExecution、LogMessage、RemoveOnceHit | 命中条件与断点动作 |
| PerformanceMeterGraphViewModel | P50、P90、P95、Series、XAxes、YAxes、ClearCommand | 分位数图表；计算方法仍未知 |
| ProfilerToolViewModel | SelectorGrid、StyleActivatorGrid、FindResourceGrid，三个对应 TotalDuration 字段 | 资源查找耗时也是 Profiler 调查类别 |
| ProtocolMonitorToolViewModel | Recording、Filter、RequestsGridSource | 协议记录与过滤 |
| ProtocolMonitorRequestViewModel | RequestArguments、Response | 请求与响应详情 |
| AssetDetailsViewModel | PreviewAssetCommand、ExportAssetCommand、CopyFullPathCommand、AssemblyName、RelativePath、FriendlySize、Properties | 预览、导出、路径和元数据 |
| AssetsNavToolbarViewModel | Filter、IsListView | 资产筛选及列表／网格切换 |
| ResourcesProviderToolbarViewModel | Filter、IncludeNested | 资源筛选及嵌套范围 |
| SettingsMcpPageViewModel | EditorTargets、GenerateAndSaveMcpbCommand | 编辑器配置与 MCPB 导出入口 |
| AppViewModel | CompactMode、ThemeVariant | 紧凑布局与主题 |
| TargetViewModel | IsTopMost、多项 splitter 尺寸、Session、ProtocolInfo | 布局、置顶、会话和协议信息 |

字节中的模块名称还包含 AvaloniaUI.Core.Mcp、ModelContextProtocol.Core、AvaloniaUI.DeveloperTools.Core、AvaloniaUI.DeveloperTools.Shared、AvaloniaUI.DiagnosticsProtocol.Server，以及 Previewer Process／Protocol／HostApp／DesignerSupport。命名支持按层调查，但不建立真实调用图。

预览器字面值中出现 BuildAvaloniaApp、.previewer.bin、PreviewerConfig、tcp-bson 和多个进程启动／通信错误。这些是后续精确原生交叉引用的候选入口，不证明本轮已经使用某种具体预览器实现。

## 从同一主程序提取到的预览器 CIL 组件

原始字节中的预览器名称并非全部只能作为字符串线索。扫描嵌入的 MZ 候选并校验 PE 签名、节范围、CLI 目录和 BSJB 元数据后，从该主程序派生出三个托管 PE。提取只包含各 PE 的节和头声明范围，不声称包含未知 overlay；未加载或执行这些程序集。

| 派生产物 | 主程序文件偏移 / 长度 | SHA-256 | MVID |
| --- | --- | --- | --- |
| AvaloniaUI.Previewer.Protocol | 31115595 / 73216 | 00b269e65f8fd252fb140e828f5c2287ac510b3c5b7d900c6665f8b7460a0e97 | 002fd03f-8aa6-4b2f-bf20-98391e371b54 |
| AvaloniaUI.Previewer.DesignerSupport | 31188811 / 50688 | 211ab8d6e287a9aad75283878d663eda13a8692a3781a2212e943fd8833560f9 | 79aab618-0c82-453f-a579-e377e663527a |
| AvaloniaUI.Previewer.HostApp | 31239499 / 16896 | 6a3cc64b5c36966db9d48e44af7f7998bcb81a64292758c5a2ee931ba8b938b2 | 63d13971-fe35-401d-be81-44633ca7c15d |

REA 对三者的身份和成员扫描全部完成：共 112 个类型、587 个方法、2,761 条 CIL 调用边。三者目标框架均为 net8.0；DesignerSupport 的 Avalonia 引用版本为 12.0.0.0。以下方法行为由 REA 元数据/CIL 调用关系与针对性 ILSpy 重建共同解释，重建仍不是原始源码或运行验证。

| 组件 | 身份父 Evidence | 成员 Evidence |
| --- | --- | --- |
| Protocol | ev_b62d070080200bb19657a832c722d93f161d8e739f3a85e102f922187d0925f0 | ev_3c733abe80ff04039f67bdbe06cbb229c097a5d6c9c59dd566254936e17e4ba8 |
| DesignerSupport | ev_3a8eb30047806e25da6deaa10afbc5d72539a4edcb4c44201b7dcca405d1d241 | ev_a518b8849f6ab1dc2700d7d6db7f160da8d6f7165d7b4e66962fe3966e2fa94c |
| HostApp | ev_f747033d5b9a6d5ec3f9af4eb6ef5e4675009dc78336d50f95fa5aab576bd055 | ev_ab349420e154ef4a107ca14f5624857360ca2e5d2f1542a538c381635b888aec |

### 预览器启动和重载链

~~~text
HostApp Program.Main
  -> 目标程序集旁的 .previewer.bin
  -> AssemblyLoadContext.Default + 依赖解析器
  -> DesignerSupport.EntryPoint.Main
  -> AppBuilderResolver
  -> Headless windowing + Skia rendering
  -> RemoteDesignWindowManager
  -> WindowSlot / DesignWindowLoader
~~~

HostApp 主入口的 token 为 0x06000001。它以最后一个命令行参数作为目标程序集路径，读取同目录同文件名的 .previewer.bin；缺少配置文件时使用空依赖清单。配置魔数是 AVPREV1，后面分别序列化托管与原生依赖字典。随后注册 AssemblyLoadContext 的 Resolving，预加载依赖，找到 DesignerSupport.EntryPoint.Main 并反射调用。原生桌面侧如何生成配置及启动 HostApp 仍待还原。

DesignerSupport.EntryPoint.Main（0x0600002a）建立传输和设计窗口管理器，解析目标 AppBuilder，使用 Headless windowing 与 Skia rendering，进入 UI Dispatcher 循环。解析先查目标入口类型的 BuildAvaloniaApp，再查 Application 子类型的同名方法；若没有该方法但有公开默认构造器，仍有创建 Application 的回退路径。

DesignWindowLoader 使用 AvaloniaRuntimeXamlLoader.Load，设置 LocalAssembly、DesignMode=true，并读取目标程序集的 AvaloniaUseCompiledBindingsByDefault 元数据。加载出的 Control 被放进 Window；Style 和 ResourceDictionary 可通过 Design.PreviewWith 提供预览内容，缺少时显示提示；该组件明确把 IDataTemplate 与 Application 文件标为不可预览。

WindowSlot.ApplyXaml（0x06000071）对新 XAML 创建并替换预览窗口，移除旧窗口的场景监听、关闭旧窗口并清空内容。它支持主题变体和渲染缩放。此观察不能推广为任意 C# 热重载，也不能声称保留重载前控件实例状态。

### 帧流和输入

预览器传输使用 tcp-bson。BsonStreamTransportConnection.Reader（0x06000051）读取 20 字节头：4 字节 payload 长度和 16 字节消息类型 GUID，再按类型反序列化 BSON。Send（0x06000052）采用对应布局。BitConverter 表示当前平台字节序，在本机为小端；不能把该观察泛称为跨端网络字节序。

FrameMessage 包含 SequenceId、Format、Data、Width、Height、Stride、DpiX、DpiY。PixelFormat 是 Rgb565、Rgba8888、Bgra8888；BitmapUtilities 复制或转换原始像素，行 stride 对齐到 4 字节。该预览帧的数据是像素缓冲，不能与 MCP screenshot 声明的 PNG 输出混为一谈。

WindowSlot.TakePendingFrame（0x06000079）在上一已发帧尚未 ACK 时不再取下一帧；pending 帧被较新的帧替换并释放，避免为每次场景变化保留完整帧队列。AcknowledgeFrame（0x0600007a）更新接收序号，RemoteDesignWindowManager 再尝试发送最新 pending 帧。该路径支持最多一帧在途的节流策略。

PointerMoved／Pressed／Released、Scroll、Key、TextInput 消息通过 UI Dispatcher 调用 Avalonia.Headless 的鼠标、滚轮与键盘 API。它们属于预览器的框架输入，不是向用户桌面发送 OS 级输入。

三份 CIL 实现使预览器的托管启动、XAML 解析、帧与输入链得到具体解释；原生编排、目标运行中的真实表现和多版本兼容仍未验证。自研实现应独立设计协议，并补齐长度、容量、取消和错误边界。


## Ghidra 的本次恢复路线

上一轮默认配置在 330,000 ms 启动期限内没有完成导入。此次启动独立的 REA 6.7.0 stdio MCP 进程，使用已安装的 Ghidra 12.1.4／JDK 21，配置：

~~~text
REA_GHIDRA_STARTUP_TIMEOUT_MS=900000
REA_GHIDRA_DISABLED_ANALYZERS=WindowsResourceReference,Decompiler Switch Analysis
~~~

配置仅作用于研究子进程，没有修改 Codex 注册或用户全局配置。Ghidra 日志确认两个分析器实际进入 disabled 状态；Windows 资源引用与部分 switch 恢复因此主动缩减了覆盖。

该配置在 900,000 ms 后仍返回 provider_timeout；错误没有确认 OutOfMemoryError，不能将超时归因于内存耗尽。已保存错误输出、成功关闭研究会话，并确认该轮 Java 进程结束。该轮导出调用被先前配置诊断留下的同名文件拒绝，不能称已导出可用原生记录；离线 PE 的三条 canonical 证据另已成功导出。

本研究拥有的 Ghidra Java 进程的一次只读线程诊断显示，主线程正在 FindNoReturnFunctionsAnalyzer.repairDamagedLocations / ClearFlowAndRepairCmd 中工作。已从本机 Ghidra 类常量核实其名称为 Non-Returning Functions - Discovered。这是后续调整分析覆盖的具体依据，不能由 CPU 或内存用量推断 OOM。

据此另起独立研究会话，额外禁用 Non-Returning Functions - Discovered，仍保留 900,000 ms 预算。日志确认三个分析器全部禁用；后续线程观察已进入 EntryPointAnalyzer.doDisassembly，但该轮仍在 900,000 ms 后超时，未取得原生业务函数伪代码。该轮成功导出会话空记录并关闭，研究 Java 进程已结束。不能把空记录、线程活动或未完成的反汇编当作功能实现证据。

本轮保留了两种明确不同的分析配置和超时诊断，不再以同一配置盲目重复整幅冷导入。下一步更有价值的路线是适配 RTR 16 的元数据恢复，或在可识别函数锚点建立后做更小范围原生分析；已有同产物 CIL 和公开运行接口可先支撑独立实现。

## 对自研的直接建议

1. 提供 ancestors 服务，在目标进程计算父链；检查器拾取定位可沿父链展开，减少当前最多 3,000 节点的全树搜索。
2. 样式帧、绑定详情和布局分别建模；绑定详情保留 Source／Path／Converter／Fallback／Validation 结构。
3. 事件记录保留传播链、Handled 状态和源对象句柄，断点配置与事件列表共享对象身份。
4. 合成服务与 3D 展示分层：定义图片／矩阵／尺寸／子层快照，桌面展示算法独立实现。
5. 将资源查找纳入性能调查清单；Profiler 与通用 Meter 图表保持各自语义。
6. 协议监视器记录请求、响应、耗时和错误，设置默认容量边界。
7. MCP 的只读工具、修改工具与附加生命周期显式区分，安装版目录与最新在线文档分开跟踪。

## 本机产物和未解决问题

研究临时目录：

~~~text
C:/Users/10062/AppData/Local/Temp/avdt-native-deep-20261024
~~~

| 文件 | 内容 |
| --- | --- |
| pe-offline-evidence.json | 3 条 REA canonical PE 证据 |
| app-server.json | 原版 MCP 实际身份和能力 |
| app-tools.json | 实际 15 工具与完整 schema |
| native-literal-candidates.json | 有文件偏移的字面值候选 |
| compiled-binding-candidates.json | 编译绑定 owner、属性与范围线索 |
| embedded-pe-inventory.json | 嵌入 PE 的父文件哈希、偏移、范围及提取哈希 |
| embedded-managed-*.dll | 由主程序字节派生的三个 CIL 组件，仅用于临时分析 |
| pe-and-embedded-evidence.json | PE 与三个 CIL 组件的 13 条 canonical 记录，约 4.3 MB |
| embedded-reconstruction-final.json | 精选 ILSpy 重建输出，未作为产品源码复制 |
| native-mcp-driver.mjs | 一次性研究驱动，不属于项目运行依赖 |
| ghidra-*.json | 独立 Ghidra MCP 调用结果与导出路径 |

临时文件不作为项目依赖，清理后不保证存在。长期说明以本文件、原版分析和功能目录为入口。

仍未解决：桌面原生业务方法体、3D 展示算法、真实 MCP 附加与操作行为、预览器原生端的配置生成和进程编排、Binding／Profiler UI 模型背后的聚合实现。原版 GUI 认证入口之后的行为也未观察，不能用命名候选或文档描述替代运行验证。

