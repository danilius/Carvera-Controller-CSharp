using Carvera.Core.Transfer;

namespace Carvera.Core.Config;

/// <summary>What a configuration backup did: the files copied to the computer, and those that could not be.</summary>
public sealed record BackupResult(IReadOnlyList<string> Saved, IReadOnlyList<string> Failed, bool Cancelled = false);

/// <summary>
/// Copies the machine's configuration files from the SD card to a folder on the computer, as the Python controller's
/// "Back up configuration files" does: config.txt, the saved defaults, custom tool slots and the compensation grids.
/// </summary>
public static class ConfigBackup
{
    public static readonly string[] Files =
    [
        "/sd/cartesian_nm.grid",
        "/sd/config.default",
        "/sd/config.txt",
        "/sd/custom_tool_slots.txt",
        "/sd/flex_compensation.dat",
    ];

    /// <summary>The files of <see cref="Files"/> that appear in the listing of the SD card's root folder.</summary>
    public static IReadOnlyList<RemoteEntry> Present(IEnumerable<RemoteEntry> listing) =>
        [.. listing.Where(e => !e.IsDirectory && Files.Contains(e.Path, StringComparer.Ordinal))];

    /// <summary>
    /// Lists the SD card and downloads each configuration file into <paramref name="folder"/>. The pause between files
    /// keeps the machine responsive (the Python controller waits 1.5 s).
    /// </summary>
    public static async Task<BackupResult> RunAsync(CarveraController controller, string folder, TimeSpan pause, CancellationToken cancellationToken)
    {
        var listing = await new RemoteFiles(controller).ListAsync(RemoteFiles.Root, cancellationToken).ConfigureAwait(false);
        var wanted = Present(listing);
        if (wanted.Count == 0) throw new RemoteFileException("None of the configuration files were found on the SD card.");
        Directory.CreateDirectory(folder);
        var saved = new List<string>();
        var failed = new List<string>();
        for (var i = 0; i < wanted.Count; i++)
        {
            var entry = wanted[i];
            var result = await FileDownloader.DownloadAsync(controller, entry.Path, Path.Combine(folder, entry.Name), entry.Size, cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case DownloadResult.Success: saved.Add(entry.Name); break;
                case DownloadResult.Cancelled: return new BackupResult(saved, failed, Cancelled: true);
                default: failed.Add(entry.Name); break;
            }
            if (i < wanted.Count - 1) await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        }
        return new BackupResult(saved, failed);
    }
}
