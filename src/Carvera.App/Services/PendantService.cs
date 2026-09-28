using Carvera.Core.Pendant;

namespace Carvera.App.Services;

/// <summary>
/// Starts and stops the configured pendants when the settings change. The pendant reads host, port,
/// macros and jogging preferences from <see cref="Settings"/> each time it needs them, so edits apply live.
/// </summary>
public sealed class PendantService(AppServices services) : IDisposable
{
    private CydPendant? _cyd;

    public CydPendant? Cyd => _cyd;

    /// <summary>Brings running pendants in line with the settings (starts, stops, or leaves them alone).</summary>
    public void Apply()
    {
        var settings = services.Settings;
        if (settings.CydEnabled && _cyd is null)
        {
            _cyd = new CydPendant(services.Controller, services.Commands, new CydPendantOptions
            {
                Host = () => services.Settings.CydHost,
                Port = () => services.Settings.CydPort,
                Macros = () => services.Settings.Macros
                    .Select((m, i) => new PendantMacro(i + 1, m.Name ?? "", m.Gcode ?? ""))
                    .ToList(),
                Policy = () => new PendantPolicy(services.Settings.PendantJoggingDefault, services.Settings.AllowJoggingWhileRunning, services.Settings.AllowJoggingWhileSpindleOn),
            });
            _cyd.Start();
            services.Console.Info($"CYD pendant enabled ({settings.CydHost}:{settings.CydPort}).");
        }
        else if (!settings.CydEnabled && _cyd is not null)
        {
            _cyd.Dispose();
            _cyd = null;
            services.Console.Info("CYD pendant disabled.");
        }
    }

    public void Dispose()
    {
        _cyd?.Dispose();
        _cyd = null;
    }
}
