# 原版 MCP 差分目标准备记录

准备日期：**2026-10-25（Asia/Shanghai）**。目标是独立、临时的 .NET 10 / Avalonia 12.1.4 / 官方 DiagnosticsSupport 2.2.3 桌面 app，供后续正常原版 MCP 对照。源码与工件均在系统 Temp，未添加到本仓库解决方案或生产/测试项目，也没有复用自研诊断 Agent。该目标只使用公开 Avalonia 与 DiagnosticsSupport API，没有复制原版实现。

## 位置与冻结身份

根目录：`C:/Users/10062/AppData/Local/Temp/avdt-official-differential-fixture-20261025`。SDK 为本机 `10.0.400`；项目为 `OfficialDifferentialFixture.csproj`，直接依赖 Avalonia、Avalonia.Desktop、Avalonia.Themes.Fluent **12.1.4** 与 AvaloniaUI.DiagnosticsSupport **2.2.3**，已生成 `packages.lock.json`。

`fixture-manifest.json` 记录全部 11 份源码/锁文件与工件哈希，SHA-256 为 `bdf64aaa4b6053de09fbbbaea12bb6b25aeed519d3f167e874465bc1e0107da7`。该清单生成后已冻结源码；后续运行输出写入独立子目录。

| 工件（相对临时根目录） | SHA-256 |
| --- | --- |
| `bin/Debug/net10.0/OfficialDifferentialFixture.exe` | `0ec72801f197c10b48dbed03ff618ef49f350e0f2faa8521421d7e04738d6e7a` |
| `bin/Debug/net10.0/OfficialDifferentialFixture.dll` | `a53c3a6afcb8855bf85c49a45c01e409b6b413b8e3d06f9bd8a9b56c117751ff` |
| `bin/Debug/net10.0/AvaloniaUI.DiagnosticsSupport.Avalonia.dll` | `6ce4138e84b9ed5fbd3aa446ef292e023e92ecd0a2ae18e261293c47ac27a6d2` |
| `smoke-final/fixture-observation.json` | `23aa972298d38753bf4621b162ff9e3c94f7861f2cdd7d1d9ede985c46e370eb` |
| `smoke-final/fixture-window.png` | `658129c134bac8f2aadcaa0e9cbeb300fc042af5577cdf75af44f90fe502a535` |

EXE 是 .NET apphost；应用程序集和实际解析到的桥 DLL 另外列出，不能只用 apphost 哈希标识源码版本。Temp 被清理后不保证工件继续存在。

## API 依据与连接控制

