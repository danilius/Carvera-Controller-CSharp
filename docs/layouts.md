# Writing layouts

Everything you see in Carvera Controller C# comes from a layout file. You decide which controls exist, where they go, how big they are, and how they look in each state. This guide explains the format. For every property and command, see the generated [layout reference](layout-reference.md).

## Where layouts live

| Folder | Purpose |
|---|---|
| `layouts\` next to `CarveraController.exe` | Layouts that ship with the program (`desktop`, `touch`, `canvas-demo`). |
| `%APPDATA%\CarveraControllerCS\layouts\` | Your own layouts. A file here replaces a shipped one with the same name. |

- Choose a layout with the `layoutSelector` element, the `openLayout` command, or `CarveraController.exe --layout <name or path>`. The last one used is remembered.
- **Ctrl+L** opens a layout picker in any layout, even one without a `layoutSelector`. F5 and Ctrl+L are reserved and cannot be used as layout shortcuts.
- The app watches the current layout file. **Save the file and the window rebuilds immediately.** F5 also reloads it.
- If a file has errors, a red banner lists them (with the JSON path of each problem) and the previous layout stays on screen. At startup, the built-in desktop layout is used instead, so the machine can always be operated.
- Point your editor at `schema/layout.schema.json` (the shipped layouts do this with `"$schema"`) to get autocompletion and inline checks. Comments (`//`) and trailing commas are allowed.

## File structure

```jsonc
{
  "$schema": "../schema/layout.schema.json",
  "name": "My layout",
  "description": "Shown nowhere yet; for you.",
  "window": { "minWidth": 1000, "minHeight": 700, "title": "Carvera" },
  "theme": { "accent": "#0F766E", "fontSize": "14" },   // override colour and font tokens
  "styles": { "big": { "fontSize": 20 } },               // named styles, used via "class"
  "regions": { "dro": { ... } },                         // named, reusable pieces
  "root": { ... },                                       // the window content
  "shortcuts": [ { "key": "Escape", "command": "feedHold" } ]
}
```

## Elements

Every element has a `type`. **Containers** arrange children; **components** do something.

| Containers | |
|---|---|
| `stack` | Children in a row (`"orientation": "horizontal"`) or column (default). |
| `panel` | A framed `stack` with an optional `title`; can be `collapsible`. |
| `grid` | Rows and columns; children pick cells with `row`/`column` (+ spans). |
| `split` | Panes with draggable splitters. |
| `tabs` | One child at a time; each child's `title` is its tab. |
| `scroll` | Scrolls one `child`. |
| `canvas` | Free placement with `x`/`y`. |

Components include `axisReadout`, `machineStatus`, `button`, `toggle`, `choice`, `jogPad`, `jogStep`, `wcsSelector`, `override`, `slider`, `value`, `text`, `indicator`, `progress`, `image`, `console`, `mdi`, `connection`, `toolpath`, `gcodeList`, `layoutSelector` and `spacer`. The [reference](layout-reference.md) lists their properties.

## Sizing works like a web page

- **No `width`/`height`: fill the available space.** In a `stack`, unsized children share whatever the sized ones leave, equally.
- A **number** is pixels: `"width": 120`.
- A **percentage** is relative to the parent: `"width": "30%"`.
- A **weight** takes a bigger share of the leftover space: `"2*"` gets twice as much as an unsized sibling.
- **`"auto"`** fits the content.
- `minWidth`, `maxWidth`, `minHeight` and `maxHeight` limit any of these.
- `margin` adds space outside and `padding` inside, written CSS-style: `8`, `"8 4"` (vertical, horizontal) or `"8 4 2 6"` (top, right, bottom, left).
- Sizes include padding and border but not margin, like CSS `box-sizing: border-box`.

**Empty areas are allowed.** If every child in a stack has a size and they don't fill it, the rest stays empty. `justify` decides where that leftover space goes: `start` (default), `center`, `end`, `space-between`, `space-around` or `space-evenly`. On the cross axis, a sized child sits at the start unless you set `align` (horizontal) or `valign` (vertical) to `start`, `center`, `end` or `stretch`.

Inside a `scroll`, there is no fixed length to share. Unsized children take their content size and grow only when there is spare room.

In a `grid` or `split`, the column, row and pane sizes accept the same forms. Percentages there act as proportions: `"30%"` and `"70%"` split the space 3:7.

On a `canvas`, `x` and `y` place each child (pixels or percent of the canvas). A child without a `width` or `height` extends to the right or bottom edge.

## Arrange things however you like

The X, Y and Z readouts are separate elements, so you can put them in any order, in any orientation, anywhere:

```json
{ "type": "stack", "orientation": "horizontal", "height": 110, "spacing": 16, "children": [
  { "type": "axisReadout", "axis": "Z", "show": ["work"], "width": 260 },
  { "type": "axisReadout", "axis": "X", "show": ["work"], "width": 260 },
  { "type": "axisReadout", "axis": "Y", "show": ["work"], "width": 260 }
] }
```

`show` picks which coordinates appear and in what order (`work`, `machine`, `offset`).

## Regions: build once, place anywhere

