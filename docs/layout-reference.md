# Layout reference

_Generated from the component catalog and command registry (`SchemaGenerator`). Do not edit by hand. See [layouts.md](layouts.md) for the guide._

## Properties every element accepts

| Property | Kind | Description |
|---|---|---|
| `id` | string | Optional identifier, shown in diagnostics. |
| `class` | stringlist | Named style(s) from the layout's 'styles', applied in order. |
| `width` | size | Omitted = fill the available width. Number = pixels, "30%", "2*" (weighted share) or "auto" (fit content). |
| `height` | size | Omitted = fill the available height. Same forms as width. |
| `minWidth` | number | Minimum width in pixels. |
| `maxWidth` | number | Maximum width in pixels. |
| `minHeight` | number | Minimum height in pixels. |
| `maxHeight` | number | Maximum height in pixels. |
| `margin` | edges | Space outside the element, CSS order: 8 \| "8 4" \| "t r b l". |
| `padding` | edges | Space inside the element's border. |
| `align` | enum: `start`, `center`, `end`, `stretch` | Horizontal placement when the element is narrower than its slot. |
| `valign` | enum: `start`, `center`, `end`, `stretch` | Vertical placement when the element is shorter than its slot. |
| `visible` | expression | false hides the element; an expression shows it conditionally. |
| `enabled` | expression | false or an expression that disables the element. |
| `tooltip` | template | Hover text. |
| `style` | style | Visual properties applied to this element. |
| `visuals` | visuals | Visual properties (including images) per state, e.g. { "hover": {...}, "disabled": {...} }. |
| `conditions` | conditions | Array of { "when": expression, ...visual properties }. Matching entries apply in order, last wins. |
| `row` | number | Grid row (0-based). |
| `column` | number | Grid column (0-based). |
| `rowSpan` | number | Grid rows spanned. |
| `columnSpan` | number | Grid columns spanned. |
| `x` | size | Canvas: left offset in pixels or percent. |
| `y` | size | Canvas: top offset in pixels or percent. |
| `title` | template | Tabs: the tab header. Panel: the caption. |

## Visual properties

Used in `style`, in each state of `visuals`, in `conditions` entries and in named `styles`.

| Property | Kind | Description |
|---|---|---|
| `background` | color | Fill colour, e.g. #FFFFFF or a theme token such as @surface. |
| `foreground` | color | Text colour. |
| `borderColor` | color | Border colour. |
| `borderWidth` | edges | Border thickness. |
| `cornerRadius` | number | Corner rounding in pixels. |
| `fontSize` | number | Text size. |
| `fontWeight` | enum: `light`, `normal`, `medium`, `semibold`, `bold`, `black` | Text weight. |
| `fontFamily` | string | Font family name(s). |
| `fontStyle` | enum: `normal`, `italic` | Italic or normal. |
| `textAlign` | enum: `left`, `center`, `right` | Text alignment. |
| `opacity` | number | 0 (invisible) to 1. |
| `image` | image | Image file (SVG, PNG, JPG) relative to the layout file, or builtin:<name>. |
| `imageWidth` | number | Image width in pixels. |
| `imageHeight` | number | Image height in pixels. |
| `imagePlacement` | enum: `left`, `top`, `right`, `bottom`, `only`, `fill` | Where the image sits relative to the text. 'fill' stretches it behind the content. |
| `text` | template | Replaces the element's text in this state. |
| `padding` | edges | Inner spacing in this state. |

## Containers

### `stack`

Lays children out in a row or column. Children without a size share the free space equally.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `orientation` | enum: `horizontal`, `vertical` | Direction children are laid out. |
| `spacing` | number | Gap between children in pixels. |
| `justify` | enum: `start`, `center`, `end`, `space-between`, `space-around`, `space-evenly` | Where leftover space goes when no child fills it. |

### `grid`

Rows and columns. Children choose a cell with row/column (and rowSpan/columnSpan).

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `columns` | sizelist | Column sizes, e.g. ["200", "*", "2*", "auto"]. |
| `rows` | sizelist | Row sizes. |
| `rowSpacing` | number | Gap between rows. |
| `columnSpacing` | number | Gap between columns. |
| `spacing` | number | Gap between children in pixels. |

### `split`

Two or more panes separated by draggable splitters.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `orientation` | enum: `horizontal`, `vertical` | Direction children are laid out. |
| `sizes` | sizelist | Initial pane sizes; default is equal shares. |
| `splitterSize` | number | Splitter thickness in pixels. |

### `tabs`

