using System.Globalization;
using Mcotis.Analysis;

namespace Mcotis.Desktop.Application;

/// <summary>What the run monitor says of the model of a run. Every number is counted from the model as it is run.</summary>
/// <param name="ModelName">The file the model is in, as it is named on disk; a model without a file by its own name.</param>
/// <param name="Supports">The restrained joints, by name.</param>
/// <param name="LoadingWarnings">Warnings of the loading held in the model; they are listed under Define → Load Patterns.</param>
public sealed record FemRunModelInfo(string ModelName, int Joints, IReadOnlyList<string> Supports, int Frames, int Cables, int Shells,
    int LoadPatterns, int JointLoads, int MemberLoads, int Cases, int Combinations, int LoadingWarnings, IReadOnlyList<string> Notes);

public enum FemRunStatus { NotRun, Running, Completed, CompletedWithWarnings, Failed, Cancelled }

/// <summary>How a load case stands. <see cref="NotConverged"/> is an equilibrium iteration that did not converge;
/// <see cref="NumericalInstability"/> is a stiffness the solver could not rely on. They are different outcomes.</summary>
public enum FemCaseStatus { Pending, Running, Converged, ConvergedWithWarnings, NotConverged, NumericalInstability, Failed, Cancelled }

public enum FemLogLevel { Information, Warning, Error }

public sealed record FemRunLogEntry(DateTime Time, string Text, FemLogLevel Level = FemLogLevel.Information);

/// <summary>One solve of a run: a load case, or the stage that fits the cable target forces of one.</summary>
/// <param name="Last">The solver's last report of it.</param>
/// <param name="Note">Why a case that was not solved was not.</param>
/// <param name="Version">Goes up whenever anything of the case changes.</param>
public sealed record FemRunCase(int Number, string Name, bool FitStage, FemCaseStatus Status, DateTime? Started, DateTime? Ended,
    FemEquilibriumProgress? Last, IReadOnlyList<FemRunLogEntry> Log, string? Note, int Version)
{
    public string Label => !FitStage ? Name : Name.Length > 0 ? $"{Name} — cable target-force fit" : "Cable target-force fit";
    public FemStabilityReport? Stability => Last?.Stability;
    /// <summary>Rotations left out of the equations: on, held by no support, and without stiffness or load.</summary>
    public int LeftOutDofs => Last is { SolvedDofs: > 0 } last ? Math.Max(0, last.ActiveDofs - last.SolvedDofs - last.RestrainedDofs) : 0;
}

public sealed record FemRunSnapshot(FemRunStatus Status, DateTime? Started, DateTime? Ended, double Fraction, int ActiveDofs, int SolvedDofs,
    int RestrainedDofs, FemRunModelInfo? Model, IReadOnlyList<FemRunCase> Cases, string? Message)
{
    public static FemRunSnapshot Empty { get; } = new(FemRunStatus.NotRun, null, null, 0, 0, 0, 0, null, [], null);
}

/// <summary>Follows one run of the model for the run monitor: every solve with its status, what the solver reported of
/// it and when, and its numerical stability. It takes the solver's reports as they come (on the solving thread) and
/// hands out snapshots (on the thread that shows them); it computes nothing of its own, and it knows no window.
/// <para>A status is what the solver said: <see cref="FemCaseStatus.Converged"/> only for a case the solver finished —
/// which it does only after its residual, stability and equilibrium checks — never for one that merely returned.</para></summary>
public sealed class FemRunMonitor(Func<DateTime>? clock = null) : IProgress<FemEquilibriumProgress>
{
    /// <summary>Iteration lines kept per case; a fit of many cables can make thousands.</summary>
    public const int MaximumIterationLines = 600;
    private const int MaximumDiagnosticLines = 12;
    private static readonly CultureInfo Plain = CultureInfo.InvariantCulture;

