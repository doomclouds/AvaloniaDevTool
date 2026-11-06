# 官方工具与自研 Agent 的差异

核对日期：2026-10-25。本页比较已归档的官方 MCP 合同与自研工具范围，不承诺官方授权运行等价。当前产品边界以 [当前 Agent 范围](../architecture/current-agent-scope.md) 为准：提供通用操作与可观察数据，业务关联和判断由调用方负责。

## 功能范围

| 范围 | 官方资料/归档基准 | 自研当前状态 |
| --- | --- | --- |
| 连接、树、属性/样式、资源/资产、截图、XAML 预览 | 公开基准 16 项；2.2.3 初始归档目录实际 15 项，动态目录与部分运行语义未核实 | canonical 16 独立实现；official profile 保留该兼容入口，未承诺原版返回/错误/默认值逐字段等价。 |
| 控件操作 | canonical 包含 Click/KeyDown/KeyUp/Text 及 Focus/Enable/Disable/BringIntoView | 默认 agent 另有选区替换、组合键、多击/右键、选择、provider 动作、滚轮、指针拖动、条件等待及控件/区域 PNG。 |
| Windows 原生界面 | 归档 MCP 目录没有原生 UIA/OS 输入专用工具 | native 窗口/树/查找/属性、pattern 动作、OS 输入及限定窗口/节点区域 PNG；按附加进程身份和 owner 限定作用域。 |
| 系统文件对话框 | 归档目录没有专用选择/保存/覆盖确认工具；attach-to-file 是 XAML 预览 | 真实单/多文件、目录、另存为、过滤器、覆盖 Yes/No、取消、等待与 stale 拒绝；使用实际 StorageProvider 和 Windows 公共对话框。 |
| 原始诊断 | 完整 DevTools 桌面诊断能力不能等同于 MCP 公开接口 | 默认 capture 复用当前连接，输出原始 metrics、样式/资源 Profiler、logs/events 及损失/错误；extended 另保留旧诊断表面。产品不据此判断业务或性能通过。 |
| 使用入口 | 官方桌面产品与 MCP 具有各自范围 | 默认 CLI/MCP 44 项；原独立 Inspector 已退出分支，旧 GUI 基线保存在 main/fa1c623。 |
| 平台 | 官方产品平台资料作为研究背景 | 本项目 native 范围限 Windows；用户不做 macOS/Linux 原生后端，交叉发布结果不扩大运行承诺。 |

默认 agent 44 项为 canonical16 + ui9 + native8 + file-dialog8 + capture3；official16 和 extended62 是另外两种显式 profile，不能相加为不同功能。工具参数见 [MCP](../../mcp.md) 和 [Agent 交互](../features/agent-interaction.md)，实际目录以当前构建 tools/list 为准。

## 已有证据及其含义

Windows 专项已观察真实文件读取/写入、Unicode 内容、独立磁盘字节/hash、长文件覆盖截断、重新打开、拒绝覆盖和取消保留状态。界面专项观察了窗口/模态、Popup/Flyout/菜单、选择、滚动与拖动后的状态。这些代表性业务流程用于验证通用工具链路，业务结果由测试驱动判断；Injected、dispatched 或 picker 关闭本身均不证明业务成功。

本轮新增原始 capture 与 PNG 专项：capture 20 项 Headless/故障 RPC 验证原始值、冻结、所有权、冲突与错误；managed PNG 有真实渲染像素和区域/缩放/预算验证；WinForms SDK 验证 RGB、动态文字及 foreground 不变；真实 stdio MCP 验证 Avalonia 控件、原生主窗、CommonItemDialog/路径框区域与无效/stale 拒绝。实际主窗和 OS picker PNG 已人工查看。native 几何 70 项只验证坐标/预算，不是运行截图证据。完整分层和本轮统一结果见 [测试说明](../../testing.md)，专项不提前冒充整套通过。

此前 [477 通过、1 失败、1 Explicit 未执行及独立复测](../verification/2026-10-25-agent-ui-final.json)、467、459、417 和 368 项均为历史源码/产物的批次，不是本轮结果；旧官方 48/52 也不是当前产品完成率。

## 仍需保留的运行限制

- 任意自绘/第三方 picker 不保证匹配当前公共文件弹窗结构；结构、唯一性或树完整性不足时明确拒绝。
- native PNG 取 PrintWindow(PW_RENDERFULLCONTENT) 窗口表面，不激活/恢复窗口、不回退桌面抓屏。未绘制区域会报错，但 GPU、受保护、分层或嵌入内容仍可能黑屏或不完整；颜色正常不证明完整呈现。控件子树应优先用 ui-screenshot。
- 窗口销毁 epoch 是异步通知，不是同步屏障。实际 HWND 复用已有红样本及防护，修复后的完整复用拒绝、真实 PID 循环、强制 OS 部分 SendInput 失败及真实清理状态仍未取得完整证据。
- 双 picker 歧义已有 SDK/controller + 真实 UIA 层证据，MCP 双 picker 样本单列；立即读回不一致、慢 provider 和失败前缀清理有合成 provider/sender 证据，不能冒充真实 OS 故障复现。

具体窗口/输入边界见 [Windows 边界补证](windows-boundary-coverage.md) 与 [窗口身份研究](native-window-identity.md)。工具数、测试数和研究条目是不同口径，均不表示整个产品完成百分比。

## 已明确排除的范围

官方授权运行等价、macOS/Linux 原生后端不做；IME 候选/组合输入及系统文件/数据 DragDrop 暂不做。完整 CPU 调用栈与内存分配剖析交由其他工具；本项目 Profiler 只采样式/资源活动。

业务场景语言、业务断言引擎、业务/性能通过判断、视觉比对/基线、性能阈值、发版门禁和 CI 平台均不在产品范围。调用方可用通用工具和原始数据自行编排、关联和判断。旧文档将这些项目写成“尚未完成”不再构成本轮待办，当前没有 run-scenario 产品入口。

## 研究依据

- [官方能力与证据等级](official-capabilities.md)
- [原版 2.2.3 工具采样与输入 schema](official-mcp-2.2.3-schema.json)
- [原版未决运行语义研究](runtime-unknowns.md)
- [DevTools MCP 公开资料](https://docs.avaloniaui.net/tools/developer-tools/mcp)

公开资料与归档用于界定基准，源码说明本地实现，分层测试证明实际行为。保留这些资料不表示将原版授权运行对照重新列入计划。


后续显式补证（2026-10-25）：完整根HWND复用实验已独立1/1通过，32,766轮/61,173ms后原句柄593860被新窗口使用；旧根/子节点读取及后续写入/聚焦均拒绝，新内容不变。该结果不改写之前常规回归的1项未执行。动作发生在失效读取之后、异步通知非原子屏障等限制仍保留，参见[显式记录](../verification/2026-10-25-hwnd-reuse-verified.json)。