Shows one child at a time; each child's 'title' is its tab header.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `placement` | enum: `top`, `bottom`, `left`, `right` | Where the tab headers sit. |
| `selected` | number | Initially selected tab (0-based). |

### `scroll`

Scrolls a single child that is larger than the available space.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `orientation` | enum: `vertical`, `horizontal`, `both` | Scroll direction. |

### `canvas`

Free placement: children are positioned with x/y and sized with width/height. Uncovered areas stay empty.

States for `visuals`: `normal`, `hover`, `disabled`.

### `panel`

A framed region with an optional caption. Children are stacked like 'stack'.

States for `visuals`: `normal`, `hover`, `disabled`, `collapsed`.

| Property | Kind | Description |
|---|---|---|
| `orientation` | enum: `horizontal`, `vertical` | Direction children are laid out. |
| `spacing` | number | Gap between children in pixels. |
| `justify` | enum: `start`, `center`, `end`, `space-between`, `space-around`, `space-evenly` | Leftover space placement. |
| `collapsible` | bool | Lets the user collapse the panel by clicking its caption. |
| `collapsed` | bool | Starts collapsed. |

## Components

### `text`

Static or live text: "X {axis.x.work:0.000}".

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `text` | template | The text; {expression:format} inserts live values. |
| `wrap` | bool | Wrap long text. |

### `value`

A labelled live value.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `bind` | expression | State path or expression to show. |
| `format` | string | Number format, e.g. 0.000, 0, 0%. |
| `label` | template | Caption. |
| `unit` | string | Unit suffix. |
| `orientation` | enum: `horizontal`, `vertical` | Caption beside (horizontal) or above (vertical) the value. |

### `axisReadout`

Position of one axis in work, machine and/or offset coordinates.

States for `visuals`: `normal`, `hover`, `disabled`, `moving`.

| Property | Kind | Description |
|---|---|---|
| `axis` | enum: `X`, `Y`, `Z`, `A` | Axis to show. |
| `show` | stringlist: `work`, `machine`, `offset` | Any of work, machine, offset (in display order). |
| `format` | string | Number format, e.g. 0.000, 0, 0%. |
| `label` | string | Axis caption; defaults to the axis letter. |
| `orientation` | enum: `horizontal`, `vertical` | Caption beside or above the numbers. |
| `color` | color | Axis accent colour. |
| `command` | command | Command to run (see the command reference). |
| `args` | args | Arguments for the command. |

### `machineStatus`

Machine state (Idle, Run, Alarm...) with a colour per state.

States for `visuals`: `normal`, `hover`, `disabled`, `idle`, `run`, `hold`, `alarm`, `home`, `tool`, `wait`, `pause`, `sleep`, `disable`, `disconnected`.

| Property | Kind | Description |
|---|---|---|
| `text` | template | Defaults to "{machine.state}". |

### `indicator`

A lamp that is on or off according to an expression.

States for `visuals`: `normal`, `hover`, `disabled`, `on`, `off`.

| Property | Kind | Description |
|---|---|---|
| `bind` | expression | On when true. |
| `text` | template | Caption. |
| `onText` | template | Caption while on. |
| `offText` | template | Caption while off. |
| `lampSize` | number | Diameter of the default lamp; 0 hides it. |

### `progress`

A progress bar.

States for `visuals`: `normal`, `hover`, `disabled`, `active`.

| Property | Kind | Description |
|---|---|---|
| `bind` | expression | Value; defaults to job.percent. |
| `min` | number | Minimum (0). |
| `max` | number | Maximum (100). |
| `text` | template | Text drawn on the bar. |
| `barColor` | color | Bar colour. |

### `image`

A picture. Use 'visuals'/'conditions' to swap it by state.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `source` | image | Image file or builtin:<name>. |
| `stretch` | enum: `none`, `fill`, `uniform`, `uniformToFill` | How the image fills its box. |

### `spacer`

Empty space. Give it a size, or leave it unsized to absorb free space.

States for `visuals`: `normal`.

### `button`

Runs a command when clicked.

States for `visuals`: `normal`, `hover`, `pressed`, `disabled`, `active`.

| Property | Kind | Description |
|---|---|---|
| `text` | template | Caption. |
| `command` | command | Command to run (see the command reference). |
| `args` | args | Arguments for the command. |
| `repeat` | bool | Repeat while held (e.g. jogging). |
| `confirm` | template | Ask for confirmation with this message first. |
| `active` | expression | Shows the 'active' state when true. |

### `toggle`

An on/off switch bound to machine state.

States for `visuals`: `normal`, `hover`, `pressed`, `disabled`, `on`, `off`.

