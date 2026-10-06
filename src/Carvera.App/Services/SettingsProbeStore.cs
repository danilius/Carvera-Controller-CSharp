using Carvera.Core.Probing;

namespace Carvera.App.Services;

/// <summary>Keeps the probing preferences in <c>settings.json</c>.</summary>
public sealed class SettingsProbeStore(Settings settings) : IProbeStore
{
    public IReadOnlyDictionary<string, string> ProbeSettings(string family) =>
        settings.ProbeSettings.TryGetValue(family, out var values) ? values : new Dictionary<string, string>();

    public DriftCorrection Drift
    {
        get => new(settings.RingGaugeEnabled, settings.RingGaugeX, settings.RingGaugeY);
        set
        {
            settings.RingGaugeEnabled = value.Enabled;
            settings.RingGaugeX = value.X;
            settings.RingGaugeY = value.Y;
            settings.Save();
        }
    }

    public ZProbeSetting ZProbe
    {
        get => new(settings.ZProbeOrigin, settings.ZProbeX, settings.ZProbeY);
        set
        {
            settings.ZProbeOrigin = value.Origin;
            settings.ZProbeX = value.X;
            settings.ZProbeY = value.Y;
            settings.Save();
        }
    }

    public Carvera.Core.Job.JobSettings Job
    {
        get => settings.JobSetup ?? new();
        set
        {
            settings.JobSetup = value;
            settings.Save();
        }
    }
}
