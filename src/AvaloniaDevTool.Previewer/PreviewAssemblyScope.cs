using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Avalonia.Controls;

namespace AvaloniaDevTool.Previewer;

/// <summary>Resolves an explicitly supplied assembly into the shared default context.</summary>
internal sealed class PreviewAssemblyScope : IDisposable
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;
    private bool _disposed;

    public PreviewAssemblyScope(string assemblyPath)
    {
        assemblyPath = PreviewOptions.AbsoluteFilePath(assemblyPath, "--assembly");
        if (!File.Exists(assemblyPath))
            throw new PreviewDocumentException("assembly_not_found", $"Target assembly was not found: {assemblyPath}");
        ValidateAvaloniaReferences(assemblyPath);
        _directory = Path.GetDirectoryName(assemblyPath)!;
        _resolver = new AssemblyDependencyResolver(assemblyPath);
        AssemblyLoadContext.Default.Resolving += Resolve;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
        try { Assembly = LoadShared(assemblyPath); }
        catch
        {
            AssemblyLoadContext.Default.Resolving -= Resolve;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveNative;
            throw;
        }
    }

    public Assembly Assembly { get; }

    private IntPtr ResolveNative(Assembly assembly, string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? IntPtr.Zero : System.Runtime.InteropServices.NativeLibrary.Load(path);
    }

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (_disposed)
            return null;
        var existing = context.Assemblies.FirstOrDefault(item => item.GetName().Name == name.Name);
        if (existing is not null)
        {
            if (IsAvaloniaAssembly(name.Name) && name.Version is { } requested && requested != existing.GetName().Version)
                throw VersionMismatch(name.Name!, requested, existing.GetName().Version);
            return existing;
        }
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is null && name.Name is { } simpleName && Path.GetFileName(simpleName) == simpleName)
        {
            var adjacent = Path.Combine(_directory, simpleName + ".dll");
            if (File.Exists(adjacent))
                path = adjacent;
        }
        if (path is null)
            return null;
        ValidateAvaloniaReferences(path);
        return LoadShared(path);
    }

    private static Assembly LoadShared(string path)
    {
        var identity = AssemblyName.GetAssemblyName(path);
        var existing = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(item => item.GetName().FullName == identity.FullName);
        if (existing is not null)
        {
            if (!string.Equals(Path.GetFullPath(existing.Location), Path.GetFullPath(path),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var pe = new PEReader(input);
                var metadata = pe.GetMetadataReader();
                var moduleId = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
                if (moduleId != existing.ManifestModule.ModuleVersionId)
                    throw new PreviewDocumentException("assembly_already_loaded",
                        $"Assembly '{identity.Name}' is already loaded from '{existing.Location}' with a different build. Restart the preview process or select matching application/library outputs for '{path}'.");
            }
            return existing;
        }
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    internal static void ValidateAvaloniaReferences(string path)
    {
        var hostVersion = typeof(Control).Assembly.GetName().Version;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                throw new PreviewDocumentException("assembly_not_managed", $"Assembly has no managed metadata: {path}");
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var name = metadata.GetString(reference.Name);
                if (IsCoreAvaloniaReference(name) && reference.Version != hostVersion)
                    throw VersionMismatch(name, reference.Version, hostVersion);
            }
        }
        catch (BadImageFormatException exception)
        {
            throw new PreviewDocumentException("invalid_assembly", $"Cannot read managed assembly '{path}': {exception.Message}", exception);
        }
    }

    private static bool IsAvaloniaAssembly(string? name) =>
        name == "Avalonia" || name?.StartsWith("Avalonia.", StringComparison.Ordinal) == true;

    private static bool IsCoreAvaloniaReference(string name) => name is
        "Avalonia" or "Avalonia.Base" or "Avalonia.Controls" or "Avalonia.Markup" or "Avalonia.Markup.Xaml";

    private static PreviewDocumentException VersionMismatch(string name, Version requested, Version? host) =>
        new("avalonia_version_mismatch",
            $"'{name}' requires Avalonia assembly version {requested}; this preview host uses {host}. Build the target against the same Avalonia version.");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AssemblyLoadContext.Default.Resolving -= Resolve;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveNative;
    }
}

