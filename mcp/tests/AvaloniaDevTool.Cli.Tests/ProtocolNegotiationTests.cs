using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Channels;
using AvaloniaDevTool.Cli.Catalog;
using AvaloniaDevTool.Cli.Gateway;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class ProtocolNegotiationTests
{
    [Fact]
    public async Task Default_client_can_initialize_after_its_discovery_probe_times_out_on_the_same_connection()
    {
        await using var fixture = new WireFixture(delayDiscoveryReply: true);
        // Do not pin the client version: exercise its default discovery -> initialize transition.
        await using var client = await fixture.ConnectAsync(new McpClientOptions
        {
            DiscoverProbeTimeout = TimeSpan.FromMilliseconds(50),
            InitializationTimeout = TimeSpan.FromSeconds(15)
        });
        Assert.True(fixture.Transport.RejectedUnsupportedDiscovery);
        Assert.Equal(ExecutionProtocol.Version, client.NegotiatedProtocolVersion);
        Assert.Equal(HttpMcpIntegrationTests.ExecutionToolNames.Length + 1,
            (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task A_client_pinned_to_an_unoffered_protocol_gets_an_explicit_supported_version_error()
    {
        await using var fixture = new WireFixture(delayDiscoveryReply: false);
        var error = await Assert.ThrowsAsync<McpException>(() => fixture.ConnectAsync(new McpClientOptions
        {
            ProtocolVersion = "2026-07-28", InitializationTimeout = TimeSpan.FromSeconds(15)
        }));
        Assert.Contains("2026-07-28", error.Message);
        // The SDK can surface either the discovery or initialize rejection and
        // formats their exception messages differently. Check the actual wire
        // rejection for the advertised supported version, not incidental prose.
        Assert.Contains(ExecutionProtocol.Version, fixture.Transport.UnsupportedVersionReply);
    }

    private sealed class WireFixture : IAsyncDisposable
    {
        private readonly Pipe _requests = new();
        private readonly Pipe _responses = new();
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(30));
        private readonly GatewaySession _gateway = new(null);
        private readonly McpServer _server;
        private readonly Task _run;
        internal DelayedReplyTransport Transport { get; }

        internal WireFixture(bool delayDiscoveryReply)
        {
            var options = new McpServerOptions();
            // Use the production configuration, without a test-only protocol override.
            McpToolCatalog.Configure(options, _gateway.CreateTools());
            var wire = new StreamServerTransport(_requests.Reader.AsStream(),
                _responses.Writer.AsStream(),
                "Protocol regression",
                loggerFactory: null);
            Transport = new(wire, delayDiscoveryReply, _lifetime.Token);
            _server = McpServer.Create(Transport, options);
            _run = _server.RunAsync(_lifetime.Token);
        }

        internal Task<McpClient> ConnectAsync(McpClientOptions options) => McpClient.CreateAsync(
            new StreamClientTransport(_requests.Writer.AsStream(), _responses.Reader.AsStream(), loggerFactory: null),
            options,
            cancellationToken: _lifetime.Token);

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            await _server.DisposeAsync();
            try
            {
                await _run;
            }
            catch (OperationCanceledException)
            {
            }

            await _gateway.DisposeAsync();
            _lifetime.Dispose();
        }
    }

    private sealed class DelayedReplyTransport(ITransport inner, bool delayDiscoveryReply, CancellationToken lifetime)
        : ITransport
    {
        private int _firstResponse;
        internal bool RejectedUnsupportedDiscovery { get; private set; }
        internal string UnsupportedVersionReply { get; private set; } = "";
        public string? SessionId => inner.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

        public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (message is JsonRpcError rejected &&
                rejected.Error.Code == (int)McpErrorCode.UnsupportedProtocolVersion)
                UnsupportedVersionReply = JsonSerializer.Serialize(rejected.Error);
            if ((message is JsonRpcResponse or JsonRpcError) && Interlocked.Exchange(ref _firstResponse, 1) == 0)
            {
                RejectedUnsupportedDiscovery = message is JsonRpcError error &&
                                               error.Error.Code == (int)McpErrorCode.UnsupportedProtocolVersion;
                if (delayDiscoveryReply)
                {
                    // Simulate a response already processed by the server arriving after the abandoned probe.
                    await Task.Delay(300, lifetime);
                    await inner.SendMessageAsync(message, lifetime);
                    return;
                }
            }

            await inner.SendMessageAsync(message, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
