namespace AvaloniaDevTool.Packaging;

internal sealed record BundleManifest(
    int SchemaVersion,
    string ApplicationVersion,
    string RuntimeIdentifier,
    string EntryPoint,
    string PayloadSha256,
    BundleFile[] Files)
{
    internal void Validate()
    {
        if (SchemaVersion is not (1 or 2) || Files is not { Length: > 0 })
            throw new InvalidDataException("The bundle manifest is unsupported or empty.");
        if (RuntimeIdentifier is not ("win-x64" or "win-arm64"))
            throw new InvalidDataException("The bundle must target win-x64 or win-arm64.");
        ValidateHash(PayloadSha256);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null)
                throw new InvalidDataException("The bundle manifest contains an empty entry.");
            ValidateRelativePath(file.Path);
            ValidateHash(file.Sha256);
            if (file.Length < 0 || !paths.Add(file.Path) ||
                file.Path.Equals(BundleStore.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Invalid or duplicate bundle entry '{file.Path}'.");
        }

        var filesByPath = Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files.Where(file => file.SourcePath is not null))
        {
            ValidateRelativePath(file.SourcePath!);
            if (SchemaVersion != 2 || !filesByPath.TryGetValue(file.SourcePath!, out var source) ||
                source.SourcePath is not null || source.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) ||
                source.Length != file.Length || !source.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Invalid shared bundle content for '{file.Path}'.");
        }

        ValidateRelativePath(EntryPoint);
        if (!paths.Contains(EntryPoint) || !EntryPoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The bundle entry point is missing or is not an executable.");
    }

    internal static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') ||
            path.Contains('\0') || path.Split('/').Any(segment => segment is "" or "." or ".." ||
                                                                  segment.EndsWith(' ') || segment.EndsWith('.')))
            throw new InvalidDataException($"Unsafe bundle entry '{path}'.");
    }

    private static void ValidateHash(string value)
    {
        if (value is not { Length: 64 } || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("A bundle SHA-256 value is invalid.");
    }
}

internal sealed record BundleFile(string Path, long Length, string Sha256, string? SourcePath = null);
