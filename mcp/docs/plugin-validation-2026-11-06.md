# 插件与 Windows 热附加最小验证

验证日期：2026-11-06。代码仍保留既有能力域，本次只迁移到插件目录，增加 C# 单文件入口、首次构建脚本、使用技能、图标和独立热附加实验。

## 已通过

- .NET SDK 10.0.402 构建 `AvaloniaDevTool.cs`，通过 `#:project` 引用现有 CLI。使用共享运行时，无自包含 publish。
- 插件 MCP 完成 initialize、tools/list，返回 89 个工具；stdout 为合法 MCP JSON，服务信息携带离线 PNG 图标。
- 安装副本的 MCP 握手、工具发现、只读应用发现通过；`dotnet run --file ... --no-build --no-restore` 也完成同样的三项调用。
- Codex CLI 在隔离 CODEX_HOME 中注册本地市场并安装 portable 插件，返回安装路径且 enabled=true。
- 无 Agent 项目引用、无诊断启动代码的 Avalonia 12.1.4 样例先独立启动，再通过 Windows x64 原生 DLL 加载托管引导。
- 目标确认注入前没有加载 Agent，`Avalonia.Application` 所在程序集只有一份。
- 成功读取树和属性，点击按钮后读取到 `Clicks: 1`，临时修改 Text 后 unset 恢复；得到 460×240 PNG。
- detach 后目标继续运行，重新发现与附加成功。
- 通过 select-machine 选择回环 HTTP Console，重新附加同一目标并读取属性，随后切回本机。
- 停止注入 Agent 后目标继续运行；验证脚本最后关闭自己创建的样例及 Console。
- CLI 参数与网关集成回归 47/47 通过；单一使用技能通过 skill-creator 校验。
- Codex 安装副本实际完成首次源码构建，耗时约 55 秒。首次启用应先运行技能准备；后续启动使用缓存，不能假定冷构建能在宿主默认握手超时内完成。

原生引导保留 C++，托管入口使用已有 CoreCLR 的 GetCLRRuntimeHost / ExecuteInDefaultAppDomain，不创建第二套 CLR，不依赖 Harmony。

本次源插件暂存为 521 个文件、约 5.81 MiB；这是测试时的源码副本大小，随后文档更新可能改变数字。SDK、NuGet 缓存和构建输出不包含在其中，不应与首次下载总量或本机磁盘占用混同。

## 边界

热附加在 `experiments/hot-attach/`，未正式整合到 attach-to-app 的自动注入流程。原型采用默认程序集加载上下文及限定依赖解析，不代表跨版本隔离已经完成。

通过范围为 Windows x64、.NET 10、Avalonia 12.1.4、框架依赖普通目录部署。未验证其他架构、自包含单文件、NativeAOT、Linux/macOS 或真实双机网络。回环 HTTP 不能替代物理双机测试。

最后一次完整本地证据保存在仓库忽略目录 `artifacts/plugin-probe/run-final-2/`：report.json、tree.json、attached.png、注入状态及 MCP/Console 日志。早期失败输出保留，不将其改写为通过。断开后的端点重建可能使发现 ID 在选择前失效；脚本仅在明确的 app_not_found（尚未附加）错误时重新发现，不重发不确定的注入或 UI 动作。
