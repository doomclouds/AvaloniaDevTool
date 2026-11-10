# 官方 XAML 文件附加

`attach-to-file` 使用官方必填参数 `xamlFilePath`、`xamlProjectPath`；`build` 为可选布尔值，默认 `false`。不要求调用方提供程序集路径。成功后 `tree`、`search`、`props`、`screenshot` 等工具操作同一预览进程。

```json
{
  "name": "attach-to-file",
  "arguments": {
    "xamlFilePath": "C:/Projects/示例 项目/MainWindow.axaml",
    "xamlProjectPath": "C:/Projects/示例 项目/示例.csproj",
    "build": true
  }
}
```

两个路径必须为已存在的绝对文件路径；源文件须位于所选 C# 项目目录下。中文和空格通过进程参数列表传递，不拼接 shell 命令。

## 项目与构建

工具调用本机 .NET SDK 的 `dotnet msbuild -getProperty`，只评估项目与其导入的配置，不执行构建 target。产物位置使用 MSBuild 计算的 `TargetPath`，因此遵循项目的 `AssemblyName`、`OutputPath`、`Configuration` 等配置；没有指定配置时使用项目自身评估出的默认值，常见 SDK 项目为 Debug。不会扫描 bin 目录并猜测“最新”DLL，也无法推断未写入项目配置的某一次外部 `--artifacts-path` 构建参数。

单目标使用项目 TFM。多目标按已评估的框架标识、版本和平台选择：优先可被当前 .NET 10 宿主加载的最高 `.NETCoreApp` 版本，其次兼容 `.NETStandard`；相同版本优先不带平台的框架。Windows 宿主可选择 Windows TFM；Android、iOS、Browser、.NET Framework 和比宿主更新的 .NET TFM 会明确拒绝。该规则是本实现的选择策略；官方原版的选择规则尚未实测。

- 省略 `build` 或传 `false`：不主动构建。选定 `TargetPath` 缺失时返回 `project_not_built`，提示构建项目或使用 `build=true`。
- `build=true`：先实际执行 `dotnet build 项目 --framework 选定TFM`，成功且产物存在后才启动预览。编译或还原失败返回 `project_build_failed`，包含有界构建输出；不会报告附加成功。已有连接在构建失败前保持原状。
- 项目评估失败、没有兼容 TFM、缺少 .NET SDK 都产生可见错误。评估限时 30 秒；构建由请求取消控制，不另设任意构建时限。

## 真实预览与热更新

预览在独立进程中初始化所选程序集的 `BuildAvaloniaApp()` 或唯一 `Application` 类型，执行 App 初始化与资源加载，再按项目程序集加载源 XAML。它设置真实设计模式，保留 `x:Class`，使用项目类型、自定义控件、资源和绑定配置；不调用目标 `Main`。没有可识别 AppBuilder / Application 的控件库、非常规 DI 启动结构等会明确失败，不能声称已加载其项目 App 上下文。宿主固定 Avalonia 12.1.4、.NET 10，引用版本不兼容会报错。

连接保持时，文件保存经防抖后重新加载所选 XAML，不重新构建程序集。正常更新后必须重新 `tree` / `search` 获取节点；旧节点不可继续当作新文档节点使用。保存临时不完整或非法 XAML 时保留最后成功视图并显示 `PreviewErrorText`；修正保存后恢复。初次非法 XAML 直接附加失败。

文件监视只重新加载所选源文件，引用资源来自构建产物；程序集和依赖变化需要重新构建并重新附加。外部链接源文件、NativeAOT / 裁剪产物、仅桌面 lifetime 分支初始化的状态不在当前支持范围。详细设计期加载机制见 [原预览说明](../../preview-attach.md)。

默认显示预览窗口；无桌面自动化可在宿主环境设置 `AVDT_PREVIEW_HEADLESS=true`，使用实际 Skia 绘制。这个环境设置不增加官方工具参数。

## 进程所有权

