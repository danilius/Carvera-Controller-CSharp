using System.Security.Cryptography;
using Carvera.Core.Protocol;
using Carvera.Core.State;

namespace Carvera.Core.Transfer;

public enum UploadResult { Success, Failed, Cancelled }

public sealed record UploadOptions(string RemoteDirectory = "/sd/gcodes", bool Compress = false, TimeSpan? DecompressionTimeout = null)
{
    /// <summary>Compression is offered when the machine says it accepts <c>.lz</c> files (<c>ftype = lz</c>).</summary>
    public static bool MachineAcceptsLz(string? fileType) => fileType?.Contains("lz", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Sends a local file to the machine over XMODEM, as the Python controller's <c>doUpload</c> does: MD5 of the
/// original file, an <c>upload &lt;path&gt;</c> command, then the packets. Progress and the outcome are published in
/// the state store under <c>transfer.*</c>. For <c>.lz</c> uploads it then waits for the machine to unpack the file.
/// </summary>
public static class FileUploader
{
    public static string RemotePathFor(string localPath, UploadOptions options)
    {
        var name = Path.GetFileName(localPath);
        return options.RemoteDirectory.TrimEnd('/', '\\').Replace('\\', '/') + "/" + name;
    }

    private static string Md5Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    public static async Task<UploadResult> UploadAsync(CarveraController controller, string localPath, UploadOptions options, CancellationToken cancellationToken)
    {
        var state = controller.State;
        var console = controller.Console;
        var name = Path.GetFileName(localPath);
        if (!controller.IsConnected) throw new InvalidOperationException("Not connected to a machine.");
        if (!File.Exists(localPath)) throw new FileNotFoundException($"'{localPath}' does not exist.", localPath);

        void Publish(string phase, double percent, bool active = true)
        {
            using var _ = state.BeginBatch();
            state.Set(StatePaths.TransferActive, active);
            state.Set(StatePaths.TransferPhase, phase);
            state.Set(StatePaths.TransferPercent, Math.Round(percent, 1));
        }

        state.Set(StatePaths.TransferName, name);
        state.Set(StatePaths.TransferMessage, null);
        Publish("Preparing", 0);

        string? temporary = null;
        try
        {
            var md5 = Md5Of(localPath);
            var sendPath = localPath;
            var remotePath = RemotePathFor(localPath, options);
            var blocks = 0;
            if (options.Compress)
            {
                temporary = Path.Combine(Path.GetTempPath(), "carvera-upload-" + Guid.NewGuid().ToString("N") + ".lz");
                await using (var input = File.OpenRead(localPath))
                await using (var output = File.Create(temporary))
                    blocks = await LzFile.WriteAsync(input, output, cancellationToken).ConfigureAwait(false);
                sendPath = temporary;
                remotePath += ".lz";
            }

            var total = new FileInfo(sendPath).Length;
            var packets = Math.Max(1, (int)((total + XmodemSender.PacketSize - 1) / XmodemSender.PacketSize));
            console.Info($"Uploading {name} to {remotePath} ({total:N0} bytes{(options.Compress ? ", .lz" : "")}).");
            state.Set(StatePaths.TransferDecompressed, 0);

            TransferOutcome outcome;
            await using (var data = File.OpenRead(sendPath))
            {
                outcome = await controller.RunExclusiveAsync(async link =>
                {
                    Publish("Uploading", 0);
                    await link.WriteAsync(System.Text.Encoding.UTF8.GetBytes(MachineCommands.Upload(remotePath)), cancellationToken).ConfigureAwait(false);
                    return await XmodemSender.SendAsync(data, md5, link,
                        (done, _) => Publish("Uploading", Math.Min(100.0, done * 100.0 / packets)), cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            switch (outcome)
            {
                case TransferOutcome.Cancelled:
                    Finish(state, "Upload cancelled.");
                    console.Warning("Upload cancelled.");
                    return UploadResult.Cancelled;
                case TransferOutcome.Failed:
                    Finish(state, "Upload failed.");
                    console.Error($"Uploading {name} failed. Check the connection and try again.");
                    return UploadResult.Failed;
            }

            if (options.Compress && blocks > 0)
            {
                if (!await WaitForDecompressionAsync(controller, blocks, options.DecompressionTimeout ?? TimeSpan.FromSeconds(30), Publish, cancellationToken).ConfigureAwait(false))
                    console.Warning($"The machine did not report finishing unpacking {name}; check the file on the machine.");
            }
            Finish(state, $"Uploaded {name}.");
            console.Info($"Uploaded {name} to {remotePath}.");
            return UploadResult.Success;
        }
        catch (OperationCanceledException)
        {
            Finish(state, "Upload cancelled.");
            return UploadResult.Cancelled;
        }
        catch (Exception ex)
        {
            Finish(state, $"Upload failed: {ex.Message}");
            console.Error($"Uploading {name} failed: {ex.Message}");
            return UploadResult.Failed;
        }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } }
        }
    }

    private static void Finish(StateStore state, string message)
    {
        using var _ = state.BeginBatch();
        state.Set(StatePaths.TransferActive, false);
        state.Set(StatePaths.TransferPhase, "Idle");
        state.Set(StatePaths.TransferMessage, message);
    }

    /// <summary>Waits until the machine reports all <paramref name="blocks"/> unpacked; gives up when its progress stalls for <paramref name="stall"/>.</summary>
    private static async Task<bool> WaitForDecompressionAsync(CarveraController controller, int blocks, TimeSpan stall, Action<string, double, bool> publish, CancellationToken cancellationToken)
    {
        var last = -1;
        var lastChange = DateTime.UtcNow;
        while (true)
        {
            var done = controller.State.Get(StatePaths.TransferDecompressed, 0);
            if (done != last)
            {
                last = done;
                lastChange = DateTime.UtcNow;
                publish("Decompressing", Math.Min(100.0, done * 100.0 / blocks), true);
            }
            if (done >= blocks) return true;
            if (DateTime.UtcNow - lastChange > stall) return false;
            if (!controller.IsConnected) return false;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }
}