    private readonly Func<DateTime> now = clock ?? (() => DateTime.Now);
    private readonly object gate = new();
    private readonly List<CaseState> cases = [];
    private FemRunStatus status = FemRunStatus.NotRun;
    private FemRunModelInfo? model;
    private DateTime? started, ended, lastEnded;
    private double fraction;
    private int active, solved, restrained;
    private string? message;

    private sealed class CaseState(int number, string name, bool fitStage, string? remark = null)
    {
        public readonly int Number = number;
        public readonly string Name = name;
        public readonly bool FitStage = fitStage;
        /// <summary>Said when the case starts: why the run solves a case that is not set to run.</summary>
        public readonly string? Remark = remark;
        public readonly List<FemRunLogEntry> Log = [];
        public FemCaseStatus Status = FemCaseStatus.Pending;
        public DateTime? Started, Ended;
        public FemEquilibriumProgress? Last;
        public string? Note;
        public bool Assembled;
        public int Version, FitRun = -1, Equilibria, Trials, TrialIterations, IterationLines, Omitted;
        public FemRunCase? Shown;
    }

    private static string Number(double value, string format = "0.00E+00") => value.ToString(format, Plain);
    private static string Count(int value, string one, string many) => $"{value.ToString("N0", Plain)} {(value == 1 ? one : many)}";

    private static void Add(CaseState item, DateTime time, string text, FemLogLevel level = FemLogLevel.Information)
    {
        item.Log.Add(new(time, text, level));
        item.Version++;
    }

    /// <summary>Starts the run: the model, and the solves it will make, in order — they wait as Pending.</summary>
    /// <param name="remarks">By case: what its log says first — a case the run needs but that is not set to run.</param>
    public void Begin(FemRunModelInfo runModel, IEnumerable<(string Case, bool FitStage)> planned, IReadOnlyDictionary<string, string>? remarks = null)
    {
        lock (gate)
        {
            cases.Clear();
            foreach (var (name, fitStage) in planned)
                cases.Add(new(cases.Count + 1, name, fitStage, !fitStage && remarks is not null && remarks.TryGetValue(name, out var remark) ? remark : null));
            model = runModel;
            status = FemRunStatus.Running;
            started = lastEnded = now();
            ended = null;
            fraction = 0;
            active = solved = restrained = 0;
            message = null;
        }
    }

    /// <summary>A note on the model the run found while it ran — a joint it left out — said with the model information.</summary>
    public void Note(string note)
    {
        lock (gate)
            if (model is not null) model = model with { Notes = [.. model.Notes, note] };
    }

    /// <summary>How a case stands by the solver's last report of it.</summary>
    public static FemCaseStatus Classify(FemEquilibriumProgress report)
    {
        bool reported = report.Stability is { Passed: false };
        // it did not fail in its own solution: what failed is the stage it continues from, which has its own entry
        if (report.PrerequisiteFailed) return FemCaseStatus.Failed;
        switch (report.State)
        {
            case FemEquilibriumState.Running: return FemCaseStatus.Running;
            case FemEquilibriumState.NotConverged: return FemCaseStatus.NotConverged;
            case FemEquilibriumState.Unstable: return FemCaseStatus.NumericalInstability;
            case FemEquilibriumState.Converged:
                bool leftOut = report.ActiveDofs - report.SolvedDofs - report.RestrainedDofs > 0;
                return reported || leftOut || report.Outcome is { FitMet: false } || report.ModelWarnings.Count > 0
                    ? FemCaseStatus.ConvergedWithWarnings : FemCaseStatus.Converged;
            default:
                // The solver factorized the stiffness and then withheld the result because its own accuracy checks
                // failed. With degrees of freedom reported for lost accuracy, that is a numerical refusal.
                return report.FailureCode is "static.residual" or "static.equilibrium" && reported ? FemCaseStatus.NumericalInstability : FemCaseStatus.Failed;
        }
    }

