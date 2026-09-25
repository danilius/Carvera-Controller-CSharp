using System.Text.Json.Nodes;
using Avalonia.Controls;
using Carvera.App.Services;
using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Expressions;
using Carvera.Core.State;
using Carvera.Layout;

namespace Carvera.App.Layout;

/// <summary>Everything components need while a layout is being built and while it is live.</summary>
public sealed class BuildContext : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly Dictionary<string, VisualBlock> _styles;

    public BuildContext(LayoutDocument document, AppServices services)
    {
        Document = document;
        Services = services;
        Theme = new Theme(document.Theme);
        Images = new ImageLoader(document.BaseDirectory, services.Console);
        _styles = document.Styles.ToDictionary(p => p.Key, p => VisualBlock.Parse(p.Value), StringComparer.OrdinalIgnoreCase);
    }

    public LayoutDocument Document { get; }
    public AppServices Services { get; }
    public Theme Theme { get; }
    public ImageLoader Images { get; }
    public StateStore State => Services.State;
    public StateBinder Binder => Services.Binder;
    public CommandRegistry Commands => Services.Commands;
    public ConsoleLog Console => Services.Console;

    public object? Resolve(string path) => State.Get(path);

    public void Track(IDisposable disposable) => _disposables.Add(disposable);

    public void Watch(IEnumerable<string> paths, Action onChange)
    {
        var list = paths.ToArray();
        if (list.Length > 0) Track(Binder.Subscribe(list, onChange));
    }

    public VisualBlock ClassStyle(LayoutNode node)
    {
        var result = VisualBlock.Empty;
        foreach (var name in node.GetStringList("class"))
            if (_styles.TryGetValue(name, out var style)) result = result.Merge(style);
        return result;
    }

    /// <summary>Parses an expression property. Plain booleans become constant expressions.</summary>
    public Expression? ExpressionProp(LayoutNode node, string name)
    {
        var value = node.Get(name);
        if (value is JsonValue v && v.TryGetValue<bool>(out var b)) return Expression.Parse(b ? "true" : "false");
        var text = value is JsonValue s && s.TryGetValue<string>(out var t) ? t : null;
        if (text is null) return null;
        return Expression.TryParse(text, out var expr, out _) ? expr : null;
    }

    public TextTemplate? TemplateProp(LayoutNode node, string name) => TemplateOf(node.GetString(name));

    public static TextTemplate? TemplateOf(string? text) =>
        text is not null && TextTemplate.TryParse(text, out var template, out _) ? template : null;

    public CommandArgs Args(LayoutNode node, string name = "args") => CommandArgs.FromJson(node.Get(name) is JsonObject o ? System.Text.Json.JsonSerializer.SerializeToElement(o) : null);

    /// <summary>Runs a command without blocking the UI; failures are reported to the console.</summary>
    public void Run(string command, CommandArgs args) => _ = Commands.ExecuteAsync(command, args);

    public Window? Owner => Services.MainWindow;

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        _disposables.Clear();
    }
}
