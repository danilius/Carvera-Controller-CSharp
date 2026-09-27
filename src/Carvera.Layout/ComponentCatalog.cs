namespace Carvera.Layout;

public enum PropKind
{
    String, Number, Bool, Size, Edges, Color, Image,
    /// <summary>An expression such as "machine.state == 'Idle'"; a plain bool is also accepted.</summary>
    Expression,
    /// <summary>Text with {expression:format} placeholders.</summary>
    Template,
    Command, Args, Style, Visuals, Conditions, Enum, StringList, NumberList, SizeList, Options, Any,
}

public sealed record PropSpec(string Name, PropKind Kind, string Description, string[]? Values = null);

public enum ChildRule { None, Single, Many }

public sealed record ComponentSpec(
    string Type,
    string Description,
    ChildRule Children,
    IReadOnlyList<PropSpec> Properties,
    IReadOnlyList<string> States)
{
    public bool IsContainer => Children != ChildRule.None;
    public PropSpec? Find(string name) =>
        Properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal)) ??
        ComponentCatalog.Common.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));
}

/// <summary>Every element type a layout may use, with its properties. The validator and documentation derive from this.</summary>
public static class ComponentCatalog
{
    private static PropSpec S(string name, PropKind kind, string description, params string[] values) => new(name, kind, description, values.Length > 0 ? values : null);

    public static readonly string[] Orientations = ["horizontal", "vertical"];
    public static readonly string[] Alignments = ["start", "center", "end", "stretch"];
    public static readonly string[] BaseStates = ["normal", "hover", "disabled"];

    /// <summary>Properties accepted by every element.</summary>
    public static readonly IReadOnlyList<PropSpec> Common =
    [
        S("id", PropKind.String, "Optional identifier, shown in diagnostics."),
        S("class", PropKind.StringList, "Named style(s) from the layout's 'styles', applied in order."),
        S("width", PropKind.Size, "Omitted = fill the available width. Number = pixels, \"30%\", \"2*\" (weighted share) or \"auto\" (fit content)."),
        S("height", PropKind.Size, "Omitted = fill the available height. Same forms as width."),
        S("minWidth", PropKind.Number, "Minimum width in pixels."),
        S("maxWidth", PropKind.Number, "Maximum width in pixels."),
        S("minHeight", PropKind.Number, "Minimum height in pixels."),
        S("maxHeight", PropKind.Number, "Maximum height in pixels."),
        S("margin", PropKind.Edges, "Space outside the element, CSS order: 8 | \"8 4\" | \"t r b l\"."),
        S("padding", PropKind.Edges, "Space inside the element's border."),
        S("align", PropKind.Enum, "Horizontal placement when the element is narrower than its slot.", Alignments),
        S("valign", PropKind.Enum, "Vertical placement when the element is shorter than its slot.", Alignments),
        S("visible", PropKind.Expression, "false hides the element; an expression shows it conditionally."),
        S("enabled", PropKind.Expression, "false or an expression that disables the element."),
        S("tooltip", PropKind.Template, "Hover text."),
        S("style", PropKind.Style, "Visual properties applied to this element."),
        S("visuals", PropKind.Visuals, "Visual properties (including images) per state, e.g. { \"hover\": {...}, \"disabled\": {...} }."),
        S("conditions", PropKind.Conditions, "Array of { \"when\": expression, ...visual properties }. Matching entries apply in order, last wins."),
        // Placement inside particular parents
        S("row", PropKind.Number, "Grid row (0-based)."),
        S("column", PropKind.Number, "Grid column (0-based)."),
        S("rowSpan", PropKind.Number, "Grid rows spanned."),
        S("columnSpan", PropKind.Number, "Grid columns spanned."),
        S("x", PropKind.Size, "Canvas: left offset in pixels or percent."),
        S("y", PropKind.Size, "Canvas: top offset in pixels or percent."),
        S("title", PropKind.Template, "Tabs: the tab header. Panel: the caption."),
    ];

    /// <summary>Properties of a visual block (style, visuals.*, conditions[] and named styles).</summary>
    public static readonly IReadOnlyList<PropSpec> VisualProperties =
    [
        S("background", PropKind.Color, "Fill colour, e.g. #FFFFFF or a theme token such as @surface."),
        S("foreground", PropKind.Color, "Text colour."),
        S("borderColor", PropKind.Color, "Border colour."),
        S("borderWidth", PropKind.Edges, "Border thickness."),
        S("cornerRadius", PropKind.Number, "Corner rounding in pixels."),
        S("fontSize", PropKind.Number, "Text size."),
        S("fontWeight", PropKind.Enum, "Text weight.", "light", "normal", "medium", "semibold", "bold", "black"),
        S("fontFamily", PropKind.String, "Font family name(s)."),
        S("fontStyle", PropKind.Enum, "Italic or normal.", "normal", "italic"),
        S("textAlign", PropKind.Enum, "Text alignment.", "left", "center", "right"),
        S("opacity", PropKind.Number, "0 (invisible) to 1."),
        S("image", PropKind.Image, "Image file (SVG, PNG, JPG) relative to the layout file, or builtin:<name>."),
        S("imageWidth", PropKind.Number, "Image width in pixels."),
        S("imageHeight", PropKind.Number, "Image height in pixels."),
        S("imagePlacement", PropKind.Enum, "Where the image sits relative to the text. 'fill' stretches it behind the content.", "left", "top", "right", "bottom", "only", "fill"),
        S("text", PropKind.Template, "Replaces the element's text in this state."),
        S("padding", PropKind.Edges, "Inner spacing in this state."),
    ];

