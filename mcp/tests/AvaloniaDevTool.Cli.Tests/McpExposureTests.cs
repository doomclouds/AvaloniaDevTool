using AvaloniaDevTool.Cli;
using AvaloniaDevTool.Cli.Catalog;
using ModelContextProtocol.Server;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class McpExposureTests
{
    [Fact]
    public async Task CapabilityCatalogPublishesTargetInteractionNativeAndCaptureToolsWithIcons()
    {
        await using var session = new ToolSession(null);
        var tools = McpToolCatalog.CreateTools(session);
        var options = new McpServerOptions();
        McpToolCatalog.Configure(options, tools);
        var names = tools.Select(tool => tool.ProtocolTool.Name).ToArray();
        Assert.Contains("attach-to-app", names);
        Assert.Contains("tree", names);
        Assert.Contains("ui-click", names);
        Assert.Contains("native-input", names);
        Assert.Contains("file-dialog-accept", names);
        Assert.Contains("capture-start", names);
        Assert.NotEmpty(options.ServerInfo!.Icons!);
        Assert.All(tools, tool => Assert.NotEmpty(tool.ProtocolTool.Icons!));
        Assert.Contains("requiresInjection", tools.Single(tool => tool.ProtocolTool.Name == "attach-to-app").ProtocolTool.Description!);
    }
}
