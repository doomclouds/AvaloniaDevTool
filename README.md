# Avalonia DevTool

Avalonia DevTool 可以作为普通 MCP 服务器独立安装，也可以由 Codex 插件启动。分发与插件版本为 0.1.6，管理脚本新增独立 MCP 的安装、更新、回退和卸载；程序继续使用已验证的自包含 EXE 0.1.4。

EXE 面向 Windows x64，包含 .NET 运行库、预览器、原生自动化与录屏 worker、热附加引导及 Avalonia 12.1.1/12.1.4 的预编译 Agent。运行服务不需要 .NET SDK、NuGet 恢复或现场编译；安装管理脚本需要 PowerShell 7 和 Codex。

```text
.agents/plugins/marketplace.json   仓库 marketplace，指向插件子目录
plugins/avalonia-devtool/          唯一可安装目录
  plugin.json                     插件版本与展示信息
  mcp.json                        直接启动 ./mcp/AvaloniaDevTool.exe
  mcp/AvaloniaDevTool.exe          自包含可执行文件
  scripts/                        与 source/tools 同步的安装管理脚本
  skills/avalonia-devtool/SKILL.md  唯一操作技能
  assets/avalonia.png              插件图标
  assets/README.md                 图标来源说明
source/                           源码、测试、开发文档与构建材料
README.md                         仓库开发说明，不随插件安装
```

## 独立 MCP 安装与维护

独立分发 ZIP 包含 EXE、三个 PowerShell 脚本和安装说明。解压后在该目录运行：

```powershell
pwsh -NoProfile -File ./Manage-CodexMcp.ps1 -Action Install
pwsh -NoProfile -File ./Manage-CodexMcp.ps1 -Action Status
```

从源码仓库操作时，入口为 `source/tools/Manage-CodexMcp.ps1`；已安装插件的入口为实际插件目录中的 `scripts/Manage-CodexMcp.ps1`。`-PackagePath` 可显式指定待安装的 EXE，默认采用当前分发包中的程序。安装器读取 EXE 内嵌的程序版本和平台，验证载荷后安装到用户版本目录，并将普通 `avalonia` MCP 注册到固定入口 `%USERPROFILE%/.local/bin/AvaloniaDevTool.exe mcp`。

若 Avalonia 插件已安装，管理器保留插件提供的技能，单独关闭其内置 MCP。只注册一个服务，避免两种入口重复加载。普通服务器与插件分发相互独立：卸载插件缓存不应删掉已安装的普通服务程序，插件升级也不会自动升级该固定入口。

后续维护使用同一管理入口：

| 操作 | 命令及结果 |
| --- | --- |
| 更新 | 获取并解压新版分发 ZIP，运行 `-Action Update`；也可加 `-PackagePath` 指定新版 EXE。固定启动路径不变，旧版本保留。 |
| 回退 | `-Action Rollback -Version 0.1.4` 选择已安装的程序版本，版本号以实际保留目录为准。 |
| 状态 | `-Action Status` 核对当前程序、固定路径及 Codex 注册。当前聊天是否已加载工具还需在聊天中确认。 |
| 卸载 | `-Action Uninstall` 移除普通 MCP 注册；默认保留程序、历史版本和录屏。明确清理本工具管理的程序文件时增加 `-RemoveFiles`。 |

`-ExpectedSha256` 可核对可信发布源提供的 EXE 校验值；自定义安装位置时，后续操作传入相同的 `-InstallRoot` 和 `-BinDirectory`。`-CodexPath` 可指定需要配置的 Codex 程序。脚本没有后台更新，不会为了替换被占用的 EXE 强行结束 Codex；遇到文件锁时先关闭对应 MCP 连接再更新。

独立 MCP 不参与当前 Codex 对 portable Agent Plugin 工具声明的共享字节预算，仍需遵守宿主权限、工具可见性及模型上下文限制。安装或切换后重新加载 MCP 连接；验收时分别检查服务器完整目录和当前聊天的实际可调用目录，不能只把服务端发现成功写成聊天已完整加载。

## 插件模式安装与使用

本仓库提供 `.agents/plugins/marketplace.json`，可从当前分支安装或更新：

