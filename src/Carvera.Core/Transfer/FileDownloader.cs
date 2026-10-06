using Carvera.Core.Protocol;
using Carvera.Core.State;

namespace Carvera.Core.Transfer;

public enum DownloadResult { Success, Failed, Cancelled }

/// <summary>Only one file transfer runs at a time. This holds the cancellation for the one in progress so a second press of the button can cancel it.</summary>
public sealed class TransferGate
{
    private readonly object _lock = new();
    private CancellationTokenSource? _current;

    public bool Active { get { lock (_lock) return _current is not null; } }

    /// <summary>Starts a transfer, or returns null when one is already running.</summary>
    public CancellationTokenSource? TryBegin()
    {
        lock (_lock)
        {
            if (_current is not null) return null;
            return _current = new CancellationTokenSource();
        }
    }

    public void End(CancellationTokenSource source)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_current, source)) _current = null;
        }
        source.Dispose();
    }

    /// <summary>Cancels the transfer in progress. Returns false when there is none.</summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (_current is null) return false;
            _current.Cancel();
            return true;
        }
    }
}

/// <summary>
/// Fetches a file from the machine over XMODEM, as the Python controller's <c>doDownload</c> does. The file is written to a
/// temporary name and moved into place only when the MD5 checks out, so a failed download never leaves a broken file behind.
/// </summary>
public static class FileDownloader
{
    public static async Task<DownloadResult> DownloadAsync(CarveraController controller, string remotePath, string localPath, long expectedSize, CancellationToken cancellationToken)
    {
        var state = controller.State;
        var console = controller.Console;
        var name = RemoteFiles.Normalize(remotePath).Split('/').Last();
        if (!controller.IsConnected) throw new InvalidOperationException("Not connected to a machine.");

        void Publish(string phase, double percent, bool active = true)
        {
            using var _ = state.BeginBatch();
            state.Set(StatePaths.TransferActive, active);
            state.Set(StatePaths.TransferPhase, phase);
            state.Set(StatePaths.TransferPercent, Math.Round(percent, 1));
        }
        void Finish(string message)
        {
            using var _ = state.BeginBatch();
            state.Set(StatePaths.TransferActive, false);
            state.Set(StatePaths.TransferPhase, "Idle");
            state.Set(StatePaths.TransferMessage, message);
        }

        state.Set(StatePaths.TransferName, name);
        state.Set(StatePaths.TransferMessage, null);
        Publish("Downloading", 0);
        var temporary = localPath + ".part";
        try
        {
            console.Info($"Downloading {remotePath} to {localPath}.");
            ReceiveResult result;
            await using (var output = File.Create(temporary))
            {
                result = await controller.RunExclusiveAsync(async link =>
                {
                    await link.WriteAsync(System.Text.Encoding.UTF8.GetBytes(MachineCommands.Download(RemoteFiles.Normalize(remotePath))), cancellationToken).ConfigureAwait(false);
                    return await XmodemReceiver.ReceiveAsync(output, link,
                        bytes => Publish("Downloading", expectedSize > 0 ? Math.Min(100.0, bytes * 100.0 / expectedSize) : 0), cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            switch (result.Outcome)
            {
                case TransferOutcome.Cancelled:
                    Finish("Download cancelled.");
                    console.Warning("Download cancelled.");
                    return DownloadResult.Cancelled;
                case TransferOutcome.Failed:
                    var why = result.Md5Mismatch ? "the file did not match the MD5 the machine gave, so it was discarded" : $"the transfer failed after {result.Bytes:N0} bytes";
                    Finish($"Download failed: {why}.");
                    console.Error($"Downloading {name} failed: {why}.");
                    return DownloadResult.Failed;
            }

            var unpacked = false;
            if (result.LooksCompressed)
            {
                try
                {
                    File.WriteAllBytes(temporary, LzFile.Read(File.ReadAllBytes(temporary)));
                    unpacked = true;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    console.Warning($"{name} arrived QuickLZ-compressed but could not be unpacked ({ex.Message}); it was saved as it came.");
                }
            }
            File.Move(temporary, localPath, overwrite: true);
            if (unpacked) console.Info($"{name} arrived compressed and was unpacked.");
            Finish($"Downloaded {name}.");
            console.Info($"Downloaded {name} ({result.Bytes:N0} bytes) to {localPath}.");
            return DownloadResult.Success;
        }
        catch (OperationCanceledException)
        {
            Finish("Download cancelled.");
            return DownloadResult.Cancelled;
        }
        catch (Exception ex)
        {
            Finish($"Download failed: {ex.Message}");
            console.Error($"Downloading {name} failed: {ex.Message}");
            return DownloadResult.Failed;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }
}
