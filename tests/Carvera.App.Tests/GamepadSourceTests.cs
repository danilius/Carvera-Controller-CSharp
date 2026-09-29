using Carvera.App.Services;
using Carvera.Core;
using Xunit;

namespace Carvera.App.Tests;

public class GamepadSourceTests
{
    [Fact]
    public void TheSdlSourceStartsAndStopsCleanlyWithoutAnyPadPlugged()
    {
        var console = new ConsoleLog();
        var source = new SdlGamepadSource(console);
        source.Start();
        Thread.Sleep(1500); // long enough for two device scans
        source.Dispose();
        Assert.DoesNotContain(console.Entries, e => e.Kind is ConsoleEntryKind.Error);
        Assert.DoesNotContain(console.Entries, e => e.Text.Contains("unavailable")); // the native library loaded
    }
}