附加成功以首次 XAML 加载完成、Agent 就绪及 PID / 管道握手校验为准。启动失败、请求取消和 30 秒预览启动超时都会回收候选进程。`detach`、文件切换、附加其他目标、正常退出会停止自建预览；外部附加的应用不获得进程所有权。

父 MCP 进程异常退出时，预览控制管道断开会结束预览，包括启动构造阻塞的情况。Windows 下 MSBuild / build 使用 `KILL_ON_JOB_CLOSE` 作业对象，父进程异常退出也会停止其构建进程树；其他系统的构建取消会终止进程树，但当前未实现构建阶段的父进程异常退出保护。构建关闭 MSBuild node reuse / CLI build server / C# shared compilation，避免为本次调用留下可复用服务进程。

## 验证入口

`tests/AvaloniaDevTool.Cli.Tests/OfficialPreviewTests.cs` 启动 production CLI 的真实 stdio MCP，在独立临时目录建立真实 fixture 项目并构建，覆盖 FILE01–FILE05：官方参数/schema、中文空格绝对路径、默认不构建、缺产物、实际构建成功和失败、多 TFM 选择、真实 App 资源、自定义控件、`x:Class`、保存热更新、非法 XAML 恢复、节点失效、文件切换、取消与 detach / 父进程退出后的 PID 清理。原版返回 DTO、默认 TFM 选择和错误文字尚未获得真实 `tools/call` 对照，不能据本地测试宣称字节级兼容。

2026-10-25 本地验证：`OfficialPreviewTests` 的 8 个方法均已通过（完整运行 7 项通过；整体预览截图检查调整后对应方法单独重跑通过），`AvaloniaDevTool.Previewer.Tests` 的 15 项回归通过，构建零警告零错误。截图验证的是实际 `FixtureView` 预览根的 PNG：有效尺寸、多种像素颜色、源文本热更后内容变化；不依赖单个 TextBlock 的抗锯齿颜色数量。

以下方法位于 `AvaloniaDevTool.Cli.Tests.OfficialPreviewTests`：

| 条目 | 直接证据方法与断言 |
| --- | --- |
| FILE01 | `Official_paths_default_false_real_project_context_live_saves_and_detach_work_over_stdio`：tools/list 仅三个字段、两个官方必填名；返回规范化中文空格文件 / 项目 / TargetPath 与所选项目一致 |
| FILE02 | 同上：省略 build 后产物时间不变，CoreCompile marker 不存在；`Missing_build_compile_failure_invalid_xaml_and_invalid_paths_never_create_an_attachment`：显式 false 缺产物报错且不产生构建 marker |
| FILE03 | `Build_true_creates_the_selected_multitarget_output_before_attaching_and_can_switch_files`：实际生成 DLL / marker，选择 net10.0 而非同版本 Windows TFM；`Missing_build_compile_failure_invalid_xaml_and_invalid_paths_never_create_an_attachment`：真实 C# 编译失败，即使旧 DLL 存在也不附加 |
| FILE04 | 第一个方法：没有用户预先运行的 app，真实 App 资源、classful 源文件、自定义控件和引用库控件都可查询；fixture 的 Main 抛异常而预览成功，证明未执行 Main |
| FILE05 | 第一个方法：保存后重取节点，完整预览 PNG 内容变化；旧节点报错；非法保存保留原视图和节点，修正后恢复，无重新构建 |

生命周期另由 `Official_startup_cancellation_stops_the_candidate_and_leaves_no_attachment`、`Cancelling_a_file_switch_keeps_the_previous_preview_and_releases_only_the_candidate`、`Abrupt_stdio_owner_exit_stops_a_preview_blocked_in_the_real_project_builder`、`Windows_abrupt_owner_exit_kills_an_in_progress_build_and_its_owned_descendant`、`Windows_build_request_cancellation_stops_the_build_and_its_owned_descendant` 验证。测试结束后按本次临时项目与 artifact 命令行检查，无预览 / 构建进程残留；没有终止用户既有进程。
