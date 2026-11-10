# DeveloperTools 2.2.3 诊断服务深度分析

> 历史研究：本文对应完整桌面产品基线 `main/fa1c623`，其中 GUI、外部后端、旧路线建议和阶段计数不作为当前分支交付范围或通过证据。保留它用于来源、许可和设计研究；当前官方 Agent/MCP 合同与原版证据边界见 [官方能力基准](../agent-tools/research/official-capabilities.md)，当前状态见 [实现状态](../implementation-status.md)。

应用侧 SDK 已包含样式和值来源、绑定表达式、资源、资产、日志、路由事件、断点以及性能数据服务。自研时应把这些能力放在 Agent 内，再通过独立 DTO 暴露；桌面检查器负责展示、过滤和交互。原版某些诊断接口虽然可公开调用，仍带有 `PrivateApi` 标记或使用私有字段反射，不能当作跨版本稳定协议。

本次覆盖官方 NuGet `AvaloniaUI.DiagnosticsSupport 2.2.3` 的 `lib/net10.0` 变体。下文的“观察”指 REA 元数据、CIL 调用关系及关键方法的 ILSpy 重建；没有执行该 SDK 的运行会话。桌面侧 UI 算法仍需独立证据，尤其是 Histogram 百分位、3D 展示和完整预览器流程。

## 产物和证据范围

- SHA-256：`6ce4138e84b9ed5fbd3aa446ef292e023e92ecd0a2ae18e261293c47ac27a6d2`
- MVID：`098e181d-5238-45a7-a325-c9de5bdee015`
- 类型总数 846，方法总数 7,224；本次使用精确类型选择，复用上一轮证据，未重做全量成员扫描。
- 本次四组精选扫描分别覆盖 12、10、5、12 个类型；第二组曾有一个嵌套类型未匹配，随后用元数据名称独立选择并完成扫描，未将未匹配项当作不存在。
- 第一组的元数据/CIL 成员覆盖完整，但三个 `#US` 字符串的终止标志与 ECMA-335 期望不同，REA 保留了可读 UTF-16 并报告字符串覆盖为 partial。本文不使用它证明字符串库存完整。
- ILSpy 输出包含部分依赖解析警告和非原始变量名。本文依据其可辨识控制流整理语义，不把重建文本当作可直接编译或复用的产品源码。

| 索引 | REA 父证据 | 主要范围 |
| --- | --- | --- |
| E1 | `ev_f1adc21e06599a5489c1c4bfef0e6cc00387b3fc9b139be322c491b3b6d24067` | ApplicationHandler、绑定诊断、Meter/Activity 和关键 DTO；237 个方法 |
| E2 | `ev_2b64a75367c68eefced656a349395393d3d0b4809c602a79f17639d086fceb56` | LogListener、AvaloniaSink、Debugger、日志及事件 DTO；146 个方法 |
| E3 | `ev_108fe0fdae75314b7cec0ed5097ccb1a355d028d9934de9807c33afd4b712b4f` | 路由事件、属性/事件断点、Meter/Activity 嵌套状态；31 个方法 |
| E4 | `ev_2f0807e5709b8b7c3e03083739e19e3e5c038c0f524e641a4642909a93f95b58` | 四类服务接口、请求和值/验证 DTO；163 个方法 |
| E5 | `ev_4cee3cddc6a77b22cdb65683b7ada8cc5595762cf025e0db14425fe2c58cc4db` | 上轮 VisualHandler、属性及 Composition 方法，已导入复用 |

精选方法页：E1 对应 `ev_98cacd842b94e895872e552cab7f79608b15f2375c237682955d35c18a99342f`，E2 对应 `ev_5f0c720314183e5881cfb5e05064c8805a26645df48cb03028ef6f3bd27e7bb6`，E3 对应 `ev_a8fa03407f2fc9f08453332fc83d557a8cb5e186e29be2aa1cfa9caae1dd62a6`。

本机临时证据目录为 `C:/Users/10062/AppData/Local/Temp/avdt-services-deep-1792855877079`：`services-evidence.json` 包含 29 条 canonical 记录、约 58.4 MB；`selected-reconstruction.json` 保存 22 组重建或调用结果。路径不是项目运行依赖，临时目录被清理后可能失效。

