using System.Diagnostics;
using System.Text.Json;

namespace AvaloniaDevTool.Recording;

internal sealed class RecordingAdmission : IDisposable
{
    private readonly FileStream _lock;

    private RecordingAdmission(FileStream admissionLock) => _lock = admissionLock;

    internal static RecordingAdmission Acquire(string? dataDirectory = null)
    {
        var directory = dataDirectory ?? RecordingArtifacts.DefaultDirectory;
        Directory.CreateDirectory(directory);
        FileStream admission;
        try
        {
            admission = new FileStream(Path.Combine(directory, "active.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new RecordingException("recording_busy",
                "Another recording on this computer is starting, recording or finalizing.", error);
        }
        try
        {
            using var current = Process.GetCurrentProcess();
            var owner = new RecordingOwner(current.Id, current.StartTime.ToUniversalTime());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(owner, RecordingJsonContext.Default.RecordingOwner);
            admission.SetLength(0);
            admission.Write(bytes);
            admission.Flush();
            return new RecordingAdmission(admission);
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }

    public void Dispose() => _lock.Dispose();
}
