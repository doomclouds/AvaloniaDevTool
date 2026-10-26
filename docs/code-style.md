# 代码格式与 ReSharper

解决方案使用根目录的 `AvaloniaDevTool.slnx.DotSettings` 作为团队共享格式配置，Rider/ReSharper 打开解决方案时加载。`dotnet-tools.json` 将 ReSharper CLI 固定为 **2026.2.3.1**，终端执行与 IDE 使用同一份规则。

配置显式保存当前版本全部非过时的 C#、通用、XML 格式字段，共 **413 项**：C# 356 项、通用 9 项、XML 48 项。键名、字段类型及枚举值已逐项对照工具程序集验证。配置只包含适用于本项目的格式设置；个人 IDE、快捷键和检查严重程度不属于团队格式规则。

统一约定为 UTF-8、LF、末尾换行、空格缩进、C#/XML 缩进 4、JSON 缩进 2、120 列换行参考线、C# Allman 大括号，以及长参数列表按项换行。`.editorconfig` 提供文件级基础约定；相同设置以 EditorConfig 为先，二者保持一致。

## 一条命令执行

从仓库根目录运行：

```powershell
pwsh -NoProfile -File ./tools/Format-Code.ps1 -IncludeAuxiliary
```

只检查格式，并在发现差异时返回失败：

```powershell
pwsh -NoProfile -File ./tools/Format-Code.ps1 -Check -IncludeAuxiliary
```

不带 `-IncludeAuxiliary` 时仅执行 ReSharper。首次运行自动执行 `dotnet tool restore`；已恢复工具后可传 `-NoRestore`。完整命令还使用固定版本的辅助格式器，具体版本、依赖与配置见 `tools/Format-Auxiliary.ps1`。

ReSharper 入口需要 .NET 10 SDK、PowerShell 7 和 Git。辅助入口另需 Node.js/npm、uv 及 PowerShell 的 PSResourceGet，工具在首次执行时下载到独立目录，不作为产品运行时依赖。

ReSharper 显式使用 **`Built-in: Reformat Code`**，关闭机器及个人设置层，再加载团队 DotSettings。命令在文件模式下运行，无需构建整个解决方案。该 profile 只调整格式，保留命名、using、namespace 形式、成员顺序及程序逻辑；CLI 默认的 Full Cleanup 不用于本命令。参见 [JetBrains CleanupCode 说明](https://www.jetbrains.com/help/resharper/CleanupCode.html)。

## 范围与文件类型

ReSharper 处理 Git 工作区内的 C#、项目与 XML 文件，包括 `src`、`samples`、`tests`。脚本使用存在的具体文件路径，分批避免 Windows 命令行长度限制；排除 `bin`、`obj`、`artifacts` 及生成代码。

当前 CLI 文件模式会跳过 AXAML/XAML。脚本为 AXAML/XAML、SVG 和 SLNX 建立临时 XML 副本，使用同一个 ReSharper XML 格式器处理后写回。此适配只改变工具识别的后缀。首轮格式化已对 AXAML/XML 的元素、属性和显著文本值进行等价检查。

Python、PowerShell、HTML 与 JSON 由辅助格式化脚本中的专用工具处理。历史测试记录、Markdown、许可、截图、二进制和文本测试资产不参与格式化。格式化缓存和临时副本位于独立的 `artifacts/resharper/format-*`，命令结束时清理。

## 修改后的验证

规则调整后，先执行格式化，再执行 `-Check` 确认幂等；涉及编译输入时使用现有解决方案构建。对源码进行字符串替换的预览测试也需要通过，避免格式调整破坏测试夹具约定。

本轮两个长嵌套三元表达式需要 ReSharper 额外一轮整理换行，已处理并通过最终全量 `-Check`。首次引入或升级格式器时，以检查结果确认稳定状态。

既有架构验证 JSON 保留其当时的源码哈希，不回写成格式化后的哈希。格式化是单独的变更批次，验证结果记录在 `docs/architecture/formatting-2026-10-26.json`。
