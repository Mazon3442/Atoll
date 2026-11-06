using System.IO.Pipes;
using System.Text.Json;
using Island.Claude;

namespace Island.Modules.Claude;

/// <summary>
/// Receives <see cref="HookMessage"/> lines from Island.ClaudeHook. Two listeners run so a burst
/// of parallel tool calls does not make hooks wait. The pipe only accepts the current user.
/// <see cref="Received"/> fires on a thread-pool thread.
/// </summary>
public sealed class HookPipeServer : IDisposable
{
    private const int Listeners = 2;
    private readonly CancellationTokenSource _cts = new();

    public event Action<HookMessage>? Received;

    public void Start()
    {
        for (var i = 0; i < Listeners; i++) _ = ListenAsync(_cts.Token);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    HookProtocol.PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                var message = await ReadMessageAsync(server, ct);
                if (message is not null) Received?.Invoke(message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Error("Claude hook message dropped", ex);
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

    private static async Task<HookMessage?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[HookProtocol.MaxMessageBytes + 1];
        var length = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2)); // a stuck client must not hold a listener
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
            if (read == 0) break;
            length += read;
            if (Array.IndexOf(buffer, (byte)'\n', length - read, read) >= 0) break;
        }
        if (length == 0 || length > HookProtocol.MaxMessageBytes) return null;
        var end = Array.IndexOf(buffer, (byte)'\n', 0, length);
        return JsonSerializer.Deserialize<HookMessage>(buffer.AsSpan(0, end < 0 ? length : end), HookProtocol.Json);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
