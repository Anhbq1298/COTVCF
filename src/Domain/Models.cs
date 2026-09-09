using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace COT_VCF.Domain;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name); return true;
    }
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public readonly record struct Position(double X, double Y, double Z)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
}
public record StructuralPoint(string Name, Position Position);
public enum MemberKind { Frame, Cable, Tendon }
public record StructuralMember(string Name, string Start, string End, MemberKind Kind);
public record GeometryModel(IReadOnlyDictionary<string, StructuralPoint> Points, IReadOnlyList<StructuralMember> Members)
{
    public static GeometryModel Empty { get; } = new(new Dictionary<string, StructuralPoint>(), []);
}
public record ModelInfo(string Path, string Version, int UnitsCode, string ForceUnit, string LengthUnit, bool IsLocked)
{
    public string Name => string.IsNullOrWhiteSpace(Path) ? "Unsaved model" : System.IO.Path.GetFileName(Path);
    public string Units => $"{ForceUnit} / {LengthUnit}";
}
public record ModelSnapshot(ModelInfo Info, GeometryModel Geometry, IReadOnlyList<string> LoadPatterns);
public record PointLoad(string PointName, string LoadPattern, double Fx, double Fy, double Fz);
public record CableTargetForce(string CableName, string LoadPattern, double TargetForce, double RelativeDistance);
/// <summary>One cable's assigned section property and cross-section area [m²], read from SAP.
/// <see cref="AreaM2"/> is 0 with a <see cref="Problem"/> when the section could not be read.</summary>
public record CableSection(string CableName, double AreaM2, string? PropertyName = null, string? Problem = null);
public record ForceSource(string ObjectName, MemberKind Kind, string Source, double? Force, string? Problem = null);
public enum ValidationSeverity { Error, Warning, Info }
public record ValidationIssue(string Section, string Message, ValidationSeverity Severity = ValidationSeverity.Error);
public sealed record LoadingCandidate(string AppliedPt, string Case, double Qz, double TribArea, int NStories, double FzKn);
public sealed record LoadingCandidatePreview(int CaseCount, IReadOnlyList<LoadingCandidate> Rows);
public sealed record TrussCandidate(int Index, double Height, double DeltaH, string Reference, double TopDz, double MidDz, double BotDz);
public sealed class AppliedPoint : Observable
{
    private double x, y, z;
    private int order;
    private bool isSelected;
    private string status = "Selected";
    public required string PointName { get; init; }
    public double X { get => x; set => Set(ref x, value); }
    public double Y { get => y; set => Set(ref y, value); }
    public double Z { get => z; set => Set(ref z, value); }
    public int Order { get => order; set => Set(ref order, value); }
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
    public string Status { get => status; set => Set(ref status, value); }
}
public sealed class LoadDefinition : Observable, IDataErrorInfo
{
    public const int MaterializeCeiling = 1_000_000;
    private string loadName = "";
    private bool applyIncrementStep;
    private double? qz = 0, minQz, maxQz, incrementStep;
    public string LoadName { get => loadName; set => Set(ref loadName, value); }
    public bool ApplyIncrementStep
    {
        get => applyIncrementStep;
        set
        {
            if (!Set(ref applyIncrementStep, value)) return;
            Notify(nameof(IsSingleValue));
            Notify(nameof(CandidateCount));
            Notify(nameof(CandidateValues));
            Notify(nameof(Qz));
            Notify(nameof(MinQz));
            Notify(nameof(MaxQz));
            Notify(nameof(IncrementStep));
        }
    }
    public bool IsSingleValue => !ApplyIncrementStep;
    public double? Qz { get => qz; set { if (Set(ref qz, value)) { Notify(nameof(CandidateCount)); Notify(nameof(CandidateValues)); } } }
    public double? MinQz { get => minQz; set { if (Set(ref minQz, value)) { Notify(nameof(MaxQz)); Notify(nameof(CandidateCount)); Notify(nameof(CandidateValues)); } } }
    public double? MaxQz { get => maxQz; set { if (Set(ref maxQz, value)) { Notify(nameof(CandidateCount)); Notify(nameof(CandidateValues)); } } }
    public double? IncrementStep { get => incrementStep; set { if (Set(ref incrementStep, value)) { Notify(nameof(CandidateCount)); Notify(nameof(CandidateValues)); } } }

