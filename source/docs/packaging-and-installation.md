# 单文件打包与版本安装

当前插件目录为仓库 `plugins/avalonia-devtool/`，完整源码在 `source/`。marketplace 仅安装插件子目录。0.1.6 新增独立 MCP 管理与分发脚本，继续使用 EXE 0.1.4；插件版本与程序版本分开维护。0.1.5 的安装与宿主验证见 [验证记录](release-0.1.5.md)，下文的 0.1.4 与 0.1.0 路径和结果按各自历史版本保留。

## 0.1.6 独立 MCP 分发

在仓库根运行：

```powershell
pwsh -NoProfile -File source/build/Build-Distribution.ps1
```

该脚本读取插件版本和 EXE 的 `--bundle-info`，执行 `--bundle-verify`，同步 `Install-AvaloniaDevTool.ps1`、`Manage-CodexMcp.ps1`、`Codex-Mcp.ps1` 到插件 `scripts/`，再生成 `artifacts/distributions/0.1.6/avalonia-devtool-0.1.6-win-x64.zip` 和对应 `.sha256`。只同步插件脚本时使用 `-SyncOnly`；`-PackagePath`、`-OutputDirectory` 可指定实际程序和输出目录。它不重新编译 EXE，也不注册或更新用户 MCP。

独立 ZIP 根目录包含 EXE、上述三个脚本及 `README.md`。插件包新增同样的三个脚本，其他入口不变，共九个文件。两个分发渠道复用同一份管理脚本源码，`source/tools/` 是维护入口；发布前重新运行打包脚本，避免插件副本与源码漂移。脚本校验复制结果和 ZIP 的 SHA-256，不覆盖已生成的同版本 ZIP。

安装、更新、回退、卸载与状态查询统一从 `Manage-CodexMcp.ps1` 执行：

```powershell
pwsh -NoProfile -File source/tools/Manage-CodexMcp.ps1 -Action Install
pwsh -NoProfile -File source/tools/Manage-CodexMcp.ps1 -Action Status
pwsh -NoProfile -File source/tools/Manage-CodexMcp.ps1 -Action Update -PackagePath ./new-release/AvaloniaDevTool.exe
pwsh -NoProfile -File source/tools/Manage-CodexMcp.ps1 -Action Rollback -Version 0.1.4
pwsh -NoProfile -File source/tools/Manage-CodexMcp.ps1 -Action Uninstall
```

管理器从真实内嵌清单读取程序版本及 RID，执行载荷校验，再复用下述安装器维护版本目录与固定命令。普通 MCP 注册使用固定命令的绝对路径，不依赖用户 PATH，也不指向 Codex 插件安装缓存。若插件已安装，保留技能并单独停用其 MCP，避免相同名称的服务重复加载。

升级使用新版分发包中的脚本和程序，或显式提供 `-PackagePath`；没有后台下载或自动替换。`-ExpectedSha256` 用于核对可信来源提供的 EXE 摘要。历史程序版本保留，回退重新选择历史文件。正在运行的 MCP 锁住固定 EXE 时，切换失败应保留原入口，先关闭对应连接后重试。

卸载默认仅移除普通 `avalonia` 注册，不重新启用插件内置 MCP，不删除程序、历史版本、运行缓存或录屏；`-RemoveFiles` 才清理管理器拥有的安装文件。`-InstallRoot` 和 `-BinDirectory` 改过后，维护与卸载须传入相同位置。`-CodexHome` 供隔离测试使用，只影响管理器启动的 Codex 子进程，不修改父进程的环境。

服务端返回完整目录、Codex 成功发现目录、当前聊天可调用完整目录是三个不同的检查点。普通 MCP 不计入本次发现的 Agent Plugin 共享声明预算，但仍应检查当前聊天的真实工具目录和权限；不能用独立客户端发现成功代替当前聊天的验证。本版验证范围见 [0.1.6 说明](release-0.1.6.md)。

