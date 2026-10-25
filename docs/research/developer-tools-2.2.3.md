# AvaloniaUI DeveloperTools 2.2.3 实现分析

> 历史研究：本文对应完整桌面产品基线 `main/fa1c623`，其中 GUI、外部后端、旧路线建议和阶段计数不作为当前分支交付范围或通过证据。保留它用于来源、许可和设计研究；当前官方 Agent/MCP 合同与原版证据边界见 [官方能力基准](../agent-tools/research/official-capabilities.md)，当前状态见 [实现状态](../implementation-status.md)。

2026 年 10 月 24 日使用 REA MCP 对已发布的 DeveloperTools 2.2.3 产物进行了静态分析，并使用 ILSpy 精读关键方法。主要结论是：应用内 `DiagnosticsSupport` 提供树、属性、视觉操作、资源、日志和性能服务，独立桌面工具调用这些服务并呈现结果。

这里记录原版产物中的观察，以及这些观察对本项目设计的影响。本项目使用自己的 DTO 和通信协议。

## 分析对象和覆盖范围

| 产物 | 身份与范围 |
| --- | --- |
| `AvaloniaUI.DeveloperTools.Runner.dll` | 2.2.3.0；AnyCPU CIL；面向 .NET 6；分析启动路径与标准流代理 |
| `AvaloniaUI.DeveloperTools.exe` | Windows x64 PE；55,258,952 字节；无普通 CLI 元数据 |
| `AvaloniaUI.DiagnosticsSupport.Avalonia.dll` | 官方 NuGet 2.2.3 的 `lib/net10.0` 变体；846 类型、7,224 方法；分析服务接口和精选实现 |

SHA-256：

```text
Runner:
cdd9f6016f858768448fb4c450a78c31252fc79d0b7ca76fc2b7a873c78e2d59

Desktop executable:
76a88bb2fd68d40a7b663a0f710c8bc18dc85bf9a2f732dc6f2f84c6ac0f9f08

DiagnosticsSupport:
6ce4138e84b9ed5fbd3aa446ef292e023e92ecd0a2ae18e261293c47ac27a6d2
```

Runner 的 MVID 为 `8fd3a9a4-cbff-4fe7-b5af-421f3455efb1`，DiagnosticsSupport 的 MVID 为 `098e181d-5238-45a7-a325-c9de5bdee015`。元数据 token 只在对应哈希与 MVID 的产物中有效。

REA 元数据和 CIL 调用关系属于静态观察；ILSpy 输出属于反编译重建。全量成员扫描达到 64 MiB 表示预算后未完成，随后对精选类型的扫描均完成。桌面主程序的 Ghidra 导入达到 330 秒启动期限，未取得可用于声明已还原界面的反编译结果。本轮没有连接真实目标应用验证运行行为。

## 启动器与外部工具

`avdt` 的托管 Runner 负责定位各平台主程序并转发参数。无参或 `open` 进入 GUI 启动路径，其他参数进入控制台启动路径。`mcp` 作为参数传给同一桌面主程序，并非由 Runner 自己实现 MCP 工具集。

Windows 控制台模式重定向 stdin、stdout 和 stderr；Runner 还负责子进程生命周期和退出清理。这部分适合保持独立，不应承担应用诊断业务。

## 应用内服务和通信

DiagnosticsSupport 包含处理器选择、对象注册、RPC、序列化和 UI 上下文调度。服务方法可返回 `Task`、`Stream` 和 `IObservable`，覆盖一次查询与持续更新。

默认 HTTP 模式由工具端监听 `127.0.0.1:29414`。应用发起 `GET /streaming`，接收 `application/octet-stream` 数据，再通过 POST 回传消息。连接携带 `Session-Id`、`Protocol-Version` 等信息。Android 模拟器的默认工具地址为 `10.0.2.2:29414`。

命名管道模式的监听方向不同：应用创建 `NamedPipeHost.StartListening`，工具连接该管道。此外还有按进程命名的独立发现管道，用于读取应用信息和请求应用连接工具。