先读取官方包 README、Avalonia 12.1.4 XML API，并核对 2.2.3 的公开 `DeveloperToolsOptions`、`WithDeveloperTools`、`DeveloperToolsRunner.NoOp` API；当前 [官方安装说明](https://docs.avaloniaui.net/tools/developer-tools/installation) 与 [选项说明](https://docs.avaloniaui.net/tools/developer-tools/options) 于准备当日读取。Context7 检索连接失败，改查官方文档和本机实际 DLL；没有据失败结果推断 API 不存在。

AppBuilder 调用 `WithDeveloperTools`，明确设置 `ConnectOnStartup=false`、`AutoConnectFromDesignMode=false`、`Runner=DeveloperToolsRunner.NoOp`。默认 `EnableDiscovery=false`；传 `--allow-discovery` 才开启发现。Program 和启动脚本在自有进程中设置 `AVALONIA_DEVTOOLS_AUTO_CONNECT=false`、`AVALONIA_DEVTOOLS_RUNNER=noop`，防止继承环境覆盖这两项选择。NoOp 只防止启动工具，本身不能替代自动连接与发现开关。

默认烟测没有启动连接路径；开启发现后，可以接受正常工具发出的连接请求。没有自动操作登录、启动 avdt、按 F12 或调用官方 MCP 的代码。[官方文档](https://docs.avaloniaui.net/tools/developer-tools/options) 说明 NoOp 假定工具已由用户启动，桌面启动连接默认关闭。

## 样本与已做验证

| 样本 | 可用于对照的区别 | 本轮自有 app 观察 |
| --- | --- | --- |
| `AncestorLeaf` | Logical 父链至少有 `AncestorLevelTwo`、`AncestorLevelOne`，上面还有 OuterPanel、ScrollViewer、Window | 真实树快照确认两级父节点存在；该快照是自有 Avalonia API 结果，不是官方 ancestors 输出 |
| `TemplatedProbe: ProbeHost` | 自定义 ContentControl 模板包含 TemplateBorder、TemplateStack、TemplateTitle、PART_ContentPresenter；槽内另有 LogicalContentBorder、LogicalContentPanel、LogicalContentLeaf | 4 个模板名称出现在 Visual；TemplateTitle 不在 Logical；LogicalContentLeaf 同时可达。该模板和内容槽可用来比较 Merged 的 TemplateRoot/slot 结构，不能只比较节点集合 |
| `TemplatedProbe` 伪类 | 公开 `[PseudoClasses(":active", ":fixture")]`，初始 fixture=true、active=false；active 样式改变 Background，经 TemplateBinding 显示 | public control logic 切换 active 时 Classes 和背景都变化，随后恢复。pointerover 等框架状态可能随真实鼠标位置变化，不能当固定基线 |
| `BindingProbe.Text` | TwoWay 绑定 ViewModel.SourceText；ChangeSourceButton 更改源，SourceEcho 显示相同源 | 源变更传播到 TextBox，已恢复 bound-initial |
| `NullableProbe.Text` | 初始 local value 为 nullable-seed | public API 设置 null 成功，已恢复初值 |
| `UnsetProbe.Text` | 样式值 style-fallback，上面有 local-over-style | public ClearValue 后确实露出样式值，已恢复本地值；与绑定样本分开，避免混淆两种恢复语义 |
| `ClickCounterButton` | 简单可见点击计数，作为后续输入对照辅助 | 控件已加载；本轮未通过官方 input 调用 |

构建命令为临时目录内 `dotnet build OfficialDifferentialFixture.csproj --no-restore --nologo --verbosity minimal`，最后一次结果 **0 警告、0 错误**。构建输出含官方包的 Community telemetry 提示；本轮未更改订阅或遥测配置。

安全烟测使用 `run-fixture.ps1 -ExitAfterSeconds 5 -OutputDirectory <root>/smoke-final -Wait`，owned PID **32448** 正常退出，13 项 readiness 检查全部通过，截图已读取确认窗口完整。烟测前后原版 GUI 均仅 PID **21188**，没有关闭或操作其认证。截图与 JSON 是本目标通过公开 Avalonia API 自行产生，只证明样本就绪，不是官方 MCP 响应。

每次运行的初始化自检会短暂改动样本并恢复初值；**后续对照先等待该次输出 `fixture-observation.json` 出现且 ownedPid 匹配当前进程**，再附加和记录结果，避免与初始化自检重叠。

## 正常原版对照的启动方式

构建与烟测完成后，用户已授权正常原版运行对照。本轮以以下命令只开启目标发现，供根任务进行后续许可与工具验证：

```powershell
& 'C:\Users\10062\AppData\Local\Temp\avdt-official-differential-fixture-20261025\run-fixture.ps1' -AllowDiscovery -OutputDirectory 'C:\Users\10062\AppData\Local\Temp\avdt-official-differential-fixture-20261025\live-discovery'
```

返回 owned PID **37892**，显示名 **Official MCP Differential Fixture 20261025**。`live-discovery/fixture-observation.json` 于 `2026-10-25T08:40:01.3780999+08:00` 报告该 PID、discoveryEnabled=true 和全部 13 检查通过；该目标留给根任务继续，未由本次准备工作回收。进程号会过期，重新启动后以实际输出为准；结束时可正常关闭该窗口，或在核对 PID 和可执行路径后只停止该自有进程。

**仍未验证**：真实官方 attach 成功、许可状态、三树实际返回格式与默认值、ancestors 顺序/自身、pseudo-class nullable/省略/冒号、官方 set-prop 对绑定/null/unset 的处理、detach 广告与进程处置。本准备任务没有执行官方 `tools/call`，也没有将自有样本检查替代 [runtime-unknowns.md](runtime-unknowns.md) 中的未决结论。

后续根任务已做一次 [正常许可检查](official-permission-probe.md)，原版返回订阅等级不包含该工具权限，未附加。PID37892在核对实际exe路径后已回收，不应再把上述运行记录当作当前活跃进程；临时冻结工件可按相同命令重启。
