using System.Reflection;
using System.Text.Json;
using Carvera.Core.State;
using Carvera.Core.Transfer;

namespace Carvera.Core.Config;

/// <summary>One setting of the machine's <c>config.txt</c>, as described by the Python controller's config_c1.json / config_ca1.json.</summary>
public sealed record ConfigItem(string Type, string Title, string Description, string Section, string Key, IReadOnlyList<string> Options, string? Default)
{
    public bool IsTitle => Type == "title";
    public bool IsBool => Type == "bool";
    public bool IsNumeric => Type == "numeric";
}

/// <summary>The settings a machine model has, in display order.</summary>
public static class ConfigSchema
{
    public static IReadOnlyList<ConfigItem> Load(string? model)
    {
        var resource = model?.ToUpperInvariant() switch { "C1" => "config_c1.json", "CA1" => "config_ca1.json", _ => null };
        if (resource is null) return [];
        using var stream = typeof(ConfigSchema).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Missing resource {resource}.");
        using var document = JsonDocument.Parse(stream);
        var items = new List<ConfigItem>();
        foreach (var e in document.RootElement.EnumerateArray())
        {
            string S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var options = e.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array ? o.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            items.Add(new ConfigItem(S("type"), S("title"), S("desc"), S("section"), S("key"), options, e.TryGetProperty("default", out var d) ? d.ToString() : null));
        }
        return items;
    }

    /// <summary>Settings the user edits (Basic and Advanced sections), with their group titles.</summary>
    public static IEnumerable<ConfigItem> Editable(IEnumerable<ConfigItem> items) => items.Where(i => i.Section is "Basic" or "Advanced");
}

public static class ConfigFile
{
    /// <summary>
    /// Parses a machine <c>config.txt</c>: "key value" lines with # comments. Values keep their inner spaces, so the
    /// key is everything up to the first blank.
    /// </summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;
            var cut = line.IndexOfAny([' ', '\t']);
            if (cut < 0) values[line] = "";
            else values[line[..cut]] = line[(cut + 1)..].Trim();
        }
        return values;
    }

    /// <summary>The text of a value as the machine wants it written: booleans as true/false.</summary>
    public static string ToMachine(ConfigItem item, string value) =>
        item.IsBool ? (value is "1" or "true" or "True" or "TRUE" ? "true" : "false") : value.Trim();

    /// <summary>The text of a value as an editor shows it: booleans as "true"/"false" whatever the file spelled.</summary>
    public static string ToDisplay(ConfigItem item, string value) =>
        item.IsBool ? (value is "1" or "true" or "True" or "TRUE" ? "true" : "false") : value;
}

/// <summary>
/// The machine's configuration: its schema, the values read from <c>/sd/config.txt</c>, and the changes waiting to be sent
/// with <c>config-set sd</c>. Ported from the Python controller's config popup. Changes take effect when the machine is reset.
/// </summary>
public sealed class MachineConfigStore
{
    private readonly CarveraController _controller;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);

    public MachineConfigStore(CarveraController controller) => _controller = controller;

    /// <summary>Raised after every change, including each edit.</summary>
    public event Action? Changed;

    /// <summary>Raised when the whole list must be shown again: loaded, discarded or sent.</summary>
    public event Action? Reset;

    public IReadOnlyList<ConfigItem> Items { get; private set; } = [];
    public IReadOnlyDictionary<string, string> Values => _values;
    public IReadOnlyDictionary<string, string> Pending => _pending;
    public bool Loaded { get; private set; }

    /// <summary>The value to show for an item: the pending edit, else what the machine has, else the schema default.</summary>
    public string Current(ConfigItem item) =>
        ConfigFile.ToDisplay(item, _pending.TryGetValue(item.Key, out var p) ? p : _values.TryGetValue(item.Key, out var v) ? v : item.Default ?? "");

    /// <summary>Records an edit. Setting a value back to what the machine has removes the pending change.</summary>
    public void Edit(ConfigItem item, string value)
    {
        var text = ConfigFile.ToMachine(item, value);
        var onMachine = _values.TryGetValue(item.Key, out var current) ? ConfigFile.ToMachine(item, current) : item.Default is null ? null : ConfigFile.ToMachine(item, item.Default);
        if (onMachine == text) _pending.Remove(item.Key); else _pending[item.Key] = text;
        Publish();
    }

    public void Discard()
    {
        _pending.Clear();
        Publish(reset: true);
    }

    /// <summary>Downloads <c>/sd/config.txt</c> and reads it. The schema follows the machine model.</summary>
    public async Task<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        var model = _controller.State.Get<string>(StatePaths.MachineModelName);
        Items = ConfigSchema.Load(model);
        if (Items.Count == 0)
        {
            _controller.Console.Warning(string.IsNullOrEmpty(model) ? "The machine model is not known yet, so its settings cannot be listed." : $"There is no settings list for the {model}.");
            Publish(reset: true);
            return false;
        }
        var temp = Path.Combine(Path.GetTempPath(), $"carvera-config-{Guid.NewGuid():N}.txt");
        try
        {
            if (await FileDownloader.DownloadAsync(_controller, "/sd/config.txt", temp, 5 * 1024, cancellationToken).ConfigureAwait(false) != DownloadResult.Success) return false;
            _values.Clear();
            foreach (var (key, value) in ConfigFile.Parse(await File.ReadAllTextAsync(temp, cancellationToken).ConfigureAwait(false))) _values[key] = value;
            _pending.Clear();
            Loaded = true;
            Publish(reset: true);
            return true;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>Sends every pending change (<c>config-set sd key value</c>) and moves it into the known values.</summary>
    public async Task<int> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var (key, value) in _pending.ToList())
        {
            if (value.Length == 0) continue; // the Python controller does not send empty values either
            await _controller.SendLineAsync($"config-set sd {key} {value}").ConfigureAwait(false);
            _values[key] = value;
            _pending.Remove(key);
            count++;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false); // the machine takes them one at a time
        }
        Publish(reset: true);
        return count;
    }

    private void Publish(bool reset = false)
    {
        _controller.State.Set(StatePaths.ConfigLoaded, Loaded);
        _controller.State.Set(StatePaths.ConfigPending, _pending.Count);
        Changed?.Invoke();
        if (reset) Reset?.Invoke();
    }
}
