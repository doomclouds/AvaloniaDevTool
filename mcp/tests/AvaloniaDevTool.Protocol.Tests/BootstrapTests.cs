using System.IO.Pipes;
using System.Text.Json;
using AvaloniaDevTool.Bootstrap;
using Xunit;

namespace AvaloniaDevTool.Protocol.Tests;

public class BootstrapTests
{
    [Fact]
    public async Task ProbeRepliesWithoutRequestOrStatusFiles()
    {
        using var reply = await InvokeAsync("probe", null);
        Assert.Equal("not_avalonia", reply.RootElement.GetProperty("state").GetString());
        Assert.Equal(Environment.ProcessId, reply.RootElement.GetProperty("processId").GetInt32());
    }

    [Fact]
    public async Task StartFailureRepliesWithManagedError()
    {
        using var reply = await InvokeAsync("start", Path.Combine(Path.GetTempPath(), "不存在", "Agent.dll"));
        Assert.Equal("failed", reply.RootElement.GetProperty("state").GetString());
        Assert.Contains("dispatcher", reply.RootElement.GetProperty("error").GetString());
    }

    private static async Task<JsonDocument> InvokeAsync(string action, string? agentPath)
    {
        var name = "avdt-bootstrap-test-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connected = pipe.WaitForConnectionAsync(timeout.Token);
        Assert.Equal(0, Entry.Run(JsonSerializer.Serialize(new
        {
            Action = action, ReplyPipeName = name, AgentPath = agentPath
        })));
        await connected;
        using var reader = new StreamReader(pipe);
        return JsonDocument.Parse(await reader.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException());
    }
}