```powershell
codex plugin marketplace add https://github.com/doomclouds/AvaloniaDevTool.git --ref codex/agent-tools
codex plugin add avalonia-devtool@avalonia-devtool
```

插件模式在安装目录直接启动 `mcp/AvaloniaDevTool.exe mcp`。安装或更新后，由 `avalonia-devtool` 初始化技能运行 `mcp/AvaloniaDevTool.exe --bundle-verify` 准备缓存，再刷新 MCP。首次解包可能超过宿主默认握手时限，此准备步骤只运行 EXE。若改用上述独立模式，随后不要再次启用插件内置 MCP。

启用插件后，调用 `select-machine {}` → `attach-to-app {}` → `attach-to-app {"id":发现返回的id}`。Windows 桌面 CoreCLR 候选会标记 requiresInjection；选中后确认 Avalonia 版本，加载随包 Agent 并连接管道，目标无需引用 Agent、修改代码或重启。随后使用 tree、props、ui-click、screenshot 等工具。完整操作见 `avalonia-devtool` 技能。

原生支持范围为 Windows x64、.NET 10、Avalonia 12.1.1/12.1.4。首次运行会校验并解包内部资产到本机缓存，后续复用；单 EXE 指分发文件，运行时会启动隔离子进程。XAML 用户工程的评估与构建仍需要 .NET SDK。

远端只需复制同一 EXE，在目标用户交互桌面运行 `AvaloniaDevTool.exe serve`，调用方通过 select-machine 选择 IP。插件、服务和每个 MCP 工具均声明 Avalonia 官网图标，来源见 [图标来源](plugins/avalonia-devtool/assets/README.md)。

## 录屏

使用 `recording-start/status/stop/list/export`，默认整窗 H.264 MP4，`nodeIds` 可选同一窗口内控件范围；鼠标和 Agent 框架点击提示合成进视频。默认 30 fps、3 Mbps、最长 120 秒，输出最长边 1920 像素、不录音。`outputDirectory` 省略时保存到执行电脑的 `%LOCALAPPDATA%/AvaloniaDevTool/recordings`；`recording-export` 校验并下载到调用电脑。

录屏要求 Windows 10 build 19041 或更新版本、Media Foundation、VC++ 运行库及可验证的硬件 H.264 编码器，不自动回退软件编码。ScreenRecorderLib 补丁、许可证和可选重建脚本在 `source/native/win-x64/recording/`。已有旧 Agent 的目标需重启后加载新版本。

## 开发与发布

开发工程位于 `source/`，开发构建需要 .NET 10 SDK 和 PowerShell 7；发布原生启动器还需要 MSVC 与 Windows SDK。在仓库根运行：

```powershell
dotnet build source/AvaloniaDevTool.slnx -m:1
pwsh -NoProfile -File source/build/Build-Bundle.ps1 -Version 0.1.4
pwsh -NoProfile -File source/build/Build-Distribution.ps1
```

`Build-Bundle.ps1` 重新构建自包含程序并输出到 `plugins/avalonia-devtool/mcp/AvaloniaDevTool.exe`；`Build-Distribution.ps1` 校验现有 EXE，同步三个管理脚本到插件，并生成独立 ZIP 与 SHA-256 文件，不重新编译程序。只同步脚本时可传 `-SyncOnly`。版本化 ZIP 默认输出到 `artifacts/distributions/<插件版本>/`，已有同版本产物时拒绝覆盖。

程序代码变化时同时提交源码、程序版本号与实际 EXE；只有管理脚本或技能变化时可只提升插件与分发版本。开发说明见 [source/README.md](source/README.md)，打包说明见 [source/docs/packaging-and-installation.md](source/docs/packaging-and-installation.md)。


发布前用桌面版 Codex 实际安装并验证宿主连接：

```powershell
python source/tools/Test-Plugin.py --repo . --codex <桌面版codex.exe路径> --output artifacts/plugin-host-check
```

该检查使用隔离 CODEX_HOME，核对安装副本的九个文件及脚本哈希，并通过 Codex app-server 加载工具目录、调用 select-machine 和应用发现，不修改用户的插件安装。独立模式的验证入口及本版结果见 [0.1.6 分发说明](source/docs/release-0.1.6.md)。
