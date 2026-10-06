using System.Globalization;

namespace Carvera.Core.Probing;

/// <summary>One parameter of a probing command, e.g. <c>D</c> = probe tip diameter.</summary>
public sealed record ProbeParameter(string Code, string Label, string Description, bool Required = false, string Default = "");

/// <summary>What a probing operation produced: the G-code line, or the parameter that is missing or invalid.</summary>
public sealed record ProbeResult(string? Gcode, string? Problem, string? Note = null)
{
    public bool Ok => Gcode is not null;
}

/// <summary>
/// A probing operation of the community firmware (M460-M469). Ported from the Python controller's probing addon:
/// each operation turns the parameters of its family into one command line.
/// </summary>
public sealed class ProbeOperation
{
    public required string Family { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>The M-code, e.g. "M461" or "M460.1".</summary>
    public required string Command { get; init; }
    /// <summary>Codes that must be given for this operation.</summary>
    public IReadOnlyList<string> Required { get; init; } = [];
    /// <summary>Codes left out of the command (parameters of the family that this operation does not use).</summary>
    public IReadOnlyList<string> Omit { get; init; } = [];
    /// <summary>Codes whose value is negated: probing in the negative direction.</summary>
    public IReadOnlyList<string> Negate { get; init; } = [];
    /// <summary>Text shown with the command, e.g. what must be installed first.</summary>
    public string? Note { get; init; }

    public ProbeResult Build(IReadOnlyDictionary<string, string> config, IReadOnlyList<ProbeParameter> parameters)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in config)
            if (!string.IsNullOrWhiteSpace(value)) values[key.Trim().ToUpperInvariant()] = value.Trim();

        foreach (var code in Required)
            if (!values.ContainsKey(code))
                return new ProbeResult(null, $"Missing required parameter {parameters.FirstOrDefault(p => p.Code == code)?.Label ?? code}");

        foreach (var code in Omit) values.Remove(code);
        foreach (var code in Negate)
        {
            if (!values.TryGetValue(code, out var text)) continue;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return new ProbeResult(null, $"{parameters.FirstOrDefault(p => p.Code == code)?.Label ?? code} is not a number: {text}");
            values[code] = (-number).ToString("0.######", CultureInfo.InvariantCulture);
        }

        var known = parameters.Select(p => p.Code).ToList();
        foreach (var (code, text) in values)
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return new ProbeResult(null, $"{parameters.FirstOrDefault(p => p.Code == code)?.Label ?? code} is not a number: {text}");

        var parts = known.Where(values.ContainsKey).Select(code => code + values[code]);
        return new ProbeResult(Command + " " + string.Join(' ', parts), null, Note);
    }
}

/// <summary>A group of probing operations that share parameters (bore, boss, outside corner...).</summary>
public sealed class ProbeFamily
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<ProbeParameter> Parameters { get; init; }
    public required IReadOnlyList<ProbeOperation> Operations { get; init; }
}

public static class ProbeOperations
{
    private static ProbeParameter P(string code, string label, string description, bool required = false, string def = "") => new(code, label, description, required, def);

    private static readonly ProbeParameter X = P("X", "X distance", "Distance along X to probe.");
    private static readonly ProbeParameter Y = P("Y", "Y distance", "Distance along Y to probe.");
    private static readonly ProbeParameter Z = P("Z", "Z distance", "Distance along Z to probe.");
    private static readonly ProbeParameter PocketDepth = P("H", "Pocket depth", "If set, probes down by this amount to find the bottom and retracts slightly before probing the sides. Useful for shallow pockets.");
    private static readonly ProbeParameter FastFeed = P("F", "Fast feed", "Fast feed rate override (mm/min).");
    private static readonly ProbeParameter Rapid = P("K", "Rapid", "Rapid feed rate override (mm/min).");
    private static readonly ProbeParameter Repeat = P("L", "Repeat", "1 repeats the whole probe from the newly found centre.");
    private static readonly ProbeParameter EdgeRetract = P("R", "Edge retract", "Retract distance from the edge for the double-tap probing.");
    private static readonly ProbeParameter Angle = P("Q", "Angle", "Angle of the feature in degrees.");
    private static readonly ProbeParameter BottomRetract = P("C", "Bottom retract", "With Pocket depth set: how far to retract off the bottom surface (default 2 mm).");
    private static readonly ProbeParameter TipDiameter = P("D", "Tip diameter", "Probe tip diameter.");
    private static readonly ProbeParameter NormallyClosed = P("I", "Normally closed", "1 if the probe is normally closed.");
    private static readonly ProbeParameter ProbeDepth = P("E", "Depth", "How far below the top surface to move down to probe each side.");
    private static readonly ProbeParameter Clearance = P("J", "Probe clearance", "Added to X and Y when moving outside a boss.");

    private static ProbeParameter ZeroXy(string description, string def = "1") => P("S", "Zero XY", description, false, def);

