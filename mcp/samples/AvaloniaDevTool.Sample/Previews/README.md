# 源文件预览示例

`PreviewCard.axaml` 是没有 `x:Class` 的独立 UserControl，包含资源、局部样式、绑定和命名控件。

在仓库根目录使用绝对路径启动宿主：

```powershell
$xaml = (Resolve-Path samples/AvaloniaDevTool.Sample/Previews/PreviewCard.axaml).Path
dotnet run --project src/AvaloniaDevTool.Previewer -- --xaml $xaml --pipe avalonia-devtool-preview
```

再让诊断客户端连接 `avalonia-devtool-preview`，即可查询和修改预览中的控件。修改 AXAML 并保存后会重新加载；临时写入错误时保留上一次成功预览。详细边界见 [预览器说明](../../../docs/previewer.md)。
