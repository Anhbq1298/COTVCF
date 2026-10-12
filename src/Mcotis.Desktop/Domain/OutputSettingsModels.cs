using System.Collections.ObjectModel;

namespace Mcotis.Desktop.Domain;

public static class OutputUnits
{
    public const string Force = "kN";
    public const string Moment = "kNm";
    public const string Displacement = "mm";
}

public static class OutputGroupNaming
{
    public const string Prefix = "[O]-";
    public const string DeflectionMonitor = Prefix + "DeflectionMonitorPts";
    public const string FoundationLeft = Prefix + "FoundationLeft";
    public const string FoundationRight = Prefix + "FoundationRight";
    public const string CableMonitor = Prefix + "CableMonitor";

    public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(
        [DeflectionMonitor, FoundationLeft, FoundationRight, CableMonitor],
        StringComparer.OrdinalIgnoreCase);

    public static string MemberGroupName(string suffix)
    {
        var value = (suffix ?? string.Empty).Trim();
        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) value = value[Prefix.Length..].Trim();
        return Prefix + value;
    }
}

/// <summary>Deflection acceptance window on a candidate's peak downward deflection δ = −min(Uz) [mm, + = sag].
/// A candidate passes when Min ≤ δ ≤ Max. A blank bound is open (Max only = the usual serviceability cap);
/// both blank = no deflection criterion. Post-processing only — never part of the run fingerprint.</summary>
public sealed record DeflectionLimitBand(double? MinMm, double? MaxMm)
{
    public bool IsActive => MinMm.HasValue || MaxMm.HasValue;

    public bool Contains(double peakDownMm) =>
        (MinMm is not double lo || peakDownMm >= lo) && (MaxMm is not double hi || peakDownMm <= hi);

    /// <summary>First problem with the bounds, or null when they are usable.</summary>
    public string? Problem() =>
        MinMm is double lo && (!double.IsFinite(lo) || lo < 0) ? "Deflection limit band Min must be a finite value ≥ 0 mm." :
        MaxMm is double hi && (!double.IsFinite(hi) || hi < 0) ? "Deflection limit band Max must be a finite value ≥ 0 mm." :
        MinMm is double min && MaxMm is double max && max < min ? "Deflection limit band Max must be greater than or equal to Min." :
        null;

    public string Label => (MinMm, MaxMm) switch
    {
        (double lo, double hi) => $"{Mm(lo)}–{Mm(hi)} mm",
        (null, double hi) => $"≤ {Mm(hi)} mm",
        (double lo, null) => $"≥ {Mm(lo)} mm",
        _ => "none",
    };

    /// <summary>Peak downward deflection δ = −min(Uz) over <paramref name="uzMm"/>, or null when empty.</summary>
    public static double? PeakDownMm(IEnumerable<double> uzMm)
    {
        double? lowest = null;
        foreach (var uz in uzMm) if (lowest is null || uz < lowest) lowest = uz;
        return lowest is double value ? (value == 0 ? 0 : -value) : null;
    }