独立的 `AvaloniaDevTool.Packaging` 工程负责生成对外分发的 `AvaloniaDevTool.exe`。它不合并目标应用、预览宿主和 Windows 原生 worker 的进程；它将各角色的完整自包含发布目录嵌入一个启动程序，运行时校验、解包并启动内部控制台。

以下保留单文件包装程序及 `0.1.0 / win-x64` 实物的历史验证记录；缓存专项、真实 MCP 运行和隔离安装的证据分别记录，不相互替代。

## 0.1.4 插件分发

当前完整工程位于仓库 `source/`，插件的 `mcp/` 仅保存当前版本 `AvaloniaDevTool.exe`。在仓库根运行 `pwsh -NoProfile -File source/build/Build-Bundle.ps1 -Version 0.1.4` 更新此 EXE；MCP 配置直接执行 EXE 的 `mcp` 角色。C# 文件入口与首次源码准备脚本已移除。

自包含载荷包含 console、Previewer、native worker、recording worker、热附加原生/托管引导及 Avalonia 12.1.1/12.1.4 的预编译 Agent。各角色使用隔离发布目录，普通启动、热附加、原生交互及录屏均不现场编译。用户 XAML 工程的评估和构建仍需要 SDK。

载荷清单 schema 2 通过 `sourcePath` 引用具有相同长度和 SHA-256 的原始条目，ZIP 仅保存一份相同内容，提取后复制恢复各角色独立目录。引用不允许指向其他引用、缺失条目、自身或不同内容；缓存仍逐文件校验。schema 1 载荷保持兼容。外层包装工程发布为 Native AOT 原生 EXE，避免再次携带包装程序自己的完整运行库；开发打包需要 MSVC 与 Windows SDK，使用端无需这些工具。内部 CLI、Agent 和各 worker 仍按原有托管方式运行。

下面的 0.1.0 产物大小、测试数量和安装记录为历史证据，当前发布验证见 [0.1.4 验证记录](release-0.1.4.md)。

## 构建独立分发文件

在仓库根目录使用 PowerShell 7 和 .NET 10 SDK。打包脚本按项目文件名发现控制台、Previewer 和 Windows worker，依次执行自包含发布，再发布独立包装工程。默认配置为 Release，默认 RID 为 `win-x64`；脚本还接受 `win-arm64`，本轮只验证 `win-x64`。

```powershell
pwsh -File src/Packaging/AvaloniaDevTool.Packaging/Build-Bundle.ps1 `
    -RuntimeIdentifier win-x64 -Version 0.1.0
