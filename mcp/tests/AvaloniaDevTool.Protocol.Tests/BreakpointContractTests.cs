using System.Text.Json;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Protocol.Tests;

public sealed class BreakpointContractTests
{
    [Fact]
    public void Legacy_event_definition_keeps_object_id_as_receiver_and_new_source_filter_roundtrips()
    {
        var legacy = JsonSerializer.Deserialize<BreakpointSpec>(
            """{"kind":"Event","memberId":"Avalonia.Controls.Button:Click","objectId":7}""",
            ProtocolJson.Options)!;
        Assert.Equal(7, legacy.ObjectId);
        Assert.Null(legacy.SourceObjectId);
        var updated = legacy with { SourceObjectId = 11 };
        var roundtrip =
            JsonSerializer.Deserialize<BreakpointSpec>(JsonSerializer.Serialize(updated, ProtocolJson.Options),
                ProtocolJson.Options)!;
        Assert.Equal(7, roundtrip.ObjectId);
        Assert.Equal(11, roundtrip.SourceObjectId);
        Assert.Equal(updated, roundtrip);
    }
}
