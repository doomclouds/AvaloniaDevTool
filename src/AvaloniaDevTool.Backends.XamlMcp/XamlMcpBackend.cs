using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Upstream = global::XamlMcp.Protocol;

namespace AvaloniaDevTool.Backends.XamlMcp;

/// <summary>Read-only adapter over the published upstream stdio MCP server and its authenticated agent.</summary>
public sealed class XamlMcpBackend : IInspectionBackend
{
    private const int MaximumJsonCharacters = 8 * 1024 * 1024;
    private readonly XamlMcpBackendOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly XamlSnapshotCache _snapshot = new();
    private McpClient? _client;
    private Upstream.CapabilitiesDto? _capabilities;
    private string? _snapshotNotice;
    private string? _detailNotice;
    private int _disposed;

    public XamlMcpBackend(XamlMcpBackendOptions? options = null)
    {
        _options = options ?? new XamlMcpBackendOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.ServerCommand);
        ArgumentNullException.ThrowIfNull(_options.ServerArguments);
        if (_options.MaxSnapshotNodes is < 1 or > 10_000 || _options.SnapshotDepth is < 0 or > 24 ||
            _options.OperationTimeout <= TimeSpan.Zero || _options.OperationTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(options), "Snapshot and timeout limits are outside the supported range.");
    }

    public string BackendId => "xamlmcp";
    public string DisplayName => "XamlMcp 开源后端（只读）";
    public bool HasStableObjectIds => false;
    public bool HasPushUpdates => false;
    public bool IsConnected => _client is not null && Session is not null;
    public SessionInfo? Session { get; private set; }
    public long? CurrentSnapshotId => _snapshot.SnapshotId;
    public int? LastServerProcessId { get; private set; }
    public string? LastNotice => string.Join(" ", new[]
    {
        "XamlMcp 只读适配；树是有界目标快照，子项从本地缓存展开；不提供实时推送。",
        _snapshotNotice, _detailNotice
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public event Action? ConnectionChanged;

    public Task<IReadOnlyList<XamlMcpApplicationInfo>> ListApplicationsAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            await EnsureClientAsync(token).ConfigureAwait(false);
            return await ListApplicationsCoreAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var selector = ParseEndpoint(endpoint);
            await DisconnectCoreAsync().ConfigureAwait(false);
            try
            {
                await EnsureClientAsync(token).ConfigureAwait(false);
                var apps = await ListApplicationsCoreAsync(token).ConfigureAwait(false);
                var application = apps.SingleOrDefault(app => selector.Pid is { } pid ? app.Pid == pid : app.InstanceId == selector.InstanceId)
                    ?? throw new RpcException("target_not_found", "No matching Avalonia agent is currently discoverable.");
                var arguments = selector.Pid is { } targetPid
                    ? new Dictionary<string, object?> { ["pid"] = targetPid }
                    : new Dictionary<string, object?> { ["instanceId"] = selector.InstanceId };
                _capabilities = await CallJsonAsync<Upstream.CapabilitiesDto>("attach", arguments, token).ConfigureAwait(false);
                if (_capabilities.ProtocolVersion != Upstream.ProtocolInfo.ProtocolVersion)
                    throw new RpcException("unsupported_version", $"The upstream agent uses protocol {_capabilities.ProtocolVersion}; this adapter maps protocol {Upstream.ProtocolInfo.ProtocolVersion}.");
                if (_capabilities.FrameworkId != "avalonia")
                    throw new RpcException("unsupported_framework", "This adapter currently maps Avalonia agents only.");
                Session = new SessionInfo(Guid.NewGuid().ToString("N"),
                    application.MainWindowTitle ?? application.ProcessName, application.Pid ?? 0,
                    _capabilities.FrameworkVersion, _capabilities.ProtocolVersion);
                _detailNotice = $"来源 {_capabilities.Product} {_capabilities.ProductVersion}，协议 {_capabilities.ProtocolVersion}。属性修改能力未映射。";
                PublishConnectionChanged();
            }
            catch
            {
                await DisconnectCoreAsync().ConfigureAwait(false);
                throw;
            }
        }, cancellationToken);

    public Task<IReadOnlyList<TreeNodeInfo>> GetRootsAsync(TreeKind kind, CancellationToken cancellationToken = default) =>
        RunConnectedAsync(async token =>
        {
            RequireCapability("tree");
            if (!Enum.IsDefined(kind)) throw new RpcException("invalid_request", "The tree scope is invalid.");
            RequireSupportedTree(kind);
            var tree = await CallJsonAsync<Upstream.TreeResult>("tree", new()
            {
                ["scope"] = kind == TreeKind.Logical ? "logical" : "visual",
                ["profile"] = "raw", ["depth"] = _options.SnapshotDepth, ["maxNodes"] = _options.MaxSnapshotNodes
            }, token).ConfigureAwait(false);
            var roots = _snapshot.Load(tree, kind, _options.MaxSnapshotNodes);
            _snapshotNotice = $"快照 {tree.SnapshotId}：已缓存 {_snapshot.Count} 个对象；此前选中与对象句柄已失效。" +
                (tree.Truncated ? $" 目标树已按 {_options.MaxSnapshotNodes} 节点／{_options.SnapshotDepth} 层上限截断；截断分支会明确报错。" : "");
            return roots;
        }, cancellationToken);

    public Task<IReadOnlyList<TreeNodeInfo>> GetChildrenAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) =>
        RunConnectedAsync(_ =>
        {
            RequireSupportedTree(kind);
            return Task.FromResult(_snapshot.Children(objectId, kind));
        }, cancellationToken);

    public Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesAsync(long objectId, CancellationToken cancellationToken = default) =>
        RunConnectedAsync(async token =>
        {
            RequireCapability("props");
            var node = _snapshot.Resolve(objectId);
            var result = await CallJsonAsync<Upstream.PropsResult>("props",
                new() { ["target"] = node.Target }, token).ConfigureAwait(false);
            return (IReadOnlyList<PropertyInfoDto>)result.Properties.Select(property => new PropertyInfoDto(
                property.OwnerType + ":" + property.Name, property.Name, property.OwnerType, property.PropertyType,
                FormatValue(property.Value), property.Priority, property.IsReadOnly, false, false,
                property.Value.Truncated ? "Upstream value is truncated." : null)).ToArray();
        }, cancellationToken);

    public Task<StyleSnapshot> GetStylesAsync(long objectId, CancellationToken cancellationToken = default) =>
        RunConnectedAsync(async token =>
        {
            var node = _snapshot.Resolve(objectId);
            var notes = new List<string>
            {
                "来源为公开 XamlMcp 服务；上游 setter 不含精确 owner 与有效值匹配证据，MatchesEffectiveValue 为未知。",
                "上游未提供有效优先级；active/inactive 来自其 IsRunning，其他错误分类不能反推运行状态，保持未知。"
            };
            var frames = new List<StyleFrameInfo>();
            if (HasCapability("styles"))
            {
                var result = await CallJsonAsync<Upstream.StylesResult>("styles", new() { ["target"] = node.Target }, token).ConfigureAwait(false);
                frames.AddRange(result.Frames.Select(frame => new StyleFrameInfo(
                    frame.Selector ?? "XamlMcp " + frame.FrameType, frame.FrameType, frame.Priority, frame.IsActive,
                    frame.Setters.Select(setter => new StyleSetterInfo(setter.Property, "Unknown", setter.ValueText, null)).ToArray())));
                if (result.Degraded) notes.Add("样式降级：" + result.DegradationReason);
            }
            else notes.Add("目标未声明 styles capability。");
            var bindings = new List<BindingDiagnosticInfo>();
            var validation = new List<string>();
            var truncated = false;
            if (HasCapability("bindings"))
            {
                var result = await CallJsonAsync<Upstream.BindingsResult>("bindings", new() { ["target"] = node.Target }, token).ConfigureAwait(false);
                var mapped = XamlBindingDetailsMapper.Map(result);
                bindings.AddRange(mapped.Bindings);
                validation.AddRange(mapped.ValidationErrors);
                notes.AddRange(mapped.Limitations);
                truncated = mapped.Truncated;
            }
            else notes.Add("目标未声明 bindings capability。");
            return new StyleSnapshot(frames, bindings, validation.Distinct().ToArray(),
                "XamlMcp " + _capabilities!.ProductVersion, truncated, notes.Distinct().Take(64).ToArray());
        }, cancellationToken);

    public Task<IReadOnlyList<TreeNodeInfo>> GetAncestorsAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) =>
        RunConnectedAsync(async token =>
        {
            RequireSupportedTree(kind);
            _snapshot.RequireKind(kind);
            var target = _snapshot.Resolve(objectId);
            if (kind == TreeKind.Logical)
            {
                _detailNotice = "逻辑祖先链由当前有界 logical 快照的父关系生成；没有把上游 visual ancestors 冒充 logical。";
                return _snapshot.LocalAncestors(objectId, kind);
            }
            RequireCapability("ancestors");
            var result = await CallJsonAsync<Upstream.AncestorsResult>("ancestors",
                new() { ["target"] = target.Target }, token).ConfigureAwait(false);
            if (result.SnapshotId != target.SnapshotId)
                throw new RpcException("snapshot_expired", "The upstream ancestor response changed its snapshot namespace. Refresh the tree.");
            return (IReadOnlyList<TreeNodeInfo>)result.Ancestors.Reverse()
                .Select(ancestor => _snapshot.ResolveNative(ancestor.NodeId).Info).Append(target.Info).ToArray();
        }, cancellationToken);

    private static void RequireSupportedTree(TreeKind kind)
    {
        if (kind == TreeKind.Merged)
            throw new RpcException("unsupported_tree", "The XamlMcp adapter does not provide merged template and content-slot semantics.");
    }

    public Task<ElementScreenshot> CaptureElementAsync(long objectId, CancellationToken cancellationToken = default) =>
        RunConnectedAsync(async token =>
        {
            RequireCapability("screenshot");
            var node = _snapshot.Resolve(objectId);
            var result = await CallToolAsync("screenshot", new()
            {
                ["target"] = node.Target, ["includeNodeMap"] = false, ["maxBytes"] = 2 * 1024 * 1024
            }, token).ConfigureAwait(false);
            var images = result.Content.OfType<ImageContentBlock>().ToArray();
            if (images.Length != 1 || images[0].MimeType != "image/png")
                throw new RpcException("invalid_response", "The upstream screenshot is not one PNG image block.");
            var bytes = images[0].DecodedData.ToArray();
            if (bytes.Length < 24 || bytes.Length > 2 * 1024 * 1024 ||
                !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new RpcException("invalid_response", "The upstream screenshot has an invalid PNG envelope.");
            var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            if (width <= 0 || height <= 0) throw new RpcException("invalid_response", "The upstream PNG dimensions are invalid.");
            _detailNotice = "截图保持上游 PNG 字节；为视觉重绘，不保证 GPU／原生互操作内容，且没有额外生成 node-map 快照。";
            return new ElementScreenshot(width, height, Convert.ToBase64String(bytes));
        }, cancellationToken);

    private async Task<IReadOnlyList<XamlMcpApplicationInfo>> ListApplicationsCoreAsync(CancellationToken token)
    {
        var document = await CallJsonAsync<JsonElement>("list-apps", null, token).ConfigureAwait(false);
        if (!document.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
            throw new RpcException("invalid_response", "The upstream app directory has no apps array.");
        return apps.EnumerateArray().Select(app => new XamlMcpApplicationInfo(
                app.GetProperty("instanceId").GetString() ?? "",
                app.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt32() : null,
                app.GetProperty("processName").GetString() ?? "",
                app.TryGetProperty("mainWindowTitle", out var title) ? title.GetString() : null,
                app.GetProperty("frameworkId").GetString() ?? "", app.GetProperty("frameworkVersion").GetString() ?? "",
                app.GetProperty("platform").GetString() ?? ""))
            .Where(app => app.FrameworkId == "avalonia" && !string.IsNullOrWhiteSpace(app.InstanceId)).Take(1024).ToArray();
    }

    private async Task EnsureClientAsync(CancellationToken token)
    {
        if (_client is not null) return;
        if (_options.ServerArguments.Count == 1 && _options.ServerArguments[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(_options.ServerArguments[0]))
            throw new RpcException("server_not_found", "The configured upstream server assembly does not exist. Restore and build the backend package payload, or pass an explicit server path.");
        var environment = new Dictionary<string, string?>(StdioClientTransportOptions.GetDefaultEnvironmentVariables());
        if (_options.DiscoveryDirectory is { } directory) environment["XAML_MCP_DIR"] = directory;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "AvaloniaDevTool.XamlMcp", Command = _options.ServerCommand,
            Arguments = _options.ServerArguments.ToList(), WorkingDirectory = _options.WorkingDirectory,
            InheritEnvironmentVariables = false, EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(5), StandardErrorLines = _options.StandardErrorLines
        });
        var client = await McpClient.CreateAsync(transport, cancellationToken: token).ConfigureAwait(false);
        _client = client;
        _ = ObserveCompletionAsync(client);
    }

    private async Task ObserveCompletionAsync(McpClient client)
    {
        var completion = await client.Completion.ConfigureAwait(false);
        if (completion is StdioClientCompletionDetails process) LastServerProcessId = process.ProcessId;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_client, client)) return;
            _client = null;
            var wasConnected = Session is not null;
            Session = null;
            _capabilities = null;
            _snapshot.Clear();
            _detailNotice = "上游 MCP Server 进程已退出，请重新连接。";
            if (wasConnected) PublishConnectionChanged();
        }
        finally { _gate.Release(); }
    }

    private async Task<T> CallJsonAsync<T>(string tool, Dictionary<string, object?>? arguments, CancellationToken token)
    {
        var result = await CallToolAsync(tool, arguments, token).ConfigureAwait(false);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        if (text.Length > MaximumJsonCharacters)
            throw new RpcException("response_too_large", "The upstream JSON response exceeded the adapter bound.");
        try
        {
            return JsonSerializer.Deserialize<T>(text, Upstream.InspectorJson.Options)
                ?? throw new RpcException("invalid_response", "The upstream response is empty.");
        }
        catch (JsonException exception) { throw new RpcException("invalid_response", exception.Message); }
    }

    private async Task<CallToolResult> CallToolAsync(string tool, Dictionary<string, object?>? arguments, CancellationToken token)
    {
        var client = _client ?? throw new RpcException("not_connected", "The upstream MCP server is not connected.");
        try
        {
            var result = await client.CallToolAsync(tool, arguments, cancellationToken: token).ConfigureAwait(false);
            if (result.IsError == true)
                throw TranslateError(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
            return result;
        }
        catch (McpException exception) { throw TranslateError(exception.Message); }
        catch (ClientTransportClosedException) { throw new RpcException("connection_closed", "The upstream MCP transport closed."); }
    }

    private static RpcException TranslateError(string message)
    {
        var known = new[] { "stale-snapshot", "stale-node", "app-unresponsive", "outcome-indeterminate",
            "agent-timeout", "agent-disconnected", "unsupported-capability", "ambiguous-target", "invalid-query" };
        var code = known.FirstOrDefault(candidate => message.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        return new RpcException(code ?? "upstream_error", message.Length > 2048 ? message[..2048] : message);
    }

    private Task<T> RunConnectedAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) =>
        RunAsync(token =>
        {
            if (!IsConnected) throw new RpcException("not_connected", "Attach to an upstream Avalonia application first.");
            return action(token);
        }, cancellationToken);

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeout);
        await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try { return await action(timeout.Token).ConfigureAwait(false); }
        catch (RpcException error) when (error.Code is "agent-disconnected" or "agent-timeout" or "connection_closed")
        {
            var wasConnected = Session is not null;
            Session = null;
            _snapshot.Clear();
            _detailNotice = error.Message;
            if (wasConnected) PublishConnectionChanged();
            throw;
        }
        catch (OperationCanceledException)
        {
            var wasConnected = Session is not null;
            Session = null;
            _snapshot.Clear();
            _detailNotice = "上游请求被取消或超时，连接状态需要重新确认；请重新连接。";
            if (wasConnected) PublishConnectionChanged();
            throw;
        }
        finally { _gate.Release(); }
    }

    private Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        RunAsync(async token => { await action(token).ConfigureAwait(false); return true; }, cancellationToken);

    private bool HasCapability(string capability) => _capabilities?.Tools.GetValueOrDefault(capability) == true;
    private void RequireCapability(string capability)
    {
        if (!HasCapability(capability))
            throw new RpcException("not_supported", $"The upstream application did not enable {capability}.");
    }

    private static string? FormatValue(Upstream.ValueDto value) =>
        value.Kind == Upstream.ValueKind.Null ? null : value.Text ?? value.Summary;

    private static (int? Pid, string? InstanceId) ParseEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var text = endpoint.Trim();
        if (text.StartsWith("instance:", StringComparison.OrdinalIgnoreCase) && text[9..].Trim() is { Length: > 0 } instance)
            return (null, instance);
        if (text.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0) return (pid, null);
        throw new RpcException("invalid_endpoint", "Use pid:<positive process id> or instance:<opaque instance id>.");
    }

    private void PublishConnectionChanged()
    {
        if (ConnectionChanged is not { } handlers) return;
        foreach (Action callback in handlers.GetInvocationList())
            try { callback(); } catch { }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        var client = _client;
        var wasConnected = Session is not null;
        _client = null;
        Session = null;
        _capabilities = null;
        _snapshot.Clear();
        _snapshotNotice = null;
        if (client is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                _ = await client.CallToolAsync("detach", cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is McpException or OperationCanceledException or IOException or InvalidOperationException) { }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
                if (await client.Completion.ConfigureAwait(false) is StdioClientCompletionDetails process)
                    LastServerProcessId = process.ProcessId;
            }
        }
        if (wasConnected) PublishConnectionChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await DisconnectAsync().ConfigureAwait(false);
    }
}


