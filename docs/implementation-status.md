# 自研 Agent / MCP 实现状态

核对日期：2026-10-25。当前用户目标是 **Windows 完整界面交互与真实原生文件流程**。默认 `agent` profile 已注册 **44 项工具**：canonical16 + ui9 + native8 + file-dialog8 + capture3。工具注册、源码实现、专项通过、真实桌面和最终统一批次分别记录；当前范围内基础能力与研发验证已交付，排除项和平台/渲染限制见当前范围文档；不以工具或测试数量换算产品完成率。

最新能力验证批次为 **579通过、0失败、1项Explicit未执行**，绑定340个输入、22项目Debug/Release零警告零错误，source drift=0，自有进程清理完成。逐项结果与DLL/TRX哈希见[最新验证](agent-tools/verification/2026-10-25-capabilities-final.json)，产品范围以[当前边界](agent-tools/architecture/current-agent-scope.md)为准。此前477/1/1与467等批次保留为历史。

双击/焦点修复阶段的历史统一批次 **417/417通过，0失败、0跳过**，22项目Debug/Release构建均零警告零错误。原生双击业务计数/手势、窗口切换/抢占、受限光标防错与DPI恢复均已进入同一批。详见 [修复阶段结果](agent-tools/verification/2026-10-25-native-fixed.json)、[修复记录](agent-tools/research/native-input-fixes.md) 与 [官方差异/未完成清单](agent-tools/research/official-vs-own-agent.md)。较早413项中的2项失败保留为历史，不再代表最新源码；全套测试通过也不代表所有平台/provider/异常分支均验收完成。

显式 `--profile official` 保留 canonical 16 项兼容入口，`--profile extended` 保留 62 项旧诊断工具。原版公开 schema、未知语义及许可运行对照属于独立兼容研究；原版许可对照继续 deferred，不把原版 Community 产品限制作为自研 Agent 的前置要求。

## 当前交付与证据

| 范围 | 已交付或已观察结果 | 当前验收边界 |
| --- | --- | --- |
| 默认 MCP 表面 | agent 44 项；official 16、extended 62 显式选择 | 实际 schema/结果需匹配当前 tools/list 和构建版本 |
| Avalonia 框架交互 | 点击/双击/右键、组合键、文本选区、控件选择/provider动作、capture 拖动、等待 | 新 stdio 交互专项 7/7；headless 状态断言不等于全部真实桌面已过 |
| Windows native | net10.0 facade + net10.0-windows7.0 隔离 worker；UIA pattern、窗口/树/查找、受限 OS 输入 | 最新NativeAutomation.Tests 127通过、1项Explicit未执行；16条文件路径预检是子集，不重复累加 |
| 原生文件弹窗 | 发现/检查、路径/类型设置、接受/取消、真实覆盖提示、等待、过期身份拒绝 | Controller → 真实 AutomationSample → 应用 I/O 独立9个场景已通过；不是 stdio MCP 全套结果 |
| 真实 MCP native/文件 | 完整文件、普通UI、十轮同点双击与手势等已进入当前常规统一回归 | 不推导任意自绘provider、所有输入设备或未覆盖失败分支均支持 |
| 验收目标与项目 | AutomationSample、NativeAutomation.Tests/Fixture；slnx 22项目 | Debug/Release构建已过，本批常规执行项全部通过，Explicit未计通过；固定验收清单仍保留部分验证项 |
| 构建与发布 | 修复 worker 重复 MSBuild 实例竞态；按 RID 条件构建/打包；许可文件 TargetPath 隔离 | linux-x64 framework-dependent 交叉发布通过，未实际 Linux 运行 |

完整工具行为见 [Agent 交互](agent-tools/features/agent-interaction.md)，层次、并发、身份和固定验收 ID 见 [完整 UI 自动化](agent-tools/architecture/complete-ui-automation.md)。项目当前为9个生产、3个样例、10个测试/支撑项目；较早17项目说明属于收缩阶段历史。