```

发布目录默认为：

```text
artifacts/package/win-x64/
└─ AvaloniaDevTool.exe
```

`-Version` 传给各角色的发布和包装工程，并写入嵌入清单；省略时为当前未发布版本 `0.1.0`。安装时给安装脚本传入同一版本。安装脚本不会替调用方推导或校正这个版本。

可用 `-OutputDirectory` 改变交付目录。该目录应为空，或仅包含上一份 `AvaloniaDevTool.exe`；脚本拒绝覆盖混有其他内容的目录。`-SourceRoot` 显式指定仓库根；`-CliProjectPath`、`-PreviewerProjectPath`、`-NativeWorkerProjectPath` 可覆盖项目自动发现。包装工程本身不引用这些项目，由脚本串行发布和准备嵌入材料。

中间发布目录、压缩载荷和逐文件 SHA-256 清单位于 `artifacts/packaging/<RID>/<本次构建标识>/`。对外交付目录仅含 EXE；角色依赖、许可及第三方说明包含在内嵌发布材料中。自有托管程序集的调试符号内嵌，第三方随包原生符号保留在内嵌载荷。项目构建输出成功不等于已验证整个分发文件；交付前还须检查实际发布结果及运行路径。

## 包装程序的检查入口

```powershell
$package = '.\artifacts\package\win-x64\AvaloniaDevTool.exe'
& $package --bundle-info
& $package --bundle-verify
& $package --help
```

- `--bundle-info` 输出嵌入清单摘要：版本、RID、内部入口、载荷 SHA-256、文件数与解压字节数，不启动控制台。
- `--bundle-verify` 校验嵌入载荷并准备运行缓存，输出真实运行目录、SHA-256 和文件数，不启动控制台。
- 其余参数交给内部控制台，标准输入、标准输出、标准错误、工作目录及退出码保持原有语义。标准 MCP 输出不混入解包进度。

例如 `AvaloniaDevTool.exe mcp` 运行统一 MCP 入口；`AvaloniaDevTool.exe serve` 运行远端执行控制台。具体工具和执行位置切换语义见 [MCP 与命令行](mcp.md)。

## 缓存与进程生命周期

默认运行缓存位于：

```text
%LOCALAPPDATA%/AvaloniaDevTool/bundles/<RID>/<完整载荷 SHA-256>/
```

可通过 `AVDT_BUNDLE_CACHE` 选择其他缓存根。不同载荷使用不同目录，同一载荷的并发启动通过独占锁协调。每次启动校验缓存清单、文件数量、长度和 SHA-256；缺失、修改或新增文件会触发重新提取。提取直接在受锁保护的运行目录内进行，全部文件校验完成后最后写入完成清单，不依赖整棵目录重命名。中断留下的目录没有有效完成清单，下次取得锁后修复；完成前不启动内部控制台。已提交缓存临时无法读取时保留原内容并报告读取错误，不将临时文件锁当成损坏而删除缓存。

完整校验需要读取全部运行文件。本轮 Windows 实物曾测得一次热缓存校验约 43 秒；验证通过不代表快速启动。MCP 客户端应为首次准备和完整校验留足启动时间，当前配置示例使用 `startup_timeout_sec = 120`，见 [MCP 与命令行](mcp.md)。

内部控制台通过 `AVDT_RUNTIME_ROOT` 获取完整分发根，直接启动自包含 Previewer 和 native worker。包装程序等待控制台退出，返回其退出码。Windows Job Object 将控制台及其后代绑定到包装进程：包装进程异常结束时结束这些自建进程。正常 Ctrl+C 给控制台最多十秒清理自身会话，超过后终止其剩余进程树。外部附加的用户应用不因此获得进程所有权。

自包含解决的是运行时分发；它不包含用户应用工程和 .NET SDK。运行打包程序及其预览/native 角色不应依赖 PATH 中的 `dotnet`，但评估或构建用户 XAML 工程仍需要 .NET SDK。被测应用仍需接入 Agent。单文件分发会解包实际依赖文件，也会启动多个隔离进程。

## 安装版本与稳定命令

安装脚本在 Windows 上使用 PowerShell 7 运行，会验证 EXE 文件头和复制后的 SHA-256；可额外传入可信来源提供的 `-ExpectedSha256`。

```powershell
pwsh -File tools/Install-AvaloniaDevTool.ps1 `
    -PackagePath .\artifacts\package\win-x64\AvaloniaDevTool.exe `
    -Version 0.1.0
```

默认布局为：

```text
%USERPROFILE%/.local/share/avalonia-devtool/
├─ versions/
│  ├─ 0.1.0/AvaloniaDevTool.exe
│  └─ 其他已安装版本/
└─ current.json

