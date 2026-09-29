using Carvera.Core.State;

namespace Carvera.Core.Transfer;

/// <summary>
/// The state of the remote file browser: the folder being shown, its entries and the selection. Layout components show
/// it and commands drive it; it is published under <c>remote.*</c> in the state store. The first listing happens by itself
/// once a machine is connected and idle, and the list refreshes after an upload into the folder shown.
/// </summary>
public sealed class RemoteBrowser : IDisposable
{
    private readonly CarveraController _controller;
    private readonly RemoteFiles _files;
    private readonly object _gate = new();
    private IReadOnlyList<RemoteEntry> _entries = [];
    private string _directory;
    private string? _selected;
    private int _generation;
    private bool _listedOnce;

    /// <param name="initialDirectory">The folder shown first, normally the upload folder from the settings.</param>
    public RemoteBrowser(CarveraController controller, string? initialDirectory = null)
    {
        _controller = controller;
        _directory = RemoteFiles.Normalize(string.IsNullOrWhiteSpace(initialDirectory) ? RemoteFiles.Root : initialDirectory);
        if (_directory.Length < RemoteFiles.Root.Length) _directory = RemoteFiles.Root;
        _files = new RemoteFiles(controller);
        controller.State.Changed += OnStateChanged;
        Publish(loading: false, error: null);
    }

    public event Action? Changed;

    public RemoteFiles Files => _files;
    public string Directory { get { lock (_gate) return _directory; } }
    public IReadOnlyList<RemoteEntry> Entries { get { lock (_gate) return _entries; } }
    public RemoteEntry? Selected { get { lock (_gate) return _entries.FirstOrDefault(e => e.Path == _selected); } }

    public void Dispose() => _controller.State.Changed -= OnStateChanged;

    private void OnStateChanged(IReadOnlyCollection<string> paths)
    {
        var state = _controller.State;
        if (paths.Contains(StatePaths.Connected) && !state.Get(StatePaths.Connected, false))
        {
            // Disconnected: forget the listing so a stale one is not shown against a different machine.
            lock (_gate) { _entries = []; _selected = null; _listedOnce = false; _generation++; }
            Publish(loading: false, error: null);
            return;
        }
        if (!_listedOnce && state.Get(StatePaths.Connected, false) && state.Get<string>(StatePaths.MachineState) == "Idle")
        {
            _listedOnce = true;
            _ = RefreshAsync();
        }
        // A finished upload changes the folder it went into.
        if (paths.Contains(StatePaths.TransferActive) && !state.Get(StatePaths.TransferActive, false)
            && state.Get<string>(StatePaths.TransferMessage)?.StartsWith("Uploaded", StringComparison.Ordinal) == true && _listedOnce)
            _ = RefreshAsync();
    }

    private void Publish(bool loading, string? error)
    {
        var state = _controller.State;
        RemoteEntry? selected;
        string directory;
        int count;
        lock (_gate)
        {
            selected = _entries.FirstOrDefault(e => e.Path == _selected);
            directory = _directory;
            count = _entries.Count;
        }
        using (state.BeginBatch())
        {
            state.Set(StatePaths.RemoteDirectory, directory);
            state.Set(StatePaths.RemoteCount, count);
            state.Set(StatePaths.RemoteLoading, loading);
            state.Set(StatePaths.RemoteError, error);
            state.Set(StatePaths.RemoteSelected, selected?.Path);
            state.Set(StatePaths.RemoteSelectedName, selected?.Name);
            state.Set(StatePaths.RemoteSelectedIsDirectory, selected?.IsDirectory ?? false);
        }
        Changed?.Invoke();
    }

    /// <summary>Lists <paramref name="directory"/> (the current one when null). Errors are published, not thrown.</summary>
    public async Task OpenAsync(string? directory = null)
    {
        var target = RemoteFiles.Normalize(directory ?? Directory);
        if (target.Length < RemoteFiles.Root.Length) target = RemoteFiles.Root;
        int generation;
        lock (_gate) generation = ++_generation;
        Publish(loading: true, error: null);
        try
        {
            var entries = await _files.ListAsync(target).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation) return; // a newer request superseded this one
                _directory = target;
                _entries = entries;
                if (_selected is not null && entries.All(e => e.Path != _selected)) _selected = null;
            }
            Publish(loading: false, error: null);
        }
        catch (Exception ex) when (ex is RemoteFileException or InvalidOperationException)
        {
            lock (_gate) { if (generation != _generation) return; }
            Publish(loading: false, error: ex.Message);
        }
    }

    public Task RefreshAsync() => OpenAsync(null);

    public Task UpAsync() => OpenAsync(RemoteFiles.Parent(Directory));

    public void Select(string? path)
    {
        lock (_gate) _selected = path;
        Publish(loading: _controller.State.Get(StatePaths.RemoteLoading, false), error: _controller.State.Get<string>(StatePaths.RemoteError));
    }

    /// <summary>Runs a change on the machine, then shows the folder again. Failures are published as the error.</summary>
    private async Task ChangeAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) when (ex is RemoteFileException or InvalidOperationException)
        {
            Publish(loading: false, error: ex.Message);
            return;
        }
        await RefreshAsync().ConfigureAwait(false);
    }

    public Task DeleteAsync(string path) => ChangeAsync(() => _files.DeleteAsync(path));

    public Task MakeDirectoryAsync(string name) => ChangeAsync(() => _files.MakeDirectoryAsync(RemoteFiles.Combine(Directory, name)));

    public Task RenameAsync(string path, string newName) =>
        ChangeAsync(async () =>
        {
            var target = RemoteFiles.Combine(RemoteFiles.Parent(path), newName);
            await _files.RenameAsync(path, target).ConfigureAwait(false);
            lock (_gate) _selected = target;
        });
}
