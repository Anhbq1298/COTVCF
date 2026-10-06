using System.Globalization;

namespace COT_VCF.Application.Results;

/// <summary>Report-side reading of a Top Chord, Bottom Chord or Mega Column member-force group: the governing
/// axial force of each iteration and, for a chord, one governing point per physical station. Derived from the
/// stored SAP station rows only — no smoothing, no interpolation, nothing written back — so every plotted value
/// traces to a source <c>P</c>.</summary>
public static class MemberGroupSummary
{
    public const string Top = "top", Bottom = "bottom", MegaColumn = "mega";

    /// <summary>Stations whose profile coordinates agree to this many decimals [m] are the same physical station
    /// (two frame elements meeting at a node each report it).</summary>
    public const int StationDecimals = 4;

    /// <summary><see cref="Top"/> / <see cref="Bottom"/> for a member-force group named as a chord (TopChord,
    /// Top Chords, [O]-BottomChord …), <see cref="MegaColumn"/> for one named as mega columns (MegaColumns,
    /// Mega Column …), otherwise null — the group keeps the generic views.</summary>
    public static string? Kind(string? groupName)
    {
        var name = new string((groupName ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (name.Contains("mega", StringComparison.Ordinal) && name.Contains("col", StringComparison.Ordinal)) return MegaColumn;
        if (!name.Contains("chord", StringComparison.Ordinal)) return null;
        if (name.Contains("top", StringComparison.Ordinal) || name.Contains("upper", StringComparison.Ordinal)) return Top;
        if (name.Contains("bottom", StringComparison.Ordinal) || name.Contains("btm", StringComparison.Ordinal) ||
            name.Contains("lower", StringComparison.Ordinal)) return Bottom;
        return null;
    }

    public static string Title(string kind) => kind switch { Top => "Top Chord", Bottom => "Bottom Chord", _ => "Mega Column" };

    /// <summary>Whether the group has a By Station view. A chord is one line of members, so a position along it
    /// is a physical station. A mega-column group is a set of separate columns: a position along "the group" means
    /// nothing, so no station is plotted or reported for it — only which column governs.</summary>
    public static bool HasStations(string kind) => kind != MegaColumn;

    /// <summary>What governs: tension P (as stored) in the bottom chord; compression magnitude |P| in the top
    /// chord and the mega columns.</summary>
    public static double Measure(string kind, double p) => kind == Bottom ? p : Math.Abs(p);

    /// <summary>One iteration's result over every valid station and result step. An iteration without a valid
    /// station has no governing value and an empty profile — never a zero.</summary>
    public static MemberGroupSummaryCandidateDto Summarise(string kind, string iterationKey, IReadOnlyList<MemberForceStationDto> stations)
    {
        var valid = stations
            .Where(s => s.Status == "Valid" && double.IsFinite(s.P) && double.IsFinite(s.ProfileCoordinateM))
            .OrderBy(s => s.ProfileCoordinateM).ThenBy(s => s.FrameSequence).ThenBy(s => s.ElementStationM)
            .ToArray();
        if (valid.Length == 0) return new(iterationKey, null, null, null, null, null, []);

        var governing = valid.MaxBy(s => Measure(kind, s.P))!;      // first of equals: the left-most station
        if (!HasStations(kind))
            return new(iterationKey, governing.P, governing.FrameName, governing.ElementName, null, StepLabel(governing), []);

        var profile = valid.GroupBy(s => Math.Round(s.ProfileCoordinateM, StationDecimals))
            .OrderBy(station => station.Key)
            .Select(station => new[] { station.Key, Math.Round(station.MaxBy(s => Measure(kind, s.P))!.P, 3) })
            .ToArray();
        return new(iterationKey, governing.P, governing.FrameName, governing.ElementName,
            Math.Round(governing.ProfileCoordinateM, StationDecimals), StepLabel(governing), profile);
    }

    /// <summary>Same wording as the report's Result step selector.</summary>
    public static string StepLabel(MemberForceStationDto station) =>
        $"{(string.IsNullOrEmpty(station.StepType) ? "Static" : station.StepType)} · {station.StepNumber.ToString("0.###", CultureInfo.InvariantCulture)}";
}
