using System.Text.Json.Nodes;
using Avalonia.Threading;
using Carvera.App.Services;
using Carvera.Editor.Model;
using Carvera.Layout;

namespace Carvera.Editor;

/// <summary>
/// Everything the editor's panels share: the document, the selection, the link to the Controller, and the rules that connect them
/// (edits go to the file while live, the Controller is asked to show whatever is selected).
/// </summary>
public sealed class EditorContext : IDisposable
{
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _followTimer;
    private string? _selection = "root";
    private string? _lastWritten;
    private FileSystemWatcher? _watcher;
    private string? _pendingFollow;

    public EditorContext(EditorDocument document, EditorSettings settings, ControllerLink? link = null)
    {
        Doc = document;
        Settings = settings;
        Link = link;
        Actions = new EditorActions(this);
        _lastWritten = document.SavedText;
        Doc.Changed += OnDocChanged;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _saveTimer!.Stop(); SaveIfLive(); });
        _followTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            _followTimer!.Stop();
            if (_pendingFollow is { } path) Link?.Reveal(path);
            _pendingFollow = null;
        });
        WatchFile();
    }

    public EditorDocument Doc { get; private set; }
    public EditorSettings Settings { get; }
    public ControllerLink? Link { get; }
    public EditorActions Actions { get; }

    /// <summary>Set when a layout file is opened straight from the library and would be saved as the user's own copy.</summary>
    public string? BuiltInSource { get; private set; }

    public string? Selection => _selection;

    /// <summary>
    /// Changes whenever the forms in the inspector no longer describe what is in the document (the selection moved, or the document changed
    /// other than by one of the forms). A form that was built earlier must not write anything back: its idea of which element it edits is stale.
    /// </summary>
    public int FormGeneration { get; private set; }
    public void BumpForms() => FormGeneration++;
    private bool _formEdit;

    /// <summary>Set by the main window: shows the code view at the text of a path.</summary>
    public Action<string>? RequestCodeAt { get; set; }

    /// <summary>Raised after every change to the document, or when another document is opened.</summary>
    public event Action<ChangeOrigin>? DocumentChanged;
    /// <summary>Raised when the selection changes. The second argument is whoever made the change, so it can skip re-selecting itself.</summary>
    public event Action<string?, object?>? SelectionChanged;
    /// <summary>Raised with a short message for the status bar.</summary>
    public event Action<string, bool>? Notice;
    /// <summary>Raised when the file was saved or failed to.</summary>
    public event Action? SaveStateChanged;
    /// <summary>Raised when the file on disk changed while there are unsaved edits.</summary>
    public event Action? DiskConflict;

    public string? LastSaveError { get; private set; }
    public DateTime? LastSaved { get; private set; }

    // ------------------------------------------------------------------ selection

    public void Select(string? path, object? source = null)
    {
        if (path == _selection) return;
        _selection = path;
        FormGeneration++;
        SelectionChanged?.Invoke(path, source);
        if (path is not null && Settings.FollowInController && Link is { Connected: true } && JsonPath.IsElementPath(path))
        {
            _pendingFollow = path;
            _followTimer.Stop();
            _followTimer.Start();
        }
    }

    /// <summary>The selected element as JSON, or null when the selection is not an element.</summary>
    public JsonObject? SelectedElement => Selection is { } s && JsonPath.IsElementPath(s) ? Doc.Element(s) : null;

    // ------------------------------------------------------------------ editing

    /// <summary>Runs a visual edit. On success the returned path (if any) becomes the selection; on failure the reason goes to the status bar.</summary>
    public bool Edit(string label, Func<JsonObject, string?> edit, string? coalesce = null, object? source = null)
    {
        var hadComments = Doc.HasComments;
        var result = Doc.Mutate(label, edit, coalesce);
        if (result is not null && hadComments && !Doc.HasComments) Notify("The file had comments; editing visually rewrites it without them. Undo brings them back.", warning: true);
        if (result is null)
        {
            Notify(Doc.LastError ?? "That edit is not possible.", warning: true);
            return false;
        }
        if (result.Length > 0 && result != Selection) Select(result, source);
        return true;
    }

    /// <summary>
    /// Sets or clears one property of the object at <paramref name="objectPath"/> (an element, a style, the theme, a visuals block, an args object...).
    /// Missing objects on the way are created, and objects left empty by a removal are pruned. Repeated edits of the same property share one undo step.
    /// </summary>
    public bool SetProperty(string objectPath, string name, JsonNode? value, string? label = null, object? source = null)
    {
        // The change comes from a form, which already shows the new value; the forms stay valid.
        _formEdit = true;
        try
        {
            return Edit(label ?? $"Set {name}", data =>
            {
                SetAt(data, objectPath, name, value);
                return "";
            }, coalesce: objectPath + "|" + name, source);
        }
        finally { _formEdit = false; }
    }

    public static void SetAt(JsonObject data, string objectPath, string name, JsonNode? value)
    {
        var segments = JsonPath.Parse(objectPath);
        // Walk down, creating objects as needed when there is something to set.
        var chain = new List<(JsonObject Owner, string Key, string Prefix)>();
        JsonNode? node = data;
        for (var position = 0; position < segments.Count; position++)
        {
            var segment = segments[position];
            var next = segment switch
            {
                string key when node is JsonObject o => o[key],
                int index when node is JsonArray a && index >= 0 && index < a.Count => a[index],
                _ => null,
            };
            if (next is null)
            {
                if (value is null) return; // nothing to clear
                if (segment is string missing && node is JsonObject owner) next = owner[missing] = new JsonObject();
                else return;
            }
            if (node is JsonObject ownerObject && segment is string k) chain.Add((ownerObject, k, JsonPath.Format(segments.Take(position + 1))));
            node = next;
        }
        if (node is not JsonObject target) return;
        if (value is null) target.Remove(name); else target[name] = value.DeepClone();

        // Prune objects the removal emptied, stopping at the element itself.
        if (value is not null) return;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var (owner, key, prefix) = chain[i];
            if (owner[key] is JsonObject child && child.Count == 0 && !JsonPath.IsElementPath(prefix)) owner.Remove(key);
            else break;
        }
    }

    public void Notify(string message, bool warning = false) => Notice?.Invoke(message, warning);

    public void Undo()
    {
        if (Doc.Undo()) Notify("Undid: " + (Doc.RedoLabel ?? "edit"));
    }

    public void Redo()
    {
        if (Doc.Redo()) Notify("Redid: " + (Doc.UndoLabel ?? "edit"));
    }

    private void OnDocChanged(ChangeOrigin origin)
    {
        if (!_formEdit) FormGeneration++;
        // Keep the selection on something that still exists.
        if (_selection is { } s && Doc.Data is not null && !Exists(s))
        {
            var fallback = s;
            while (JsonPath.Parent(fallback) is { } parent && !Exists(fallback)) fallback = parent;
            _selection = Exists(fallback) ? fallback : "root";
            SelectionChanged?.Invoke(_selection, null);
        }
        DocumentChanged?.Invoke(origin);
        if (origin != ChangeOrigin.External && origin != ChangeOrigin.Load)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        SaveStateChanged?.Invoke();
    }

    private bool Exists(string path) => path is "theme" or "window" or "meta" || JsonPath.Resolve(Doc.Data, path) is not null;

    // ------------------------------------------------------------------ files

    /// <summary>Opens a text as the document (the panels re-read everything).</summary>
    public void Open(EditorDocument document, string? builtInSource = null)
    {
        Doc.Changed -= OnDocChanged;
        Doc = document;
        Doc.Changed += OnDocChanged;
        BuiltInSource = builtInSource;
        _lastWritten = document.SavedText;
        LastSaveError = null;
        _selection = "root";
        WatchFile();
        DocumentChanged?.Invoke(ChangeOrigin.Load);
        SelectionChanged?.Invoke(_selection, null);
        SaveStateChanged?.Invoke();
    }

    /// <summary>Opens a layout from the Controller's library. A built-in layout is edited as a copy in the user's folder, which then takes over from the built-in one.</summary>
    public void OpenLibrary(LayoutEntry entry)
    {
        var text = LiveFile.TryRead(entry.Path);
        if (text is null)
        {
            Notify($"Cannot read {entry.Path}.", warning: true);
            return;
        }
        if (entry.IsUser)
        {
            Open(new EditorDocument(text, entry.Name, entry.Path), null);
            return;
        }
        var target = Path.Combine(EditorSettings.UserLayoutsDirectory, Path.GetFileName(entry.Path));
        Open(new EditorDocument(text, entry.Name, target, Path.GetDirectoryName(entry.Path)), entry.Path);
    }

    public void OpenFile(string path)
    {
        var text = LiveFile.TryRead(path);
        if (text is null)
        {
            Notify($"Cannot read {path}.", warning: true);
            return;
        }
        Open(new EditorDocument(text, Path.GetFileNameWithoutExtension(path), path), null);
    }

    public void SaveAs(string path)
    {
        Doc.FilePath = path;
        Doc.BaseDirectory = Path.GetDirectoryName(path)!;
        BuiltInSource = null;
        WatchFile();
        SaveNow(force: true);
    }

    /// <summary>Writes the layout to its file if it is valid. Returns whether the file is now up to date.</summary>
    public bool SaveNow(bool force = false)
    {
        if (Doc.FilePath is not { } path) return false;
        if (!Doc.IsValid)
        {
            LastSaveError = Doc.ParseError ?? "The layout has errors: " + Doc.Result.Errors.FirstOrDefault()?.Message;
            SaveStateChanged?.Invoke();
            return false;
        }
        if (!force && Doc.Text == _lastWritten) return true;
        try
        {
            LiveFile.Write(path, Doc.Text);
            _lastWritten = Doc.Text;
            Doc.MarkSaved();
            LastSaveError = null;
            LastSaved = DateTime.Now;
            SaveStateChanged?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastSaveError = ex.Message;
            SaveStateChanged?.Invoke();
            return false;
        }
    }

    private void SaveIfLive()
    {
        if (!Settings.Live || Doc.FilePath is null || Doc.Text == _lastWritten) return;
        // A broken state is held back: the Controller keeps the last good layout.
        SaveNow();
    }

    /// <summary>Discards the customised copy of a built-in layout so the Controller uses the built-in one again.</summary>
    public void RevertToBuiltIn()
    {
        if (Doc.FilePath is not { } path) return;
        var name = Path.GetFileName(path);
        var shipped = Path.Combine(EditorSettings.ShippedLayoutsDirectory, name);
        if (!File.Exists(shipped)) return;
        if (File.Exists(path)) File.Delete(path);
        OpenLibrary(new LayoutEntry(Path.GetFileNameWithoutExtension(name), shipped, false));
        Notify($"{name} is back to the built-in version.");
    }

    public bool HasCustomisedCopy => Doc.FilePath is { } path && Path.GetDirectoryName(path)!.Equals(Path.GetFullPath(EditorSettings.UserLayoutsDirectory), StringComparison.OrdinalIgnoreCase)
        && File.Exists(Path.Combine(EditorSettings.ShippedLayoutsDirectory, Path.GetFileName(path))) && File.Exists(path);

    // ------------------------------------------------------------------ external changes

    private void WatchFile()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (Doc.FilePath is not { } path || Path.GetDirectoryName(Path.GetFullPath(path)) is not { } dir || !Directory.Exists(dir)) return;
        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(OnFileChanged);
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(OnFileChanged);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException) { }
    }

    private void OnFileChanged()
    {
        if (Doc.FilePath is not { } path || LiveFile.TryRead(path) is not { } onDisk) return;
        if (onDisk == _lastWritten || onDisk == Doc.Text) return; // our own write
        if (!Doc.IsDirty)
        {
            _lastWritten = onDisk;
            Doc.Reload(onDisk);
            Notify("The file changed on disk; reloaded.");
        }
        else DiskConflict?.Invoke();
    }

    /// <summary>Takes the disk version, dropping unsaved edits.</summary>
    public void ReloadFromDisk()
    {
        if (Doc.FilePath is { } path && LiveFile.TryRead(path) is { } onDisk)
        {
            _lastWritten = onDisk;
            Doc.Reload(onDisk);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _saveTimer.Stop();
        _followTimer.Stop();
    }
}
