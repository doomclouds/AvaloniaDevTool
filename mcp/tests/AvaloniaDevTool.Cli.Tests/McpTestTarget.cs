using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

internal static class McpTestTarget
{
    internal static async Task<CallToolResult> AttachAsync(McpClient client, string pipe)
    {
        var discovery =
            await client.CallToolAsync("attach-to-app", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(discovery.IsError == true, Text(discovery));
        var listed = JsonSerializer.Deserialize<ApplicationAttachmentResult>(
            Assert.Single(discovery.Content.OfType<TextContentBlock>()).Text,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var target = Assert.Single(listed.Applications, target => target.PipeName == pipe);
        return await client.CallToolAsync("attach-to-app",
            new Dictionary<string, object?> { ["id"] = target.Id },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
