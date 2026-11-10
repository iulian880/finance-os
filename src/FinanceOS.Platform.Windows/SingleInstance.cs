using System.IO.Pipes;
using System.Security.Principal;

namespace FinanceOS.Platform.Windows;

/// <summary>
/// Ensures one running instance per Windows user. A second launch forwards its command-line
/// arguments to the first instance over a per-user named pipe and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public SingleInstance(string appId)
    {
        // Scoped to the user: the mutex lives in the session namespace, the pipe name carries the SID,
        // and the pipe itself only accepts connections from the same user.
        var userKey = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{appId}.{userKey}", out var createdNew);
        IsPrimary = createdNew;
        _pipeName = $"{appId}.{userKey}";
    }

    public bool IsPrimary { get; }

    /// <summary>Starts accepting forwarded arguments. <paramref name="onArguments"/> runs on a background thread.</summary>
    public void StartListening(Action<string[]> onArguments)
    {
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance can listen.");
        }

        _ = Task.Run(() => ListenAsync(onArguments, _cts.Token));
    }

    public bool TrySendToPrimary(IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client);
            // Always send a marker so an empty command line still means "bring the window forward".
            writer.Write(string.Join('\n', ["--activate", .. args]));
            writer.Flush();
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task ListenAsync(Action<string[]> onArguments, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                onArguments(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client disconnected mid-write; keep serving.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
