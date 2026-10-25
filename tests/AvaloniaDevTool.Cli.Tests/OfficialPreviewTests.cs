using System.Diagnostics;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit;
using Microsoft.Win32.SafeHandles;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class OfficialPreviewTests
{
    private static string CliPath => Metadata("PreviewCliPath");
    private static string Repository => Metadata("PreviewRepositoryPath");

    [Fact]
    public async Task Official_paths_default_false_real_project_context_live_saves_and_detach_work_over_stdio()
    {
        using var files = new ProjectFiles();
        await files.BuildAsync();
        await using var client = await CreateClientAsync();
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var schema = Assert.Single(tools, tool => tool.Name == "attach-to-file").JsonSchema;
        Assert.Equal(new[] { "xamlFilePath", "xamlProjectPath" }, schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(3, schema.GetProperty("properties").EnumerateObject().Count());
        Assert.False(schema.GetProperty("properties").GetProperty("build").GetProperty("default").GetBoolean());
        var before = File.GetLastWriteTimeUtc(files.Output);
        var attached = Read<PreviewAttachment>(await CallAsync(client, "attach-to-file", files.Arguments()));
        var pid = attached.Session.ProcessId;
        try
        {
            Assert.Equal(files.Source, attached.FilePath);
            Assert.Equal(files.Project, attached.ProjectPath);
            Assert.Equal(files.Output, attached.AssemblyPath);
            Assert.Equal(before, File.GetLastWriteTimeUtc(files.Output));
            Assert.False(File.Exists(files.BuildMarker));
            Assert.Equal("Project App resource", (await FindAsync(client, "ProjectResourceText")).Preview);
            Assert.Equal("Built custom control", (await FindAsync(client, "CustomControlText")).Preview);
            Assert.Equal("Referenced assembly control", (await FindAsync(client, "ReferencedControlText")).Preview);
            var original = await FindAsync(client, "SavedText");
            var originalImage = await ScreenshotAsync(client, (await FindTypeAsync(client, "AvaloniaDevTool.PreviewFixture.FixtureView")).Id);
            files.Write(files.Initial.Replace("First source", "官方路径 热更新", StringComparison.Ordinal));
            var second = await WaitForNodeAsync(client, "SavedText", node => node?.Preview == "官方路径 热更新");
            Assert.NotEqual(original.Id, second.Id);
            Assert.NotEqual(originalImage, await ScreenshotAsync(client, (await FindTypeAsync(client, "AvaloniaDevTool.PreviewFixture.FixtureView")).Id));
            Assert.True((await CallAsync(client, "props", new() { ["nodeId"] = original.Id })).IsError);
            files.Write("<UserControl");
            await WaitForNodeAsync(client, "PreviewErrorText", node => node?.Preview?.Contains("xaml_load_failed", StringComparison.Ordinal) == true);
            Assert.Equal(second.Id, (await FindAsync(client, "SavedText")).Id);
            files.Write(files.Initial.Replace("First source", "Recovered official preview", StringComparison.Ordinal));
            await WaitForNodeAsync(client, "SavedText", node => node?.Preview == "Recovered official preview");
            Assert.NotEqual(true, (await CallAsync(client, "detach", new())).IsError);
            await WaitForExitAsync(pid);
            Assert.True((await CallAsync(client, "tree", new())).IsError);
        }
        finally { await CallAsync(client, "detach", new()); }
    }

    [Fact]
    public async Task Build_true_creates_the_selected_multitarget_output_before_attaching_and_can_switch_files()
    {
        using var files = new ProjectFiles(multitarget: true);
        await using var client = await CreateClientAsync();
        var first = Read<PreviewAttachment>(await CallAsync(client, "attach-to-file", files.Arguments(build: true)));
        Assert.Equal(files.Output, first.AssemblyPath);
        Assert.True(File.Exists(files.BuildMarker));
        Assert.True(File.Exists(files.Output));
        var oldNode = await FindAsync(client, "SavedText");
        var window = files.Arguments();
        window["xamlFilePath"] = Path.Combine(files.Root, "FixtureWindow.axaml");
        var second = Read<PreviewAttachment>(await CallAsync(client, "attach-to-file", window));
        try
        {
            Assert.NotEqual(first.Session.ProcessId, second.Session.ProcessId);
            await WaitForExitAsync(first.Session.ProcessId);
            Assert.Equal("Project App resource", (await FindAsync(client, "WindowResourceText")).Preview);
            Assert.True((await CallAsync(client, "props", new() { ["nodeId"] = oldNode.Id })).IsError);
        }
        finally { await CallAsync(client, "detach", new()); }
        await WaitForExitAsync(second.Session.ProcessId);
    }

    [Fact]
    public async Task Missing_build_compile_failure_invalid_xaml_and_invalid_paths_never_create_an_attachment()
    {
        using var files = new ProjectFiles();
        await using var client = await CreateClientAsync();
        var missing = await CallAsync(client, "attach-to-file", files.Arguments(build: false));
        Assert.True(missing.IsError);
        Assert.Contains("project_not_built", Text(missing));
        Assert.False(File.Exists(files.BuildMarker));
        await File.WriteAllTextAsync(Path.Combine(files.Root, "Broken.cs"), "this is not valid C#;", Encoding.UTF8, TestContext.Current.CancellationToken);
        var failed = await CallAsync(client, "attach-to-file", files.Arguments(build: true));
        Assert.True(failed.IsError);
        Assert.Contains("project_build_failed", Text(failed));
        Assert.True((await CallAsync(client, "tree", new())).IsError);
        File.Delete(Path.Combine(files.Root, "Broken.cs"));
        await files.BuildAsync();
        await File.WriteAllTextAsync(Path.Combine(files.Root, "Broken.cs"), "still not valid C#;", Encoding.UTF8, TestContext.Current.CancellationToken);
        Assert.Contains("project_build_failed", Text(await CallAsync(client, "attach-to-file", files.Arguments(build: true))));
        Assert.True((await CallAsync(client, "tree", new())).IsError);
        File.Delete(Path.Combine(files.Root, "Broken.cs"));
        files.Write("<UserControl");
        var invalid = await CallAsync(client, "attach-to-file", files.Arguments());
        Assert.True(invalid.IsError);
        Assert.Contains("xaml_load_failed", Text(invalid));
        Assert.True((await CallAsync(client, "tree", new())).IsError);
        var relative = files.Arguments();
        relative["xamlFilePath"] = "FixtureView.axaml";
        Assert.Contains("invalid_path", Text(await CallAsync(client, "attach-to-file", relative)));
        var outside = files.Arguments();
        outside["xamlFilePath"] = Path.Combine(Repository, "tests", "AvaloniaDevTool.PreviewFixture", "FixtureView.axaml");
        Assert.Contains("source_outside_project", Text(await CallAsync(client, "attach-to-file", outside)));
    }

    [Fact]
    public async Task Official_startup_cancellation_stops_the_candidate_and_leaves_no_attachment()
    {
        using var files = new ProjectFiles();
        await files.BuildAsync();
        var childPid = Path.Combine(files.Root, "blocked-child.pid");
        await using var client = await CreateClientAsync(new() { ["AVDT_OFFICIAL_PREVIEW_BLOCK"] = childPid });
        var request = new RequestId("official-preview-cancel");
        var pending = client.SendRequestAsync(new JsonRpcRequest
        {
            Id = request, Method = "tools/call", Params = JsonSerializer.SerializeToNode(new { name = "attach-to-file", arguments = files.Arguments() })
        }, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => File.Exists(childPid));
        var pid = int.Parse((await File.ReadAllTextAsync(childPid, Encoding.UTF8, TestContext.Current.CancellationToken)).Trim('\uFEFF', ' ', '\r', '\n'));
        await client.SendMessageAsync(new JsonRpcNotification
        {
            Method = "notifications/cancelled", Params = JsonSerializer.SerializeToNode(new CancelledNotificationParams { RequestId = request })
        }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await WaitForExitAsync(pid);
        Assert.True((await CallAsync(client, "tree", new())).IsError);
    }

    [Fact]
    public async Task Cancelling_a_file_switch_keeps_the_previous_preview_and_releases_only_the_candidate()
    {
        using var files = new ProjectFiles();
        await files.BuildAsync();
        var candidatePid = Path.Combine(files.Root, "blocked-window.pid");
        await using var client = await CreateClientAsync(new() { ["AVDT_OFFICIAL_PREVIEW_BLOCK_WINDOW"] = candidatePid });
        var first = Read<PreviewAttachment>(await CallAsync(client, "attach-to-file", files.Arguments()));
        var original = await FindAsync(client, "SavedText");
        try
        {
            var arguments = files.Arguments();
            arguments["xamlFilePath"] = Path.Combine(files.Root, "FixtureWindow.axaml");
            var request = new RequestId("official-switch-cancel");
            var pending = client.SendRequestAsync(new JsonRpcRequest
            {
                Id = request, Method = "tools/call", Params = JsonSerializer.SerializeToNode(new { name = "attach-to-file", arguments })
            }, TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => File.Exists(candidatePid));
            var pid = int.Parse((await File.ReadAllTextAsync(candidatePid, Encoding.UTF8, TestContext.Current.CancellationToken)).Trim('\uFEFF', ' ', '\r', '\n'));
            await client.SendMessageAsync(new JsonRpcNotification
            {
                Method = "notifications/cancelled", Params = JsonSerializer.SerializeToNode(new CancelledNotificationParams { RequestId = request })
            }, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            await WaitForExitAsync(pid);
            Assert.Equal(original.Id, (await FindAsync(client, "SavedText")).Id);
            Assert.NotEqual(true, (await CallAsync(client, "props", new() { ["nodeId"] = original.Id })).IsError);
            using var previous = Process.GetProcessById(first.Session.ProcessId);
            Assert.False(previous.HasExited);
        }
        finally { await CallAsync(client, "detach", new()); }
        await WaitForExitAsync(first.Session.ProcessId);
    }

    [Fact]
    public async Task Abrupt_stdio_owner_exit_stops_a_preview_blocked_in_the_real_project_builder()
    {
        using var files = new ProjectFiles();
        await files.BuildAsync();
        var childPid = Path.Combine(files.Root, "owner-child.pid");
        using var owner = StartOwner(new() { ["AVDT_OFFICIAL_PREVIEW_BLOCK"] = childPid });
        var errors = owner.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await InitializeOwnerAsync(owner);
            await owner.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "attach-to-file", arguments = files.Arguments() } }));
            await WaitUntilAsync(() => File.Exists(childPid));
            var pid = int.Parse((await File.ReadAllTextAsync(childPid, Encoding.UTF8, TestContext.Current.CancellationToken)).Trim('\uFEFF', ' ', '\r', '\n'));
            owner.Kill();
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            await WaitForExitAsync(pid);
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            await errors;
        }
    }

    [Fact]
    public async Task Windows_abrupt_owner_exit_kills_an_in_progress_build_and_its_owned_descendant()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new ProjectFiles(blockBuild: true);
        using var owner = StartOwner();
        var errors = owner.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await InitializeOwnerAsync(owner);
            await owner.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "attach-to-file", arguments = files.Arguments(build: true) } }));
            await WaitUntilAsync(() => File.Exists(files.BuildPid));
            var pid = int.Parse((await File.ReadAllTextAsync(files.BuildPid, Encoding.UTF8, TestContext.Current.CancellationToken)).Trim('\uFEFF', ' ', '\r', '\n'));
            owner.Kill();
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            await WaitForExitAsync(pid);
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            await errors;
        }
    }

    [Fact]
    public async Task Windows_build_request_cancellation_stops_the_build_and_its_owned_descendant()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new ProjectFiles(blockBuild: true);
        await using var client = await CreateClientAsync();
        var request = new RequestId("official-build-cancel");
        var pending = client.SendRequestAsync(new JsonRpcRequest
        {
            Id = request, Method = "tools/call", Params = JsonSerializer.SerializeToNode(new { name = "attach-to-file", arguments = files.Arguments(build: true) })
        }, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => File.Exists(files.BuildPid));
        var pid = int.Parse((await File.ReadAllTextAsync(files.BuildPid, Encoding.UTF8, TestContext.Current.CancellationToken)).Trim('\uFEFF', ' ', '\r', '\n'));
        await client.SendMessageAsync(new JsonRpcNotification
        {
            Method = "notifications/cancelled", Params = JsonSerializer.SerializeToNode(new CancelledNotificationParams { RequestId = request })
        }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await WaitForExitAsync(pid);
        Assert.True((await CallAsync(client, "tree", new())).IsError);
    }

    [Fact]
    public async Task Windows_fixture_prebuild_releases_a_detached_child_holding_its_project_directory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new ProjectFiles(detachBuildChild: true);
        await files.BuildAsync();
        var pid = int.Parse((await File.ReadAllTextAsync(files.DetachedBuildPid, Encoding.UTF8,
            TestContext.Current.CancellationToken)).Trim('\uFEFF', ' ', '\r', '\n'));
        await WaitForExitAsync(pid);
    }

    private static async Task InitializeOwnerAsync(Process owner)
    {
        await owner.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "official-preview-owner", version = "1" } }
        }));
        var initialized = JsonNode.Parse((await owner.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken))!);
        Assert.NotNull(initialized?["result"]);
        await owner.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
    }

    private static Process StartOwner(Dictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(CliPath);
        start.ArgumentList.Add("mcp"); start.ArgumentList.Add("--profile"); start.ArgumentList.Add("official");
        start.Environment["AVDT_PREVIEW_HEADLESS"] = "true";
        foreach (var item in environment ?? []) start.Environment[item.Key] = item.Value;
        return Process.Start(start)!;
    }

    private static Task<McpClient> CreateClientAsync(Dictionary<string, string?>? environment = null)
    {
        environment ??= new();
        environment["AVDT_PREVIEW_HEADLESS"] = "true";
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        { Command = "dotnet", Name = "official project preview", Arguments = [CliPath, "mcp", "--profile", "official"], EnvironmentVariables = environment, ShutdownTimeout = TimeSpan.FromSeconds(5) }));
    }

    private static Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments) =>
        client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken).AsTask();

    private static async Task<TreeNodeInfo> FindAsync(McpClient client, string name) => Assert.Single(
        Read<TreeSearchResult>(await CallAsync(client, "search", new() { ["query"] = name })).Matches, node => node.Name == name);

    private static async Task<TreeNodeInfo> FindTypeAsync(McpClient client, string type) => Assert.Single(
        Read<TreeSearchResult>(await CallAsync(client, "search", new() { ["query"] = type })).Matches, node => node.TypeName == type);

    private static async Task<string> ScreenshotAsync(McpClient client, long nodeId)
    {
        var result = await CallAsync(client, "screenshot", new() { ["nodeId"] = nodeId });
        Assert.False(result.IsError == true, Text(result));
        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        using var bitmap = SKBitmap.Decode(image.DecodedData.ToArray());
        Assert.NotNull(bitmap);
        Assert.True(bitmap.Width > 1 && bitmap.Height > 1);
        Assert.True(bitmap.Pixels.Distinct().Take(3).Count() >= 3,
            $"The project preview must render multiple colors, found {bitmap.Pixels.Distinct().Count()} in {bitmap.Width}x{bitmap.Height}.");
        return Convert.ToBase64String(image.DecodedData.Span);
    }

    private static async Task<TreeNodeInfo> WaitForNodeAsync(McpClient client, string name, Func<TreeNodeInfo?, bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var call = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = name }, cancellationToken: timeout.Token);
            if (call.IsError == true && Text(call).Contains("stale_session", StringComparison.Ordinal))
            {
                await Task.Delay(50, timeout.Token);
                continue;
            }
            var result = Read<TreeSearchResult>(call);
            var node = result.Matches.FirstOrDefault(value => value.Name == name);
            if (predicate(node)) return node!;
            await Task.Delay(50, timeout.Token);
        }
    }

    private static Task WaitForExitAsync(int pid) => WaitUntilAsync(() =>
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        while (!predicate()) await Task.Delay(40, timeout.Token);
    }

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(item => item.Text));
    private static T Read<T>(CallToolResult result)
    {
        Assert.False(result.IsError == true, Text(result));
        var root = result.StructuredContent is { } structured ? JsonNode.Parse(JsonSerializer.Serialize(structured)) : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped)) root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }
    private static string Metadata(string key) => Path.GetFullPath(typeof(OfficialPreviewTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == key).Value!);

    private sealed class ProjectFiles : IDisposable
    {
        private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "AvaloniaDevTool.OfficialPreview.Tests");
        private readonly string _container = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(_container, "中文 空格 预览项目");
        public string Project => Path.Combine(Root, "Fixture 项目.csproj");
        public string Source => Path.Combine(Root, "FixtureView.axaml");
        public string Output => Path.Combine(Root, "bin", "Debug", "net10.0", "AvaloniaDevTool.PreviewFixture.dll");
        public string BuildMarker => Path.Combine(Root, "build.marker");
        public string BuildPid => Path.Combine(Root, "build.pid");
        public string DetachedBuildPid => Path.Combine(Root, "detached-build-child.pid");
        public string Initial { get; }
        public ProjectFiles(bool multitarget = false, bool blockBuild = false, bool detachBuildChild = false)
        {
            Directory.CreateDirectory(Root);
            var controls = Path.Combine(_container, "AvaloniaDevTool.PreviewFixture.Controls");
            Directory.CreateDirectory(controls);
            foreach (var file in Directory.GetFiles(Path.Combine(Repository, "tests", "AvaloniaDevTool.PreviewFixture.Controls"), "*", SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(controls, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(_container, "Directory.Build.props"), """
                <Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AvaloniaVersion>12.1.4</AvaloniaVersion></PropertyGroup></Project>
                """, Encoding.UTF8);
            var source = Path.Combine(Repository, "tests", "AvaloniaDevTool.PreviewFixture");
            foreach (var file in Directory.GetFiles(source, "*.axaml")) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
            var program = File.ReadAllText(Path.Combine(source, "Program.cs"), Encoding.UTF8).Replace("public static AppBuilder BuildAvaloniaApp()\n    {", "public static AppBuilder BuildAvaloniaApp()\n    {\n        if (Environment.GetEnvironmentVariable(\"AVDT_OFFICIAL_PREVIEW_BLOCK\") is { } pid) { Program.PublishPid(pid); Thread.Sleep(TimeSpan.FromSeconds(60)); }", StringComparison.Ordinal);
            // Repository files may use CRLF. Preserve the actual builder rather than relying on line endings.
            program = program.Replace("public static AppBuilder BuildAvaloniaApp()\r\n    {", "public static AppBuilder BuildAvaloniaApp()\r\n    {\r\n        if (Environment.GetEnvironmentVariable(\"AVDT_OFFICIAL_PREVIEW_BLOCK\") is { } pid) { Program.PublishPid(pid); Thread.Sleep(TimeSpan.FromSeconds(60)); }", StringComparison.Ordinal);
            program = program.Replace("public FixtureWindow() => AvaloniaXamlLoader.Load(this);", "public FixtureWindow() { if (Environment.GetEnvironmentVariable(\"AVDT_OFFICIAL_PREVIEW_BLOCK_WINDOW\") is { } pid) { Program.PublishPid(pid); Thread.Sleep(TimeSpan.FromSeconds(60)); } AvaloniaXamlLoader.Load(this); }", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(Root, "Program.cs"), program, Encoding.UTF8);
            var framework = multitarget ? "<TargetFramework /><TargetFrameworks>net10.0-windows;net10.0</TargetFrameworks>" : "<TargetFramework>net10.0</TargetFramework>";
            var blocked = blockBuild ? "<Exec Command=\"powershell -NoProfile -ExecutionPolicy Bypass -File &quot;$(MSBuildProjectDirectory)/block-build.ps1&quot;\" />" : "";
            var detached = detachBuildChild ? "<Target Name=\"StartDetachedBuildChild\" AfterTargets=\"Build\"><Exec Command=\"powershell -NoProfile -ExecutionPolicy Bypass -File &quot;$(MSBuildProjectDirectory)/detach-build-child.ps1&quot;\" /></Target>" : "";
            File.WriteAllText(Project, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><AssemblyName>AvaloniaDevTool.PreviewFixture</AssemblyName><AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>{{framework}}</PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Avalonia.Desktop" Version="$(AvaloniaVersion)" />
                    <PackageReference Include="Avalonia.Headless" Version="$(AvaloniaVersion)" />
                    <PackageReference Include="Avalonia.Themes.Fluent" Version="$(AvaloniaVersion)" />
                    <ProjectReference Include="../AvaloniaDevTool.PreviewFixture.Controls/AvaloniaDevTool.PreviewFixture.Controls.csproj" />
                  </ItemGroup>
                  <Target Name="RecordOfficialBuild" BeforeTargets="CoreCompile"><WriteLinesToFile File="$(MSBuildProjectDirectory)/build.marker" Lines="real build" Overwrite="true" />{{blocked}}</Target>
                  {{detached}}
                </Project>
                """, Encoding.UTF8);
            if (blockBuild) File.WriteAllText(Path.Combine(Root, "block-build.ps1"), "$PID | Set-Content -LiteralPath '" + BuildPid.Replace("'", "''", StringComparison.Ordinal) + "' -Encoding utf8\nStart-Sleep -Seconds 60\n", Encoding.UTF8);
            if (detachBuildChild)
            {
                var childScript = Path.Combine(Root, "owned-build-child.ps1");
                File.WriteAllText(childScript, "[System.IO.File]::WriteAllText('" + DetachedBuildPid.Replace("'", "''", StringComparison.Ordinal) + "', $PID.ToString(), [System.Text.UTF8Encoding]::new($false))\nStart-Sleep -Seconds 60\n", Encoding.UTF8);
                File.WriteAllText(Path.Combine(Root, "detach-build-child.ps1"), $$"""
                    $taskShell = (Get-Process -Id $PID).Path
                    $taskChild = Start-Process -FilePath $taskShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', '"{{childScript.Replace("'", "''", StringComparison.Ordinal)}}"') -WorkingDirectory '{{controls.Replace("'", "''", StringComparison.Ordinal)}}' -WindowStyle Hidden -PassThru
                    $taskWatch = [System.Diagnostics.Stopwatch]::StartNew()
                    while (-not [System.IO.File]::Exists('{{DetachedBuildPid.Replace("'", "''", StringComparison.Ordinal)}}') -and $taskWatch.ElapsedMilliseconds -lt 5000) { Start-Sleep -Milliseconds 10 }
                    if (-not [System.IO.File]::Exists('{{DetachedBuildPid.Replace("'", "''", StringComparison.Ordinal)}}')) { throw 'The owned build child did not start.' }
                    """, Encoding.UTF8);
            }
            Initial = File.ReadAllText(Source, Encoding.UTF8);
        }
        public Dictionary<string, object?> Arguments(bool? build = null)
        {
            var arguments = new Dictionary<string, object?> { ["xamlFilePath"] = Source, ["xamlProjectPath"] = Project };
            if (build is not null) arguments["build"] = build.Value;
            return arguments;
        }
        public void Write(string value) => File.WriteAllText(Source, value, new UTF8Encoding(false));
        public async Task BuildAsync()
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "build", Project, "--framework", "net10.0", "--nologo", "-nodeReuse:false", "-property:UseSharedCompilation=false" }) start.ArgumentList.Add(argument);
            start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            using var job = OwnedTestBuildJob.Create();
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            try
            {
                job?.Assign(process);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                // BuildServices starts a detached collector with the project directory as its CWD.
                // A successful parent exit does not release those owned descendants or inherited pipes.
                job?.Dispose();
                Assert.True(process.ExitCode == 0, await output + await errors);
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Win32Exception) when (process.HasExited) { }
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                job?.Dispose();
                await Task.WhenAll(output, errors);
            }
            Assert.True(File.Exists(Output));
            File.Delete(BuildMarker);
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(_container);
            if (!absolute.StartsWith(Path.GetFullPath(TestRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cleanup escaped the official preview temporary root.");
            // Preview owners and children have exited before disposal; Windows can briefly retain directory handles.
            var cleanup = Stopwatch.StartNew();
            var retryLimit = TimeSpan.FromSeconds(2);
            while (Directory.Exists(absolute))
            {
                try { Directory.Delete(absolute, recursive: true); return; }
                catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && cleanup.Elapsed < retryLimit)
                {
                    var remaining = retryLimit - cleanup.Elapsed;
                    if (remaining > TimeSpan.Zero)
                        Thread.Sleep(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50));
                }
            }
        }
    }

    private sealed class OwnedTestBuildJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        private OwnedTestBuildJob() : base(ownsHandle: true) { }
        public static OwnedTestBuildJob? Create()
        {
            if (!OperatingSystem.IsWindows()) return null;
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the fixture build job.");
            var limits = new ExtendedLimit { BasicLimitInformation = new BasicLimit { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>()))
            {
                var error = Marshal.GetLastWin32Error(); job.Dispose();
                throw new Win32Exception(error, "Could not configure the fixture build job.");
            }
            return job;
        }
        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(this, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not own the fixture build process.");
        }
        protected override bool ReleaseHandle() => CloseHandle(handle);
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
        {
            public BasicLimit BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern OwnedTestBuildJob CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(OwnedTestBuildJob job, int informationClass, ref ExtendedLimit information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(OwnedTestBuildJob job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
