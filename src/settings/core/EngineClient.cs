using System.IO.Pipes;
using System.Text;

namespace MagneticBorders.Settings.Core;

/// <summary>Sends one command to the running engine; the seam that lets tests replace the named pipe.</summary>
public interface IEngineClient
{
    /// <summary>Sends a command such as "status" or "quit". Never throws for connection problems; returns a failed PipeResponse instead.</summary>
    Task<PipeResponse> SendAsync(string cmd, CancellationToken ct = default);
}

/// <summary>
/// Named-pipe client for \\.\pipe\MagneticBorders: one JSON line per request and per response, one new
/// connection per call. Safe to call from any thread; SettingsViewModel awaits it on the UI thread.
/// </summary>
public sealed class PipeEngineClient : IEngineClient
{
    /// <summary>Pipe name shared by all three engines.</summary>
    public const string PipeName = "MagneticBorders";
    private readonly string _pipe;
    private readonly int _timeoutMs;

    /// <summary>Creates a client; timeoutMs covers connect, write, and read together.</summary>
    public PipeEngineClient(string pipeName = PipeName, int timeoutMs = 1000)
    {
        _pipe = pipeName;
        _timeoutMs = timeoutMs;
    }

    /// <summary>Connects, writes {"cmd":...}, reads one line and parses it. A missing engine, timeout, or I/O error yields PipeResponse.Failure("engine not reachable").</summary>
    public async Task<PipeResponse> SendAsync(string cmd, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);
            await using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            var req = Encoding.UTF8.GetBytes("{\"cmd\":\"" + cmd + "\"}\n");
            await client.WriteAsync(req, cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            return PipeResponse.Parse(line);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return PipeResponse.Failure("engine not reachable");
        }
    }
}
