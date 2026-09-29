using Carvera.Core.Pendant;
using Carvera.Core.Pendant.Gamepad;

namespace Carvera.App.Services;

/// <summary>
/// Starts and stops the configured pendants when the settings change. Pendants read host, port, macros and jogging
/// preferences from <see cref="Settings"/> each time they need them, so most edits apply live.
/// </summary>
public sealed class PendantService(AppServices services) : IDisposable
{
    private CydPendant? _cyd;
    private GamepadPendant? _gamepad;
    private GamepadBindings _bindings = GamepadBindings.Default;

    public CydPendant? Cyd => _cyd;
    public GamepadPendant? Gamepad => _gamepad;

    private IReadOnlyList<PendantMacro> Macros() =>
        services.Settings.Macros.Select((m, i) => new PendantMacro(i + 1, m.Name ?? "", m.Gcode ?? "")).ToList();

    private PendantPolicy Policy() =>
        new(services.Settings.PendantJoggingDefault, services.Settings.AllowJoggingWhileRunning, services.Settings.AllowJoggingWhileSpindleOn);

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
                Macros = Macros,
                Policy = Policy,
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

        if (settings.GamepadEnabled && _gamepad is null)
        {
            ReloadGamepadBindings();
            _gamepad = new GamepadPendant(services.Controller, services.Commands, new SdlGamepadSource(services.Console), new GamepadOptions
            {
                Bindings = () => _bindings,
                Policy = Policy,
                Macros = Macros,
                Deadzone = () => services.Settings.GamepadDeadzone,
                MaxJogSpeed = () => services.Settings.GamepadMaxJogSpeed,
                Invert = () => (services.Settings.GamepadInvertX, services.Settings.GamepadInvertY, services.Settings.GamepadInvertZ, services.Settings.GamepadInvertA),
            });
            _gamepad.Start();
            services.Console.Info("Gamepad enabled. Press a button on the pad you want to use.");
        }
        else if (!settings.GamepadEnabled && _gamepad is not null)
        {
            _gamepad.Dispose();
            _gamepad = null;
            services.Console.Info("Gamepad disabled.");
        }
    }

    /// <summary>Re-reads the gamepad bindings file (after it was edited or a preset was chosen) and reports problems in the console.</summary>
    public void ReloadGamepadBindings()
    {
        _bindings = GamepadBindingsStore.Load(services.Settings, out var problems);
        foreach (var problem in problems) services.Console.Warning($"Gamepad bindings: {problem}");
        if (problems.Count == 0 && _gamepad is not null) services.Console.Info("Gamepad bindings reloaded.");
    }

    public void Dispose()
    {
        _cyd?.Dispose();
        _cyd = null;
        _gamepad?.Dispose();
        _gamepad = null;
    }
}
