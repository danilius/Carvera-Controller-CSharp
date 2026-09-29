using System.Globalization;
using Carvera.Core.Protocol;

namespace Carvera.Core.Transfer;

public sealed record RemoteEntry(string Name, string Path, bool IsDirectory, long Size, DateTime? Modified);

public sealed class RemoteFileException(string message) : Exception(message);

/// <summary>
/// The machine's file system as seen through its text commands (<c>ls</c>, <c>rm</c>, <c>mkdir</c>, <c>mv</c>), ported from the
/// Python controller's file popup. Paths use forward slashes and start at <see cref="Root"/> (<c>/sd</c>, the SD card).
/// </summary>
public sealed class RemoteFiles(CarveraController controller)
{
    public const string Root = "/sd";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>Joins path parts with single forward slashes; backslashes become slashes.</summary>
    public static string Combine(string directory, string name) =>
        Normalize(directory).TrimEnd('/') + "/" + name.Trim('/', '\\');

    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>The folder containing <paramref name="path"/>, never above <see cref="Root"/>.</summary>
    public static string Parent(string path)
    {
        var normal = Normalize(path);
        var cut = normal.LastIndexOf('/');
        var parent = cut <= 0 ? "/" : normal[..cut];
        return parent.Length < Root.Length ? Root : parent;
    }

    /// <summary>
    /// Parses <c>ls -e -s</c> output: one line per entry, "name size timestamp", spaces in names sent as 0x01,
    /// folders with a trailing slash and the timestamp as yyyyMMddHHmmss. Hidden entries and other lines are skipped.
    /// Folders come first, then files, each sorted by name.
    /// </summary>
    public static IReadOnlyList<RemoteEntry> ParseListing(string directory, IEnumerable<string> lines)
    {
        var entries = new List<RemoteEntry>();
        foreach (var raw in lines)
        {
            var line = raw.Trim('\r', '\n');
            if (line.Length == 0 || line[0] == '<') continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0].StartsWith('.') || !long.TryParse(parts[1], out var size) || !parts[2].All(char.IsDigit)) continue;
            var name = parts[0].Replace('\x01', ' ');
            var isDirectory = name.EndsWith('/');
            if (isDirectory) name = name[..^1];
            DateTime? modified = DateTime.TryParseExact(parts[2], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
            entries.Add(new RemoteEntry(name, Combine(directory, name), isDirectory, size, modified));
        }
        return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        var path = Normalize(directory);
        var reply = await controller.CaptureAsync(MachineCommands.ListDirectory(path), Timeout, cancellationToken).ConfigureAwait(false);
        if (reply.TimedOut || !reply.Succeeded)
        {
            // Say what did arrive: it tells a busy machine (no reply at all) from one that refused (an error line, or CAN).
            var heard = reply.Lines.Count == 0 ? "nothing" : $"{reply.Lines.Count} line{(reply.Lines.Count == 1 ? "" : "s")}, the first “{Truncate(reply.Lines[0])}”";
            var message = reply.TimedOut
                ? $"The machine did not finish answering while listing {path} (it sent {heard}). It may be busy with a job."
                : $"The machine could not list {path} (it sent {heard}).";
            controller.Console.Warning(message);
            throw new RemoteFileException(message);
        }
        return ParseListing(path, reply.Lines);
    }

    private static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private async Task RunAsync(string command, string failure, CancellationToken cancellationToken)
    {
        var reply = await controller.CaptureAsync(command, Timeout, cancellationToken).ConfigureAwait(false);
        if (reply.TimedOut) throw new RemoteFileException($"{failure}: the machine did not answer.");
        if (!reply.Succeeded) throw new RemoteFileException(failure + ".");
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        RunAsync(MachineCommands.Remove(Normalize(path)), $"Could not delete {Normalize(path)}", cancellationToken);

    public Task MakeDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        RunAsync(MachineCommands.MakeDirectory(Normalize(path)), $"Could not create the folder {Normalize(path)}", cancellationToken);

    public Task RenameAsync(string from, string to, CancellationToken cancellationToken = default) =>
        RunAsync(MachineCommands.Move(Normalize(from), Normalize(to)), $"Could not rename {Normalize(from)}", cancellationToken);
}
