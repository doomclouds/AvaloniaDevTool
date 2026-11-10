# 0.1.5 插件安装与宿主启动验证

验证日期：2026-11-10，Windows x64。插件版本为 0.1.5，工具实现与自包含 EXE 沿用已验证的 0.1.4。

## 0.1.4 问题与修正

用户安装后的 Codex 日志明确报告：`Agent Plugins stdio command must be a bare executable name or a contained ./ path`。0.1.4 的 command 使用 `${PLUGIN_ROOT}/mcp/AvaloniaDevTool.exe`，因此在宿主解析阶段被拒绝，未加载工具；单独验证 EXE 无法覆盖这个错误。

0.1.5 改为 `command: ./mcp/AvaloniaDevTool.exe`，`cwd: ./mcp`。marketplace 的 source.path 从仓库根 `./` 改为 `./plugins/avalonia-devtool`。完整工程留在仓库 source/；.git、.codegraph、.agents、开发文档、测试和构建脚本均不在可安装插件目录。

## 最终安装内容

```text
plugin.json
mcp.json
mcp/AvaloniaDevTool.exe
skills/avalonia-devtool/SKILL.md
assets/avalonia.png
assets/README.md
```

只有六个文件和四个必要目录，没有 setup 空目录。构建专用的 MCP 小图标已移到 source/assets；运行时该图标已嵌入 EXE。

## 实际宿主验证

使用当前桌面版正在运行的同一份 Codex 程序，版本 `0.167.0-alpha.3.2`，在隔离 CODEX_HOME 下通过正式 CLI 注册本地 marketplace、安装 0.1.5，再运行 app-server：

- 安装目录文件白名单与内容哈希核对通过。
- 通过 `mcpServerStatus/list` 实际加载 avalonia，返回 94 个工具，包含 attach-to-app、tree 和 recording-start。
- 通过 `mcpServer/tool/call` 调用 select-machine，返回本机 ready。
- 同样通过宿主调用 attach-to-app 发现应用，未返回 MCP 错误。
- 两个图标的 MSBuild 资源路径均能解析到实际文件。
- 独立 ZIP 包核对通过，仅包含上述六个文件。

可重复执行 `source/tools/Test-Plugin.py --repo <仓库> --codex <桌面版codex.exe路径> --output <隔离测试目录>`。脚本不修改用户的现有插件安装。原始记录保留在本地 artifacts/plugin-host-final-0.1.5/。

EXE SHA-256 仍为 `b761bdb95c40a4ed82f9428477dc0a4a617ebdd8542128d1d2bb418accadfd0f`，与 0.1.4 完全一致。工具运行、热附加和录屏的实测边界见 [0.1.4 记录](release-0.1.4.md)。本轮验证集中在安装边界和真实 Codex 宿主连接。
