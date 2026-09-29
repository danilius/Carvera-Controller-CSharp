using System.Security.Cryptography;
using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Simulation;
using Carvera.Core.State;
using Carvera.Core.Transfer;
using Xunit;

namespace Carvera.Tests;

public class RemoteDownloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "carvera-dl-tests", Guid.NewGuid().ToString("N"));

    public RemoteDownloadTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private sealed class Host : IAppHost
    {
        public string? SavePath, Opened;
        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult(SavePath);
        public Task OpenFileAsync(string? path) { Opened = path; return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string message) => Task.FromResult(true);
        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(initial);
        public Task UploadFileAsync(string? path, string? remoteDirectory) => Task.CompletedTask;
        public Task CloseFileAsync() => Task.CompletedTask;
        public Task LoadLayoutAsync(string name) => Task.CompletedTask;
        public Task ReloadLayoutAsync() => Task.CompletedTask;
        public Task ExitAsync() => Task.CompletedTask;
        public Task SaveFileAsync(string? path) => Task.CompletedTask;
        public Task SetOperationToolAsync(int operation, int tool) => Task.CompletedTask;
        public Task StepPreviewAsync(int? delta) => Task.CompletedTask;
        public Task SelectOperationAsync(int operation) => Task.CompletedTask;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required CarveraController Controller { get; init; }
        public required SimulatedMachine Machine { get; init; }
        public required RemoteBrowser Browser { get; init; }
        public required CommandRegistry Commands { get; init; }
        public required Host Host { get; init; }
        public StateStore State => Controller.State;

        public async ValueTask DisposeAsync()
        {
            Browser.Dispose();
            await Controller.DisposeAsync();
        }
    }

    private static async Task<Rig> Start()
    {
        var machine = new SimulatedMachine();
        var controller = new CarveraController(streamFactory: _ => machine) { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        var host = new Host();
        var commands = new CommandRegistry(new CommandContext(controller));
        StandardCommands.Register(commands);
        var browser = new RemoteBrowser(controller, "/sd/gcodes");
        RemoteCommands.Register(commands, browser, new TransferGate(), host);
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Idle"));
        return new Rig { Controller = controller, Machine = machine, Browser = browser, Commands = commands, Host = host };
    }

    private string Target(string name = "download.nc") => Path.Combine(_folder, name);

    [Fact]
    public async Task DownloadsAFileAndChecksItsMd5()
    {
        await using var rig = await Start();
        var content = new byte[30_000];
        new Random(5).NextBytes(content);
        var source = Path.Combine(_folder, "source.bin");
        await File.WriteAllBytesAsync(source, content);
        await FileUploader.UploadAsync(rig.Controller, source, new UploadOptions("/sd/gcodes"), default); // put it on the card like a user would

        var target = Target();
        var result = await FileDownloader.DownloadAsync(rig.Controller, "/sd/gcodes/source.bin", target, content.Length, default);

        Assert.Equal(DownloadResult.Success, result);
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Equal("Downloaded source.bin.", rig.State.Get<string>(StatePaths.TransferMessage));
        Assert.False(rig.State.Get<bool>(StatePaths.TransferActive));
        Assert.False(rig.Controller.IsExclusive);
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchTheAdvertisedMd5IsDiscarded()
    {
        await using var rig = await Start();
        rig.Machine.AdvertisedMd5Override = new string('a', 32);
        var target = Target();
        var result = await FileDownloader.DownloadAsync(rig.Controller, "/sd/gcodes/demo part.nc", target, 24, default);
        Assert.Equal(DownloadResult.Failed, result);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Contains(rig.Controller.Console.Entries, e => e.Kind == ConsoleEntryKind.Error && e.Text.Contains("MD5"));
    }

    [Fact]
    public async Task APlaceholderDigestSkipsTheCheck()
    {
        await using var rig = await Start();
        rig.Machine.AdvertisedMd5Override = "default_md5_hash_value_32_bytes_"; // what stock Z1 firmware sends; it is not a digest
        var target = Target();
        Assert.Equal(DownloadResult.Success, await FileDownloader.DownloadAsync(rig.Controller, "/sd/gcodes/demo part.nc", target, 24, default));
        Assert.Equal("G21 G90\nG1 X10 Y10 F800\n", await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789ABCDEF", "0123456789abcdef0123456789abcdef")]
    [InlineData("default_md5_hash_value_32_bytes_", null)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void OnlyRealHexDigestsCount(string? advertised, string? expected) => Assert.Equal(expected, XmodemReceiver.NormalizeMd5(advertised));

    private sealed class SilentLink : IByteLink
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<int> ReadByteAsync(TimeSpan timeout, CancellationToken cancellationToken) => ValueTask.FromResult(-1);
        public ValueTask DrainAsync(TimeSpan quiet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ASenderThatNeverAnswersMakesTheReceiverGiveUp()
    {
        var result = await XmodemReceiver.ReceiveAsync(Stream.Null, new SilentLink(), null, default, retry: 2, timeout: TimeSpan.FromMilliseconds(5));
        Assert.Equal(TransferOutcome.Failed, result.Outcome);
    }

    /// <summary>A sender that corrupts the CRC of its first packet, then behaves.</summary>
    private sealed class FlakySenderLink(byte[] content) : IByteLink
    {
        private readonly Queue<byte> _incoming = new();
        private int _packet = -1;
        private bool _corrupted, _done, _started;
        private byte[] _last = [];
        public int Naks;

        private byte[] Build(int index)
        {
            var payload = index < 0
                ? System.Text.Encoding.ASCII.GetBytes(Convert.ToHexStringLower(MD5.HashData(content)))
                : content.AsSpan(index * 8192, Math.Min(8192, content.Length - index * 8192)).ToArray();
            var packet = XmodemSender.BuildPacket(payload, (byte)(index + 1), true);
            if (index < 0 && !_corrupted)
            {
                _corrupted = true;
                packet[^1] ^= 0xFF;
            }
            return packet;
        }

        private void Send(byte[] bytes)
        {
            foreach (var b in bytes) _incoming.Enqueue(b);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            foreach (var b in data.Span)
            {
                if (!_started && b == XmodemSender.CrcRequest) { _started = true; Send(_last = Build(-1)); continue; }
                if (b == XmodemSender.Nak) { Naks++; Send(_last = _last.Length == 1 ? _last : Build(_packet)); continue; } // a refused packet is rebuilt, this time correctly
                if (b != XmodemSender.Ack || _done) continue;
                _packet++;
                if (_packet * 8192 >= content.Length) { _done = true; Send(_last = [XmodemSender.Eot]); }
                else Send(_last = Build(_packet));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReadByteAsync(TimeSpan timeout, CancellationToken cancellationToken) => ValueTask.FromResult(_incoming.TryDequeue(out var b) ? b : -1);

        public ValueTask DrainAsync(TimeSpan quiet, CancellationToken cancellationToken)
        {
            _incoming.Clear();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task TheReceiverAsksAgainForAPacketWithABadCrc()
    {
        var content = new byte[20_000];
        new Random(9).NextBytes(content);
        var link = new FlakySenderLink(content);
        using var output = new MemoryStream();
        var result = await XmodemReceiver.ReceiveAsync(output, link, null, default, timeout: TimeSpan.FromMilliseconds(5));
        Assert.Equal(TransferOutcome.Success, result.Outcome);
        Assert.Equal(content, output.ToArray());
        Assert.True(link.Naks >= 1);
    }

    [Fact]
    public async Task DownloadCommandSavesToTheChosenPathAndViewOpensATemporaryCopy()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 3));
        await rig.Commands.ExecuteAsync("remoteSelect", CommandArgs.Empty.With("path", "/sd/gcodes/demo part.nc"));
        var target = Target("saved.nc");
        rig.Host.SavePath = target;
        Assert.True(await rig.Commands.ExecuteAsync("remoteDownload"));
        Assert.Equal("G21 G90\nG1 X10 Y10 F800\n", await File.ReadAllTextAsync(target));

        Assert.True(await rig.Commands.ExecuteAsync("remoteView"));
        Assert.NotNull(rig.Host.Opened);
        Assert.EndsWith("demo part.nc", rig.Host.Opened);
        Assert.Equal("G21 G90\nG1 X10 Y10 F800\n", await File.ReadAllTextAsync(rig.Host.Opened!));

        // Cancelling the file dialog downloads nothing.
        rig.Host.SavePath = null;
        var before = rig.Controller.Console.Entries.Count(e => e.Text.StartsWith("Downloading"));
        await rig.Commands.ExecuteAsync("remoteDownload");
        Assert.Equal(before, rig.Controller.Console.Entries.Count(e => e.Text.StartsWith("Downloading")));
    }

    [Fact]
    public async Task DownloadNeedsAFileNotAFolder()
    {
        await using var rig = await Start();
        Assert.True(await WaitFor(() => rig.State.Get(StatePaths.RemoteCount, 0) == 3));
        Assert.False(rig.Commands.CanExecute("remoteDownload", CommandArgs.Empty)); // nothing selected
        await rig.Commands.ExecuteAsync("remoteSelect", CommandArgs.Empty.With("path", "/sd/gcodes/old jobs"));
        Assert.False(rig.Commands.CanExecute("remoteDownload", CommandArgs.Empty)); // a folder
        await rig.Commands.ExecuteAsync("remoteSelect", CommandArgs.Empty.With("path", "/sd/gcodes/notes.txt"));
        Assert.True(rig.Commands.CanExecute("remoteDownload", CommandArgs.Empty));
    }

    [Fact]
    public async Task ACancelledDownloadLeavesNothingBehind()
    {
        await using var rig = await Start();
        var content = new byte[3_000_000];
        new Random(2).NextBytes(content);
        var source = Path.Combine(_folder, "big.bin");
        await File.WriteAllBytesAsync(source, content);
        await FileUploader.UploadAsync(rig.Controller, source, new UploadOptions("/sd/gcodes"), default);
        using var cts = new CancellationTokenSource();
        rig.State.Changed += paths =>
        {
            if (paths.Contains(StatePaths.TransferPercent) && rig.State.Get<string>(StatePaths.TransferPhase) == "Downloading" && rig.State.Get<double>(StatePaths.TransferPercent) > 5) cts.Cancel();
        };
        var target = Target("big-copy.bin");
        var outcome = await FileDownloader.DownloadAsync(rig.Controller, "/sd/gcodes/big.bin", target, content.Length, cts.Token);
        Assert.True(outcome == DownloadResult.Cancelled, string.Join(" | ", rig.Controller.Console.Entries.Select(e => e.Text)));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.False(rig.Controller.IsExclusive);
        // The machine is still usable afterwards.
        await rig.Controller.SendLineAsync("M821");
        Assert.True(await WaitFor(() => rig.State.Get<bool>("switch.light")));
    }
}