    private static ProbeOperation Op(string family, string id, string title, string command, string[]? required = null, string[]? omit = null, string[]? negate = null, string? note = null) =>
        new() { Family = family, Id = id, Title = title, Command = command, Required = required ?? [], Omit = omit ?? [], Negate = negate ?? [], Note = note };

    private static ProbeFamily Corners(string id, string title, string command, string description)
    {
        ProbeOperation C(string suffix, string name, bool negX, bool negY) =>
            Op(id, suffix, $"{title} - {name}", command, required: ["X", "Y"], negate: [.. (negX ? new[] { "X" } : []), .. (negY ? new[] { "Y" } : [])]);
        return new ProbeFamily
        {
            Id = id, Title = title, Description = description,
            Parameters = [P("X", "X distance", "Distance along X to probe.", true), P("Y", "Y distance", "Distance along Y to probe.", true),
                PocketDepth, FastFeed, Rapid, Repeat, EdgeRetract, BottomRetract,
                ZeroXy("Save the corner as the new work zero: 1 = X and Y, 2 = X, Y and Z, 0 = do nothing."), TipDiameter, ProbeDepth, NormallyClosed, Angle],
            Operations = [C("TopLeft", "top left", false, true), C("TopRight", "top right", true, true), C("BottomRight", "bottom right", true, false), C("BottomLeft", "bottom left", false, false)],
        };
    }

