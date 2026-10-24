using System.Text;
using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

/// <summary>Stores only explicit event identities and diagnostic group names, never target objects.</summary>
public sealed class EventSelectionDefaultsStore(string path)
{
    private const int MaximumBytes = 64 * 1024;
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AvaloniaDevTool", "event-selection.json");

    public async Task<EventSelection?> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The saved event selection exceeds 64 KiB.");
        var bytes = await File.ReadAllBytesAsync(path, token);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The saved event selection exceeds 64 KiB.");
        var selection = JsonSerializer.Deserialize<EventSelection>(bytes, ProtocolJson.Options)
            ?? throw new InvalidDataException("The saved event selection is empty.");
        Validate(selection);
        return selection;
    }

    public async Task SaveAsync(EventSelection selection, CancellationToken token = default)
    {
        Validate(selection);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(selection, ProtocolJson.Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The event selection exceeds 64 KiB.");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, Encoding.UTF8.GetString(bytes), new UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(EventSelection selection)
    {
        string[] groups = ["Pointer", "Keyboard", "Focus", "Control", "Gesture", "DragDrop"];
        if (selection.EventIds?.Count > 1024 || selection.EventIds?.Any(item => string.IsNullOrEmpty(item) || item.Length > 1024) == true ||
            selection.Groups?.Count > groups.Length || selection.Groups?.Any(item => !groups.Contains(item, StringComparer.Ordinal)) == true)
            throw new InvalidDataException("The saved event selection contains invalid identities or groups.");
    }
}
