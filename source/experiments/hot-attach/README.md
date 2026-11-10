# Windows x64 外部热附加最小验证

本目录是独立实验，不修改正式 `attach-to-app` 的发现与注入行为。目标 `UninstrumentedApp` 只引用 Avalonia Desktop 与主题包，没有 Agent 项目引用、启动钩子或诊断接入代码。

`Build-Probe.ps1` 是早期独立原型，在用户缓存中构建目标、托管引导和原生引导。实验开发机需要 MSVC x64 与包含 `mscoree.h` 的 Windows/.NET Framework SDK。正式插件使用 `mcp/AvaloniaDevTool.exe`，包含预编译引导及 Agent，不需要这些开发工具。

`-AvaloniaVersion` 可将实验 Agent 按目标实际版本重新构建；应同时通过 `-OutputRoot` 使用独立目录，不能用较新 Avalonia 引用强行覆盖目标已有程序集。该参数提供显式实验构建，不代表正式的版本自动选择已经实现。

`Injector.cs` 仅供早期独立原型研究，不是插件启动入口。调用方必须提供所选进程的 PID、启动时间和进程名；通过原生引导调用目标已有 CoreCLR 的 `GetCLRRuntimeHost` / `ExecuteInDefaultAppDomain`，然后在 Avalonia UI 线程启动现有 Agent。原生模块保留到进程退出，不宣称完全卸载。

托管入口采用 Snoop 所需的 `static int Run(string)` 签名。该原型使用默认加载上下文与限定依赖解析，验证单一 .NET 10 / Avalonia 12.1.4 组合；正式版本隔离和跨版本附加尚未实现。它不使用 Harmony、Profiler 或另一套 CLR。

源码依据：
- https://github.com/snoopwpf/snoopwpf/blob/develop/Snoop.GenericInjector/NetExecutor.cpp
- https://github.com/snoopwpf/snoopwpf/blob/develop/Snoop.InjectorLauncher/Injector.cs

成功标准：SDK-only 目标在注入前没有 Agent 依赖；目标先独立运行；注入后用插件 MCP 发现并附加，读取树、属性、截图，验证交互与属性覆盖恢复；断开后目标继续运行，重复连接可用。

当前分发回归使用 `source/tools/Test-Bundle.py --repo <插件根> --fixture <已构建的UninstrumentedApp.dll> --output <证据目录>`；可加 `--recording` 验证录屏与导出。它将 EXE 单独复制到测试目录，隔离缓存并从工具 PATH 移除 dotnet，通过正式 MCP 完成原生附加、本机交互、截图、原生 worker、重连及 HTTP 回环。测试仅操作自己启动的样例和 HTTP Console，并在结束时关闭它们。无需运行本目录的原型注入器。

不支持 NativeAOT，不对自包含单文件、其他架构、其他 Avalonia 版本或双机网络声称通过。远程线程超时返回执行结果未知，不自动再次注入或终止远程线程。