## 真实文件验收与剩余批次

Controller 的9个真实场景包含单选、多选、明确扩展名和类型保存、文件夹、真实覆盖 prompt、覆盖“是”、覆盖“否”后取消、无效路径后取消及 stale dialog 替换拒绝。证据位于本机 `%TEMP%/avdt-native-dialog-validation/acceptance.jsonl` 与对应 app-result JSON；日志同时记录应用文档、真实路径、UTF-8 内容/字节长度及 SHA256，驱动另行核对磁盘。

这些是底层Controller独立证据；后续完整stdio MCP文件/UI流程及双击/模态已进入当前统一批次。固定35项中的未覆盖分支仍逐项标“部分验证”，不由总测试数抵扣。

保存验收必须验证目标流真实写入、短内容覆盖长文件时截断、关闭流后磁盘长度/hash、重新通过真实 Open 读回。取消必须比较原文档/路径和已有文件；选择器关闭、`AcceptedDialogClosed`、`Injected`、`completed` 都不能单独证明业务成功。实际 I/O 错误需要应用 UI 和结果文件可观察，不静默吞掉。

当前统一结果：**579通过、0失败、1项Explicit未执行**（Agent282、CLI118、Native127通过/1未执行、Previewer15、Protocol26、Remote11）。已排除能力不作为待完成；未执行实验与已知渲染/平台边界仍明确保留。

## 运行边界

native 只支持已附加的本机 Windows 交互桌面及可验证 owner 范围，拒绝远程传输；macOS/Linux 没有对应 native 后端。节点分为 canonical/ui 数值身份和 native 字符串身份，PID+start time、owner、runtime ID 和会话代际均需复核。worker 超时/取消时失效并回收，重新获取会话和节点。

OS 输入需要窗口前台、未被遮挡和允许的权限级别；framework 输入不是 OS 注入，Unicode OS 输入也不是 IME composition，pointer drag 不是文件/数据 DragDrop。自绘或第三方 picker 不提供已知 provider/profile 时明确失败。Linux 交叉发布不证明桌面显示、图像或输入在 Linux 已工作。

Previewer 固定当前 .NET/Avalonia 并有动态 XAML/AOT边界；严格 Native AOT、跨主机/TLS和性能发版门禁仍须分别验证。交互工具不自动构成任意业务流程的通用测试编排器。

## 历史官方兼容基线

较早 canonical 阶段的 **368项测试**、**48/52公开兼容验收**以及当时的17项目构建结果，保存在 [历史当前进度](agent-tools/progress-current.md)、[验证索引](agent-tools/verification/2026-10-25.json)。这些资料绑定较早源码，不是当前44工具后的总数或完成率。

原版2.2.3当时仅有未附加目标的 initialize/tools/list 采样，公开文档16项、初始归档15项且无outputSchema；成功 tools/call、动态目录和部分语义仍未知。C03、DET01、ANC01、PSEUDO03 的 [静态研究](agent-tools/research/runtime-unknowns.md) 不补造运行验证。

更早完整 Inspector/XamlMcp GUI 的476项和约63%功能条目口径只适用于 `main/fa1c623`；已退役测试没有自动变成 CLI 覆盖。[范围审计](agent-tools/architecture/scope-and-dependencies.md) 保留历史删除边界，当前项目清单以 slnx 为准。

本轮实际复现了旧HWND根ID重定向，加入按PID订阅的销毁epoch与根/节点组合校验、确认stale后永久退役、缓存键纳入PID/start time。579项统一批次时尚无修复后完整复用成功样本，随后独立Explicit已1/1通过；异步通知仍不构成原子生命周期屏障，详见[窗口身份研究](agent-tools/research/native-window-identity.md)和[显式验证](agent-tools/verification/2026-10-25-hwnd-reuse-verified.json)。原统一结果保留当时1未执行，后续结果不合并冒充同批。文件类型回退正/负检查与真实文件流程已纳入结果，未扩展平台或业务判断框架。
