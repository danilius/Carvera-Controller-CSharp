using Carvera.Core.Commands;
using Carvera.Core.Probing;
using Carvera.Core.State;
using Xunit;

namespace Carvera.Tests;

public class RingGaugeTests
{
    private static MemoryProbeStore Store()
    {
        var store = new MemoryProbeStore();
        store.Families["probeTip"] = new Dictionary<string, string> { ["X"] = "20", ["Y"] = "20", ["D"] = "3.175", ["F"] = "200" };
        return store;
    }

    private static StateStore Connected(string machineState = "Idle")
    {
        var state = new StateStore();
        state.Set(StatePaths.Connected, true);
        state.Set(StatePaths.MachineState, machineState);
        return state;
    }

    [Fact]
    public void TheProbeCommandZeroesNothingAndCarriesTheTipDiameterOnlyWhenAsked()
    {
        var tip = new Dictionary<string, string> { ["X"] = "20", ["Y"] = "20", ["D"] = "3.175", ["S"] = "1", ["Z"] = "9" };
        Assert.Equal("M461 X20 Y20 S0 D3.175", RingGaugeDrift.BuildProbe(tip, true).Gcode);
        Assert.Equal("M461 X20 Y20 S0", RingGaugeDrift.BuildProbe(tip, false).Gcode);
        Assert.False(RingGaugeDrift.BuildProbe(new Dictionary<string, string>(), true).Ok);
    }

    [Fact]
    public void TheCorrectionIsTheAverageCentreRelativeToTheMarkedPosition()
    {
        var result = RingGaugeDrift.Analyse([(10.0, 10.0), (10.03, 10.0), (10.03, 10.06)])!;
        Assert.Equal(0.02, result.CorrectionX, 6);
        Assert.Equal(0.02, result.CorrectionY, 6);
        Assert.Equal(0, result.First);
        Assert.Equal(2, result.Second);
        Assert.Equal(result.MaxShift / 2, result.LowerBound, 9);
        Assert.Null(RingGaugeDrift.Analyse([(0.0, 0.0)]));
    }

    [Theory]
    [InlineData(0.05, 0.0, 0.05, 0.05, "usable")]
    [InlineData(0.001, 0.0, 0.001, 0.002, "small shift")]
    [InlineData(0.05, 0.0, 0.0, 0.0, "rough")]
    public void TheResultIsRatedByTheSpread(double x1, double y1, double x2, double y2, string expected)
    {
        // Three points: the first at the origin, the others as given.
        var result = RingGaugeDrift.Analyse([(0.0, 0.0), (x1, y1), (x2, y2)])!;
        Assert.Contains(expected, result.Quality);
    }

    [Fact]
    public void ZeroingProbesGetTheCorrectionAndOthersDoNot()
    {
        var on = new DriftCorrection(true, 0.0125, -0.03);
        var s1 = new Dictionary<string, string> { ["S"] = "1" };
        Assert.Equal(["M461 X20 S1", "G10L20P0 X-0.0125 Y0.0300"], RingGaugeDrift.WithCorrection("M461 X20 S1", s1, on));
        Assert.Equal(["M461 X20 S0"], RingGaugeDrift.WithCorrection("M461 X20 S0", new Dictionary<string, string> { ["S"] = "0" }, on));
        Assert.Equal(["M461 X20"], RingGaugeDrift.WithCorrection("M461 X20", new Dictionary<string, string>(), on));
        Assert.Equal(["M466 X5 S1"], RingGaugeDrift.WithCorrection("M466 X5 S1", s1, on));
        Assert.Equal(["M461 X20 S1"], RingGaugeDrift.WithCorrection("M461 X20 S1", s1, on with { Enabled = false }));
        Assert.Equal(["M461 X20 S1"], RingGaugeDrift.WithCorrection("M461 X20 S1", s1, new DriftCorrection(true, 0, 0)));
    }

