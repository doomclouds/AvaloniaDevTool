using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class RecordingInputEventTests
{
    [AvaloniaFact]
    public async Task Pointer_notifications_are_opt_in_and_use_actual_transformed_screen_coordinates()
    {
        var target = new Border
        {
            Width = 120, Height = 80, Background = Brushes.White,
            RenderTransformOrigin = RelativePoint.TopLeft, RenderTransform = new ScaleTransform(1.5, 2)
        };
        var host = new Canvas { Children = { target } };
        Canvas.SetLeft(target, 35);
        Canvas.SetTop(target, 45);
        var window = new Window { Width = 600, Height = 450, Content = host };
        var notifications = new ConcurrentQueue<RecordingInputEvent>();
        var objects = new WeakObjectRegistry();
        var id = objects.GetId(target);
        using var service = new InputInspectionService(objects, (method, payload, _) =>
        {
            Assert.Equal(RecordingInputMethods.Event, method);
            notifications.Enqueue(Assert.IsType<RecordingInputEvent>(payload));
            return Task.CompletedTask;
        });
        try
        {
            window.Show();
            window.UpdateLayout();
            service.Send(new InputRequest(id, InputKind.PointerMove, X: 12, Y: 18));
            Assert.Empty(notifications);

            var configured = await service.ConfigureRecordingEventsAsync(new(true), CancellationToken.None);
            Assert.True(configured.Enabled);
            var expected = target.PointToScreen(new Point(12, 18));
            var before = Stopwatch.GetTimestamp();
            service.Send(new InputRequest(id, InputKind.Click, X: 12, Y: 18, Button: "Right"));
            service.Send(new InputRequest(id, InputKind.Wheel, X: 12, Y: 18, DeltaY: -2));
            var stopped = await service.ConfigureRecordingEventsAsync(new(false), CancellationToken.None);

            Assert.False(stopped.Enabled);
            Assert.Equal(4, stopped.PublishedCount);
            Assert.Equal(0, stopped.DroppedCount);
            var events = notifications.ToArray();
            Assert.Equal(new[] { "PointerMove", "PointerDown", "PointerUp", "Wheel" }, events.Select(e => e.Kind));
            Assert.All(events, item =>
            {
                Assert.Equal(expected.X, item.ScreenX);
                Assert.Equal(expected.Y, item.ScreenY);
                Assert.Equal(configured.SubscriptionId, item.SubscriptionId);
                Assert.True(item.MonotonicTimestamp >= before);
            });
            Assert.Equal("Right", events[1].Button);
            Assert.Equal(1, events[1].ClickCount);
            Assert.Equal(-2, events[3].DeltaY);
            service.Send(new InputRequest(id, InputKind.PointerMove));
            Assert.Equal(4, notifications.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_pointer_press_is_notified_before_a_blocking_application_handler_returns()
    {
        var button = new Button { Width = 180, Height = 60, Content = "Press" };
        var window = new Window { Width = 400, Height = 200, Content = button };
        using var notificationReceived = new ManualResetEventSlim();
        var objects = new WeakObjectRegistry();
        var id = objects.GetId(button);
        using var service = new InputInspectionService(objects, (_, payload, _) =>
        {
            if (payload is RecordingInputEvent { Kind: "PointerDown" })
                notificationReceived.Set();
            return Task.CompletedTask;
        });
        try
        {
            window.Show();
            window.UpdateLayout();
            await service.ConfigureRecordingEventsAsync(new(true), CancellationToken.None);
            var observedInsideHandler = false;
            button.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
            {
                observedInsideHandler = notificationReceived.Wait(TimeSpan.FromSeconds(2));
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            service.Send(new InputRequest(id, InputKind.PointerDown));
            service.Send(new InputRequest(id, InputKind.PointerUp));
            Assert.True(observedInsideHandler);
            await service.ConfigureRecordingEventsAsync(new(false), CancellationToken.None);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public async Task Starting_again_creates_a_new_subscription_and_keeps_late_events_distinguishable()
    {
        var senderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<RecordingInputEvent>();
        var sends = 0;
        using var publisher = new RecordingInputEventPublisher(async (_, payload, cancellation) =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                senderEntered.TrySetResult();
                await releaseSender.Task.WaitAsync(cancellation);
            }
            delivered.Enqueue((RecordingInputEvent)payload);
        });
        void Publish() => publisher.Publish((subscription, sequence) =>
            new RecordingInputEvent(subscription, sequence, "PointerDown", "Left", 1, 2, 3,
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()));
        var old = publisher.Configure(true);
        Publish();
        await senderEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Publish();
        var current = publisher.Configure(true);
        Assert.NotEqual(old.SubscriptionId, current.SubscriptionId);
        Publish();
        releaseSender.SetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await publisher.FlushAsync(timeout.Token);
        var observations = delivered.ToArray();
        Assert.Equal(2, observations.Length);
        Assert.Equal(old.SubscriptionId, observations[0].SubscriptionId);
        Assert.Equal(current.SubscriptionId, observations[1].SubscriptionId);
        Assert.Equal(1, observations[1].Sequence);
        Assert.Equal(1, publisher.Status().PublishedCount);
    }

    [Fact]
    public async Task A_blocked_notification_connection_does_not_block_input_or_grow_the_queue()
    {
        var senderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publisher = new RecordingInputEventPublisher(async (_, _, cancellation) =>
        {
            senderEntered.TrySetResult();
            await releaseSender.Task.WaitAsync(cancellation);
        });
        publisher.Configure(true);
        void Publish() => publisher.Publish((subscription, sequence) =>
            new RecordingInputEvent(subscription, sequence, "PointerMove", "None", 1, 2, 3,
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()));
        Publish();
        await senderEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        for (var index = 0; index < RecordingInputEventPublisher.Capacity * 3; index++)
            Publish();
        var busy = publisher.Status();
        Assert.InRange(busy.PendingCount, 1, RecordingInputEventPublisher.Capacity + 1);
        Assert.True(busy.DroppedCount > 0);
        Assert.Equal(0, busy.PublishedCount);
        publisher.Configure(false);
        releaseSender.SetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await publisher.FlushAsync(timeout.Token);
        Assert.Equal(0, publisher.Status().PendingCount);
    }
}
