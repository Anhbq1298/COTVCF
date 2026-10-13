using System.Globalization;

namespace Mcotis.Desktop.Application;

/// <summary>What the analysis log says of the model before the run starts.</summary>
/// <param name="FrameMesh">Segments each frame piece is read at.</param>
/// <param name="LoadingWarnings">Warnings of the loading held in the model; they are listed under Define → Load Patterns.</param>
public sealed record FemRunLogModel(string ModelName, int Joints, IReadOnlyList<string> Supports, int Frames, int Cables,
    int LoadPatterns, int JointLoads, int MemberLoads, int LoadingWarnings,
    IReadOnlyList<string> Cases, IReadOnlyList<string> Combinations, IReadOnlyList<string> FitCases, IReadOnlyList<string> Notes,
    double TargetFitPercent, int MaximumFitRuns, int FrameMesh, bool CableSag);

/// <summary>What the analysis log says of the results of a run that has them.</summary>
/// <param name="FitMet">Null when the run fitted no cable target forces.</param>
/// <param name="Cases">The largest values of every load case and combination, in the order they are shown.</param>
public sealed record FemRunLogResults(bool? FitMet, int FitRuns, double FitDeviationPercent,
    IReadOnlyList<FemRunLogCaseResult> Cases, string? OutputDatabase);

public sealed record FemRunLogCaseResult(string Name, double LargestAxialKN, double LargestDeflectionMm, int SlackCables);

/// <summary>The text of the run monitor's analysis log. It is laid out like the analysis log of SAP2000 (user,
/// 2026-10-12: "tao muốn ra giao diện log như này") — spaced section titles with the time at the right,
/// <c>NAME = value</c> lines, one line per equilibrium iteration, the stability check of the converged state — and
/// says what this solver does, in its own terms: nothing is written here that does not happen in the run.
/// <para>Lines are written on the thread that reports and taken, in order, on the thread that shows them.</para></summary>
public sealed class FemRunLog(Func<DateTime>? clock = null)
{
    private const int NameWidth = 41, ValueWidth = 12, LineWidth = 78, WrapWidth = 76;
    private static readonly CultureInfo Plain = CultureInfo.InvariantCulture;
    private static readonly string Rule = " " + new string('-', 39);
    public const string Warning = " * * * W A R N I N G * * *", Error = " * * * E R R O R * * *";
    /// <summary>The line that closes the stability check of a converged case.</summary>
    public const string Stable = "    NUMBER FOUND AT FINAL CONVERGED STATE  =         0,  OK.";

    private readonly Func<DateTime> now = clock ?? (() => DateTime.Now);
    private readonly object gate = new();
    private readonly List<string> pending = [];
    private readonly Dictionary<(string Case, bool FitStage), CaseLog> cases = [];
    private (int Active, int Restrained, int Solved)? equations;
    private DateTime began;

    private sealed class CaseLog
    {
        public DateTime Started;
        public bool Header, Table;
        public int FitRun = -1, Equilibria, Trials, TrialIterations;
    }

