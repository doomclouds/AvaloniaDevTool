# Avalonia DevTool Codex 插件

插件使用 .NET 10 C# 单文件入口，通过 `#:project` 引用现有 MCP 工程。MCP 直接由 dotnet 启动，首次 build 由初始化技能完成，不做自包含发布。

```text
plugin.json                       插件清单
mcp.json                          本地 stdio MCP
skills/avalonia-devtool/           环境检查、首次构建、本机与远端用法
mcp/
  AvaloniaDevTool.cs               C# 单文件入口
  Prepare.ps1                     SDK 检查、源码构建、缓存
  native/win-x64/                  原生引导 DLL（约 128 KiB）与 SHA-256
  AvaloniaDevTool.slnx             原解决方案
  src/ tests/ samples/ docs/       原工程，内部引用关系保持
  src/Hosts/AvaloniaDevTool.Bootstrap/  无 Avalonia/Agent 引用的托管引导
  experiments/hot-attach/          早期验证样例
```

要求 Windows x64、PowerShell 7、.NET 10 SDK，以及可恢复项目依赖的 NuGet 源或离线缓存。Runtime 不能代替 SDK。

首次准备：`pwsh -NoProfile -File ./mcp/Prepare.ps1`。之后在 `mcp/` 目录启动：

```text
dotnet run --file AvaloniaDevTool.cs --no-build --no-restore --configuration Release -- mcp
```

工具调用顺序为 `select-machine {}` → `attach-to-app {}` → `attach-to-app {"id":发现返回的id}`。Windows 桌面 CoreCLR 候选会标记 requiresInjection；选中后确认 Avalonia、构建匹配 Agent 并通过原生引导热附加。目标无需引用 Agent、修改代码或重启。随后直接使用 tree、props、ui-click、screenshot 等 MCP 工具。

原生支持范围为 Windows x64、.NET 10、Avalonia 12.1.1/12.1.4。正常运行不需要 C++ 编译器；重建原生引导时才需要 MSVC。SDK 会生成本机缓存与工程 bin/obj，这些生成文件不会随 Git 分发。

本仓库提供 `.agents/plugins/marketplace.json`。安装后对安装副本运行初始化技能；MCP 使用 --no-build，不能把未准备的副本当作已经就绪。

也可从当前开发分支安装，Git 源只分发受版本控制的源码，不包含开发目录中的 bin、obj 和历史自包含产物：

```powershell
codex plugin marketplace add https://github.com/doomclouds/AvaloniaDevTool.git --ref codex/agent-tools
codex plugin add avalonia-devtool@avalonia-devtool
```

启用插件后使用唯一的 `avalonia-devtool` 技能完成环境准备和本机／远程操作。远端在其交互桌面运行同一入口的 serve 角色；调用方通过 select-machine 选择 IP。所有路径和原生操作都属于执行电脑。插件、服务和每个 MCP 工具均声明 Avalonia 官网图标，来源见 `assets/README.md`。

录屏使用 `recording-start/status/stop/list/export`。默认整窗 H.264 MP4，`nodeIds` 可选同一窗口内的控件局部范围；鼠标和 Agent 框架输入的点击提示直接合成进视频。`outputDirectory` 可选，省略时保存到执行电脑的 `%LOCALAPPDATA%/AvaloniaDevTool/recordings`，录制过程中持续写盘。`recording-export` 经网关完整下载并校验到调用电脑，`destinationPath` 同样可选。默认 30 fps、3 Mbps、最长 120 秒，输出最长边 1920 像素、不录音；可按需调整帧率、码率、时长和文件预算。

录屏要求 Windows 10 build 19041 或更新版本、Media Foundation、VC++ 运行库及可验证的硬件 H.264 编码器；不自动回退软件编码。所用 ScreenRecorderLib 的静态图层性能补丁、许可证和可选重建脚本随源码提供，正常使用不要求 C++ 编译器。已有应用内 Agent 需要重新启动目标以加载本次新增的控件取景与输入通知能力。

现有能力域保持原结构。开发说明见 [mcp/README.md](mcp/README.md)。
