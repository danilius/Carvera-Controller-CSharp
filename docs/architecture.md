# Architecture

Carvera Controller C# is a Windows desktop controller for Makera Carvera CNC machines. It is a port of the [Community Carvera Controller](https://github.com/danilius/Carvera_Controller) (Python/Kivy) with one fundamental difference: **the user interface is defined entirely by JSON layout files.** No part of the machine logic knows where anything is on screen.

```
┌──────────────────────────── Carvera.App (Avalonia) ─────────────────────────────┐
│  Shell: MainWindow (banners, live reload, shortcuts)                            │
│  Layout engine: LayoutBuilder → ComponentHost (visual states) → components      │
│  Panels: FlexPanel (web-like stack/slot sizing), AbsolutePanel (canvas)         │
└───────────────▲──────────────────────────────────────▲──────────────────────────┘
                │ LayoutDocument                        │ StateBinder (UI thread)
┌───────────────┴──────── Carvera.Layout ──────┐        │   CommandRegistry
│ LayoutLoader (JSON, regions)                 │        │
│ ComponentCatalog · LayoutValidator           │        │
│ SafetyAnalyzer · SchemaGenerator             │        │
└───────────────▲──────────────────────────────┘        │
                │ command ids, expressions              │
┌───────────────┴─────────────────── Carvera.Core ──────┴─────────────────────────┐
│ CarveraController: stream, status/diagnose polling, send queue, console log     │
│ ResponseParser → StateStore (dotted paths)   MachineCommands (text protocol)    │
│ Commands (StandardCommands, AppCommands)     Expressions / TextTemplate         │
│ Streams: TCP (Wi-Fi, port 2222), serial (USB), SimulatedMachine; UDP discovery  │
│ GcodeProgram (preview parser)                                                   │
└─────────────────────────────────────────────────────────────────────────────────┘
```

## Core (`src/Carvera.Core`)

- **`CarveraController`** opens an `IMachineStream` (Wi-Fi TCP, USB serial or the in-process simulator). It polls `?` every 200 ms and `diagnose` every second, and routes each reply line through `ResponseParser`. On connect it queries `version`, `model` and `get wcs`.
- **`StateStore`** holds every machine and application value under a dotted path (`axis.x.work`, `machine.state`, `switch.light`...). It publishes changes in batches, so one status report is one notification. Layouts bind to these paths; `StatePaths` lists the well-known ones.
- **`ResponseParser`** is a direct port of `parseBracketAngle`, `parseBigParentheses` and `parseWCSParameters` from `Controller.py`, including the rotated-WCS offset formula.
- **`MachineCommands`** builds the firmware's text commands (jog, G10 work offsets, M8xx switches, overrides, tool change...). **`StandardCommands`** exposes them as named commands with arguments, availability rules and descriptions. **`AppCommands`** adds application actions (open file, switch layout...) through the `IAppHost` interface.
- **`Expression`/`TextTemplate`** are a small, safe expression language for `visible`, `enabled`, `bind`, `conditions` and live text.
- **`GcodeProgram`, `GcodeStructure` and `GcodeEditor`** parse G-code into path segments and operations and tools (Fusion Carvera post, MakeraStudio markers, or per tool change), and edit an operation's tool safely.
- **`SimulatedMachine`** answers status, diagnose, jogging, G0 moves, G10 work offsets, switches, hold, resume and reset, so layouts and tests work without hardware.

## Layout model (`src/Carvera.Layout`)

- **`LayoutLoader`** parses JSON (comments and trailing commas allowed), expands region references (detecting cycles; overriding properties) and produces a `LayoutDocument` of `LayoutNode`s. Each node records the JSON path it came from.
- **`ComponentCatalog`** is the single description of every element type, its properties and its visual states. The validator, the JSON schema (`schema/layout.schema.json`) and the reference (`docs/layout-reference.md`) are all derived from it. A test fails if the generated files are stale; run tests with `UPDATE_GENERATED=1` to refresh them.
- **`LayoutValidator`** reports unknown types, properties, commands and arguments (with "did you mean" suggestions). It also reports bad sizes, colours, expressions and text templates, misplaced grid/canvas properties, and missing safety controls.
- **`SafetyAnalyzer`** checks that Feed Hold, Stop and Reset are each reachable through a visible button or toggle.