    /// <summary>Allocation-free candidate cardinality. Values above the materialization ceiling are retained
    /// as a count so callers can reject the range before allocating it.</summary>
    public BigInteger CandidateCount
    {
        get
        {
            if (!ApplyIncrementStep)
                return Qz is double single && double.IsFinite(single) && single >= 0 ? BigInteger.One : BigInteger.Zero;
            if (MinQz is not double min || MaxQz is not double max || IncrementStep is not double step ||
                !double.IsFinite(min) || !double.IsFinite(max) || !double.IsFinite(step) || min < 0 || max < min || step <= 0)
                return BigInteger.Zero;
            var intervals = Math.Floor((max - min) / step + 1e-10);
            if (!double.IsFinite(intervals)) return new BigInteger(MaterializeCeiling) + 1;
            return intervals < 0 ? BigInteger.Zero : new BigInteger(intervals) + 1;
        }
    }

    public IReadOnlyList<double> CandidateValues
    {
        get
        {
            var count = CandidateCount;
            if (count <= 0) return [];
            if (count > MaterializeCeiling)
                throw new InvalidOperationException($"{(string.IsNullOrWhiteSpace(LoadName) ? "Loading range" : LoadName)}: {count:N0} candidate values exceed the {MaterializeCeiling:N0} materialization limit. Widen the step.");
            if (!ApplyIncrementStep) return [Qz!.Value];
            var min = MinQz!.Value;
            var step = IncrementStep!.Value;
            return Enumerable.Range(0, (int)count).Select(i => min + i * step).ToArray();
        }
    }
    string IDataErrorInfo.Error => "";
    public string this[string columnName] => columnName switch
    {
        nameof(Qz) when IsSingleValue && (Qz is not double value || !double.IsFinite(value) || value < 0) => "qz must be zero or greater.",
        nameof(MinQz) when ApplyIncrementStep && (MinQz is not double value || !double.IsFinite(value) || value < 0) => "Minimum qz must be zero or greater.",
        nameof(MaxQz) when ApplyIncrementStep && (MaxQz is not double value || !double.IsFinite(value) || MinQz is not double min || value < min) => "Maximum qz must be greater than or equal to minimum qz.",
        nameof(IncrementStep) when ApplyIncrementStep && (IncrementStep is not double value || !double.IsFinite(value) || value <= 0) => "Increment step must be greater than zero.",
        nameof(IncrementStep) when ApplyIncrementStep && CandidateCount > MaterializeCeiling => $"Range exceeds {MaterializeCeiling:N0} values. Widen the step.",
        _ => ""
    };
}
public sealed class PointLoadConfiguration : Observable
{
    public string PointName { get; init; } = "";
    private string pattern = "", status = "Modified";
    private double fx, fy, fz;
    public string LoadPattern { get => pattern; set { if (Set(ref pattern, value)) Status = "Modified"; } }
    public double Fx { get => fx; set { if (Set(ref fx, value)) Status = "Modified"; } }
    public double Fy { get => fy; set { if (Set(ref fy, value)) Status = "Modified"; } }
    public double Fz { get => fz; set { if (Set(ref fz, value)) Status = "Modified"; } }
    public string Status { get => status; set => Set(ref status, value); }
    public PointLoad ToLoad() => new(PointName, LoadPattern, Fx, Fy, Fz);
}
public enum ReferenceChord { Top, Middle, Bottom }
public sealed class TrussHeightConfiguration : Observable
{
    public IReadOnlyList<string> TopPoints { get; set; } = [];
    public IReadOnlyList<string> MiddlePoints { get; set; } = [];
    public IReadOnlyList<string> BottomPoints { get; set; } = [];
    private double baseline, current, minimum, maximum, increment = 1;
    private ReferenceChord reference = ReferenceChord.Top;
    public double BaselineHeight { get => baseline; set { if (Set(ref baseline, value)) NotifyCandidates(); } }
    public double CurrentHeight { get => current; set => Set(ref current, value); }
    public double MinimumHeight { get => minimum; set { if (Set(ref minimum, value)) NotifyCandidates(); } }
    public double MaximumHeight { get => maximum; set { if (Set(ref maximum, value)) NotifyCandidates(); } }
    public double Increment { get => increment; set { if (Set(ref increment, value)) NotifyCandidates(); } }
    // Which chord stays fixed while candidate heights are generated. Top reproduces the original behaviour.
    public ReferenceChord Reference { get => reference; set => Set(ref reference, value); }
    private void NotifyCandidates() { Notify(nameof(CandidateCount)); Notify(nameof(CandidateHeights)); Notify(nameof(LandsOnMaximum)); }
    // CandidateCount = floor((Max - Min) / Step) + 1, guarded and capped.
    public int CandidateCount
    {
        get
        {
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || !double.IsFinite(increment) || increment <= 0 || maximum < minimum) return 0;
            var steps = Math.Floor((maximum - minimum) / increment + 1e-9);
            if (!double.IsFinite(steps) || steps < 0) return 0;
            return steps > 1_000_000 ? 1_000_001 : (int)steps + 1;
        }
    }
    public IReadOnlyList<double> CandidateHeights
    {
        get
        {
            var n = CandidateCount;
            return n <= 0 ? [] : Enumerable.Range(0, n).Select(i => minimum + i * increment).ToArray();
        }
    }
    // Whether the last candidate lands exactly on MaximumHeight.
    public bool LandsOnMaximum
    {
        get
        {
            var n = CandidateCount;
            return n > 0 && Math.Abs((minimum + (n - 1) * increment) - maximum) <= 1e-6 * Math.Max(1, Math.Abs(maximum));
        }
    }
}
// One selected SAP2000 cable and its candidate PRETENSION target-force definition (Fixed or Range).
// Editing this never touches SAP2000 — values are candidate definitions only.
public sealed record PretensionCombination(long CombinationIndex, IReadOnlyDictionary<string, double> CableForces);

