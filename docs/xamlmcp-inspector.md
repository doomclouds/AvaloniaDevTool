# Inspector 的 XamlMcp 只读后端

Inspector 保留现有 Native 后端，同时可以显式连接公开 NuGet XamlMcp Server／Agent。两种模式在启动时选择，连接期间不切换后端。XamlMcp 模式目前只映射共同读能力：控件树、只读属性、样式／绑定摘要、祖先路径和 PNG 截图。

这不是把高级功能名称映射到空实现，也不声明与官方 DeveloperTools 或所有 XamlMcp 功能等价。上游没有提供的 owner、setter 胜者或绑定字段保持 Unknown／null；界面不把未知值当作 false。

## 启动

默认模式保持现有用法：

~~~powershell
dotnet run --project src/AvaloniaDevTool.Inspector -- --backend native --pipe avalonia-devtool-demo
~~~

XamlMcp 模式要求用户明确给出目标和已经存在的 Server.dll／Server.exe 的绝对路径。运行时不会安装上游、修改用户环境或写入其他 MCP 客户端配置。DLL 使用 dotnet 启动；exe 直接执行，参数通过 ArgumentList 逐项传入。

可以先运行本项目的独立 NuGet Agent 示例，它不引用本项目 Agent：

~~~powershell
dotnet run --project samples/AvaloniaDevTool.XamlMcpTarget -- --enable-inspection
~~~

目标窗口和标准输出会显示 PID。另外一个终端构建 Inspector 后，明确选择随构建复制的公开 Server 载荷；本次固定版本为 1.0.0-preview.3：

~~~powershell
dotnet build src/AvaloniaDevTool.Inspector
$server = (Resolve-Path src/AvaloniaDevTool.Inspector/bin/Debug/net10.0/Upstream/XamlMcpServer/XamlMcp.Server.dll).Path
dotnet run --project src/AvaloniaDevTool.Inspector -- --backend xamlmcp --target 12345 --server $server
~~~

把 12345 替换成当前目标 PID。也可传入 pid:12345、instance:上游实例标识，或直接传实例标识；纯数字按 PID 处理。不能同时混用 --pipe 与 XamlMcp 的 --target／--server。未知参数、重复参数、相对或不存在的服务器路径会在启动前明确失败。

构建过程的普通 NuGet restore 负责项目引用和测试载荷；后端选项不会因为程序运行而自动安装 .NET tool。用户自行取得的匹配版本发布 Server 也可作为显式路径。

## 界面与行为

连接标签在 Native 模式显示“应用管道”，在外部模式显示“目标实例”。后端说明明确显示只读快照、人工刷新与上游限制。

| 能力 | XamlMcp 模式 |
| --- | --- |
| 根与直接子节点 | 通过 IInspectionBackend；上游有界快照由适配器缓存，界面展开缓存子节点 |
| Visual／Logical 切换 | 获取新的目标快照，清除旧选择 |
| 属性 | 显示真实值、来源、类型和错误；前端再强制 IsReadOnly=true、CanEdit/CanClear=false |
| 样式绑定 | 使用上游真实映射与 gaps；未知 setter 匹配值不填假证据 |
| 祖先路径 | 显式对象 ID 导航复用共同接口，不执行Native树搜索 |
| 截图 | “交互截图”页的 PNG 功能可用 |
| 编辑、拾取、高亮、跟随焦点 | 提前禁用，不等待点击后才报 Native 未连接 |
| 资源、资产、日志事件、指标、断点、3D | Native 专属页在外部模式禁用 |
| 伪类、Focus/BringIntoView/Enable/Disable、Native树搜索 | 提前禁用；不把上游不同 query 语义伪造成相同搜索 |

HasStableObjectIds=false 时，树刷新先清空旧选择、属性、样式和截图，再获取新快照，不按旧 long ID 恢复选择。HasPushUpdates=false 的外部后端不假装实时更新；目标变化后由用户手动刷新。

主动断开先清除外部快照显示，再等待上游 MCP 进程结束；不会因为服务器关闭较慢而继续展示可用的旧句柄。断开适配器不关闭目标应用。窗口关闭会释放当前后端和未使用的Native客户端。

## 架构与回归边界

MainWindowViewModel 接受可选 IInspectionBackend，默认仍使用原 DiagnosticsClient。共同读取走 _backend，Native高级服务保留原 _client。所有会话判断读取当前 _backend.Session。现有 desired-selection／CommitSelection 与断点快照 revision/merge 保护没有被回退。

Inspector 首次 Opened 会按当前屏幕 WorkingArea／RenderScaling 和窗口 frame inset 对初始尺寸设上限；保留既有最小尺寸，不持续覆盖用户后续 resize，无有效屏幕时跳过。

XamlBackendIntegrationTests 区分三类证据：

1. 受控 fake 验证共同API路由、只读强制、Native命令／页预禁用，以及不稳定快照刷新后的旧ID清理。
2. CLI解析验证默认Native、显式绝对Server路径、PID／实例转换、带空格路径和参数拒绝。
3. 真正 NuGet 后端到当前 ViewModel 的测试，启动独立 Skia Headless 目标进程；经发布Server的stdio与上游Agent认证管道，验证连接、树、属性、实际样式帧、PNG和断开。该测试不在同一进程放置目标，也不把fake测试或仅后端测试当成端到端。

本轮通过独立临时 artifacts 目录执行 Inspector 测试：35项全部通过、无跳过，包含原27项及新增8项（5项受控后端/界面能力验证、2项CLI解析、1项真实发布包到ViewModel的独立目标集成）。该数字仅指Inspector测试项目，不冒充整个解决方案的新总回归。构建为0警告、0错误。

真实目标只使用 XamlMcp.Avalonia NuGet Agent，测试显式使用 --headless、--enable-inspection 和独立 discovery 目录，不抢用户桌面焦点。标准流异步排出，测试结束只终止自行启动的目标并清理其临时目录。测试目标引用 ReferenceOutputAssembly=false，支持普通 bin/配置 和 --artifacts-path 输出布局；上游Server载荷沿Backend项目引用复制，未硬编码本机Temp路径。

本轮自动化不替代真实桌面尺寸、视觉布局和另一平台的操作验收。Backend与NuGet兼容细节见相应后端文档和开源复用审计。