## App (`src/Carvera.App`)

- **`LayoutBuilder`** turns nodes into controls. Every node is wrapped in a **`ComponentHost`** (a `Border`), which:
  - resolves the element's look from built-in defaults → classes → style → per-state visuals → matching conditions;
  - tracks hover, pressed and disabled states plus component states (`on`, `selected`, `alarm`...);
  - applies enabled and visible expressions and command availability;
  - handles clicks, auto-repeat and keyboard activation.
- **`VisualContent`** renders the host's current image and text, which is how each state can have its own graphics.
- **`FlexPanel`** implements the web-like sizing rules: fixed, percentage, auto, fill and weighted sizes, justify and align, margins outside the size, and content-size-then-grow behaviour in unbounded directions. With one child it serves as the sizing slot inside grid cells, split panes, tabs and scroll viewers. **`AbsolutePanel`** implements `canvas`.
- **`StateBinder`** moves state changes from the communication threads onto the UI thread, coalescing bursts.
- **`MainWindow`** is the only fixed UI: an area for error and safety banners above the layout. It handles live reload (`FileSystemWatcher`), falling back to the embedded default layout, layout shortcuts, and the `IAppHost` services.

## 3D view renderers

The `toolpath` view has two renderers, chosen by *Settings > 3D view*:

- **CPU** rebuilds one Avalonia geometry per colour every frame and projects every segment on the CPU. It is exact and fine
  for typical files.
- **GPU** (`GlPathLayer`) keeps the whole path in one OpenGL buffer. Each segment is one instance holding its two ends and its
  segment number, operation, G-code line and rapid flag; the vertex shader expands it into a screen-space quad of a set pixel
  width (OpenGL ES has no wide lines) and picks the colour from those numbers and from uniforms (scrub position, selected
  operation, executed line), so scrubbing, selecting an operation and job progress never rebuild or re-upload anything. Rapid
  moves are dashed in the fragment shader. The camera reaches the shader as one matrix (`ViewMatrix`), which is tested to land on the same pixels as the CPU
  projection. The grid, tool, gizmo and captions stay 2D layers beneath and above it.

*Auto* (the default) uses the GPU for programs of 50,000 segments or more. The view falls back to the CPU renderer by itself if
shaders fail to compile or no OpenGL context is available (no frame within 3 seconds, as on a remote desktop), and says why in
the console. On Windows Avalonia's OpenGL is ANGLE's OpenGL ES 3.0, which needs the instanced quads above.

`CarveraController.exe --check-gpu` opens a window with a 120,000-segment test path, saves a screen capture and the scrubbed
version, and writes a report to `%TEMP%\carvera-gpu-check.txt`. It is a quick way to see whether the GPU renderer works on a
given machine.

## Status of the port

Done:

- Wi-Fi, USB and simulator connections
- Network discovery
- Status and diagnose parsing
- Jogging
- Work offsets and WCS selection
- Overrides
- Switches
- Run control (hold, resume, stop, reset, unlock, home)
- Go-to positions
- Tool commands
- MDI and console
- Local G-code: 3D view with Blender navigation, scrubbing, per-operation colours, operations and tools lists, changing an operation's tool (with spindle and coolant restored after an inserted tool change), save as
- Running files already on the machine (`playFile`)
- Settings page, window placement, auto-connect at start-up and reconnection after a dropped connection
- CYD pendant (see `pendants.md`)
- File upload over XMODEM, with QuickLZ-compressed `.lz` files (see `uploading.md`)
- Remote file browser, download and view, firmware upload, configuration backup
- Tool widgets, probing (all families), ring-gauge drift check, Z probe position, margin, auto-level, XYZ probe
- Machine configuration editor, set work origin from an anchor, rotation, diagnose lamps
- Gamepad, operation colour schemes, remembered folders for the file pickers

Not yet ported from the Python controller:

- The framed Makera protocol
- The WHB04 pendant
- Facing wizard, CMM workbench, camera and tool-visualisation addons, laser workflows
- Translations