/// <summary>How a cable's pretension candidates relate to another cable. Every non-Independent mode is a
/// <i>follower</i>: it adds no Cartesian axis and is zipped to its reference by candidate index.</summary>
public enum CableSyncMode
{
    /// <summary>Own range / fixed value; one axis of the pretension Cartesian product.</summary>
    Independent,
    /// <summary>Follows the exact candidate values of the reference cable.</summary>
    SameValues,
    /// <summary>Keeps its own Min / Max end points, but its spacing is derived so it lands on exactly the
    /// reference's number of values (candidate j pairs with the reference's candidate j).</summary>
    SameStep,
    /// <summary>Follower value j = reference value j × <see cref="CablePretensionConfig.SyncFactor"/> (k).</summary>
    SameRatio,
    /// <summary>Follower value j = reference value j + <see cref="CablePretensionConfig.SyncFactor"/> (Δ, kN).</summary>
    SameOffset
}

public static class CableSyncModeText
{
    public static string Label(this CableSyncMode m) => m switch
    {
        CableSyncMode.SameValues => "Same Values",
        CableSyncMode.SameStep => "Same Step",
        CableSyncMode.SameRatio => "Same Ratio",
        CableSyncMode.SameOffset => "Same Offset",
        _ => "Independent"
    };
}

/// <summary>Pure dependency-graph helpers over a set of cable configs. A follower chain (C follows B follows
/// an Independent A) is resolved transitively; a broken or cyclic chain resolves to nothing.</summary>
public static class CableSyncGraph
{
    public static string? ReferenceOf(CablePretensionConfig c) =>
        c.SyncMode == CableSyncMode.Independent || string.IsNullOrEmpty(c.ReferenceCableName) ? null : c.ReferenceCableName;