    public static readonly IReadOnlyList<ProbeFamily> Families =
    [
        new ProbeFamily
        {
            Id = "singleAxis", Title = "Single axis", Description = "Probe one edge or the top of the workpiece (M466).",
            Parameters = [X, Y, Z, TipDiameter, Angle, FastFeed, Repeat, EdgeRetract, ZeroXy("Save the touched position as zero: 2 = the probed axis.", "2"), NormallyClosed],
            Operations =
            [
                Op("singleAxis", "Top", "Top side (Y-)", "M466", required: ["Y"], omit: ["X", "Z"], negate: ["Y"]),
                Op("singleAxis", "Left", "Left side (X+)", "M466", required: ["X"], omit: ["Y", "Z"]),
                Op("singleAxis", "WorkpieceTop", "Workpiece top (Z)", "M466", required: ["Z"], omit: ["X", "Y"], negate: ["Z"]),
                Op("singleAxis", "Right", "Right side (X-)", "M466", required: ["X"], omit: ["Y", "Z"], negate: ["X"]),
                Op("singleAxis", "Bottom", "Bottom side (Y+)", "M466", required: ["Y"], omit: ["X", "Z"]),
            ],
        },
        Corners("outsideCorner", "Outside corner", "M464", "Find the corner of a block (M464)."),
        Corners("insideCorner", "Inside corner", "M463", "Find the corner of a pocket (M463)."),
        new ProbeFamily
        {
            Id = "bore", Title = "Bore / pocket", Description = "Find the centre of a hole or pocket (M461).",
            Parameters = [X, Y, PocketDepth, FastFeed, Rapid, Repeat, EdgeRetract, Angle, BottomRetract, ZeroXy("Save the centre as the new work zero in X and Y."), TipDiameter, NormallyClosed],
            Operations =
            [
                Op("bore", "CenterX", "Centre X", "M461", required: ["X"], omit: ["Y"]),
                Op("bore", "CenterY", "Centre Y", "M461", required: ["Y"], omit: ["X"]),
                Op("bore", "CenterBore", "Centre of bore", "M461", required: ["X", "Y"]),
                Op("bore", "CenterPocket", "Centre of pocket", "M461", required: ["X", "Y"]),
            ],
        },
        new ProbeFamily
        {
            Id = "boss", Title = "Boss", Description = "Find the centre of a boss or pin (M462).",
            Parameters = [P("X", "X diameter", "Boss diameter along X."), P("Y", "Y diameter", "Boss diameter along Y."), PocketDepth, FastFeed, Rapid, Repeat, EdgeRetract, Angle, BottomRetract,
                ZeroXy("Save the centre as the new work zero in X and Y."), TipDiameter, ProbeDepth, NormallyClosed, Clearance],
            Operations =
            [
                Op("boss", "CenterX", "Centre X", "M462", required: ["X"], omit: ["Y"]),
                Op("boss", "CenterY", "Centre Y", "M462", required: ["Y"], omit: ["X"]),
                Op("boss", "CenterBoss", "Centre of boss", "M462", required: ["X", "Y"]),
                Op("boss", "CenterPocket", "Centre of pocket", "M462", required: ["X", "Y"]),
            ],
        },
        new ProbeFamily
        {
            Id = "angle", Title = "Angle", Description = "Measure the angle of an edge (M465).",
            Parameters = [X, Y, PocketDepth, FastFeed, Rapid, Repeat, EdgeRetract, Angle, BottomRetract, ZeroXy("Save the result as the new work zero in X and Y."), TipDiameter,
                P("E", "Probe depth", "How far below the top surface to move down to probe each side."), NormallyClosed, P("V", "Visualise", "Visualise distance.")],
            Operations =
            [
                Op("angle", "XBelow", "X below", "M465", required: ["X", "E"], omit: ["Y"]),
                Op("angle", "XAbove", "X above", "M465", required: ["X", "E"], omit: ["Y"], negate: ["E"]),
                Op("angle", "YLeft", "Y left", "M465", required: ["Y", "E"], omit: ["X"]),
                Op("angle", "YRight", "Y right", "M465", required: ["Y", "E"], omit: ["X"], negate: ["E"]),
                Op("angle", "ArbitraryNegative", "Arbitrary negative", "M465", required: ["X", "E"], omit: ["Y"], negate: ["E"]),
                Op("angle", "ArbitraryPositive", "Arbitrary positive", "M465", required: ["X", "E"], omit: ["Y"]),
            ],
        },
        new ProbeFamily
        {
            Id = "probeTip", Title = "Probe tip", Description = "Measure the probe tip's diameter (M460.x).",
            Parameters = [X, Y, PocketDepth, FastFeed, Rapid, Repeat, EdgeRetract, Angle, BottomRetract, ZeroXy("Save the result as the new work zero in X and Y.", ""),
                P("E", "Probe depth", "How far below the top surface to move down to probe each side."), NormallyClosed, Clearance],
            Operations =
            [
                Op("probeTip", "Bore", "Bore", "M460.1", required: ["X"], omit: ["Y"]),
                Op("probeTip", "BossX", "Boss X", "M460.2", required: ["X"], omit: ["Y"]),
                Op("probeTip", "BossY", "Boss Y", "M460.2", required: ["Y"], omit: ["X"]),
                Op("probeTip", "Anchor2", "Anchor 2", "M460.3"),
            ],
        },
        new ProbeFamily
        {
            Id = "calibration", Title = "Calibration", Description = "Calibrate the 4th axis and the anchors (M469.x).",
            Parameters = [X, Y, PocketDepth, P("E", "Y probe depth", "Depth of the Y side probing."), P("R", "Pin diameter", "Diameter of the calibration pin."), ZeroXy("Save the result as the new work zero.", ""),
                TipDiameter, NormallyClosed, P("C", "Y axis clearance", "Clearance in Y.")],
            Operations =
            [
                Op("calibration", "FourthY", "4th axis Y", "M469.4", omit: ["X", "R", "C"], note: "Make sure the 4th axis and the 3-axis probe are installed."),
                Op("calibration", "FourthZ", "4th axis Z", "M469.5", required: ["X"], omit: ["Y", "E"], note: "Make sure the 4th axis is on and has a pin in the chuck."),
                Op("calibration", "Anchor1", "Anchor 1", "M469.1", omit: ["X", "Y", "R", "H", "E", "C"], note: "Make sure anchor 1 and the 3-axis probe are installed."),
                Op("calibration", "Anchor2", "Anchor 2", "M469.2", omit: ["X", "Y", "R", "H", "E", "C"], note: "Make sure anchor 2 and the 3-axis probe are installed."),
            ],
        },
        new ProbeFamily
        {
            Id = "fourthAxis", Title = "4th axis stock", Description = "Find the position of stock in the 4th axis (M465.1).",
            Parameters = [P("Y", "Y probing distance", "Total probing distance; the machine moves to +Y/2 and -Y/2 from the current position.", true), P("H", "Probe height", "Distance to probe down from the current position.", true),
                P("F", "Feed rate", "Probing feed rate (mm/min)."), P("K", "Rapid rate", "Rapid feed rate for positioning (mm/min)."), P("L", "Repeat", "Number of probe cycles."),
                P("R", "Retract", "Retract distance from the touched surface (mm)."), P("V", "Rotate A after probe", "1 rotates the A axis after probing.", false, "1"), P("S", "Save A offset", "1 saves the A axis offset after probing.", false, "1")],
            Operations = [Op("fourthAxis", "Stock", "Probe 4th axis stock", "M465.1", required: ["Y", "H"])],
        },
    ];

    public static ProbeFamily? FindFamily(string id) => Families.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static ProbeOperation? FindOperation(string familyId, string operationId) =>
        FindFamily(familyId)?.Operations.FirstOrDefault(o => o.Id.Equals(operationId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The default values a family starts with (only parameters that have one).</summary>
    public static Dictionary<string, string> Defaults(ProbeFamily family) =>
        family.Parameters.Where(p => p.Default.Length > 0).ToDictionary(p => p.Code, p => p.Default);

    public static ProbeResult Build(string familyId, string operationId, IReadOnlyDictionary<string, string> config)
    {
        var family = FindFamily(familyId) ?? throw new ArgumentException($"Unknown probing family '{familyId}'.");
        var operation = FindOperation(familyId, operationId) ?? throw new ArgumentException($"Unknown probing operation '{operationId}' in {family.Title}.");
        return operation.Build(config, family.Parameters);
    }
}
