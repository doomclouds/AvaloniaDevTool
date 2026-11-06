---
name: avalonia-devtool
description: 初始化、构建和使用 Avalonia DevTool MCP，检查或操作本机及远端 Avalonia 应用，包括目标发现、控件属性与样式、交互、截图和诊断。
---

插件根可从本技能目录向上两级确定。命令使用实际绝对路径，不依赖当前工作目录。参数、枚举和可选字段以本次 `tools/list` 为准；下文的 ID 都是发现返回的示例占位符，不是可以直接使用的值。

## 首次使用与环境检查

首次安装、源码更新或 MCP 启动失败时，运行 `pwsh -NoProfile -File <插件根>/mcp/Prepare.ps1`。它检查 Windows x64、.NET 10 SDK，构建 `AvaloniaDevTool.cs` 及其引用的现有 CLI 项目。构建失败时据真实错误报告缺失环境，不自动安装 SDK、C++ 工具链或修改用户 Codex 配置。

核对 `SdkVersion`、`EntryAssembly`、`RuntimeRoot` 和 `Reused`。成功后才写入用户缓存的就绪记录；源码或 SDK 变化后重新构建，排查损坏缓存时使用 `-Force`。工具使用共享 .NET，不执行自包含 publish；项目源码随插件提供，NuGet 恢复及本机编译会产生缓存。

验证 MCP 初始化、工具列表和 `attach-to-app {}` 只读发现。没有目标时空列表也可以证明发现调用成功，不能据此声称热附加通过。工具未出现在当前聊天时，区分直接 MCP 验证与宿主安装状态。`mcp/Start-Mcp.ps1` 在没有有效缓存时也会调用准备步骤，构建日志写 stderr，stdout 留给 MCP 协议。

环境检查、构建、MCP 连接和真实目标附加分别报告。热附加原型按 `../../mcp/experiments/hot-attach/README.md` 执行，仅操作本任务的验证目标；本技能不授权修改目标工程、注入未选择的进程或写入禅道。

## 本机

1. `select-machine {}` 选择本机；`attach-to-app {}` 发现应用，再以返回的 `id` 调用 `attach-to-app {"id": ID}`。应用 ID 不能假定等于 PID。
2. 用 `tree {"depth": 3}` 读取有限深度的树，用 `search {"query": "控件名称"}` 缩小范围。不要先读取整个应用的无限深度树；分页时查看截断、游标和剩余项。
3. 对返回的节点使用 `props`、`styles`、`ancestors`、`element-metadata`。`object` 只对实际返回的对象 ID 下钻；getter 可能执行目标代码，记录每项失败。
4. 需要操作时使用 `ui-click`、`ui-input`、`ui-select-item` 或 `action`。操作回执后回读相关属性、树或截图，不能把输入已投递解释成业务已成功。
5. 使用 `screenshot` 或 `ui-screenshot` 取得 MCP 图像内容；检查实际区域、尺寸和限制。`native-screenshot` 与框架截图的坐标空间不同。
6. `set-prop` 用于临时调试覆盖；按当前工具合同用 `unset` 释放覆盖，字符串 `null` 表示真正的 null 值。结束时 `detach {}`，释放会话临时修改，不终止外部应用。

当前正式 `attach-to-app` 仍连接 Agent 端点。零引用、零接入代码的热附加正在 `mcp/experiments/hot-attach/` 独立验证，未正式接入工具前不要声称可以直接按 PID 注入任意程序，也不要为此改目标工程。

## 远端

远端执行电脑也需要插件源码、PowerShell 7、.NET 10 SDK。首次在那里运行 `mcp/Prepare.ps1`；在目标用户的交互桌面启动：

```powershell
pwsh -NoProfile -File <远端插件根>/mcp/Start-Mcp.ps1 serve
```

默认 HTTP 执行端为 TCP 7443、路径 `/mcp`；可用 `serve --listen IP --port PORT` 指定监听。`select-machine` 只接收 IP；使用非默认端口时，在调用方启动 MCP 前将 `AVDT_EXECUTOR_PORT` 设置为同一个端口。当前 Console 为无 Token 内网模式，需现有网络可达，不能从 URL 自动启动远端进程。

调用方只使用一个 stdio MCP：`select-machine {"ip":"远端IPv4"}`，再 `attach-to-app {}` 重新发现和附加。后续 tree、属性、交互、截图等都在所选电脑执行。显式 `127.0.0.1` 也走 HTTP，可用于本机验证远端链路。`select-machine {}` 切回本机。

机器或应用切换后，重新取得应用、节点、弹窗、截图采集和操作 ID；不要复用旧句柄。远端断线不会自动退回本机，超时不代表操作未执行，不能盲目重发点击、输入、保存等动作。

文件、项目和文件对话框路径都属于执行电脑；不能把本机路径当远端路径。截图应随 MCP 返回图像，远端磁盘路径不等于图像已传回。

## 原生弹窗与诊断

Windows 系统文件弹窗使用 `native-windows`、`native-tree`、`native-find` 和 `file-dialog-*`，不要拿 Avalonia 节点 ID 当 UIA 节点 ID。原生异步动作返回 pending/dispatched 后，用相应状态工具及实际界面验证完成。

日志、事件、指标与性能采集按任务选择 `capture-*` 或对应域工具；只收集问题所需的范围。采样为空、未订阅、读取失败和数据丢失分别解释。业务断言、硬件结果和视觉判断由调用方依据证据完成。

完整能力及边界见 `../../mcp/docs/mcp.md` 与 `../../mcp/docs/architecture/runtime-contracts.md`。报告时区分已验证的本机、回环 HTTP、真实双机网络和零接入热附加，不能互相替代。
