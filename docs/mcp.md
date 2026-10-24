# MCP 与命令行

本项目通过官方 C# MCP SDK 提供独立 stdio 服务器，复用现有 Client 和 Agent，不依赖原版 DeveloperTools 或其账户状态。SDK 固定为 ModelContextProtocol 2.3.0；标准输出只承载 MCP，运行日志输出到标准错误。

## 启动

先在另一个终端启动已接入 Agent 的目标应用。构建后启动服务器：

```powershell
dotnet run --project src/AvaloniaDevTool.Cli -- mcp
```

可指定首次调用时自动附加的管道：

```powershell
dotnet run --project src/AvaloniaDevTool.Cli -- mcp --pipe avalonia-devtool-demo
```

生产调用应直接运行已构建的程序集，避免 dotnet run 的构建输出干扰 stdio。客户端启动参数示例：

```json
{
  "command": "dotnet",
  "args": [
    "C:/WorkSpace/ResearchProjects/AvaloniaDevTool/src/AvaloniaDevTool.Cli/bin/Debug/net10.0/AvaloniaDevTool.Cli.dll",
    "mcp"
  ]
}
```

此示例只描述 MCP 进程启动方式，不会自动改动任何客户端配置。

## 会话与能力

1. 客户端按 MCP 标准执行 initialize 与 tools/list。
2. Windows 可以调用 apps 查找本工具的管道；也可从目标界面复制名称。
3. 调用 attach-to-app，参数 pipeName 为明确的目标管道。
4. 使用 tree、search、props 等工具取得对象和完整属性 ID。
5. 调用 detach 结束会话，释放诊断覆盖、资源修改、伪类修改、监听和覆盖层。

当前 Agent 的连接仍是单客户端。MCP 与 GUI 检查器需要分别连接；切换前先在另一端断开。同一 MCP 服务器可以通过 attach-to-app 切换应用，不会沿用旧会话对象 ID。跨客户端共享目标、远程 HTTP 以及移动平台尚未实现。

## 工具

| 工具 | 行为 |
| --- | --- |
| apps / attach-to-app / detach | 本机发现、显式附加、会话释放 |
| attach-to-remote | 使用 MCP 进程中的专用环境凭据附加 Native ws／wss 端点 |
| attach-to-file | 使用已构建应用／控件库和项目上下文创建受管理的预览进程，支持 x:Class、App 资源和文件监视 |
| tree / ancestors / search | 有界快照、根到对象父链、折叠子树搜索 |
| tree-children / template-parent-selector | 真实直接子项分页和游标；模板所属控件 selector 辅助查询，部件全路径仍由 element-selector 返回 |
| element-selector / element-metadata / tree-release | 生成并验证真实 Avalonia selector、复制对象元数据、释放展开分支观察 |
| props / set-prop | 属性值、来源、可编辑状态、临时覆盖与清除 |
| set-brush | 结构化 Solid／Linear／Radial／Conic 草稿，复制新画刷并临时覆盖；从 props 的 presentation.brush 读取字段及允许类型 |
| styles | 样式帧、setter、绑定表达式和验证状态 |
| action / follow-focus | Focus、BringIntoView、可撤销 Enable/Disable、焦点跟随 |
| overlay / overlay-set / overlay-clear | 根 DIP 盒模型、信息／标尺／引导线、配置与移除诊断高亮 |
| renderer-diagnostics / renderer-diagnostics-set / renderer-diagnostics-stop | 查询、启用和恢复真实框架 FPS／脏矩形／布局及渲染耗时图 |
| popup-freeze / popup-freeze-set / popup-freeze-resume | 按配置根抑制支持弹层的外部点击／失焦关闭并恢复；逐项返回不支持的自动关闭路径 |
| input | 真实框架文本、按键、指针和滚轮路由；不注入OS/IME |
| object / layout | 只读CLR对象和集合分页、VisualGeometry层级快照 |
| composition | 真实合成器逐节点自身绘制 PNG、变换／层级／裁剪／透明度及明确限制 |
| pseudo-class | 查询、切换、恢复伪类 |
| screenshot | 元素 PNG，返回原生 MCP ImageContentBlock |
| resources / set-resource / reset-resource | 查看资源层级、编辑已有字符串键、保留应用新值的冲突处理 |
| resource-tree / resource-query / resource-entry | 有界 provider 树、嵌套条目分页／筛选／排序、原 owner／主题来源和弱键 ID 重读 |
| assets / open-asset | 枚举内嵌资源、文本或图片预览 |
| export-asset | 完整分块导出到本机绝对路径，验证长度／SHA-256，默认不覆盖 |
| activity-start / activity-stop / activity | 日志和路由事件捕获、停止与有界历史 |
| events-catalog / events-start / events-routes / events-status | 实际注册事件目录、精确或分组选择、关联路由及预算／过期状态 |
| metrics-start / metrics-stop / metrics / metrics-clear | 真实时间窗的原始指标、帧聚合／P90、间隔配置与单序列或全部清空 |
| profiler-start / profiler-stop / profiler-clear / profiler | 真实框架样式匹配、激活器求值及资源查找，微秒耗时和有界窗口统计 |
| breakpoints / breakpoint-create / breakpoint-update / breakpoint-remove | 观察属性或事件、命中条件、启停、命中历史与释放 |

