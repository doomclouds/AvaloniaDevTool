# 桌面自动化验收样例

独立 .NET 10 / Avalonia 12.1.4 ClassicDesktopLifetime 应用，始终附加本项目 `DiagnosticsAgent`，不启动官方开发工具。Agent 使用真实桌面 Lifetime 枚举窗口，退出时释放连接；没有自定义 `TopLevelProvider` 或替换平台 `StorageProvider`。

## 构建与启动

建议把构建产物、测试文件、结果 JSON 都放在系统临时目录。以下命令在仓库根目录执行；具有成功依赖的命令请分次运行。

```powershell
$buildRoot = Join-Path $env:TEMP 'AvaloniaDevTool-automation-sample-artifacts'
dotnet build samples\AvaloniaDevTool.AutomationSample\AvaloniaDevTool.AutomationSample.csproj -c Release --artifacts-path $buildRoot
```

```powershell
$testRoot = Join-Path $env:TEMP ('AvaloniaDevTool-acceptance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$sampleExe = Join-Path $buildRoot 'bin\AvaloniaDevTool.AutomationSample\release\AvaloniaDevTool.AutomationSample.exe'
$sampleArgs = @('--pipe', ('avalonia-acceptance-' + [guid]::NewGuid().ToString('N')), '--test-root', $testRoot, '--ready-file', (Join-Path $testRoot 'ready.txt'), '--result-file', (Join-Path $testRoot 'result.json'))
Start-Process -FilePath $sampleExe -ArgumentList $sampleArgs -WindowStyle Hidden -PassThru
```

`--pipe` 默认为包含进程 ID 的唯一名称。`--test-root` 只决定真实 OS 选择器的起始目录，不代替实际选择，不创建测试文件。`--ready-file` 可选，在主窗口打开后写入 PID 和管道名。`--result-file` 可选，每次文件操作完成、取消或失败后，通过同目录临时文件替换结果 JSON。测试驱动负责显示、激活和关闭这个进程的窗口，操作范围应限于本示例及其拥有的系统弹窗。

## 控件与可观察状态

所有下表标识同时是控件 `Name` 和 `AutomationId`。主窗口标识为 `AutomationMainWindow`，标题为 `AvaloniaDevTool automation acceptance`。初始页是 `ControlsTab`。

| 场景 | 控件标识 | 预期状态 |
| --- | --- | --- |
| 文本与键盘 | `InputTextBox`, `CommitInputButton` | `InputStatus`: `Committed: <text>`；Enter 为 `Enter: <text>` |
| 点击 | `CounterButton` | `InteractionStatus`: `Clicks=1`，后续递增 |
| 勾选/切换/单选 | `OptionCheckBox`, `OptionToggle`, `ModeARadio`, `ModeBRadio` | `InteractionStatus`: `Option=True`, `Toggle=True`, `Mode=B` 等 |
| ComboBox | `ChoiceComboBox`, `ComboAlphaItem`, `ComboBetaItem`, `ComboGammaItem` | `SelectionStatus` 的 `Combo=Beta` 等 |
| ListBox | `ChoiceListBox`, `ListOneItem`, `ListTwoItem`, `ListThreeItem` | `SelectionStatus` 的 `List=Two` 等 |
| Tab | `MainTabs`, `ControlsTab`, `FilesTab`, `ScrollTab` | `SelectionStatus` 的 `Tab=Files` 等 |
| Slider | `ValueSlider` | `InteractionStatus`: `Slider=<value>` |
| 菜单/嵌套菜单 | `ActionsMenu`, `MenuActionItem`, `MenuCheckItem`, `NestedMenu`, `NestedMenuActionItem` | `MenuStatus`: `Menu action invoked`, `Menu checked=True`, `Nested menu action invoked` |
| 右键菜单 | `ContextMenuTarget`, `AcceptanceContextMenu`, `ContextMenuActionItem` | `MenuStatus`: `Context menu action invoked` |
| Popup | `OpenPopupButton`, `AcceptancePopup`, `PopupActionButton` | `PopupStatus`: `Popup open`，点击后 `Popup action invoked` 并关闭 |
| Flyout | `OpenFlyoutButton`, `FlyoutActionButton` | `FlyoutStatus`: `Flyout open`, `Flyout action invoked` |
| 第二窗口 | `OpenSecondaryButton`, `SecondaryWindow`, `SecondaryInput`, `SecondaryActionButton`, `CloseSecondaryButton` | 窗口标题 `Automation secondary window`；`WindowStatus`/`SecondaryStatus`: `Applied 1: <text>`；关闭为 `Secondary closed` |
| 模态窗口 | `OpenDialogButton`, `ModalDialog`, `DialogInput`, `DialogAcceptButton`, `DialogCancelButton` | 标题 `Automation modal dialog`；打开时禁用 owner，`DialogStatus`: `Dialog accepted: <text>` 或 `Dialog cancelled` |
| 滚动 | `ScrollTab`, `LongScrollViewer`, `ScrollItem01` 至 `ScrollItem60` | `ScrollStatus` 含偏移；点击底行后 `Clicked row=60` |
| 拖动 | `DragCanvas`, `DragHandle`, `DropTarget`, `ResetDragButton` | `DragStatus`: `Dragging`，释放后 `Dropped=True; x=<value>; y=<value>`；将蓝色块中心移入绿色区域才成功 |

