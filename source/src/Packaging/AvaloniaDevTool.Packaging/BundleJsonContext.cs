using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Packaging;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(BundleManifest))]
[JsonSerializable(typeof(BundleInfo))]
[JsonSerializable(typeof(BundleVerification))]
internal sealed partial class BundleJsonContext : JsonSerializerContext;

internal sealed record BundleInfo(int SchemaVersion, string ApplicationVersion, string RuntimeIdentifier,
    string EntryPoint, string PayloadSha256, int FileCount, long UncompressedBytes);

internal sealed record BundleVerification(bool Verified, string RuntimeRoot, string RuntimeIdentifier,
    string PayloadSha256, int FileCount);
