using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Remote.Tests;

public sealed class RemoteIntegrationTests
{
    [AvaloniaFact]
    public async Task Real_websocket_typed_client_inspects_edits_receives_push_restores_and_reconnects()
    {
        await using var target = await Target.CreateAsync();
        await using var client = new DiagnosticsClient();
        await client.ConnectRemoteAsync(target.Server.Endpoint, target.Token);
        Assert.Equal(DiagnosticsProtocol.Version, client.Session!.ProtocolVersion);
        var firstSession = client.Session.SessionId;
        var root = Assert.Single(await client.Tree.GetRootsAsync(TreeKind.Logical));
        var node = (await client.Tree.GetChildrenAsync(root.Id, TreeKind.Logical)).Single(value =>
            value.Name == "RemoteMessage");
        var property = (await client.Properties.GetPropertiesAsync(node.Id)).Single(value => value.Name == "Text");
        Assert.Equal("Original", property.Value);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Properties.PropertiesChanged += _ => changed.TrySetResult();
        await client.Properties.SetPropertyAsync(node.Id, property.Id, "Remote edit");
        Assert.Equal("Remote edit", target.Text.Text);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(await client.Properties.GetPropertiesAsync(node.Id),
            value => value.Name == "Text" && value.Value == "Remote edit");
        await client.DisconnectAsync();
        await WaitUntilAsync(() => !target.Agent.IsConnected);
        Assert.Equal("Original", target.Text.Text);
        await client.ConnectRemoteAsync(target.Server.Endpoint, target.Token);
        Assert.NotEqual(firstSession, client.Session!.SessionId);
        Assert.Single(await client.Tree.GetRootsAsync(TreeKind.Visual));
    }

    [AvaloniaFact]
    public async Task Authentication_and_origin_fail_before_session_creation_and_never_expose_token()
    {
        await using var target = await Target.CreateAsync();
        using var http = new HttpClient();
        var uri = new UriBuilder(target.Server.Endpoint) { Scheme = "http" }.Uri;
        foreach (var token in new string?[] { null, DiagnosticsRemoteServer.GenerateBearerToken() })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain(target.Token, await response.Content.ReadAsStringAsync());
            Assert.False(target.Agent.IsConnected);
        }

        using var originRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        originRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
        originRequest.Headers.Add("Origin", "https://untrusted.invalid");
        using var forbidden = await http.SendAsync(originRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.False(target.Agent.IsConnected);
        Assert.DoesNotContain(target.Token, target.Server.Endpoint.ToString());
        await using var client = new DiagnosticsClient();
        await client.ConnectRemoteAsync(target.Server.Endpoint, target.Token);
        Assert.True(client.IsConnected);
    }

    [AvaloniaFact]
    public async Task Pipe_and_remote_share_a_single_gate_and_rejected_transport_does_not_dispose_active_session()
    {
        await using var target = await Target.CreateAsync();
        await using var pipeClient = new DiagnosticsClient();
        await pipeClient.ConnectAsync(target.Agent.PipeName);
        await using var remote = new DiagnosticsClient();
        await Assert.ThrowsAsync<WebSocketException>(() =>
            remote.ConnectRemoteAsync(target.Server.Endpoint, target.Token));
        Assert.Single(await pipeClient.Tree.GetRootsAsync(TreeKind.Logical));
        await pipeClient.DisconnectAsync();
        await WaitUntilAsync(() => !target.Agent.IsConnected);
        await remote.ConnectRemoteAsync(target.Server.Endpoint, target.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => pipeClient.ConnectAsync(target.Agent.PipeName));
        Assert.Single(await remote.Tree.GetRootsAsync(TreeKind.Logical));
        await remote.DisconnectAsync();
        await WaitUntilAsync(() => !target.Agent.IsConnected);
        await pipeClient.ConnectAsync(target.Agent.PipeName);
        Assert.Single(await pipeClient.Tree.GetRootsAsync(TreeKind.Logical));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Text_and_invalid_rpc_frames_close_connection_then_allow_clean_reconnect(bool text)
    {
        await using var target = await Target.CreateAsync();
        using var socket = await target.ConnectSocketAsync();
        if (text)
            await socket.SendAsync(Encoding.UTF8.GetBytes("invalid"),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        else
        {
            var invalid = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(invalid, RpcPeer.MaximumFrameBytes + 1);
            await socket.SendAsync(invalid, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        await WaitUntilAsync(() => !target.Agent.IsConnected);
        await using var client = new DiagnosticsClient();
        await client.ConnectRemoteAsync(target.Server.Endpoint, target.Token);
        Assert.Single(await client.Tree.GetRootsAsync(TreeKind.Logical));
    }

    [AvaloniaFact]
    public async Task Fragmented_binary_messages_preserve_native_rpc_framing()
    {
        await using var target = await Target.CreateAsync();
        using var socket = await target.ConnectSocketAsync();
        var json = Encoding.UTF8.GetBytes("{\"id\":17,\"method\":\"session.info\",\"parameters\":null}");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await socket.SendAsync(header.AsMemory(0, 2), WebSocketMessageType.Binary, false, CancellationToken.None);
        await socket.SendAsync(header.AsMemory(2), WebSocketMessageType.Binary, false, CancellationToken.None);
        await socket.SendAsync(json, WebSocketMessageType.Binary, true, CancellationToken.None);
        using var stream = new RpcWebSocketStream(socket);
        await stream.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await stream.ReadExactlyAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"id\":17", Encoding.UTF8.GetString(bytes));
        Assert.Contains("protocolVersion", Encoding.UTF8.GetString(bytes));
    }

    [AvaloniaFact]
    public async Task Server_disposal_disconnects_clients_releases_session_and_closes_listener()
    {
        await using var target = await Target.CreateAsync();
        await using var client = new DiagnosticsClient();
        await client.ConnectRemoteAsync(target.Server.Endpoint, target.Token);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += () =>
        {
            if (!client.IsConnected)
                disconnected.TrySetResult();
        };
        await target.Server.DisposeAsync();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !target.Agent.IsConnected);
        Assert.Null(client.Session);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + target.Token);
        await Assert.ThrowsAsync<WebSocketException>(() =>
            socket.ConnectAsync(target.Server.Endpoint, CancellationToken.None));
        await client.ConnectAsync(target.Agent.PipeName);
        Assert.Single(await client.Tree.GetRootsAsync(TreeKind.Logical));
    }

    [AvaloniaFact]
    public async Task Weak_tokens_and_plaintext_nonloopback_listeners_are_rejected_before_listening()
    {
        await using var target = await Target.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => DiagnosticsRemoteServer.StartAsync(target.Agent,
            new DiagnosticsRemoteServerOptions { BearerToken = "short" }));
        await Assert.ThrowsAsync<ArgumentException>(() => DiagnosticsRemoteServer.StartAsync(target.Agent,
            new DiagnosticsRemoteServerOptions { BearerToken = target.Token, ListenAddress = IPAddress.Any }));
        Assert.False(target.Agent.IsConnected);
    }

