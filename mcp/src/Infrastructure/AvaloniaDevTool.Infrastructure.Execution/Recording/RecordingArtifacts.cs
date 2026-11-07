using System.Text.Json;

namespace AvaloniaDevTool.Recording;

public static class RecordingArtifacts
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaloniaDevTool", "recordings");

    internal static string IndexDirectory => Path.Combine(DefaultDirectory, "index");

    public static RecordingStatus[] List(int limit = 100)
    {
        if (limit is < 1 or > 1000)
            throw new RecordingException("invalid_limit", "The recording list limit must be 1..1000.");
        if (!Directory.Exists(IndexDirectory))
            return [];
        return new DirectoryInfo(IndexDirectory).EnumerateFiles("recording-*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(limit)
            .Select(file => Read(file.FullName))
            .OfType<RecordingStatus>()
            .ToArray();
    }

    public static RecordingStatus Get(string artifactId)
    {
        if (!artifactId.StartsWith("recording-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(artifactId[10..], "N", out _))
            throw new RecordingException("recording_not_found", "The recording reference is invalid.");
        return Read(Path.Combine(IndexDirectory, artifactId + ".json")) ??
               throw new RecordingException("recording_not_found", "The recording is not in this computer's index.");
    }

    internal static void Save(RecordingStatus status)
    {
        Directory.CreateDirectory(IndexDirectory);
        var path = Path.Combine(IndexDirectory, status.RecordingId + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, status, RecordingJsonContext.Default.RecordingStatus);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static RecordingStatus? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024)
                return null;
            using var file = File.OpenRead(path);
            return JsonSerializer.Deserialize(file, RecordingJsonContext.Default.RecordingStatus);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
