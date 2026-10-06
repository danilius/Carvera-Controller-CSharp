using System.Globalization;
using Carvera.Core.Commands;
using Carvera.Core.Probing;
using Carvera.Core.State;

namespace Carvera.Core.Job;

/// <summary>Which point on the bed the work origin is taken from.</summary>
public static class OriginChoice
{
    public const string Anchor1 = "anchor1", Anchor2 = "anchor2", Probe = "probe";
    public static bool IsValid(string? value) => value is Anchor1 or Anchor2 or Probe;
}

/// <summary>What the job setup page remembers: the origin choice and its offset, which preparation steps to run, and the auto-level grid.</summary>
public sealed record JobSettings(
    string Origin = OriginChoice.Anchor1, double OriginX = 0, double OriginY = 0,
    bool Margin = false, bool ZProbe = true, bool Leveling = false, bool GotoOrigin = true,
    int LevelPointsX = 3, int LevelPointsY = 3, double LevelHeight = 5,
    double LevelXn = 0, double LevelXp = 0, double LevelYn = 0, double LevelYp = 0)
{
    public double[] LevelOffsets => [LevelXn, LevelXp, LevelYn, LevelYp];
}

/// <summary>
/// Publishes what the job setup page shows, under <c>job.*</c> and <c>bed.*</c>: the origin choice and where the work origin
/// lies relative to it, the path's size and origin, the steps chosen and the auto-level grid. It recomputes when the machine's
/// offsets, the open file or the settings change.
/// </summary>
public sealed class JobSetup : IDisposable
{
    private readonly StateStore _state;
    private readonly IProbeStore _store;
    private BedGeometry _geometry = BedGeometry.Default;
    private bool _updating;

    public JobSetup(StateStore state, IProbeStore store)
    {
        _state = state;
        _store = store;
        _state.Changed += OnChanged;
        Publish();
    }

    public BedGeometry Geometry => _geometry;

    /// <summary>Uses the coordinates of the machine's config.txt (called when the settings have been read).</summary>
    public void UseConfig(IReadOnlyDictionary<string, string> config)
    {
        _geometry = BedGeometry.From(config);
        Publish();
    }

    public void Dispose() => _state.Changed -= OnChanged;

    private static readonly HashSet<string> Watched =
    [
        StatePaths.AxisOffset("x"), StatePaths.AxisOffset("y"), StatePaths.WcsRotation, StatePaths.WcsActiveName,
        StatePaths.FileHasBounds, StatePaths.FileXMin, StatePaths.FileXMax, StatePaths.FileYMin, StatePaths.FileYMax, StatePaths.FileZMin, StatePaths.FileZMax,
        StatePaths.ZProbeOrigin, StatePaths.ZProbeX, StatePaths.ZProbeY,
    ];

    private void OnChanged(IReadOnlyCollection<string> paths)
    {
        if (_updating || !paths.Any(Watched.Contains)) return;
        Publish();
    }

    private static string G(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public void Publish()
    {
        _updating = true;
        try
        {
            _geometry.Publish(_state);
            var job = _store.Job;
            using var _ = _state.BeginBatch();
            _state.Set(StatePaths.JobOrigin, job.Origin);
            _state.Set(StatePaths.JobOriginOffsetX, job.OriginX);
            _state.Set(StatePaths.JobOriginOffsetY, job.OriginY);
            _state.Set(StatePaths.JobMargin, job.Margin);
            _state.Set(StatePaths.JobZProbe, job.ZProbe);
            _state.Set(StatePaths.JobLeveling, job.Leveling);
            _state.Set(StatePaths.JobGotoOrigin, job.GotoOrigin);
            _state.Set(StatePaths.JobLevelX, job.LevelPointsX);
            _state.Set(StatePaths.JobLevelY, job.LevelPointsY);
            _state.Set(StatePaths.JobLevelXn, job.LevelXn);
            _state.Set(StatePaths.JobLevelXp, job.LevelXp);
            _state.Set(StatePaths.JobLevelYn, job.LevelYn);
            _state.Set(StatePaths.JobLevelYp, job.LevelYp);
            _state.Set(StatePaths.JobLevelText, $"{job.LevelPointsX} x {job.LevelPointsY} points, height {G(job.LevelHeight)}"
                + (job.LevelOffsets.Any(o => o != 0) ? $", margins -X {G(job.LevelXn)} +X {G(job.LevelXp)} -Y {G(job.LevelYn)} +Y {G(job.LevelYp)}" : ""));

            // Where the work origin lies relative to the anchors (as the Python controller's origin label shows it).
            var wcoX = _state.Get(StatePaths.AxisOffset("x"), 0.0);
            var wcoY = _state.Get(StatePaths.AxisOffset("y"), 0.0);
            var g = _geometry;
            var fromA1 = (X: wcoX - g.Anchor1X, Y: wcoY - g.Anchor1Y);
            var fromA2 = (X: fromA1.X - g.Anchor2OffsetX, Y: fromA1.Y - g.Anchor2OffsetY);
            var wcs = _state.Get<string>(StatePaths.WcsActiveName) ?? "";
            _state.Set(StatePaths.JobOriginText, $"{wcs} at ({G(fromA1.X)}, {G(fromA1.Y)}) from anchor 1, ({G(fromA2.X)}, {G(fromA2.Y)}) from anchor 2".TrimStart());

            var has = _state.Get(StatePaths.FileHasBounds, false);
            _state.Set(StatePaths.JobBoundsText, has
                ? $"{G(_state.Get(StatePaths.FileXMax, 0.0) - _state.Get(StatePaths.FileXMin, 0.0))} x {G(_state.Get(StatePaths.FileYMax, 0.0) - _state.Get(StatePaths.FileYMin, 0.0))} x {G(_state.Get(StatePaths.FileZMax, 0.0) - _state.Get(StatePaths.FileZMin, 0.0))} mm"
                : "No file open");
            _state.Set(StatePaths.JobPathOriginText, has
                ? $"({G(_state.Get(StatePaths.FileXMin, 0.0))}, {G(_state.Get(StatePaths.FileYMin, 0.0))}) from the work origin"
                : "No file open");
        }
        finally { _updating = false; }
    }
}
