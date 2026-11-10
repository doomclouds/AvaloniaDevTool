# 0.1.4 分发与验证

验证日期：2026-11-10，Windows x64。本次将完整开发工程迁至 `source/`，`mcp/` 只分发当前版本 `AvaloniaDevTool.exe`，MCP 配置直接启动 EXE 的 `mcp` 角色。正式 C# 文件入口、Prepare.ps1、Start-Mcp.ps1 已删除；热附加使用随包的预编译 Agent，不现场构建。

## 实际产物

- EXE：`mcp/AvaloniaDevTool.exe`，版本 0.1.4，96,030,208 字节。
- EXE SHA-256：`b761bdb95c40a4ed82f9428477dc0a4a617ebdd8542128d1d2bb418accadfd0f`。
- 载荷 SHA-256：`e3b40357603cf9fdea6136fb511b55d19574468aa96de6f87baaf9b0cbfb1514`。
- 载荷清单 schema 2，1102 个运行文件，解压后 483,677,348 字节；相同内容在 ZIP 中只存一份。第三方原生 PDB 不进入运行包，许可证保留。
- 包含自包含 CLI、Previewer、native worker、recording worker，Windows 原生/托管引导，以及 Avalonia 12.1.1/12.1.4 Agent。外层启动器为 Native AOT EXE。
- 四个运行角色的 Microsoft.NETCore.App 均为 10.0.14，AOT 编译器也为 10.0.14，未关闭 NuGet Audit。版本选择对应 [Microsoft 的修复公告](https://github.com/advisories/GHSA-p3jp-9v83-76w9)。

## 已完成的验证

| 项目 | 结果 |
| --- | --- |
| 包装与缓存测试 | 21 项通过，含重复内容恢复、缓存篡改修复、非法引用拒绝及进程生命周期 |
| 协议、录屏合同与引导测试 | 48 项通过 |
| MCP 相关回归 | 27 项通过，1 项异常断线测试超时，见下文 |
| 独立 EXE / Avalonia 12.1.4 | 通过热附加、树与属性、点击、临时覆盖恢复、PNG、native worker、断开重连、HTTP 回环 |
| 独立 EXE / Avalonia 12.1.1 | 同上通过 |
| 录屏 / Avalonia 12.1.4 | MP4 完成，硬件编码已验证，导出长度与 SHA-256 校验通过 |
| 正式本机缓存 | `--bundle-verify` 通过，MCP 版本 0.1.4、94 个工具，初始化约 3.19 秒 |

独立 EXE 测试只将该文件复制到隔离目录，使用空运行缓存，并移除工具 PATH 中的 dotnet、设置不可用的 DOTNET_HOST_PATH 和 DOTNET_ROOT。被测样例没有 Agent 引用和启动钩子，由测试单独启动；测试结束后关闭自建进程。测试脚本为 `source/tools/Test-Bundle.py`。

空缓存的两次初始化分别约 13.98 秒和 19.22 秒。首次安装或更新先运行 `AvaloniaDevTool.exe --bundle-verify` 准备缓存，再刷新 MCP，避免首次解包占用握手时限；这一步无需 SDK 或源码编译。本机正式缓存已按最终 EXE 验证。

原始日志、TRX、PNG 和 MP4 保留在仓库的本地 `artifacts/test-results/0.1.4/`、`artifacts/bundle-e2e-0.1.4/` 和 `artifacts/release-0.1.4/`，不随插件分发。ScreenRecorderLib 上游补丁迁移前后的 Git blob 均为 `cbe0e49cd56f7b0ea83a9a3eb82b45c5405d93de`。

## 已有失败与边界

`GatewayMcpIntegrationTests.Machine_switch_refuses_an_active_wait_and_remote_loss_never_falls_back_or_replays_input` 在当前代码中两次超时。对原始提交 `7bc98eb` 建立独立 worktree 后，相同测试也在相同位置以 TaskCanceledException 超时，约 96 秒，已保留基线 TRX。本次不修改该网关异常断线行为。

HTTP 验证为本机回环，未替代真实双机网络验证。Previewer 已完成自包含发布和资产核对，本次未重新执行用户 XAML 工程预览的端到端流程。
