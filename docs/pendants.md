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