    public static CablePretensionConfig? DirectReference(CablePretensionConfig c, IReadOnlyList<CablePretensionConfig> all) =>
        ReferenceOf(c) is { } name
            ? all.FirstOrDefault(o => !ReferenceEquals(o, c) && o.CableName.Equals(name, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>The Independent cable at the end of <paramref name="c"/>'s reference chain, or null when the
    /// chain is broken (missing reference) or cyclic.</summary>
    public static CablePretensionConfig? RootOf(CablePretensionConfig c, IReadOnlyList<CablePretensionConfig> all)
    {
        var seen = new HashSet<CablePretensionConfig>();
        var node = c;
        while (node is not null)
        {
            if (!seen.Add(node)) return null;                 // cycle
            if (node.SyncMode == CableSyncMode.Independent) return node;
            node = DirectReference(node, all);
        }
        return null;                                          // dangling reference
    }

    public static bool IsFollower(CablePretensionConfig c, IReadOnlyList<CablePretensionConfig> all) =>
        c.SyncMode != CableSyncMode.Independent && RootOf(c, all) is { } r && !ReferenceEquals(r, c);

    /// <summary>The resolved candidate force list for a follower — its direct reference's resolved values with
    /// this cable's follower rule applied. Independent cables just return their own <c>GetCandidateForces()</c>.</summary>
    public static IReadOnlyList<double> ResolvedForces(CablePretensionConfig c, IReadOnlyList<CablePretensionConfig> all)
        => ResolvedForces(c, all, []);

    private static IReadOnlyList<double> ResolvedForces(
        CablePretensionConfig c, IReadOnlyList<CablePretensionConfig> all, HashSet<CablePretensionConfig> seen)
    {
        if (c.SyncMode == CableSyncMode.Independent) return c.GetCandidateForces();
        if (!seen.Add(c)) return [];                          // cycle guard
        var reference = DirectReference(c, all);
        if (reference is null) return [];
        var refForces = ResolvedForces(reference, all, seen);
        if (refForces.Count == 0) return [];
        return c.SyncMode switch
        {
            CableSyncMode.SameValues  => refForces,
            CableSyncMode.SameStep    => c.GetCandidateForces(refForces.Count),
            CableSyncMode.SameRatio   => refForces.Select(v => v * (c.SyncFactor ?? 1.0)).ToArray(),
            CableSyncMode.SameOffset  => refForces.Select(v => v + (c.SyncFactor ?? 0.0)).ToArray(),
            _ => refForces
        };
    }
}

public sealed class CablePretensionConfig : Observable, IDataErrorInfo
{
    public const string PretensionLoadCase = "PRETENSION";
    public const int MaterializeCeiling = 1_000_000;

    public required string CableName { get; init; }
    private string loadPattern = PretensionLoadCase;
    public string LoadPattern
    {
        get => loadPattern;
        set
        {
            if (!Set(ref loadPattern, value)) return;
            Notify(nameof(LoadCase));
            Notify(nameof(Status));
        }
    }
    // Version-1.0 code/config compatibility alias. Cable target force is assigned to a load pattern.
    public string LoadCase { get => LoadPattern; set => LoadPattern = value; }

    private bool applyRange, missing;
    private double? targetForce = 0, minForce, maxForce, stepForce, syncFactor;
    private CableSyncMode syncMode = CableSyncMode.Independent;
    private string? referenceCableName;

    /// <summary>Set once by the view-model so a row can resolve its Sync reference / preview / problem against
    /// its siblings. Null in isolation (unit tests pass an explicit list instead).</summary>
    public Func<IReadOnlyList<CablePretensionConfig>>? Peers { get; set; }
    private IReadOnlyList<CablePretensionConfig> PeerList => Peers?.Invoke() ?? [this];

    /// <summary>Independent / Same Values / Same Step. See <see cref="CableSyncMode"/>. Setting Independent
    /// clears <see cref="ReferenceCableName"/>.</summary>
    public CableSyncMode SyncMode
    {
        get => syncMode;
        set
        {
            if (!Set(ref syncMode, value)) return;
            if (value == CableSyncMode.Independent && referenceCableName is not null)
            { referenceCableName = null; Notify(nameof(ReferenceCableName)); }
            NotifySyncState();
        }
    }
    /// <summary>The independent cable this one follows (for Same Values / Same Step). Never itself.</summary>
    public string? ReferenceCableName
    {
        get => referenceCableName;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v is not null && string.Equals(v, CableName, StringComparison.OrdinalIgnoreCase)) v = null;
            if (!Set(ref referenceCableName, v)) return;
            NotifySyncState();
        }
    }
    /// <summary>k for Same Ratio (default 1), Δ [kN] for Same Offset (default 0). Ignored by other modes.</summary>
    public double? SyncFactor { get => syncFactor; set { if (Set(ref syncFactor, value)) NotifySyncState(); } }

    private void NotifySyncState()
    {
        Notify(nameof(IsIndependent)); Notify(nameof(IsFollower)); Notify(nameof(NotFollower));
        Notify(nameof(IsSameStepFollower)); Notify(nameof(SyncSummary)); Notify(nameof(SyncProblem)); Notify(nameof(SyncPreview));
        Notify(nameof(CanEditTarget)); Notify(nameof(CanEditRange)); Notify(nameof(CanEditStepForce));
        Notify(nameof(CanEditFactor)); Notify(nameof(CanToggleRange)); Notify(nameof(CanPickReference));
        Notify(nameof(CandidateCount)); Notify(nameof(CandidateSummary)); Notify(nameof(Status));
    }
    /// <summary>Re-raise the sibling-dependent Sync properties — call on every row when any row's Sync changes.</summary>
    public void RaiseSyncRecompute()
    { Notify(nameof(SyncProblem)); Notify(nameof(SyncPreview)); Notify(nameof(SyncSummary)); Notify(nameof(CandidateCount)); Notify(nameof(Status)); }

    public bool IsIndependent => syncMode == CableSyncMode.Independent;
    public bool IsSameStepFollower => syncMode == CableSyncMode.SameStep && !string.IsNullOrEmpty(referenceCableName);
    /// <summary>Any non-Independent mode with a reference set — contributes no independent axis.</summary>
    public bool IsFollower => syncMode != CableSyncMode.Independent && !string.IsNullOrEmpty(referenceCableName);
    public bool NotFollower => !IsFollower;

