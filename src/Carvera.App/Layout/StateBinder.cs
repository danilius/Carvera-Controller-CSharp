using Avalonia.Threading;
using Carvera.Core.State;

namespace Carvera.App.Layout;

/// <summary>
/// Delivers <see cref="StateStore"/> changes to UI subscribers on the UI thread. Changes arriving between
/// dispatches are coalesced, so a status report updates each subscriber once.
/// </summary>
public sealed class StateBinder : IDisposable
{
    private readonly StateStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Action>> _subscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _scheduled;

    public StateBinder(StateStore store)
    {
        _store = store;
        _store.Changed += OnChanged;
    }

    public StateStore Store => _store;

    /// <summary>Calls <paramref name="onChange"/> on the UI thread whenever any of <paramref name="paths"/> changes.</summary>
    public IDisposable Subscribe(IEnumerable<string> paths, Action onChange)
    {
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_gate)
            foreach (var path in list)
            {
                if (!_subscribers.TryGetValue(path, out var actions)) _subscribers[path] = actions = [];
                actions.Add(onChange);
            }
        return new Subscription(() =>
        {
            lock (_gate)
                foreach (var path in list)
                    if (_subscribers.TryGetValue(path, out var actions)) actions.Remove(onChange);
        });
    }

    private void OnChanged(IReadOnlyCollection<string> paths)
    {
        lock (_gate)
        {
            foreach (var p in paths) _pending.Add(p);
            if (_scheduled) return;
            _scheduled = true;
        }
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    /// <summary>Delivers pending changes now (used by tests and after synchronous updates on the UI thread).</summary>
    public void Flush()
    {
        var actions = new List<Action>();
        var seen = new HashSet<Action>();
        lock (_gate)
        {
            _scheduled = false;
            foreach (var path in _pending)
                if (_subscribers.TryGetValue(path, out var list))
                    foreach (var action in list)
                        if (seen.Add(action)) actions.Add(action);
            _pending.Clear();
        }
        foreach (var action in actions) action();
    }

    public void Dispose() => _store.Changed -= OnChanged;

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