| Property | Kind | Description |
|---|---|---|
| `text` | template | Caption. |
| `bind` | expression | On when true, e.g. switch.light. |
| `command` | command | Command to run (see the command reference). |
| `args` | args | Arguments for the command. |
| `onArgs` | args | Arguments used when turning on. |
| `offArgs` | args | Arguments used when turning off. |

### `choice`

A row or column of mutually exclusive options.

States for `visuals`: `normal`, `hover`, `pressed`, `disabled`, `selected`.

| Property | Kind | Description |
|---|---|---|
| `bind` | expression | Current value. |
| `options` | options | Array of { "value": ..., "text": ..., "image": ... } or plain values. |
| `command` | command | Command to run (see the command reference). |
| `argName` | string | Argument that receives the option value (default 'value'). |
| `args` | args | Arguments for the command. |
| `orientation` | enum: `horizontal`, `vertical` | Direction children are laid out. |
| `spacing` | number | Gap between children in pixels. |
| `optionVisuals` | visuals | Visuals for each option button (normal, hover, pressed, disabled, selected). |

### `jogStep`

Jog step selector (a 'choice' preset).

States for `visuals`: `normal`, `hover`, `pressed`, `disabled`, `selected`.

| Property | Kind | Description |
|---|---|---|
| `values` | numberlist | Steps in mm (default 0.01, 0.1, 1, 10, 100). |
| `orientation` | enum: `horizontal`, `vertical` | Direction children are laid out. |
| `spacing` | number | Gap between children in pixels. |
| `optionVisuals` | visuals | Visuals for each option. |

### `wcsSelector`

Work coordinate system selector G54-G59 (a 'choice' preset).

States for `visuals`: `normal`, `hover`, `pressed`, `disabled`, `selected`.

| Property | Kind | Description |
|---|---|---|
| `systems` | stringlist | Systems to offer (default G54-G59). |
| `orientation` | enum: `horizontal`, `vertical` | Direction children are laid out. |
| `spacing` | number | Gap between children in pixels. |
| `optionVisuals` | visuals | Visuals for each option. |

### `slider`

A slider that sends a command when released.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `bind` | expression | Current value. |
| `command` | command | Command to run (see the command reference). |
| `argName` | string | Argument that receives the value (default 'value'). |
| `args` | args | Arguments for the command. |
| `min` | number | Minimum. |
| `max` | number | Maximum. |
| `step` | number | Snap interval. |
| `orientation` | enum: `horizontal`, `vertical` | Direction children are laid out. |

### `override`

Feed, spindle or laser override: value, slider, -/+ and reset to 100%.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `kind` | enum: `feed`, `spindle`, `laser` | Which override. |
| `label` | template | Caption. |
| `increment` | number | Step for -/+ (default 10). |
| `min` | number | Slider minimum (default 10). |
| `max` | number | Slider maximum (default 200 / 300). |
| `orientation` | enum: `horizontal`, `vertical` | Direction children are laid out. |
| `buttonVisuals` | visuals | Visuals for the -, + and reset buttons. |

### `jogPad`

Jog buttons for the chosen axes, using the current jog step and feed.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `axes` | stringlist: `X`, `Y`, `Z`, `A` | Axes to include: X, Y, Z, A (default X, Y, Z). |
| `diagonals` | bool | Add XY diagonal buttons. |
| `buttonSize` | number | Button size in pixels (default: fill). |
| `spacing` | number | Gap between buttons. |
| `buttonVisuals` | visuals | Visuals applied to every jog button. |
| `images` | any | Per-button images, keyed X+, X-, Y+, Y-, Z+, Z-, A+, A-, X+Y+ ... e.g. { "X+": "assets/right.svg" }. |

### `mdi`

Manual command entry with history (Up/Down).

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `placeholder` | string | Hint text. |
| `buttonText` | string | Send button caption; empty hides the button. |

### `console`

Machine traffic and messages.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `maxLines` | number | Lines kept on screen (default 500). |
| `showSent` | bool | Show sent commands (default true). |
| `showTimestamps` | bool | Prefix lines with the time. |
| `fontSize` | number | Text size. |

### `connection`

Connect/disconnect: Wi-Fi address, USB port or the built-in simulator, with network discovery.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `kinds` | stringlist: `wifi`, `usb`, `simulator` | Connection kinds offered (default wifi, usb, simulator). |
| `orientation` | enum: `horizontal`, `vertical` | Direction children are laid out. |

### `toolpath`