set-prop 的 propertyName 支持完整属性 ID；同名属性可能属于不同 owner，因此建议直接使用 props 返回的 Id。value 为 JSON null 表示清除本会话的诊断覆盖；字符串 `"null"` 仍按目标类型转换。当前不解析原版 isXamlValue 参数。

set-brush 接受结构化 brush：kind、opacity、stops、spreadMethod，以及对应类型的起终点／中心／焦点／半径／角度和矩阵。目标端校验类型、有限数值和色标预算，应用创建新对象，不修改应用原画刷或色标集合；set-prop 的 JSON null 清除或 detach 恢复最新底层值。Image／Drawing／shader／custom 仍只读，详见 [属性编辑](properties.md)。

tree 的 depth 为0只列当前节点，-1在上限内递归，最大64层；maxNodes 为1至2000，结果明确给出 Truncated。search 另有 MaxResults 和 MaxVisited。父链通过对象实际父节点取得，不需要把整棵树搬到客户端。

tree-children 的 count 默认100、最大2000，offset 与 cursor 用于读取直接孩子；结果显式给出 totalCount、hasMore、nextCursor。游标绑定会话／父节点／树模式／真实分支观察版本，变更后应从零重读，单分支超过20000项明确返回 tree_limit。tree 也可请求页参数；这些能力由真实 stdio 测试覆盖。Inspector 对仅缺少新方法的旧 Agent 可退回旧读取并本地分页，比较已加载前缀；该模式不具备原生 cursor／事件 revision 保证。详见 [树工作流](tree-workflows.md)。

props 的 contextual、includeClr 均默认 false，保留旧行为；前者只过滤已知直接视觉父布局消费的附加属性，后者才执行公共 CLR getter。未知自定义用途保留，已知上下文位于 presentation.context。Inspector 在本地隐藏完整元数据中的非相关项，保留草稿并跟随换父；详见 [上下文属性](contextual-properties.md)。

popup-freeze-set 的 frozen 和可选 rootId 控制保持范围，省略 rootId 表示所有配置根及后续新根；popup-freeze-resume 释放全部自有改写。普通 Popup／标准 Flyout 支持范围已实测，ContextMenu 的 Overlay 独立关闭、ToolTip 与 transient Flyout 仍有限制，不能按返回 IsActive 推断所有弹层完全冻结。详见 [弹层保持](popup-freeze.md)。

tree／ancestors／search 接受 Merged。tree 的 includeTemplates 默认 true，false 时展示模板组但停止在组边界。合成模板组与内容插槽带 Role、TemplateOwnerId／SlotContentId，不支持控件属性或输入，需导航到真实 owner／content；详见 [Merged 树](merged-tree.md)。

element-selector 使用实际解析与匹配验证，结果显式区分歧义、预算截断与不支持的名字；不会在截断后声称唯一。tree-release 只释放指定树分支的观察，保留对象身份和属性／断点监听，再次读取孩子会恢复观察。详见 [树工作流](tree-workflows.md)。

