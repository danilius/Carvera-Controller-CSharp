# Pendants

A pendant is a hand-held controller that jogs the machine and shows its state. Pendants are set up in the
settings page: press the gear button in the top-right corner of any layout (or Ctrl+,), and use *Back to main view* (or Esc)
to return. Settings are stored in `%APPDATA%\CarveraControllerCS\settings.json`.

## Design

- Each pendant is a built-in driver in `Carvera.Core/Pendant`. A driver talks to the device and turns its
  requests into machine commands. Drivers are hardwired for now; support for more devices is added as the need arises.
- A pendant never gets more power than the on-screen controls. Motion needs a live heartbeat from the pendant, the machine state
  must allow jogging (idle or paused unless you allow more in the settings), and every request is validated.
- The pendant's link state is published to the state store as `pendant.connected` and `pendant.name`, so layouts can show it.

## CYD pendant

The driver is a port of `cyd.py` and `tcp_client.py` from the Python controller and speaks the same newline-delimited JSON
protocol on TCP port 9876, so the existing CYD firmware works unchanged.

| Direction | Messages |
|---|---|
| Controller to pendant | `heartbeat`, `pos`, `machine_state`, `macro_list`, `jog_result`, `gcode_result`, `tool_result`, `macro_result`, `runtime_result`, `position_result`, `manual_result`, `pong` |
| Pendant to controller | `heartbeat_ack`, `jog`, `jog_cont`, `tool`, `gcode`, `macro`, `runtime`, `manual`, `position`, `ping`, `machine_state_query`, `macro_query` |

Differences from the Python controller:

- `position` with target `path_origin` is refused (`path_origin_unavailable`): this app does not track the program extents on the
  machine side yet.
- The status poll sends the `?1` keepalive during a continuous jog (the non-framed protocol). The framed `?` + Ctrl+Z variant is not implemented.
- Not validated against real hardware yet.

Macros 1-10 are edited in the settings page. Only macros with both a name and G-code are offered to the pendant.

## Gamepad

A port of the Python controller's gamepad support. Input comes from SDL's joystick API (the `SDL2.dll` that ships in
`runtimes\win-x64\native`), so Xbox, PlayStation, Switch Pro and generic USB pads all work. Every attached device is opened
and the first one you use (a button, the D-pad, or a stick pushed well off centre) becomes the active pad. Enable it in
*Settings > Gamepad*.

- **Sticks and D-pad jog.** In *step* mode each push makes one step of the current jog step size; in *continuous* mode the
  machine moves while the stick is held, at a fraction of the fastest speed (2% for 0.01 mm steps, 10% for 0.1, 30% for 1, 100%
  for 10 and above), with Z capped at 800 mm/min. Continuous jogging needs community firmware.
- **Buttons and triggers run actions.** Triggers fire once per press.
- **Jogging obeys the same rules as every pendant** (the *Pendants* group in settings). Stop, reset and pause are never gated.
- Letting go of a stick, unplugging the pad or turning the gamepad off stops a continuous jog.

Options in the settings page: a preset (Xbox 360 / Xbox One, PlayStation, Nintendo Switch Pro), the stick dead zone, the
fastest continuous jog and per-axis inversion. The mapping itself is `gamepad-bindings.json` in
`%APPDATA%\CarveraControllerCS`. It is the same JSON as the Python controller's `gamepad_bindings`:

```json
{
  "axes":     { "0": "jog_x", "1": "jog_y", "4": "jog_z", "3": "jog_a" },
  "triggers": { "2:+": "feed_minus", "5:+": "feed_plus" },
  "buttons":  { "4": "step_size_down", "5": "step_size_up", "6": "mode_toggle", "7": "spindle_on_off" },
  "hat":      { "left": "jog_x", "right": "jog_x", "up": "jog_z", "down": "jog_z" }
}
```

Axis, button and hat numbers are SDL joystick indices. Edit the file, save it, and press *Reload the bindings* in the settings;
mistakes (unknown actions with a "did you mean", bad keys) are reported in the console.

Actions: `jog_x`, `jog_y`, `jog_z`, `jog_a` (axes and D-pad only), `feed_plus`, `feed_minus`, `spindle_plus`, `spindle_minus`,
`start_pause`, `stop`, `reset`, `mode_toggle`, `m_home`, `w_home`, `safe_z`, `spindle_on_off`, `step_size_up`, `step_size_down`,
`macro_1` to `macro_10`, and `probe_z` (not available yet: probing is not ported).

## State published by pendants

`pendant.<device>.connected` and `pendant.<device>.name` for each device (`cyd`, `gamepad`), the combined `pendant.connected`
and `pendant.name`, and `jog.mode` (`step` or `continuous`).
