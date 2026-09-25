namespace Carvera.Core;

public enum ConsoleEntryKind { Sent, Received, Info, Warning, Error }

public sealed record ConsoleEntry(DateTime Time, ConsoleEntryKind Kind, string Text);

/// <summary>Bounded, thread-safe log of machine traffic and application messages.</summary>
public sealed class ConsoleLog(int capacity = 2000)
{
    private readonly object _gate = new();
    private readonly LinkedList<ConsoleEntry> _entries = new();

    public event Action<ConsoleEntry>? EntryAdded;
    public event Action? Cleared;

    public void Add(ConsoleEntryKind kind, string text)
    {
        var entry = new ConsoleEntry(DateTime.Now, kind, text);
        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > capacity) _entries.RemoveFirst();
        }
        EntryAdded?.Invoke(entry);
    }

    public void Info(string text) => Add(ConsoleEntryKind.Info, text);
    public void Warning(string text) => Add(ConsoleEntryKind.Warning, text);
    public void Error(string text) => Add(ConsoleEntryKind.Error, text);

    public IReadOnlyList<ConsoleEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Cleared?.Invoke();
    }
}