3D view of the loaded G-code and the tool, navigated like Blender: middle-drag orbits, Shift+middle-drag pans, Ctrl+middle-drag or wheel zooms, numpad 1/3/7 (Ctrl: opposite) align front/right/top, numpad 5 toggles perspective, 2/4/6/8 orbit, 9 flips, . frames the tool, Home frames all. Alt+left-drag replaces the middle button; the corner gizmo can be clicked or dragged.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `view` | enum: `user`, `top`, `front`, `right` | Initial view. |
| `projection` | enum: `perspective`, `orthographic` | Initial projection. |
| `showGrid` | bool | Draw the XY grid at Z0 (default true). |
| `gridSize` | number | Grid pitch in mm (default 10). |
| `showGizmo` | bool | Show the navigation gizmo (default true). |
| `toolLength` | number | Length of the drawn tool in mm (default 25). |
| `pathColor` | color | Feed moves. |
| `rapidColor` | color | Rapid moves. |
| `doneColor` | color | Moves already executed by the running job. |
| `positionColor` | color | Tool marker. |
| `colorBy` | enum: `operation`, `single` | Colour feed moves per operation (default) or all alike with pathColor. |

### `gcodeList`

The loaded G-code file with a colour bar per operation. Follows the scrub position (or the running job); clicking a line scrubs to it.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `fontSize` | number | Text size. |

### `gcodeScrubber`

Scrubs through the loaded G-code: slider, step and operation-jump buttons, play/pause, and the line at the scrub position.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `speed` | number | Path segments per second while playing (default 150). |
| `showLine` | bool | Show the operation and G-code line under the slider (default true). |
| `buttonVisuals` | visuals | Visuals applied to the scrubber buttons. |

### `operationList`

The operations (toolpaths) in the loaded G-code with their colour, lines and tool. The tool can be changed per operation; click an operation to show it alone.

States for `visuals`: `normal`, `hover`, `disabled`.

| Property | Kind | Description |
|---|---|---|
| `tools` | numberlist | Tool numbers offered in addition to those in the file (default 1-6). |

### `toolList`

The tools the loaded G-code uses: number, description, diameter, type and the operations that use each. Highlights the tool in the spindle and the one at the scrub position.

States for `visuals`: `normal`, `hover`, `disabled`.

### `layoutSelector`

Switches between the available layout files.

States for `visuals`: `normal`, `hover`, `disabled`.

## Commands

### Application

| Command | Arguments | Description |
|---|---|---|
| `closeFile` |  | Close file. Closes the local G-code file. Works without a machine connection. |
| `exit` |  | Exit. Closes the application. Works without a machine connection. |
| `openFile` | `path`: File to open | Open G-code file. Opens a local G-code file for preview; asks for a file when 'path' is omitted. Works without a machine connection. |
| `openLayout` | `name` (required): Layout file name without .json | Switch layout. Loads another layout file by name. Works without a machine connection. |
| `previewClear` |  | Show whole G-code. Leaves scrubbing and shows the whole program. Works without a machine connection. |
| `previewStep` | `delta`: Segments to move (negative goes back) | Scrub G-code. Moves the preview scrub position by 'delta' path segments. Works without a machine connection. |
| `reloadLayout` |  | Reload layout. Reloads the current layout file from disk. Works without a machine connection. |
| `saveFile` | `path`: File to write | Save G-code as. Saves the (edited) local G-code file; asks for a name when 'path' is omitted. Works without a machine connection. |
| `selectOperation` | `index`: Operation index (0-based) or -1 | Show one operation. Shows only one operation in the viewer (-1 shows all). Works without a machine connection. |
| `setOperationTool` | `operation` (required): Operation index (0-based)<br>`tool` (required): New tool number | Change operation tool. Makes one operation of the local G-code use another tool. Only the copy in memory changes until you save it. Works without a machine connection. |

### Connection

| Command | Arguments | Description |
|---|---|---|
| `connect` | `kind`: wifi, usb or simulator<br>`address`: IP address[:port] or COM port | Connect. Connects using the given or last entered connection settings. Works without a machine connection. |
| `disconnect` |  | Disconnect.  |

### Console

| Command | Arguments | Description |
|---|---|---|
| `clearConsole` |  | Clear console.  Works without a machine connection. |
| `sendGcode` | `line` (required): The command to send | Send G-code.  |

### Motion

