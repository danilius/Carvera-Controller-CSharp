using Carvera.Core;
using Carvera.Core.Connection;
using Carvera.Core.Simulation;
using Carvera.Core.State;
using Carvera.Core.Transfer;
using Xunit;

namespace Carvera.Tests;

public class UploadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "carvera-upload-tests", Guid.NewGuid().ToString("N"));

    public UploadTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private string MakeFile(string name, int size, int seed = 1)
    {
        var path = Path.Combine(_folder, name);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private static async Task<(CarveraController Controller, SimulatedMachine Machine)> Connect(string fileType = "lz")
    {
        var machine = new SimulatedMachine { FileType = fileType };
        var controller = new CarveraController(streamFactory: _ => machine) { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        await controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "simulator"));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineState) == "Idle"));
        Assert.True(await WaitFor(() => controller.State.Get<string>(StatePaths.MachineFileType) == fileType));
        return (controller, machine);
    }

    [Fact]
    public void Crc16MatchesTheXmodemCheckValue() =>
        Assert.Equal(0x31C3, XmodemSender.Crc16("123456789"u8));

    [Theory]
    [InlineData(1)]
    [InlineData(215)]
    [InlineData(216)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(20000)]
    public async Task LzFilesRoundTripAndUseQuickLzBlockHeaders(int size)
    {
        var original = new byte[size];
        new Random(size).NextBytes(original);
        using var output = new MemoryStream();
        var blocks = await LzFile.WriteAsync(new MemoryStream(original), output);
        var file = output.ToArray();

        Assert.Equal((size + LzFile.BlockSize - 1) / LzFile.BlockSize, blocks);
        Assert.Equal(original, LzFile.ReadStored(file));
        // The first block: 4-byte big-endian length, then a stored level-1 QuickLZ block.
        var firstLength = (file[0] << 24) | (file[1] << 16) | (file[2] << 8) | file[3];
        var firstSize = Math.Min(size, LzFile.BlockSize);
        Assert.Equal(firstSize < 216 ? 3 + firstSize : 9 + firstSize, firstLength);
        Assert.Equal(firstSize < 216 ? 0x44 : 0x46, file[4]);
        // Corrupting the data breaks the trailer check.
        file[^3] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => LzFile.ReadStored(file));
    }

    [Fact]
    public void PacketsCarryTheLengthPrefixPaddingAndCrc()
    {
        var packet = XmodemSender.BuildPacket("abc"u8, 5, crcMode: true);
        Assert.Equal(1 + 2 + 2 + XmodemSender.PacketSize + 2, packet.Length);
        Assert.Equal(new byte[] { XmodemSender.Stx, 5, 0xFA }, packet[..3]);
        Assert.Equal(new byte[] { 0, 3, (byte)'a', (byte)'b', (byte)'c', XmodemSender.Pad }, packet[3..9]);
        var crc = XmodemSender.Crc16(packet.AsSpan(3, 2 + XmodemSender.PacketSize));
        Assert.Equal(new[] { (byte)(crc >> 8), (byte)crc }, packet[^2..]);
        var checksum = XmodemSender.BuildPacket("abc"u8, 5, crcMode: false);
        Assert.Equal(packet.Length - 1, checksum.Length);
    }

    [Fact]
    public async Task UploadsARawFileToTheSimulator()
    {
        var (controller, machine) = await Connect();
        await using var _c = controller;
        var path = MakeFile("part.nc", 20000);

        var result = await FileUploader.UploadAsync(controller, path, new UploadOptions("/sd/gcodes", Compress: false), default);

        Assert.Equal(UploadResult.Success, result);
        Assert.Equal(File.ReadAllBytes(path), machine.UploadedFiles["/sd/gcodes/part.nc"]);
        Assert.False(controller.State.Get<bool>(StatePaths.TransferActive));
        Assert.Equal("Uploaded part.nc.", controller.State.Get<string>(StatePaths.TransferMessage));
        Assert.False(controller.IsExclusive);
    }

    [Fact]
    public async Task UploadsACompressedFileAndWaitsForTheMachineToUnpackIt()
    {
        var (controller, machine) = await Connect();
        await using var _c = controller;
        var path = MakeFile("big part.nc", 50000, seed: 7); // a space in the name must survive the command escaping

        var result = await FileUploader.UploadAsync(controller, path, new UploadOptions("/sd/gcodes/sub", Compress: true), default);

        Assert.Equal(UploadResult.Success, result);
        Assert.Equal(File.ReadAllBytes(path), machine.UploadedFiles["/sd/gcodes/sub/big part.nc"]);
        Assert.Equal(13, controller.State.Get<int>(StatePaths.TransferDecompressed)); // 50000 bytes = 13 blocks of 4096
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "carvera-upload-*.lz"));
    }

    [Fact]
    public async Task ReportsProgressWhileUploading()
    {
        var (controller, _) = await Connect();
        await using var _c = controller;
        var path = MakeFile("progress.nc", 200_000);
        var seen = new List<double>();
        controller.State.Changed += paths =>
        {
            if (paths.Contains(StatePaths.TransferPercent)) seen.Add(controller.State.Get<double>(StatePaths.TransferPercent));
        };
        await FileUploader.UploadAsync(controller, path, new UploadOptions(Compress: false), default);
        Assert.Contains(seen, p => p is > 0 and < 100);
        Assert.Equal(seen.Order().ToList(), seen.Where(p => true).ToList()); // never goes backwards
    }

    [Fact]
    public async Task CancellingStopsTheUploadAndTheMachineCarriesOn()
    {
        var (controller, machine) = await Connect();
        await using var _c = controller;
        var path = MakeFile("cancel.nc", 4_000_000);
        using var cts = new CancellationTokenSource();
        controller.State.Changed += paths =>
        {
            if (paths.Contains(StatePaths.TransferPercent) && controller.State.Get<double>(StatePaths.TransferPercent) > 5) cts.Cancel();
        };

        var result = await FileUploader.UploadAsync(controller, path, new UploadOptions(Compress: false), cts.Token);

        Assert.Equal(UploadResult.Cancelled, result);
        Assert.Empty(machine.UploadedFiles);
        Assert.False(controller.State.Get<bool>(StatePaths.TransferActive));
        Assert.False(controller.IsExclusive);
        // Polling resumes: the machine still answers status queries and commands.
        var before = controller.State.Get<double>(StatePaths.AxisWork("x"));
        await controller.SendLineAsync("$J X1");
        Assert.True(await WaitFor(() => Math.Abs(controller.State.Get<double>(StatePaths.AxisWork("x")) - (before + 1)) < 1e-6));
    }

    [Fact]
    public async Task OtherCommandsAreRefusedWhileATransferHoldsTheStream()
    {
        var (controller, _) = await Connect();
        await using var _c = controller;
        var release = new TaskCompletionSource();
        var running = controller.RunExclusiveAsync(async _ =>
        {
            await release.Task;
            return 0;
        });
        Assert.True(await WaitFor(() => controller.IsExclusive));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SendLineAsync("M821"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunExclusiveAsync(_ => Task.FromResult(0)));
        release.SetResult();
        await running;
        await controller.SendLineAsync("M821"); // fine again
        Assert.True(await WaitFor(() => controller.State.Get<bool>("switch.light")));
    }

    [Fact]
    public async Task UploadFailsCleanlyWhenTheMachineDoesNotAnswer()
    {
        var (controller, _) = await Connect();
        await using var _c = controller;
        var path = MakeFile("silent.nc", 100);
        // A link that never answers: the sender gives up after its retries.
        var outcome = await XmodemSender.SendAsync(new MemoryStream(new byte[100]), "0".PadRight(32, '0'), new SilentLink(), null, default, retry: 2, timeout: TimeSpan.FromMilliseconds(10));
        Assert.Equal(TransferOutcome.Failed, outcome);
        Assert.False(controller.IsExclusive);
        _ = path;
    }

    private sealed class SilentLink : IByteLink
    {
        public List<byte> Written { get; } = [];
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Written.AddRange(data.ToArray());
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadByteAsync(TimeSpan timeout, CancellationToken cancellationToken) => ValueTask.FromResult(-1);
        public ValueTask DrainAsync(TimeSpan quiet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>A receiver that asks for CRC mode, refuses the first copy of a packet, then accepts.</summary>
    private sealed class FlakyLink : IByteLink
    {
        private readonly Queue<int> _replies = new([XmodemSender.CrcRequest, XmodemSender.Nak, XmodemSender.Ack, XmodemSender.Ack, XmodemSender.Ack]);
        public int PacketsSeen;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (data.Span[0] == XmodemSender.Stx) PacketsSeen++;
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadByteAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_replies.TryDequeue(out var reply) ? reply : XmodemSender.Ack);
        public ValueTask DrainAsync(TimeSpan quiet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ResendsAPacketTheReceiverRefuses()
    {
        var link = new FlakyLink();
        var outcome = await XmodemSender.SendAsync(new MemoryStream(new byte[100]), new string('0', 32), link, null, default);
        Assert.Equal(TransferOutcome.Success, outcome);
        Assert.Equal(3, link.PacketsSeen); // the md5 packet twice (once refused), then the one data packet
    }

    [Theory]
    [InlineData("/sd/gcodes", "part.nc", "/sd/gcodes/part.nc")]
    [InlineData("/sd/gcodes/", "part.nc", "/sd/gcodes/part.nc")]
    [InlineData("\\sd\\gcodes", "part.nc", "/sd/gcodes/part.nc")]
    public void RemotePathsUseForwardSlashes(string directory, string file, string expected) =>
        Assert.Equal(expected, FileUploader.RemotePathFor(Path.Combine(_folder, file), new UploadOptions(directory)));

    [Theory]
    [InlineData("lz", true)]
    [InlineData("nc", false)]
    [InlineData(null, false)]
    public void CompressionIsOfferedOnlyWhenTheMachineAcceptsLz(string? fileType, bool expected) =>
        Assert.Equal(expected, UploadOptions.MachineAcceptsLz(fileType));
}