    public bool CanEditTarget => IsFixed && !IsFollower;
    /// <summary>Min / Max are editable for an Independent cable and a Same Step follower (its own end points).</summary>
    public bool CanEditRange => applyRange && syncMode is CableSyncMode.Independent or CableSyncMode.SameStep;
    /// <summary>Step Force is only entered for an Independent cable — a Same Step follower's spacing is derived.</summary>
    public bool CanEditStepForce => CanEditRange && syncMode == CableSyncMode.Independent;
    public bool CanEditFactor => syncMode is CableSyncMode.SameRatio or CableSyncMode.SameOffset;
    public bool CanToggleRange => syncMode is CableSyncMode.Independent or CableSyncMode.SameStep;
    public bool CanPickReference => syncMode != CableSyncMode.Independent;

    /// <summary>Compact grid text for the Sync column.</summary>
    public string SyncSummary => syncMode switch
    {
        CableSyncMode.Independent => "Independent",
        _ when string.IsNullOrEmpty(referenceCableName) => $"{syncMode.Label()} · pick cable",
        CableSyncMode.SameValues => $"= {referenceCableName}",
        CableSyncMode.SameStep => $"Step · {referenceCableName}",
        CableSyncMode.SameRatio => $"× {(syncFactor ?? 1.0):0.###} · {referenceCableName}",
        CableSyncMode.SameOffset => $"{(syncFactor ?? 0.0):+0.###;-0.###;0} · {referenceCableName}",
        _ => syncMode.Label()
    };

    /// <summary>"" when the Sync config is fine; otherwise a short message (drives the grid red-flag).</summary>
    public string SyncProblem
    {
        get
        {
            if (syncMode == CableSyncMode.Independent) return "";
            if (string.IsNullOrEmpty(referenceCableName)) return "Pick a reference cable.";
            var all = PeerList;
            if (CableSyncGraph.DirectReference(this, all) is null) return $"Reference cable '{referenceCableName}' does not exist.";
            if (CableSyncGraph.RootOf(this, all) is null) return "Circular Sync reference.";
            if (CanEditFactor && (syncFactor is not double f || !double.IsFinite(f) || (syncMode == CableSyncMode.SameRatio && f <= 0)))
                return syncMode == CableSyncMode.SameRatio ? "Ratio must be greater than zero." : "Enter an offset value.";
            if (syncMode == CableSyncMode.SameStep && CandidateCount <= 0) return "Enter this cable's own Min and Max force.";
            return "";
        }
    }

