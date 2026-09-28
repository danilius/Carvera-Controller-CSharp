using Carvera.Core.Connection;
using Xunit;

namespace Carvera.Tests;

public class AutoConnectorTests
{
    private static AutoConnector Make(Func<AutoConnectPolicy> policy, Func<int, bool> connectAttempt, List<string> log, List<TimeSpan> waits,
        Func<bool>? hasTarget = null, Func<bool>? busy = null)
    {
        var attempts = 0;
        return new AutoConnector(policy, hasTarget ?? (() => true), busy ?? (() => false),
            _ => Task.FromResult(connectAttempt(++attempts)), log.Add, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
    }

    [Fact]
    public async Task ConnectsOnTheFirstAttemptWithoutWaiting()
    {
        List<string> log = [];
        List<TimeSpan> waits = [];
        var result = await Make(() => new(3, TimeSpan.FromSeconds(5)), _ => true, log, waits).RunAsync(default);
        Assert.Equal(AutoConnectResult.Connected, result);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task RetriesAtTheConfiguredIntervalUntilItConnects()
    {
        List<string> log = [];
        List<TimeSpan> waits = [];
        var result = await Make(() => new(5, TimeSpan.FromSeconds(7)), n => n == 3, log, waits).RunAsync(default);
        Assert.Equal(AutoConnectResult.Connected, result);
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)], waits);
        Assert.Contains(log, l => l.Contains("attempt 1 of 6"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    public async Task GivesUpAfterTheConfiguredNumberOfRetries(int retries, int expectedAttempts)
    {
        List<string> log = [];
        List<TimeSpan> waits = [];
        var attempts = 0;
        var connector = new AutoConnector(() => new(retries, TimeSpan.FromSeconds(2)), () => true, () => false,
            _ => { attempts++; return Task.FromResult(false); }, log.Add, (w, _) => { waits.Add(w); return Task.CompletedTask; });
        Assert.Equal(AutoConnectResult.GaveUp, await connector.RunAsync(default));
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(expectedAttempts - 1, waits.Count);
        Assert.Contains(log, l => l.Contains("gave up"));
    }

    [Fact]
    public async Task ShortIntervalsAreRaisedToOneSecond()
    {
        List<TimeSpan> waits = [];
        await Make(() => new(1, TimeSpan.Zero), _ => false, [], waits).RunAsync(default);
        Assert.Equal([TimeSpan.FromSeconds(1)], waits);
    }

    [Fact]
    public async Task PolicyChangesApplyToTheNextWait()
    {
        List<TimeSpan> waits = [];
        var interval = TimeSpan.FromSeconds(10);
        var connector = new AutoConnector(() => new(3, interval), () => true, () => false,
            _ => Task.FromResult(false), _ => { }, (w, _) => { waits.Add(w); interval = TimeSpan.FromSeconds(3); return Task.CompletedTask; });
        await connector.RunAsync(default);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], waits);
    }

    [Fact]
    public async Task StepsAsideWhenTheUserIsAlreadyConnecting()
    {
        var busy = false;
        var attempts = 0;
        var connector = new AutoConnector(() => new(5, TimeSpan.FromSeconds(1)), () => true, () => busy,
            _ => { attempts++; return Task.FromResult(false); }, _ => { }, (_, _) => { busy = true; return Task.CompletedTask; });
        Assert.Equal(AutoConnectResult.Superseded, await connector.RunAsync(default));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DoesNothingWithoutASavedAddress()
    {
        List<string> log = [];
        var result = await Make(() => new(5, TimeSpan.FromSeconds(1)), _ => true, log, [], hasTarget: () => false).RunAsync(default);
        Assert.Equal(AutoConnectResult.NothingToConnect, result);
        Assert.Single(log);
    }

    [Fact]
    public async Task CancellationStopsTheWait()
    {
        using var cts = new CancellationTokenSource();
        var connector = new AutoConnector(() => new(5, TimeSpan.FromSeconds(30)), () => true, () => false,
            _ => Task.FromResult(false), _ => { });
        var run = connector.RunAsync(cts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        cts.Cancel();
        Assert.Equal(AutoConnectResult.Cancelled, await run);
    }
}
