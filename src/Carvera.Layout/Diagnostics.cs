namespace Carvera.Layout;

public enum DiagnosticSeverity { Info, Warning, Error }

/// <summary>A problem found in a layout file. <see cref="Path"/> is a JSON path such as root.children[2].width.</summary>
public sealed record LayoutDiagnostic(DiagnosticSeverity Severity, string Path, string Message)
{
    public override string ToString() => $"{Severity}: {Path}: {Message}";
}

public sealed class DiagnosticBag
{
    private readonly List<LayoutDiagnostic> _items = [];
    public IReadOnlyList<LayoutDiagnostic> Items => _items;
    public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);
    public void Error(string path, string message) => _items.Add(new(DiagnosticSeverity.Error, path, message));
    public void Warning(string path, string message) => _items.Add(new(DiagnosticSeverity.Warning, path, message));
    public void Info(string path, string message) => _items.Add(new(DiagnosticSeverity.Info, path, message));
}