    [Fact]
    public async Task ThreeProbesConcludeAndStoreTheCorrection()
    {
        var state = Connected();
        var store = Store();
        var sent = new List<string>();
        RingGaugeSession? session = null;
        var points = new Queue<(double, double)>([(5.0, 5.0), (5.03, 5.0), (5.03, 5.06)]);
        session = new RingGaugeSession(state, store, line =>
        {
            sent.Add(line);
            // The machine runs, finishes and reports where it stopped.
            _ = Task.Run(async () =>
            {
                await Task.Delay(20);
                state.Set(StatePaths.MachineState, "Run");
                await Task.Delay(30);
                var (x, y) = points.Dequeue();
                state.Set(StatePaths.AxisWork("x"), x);
                state.Set(StatePaths.AxisWork("y"), y);
                state.Set(StatePaths.MachineState, "Idle");
            });
            return Task.CompletedTask;
        })
        { PollInterval = TimeSpan.FromMilliseconds(5) };

        session.SetPersist(true);
        Assert.Equal("Step 1 of 3: Marked cable position", state.Get<string>(StatePaths.DriftTitle));
        var asked = new List<string>();
        for (var i = 0; i < 3; i++)
            await session.ProbeAsync(m => { asked.Add(m); return Task.FromResult(true); });

        Assert.Equal(3, asked.Count);
        Assert.Equal(["M461 X20 Y20 F200 S0 D3.175\n", "M461 X20 Y20 F200 S0 D3.175\n", "M461 X20 Y20 F200 S0 D3.175\n"], sent);
        Assert.True(session.Done);
        Assert.True(state.Get(StatePaths.DriftDone, false));
        Assert.Equal("Restart", state.Get<string>(StatePaths.DriftPrimary));
        Assert.True(store.Drift.Enabled);
        Assert.Equal(0.02, store.Drift.X, 6);
        Assert.Equal(0.02, store.Drift.Y, 6);
        Assert.Contains("Correction stored", state.Get<string>(StatePaths.DriftResult));
        Assert.Contains("Stored correction: X0.0200 Y0.0200", state.Get<string>(StatePaths.DriftStored));

        // Pressing the button once more starts over, keeping the stored correction.
        await session.ProbeAsync(_ => Task.FromResult(true));
        Assert.False(session.Done);
        Assert.Empty(session.Points);
        Assert.True(store.Drift.Enabled);
    }

    [Fact]
    public async Task ADeclinedQuestionSendsNothingAndAProbeThatNeverStartsIsReported()
    {
        var state = Connected();
        var sent = 0;
        var session = new RingGaugeSession(state, Store(), _ => { sent++; return Task.CompletedTask; }) { PollInterval = TimeSpan.FromMilliseconds(1), StartPolls = 5 };
        await session.ProbeAsync(_ => Task.FromResult(false));
        Assert.Equal(0, sent);
        await session.ProbeAsync(_ => Task.FromResult(true));
        Assert.Equal(1, sent);
        Assert.Contains("did not appear to start", state.Get<string>(StatePaths.DriftResult));
        Assert.Equal(0, session.Step);
        Assert.False(session.Running);
    }

    [Fact]
    public async Task MissingProbeTipSettingsAreExplained()
    {
        var state = Connected();
        var session = new RingGaugeSession(state, new MemoryProbeStore(), _ => Task.CompletedTask);
        await session.ProbeAsync(_ => Task.FromResult(true));
        Assert.Contains("Missing required parameter", state.Get<string>(StatePaths.DriftResult));
    }

    [Fact]
    public void GoingBackForgetsTheLastMeasurement()
    {
        var session = new RingGaugeSession(new StateStore(), Store(), _ => Task.CompletedTask);
        session.Capture((1, 1));
        session.Capture((2, 2));
        Assert.Equal(2, session.Step);
        session.Back();
        Assert.Equal(1, session.Step);
        Assert.Single(session.Points);
    }

    [Fact]
    public void TheZProbePositionIsCorrectedForTheOriginAndTheLevellingMargin()
    {
        var state = new StateStore();
        state.Set(StatePaths.FileXMin, 10.0);
        state.Set(StatePaths.FileYMin, 20.0);
        Assert.Equal((-5.0, -18.0), ProbeCommands.ZProbeOffsets(state, new ZProbeSetting("work", 5, 2)));
        Assert.Equal((5.0, 2.0), ProbeCommands.ZProbeOffsets(state, new ZProbeSetting("path", 5, 2)));
        Assert.Equal((3.0, -2.0), ProbeCommands.ZProbeOffsets(state, new ZProbeSetting("path", 5, 2), [2, 0, 4, 0]));
    }

    [Theory]
    [InlineData("work 10 5", "work", 10.0, 5.0)]
    [InlineData("path -3,2.5", "path", -3.0, 2.5)]
    [InlineData("7 8", "path", 7.0, 8.0)]
    public void TheZProbePositionIsParsedFromTypedText(string text, string origin, double x, double y) =>
        Assert.Equal(new ZProbeSetting(origin, x, y), ProbeCommands.ParseZProbe(text, new ZProbeSetting("path", 0, 0)));

    [Theory]
    [InlineData("")]
    [InlineData("work 10")]
    [InlineData("work a b")]
    public void BadZProbeTextIsRefused(string text) => Assert.Null(ProbeCommands.ParseZProbe(text, ZProbeSetting.Default));
}
