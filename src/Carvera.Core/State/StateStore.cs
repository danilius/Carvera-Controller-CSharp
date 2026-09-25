using System.Globalization;

namespace Carvera.Core.State;

/// <summary>
/// Thread-safe store of named machine and application values, addressed by dotted paths such as
/// <c>axis.x.work</c>. Layout components bind to these paths. Updates made inside
/// <see cref="BeginBatch"/> are published together once the batch is disposed.
/// </summary>
public sealed class StateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private int _batchDepth;

    /// <summary>Raised after values change, with the set of changed paths. May be raised on any thread.</summary>
    public event Action<IReadOnlyCollection<string>>? Changed;

    public object? Get(string path)
    {
        lock (_gate) return _values.TryGetValue(path, out var value) ? value : null;
    }

    public bool Contains(string path)
    {
        lock (_gate) return _values.ContainsKey(path);
    }

    public T Get<T>(string path, T fallback = default!)
    {
        var value = Get(path);
        if (value is null) return fallback;
        if (value is T typed) return typed;
        try
        {
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (target == typeof(bool)) return (T)(object)ToBoolean(value);
            return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return fallback;
        }
    }

    public void Set(string path, object? value)
    {
        IReadOnlyCollection<string>? publish = null;
        lock (_gate)
        {
            if (_values.TryGetValue(path, out var existing) && Equals(existing, value)) return;
            _values[path] = value;
            _pending.Add(path);
            if (_batchDepth == 0) publish = Drain();
        }
        if (publish is not null) Changed?.Invoke(publish);
    }

    public IDisposable BeginBatch()
    {
        lock (_gate) _batchDepth++;
        return new Batch(this);
    }

    public IReadOnlyDictionary<string, object?> Snapshot()
    {
        lock (_gate) return new Dictionary<string, object?>(_values, StringComparer.OrdinalIgnoreCase);
    }

    private void EndBatch()
    {
        IReadOnlyCollection<string>? publish = null;
        lock (_gate)
        {
            if (--_batchDepth == 0 && _pending.Count > 0) publish = Drain();
        }
        if (publish is not null) Changed?.Invoke(publish);
    }

    private string[] Drain()
    {
        var changed = _pending.ToArray();
        _pending.Clear();
        return changed;
    }

    public static bool ToBoolean(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0",
        IConvertible c => Convert.ToDouble(c, CultureInfo.InvariantCulture) != 0,
        _ => true,
    };

    private sealed class Batch(StateStore owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndBatch();
        }
    }
}
