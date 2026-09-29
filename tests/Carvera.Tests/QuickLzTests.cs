using Carvera.Core.Transfer;
using Xunit;

namespace Carvera.Tests;

public class QuickLzTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "quicklz", name);

    public static IEnumerable<object[]> Samples() => [["gcode"], ["short"], ["runs"], ["random"]];

    /// <summary>The .qlz files were made by pyquicklz, the reference the Python controller uses.</summary>
    [Theory, MemberData(nameof(Samples))]
    public void UnpacksBlocksMadeByPyQuickLz(string name)
    {
        var raw = File.ReadAllBytes(Fixture(name + ".raw"));
        Assert.Equal(raw, QuickLz.Decompress(File.ReadAllBytes(Fixture(name + ".qlz"))));
    }

    [Theory, MemberData(nameof(Samples))]
    public void RoundTripsAndCompressesRepetitiveData(string name)
    {
        var raw = File.ReadAllBytes(Fixture(name + ".raw"));
        var packed = QuickLz.Compress(raw);
        Assert.Equal(raw, QuickLz.Decompress(packed));
        if (name is "gcode" or "runs") Assert.True(packed.Length < raw.Length / 2, $"{name}: {packed.Length} of {raw.Length}");
        // Set QUICKLZ_EXPORT_DIR to let pyquicklz check our output (see docs/uploading.md).
        if (Environment.GetEnvironmentVariable("QUICKLZ_EXPORT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".mine"), packed);
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(215)] [InlineData(216)] [InlineData(4096)]
    public void RoundTripsAssortedSizesAndPatterns(int size)
    {
        foreach (var pattern in new[] { 0, 1, 2 })
        {
            var data = new byte[size];
            var random = new Random(size * 3 + pattern);
            for (var i = 0; i < size; i++)
                data[i] = pattern switch { 0 => (byte)random.Next(256), 1 => (byte)(i % 7), _ => (byte)("G1 X12.5 Y3\n"[i % 12] + (i / 300)) };
            Assert.Equal(data, QuickLz.Decompress(QuickLz.Compress(data)));
        }
    }

    [Fact]
    public void RejectsDamagedBlocks()
    {
        var packed = QuickLz.Compress(File.ReadAllBytes(Fixture("gcode.raw")));
        Assert.Throws<InvalidDataException>(() => QuickLz.Decompress(packed.AsSpan(0, packed.Length / 2)));
        Assert.Throws<InvalidDataException>(() => QuickLz.Decompress(new byte[] { 0x4f, 18, 0, 0, 0, 100, 0, 0, 0, 0x01, 0x00, 0x00, 0x80, 0x04, 0, 0, 0 }));
    }

    [Fact]
    public async Task LzFilesAreSmallerForGcodeAndReadBack()
    {
        var raw = File.ReadAllBytes(Fixture("gcode.raw"));
        var big = raw.Concat(raw).Concat(raw).ToArray();
        using var output = new MemoryStream();
        await LzFile.WriteAsync(new MemoryStream(big), output, TestContext.Current.CancellationToken);
        Assert.True(output.Length < big.Length / 2);
        Assert.Equal(big, LzFile.Read(output.ToArray()));
    }
}

public class BackupAndCompressedDownloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "carvera-backup-tests", Guid.NewGuid().ToString("N"));

    public BackupAndCompressedDownloadTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private static async Task<(Carvera.Core.CarveraController Controller, Carvera.Core.Simulation.SimulatedMachine Machine)> Start()
    {
        var machine = new Carvera.Core.Simulation.SimulatedMachine();
        var controller = new Carvera.Core.CarveraController(streamFactory: _ => machine) { StatusInterval = TimeSpan.FromMilliseconds(30), DiagnoseInterval = TimeSpan.FromMilliseconds(60) };
        await controller.ConnectAsync(new Carvera.Core.Connection.ConnectionOptions(Carvera.Core.Connection.ConnectionKind.Simulator, "simulator"));
        var until = DateTime.UtcNow.AddSeconds(6);
        while (controller.State.Get<string>(Carvera.Core.State.StatePaths.MachineState) != "Idle" && DateTime.UtcNow < until) await Task.Delay(20);
        return (controller, machine);
    }

    [Fact]
    public async Task ADownloadThatArrivesInLzFormatIsUnpacked()
    {
        var (controller, machine) = await Start();
        await using var _ = controller;
        var text = string.Concat(Enumerable.Range(0, 400).Select(i => $"G1 X{i % 50} Y{i % 7} F800\n"));
        var original = System.Text.Encoding.ASCII.GetBytes(text);
        using var packed = new MemoryStream();
        await LzFile.WriteAsync(new MemoryStream(original), packed, TestContext.Current.CancellationToken);
        Assert.True(packed.Length < original.Length / 2);
        machine.PutFile("/sd/gcodes/packed.nc", packed.ToArray());

        var target = Path.Combine(_folder, "packed.nc");
        var result = await FileDownloader.DownloadAsync(controller, "/sd/gcodes/packed.nc", target, packed.Length, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadResult.Success, result);
        Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheConfigurationBackupCopiesTheFilesThatExist()
    {
        var (controller, machine) = await Start();
        await using var _ = controller;
        machine.PutFile("/sd/config.default", "defaults"u8.ToArray());
        machine.PutFile("/sd/custom_tool_slots.txt", "slots"u8.ToArray());
        machine.PutFile("/sd/unrelated.txt", "nope"u8.ToArray());

        var result = await Carvera.Core.Config.ConfigBackup.RunAsync(controller, Path.Combine(_folder, "backup"), TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);

        Assert.Empty(result.Failed);
        Assert.Equal(["config.default", "config.txt", "custom_tool_slots.txt"], result.Saved.Order().ToArray());
        Assert.Equal("defaults", await File.ReadAllTextAsync(Path.Combine(_folder, "backup", "config.default"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_folder, "backup", "unrelated.txt")));
    }
}
