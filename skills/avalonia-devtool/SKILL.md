---
name: avalonia-devtool
description: 使用 Avalonia DevTool 自包含 EXE 的 MCP 工具发现并原生热附加 Windows Avalonia 进程，检查控件、执行交互、截图和录屏，支持本机及远程电脑，无需首次源码构建。
---

本插件提供直接 MCP 工具。正常操作使用这些工具，不要先编写临时 CLI、Node 或 Python 客户端。MCP 工具尚未加载时先修复连接，不把“可通过手工脚本调用”当成宿主连接成功。

## 首次准备和启动

插件根为本技能目录的上两级。正式入口是 `<插件根>/mcp/AvaloniaDevTool.exe mcp`，`mcp.json` 已声明直接启动 EXE。要求 Windows x64；正常使用不需要 .NET SDK、NuGet 恢复、PowerShell 初始化或源码构建。不要调用已移除的 C# 文件入口及准备脚本。

首次安装或版本更新时，直接执行 `<插件根>/mcp/AvaloniaDevTool.exe --bundle-verify`，校验并准备 `%LOCALAPPDATA%/AvaloniaDevTool/bundles` 中的运行缓存，然后刷新 MCP。首次解包可能超过宿主默认握手时限；这一步只运行随包 EXE，不构建源码。可用 `--bundle-info` 查看版本与载荷摘要。初始化失败时核对 EXE 是否存在、版本、启动日志与校验结果；不自动安装 SDK 或修改用户 Codex 配置。

EXE 已包含运行库、原生引导与匹配 Avalonia 12.1.1/12.1.4 的预编译 Agent，热附加不会现场编译。只有评估或构建用户 XAML 工程才需要 .NET SDK。源码与开发打包脚本位于插件的 `source/`。

初始化后检查 tools/list、工具图标和 `attach-to-app {}`。空列表只证明发现调用成功，不能证明热附加成功。若宿主提示初始化失败，检查实际启动命令、准备输出和启动日志；工具目录在当前聊天缺失时，不得声称已经通过直接工具调用完成。

## 本机发现与原生附加

1. `select-machine {}` 选择本机。
2. `attach-to-app {}` 列出 Agent 端点和 Windows 桌面 CoreCLR 候选。以 processId、名称及 startTimeUtc 核对用户选定的程序。
3. 将本次返回的 **id** 传给 `attach-to-app {"id": ID}`。id 是发现标识，不能用 PID 替代。`requiresInjection=true` 表示需要原生引导；工具会确认 Avalonia 版本、加载匹配 Agent 并连接管道，不需要目标引用 Agent、修改接入代码、重新编译或重启。
4. 只有结果 attached=true 且 session 指向预期进程时才开始操作。attachment 返回准备、注入和总耗时；existing-endpoint 表示复用已经存在的端点。

当前原生附加范围是 Windows x64、.NET 10 CoreCLR、Avalonia 12.1.1/12.1.4。候选进程不保证是 Avalonia；不支持的目标会明确失败。其他平台仍可连接已有的 Agent 端点，不能据此宣称支持其原生热附加。NativeAOT、自包含单文件和其他版本没有在本次支持范围内。

## 检查、操作与确认

先用 `tree {"depth":3}` 读取有限结构；需要模板中的按钮时使用 `search` 的 searchTemplates=true，或对已知子树读取 Visual。按实际类型、name、preview 选择控件，不猜测节点 ID。大树使用 tree-children 分页并核对截断及游标。

`props`、`styles`、`ancestors`、`element-metadata` 用于读取状态；`object` 只对实际返回的对象 ID 下钻。getter 可能执行目标代码，逐项报告失败。

交互使用 `ui-click`、`ui-input`、`ui-key-chord`、`ui-select-item`、`action` 等当前目录中的工具。只读的自定义编辑器应使用其正常按钮或键盘交互，不解除只读限制来绕过业务校验。操作后回读属性、树或图像验证效果，输入投递回执不等于业务成功。

`screenshot`、`ui-screenshot` 返回框架图像，`native-screenshot` 返回原生图像；核对区域、尺寸和坐标空间。`set-prop` 是临时诊断覆盖，unset 释放覆盖，字符串 null 表示真正 null；不要用临时覆盖代替需要持久化的业务输入。

Windows 文件选择器使用 file-dialogs、file-dialog-wait、file-dialog-set-paths、file-dialog-accept 等；其他系统窗口使用 native-windows/native-tree/native-action。原生节点 ID 与 Avalonia 节点 ID 不混用。pending/dispatched 后继续查状态。对话框关闭后还要验证目标文件实际写入和应用状态。

采集、日志、指标、事件与性能工具按任务需要选择 capture-* 或对应能力域，不把空样本、丢失、未订阅和读取失败混同。最终报告实际完成的动作和证据。`detach {}` 清理本会话临时状态，不终止外部应用；原生 DLL/程序集可能保留到目标进程退出。

## 录屏

附加应用后调用 `recording-start {}` 录制整窗；可传 `nodeIds` 自动取同一窗口内控件的包围范围，或传 `windowHandle` 选择 `native-windows` 返回的已核验窗口。两者互斥。`outputDirectory` 可选，省略时保存到执行电脑的 `%LOCALAPPDATA%/AvaloniaDevTool/recordings`；MP4 在录制时持续写入。

开始后先查询 `recording-status`，等待 `state=recording` 再复现。默认 30 fps、3 Mbps、120 秒、128 MiB 文件预算；长流程开始前调整 `maxDurationSeconds`（最多 1800 秒）和 `maxBytes`（最多 512 MiB），避免自动结束后遗漏证据。默认 `mouseClicks=true`，真人鼠标及 Agent 框架点击、滚动、拖拽由视频内标记显示；检查 `Limitations`，旧 Agent 或暂停中的初始订阅可能缺少框架输入提示。标记不修改目标画面。整窗和局部范围跟随窗口移动；跨 DPI 或局部范围失效时核对结束原因，不把旧坐标当成功取景。

结束用 `recording-stop`，若仍在 finalizing 就继续查询。`recording-list` 找回成片；`recording-export {"artifactId":"..."}` 下载到调用电脑，`destinationPath` 可选。停止成功、文件可播放和 bug 已修复分别确认。实际硬编能力、输入提示损失及错误以返回状态为准。

## 远端电脑

执行电脑需要 Windows x64 和同一版本的 `AvaloniaDevTool.exe`，无需复制源码或安装 SDK。在目标用户交互桌面运行：

```text
AvaloniaDevTool.exe serve
```

默认监听 TCP 7443 的 `/mcp`。可加 `--listen IP --port PORT`；非默认端口还要在调用方启动 MCP 前设置相同的 AVDT_EXECUTOR_PORT。当前为无 Token 内网模式，网络需已可达，URL 不会自动启动远端服务。

调用方仍只有一个 stdio MCP。`select-machine {"ip":"远端IPv4"}` 后重新执行发现和附加；原生注入、Agent 加载、UIA、文件路径都属于所选执行电脑。显式 127.0.0.1 也走 HTTP，可用于回环检查；它不能替代真实双机验证。

切换机器、应用或重连后重新取得应用、节点、弹窗和采集句柄。远端断线不会回退本机，超时不表示未执行，不盲目重发注入或业务写操作。完成远端操作后可 `select-machine {}` 切回本机。

工具名、枚举和参数以当前 tools/list 为准。完整边界见 `../../source/docs/architecture/runtime-contracts.md`。此技能不授权修改目标工程、操作未选进程、自动添加测试业务内容或写回禅道。