应用诊断协议使用嵌入的 CoreRPC 和 System.Text.Json。静态证据不支持把它称为 WebSocket 或 JSON-RPC 2.0。公开配置与连接说明可参阅 [Options](https://docs.avaloniaui.net/tools/developer-tools/options) 和 [远程连接](https://docs.avaloniaui.net/tools/developer-tools/attaching-to-the-remote-tool)。

## 功能与实现

| 能力 | 静态观察 |
| --- | --- |
| 控件树 | `GetAvailableTrees`、`RequestNode`、`RequestChildNodes`；Visual、Logical、Merged 三种树 |
| 模板层级 | Merged 树表达模板根、模板父节点和内容插槽，不能简单拼接两棵树 |
| 树更新 | `ChildNodesChanged` 通知父节点失效；随后重新请求子节点 |
| 对象身份 | `ConditionalWeakTable<object, StrongBox<TKey>>` 正向映射，弱引用字典逆向查询 |
| 属性 | `GetDiagnostic` 读取优先级；绑定表达式提供诊断；`SetValue`／`ClearValue` 执行写入 |
| 样式 | `GetValueStoreDiagnostic().AppliedFrames` 提供样式、主题、本地与默认值帧 |
| 拾取和覆盖层 | 监听预处理输入和指针事件，在目标进程绘制高亮和尺子 |
| 输入 | 构造框架内指针、键盘、文本和滚轮事件，再向目标元素派发 |
| 截图和 3D 数据 | Composition 快照返回位图、`Matrix4x4`、尺寸和子节点 |
| 日志和事件 | 日志源订阅与路由事件类处理器 |
| 断点 | 属性／事件观察、命中条件和计数，可调用 `Debugger.Break()` |
| 性能 | `MeterListener`、`ActivityListener`；不能由此推断具备 EventPipe CPU 采样 |
| 资源和 XAML | 资源查询／写入、资产读取、XAML 解析和重载接口 |

树查询允许指定深度和是否停在模板边界。树失效通知只携带 `TreeId`、`ParentId`、`HasChildren`、`HasTemplate`，不是完整的插入／删除补丁。

属性变化以约 100 ms 窗口聚合。连接、树、事件、日志和性能订阅都有释放路径。对象 ID 不阻止对象 GC，查询已失效 ID 时会明确失败。

Composition 服务证明了应用侧能产生 3D 所需的数据，但桌面工具如何将这些数据组织为 3D 视图仍未还原。MCP 和预览器的桌面侧处理流程也属于未还原范围。

## 对自研设计的影响

本项目把首版划分为 Protocol、Client、Agent 和 Inspector，另提供可运行 Sample。设计首先保障对象生命周期、UI 线程访问、断连清理和属性编辑语义，再逐步增加界面面板。

首版使用本机命名管道和自己的长度前缀 JSON RPC。Visual 和 Logical 树分别展示，Merged 树及模板插槽留待后续。树更新采用失效后重拉；对象 ID 使用弱引用；属性修改需要可撤销并保护已有绑定。

后续版本若增加 MCP，应通过 Client 复用已有服务。新增截图、样式、日志和性能时，应扩充 Agent 服务与 DTO，而不是让界面直接访问目标进程对象。

官方包包含 net8／net10 以及各自的 Avalonia 11 变体，默认变体引用 Avalonia 12；MSBuild 根据目标 Avalonia 大版本选择 DLL。这说明版本适配需要明确验证。本项目第一版固定使用 Avalonia 12.1.4。

## REA 证据索引

以下 ID 对应当次分析的 canonical Evidence 记录。原始 bundle 保留在本机临时分析目录，未将约 39.5 MB 的 SDK bundle 或反编译输出纳入仓库。

| 主题 | Evidence ID |
| --- | --- |
| 主程序身份 | `ev_34cf4416641c99d1a99a39bffc43311cdd96d132f62dd48636bdc4a2f693da0f` |
| Runner 精选实现 | `ev_22bd2ecd7ec12ce1e4d161b44afd8c8cfc568a63128990c38c906f814389e05e` |
| SDK 产物身份 | `ev_5de17d57ad0f50588b738905a36b50b599c08a710be15194a1e86fe25e2d4ba7` |
| 服务入口 | `ev_c90b7a80ccf904219fd28c67ec6342ffe25ec0dd0c25af82a34a1f7928cbb57c` |
| 传输 | `ev_089b0b1dfdd7ec9728ac211fbbc41fcfcd44c22a92f117fd4e16298c2766401c` |
| 树和对象生命周期 | `ev_a696aa283e9c1162c52086b5b4376574759bb2433c0a30aa9fd75d037eb0ba22` |
| 属性、输入和 Composition | `ev_4cee3cddc6a77b22cdb65683b7ada8cc5595762cf025e0db14425fe2c58cc4db` |
| 接口和 DTO | `ev_25f3ac762e16506fa852ac744203a56fe0691cd7ef8a3fc24e09d3235a658536` |
| 日志、性能和断点 | `ev_2aadc8cdb23d01d699f40a533f710070b0cee8a1b18edd2bbb97366c39eee3a1` |
| 属性和事件订阅 | `ev_4ca267481b1be56b676d09bb037c9c897bcfd8e4e86673a9becfaa54b11e91bd` |
| 树通知和覆盖层 | `ev_1bc92eea4e8d2e6ddba5d4d87b6b6bb90a0799530304e2a7d49ca0f2f511eaee` |

本机原始分析索引：`C:/Users/10062/AppData/Local/Temp/avdt-research-20261024-01a16e48/analysis-index.json`。该路径是研究产物的位置，不是运行项目的依赖，临时目录被清理后不保证继续存在。

## 官方公开资料

- [DeveloperTools 安装与接入](https://docs.avaloniaui.net/tools/developer-tools/installation)
- [Elements](https://docs.avaloniaui.net/tools/developer-tools/elements-tool)
- [MCP](https://docs.avaloniaui.net/tools/developer-tools/mcp)
- [新版公开仓库](https://github.com/AvaloniaUI/AvaloniaUI.DeveloperTools)
- [旧版 Avalonia.Diagnostics 源码](https://github.com/AvaloniaUI/Avalonia.Diagnostics)

旧版进程内工具的公开源码可帮助理解 Avalonia 诊断 API；新版仓库主要提供说明与示例，不能将其当作新版桌面实现的完整源码。

