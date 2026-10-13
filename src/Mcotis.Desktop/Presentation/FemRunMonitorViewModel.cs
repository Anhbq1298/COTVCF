using System.Collections.ObjectModel;
using System.Globalization;
using Mcotis.Analysis;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

public enum FemCellLevel { Normal, Muted, Warning, Severe, Infinite }

/// <summary>One degree-of-freedom cell of the instability table.</summary>
public sealed record FemDofCell(string Text, FemCellLevel Level, string ToolTip);

/// <summary>One affected joint: its name, its global coordinates [m], its six degrees of freedom and what is reported of them.</summary>
public sealed record FemInstabilityRow(string Joint, double X, double Y, double Z,
    FemDofCell UX, FemDofCell UY, FemDofCell UZ, FemDofCell RX, FemDofCell RY, FemDofCell RZ, string Remarks);

public sealed record FemRunField(string Label, string Value);

/// <summary>One load case of the run monitor's list. Everything it shows is what <see cref="FemRunMonitor"/> holds of
/// the case; nothing is worked out here but its wording.</summary>
public sealed class FemRunCaseViewModel : Observable
{
    private static readonly CultureInfo Plain = CultureInfo.InvariantCulture;
    private FemRunCase? shown;
    private bool isExpanded, opened;
    private string elapsedText = "";

    public int Number { get; private set; }
    public string Name { get; private set; } = "";
    public FemCaseStatus Status { get; private set; }
    public string StatusText { get; private set; } = "";
    public string StatusGlyph { get; private set; } = "";
    /// <summary>What the header of the case reads to a screen reader.</summary>
    public string Summary => $"Load case {Number}, {Name}: {StatusText}";
    public string ElapsedText { get => elapsedText; private set => Set(ref elapsedText, value); }
    public bool IsExpanded { get => isExpanded; set => Set(ref isExpanded, value); }

    public string ConvergenceText { get; private set; } = "";
    public string StabilityText { get; private set; } = "";
    public string InstabilitiesText { get; private set; } = "";
    public FemCellLevel StabilityLevel { get; private set; }
    public string? FailureText { get; private set; }
    public bool HasFailure => !string.IsNullOrEmpty(FailureText);
    public IReadOnlyList<FemRunField> Details { get; private set; } = [];
    public IReadOnlyList<FemInstabilityRow> Rows { get; private set; } = [];
    public bool HasRows => Rows.Count > 0;
    public string TableTitle { get; private set; } = "";
    public string TableNote { get; private set; } = "";
    public string LogText { get; private set; } = "";

