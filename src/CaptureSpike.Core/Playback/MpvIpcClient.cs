using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CaptureSpike.Core.Playback;

/// <summary>
/// Minimal mpv JSON IPC client. Sends commands as newline-terminated JSON
/// with a request id and matches responses; anything else is raised as an
/// event (e.g. end-file, property-change).
/// </summary>
public sealed class MpvIpcClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _readCts = new();
    private readonly Task _readLoop;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _nextId;

    public event Action<JsonElement>? EventReceived;

    private MpvIpcClient(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Test seam: build a client over an arbitrary stream.</summary>
    internal static MpvIpcClient CreateForTest(Stream stream) => new(stream);

    public static async Task<MpvIpcClient> ConnectUnixAsync(string socketPath, CancellationToken ct = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new MpvIpcClient(new NetworkStream(socket, ownsSocket: true));
    }

    public static async Task<MpvIpcClient> ConnectPipeAsync(string pipeName, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ct).ConfigureAwait(false);
        return new MpvIpcClient(pipe);
    }

    /// <summary>Runs an mpv command and returns the parsed response JSON.</summary>
    /// <exception cref="InvalidOperationException">mpv reported an error.</exception>
    public async Task<JsonElement> CommandAsync(object[] command, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var msg = JsonSerializer.Serialize(new { command, request_id = id }) + "\n";
            await _writer.WriteAsync(msg.AsMemory(), ct).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<T?> GetPropertyAsync<T>(string name, CancellationToken ct = default)
    {
        var resp = await CommandAsync(["get_property", name], ct).ConfigureAwait(false);
        if (resp.TryGetProperty("error", out var err) && err.GetString() != "success")
            return default;
        if (resp.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
            return data.Deserialize<T>();
        return default;
    }

    public Task SetPropertyAsync(string name, object value, CancellationToken ct = default) =>
        CommandAsync(["set_property", name, value], ct);

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _reader.ReadLineAsync(_readCts.Token).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }

                var root = doc.RootElement;
                if (root.TryGetProperty("request_id", out var idProp) &&
                    idProp.ValueKind == JsonValueKind.Number &&
                    _pending.TryRemove(idProp.GetInt32(), out var tcs))
                {
                    tcs.TrySetResult(root.Clone());
                }
                else
                {
                    EventReceived?.Invoke(root.Clone());
                }
                doc.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            foreach (var kv in _pending)
                kv.Value.TrySetException(new IOException("mpv IPC connection closed."));
        }
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _readCts.Cancel();
        try { await _readLoop.ConfigureAwait(false); } catch { }
        // The reader and writer share one stream: dispose the writer first, since
        // disposing the reader closes the stream and makes the writer's flush throw
        // ("Cannot access a closed pipe"). mpv may also have died already, leaving
        // the transport broken — disposal must never throw.
        foreach (var disposable in new IDisposable[] { _writer, _reader, _stream })
        {
            try { disposable.Dispose(); }
            catch { /* already closed or broken */ }
        }
        _readCts.Dispose();
    }
}
