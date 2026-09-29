# Handover — Carvera Controller C#

State as of 29 September 2026. Read this with `AGENTS.md`, which holds the agreed project direction and working conventions.

## Where things are

- **Repository:** https://github.com/danilius/Carvera-Controller-CSharp (GPL-2.0).
- **Local clone:** `F:\Git Repos\Carvera Controller C#`.
- **Branches:** `main` holds everything; there are no open pull requests. Work on descriptive branches off `main` and merge through pull requests.
- **CI:** `.github/workflows/build.yml` runs on `windows-latest`. It runs the tests and publishes a self-contained win-x64 zip as the `CarveraController-win-x64` artifact. Pushing a `v*` tag also creates a GitHub release with the zip.

## What has been done

| PR | Contents |
|---|---|
| [#1](https://github.com/danilius/Carvera-Controller-CSharp/pull/1) | The initial port and the JSON layout system. |
| [#2](https://github.com/danilius/Carvera-Controller-CSharp/pull/2) | Layout switching from any layout. |
| [#3](https://github.com/danilius/Carvera-Controller-CSharp/pull/3) | 3D G-code view, scrubbing, operations and tools. |
| [#4](https://github.com/danilius/Carvera-Controller-CSharp/pull/4) | CYD pendant, settings page, window persistence, auto-connect and reconnect. |

**#1: initial port and layout system**
- **Connection:** Wi-Fi (TCP 2222, with UDP 3333 discovery), USB serial at 115200 baud, and a built-in simulator.
- **Machine controls:** status parsing, jogging, work offsets (WCS), overrides, switches, run control (feed hold, resume, stop, reset), MDI and the console.
- **Layouts:** every UI element comes from JSON layouts in `layouts/` or `%APPDATA%\CarveraControllerCS\layouts`.
  - Containers: `stack`, `grid`, `split`, `tabs`, `scroll`, `canvas` and `panel`.
  - Sizing follows web rules: an element without a size fills the space, and explicit px, %, `auto` and star sizes are honoured.
  - Regions are reusable and can be overridden where they are placed.
  - Styles, classes, theme tokens, per-state visuals (including SVG/PNG images), expression-driven conditions and keyboard shortcuts.
  - Layout files reload live when saved. Errors show with their location and a "did you mean" suggestion, and the previous layout stays on screen.
  - A warning appears when a layout hides or omits Feed Hold, Stop or Reset.
- **Shipped layouts:** `desktop`, `touch` (teal theme) and `canvas-demo`, which deliberately omits Reset.

**#2: layout switching**
- Ctrl+L opens a layout picker in every layout.
- `canvas-demo` gained a `layoutSelector`.
- The validator warns when a layout binds the reserved keys F5 or Ctrl+L.

**#3: 3D G-code view**
- **Navigation:** a Z-up 3D `toolpath` view with Blender-style controls: middle-drag orbits, Shift+middle pans and the wheel zooms.
  - Numpad views: 1/3/7 plus Ctrl, 5 toggles perspective/orthographic, 2/4/6/8 orbit in steps, 9 flips to the opposite side.
  - Home frames everything and numpad `.` frames the tool.
  - Alt+left-drag stands in for the middle button, and the corner axis gizmo can be clicked or dragged.
- **Operations:** detected from Fusion Carvera-post comments, MakeraStudio `;@MKR|…` markers, or tool changes. Each operation is drawn in its own colour.
- **`gcodeScrubber`:** a slider with step, jump and play controls. The view fades moves after the scrub point and shows a preview tool there.
- **`operationList`:** a per-operation tool selector.
  - If the operation has its own tool change, only the tool number is rewritten.
  - Otherwise a `T# M6` is inserted, and the active spindle speed and coolant are restored after it.
  - If the next operation relied on the old tool, it gets an explicit change back.
- **`toolList`:** each tool's details, highlighting the tool in the spindle and the tool at the scrub point.
- **`saveFile`:** Save as writes the edited file.

**#4: pendant, settings and connection handling**
- **CYD pendant** (`Carvera.Core/Pendant/`): a port of `cyd.py` and `tcp_client.py` that speaks the same newline-delimited JSON protocol on TCP 9876, so the existing firmware works unchanged. See `docs/pendants.md`.
  - Motion needs a live heartbeat (250 ms probes, 1 s timeout) and a machine state that allows jogging. Pendant jogging while running or with the spindle on is off unless enabled in Settings.
  - Only `M461`, `M462` and `M466` probing G-code is accepted from the pendant.
  - Macros 1–10 (imported from the Python controller's config), overrides, tool actions, work-origin actions and manual milling (C1 only).
  - `position` with target `path_origin` is refused (`path_origin_unavailable`): the program extents are not tracked on the machine side yet.
- **Core additions:** continuous jog (`$J -c`, the `?1` keepalive, `^Y` handling) and a `ConnectionLost` event. The framed `?` + Ctrl+Z keepalive is not implemented.
- **Settings page** (`Shell/SettingsView.cs`, hardcoded Avalonia): a group list on the left and one scrolling column with a header per group (General, Connection, CYD pendant, Macros). A gear button in its own column on the right of every layout opens it; Ctrl+, and Esc also flip. It is not part of any layout file.
- **Layout selector removed** from the shipped layouts. Switch layouts in Settings or with Ctrl+L. The `layoutSelector` component still exists.
- **Window state persists:** position, size and maximised state. A saved position that is off every screen falls back to the centre.
- **Auto-connect at start-up** to the last-used machine, and **reconnect after a dropped connection**, with a configurable retry count and interval (defaults 5 retries, 5 s). A deliberate Disconnect is respected, a simulator is never connected automatically, and `--connect` skips auto-connect.
- **Build shortcut:** `build/publish.ps1` refreshes `Carvera Controller (latest build).lnk` in the repository root (git-ignored).

**Branch `claude/upload-gamepad-gpu` (work after #4, not yet merged)**
- **File upload** (`Carvera.Core/Transfer/`, `docs/uploading.md`): the legacy XMODEM sender ported from `XMODEM.send_legacy` (8 KiB packets, CRC, MD5 in packet 0, the machine's cancel byte 0x16), an exclusive-access mode on the controller (polling stops, other commands are refused, the poll resumes afterwards), `.lz` files, and the `uploadFile` command. An **Upload** button is on the desktop layout; it turns into "Cancel: Uploading 42%" while sending. Settings: upload folder (`/sd/gcodes`) and Auto/On/Off for `.lz`.
  - The simulator now receives uploads (`SimulatedMachine.UploadedFiles`), so the whole path is tested end to end.
  - **The `.lz` blocks are stored, not compressed**, so they are valid QuickLZ blocks but the upload is as long as the original. Their format is from the QuickLZ spec and has not been checked against a real machine or `pyquicklz`; if an upload fails with *Auto*, set *Send as .lz files* to *Off*.
  - Only the plain-text (Smoothie) protocol is supported.
- **Gamepad** (`Carvera.Core/Pendant/Gamepad/`, `docs/pendants.md`): a port of the Python gamepad support. SDL reads the pad (`ppy.SDL2-CS`, `SDL2.dll` ships with the build), sticks and D-pad jog in step or continuous mode, buttons and triggers run actions, and the mapping is `gamepad-bindings.json` (the same JSON as Python's, with presets and validation). Shared jogging rules live in `PendantGate`. Each pendant publishes its own link state (`pendant.cyd.*`, `pendant.gamepad.*`) as well as the combined `pendant.connected`. **Not yet tried with a real pad.**
- **GPU toolpath renderer** (`Components/Viewer/GlPathLayer.cs`, `docs/architecture.md`): the whole path in one OpenGL buffer, coloured in the vertex shader, so scrubbing costs nothing. *Auto* uses it from 50,000 segments; *CPU* and *GPU* can be forced in Settings > 3D view. It falls back to the CPU renderer if OpenGL is unavailable. It was run on the real GPU (ANGLE, OpenGL ES 3.0) with 120,000 segments and looked right in a screen capture. `CarveraController.exe --check-gpu` repeats that check and writes `%TEMP%\carvera-gpu-check.txt` and screen captures.
- Settings gained groups *3D view*, *File upload*, *Pendants* (the jogging rules shared by all pendants) and *Gamepad*.

## Tests

- 215 tests: 161 in `tests/Carvera.Tests` and 54 headless Avalonia tests in `tests/Carvera.App.Tests`. They need no screen or mouse.
- Set `CARVERA_SCREENSHOT_DIR=<folder>` to render layout previews during the tests. `docs/images/*.png` come from these renders.
- After changing `ComponentCatalog.cs` or the commands, run with `UPDATE_GENERATED=1`. This regenerates `schema/layout.schema.json` and `docs/layout-reference.md`, and the tests fail if they are stale.

```powershell
dotnet test
$env:UPDATE_GENERATED=1; dotnet test; Remove-Item Env:UPDATE_GENERATED
dotnet run --project src/Carvera.App -- --layout desktop --connect simulator
./build/publish.ps1        # self-contained build in publish\win-x64
```

## Key files

- `src/Carvera.Core`: pendants (`Pendant/`: `CydPendant`, `CydClient`, `NdjsonDecoder`, `PendantGate`, `Gamepad/`), file transfer (`Transfer/`: `FileUploader`, `XmodemSender`, `LzFile`, `ByteLink`), `Connection/AutoConnector.cs`, protocol parsing (`Protocol/`), connections (`Connection/`), `CarveraController.cs`, `State/StateStore.cs` (dotted-path state), `Commands/` (named commands and availability), `Expressions/`, `Gcode/` (`GcodeProgram`, `GcodeStructure`, `GcodeEditor`), `Simulation/SimulatedMachine.cs`.
- `src/Carvera.Layout`: `ComponentCatalog.cs` is the single source for element types and properties. Also the loader, validator, safety analyser and schema generator.
- `src/Carvera.App`:
  - `Layout/`: `FlexPanel`/`AbsolutePanel` sizing, `ComponentHost` visual-state layering, `Theme.cs`.
  - `Components/`: all UI components. The 3D viewer is in `Viewer/`.
  - `Shell/`: `MainWindow`, `SettingsView`, `LayoutSession`, `Dialogs`. `Services/` holds `Settings`, `PendantService` and `AppServices`.
- `docs/architecture.md` describes the design and lists the porting status. `docs/layouts.md` is the user guide.

## Known gaps and risks

- **Never run against a real machine.** Check the protocol behaviour against the Python controller before relying on it; keep the physical stop button within reach. The CYD pendant has been tried with the real CYD, but its safety gating has only been tested against the simulator and fake pendants.
- **Formats only checked against samples:** operation parsing was written from the community Fusion post source and a MakeraStudio sample. It has not been checked against the user's own Fusion output.
- **The GPU view's lines are one pixel wide** (ANGLE's OpenGL ES does not do wide lines) and dashed rapids are drawn as plain grey lines. The CPU view has both.
- **Protocol:** only the plain-text (Smoothie) protocol is spoken. The Python controller also detects and speaks the framed Makera protocol, which machines on stock firmware may need; the C# app has not been tried on one.
- **No in-app layout editor:** JSON only, as agreed.
- **Pendant gaps:** the WHB04 driver is deferred (no device to test with); `path_origin` goto and the framed jog keepalive are missing (see #4 above).
- **Windows only, as agreed.** The code also builds and runs its tests on Linux (SDL and OpenGL are untested there).

## Suggested next steps

1. **Real-world testing** of connecting, jogging, upload, the CYD and the gamepad against the actual machine. Nothing here has run against one yet.
2. **Real Fusion files:** the user will generate G-code from the Carvera post-processor; check the operations view against it and fix parsing gaps.
3. **Release:** tag `v0.1.0` once the real-world testing looks good.
4. **Protocol detection and the framed Makera protocol,** if the machine (or other users' machines) need it.
5. **Remote file browser, download and upload-and-run:** the machine's file list, folders, delete/rename, and running an uploaded file. Then a real QuickLZ compressor.
6. **Probing:** Z and XYZ probe screens, the probing addon (bore, boss, corner, angle, single axis, ring gauge), tool-length calibration and probe selection.
7. **Tool widgets** (requested by the user):
   - **Current tool widget:** shows the tool in the spindle (number and a label such as Probe, Laser, 3D Probe or "No Tool"), its length offset, and the target tool while a change is under way. The state paths already exist (`tool.current`, `tool.offset`, `tool.target`, `atc.state`); the CYD driver has the labelling rules (`CydPendant.ToolLabel`).
   - **Tool change and tool functions widget:** change to a chosen tool (`changeTool`), drop the tool, clamp and unclamp the collet, calibrate tool length, set the current tool number (`setTool`), probe and laser tool changes. The commands already exist in `StandardCommands`; the widget needs the tool list, availability rules (idle only, as for the pendant's tool actions) and confirmation for actions that move the machine. Consider linking it to the program's `toolList` so tools the file names can be changed to with one click.
8. **The rest of the missing features, roughly by value:**
   - WCS settings, set-rotation and origin dialogs.
   - Auto-levelling setup.
   - Machine configuration editor.
   - Diagnose view (the switch and sensor states are already in the state store; this is a component and a layout).
   - Firmware update.
   - Wireless probe pairing.
   - The facing wizard, CMM workbench and camera addons.
   - Laser workflows.
   - Translations (English and zh-CN in the Python app).
   - G-code colour schemes.
9. **More pendants:** WHB04 when a device is available, then the `path_origin` goto.