    private static string Mm(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

public enum OutputGroupObjectKind { Point, Frame, Cable }

public sealed record OutputSapGroupDefinition(
    string Name,
    OutputGroupObjectKind ObjectKind,
    IReadOnlyList<string> ObjectNames);

public sealed record SapGroupSnapshot(
    string Name,
    bool Exists,
    IReadOnlyList<string> PointNames,
    IReadOnlyList<string> FrameNames,
    IReadOnlyList<string> CableNames)
{
    public IReadOnlyList<string> Names(OutputGroupObjectKind kind) => kind switch
    {
        OutputGroupObjectKind.Point => PointNames,
        OutputGroupObjectKind.Frame => FrameNames,
        OutputGroupObjectKind.Cable => CableNames,
        _ => []
    };
}

public sealed class MemberForceOutputGroup : Observable
{
    private string name = "";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name
    {
        get => name;
        set
        {
            var next = (value ?? "").Trim();
            if (next.StartsWith(OutputGroupNaming.Prefix, StringComparison.OrdinalIgnoreCase))
                next = next[OutputGroupNaming.Prefix.Length..].Trim();
            if (Set(ref name, next)) Notify(nameof(SapGroupName));
        }
    }
    public string SapGroupName => OutputGroupNaming.MemberGroupName(Name);
    public ObservableCollection<string> Members { get; } = [];
}

public sealed class OutputSettingsConfiguration : Observable
{
    private string cableResultLoadCase = "";
    private string deflectionOutputCaseName = "";
    private string memberForceResultLoadCase = "";
    private string foundationReactionResultLoadCase = "";
    private double? deflectionLimitMinMm, deflectionLimitMaxMm;
    private bool deflectionCriterionEnabled = true, cableCriterionEnabled = true;
    private double maxCableUtilization = 1.0;
    public ObservableCollection<string> DeflectionPoints { get; } = [];
    public ObservableCollection<MemberForceOutputGroup> MemberForceGroups { get; } = [];
    public ObservableCollection<string> FoundationLeftPoints { get; } = [];
    public ObservableCollection<string> FoundationRightPoints { get; } = [];
    public ObservableCollection<string> CableNames { get; } = [];
    public string CableResultLoadCase { get => cableResultLoadCase; set => Set(ref cableResultLoadCase, (value ?? "").Trim()); }
    /// <summary>SAP analysis load case the fixed U3/Global-Z deflection output is read under. Separate from
    /// <see cref="CableResultLoadCase"/>. Empty until the user picks one — never defaulted to the first case.</summary>
    public string DeflectionOutputCaseName { get => deflectionOutputCaseName; set => Set(ref deflectionOutputCaseName, (value ?? "").Trim()); }
    /// <summary>SAP analysis load case or combo used for all configured frame-force groups.</summary>
    public string MemberForceResultLoadCase { get => memberForceResultLoadCase; set => Set(ref memberForceResultLoadCase, (value ?? "").Trim()); }
    /// <summary>SAP analysis load case or combo used for global support reactions at the selected foundations.</summary>
    public string FoundationReactionResultLoadCase { get => foundationReactionResultLoadCase; set => Set(ref foundationReactionResultLoadCase, (value ?? "").Trim()); }
    /// <summary>Deflection limit band on the peak downward U3 [mm] — drives the Deflection report band and the
    /// Tab 06A deflection criterion. Post-processing only: editing it never invalidates analysed candidates.</summary>
    public double? DeflectionLimitMinMm { get => deflectionLimitMinMm; set => Set(ref deflectionLimitMinMm, value); }
    public double? DeflectionLimitMaxMm { get => deflectionLimitMaxMm; set => Set(ref deflectionLimitMaxMm, value); }
    public DeflectionLimitBand DeflectionLimitBand => new(DeflectionLimitMinMm, DeflectionLimitMaxMm);
    /// <summary>Tab 06A qualification criteria: whether the deflection limit band and the cable-force limit are
    /// applied, and the largest allowed cable force / design capacity (1.00 = 100 %). Post-processing only, like
    /// the band: editing them never invalidates analysed candidates.</summary>
    public bool DeflectionCriterionEnabled { get => deflectionCriterionEnabled; set => Set(ref deflectionCriterionEnabled, value); }
    public bool CableCriterionEnabled { get => cableCriterionEnabled; set => Set(ref cableCriterionEnabled, value); }
    public double MaxCableUtilization { get => maxCableUtilization; set => Set(ref maxCableUtilization, value); }

    public bool IsConfigured => DeflectionPoints.Count > 0 || MemberForceGroups.Count > 0 ||
        FoundationLeftPoints.Count > 0 || FoundationRightPoints.Count > 0 || CableNames.Count > 0 ||
        !string.IsNullOrWhiteSpace(CableResultLoadCase) || !string.IsNullOrWhiteSpace(DeflectionOutputCaseName) ||
        !string.IsNullOrWhiteSpace(MemberForceResultLoadCase) || !string.IsNullOrWhiteSpace(FoundationReactionResultLoadCase) ||
        DeflectionLimitBand.IsActive;

    public IReadOnlyList<OutputSapGroupDefinition> ToSapGroups()
    {
        var groups = new List<OutputSapGroupDefinition>();
        if (DeflectionPoints.Count > 0)
            groups.Add(new(OutputGroupNaming.DeflectionMonitor, OutputGroupObjectKind.Point, DeflectionPoints.ToArray()));
        foreach (var group in MemberForceGroups)
            if (!string.IsNullOrWhiteSpace(group.Name) && group.Members.Count > 0)
                groups.Add(new(group.SapGroupName, OutputGroupObjectKind.Frame, group.Members.ToArray()));
        if (FoundationLeftPoints.Count > 0)
            groups.Add(new(OutputGroupNaming.FoundationLeft, OutputGroupObjectKind.Point, FoundationLeftPoints.ToArray()));
        if (FoundationRightPoints.Count > 0)
            groups.Add(new(OutputGroupNaming.FoundationRight, OutputGroupObjectKind.Point, FoundationRightPoints.ToArray()));
        if (CableNames.Count > 0)
            groups.Add(new(OutputGroupNaming.CableMonitor, OutputGroupObjectKind.Cable, CableNames.ToArray()));
        return groups;
    }
}