    public void Report(FemEquilibriumProgress report)
    {
        lock (gate)
        {
            var time = now();
            if (status != FemRunStatus.Running)
            {
                // reports without a Begin: a run that was not planned here
                status = FemRunStatus.Running;
                started ??= time;
                lastEnded ??= started;
                ended = null;
            }
            var item = cases.FirstOrDefault(found => found.FitStage == report.FitStage && found.Name == report.CaseName);
            if (item is null) cases.Add(item = new(cases.Count + 1, report.CaseName, report.FitStage));
            if (item.Started is null)
            {
                // the solver works one case at a time: this one began when the one before it ended
                item.Started = lastEnded ?? time;
                item.Status = FemCaseStatus.Running;
                Add(item, item.Started.Value, "Analysis started.");
                if (item.Remark is not null) Add(item, item.Started.Value, item.Remark);
                // what the solver said of the model the case is solved on: a joint held by a degree-of-freedom option
                foreach (string warning in report.ModelWarnings.Take(MaximumDiagnosticLines)) Add(item, item.Started.Value, warning, FemLogLevel.Warning);
                if (report.ModelWarnings.Count > MaximumDiagnosticLines)
                    Add(item, item.Started.Value, $"{Count(report.ModelWarnings.Count - MaximumDiagnosticLines, "more warning", "more warnings")} of the same kind.", FemLogLevel.Warning);
            }
            if (report.SolvedDofs > 0) (active, solved, restrained) = (report.ActiveDofs, report.SolvedDofs, report.RestrainedDofs);
            fraction = Math.Max(fraction, report.Fraction);
            if (!item.Assembled && report.SolvedDofs > 0)
            {
                item.Assembled = true;
                int leftOut = report.ActiveDofs - report.SolvedDofs - report.RestrainedDofs;
                Add(item, time, $"Stiffness and loads assembled: {Count(report.SolvedDofs, "equation", "equations")} " +
                    $"({report.ActiveDofs.ToString("N0", Plain)} active degrees of freedom, {report.RestrainedDofs.ToString("N0", Plain)} restrained" +
                    (leftOut > 0 ? $", {Count(leftOut, "rotation", "rotations")} left out" : "") + ").");
            }
            if (report.State != FemEquilibriumState.Running) Ending(item, report, time);
            else if (report.OutOfBalance is not null) Iteration(item, report, time);
            item.Last = report;
            item.Version++;
        }
    }

    private void Iteration(CaseState item, FemEquilibriumProgress report, DateTime time)
    {
        double unbalance = report.OutOfBalance!.Value, tolerance = report.Tolerance!.Value;
        if (report.Linear)
        {
            Add(item, time, $"Stiffness factorized and the equations solved directly: scaled backward error {Number(unbalance)} (accepted {Number(tolerance)}).");
            return;
        }
        // An equilibrium counts its iterations from 1. In a fitting run the first equilibrium is that of the run's
        // pretension; those after it are trial pretensions, one cable at a time, for the response of the cables.
        bool newRun = report.FitRun != item.FitRun;
        if (newRun)
        {
            Trials(item, time);
            bool fitted = item.FitRun > 0;
            item.FitRun = report.FitRun;
            item.Equilibria = 0;
            if (report.FitRun > 0) Add(item, time, $"Fitting run {report.FitRun}: equilibrium under the cable pretension of the run.");
            else if (fitted) Add(item, time, "Equilibrium of the case under the fitted cable pretension.");
        }
        if (newRun || report.Iteration == 1) item.Equilibria++;
        if (report.FitRun > 0 && item.Equilibria > 1)
        {
            if (report.Iteration == 1) item.Trials++;
            item.TrialIterations++;
            return;
        }
        if (item.IterationLines >= MaximumIterationLines)
        {
            item.Omitted++;
            return;
        }
        item.IterationLines++;
        string unit = report.WorstDof?.StartsWith('R') == true ? "kN·m" : "kN";
        Add(item, time, $"Iteration {report.Iteration}: out-of-balance {Number(unbalance, "0.000E+00")} {unit} (accepted {Number(tolerance, "0.000E+00")} kN), " +
            $"{report.JointsInEquilibrium.ToString("N0", Plain)} of {report.Joints.ToString("N0", Plain)} joints in equilibrium" +
            (report.JointsInEquilibrium < report.Joints && report.WorstJoint is { } joint ? $", largest at {joint} {report.WorstDof}" : "") + ".");
    }

