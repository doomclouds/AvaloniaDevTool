# Native AOT 消费程序验证

C13 当前为 **P：最小消费程序的真实 Native AOT 核心已验证，整个 Agent 的无警告 AOT 兼容尚未完成**。这不是只做 trimmed 发布或在 JIT 进程里关闭动态代码标记得到的结论。

## 验证对象与工具链

`samples/AvaloniaDevTool.AotSample` 是独立的最小 Avalonia 12.1.4 / net10.0 消费程序，直接引用 Agent，并在 Release 中显式附加。它使用 C# 创建控件和样式，没有运行时 XAML 或反射数据绑定。此样例专供诊断验证，不能作为生产产品默认启用 Release 诊断的建议。

2026-10-25 本机实际检查：Windows x64、.NET SDK 10.0.400、.NET runtime 10.0.11、Visual Studio Community 2026 18.9.2、VC++ x64 工具组件、MSVC 14.51.36231 和 Windows SDK 10.0.26100.0。没有安装工具链、修改全局配置或增加外部服务。

微软要求 Windows Native AOT 使用 Visual Studio 2022 或更高版本及 Desktop development with C++ 工作负载；Native AOT 在发布时将 IL 编译为本机代码。Avalonia 官方说明主程序使用 `PublishAot`，库使用 `IsAotCompatible`，并要求避免运行时 XAML 和反射式服务定位。参见 [Microsoft Native AOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) 和 [Avalonia Native AOT](https://docs.avaloniaui.net/docs/deployment/native-aot)。

## 本轮修复

- Protocol 增加显式 `System.Text.Json` 源生成 metadata，覆盖公开 record 及其数组、`List<T>`、`IReadOnlyList<T>`，并注册 RPC envelope/error；JSON 保持 Web/camelCase 和现有 string enum 合同。
- RpcPeer、Agent 的 JSON 字节估算和帧序列化使用 `JsonTypeInfo`，避免 Native AOT 中依赖默认反射序列化器。
- JIT 中仍允许现有 RpcPeer 自定义 payload；只在动态代码和 System.Text.Json 反射 feature switch 同时启用时追加反射 resolver。Native AOT 使用源生成 resolver；未注册的自定义类型不会暗中回退到反射。
- Protocol 声明 `IsAotCompatible`。其兼容分析单独构建为零警告、零错误。新增测试检查所有公开协议 record 的 metadata，后续增加 DTO 而漏注册会失败。

## 可复现的真实进程检查

在仓库根目录使用 PowerShell 运行：

```powershell
# Strict mode: retains the repository's warnings-as-errors policy.
./tests/scripts/Test-NativeAot.ps1 -Mode All

# Evidence mode: keeps every compatibility warning in publish.log and runs the real binaries.
./tests/scripts/Test-NativeAot.ps1 -Mode All -AllowCompatibilityWarnings
```

脚本默认将构建、发布、日志和截图全部写入系统临时目录，并打印实际路径。可以通过 `-OutputDirectory PATH` 指定证据目录。`-Mode Trimmed` 与 `-Mode NativeAot` 可单独运行。脚本不安装任何工具链。

严格模式当前预期会因 Agent 的 IL 分析错误而失败。`-AllowCompatibilityWarnings` 仅在这一次发布命令中设置 `TreatWarningsAsErrors=false` 和 `ILLinkTreatWarningsAsErrors=false`；没有设置 `NoWarn`、关闭分析器或删除诊断功能。它用于收集真实运行证据，不能被解释为通过严格兼容检查。

`tests/AvaloniaDevTool.AotSmoke` 是控制台验证程序，使用真实 `DiagnosticsClient` 连接发布后的消费程序，而不是 AgentSession 单元测试。其自身默认关闭 System.Text.Json 反射。它启动目标、校验握手 PID，运行断言后结束自己启动的目标进程，并保留 `target-ready.txt`、`smoke-result.txt`、stdout/stderr 和 PNG。

| 检查 | Trimmed/JIT 消费进程 | Native AOT 消费进程 |
| --- | --- | --- |
| `IsDynamicCodeSupported` | `True` | `False` |
| `IsDynamicCodeCompiled` | `True` | `False` |
| 真实 Named Pipe Client 握手与 PID 校验 | 已验证 | 已验证 |
| Visual / Logical / Merged 树与实际控件枚举 | 已验证 | 已验证 |
| 有界子节点首次分页及 continuation metadata | 已验证 | 已验证 |
| `Contextual=true` 属性请求及过滤 | 已验证 | 已验证 |
| Popup Freeze 只读根状态 | 已验证 | 已验证 |
| Text 属性读取、诊断覆写、通知、清除后恢复 Style 值和优先级 | 已验证 | 已验证 |
| 有效 PNG 截图 | 已验证 | 已验证 |
| 拾取开启、路由 pointer 选择、自动停止及通知 | 已验证 | 已验证 |
| 活动覆写断连清理与重连后的原值恢复 | 已验证 | 已验证 |

拾取测试使用本项目输入 API 发送 Avalonia 路由 pointer，不把它描述成真实鼠标硬件输入。截图验证 PNG 签名、宽高及字节，并保存供人工查看。它不证明任意控件、任意应用程序集或所有平台均兼容。

### 2026-10-25 最终证据

最终冻结的 Agent/Protocol 源发布后，两种实际消费进程均通过完整 smoke：Visual / Logical / Merged 分别 32 / 7 / 41 节点；首次分页 1 个结果、3 个总数及 continuation；属性 160 个原始条目过滤为 155 个上下文条目；截图为 520×280、8161 字节，并已人工查看正常控件内容。Popup Freeze 只验证无活动弹窗时的只读根状态，不据此声称 AOT 下实际弹窗冻结的全部路径已验收。

这次最终发布包含选择器针对真实目标的匹配验证：`StyleKeyOverride` 可让旧逻辑统计到唯一的其他对象，因此 `ElementSelectorBuilder` 现在先验证 `Match(target)`。该内部选择器路径未纳入 Native AOT smoke，仍按下面的反射兼容边界处理；本次重发保证证据对应修复后的最终生产源。

| 结果 | Native AOT | Trimmed / JIT |
| --- | --- | --- |
| 最终 smoke PID | 29276 | 36768 |
| 动态代码支持 / 已编译 | False / False | True / True |
| 编译阶段 IL warnings | 46 | 45 |
| 发布分析阶段 IL warnings | 41 | 40 |
| 合计警告输出条数 | 87 | 85 |
| EXE 字节数 | 31,354,880 | 162,304（apphost，旁边有托管程序集和运行时） |
| EXE SHA-256 | `7A69DB3067EE8E90818192E49F091D5EA1B937C60F997E2869FCC95B4C6661C9` | `9EE4248CFEC64729A238A7C67227CC4125F376015E45BC8D64D14C183807206A` |
| 发布后 Agent DLL SHA-256 | 原生代码位于 EXE | `565E3D450A1040309A6D68E018C11B9D77712C42163CC9E5CE9FF33979DE7D46` |

以上计数按日志中编译器和链接/本机编译阶段的警告输出行分别计算，同一源位置可能在两个阶段各报告一次，不表示相同数量的独立缺陷。两次发布均明确采用 `-AllowCompatibilityWarnings` 证据模式。Protocol 专项最终为 26/26 通过，兼容分析构建零警告、零错误。

本机证据保存在系统临时目录下（目录均包含 `publish.log` 与 `smoke/`）：

- Native AOT：`%TEMP%/avdt-c13-final-selector-20261025/NativeAot/`。
- Trimmed 新鲜发布：`%TEMP%/avdt-c13-final-selector-20261025/Trimmed/`。
- 严格模式失败：`%TEMP%/avdt-c13-final-selector-20261025/strict/NativeAot/publish.log`，实际 exit 1，编译阶段 46 条 IL error。

同目录的 `source-manifest.txt` 保存 77 个 Agent/Protocol 生产源文件的 SHA-256；`ElementSelectorBuilder.cs` 的最终源哈希为 `64DB76FF56E7366A29DB18FEE6B3C00AD624F510BD4B15A6E6450E8E60786B31`。每种发布目录另有 `publish-summary.txt`。Trimmed 的 EXE 是 apphost，其哈希不随 Agent 方法体改变，因此同时保留发布后 Agent DLL 的哈希。全部由验证程序启动的消费进程均已退出。

验证程序会等待 readiness 文件写入完整内容，读取正被目标写入的文件时重试，避免把启动时的短暂文件共享冲突误判为诊断连接失败。

## 尚未完成的兼容边界

默认 `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishAot=true` 已实际执行并以 exit 1 失败。阻塞来自 Agent 现有的开放反射诊断，而不是缺少 SDK/VC++ 或只支持 JIT：

- `PropertyPresentationAdapter` 使用运行时属性/字段枚举与方法 IL 读取来识别 CLR wrapper；`GetMethodBody` 和 metadata token 在修剪或 Native AOT 下没有通用保证。
- `RuntimeInspectionService`、`BindingDetailsReader`、日志快照及内部诊断适配器反射任意运行时对象。应用中被剪除的 CLR 成员无法由工具重新找回，需要进一步明确调用边界和消费端 metadata 保留策略。
- `Avalonia12CompositionAdapter` 包含反射构造内部类型和 `MakeGenericMethod`，仍有 IL2060/IL207x/IL3050；本轮未声明 composition 全量捕获在 AOT 下可用。
- 选择器运行时解析和部分 Avalonia 值验证仍会产生 IL2026。基础树枚举和本样例的字符串属性路径通过，不代表所有动态类型转换均已覆盖。
- 其他 profiler、绑定深入检查、运行时对象浏览、资源内部状态、平台差异等功能没有通过此 Native AOT 消费样例完成逐项验收。

应继续按实际警告修复精确边界，再扩展独立消费样例。不能通过全局压制警告、把核心通过升级为全功能兼容、把 trimmed 结果代替 Native AOT，或永久保留整个应用程序集来声称完成。
