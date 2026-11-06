# 样式与资源 Profiler

> 范围说明：本文保留 Agent/Client 扩展服务的协议、预算、恢复和历史验证资料。文中 Inspector 界面、操作和旧阶段结果仅适用于完整桌面基线 `main/fa1c623`，不代表当前分支 GUI 或统一回归；当前默认 official 的 16 项合同以 [官方能力基准](agent-tools/research/official-capabilities.md) 和对应功能文档为准。扩展入口须显式使用 `--profile extended`，并以该目录的 `tools/list` 确认参数。

Native Agent 通过 `System.Diagnostics.ActivityListener` 采集目标进程实际发出的 Avalonia 活动。它与日志／路由事件、`.NET Meter` 是三个独立服务。Inspector 第 11 页“性能”和 MCP 使用同一组类型化 RPC；XamlMcp 只读后端未提供这些数据，对应页面及命令禁用。

## 来源与结果

固定验证框架为 Avalonia **12.1.4**，源名为 `Avalonia.Diagnostic.Source`。

| 类别 | 框架 operation | 标签与含义 |
| --- | --- | --- |
| StyleAttach | Avalonia.AttachingStyle | Style、SelectorResult。NeverThisType 是类型快速拒绝，NeverThisInstance 是实例不匹配，AlwaysThisInstance／AlwaysThisType 是静态匹配。Sometimes 是需要激活器的候选匹配，不证明此刻活动。 |
| StyleEvaluate | Avalonia.EvaluatingStyle | Selector、Style、Activator、IsActive。记录实际求值及最终活动状态，不仅是状态变化次数。 |
| ResourceLookup | Avalonia.FindingResource | Key、ThemeVariant、Result。成功写 Result=true；普通未找到路径没有 Result 标签，保持未知／未确认，不能与异常中止强行归为同一种失败。 |

每条样本保存序号、分组、时间、operation、耗时及有界标签。耗时单位统一为 **微秒**。分组统计次数、total／min／max／mean 和结果计数，`WindowScope=retained_samples` 明确只覆盖保留样本；历史淘汰后按保留窗口重算。资源同键的 Light／Dark／未指定主题分别分组。

筛选按选择器或资源键包含文本匹配，不区分大小写，在开始时生效。对已验证的 TypeNameAndClassSelector 链还原有限的类型／名字／类文本；复杂或自定义选择器显示类型和进程内身份摘要。不会调用任意业务 `ToString()`。复杂选择器完整语法仍是缺口，不能宣称与原版所有选择器视图等价。

## 初始化与生命周期

Avalonia 的 `Diagnostic.IsEnabled` 在静态构造时读取 AppContext 开关并缓存，晚改开关不能重新执行构造。`Avalonia12ActivityAdapter` 隔离私有 API：先触发正常静态初始化；共享 `s_activitySource` 已存在则复用，仅为空时调用 `InitActivitySource()`。适配器验证类型／字段／方法形状，失败返回 `adapter_unavailable`；不修改静态只读开关，不额外启动全局 Meter。

共享源的生命周期属于目标进程。停止、断连和释放只撤销本会话 listener 与批次计时器，不替换或释放共享源。晚启动已由真实框架测试证明；其他 Avalonia 小版本、裁剪和 Native AOT 未验证。

采样回调可在后台执行，锁内只处理有界集合和字符串／数值 DTO，不持有 Activity、Style、控件或业务资源键。W3C 和 hierarchical 活动身份分别处理。generation 阻止旧 listener 在 Dispose、停止、清空或重启后提交；正在采集时清空会创建新采集代数，旧进行中活动不得补入新窗口。停止报告未完成数及未发送通知数。

开始／停止／清空由 UI 线程协调；通知由 UI 线程计时器按批发送。Inspector 通过会话 ID、请求代数与取消令牌提交快照，通知只触发当前采集刷新，避免旧状态覆盖新状态。

## 预算

默认历史 512、通知队列 256、分组 128、进行中 256、标签 8、批次间隔 200 ms。允许历史 1–1024、通知队列 1–2048、分组 1–128、进行中 1–2048、标签 0–8、间隔 50–5000 ms、筛选最多 128 字符。格式器内部上限 256 字符，保存的描述列最多 96 字符；raw 标签键最多 32、值最多 96、单样本标签文本总预算 192 字符，最多检查 64 个标签。最坏 Unicode 快照有序列化帧大小测试。

状态分别报告历史淘汰、通知丢弃、分组／并发上限丢弃、未完成、标签截断及回调／通知错误。到达分组上限不再分配新组，清空或重启才能重建。字符串按完整 UTF-16 内容 SHA-256 作固定长度身份，描述截断不合并同前缀长键；资源键和主题身份以结构键隔离，避免分隔符碰撞。对象键／Style 使用类型及会话身份摘要，有摘要碰撞的极小可能，不持有原对象。监听带来采样开销，所得耗时是启用诊断后的观测。

## 接口与示例

- RPC：profiler.start／stop／clear／status／snapshot，通知 profiler.samples-added。
- Client：StartProfilerAsync／StopProfilerAsync／ClearProfilerAsync／GetProfilerStatusAsync／GetProfilerSnapshotAsync。
- MCP：profiler-start／profiler-stop／profiler-clear／profiler；categories 为 StyleAttach／StyleEvaluate／ResourceLookup，缺省全采，空集及非法类别拒绝。
- Sample：开始采集后点击“生成样式与资源活动”。按钮真实创建和移除控件、切换样式类并查找已有／缺失资源，没有自造源或手填耗时。

## 证据

使用公开 .NET API；生命周期参考 [.NET 官方说明](https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-collection-walkthroughs)，其明确指出 Dispose 返回后仍可能有已派发回调。Avalonia 私有边界按当前 NuGet 程序集精确核查，未复用商业工具反编译源码。

REA inspect_managed_members 精确选择 Diagnostic、Style、StyleInstance、ResourceNodeExtensions，4 类型／48 方法，无未匹配项，选中范围 coverage complete。

- 产物：Avalonia/12.1.4/lib/net8.0/Avalonia.Base.dll。
- SHA-256：`ffaf5263f964ea313ed29361c0dc86ed20fa7fda1473aefa687fde3a90622f8d`。
- MVID：`6b8c048b-6062-4c98-bbb3-ddb072211ea2`。
- 父证据：`ev_42bfa0ea594d92d0252c3a0fdc20cd9cd621ce406582eb9527badb5b4c62d8ee`。
- 本机 canonical 导出：`C:\Users\10062\AppData\Local\Temp\avdt-profiler-rea-20261025.json`，2 records／3,699,352 bytes；临时文件不随仓库分发。

静态证据仅证明该产物的类型／CIL／调用和标签线索。运行证明来自 ProfilerIntegrationTests 的真实框架活动及 typed Client＋管道、ProfilerViewTests 的真实 Agent＋VM 生命周期／外部禁用，以及 McpIntegrationTests 的实际 stdio 资源查找采集。统一总数见 [测试说明](testing.md)，桌面观察单列，不能替代未执行的平台验收。
