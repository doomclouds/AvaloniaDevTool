using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class ProfilerIntegrationTests
{
    [AvaloniaFact]
    public void Late_start_captures_real_framework_style_attach_class_evaluation_and_resource_lookups()
    {
        // Constructing and showing the host precedes capture; the diagnostic static initializer has already run.
        var panel = new StackPanel();
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            var diagnostic = typeof(AvaloniaObject).Assembly.GetType("Avalonia.Diagnostics.Diagnostic")!;
            var enabled =
                diagnostic.GetProperty("IsEnabled", BindingFlags.Static | BindingFlags.Public)!.GetValue(null);
            using var service = new ProfilerService((_, _) => { });
            service.Start(new ProfilerStartRequest(Filter: "ProfilerProbe", NotificationIntervalMs: 5000));
            Assert.Equal(enabled,
                diagnostic.GetProperty("IsEnabled", BindingFlags.Static | BindingFlags.Public)!.GetValue(null));
            window.Styles.Add(new Style(selector => selector.OfType<Button>().Class("ProfilerProbeActive"))
            {
                Setters = { new Setter(Visual.OpacityProperty, 0.5d) }
            });
            window.Styles.Add(new Style(selector => selector.OfType<TextBox>().Class("ProfilerProbeNever"))
            {
                Setters = { new Setter(Visual.OpacityProperty, 0.25d) }
            });
            var button = new Button { Content = "Probe" };
            panel.Children.Add(button);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1d, button.Opacity);
            button.Classes.Add("ProfilerProbeActive");
            Assert.Equal(0.5d, button.Opacity);
            button.Classes.Remove("ProfilerProbeActive");
            Assert.Equal(1d, button.Opacity);
            window.Resources["ProfilerProbeExisting"] = "present";
            Assert.True(button.TryFindResource("ProfilerProbeExisting", out var found));
            Assert.Equal("present", found);
            Assert.False(button.TryFindResource("ProfilerProbeMissing", out _));
            var snapshot = service.Snapshot();
            Assert.Equal(0, snapshot.Status.CallbackErrors);
            Assert.Contains(snapshot.Samples,
                sample => sample.Category == ProfilerCategory.StyleAttach &&
                          sample.Selector!.Contains("ProfilerProbeActive", StringComparison.Ordinal) &&
                          sample.Result == "Sometimes" && sample.IsMatch is true);
            Assert.Contains(snapshot.Samples,
                sample => sample.Category == ProfilerCategory.StyleAttach &&
                          sample.Selector!.Contains("ProfilerProbeNever", StringComparison.Ordinal) &&
                          sample.FastReject &&
                          sample.IsMatch is false);
            Assert.Contains(snapshot.Samples,
                sample => sample.Category == ProfilerCategory.StyleEvaluate && sample.IsActive is true);
            Assert.Contains(snapshot.Samples,
                sample => sample.Category == ProfilerCategory.StyleEvaluate && sample.IsActive is false);
            var hit = Assert.Single(snapshot.Samples, sample => sample.Key == "ProfilerProbeExisting");
            Assert.Equal("True", hit.Result);
            Assert.True(hit.IsMatch);
            var missing = Assert.Single(snapshot.Samples, sample => sample.Key == "ProfilerProbeMissing");
            Assert.Null(missing.Result);
            Assert.Null(missing.IsMatch);
            Assert.All(snapshot.Samples, sample => Assert.True(sample.DurationMicroseconds >= 0));
            Assert.All(snapshot.Groups, group => Assert.Equal("retained_samples", group.WindowScope));
            service.Stop();
            window.Close();
            // The service owns its listener, never Avalonia's shared ActivitySource.
            var source =
                (ActivitySource)diagnostic.GetField("s_activitySource", BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetValue(null)!;
            using var external = new ActivityListener
            {
                ShouldListenTo = candidate => ReferenceEquals(candidate, source),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
            };
            ActivitySource.AddActivityListener(external);
            using var stillAvailable = source.StartActivity("ExternalProbe");
            Assert.NotNull(stillAvailable);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void History_pending_group_and_inflight_limits_have_independent_accounting()
    {
        using var source = TestSource();
        using var service = new ProfilerService((_, _) => { });
        service.Start(new ProfilerStartRequest(Categories: [ProfilerCategory.ResourceLookup],
            Capacity: 3,
            PendingCapacity: 2,
            MaxGroups: 1,
            MaxInFlight: 2,
            MaxTags: 1,
            NotificationIntervalMs: 5000));
        for (var index = 0; index < 5; index++)
            Resource(source, "one", true);
        Resource(source, "other", true);
        using var first = source.StartActivity("Avalonia.FindingResource");
        using var second = source.StartActivity("Avalonia.FindingResource");
        var excess = source.StartActivity("Avalonia.FindingResource");
        excess?.Stop();
        var snapshot = service.Snapshot();
        Assert.Equal(3, snapshot.Samples.Count);
        Assert.Equal(2, snapshot.Status.PendingSampleCount);
        Assert.Equal(2, snapshot.Status.InFlightCount);
        Assert.Equal(2, snapshot.Status.EvictedHistorySamples);
        Assert.Equal(3, snapshot.Status.DroppedPendingSamples);
        Assert.Equal(1, snapshot.Status.DroppedGroupSamples);
        Assert.Equal(1, snapshot.Status.DroppedInFlightOperations);
        Assert.Equal(6, snapshot.Status.TruncatedTagSets);
        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(3, group.WindowCount);
        Assert.Equal(3, group.MatchCount);
        Assert.Equal(snapshot.Samples.Sum(sample => sample.DurationMicroseconds), group.TotalMicroseconds);
        Assert.Equal(snapshot.Samples.Min(sample => sample.DurationMicroseconds), group.MinMicroseconds);
        Assert.Equal(snapshot.Samples.Max(sample => sample.DurationMicroseconds), group.MaxMicroseconds);
        Assert.Equal(group.TotalMicroseconds / 3, group.MeanMicroseconds);
        var stopped = service.Stop();
        Assert.Equal(2, stopped.IncompleteOperations);
        Assert.Equal(0, stopped.InFlightCount);
        first?.Stop();
        second?.Stop();
        Assert.Equal(stopped.CompletedOperations, service.Status().CompletedOperations);
    }

    [AvaloniaFact]
    public void Category_and_substring_filters_are_applied_to_final_tags()
    {
        using var source = TestSource();
        using var service = new ProfilerService((_, _) => { });
        service.Start(new ProfilerStartRequest(Categories:
            [ProfilerCategory.ResourceLookup],
            Filter: "wanted",
            NotificationIntervalMs: 5000));
        using (source.StartActivity("Avalonia.AttachingStyle"))
        {
        }

        using (source.StartActivity("UnknownOperation"))
        {
        }

        Resource(source, "WANTED-key", true);
        Resource(source, "excluded-key", true);
        var snapshot = service.Snapshot();
        Assert.Equal("WANTED-key", Assert.Single(snapshot.Samples).Key);
        Assert.Equal(2, snapshot.Status.StartedOperations);
        Assert.Equal(2, snapshot.Status.CompletedOperations);
        Assert.Equal(1, snapshot.Status.FilteredOperations);
    }

    [AvaloniaFact]
    public void Real_framework_resource_groups_include_the_requested_theme_variant()
    {
        var button = new Button();
        button.Resources["ProfilerThemeKey"] = 42;
        using var service = new ProfilerService((_, _) => { });
        service.Start(new ProfilerStartRequest(Categories: [ProfilerCategory.ResourceLookup],
            Filter: "ProfilerThemeKey",
            NotificationIntervalMs: 5000));
        Assert.True(button.TryFindResource("ProfilerThemeKey", ThemeVariant.Light, out _));
        Assert.True(button.TryFindResource("ProfilerThemeKey", ThemeVariant.Dark, out _));
        var snapshot = service.Snapshot();
        Assert.Equal(2, snapshot.Groups.Count);
        Assert.Contains(snapshot.Groups, group => group.Name.Contains("theme=Light", StringComparison.Ordinal));
        Assert.Contains(snapshot.Groups, group => group.Name.Contains("theme=Dark", StringComparison.Ordinal));
        Assert.Equal(2, snapshot.Samples.Select(sample => sample.GroupId).Distinct().Count());
    }

    [AvaloniaFact]
    public void Long_string_keys_and_theme_delimiters_keep_distinct_group_identities()
    {
        using var source = TestSource();
        using var service = new ProfilerService((_, _) => { });
        service.Start(new ProfilerStartRequest(Categories:
            [ProfilerCategory.ResourceLookup],
            NotificationIntervalMs: 5000));
        var prefix = new string('k', 300);
        Resource(source, prefix + "first", true);
        Resource(source, prefix + "second", true);
        Resource(source, prefix + "first", true);
        using (var first = source.StartActivity("Avalonia.FindingResource"))
        {
            first!.SetTag("Key", "a:b");
            first.SetTag("ThemeVariant", "c");
        }

        using (var second = source.StartActivity("Avalonia.FindingResource"))
        {
            second!.SetTag("Key", "a");
            second.SetTag("ThemeVariant", "b:c");
        }

        var snapshot = service.Snapshot();
        Assert.Equal(4, snapshot.Groups.Count);
        Assert.Equal(snapshot.Samples[0].Key, snapshot.Samples[1].Key);
        Assert.NotEqual(snapshot.Samples[0].GroupId, snapshot.Samples[1].GroupId);
        Assert.Equal(snapshot.Samples[0].GroupId, snapshot.Samples[2].GroupId);
        Assert.NotEqual(snapshot.Samples[3].GroupId, snapshot.Samples[4].GroupId);
    }

    [AvaloniaFact]
    public async Task Cross_thread_callbacks_are_batched_on_UI_thread_and_stop_clear_restart_ignore_old_spans()
    {
        using var source = TestSource();
        var notifications = new List<ProfilerSamplesAddedNotification>();
        var onUiThread = true;
        var service = new ProfilerService((_, payload) =>
        {
            onUiThread &= Dispatcher.UIThread.CheckAccess();
            notifications.Add((ProfilerSamplesAddedNotification)payload);
        });
        service.Start(new ProfilerStartRequest(Categories:
            [ProfilerCategory.ResourceLookup],
            NotificationIntervalMs: 50));
        await Task.Run(() =>
        {
            for (var index = 0; index < 130; index++)
                Resource(source, "background", true);
        });
        await WaitUntilAsync(() => notifications.Sum(item => item.Samples.Count) == 130);
        Assert.True(onUiThread);
        Assert.All(notifications, item => Assert.InRange(item.Samples.Count, 1, 64));
        var old = source.StartActivity("Avalonia.FindingResource");
        var firstCapture = service.Status().CaptureId;
        service.Clear();
        Assert.True(service.Status().IsRunning);
        Assert.NotEqual(firstCapture, service.Status().CaptureId);
        old?.Stop();
        Assert.Empty(service.Snapshot().Samples);
        Resource(source, "new", true);
        var stopped = service.Stop();
        var count = notifications.Count;
        await Task.Run(() => Resource(source, "after-stop", true));
        await Task.Delay(100);
        Assert.Equal(count, notifications.Count);
        Assert.Equal(stopped.CompletedOperations, service.Status().CompletedOperations);
        Assert.Equal("new", Assert.Single(service.Snapshot().Samples).Key);
        service.Clear();
        Assert.Empty(service.Snapshot().Samples);
        Assert.False(service.Status().IsRunning);
        service.Start(new ProfilerStartRequest(Categories:
            [ProfilerCategory.ResourceLookup],
            NotificationIntervalMs: 50));
        var final = source.StartActivity("Avalonia.FindingResource");
        service.Dispose();
        final?.Stop();
        await Task.Delay(100);
        Assert.Empty(service.Snapshot().Samples);
        Assert.False(service.Status().IsRunning);
        Assert.Throws<ObjectDisposedException>(() => service.Start(new ProfilerStartRequest()));
    }

    [AvaloniaFact]
    public void Business_tag_objects_are_never_formatted_or_retained_and_text_is_bounded()
    {
        using var source = TestSource();
        using var service = new ProfilerService((_, _) => { });
        service.Start(new ProfilerStartRequest(NotificationIntervalMs: 5000));
        var weak = CaptureTemporaryObjects(source);
        for (var index = 0; index < 3 && weak.Any(reference => reference.IsAlive); index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(weak, reference => Assert.False(reference.IsAlive));
        Resource(source, new string('x', 5000), true);
        var snapshot = service.Snapshot();
        Assert.Equal(2, snapshot.Samples.Count);
        Assert.Contains(nameof(ThrowingObject), snapshot.Samples[0].Key);
        Assert.All(snapshot.Samples.SelectMany(sample => sample.Tags),
            tag =>
            {
                Assert.True(tag.Key.Length <= 32);
                Assert.True(tag.Value?.Length is null or <= 96);
            });
        Assert.Equal(96, snapshot.Samples[1].Key!.Length);
    }

    [AvaloniaFact]
    public void Invalid_settings_preserve_capture_and_worst_case_snapshot_fits_protocol_frame()
    {
        using var source = TestSource();
        using var service = new ProfilerService((_, _) => { });
        var status = service.Start(new ProfilerStartRequest(NotificationIntervalMs: 5000));
        Assert.Throws<RpcException>(() => service.Start(new ProfilerStartRequest(Capacity: 1025)));
        Assert.Throws<RpcException>(() => service.Start(new ProfilerStartRequest(Categories: [(ProfilerCategory)99])));
        Assert.Equal(status.CaptureId, service.Status().CaptureId);
        Assert.Throws<RpcException>(() => service.Start(new ProfilerStartRequest(Categories: [])));
        service.Start(new ProfilerStartRequest(Capacity: 1024,
            PendingCapacity: 2048,
            MaxTags: 8,
            MaxGroups: 128,
            NotificationIntervalMs: 5000));
        for (var index = 0; index < 1024; index++)
        {
            using var activity = source.StartActivity("Avalonia.EvaluatingStyle");
            activity!.SetTag("Selector", index % 128 + new string('\u4e2d', 300));
            activity.SetTag("Style", new string('\u4e2d', 300));
            activity.SetTag("IsActive", new string('\u4e2d', 300));
            for (var tag = 0; tag < 8; tag++)
                activity.SetTag("tag" + tag + new string('\u4e2d', 80), new string('\u4e2d', 400));
        }

        var snapshot = service.Snapshot();
        Assert.Equal(1024, snapshot.Samples.Count);
        Assert.Equal(128, snapshot.Groups.Count);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot, ProtocolJson.Options).Length <
                    RpcPeer.MaximumFrameBytes);
    }

    [AvaloniaFact]
    public async Task Concurrent_stop_and_restart_do_not_commit_stale_background_operations()
    {
        using var source = TestSource();
        using var service = new ProfilerService((_, _) => { });
        service.Start(new ProfilerStartRequest(Categories:
            [ProfilerCategory.ResourceLookup],
            NotificationIntervalMs: 5000));
        using var cancel = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var producer = Task.Run(() =>
        {
            started.Set();
            while (!cancel.IsCancellationRequested)
                Resource(source, "race", true);
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        await WaitUntilAsync(() => service.Status().CompletedOperations > 10);
        var stopped = service.Stop();
        cancel.Cancel();
        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(stopped.CompletedOperations, service.Status().CompletedOperations);
        Assert.Equal(stopped.StartedOperations, service.Status().StartedOperations);
        Assert.Equal(0, service.Status().InFlightCount);
        service.Start(new ProfilerStartRequest(Categories:
            [ProfilerCategory.ResourceLookup],
            NotificationIntervalMs: 5000));
        Resource(source, "after-restart", true);
        Assert.Equal("after-restart", Assert.Single(service.Snapshot().Samples).Key);
        Assert.Equal(0, service.Status().CallbackErrors);
    }

    [AvaloniaFact]
    public void Hierarchical_parent_activities_have_distinct_bounded_inflight_identifiers()
    {
        using var source = TestSource();
        using var service = new ProfilerService((_, _) => { });
        service.Start(new ProfilerStartRequest(Categories:
            [ProfilerCategory.ResourceLookup],
            NotificationIntervalMs: 5000));
        using var parent = new Activity("hierarchical-parent").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
        var first = source.StartActivity("Avalonia.FindingResource");
        var second = source.StartActivity("Avalonia.FindingResource");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(ActivityIdFormat.Hierarchical, first.IdFormat);
        Assert.Equal(2, service.Status().InFlightCount);
        second.SetTag("Key", "second");
        second.Stop();
        first.SetTag("Key", "first");
        first.Stop();
        Assert.Equal(2, service.Snapshot().Samples.Count);
        Assert.Equal(0, service.Status().InFlightCount);
    }

    [AvaloniaFact]
    public async Task Typed_client_controls_real_framework_capture_and_disconnect_disposes_its_listener()
    {
        var window = new Window { Content = new Button { Content = "Profiler host" } };
        window.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = "profiler-rpc-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window]
                });
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            var notification =
                new TaskCompletionSource<ProfilerSamplesAddedNotification>(TaskCreationOptions
                    .RunContinuationsAsynchronously);
            client.ProfilerSamplesAdded += payload => notification.TrySetResult(payload);
            var started = await client.StartProfilerAsync(new ProfilerStartRequest(Categories:
                [ProfilerCategory.ResourceLookup],
                Filter: "ProfilerClient",
                NotificationIntervalMs: 50));
            window.Resources["ProfilerClientKey"] = 42;
            Assert.True(window.TryFindResource("ProfilerClientKey", out _));
            var notified = await notification.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("ProfilerClientKey", Assert.Single(notified.Samples).Key);
            Assert.True(Assert.Single(notified.Samples).IsMatch);
            Assert.Equal(started.CaptureId, (await client.GetProfilerStatusAsync()).CaptureId);
            var stopped = await client.StopProfilerAsync();
            Assert.False(stopped.IsRunning);
            Assert.Single((await client.GetProfilerSnapshotAsync()).Samples);
            await client.ClearProfilerAsync();
            Assert.Empty((await client.GetProfilerSnapshotAsync()).Samples);
            await client.StartProfilerAsync(new ProfilerStartRequest(Categories: [ProfilerCategory.ResourceLookup]));
            var source = (ActivitySource)typeof(AvaloniaObject).Assembly.GetType("Avalonia.Diagnostics.Diagnostic")!
                .GetField("s_activitySource", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.True(source.HasListeners());
            await client.DisconnectAsync();
            await WaitUntilAsync(() => !source.HasListeners());
        }
        finally
        {
            window.Close();
        }
    }

    private static ActivitySource TestSource() => new(Avalonia12ActivityAdapter.SourceName);

    private static void Resource(ActivitySource source, object key, bool? found)
    {
        using var activity = source.StartActivity("Avalonia.FindingResource");
        activity?.SetTag("Key", key);
        if (found is not null)
            activity?.SetTag("Result", found.Value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CaptureTemporaryObjects(ActivitySource source)
    {
        var key = new ThrowingObject();
        var control = new Button();
        var style = new Style(selector => selector.OfType<Button>().Class("weak"));
        using (var activity = source.StartActivity("Avalonia.FindingResource"))
        {
            activity?.SetTag("Key", key);
            activity?.SetTag("Control", control);
            activity?.SetTag("Style", style);
        }

        return [new(key), new(control), new(style)];
    }

    private sealed class ThrowingObject
    {
        public override string ToString() =>
            throw new InvalidOperationException("Application ToString must not execute.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }
}
