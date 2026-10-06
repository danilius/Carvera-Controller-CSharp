using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace Carvera.App.Services;

/// <summary>
/// The link between the Controller and the layout editor running beside it (usually on another screen). A named pipe
/// that only the same Windows user can open; one JSON object per line in each direction. The editor sends
/// <c>{"cmd":"reveal","path":"root.children[2]"}</c> and <c>{"cmd":"inspect","on":true}</c>; the Controller sends
/// <c>{"event":"layout",...}</c> when a layout is shown and <c>{"event":"picked","path":...}</c> when an element is clicked in pick mode.
/// The link never carries machine commands.
/// </summary>
public sealed class EditorLink(string? pipeName = null) : IDisposable
{
    public const string PipeName = "CarveraController.LayoutEditor";

    private readonly string _pipeName = pipeName ?? PipeName;

    private readonly CancellationTokenSource _cts = new();
    private volatile LineWriter? _writer;

    /// <summary>Raised on a background thread for each message the editor sends.</summary>
    public event Action<JsonObject>? Message;
    /// <summary>Raised on a background thread when an editor connects (true) or goes away (false).</summary>
    public event Action<bool>? ConnectionChanged;

    public bool HasClient => _writer is not null;

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public void Send(JsonObject message) => _writer?.Send(message.ToJsonString());

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);
            }
            catch (IOException)
            {
                return; // another Controller already serves the pipe
            }

            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                _writer = new LineWriter(new StreamWriter(pipe) { AutoFlush = true });
                ConnectionChanged?.Invoke(true);
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    try
                    {
                        if (JsonNode.Parse(line) is JsonObject message) Message?.Invoke(message);
                    }
                    catch (System.Text.Json.JsonException) { /* ignore a malformed line */ }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
            finally
            {
                var writer = _writer;
                _writer = null;
                if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
                await pipe.DisposeAsync().ConfigureAwait(false);
                ConnectionChanged?.Invoke(false);
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
