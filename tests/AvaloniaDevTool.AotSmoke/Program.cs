using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;

if (args.Length != 3 || args[2] is not ("aot" or "trimmed"))
    throw new ArgumentException("Usage: AvaloniaDevTool.AotSmoke TARGET_EXE OUTPUT_DIRECTORY aot|trimmed");
var executable = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var ready = Path.Combine(output, "target-ready.txt");
File.Delete(ready);
var pipe = $"avalonia-devtool-aot-smoke-{Guid.NewGuid():N}";
var start = new ProcessStartInfo(executable)
    { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
start.ArgumentList.Add("--pipe");
start.ArgumentList.Add(pipe);
start.ArgumentList.Add("--ready-file");
start.ArgumentList.Add(ready);
using var target = Process.Start(start) ?? throw new InvalidOperationException("The consumer process did not start.");
var stdout = target.StandardOutput.ReadToEndAsync();
var stderr = target.StandardError.ReadToEndAsync();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
var report = new List<string>();
try
{
    var readiness = "";
    while (true)
    {
        if (target.HasExited)
            throw new InvalidOperationException($"Consumer exited before readiness: {target.ExitCode}");
        if (File.Exists(ready))
        {
            try
            {
                readiness = await File.ReadAllTextAsync(ready, Encoding.UTF8, timeout.Token);
            }
            catch (IOException)
            {
            }

            if (readiness.Contains($"pid={target.Id}\n") && readiness.Contains("dynamicCodeCompiled=") &&
                readiness.EndsWith('\n'))
                break;
        }

        await Task.Delay(100, timeout.Token);
    }

    if (args[2] == "aot" && (!readiness.Contains("dynamicCodeSupported=False") ||
                             !readiness.Contains("dynamicCodeCompiled=False")))
        throw new InvalidOperationException("The target did not identify itself as Native AOT.");
    if (args[2] == "trimmed" && !readiness.Contains("dynamicCodeSupported=True"))
        throw new InvalidOperationException("The trimmed target unexpectedly disabled dynamic code.");
    report.Add($"PASS runtime={args[2]}, pid={target.Id}");
    await using var client = new DiagnosticsClient();
    await client.ConnectAsync(pipe, timeout.Token);
    if (client.Session?.ProcessId != target.Id)
        throw new InvalidOperationException("Connected to the wrong target.");
    report.Add("PASS real DiagnosticsClient named-pipe handshake");
    var notifications = new ConcurrentQueue<long>();
    var picking = new ConcurrentQueue<bool>();
    var selections = new ConcurrentQueue<long>();
    client.Properties.PropertiesChanged += value => notifications.Enqueue(value.ObjectId);
    client.Selection.PickingChanged += value => picking.Enqueue(value.IsPicking);
    client.Selection.SelectionChanged += value => selections.Enqueue(value.ObjectId);
    TreeNodeInfo? status = null;
    TreeNodeInfo? button = null;
    long rootId = 0;
    foreach (var kind in new[] { TreeKind.Visual, TreeKind.Logical, TreeKind.Merged })
    {
        TreeNodeInfo? kindStatus = null;
        TreeNodeInfo? kindButton = null;
        var roots = await client.Tree.GetRootsAsync(kind, timeout.Token);
        if (roots.Count != 1 || roots[0].Name != "AotWindow")
            throw new InvalidOperationException($"Unexpected {kind} roots.");
        rootId = roots[0].Id;
        var queue = new Queue<TreeNodeInfo>(roots);
        var seen = new HashSet<long>();
        while (queue.TryDequeue(out var node))
        {
            if (!seen.Add(node.Id))
                continue;
            if (node.Name == "AotStatus")
                kindStatus = node;
            if (node.Name == "AotButton")
                kindButton = node;
            if (node.HasChildren)
                foreach (var child in await client.Tree.GetChildrenAsync(node.Id, kind, timeout.Token))
                    queue.Enqueue(child);
        }

        if (kindStatus is null || kindButton is null)
            throw new InvalidOperationException($"Consumer controls missing from {kind} tree.");
        status = kindStatus;
        button = kindButton;
        report.Add($"PASS {kind} tree, {seen.Count} nodes");
    }

    var panel = (await client.Tree.SearchTreeAsync(new TreeSearchRequest("AotPanel", TreeKind.Logical), timeout.Token))
        .Matches.Single();
    var firstPage =
        await client.Tree.GetChildrenPageAsync(panel.Id, TreeKind.Logical, count: 1, cancellationToken: timeout.Token);
    if (firstPage.Children.Count != 1 || firstPage.TotalCount != 3 || !firstPage.HasMore ||
        firstPage.NextCursor is null)
        throw new InvalidOperationException("The first bounded child page did not describe the consumer panel.");
    report.Add("PASS bounded tree.children-page first page and continuation metadata");
    var popupStatus = await client.Popups.GetPopupFreezeAsync(timeout.Token);
    if (popupStatus.IsActive || popupStatus.Popups.Count != 0 || popupStatus.Roots.All(root => root.RootId != rootId))
        throw new InvalidOperationException("Popup-freeze status did not describe the consumer's unfrozen root.");
    report.Add("PASS popup-freeze read-only status and generated DTO metadata");
    var allProperties = await client.Properties.GetPropertiesAsync(status!.Id, timeout.Token);
    var properties =
        await client.Properties.GetPropertiesAsync(new PropertiesRequest(status.Id, Contextual: true), timeout.Token);
    if (properties.Any(property => property.Presentation?.Context?.IsApplicable == false) ||
        properties.Any(property => allProperties.All(original => original.Id != property.Id)))
        throw new InvalidOperationException(
            "The contextual property request returned inapplicable or unknown properties.");
    report.Add(
        $"PASS PropertiesRequest Contextual=true, {allProperties.Count} raw / {properties.Count} contextual properties");
    var text = properties.Single(property =>
        property.Name == "Text" && property.DeclaringType == "Avalonia.Controls.TextBlock");
    if (text.Value != "Native diagnostics ready" || !text.CanEdit || text.CanClear)
        throw new InvalidOperationException($"Unexpected baseline Text: {text}");
    await client.Properties.SetPropertyAsync(status.Id, text.Id, "Edited over the native wire", timeout.Token);
    var changed =
        (await client.Properties.GetPropertiesAsync(status.Id, timeout.Token)).Single(property =>
            property.Id == text.Id);
    if (changed.Value != "Edited over the native wire" || !changed.CanClear ||
        changed.ValueSource != "DiagnosticOverride")
        throw new InvalidOperationException("The diagnostic property override was not applied.");
    await WaitUntilAsync(() => notifications.Contains(status.Id), timeout.Token);
    await client.Properties.ClearPropertyAsync(status.Id, text.Id, timeout.Token);
    var restored =
        (await client.Properties.GetPropertiesAsync(status.Id, timeout.Token)).Single(property =>
            property.Id == text.Id);
    if (restored.Value != text.Value || restored.ValueSource != text.ValueSource || restored.CanClear)
        throw new InvalidOperationException("Clearing did not restore the original styled value and priority.");
    report.Add("PASS property read/write/notification/clear restores original Style value");
    var image = await client.Rendering.CaptureElementAsync(rootId, timeout.Token);
    var png = Convert.FromBase64String(image.PngBase64);
    if (image.Width <= 0 || image.Height <= 0 ||
        !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        throw new InvalidOperationException("Screenshot did not contain a valid PNG.");
    await File.WriteAllBytesAsync(Path.Combine(output, "native-consumer.png"), png, timeout.Token);
    report.Add($"PASS screenshot {image.Width}x{image.Height}, {png.Length} bytes");
    await client.Selection.SetPickingAsync(true, timeout.Token);
    await WaitUntilAsync(() => picking.Contains(true), timeout.Token);
    var input = await client.Input.SendInputAsync(new InputRequest(button!.Id, InputKind.Click), timeout.Token);
    if (!input.Delivered)
        throw new InvalidOperationException(input.Detail);
    await WaitUntilAsync(() => selections.Contains(button.Id) && picking.Contains(false), timeout.Token);
    report.Add("PASS picking start, routed pointer selection, automatic stop notifications");
    await client.Properties.SetPropertyAsync(status.Id, text.Id, "Restore on session disconnect", timeout.Token);
    await client.DisconnectAsync();
    await client.ConnectAsync(pipe, timeout.Token);
    var reconnected =
        await client.Tree.SearchTreeAsync(new TreeSearchRequest("AotStatus", TreeKind.Logical), timeout.Token);
    var reconnectedText =
        (await client.Properties.GetPropertiesAsync(reconnected.Matches.Single().Id, timeout.Token)).Single(property =>
            property.Id == text.Id);
    if (reconnectedText.Value != text.Value || reconnectedText.CanClear)
        throw new InvalidOperationException("A reconnect did not preserve the restored target state.");
    report.Add("PASS disconnect restores an active diagnostic override; reconnect reads original Style state");
    foreach (var line in report)
        Console.WriteLine(line);
    await File.WriteAllLinesAsync(Path.Combine(output, "smoke-result.txt"), report, Encoding.UTF8);
    return 0;
}
catch (Exception exception)
{
    report.Add($"FAIL {exception}");
    foreach (var line in report)
        Console.Error.WriteLine(line);
    await File.WriteAllLinesAsync(Path.Combine(output, "smoke-result.txt"), report, Encoding.UTF8);
    return 1;
}
finally
{
    if (!target.HasExited)
        target.Kill(entireProcessTree: true);
    await target.WaitForExitAsync();
    await File.WriteAllTextAsync(Path.Combine(output, "target-stdout.txt"), await stdout, Encoding.UTF8);
    await File.WriteAllTextAsync(Path.Combine(output, "target-stderr.txt"), await stderr, Encoding.UTF8);
}

static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
{
    for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        await Task.Delay(50, cancellationToken);
    if (!condition())
        throw new TimeoutException("The expected target notification did not arrive.");
}
