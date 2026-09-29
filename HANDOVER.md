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
- **Shipped layouts:** `desktop`. (`touch` and `canvas-demo` were shipped at first and removed later at the user's request: for now there is one layout.)

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

**Branch `claude/remote-file-browser` (merged as #6)**
- **Remote file browser** (`Carvera.Core/Transfer/`: `RemoteFiles`, `RemoteBrowser`; `Commands/RemoteCommands.cs`; layout component `remoteFiles`): lists the machine's SD card (`ls -e -s`), opens and leaves folders, creates folders, renames, deletes (after asking), runs a file (`play`, after asking) and uploads into the folder shown. It lists by itself once the machine is connected and idle, and again after every change and every upload. The desktop layout has a **Machine files** tab with the buttons (Up, Refresh, New folder, Upload here, Run, View, Download, Rename, Delete). State is under `remote.*`.
  - New in the controller: `CaptureAsync` collects the reply to a command that ends with EOT (or CAN on failure), which is how `ls`, `rm`, `mkdir` and `mv` answer; status polling pauses while it waits.
  - **Download** (`XmodemReceiver`, `FileDownloader`) ports `recv_legacy`: CRC then checksum start-up, the MD5 in packet 0, retransmission of bad packets, and an MD5 check at the end (a placeholder digest such as stock Z1's skips the check; data that starts with two zero bytes is QuickLZ-compressed and is saved as it came, since unpacking is not implemented). A failed or cancelled download leaves no file. **View** downloads to a temporary folder and opens the file in the 3D view.
  - One transfer at a time: `TransferGate` holds the cancellation, and pressing Upload, Download or View during a transfer cancels it.
  - The simulator now has a small SD card (files, folders, `ls`/`rm`/`mkdir`/`mv`, `download`), so all of this is tested end to end. Two simulator bugs found on the way: it truncated replies bigger than the reader's buffer, and its upload receiver mistook sequence number 0 after the wrap at 256 for the MD5 packet.
- **Layouts:** `touch` and `canvas-demo` were removed at the user's request; `desktop` is the only layout for now.
- **Desktop layout additions:** a label with the open file's name, Air and Vacuum on/off switches in the top bar (the Switches panel still has them too), and a progress bar for uploads and downloads.
- **Also added:** `IAppHost.ConfirmAsync`, `PromptAsync` and `PickSavePathAsync` (a text prompt and a save dialog in `Shell/Dialogs.cs`).

**Branch `claude/tool-widgets-and-more` (after #6, not yet merged)**
- **Tool widgets** (desktop layout, right column): *Current tool* (label such as `T3`, Probe, Laser, 3D Probe or No Tool; length offset; "Changing to ..." and the ATC activity while a change is under way) and *Tool change* (T1-T6, Probe, 3D Probe, "Other...", Drop, Calibrate, Clamp, Release, Set tool number). New state: `tool.label`, `tool.targetLabel`, `atc.label` (`ToolInfo` in `Protocol/`). New commands in `Commands/ToolCommands.cs` (`toolChange`, `toolChoose`, `toolDrop`, `toolClamp`, `toolUnclamp`, `toolCalibrate`, `toolSetNumber`): idle only (no ATC activity, no program), and each that moves the machine asks first. The older `changeTool`/`dropTool`/... commands are unchanged (the pendants use them).
- **Stalled-machine detection** (`CarveraController.CheckForStall`): `machine.awaitingStatus` is true from connecting until the first status report (the placeholder "Wait"), `machine.stalled` and `machine.silentSeconds` say the machine has stopped answering for `StallTimeout` (5 s; not counted during transfers and listings). The status box shows "Waiting..." or "No response"; the console warns once. The simulator has `IgnoreStatusQueries` for tests.
- **Probing** (`Carvera.Core/Probing/ProbeOperations.cs`, `Commands/ProbeCommands.cs`, component `probePanel`, desktop tab *Probing*): the Python probing addon ported: single axis (M466), outside/inside corner (M464/M463), bore (M461), boss (M462), angle (M465), probe tip (M460.x), calibration (M469.x) and 4th-axis stock (M465.1). The panel remembers the values per family in the settings and shows the command live; the `probe` command asks before sending and only runs while idle. Also `xyzProbe` (M495.3), `autoRun` (margin, Z probe, auto-level, go to origin over the open program's extents: the Python `autoCommand`) and `gotoPathOrigin` (M496.5), which the CYD's `path_origin` now uses too. The open program's extents are in `file.hasBounds`, `file.xmin` ... `file.zmax`. The desktop layout has a *Prepare workpiece* panel (Margin, Auto-level 3x3, Go to path origin, XYZ probe, Clear auto-level, Pair probe).
  - **Not ported:** the ring-gauge drift correction, the probing preview popup's "run" flow, and the Z-probe *screen* (its offsets); `autoRun` takes them as arguments (`zProbe`, `zProbeOffsetX`/`Y`) but no layout button sets them.
- **Machine settings** (`Carvera.Core/Config/`, component `machineConfig`, desktop tab *Machine settings*): reads `/sd/config.txt` by download, lists the settings from the Python controller's `config_c1.json`/`config_ca1.json` (embedded), edits them (changed fields turn yellow) and sends them with `config-set sd`; restore (`config-restore`) and save-as-default (`config-default`). The machine needs a reset afterwards. The simulator has a `config.txt` and answers `config-set`.
- **Firmware update** (`updateFirmware`, button in *Machine settings*): sends a `.bin` to `/sd/firmware.bin` (never as `.lz`, unlike the Python controller), then offers a reset. `IAppHost.PickOpenPathAsync` is new (a default interface method).
- **Diagnose** tab (layout only): lamps for every switch, sensor and level from the diagnose report.
- **Work offsets:** `setRotation`, `setWorkPositionPrompt`, buttons in the *Work offsets* panel; `pairProbe` (M471).

**Branch `claude/quicklz-and-leftovers` (off `claude/tool-widgets-and-more`, not yet merged; it includes that branch's uncommitted work)**
- **Real QuickLZ** (`Transfer/QuickLz.cs`): QuickLZ 1.4.1 level 3, which is what `pyquicklz` produces (checked: its blocks carry flags 0x4d/0x4f). `LzFile` now compresses each 4096-byte block (or stores it when that is not smaller) and unpacks any block. Checked both ways against `pyquicklz` (installed here, 1.4.1): the tests unpack fixtures made by it (`tests/Carvera.Tests/Fixtures/quicklz`), and setting `QUICKLZ_EXPORT_DIR` while running the `QuickLz` tests writes the C# output for `quicklz.decompress`. The old stored blocks claimed level 1 (0x44/0x46); they now say level 3. Downloads that arrive in `.lz` form are unpacked by `FileDownloader` (saved as received, with a warning, if they cannot be). Still untried on a real machine.
- **Ring-gauge drift check** (`Probing/RingGaugeDrift.cs`, `RingGaugeSession.cs`; commands `ringGaugeProbe/Back/Reset/ApplyTip/Persist`; desktop tab *Ring gauge*): port of the Python `RingGaugeDriftPopup`, built from ordinary layout elements over `probe.drift.*` state. The correction is stored in the settings and `probe` appends `G10L20P0 X.. Y..` after XY-zeroing M461-M464 probes, as Python does. `IProbeStore` (`SettingsProbeStore` in the app) holds the probing preferences.
- **Z probe position** (`zProbeSetup`, state `zprobe.*`, buttons in *Prepare workpiece*): origin (work or path) and X/Y offset, remembered; `autoRun` with `zProbe` computes the `M495 O..F..` offsets from it the way Python's `apply` does. Probe *selection* was already covered by the Probe / 3D Probe tool-change buttons.
- **Set work origin** (`setWorkOrigin`, *Set origin…* in *Work offsets*): offset from anchor 1, anchor 2, the rotation centre or the current position; anchor positions come from `coordinate.*` in the machine's config.txt (read on demand). Sends `G10L2P0`.
- **Configuration backup** (`configBackup`, *Back up…* in *Machine settings*): copies `config.txt`, `config.default`, `custom_tool_slots.txt`, `cartesian_nm.grid` and `flex_compensation.dat` (those that exist) to a chosen folder. `IAppHost.PickFolderAsync` is new.
- **Operation colour schemes** (Settings > 3D view): *Layout* (the theme's `operation1..10`, the default), colour-blind safe, high contrast, blue ramp, earth tones. The Python app has no such setting, so this is new. **Remembered folders:** the file pickers open in the folder last used (`Settings.RecentFolders`, five kept).
- **GPU lines are now wide and rapids dashed:** `GlPathLayer` draws one instanced screen-space quad per segment (feed 1.6 px, rapid 1 px dashed 6/5). Checked with `--check-gpu` on the real GPU (ANGLE, 120,027 segments, no GL error); the check now adds a sparse part with rapids. The GPU buffer holds 10 floats per segment instead of 14.
- **Desktop layout:** the right column is now four tabs (Move, Machine, Tools, Prepare). `docs/layouts.md`, `architecture.md`, `uploading.md` and the README screenshot are updated.
- **Not done from the list:** the framed Makera protocol, the WHB04 driver (no device), the facing wizard, CMM workbench, camera and tool-visualisation addons, laser workflows, translations, and the ring-gauge / Z-probe popups' *layout* is text-only (no diagram).

**Branch `claude/upload-gamepad-gpu` (merged as #5)**
- **File upload** (`Carvera.Core/Transfer/`, `docs/uploading.md`): the legacy XMODEM sender ported from `XMODEM.send_legacy` (8 KiB packets, CRC, MD5 in packet 0, the machine's cancel byte 0x16), an exclusive-access mode on the controller (polling stops, other commands are refused, the poll resumes afterwards), `.lz` files, and the `uploadFile` command. An **Upload** button is on the desktop layout; it turns into "Cancel: Uploading 42%" while sending. Settings: upload folder (`/sd/gcodes`) and Auto/On/Off for `.lz`.
  - The simulator now receives uploads (`SimulatedMachine.UploadedFiles`), so the whole path is tested end to end.
  - **The `.lz` format is real QuickLZ level 3** (see the `claude/quicklz-and-leftovers` notes) and is checked against `pyquicklz`, but not yet against a real machine. If an upload fails with *Auto*, set *Send as .lz files* to *Off*.
  - Only the plain-text (Smoothie) protocol is supported.
- **Gamepad** (`Carvera.Core/Pendant/Gamepad/`, `docs/pendants.md`): a port of the Python gamepad support. SDL reads the pad (`ppy.SDL2-CS`, `SDL2.dll` ships with the build), sticks and D-pad jog in step or continuous mode, buttons and triggers run actions, and the mapping is `gamepad-bindings.json` (the same JSON as Python's, with presets and validation). Shared jogging rules live in `PendantGate`. Each pendant publishes its own link state (`pendant.cyd.*`, `pendant.gamepad.*`) as well as the combined `pendant.connected`. **Not yet tried with a real pad.**
- **GPU toolpath renderer** (`Components/Viewer/GlPathLayer.cs`, `docs/architecture.md`): the whole path in one OpenGL buffer, coloured in the vertex shader, so scrubbing costs nothing. *Auto* uses it from 50,000 segments; *CPU* and *GPU* can be forced in Settings > 3D view. It falls back to the CPU renderer if OpenGL is unavailable. It was run on the real GPU (ANGLE, OpenGL ES 3.0) with 120,000 segments and looked right in a screen capture. `CarveraController.exe --check-gpu` repeats that check and writes `%TEMP%\carvera-gpu-check.txt` and screen captures.
- Settings gained groups *3D view*, *File upload*, *Pendants* (the jogging rules shared by all pendants) and *Gamepad*.

## Tests

- 347 tests: 282 in `tests/Carvera.Tests` and 65 headless Avalonia tests in `tests/Carvera.App.Tests`. They need no screen or mouse.
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
- **GPU view:** lines are drawn as instanced quads (feed 1.6 px wide, rapids dashed). Dashes restart on every segment and use screen-space interpolation without perspective correction, which is fine at these lengths. Checked on the real GPU only with the `--check-gpu` test path.
- **Protocol:** only the plain-text (Smoothie) protocol is spoken. The Python controller also detects and speaks the framed Makera protocol, which machines on stock firmware may need; the C# app has not been tried on one.
- **No in-app layout editor:** JSON only, as agreed.
- **Pendant gaps:** the WHB04 driver is deferred (no device to test with); `path_origin` goto and the framed jog keepalive are missing (see #4 above).
- **Windows only, as agreed.** The code also builds and runs its tests on Linux (SDL and OpenGL are untested there).
- **Real machine so far:** connecting, jogging, an upload and the CYD have been tried on the user's machine and worked. The remote file browser, download, view, the gamepad and the GPU view on real files have not. The user once saw the machine stuck on "Wait" (see below) and a restart of the *machine* cleared it, so they think it was a firmware bug.
- **"Wait" state:** the app shows "Wait" from connecting until the first status report arrives (the Python controller does the same: `CONNECTED = "Wait"`), and the firmware can also report it itself (for example after a file is started and awaits confirmation on the machine). The desktop layout now shows "Waiting..." for the placeholder (`machine.awaitingStatus`) and "No response" when reports stop (`machine.stalled`).
- **The latest listing diagnostics are not in a published build:** a failed file listing now says what the machine actually sent (`RemoteFiles.ListAsync`). It is in the code and passes tests.

## Outstanding work (everything not done yet)

Ordered roughly by value. Items 1-3 need the user; the rest can be started at once.

1. **Real-world testing** by the user: the gamepad, the Machine files tab (list, rename, delete, download, view, run), a `.lz` upload, the GPU view on a large real file, and now the tool widgets, the probing panel and `autoRun`, the Machine settings tab, firmware update and stall detection. Probing, firmware update and tool changes move or reconfigure the real machine: try them with the simulator first, and keep the stop button within reach.
2. **Real Fusion files:** the user will generate G-code from the Carvera post-processor; check the operations view against it and fix parsing gaps. The post-processor has features the app must support.
3. **Release:** tag `v0.1.0` once the testing looks good. This publishes a public release, so it is the user's call.
4. ~~QuickLZ~~ done: see the `claude/quicklz-and-leftovers` notes. Try a `.lz` upload and a compressed download on the real machine.
5. ~~Probing leftovers~~ done (ring-gauge drift check and Z probe position). Not ported: the probing preview popup's run flow, and the Z probe *diagram*.
6. **The rest of the missing features:** the set-origin dialog, configuration backup, colour schemes and recent folders are done. Still open:
   - The facing wizard, CMM workbench, camera and tool-visualisation addons.
   - Laser workflows.
   - Translations (English and zh-CN in the Python app).
7. **Protocol detection and the framed Makera protocol** (`echo echo` probe: no echo means framed; frames, file-transfer packets, `?` + Ctrl+Z jog keepalive), only if a machine needs it. The user's machine works with the plain protocol.
8. **Pendants:** the WHB04 driver when a device is available.
9. ~~GPU view polish~~ done (wide lines, dashed rapids).
10. ~~Housekeeping~~ done: docs re-read, README screenshot regenerated, the desktop layout's right column is now tabs.

## Notes for whoever continues

- **Working agreements with the user:** branch off `main` before changing code and merge through pull requests; **merging is always the user's call** and so is tagging a release. Commit and push only when asked. One shipped layout (`desktop`) for now. Every build goes through `build/publish.ps1`, which also refreshes the latest-build shortcut in the repository root.
- **`publish.ps1` kills a running `CarveraController.exe`.** Check with `Get-Process CarveraController` first if the user might be using it.
- **Tooling gotchas seen in this project:**
  - Do not rewrite source files with PowerShell `Set-Content`/`-replace`: it mangles non-ASCII characters (`‹`, `⚠`, `·`). Use the edit tools.
  - Long shell heredocs containing apostrophes or unusual quoting failed intermittently; write files with the file tools instead.
  - PowerShell script files cannot be run (execution policy); call the tools directly.
- **How the parts fit:** commands and state (`StatePaths`) are the seam between the machine logic and everything else (layouts, pendants, dialogs). New machine functions should be a command plus state, then a layout component or button. `SimulatedMachine` should learn any new machine command so it can be tested end to end; it already has status, jogging, switches, uploads, downloads and a small SD card.
- **Checking the GPU view on a machine:** `CarveraController.exe --check-gpu` writes a report and screen captures to `%TEMP%`.
- **Test commands:** `dotnet test` (run with `UPDATE_GENERATED=1` after changing commands or the component catalog, then again without).
