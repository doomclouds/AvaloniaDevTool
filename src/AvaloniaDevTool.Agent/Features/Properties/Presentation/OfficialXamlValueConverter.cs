using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Markup.Xaml;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class OfficialXamlValueConverter
{
    public static object Parse(string text, Type targetType)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new RpcException("xaml_unavailable",
                "Runtime XAML object loading requires a runtime with dynamic code support.");
        if (text.Length > 1024 * 1024)
            throw new RpcException("invalid_xaml", "The XAML value exceeds the 1 MiB character limit.");
        try
        {
            using var xml = XmlReader.Create(new StringReader(text),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
                });
            var document = XDocument.Load(xml);
            if (document.Root is not { } root || string.IsNullOrWhiteSpace(root.Name.NamespaceName))
                throw new FormatException("The XAML object must declare its XML namespace.");
            // Avalonia's runtime compiler rejects a standalone text root before materialization.
            if (root.Name == XName.Get("String", "http://schemas.microsoft.com/winfx/2006/xaml") &&
                !root.HasElements && root.Attributes().All(attribute => attribute.IsNamespaceDeclaration ||
                                                                        attribute.Name == XNamespace.Xml + "space"))
                return root.Value;
            // Parse a detached value completely before touching the existing diagnostic override.
            var value = Load(document.ToString(SaveOptions.DisableFormatting), targetType);
            return value ?? throw new FormatException("The XAML value did not create an object.");
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            throw new RpcException("invalid_xaml", PropertyErrors.Describe(error));
        }
    }

    [UnconditionalSuppressMessage("Trimming",
        "IL2026",
        Justification =
            "Runtime XAML is explicitly unavailable without dynamic code; loaded application types are resolved by the Avalonia runtime compiler.")]
    [UnconditionalSuppressMessage("AOT",
        "IL3050",
        Justification =
            "The caller checks RuntimeFeature.IsDynamicCodeSupported before reaching the runtime XAML compiler.")]
    private static object Load(string text, Type targetType) =>
        AvaloniaRuntimeXamlLoader.Parse(text, targetType.Assembly);
}