## 样式和值来源

`VisualHandler.GetValueStoreDiagnostic` 调用 `GetValueStoreFrames`，按请求中的属性 ID 提取相关帧。服务不是只有有效值的平面列表：

1. 通过 `GetValueStoreDiagnostic().AppliedFrames` 取得本地值、主题、样式和模板等帧。
2. 处理 Local 帧的排序，再映射来源类型、selector、活动状态、优先级和 setter。
3. 每个 setter 对应属性 ID，携带 `ValueVariant` 和可选 `ExpressionDiagnostic`。
4. 遇到资源引用时按目标 `StyledElement.ActualThemeVariant` 查找资源；遇到绑定扩展时使用带目标 DataContext 的临时 `BindingEvaluator` 评估。
5. 对有 styled-property metadata 的属性补充默认值帧；必要时从 `GetDiagnostic` 补充本地有效值帧。
6. 单个 setter 的评估异常被转成远程值，避免一条 setter 阻断整个列表。

帧 DTO 为：

| DTO | 核心字段 |
| --- | --- |
| ValueStoreDiagnosticFrame | FrameType、SourceSelector、SourceType、Priority、IsActive、Setters |
| ValueStoreDiagnosticSetter | Value、Expression |
| FrameType | Unknown、Local、Theme、Style、Template；协议另外有 Default |

`SourceSelector` 来自 `Style.Selector`，`SourceType` 根据 ControlTheme.TargetType、selector target 或元素 StyleKey 映射。已被覆盖的 setter 仍可能在帧中出现，UI 应分别展示“帧激活”和“最终值胜出”，不能把二者混成一个布尔值。

证据：E1、E5。官方公开源码中的 [GetValueStoreDiagnostic](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Diagnostics/AvaloniaObjectExtensions.cs) 是 public；其返回的 [IValueFrameDiagnostic 和 ValueEntryDiagnostic](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Diagnostics/IValueFrameDiagnostic.cs) 带 `PrivateApi` 标记。自研应把这种读取封装到 Avalonia 版本适配层。

## 绑定表达式

`AvExpressionDiagnostic` 分成“从 XAML/Binding 扩展声明映射”和“从活动表达式映射”两条路径。

| 分支 | 可观察数据 |
| --- | --- |
| ReflectionBinding / CompiledBinding | 路径或描述、Mode、UpdateSourceTrigger、StringFormat、Converter、Culture、Parameter、FallbackValue、TargetNullValue、Source |
| TemplateBinding | 目标属性、Mode；声明映射还带转换器信息 |
| MultiBinding | 子表达式集合、转换器、StringFormat、FallbackValue、TargetNullValue |
| StaticResource / DynamicResource | 资源键、是否动态 |
| IndexerBindingExpression | 源对象、源属性 |
| 未识别表达式 | 类型/描述和验证结果，保持 Unknown 状态 |

活动表达式使用 `IValueEntry.GetDataValidationState` 获取验证状态和异常，映射为 `ExpressionValidation` 与 `RemoteException`。Source 映射成远程对象引用，并计算源对象是否仍附着于视觉树。访问 BindingExpression.LeafNode 出错时返回空诊断，不能把这种情况表示为“无绑定”。

版本耦合集中在以下位置：

- 用非公开实例字段反射读取 `BindingExpression._mode` 和 `TemplateBindingExpression._mode`。
- 用非公开字段反射读取 `IndexerBindingExpression._sourceProperty`、`_source`。
- 按运行时类型名识别 `DynamicResourceExpression`。
- 数据验证和值存储接触 `Avalonia.PropertyStore` 的诊断结构。

