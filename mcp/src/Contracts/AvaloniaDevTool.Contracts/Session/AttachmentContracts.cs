namespace AvaloniaDevTool.Protocol;

public sealed record ProcessAttachmentInfo(string Method, string RuntimeVersion, string AvaloniaVersion,
    double PreparationMilliseconds, double InjectionMilliseconds, double TotalMilliseconds, bool Reused);
