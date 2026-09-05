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
public record ValidationIssue(string Section, string Message);
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
public sealed class TrussHeightConfiguration : Observable
{
    public IReadOnlyList<string> TopPoints { get; set; } = [];
    public IReadOnlyList<string> MiddlePoints { get; set; } = [];
    public IReadOnlyList<string> BottomPoints { get; set; } = [];
    private double baseline, current, minimum, maximum, increment = 1;
    public double BaselineHeight { get => baseline; set => Set(ref baseline, value); }
    public double CurrentHeight { get => current; set => Set(ref current, value); }
    public double MinimumHeight { get => minimum; set => Set(ref minimum, value); }
    public double MaximumHeight { get => maximum; set => Set(ref maximum, value); }
    public double Increment { get => increment; set => Set(ref increment, value); }
}
public sealed class CableForceConfiguration : Observable
{
    public string ObjectName { get; init; } = "";
    public MemberKind Kind { get; init; }
    public string Source { get; init; } = "";
    public double? CurrentForce { get; init; }
    public string? ReadProblem { get; init; }
    private double minimum, maximum, increment = 1, target;
    public double MinimumForce { get => minimum; set { Set(ref minimum, value); Notify(nameof(Status)); } }
    public double MaximumForce { get => maximum; set { Set(ref maximum, value); Notify(nameof(Status)); } }
    public double Increment { get => increment; set { Set(ref increment, value); Notify(nameof(Status)); } }
    public double TargetForce { get => target; set { Set(ref target, value); Notify(nameof(Status)); } }
    public string Status => ReadProblem ?? (CurrentForce is null ? "No force source" : "Configured");
}
public sealed class ApplicationState : Observable
{
    private ModelInfo? model;
    private GeometryModel geometry = GeometryModel.Empty, baseline = GeometryModel.Empty, preview = GeometryModel.Empty;
    private string connection = "Disconnected";
    private double tributaryWidthB = 1;
    public ModelInfo? Model { get => model; set { Set(ref model, value); Notify(nameof(Units)); } }
    public string Connection { get => connection; set { Set(ref connection, value); Notify(nameof(IsSapConnected)); } }
    public bool IsSapConnected => Connection == "Connected";
    public string Units => Model?.Units ?? "Units unavailable";
    public GeometryModel Geometry { get => geometry; set => Set(ref geometry, value); }
    public GeometryModel Baseline { get => baseline; set => Set(ref baseline, value); }
    public GeometryModel Preview { get => preview; set => Set(ref preview, value); }
    public ObservableCollection<string> LoadPatterns { get; } = [];
    public ObservableCollection<PointLoadConfiguration> PointLoads { get; } = [];
    public ObservableCollection<AppliedPoint> AppliedPoints { get; } = [];
    public ObservableCollection<LoadDefinition> LoadDefinitions { get; } =
    [
        new() { LoadName = "DL_Above" },
        new() { LoadName = "LL_Above" }
    ];
    public double TributaryWidthB { get => tributaryWidthB; set => Set(ref tributaryWidthB, value); }
    public ObservableCollection<CableForceConfiguration> Cables { get; } = [];
    public TrussHeightConfiguration Truss { get; } = new();
    public bool BaselineCaptured { get; set; }
    public double CommittedHeight { get; set; }
}
