using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Remote;
using Avalonia.Threading;

namespace AvaloniaDevTool.Sample;

public sealed partial class App
{
    private DiagnosticsRemoteServer? _remoteServer;
    private bool _remoteStopping;
    internal static string? RemoteToken { get; private set; }

    private async Task StartRemoteAsync(DiagnosticsAgent agent, MainWindow window)
    {
        try
        {
            var token = Program.RemoteTokenEnvironmentVariable is { } variable
                ? Environment.GetEnvironmentVariable(variable)
                : DiagnosticsRemoteServer.GenerateBearerToken();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("指定的令牌环境变量为空。");
            var server = await DiagnosticsRemoteServer.StartAsync(agent,
                new DiagnosticsRemoteServerOptions
                    { BearerToken = token, Port = Program.RemotePort });
            var accepted = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_remoteStopping)
                    return false;
                _remoteServer = server;
                RemoteToken = token;
                window.ViewModel.RemoteEndpoint = server.Endpoint.AbsoluteUri;
                window.ViewModel.RemoteReady = true;
                return true;
            });
            if (!accepted)
                await server.DisposeAsync();
        }
        catch (Exception error)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.ViewModel.RemoteEndpoint = "远程监听未启动：" + error.GetType().Name;
                window.ViewModel.RemoteReady = false;
            });
        }
    }

    private void StopRemote()
    {
        _remoteStopping = true;
        RemoteToken = null;
        if (_remoteServer is { } server)
            _ = server.DisposeAsync().AsTask();
        _remoteServer = null;
    }
}
