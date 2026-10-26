using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed record UiWaitResult(bool Matched, string Condition, long? NodeId, string? PropertyValue, long ElapsedMs);

public sealed partial class AgentTools
{
    [McpServerTool(Name = "ui-wait", ReadOnly = true, Destructive = false),
     Description(
         "Waits on the real Avalonia tree or a property until Exists, Absent, Equals or Contains matches. Query uses type/name search; property predicates require one match. The default property is the owner-qualified Avalonia text property shared by TextBox/TextBlock. Each poll releases the managed call gate. Timeout fails explicitly.")]
    public async Task<UiWaitResult> UiWait(string query,
        string condition = "Exists",
        string propertyName = "Avalonia.Controls.TextBlock:Text",
        string? expectedValue = null,
        int timeoutMs = 10000,
        int pollIntervalMs = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || timeoutMs is < 1 or > 120000 || pollIntervalMs is < 25 or > 1000 ||
            condition is not ("Exists" or "Absent" or "Equals" or "Contains") ||
            (condition == "Contains" && expectedValue is null))
            throw new McpException(
                "invalid_argument: Use a nonempty query, a supported condition, timeoutMs 1..120000 and pollIntervalMs 25..1000; Contains requires expectedValue.");
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        var owner = session.CurrentSession;
        try
        {
            while (true)
            {
                var matches = await ManagedTreeQueries.SearchAsync(managed, query, 0, true, 2, deadline.Token)
                    .ConfigureAwait(false);
                owner ??= session.CurrentSession;
                if (!ReferenceEquals(owner, session.CurrentSession))
                    throw new McpException("stale_session: The application changed while waiting for a UI condition.");
                var first = matches.Matches.FirstOrDefault();
                if (condition == "Absent" && first is null)
                    return new(true, condition, null, null, watch.ElapsedMilliseconds);
                if (condition == "Exists" && first is not null)
                    return new(true, condition, first.Id, null, watch.ElapsedMilliseconds);
                if (condition is "Equals" or "Contains" && first is not null)
                {
                    if (matches.Matches.Count != 1 || matches.Truncated)
                        throw new McpException(
                            "ambiguous_query: Property waits require a query that identifies exactly one node.");
                    var properties = await ManagedPropertyQueries
                        .ReadAsync(managed, first.Id, [propertyName], deadline.Token).ConfigureAwait(false);
                    if (properties.Count != 1)
                        throw new McpException(
                            "ambiguous_property: The property name must identify exactly one readable property.");
                    var value = properties[0].Value;
                    var matched = condition == "Equals"
                        ? string.Equals(value, expectedValue, StringComparison.Ordinal)
                        : value?.Contains(expectedValue!, StringComparison.Ordinal) == true;
                    if (matched)
                        return new(true, condition, first.Id, value, watch.ElapsedMilliseconds);
                }

                await Task.Delay(pollIntervalMs, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"ui_wait_timeout: Condition {condition} was not observed within {timeoutMs}ms.");
        }
    }
}