    private static readonly PropSpec Command = S("command", PropKind.Command, "Command to run (see the command reference).");
    private static readonly PropSpec Args = S("args", PropKind.Args, "Arguments for the command.");
    private static readonly PropSpec Orientation = S("orientation", PropKind.Enum, "Direction children are laid out.", Orientations);
    private static readonly PropSpec Spacing = S("spacing", PropKind.Number, "Gap between children in pixels.");
    private static readonly PropSpec Format = S("format", PropKind.String, "Number format, e.g. 0.000, 0, 0%.");

    public static readonly IReadOnlyList<ComponentSpec> All =
    [
        // ------------------------------------------------------------------ containers
        new("stack", "Lays children out in a row or column. Children without a size share the free space equally.", ChildRule.Many,
            [Orientation, Spacing, S("justify", PropKind.Enum, "Where leftover space goes when no child fills it.", "start", "center", "end", "space-between", "space-around", "space-evenly")],
            BaseStates),
        new("grid", "Rows and columns. Children choose a cell with row/column (and rowSpan/columnSpan).", ChildRule.Many,
            [S("columns", PropKind.SizeList, "Column sizes, e.g. [\"200\", \"*\", \"2*\", \"auto\"]."), S("rows", PropKind.SizeList, "Row sizes."),
             S("rowSpacing", PropKind.Number, "Gap between rows."), S("columnSpacing", PropKind.Number, "Gap between columns."), Spacing],
            BaseStates),
        new("split", "Two or more panes separated by draggable splitters.", ChildRule.Many,
            [Orientation, S("sizes", PropKind.SizeList, "Initial pane sizes; default is equal shares."), S("splitterSize", PropKind.Number, "Splitter thickness in pixels.")],
            BaseStates),
        new("tabs", "Shows one child at a time; each child's 'title' is its tab header.", ChildRule.Many,
            [S("placement", PropKind.Enum, "Where the tab headers sit.", "top", "bottom", "left", "right"), S("selected", PropKind.Number, "Initially selected tab (0-based).")],
            BaseStates),
        new("scroll", "Scrolls a single child that is larger than the available space.", ChildRule.Single,
            [S("orientation", PropKind.Enum, "Scroll direction.", "vertical", "horizontal", "both")], BaseStates),
        new("canvas", "Free placement: children are positioned with x/y and sized with width/height. Uncovered areas stay empty.", ChildRule.Many,
            [], BaseStates),
        new("panel", "A framed region with an optional caption. Children are stacked like 'stack'.", ChildRule.Many,
            [Orientation, Spacing, S("justify", PropKind.Enum, "Leftover space placement.", "start", "center", "end", "space-between", "space-around", "space-evenly"),
             S("collapsible", PropKind.Bool, "Lets the user collapse the panel by clicking its caption."), S("collapsed", PropKind.Bool, "Starts collapsed.")],
            [.. BaseStates, "collapsed"]),

        // ------------------------------------------------------------------ display
        new("text", "Static or live text: \"X {axis.x.work:0.000}\".", ChildRule.None,
            [S("text", PropKind.Template, "The text; {expression:format} inserts live values."), S("wrap", PropKind.Bool, "Wrap long text.")],
            BaseStates),
        new("value", "A labelled live value.", ChildRule.None,
            [S("bind", PropKind.Expression, "State path or expression to show."), Format, S("label", PropKind.Template, "Caption."), S("unit", PropKind.String, "Unit suffix."),
             S("orientation", PropKind.Enum, "Caption beside (horizontal) or above (vertical) the value.", Orientations)],
            BaseStates),
        new("axisReadout", "Position of one axis in work, machine and/or offset coordinates.", ChildRule.None,
            [S("axis", PropKind.Enum, "Axis to show.", "X", "Y", "Z", "A"), S("show", PropKind.StringList, "Any of work, machine, offset (in display order).", "work", "machine", "offset"),
             Format, S("label", PropKind.String, "Axis caption; defaults to the axis letter."), S("orientation", PropKind.Enum, "Caption beside or above the numbers.", Orientations),
             S("color", PropKind.Color, "Axis accent colour."), Command, Args],
            [.. BaseStates, "moving"]),
        new("machineStatus", "Machine state (Idle, Run, Alarm...) with a colour per state.", ChildRule.None,
            [S("text", PropKind.Template, "Defaults to \"{machine.state}\".")],
            ["normal", "hover", "disabled", "idle", "run", "hold", "alarm", "home", "tool", "wait", "pause", "sleep", "disable", "disconnected"]),
        new("indicator", "A lamp that is on or off according to an expression.", ChildRule.None,
            [S("bind", PropKind.Expression, "On when true."), S("text", PropKind.Template, "Caption."), S("onText", PropKind.Template, "Caption while on."), S("offText", PropKind.Template, "Caption while off."),
             S("lampSize", PropKind.Number, "Diameter of the default lamp; 0 hides it.")],
            ["normal", "hover", "disabled", "on", "off"]),
        new("progress", "A progress bar.", ChildRule.None,
            [S("bind", PropKind.Expression, "Value; defaults to job.percent."), S("min", PropKind.Number, "Minimum (0)."), S("max", PropKind.Number, "Maximum (100)."),
             S("text", PropKind.Template, "Text drawn on the bar."), S("barColor", PropKind.Color, "Bar colour.")],
            [.. BaseStates, "active"]),
        new("image", "A picture. Use 'visuals'/'conditions' to swap it by state.", ChildRule.None,
            [S("source", PropKind.Image, "Image file or builtin:<name>."), S("stretch", PropKind.Enum, "How the image fills its box.", "none", "fill", "uniform", "uniformToFill")],
            BaseStates),
        new("spacer", "Empty space. Give it a size, or leave it unsized to absorb free space.", ChildRule.None, [], ["normal"]),

        // ------------------------------------------------------------------ controls
        new("button", "Runs a command when clicked.", ChildRule.None,
            [S("text", PropKind.Template, "Caption."), Command, Args, S("repeat", PropKind.Bool, "Repeat while held (e.g. jogging)."),
             S("confirm", PropKind.Template, "Ask for confirmation with this message first."), S("active", PropKind.Expression, "Shows the 'active' state when true.")],
            ["normal", "hover", "pressed", "disabled", "active"]),
        new("toggle", "An on/off switch bound to machine state.", ChildRule.None,
            [S("text", PropKind.Template, "Caption."), S("bind", PropKind.Expression, "On when true, e.g. switch.light."), Command, Args,
             S("onArgs", PropKind.Args, "Arguments used when turning on."), S("offArgs", PropKind.Args, "Arguments used when turning off.")],
            ["normal", "hover", "pressed", "disabled", "on", "off"]),
        new("choice", "A row or column of mutually exclusive options.", ChildRule.None,
            [S("bind", PropKind.Expression, "Current value."), S("options", PropKind.Options, "Array of { \"value\": ..., \"text\": ..., \"image\": ... } or plain values."),
             Command, S("argName", PropKind.String, "Argument that receives the option value (default 'value')."), Args, Orientation, Spacing,
             S("optionVisuals", PropKind.Visuals, "Visuals for each option button (normal, hover, pressed, disabled, selected).")],
            ["normal", "hover", "pressed", "disabled", "selected"]),
        new("jogStep", "Jog step selector (a 'choice' preset).", ChildRule.None,
            [S("values", PropKind.NumberList, "Steps in mm (default 0.01, 0.1, 1, 10, 100)."), Orientation, Spacing, S("optionVisuals", PropKind.Visuals, "Visuals for each option.")],
            ["normal", "hover", "pressed", "disabled", "selected"]),
        new("wcsSelector", "Work coordinate system selector G54-G59 (a 'choice' preset).", ChildRule.None,
            [S("systems", PropKind.StringList, "Systems to offer (default G54-G59)."), Orientation, Spacing, S("optionVisuals", PropKind.Visuals, "Visuals for each option.")],
            ["normal", "hover", "pressed", "disabled", "selected"]),
        new("slider", "A slider that sends a command when released.", ChildRule.None,
            [S("bind", PropKind.Expression, "Current value."), Command, S("argName", PropKind.String, "Argument that receives the value (default 'value')."), Args,
             S("min", PropKind.Number, "Minimum."), S("max", PropKind.Number, "Maximum."), S("step", PropKind.Number, "Snap interval."), Orientation],
            BaseStates),
        new("override", "Feed, spindle or laser override: value, slider, -/+ and reset to 100%.", ChildRule.None,
            [S("kind", PropKind.Enum, "Which override.", "feed", "spindle", "laser"), S("label", PropKind.Template, "Caption."), S("increment", PropKind.Number, "Step for -/+ (default 10)."),
             S("min", PropKind.Number, "Slider minimum (default 10)."), S("max", PropKind.Number, "Slider maximum (default 200 / 300)."), Orientation,
             S("buttonVisuals", PropKind.Visuals, "Visuals for the -, + and reset buttons.")],
            BaseStates),
        new("jogPad", "Jog buttons for the chosen axes, using the current jog step and feed.", ChildRule.None,
            [S("axes", PropKind.StringList, "Axes to include: X, Y, Z, A (default X, Y, Z).", "X", "Y", "Z", "A"), S("diagonals", PropKind.Bool, "Add XY diagonal buttons."),
             S("buttonSize", PropKind.Number, "Button size in pixels (default: fill)."), S("spacing", PropKind.Number, "Gap between buttons."),
             S("buttonVisuals", PropKind.Visuals, "Visuals applied to every jog button."),
             S("images", PropKind.Any, "Per-button images, keyed X+, X-, Y+, Y-, Z+, Z-, A+, A-, X+Y+ ... e.g. { \"X+\": \"assets/right.svg\" }.")],
            BaseStates),
        new("mdi", "Manual command entry with history (Up/Down).", ChildRule.None,
            [S("placeholder", PropKind.String, "Hint text."), S("buttonText", PropKind.String, "Send button caption; empty hides the button.")],
            BaseStates),
        new("console", "Machine traffic and messages.", ChildRule.None,
            [S("maxLines", PropKind.Number, "Lines kept on screen (default 500)."), S("showSent", PropKind.Bool, "Show sent commands (default true)."),
             S("showTimestamps", PropKind.Bool, "Prefix lines with the time."), S("fontSize", PropKind.Number, "Text size.")],
            BaseStates),
        new("connection", "Connect/disconnect: Wi-Fi address, USB port or the built-in simulator, with network discovery.", ChildRule.None,
            [S("kinds", PropKind.StringList, "Connection kinds offered (default wifi, usb, simulator).", "wifi", "usb", "simulator"), Orientation],
            BaseStates),
        new("toolpath", "3D view of the loaded G-code and the tool, navigated like Blender: middle-drag orbits, Shift+middle-drag pans, Ctrl+middle-drag or wheel zooms, numpad 1/3/7 (Ctrl: opposite) align front/right/top, numpad 5 toggles perspective, 2/4/6/8 orbit, 9 flips, . frames the tool, Home frames all. Alt+left-drag replaces the middle button; the corner gizmo can be clicked or dragged.", ChildRule.None,
            [S("view", PropKind.Enum, "Initial view.", "user", "top", "front", "right"), S("projection", PropKind.Enum, "Initial projection.", "perspective", "orthographic"),
             S("showGrid", PropKind.Bool, "Draw the XY grid at Z0 (default true)."), S("gridSize", PropKind.Number, "Grid pitch in mm (default 10)."),
             S("showGizmo", PropKind.Bool, "Show the navigation gizmo (default true)."), S("toolLength", PropKind.Number, "Length of the drawn tool in mm (default 25)."),
             S("pathColor", PropKind.Color, "Feed moves."), S("rapidColor", PropKind.Color, "Rapid moves."), S("doneColor", PropKind.Color, "Moves already executed by the running job."),
             S("positionColor", PropKind.Color, "Tool marker."),
             S("colorBy", PropKind.Enum, "Colour feed moves per operation (default) or all alike with pathColor.", "operation", "single")],
            BaseStates),
        new("gcodeList", "The loaded G-code file with a colour bar per operation. Follows the scrub position (or the running job); clicking a line scrubs to it.", ChildRule.None,
            [S("fontSize", PropKind.Number, "Text size.")], BaseStates),
        new("gcodeScrubber", "Scrubs through the loaded G-code: slider, step and operation-jump buttons, play/pause, and the line at the scrub position.", ChildRule.None,
            [S("speed", PropKind.Number, "Path segments per second while playing (default 150)."), S("showLine", PropKind.Bool, "Show the operation and G-code line under the slider (default true)."),
             S("buttonVisuals", PropKind.Visuals, "Visuals applied to the scrubber buttons.")],
            BaseStates),
        new("operationList", "The operations (toolpaths) in the loaded G-code with their colour, lines and tool. The tool can be changed per operation; click an operation to show it alone.", ChildRule.None,
            [S("tools", PropKind.NumberList, "Tool numbers offered in addition to those in the file (default 1-6).")], BaseStates),
        new("toolList", "The tools the loaded G-code uses: number, description, diameter, type and the operations that use each. Highlights the tool in the spindle and the one at the scrub position.", ChildRule.None,
            [], BaseStates),
        new("layoutSelector", "Switches between the available layout files.", ChildRule.None, [], BaseStates),
    ];

    private static readonly Dictionary<string, ComponentSpec> ByType = All.ToDictionary(c => c.Type, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string type, out ComponentSpec spec) => ByType.TryGetValue(type, out spec!);
}
