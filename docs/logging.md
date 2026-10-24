# Structured application logging

The native agent captures Avalonia logs, explicitly registered custom sources, and an optional real
`Microsoft.Extensions.Logging.ILoggerProvider`. All sources use the existing `activity.start`,
`activity.snapshot`, `activity.entry`, `activity.stop`, and `activity.clear` protocol. Routed event
capture remains independent.

## Inspector

Open **日志事件**, select the minimum level, and optionally enter category, message, parameter name,
and parameter value predicates. Click **开始捕获** to apply them. Predicates are case-insensitive
substrings combined with AND; parameter name and value must match the same captured parameter.
Message predicates check the safe rendered message and the original template. `null` matches an
explicit null parameter; unavailable objects do not pretend to be null. Filtering happens before
the pending queue and history, so rejected entries do not consume the 500-entry budget.

Select an entry to see its original template, named typed arguments, parameter availability,
exception graph, origin, event ID/name, and captured scopes. The message column is a convenience
rendering; it is not the structured payload. Selecting an entry locates its source using the usual
ancestor path. **定位日志源对象** repeats that navigation. Sources must still exist and belong to the
selected tree. Expired or detached sources produce the existing actionable navigation status.

Capture does not clear previous history. Use **清空** before a new filtered run when only the new
run's entries should remain. Stop/disconnect removes subscriptions and retains only bounded DTOs.
Refreshing the page reconciles immutable entries by sequence, preserving the selected row and
its details while source navigation refreshes the tree.

## Microsoft.Extensions.Logging

Reference the optional `AvaloniaDevTool.Logging` project/package. The core agent does not depend on
Microsoft logging. Register the same provider instance with the application logger factory and
the agent:

```csharp
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Logging;
using Microsoft.Extensions.Logging;

using var logs = new DiagnosticsLoggerProvider();
using var factory = LoggerFactory.Create(builder =>
    builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
using var agent = DiagnosticsAgent.Attach(application, new DiagnosticsAgentOptions
{
    LogSources = [logs]
});

var logger = factory.CreateLogger("Application.Orders");
using (logger.BeginScope(new Dictionary<string, object?> { ["Tenant"] = 42 }))
{
    logger.LogWarning(new EventId(71, "OrderAccepted"),
        "Accepted {OrderId} from {Source}", 84, sourceControl);
}
```

The provider preserves `{OriginalFormat}`, named values, category, EventId, EventId.Name, exception,
and up to eight external scopes. A `Source` structured parameter also supplies the weak source
identity for navigation. `Trace` maps to `Verbose`, and `Critical` maps to `Fatal`. The application
logger factory's filters apply first; the inspector minimum level and predicates apply afterward.
The provider emits nothing after disposal or while no capture subscriber exists. It does not
change global logging configuration or send telemetry outside the inspector connection.

The provider accepts the standard read-only key/value list state and bounded key/value enumerable
state, including dictionary scopes. Plain string state has a known template and zero parameters.
Other opaque state has unavailable template/parameters. It deliberately does not invoke an
arbitrary formatter delegate, `ToString`, or business-object `IFormattable` implementation merely
to obtain a preview. This can produce a different preview from an application's custom formatter.

This uses the public logging APIs described in Microsoft's
[custom provider documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/custom-provider)
and [provider lifetime guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/providers).

## Custom log adapters

`IDiagnosticsLogSource` is the public minimal adapter boundary. A third-party integration can
implement its own subscription or publish synchronously through `DiagnosticsLogSource`:

```csharp
var source = new DiagnosticsLogSource();
using var agent = DiagnosticsAgent.Attach(application, new DiagnosticsAgentOptions
{
    LogSources = [source]
});

source.Publish(new DiagnosticsLogEvent("Warning", "Application.Import",
    "Imported {Count} rows for {File}",
    [new("Count", 27), new("File", "input.csv")],
    Exception: failure, Source: sourceControl));
```

`Parameters: null` means structured parameter state is unavailable. `Parameters: []` means the
adapter knows there are no parameters. A key/value pair with a null value is a known null argument.
An object with no approved scalar representation retains its type and `Unavailable` state, not
its object reference. Adapters should supply their existing raw template and state rather than
inventing structure from a flattened message. This is an application adapter API; it does not
claim to implement another product's private logging interface.

Subscribe returns an `IDisposable` and is invoked only while log capture is running. An adapter
must invoke its observers synchronously so values can be snapshotted before application state is
reused. The built-in publisher isolates observer exceptions; stop/disposal unsubscribes it.

## Bounds, safety, and lifecycle

