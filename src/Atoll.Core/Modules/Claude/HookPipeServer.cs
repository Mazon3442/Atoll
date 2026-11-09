using System.IO.Pipes;
using System.Text.Json;
using Atoll.Claude;

namespace Atoll.Modules.Claude;

/// <summary>
/// Receives <see cref="HookMessage"/> lines from Atoll.ClaudeHook. Two listeners run so a burst
/// of parallel tool calls does not make hooks wait. The pipe only accepts the current user.
/// <see cref="Received"/> fires on a thread-pool thread.
/// </summary>
public sealed class HookPipeServer(string pipeName = HookProtocol.PipeName) : IDisposable
{
    private const int Listeners = 2;
    private readonly CancellationTokenSource _cts = new();

    public event Action<HookMessage>? Received;

    /// <summary>Answers a message that <see cref="HookMessage.WantsReply"/>: "allow", "deny" or null for the terminal.</summary>
    public Func<HookMessage, Task<string?>>? Decide { get; set; }

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
                var server = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await server.WaitForConnectionAsync(ct);
                    var message = await ReadMessageAsync(server, ct);
                    if (message is { WantsReply: true } && Decide is { } decide)
                    {
                        _ = ReplyAsync(server, decide(message), ct); // owns the pipe now; this listener moves on
                        server = null;
                    }
                    else if (message is not null) Received?.Invoke(message);
                }
                finally { if (server is not null) await server.DisposeAsync(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) { } // A stalled sender timed out; keep the listener alive.
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Error("Claude hook message dropped", ex);
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

    private static async Task ReplyAsync(NamedPipeServerStream server, Task<string?> decision, CancellationToken ct)
    {
        await using (server)
        {
            try
            {
                var answer = await decision.WaitAsync(HookProtocol.ReplyTimeout, ct);
                await server.WriteAsync(System.Text.Encoding.UTF8.GetBytes((answer ?? "") + "\n"), ct);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException) { } // the terminal asks instead
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
