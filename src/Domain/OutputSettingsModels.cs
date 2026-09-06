using System.Collections.ObjectModel;

namespace COT_VCF.Domain;

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
    public ObservableCollection<string> DeflectionPoints { get; } = [];
    public ObservableCollection<MemberForceOutputGroup> MemberForceGroups { get; } = [];
    public ObservableCollection<string> FoundationLeftPoints { get; } = [];
    public ObservableCollection<string> FoundationRightPoints { get; } = [];
    public ObservableCollection<string> CableNames { get; } = [];
    public string CableResultLoadCase { get => cableResultLoadCase; set => Set(ref cableResultLoadCase, (value ?? "").Trim()); }

    public bool IsConfigured => DeflectionPoints.Count > 0 || MemberForceGroups.Count > 0 ||
        FoundationLeftPoints.Count > 0 || FoundationRightPoints.Count > 0 || CableNames.Count > 0 ||
        !string.IsNullOrWhiteSpace(CableResultLoadCase);

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