* Pending queue and history share a 500-entry, 2 MiB encoded UTF-8 budget. An entry's cost is measured
  once with the same JSON serializer/escaping as the RPC and conservative numeric ID widths;
  transferring it into history reuses its strings and payload. Byte/count overflow evicts oldest
  entries and increments `DroppedCount`; byte evictions also increment `ByteEvictedCount`.
  `BufferedBytes` and `ByteBudget` describe this combined retained window. Snapshot array/envelope
  overhead has more than 1 MiB of headroom below the 4 MiB RPC frame limit.
* The original template and rendered preview keep at most 4,096 characters plus a truncation mark.
  There are at most 32 named parameters; values keep 1,024 characters plus a mark, names 128,
  and type names 512. Template/parameter truncation is explicit in the protocol and UI.
  All template, preview, parameter, exception and scope strings additionally share a **96 KiB JSON
  escaped UTF-8 text budget per entry**, so Chinese text, quotes, backslashes and control characters
  are charged by encoded bytes, not character count. The complete encoded entry is capped at
  **128 KiB**, leaving ample room for a notification envelope. Whole JSON documents are never cut.
  `LogDetails.ByteBudgetTruncated`, `LogParameterInfo.IsTruncated`, and exception/scope truncation
  flags distinguish partial values from complete known/null/unavailable states.
  `BudgetTruncatedCount` counts accepted entries shortened by the shared byte budget. An unexpected
  future entry exceeding the final entry guard is dropped and counted rather than breaking RPC.
* Safe scalar types are null, strings, characters, booleans, built-in numeric types, enums, Guid,
  DateTime, DateTimeOffset, DateOnly, TimeOnly, and TimeSpan. Other types retain only type metadata.
* Exception graphs contain at most eight nodes and four edges of depth. Cycles and truncation
  are explicit. Each message keeps 2,048 characters and each stack keeps 4,096, plus truncation
  marks. The original exception and its Data dictionary are never retained or serialized.
* Exception message/stack getters are evaluated only when inherited from the safe base Exception
  getter. Overridden getters are marked unavailable. AggregateException.Message is never evaluated:
  it can recursively format unbounded or cyclic child graphs. The aggregate summary reports its
  child count, and children are captured independently under the graph budget.
* Scope capture is limited to eight scopes and 32 parameters per scope. Source objects are weak
  until the dispatcher resolves a weak registry ID. An expired pending source has no ID; an expired
  historical source reports expiry on navigation. Sources, arguments and exceptions are not kept
  alive by capture history.
* Avalonia's previous sink still receives the original template/values. On stop, it is restored
  only if the capture wrapper is still the current sink; a later application replacement is kept.
  Reentrant sink/observer logging is suppressed within diagnostics capture.
* Actual agent notifications use an independent **512 KiB / 100 notification** queue, including at
  most one in-flight send and conservative RPC envelope costs. Queued notifications retain existing
  DTO references; they do not pre-serialize every queued message into another byte array. A single
  pump waits for the actual sender task before sending the next item, so slow clients cannot create
  an unbounded number of waiting RPC writes. Pending-to-history conversion and snapshots do not wait
  for notification delivery. Old waiting notifications are evicted separately: `NotificationDroppedCount`
  reports delivery loss, while the corresponding history can still be read by snapshot.
  `NotificationBufferedBytes`, `NotificationByteBudget`, and `PendingNotificationCount` expose this
  delivery window. Stop, clear and disposal discard waiting references and cancel their delivery
  generation; an already-started RPC frame finishes atomically. Clear resets all capture/drop counters.
  The sender must honor cancellation while awaiting the transport; even an uncooperative sender is
  limited to one in-flight call until it completes, without spawning replacement sends.

`StructuredLoggingTests` covers actual agent/client transport, pre-history predicates, typed/raw
data, factory/provider emission, scopes, exception cycles/overflow, weak retention, source disposal,
and reentrancy. `ActivityIntegrationTests` verifies previous/later sink restoration and bounded
background capture. `LogDetailsTests` uses an actual ActivityView with pointer and keyboard input to
verify detail bindings, filters, source navigation, expiry and disconnect cleanup.

`LoggingBudgetTests.Unicode_and_escaped_log_history_fits_real_rpc_snapshot_and_keeps_session_usable`
first reproduced `frame_too_large` over a real agent/client connection, then verifies the bounded
snapshot and a subsequent tree RPC. Additional budget tests cover maximal scopes/exception gaps,
complete ordinary 32-parameter entries, concurrent producers with clear/restart, and a blocked sender
fed 600 entries. The latter verifies bounded delivery bytes/count, independent snapshots, recovery in
sequence order, cancellation on clear/stop/disposal, and maximum sender concurrency of one.
