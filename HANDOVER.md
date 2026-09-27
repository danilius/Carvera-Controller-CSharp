# Handover — Carvera Controller C#

State as of 27 September 2026. Read this with `AGENTS.md`, which holds the agreed project direction and working conventions.

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

## Tests

- 127 tests: 95 in `tests/Carvera.Tests` and 32 headless Avalonia tests in `tests/Carvera.App.Tests`. They need no screen or mouse.
- Set `CARVERA_SCREENSHOT_DIR=<folder>` to render layout previews during the tests. `docs/images/*.png` come from these renders.
- After changing `ComponentCatalog.cs` or the commands, run with `UPDATE_GENERATED=1`. This regenerates `schema/layout.schema.json` and `docs/layout-reference.md`, and the tests fail if they are stale.

```powershell
dotnet test
$env:UPDATE_GENERATED=1; dotnet test; Remove-Item Env:UPDATE_GENERATED
dotnet run --project src/Carvera.App -- --layout desktop --connect simulator
./build/publish.ps1        # self-contained build in publish\win-x64
```

## Key files

- `src/Carvera.Core`: protocol parsing (`Protocol/`), connections (`Connection/`), `CarveraController.cs`, `State/StateStore.cs` (dotted-path state), `Commands/` (named commands and availability), `Expressions/`, `Gcode/` (`GcodeProgram`, `GcodeStructure`, `GcodeEditor`), `Simulation/SimulatedMachine.cs`.
- `src/Carvera.Layout`: `ComponentCatalog.cs` is the single source for element types and properties. Also the loader, validator, safety analyser and schema generator.
- `src/Carvera.App`:
  - `Layout/`: `FlexPanel`/`AbsolutePanel` sizing, `ComponentHost` visual-state layering, `Theme.cs`.
  - `Components/`: all UI components. The 3D viewer is in `Viewer/`.
  - `Shell/`: `MainWindow`, `LayoutSession`, `Dialogs`.
- `docs/architecture.md` describes the design and lists the porting status. `docs/layouts.md` is the user guide.

## Known gaps and risks

- **Never run against a real machine.** Check the protocol behaviour against the Python controller before relying on it; keep the physical stop button within reach.
- **Formats only checked against samples:** operation parsing was written from the community Fusion post source and a MakeraStudio sample. It has not been checked against the user's own Fusion output.
- **The 3D view draws on the CPU.** This is fine for typical files; very large programs may need a GPU renderer.
- **No in-app layout editor:** JSON only, as agreed.
- **Windows only, as agreed.** The code also builds and runs its tests on Linux.

## Suggested next steps

1. **Release:** tag `v0.1.0` to publish a release zip.
2. **Real files:** check the operations view against real Fusion files and fix any parsing gaps.
3. **File transfer:** port the machine file upload (XMODEM with QuickLZ) and a remote file browser.
4. **Remaining features:** probing, pendant support and the other features still missing from the port (see `docs/architecture.md`).
5. **Large files:** GPU rendering for very large toolpaths.
