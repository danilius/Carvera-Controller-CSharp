namespace Carvera.Core.Protocol;

/// <summary>Names for tool numbers and automatic-tool-changer activity, shared by layouts and pendants.</summary>
public static class ToolInfo
{
    public static string Label(int tool) => tool switch
    {
        0 => "Probe",
        8888 => "Laser",
        >= 999990 and <= 999999 => "3D Probe",
        > 0 => $"T{tool}",
        _ => "No Tool",
    };

    /// <summary>Plain-language description of the "A" (ATC) field of a status report; empty when nothing is happening.</summary>
    public static string AtcLabel(int atcState) => atcState switch
    {
        0 => "",
        1 or 2 or 3 => "Changing tool",
        5 => "Probing",
        6 => "Auto-levelling",
        _ => $"ATC state {atcState}",
    };
}
