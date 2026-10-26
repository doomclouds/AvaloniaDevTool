using System.Text;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AvaloniaDevTool.Previewer;

/// <summary>Loads source using the same runtime document configuration as Avalonia's designer.</summary>
public sealed class PreviewDocumentLoader : IDisposable
{
    public const int MaximumXamlBytes = 1024 * 1024;
    private readonly PreviewAssemblyScope? _assembly;
    private bool _disposed;

    public PreviewDocumentLoader(string xamlPath, string? assemblyPath = null, string? projectPath = null)
    {
        FilePath = PreviewOptions.AbsoluteFilePath(xamlPath, "--xaml");
        AssemblyPath = assemblyPath is null ? null : PreviewOptions.AbsoluteFilePath(assemblyPath, "--assembly");
        string? relative = null;
        if (projectPath is not null)
        {
            ProjectPath = PreviewOptions.AbsoluteFilePath(projectPath, "--project");
            if (!File.Exists(ProjectPath))
                throw new PreviewDocumentException("project_not_found", $"Project file was not found: {ProjectPath}");
            relative = Path.GetRelativePath(Path.GetDirectoryName(ProjectPath)!, FilePath);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
                throw new PreviewDocumentException("source_outside_project",
                    "The source file must be inside the explicitly selected project directory.");
            if (AssemblyPath is null)
                throw new PreviewDocumentException("assembly_required",
                    "Project preview requires an existing built application assembly.");
        }

        if (AssemblyPath is not null)
            _assembly = new PreviewAssemblyScope(AssemblyPath);
        DocumentUri = relative is null
            ? new Uri(FilePath)
            : new Uri($"avares://{_assembly!.Assembly.GetName().Name}/{relative.Replace('\\', '/')}");
    }

    public string FilePath { get; }
    public string? AssemblyPath { get; }
    public string? ProjectPath { get; }
    public Uri DocumentUri { get; }

    public Control Load()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var xaml = ReadBoundedFile(FilePath);
            using var xml = XmlReader.Create(new StringReader(xaml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumXamlBytes
                });
            var document = XDocument.Load(xml, LoadOptions.SetLineInfo);
            var root = document.Root ??
                       throw new PreviewDocumentException("empty_xaml", "The XAML document has no root element.");
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            if (root.Attribute(x + "Class") is not null && _assembly is null)
                throw new PreviewDocumentException("xclass_not_supported",
                    "x:Class requires --assembly with the built project output. Rebuild the project before previewing classful XAML.");
            if (root.Attribute(x + "Class") is { } declaredClass &&
                _assembly?.Assembly.GetType(declaredClass.Value) is null)
                throw new PreviewDocumentException("class_not_built",
                    $"x:Class '{declaredClass.Value}' is absent from the selected assembly. Build the matching project first.");
            if (root.Name.NamespaceName == "https://github.com/avaloniaui" &&
                (root.Name.LocalName == "Application" || (ProjectPath is null &&
                                                          root.Name.LocalName is "Window" or "ResourceDictionary"
                                                              or "Style" or "Styles")))
                throw new PreviewDocumentException("unsupported_root",
                    $"Expected an embeddable Control root such as UserControl or Border, received {root.Name.LocalName}.");

            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
            var compiledBindings = _assembly?.Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "AvaloniaUseCompiledBindingsByDefault")?.Value;
            var loaded = AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(DocumentUri, input),
                new RuntimeXamlLoaderConfiguration
                {
                    LocalAssembly = _assembly?.Assembly,
                    DesignMode = true,
                    UseCompiledBindingsByDefault = bool.TryParse(compiledBindings, out var useCompiled) && useCompiled
                });
            if (ProjectPath is not null)
            {
                // The shipped designer calls this exact helper, which is hidden in reference assemblies.
                // Loading and x:Class construction above stay entirely in the public runtime loader.
                var method = typeof(Design).GetMethod("CreatePreviewWithControl",
                    BindingFlags.Public | BindingFlags.Static);
                if (typeof(Design).Assembly.GetName().Version is not { Major: 12, Minor: 1, Build: 4 } ||
                    method is null)
                    throw new PreviewDocumentException("unsupported_designer_api",
                        "The preview conversion helper requires Avalonia 12.1.4.");
                loaded = method.Invoke(null, [loaded]) ??
                         throw new PreviewDocumentException("unsupported_root",
                             "The designer returned no preview control.");
            }

            if (loaded is not Control control || (ProjectPath is null && control is TopLevel))
            {
                if (loaded is Window window)
                    window.Close();
                throw new PreviewDocumentException("unsupported_root",
                    $"Expected an embeddable Control root, received {loaded.GetType().FullName}.");
            }

            ApplyRootDesignProperties(control);
            return control;
        }
        catch (PreviewDocumentException)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw new PreviewDocumentException("file_not_found", $"XAML file was not found: {FilePath}", exception);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            var messages = new List<string>();
            for (var current = exception; current is not null && messages.Count < 5; current = current.InnerException)
                if (!messages.Contains(current.Message, StringComparer.Ordinal))
                    messages.Add(current.Message);
            throw new PreviewDocumentException("xaml_load_failed", string.Join(" → ", messages), exception);
        }
    }

    private static void ApplyRootDesignProperties(Control control)
    {
        if (control.IsSet(Design.WidthProperty) && double.IsNaN(control.Width))
            control.Width = Design.GetWidth(control);
        if (control.IsSet(Design.HeightProperty) && double.IsNaN(control.Height))
            control.Height = Design.GetHeight(control);
        if (control.IsSet(Design.DataContextProperty) && !control.IsSet(StyledElement.DataContextProperty))
            control.DataContext = Design.GetDataContext(control);
        if (control.IsSet(Design.DesignStyleProperty))
            control.Styles.Add(Design.GetDesignStyle(control));
    }

    private static string ReadBoundedFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaximumXamlBytes)
            throw new PreviewDocumentException("xaml_too_large",
                $"XAML source exceeds the {MaximumXamlBytes} byte limit.");
        var bytes = new byte[MaximumXamlBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0)
                break;
            count += read;
        }

        if (count > MaximumXamlBytes)
            throw new PreviewDocumentException("xaml_too_large",
                $"XAML source exceeds the {MaximumXamlBytes} byte limit.");
        // Strict UTF-8 makes partially written multibyte input a recoverable load error.
        var content = new UTF8Encoding(false, true).GetString(bytes, 0, count);
        return content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _assembly?.Dispose();
    }
}
