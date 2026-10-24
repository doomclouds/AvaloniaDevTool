using System.Security.Cryptography;
using Avalonia.Platform;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Owns bounded sequential readers for one diagnostics connection.</summary>
internal sealed class AssetExportService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Reader> _readers = new(StringComparer.Ordinal);
    private readonly Func<string, Stream> _open;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer _expiry;
    private bool _disposed;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    public AssetExportService() : this(uri => AssetLoader.Open(AssetInspectionService.ValidateUri(uri))) { }
    internal AssetExportService(Func<string, Stream> open, Func<DateTimeOffset>? clock = null)
    {
        _open = open;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _expiry = new Timer(_ => { lock (_gate) Expire(); }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    internal int ActiveCount { get { lock (_gate) return _readers.Count; } }

    public BeginAssetExportResult Begin(BeginAssetExportRequest request)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Expire();
            if (_readers.Count >= 2) throw new RpcException("asset_export_busy", "At most two asset exports can be open per session.");
            Stream? stream = null;
            try
            {
                stream = _open(request.Uri);
                var length = AssetInspectionService.TryGetLength(stream);
                if (length > AssetExportMethods.MaximumBytes)
                    throw new RpcException("asset_export_limit", "The asset exceeds the 100 MiB export limit.");
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
                _readers.Add(token, new Reader(stream, length, _clock()));
                stream = null;
                return new BeginAssetExportResult(token, request.Uri, length, AssetExportMethods.ChunkBytes, AssetExportMethods.MaximumBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { throw new RpcException("asset_read_failed", ex.Message); }
            finally { stream?.Dispose(); }
        }
    }

    public AssetExportChunk Read(ReadAssetExportRequest request)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Expire();
            if (!_readers.TryGetValue(request.Token, out var reader))
                throw new RpcException("asset_export_expired", "The export token is unknown, closed, or expired.");
            if (request.Offset != reader.Offset)
                throw new RpcException("asset_export_offset", "Chunks must be read in their original byte order.");
            try
            {
                var buffer = new byte[AssetExportMethods.ChunkBytes];
                var count = 0;
                var complete = false;
                while (count < buffer.Length)
                {
                    var remaining = AssetExportMethods.MaximumBytes - reader.Offset - count;
                    var read = reader.Stream.Read(buffer, count, (int)Math.Min(buffer.Length - count, remaining + 1));
                    if (read == 0) { complete = true; break; }
                    count += read;
                    if (reader.Offset + count > AssetExportMethods.MaximumBytes)
                        throw new RpcException("asset_export_limit", "The asset exceeds the 100 MiB export limit.");
                }
                if (reader.TotalBytes is { } total && reader.Offset + count == total) complete = true;
                if (complete && reader.TotalBytes is { } expected && reader.Offset + count != expected)
                    throw new RpcException("asset_length_changed", "The asset stream ended before its reported length.");
                var offset = reader.Offset;
                reader.Hash.AppendData(buffer.AsSpan(0, count));
                reader.Offset += count;
                reader.LastRead = _clock();
                var hash = complete ? Convert.ToHexString(reader.Hash.GetHashAndReset()).ToLowerInvariant() : null;
                var bytes = count == buffer.Length ? buffer : buffer.AsSpan(0, count).ToArray();
                var result = new AssetExportChunk(offset, bytes, complete, complete ? reader.Offset : reader.TotalBytes, hash);
                if (complete) Remove(request.Token);
                return result;
            }
            catch (Exception ex)
            {
                Remove(request.Token);
                if (ex is RpcException) throw;
                throw new RpcException("asset_read_failed", ex.Message);
            }
        }
    }

    public OperationResult Close(CloseAssetExportRequest request)
    {
        lock (_gate) Remove(request.Token);
        return new OperationResult();
    }

    private void Expire()
    {
        foreach (var token in _readers.Where(pair => _clock() - pair.Value.LastRead >= IdleTimeout).Select(pair => pair.Key).ToArray())
            Remove(token);
    }
    private void Remove(string token) { if (_readers.Remove(token, out var reader)) reader.Dispose(); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _expiry.Dispose();
            foreach (var reader in _readers.Values) reader.Dispose();
            _readers.Clear();
        }
    }
    private sealed class Reader(Stream stream, long? totalBytes, DateTimeOffset lastRead) : IDisposable
    {
        public Stream Stream { get; } = stream;
        public long? TotalBytes { get; } = totalBytes;
        public long Offset { get; set; }
        public DateTimeOffset LastRead { get; set; } = lastRead;
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public void Dispose()
        {
            try { Stream.Dispose(); }
            catch { /* Reader cleanup must not prevent other session readers from closing. */ }
            finally { Hash.Dispose(); }
        }
    }
}
