using System.Collections.ObjectModel;
using System.ComponentModel;
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
            Notify(nameof(CandidateValues));
            Notify(nameof(Qz));
            Notify(nameof(MinQz));
            Notify(nameof(MaxQz));
            Notify(nameof(IncrementStep));
        }
    }
    public bool IsSingleValue => !ApplyIncrementStep;
    public double? Qz { get => qz; set { if (Set(ref qz, value)) Notify(nameof(CandidateValues)); } }
    public double? MinQz { get => minQz; set { if (Set(ref minQz, value)) { Notify(nameof(MaxQz)); Notify(nameof(CandidateValues)); } } }
    public double? MaxQz { get => maxQz; set { if (Set(ref maxQz, value)) Notify(nameof(CandidateValues)); } }
    public double? IncrementStep { get => incrementStep; set { if (Set(ref incrementStep, value)) Notify(nameof(CandidateValues)); } }
    public IReadOnlyList<double> CandidateValues
    {
        get
        {
            if (!ApplyIncrementStep)
                return Qz is double single && double.IsFinite(single) && single >= 0 ? [single] : [];
            if (MinQz is not double min || MaxQz is not double max || IncrementStep is not double step ||
                !double.IsFinite(min) || !double.IsFinite(max) || !double.IsFinite(step) || min < 0 || max < min || step <= 0)
                return [];
            var intervals = (int)Math.Floor((max - min) / step + 1e-10);
            return Enumerable.Range(0, intervals + 1).Select(i => min + i * step).ToArray();
        }
    }
    string IDataErrorInfo.Error => "";
    public string this[string columnName] => columnName switch
    {
        nameof(Qz) when IsSingleValue && (Qz is not double value || !double.IsFinite(value) || value < 0) => "qz must be zero or greater.",
        nameof(MinQz) when ApplyIncrementStep && (MinQz is not double value || !double.IsFinite(value) || value < 0) => "Minimum qz must be zero or greater.",
        nameof(MaxQz) when ApplyIncrementStep && (MaxQz is not double value || !double.IsFinite(value) || MinQz is not double min || value < min) => "Maximum qz must be greater than or equal to minimum qz.",
        nameof(IncrementStep) when ApplyIncrementStep && (IncrementStep is not double value || !double.IsFinite(value) || value <= 0) => "Increment step must be greater than zero.",
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
    private double? targetForce = 0, minForce, maxForce, stepForce;

    public bool ApplyRange
    {
        get => applyRange;
        set
        {
            if (!Set(ref applyRange, value)) return;
            Notify(nameof(IsFixed));
            Notify(nameof(TargetForce)); Notify(nameof(MinForce)); Notify(nameof(MaxForce)); Notify(nameof(StepForce));
            Notify(nameof(CandidateCount)); Notify(nameof(Status));
        }
    }
    public bool IsFixed => !applyRange;
    public bool Missing { get => missing; set { if (Set(ref missing, value)) Notify(nameof(Status)); } }

    public double? TargetForce { get => targetForce; set { if (Set(ref targetForce, value)) Recount(); } }
    public double? MinForce { get => minForce; set { if (Set(ref minForce, value)) { Notify(nameof(MaxForce)); Recount(); } } }
    public double? MaxForce { get => maxForce; set { if (Set(ref maxForce, value)) Recount(); } }
    public double? StepForce { get => stepForce; set { if (Set(ref stepForce, value)) Recount(); } }
    private void Recount() { Notify(nameof(CandidateCount)); Notify(nameof(Status)); }

    // Efficient count — never allocates the value list (spec: do not materialise just to count).
    public int CandidateCount
    {
        get
        {
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
        var count = CandidateCount;
        if (count <= 0) return [];
        if (count > MaterializeCeiling) throw new InvalidOperationException($"{CableName}: {count:N0} candidate forces exceed the {MaterializeCeiling:N0} limit. Widen the step.");
        if (!applyRange) return [targetForce!.Value];
        double min = minForce!.Value, step = stepForce!.Value;
        return Enumerable.Range(0, count).Select(i => min + i * step).ToArray();
    }

    string IDataErrorInfo.Error => "";
    public string this[string columnName] => columnName switch
    {
        nameof(TargetForce) when IsFixed && (TargetForce is not double v || !double.IsFinite(v)) => "Enter a target force.",
        nameof(MinForce) when ApplyRange && (MinForce is not double v || !double.IsFinite(v)) => "Enter a minimum force.",
        nameof(MaxForce) when ApplyRange && (MaxForce is not double v || !double.IsFinite(v) || MinForce is not double min || v < min) => "Max force must be greater than or equal to Min force.",
        nameof(StepForce) when ApplyRange && (StepForce is not double v || !double.IsFinite(v) || v <= 0) => "Step force must be greater than zero.",
        _ => ""
    };
}
// Tab 05 study mode: isolate one design variable before running the full interaction.
public enum IterationStudyMode { FixPretension, FixHeight, Combined }
public sealed class IterationStudyConfig : Observable
{
    private IterationStudyMode mode = IterationStudyMode.FixPretension;
    private string fixedHeightCandidateId = "H01", fixedPretensionCandidateId = "P01", outputDirectory = "";
    private readonly HashSet<string> excludedIterationKeys = new(StringComparer.Ordinal);
    public IterationStudyMode Mode { get => mode; set => Set(ref mode, value); }
    public string FixedHeightCandidateId { get => fixedHeightCandidateId; set => Set(ref fixedHeightCandidateId, value ?? ""); }
    public string FixedPretensionCandidateId { get => fixedPretensionCandidateId; set => Set(ref fixedPretensionCandidateId, value ?? ""); }
    public string OutputDirectory { get => outputDirectory; set => Set(ref outputDirectory, value?.Trim() ?? ""); }
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
    public TrussHeightConfiguration Truss { get; } = new();
    public IterationStudyConfig Study { get; } = new();
    public bool BaselineCaptured { get; set; }
    public double CommittedHeight { get; set; }
}