    private static string Hms(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";

    /// <summary>The time a running case has taken so far.</summary>
    internal void Tick(DateTime now)
    {
        if (shown is { Status: FemCaseStatus.Running, Started: { } started }) ElapsedText = Hms(now - started);
    }

    internal void Apply(FemRunCase item, DateTime now)
    {
        if (ReferenceEquals(shown, item)) return;
        shown = item;
        Number = item.Number;
        Name = item.Label;
        Status = item.Status;
        (StatusGlyph, StatusText) = item.Status switch
        {
            FemCaseStatus.Pending => ("○", "Pending"),
            FemCaseStatus.Running => ("…", item.Last is { FitRun: > 0 } fitting ? $"Running — fitting run {fitting.FitRun}" : "Running"),
            FemCaseStatus.Converged => ("✓", "Converged"),
            FemCaseStatus.ConvergedWithWarnings => ("▲", "Converged with Warnings"),
            FemCaseStatus.NotConverged => ("✕", "Not Converged"),
            FemCaseStatus.NumericalInstability => ("✕", "Numerical Instability"),
            FemCaseStatus.Failed => ("✕", "Failed"),
            _ => ("–", "Cancelled"),
        };
        elapsedText = item is { Started: { } started } ? Hms((item.Ended ?? now) - started) : "";

        var last = item.Last;
        var stability = item.Stability;
        bool solved = item.Status is FemCaseStatus.Converged or FemCaseStatus.ConvergedWithWarnings;
        ConvergenceText = item.Status switch
        {
            FemCaseStatus.Pending => "Pending",
            FemCaseStatus.Running => "In progress",
            FemCaseStatus.Cancelled => "Not completed",
            FemCaseStatus.NotConverged => "Not converged",
            _ when solved => "Passed",
            _ => "No result",
        };
        StabilityText = stability is null
            ? item.Status is FemCaseStatus.Pending or FemCaseStatus.Running ? "—" : "Not assessed"
            : FemStabilityText.Summary(stability);
        StabilityLevel = stability switch
        {
            null or { Passed: true } => FemCellLevel.Normal,
            { Instabilities: > 0 } => FemCellLevel.Severe,
            _ => FemCellLevel.Warning,
        };
        InstabilitiesText = stability is null ? "—" : stability.Instabilities.ToString("N0", Plain);
        FailureText = item.Note ?? (solved ? null
            : last?.Failure is { } failure ? (last.PrerequisiteFailed ? "The cable target-force fit this case continues from failed. " : "") + failure : null);
        Details = Describe(item, last, stability, now);
        Rows = stability is null ? [] : stability.Joints.Select(joint => Row(joint, stability)).ToArray();
        TableTitle = stability is null || Rows.Count == 0 ? ""
            : $"NUMERICAL STABILITY — {Rows.Count.ToString("N0", Plain)} AFFECTED {(Rows.Count == 1 ? "JOINT" : "JOINTS")}";
        TableNote = stability is null || Rows.Count == 0 ? "" : Note(stability);
        LogText = string.Join(Environment.NewLine, item.Log.Select(entry => $"{entry.Time.ToString("HH:mm:ss", Plain)}  {entry.Text}"));
        Notify(string.Empty);
        // a case that failed opens itself, once; nothing is ever closed here
        if (!opened && item.Status is FemCaseStatus.NotConverged or FemCaseStatus.NumericalInstability or FemCaseStatus.Failed)
        {
            opened = true;
            IsExpanded = true;
        }
    }

    private static IReadOnlyList<FemRunField> Describe(FemRunCase item, FemEquilibriumProgress? last, FemStabilityReport? stability, DateTime now)
    {
        var fields = new List<FemRunField>();
        void Add(string label, string value) => fields.Add(new(label, value));
        static string Number(double value) => value.ToString("0.00E+00", Plain);
        if (last is null) return fields;
        bool measured = last.OutOfBalance is not null, ended = last.State != FemEquilibriumState.Running;
        Add("Analysis type", item.FitStage ? "Nonlinear static, cable forces fitted" : !measured ? "Static" : last.Linear ? "Linear static" : "Nonlinear static");
        if (last.SolvedDofs > 0) Add("Equations solved", last.SolvedDofs.ToString("N0", Plain));
        if (measured && last.Linear)
            Add("Scaled backward error", $"{Number(last.OutOfBalance!.Value)} (accepted {Number(last.Tolerance!.Value)})");
        else if (measured)
        {
            bool within = last.OutOfBalance <= last.Tolerance;
            Add("Nonlinear convergence", !ended ? "Iterating" : last.State == FemEquilibriumState.Converged ? "Converged" : within ? "Converged, result withheld" : "Not converged");
            Add("Iterations", last.Iteration.ToString("N0", Plain) + (ended ? "" : " so far"));
            Add("Convergence tolerance", within ? "Satisfied" : "Not satisfied");
            Add("Out-of-balance", $"{Number(last.OutOfBalance!.Value)} {(last.WorstDof?.StartsWith('R') == true ? "kN·m" : "kN")} (accepted {Number(last.Tolerance!.Value)} kN)");
            Add("Joints in equilibrium", $"{last.JointsInEquilibrium.ToString("N0", Plain)} of {last.Joints.ToString("N0", Plain)}" +
                (last.JointsInEquilibrium < last.Joints && last.WorstJoint is { } joint ? $" — largest out-of-balance at {joint} {last.WorstDof}" : ""));
            Add("Load steps", "1 (loads applied in one step)");
        }
        if (last.FitRun > 0) Add(ended ? "Fitting runs" : "Fitting run", last.FitRun.ToString("N0", Plain));
        if (last.Outcome is { } outcome)
        {
            if (outcome.FitDeviationPercent is { } deviation)
                Add("Cable target forces", outcome.FitMet ? $"Met — largest deviation {deviation.ToString("0.######", Plain)} %"
                    : $"Not met — closest pretension used, largest deviation {deviation.ToString("0.###", Plain)} %");
            Add("Smallest scaled pivot", Number(outcome.SmallestPivot));
            Add("Resultant of internal forces", $"{Number(outcome.ResultantError)} (relative)");
        }
        if (stability is not null)
        {
            if (FemStabilityText.Largest(stability) is { } largest) Add("Largest loss of accuracy", largest);
            Add("Accuracy warnings", stability.Warnings.ToString("N0", Plain));
            if (stability.ZeroStiffnessModes > 0) Add("Movements without stiffness", stability.ZeroStiffnessModes.ToString("N0", Plain));
        }
        if (item.LeftOutDofs > 0) Add("Rotations left out", $"{item.LeftOutDofs.ToString("N0", Plain)} — no stiffness and no load there");
        if (item.Started is { } started) Add("Elapsed", $"{((item.Ended ?? now) - started).TotalSeconds.ToString("0.00", Plain)} s");
        return fields;
    }

    private static FemInstabilityRow Row(FemJointDiagnostic joint, FemStabilityReport report)
    {
        FemDofCell Cell(int index)
        {
            var dof = joint.Dofs[index];
            string text = FemStabilityText.Cell(dof, report), name = $"{dof.Dof} of {joint.Joint}";
            return dof.State switch
            {
                FemDofState.Inactive => new(text, FemCellLevel.Muted, $"{name}: inactive — not a degree of freedom of this analysis."),
                FemDofState.Restrained => new(text, FemCellLevel.Muted, $"{name}: restrained by a support — not an equation."),
                FemDofState.Infinite => new(text, FemCellLevel.Infinite, FemStabilityText.Message(dof, report)),
                FemDofState.Estimated => new(text, dof.Severity switch
                {
                    StabilitySeverity.Severe => FemCellLevel.Severe, StabilitySeverity.Warning => FemCellLevel.Warning, _ => FemCellLevel.Normal,
                }, dof.Severity == StabilitySeverity.None ? $"{name}: {dof.DigitsLost!.Value.ToString("0.###", Plain)} digits of accuracy lost — normal."
                    : FemStabilityText.Message(dof, report)),
                _ => new(text, dof.Severity == StabilitySeverity.Severe ? FemCellLevel.Severe : FemCellLevel.Muted,
                    dof.Severity == StabilitySeverity.Severe ? FemStabilityText.Message(dof, report) : $"{name}: active, but no estimate of the digits lost is available."),
            };
        }
        return new(joint.Joint, joint.X, joint.Y, joint.Z, Cell(0), Cell(1), Cell(2), Cell(3), Cell(4), Cell(5), FemStabilityText.Remarks(joint, report));
    }

    private static string Note(FemStabilityReport report)
    {
        string legend = $"Digits of accuracy lost by degree of freedom. {FemStabilityText.Inactive} inactive · {FemStabilityText.Restrained} restrained · " +
            $"{FemStabilityText.Unavailable} no estimate · {FemStabilityText.Infinity} singular. Coordinates in m.";
        return legend + (report.Stage == StabilityStage.EquilibriumIteration
                ? $" Found in equilibrium iteration {report.Iteration}: only movements without any stiffness are identified at that point."
            : report.State == StabilityState.Singular ? " A singular stiffness has no estimate for the degrees of freedom outside the movements found."
            : report.State == StabilityState.NotPositiveDefinite ? " The joint named for a negative pivot depends on the order of elimination."
            : "");
    }
}

/// <summary>The run monitor: the run in the header, the model in numbers and one entry per load case. It shows
/// snapshots of <see cref="FemRunMonitor"/>; it holds no solver state and no matrix.</summary>
public sealed class FemRunMonitorViewModel : Observable
{
    private static readonly CultureInfo Plain = CultureInfo.InvariantCulture;
    private FemRunSnapshot shown = FemRunSnapshot.Empty;
    private bool isNoteExpanded = true;
    private string elapsedText = "";

    public ObservableCollection<FemRunCaseViewModel> Cases { get; } = [];
    public FemRunStatus Status => shown.Status;
    public string StatusText => shown.Status switch
    {
        FemRunStatus.Running => "Running",
        FemRunStatus.Completed => "Completed",
        FemRunStatus.CompletedWithWarnings => "Completed with Warnings",
        FemRunStatus.Failed => "Failed",
        FemRunStatus.Cancelled => "Cancelled",
        _ => "No analysis run yet",
    };
    public bool HasRun => shown.Status != FemRunStatus.NotRun;
    public double Progress => shown.Fraction;
    /// <summary>Running, and no load case has ended yet: the bar has nothing to count, so it only shows that the run goes.</summary>
    public bool IsIndeterminate => shown.Status == FemRunStatus.Running && shown.Fraction <= 0;
    /// <summary>The degrees of freedom of the equations the solver assembled; empty until it has.</summary>
    public string DofText => shown.SolvedDofs <= 0 ? ""
        : $"{shown.ActiveDofs.ToString("N0", Plain)} active | {shown.SolvedDofs.ToString("N0", Plain)} solved | {shown.RestrainedDofs.ToString("N0", Plain)} restrained" +
          (shown.ActiveDofs - shown.SolvedDofs - shown.RestrainedDofs is > 0 and var leftOut ? $" | {leftOut.ToString("N0", Plain)} left out" : "");
    public string StartedText => shown.Started?.ToString("yyyy-MM-dd  HH:mm:ss", Plain) ?? "";
    public string ElapsedText { get => elapsedText; private set => Set(ref elapsedText, value); }
    public string Message => shown.Message ?? "";
    public bool HasMessage => !string.IsNullOrEmpty(shown.Message);

    public bool HasModel => shown.Model is not null;
    public string ModelName => shown.Model?.ModelName ?? "";
    public IReadOnlyList<FemRunField> ModelLeft { get; private set; } = [];
    public IReadOnlyList<FemRunField> ModelRight { get; private set; } = [];
    public string SupportSummary { get; private set; } = "";
    public string ModelNotes { get; private set; } = "";
    public string CasesSummary { get; private set; } = "";

    /// <summary>The note on numerical stability is open; the user closes it and it stays closed.</summary>
    public bool IsNoteExpanded { get => isNoteExpanded; set => Set(ref isNoteExpanded, value); }
    public double WarningDigits { get; private set; } = StabilityThresholds.Default.WarningDigits;
    public double SevereDigits { get; private set; } = StabilityThresholds.Default.SevereDigits;
    public string NoteText =>
        "Numerical stability is assessed using estimated digits of accuracy lost during matrix solution.\n\n" +
        $"A degree of freedom is flagged when the estimated loss reaches {WarningDigits.ToString("0.###", Plain)} digits (numerical accuracy warning) and is " +
        $"reported as a severe instability above {SevereDigits.ToString("0.###", Plain)} digits.\n\n" +
        "A value of ∞ indicates that a singularity has been identified and the diagnostic supports reporting an unbounded loss of accuracy.\n\n" +
        "Review the affected joints and degrees of freedom for insufficient restraints, disconnected elements, structural mechanisms, or numerical ill-conditioning.";

    private static string Hms(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";

    /// <summary>Thresholds the run reports with, for the note.</summary>
    public void UseThresholds(StabilityThresholds thresholds)
    {
        (WarningDigits, SevereDigits) = (thresholds.WarningDigits, thresholds.SevereDigits);
        Notify(nameof(NoteText));
    }

    public void Reset() => Apply(FemRunSnapshot.Empty);

    /// <summary>The clock of a run that is going. The view calls it; a run that has ended keeps its times.</summary>
    public void Tick(DateTime now)
    {
        if (shown.Status != FemRunStatus.Running || shown.Started is not { } started) return;
        ElapsedText = Hms(now - started);
        foreach (var item in Cases) item.Tick(now);
    }

    public void Apply(FemRunSnapshot snapshot) => Apply(snapshot, DateTime.Now);

    public void Apply(FemRunSnapshot snapshot, DateTime now)
    {
        bool another = snapshot.Started != shown.Started || snapshot.Cases.Count < Cases.Count;
        bool modelChanged = !ReferenceEquals(snapshot.Model, shown.Model);
        shown = snapshot;
        if (another) Cases.Clear();
        for (int index = 0; index < snapshot.Cases.Count; index++)
        {
            if (index == Cases.Count) Cases.Add(new());
            Cases[index].Apply(snapshot.Cases[index], now);
        }
        if (modelChanged) DescribeModel(snapshot.Model);
        int ended = snapshot.Cases.Count(item => item.Status is not (FemCaseStatus.Pending or FemCaseStatus.Running));
        CasesSummary = snapshot.Cases.Count == 0 ? "" : $"{ended.ToString("N0", Plain)} of {snapshot.Cases.Count.ToString("N0", Plain)} ended";
        elapsedText = snapshot.Started is { } started ? Hms((snapshot.Ended ?? now) - started) : "";
        Notify(string.Empty);
    }

    private void DescribeModel(FemRunModelInfo? model)
    {
        if (model is null)
        {
            (ModelLeft, ModelRight, SupportSummary, ModelNotes) = ([], [], "", "");
            return;
        }
        static FemRunField Field(string label, int value) => new(label, value.ToString("N0", Plain));
        ModelLeft =
        [
            Field("Number of Joints", model.Joints), Field("Number of Restrained Joints", model.Supports.Count),
            Field("Number of Frame Members", model.Frames), Field("Number of Cable Members", model.Cables), Field("Number of Shell Elements", model.Shells),
        ];
        ModelRight =
        [
            Field("Number of Load Patterns", model.LoadPatterns), Field("Number of Joint Load Assignments", model.JointLoads),
            Field("Number of Member Load Assignments", model.MemberLoads), Field("Number of Load Cases", model.Cases),
            Field("Number of Load Combinations", model.Combinations),
        ];
        SupportSummary = model.Supports.Count == 0 ? "No restrained joints"
            : $"{model.Supports.Count.ToString("N0", Plain)} pin {(model.Supports.Count == 1 ? "support" : "supports")} (UX, UZ restrained; rotation free): {string.Join(", ", model.Supports)}";
        ModelNotes = string.Join("\n", (model.LoadingWarnings > 0
                ? new[] { $"The loading of the model has {model.LoadingWarnings.ToString("N0", Plain)} warning{(model.LoadingWarnings == 1 ? "" : "s")}; see Define → Load Patterns." } : [])
            .Concat(model.Notes));
    }
}
