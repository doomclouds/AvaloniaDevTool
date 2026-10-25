using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

internal static class NativeFileFilterTimingFixture
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ConfigurationName = "native-file-filter-timing.json";

    [ModuleInitializer]
    internal static void Initialize()
    {
        var configurationPath = Path.Combine(AppContext.BaseDirectory, ConfigurationName);
        if (!File.Exists(configurationPath)) return;
        try
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false);
            var configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configurationPath, Encoding.UTF8), JsonOptions)
                ?? throw new InvalidOperationException("A filter timing configuration is required.");
            if (!string.Equals(Path.GetFullPath(configuration.ControlDirectory), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Filter timing control files must stay in the worker directory.");
            new Worker(configuration).Run();
            Environment.Exit(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Environment.Exit(1);
        }
    }

    private sealed class Worker(Configuration configuration)
    {
        private readonly List<ActionEntry> _actions = [];
        private string _prefix = "";
        private int _targetId;
        private bool _open = true;
        private bool _expanded;
        private int _comboReads;
        private int _windowsReads;
        private int _pathWrites;
        private int _actionStatusReads;
        private int _dialogReads;
        private int _truncatedDialogReads;
        private string _filename = "timing.txt";
        private string _filterValue = "All files (*.*)";

        public void Run()
        {
            while (Console.ReadLine() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var request = document.RootElement;
                var id = request.GetProperty("id").GetInt64();
                var payload = request.GetProperty("payload");
                object result = request.GetProperty("command").GetString() switch
                {
                    "attach" => Attach(payload),
                    "windows" => Windows(),
                    "tree" => Tree(payload),
                    "element" => Element(payload.GetString()!),
                    "action" => Action(payload),
                    "action-status" => ActionStatus(payload.GetString()!),
                    _ => throw new InvalidOperationException("Unexpected filter timing worker command.")
                };
                WriteTrace();
                Console.WriteLine(JsonSerializer.Serialize(new { id, success = true, result }, JsonOptions));
                Console.Out.Flush();
            }
        }

        private bool Attach(JsonElement payload)
        {
            _prefix = "native:" + payload.GetProperty("sessionId").GetString() + ":";
            _targetId = payload.GetProperty("target").GetProperty("processId").GetInt32();
            using var current = Process.GetCurrentProcess();
            WriteState("attached.json", new ProcessIdentity(current.Id, current.StartTime.ToUniversalTime().Ticks,
                Path.GetFullPath(current.MainModule!.FileName!)));
            return true;
        }

        private object[] Windows()
        {
            if (++_windowsReads == configuration.SlowWindowsRead)
            {
                Thread.Sleep(500);
                if (configuration.OmitSlowWindowsResult) return [];
            }
            return _open ? [new
            {
                nodeId = Id("dialog"), handle = 24001L, ownerHandle = 0L, processId = _targetId,
                title = "Synthetic filter timing picker", className = "#32770", isModal = true, isOwned = true,
                isEnabled = true, isVisible = true
            }] : [];
        }

        private object Tree(JsonElement query)
        {
            var root = query.GetProperty("rootNodeId").GetString();
            object[] nodes;
            var truncated = false;
            if (root == Id("filter"))
            {
                if (!_expanded && !configuration.IgnoreExpand) throw new InvalidOperationException("The filter must be expanded before reading its choices.");
                if (++_comboReads == 1)
                {
                    WriteTrace();
                    // Only this provider response is slow; it remains below the session's own RPC timeout.
                    Thread.Sleep(configuration.FirstComboReadDelayMs);
                }
                var buttonCount = configuration.DropDownButtonCount > 0 ? configuration.DropDownButtonCount : configuration.IgnoreExpand ? 1 : 0;
                nodes = configuration.ExposeOptions && _expanded ? [Combo(), Choice("text", "Text files (*.txt)", 0), Choice("all", "All files (*.*)", 1)] :
                    [Combo(), .. Enumerable.Range(0, buttonCount).Select(index => Node(index == 0 ? "dropdown" : "dropdown" + index,
                        "filter", "DropDown", "Button", patterns: ["Invoke"]))];
            }
            else if (root == Id("dialog"))
            {
                nodes = DialogTree();
                truncated = ++_dialogReads == configuration.TruncatedDialogRead;
                if (truncated) _truncatedDialogReads++;
            }
            else throw new InvalidOperationException("The requested root does not belong to this synthetic dialog.");
            return new { nodes, truncated, visitedNodes = nodes.Length };
        }

        private object[] DialogTree() =>
        [
            Node("dialog", null, "", "Window", "#32770"),
            Node("dui", "dialog", "", "Pane", "DUIViewWndClassName"),
            Node("footer", "dui", "FileDialogFooter", "Pane"),
            Node("filenameHost", "footer", "FileNameControlHost", "Pane"),
            Filename(),
            Node("accept", "footer", "1", "Button", patterns: ["Invoke"]),
            Node("cancel", "footer", "2", "Button", patterns: ["Invoke"]),
            Combo()
        ];

        private object Combo() => Node("filter", "footer", "FileTypeControlHost", "ComboBox", value: _filterValue,
            patterns: ["ExpandCollapse"], expanded: _expanded ? "Expanded" : "Collapsed");

        private object Filename() => Node("filename", "filenameHost", "1148", "Edit", value: _filename, patterns: ["Value"]);

        private object Choice(string suffix, string name, int index) => new
        {
            nodeId = Id(suffix), parentNodeId = Id("filter"), depth = 1, processId = _targetId,
            automationId = "filterOption" + index, name, controlType = "ListItem", className = "",
            isEnabled = true, isOffscreen = false, isSelected = _filterValue == name, supportedPatterns = new[] { "SelectionItem" }
        };

        private object Element(string id) => id == Id("filter") ? Combo() :
            id == Id("filename") ? Filename() :
            id == Id("text") ? Choice("text", "Text files (*.txt)", 0) :
            id == Id("all") ? Choice("all", "All files (*.*)", 1) :
            throw new InvalidOperationException("An unexpected synthetic element was inspected.");

        private object Action(JsonElement request)
        {
            var nodeId = request.GetProperty("nodeId").GetString()!;
            var action = request.GetProperty("action").GetString()!;
            _actions.Add(new ActionEntry(nodeId, action));
            if (nodeId == Id("filter") && action == "Expand")
            {
                _expanded = !configuration.IgnoreExpand;
                if (configuration.PendingExpand) return new { nodeId, action, status = "pending", operationId = Id("expand-operation") };
            }
            else if (nodeId == Id("dropdown") && action == "Invoke" && configuration.IgnoreExpand) _expanded = true;
            else if (nodeId == Id("filename") && action == "SetValue")
            {
                var requested = request.GetProperty("value").GetString()!;
                _filename = ++_pathWrites == 1 && configuration.CorruptFirstPathWrite ? requested + ".unexpected" : requested;
            }
            else if (nodeId == Id("text") && action == "Select" && configuration.ExposeOptions)
            { _filterValue = "Text files (*.txt)"; _expanded = false; }
            else if (nodeId == Id("cancel") && action == "Invoke") _open = false;
            else throw new InvalidOperationException("An unexpected synthetic dialog mutation was requested.");
            return new { nodeId, action, status = "completed" };
        }

        private object ActionStatus(string operationId)
        {
            if (!configuration.PendingExpand || operationId != Id("expand-operation"))
                throw new InvalidOperationException("An unexpected synthetic operation was inspected.");
            _actionStatusReads++;
            return new { nodeId = Id("filter"), action = "Expand", status = "pending", operationId };
        }

        private object Node(string suffix, string? parent, string automationId, string controlType, string className = "",
            string? value = null, string[]? patterns = null, string? expanded = null) => new
        {
            nodeId = Id(suffix), parentNodeId = parent is null ? null : Id(parent), depth = parent is null ? 0 : 1,
            processId = _targetId, automationId, name = suffix, controlType, className, value,
            isEnabled = true, isOffscreen = false, supportedPatterns = patterns ?? [], expandCollapseState = expanded
        };

        private string Id(string suffix) => _prefix + suffix;
        private void WriteTrace() => WriteState("trace.json", new { actions = _actions, comboReads = _comboReads, open = _open,
            filterValue = _filterValue, actionStatusReads = _actionStatusReads, dialogReads = _dialogReads, truncatedDialogReads = _truncatedDialogReads });
        private void WriteState(string filename, object value)
        {
            var path = Path.Combine(configuration.ControlDirectory, filename);
            var pending = path + ".pending";
            File.WriteAllText(pending, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            File.Move(pending, path, overwrite: true);
        }
    }

    private sealed record Configuration(string ControlDirectory, int FirstComboReadDelayMs, bool ExposeOptions,
        int SlowWindowsRead = 0, bool OmitSlowWindowsResult = false, bool CorruptFirstPathWrite = false, bool IgnoreExpand = false,
        bool PendingExpand = false, int DropDownButtonCount = 0, int TruncatedDialogRead = 0);
    private sealed record ActionEntry(string NodeId, string Action);
    private sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);
}
