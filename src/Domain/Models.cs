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
    public ModelInfo? Model { get => model; set { Set(ref model, value); Notify(nameof(Units)); } }
    public string Connection { get => connection; set { Set(ref connection, value); Notify(nameof(IsSapConnected)); } }
    public bool IsSapConnected => Connection == "Connected";
    public string Units => Model?.Units ?? "Units unavailable";
    public GeometryModel Geometry { get => geometry; set => Set(ref geometry, value); }
    public GeometryModel Baseline { get => baseline; set => Set(ref baseline, value); }
    public GeometryModel Preview { get => preview; set => Set(ref preview, value); }
    public ObservableCollection<string> LoadPatterns { get; } = [];
    public ObservableCollection<PointLoadConfiguration> PointLoads { get; } = [];
    public ObservableCollection<CableForceConfiguration> Cables { get; } = [];
    public TrussHeightConfiguration Truss { get; } = new();
    public bool BaselineCaptured { get; set; }
    public double CommittedHeight { get; set; }
}