## 真实文件选择与读写

先选择 `FilesTab`。本示例不会把路径输入框伪装成 OS 选择器。选择器选中后才执行读写，文件操作期间编辑器只读，避免等待 OS 弹窗时编辑导致竞态。

| 按钮 | OS 选择器标题 | 完成状态 |
| --- | --- | --- |
| `OpenSingleButton` | `Automation sample - Open single` | `FileStatus`: `Opened: 1 file; Characters=...; Path=...` |
| `OpenMultipleButton` | `Automation sample - Open multiple` | `FileStatus`: `Opened: N files; Characters=...` |
| `OpenFolderButton` | `Automation sample - Open folder` | `FileStatus`: `Selected folder: <path>` |
| `SaveAsButton` | `Automation sample - Save UTF-8 document` | `FileStatus`: `Saved: Characters=...; Bytes=...; Path=...` |

`DocumentEditor` 显示真实读取的 UTF-8 内容并允许修改；`DocumentPath` 显示当前文档路径；`SelectedFiles` 列出实际打开的文件；`FolderPath` 显示实际选中的文件夹。多选只有在所有文件都读取成功后更新文档，按选择器返回顺序用 `\n--- next file ---\n` 拼接。UTF-8 BOM 会被去除，非法 UTF-8 在 `FileStatus` 中明确报错。目录选择不改变文档内容。

保存使用真实 `SaveFilePickerAsync`，明确设置 `ShowOverwritePrompt = true`，覆盖确认由 OS 显示。确认选中后通过 `OpenWriteAsync` 写入 UTF-8（无 BOM），显式 `SetLength` 以删除旧文件尾部，关闭流后才报告成功。流不可 seek 时明确失败，不宣称保存成功。保存对 I/O 错误没有事务回滚保证；错误直接显示在 UI。

取消任何选择器时显示 `Cancelled: Open single` / `Open multiple` / `Open folder` / `Save as`，文档内容、文档路径、之前的选中列表和目录保持不变，保存取消不会打开写入流。取消 OS 覆盖确认通常会回到“另存为”弹窗，此时继续取消该弹窗，才会结束本次 `SaveFilePickerAsync`。

结果 JSON 字段：`sequence`（递增）、`action`（`openSingle`/`openMultiple`/`openFolder`/`saveAs`）、`cancelled`、`selectedPaths`（本次选择）、`documentPath`、`content`（操作后文档）、`byteLength`（该文档 UTF-8 字节数）、`sha256`（该文档 UTF-8 SHA-256）、`status`、`error`、`timestampUtc`。取消时 `selectedPaths` 为空，仍输出原文档和 hash，便于核对“未改动”。JSON 的 hash 描述编辑器内容，验收驱动还应独立读取实际输出文件核对字节和 hash。

## 建议真实验收步骤

1. 在唯一临时目录中准备两个包含中文和不同文本的 UTF-8 文件，以及一个比编辑内容更长的待覆盖文件，记录原始 hash。
2. 用真实 OS 单选打开第一个文件，核对编辑器文本与源文件；单选取消，核对前后内容与路径不变。
3. 用真实 OS 多选两个文件，核对实际选中数量、路径和拼接内容；再取消一次并核对文档未变。
4. 选择一个真实文件夹，核对 `FolderPath`；取消选择，核对文件夹和文档状态未变。
5. 修改 `DocumentEditor`，另存为新路径。独立读取文件，核对 UTF-8 文本与 JSON hash；重新用 Open 打开该文件核对 UI。
6. 另存为比编辑内容更长的已有文件，操作 OS 的覆盖确认“否”并取消选择器，核对原文件 hash 和文档未变。再操作“是”，核对字节内容、长度和尾部截断；重新打开核对 UI。
7. 在真实窗口中操作其余控件。模态窗口存在时验证 owner 交互受到阻止，关闭后可继续操作。第二窗口关闭后应从桌面枚举消失。
8. 关闭主窗口，确认进程退出、Agent 管道释放。不要操作不属于此示例进程的窗口。
