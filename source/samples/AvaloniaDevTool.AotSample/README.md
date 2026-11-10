# Native AOT 消费样例

这是独立的最小 Avalonia 12.1.4 消费程序，直接引用本项目 Agent，使用 C# 创建窗口、样式和控件，不使用运行时 XAML 或反射绑定。它专门验证 Release 中显式附加 Agent 的 Native AOT 行为；普通产品不应照搬这一启用策略。

`--pipe NAME` 指定诊断管道，`--ready-file PATH` 写入 PID、管道以及真实的 `RuntimeFeature.IsDynamicCodeSupported` / `IsDynamicCodeCompiled` 值。实际连接和断言由 `tests/AvaloniaDevTool.AotSmoke` 中的真实 `DiagnosticsClient` 完成。

运行方法、发布警告和已验证范围见 [Native AOT 验证说明](../../docs/native-aot.md)。目前已经验证最小消费程序的核心功能；整个 Agent 的无警告 AOT 兼容仍未完成。
