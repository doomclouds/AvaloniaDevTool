using System.Collections;

namespace AvaloniaDevTool.Sample;

public sealed partial class SampleViewModel
{
    // Business-shaped sample data for the read-only runtime browser.
    public RuntimeDemoData RuntimeDemo { get; } = new();
}

public sealed class RuntimeDemoData
{
    public string Name => "对象浏览演示";
    public RuntimeDemoData Self => this;
    public RuntimeDemoChild Child { get; }
    public IList Items { get; }
    public IDictionary Map { get; }
    public string LongPreview => new('文', 2_100);
    public string ThrowingGetter => throw new InvalidOperationException("演示 getter 单项失败，其他成员仍可读取。");
    public string? MissingValue => null;
    public readonly string PublicField = "公共字段只读展示";

    public RuntimeDemoData()
    {
        Child = new RuntimeDemoChild(this);
        var items = new ArrayList { null, "列表文本", Child, this };
        for (var i = 4; i < 125; i++)
            items.Add($"条目 {i}");
        Items = items;
        var map = new Hashtable { ["child"] = Child, ["self"] = this };
        for (var i = 0; i < 120; i++)
            map[$"键 {i:000}"] = i;
        Map = map;
    }
}

public sealed class RuntimeDemoChild(RuntimeDemoData parent)
{
    public string Name => "嵌套业务对象";
    public RuntimeDemoData Parent { get; } = parent;
}
