using Carvera.Core;
using Carvera.Core.Pendant.Gamepad;
using SDL2;

namespace Carvera.App.Services;

/// <summary>
/// Reads game controllers through SDL's joystick API, so any pad SDL knows works: Xbox, PlayStation, Switch Pro and
/// generic HID pads. Axis, button and hat numbers are SDL joystick indices, matching the Python controller's bindings.
/// Every attached device is opened; the first one to be used (a button, a hat, or a stick pushed well off centre)
/// becomes the active pad, like the Python controller does, so a mouse or keyboard that shows up as a joystick is ignored.
/// </summary>
public sealed class SdlGamepadSource(ConsoleLog console) : IGamepadSource
{
    private const int PollMilliseconds = 8;
    private const int RescanMilliseconds = 1000;
    private const int PickThreshold = 16000;

    private Thread? _thread;
    private volatile bool _stop;

    public event Action<GamepadEvent>? Event;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "sdl-gamepad" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private sealed class Pad(int index, IntPtr handle, string name)
    {
        public int Index { get; } = index;
        public IntPtr Handle { get; } = handle;
        public string Name { get; } = name;
        public short[] Axes { get; } = new short[Math.Max(0, SDL.SDL_JoystickNumAxes(handle))];
        public bool[] Buttons { get; } = new bool[Math.Max(0, SDL.SDL_JoystickNumButtons(handle))];
        public byte[] Hats { get; } = new byte[Math.Max(0, SDL.SDL_JoystickNumHats(handle))];
    }

    private void Run()
    {
        try
        {
            // Without this SDL only reports input while one of its own windows has focus.
            SDL.SDL_SetHint(SDL.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
            if (SDL.SDL_InitSubSystem(SDL.SDL_INIT_JOYSTICK) != 0)
            {
                console.Warning($"Gamepad support is unavailable: {SDL.SDL_GetError()}");
                return;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            console.Warning($"Gamepad support is unavailable: the SDL library could not be loaded ({ex.Message}).");
            return;
        }

        var pads = new List<Pad>();
        Pad? active = null;
        var lastScan = Environment.TickCount64 - RescanMilliseconds;
        try
        {
            while (!_stop)
            {
                SDL.SDL_JoystickUpdate();
                if (Environment.TickCount64 - lastScan >= RescanMilliseconds)
                {
                    lastScan = Environment.TickCount64;
                    Rescan(pads, ref active);
                }
                foreach (var pad in pads.ToList())
                    if (active is null || pad == active) Poll(pad, ref active);
                Thread.Sleep(PollMilliseconds);
            }
        }
        finally
        {
            foreach (var pad in pads) SDL.SDL_JoystickClose(pad.Handle);
            SDL.SDL_QuitSubSystem(SDL.SDL_INIT_JOYSTICK);
        }
    }

    private void Rescan(List<Pad> pads, ref Pad? active)
    {
        // Forget pads that were unplugged.
        foreach (var pad in pads.Where(p => SDL.SDL_JoystickGetAttached(p.Handle) != SDL.SDL_bool.SDL_TRUE).ToList())
        {
            SDL.SDL_JoystickClose(pad.Handle);
            pads.Remove(pad);
            if (pad == active)
            {
                active = null;
                Raise(new GamepadDisconnected());
            }
        }
        // Open pads that appeared. Indices can shift, so devices are told apart by their instance.
        var count = SDL.SDL_NumJoysticks();
        for (var i = 0; i < count; i++)
        {
            var handle = SDL.SDL_JoystickOpen(i);
            if (handle == IntPtr.Zero) continue;
            var instance = SDL.SDL_JoystickInstanceID(handle);
            if (pads.Any(p => SDL.SDL_JoystickInstanceID(p.Handle) == instance))
            {
                SDL.SDL_JoystickClose(handle); // a second reference to one we already hold
                continue;
            }
            var pad = new Pad(i, handle, SDL.SDL_JoystickName(handle) ?? "Gamepad");
            for (var axis = 0; axis < pad.Axes.Length; axis++) pad.Axes[axis] = SDL.SDL_JoystickGetAxis(handle, axis);
            for (var button = 0; button < pad.Buttons.Length; button++) pad.Buttons[button] = SDL.SDL_JoystickGetButton(handle, button) != 0;
            for (var hat = 0; hat < pad.Hats.Length; hat++) pad.Hats[hat] = SDL.SDL_JoystickGetHat(handle, hat);
            pads.Add(pad);
        }
    }

    private void Poll(Pad pad, ref Pad? active)
    {
        var events = new List<GamepadEvent>();
        var strong = false; // deliberate input, enough to choose this pad
        for (var axis = 0; axis < pad.Axes.Length; axis++)
        {
            var value = SDL.SDL_JoystickGetAxis(pad.Handle, axis);
            if (value == pad.Axes[axis]) continue;
            pad.Axes[axis] = value;
            events.Add(new GamepadAxis(axis, value));
            if (Math.Abs((int)value) > PickThreshold) strong = true;
        }
        for (var button = 0; button < pad.Buttons.Length; button++)
        {
            var down = SDL.SDL_JoystickGetButton(pad.Handle, button) != 0;
            if (down == pad.Buttons[button]) continue;
            pad.Buttons[button] = down;
            events.Add(new GamepadButton(button, down));
            strong = true;
        }
        for (var hat = 0; hat < pad.Hats.Length; hat++)
        {
            var value = SDL.SDL_JoystickGetHat(pad.Handle, hat);
            if (value == pad.Hats[hat]) continue;
            pad.Hats[hat] = value;
            var dx = (value & SDL.SDL_HAT_RIGHT) != 0 ? 1 : (value & SDL.SDL_HAT_LEFT) != 0 ? -1 : 0;
            var dy = (value & SDL.SDL_HAT_UP) != 0 ? 1 : (value & SDL.SDL_HAT_DOWN) != 0 ? -1 : 0;
            events.Add(new GamepadHat(dx, dy));
            strong = true;
        }
        if (events.Count == 0) return;
        if (active is null)
        {
            if (!strong) return; // drift or noise on an unused device
            active = pad;
            Raise(new GamepadConnected(pad.Name));
        }
        foreach (var e in events) Raise(e);
    }

    private void Raise(GamepadEvent e)
    {
        try { Event?.Invoke(e); }
        catch (Exception ex) { console.Error($"Gamepad: {ex.Message}"); }
    }
}
