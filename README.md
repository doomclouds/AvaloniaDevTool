# Avalonia DevTool Codex 插件

插件直接启动 `mcp/AvaloniaDevTool.exe mcp`。EXE 为 Windows x64 自包含分发，包含 .NET 运行库、预览器、原生自动化与录屏 worker、热附加引导及 Avalonia 12.1.1/12.1.4 的预编译 Agent。正常使用不需要 .NET SDK、NuGet 恢复、PowerShell 初始化或现场编译。

```text
plugin.json                       插件清单
mcp.json                          直接启动 EXE 的 stdio MCP 配置
mcp/AvaloniaDevTool.exe            当前版本可执行文件
skills/avalonia-devtool/           本机与远端用法
assets/                           插件与工具图标
source/                           完整开发工程
  AvaloniaDevTool.slnx
  src/ tests/ samples/ docs/       原工程，内部引用关系保持
  native/ build/ tools/           原生材料与构建打包脚本
  experiments/hot-attach/          独立热附加验证样例
```

## 安装与使用

本仓库提供 `.agents/plugins/marketplace.json`，可从当前分支安装或更新：

```powershell
codex plugin marketplace add https://github.com/doomclouds/AvaloniaDevTool.git --ref codex/agent-tools
codex plugin add avalonia-devtool@avalonia-devtool
```

安装或更新后，由 `avalonia-devtool` 初始化技能运行 `mcp/AvaloniaDevTool.exe --bundle-verify` 准备缓存，再刷新 MCP。首次解包可能超过宿主默认握手时限，此准备步骤只运行 EXE。

启用插件后，调用 `select-machine {}` → `attach-to-app {}` → `attach-to-app {"id":发现返回的id}`。Windows 桌面 CoreCLR 候选会标记 requiresInjection；选中后确认 Avalonia 版本，加载随包 Agent 并连接管道，目标无需引用 Agent、修改代码或重启。随后使用 tree、props、ui-click、screenshot 等工具。完整操作见 `avalonia-devtool` 技能。

原生支持范围为 Windows x64、.NET 10、Avalonia 12.1.1/12.1.4。首次运行会校验并解包内部资产到本机缓存，后续复用；单 EXE 指分发文件，运行时会启动隔离子进程。XAML 用户工程的评估与构建仍需要 .NET SDK。

远端只需复制同一 EXE，在目标用户交互桌面运行 `AvaloniaDevTool.exe serve`，调用方通过 select-machine 选择 IP。插件、服务和每个 MCP 工具均声明 Avalonia 官网图标，来源见 [assets/README.md](assets/README.md)。

## 录屏

使用 `recording-start/status/stop/list/export`，默认整窗 H.264 MP4，`nodeIds` 可选同一窗口内控件范围；鼠标和 Agent 框架点击提示合成进视频。默认 30 fps、3 Mbps、最长 120 秒，输出最长边 1920 像素、不录音。`outputDirectory` 省略时保存到执行电脑的 `%LOCALAPPDATA%/AvaloniaDevTool/recordings`；`recording-export` 校验并下载到调用电脑。

录屏要求 Windows 10 build 19041 或更新版本、Media Foundation、VC++ 运行库及可验证的硬件 H.264 编码器，不自动回退软件编码。ScreenRecorderLib 补丁、许可证和可选重建脚本在 `source/native/win-x64/recording/`。已有旧 Agent 的目标需重启后加载新版本。

## 开发与发布

开发工程位于 `source/`，开发构建需要 .NET 10 SDK 和 PowerShell 7；发布原生启动器还需要 MSVC 与 Windows SDK。在仓库根运行：

```powershell
dotnet build source/AvaloniaDevTool.slnx -m:1
pwsh -NoProfile -File source/build/Build-Bundle.ps1 -Version 0.1.4
```

打包脚本将当前版本输出到 `mcp/AvaloniaDevTool.exe`，按内容哈希去除内嵌资产的重复副本，解包后恢复各角色的独立目录。更新时同时提交源码、版本号与实际 EXE。开发说明见 [source/README.md](source/README.md)，打包说明见 [source/docs/packaging-and-installation.md](source/docs/packaging-and-installation.md)。
