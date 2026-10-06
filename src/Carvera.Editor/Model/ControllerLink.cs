using System.IO.Pipes;
using System.Text.Json.Nodes;
using Carvera.App.Services;

namespace Carvera.Editor.Model;

/// <summary>
/// The editor's end of the link to a running Controller: it connects to the Controller's pipe (retrying while the Controller is not
/// running), learns which layout the Controller shows, asks it to reveal an element, and hears which element was picked there.
/// </summary>
public sealed class ControllerLink(string? pipeName = null) : IDisposable
{
    private readonly string _pipeName = pipeName ?? EditorLink.PipeName;

    private readonly CancellationTokenSource _cts = new();
    private volatile LineWriter? _writer;

    public bool Connected { get; private set; }
    /// <summary>The name of the layout the Controller is showing, or null.</summary>
    public string? Layout { get; private set; }
    public string? LayoutFile { get; private set; }
    public bool Picking { get; private set; }

    /// <summary>Raised on a background thread.</summary>
    public event Action? StatusChanged;
    /// <summary>Raised on a background thread with the layout path of the element picked in the Controller.</summary>
    public event Action<string, string?>? Picked;

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public void Reveal(string path) => Send(new JsonObject { ["cmd"] = "reveal", ["path"] = path });

    /// <summary>Asks the Controller to show a layout, by name or by file.</summary>
    public void Load(string nameOrPath) => Send(new JsonObject { ["cmd"] = "load", ["layout"] = nameOrPath });

    public void SetPicking(bool on)
    {
        Picking = on;
        Send(new JsonObject { ["cmd"] = "inspect", ["on"] = on });
        StatusChanged?.Invoke();
    }

    private void Send(JsonObject message) => _writer?.Send(message.ToJsonString());

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(1000, token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                _writer = new LineWriter(new StreamWriter(pipe) { AutoFlush = true });
                Connected = true;
                Picking = false;
                StatusChanged?.Invoke();
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    try { Handle(JsonNode.Parse(line) as JsonObject); }
                    catch (System.Text.Json.JsonException) { }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (TimeoutException) { }
            catch (IOException) { }
            finally
            {
                var writer = _writer;
                _writer = null;
                if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
                if (Connected)
                {
                    Connected = false;
                    Layout = null;
                    LayoutFile = null;
                    Picking = false;
                    StatusChanged?.Invoke();
                }
            }
            try { await Task.Delay(1000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Handle(JsonObject? message)
    {
        switch (message?["event"]?.GetValue<string>())
        {
            case "layout":
                Layout = message["name"]?.GetValue<string>();
                LayoutFile = message["file"]?.GetValue<string>();
                StatusChanged?.Invoke();
                break;
            case "inspect":
                Picking = message["on"]?.GetValue<bool>() ?? false;
                StatusChanged?.Invoke();
                break;
            case "picked" when message["path"]?.GetValue<string>() is { } path:
                Picked?.Invoke(path, message["layout"]?.GetValue<string>());
                break;
        }
    }

    public void Dispose() => _cts.Cancel();
}
