using System.Globalization;
using Carvera.App.Services;
using Carvera.Core;
using Carvera.Core.Commands;
using Carvera.Core.Connection;
using Carvera.Core.Gcode;
using Carvera.Core.State;

namespace Carvera.Editor.Preview;

/// <summary>A machine situation the preview can pretend to be in, so layouts that change with the machine's state can be checked without a machine.</summary>
public sealed record SimulatedSituation(string Name, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// The application services behind the preview: a controller that is never connected, so no command reaches a machine, and a state
/// store the editor fills with sample values. The preview's settings live in a scratch folder.
/// </summary>
public sealed class PreviewServices : IDisposable
{
    public static readonly string[] MachineStates = ["Idle", "Run", "Hold", "Alarm", "Home", "Tool", "Wait", "Pause", "Sleep", "Disable"];

    public PreviewServices()
    {
        Controller = new CarveraController(streamFactory: _ => new SilentStream())
        {
            // Nothing answers the status queries; the controller must not decide the machine has hung.
            StallTimeout = TimeSpan.FromDays(1), DiagnosePolling = false,
        };
        Services = new AppServices(Controller, new PreviewHost(), new LayoutLibrary([Path.Combine(AppContext.BaseDirectory, "layouts")]), new Settings { AutoReadConfig = false });
        _ = ApplyAsync(Situations[1]);
    }

    public CarveraController Controller { get; }
    public AppServices Services { get; }
    public StateStore State => Services.State;
    public SimulatedSituation Current { get; private set; } = null!;
    public bool HasSampleProgram => Services.Program is not null;

    private static Dictionary<string, object?> Common() => new()
    {
        [StatePaths.ConnectionKind] = "usb",
        [StatePaths.MachineModelName] = "Carvera",
        [StatePaths.FirmwareVersion] = "1.0.3",
        [StatePaths.AxisWork("x")] = 12.5, [StatePaths.AxisWork("y")] = 40.25, [StatePaths.AxisWork("z")] = -3.0, [StatePaths.AxisWork("a")] = 0.0,
        [StatePaths.AxisMachine("x")] = -150.5, [StatePaths.AxisMachine("y")] = -90.0, [StatePaths.AxisMachine("z")] = -20.0, [StatePaths.AxisMachine("a")] = 0.0,
        [StatePaths.AxisOffset("x")] = -163.0, [StatePaths.AxisOffset("y")] = -130.25, [StatePaths.AxisOffset("z")] = -17.0, [StatePaths.AxisOffset("a")] = 0.0,
        [StatePaths.WcsActive] = 0, [StatePaths.WcsActiveName] = "G54",
        [StatePaths.FeedOverride] = 100.0, [StatePaths.SpindleOverride] = 100.0,
        [StatePaths.JogStep] = 1.0, [StatePaths.JogFeed] = 1000.0,
        [StatePaths.ToolCurrent] = 1, [StatePaths.ToolLabel] = "T1",
    };

    public static readonly IReadOnlyList<SimulatedSituation> Situations = BuildSituations();

    private static List<SimulatedSituation> BuildSituations()
    {
        Dictionary<string, object?> With(string state, Action<Dictionary<string, object?>>? extra = null)
        {
            var v = Common();
            v[StatePaths.Connected] = true;
            v[StatePaths.ConnectionState] = "Connected";
            v[StatePaths.MachineState] = state;
            extra?.Invoke(v);
            return v;
        }
        var disconnected = Common();
        disconnected[StatePaths.Connected] = false;
        disconnected[StatePaths.ConnectionState] = "Disconnected";
        disconnected[StatePaths.MachineState] = "N/A";
        return
        [
            new("Disconnected", disconnected),
            new("Idle", With("Idle")),
            new("Running a job", With("Run", v =>
            {
                v[StatePaths.JobPlaying] = true; v[StatePaths.JobPercent] = 42.0; v[StatePaths.JobLines] = 1200; v[StatePaths.JobSeconds] = 754;
                v[StatePaths.FeedCurrent] = 1500.0; v[StatePaths.FeedTarget] = 1500.0; v[StatePaths.SpindleCurrent] = 12000.0; v[StatePaths.SpindleTarget] = 12000.0;
            })),
            new("Feed hold", With("Hold", v => { v[StatePaths.JobPlaying] = true; v[StatePaths.JobPercent] = 42.0; })),
            new("Alarm", With("Alarm", v => v[StatePaths.HaltReason] = "Hard limit")),
        ];
    }

    /// <summary>
    /// Applies a situation, then any values the user typed in the "state values" box. A connected situation "connects" to a stream that
    /// swallows everything, so controls look enabled as they do on a live machine while nothing reaches any device.
    /// </summary>
    public async Task ApplyAsync(SimulatedSituation situation, IReadOnlyDictionary<string, object?>? overrides = null)
    {
        Current = situation;
        var wantConnected = situation.Values.TryGetValue(StatePaths.Connected, out var c) && c is true;
        if (wantConnected && !Controller.IsConnected)
        {
            try { await Controller.ConnectAsync(new ConnectionOptions(ConnectionKind.Simulator, "preview")).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { }
        }
        else if (!wantConnected && Controller.IsConnected) await Controller.DisconnectAsync().ConfigureAwait(false);

        using (State.BeginBatch())
        {
            foreach (var (path, value) in situation.Values) State.Set(path, value);
            State.Set(StatePaths.AwaitingStatus, false);
            State.Set(StatePaths.Stalled, false);
            if (overrides is not null)
                foreach (var (path, value) in overrides) State.Set(path, value);
        }
    }

    /// <summary>Loads the sample G-code so the 3D view, the G-code list and the job pages show something.</summary>
    public bool SetSampleProgram(bool on)
    {
        if (!on)
        {
            Services.SetProgram(null);
            return true;
        }
        var file = Path.Combine(AppContext.BaseDirectory, "samples", "demo.nc");
        if (!File.Exists(file)) return false;
        Services.SetProgram(GcodeProgram.Load(file));
        return true;
    }

    /// <summary>Parses "path = value" lines into typed values: true/false, numbers, and text otherwise.</summary>
    public static Dictionary<string, object?> ParseOverrides(string text, out List<string> problems)
    {
        problems = [];
        var values = new Dictionary<string, object?>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) { problems.Add($"'{line}' is not path = value"); continue; }
            var path = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            values[path] = value.ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                "null" => null,
                _ => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : value.Trim('"', '\''),
            };
        }
        return values;
    }

    public void Dispose()
    {
        _ = Controller.DisconnectAsync();
        Services.Dispose();
    }

    /// <summary>A machine that never says anything and forgets everything it is told.</summary>
    private sealed class SilentStream : IMachineStream
    {
        public string Description => "layout preview (no machine)";
        public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PreviewHost : IAppHost
    {
        public Task OpenFileAsync(string? path) => Task.CompletedTask;
        public Task CloseFileAsync() => Task.CompletedTask;
        public Task LoadLayoutAsync(string name) => Task.CompletedTask;
        public Task ReloadLayoutAsync() => Task.CompletedTask;
        public Task ExitAsync() => Task.CompletedTask;
        public Task SaveFileAsync(string? path) => Task.CompletedTask;
        public Task SetOperationToolAsync(int operation, int tool) => Task.CompletedTask;
        public Task StepPreviewAsync(int? delta) => Task.CompletedTask;
        public Task SelectOperationAsync(int operation) => Task.CompletedTask;
        public Task UploadFileAsync(string? path, string? remoteDirectory) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message) => Task.FromResult(false);
        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(null);
        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult<string?>(null);
    }
}
