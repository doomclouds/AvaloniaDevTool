# Avalonia DevTool Codex 插件

插件使用 .NET 10 C# 单文件入口，通过 `#:project` 引用现有 MCP 工程。无需自包含发布或手动上传运行时 DLL；首次使用检查环境并从源码构建，结果缓存到用户目录。

```text
plugin.json                       插件清单
mcp.json                          本地 stdio MCP
skills/avalonia-devtool/           环境检查、首次构建、本机与远端用法
mcp/
  AvaloniaDevTool.cs               C# 单文件入口
  Prepare.ps1                     SDK 检查、源码构建、缓存
  Start-Mcp.ps1                    MCP 启动
  AvaloniaDevTool.slnx             原解决方案
  src/ tests/ samples/ docs/       原工程，内部引用关系保持
  experiments/hot-attach/          独立外部附加验证
```

要求 Windows x64、PowerShell 7、.NET 10 SDK，以及可恢复项目依赖的 NuGet 源或离线缓存。Runtime 不能代替 SDK。

首次准备：`pwsh -NoProfile -File ./mcp/Prepare.ps1`。启动：`pwsh -NoProfile -File ./mcp/Start-Mcp.ps1`。

本仓库提供 `.agents/plugins/marketplace.json`，可作为本地插件源注册；安装前应完成上述准备。插件缓存中的副本独立构建，所有生成输出位于用户缓存目录。stdio 首次构建可能较慢，建议先运行初始化技能。

也可从当前开发分支安装，Git 源只分发受版本控制的源码，不包含开发目录中的 bin、obj 和历史自包含产物：

```powershell
codex plugin marketplace add https://github.com/doomclouds/AvaloniaDevTool.git --ref codex/agent-tools
codex plugin add avalonia-devtool@avalonia-devtool
```

启用插件后使用 `avalonia-devtool` 技能完成环境准备和本机／远程操作。插件展示与 MCP 服务共用 Avalonia 官网图标，来源见 `assets/README.md`。

本轮仅验证插件安装、单文件 MCP 与无 Agent 目标热附加，不正式重构既有能力域。原说明见 [mcp/README.md](mcp/README.md)。
