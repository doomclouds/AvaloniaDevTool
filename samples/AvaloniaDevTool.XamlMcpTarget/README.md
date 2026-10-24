# XamlMcp 独立目标

此样例只引用公开 NuGet `XamlMcp.Avalonia 1.0.0-preview.3` 与 Avalonia 12.1.4，不引用本项目 Agent。用于验证可替换开源后端。

```powershell
dotnet run --project samples/AvaloniaDevTool.XamlMcpTarget
```

Debug 会接入上游 Agent，窗口和 stdout 显示 PID，可用 `pid:<PID>` 连接。Release 仅在显式提供 `--enable-inspection` 时通过上游 AppBuilder 开启检查。

自动化可以使用 `--headless --enable-inspection --discovery-dir PATH --ready-file PATH`。headless 使用真实 Skia；ready file 只包含 PID、endpoint 和版本，不包含认证 token 或 pipe name。文件必须尚不存在。

此样例与内置 Native Agent 的测试工作台相互独立。上游 package 的许可与归属见项目文档。

