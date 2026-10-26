using System.Collections;
using System.Globalization;
using System.Reflection;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Reads a bounded page of public CLR members without recursively walking object graphs.</summary>
internal sealed class RuntimeInspectionService
{
    internal const int MaximumPreviewLength = 2048;
    private readonly WeakObjectRegistry _objects;

    public RuntimeInspectionService(WeakObjectRegistry objects)
    {
        Dispatcher.UIThread.VerifyAccess();
        _objects = objects;
    }

    public RuntimeObjectSnapshot GetMembers(RuntimeMembersRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        ValidatePage(request.Offset, request.Limit);
        var target = _objects.Get(request.ObjectId);
        var type = target.GetType();
        var members = EnumerateMembers(type);
        var page = members.Skip(request.Offset).Take(request.Limit)
            .Select(member => ReadMember(request.ObjectId, target, member)).ToArray();
        return new RuntimeObjectSnapshot(request.ObjectId,
            TypeName(type),
            request.Offset,
            members.Count,
            page,
            request.Offset + page.Length < members.Count,
            CollectionKind(target));
    }

    public RuntimeItemsSnapshot GetItems(RuntimeItemsRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        ValidatePage(request.Offset, request.Limit);
        var target = _objects.Get(request.ObjectId);
        var kind = CollectionKind(target);
        if (kind is not (RuntimeCollectionKind.List or RuntimeCollectionKind.Dictionary))
            throw new RpcException("unsupported_collection",
                "Only non-generic IList and IDictionary collections are supported; arbitrary IEnumerable values are not enumerated.");
        int total;
        try
        {
            total = target is IDictionary dictionary ? dictionary.Count : ((IList)target).Count;
        }
        catch (Exception exception)
        {
            throw new RpcException("collection_read_failed", ErrorText(exception).Text);
        }

        if (total < 0)
            throw new RpcException("collection_read_failed", "The collection returned a negative Count.");
        if (request.Offset >= total)
            return new RuntimeItemsSnapshot(request.ObjectId,
                TypeName(target.GetType()),
                kind,
                request.Offset,
                total,
                [],
                false);
        var end = Math.Min(total, request.Offset + request.Limit);
        IReadOnlyList<RuntimeItemInfo> items = target is IDictionary values
            ? ReadDictionary(request.ObjectId, values, request.Offset, end)
            : ReadList(request.ObjectId, (IList)target, request.Offset, end);
        return new RuntimeItemsSnapshot(request.ObjectId,
            TypeName(target.GetType()),
            kind,
            request.Offset,
            total,
            items,
            request.Offset + items.Count < total);
    }

    private static void ValidatePage(int offset, int limit)
    {
        if (offset is < 0 or > 10000 || limit is < 1 or > 100)
            throw new RpcException("invalid_request", "Offset must be 0..10000 and Limit must be 1..100.");
    }

    private static IReadOnlyList<MemberDescriptor> EnumerateMembers(Type type)
    {
        var result = new List<MemberDescriptor>();
        var seen = new HashSet<(RuntimeMemberKind Kind, string Name)>();
        for (var owner = type; owner is not null; owner = owner.BaseType)
        {
            foreach (var property in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance |
                                                         BindingFlags.DeclaredOnly))
            {
                if (property.GetMethod is not { IsPublic: true, IsStatic: false } ||
                    property.GetIndexParameters().Length != 0)
                    continue;
                if (seen.Add((RuntimeMemberKind.Property, property.Name)))
                    result.Add(new MemberDescriptor(property, RuntimeMemberKind.Property, property.PropertyType));
            }

            foreach (var field in owner.GetFields(BindingFlags.Public | BindingFlags.Instance |
                                                  BindingFlags.DeclaredOnly))
            {
                if (seen.Add((RuntimeMemberKind.Field, field.Name)))
                    result.Add(new MemberDescriptor(field, RuntimeMemberKind.Field, field.FieldType));
            }
        }