Define a piece once under `regions` and use it anywhere with `{ "region": "name" }`. Properties written next to the reference override the region's own, so the same region can have a different size, margin or class in each place:

```json
"regions": {
  "dro": { "type": "panel", "title": "Position", "children": [ ... ] }
},
"root": { "type": "stack", "children": [
  { "region": "dro", "height": "auto" },
  { "type": "split", "children": [ { "region": "dro", "orientation": "horizontal" }, { "type": "toolpath" } ] }
] }
```

Regions can contain other regions. A region that refers back to itself is reported as an error.

## Live values: bindings, expressions and text

- `bind` takes a **state path** (such as `switch.light`) or an **expression**. The [reference](layout-reference.md) and `src/Carvera.Core/State/StatePaths.cs` list the paths. Diagnose switches and sensors are `switch.*`, `level.*` and `sensor.*`.
- **Expressions** are used by `bind`, `visible`, `enabled`, `active` and `conditions[].when`. Example: `machine.state == 'Alarm' || !connection.connected`. They support `== != < <= > >=`, `&& || !` (or `and`, `or`, `not`), `+ - * / %`, parentheses, `'strings'`, numbers, `true`/`false`/`null`. String comparisons ignore case.
- **Text** fields accept live placeholders: `"Feed {feed.current:0} mm/min"`, `"{job.percent:0}%"`. After the colon comes a .NET number format such as `0.000`. Write `{{` and `}}` for literal braces.

## Custom graphics for every state

Each element's look is built up in layers. Later layers win:

1. Built-in defaults for that element type (light theme)
2. Named `styles` listed in `class`, in order
3. `style`
4. `visuals.normal`
5. For each active state: the built-in look for that state, then `visuals.<state>`. The order is the element's own states (such as `on`/`off`, `selected`, `active`, machine states like `alarm`), then `hover`, `pressed`, `disabled`.
6. Every entry in `conditions` whose `when` is true, in order

A visual block can set `background`, `foreground`, `borderColor`, `borderWidth`, `cornerRadius`, font properties, `opacity`, `padding`, **`image`** (SVG, PNG, JPG or `builtin:<name>`), `imageWidth`/`imageHeight`, `imagePlacement` (`left`, `top`, `right`, `bottom`, `only`, `fill`) and **`text`**. So each state can have its own artwork and caption:

```json
{
  "type": "toggle", "text": "Light", "bind": "switch.light", "command": "setLight",
  "visuals": {
    "off":      { "image": "assets/light-off.svg" },
    "on":       { "image": "assets/light-on.svg", "background": "#FEFCE8" },
    "pressed":  { "image": "assets/light-pressed.svg" },
    "disabled": { "image": "assets/light-disabled.svg", "opacity": 0.6 }
  }
}
```

`conditions` handle anything else that depends on machine state:

```json
"conditions": [
  { "when": "machine.state == 'Hold'", "text": "Resume", "image": "assets/resume.svg" }
]
```

Colours can refer to theme tokens with `@name` (`@accent`, `@surface`, `@danger`...). The full list of tokens is in `src/Carvera.App/Layout/Theme.cs`. Built-in icons: `arrow-up/down/left/right`, the diagonals (`arrow-up-left` and so on), `play`, `pause`, `stop`, `reset`, `home`, `unlock`, `light`, `air`, `spindle`, `plus`, `minus`, `folder`, `target`, `warning`, `plug`, `circle`, `check`, `close`, `tool`, `probe`, `zero`.

## Commands

Buttons, toggles, choices, sliders, readouts and keyboard shortcuts run **commands**, optionally with `args`:

```json
{ "type": "button", "text": "Zero XY", "command": "setWorkZero", "args": { "axes": "XY" } }
{ "type": "button", "text": "Home", "command": "home", "confirm": "Home all axes now?" }
{ "type": "button", "text": "X+", "command": "jog", "args": { "axis": "X+" }, "repeat": true }
```

A control is automatically disabled while its command can't run, for example when no machine is connected or motion is blocked during an alarm. Switch commands (`setLight`, `setAir`, ...) toggle when `on` is omitted, and a `toggle` sends the opposite of its bound state.

Shortcuts use key names like `Escape`, `F5`, `Ctrl+Up`, `Ctrl+Shift+H`. Plain keys are ignored while you type in a text box. Shortcuts with Ctrl/Alt, function keys, Escape and Pause always work.

## Safety controls

Every layout should show **Feed Hold** (`feedHold` or `pauseResume`), **Stop** (`stop`) and **Reset** (`reset`) as visible buttons or toggles.

If one is missing, or hidden by `"visible": false`, a zero size or zero opacity (on the element or any container around it), the app shows a **yellow warning banner** and writes the warning to the console. The layout still loads. Controls with conditional visibility (an expression) count as present. Keyboard shortcuts do not count, because you cannot see them.

The `canvas-demo` layout leaves out Reset on purpose so you can see the warning.

## Try layouts without a machine

Choose **Simulator** in the connection bar (or start with `--connect simulator`). The built-in simulator answers status and diagnose queries and handles jogging, work zero, switches, feed hold, resume and reset. That's enough to try layouts and custom graphics without hardware. It does not run G-code files.