    [AvaloniaFact]
    public async Task Untrusted_tls_certificate_is_rejected_without_an_insecure_fallback()
    {
        await using var target = await Target.CreateAsync();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=avdt-untrusted-test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate =
            request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        await using var tlsServer = await DiagnosticsRemoteServer.StartAsync(target.Agent,
            new DiagnosticsRemoteServerOptions { BearerToken = target.Token, ServerCertificate = certificate });
        Assert.Equal("wss", tlsServer.Endpoint.Scheme);
        await using var client = new DiagnosticsClient();
        await Assert.ThrowsAsync<WebSocketException>(() => client.ConnectRemoteAsync(tlsServer.Endpoint, target.Token));
        Assert.False(client.IsConnected);
        Assert.False(target.Agent.IsConnected);
    }

    [Fact]
    public async Task Caller_cancellation_stops_a_stalled_websocket_upgrade()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var client = new DiagnosticsClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connecting = client.ConnectRemoteAsync(new Uri($"ws://127.0.0.1:{port}/diagnostics"),
            DiagnosticsRemoteServer.GenerateBearerToken(),
            cancellation.Token);
        using var connection = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connecting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Insecure_remote_addresses_credential_urls_are_rejected()
    {
        using var cancellation = new CancellationTokenSource();
        await using var client = new DiagnosticsClient();
        foreach (var endpoint in new[]
                 {
                     "http://127.0.0.1/diagnostics", "ws://example.com/diagnostics", "ws://localhost/diagnostics",
                     "ws://user:secret@127.0.0.1/diagnostics", "ws://127.0.0.1/diagnostics?token=secret",
                     "ws://127.0.0.1/diagnostics#secret"
                 })
            await Assert.ThrowsAsync<ArgumentException>(() =>
                client.ConnectRemoteAsync(new Uri(endpoint), "token", cancellation.Token));
        Assert.Equal(43, DiagnosticsRemoteServer.GenerateBearerToken().Length);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(25, timeout.Token);
    }

    private sealed class Target(
        Window window,
        TextBlock text,
        DiagnosticsAgent agent,
        DiagnosticsRemoteServer server,
        string token) : IAsyncDisposable
    {
        public TextBlock Text { get; } = text;
        public DiagnosticsAgent Agent { get; } = agent;
        public DiagnosticsRemoteServer Server { get; } = server;
        public string Token { get; } = token;

        public static async Task<Target> CreateAsync()
        {
            var text = new TextBlock { Name = "RemoteMessage", Text = "Original" };
            var window = new Window { Width = 400, Height = 300, Content = text };
            window.Show();
            var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                    { PipeName = "avdt-remote-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var token = DiagnosticsRemoteServer.GenerateBearerToken();
            var server =
                await DiagnosticsRemoteServer.StartAsync(agent,
                    new DiagnosticsRemoteServerOptions { BearerToken = token });
            return new Target(window, text, agent, server, token);
        }

        public async Task<ClientWebSocket> ConnectSocketAsync()
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + Token);
            await socket.ConnectAsync(Server.Endpoint, CancellationToken.None);
            await WaitUntilAsync(() => Agent.IsConnected);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Agent.DisposeAsync();
            window.Close();
        }
    }
}