| Command | Arguments | Description |
|---|---|---|
| `adjustJogStep` | `delta`: +1 for larger, -1 for smaller | Next jog step.  Works without a machine connection. |
| `calibrateTool` |  | Calibrate tool length.  |
| `dropTool` |  | Drop tool.  |
| `goto` | `x`: X<br>`y`: Y<br>`z`: Z | Go to position. Rapid move in work coordinates. |
| `gotoAnchor1` |  | Go to anchor 1.  |
| `gotoAnchor2` |  | Go to anchor 2.  |
| `gotoClearance` |  | Go to clearance.  |
| `gotoMachineHome` |  | Go to machine home.  |
| `gotoSafeZ` |  | Raise to safe Z.  |
| `gotoWorkHome` |  | Go to work XY zero.  |
| `gotoWorkOrigin` |  | Go to work origin.  |
| `home` |  | Home. Homes all axes ($H). |
| `jog` | `axis` (required): e.g. X, Z-, X+Y+<br>`direction`: +1 or -1 (multiplies the signs in 'axis')<br>`distance`: Overrides the jog step<br>`feed`: mm/min; defaults to jog.feed | Jog. Relative jog by the current jog step (or 'distance'). 'axis' is X, Y, Z or A with an optional sign, and may combine axes: "X+Y-". |
| `setJogFeed` | `value` (required): mm/min | Set jog feed.  Works without a machine connection. |
| `setJogStep` | `value` (required): Step in mm | Set jog step.  Works without a machine connection. |

### Overrides

| Command | Arguments | Description |
|---|---|---|
| `feedOverride` | `value`: Percent<br>`delta`: Change in percent | Feed override.  |
| `laserScale` | `value`: Percent<br>`delta`: Change in percent | Laser scale.  |
| `spindleOverride` | `value`: Percent<br>`delta`: Change in percent | Spindle override.  |

### Run

| Command | Arguments | Description |
|---|---|---|
| `cycleStart` |  | Cycle start. Resumes after a feed hold (real-time '~'). |
| `feedHold` |  | Feed hold. Pauses motion immediately (real-time '!'). |
| `pauseResume` |  | Pause / resume. Feed hold when running, cycle start when held. |
| `playFile` | `path` (required): Remote path, e.g. /sd/gcodes/part.nc | Run file. Runs a file already on the machine's SD card. |
| `reset` |  | Reset. Soft reset (Ctrl-X): stops everything and clears the planner. |
| `resume` |  | Resume.  |
| `stop` |  | Stop. Aborts the running job. |
| `suspend` |  | Suspend.  |
| `unlock` |  | Unlock. Clears an alarm ($X). |

### Switches

| Command | Arguments | Description |
|---|---|---|
| `setAir` | `on`: true or false; omit to toggle | Air. Turns air on or off; toggles when 'on' is omitted. |
| `setLaserMode` | `on`: true or false; omit to toggle | Laser mode. Turns laser mode on or off; toggles when 'on' is omitted. |
| `setLaserTest` | `on`: true or false; omit to toggle | Laser test. Turns laser test on or off; toggles when 'on' is omitted. |
| `setLight` | `on`: true or false; omit to toggle | Light. Turns light on or off; toggles when 'on' is omitted. |
| `setSpindle` | `on`: Omit to toggle<br>`rpm`: Speed | Spindle.  |
| `setToolSensorPower` | `on`: true or false; omit to toggle | Tool sensor power. Turns tool sensor power on or off; toggles when 'on' is omitted. |
| `setVacuum` | `on`: Omit to toggle<br>`power`: 0-100 | Vacuum.  |
| `setVacuumMode` | `on`: true or false; omit to toggle | Vacuum mode. Turns vacuum mode on or off; toggles when 'on' is omitted. |
| `setWorkpieceCharge` | `on`: true or false; omit to toggle | Workpiece probe charging. Turns workpiece probe charging on or off; toggles when 'on' is omitted. |

### Tools

| Command | Arguments | Description |
|---|---|---|
| `changeTool` | `tool` (required): Tool number (0 = empty, 8888 = probe) | Change tool.  |
| `clampTool` |  | Clamp collet.  |
| `setTool` | `tool` (required): Tool number | Set current tool.  |
| `unclampTool` |  | Release collet.  |

### Work offsets

| Command | Arguments | Description |
|---|---|---|
| `clearAutoLevel` |  | Clear auto-levelling.  |
| `clearRotation` |  | Clear WCS rotation.  |
| `selectWcs` | `index`: 0 = G54 ... 5 = G59<br>`name`: G54 ... G59 | Select work coordinates.  |
| `setWorkPosition` | `x`: X<br>`y`: Y<br>`z`: Z<br>`a`: A | Set work position.  |
| `setWorkZero` | `axes`: Any of X, Y, Z, A (default XYZ) | Set work zero. Makes the current position zero in the active work coordinate system. |