    /// <summary>The resolved candidate forces this follower would apply, e.g. "10000 · 13000 · 16000". "" for
    /// an Independent cable or a broken chain.</summary>
    public string SyncPreview
    {
        get
        {
            if (syncMode == CableSyncMode.Independent) return "";
            var v = CableSyncGraph.ResolvedForces(this, PeerList);
            return v.Count == 0 ? "" : string.Join("  ·  ", v.Take(12).Select(x => x.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)))
                + (v.Count > 12 ? "  …" : "");
        }
    }

    public bool ApplyRange
    {
        get => applyRange;
        set
        {
            if (!Set(ref applyRange, value)) return;
            Notify(nameof(IsFixed));
            Notify(nameof(TargetForce)); Notify(nameof(MinForce)); Notify(nameof(MaxForce)); Notify(nameof(StepForce));
            Notify(nameof(CanEditTarget)); Notify(nameof(CanEditRange)); Notify(nameof(CanEditStepForce));
            Notify(nameof(CandidateCount)); Notify(nameof(CandidateSummary)); Notify(nameof(Status));
            Notify(nameof(SyncProblem)); Notify(nameof(SyncPreview));
        }
    }
    public bool IsFixed => !applyRange;
    public bool Missing { get => missing; set { if (Set(ref missing, value)) Notify(nameof(Status)); } }

    public double? TargetForce { get => targetForce; set { if (Set(ref targetForce, value)) Recount(); } }
    public double? MinForce { get => minForce; set { if (Set(ref minForce, value)) { Notify(nameof(MaxForce)); Recount(); } } }
    public double? MaxForce { get => maxForce; set { if (Set(ref maxForce, value)) Recount(); } }
    public double? StepForce { get => stepForce; set { if (Set(ref stepForce, value)) Recount(); } }
    private void Recount() { Notify(nameof(CandidateCount)); Notify(nameof(CandidateSummary)); Notify(nameof(Status)); Notify(nameof(SyncPreview)); Notify(nameof(SyncProblem)); }

    /// <summary>Grid display for the Candidates column: "linked" (Same Values), "lockstep" (Same Step),
    /// "×k" / "±Δ" (Same Ratio / Offset), else the independent candidate count.</summary>
    public string CandidateSummary => syncMode switch
    {
        CableSyncMode.Independent => CandidateCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        CableSyncMode.SameValues => "linked",
        CableSyncMode.SameStep => "lockstep",
        CableSyncMode.SameRatio => $"×{(syncFactor ?? 1.0):0.###}",
        CableSyncMode.SameOffset => $"{(syncFactor ?? 0.0):+0.###;-0.###;0}",
        _ => "linked"
    };

    // Efficient count — never allocates the value list (spec: do not materialise just to count).
    public int CandidateCount
    {
        get
        {
            // A follower has no independent count — its value count comes from its reference at enumeration
            // time. Report 1 when its own inputs are valid (Same Step needs Min/Max), else 0. Followers are
            // excluded from the Cartesian product by PretensionCandidates.Leaders().
            if (syncMode == CableSyncMode.SameStep && !string.IsNullOrEmpty(referenceCableName))
                return minForce is double lo && maxForce is double hi && double.IsFinite(lo) && double.IsFinite(hi) && hi >= lo ? 1 : 0;
            if (IsFollower) return 1;
            if (!applyRange)
                return targetForce is double t && double.IsFinite(t) ? 1 : 0;
            if (minForce is not double min || maxForce is not double max || stepForce is not double step
                || !double.IsFinite(min) || !double.IsFinite(max) || !double.IsFinite(step) || step <= 0 || max < min)
                return 0;
            var n = Math.Floor((max - min) / step + 1e-6) + 1;
            return n < 1 ? 0 : n > int.MaxValue ? int.MaxValue : (int)n;
        }
    }

    public string Status => Missing ? "Missing in SAP"
        : string.IsNullOrWhiteSpace(loadPattern) ? "Invalid"
        : CandidateCount > 0 ? "Configured" : "Invalid";

    /// <summary>Candidate target forces for this cable: [TargetForce] in Fixed mode, or Min, Min+Step, … ≤ Max in Range mode.
    /// Materialised only on demand; never appends an off-grid Max.</summary>
    public IReadOnlyList<double> GetCandidateForces()
    {
        // A Same Step follower has no step of its own — its spacing is set at enumeration time from the
        // reference's value count. Return its end points as a reasonable stand-alone fallback.
        if (IsSameStepFollower)
            return minForce is double lo && maxForce is double hi ? (lo == hi ? [lo] : [lo, hi]) : [];
        var count = CandidateCount;
        if (count <= 0) return [];
        if (count > MaterializeCeiling) throw new InvalidOperationException($"{CableName}: {count:N0} candidate forces exceed the {MaterializeCeiling:N0} limit. Widen the step.");
        if (!applyRange) return [targetForce!.Value];
        double min = minForce!.Value, step = stepForce!.Value;
        return Enumerable.Range(0, count).Select(i => min + i * step).ToArray();
    }

    /// <summary>The <paramref name="count"/> evenly-spaced force values from Min to Max inclusive — used by a
    /// Same Step follower, whose count is taken from its reference cable.</summary>
    public IReadOnlyList<double> GetCandidateForces(int count)
    {
        if (count <= 0 || minForce is not double lo || maxForce is not double hi || !double.IsFinite(lo) || !double.IsFinite(hi)) return [];
        if (count == 1) return [lo];
        var step = (hi - lo) / (count - 1);
        return Enumerable.Range(0, count).Select(i => i == count - 1 ? hi : lo + i * step).ToArray();
    }

    private double? sectionAreaM2, designCapacityKn;
    private string? sectionName;
    /// <summary>SAP cable-section property assigned to this cable (e.g. "109T15.7"). Read-only: retrieved
    /// straight from the model on + Add Cable and never editable in the app.</summary>
    public string? SectionName { get => sectionName; set => Set(ref sectionName, value); }
    /// <summary>Cross-section area [m²] read straight from the SAP cable section on + Add Cable. Read-only in
    /// the app. Kept so the resolved capacity can be recomputed when the global strength / φ change.</summary>
    public double? SectionAreaM2 { get => sectionAreaM2; set => Set(ref sectionAreaM2, value); }
    /// <summary>Resolved cable design capacity [kN] — <c>area × strength × 1000 × φ</c>, or a hand-entered
    /// override. Null when no capacity has been resolved; the cable report then has no utilisation view for
    /// this cable and falls back to "% of study max".</summary>
    public double? DesignCapacityKN { get => designCapacityKn; set => Set(ref designCapacityKn, value); }
    /// <summary>Capacity [kN] implied by this cable's section area and the supplied global strength [MPa] / φ,
    /// or null when the area is unknown or non-positive.</summary>
    public double? ResolveCapacity(double strengthMpa, double phi) =>
        sectionAreaM2 is { } a && double.IsFinite(a) && a > 0 && double.IsFinite(strengthMpa) && strengthMpa > 0 &&
        double.IsFinite(phi) && phi is > 0 and <= 1
            ? a * strengthMpa * 1000.0 * phi : null;

    string IDataErrorInfo.Error => "";
    public string this[string columnName] => columnName switch
    {
        nameof(TargetForce) when IsFixed && (TargetForce is not double v || !double.IsFinite(v)) => "Enter a target force.",
        nameof(MinForce) when ApplyRange && (MinForce is not double v || !double.IsFinite(v)) => "Enter a minimum force.",
        nameof(MaxForce) when ApplyRange && (MaxForce is not double v || !double.IsFinite(v) || MinForce is not double min || v < min) => "Max force must be greater than or equal to Min force.",
        nameof(StepForce) when CanEditStepForce && (StepForce is not double v || !double.IsFinite(v) || v <= 0) => "Step force must be greater than zero.",
        nameof(DesignCapacityKN) when DesignCapacityKN is double cap && (!double.IsFinite(cap) || cap <= 0) => "Design capacity must be greater than zero.",
        _ => ""
    };
}
// Tab 05 study mode: isolate one design variable before running the full interaction.
public enum IterationStudyMode { FixPretension, FixHeight, Combined }
public sealed class IterationStudyConfig : Observable
{
    private IterationStudyMode mode = IterationStudyMode.FixPretension;
    private string fixedHeightCandidateId = "H01", fixedPretensionCandidateId = "P01", outputDirectory = "";
    private bool useCandidateFilters;
    private double? minLoadingKpa, maxLoadingKpa, minHeightM, maxHeightM, minPretensionKn, maxPretensionKn;
    private double? maxLoadingToHeightRatio, maxPretensionToHeightRatio;
    private double? deflectionPassLimitMm = 50, deflectionFailLimitMm = 75;
    private double? minimumCableForceKn, maximumCableForceKn;
    private double decisionWarningUtilization = 0.9;
    private double deflectionRankWeight = 0.5, pretensionRankWeight = 0.3, heightRankWeight = 0.2;
    private readonly HashSet<string> excludedIterationKeys = new(StringComparer.Ordinal);
    public IterationStudyMode Mode { get => mode; set => Set(ref mode, value); }
    public string FixedHeightCandidateId { get => fixedHeightCandidateId; set => Set(ref fixedHeightCandidateId, value ?? ""); }
    public string FixedPretensionCandidateId { get => fixedPretensionCandidateId; set => Set(ref fixedPretensionCandidateId, value ?? ""); }
    public string OutputDirectory { get => outputDirectory; set => Set(ref outputDirectory, value?.Trim() ?? ""); }
    public bool UseCandidateFilters { get => useCandidateFilters; set => Set(ref useCandidateFilters, value); }
    public double? MinLoadingKpa { get => minLoadingKpa; set => Set(ref minLoadingKpa, value); }
    public double? MaxLoadingKpa { get => maxLoadingKpa; set => Set(ref maxLoadingKpa, value); }
    public double? MinHeightM { get => minHeightM; set => Set(ref minHeightM, value); }
    public double? MaxHeightM { get => maxHeightM; set => Set(ref maxHeightM, value); }
    public double? MinPretensionKn { get => minPretensionKn; set => Set(ref minPretensionKn, value); }
    public double? MaxPretensionKn { get => maxPretensionKn; set => Set(ref maxPretensionKn, value); }
    /// <summary>Optional cross-dimension predicate: max |qz| / height must not exceed this kPa/m value.</summary>
    public double? MaxLoadingToHeightRatio { get => maxLoadingToHeightRatio; set => Set(ref maxLoadingToHeightRatio, value); }
    /// <summary>Optional cross-dimension predicate: max |target force| / height must not exceed this kN/m value.</summary>
    public double? MaxPretensionToHeightRatio { get => maxPretensionToHeightRatio; set => Set(ref maxPretensionToHeightRatio, value); }
    public double? DeflectionPassLimitMm { get => deflectionPassLimitMm; set => Set(ref deflectionPassLimitMm, value); }
    public double? DeflectionFailLimitMm { get => deflectionFailLimitMm; set => Set(ref deflectionFailLimitMm, value); }
    public double? MinimumCableForceKn { get => minimumCableForceKn; set => Set(ref minimumCableForceKn, value); }
    public double? MaximumCableForceKn { get => maximumCableForceKn; set => Set(ref maximumCableForceKn, value); }
    public double DecisionWarningUtilization { get => decisionWarningUtilization; set => Set(ref decisionWarningUtilization, value); }
    public double DeflectionRankWeight { get => deflectionRankWeight; set => Set(ref deflectionRankWeight, value); }
    public double PretensionRankWeight { get => pretensionRankWeight; set => Set(ref pretensionRankWeight, value); }
    public double HeightRankWeight { get => heightRankWeight; set => Set(ref heightRankWeight, value); }
    public IReadOnlyCollection<string> ExcludedIterationKeys => excludedIterationKeys;