overlay 返回的 margin／padding、旋转后的区域与设备缩放都有明确坐标空间；截图暂时隐藏诊断覆盖层。renderer-diagnostics 的开关作用于真实 TopLevel.RendererDiagnostics，停止或断开时按冲突规则恢复应用值。详见 [高亮](overlays.md) 与 [渲染诊断](renderer-diagnostics.md)。

events-start 默认使用六组事件，可指定注册目录中的精确事件 ID；events-routes 把各观察节点、策略结束与源／接收对象关联到同一路由。Handled 变化表示观察区间，不能归因到具体实例 handler。activity-stop 停止监听并保留有界历史。详见 [事件路由](events.md)。

profiler-start 的 categories 为 StyleAttach／StyleEvaluate／ResourceLookup，可用 filter 筛选选择器或资源键。profiler 返回原始活动、窗口统计和丢弃数；资源缺失 Result 标签保持未知。停止保留窗口，清空正在运行的采集开启新代数；详见 [Profiler](profiler.md)。

attach-to-file 接受 filePath／assemblyPath／projectPath，可选 applicationAssemblyPath 分离启动应用与源控件库。它不执行目标 Main、不自动构建；detach 或 MCP 退出会结束本会话拥有的子进程，attach-to-app 的外部应用不会被终止。详见 [项目预览附加](preview-attach.md)。

composition 的图像是节点自身绘制，不包含孩子，也不是完整子树截图。4MiB RPC 帧、PNG／像素总预算和效果／mask／缓存限制均显式返回；layout 仍只描述几何。详见 [合成采集](composition-capture.md)。

resource-query 的 includeNested 保留重复键的来源，不计算最终胜出值；写入使用每条 OwnerDictionaryId，resource-entry 用 KeyObjectId 重读原条目。activity-start 增加 categoryFilter／messageFilter／parameterName／parameterValue；日志保留原始模板、参数状态、异常图、scope 和 EventId。完整日志与通知各有独立字节预算和丢弃统计，见 [资源](resources.md) 与 [结构化日志](logging.md)。

screenshot 仅捕获已附加且尺寸有效的控件，96 DPI，最大边2048、最多2M像素、PNG最多1 MiB。超限明确失败，不自动缩小后冒充原始截图。open-asset 只接受目标应用的 avares URI，预览正文最多1 MiB；SVG显示源文本。

断点创建的 sourceObjectId 只约束实际事件源，nodeId 仍约束路由接收节点；二者可以独立或同时设置，旧 nodeId 定义保持兼容。Suspend 默认关闭，且需要目标应用已附加调试器；无调试器时服务返回明确错误，不启动即时调试对话框。

metrics-start 支持 pollIntervalMs、notificationIntervalMs、historyWindowMs、frameIntervalMs、aggregateFrames，默认分别为 1000、200、60000、250、true。指标统计只覆盖真实时间窗内仍受容量限制的原始样本；绘图聚合时 Histogram 取帧均值，Counter／Gauge 取帧末原始值，不把 Counter 原始增量标为累计。metrics-clear 可传 seriesId 清单图，省略则清全部，继续采集且旧快照不能复活已清数据。详情分别见 [断点](breakpoints.md) 与 [指标](metrics.md)。

## 独立协议与验证

本项目按功能目标独立实现，当前不与原版私有协议或所有参数直接兼容。框架输入、项目预览附加、Merged 模板树以及高亮／selector／渲染诊断／关联事件均有真实 stdio 链测试；完整 IDE 进程附加仍未实现。原版同一产物的实际15项初始工具声明见 [原版研究](research/native-deep-analysis.md)。

CLI.Tests 使用真正的 SDK stdio 客户端启动服务器进程，执行 initialize、tools/list、tools/call，连接同一测试进程中的真实 Agent，验证搜索、属性覆盖、绑定恢复、样式、PNG图像以及断点命中。测试不以直接调用工具方法代替 MCP 协议交互。

参考：[官方 C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)，[stdio 与工具 API](https://github.com/modelcontextprotocol/csharp-sdk/tree/main/docs/concepts)。

