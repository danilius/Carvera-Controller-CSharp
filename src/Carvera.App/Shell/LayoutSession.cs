using Avalonia.Controls;
using Avalonia.Input;
using Carvera.App.Layout;
using Carvera.App.Services;
using Carvera.Core.Commands;
using Carvera.Layout;

namespace Carvera.App.Shell;

/// <summary>A layout that has been built into controls, with its subscriptions and shortcuts.</summary>
public sealed class LayoutSession : IDisposable
{
    private readonly List<(KeyGesture Gesture, string Command, CommandArgs Args)> _shortcuts = [];

    public LayoutSession(LayoutDocument document, AppServices services)
    {
        Document = document;
        Context = new BuildContext(document, services);
        Builder = new LayoutBuilder(Context);
        Root = Builder.Build();
        foreach (var s in document.Shortcuts)
        {
            try
            {
                var args = s.Args is null ? CommandArgs.Empty : CommandArgs.FromJson(System.Text.Json.JsonSerializer.SerializeToElement(s.Args));
                _shortcuts.Add((KeyGesture.Parse(s.Key), s.Command, args));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                services.Console.Warning($"Layout {s.Path}: '{s.Key}' is not a valid key ({ex.Message}).");
            }
        }
    }

    public LayoutDocument Document { get; }
    public BuildContext Context { get; }
    public LayoutBuilder Builder { get; }
    public Control Root { get; }
    public IReadOnlyList<ComponentHost> Hosts => Builder.Hosts;

    public (string Command, CommandArgs Args)? MatchShortcut(KeyEventArgs e, bool typing)
    {
        foreach (var (gesture, command, args) in _shortcuts)
        {
            if (!gesture.Matches(e)) continue;
            if (IsReserved(gesture)) continue;
            var hasModifier = (gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
            var functionKey = gesture.Key is >= Key.F1 and <= Key.F24 or Key.Escape or Key.Pause;
            if (typing && !hasModifier && !functionKey) continue;
            return (command, args);
        }
        return null;
    }

    /// <summary>F5 (reload) and Ctrl+L (switch layout) are handled by the window for every layout.</summary>
    public static bool IsReserved(KeyGesture g) =>
        g.Key == Key.F5 && g.KeyModifiers == KeyModifiers.None || g.Key == Key.L && g.KeyModifiers == KeyModifiers.Control;

    public ComponentHost? FindById(string id) => Hosts.FirstOrDefault(h => string.Equals(h.Node.Id, id, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => Context.Dispose();
}
