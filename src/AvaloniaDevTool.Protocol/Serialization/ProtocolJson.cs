using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace AvaloniaDevTool.Protocol;

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    [UnconditionalSuppressMessage("Trimming",
        "IL2026",
        Justification =
            "The reflection resolver is used only when System.Text.Json's linker feature switch enables reflection; Native AOT removes this branch.")]
    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = RuntimeFeature.IsDynamicCodeSupported && JsonSerializer.IsReflectionEnabledByDefault
            ? JsonTypeInfoResolver.Combine(ProtocolJsonContext.Default, new DefaultJsonTypeInfoResolver())
            : ProtocolJsonContext.Default
    };

    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public static byte[] SerializeToUtf8Bytes(object? value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options.GetTypeInfo(value?.GetType() ?? typeof(object)));

    public static JsonElement ToElement(object? value) =>
        JsonSerializer.SerializeToElement(value, Options.GetTypeInfo(value?.GetType() ?? typeof(object)));

    public static T Read<T>(JsonElement value) =>
        value.Deserialize(TypeInfo<T>()) ??
        throw new RpcException("invalid_request", "The request payload is missing.");
}
