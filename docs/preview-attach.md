# XAML 文件附加与项目预览

MCP `attach-to-file` 会创建独立预览进程，以明确选择的**已构建源文件程序集和项目文件**加载 AXAML，并可另选启动应用程序集提供 App 资源。成功后该 MCP 会话使用现有 Agent 协议，`tree`、`props`、`input`、`screenshot` 和其他诊断工具与 `attach-to-app` 共用同一条连接。

## 调用

先使用目标项目正常的构建命令生成输出。例如在仓库根目录：

```powershell
dotnet build samples/AvaloniaDevTool.Sample/AvaloniaDevTool.Sample.csproj
dotnet run --project src/AvaloniaDevTool.Cli -- mcp
```

MCP 调用参数：

```json
{
  "name": "attach-to-file",
  "arguments": {
    "filePath": "C:/WorkSpace/ResearchProjects/AvaloniaDevTool/samples/AvaloniaDevTool.Sample/MainWindow.axaml",
    "projectPath": "C:/WorkSpace/ResearchProjects/AvaloniaDevTool/samples/AvaloniaDevTool.Sample/AvaloniaDevTool.Sample.csproj",
    "assemblyPath": "C:/WorkSpace/ResearchProjects/AvaloniaDevTool/samples/AvaloniaDevTool.Sample/bin/Debug/net10.0/AvaloniaDevTool.Sample.dll",
    "watch": true,
    "headless": false,
    "timeoutSeconds": 30
  }
}
```

三个路径都必须是已经存在的绝对文件路径。源文件须位于选择的项目目录中；程序集路径由调用者明确选择，不猜测 Debug/Release、TFM、RID 或多项目启动项目。工具不会自动构建、安装 SDK、修改 IDE 配置或调用目标 `Main`。

成功返回 `Session`（含实际预览 PID）、规范化的源文件／程序集／项目路径、Agent 管道名及 watch/headless 标志。所有后续工具直接操作这个预览会话。

| 参数 | 默认值 | 作用 |
| --- | --- | --- |
| `filePath` | 必填 | 实际编辑的 AXAML 源文件 |
| `assemblyPath` | 必填 | 与源文件项目对应的已构建 DLL |
| `projectPath` | 必填 | 项目文件，用于源文件相对路径和资源基准 URI |
| `applicationAssemblyPath` | `assemblyPath` | 初始化 App 的构建程序集；支持控件库源码与启动应用分离 |
| `applicationType` | 自动选择 | 可显式选择程序集中的 App 或含 `BuildAvaloniaApp()` 的类型 |
| `watch` | `true` | 250 ms 防抖保存监视 |
| `headless` | `false` | 无桌面窗口；仍用 Skia 渲染实际像素 |
| `timeoutSeconds` | `30` | 启动与连接总时限，范围 1–120 秒 |

## 真实项目语义

项目模式先设置设计模式，从 `applicationAssemblyPath`（默认 `assemblyPath`）调用目标入口类型的无参静态 `BuildAvaloniaApp(): AppBuilder`，再调用公开的 `SetupWithoutStarting()`。因此目标 App 的 `Initialize()`、App 资源／主题、字体和配置参与预览；不设置桌面应用 lifetime，也不调用目标入口 `Main`。源文件来自控件库时，`assemblyPath` 指向控件库 DLL，`projectPath` 指向库项目，`applicationAssemblyPath` 指向引用它的启动应用 DLL；库的 `x:Class` 和相对资源按库程序集解析，App 资源由启动应用提供。没有入口的程序集可自动选择唯一的具体 `Application` 类型，或传入 `applicationType`。无法识别的启动结构返回 `unsupported_project_startup`，不会退化成宿主主题并声称项目已成功加载。

AXAML 原样交给 Avalonia 12.1.4 公开的 `AvaloniaRuntimeXamlLoader.Load(RuntimeXamlLoaderDocument, RuntimeXamlLoaderConfiguration)`：

- `LocalAssembly` 是选择的构建程序集，源文档基准为 `avares://程序集名/项目相对路径`。
- `DesignMode=true`，并读取程序集的 `AvaloniaUseCompiledBindingsByDefault` 元数据。
- 保留 `x:Class`；由 Avalonia 运行时编译器处理真实类的构造和 `!XamlIlPopulateOverride`。不会删除 `x:Class` 或以 `UserControl` 替代真实根类型。
- `Window` 根保留真实 Window，显示为单独预览窗口；状态宿主中央明确显示“预览内容已在独立窗口显示：{Title}”，状态宿主和文档窗口均是 Agent 根节点。关闭文档窗口不会结束宿主，宿主提示可点击重新加载或保存源文件重新打开。普通 Control 嵌入状态宿主。
- Style、ResourceDictionary、DataTemplate 使用 Avalonia 官方的 `Design.CreatePreviewWithControl` 转换，遵循 `Design.PreviewWith`／设计数据行为。

