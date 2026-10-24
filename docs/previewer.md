# 独立 XAML 源文件预览器

`AvaloniaDevTool.Previewer` 支持已构建项目上下文和独立 classless 源文件两种运行方式。文件保存后重建预览控件；解析失败、暂时删除或不完整写入时保留上次成功内容。所有预览通过真实 Agent 暴露控件树、属性、输入和截图。

优先使用 MCP [`attach-to-file`](preview-attach.md)：它明确选择构建产物、等待 ready，并拥有子进程生命周期。直接运行预览器时由启动者管理进程。

## 已构建项目

```powershell
dotnet build samples/AvaloniaDevTool.Sample/AvaloniaDevTool.Sample.csproj
$xaml = (Resolve-Path samples/AvaloniaDevTool.Sample/MainWindow.axaml).Path
$project = (Resolve-Path samples/AvaloniaDevTool.Sample/AvaloniaDevTool.Sample.csproj).Path
$assembly = (Resolve-Path samples/AvaloniaDevTool.Sample/bin/Debug/net10.0/AvaloniaDevTool.Sample.dll).Path
dotnet run --project src/AvaloniaDevTool.Previewer -- --xaml $xaml --assembly $assembly --project $project --pipe avalonia-devtool-preview
```

目标 AppBuilder 和 App 资源参与初始化，保留 `x:Class` 和真实根类型，源文档使用项目相对 `avares://` URI。Window 根作为真实独立窗口显示，宿主中央说明内容所在窗口及其标题；关闭文档窗口保留宿主并提示重新加载方式。普通 Control 嵌入状态宿主；Style／字典／模板遵循官方设计预览转换。目标 `Main` 不运行。完整语义、受版本守卫的设计模式桥接与限制见 [文件附加文档](preview-attach.md)。

## 独立 classless 文件

```powershell
$xaml = (Resolve-Path samples/AvaloniaDevTool.Sample/Previews/PreviewCard.axaml).Path
dotnet run --project src/AvaloniaDevTool.Previewer -- --xaml $xaml --pipe avalonia-devtool-preview
```

省略 `--project` 时使用宿主的 Fluent 主题，不初始化目标 App。根必须是可嵌入 Control；`Window`、Application、Style、Styles 和 ResourceDictionary 根明确拒绝。没有 `--assembly` 时 `x:Class` 明确拒绝。有 `--assembly` 可解析实际本地类型，但需要 App 资源和项目 URI 时应同时给出 `--project`。

另一个终端运行 Inspector：

```powershell
dotnet run --project src/AvaloniaDevTool.Inspector -- --pipe avalonia-devtool-preview
```

| 参数 | 含义 |
| --- | --- |
| `--xaml ABSOLUTE.axaml` | 显式绝对源文件路径 |
| `--pipe NAME` | 本机 Agent 管道；默认 `avalonia-devtool-previewer-{PID}` |
| `--assembly ABSOLUTE.dll` | 已构建应用或控件程序集 |
| `--project ABSOLUTE.csproj` | 项目上下文，要求同时指定 `--assembly` |
| `--application-assembly ABSOLUTE.dll` | 可选启动应用程序集；默认使用源文件程序集 |
| `--application-type TYPE` | 选择 App／AppBuilder 提供类型 |
| `--headless` | 无桌面窗口，使用 Skia 实际渲染 |
| `--no-watch` | 禁用自动监视，保留手动重载 |
| `--help` | 打印参数说明 |

直接运行允许源文件初始不存在，在状态宿主显示错误；MCP attach 要求第一次加载成功才返回。参数或启动错误以退出码 2 结束。Agent 在 Debug／Release 均启用，因为诊断就是宿主的用途。

## 自动重载和诊断

FileSystemWatcher 监听保存、删除、创建和原子替换，默认 250 ms 防抖。后台事件只排队，真正加载在 UI 线程执行；完整新内容成功后才替换旧视图。重载会重建控件和交互状态。程序集不会在 AXAML 重载时卸载，构建 DLL 或依赖变化后需要重启预览。

宿主有 `PreviewDocumentHost`、`PreviewStatusText` 和 `PreviewErrorText`，可由 Inspector／MCP 检查。关闭宿主停止监视、定时器、排队回调和 Agent。目录不存在时显示监视错误并允许手动重载。单文件 UTF-8（支持 BOM）限制 1 MiB，禁止 DTD 和外部 XML 实体。

```powershell
dotnet test tests/AvaloniaDevTool.Previewer.Tests/AvaloniaDevTool.Previewer.Tests.csproj
dotnet test tests/AvaloniaDevTool.Cli.Tests/AvaloniaDevTool.Cli.Tests.csproj --filter FullyQualifiedName~PreviewAttachTests
```

前者覆盖源文件加载、实际渲染、设计尺寸、资源、绑定、防抖、删除／恢复、版本拒绝与 Agent；后者通过真实 stdio MCP 验证已构建项目预览和进程生命周期。可见桌面仍应另做交互／视觉验收。