    private static void Trials(CaseState item, DateTime time)
    {
        if (item.Trials > 0)
            Add(item, time, $"Fitting run {item.FitRun}: {Count(item.Trials, "trial pretension", "trial pretensions")} for the response of the cables ({Count(item.TrialIterations, "iteration", "iterations")}).");
        item.Trials = item.TrialIterations = 0;
    }

    private void Ending(CaseState item, FemEquilibriumProgress report, DateTime time)
    {
        Trials(item, time);
        if (item.Omitted > 0) Add(item, time, $"{Count(item.Omitted, "further iteration line is", "further iteration lines are")} not shown.");
        item.Ended = lastEnded = time;
        item.Status = Classify(report);
        if (report.State == FemEquilibriumState.Converged)
        {
            if (!report.Linear && report.Iteration > 0)
                Add(item, time, $"Equilibrium reached in {Count(report.Iteration, "iteration", "iterations")}: every joint within the accepted out-of-balance.");
            if (report.Outcome is { } outcome)
            {
                string where = report.Linear ? "solution" : "converged state";
                if (report.Stability is { MovementsHeld: > 0 } held)
                    Add(item, time, $"Stiffness at the {where}: singular — {Count(held.MovementsHeld, "movement", "movements")} without stiffness, on which the loads of the case do no work. " +
                        $"Held, and the rest factorized (smallest scaled pivot {Number(outcome.SmallestPivot)}).", FemLogLevel.Warning);
                else
                    Add(item, time, $"Stiffness at the {where} factorized: positive definite (smallest scaled pivot {Number(outcome.SmallestPivot)}).");
                Add(item, time, $"Internal forces checked: relative resultant {Number(outcome.ResultantError)}.");
                if (item.FitStage && outcome.FitDeviationPercent is { } deviation)
                {
                    if (outcome.FitMet) Add(item, time, $"Cable target forces met after {Count(report.FitRun, "fitting run", "fitting runs")}: largest deviation {Number(deviation, "0.######")} %.");
                    else Add(item, time, $"Cable target forces not met within the tolerance; the closest pretension found is used. {outcome.FitFailure}", FemLogLevel.Warning);
                }
            }
            Stability(item, report, time);
            int leftOut = report.ActiveDofs - report.SolvedDofs - report.RestrainedDofs;
            if (leftOut > 0)
                Add(item, time, $"{Count(leftOut, "rotation is", "rotations are")} left out of the equations: no stiffness and no load there.", FemLogLevel.Warning);
            Add(item, time, $"Analysis completed in {Number((time - item.Started!.Value).TotalSeconds, "0.00")} s.");
            return;
        }
        string failure = report.Failure ?? "The case has no result.";
        Add(item, time, report.PrerequisiteFailed ? "Not solved — the cable target-force fit this case continues from failed: " + failure : failure, FemLogLevel.Error);
        Stability(item, report, time);
        Add(item, time, item.Status switch
        {
            FemCaseStatus.NumericalInstability => "Analysis ended without a result: numerical instability.",
            FemCaseStatus.NotConverged => "Analysis ended without a result: the equilibrium iteration did not converge.",
            _ => "Analysis ended without a result.",
        }, FemLogLevel.Error);
    }

    private static void Stability(CaseState item, FemEquilibriumProgress report, DateTime time)
    {
        if (report.Stability is not { } stability)
        {
            if (report.State == FemEquilibriumState.NotConverged)
                Add(item, time, "Numerical stability not assessed: the iteration reached no converged state.");
            return;
        }
        string where = stability.Stage == StabilityStage.EquilibriumIteration ? $" (equilibrium iteration {stability.Iteration})" : "";
        if (stability.Passed)
        {
            Add(item, time, "Numerical stability checked: no instability detected" +
                (FemStabilityText.Largest(stability) is { } largest ? $"; largest loss of accuracy {largest}." : "."));
            return;
        }
        // a case solved with a movement held has its result: what is listed of it is a warning, not a failure
        var level = stability.Instabilities > 0 && stability.MovementsHeld == 0 ? FemLogLevel.Error : FemLogLevel.Warning;
        Add(item, time, $"Numerical stability checked{where}: {FemStabilityText.Summary(stability)}; " +
            $"{Count(stability.Instabilities, "instability", "instabilities")}, {Count(stability.Warnings, "accuracy warning", "accuracy warnings")}.", level);
        if (stability.MovementsHeld > 0)
            Add(item, time, "The case is solved with " + (stability.MovementsHeld == 1 ? "that movement" : "those movements") +
                " held: displacements are determined up to it; forces are not affected. The degrees of freedom that move in it are listed.", level);
        var reported = stability.Joints.SelectMany(joint => joint.Reported).ToArray();
        foreach (var dof in reported.Take(MaximumDiagnosticLines))
            Add(item, time, FemStabilityText.Message(dof, stability), dof.Severity == StabilitySeverity.Severe && stability.MovementsHeld == 0 ? FemLogLevel.Error : FemLogLevel.Warning);
        if (reported.Length > MaximumDiagnosticLines)
            Add(item, time, $"{Count(reported.Length - MaximumDiagnosticLines, "more degree of freedom is", "more degrees of freedom are")} listed in the table.", level);
    }

    /// <summary>Ends the run. <paramref name="completed"/>: it has its results. A case still running was stopped; a
    /// case still waiting was not run — and the log of each says why, with the reason the run gave (<paramref name="note"/>).</summary>
    public void End(bool completed, bool cancelled = false, string? note = null)
    {
        lock (gate)
        {
            var time = now();
            started ??= time;
            ended = time;
            foreach (var item in cases)
            {
                if (item.Status == FemCaseStatus.Running)
                {
                    item.Status = FemCaseStatus.Cancelled;
                    item.Ended = time;
                    item.Note = cancelled ? "Stopped: the analysis was cancelled." : "Stopped: the run ended before this case had finished.";
                    Add(item, time, item.Note, FemLogLevel.Warning);
                    if (!cancelled && note is not null) Add(item, time, note, FemLogLevel.Error);
                }
                else if (item.Status == FemCaseStatus.Pending)
                {
                    item.Status = FemCaseStatus.Cancelled;
                    item.Note = cancelled ? "Not run: the analysis was cancelled." : "Not run: the run stopped before this case.";
                    Add(item, time, item.Note, FemLogLevel.Warning);
                    if (!cancelled && note is not null) Add(item, time, note, FemLogLevel.Error);
                }
            }
            bool failed = !completed || cases.Any(item => item.Status is FemCaseStatus.NotConverged or FemCaseStatus.NumericalInstability or FemCaseStatus.Failed);
            status = cancelled ? FemRunStatus.Cancelled : failed ? FemRunStatus.Failed
                : cases.Any(item => item.Status == FemCaseStatus.ConvergedWithWarnings) ? FemRunStatus.CompletedWithWarnings : FemRunStatus.Completed;
            if (status is FemRunStatus.Completed or FemRunStatus.CompletedWithWarnings) fraction = 1;
            message = note;
        }
    }

    /// <summary>The run as it stands. A case that has not changed since the last snapshot is the same object in this one.</summary>
    public FemRunSnapshot Snapshot()
    {
        lock (gate)
        {
            var shown = cases.Select(item => item.Shown is { } kept && kept.Version == item.Version ? kept
                : item.Shown = new(item.Number, item.Name, item.FitStage, item.Status, item.Started, item.Ended, item.Last, [.. item.Log], item.Note, item.Version)).ToArray();
            return new(status, started, ended, fraction, active, solved, restrained, model, shown, message);
        }
    }
}
