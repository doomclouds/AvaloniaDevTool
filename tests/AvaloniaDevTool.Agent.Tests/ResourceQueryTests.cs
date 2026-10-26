using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class ResourceQueryTests
{
    [AvaloniaFact]
    public void Nested_query_keeps_duplicate_keys_original_owners_themes_and_style_scopes()
    {
        var merged = new ResourceDictionary { ["Duplicate"] = 20 };
        var dark = new ResourceDictionary { ["Duplicate"] = 30 };
        var control = new Border
        {
            Name = "QueryScope", Resources = new ResourceDictionary
            {
                ["Duplicate"] = 10, MergedDictionaries = { merged }, ThemeDictionaries = { [ThemeVariant.Dark] = dark }
            }
        };
        var style = new Style();
        style.Resources["StyleMarker"] = "style";
        control.Styles.Add(style);
        var objects = new WeakObjectRegistry();
        using var service = new ResourceInspectionService(Application.Current!, objects);
        var tree = service.GetTree(new(objects.GetId(control)));
        var owner = Assert.Single(tree.Roots, node => node.Name.Contains("QueryScope", StringComparison.Ordinal));
        Assert.Contains(tree.Roots, node => node.Scope == "Style" && node.EntryCount == 1);
        var local = service.QueryEntries(new(owner.Id));
        Assert.Equal("10", Assert.Single(local.Entries).Value);
        var nested = service.QueryEntries(new(owner.Id, IncludeNested: true));
        Assert.Equal(3, nested.TotalCount);
        Assert.Equal(3, nested.Entries.Select(entry => entry.OwnerDictionaryId).Distinct().Count());
        Assert.Equal(3, nested.Entries.Select(entry => entry.ProviderPath).Distinct().Count());
        Assert.Contains(nested.Entries, entry => entry.Theme == "Dark" && entry.Value == "30");
        var mergedRow = Assert.Single(nested.Entries, entry => entry.Value == "20");
        service.Set(new(mergedRow.OwnerDictionaryId, mergedRow.StringKey!, "42"));
        Assert.Equal(42, merged["Duplicate"]);
        Assert.Equal(10, control.Resources["Duplicate"]);
        service.Reset(new(mergedRow.OwnerDictionaryId, mergedRow.StringKey!));
        Assert.Equal(20, merged["Duplicate"]);
        Assert.Equal("resource_not_found",
            Assert.Throws<RpcException>(() => service.Set(new(owner.Id, "StyleMarker", "new"))).Code);
    }

    [AvaloniaFact]
    public void Pagination_filter_sort_and_budgets_are_explicit_and_bounded()
    {
        var dictionary = new ResourceDictionary();
        for (var i = 0; i < 130; i++)
            dictionary["Key" + i.ToString("D3")] = i;
        dictionary["AString"] = "sample";
        var objects = new WeakObjectRegistry();
        using var service = new ResourceInspectionService(Application.Current!, objects);
        var id = objects.GetId(dictionary);
        var first = service.QueryEntries(new(id, KeyFilter: "key", TypeFilter: "Int32", Limit: 50));
        Assert.Equal(130, first.TotalCount);
        Assert.Equal(50, first.Entries.Count);
        Assert.Equal("Key000", first.Entries[0].Key);
        Assert.True(first.HasMore);
        Assert.False(first.Truncated);
        var last = service.QueryEntries(new(id, KeyFilter: "key", Offset: 100));
        Assert.Equal(30, last.Entries.Count);
        Assert.False(last.HasMore);
        var descending = service.QueryEntries(new(id, KeyFilter: "key", Descending: true, Limit: 1));
        Assert.Equal("Key129", Assert.Single(descending.Entries).Key);
        var typeSort = service.QueryEntries(new(id, Sort: ResourceQuerySort.Type, Descending: true, Limit: 1));
        Assert.Equal("AString", Assert.Single(typeSort.Entries).Key);
        var capped = service.QueryEntries(new(id, MaxEntries: 7));
        Assert.True(capped.Truncated);
        Assert.Equal(7, capped.EntriesVisited);
        Assert.Equal(7, capped.TotalCount);
        Assert.Contains(capped.Limitations, note => note.Contains("budget", StringComparison.Ordinal));
        foreach (var request in new[]
                 {
                     new ResourceEntriesQueryRequest(id, Offset: -1), new(id, Offset: 132),
                     new(id, Limit: 201), new(id, MaxNodes: 0), new(id, MaxEntries: 20001),
                     new(id, Sort: (ResourceQuerySort)50)
                 })
            Assert.Throws<RpcException>(() => service.QueryEntries(request));
        Assert.Empty(service.QueryEntries(new(id, Offset: 131)).Entries);
        var prefix = new string('A', 300);
        var longKeys = new ResourceDictionary { [prefix + "Z"] = 1, [prefix + "A"] = 2 };
        var orderedLongKeys = service.QueryEntries(new(objects.GetId(longKeys)));
        Assert.Equal(["2", "1"], orderedLongKeys.Entries.Select(entry => entry.Value));
        Assert.All(orderedLongKeys.Entries,
            entry =>
            {
                Assert.True(entry.KeyTruncated);
                Assert.False(entry.CanEdit);
            });
    }

    [AvaloniaFact]
    public void Nonstring_keys_use_weak_identity_and_read_never_calls_user_tostring()
    {
        var key = new DangerousText();
        var value = new DangerousText();
        var dictionary = new ResourceDictionary { [key] = value, ["Safe"] = new string('中', 5000) };
        var objects = new WeakObjectRegistry();
        using var service = new ResourceInspectionService(Application.Current!, objects);
        var id = objects.GetId(dictionary);
        var page = service.QueryEntries(new(id));
        var row = Assert.Single(page.Entries, entry => entry.StringKey is null);
        Assert.Equal(typeof(DangerousText).FullName, row.KeyType);
        Assert.False(row.CanEdit);
        Assert.NotNull(row.ValueObjectId);
        Assert.Equal(row.KeyObjectId,
            service.QueryEntries(new(id)).Entries.Single(entry => entry.StringKey is null).KeyObjectId);
        Assert.Equal(row.KeyObjectId, service.ReadEntry(new(id, row.KeyObjectId)).KeyObjectId);
        Assert.Contains(service.GetEntries(id), entry => entry.KeyType == typeof(DangerousText).FullName);
        Assert.Equal(0, key.Calls);
        Assert.Equal(0, value.Calls);
        var longValue = Assert.Single(page.Entries, entry => entry.Key == "Safe");
        Assert.True(longValue.ValueTruncated);
        Assert.False(longValue.CanEdit);
        Assert.InRange(longValue.Value!.Length, 1, 1025);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(page, ProtocolJson.Options).Length, 1, 16000);
        dictionary.Remove(key);
        Assert.Equal("resource_not_found",
            Assert.Throws<RpcException>(() => service.ReadEntry(new(id, row.KeyObjectId))).Code);
    }

    [AvaloniaFact]
    public void Shared_custom_and_cyclic_provider_graphs_are_visible_and_stop_at_budget()
    {
        var leaf = new SharedDictionary { ["Shared"] = "value" };
        var custom = new CustomProvider();
        var dictionary = new ResourceDictionary { MergedDictionaries = { leaf, leaf, custom } };
        var control = new Border { Name = "ProviderGraph", Resources = dictionary };
        var objects = new WeakObjectRegistry();
        using var service = new ResourceInspectionService(Application.Current!, objects);
        var tree = service.GetTree(new(objects.GetId(control)));
        var root = Assert.Single(tree.Roots, node => node.Name.Contains("ProviderGraph", StringComparison.Ordinal));
        Assert.True(root.Children[1].IsReference);
        Assert.False(root.Children[2].CanEnumerate);
        Assert.Contains(nameof(CustomProvider), root.Children[2].TypeName);
        var page = service.QueryEntries(new(root.Id, IncludeNested: true));
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Entries.Select(entry => entry.ProviderPath).Distinct().Count());
        Assert.Contains(page.Limitations, note => note.Contains("Custom", StringComparison.Ordinal));
        var limited = service.QueryEntries(new(root.Id, IncludeNested: true, MaxNodes: 1));
        Assert.True(limited.Truncated);
        Assert.Equal(1, limited.NodesVisited);
        Assert.Empty(limited.Entries);
        // A self-contained provider graph can contain cycles even though owned dictionaries reject them.
        var cyclic = new CyclicDictionary();
        var cycle = service.QueryEntries(new(objects.GetId(cyclic), IncludeNested: true));
        Assert.Equal(1, cycle.TotalCount);
        Assert.Contains(cycle.Limitations, note => note.Contains("cyclic", StringComparison.Ordinal));
        var cycleOwner = new Border { Name = "CycleOwner", Resources = cyclic };
        var cycleTree = service.GetTree(new(objects.GetId(cycleOwner)));
        Assert.True(Assert
            .Single(Assert.Single(cycleTree.Roots, node => node.Name.Contains("CycleOwner", StringComparison.Ordinal))
                .Children).IsCycle);
    }

    [AvaloniaFact]
    public void Inspection_metadata_and_ids_do_not_keep_unowned_dictionary_or_keys_alive()
    {
        var objects = new WeakObjectRegistry();
        using var service = new ResourceInspectionService(Application.Current!, objects);
        var (ownerId, keyId) = CreateTemporary(service, objects);
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(objects.TryGet(ownerId, out _));
        Assert.False(objects.TryGet(keyId, out _));
        Assert.Equal("object_expired", Assert.Throws<RpcException>(() => service.QueryEntries(new(ownerId))).Code);
    }

    [AvaloniaFact]
    public void Removed_local_key_is_not_recreated_by_reset_or_redirected_to_a_nested_dictionary()
    {
        var merged = new ResourceDictionary { ["Marker"] = 7 };
        var dictionary = new ResourceDictionary { ["Marker"] = 3, MergedDictionaries = { merged } };
        var objects = new WeakObjectRegistry();
        using var service = new ResourceInspectionService(Application.Current!, objects);
        var id = objects.GetId(dictionary);
        service.Set(new(id, "Marker", "5"));
        dictionary.Remove("Marker");
        Assert.Equal("resource_conflict", Assert.Throws<RpcException>(() => service.Reset(new(id, "Marker"))).Code);
        Assert.False(dictionary.ContainsKey("Marker"));
        Assert.Equal(7, merged["Marker"]);
        Assert.Equal("resource_not_found",
            Assert.Throws<RpcException>(() => service.Set(new(id, "Marker", "10"))).Code);
    }

    [AvaloniaFact]
    public void Large_unicode_tree_and_entry_pages_stay_below_the_json_frame_limit()
    {
        var dictionary = new ResourceDictionary { ["TreeMarker"] = 1 };
        for (var branch = 0; branch < 60; branch++)
        {
            var child = new ResourceDictionary();
            dictionary.MergedDictionaries.Add(child);
            for (var depth = 0; depth < 12; depth++)
            {
                var next = new ResourceDictionary();
                child.ThemeDictionaries[new ThemeVariant(new string('中', 250) + depth, ThemeVariant.Light)] = next;
                child = next;
            }
        }

        var owner = new Border { Name = "UnicodeResourceTree", Resources = dictionary };
        var objects = new WeakObjectRegistry();
        using var service = new ResourceInspectionService(Application.Current!, objects);
        var tree = service.GetTree(new(objects.GetId(owner), MaxNodes: 1024));
        Assert.True(tree.Truncated);
        Assert.Contains(tree.Limitations, note => note.Contains("text budget", StringComparison.Ordinal));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(tree, ProtocolJson.Options).Length < RpcPeer.MaximumFrameBytes);
        var entries = new ResourceDictionary();
        for (var index = 0; index < 200; index++)
            entries[new string('键', 250) + index] = new string('值', 1024);
        var page = service.QueryEntries(new(objects.GetId(entries), Limit: 200));
        Assert.Equal(200, page.Entries.Count);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, ProtocolJson.Options).Length < RpcPeer.MaximumFrameBytes);
    }

    [AvaloniaFact]
    public async Task Agent_client_resource_queries_round_trip_utf8_pages_and_original_owner_edits()
    {
        var merged = new ResourceDictionary { ["共享键"] = "原值" };
        var control = new Border
        {
            Name = "WireResources", Resources = new ResourceDictionary
                { ["本地键"] = "本地值", MergedDictionaries = { merged } }
        };
        var window = new Window { Content = control };
        window.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                    { PipeName = "resource-query-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            var rootNode = Assert.Single(await client.GetRootsAsync(TreeKind.Logical));
            var node = Assert.Single(await client.GetChildrenAsync(rootNode.Id, TreeKind.Logical),
                item => item.Name == "WireResources");
            var tree = await client.QueryResourceTreeAsync(new(node.Id));
            var root = Assert.Single(tree.Roots, item => item.Name.Contains("WireResources", StringComparison.Ordinal));
            var page = await client.QueryResourceEntriesAsync(new(root.Id, IncludeNested: true, Limit: 1));
            Assert.True(page.HasMore);
            var nested = await client.QueryResourceEntriesAsync(new(root.Id, IncludeNested: true, KeyFilter: "共享"));
            var row = Assert.Single(nested.Entries);
            Assert.Equal("原值", row.Value);
            Assert.NotEqual(root.Id, row.OwnerDictionaryId);
            await client.SetResourceAsync(row.OwnerDictionaryId, row.StringKey!, "新值");
            Assert.Equal("新值",
                (await client.ReadResourceEntryAsync(new(row.OwnerDictionaryId, row.KeyObjectId))).Value);
            await client.ResetResourceAsync(row.OwnerDictionaryId, row.StringKey!);
            Assert.Equal("原值", merged["共享键"]);
        }
        finally
        {
            window.Close();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Owner, long Key) CreateTemporary(ResourceInspectionService service, WeakObjectRegistry objects)
    {
        var key = new DangerousText();
        var dictionary = new ResourceDictionary { [key] = "temporary" };
        var owner = objects.GetId(dictionary);
        var page = service.QueryEntries(new(owner));
        return (owner, Assert.Single(page.Entries).KeyObjectId);
    }

    private sealed class DangerousText
    {
        public int Calls { get; private set; }

        public override string ToString()
        {
            Calls++;
            throw new InvalidOperationException("Do not format user resource objects.");
        }
    }

    private sealed class CustomProvider : ResourceProvider
    {
        public override bool HasResources => true;

        public override bool TryGetResource(object key, ThemeVariant? theme, out object? value)
        {
            value = null;
            return false;
        }
    }

    private sealed class CyclicDictionary : ResourceDictionary, IResourceDictionary
    {
        public CyclicDictionary()
        {
            this["CycleMarker"] = 1;
        }

        IList<IResourceProvider> IResourceDictionary.MergedDictionaries => new List<IResourceProvider> { this };
    }

    private sealed class SharedDictionary : ResourceDictionary, IResourceProvider
    {
        void IResourceProvider.AddOwner(IResourceHost owner)
        {
        }

        void IResourceProvider.RemoveOwner(IResourceHost owner)
        {
        }
    }
}