官方 `Design.IsDesignMode` setter 为 internal，`Design.CreatePreviewWithControl` 从引用程序集中隐藏。本实现仅在独立预览进程内通过版本固定的桥接调用它们，严格检查运行时为 12.1.4 和方法存在；不匹配返回 `unsupported_designer_api`。XAML 构造和 `x:Class` 行为使用公开 runtime loader。没有复制 Avalonia 源码，也不实现原 IDE TCP/BSON 协议。

上游依据：[RemoteDesignerEntryPoint](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.DesignerSupport/Remote/RemoteDesignerEntryPoint.cs)、[DesignWindowLoader](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.DesignerSupport/DesignWindowLoader.cs)、[runtime compiler](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Markup/Avalonia.Markup.Xaml.Loader/AvaloniaXamlIlRuntimeCompiler.cs)。正式 `DesignWindowLoader` 自身依赖 internal runtime-loader 服务注册，本宿主采用相同 Document/Configuration 直接调用公开 loader，避免依赖该未公开服务。

## 进程与错误

CLI 与子进程使用仅当前用户可连接、随机名称的独立控制管道。子进程只有在应用初始化、第一次 AXAML 加载和 Agent 建立后才发送 ready。CLI 验证 PID／Agent 管道名，再通过普通 `DiagnosticsClient` 连接。控制握手最多 8 KiB，错误文字最多 4 KiB；既有 Agent RPC 4 MiB 帧限制保持生效。

子进程 stdout/stderr 都被持续消费，且目标代码的 `Console.Out` 重定向到子进程 stderr；目标构建器的普通日志不会混入 MCP JSON-RPC stdout。日志缓冲最多 8 KiB。

- 初次文件错误、缺少构建／类型、版本错误：返回明确失败并释放候选进程；不会留下半连接的预览。
- 启动超时或请求取消：关闭控制管道，等待最多 2 秒；必要时终止自有进程树。
- 成功后保存错误：保留上次成功视图，更新 `PreviewErrorText`；修正保存后自动恢复。
- `detach`、更换目标或 MCP 正常退出：断开 Agent 并释放自建预览进程。
- MCP 进程异常退出：控制管道断开，子进程请求退出；即使目标启动构造代码阻塞 UI 线程，独立后台监视也会在 1 秒后结束进程。
- `attach-to-app` 外部应用没有进程所有权；`detach` 不会终止它。

## 范围与限制

这是可加载真实已构建 Avalonia 项目的文件预览，不是任意 .NET 工程的兼容承诺。宿主固定 Avalonia 12.1.4、.NET 10；核心 Avalonia 引用版本不一致会明确拒绝。NativeAOT、裁剪掉反射／动态编译元数据的产物、无可用 AppBuilder／App 的非常规启动，以及仅在桌面 lifetime 分支初始化的 DI／业务状态，可能需要项目提供设计时入口和数据。构造函数和 App 初始化执行真实代码；该子进程不是执行沙箱。

程序集与本机依赖通过 `AssemblyDependencyResolver`、相邻 DLL 以及共享默认 AssemblyLoadContext 解析；避免加载第二份 Avalonia。应用输出目录和控件库输出目录中的同标识 DLL，只有模块 MVID 相同（同一次构建的副本）才共享；同标识但构建不同会明确拒绝，避免把旧依赖当作新源码对应产物。程序集／引用发生重编译后应 detach 再 attach，文件监视只重新加载选定 AXAML，不能热替换程序集。被引用的其他 AXAML／资源使用构建产物；需要先构建它们。单文件严格 UTF-8、上限 1 MiB，禁止 DTD 和外部 XML 实体。

独立运行参数与 classless 兼容模式见 [previewer.md](previewer.md)。自动化通过真实 stdio MCP 客户端启动 production CLI，再启动实际预览子进程，并验证项目 App 资源、自定义控件、保留 `x:Class`、Window、输入、Skia PNG 像素、保存错误／恢复与进程清理。

验证记录：2026年10月25日，14 项 Previewer 回归、8 项真实 MCP 文件附加测试通过。另对 Release `dotnet publish` 的独立分发目录完成 attach、跨程序集控件查询和 detach/PID 清理检查；构建及测试支持 `--artifacts-path`，不会使用固定旧 bin 目录。


