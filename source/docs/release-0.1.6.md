# 0.1.6 独立 MCP 安装与维护

分发与插件版本为 0.1.6，程序沿用自包含 EXE 0.1.4，功能代码与载荷不变。EXE SHA-256 为 `b761bdb95c40a4ed82f9428477dc0a4a617ebdd8542128d1d2bb418accadfd0f`；程序版本、RID 和载荷摘要由 `--bundle-info` 核对，不用插件版本代替程序版本。

## 本版行为

- `Manage-CodexMcp.ps1` 统一管理独立普通 MCP 的安装、更新、回退、卸载及状态查询，复用既有版本安装器。
- 普通 `avalonia` 服务器指向固定用户命令，不指向插件缓存。插件已安装时继续提供技能，其内置 MCP 单独停用，避免重复加载。
- 发布脚本同步三份管理脚本到插件，并生成含 EXE、脚本和安装说明的独立 ZIP 及 SHA-256；已有同版本 ZIP 时拒绝覆盖。
- 更新采用新版分发包或显式程序路径，保留历史程序版本；卸载默认只去除普通注册，不自动恢复内置 MCP，不清理录屏或共享运行缓存。

## 分发内容与维护入口

独立 ZIP 根目录为 `AvaloniaDevTool.exe`、`Manage-CodexMcp.ps1`、`Install-AvaloniaDevTool.ps1`、`Codex-Mcp.ps1` 和 `README.md`。插件新增 `scripts/` 及上述三个脚本，共九个文件。脚本只在 `source/tools/` 修改，再运行 `source/build/Build-Distribution.ps1` 同步和打包。

根 README 和 [打包安装说明](packaging-and-installation.md) 提供实际命令。运行 `-Action Install` 会修改所选 Codex 配置与用户安装文件；构建分发本身不注册服务器或修改用户环境。

## 验证入口与边界

`source/tests/scripts/Test-Installer.ps1` 验证底层版本安装、幂等、校验拒绝和锁定入口回滚；`source/tests/scripts/Test-CodexMcpRpc.ps1` 验证 RPC 连接辅助逻辑；管理器专项验证由对应测试脚本和交付记录说明。

`source/tools/Test-Plugin.py` 在隔离 CODEX_HOME 中安装插件，核对九个文件、内容哈希和宿主工具发现，并调用本机选择及应用发现。独立模式还需核对宿主将 `avalonia` 识别为普通 MCP，比较服务端与聊天可调用工具名称，并与其他插件同时启用验证。

## 已执行的验证

2026-11-11 在 Windows x64、PowerShell 7、当前桌面 Codex `0.167.0-alpha.3.2` 下完成以下检查。隔离测试使用独立 CODEX_HOME 和安装目录，并核对真实用户配置、PATH 与安装环境变量没有被修改。

| 检查 | 实际结果 |
| --- | --- |
| `Test-Installer.ps1` | 首次安装、幂等、多版本保留、校验拒绝、同版本不同内容拒绝、锁定入口失败回滚通过。 |
| `Test-CodexMcpRpc.ps1` | 原生 Codex 与 PowerShell 启动器、UTF-8、通知、错误、拒绝自动审批、超时及自建进程关闭通过。 |
| `Test-CodexMcpManager.ps1` | 真实 Codex 隔离安装、状态、更新、回退、冲突保护与卸载通过；普通服务器 `pluginId=null`，完整返回 94 项，用户自定义 timeout/env 保留，插件技能启用且内置 MCP 关闭。 |
| `Test-CodexMcpManagerFailure.ps1` | 受控模拟 RPC 验证未知文件保护、缺失稳定入口的原状态恢复、晚期占用释放、写入响应丢失后新连接回查且不重放、回查失败时保留可运行程序；这不是实际网络故障复测。 |
| `Test-Plugin.py` | 隔离安装 0.1.6，九文件及哈希核对通过；实际宿主发现 94 项，本机选择和应用发现调用成功。 |
| 独立 ZIP | 五个预期文件及脚本哈希核对通过；同版本产物拒绝覆盖；输出到插件安装目录被拒绝。 |

最终独立产物为 `artifacts/distributions/0.1.6/avalonia-devtool-0.1.6-win-x64.zip`，SHA-256 为 `0865ed57aa241b249965d893d4ca225160b590d4ae9dad893373f21da1e21d77`。管理器与故障专项原始输出保存在本地 `artifacts/standalone-tests/`，插件宿主输出在 `artifacts/plugin-host-0.1.6/`；这些诊断材料不随插件发布。

服务端与宿主完整目录不代表既有聊天已经重新加载工具。实际本机注册及模型可调用目录的结果在本次交付中另行说明；本版没有因此重新验证原生热附加、录屏或硬件行为。
