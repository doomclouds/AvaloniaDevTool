# 扩展能力与工具

新增能力先确定十个能力域中的主要归属，再选实际执行位置。不要新增AgentSession/DiagnosticsClient/Tools partial，也不要让领域对象反向持有整个ToolSession或AgentSession。

## 复用现有能力

只组合现有结果时，复用能力客户端和应用编排，不新增目标RPC。例如跨Metrics/Profiler/Activity采集由Application的CaptureCoordinator负责，端口为Contracts中的IDiagnosticCaptureClient，所有权规则由Domain tracker维护。

## 新增目标内能力

1. 在Contracts对应能力目录增加中立请求/结果和方法声明。保持现有wire字符串语义；基本Methods常量集中在Session/Methods.cs。
2. 给Infrastructure.Rpc/Serialization/ProtocolJsonContext添加源生成元数据。记录及数组、List、IReadOnlyList的AOT消费由Protocol.Tests核验，不只检查JIT反射路径。
3. 在Infrastructure.Avalonia的对应能力服务中实现框架访问与状态；需要已有独立规则才提入Domain。目标UI线程、订阅和恢复职责保持明确。
4. 在该能力的RpcRoutes中登记方法；AgentSession只负责组装和调用检查，不再放新算法或private状态。
5. 在Infrastructure.Execution/Client/Capabilities对应能力客户端增加类型化调用与所属通知，只增加所属能力客户端API，不恢复已删除的平铺DiagnosticsClient API。
6. 在Infrastructure.Mcp/Tools对应能力目录增加独立工具方法，并由共享Catalog注册。输入及输出转换仅停留MCP适配层。只有一套当前合同，不增加profile或历史schema分支。

## 网关与HTTP一致性

外部网关和执行端复用一个目录来源；select-machine只在网关。新工具若返回后续调用会使用的身份，必须在GatewayHandleRegistry/GatewayResultHandles明确参数和结果的映射位置及作用域。

禁止递归猜测名为id/nodeId的任意业务字段。采集原始数据、标签、自定义JSON等应保持原样；图片payload直接保留，身份元数据按明确合同处理。新增异步动作还需在NativeOperationTracker登记其已知结果位置，避免未知用户JSON造成永久busy。

网关与执行端分别协商MCP，不转发外层协议_meta。远端初始化要验证执行端身份及目录兼容；不自动重试可能产生副作用的操作。

## native、文件弹窗与截图

Windows代码留Infrastructure.Windows。共享NativeTargetRuntime提供节点/窗口身份与UIA资源；Query/Action/Input/Screenshot各自独立，Backend只分发。文件弹窗状态在Domain，通过INativeAutomationTarget复用同一个worker；不创建第二套native会话。

managed截图与native截图归图像能力，保留各自DIP/物理像素合同及限制。MCP输出应包括image和相应元数据，不以路径代替像素，不引入桌面截图回退或业务视觉判定。

## 验证

针对新增规则补回归，先运行所属测试，再核对真实stdio网关和回环HTTP。切换、旧句柄、模态并发、图片像素和错误路径应使用对应证据层，不用单纯build或几何单测代替实际操作。

项目位置和直接依赖见[项目清单](project-inventory.md)，完整分发见[打包安装](../packaging-and-installation.md)。
