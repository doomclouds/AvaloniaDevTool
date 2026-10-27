namespace AvaloniaDevTool.UseCases;

/// <summary>A capability error whose message is preserved by each presentation adapter.</summary>
public sealed class CapabilityOperationException(string message) : Exception(message);
