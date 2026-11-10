using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class RuntimeInspectionTests
{
    [AvaloniaFact]
    public void Control_datacontext_and_nested_cycles_keep_stable_weak_object_ids()
    {
        var person = new Person { Name = "XiaoMo" };
        person.Child = new Person { Name = "Nested", Child = person };
        var control = new TextBlock { DataContext = person };
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var controlId = objects.GetId(control);
        var controlSnapshot = service.GetMembers(new RuntimeMembersRequest(controlId, Limit: 100));
        var dataContext = Assert.Single(controlSnapshot.Members, member => member.Name == "DataContext");
        Assert.NotNull(dataContext.Value.ValueObjectId);
        var rootId = dataContext.Value.ValueObjectId.Value;
        var root = service.GetMembers(new RuntimeMembersRequest(rootId));
        Assert.Equal("XiaoMo", Assert.Single(root.Members, member => member.Name == "Name").Value.Preview);
        var childId = Assert.Single(root.Members, member => member.Name == "Child").Value.ValueObjectId;
        Assert.NotNull(childId);
        var child = service.GetMembers(new RuntimeMembersRequest(childId.Value));
        Assert.Equal(rootId, Assert.Single(child.Members, member => member.Name == "Child").Value.ValueObjectId);
        var self = Assert.Single(root.Members, member => member.Name == "Self").Value;
        Assert.Equal(rootId, self.ValueObjectId);
        Assert.True(self.IsSelfReference);
        Assert.All(root.Members, member => Assert.True(member.IsReadOnly));
    }

    [AvaloniaFact]
    public void Members_are_deduplicated_by_kind_and_name_and_keep_declaring_owner()
    {
        var probe = new DerivedProbe();
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var snapshot = service.GetMembers(new RuntimeMembersRequest(objects.GetId(probe)));
        var name = Assert.Single(snapshot.Members, member => member.Name == "Name");
        Assert.Equal("derived", name.Value.Preview);
        Assert.Equal(typeof(DerivedProbe).FullName, name.DeclaringType);
        Assert.Equal(typeof(DerivedProbe).FullName + ":Property:Name", name.MemberId);
        Assert.Equal("hidden-derived",
            Assert.Single(snapshot.Members, member => member.Name == "Hidden").Value.Preview);
        Assert.Equal("7", Assert.Single(snapshot.Members, member => member.Name == "PublicField").Value.Preview);
        Assert.Equal(RuntimeMemberKind.Field,
            Assert.Single(snapshot.Members, member => member.Name == "PublicField").Kind);
        Assert.Equal(snapshot.Members.Count, snapshot.Members.Select(member => member.MemberId).Distinct().Count());
        Assert.Equal(snapshot.Members.OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
                .Select(member => member.Name),
            snapshot.Members.Select(member => member.Name));
    }

    [AvaloniaFact]
    public void Getter_errors_are_isolated_and_indexers_static_and_private_getters_are_excluded()
    {
        var probe = new GetterProbe();
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var snapshot = service.GetMembers(new RuntimeMembersRequest(objects.GetId(probe)));
        var failed = Assert.Single(snapshot.Members, member => member.Name == "Failing");
        Assert.Contains("InvalidOperationException", failed.Error);
        Assert.Contains("expected getter failure", failed.Error);
        Assert.False(failed.Value.IsNull);
        Assert.Equal("<unavailable>", failed.Value.Preview);
        Assert.Equal("good", Assert.Single(snapshot.Members, member => member.Name == "Good").Value.Preview);
        Assert.DoesNotContain(snapshot.Members, member => member.Name is "Item" or "StaticValue" or "WriteOnly");
        Assert.Equal(0, probe.IndexerReads);
        Assert.Equal(0, GetterProbe.StaticReads);
    }

    [AvaloniaFact]
    public void Complex_values_and_structs_never_call_arbitrary_tostring()
    {
        var probe = new FormattingProbe();
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var snapshot = service.GetMembers(new RuntimeMembersRequest(objects.GetId(probe)));
        var complex = Assert.Single(snapshot.Members, member => member.Name == "Complex").Value;
        Assert.Equal(typeof(DangerousString).FullName, complex.Preview);
        Assert.NotNull(complex.ValueObjectId);
        var structure = Assert.Single(snapshot.Members, member => member.Name == "Structure").Value;
        Assert.Equal(typeof(DangerousStruct).FullName, structure.Preview);
        Assert.NotNull(structure.ValueObjectId);
        var structureMembers = service.GetMembers(new RuntimeMembersRequest(structure.ValueObjectId.Value));
        Assert.Equal("7", Assert.Single(structureMembers.Members, member => member.Name == "Data").Value.Preview);
        Assert.Equal(0, probe.Complex.Calls);
        Assert.Equal(0, DangerousStruct.Calls);
    }

    [AvaloniaFact]
    public void Value_and_error_previews_are_bounded_and_do_not_split_surrogate_pairs()
    {
        var probe = new LargePreviewProbe();
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var snapshot = service.GetMembers(new RuntimeMembersRequest(objects.GetId(probe)));
        var longValue = Assert.Single(snapshot.Members, member => member.Name == "Text").Value;
        Assert.True(longValue.Truncated);
        Assert.InRange(longValue.Preview.Length, 1, 2048);
        Assert.False(char.IsHighSurrogate(longValue.Preview[^1]));
        var longError = Assert.Single(snapshot.Members, member => member.Name == "Failure");
        Assert.True(longError.ErrorTruncated);
        Assert.Equal(2048, longError.Error!.Length);
    }

    [AvaloniaFact]
    public void Member_paging_reads_only_the_requested_getters()
    {
        var probe = new PagedGetterProbe();
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var snapshot = service.GetMembers(new RuntimeMembersRequest(objects.GetId(probe), Offset: 1, Limit: 1));
        Assert.Equal(3, snapshot.TotalCount);
        Assert.Equal("B", Assert.Single(snapshot.Members).Name);
        Assert.Equal((0, 1, 0), probe.ReadCounts());
        Assert.True(snapshot.HasMore);
    }

    [AvaloniaFact]
    public void Default_member_page_is_fifty_and_limit_is_one_hundred()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("RuntimePageProbe"),
            AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule("Probe");
        var builder = module.DefineType("RuntimePageProbe", TypeAttributes.Public);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        for (var index = 0; index < 120; index++)
            builder.DefineField("Field" + index.ToString("D3"), typeof(int), FieldAttributes.Public);
        var instance = Activator.CreateInstance(builder.CreateType()!)!;
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var id = objects.GetId(instance);
        var first = service.GetMembers(new RuntimeMembersRequest(id));
        Assert.Equal(50, first.Members.Count);
        Assert.Equal(120, first.TotalCount);
        var last = service.GetMembers(new RuntimeMembersRequest(id, Offset: 100, Limit: 100));
        Assert.Equal(20, last.Members.Count);
        Assert.False(last.HasMore);
        Assert.Empty(service.GetMembers(new RuntimeMembersRequest(id, Offset: 121)).Members);
        Assert.Equal("invalid_request",
            Assert.Throws<RpcException>(() =>
                service.GetMembers(new RuntimeMembersRequest(id, Limit: 101))).Code);
    }

    [AvaloniaFact]
    public void Lists_page_null_simple_complex_and_self_references()
    {
        var values = new object?[5];
        values[1] = 123;
        values[2] = "text";
        values[3] = new Person { Name = "item" };
        values[4] = values;
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var id = objects.GetId(values);
        var all = service.GetItems(new RuntimeItemsRequest(id));
        Assert.Equal(RuntimeCollectionKind.List, all.Kind);
        Assert.True(all.Items[0].Value.IsNull);
        var page = service.GetItems(new RuntimeItemsRequest(id, Offset: 1, Limit: 3));
        Assert.Equal([1, 2, 3], page.Items.Select(item => item.Index));
        Assert.Equal("123", page.Items[0].Value.Preview);
        Assert.Equal("text", page.Items[1].Value.Preview);
        Assert.NotNull(page.Items[2].Value.ValueObjectId);
        Assert.All(page.Items, item => Assert.Null(item.Key));
        Assert.True(page.HasMore);
        Assert.True(all.Items[4].Value.IsSelfReference);
        Assert.Equal(id, all.Items[4].Value.ValueObjectId);
        Assert.Empty(service.GetItems(new RuntimeItemsRequest(id, Offset: 8)).Items);
        foreach (var request in new[]
                 {
                     new RuntimeItemsRequest(id, -1), new RuntimeItemsRequest(id, 10001),
                     new RuntimeItemsRequest(id, Limit: 0)
                 })
            Assert.Equal("invalid_request", Assert.Throws<RpcException>(() => service.GetItems(request)).Code);
    }

    [AvaloniaFact]
    public void Dictionary_paging_keeps_key_previews_and_disposes_enumerator_each_request()
    {
        var dictionary = new TrackingDictionary();
        for (var index = 0; index < 10; index++)
            dictionary.Add("key" + index, new Person { Name = "value" + index });
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var id = objects.GetId(dictionary);
        var page = service.GetItems(new RuntimeItemsRequest(id, Offset: 3, Limit: 2));
        Assert.Equal(RuntimeCollectionKind.Dictionary, page.Kind);
        Assert.Equal(10, page.TotalCount);
        Assert.Equal(["key3", "key4"], page.Items.Select(item => item.Key!.Preview));
        Assert.All(page.Items, item => Assert.NotNull(item.Value.ValueObjectId));
        Assert.True(page.HasMore);
        Assert.Equal(1, dictionary.EnumerationStarts);
        Assert.Equal(1, dictionary.EnumerationDisposals);
        var later = service.GetItems(new RuntimeItemsRequest(id, Offset: 9));
        Assert.Single(later.Items);
        Assert.False(later.HasMore);
        Assert.Equal(2, dictionary.EnumerationStarts);
        Assert.Equal(2, dictionary.EnumerationDisposals);
    }

    [AvaloniaFact]
    public void Arbitrary_enumerable_is_explicitly_unsupported_without_starting_enumeration()
    {
        var values = new UnsupportedEnumerable();
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var id = objects.GetId(values);
        Assert.Equal(RuntimeCollectionKind.UnsupportedEnumerable,
            service.GetMembers(new RuntimeMembersRequest(id)).CollectionKind);
        var error = Assert.Throws<RpcException>(() => service.GetItems(new RuntimeItemsRequest(id)));
        Assert.Equal("unsupported_collection", error.Code);
        Assert.Equal(0, values.EnumerationAttempts);
    }

    [AvaloniaFact]
    public void Failing_list_index_is_reported_without_losing_neighboring_items()
    {
        var list = new FailingList { "first", "ignored", "third" };
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var page = service.GetItems(new RuntimeItemsRequest(objects.GetId(list)));
        Assert.Equal(3, page.Items.Count);
        Assert.Equal("first", page.Items[0].Value.Preview);
        Assert.Contains("index failure", page.Items[1].Error);
        Assert.Equal("third", page.Items[2].Value.Preview);
    }

    [AvaloniaFact]
    public void Returned_member_snapshots_do_not_keep_removed_nested_objects_alive()
    {
        var holder = new Person();
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var (childId, snapshot) = ReadThenRemoveChild(holder, objects, service);
        Collect();
        Assert.False(objects.TryGet(childId, out _));
        Assert.NotEmpty(snapshot.Members);
        Assert.Equal("object_expired",
            Assert.Throws<RpcException>(() =>
                service.GetMembers(new RuntimeMembersRequest(childId))).Code);
        Assert.Equal("object_expired",
            Assert.Throws<RpcException>(() =>
                service.GetItems(new RuntimeItemsRequest(long.MaxValue))).Code);
        GC.KeepAlive(holder);
    }

    [AvaloniaFact]
    public async Task Worker_thread_requests_are_rejected_before_touching_registry()
    {
        var objects = new WeakObjectRegistry();
        var service = new RuntimeInspectionService(objects);
        var id = objects.GetId(new Person());
        await Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => service.GetMembers(new RuntimeMembersRequest(id)));
            Assert.Throws<InvalidOperationException>(() => service.GetItems(new RuntimeItemsRequest(id)));
            Assert.Throws<InvalidOperationException>(() => new RuntimeInspectionService(objects));
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long ChildId, RuntimeObjectSnapshot Snapshot) ReadThenRemoveChild(
        Person holder,
        WeakObjectRegistry objects,
        RuntimeInspectionService service)
    {
        holder.Child = new Person { Name = "transient" };
        var snapshot = service.GetMembers(new RuntimeMembersRequest(objects.GetId(holder)));
        var childId = snapshot.Members.Single(member => member.Name == "Child").Value.ValueObjectId!.Value;
        holder.Child = null;
        return (childId, snapshot);
    }

    private static void Collect()
    {
        for (var index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class Person
    {
        public string Name { get; set; } = "";
        public Person? Child { get; set; }
        public Person Self => this;
    }

    private class BaseProbe
    {
        public virtual string Name => "base";
        public string Hidden => "hidden-base";
        public int PublicField = 7;
    }

    private sealed class DerivedProbe : BaseProbe
    {
        public override string Name => "derived";
        public new string Hidden => "hidden-derived";
    }

    private sealed class GetterProbe
    {
        public static int StaticReads;
        public int IndexerReads;
        public string Good => "good";
        public string Failing => throw new InvalidOperationException("expected getter failure");

        public string this[int index]
        {
            get
            {
                IndexerReads++;
                throw new InvalidOperationException();
            }
        }

        public static string StaticValue
        {
            get
            {
                StaticReads++;
                throw new InvalidOperationException();
            }
        }

        public string WriteOnly { private get; set; } = "";
    }

    private sealed class FormattingProbe
    {
        public DangerousString Complex { get; } = new();
        public DangerousStruct Structure => new();
    }

    private sealed class DangerousString
    {
        public int Calls;

        public override string ToString()
        {
            Calls++;
            throw new InvalidOperationException("Do not format arbitrary objects.");
        }
    }

    private readonly struct DangerousStruct
    {
        public static int Calls;
        public int Data => 7;

        public override string ToString()
        {
            Calls++;
            throw new InvalidOperationException("Do not format arbitrary structs.");
        }
    }

    private sealed class LargePreviewProbe
    {
        public string Text => new string('x', 2047) + "😀tail";
        public string Failure => throw new InvalidOperationException(new string('e', 3000));
    }

    private sealed class PagedGetterProbe
    {
        private int _a;
        private int _b;
        private int _c;

        public int A
        {
            get
            {
                _a++;
                return 1;
            }
        }

        public int B
        {
            get
            {
                _b++;
                return 2;
            }
        }

        public int C
        {
            get
            {
                _c++;
                return 3;
            }
        }

        public (int, int, int) ReadCounts() => (_a, _b, _c);
    }

    private sealed class UnsupportedEnumerable : IEnumerable
    {
        public int EnumerationAttempts { get; private set; }

        public IEnumerator GetEnumerator()
        {
            EnumerationAttempts++;
            throw new InvalidOperationException("Do not enumerate.");
        }
    }

    private sealed class FailingList : ArrayList
    {
        public override object? this[int index]
        {
            get => index == 1 ? throw new InvalidOperationException("index failure") : base[index];
            set => base[index] = value;
        }
    }

    private sealed class TrackingDictionary : IDictionary
    {
        private readonly OrderedDictionary _values = new();
        public int EnumerationStarts { get; private set; }
        public int EnumerationDisposals { get; private set; }

        public object? this[object key]
        {
            get => _values[key];
            set => _values[key] = value;
        }

        public bool IsFixedSize => false;
        public bool IsReadOnly => false;
        public ICollection Keys => _values.Keys;
        public ICollection Values => _values.Values;
        public int Count => _values.Count;
        public object SyncRoot => this;
        public bool IsSynchronized => false;
        public void Add(object key, object? value) => _values.Add(key, value);
        public void Clear() => _values.Clear();
        public bool Contains(object key) => _values.Contains(key);
        public void Remove(object key) => _values.Remove(key);
        public void CopyTo(Array array, int index) => _values.CopyTo(array, index);

        public IDictionaryEnumerator GetEnumerator()
        {
            EnumerationStarts++;
            return new TrackingEnumerator(_values.GetEnumerator(), () => EnumerationDisposals++);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TrackingEnumerator(IDictionaryEnumerator inner, Action dispose)
        : IDictionaryEnumerator, IDisposable
    {
        public DictionaryEntry Entry => inner.Entry;
        public object Key => inner.Key;
        public object? Value => inner.Value;
        public object Current => inner.Current;
        public bool MoveNext() => inner.MoveNext();
        public void Reset() => inner.Reset();

        public void Dispose()
        {
            (inner as IDisposable)?.Dispose();
            dispose();
        }
    }
}
