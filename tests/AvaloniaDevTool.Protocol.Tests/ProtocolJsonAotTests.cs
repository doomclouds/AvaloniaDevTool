using System.Reflection;
using System.Text.Json;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Protocol.Tests;

public sealed class ProtocolJsonAotTests
{
    [Fact]
    public void EveryProtocolRecordAndItsWireCollectionsHaveGeneratedMetadata()
    {
        var records = typeof(SessionInfo).Assembly.GetExportedTypes()
            .Where(type => type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) is not null)
            .ToArray();
        Assert.NotEmpty(records);
        foreach (var record in records)
        {
            Assert.True(ProtocolJsonContext.Default.GetTypeInfo(record) is not null, record.FullName);
            Assert.True(ProtocolJsonContext.Default.GetTypeInfo(record.MakeArrayType()) is not null, record.FullName + "[]");
            Assert.True(ProtocolJsonContext.Default.GetTypeInfo(typeof(List<>).MakeGenericType(record)) is not null, "List<" + record.FullName + ">");
            Assert.True(ProtocolJsonContext.Default.GetTypeInfo(typeof(IReadOnlyList<>).MakeGenericType(record)) is not null, "IReadOnlyList<" + record.FullName + ">");
        }
    }

    [Fact]
    public void GeneratedMetadataPreservesWebWireFormatAndStringEnums()
    {
        var info = ProtocolJsonContext.Default.GetTypeInfo(typeof(TreeRootsRequest))!;
        var json = JsonSerializer.Serialize(new TreeRootsRequest(TreeKind.Merged), info);
        Assert.Equal("{\"kind\":\"Merged\"}", json);
        Assert.Equal(TreeKind.Merged, ProtocolJson.Read<TreeRootsRequest>(ProtocolJson.ToElement(new TreeRootsRequest(TreeKind.Merged))).Kind);
    }

    [Fact]
    public void GeneratedMetadataRoundTripsAnRpcEnvelopeAndNestedError()
    {
        var message = new RpcPeer.RpcMessage(17, null, null, null, new RpcPeer.RpcError("unsupported", "test"));
        var bytes = ProtocolJson.SerializeToUtf8Bytes(message);
        var restored = JsonSerializer.Deserialize(bytes, ProtocolJson.TypeInfo<RpcPeer.RpcMessage>());
        Assert.Equal(message, restored);
    }
}
