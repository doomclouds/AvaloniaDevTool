# 能力域重构与迁移记录

本轮将全解决方案重新划分为Contracts、Domain、Application、Infrastructure、SDK、Hosts、Extensions、Packaging。目标是能力状态和实现真正由独立对象拥有；不再以大量partial共享私有字段。运行行为和验证见[架构](../architecture.md)及[测试说明](../testing.md)。

## 十个能力域

| 能力域 | 主要实现/所有者 |
| --- | --- |
| 目标与会话 | ToolSession组合、ApplicationDiscovery、ManagedToolContext、各执行会话 |
| 结构与对象检查 | TreeInspectionService、RuntimeInspectionService；TreeClient/RuntimeClient |
| 属性与样式 | PropertyInspectionService、StyleInspectionService；PropertiesClient/StylesClient |
| 资源与资产 | ResourceInspectionService、AssetInspectionService、AssetExportService；ResourcesClient/AssetsClient |
| 框架交互 | InputInspectionService、ControlInteractionService、ElementActionService；InputClient |
| 原生自动化 | NativeTargetRuntime、NativeQueryService、NativeActionService、NativeInputService |
| 文件选择对话 | Domain中的NativeFileDialogController，依赖INativeAutomationTarget |
| 图像与视觉诊断 | ScreenshotInspectionService、NativeScreenshotService、Layout/Composition/Overlay/Renderer/Popup服务 |
| 运行诊断 | Activity/Metrics/Profiler/Breakpoint服务，Application的CaptureCoordinator及Domain DiagnosticMutationTracker |
| XAML预览 | ProjectPreviewService、OwnedPreview、PreviewSession及文档加载/监视 |

资源与资产仍分别建模；managed/native图像来源保留各自坐标合同。只读查询不强制制造没有规则的领域包装类；需要Avalonia/UIA对象的算法与句柄仍在基础设施。

## 大聚合类型迁移

| 原类型 | 迁移结果 |
| --- | --- |
| AgentSession及34个partial文件 | 普通组合根，树/属性/样式/选择/截图/动作独立服务；根、原始观察、RPC、通知独立 |
| DiagnosticsClient及32个partial文件 | DiagnosticsConnection/RpcContext和17个能力客户端；平铺API删除，调用方直接使用能力对象，强类型事件归各能力 |
| OfficialTools/AgentTools/InspectorTools | 聚合门面及历史profile删除；实际注册唯一的独立能力工具类，混合输入/截图/布局已归主能力 |
| ToolSession六个partial | 普通组合入口，发现、native、采集、项目预览、自建预览进程各自拥有状态 |
| NativeAutomationBackend四个partial | 无UIA状态的分发入口、NativeBackendFactory、共享目标运行时、查询/动作/输入/截图服务；batch算法与interop类型独立，测试不再借兼容转发 |
| InputInspectionService/ActivityService等 | 去掉手写partial，按已有职责分成普通服务 |
| Methods四个partial常量容器 | 合并为一份普通RPC方法常量类 |

源码手写生产partial已移除，ProtocolJsonContext保留源生成所需partial。样例XAML和测试组织所需partial不属于本次生产能力聚合。

## 真正的项目边界

Protocol的DTO和native/file-dialog/worker记录迁Contracts，RPC transport/serialization/trace迁Infrastructure.Rpc，后者输出真实AvaloniaDevTool.Infrastructure.Rpc程序集。

FileDialogController迁Domain，并通过INativeAutomationTarget调用native端口，不依赖Process/UIA。诊断mutation tracker迁Domain；Start/Stop/Clear仍经过同一tracker。CaptureCoordinator迁Application，通过IDiagnosticCaptureClient操作当前诊断连接，MCP边界映射中立异常并保留既有错误文本。

Avalonia实现迁Infrastructure.Avalonia，Agent SDK仅保留公开接入。未发版的Client/NativeAutomation兼容程序集和全部类型转发已删除，消费者直接引用Infrastructure.Execution及Domain/Contracts。Windows实现迁Infrastructure.Windows，NativeWorker Host只负责进程入口。控制台Program和MCP适配分为Host与Infrastructure.Mcp。

只保留当前Agent SDK真实接入入口，Client调用已按接收者实际类型迁移到所属能力；不维护未发布的旧接口。ProjectReference、测试链接源、构建元数据和解决方案路径同步迁移。

## 网关、打包与安装

统一执行目录88项，网关额外增加select-machine，共89项。机器选择只改变后端，不改变用户工具schema；远端Console只提供执行目录。后端scope通过内部执行元数据核验，句柄、原生pending状态和图片元数据由专门映射器处理。

Packaging是独立工程，不靠向控制台csproj添加单文件开关冒充完整分发。它嵌入独立自包含发布的Console、Previewer、worker及依赖，真实交付文件只保留AvaloniaDevTool.exe。

安装脚本默认使用用户.local/share/avalonia-devtool/versions及.local/bin，保留旧版本，稳定命令成功替换后才更新用户环境。tools、build、文档和根配置作为Solution Items可见。

## 验证要求

最终统一构建与回归在迁移结束后执行；迁移前的287项Agent专项、81项native专项和13项包装专项不能代替最终结果。新网关需验证stdio→HTTP真实PNG、模式切换旧句柄拒绝、未知业务JSON不被误改和无自动动作重发。独立EXE发布后还需验证bundle完整性、无PATH dotnet启动和实际执行链。
