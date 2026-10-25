using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class OfficialResourceScopeTests
{
    [AvaloniaFact]
    public void Only_self_is_a_traversal_boundary_for_local_ancestors_application_and_nested_providers()
    {
        var application = Application.Current!;
        const string key = "OfficialResourceShadow";
        application.Resources[key] = "app";
        var child = new Border { Name = "Application.Resources" };
        child.Resources[key] = "child";
        child.Resources.MergedDictionaries.Add(new ResourceDictionary { ["MergedChild"] = 42 });
        child.Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary { ["DarkChild"] = 43 };
        var style = new Style(); style.Resources["SelfStyle"] = 44; child.Styles.Add(style);
        var parent = new Border { Child = child }; parent.Resources[key] = "parent";
        var objects = new WeakObjectRegistry();
        using var service = new ResourceInspectionService(application, objects);
        try
        {
            var all = service.GetTree(new(objects.GetId(child)));
            Assert.Contains(all.Roots, node => node.Scope == "Application");
            Assert.Equal(new[] { "child", "parent", "app" }, Values(all));
            var onlySelf = service.GetTree(new(objects.GetId(child), OnlySelf: true));
            Assert.DoesNotContain(onlySelf.Roots, node => node.Scope == "Application");
            Assert.Equal(new[] { "child" }, Values(onlySelf));
            var local = Assert.Single(onlySelf.Roots, node => node.Scope == "Element");
            var entries = service.QueryEntries(new(local.Id, IncludeNested: true));
            Assert.Contains(entries.Entries, entry => entry.StringKey == "MergedChild" && entry.Value == "42");
            Assert.Contains(entries.Entries, entry => entry.StringKey == "DarkChild" && entry.Theme == "Dark" && entry.Value == "43");
            Assert.Contains(onlySelf.Roots, node => node.Scope == "Style");
            var app = service.GetTree(new(OnlySelf: true));
            Assert.Equal(new[] { "app" }, Values(app));
        }
        finally { application.Resources.Remove(key); }

        string?[] Values(ResourceTreeSnapshot tree) => tree.Roots.Where(node => node.CanEnumerate)
            .SelectMany(node => service.QueryEntries(new(node.Id)).Entries)
            .Where(entry => entry.StringKey == key).Select(entry => entry.Value).ToArray();
    }
}
