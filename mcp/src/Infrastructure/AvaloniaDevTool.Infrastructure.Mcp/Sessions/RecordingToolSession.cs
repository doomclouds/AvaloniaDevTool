using AvaloniaDevTool.Client;
using System.Diagnostics;
using AvaloniaDevTool.Protocol;
using AvaloniaDevTool.Recording;
using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class ToolSession
{
    private readonly SemaphoreSlim _recordingCalls = new(1, 1);
    private RecordingSession? _recording;
    private DiagnosticsClient? _recordingInputClient;
    private Action<RecordingInputEvent>? _recordingInputHandler;
    private AvaloniaDevTool.NativeAutomation.NativeTargetIdentity? _recordingTargetIdentity;

    internal AvaloniaDevTool.NativeAutomation.NativeTargetIdentity? RecordingTargetIdentity => _recordingTargetIdentity;

    private void RememberRecordingTargetIdentity()
    {
        _recordingTargetIdentity = null;
        if (CurrentSession is not { } current)
            return;
        try
        {
            using var process = Process.GetProcessById(current.ProcessId);
            _recordingTargetIdentity = new(current.ProcessId, process.StartTime.ToUniversalTime());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                                      System.ComponentModel.Win32Exception)
        {
            // Other diagnostics can remain available; recording requires a verified process lifetime.
        }
    }

    internal bool HasActiveRecording => _recording?.IsActive == true;

    internal void EnsureRecordingAllowsTargetChange()
    {
        if (HasActiveRecording)
            throw new McpException("recording_busy: Stop the active recording before changing the application.");
    }

    internal async Task<RecordingStatus> StartRecordingAsync(RecordingStartRequest request, SessionInfo expectedSession,
        CancellationToken cancellationToken)
    {
        // Target transitions use this same gate. Keep one lock order: attachment, then recorder.
        await _attachment.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _recordingCalls.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureRecordingAllowsTargetChange();
                if (!ReferenceEquals(CurrentSession, expectedSession) ||
                    CurrentSession is not { } target || target.ProcessId != request.ProcessId ||
                    _recordingTargetIdentity is not { } expected || expected.ProcessId != request.ProcessId ||
                    expected.StartTimeUtc != request.StartTimeUtc)
                    throw new McpException("stale_session: Attach to the recording target again.");
                await UnsubscribeRecordingInputAsync().ConfigureAwait(false);
                if (_recording is { } previous)
                    await previous.DisposeAsync().ConfigureAwait(false);
                _recording = await RecordingSession.StartAsync(request, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (request.MouseClicks && _recording.IsActive)
                {
                    await SubscribeRecordingInputAsync(_recording, request.WindowHandle, cancellationToken)
                        .ConfigureAwait(false);
                    _ = StopInputObserverWhenRecordingEndsAsync(_recording);
                }
                return _recording.Current;
            }
            catch (RecordingException error)
            {
                throw new McpException($"{error.Code}: {error.Message}");
            }
            finally
            {
                _recordingCalls.Release();
            }
        }
        finally
        {
            _attachment.Release();
        }
    }

    internal async Task<RecordingStatus> ReadRecordingAsync(string? recordingId, bool stop,
        CancellationToken cancellationToken)
    {
        await _recordingCalls.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = _recording;
            if (active is null || recordingId is not null && active.Current.RecordingId != recordingId)
            {
                if (stop)
                    throw new McpException("recording_not_found: This session does not own that active recording.");
                if (recordingId is null)
                    throw new McpException("recording_not_found: No recording has been started in this session.");
                return RecordingArtifacts.Get(recordingId);
            }
            if (stop)
                await UnsubscribeRecordingInputAsync().ConfigureAwait(false);
            if (stop)
                await active.StopAsync(cancellationToken).ConfigureAwait(false);
            else
                await active.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!active.IsActive)
                await UnsubscribeRecordingInputAsync().ConfigureAwait(false);
            return active.Current;
        }
        catch (RecordingException error)
        {
            throw new McpException($"{error.Code}: {error.Message}");
        }
        finally
        {
            _recordingCalls.Release();
        }
    }

    private async Task SubscribeRecordingInputAsync(RecordingSession recording, long windowHandle,
        CancellationToken cancellationToken)
    {
        var client = _client;
        string? subscriptionId = null;
        Action<RecordingInputEvent> handler = input =>
        {
            if (input.WindowHandle == windowHandle && input.SubscriptionId == Volatile.Read(ref subscriptionId))
                _ = recording.MarkInputAsync(input.Kind, input.ScreenX, input.ScreenY, input.Button);
        };
        _recordingInputClient = client;
        _recordingInputHandler = handler;
        client.Input.RecordingInputReceived += handler;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var subscription = await client.Input.SetRecordingInputEventsAsync(true, timeout.Token).ConfigureAwait(false);
            Volatile.Write(ref subscriptionId, subscription.SubscriptionId);
        }
        catch (Exception error) when (error is RpcException or IOException or OperationCanceledException)
        {
            client.Input.RecordingInputReceived -= handler;
            _recordingInputHandler = null;
            _recordingInputClient = null;
            recording.AddLimitation("Agent framework input highlights are unavailable: " + error.Message);
            // Recording remains independent when an older or paused Agent cannot publish input events.
            Console.Error.WriteLine($"recording_input_unavailable: {error.Message}");
        }
    }

    private async Task UnsubscribeRecordingInputAsync()
    {
        var client = _recordingInputClient;
        var handler = _recordingInputHandler;
        _recordingInputClient = null;
        _recordingInputHandler = null;
        if (client is null || handler is null)
            return;
        client.Input.RecordingInputReceived -= handler;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var status = await client.Input.SetRecordingInputEventsAsync(false, timeout.Token).ConfigureAwait(false);
            if (status.DroppedCount > 0)
                _recording?.AddLimitation($"The Agent input observer dropped {status.DroppedCount} visual markers.");
        }
        catch (Exception error) when (error is RpcException or IOException or OperationCanceledException)
        {
            _recording?.AddLimitation("The Agent input observer could not confirm pending marker counts before stopping.");
            // A paused target must not prevent stopping the external recorder.
        }
    }

    private async Task StopInputObserverWhenRecordingEndsAsync(RecordingSession owner)
    {
        await owner.Completion.ConfigureAwait(false);
        await _recordingCalls.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_recording, owner))
                await UnsubscribeRecordingInputAsync().ConfigureAwait(false);
        }
        finally { _recordingCalls.Release(); }
    }

    private async Task ReleaseRecordingAsync()
    {
        // Detach already holds the attachment gate; do not reacquire it here.
        await _recordingCalls.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnsubscribeRecordingInputAsync().ConfigureAwait(false);
            var recording = _recording;
            _recording = null;
            if (recording is not null)
                await recording.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _recordingCalls.Release();
        }
    }
}