        return result.OrderBy(member => member.Member.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.Kind).ThenBy(member => member.Member.Name, StringComparer.Ordinal).ToArray();
    }

    private RuntimeMemberInfo ReadMember(long ownerId, object target, MemberDescriptor descriptor)
    {
        var member = descriptor.Member;
        var owner = TypeName(member.DeclaringType ?? target.GetType());
        var memberId = owner + ":" + descriptor.Kind + ":" + member.Name;
        try
        {
            var value = member switch
            {
                PropertyInfo property => property.GetValue(target),
                FieldInfo field => field.GetValue(target),
                _ => null
            };
            return new RuntimeMemberInfo(memberId,
                member.Name,
                descriptor.Kind,
                owner,
                TypeName(descriptor.ValueType),
                DescribeValue(ownerId, value, descriptor.ValueType));
        }
        catch (Exception exception)
        {
            var error = ErrorText(exception);
            return new RuntimeMemberInfo(memberId,
                member.Name,
                descriptor.Kind,
                owner,
                TypeName(descriptor.ValueType),
                Unavailable(descriptor.ValueType),
                Error: error.Text,
                ErrorTruncated: error.Truncated);
        }
    }

    private IReadOnlyList<RuntimeItemInfo> ReadList(long ownerId, IList list, int offset, int end)
    {
        var items = new List<RuntimeItemInfo>(end - offset);
        for (var index = offset; index < end; index++)
        {
            try
            {
                items.Add(new RuntimeItemInfo(index, null, DescribeValue(ownerId, list[index], typeof(object))));
            }
            catch (Exception exception)
            {
                var error = ErrorText(exception);
                items.Add(new RuntimeItemInfo(index, null, Unavailable(typeof(object)), error.Text, error.Truncated));
            }
        }

        return items;
    }

    private IReadOnlyList<RuntimeItemInfo> ReadDictionary(long ownerId, IDictionary dictionary, int offset, int end)
    {
        var items = new List<RuntimeItemInfo>(end - offset);
        IDictionaryEnumerator? enumerator = null;
        try
        {
            enumerator = dictionary.GetEnumerator();
            for (var index = 0; index < offset; index++)
                if (!enumerator.MoveNext())
                    return items;
            for (var index = offset; index < end && enumerator.MoveNext(); index++)
            {
                try
                {
                    var entry = enumerator.Entry;
                    items.Add(new RuntimeItemInfo(index,
                        DescribeValue(ownerId, entry.Key, typeof(object)),
                        DescribeValue(ownerId, entry.Value, typeof(object))));
                }
                catch (Exception exception)
                {
                    var error = ErrorText(exception);
                    items.Add(
                        new RuntimeItemInfo(index, null, Unavailable(typeof(object)), error.Text, error.Truncated));
                }
            }

            return items;
        }
        catch (Exception exception) when (exception is not RpcException)
        {
            throw new RpcException("collection_read_failed", ErrorText(exception).Text);
        }
        finally
        {
            if (enumerator is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private RuntimeValueInfo DescribeValue(long ownerId, object? value, Type declaredType)
    {
        if (value is null)
            return new RuntimeValueInfo(TypeName(declaredType), "null", null, true, false);
        var type = value.GetType();
        var typeName = TypeName(type);
        var simple = value switch
        {
            string text => text,
            char character => character.ToString(),
            bool boolean => boolean ? "true" : "false",
            Enum enumeration => enumeration.ToString(),
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D"),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Half
                or Int128 or UInt128 or IntPtr or UIntPtr =>
                ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            _ => null
        };
        var preview = Truncate(simple ?? typeName);
        var reference = simple is null ? _objects.GetId(value) : (long?)null;
        return new RuntimeValueInfo(typeName, preview.Text, reference, false, preview.Truncated, reference == ownerId);
    }

    private static RuntimeValueInfo Unavailable(Type declaredType) =>
        new(TypeName(declaredType), "<unavailable>", null, false, false);

    private static (string Text, bool Truncated) ErrorText(Exception exception)
    {
        if (exception is TargetInvocationException { InnerException: { } inner })
            exception = inner;
        string message;
        try
        {
            message = exception.Message;
        }
        catch (Exception)
        {
            message = "The exception message could not be read.";
        }

        return Truncate(exception.GetType().Name + ": " + message);
    }

    private static (string Text, bool Truncated) Truncate(string value)
    {
        if (value.Length <= MaximumPreviewLength)
            return (value, false);
        var length = MaximumPreviewLength;
        if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            length--;
        return (value[..length], true);
    }

    private static string TypeName(Type type) => type.FullName ?? type.Name;

    private static RuntimeCollectionKind CollectionKind(object value) => value switch
    {
        IDictionary => RuntimeCollectionKind.Dictionary,
        IList => RuntimeCollectionKind.List,
        string => RuntimeCollectionKind.None,
        IEnumerable => RuntimeCollectionKind.UnsupportedEnumerable,
        _ => RuntimeCollectionKind.None
    };

    private sealed record MemberDescriptor(MemberInfo Member, RuntimeMemberKind Kind, Type ValueType);
}
