using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Remote;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class RemoteInspectorTests
{
    [AvaloniaFact]
    public async Task Remote_viewmodel_connects_edits_restores_and_reconnects_without_exposing_token()
    {
        var text = new TextBlock { Name = "RemoteInspectorTarget", Text = "Original" };
        var target = new Window { Content = text };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "remote-inspector-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            var token = DiagnosticsRemoteServer.GenerateBearerToken();
            await using var server = await DiagnosticsRemoteServer.StartAsync(agent, new() { BearerToken = token });
            await using var vm = new MainWindowViewModel(server.Endpoint.AbsoluteUri, remoteBearerToken: token) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            Assert.True(vm.IsConnected, vm.Status);
            Assert.True(vm.IsRemoteEndpoint);
            Assert.Contains("WebSocket", vm.BackendModeNotice);
            Assert.DoesNotContain(token, vm.Status + vm.BackendModeNotice + vm.SessionSummary);
            var originalSession = vm.SessionSummary;
            vm.SelectedNode = Assert.Single(Assert.Single(vm.Roots).Children, node => node.Node.Name == text.Name);
            await WaitUntilAsync(() => vm.FilteredProperties.Any(row => row.Name == "Text" && row.DeclaringType == typeof(TextBlock).FullName));
            var row = Assert.Single(vm.FilteredProperties, item => item.Name == "Text" && item.DeclaringType == typeof(TextBlock).FullName);
            row.Draft = "Remote edit";
            row.ApplyCommand.Execute(null);
            await WaitUntilAsync(() => text.Text == "Remote edit");
            vm.DisconnectCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsConnected && vm.Roots.Count == 0);
            await WaitUntilAsync(() => text.Text == "Original");
            vm.ConnectCommand.Execute(null);
            await WaitUntilAsync(() => vm.IsConnected && vm.Roots.Count > 0);
            Assert.Equal(originalSession, vm.SessionSummary);
            vm.DisconnectCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsConnected && vm.Roots.Count == 0);
            vm.PipeName = "avalonia-devtool-local-next";
            Assert.Empty(vm.RemoteBearerToken);
            Assert.False(vm.IsRemoteEndpoint);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Empty_or_wrong_remote_token_can_be_corrected_and_the_input_is_masked()
    {
        var target = new Window { Content = new TextBlock { Text = "Remote auth" } };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "remote-auth-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            var token = DiagnosticsRemoteServer.GenerateBearerToken();
            await using var server = await DiagnosticsRemoteServer.StartAsync(agent, new() { BearerToken = token });
            await using var vm = new MainWindowViewModel(server.Endpoint.AbsoluteUri);
            await vm.InitializeAsync();
            Assert.False(vm.IsConnected);
            Assert.False(vm.ConnectCommand.CanExecute(null));
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                var input = Assert.IsType<TextBox>(window.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => Avalonia.Automation.AutomationProperties.GetAutomationId(box) == "Inspector.RemoteToken"));
                Assert.NotEqual(default, input.PasswordChar);
                vm.RemoteBearerToken = DiagnosticsRemoteServer.GenerateBearerToken();
                vm.ConnectCommand.Execute(null);
                await WaitUntilAsync(() => vm.CanChangeConnection && vm.ConnectCommand.CanExecute(null));
                Assert.False(vm.IsConnected);
                Assert.DoesNotContain(vm.RemoteBearerToken, vm.Status);
                vm.RemoteBearerToken = token;
                vm.ConnectCommand.Execute(null);
                await WaitUntilAsync(() => vm.IsConnected);
            }
            finally { window.Close(); }
        }
        finally { target.Close(); }
    }

    [Fact]
    public void Launch_options_read_only_the_named_token_environment_and_reject_ambiguous_backends()
    {
        var name = "AVDT_TEST_TOKEN_" + Guid.NewGuid().ToString("N");
        var token = DiagnosticsRemoteServer.GenerateBearerToken();
        Environment.SetEnvironmentVariable(name, token);
        try
        {
            var options = InspectorLaunchOptions.Parse(["--endpoint", "ws://127.0.0.1:12345/diagnostics", "--token-env", name]);
            Assert.Equal(token, options.ReadRemoteToken());
            Assert.DoesNotContain(token, options.ToString());
            Assert.Throws<ArgumentException>(() => InspectorLaunchOptions.Parse(["--pipe", "local", "--endpoint", "ws://127.0.0.1:12345/"]));
            Assert.Throws<ArgumentException>(() => InspectorLaunchOptions.Parse(["--endpoint", "http://127.0.0.1:12345/"]));
            Assert.Throws<ArgumentException>(() => InspectorLaunchOptions.Parse(["--backend", "xamlmcp", "--token-env", name]));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
