# 结构化绑定诊断

样式绑定页现在按字段展示绑定证据、可展开的子表达式和异常链。`StyleSnapshot.Frames` 保持现有结构；`BindingDiagnosticInfo` 末尾新增可选 `Details`，旧服务端和旧 JSON 仍可读取。setter 的 `MatchesEffectiveValue` 继续为可空值，缺少比较证据时保持未知。

## 来源与复用选择

本实现首先审阅并编译了公开的 [XamlMcp BindingDiagnosticsCompat](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Avalonia/Compat/BindingDiagnosticsCompat.cs) 和 [BindingDiagnosticsService](https://github.com/trrahul/XamlMcp/blob/1718f946cb275a71e021de58f4caa552e04feaa8/src/XamlMcp.Avalonia/Values/BindingDiagnosticsService.cs)。`XamlMcp.Avalonia 1.0.0-preview.3` 的公开 `ResolveGetBindingInfo()` 在 Avalonia 12.1.4 下可直接编译，探测结果 `IsResolved=true`。公开的 `AvaloniaBindingInfo` 已包含 Source、SourceType、Path、ConverterType、IsRunning、Priority、ErrorType 和缺口信息。

该公开记录没有有效 Mode、ConverterParameter、FallbackValue、TargetNullValue、UpdateSourceTrigger、原始异常或组合子表达式。为补齐这些已确认的缺口，Native 后端采用小型兼容边界，而没有为一个绑定读取器引入完整上游 Agent runtime／Server。诊断词汇及分类方法选择性改编自上述 Apache-2.0 模块，保留完整 [许可证](../src/AvaloniaDevTool.Agent/ThirdParty/XamlMcp-LICENSE.txt)、[来源和变更告知](../src/AvaloniaDevTool.Agent/ThirdParty/THIRD-PARTY-NOTICES.md)。没有复制商业反编译实现。

运行时形状依据 Avalonia 12.1.4 的公开开源代码核实：[BindingExpression](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Data/Core/BindingExpression.cs)、[UntypedBindingExpressionBase](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Data/Core/UntypedBindingExpressionBase.cs)、[MultiBindingExpression](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Data/Core/MultiBindingExpression.cs)、[TemplateBindingExpression](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Data/TemplateBindingExpression.cs)。

## 字段语义

每个字段使用 `BindingDetailValue`，区分以下五种状态。字段缺口同时保存在 `BindingDiagnosticGap(Field, Reason)`，界面直接显示原因。

| 状态 | 含义 |
| --- | --- |
| Known | 实际观测到的值；`(UnsetValue)` 是明确的框架哨兵值，区别于 null。 |
| Null | getter 或上游编码明确返回空值。 |
| Unknown | 上游没有值，也没有足以说明可用性的证据。 |
| Unavailable | 已确认该形状、公开 DTO 或兼容探测不支持此字段，附具体原因。 |
| NotApplicable | 已确认该表达式没有该选项，例如 TemplateBinding 没有 StringFormat、DynamicResource 没有 ConverterCulture。与未能读取配置分开。 |

结构字段包括 SourceType、Source、Path、Mode、Converter、ConverterParameter、ConverterCulture、ConverterCultureOrigin、FallbackValue、TargetNullValue、UpdateSourceTrigger、StringFormat、ElementName、RelativeSource、DataContext、DataContextOrigin、缓存的 CurrentValue 和可选的 ResourceKey。新增文化字段位于 DTO 末尾且可空；旧 JSON 缺失字段由界面显示为“当前后端未提供此字段”。

- Native 的 Source 优先取绑定起始源节点解析后的对象，普通显式绑定及空路径绑定读取其弱源；TemplateBinding 使用实际 TemplatedParent。SourceType 来自该对象的真实类型，绝不用叶节点的值类型冒充源类型。
- DataContext 及其当前值优先级独立展示；这不等同于声称某个绑定必然采用 DataContext。原始 ElementName／RelativeSource 声明无法从现有表达式合约可靠恢复时明确不可用。
- 有效 Mode 仅在匹配的 Avalonia 12.1 运行时、已验证表达式类型及字段形状下读取。TemplateBinding 的源属性、转换器和模式分别探测；MultiBinding 的成员及子表达式分别探测。形状变化会逐字段降级。
- CurrentValue 读取框架表达式缓存，不调用 UpdateTarget、UpdateSource 或业务属性 getter 来重新求值。
- `IsRunning` 与错误类别独立。Native 仅保留实际 getter；XamlMcp 仅把明确的 `active`／`inactive` 映射为 true／false，`path-error` 等错误状态保持 null。Mode 也不从错误状态或运行状态推断。
- 错误按现有证据分类为 missing-source、path-error、converter-error、validation-error 或 binding-error，保留框架仍持有的异常类型、消息及内部异常。MultiBinding 的子项失败保留在子树，不把健康父表达式改写成同样的错误。

## 转换文化、格式化与源更新时机

`ConverterCulture` 表示表达式中保留的配置值，不是诊断线程的文化快照。普通及编译绑定共用 `BindingExpression`，其公开 `ConverterCulture` getter 会将空配置替换为 `CultureInfo.CurrentCulture`，因此 Native 只在已验证的 12.1 形状中读取 `_uncommon._converterCulture`；`_uncommon` 明确为空也保留 Null。MultiBinding 和 TemplateBinding 分别读取自己的 `_converterCulture`，子绑定独立读取，不从父绑定继承或猜值。非空的标准 `CultureInfo` 使用 Name（InvariantCulture 明确标识）；自定义 CultureInfo 的可覆写 Name 和 ToString 不执行，字段降级 Unavailable，来源仍保留“Configured”的非空存储证据。

`ConverterCultureOrigin` 的“Configured”表示观察到非空存储，并不声称恢复了原始 XAML；“CurrentCulture fallback”表示观察到空存储，框架会在实际转换／格式化时解析 CurrentCulture，诊断不读取该回退值。关闭私有探测、未知形状和上游 DTO 缺字段均保持 Unavailable，不用当前文化、当前字符串或 Description 反推配置。资源表达式的文化字段为 NotApplicable。GUI 在父表达式和展开后的每个子表达式中都展示文化、来源和字段原因。

普通及编译绑定展示运行时规范化的 `StringFormat`（例如 `N2` 保留为 `{0:N2}`）。MultiBinding 会将 StringFormat 包装进公开的 [StringFormatMultiValueConverter](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Data/Converters/StringFormatMultiValueConverter.cs)，因此展示其实际 `Format`，转换器栏保留实际框架包装器类型。已验证的组合表达式没有包装器时显示 Null，并说明原始声明可能省略或被忽略；不声称能还原对非字符串目标被忽略的声明。TemplateBinding 没有 StringFormat 选项，明确不适用。依据公开 [MultiBinding](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Data/MultiBinding.cs) 和 [CompiledBinding](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Data/CompiledBinding.cs) 源码核实。

源更新触发器展示已解析的运行时值。Avalonia 12.1.4 的 [UpdateSourceTrigger](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Data/UpdateSourceTrigger.cs) 及普通／编译绑定实现均把 Default 解析为 PropertyChanged；真实 TextBox 测试确认输入改值立即写源。LostFocus 在目标仍获焦时保留源旧值，实际焦点转移后写源；Explicit 在失焦后仍保留旧值，直到显式调用公开 `BindingExpressionBase.UpdateSource()`。诊断读取本身不调用 UpdateSource／UpdateTarget，不增加业务属性 getter 或转换器调用次数。MultiBinding 和 TemplateBinding 没有可配置的 UpdateSourceTrigger，显示不适用；TemplateBinding 的 TwoWay 直接属性更新另有真实测试，不能据此声称支持上述触发器选项。

Source 和 DataContext 仅传输有界文本、类型和可选弱 ID；DTO、界面和读取器不保存业务对象。AgentSession 使用现有 `WeakObjectRegistry.GetId` 回调提供导航句柄，未创建 pin。Native 字段的“打开对象”进入对象页，读取该真实源对象的公共 CLR 成员，可继续进入嵌套值与循环。此操作保留控件树选择，不尝试把业务对象定位成控件；面包屑和“所选控件”返回树中原控件，“返回原绑定”回到样式绑定页并标识原属性卡片。缺少 ID 的字段按钮禁用，并解释不能从文本预览猜测标识。

导航命令捕获产生该卡片的 DTO 实例、所选控件 ID 和当前 SessionId；换选、样式刷新或断连后，旧卡片命令失效。对象请求沿用对象页的取消、请求代数、页面、所选控件及会话校验。切入对象页时直接来源路径先建立，然后只触发一次目的页读取；挂起的选择流程不会重置或重复加载来源路径。弱源已回收时保留来源和面包屑，清空旧行并显示 `object_expired`，不从上次成功结果补数据。协议没有 SourceVersion 或原子绑定观察版本：这些检查保证 Inspector 响应归属，不承诺跨请求的业务状态一致。

## 资源键导航与活动筛选

Native 仅在匹配 Avalonia 12.1 的真实 `Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExpression` 及其程序集时读取 `_resourceKey`；`_host` 是其实际查找宿主，Source 与 ResourceKey 分开呈现。字段形状依据公开 [DynamicResourceExpression 源码](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Markup/Avalonia.Markup.Xaml/Data/DynamicResourceExpression.cs) 核实。普通绑定、旧 DTO、关闭私有探测及上游未提供资源键时保持明确缺口，不解析 `Description`、当前值或 ToString 预览恢复键。

已知且未截断的真实字符串键打开资源页，设置字符串键过滤与“包含嵌套字典”，同时按该精确字符串检查应用、样式、所选控件及父控件的可枚举作用域。候选区保留 OwnerDictionaryId、ProviderPath、Scope 和 Theme；“所属字典”选择条目实际来源字典，合并字典中的条目不会被归到外层字典。重复键可以显示多个候选来源，界面明确不推断最终胜出者。查询最多检查 32 个顶层作用域、每个作用域 5 页、每页 50 项，并展示最多 200 个候选；预算、公开 API 无法枚举的提供者及主题／框架选择过程都可能留下缺口。

非字符串键仅在存在实际弱 ID 时进入对象页检查真实键对象，不用其预览查询字典；无弱 ID 明确不可导航。超过文本预算的字符串键标为不可用，防止前缀命中错误资源。XamlMcp DTO 没有真实 ResourceKey 或兼容 Native 弱句柄，因此保持不可用和禁用入口。

样式页提供“仅活动样式帧”“仅运行中的绑定”“仅明确错误绑定”，筛选派生展示集合，不修改原始 Frames／Bindings。运行状态未知不算运行，错误类别未知不算错误。setter 值相同仍只表示数值一致。Avalonia 的公开 `ValueFrameDiagnostic.Values` 只枚举 `entry.HasValue()` 的条目，因此当前合约不能分类或筛选未返回的无值／无效 setter，也不将空集合解释为这些条目不存在。

## 双后端与兼容边界

Native 的反射集中在 `BindingDetailsReader`。公共运行时成员逐项探测，私有成员额外检查程序集、12.1 版本及已知表达式类型。getter 或显示转换抛错只降级当前字段，其他字段继续返回。主 adapter 的 setter 比较抛错时也保留 null，其他样式及绑定继续展示。

Avalonia SDK 的引用程序集会使 PrivateApi 成员对 C# 不可见，即使 PE metadata 仍含这些名字。独立编译探针直接访问 Description／ErrorType／IsRunning／Priority 时分别得到预期 CS0122／CS1061；生产实现不直接引用这些成员。测试另外覆盖关闭私有探测、缺少运行时成员的形状以及抛错 getter。没有把引用程序集当成可执行运行时加载。

XamlMcp 保留上游 Path、Source／SourceType、Mode（若上游实际给出）、ConverterType、CurrentValue、Children、错误类别／消息及原始 gaps。上游缺少 converter 时保留 Null；上游明确报告不可用时保留对应 gap；其他缺失证据保持 Unknown。DataContext 上游只给类型和来源，因此显示类型证据并附摘要／对象句柄不可用的原因。上游 snapshot NodeRef 绝不转换为 Native 弱对象 ID。

XamlMcp 1.0.0-preview.3 的绑定 DTO 没有 ConverterCulture、文化来源、StringFormat 或源更新触发器；这些字段保留各自 Unavailable 和具体原因，不能把 Native 验收结果写成上游后端已支持。

## 响应边界

绑定树总计最多 128 表达式，每个组合节点最多 32 子项，最大递归深度 6。异常树最多 16 节点、5 层，单项文本最多 256 UTF-16 单元，整个绑定读取／映射使用 32768 UTF-16 文本预算；截断避免拆开 surrogate pair。样式 adapter 另外限制为 256 帧、合计 1024 setter、32 条独立校验错误。截断会设置 `Details.Truncated`／`StyleSnapshot.Truncated` 并保留缺口说明。

`AggregateException.Message` 会递归拼接全部内部异常，能够绕过结果树预算，因此只展示固定父说明及有界内部异常树。不会传输 StackTrace、Exception.Data 或活异常对象。

## 验证证据

`BindingDetailsTests` 覆盖健康 TwoWay／Explicit 绑定、继承 DataContext、缺源、坏路径、转换器异常链、真实校验、TemplateBinding、MultiBinding、关闭私有探测、引用形状缺员、抛错 getter／ToString、弱引用可回收、文本／异常／子节点截断及序列化大小。11 项通过，与原有 6 项 InspectionIntegrationTests 合计 17/17 通过。

`BindingDiagnosticsViewTests` 检查各字段状态、AXAML 表格、折叠子表达式、异常树和旧 DTO 降级；4/4 通过。`BindingDetailsMappingTests` 使用独立上游 DTO 覆盖字段和 gaps、不捏造原生对象句柄、错误状态、嵌套与恶意中文诊断的响应边界；完整实际包 stdio 集成也检查健康／坏路径的结构字段。

H03／H04 专项另增 `BindingCultureTests` 8 个方法、14 个执行用例，真实覆盖普通／CompiledBinding 的 fr-FR 与 en-US 正向转换、规范化 StringFormat 和 ConvertBack，MultiBinding 格式包装器与独立子文化，TemplateBinding TwoWay 转换，以及普通／编译 TextBox 的四种源更新时机。额外验证空文化、诊断不重新求值、自定义文化 getter 不执行、私有探测关闭、资源表达式不适用和旧 JSON 缺字段兼容。与现有 `BindingDetailsTests` 合计 27/27 通过；GUI 新增 1 个方法、5 个状态用例，包含实际 AXAML 展开后的子表文化及来源断言，9/9 通过；XamlMcp 映射 13/13 通过，文化缺口和已有响应预算均保留。专项构建使用独立 `--artifacts-path`，产物及 TRX 位于系统 Temp 的 `avdt-binding-culture`（`agent-final`、`ui-final`、`mapper-final` 与 `results`）。没有据此声称未运行的桌面视觉验收或非 12.1.4 版本兼容。

`BindingNavigationTests` 通过真实 Agent、Client、VM 和 AXAML 验证 Source／DataContext→公共成员→嵌套与祖先循环→原绑定／所选控件、失效弱源无旧数据、getter 重入导致换选／切页／断连和重连、DynamicResource 真实字符串键的三种作用域来源及合并字典 owner、非字符串键对象，以及 foreign 句柄和缺失事实禁用。派生过滤另检查原始证据集合不变。测试产物与 TRX 位于系统 Temp 的 `avdt-binding-navigation`。

本轮独立证据存放于系统 Temp：

- `avdt-binding-details-39ccca8b7f61475cbe2dda30676d4aae`：公开 API／引用程序集探针、Native 测试产物和 TRX。
- `avdt-binding-ui-9db588033afa468a9bb4439eeaa27277`：AXAML 构建及 UI 测试。
- `avdt-binding-mapping-051df17a30d14d2680fc767051133f0c`：映射及实际公开包后端测试。

桌面视觉验收由整体任务另行执行；这些 headless 和协议测试不替代桌面截图检查。当前兼容承诺限定于已经验证的 .NET 10／Avalonia 12.1.4 和 XamlMcp 1.0.0-preview.3。
