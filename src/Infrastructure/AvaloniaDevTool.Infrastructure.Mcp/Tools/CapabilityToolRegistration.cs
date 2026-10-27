namespace AvaloniaDevTool.Cli;

internal static class CapabilityToolRegistration
{
    internal static object[] Create(ToolSession session, ManagedToolContext context) =>
    [
        new TargetTools(session, context), new StructureTools(session, context),
        new PropertyStyleTools(session, context), new InputTools(session, context),
        new ElementScreenshotTools(session, context), new ResourceAssetTools(session, context),
        new PreviewTools(session, context), new FrameworkInteractionTools(session, context),
        new NativeAutomationTools(session, context), new VisualCaptureTools(session, context),
        new FileDialogTools(session, context), new CaptureTools(session, context),
        new ObjectInspectionTools(session), new PropertyMutationTools(session),
        new ResourceMutationTools(session), new VisualDiagnosticsTools(session),
        new RuntimeDiagnosticsTools(session)
    ];
}