    public bool IsRunSelected(string iterationKey) => !excludedIterationKeys.Contains(iterationKey);

    public void SetRunSelected(string iterationKey, bool selected)
    {
        if (selected) excludedIterationKeys.Remove(iterationKey);
        else excludedIterationKeys.Add(iterationKey);
    }

    public void RestoreExcludedIterationKeys(IEnumerable<string>? keys)
    {
        excludedIterationKeys.Clear();
        foreach (var key in keys ?? [])
            if (!string.IsNullOrWhiteSpace(key)) excludedIterationKeys.Add(key);
        Notify(nameof(ExcludedIterationKeys));
    }
}
public sealed class ApplicationState : Observable
{
    private ModelInfo? model;
    private GeometryModel geometry = GeometryModel.Empty, baseline = GeometryModel.Empty, preview = GeometryModel.Empty;
    private ModelInfo? sessionBaselineModel;
    private GeometryModel sessionBaseline = GeometryModel.Empty;
    private IReadOnlyList<PointLoad> sessionBaselinePointLoads = [];
    private IReadOnlyDictionary<string, double> sessionBaselinePretensionForces = new Dictionary<string, double>(StringComparer.Ordinal);
    private string connection = "Disconnected";
    private double tributaryWidthB = 1, numberOfStories = 1;
    private double cableStrengthMpa = 1860, cableStrengthReductionFactor = 0.5;
    public ModelInfo? Model { get => model; set { Set(ref model, value); Notify(nameof(Units)); } }
    public string Connection { get => connection; set { Set(ref connection, value); Notify(nameof(IsSapConnected)); } }
    public bool IsSapConnected => Connection == "Connected";
    public string Units => Model?.Units ?? "Units unavailable";
    public GeometryModel Geometry { get => geometry; set => Set(ref geometry, value); }
    public GeometryModel Baseline { get => baseline; set => Set(ref baseline, value); }
    public GeometryModel Preview { get => preview; set => Set(ref preview, value); }
    public ModelInfo? SessionBaselineModel { get => sessionBaselineModel; internal set { if (Set(ref sessionBaselineModel, value)) Notify(nameof(SessionBaselineCaptured)); } }
    public GeometryModel SessionBaseline { get => sessionBaseline; internal set { if (Set(ref sessionBaseline, value)) Notify(nameof(SessionBaselineCaptured)); } }
    public IReadOnlyList<PointLoad> SessionBaselinePointLoads { get => sessionBaselinePointLoads; internal set => Set(ref sessionBaselinePointLoads, value); }
    public IReadOnlyDictionary<string, double> SessionBaselinePretensionForces { get => sessionBaselinePretensionForces; internal set => Set(ref sessionBaselinePretensionForces, value); }
    public bool SessionBaselineCaptured => SessionBaselineModel is not null && SessionBaseline.Points.Count > 0;
    public ObservableCollection<string> LoadPatterns { get; } = [];
    public ObservableCollection<PointLoadConfiguration> PointLoads { get; } = [];
    public ObservableCollection<AppliedPoint> AppliedPoints { get; } = [];
    public ObservableCollection<LoadDefinition> LoadDefinitions { get; } =
    [
        new() { LoadName = "DL_Above" },
        new() { LoadName = "LL_Above" }
    ];
    public double TributaryWidthB { get => tributaryWidthB; set => Set(ref tributaryWidthB, value); }
    public double NumberOfStories { get => numberOfStories; set => Set(ref numberOfStories, value); }
    public ObservableCollection<CablePretensionConfig> Cables { get; } = [];
    /// <summary>Global cable material strength [MPa] (Grade 1860 strand by default) used with
    /// <see cref="CableStrengthReductionFactor"/> to resolve each cable's design capacity from its section area.</summary>
    public double CableStrengthMpa { get => cableStrengthMpa; set => Set(ref cableStrengthMpa, value); }
    /// <summary>Global strength-reduction coefficient φ (default 0.5) applied to <c>area × strength</c>.</summary>
    public double CableStrengthReductionFactor { get => cableStrengthReductionFactor; set => Set(ref cableStrengthReductionFactor, value); }
    public TrussHeightConfiguration Truss { get; } = new();
    public OutputSettingsConfiguration OutputSettings { get; } = new();
    public IterationStudyConfig Study { get; } = new();
    public bool BaselineCaptured { get; set; }
    public double CommittedHeight { get; set; }
}