    /// <summary>A section title: its letters spaced out, the time at the right — with the date where a run begins or ends.</summary>
    public static string Title(string text, DateTime time, bool date = false)
    {
        string spaced = " " + string.Join("   ", text.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => string.Join(' ', word.AsEnumerable())));
        string stamp = time.ToString(date ? "yyyy/MM/dd  HH:mm:ss" : "HH:mm:ss", Plain);
        return spaced.PadRight(Math.Max(LineWidth - stamp.Length, spaced.Length + 2)) + stamp;
    }

    /// <summary>A <c>NAME = value</c> line: names in one column, values right-aligned in the next.</summary>
    public static string Entry(string name, string value, int indent = 0) =>
        " " + (new string(' ', indent) + name).PadRight(NameWidth) + " =" + value.PadLeft(ValueWidth);
    public static string Entry(string name, long value, int indent = 0) => Entry(name, value.ToString(Plain), indent);
    public static string Number(double value, string format) => value.ToString(format, Plain);

    /// <summary>A sentence as lines of the log, broken between words.</summary>
    public static IEnumerable<string> Wrap(string text)
    {
        var line = new System.Text.StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > WrapWidth)
            {
                yield return " " + line;
                line.Clear();
            }
            line.Append(line.Length > 0 ? " " : "").Append(word);
        }
        if (line.Length > 0) yield return " " + line;
    }

    private void Add(string line = "") => pending.Add(line);
    private void Add(IEnumerable<string> lines) => pending.AddRange(lines);

    /// <summary>The lines written since the last call, in order.</summary>
    public IReadOnlyList<string> Take()
    {
        lock (gate)
        {
            string[] taken = [.. pending];
            pending.Clear();
            return taken;
        }
    }

    public void Begin(FemRunLogModel model)
    {
        lock (gate)
        {
            began = now();
            Add(Title("Begin analysis", began, date: true));
            Add();
            Add(" MODEL: " + model.ModelName);
            Add(" RUNNING THE GENERAL STATIC SOLVER OF MCOTIS IN THE XZ PLANE (UX, UZ, RY)");
            Add(" SAP2000 IS NOT READ OR WRITTEN");
            Add();
            Add(Entry("NUMBER OF JOINTS", model.Joints));
            Add(Entry("WITH RESTRAINTS", model.Supports.Count, 4));
            Add(Entry("NUMBER OF FRAME MEMBERS", model.Frames));
            Add(Entry("NUMBER OF CABLE MEMBERS", model.Cables));
            Add(Entry("NUMBER OF LOAD PATTERNS", model.LoadPatterns));
            Add(Entry("NUMBER OF JOINT LOAD ASSIGNMENTS", model.JointLoads));
            Add(Entry("NUMBER OF MEMBER LOAD ASSIGNMENTS", model.MemberLoads));
            Add(Entry("NUMBER OF LOAD CASES", model.Cases.Count));
            Add(Entry("NUMBER OF LOAD COMBINATIONS", model.Combinations.Count));
            Add();
            Add(Wrap("PIN SUPPORTS: " + string.Join(", ", model.Supports)));
            Add();
            Add(" LOAD CASES TO BE SOLVED:");
            Add();
            foreach (string name in model.Cases)
                Add(" CASE: " + name + (model.FitCases.Contains(name, StringComparer.OrdinalIgnoreCase) ? "   (CABLE TARGET FORCES FITTED IN THIS CASE)" : ""));
            foreach (string name in model.Combinations) Add(" COMBINATION: " + name);
            if (model.LoadingWarnings > 0)
            {
                Add();
                Add(Warning);
                Add($" THE LOADING OF THE MODEL HAS {model.LoadingWarnings} WARNING(S); SEE DEFINE > LOAD PATTERNS");
            }
            foreach (string note in model.Notes)
            {
                Add();
                Add(Wrap("NOTE: " + note));
            }
            Add();
            Add(Title("Analysis options", now()));
            Add();
            Add(Entry("TENSION-ONLY CABLES", "YES"));
            Add(Entry("CABLE SAG", model.CableSag ? "YES" : "NO"));
            Add(Entry("CABLE TARGET-FORCE TOLERANCE (PERCENT)", Number(model.TargetFitPercent, "0.000000")));
            Add(Entry("MAXIMUM NUMBER OF FITTING RUNS", model.MaximumFitRuns));
            Add(Entry("FRAME MESH (SEGMENTS PER FRAME PIECE)", model.FrameMesh));
        }
    }

    /// <summary>One report of the solver, as it comes: the equations of the run, a case's block when its first
    /// equilibrium is measured, a line per iteration, and how the case ended.</summary>
    public void Report(FemEquilibriumProgress report)
    {
        lock (gate)
        {
            if (report.SolvedDofs > 0 && equations != (report.ActiveDofs, report.RestrainedDofs, report.SolvedDofs))
            {
                equations = (report.ActiveDofs, report.RestrainedDofs, report.SolvedDofs);
                int leftOut = report.ActiveDofs - report.RestrainedDofs - report.SolvedDofs;
                Add();
                Add(Title("Equations", now()));
                Add();
                Add(Entry("NUMBER OF ACTIVE DEGREES OF FREEDOM", report.ActiveDofs));
                Add(Entry("RESTRAINED BY SUPPORTS", report.RestrainedDofs, 4));
                if (leftOut > 0) Add(Entry("ROTATIONS LEFT OUT (NO STIFFNESS)", leftOut, 4));
                Add(Entry("TOTAL NUMBER OF EQUILIBRIUM EQUATIONS", report.SolvedDofs));
            }

            var key = (report.CaseName, report.FitStage);
            if (!cases.TryGetValue(key, out var log)) cases[key] = log = new() { Started = now() };
            bool ended = report.State != FemEquilibriumState.Running, measured = report.OutOfBalance is not null;
            if (!ended && !measured) return;   // a case that has only started: its block opens with its first equilibrium
            if (!log.Header) Header(report, log, measured);
            if (!ended) Iteration(report, log);
            else Ending(report, log);
        }
    }

    private void Header(FemEquilibriumProgress report, CaseLog log, bool measured)
    {
        log.Header = true;
        Add();
        Add(Title(report.FitStage ? "Cable target-force fit" : !measured ? "Static analysis" : report.Linear ? "Linear static analysis" : "Nonlinear static analysis", log.Started));
        Add();
        Add(" CASE: " + (report.CaseName.Length > 0 ? report.CaseName : "(THE LOADING OF THE RUN)"));
        Add();
        if (report.FitStage)
        {
            Add(" STARTING FROM ZERO (UNSTRESSED) INITIAL CONDITIONS");
            Add(" CABLE PRETENSIONS ARE ADJUSTED UNTIL THE CABLE FORCES MEET THEIR TARGETS");
        }
        else if (measured && report.Linear) Add(" USING STIFFNESS AT ZERO (UNSTRESSED) INITIAL CONDITIONS");
        else Add(report.Pretensioned ? " STARTING FROM THE CABLE PRETENSION FITTED FOR THIS STAGE" : " STARTING FROM ZERO (UNSTRESSED) INITIAL CONDITIONS");
        if (!measured) return;
        Add();
        if (report.Linear)
        {
            Add(Entry("USE ITERATION", "NO"));
            Add(" SOLVED DIRECTLY, BY FACTORIZATION OF THE STIFFNESS");
            return;
        }
        Add(Entry("LOAD CONTROL TYPE", "FORCE"));
        Add(Entry("TYPE OF GEOMETRIC NONLINEARITY", "NONE"));
        Add(Entry("TENSION-ONLY CABLES", "YES"));
        Add(Entry("USE ITERATION", "YES"));
        Add(Entry("USE LINE SEARCH", "YES"));
        Add(Entry("FORCE CONVERGENCE TOLERANCE (RELATIVE)", Number(report.Tolerance!.Value / report.Scale, "0.000E+00")));
    }

    private static string Row(string fit, string converged, string iteration, string unbalance, string relative, string joints, string at) =>
        $" {fit,6}  {converged,-4} {iteration,4}  {unbalance,13}  {relative,10}  {joints,10}  {at}".TrimEnd();

    private void Iteration(FemEquilibriumProgress report, CaseLog log)
    {
        double unbalance = report.OutOfBalance!.Value, tolerance = report.Tolerance!.Value;
        string joints = $"{report.JointsInEquilibrium.ToString(Plain)} / {report.Joints.ToString(Plain)}";
        if (report.Linear)
        {
            Add();
            Add(Entry("SCALED BACKWARD ERROR OF THE SOLUTION", Number(unbalance, "0.000E+00")));
            Add(Entry("ACCEPTED BACKWARD ERROR", Number(tolerance, "0.000E+00")));
            Add(Entry("JOINTS CONVERGED", joints));
            return;
        }
        // An equilibrium counts its iterations from 1. In a fitting run the first equilibrium is that of the run's
        // pretension; those after it are trial pretensions, one cable at a time, for the response of the cables.
        bool newRun = report.FitRun != log.FitRun;
        if (newRun)
        {
            Trials(log);
            log.FitRun = report.FitRun;
            log.Equilibria = 0;
        }
        if (newRun || report.Iteration == 1) log.Equilibria++;
        if (report.FitRun > 0 && log.Equilibria > 1)
        {
            if (report.Iteration == 1) log.Trials++;
            log.TrialIterations++;
            return;
        }
        if (!log.Table)
        {
            log.Table = true;
            Add();
            Add(Title("Equilibrium iteration", now()));
            Add();
            Add(" Iterations are Newton-Raphson, with a line search on the potential energy");
            Add(" Relative Unbalance = Unbalance / largest applied load or cable pretension");
            Add();
            Add($" {"Fit",6}  {"Iteration",9}  {"Unbalance",13}  {"Relative",10}  {"Joints",10}  Largest");
            Add($" {"Run",6}  {"this Step",9}  {"[kN, kN-m]",13}  {"Unbalance",10}  {"Converged",10}  Unbalance At");
        }
        Add(Row(report.FitRun > 0 ? report.FitRun.ToString(Plain) : "-", unbalance <= tolerance ? "Conv" : "", report.Iteration.ToString(Plain),
            Number(unbalance, "0.000000E+00"), Number(unbalance / report.Scale, "0.000E+00"), joints,
            report.WorstJoint is { } joint ? $"{joint} {report.WorstDof}" : ""));
    }

    private void Trials(CaseLog log)
    {
        if (log.Trials > 0)
            Add($" {log.FitRun,6}  ({log.Trials} trial pretension{(log.Trials == 1 ? "" : "s")} for the response of the cables: {log.TrialIterations} iterations)");
        log.Trials = log.TrialIterations = 0;
    }

    private void Ending(FemEquilibriumProgress report, CaseLog log)
    {
        Trials(log);
        Add();
        if (report.State == FemEquilibriumState.Converged && report.Outcome is { } outcome)
        {
            // The solver returns a result only after factorizing the stiffness at the solution without a pivot that is
            // not positive: the stiffness is positive definite there, so it has no negative eigenvalue.
            Add(Rule);
            Add("    NUMBER OF NEGATIVE STIFFNESS EIGENVALUES SHOULD BE ZERO FOR STABILITY.");
            Add(report.Linear ? " BASIC STABILITY CHECK FOR THE STIFFNESS OF THE MODEL, FACTORIZED:"
                : " BASIC STABILITY CHECK FOR THE TANGENT STIFFNESS AT THE CONVERGED STATE:");
            Add("    (NOTE: FURTHER CHECKS SHOULD BE CONSIDERED AS DEEMED NECESSARY)");
            Add();
            Add(Stable);
            Add(Rule);
            Add();
            Add(Entry("SMALLEST SCALED PIVOT", Number(outcome.SmallestPivot, "0.000E+00")));
            Add(Entry("RESULTANT OF INTERNAL FORCES (RELATIVE)", Number(outcome.ResultantError, "0.000E+00")));
            if (report.FitStage && outcome.FitDeviationPercent is { } deviation)
            {
                Add(Entry("NUMBER OF FITTING RUNS", report.FitRun));
                Add(Entry("LARGEST DEVIATION FROM A TARGET (PERCENT)", Number(deviation, "0.000000")));
                if (!outcome.FitMet)
                {
                    Add();
                    Add(Warning);
                    Add(Wrap("CABLE TARGET FORCES NOT MET WITHIN THE TOLERANCE; THE CLOSEST PRETENSION FOUND IS USED. " + outcome.FitFailure));
                    Add();
                }
            }
            Add(Entry("TOTAL TIME FOR THIS ANALYSIS", Number((now() - log.Started).TotalSeconds, "0.00")));
            return;
        }
        Add(Error);
        Add(Wrap(report.Failure ?? "The case has no result."));
        Add();
        Add(report.State switch
        {
            FemEquilibriumState.Unstable => " CASE NOT SOLVED: THE MODEL IS UNSTABLE",
            FemEquilibriumState.NotConverged => " CASE NOT SOLVED: THE EQUILIBRIUM ITERATION DID NOT CONVERGE",
            _ => " CASE NOT SOLVED",
        });
    }

    public void Results(FemRunLogResults results)
    {
        lock (gate)
        {
            Add();
            Add(Title("Results", now()));
            Add();
            Add(Entry("CABLE TARGET FORCES", results.FitMet switch { null => "NOT FITTED", true => "MET", false => "NOT MET" }));
            if (results.FitMet is not null)
            {
                Add(Entry("NUMBER OF FITTING RUNS", results.FitRuns));
                Add(Entry("LARGEST DEVIATION FROM A TARGET (PERCENT)", Number(results.FitDeviationPercent, "0.000000")));
            }
            Add();
            Add(" LARGEST VALUES BY LOAD CASE AND COMBINATION:");
            Add();
            Add($" {"",-38}  {"Axial |N|",12}  {"Deflection",11}  {"Slack",8}");
            Add($" {"Case",-38}  {"[kN]",12}  {"Down [mm]",11}  {"Cables",8}");
            foreach (var item in results.Cases)
                Add($" {item.Name,-38}  {Number(item.LargestAxialKN, "0.00"),12}  {Number(item.LargestDeflectionMm, "0.00"),11}  {item.SlackCables.ToString(Plain),8}");
            if (results.OutputDatabase is { } database)
            {
                Add();
                Add(" OUTPUT DATABASE: " + database);
            }
        }
    }

    /// <summary>Closes the log of the run: complete, or incomplete with the reason — an error, or a plain
    /// <paramref name="note"/> such as a cancellation.</summary>
    public void End(bool complete, string? error = null, string? note = null)
    {
        lock (gate)
        {
            var ended = now();
            if (began == default) began = ended;   // the run stopped before it began: there is no time to report
            Add();
            if (error is not null)
            {
                Add(Error);
                Add(Wrap(error));
                Add();
            }
            if (note is not null)
            {
                Add(Wrap(note));
                Add();
            }
            Add(Entry("TOTAL TIME FOR THE RUN", Number((ended - began).TotalSeconds, "0.00")));
            Add();
            Add(Title(complete ? "Analysis complete" : "Analysis incomplete", ended, date: true));
        }
    }
}