[BindingOperations.GetBindingExpressionBase](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Data/BindingOperations.cs) 是公开入口，但取得入口对象不代表其内部所有诊断字段都有稳定公共访问器。官方 [Binding Expressions 功能说明](https://docs.avaloniaui.net/tools/developer-tools/elements-tool) 可用于 UI 行为规格；不能替代每种表达式的实现证据。

证据：E1，方法 `FromExtension` token `0x060001f8`、`FromExpression` token `0x060001f9`，四个私有字段读取 helper 为 `0x060001fc` 到 `0x060001ff`。这些 token 仅属于上面的产物身份。

## 资源树与编辑

资源服务接受应用级入口或对象 anchor。框架层能识别 Application、StyledElement、StyleBase、Styles、IResourceDictionary，并为部分 ResourceProvider 提供专用处理。UI 展示范围与 SDK 能接受的 anchor 范围应分开记录。

资源 provider metadata 包含对象引用、provider 类型、是否只读、是否可枚举字典、资源计数、Parent、MergedProviders 和 ThemeProviders。实现会遍历：

- ResourceDictionary.MergedDictionaries。
- ThemeDictionaries，以 ThemeVariant 文本作为键。
- Style.Children 和所属资源。
- FluentTheme 的部分颜色/资源 provider，经专用 helper 获得可枚举键。

`GetResourceEntries` 有两种语义：

- `FetchAll=false`：只读当前 anchor 的直接条目。
- `FetchAll=true`：从当前 metadata 开始填充，再选择请求的 ThemeVariant 或 fallback theme；找不到该主题时尝试 Default；继续 merged providers，最后沿 Parent 向上。内部按原始资源键去重，先遇到的键保留，并记录实际 owner。

`ResourceEntry` 带 Anchor、IsReadOnly、KeyType、Key 文本、ValueVariant。因此同名资源的显示值需要保留其实际来源，不能只给检查器一个键值 Dictionary。

写入经过 `SetResourceValue` 检查 KeyType 必须是字符串，随后按 Anchor 找到实际资源字典并执行赋值。只读 provider 明确失败。原版这一操作是直接字典写入，未见与本项目“可撤销属性覆盖”等价的资源编辑事务；自研应单独规定清除/撤销/断连时的语义。

证据：E1、E4，`GetResourceEntriesCore` token `0x0600015e`、`SetResourceValueCore` token `0x06000160`。

## 资产浏览

资产枚举扫描当前 AppDomain 中已加载、含 `!AvaloniaResources` 的程序集，构建 avares 基 URI，再调用 AssetLoader.GetAssets 和 Open 取得 URL、程序集、类型、路径、长度；过滤内部的 `!AvaloniaResourceXamlInfo`。未加载的程序集和外部文件不是这一枚举的完整覆盖对象。

打开资产只接受支持的 URI scheme。avares 分支可按 ImageOptions 的最大宽高尝试解码 Bitmap、等比例缩放并返回图片流；图片处理失败时回退到原始 stream。上层请求还有 MaxSize 检查。

自研必须区分“元数据枚举”“原始字节打开”“图片缩放预览”，并给每个请求设置大小上限。字体、XAML 和其他二进制资产的桌面专用预览器没有在本次 SDK 代码中还原。

证据：E1、E4，`GetAssetsCore` token `0x06000158`、`OpenAssetCore` token `0x0600015b`。

## 日志收集

`LogListener` 的第一个订阅者出现时订阅各 ILoggerObservable，最后一个离开时逐一释放。过滤规则同时检查全局 MinVerbosity 和区域 AreaVerbosity；全局阈值先应用，因此区域配置不能绕过更高的全局阈值。

日志 DTO 保存本地时间、level、area、消息模板、结构化参数、异常、Source 远程引用，以及 Source 是否仍属于树。异常参数转换为 ExceptionValueVariant，单独异常转换为 RemoteException。消息参数并非一开始就全部拼成字符串，检查器可以继续查看关联对象。

`AvaloniaSink` 在首次订阅时保存 Logger.Sink，将自己设为新 sink，并继续调用原 sink；最后退订时恢复原 sink。内部会排除 area 为 Diagnostics 且 Source 为 IRequestErrorHandler 的日志，避免诊断请求错误反馈形成日志回路。Microsoft.Extensions.Logging 等通过独立收集器适配进同一接口。

自研实现需要对后台线程产生日志、原 sink 被应用后续替换、订阅生命周期及队列上限分别设计，不能只把全局 Logger.Sink 置换后长期持有。

证据：E2；LogListener 的首/末订阅 token 为 `0x06000723`、`0x06000724`。

## 路由事件链

2.2.3 的事件链收尾机制与旧版开源 DevTools 不完全相同：

1. 根据 EventId 找到已注册 RoutedEvent。
2. 第一个观察者出现时添加 object 级 ClassHandler，订阅 Direct、Tunnel、Bubble，并接收已处理事件。
3. 按同一 RoutedEventArgs 对象在 ConditionalWeakTable 中积累链路，记录每一跳的 handler 对象引用、当前 Handled 和 Route。
4. 第一次看到事件时安排 DispatcherPriority.Background 回调；回调收尾后移除聚合状态，推送最终 FiredRoutedEvent。
5. 记录事件时的时间、最终 Handled、Source 对应链路，以及已格式化的常见参数；最后一个观察者离开时释放 ClassHandler。

这里的方法名虽然叫 `HandleRouteFinished`，本次看到的实际实现是 Dispatcher 延迟收尾，不是直接订阅某个 RouteFinished observable。自研需要测试同一事件的 tunnel/bubble 顺序、handled 传播、重入及派发结束时机。

参数格式化覆盖文本、键盘、指针、滚轮、焦点、selection 增删数量、size/range 旧新值、scroll delta、cancel 等已知事件。未识别参数返回空预览，不代表没有参数。

证据：E3；`HandleEvent` token `0x0600027b`、`HandleRouteFinished` token `0x06000279`。

## 属性和事件断点

断点由 Placement 和 Breakpoint 配置组成。Placement 包含属性/事件 ID 及可选目标对象 ID；省略对象可观察全局事件或某个 AvaloniaProperty 的全局 Changed 流。

- 属性断点：目标对象 PropertyChanged，或全局 property.Changed observable。
- 事件断点：目标对象 AddHandler，或 RoutedEvent.AddClassHandler；使用全部路由策略并接收已处理事件，以同一个 RoutedEventArgs 去重。
- SetBreakpoint 会释放同一 Placement 的旧断点；更新支持启用/禁用和重置命中计数。
- Trigger 先增加 HitCount 并推送命中更新，再判断小于、小于等于、等于、大于、大于等于等计数条件。
- 满足条件后可写 Information/Debugger 日志、调用 Debugger.Break，或在命中后释放自身。

这些条件是命中次数比较，不是任意 C# 条件表达式求值。暂停依赖运行时调试器行为；本次没有执行 Debugger.Break，也不把该服务称为完整 CLR 调试引擎。

证据：E2、E3，`BreakpointInstance.HitCondition` token `0x060006ec`、`Trigger` token `0x060006ed`、`DebuggerHandlerBase.SetBreakpoint` token `0x060006dc`。

## 性能 Metrics 和 Activities

### Instrument 发现和测量

`MeterObservable` 用 MeterListener.InstrumentPublished 发现仪表，分配弱引用 ID，输出 Id、Name、Description、Meter、Unit、IsDecimal 和 Type。Type 区分 Histogram、Counter、UpDownCounter、Gauge、Unknown。它识别同步及 Observable counter/up-down counter；可观察 gauge 单独归类。

`MeterInstrumentObservable` 首订阅建立 MeterListener，并为 byte/short/int/long/float/double/decimal 设置回调。请求选择 InstrumentId、FrameInterval 和 ObservablePullInterval；前者控制测量帧定时发送，后者定时调用 RecordObservableInstruments。

测量被统一为 double。主动 Counter/UpDownCounter 会累计增量；Observable 类直接使用测量值。帧保存 InstrumentName、MeasuredFrom、TimeStamp，数据项是 `(uint TimeStampMs, double Value)`，TimeStampMs 来自该 instrument 状态的 Stopwatch。

原始回调接收 tags，但该数据项没有 tags 字段；这条 SDK 通路没有保留按 tag 维度拆分的测量。Histogram 在此处也作为原始测量存储，没有看到 P50/P95/P99 计算。因此“Metrics UI 展示百分位”不能被改写成“SDK 已经计算完百分位”。

最后一个订阅者离开会释放 listener、两个 timer 和 instrument 状态。服务使用池化数组临时发送帧，自研客户端若异步保存数据应复制为自己拥有的快照。

### Activity 捕获

ActivityObservable 按 SourceName 管理状态，按 AllowedOperations 和 AllowedActivityTypes 决定采样范围，使用 ActivityListener 监听 Started/Ended。采样结果选择 AllData；不符合 operation 过滤时选择 None。

每条 activity item 保存 OperationName、Type、Id、ParentId、StartTimeUtc、Duration 和转换后的 Tags。按 FrameInterval 从队列批量发送 `PerformanceActivityFrame(Source, Timestamp, Items)`。退订会删除对应 source 状态并释放 timer；最后退订时清空 listener 委托并释放所有状态。

这种数据支持样式匹配、激活器等应用显式埋点的诊断。它不等价于对任意托管方法做 CPU 栈采样或 EventPipe GC 分析。桌面 Profiler 的聚合视图、图表与排序仍属于前端未知项。

证据：E1、E3、E4。新实现应明确时钟、double 精度、tag 维度、采样窗口、队列上限和丢弃计数；不必复制原版的 uint 毫秒及数据丢失取舍。

## 其他已找到的视觉操作

VisualHandler 还提供 Focus、SetEnabled、BringIntoView、Invalidate(Measure/Arrange/Visual)、SetPseudoClass、SetPopupsFreeze 和 SetRendererDiagnostics。能力查询按控件类型缓存，读取 PseudoClassesAttribute 获取可用伪类；顶层窗口声明 FPS、DirtyRects、LayoutTimeGraph 和 RenderTimeGraph 等覆盖层能力。

Popup 冻结通过跟踪 Popup.IsOpen 和所属 TopLevel，给 Popup.Closing 设置 Cancel=true；解除冻结时移除处理器并关闭所跟踪的弹窗。调试自身 overlay 的 Popup 被排除。此行为不同于暂停整个目标进程。

证据：E5 及本次保存的 VisualHandler 全类型重建。已有 API 不表示本项目第一版已经实现这些按钮。

## 自研 API 边界和验收建议

| 能力 | 可公开调用的入口 | 应隔离的版本耦合 |
| --- | --- | --- |
| 有效值 | GetDiagnostic、GetValue | 诊断返回模型及来源解释 |
| 样式帧 | GetValueStoreDiagnostic | IValueFrameDiagnostic 等带 PrivateApi，frame/source 类型变化 |
| 绑定 | BindingOperations.GetBindingExpressionBase | 私有 mode/source 字段、LeafNode、PropertyStore 验证模型 |
| 资源 | Application/StyledElement.Resources、IResourceDictionary、TryGetResource | Theme provider 形状、FluentTheme 特例、资源查找优先级 |
| 资产 | AssetLoader.GetAssets/Open | 程序集加载覆盖和每种预览器格式 |
| 日志 | Avalonia.Logging.ILogSink / Logger.Sink | 应用自身 sink 链、后台线程和释放时机 |
| 路由事件 | RoutedEventRegistry、AddClassHandler、AddHandler | 派发收尾、重入与参数类型 |
| 断点 | PropertyChanged、AvaloniaProperty.Changed、Debugger.Break | 全局订阅成本、调试器未附加行为 |
| 性能 | .NET MeterListener / ActivityListener | 埋点命名、tag/计量语义、前端聚合 |
| 视觉动作 | Focus、BringIntoView、Invalidate、PseudoClasses、RendererDiagnostics | Compositor/renderer 能力差异和会话清理 |

建议把扩展顺序设为：只读样式/绑定诊断 → 有界日志与事件流 → 资源/资产浏览 → 资源编辑与断点 → Metrics/Profiler。每一项均通过同一协议/Client 服务暴露，GUI 与未来 MCP 共用。

最低验收应覆盖：激活与未激活 setter；绑定失败和恢复；主题与 merged 同名键优先级；非字符串资源键拒绝编辑；原日志 sink 持续收到消息；事件 tunnel/bubble 与 handled 链；断点启停及计数条件；counter、observable gauge、histogram、activity 的已知输入与清晰的丢弃/采样统计。