%USERPROFILE%/.local/bin/
└─ AvaloniaDevTool.exe
```

版本目录保留各次分发文件，稳定命令是一份经校验的 EXE 副本。安装同一版本、同一内容可重复执行；同一版本已存在不同内容时明确失败，要求使用新版本。安装不会删除旧版本。

稳定命令成功切换后，脚本设置用户级 `AVALONIA_DEVTOOL_HOME`，指向安装根，并将 `.local/bin` 加入用户 PATH，避免重复条目。通过新进程运行安装脚本后，已有终端和 Codex 可能仍持有旧环境；新开终端或重启相应进程后使用 `AvaloniaDevTool.exe`。也可立即使用稳定命令的绝对路径。

若稳定 EXE 被锁定而无法替换，原入口保持；替换后后续步骤失败时尝试恢复原入口及已改动的用户环境。安装过程中生成的临时副本会清理，已存在版本保持。更新时先关闭当前 MCP/console，再用新版本重新安装即可，调用方命令路径不用随版本变化。

可先查看计划，或在隔离位置验证，不更新真实用户环境：

```powershell
pwsh -File tools/Install-AvaloniaDevTool.ps1 `
    -PackagePath .\artifacts\package\win-x64\AvaloniaDevTool.exe `
    -Version 0.1.0 -WhatIf

pwsh -File tools/Install-AvaloniaDevTool.ps1 `
    -PackagePath .\artifacts\package\win-x64\AvaloniaDevTool.exe `
    -Version 0.1.0 `
    -InstallRoot .\artifacts\isolated-install\store `
    -BinDirectory .\artifacts\isolated-install\bin `
    -SkipEnvironmentUpdate
```

## 验证边界

当前包装专项 16 项全部通过，覆盖八路并发提取、缓存篡改/新增文件修复、载荷及条目哈希不匹配、危险路径、额外条目、中断提取修复、Windows 无删除共享目录锁下提交、已提交缓存读取暂时受阻时保持原数据、真实 Job Object 子进程回收和短命令已退出时的成功退出码。TRX 位于 `artifacts/test-results/0.1.0/packaging-marker/packaging-marker.trx`。

安装脚本已有隔离测试，覆盖 WhatIf、首次安装、幂等重装、升级保留旧版、错误哈希拒绝、同版本不同内容拒绝和锁定入口失败回滚。隔离测试使用测试安装根并跳过真实用户环境更新，不表示用户 PATH、默认目录或实际 Codex 配置已被修改。

本轮实物交付信息：

| 项目 | 已核对结果 |
| --- | --- |
| 产物 | `artifacts/package/win-x64/AvaloniaDevTool.exe`，目录仅此文件 |
| 版本与大小 | `0.1.0 / win-x64`，214,722,986 字节 |
| EXE SHA-256 | `fcc906227a4ba558f167b64c34231e2596b6843f56b0cc72c6033f4c497ea860` |
| 内嵌载荷 SHA-256 | `2cd75a08fa56c2ede23275209072fbba8324b5355ccbb7c110debbeb5e999955` |
| 载荷 | 861 个文件；实际使用 `Infrastructure.*` 程序集，未包含旧 `AvaloniaDevTool.Client.dll`、`AvaloniaDevTool.NativeAutomation.dll` 或 `AvaloniaDevTool.Protocol.dll` |
| MCP 目录 | stdio 网关 89 项；HTTP 执行端 88 项且不含 `select-machine`，两侧执行目录一致 |

无 PATH `dotnet` 的独立缓存实测通过：真正冷准备约 5.19 秒、随后完整校验约 12.61 秒、help、stdio/HTTP 初始化和目录读取、包装进程异常退出后的自建子进程回收。最终字节再次核对 Info、已有缓存完整校验、stdio 与 HTTP：校验约 10.24 秒，两种 MCP 就绪分别约 3.89 秒、3.96 秒。此前约 43 秒的热校验记录仍作为启动成本证据保留。

最终 EXE 的隔离安装与幂等重装通过，源文件、稳定入口和版本副本 SHA-256 一致；用户 PATH 与 `AVALONIA_DEVTOOL_HOME` 未修改。清单、载荷审核、冷准备报告、最终字节检查及安装报告位于 `artifacts/bundle-release-0.1.0/`。实际 EXE 的两项图片 E2E 也已通过；具体范围与完整测试结果见 [测试说明](testing.md)。这些结果不扩大为 `win-arm64` 或其他机器、平台的运行验证。
