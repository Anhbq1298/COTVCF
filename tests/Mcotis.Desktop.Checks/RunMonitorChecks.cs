using Mcotis.Analysis;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation;

/// <summary>The run monitor against the solver it follows: every status, number and remark it shows of a load case is
/// what <see cref="FemSolver"/> reported of that case. The models are solved here; nothing shown is made up for the
/// check. The estimate itself is checked against closed forms in Mcotis.Checks (numerical stability diagnostics).</summary>
internal static class RunMonitorChecks
{
    private const double E = 200_000_000, Inertia = 0.0008, Area = 0.01;

    public static void Run()
    {
        Formatting();
        StablePlaneFrame();
        Mechanism();
        StiffnessContrast();
        NonConvergence();
        TargetsNotMet();
        CaseIndependence();
        PrerequisiteFailure();
        DegreesOfFreedom();
        Bookkeeping();
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    private static GeometryModel Geometry((string Name, double X, double Z)[] points, params (string Name, string Start, string End, MemberKind Kind)[] members) =>
        new(points.ToDictionary(point => point.Name, point => new StructuralPoint(point.Name, new(point.X, 0, point.Z)), StringComparer.Ordinal),
            members.Select(member => new StructuralMember(member.Name, member.Start, member.End, member.Kind)).ToArray());

    private static Dictionary<string, FemSectionProperties> Sections(params FemSectionProperties[] sections) =>
        sections.ToDictionary(section => section.MemberName, StringComparer.Ordinal);
    private static FemSectionProperties Frame(string name, double area = Area, bool hinged = false) => new(name, area, E, Inertia, hinged, hinged);
    private static FemSectionProperties Cable(string name) => new(name, 0.002, 1.6e8, null);
    private static FemCaseLoads Loads(string name, string point, double fx, double fz, double pretension = 0) =>
        new(name, new([new(point, fx, fz)], [], []), pretension);

    /// <summary>One run through the application's solver, followed by the monitor: what the monitor holds, what the
    /// view model shows of it, and whether the solver gave results.</summary>
    private static (FemRunSnapshot Run, FemRunMonitorViewModel Shown, IReadOnlyDictionary<string, FemAnalysisResult>? Results, string? Refusal) Solve(
        GeometryModel geometry, Dictionary<string, FemSectionProperties> frames, Dictionary<string, FemSectionProperties>? cables, string[] pins,
        FemCaseLoads[] cases, Dictionary<string, double>? targets = null, FemLoadSet? fitLoads = null, double? fitPercent = null, int fitIterations = 100,
        FemSolver? solver = null, Mcotis.Core.DofMask activeDofs = Mcotis.Core.DofMask.XZ, Mcotis.Core.DofMask cableDofs = Mcotis.Core.DofMask.Translations)
    {
        var monitor = new FemRunMonitor();
        IReadOnlyDictionary<string, FemAnalysisResult>? results = null;
        string? refusal = null;
        try
        {
            results = (solver ?? new FemSolver()).SolveCases(geometry, frames, cables ?? new Dictionary<string, FemSectionProperties>(), targets ?? new Dictionary<string, double>(), fitLoads ?? FemLoadSet.Empty, cases, pins,
                targetFitPercent: fitPercent, maximumTargetFitIterations: fitIterations, equilibrium: monitor, activeDofs: activeDofs, cableDofs: cableDofs);
        }
        catch (InvalidOperationException ex) { refusal = ex.Message; }
        monitor.End(completed: results is not null, note: refusal);
        var run = monitor.Snapshot();
        var shown = new FemRunMonitorViewModel();
        shown.Apply(run);
        // TEST 10: the monitor says of every case what the solver said, and results exist exactly when every case converged.
        bool allSolved = run.Cases.All(item => item.Status is FemCaseStatus.Converged or FemCaseStatus.ConvergedWithWarnings);
        if (allSolved != results is not null || run.Cases.Count == 0 ||
            run.Cases.Any(item => item.Status != FemRunMonitor.Classify(item.Last!) || item.Last!.State == FemEquilibriumState.Running ||
                (item.Status is FemCaseStatus.Converged or FemCaseStatus.ConvergedWithWarnings) != (item.Last.State == FemEquilibriumState.Converged && item.Last.Outcome is not null)) ||
            (run.Status is FemRunStatus.Completed or FemRunStatus.CompletedWithWarnings) != results is not null ||
            shown.Cases.Count != run.Cases.Count)
            throw new Exception("FAIL: the run monitor's statuses do not agree with what the solver returned: " +
                string.Join(", ", run.Cases.Select(item => $"{item.Label} {item.Status}/{item.Last?.State}")) + $"; results {(results is null ? "none" : "returned")}; run {run.Status}");
        return (run, shown, results, refusal);
    }

    private static double? Number(string text) => double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : null;
    private static string[] Cells(FemInstabilityRow row) => [row.UX.Text, row.UY.Text, row.UZ.Text, row.RX.Text, row.RY.Text, row.RZ.Text];

    private static void Formatting()
    {
        static string Digits(double value) => FemStabilityText.Digits(value, 6, 11);
        Check(Digits(3) == "3.0" && Digits(7.04) == "7.0" && Digits(13.26) == "13.3" && Digits(0.602) == "0.6",
            "digits lost are shown with one decimal");
        Check(Digits(5.96) == "5.96" && Digits(5.9996) == "5.9996" && Digits(6) == "6.0" && Digits(11) == "11.0" && Digits(11.04) == "11.04" && Digits(10.97) == "11.0",
            "… and with as many as it takes where one decimal would read as the other side of a threshold: 5.96 is not shown as 6.0, 11.04 not as 11.0");
        Check(FemStabilityText.Infinity == "∞" && FemStabilityText.Inactive == "-" && FemStabilityText.Unavailable == "N/A" && FemStabilityText.Restrained == "R",
            "a singularity is ∞ — not 16, 99 or 999 — an inactive degree of freedom is -, one without an estimate N/A, a restrained one R");
    }

    /// <summary>TEST 1 and TEST 8: a beam on two pins with a joint at mid-span.</summary>
    private static void StablePlaneFrame()
    {
        var geometry = Geometry([("A", 0, 0), ("B", 3, 0), ("C", 6, 0)], ("AB", "A", "B", MemberKind.Frame), ("BC", "B", "C", MemberKind.Frame));
        var frames = Sections(Frame("AB"), Frame("BC"));
        var (run, shown, results, _) = Solve(geometry, frames, null, ["A", "C"], [Loads("Load", "B", 2, -10)]);
        var only = run.Cases.Single();
        Check(results is not null && run is { Status: FemRunStatus.Completed, ActiveDofs: 9, SolvedDofs: 5, RestrainedDofs: 4, Fraction: 1 } &&
              only is { Status: FemCaseStatus.Converged, Label: "Load", Stability: { Passed: true, Instabilities: 0, Equations: 5, Joints.Count: 0 } },
            "a stable plane frame: Converged, numerical stability passed on its 5 equations, zero instabilities — of 9 active degrees of freedom, 4 restrained by the two pins");
        var item = shown.Cases.Single();
        Check(shown.StatusText == "Completed" && shown.DofText == "9 active | 5 solved | 4 restrained" && item is { StatusText: "Converged", ConvergenceText: "Passed", StabilityText: "Passed", InstabilitiesText: "0", HasRows: false, IsExpanded: false } &&
              item.Details.Any(field => field is { Label: "Analysis type", Value: "Linear static" }) && item.Details.Any(field => field.Label == "Scaled backward error" && field.Value.Contains("accepted", StringComparison.Ordinal)) &&
              !item.Details.Any(field => field.Value is "0" or "0.00E+00" && field.Label.Contains("error", StringComparison.OrdinalIgnoreCase)),
            $"… shown as Convergence: Passed · Numerical Stability: Passed · Instabilities: 0, with the backward error as the solver measured it ({item.Details.Single(field => field.Label == "Scaled backward error").Value}), not as a zero");

        // the same run reporting from 0.1 digits: every joint with a degree of freedom above that is listed
        var listed = Solve(geometry, frames, null, ["A", "C"], [Loads("Load", "B", 2, -10)], solver: new FemSolver { StabilityThresholds = new(0.1, 11) });
        var rows = listed.Shown.Cases.Single().Rows;
        Check(listed.Run.Cases.Single().Status == FemCaseStatus.ConvergedWithWarnings && rows.Select(row => row.Joint).SequenceEqual(["A", "B", "C"]) &&
              rows[0] is { X: 0, Y: 0, Z: 0 } && rows[1] is { X: 3, Y: 0, Z: 0 } && rows[2] is { X: 6, Y: 0, Z: 0 },
            "with a lower threshold the same case is Converged with Warnings and lists its joints by name with their global coordinates — the thresholds are options of the solver");
        Check(Cells(rows[0]) is ["R", "-", "R", "-", var endRotation, "-"] && Number(endRotation) is not null &&
              Cells(rows[1]) is [var ux, "-", var uz, "-", var ry, "-"] && ux == "0.0" && Number(uz) is not null && Number(ry) is not null &&
              rows.All(row => row.UY.Level == FemCellLevel.Muted && row.RX.Level == FemCellLevel.Muted && row.RZ.Level == FemCellLevel.Muted),
            $"… all six columns are there; in the XZ plane UX, UZ, RY carry values, UY, RX, RZ read '-' and the pinned translations 'R': A = {string.Join(" ", Cells(rows[0]))}, B = {string.Join(" ", Cells(rows[1]))}");
        Check(results!["Load"].Joints.Zip(listed.Results!["Load"].Joints).All(pair => pair.First == pair.Second),
            "… and the threshold changes no digit of the results");
    }

    /// <summary>TEST 3 and TEST 6: a portal on two pins whose beam is hinged at both ends sways freely.</summary>
    internal static (FemRunSnapshot Run, FemRunMonitorViewModel Shown, IReadOnlyDictionary<string, FemAnalysisResult>? Results, string? Refusal) SwayingPortal() =>
        Solve(Geometry([("A", 0, 0), ("B", 0, 3), ("C", 4, 3), ("D", 4, 0)], ("AB", "A", "B", MemberKind.Frame), ("BC", "B", "C", MemberKind.Frame), ("DC", "D", "C", MemberKind.Frame)),
            Sections(Frame("AB"), Frame("BC", hinged: true), Frame("DC")), null, ["A", "D"], [Loads("Sway", "B", 10, 0)]);

    private static void Mechanism()
    {
        var (run, shown, results, refusal) = SwayingPortal();
        var only = run.Cases.Single();
        var stability = only.Stability!;
        Check(results is null && refusal is not null && run.Status == FemRunStatus.Failed && only.Status == FemCaseStatus.NumericalInstability &&
              stability is { State: StabilityState.Singular, ZeroStiffnessModes: 1, Instabilities: 6, Warnings: 0, Equations: 8 },
            "a portal that sways freely has no results and none are made up: Numerical Instability — a singular stiffness, one movement without stiffness, 6 degrees of freedom in it");
        var item = shown.Cases.Single();
        var rows = item.Rows;
        Check(item is { StatusText: "Numerical Instability", ConvergenceText: "No result", InstabilitiesText: "6", HasRows: true, IsExpanded: true, HasFailure: true } &&
              item.StabilityText == "Singular stiffness — 1 movement without stiffness" && rows.Select(row => row.Joint).SequenceEqual(["A", "B", "C", "D"]) &&
              rows[1] is { X: 0, Y: 0, Z: 3 } && rows[2] is { X: 4, Y: 0, Z: 3 },
            "… the case opens itself and names the joints: A, B, C, D with their coordinates");
        Check(Cells(rows[0]).SequenceEqual(["R", "-", "R", "-", "∞", "-"]) && Cells(rows[1]).SequenceEqual(["∞", "-", "N/A", "-", "∞", "-"]) &&
              Cells(rows[2]).SequenceEqual(["∞", "-", "N/A", "-", "∞", "-"]) && Cells(rows[3]).SequenceEqual(["R", "-", "R", "-", "∞", "-"]) &&
              rows.All(row => row.RY.Level == FemCellLevel.Infinite) && rows[1].UZ.Level == FemCellLevel.Muted,
            "… ∞ at the degrees of freedom that move in the mechanism — the rotation of the feet, the sway and rotation of the heads — N/A where a singular stiffness has no estimate, R at the pins, and nothing against UY, RX, RZ");
        Check(rows[0].Remarks == "RY: Singular (∞) — possible mechanism" && rows[1].Remarks == "UX, RY: Singular (∞) — possible mechanism" &&
              rows.All(row => !row.Remarks.Contains("16", StringComparison.Ordinal) && !row.Remarks.Contains("99", StringComparison.Ordinal)),
            $"… the remarks name the degrees of freedom, as a possible mechanism: '{rows[1].Remarks}'");
        Check(only.Log.Any(entry => entry.Level == FemLogLevel.Error && entry.Text.StartsWith("Numerical stability checked: Singular stiffness — 1 movement without stiffness; 6 instabilities, 0 accuracy warnings.", StringComparison.Ordinal)) &&
              only.Log.Any(entry => entry.Text == "B UX: Singular (∞) — possible mechanism.") && only.Log[^1].Text == "Analysis ended without a result: numerical instability.",
            "… and the log of the case says the same, degree of freedom by degree of freedom");
    }

    /// <summary>TEST 5: a soft frame to the support and a stiff one beyond it, on a column held by two pins.</summary>
    private static void StiffnessContrast()
    {
        (FemRunSnapshot Run, FemRunMonitorViewModel Shown, IReadOnlyDictionary<string, FemAnalysisResult>? Results, string? Refusal) Chain(double ratio) =>
            Solve(Geometry([("A", 0, 0), ("B", 4, 0), ("C", 8, 0), ("D", 0, -3)],
                    ("AB", "A", "B", MemberKind.Frame), ("BC", "B", "C", MemberKind.Frame), ("DA", "D", "A", MemberKind.Frame)),
                Sections(Frame("AB", 1e-4), Frame("BC", 1e-4 * ratio), Frame("DA")), null, ["A", "D"], [Loads("Pull", "C", 5, -1)]);

        var warned = Chain(1e7);
        var item = warned.Shown.Cases.Single();
        Check(warned.Results is not null && warned.Run.Status == FemRunStatus.CompletedWithWarnings && item is { StatusText: "Converged with Warnings", ConvergenceText: "Passed", InstabilitiesText: "0" } &&
              item.StabilityText == "Numerical accuracy warning" && item.StabilityLevel == FemCellLevel.Warning &&
              item.Rows.Select(row => row.Joint).SequenceEqual(["B", "C"]) && item.Rows.All(row => row.UX is { Text: "7.0", Level: FemCellLevel.Warning } && row.Remarks == "UX: Numerical accuracy warning (7.0 digits)"),
            "a stiffness contrast of 1e7 converges — its nonlinear convergence is not in question — with a numerical accuracy warning: 7.0 digits lost at UX of B and C, in amber, zero instabilities");
        Check(item.Rows.All(row => row.UZ.Level == FemCellLevel.Normal && row.RY.Level == FemCellLevel.Normal && Number(row.UZ.Text) < 6) &&
              item.Details.Any(field => field is { Label: "Largest loss of accuracy", Value: "7.0 digits at B UX" or "7.0 digits at C UX" }),
            "… the other degrees of freedom of those joints are listed with their own small values, uncoloured");

        var withheld = Chain(1e10);
        var refusedItem = withheld.Shown.Cases.Single();
        Check(withheld.Results is null && withheld.Run.Cases.Single() is { Status: FemCaseStatus.NumericalInstability, Last.FailureCode: "static.equilibrium" } &&
              refusedItem.Rows.All(row => row.UX is { Text: "10.0", Level: FemCellLevel.Warning }) && refusedItem.Rows.SelectMany(Cells).All(text => text != "∞") &&
              refusedItem.HasFailure && refusedItem.FailureText!.Contains("do not balance", StringComparison.Ordinal),
            "a contrast of 1e10: the solver withholds the result because its internal forces no longer balance; the monitor reads Numerical Instability with 10.0 digits at UX — a finite number, no ∞");

        var nearly = Chain(1e13);
        var nearlyItem = nearly.Shown.Cases.Single();
        Check(nearly.Results is null && nearly.Run.Cases.Single() is { Status: FemCaseStatus.NumericalInstability, Stability.State: StabilityState.NearlySingular } &&
              nearlyItem.StabilityText == "Nearly singular stiffness" && nearlyItem.InstabilitiesText == "2" &&
              nearlyItem.Rows.All(row => row.UX is { Text: "13.0", Level: FemCellLevel.Severe } && row.Remarks == "UX: Nearly singular (13.0 digits)") &&
              nearlyItem.Rows.SelectMany(Cells).All(text => text != "∞"),
            "a contrast of 1e13 is refused as nearly singular: 13.0 digits at UX of B and C, in red — a small pivot is not reported as ∞");
    }

    /// <summary>TEST 7: a cable equilibrium stopped at its iteration limit.</summary>
    private static void NonConvergence()
    {
        var geometry = Geometry([("A", -5, 0), ("B", 0, 0), ("C", 5, 0), ("D", 0, 5)],
            ("AB", "A", "B", MemberKind.Frame), ("BC", "B", "C", MemberKind.Frame), ("Cable-BD", "B", "D", MemberKind.Cable));
        var (frames, cables) = (Sections(Frame("AB"), Frame("BC")), Sections(Cable("Cable-BD")));
        var stopped = Solve(geometry, frames, cables, ["A", "C", "D"], [Loads("Down", "B", 0, -50)], solver: new FemSolver { MaximumEquilibriumIterations = 1 });
        var only = stopped.Run.Cases.Single();
        var item = stopped.Shown.Cases.Single();
        Check(stopped.Results is null && only is { Status: FemCaseStatus.NotConverged, Stability: null, Last: { FailureCode: "static.nonlinear.noConvergence", Iteration: 1 } } &&
              item is { StatusText: "Not Converged", ConvergenceText: "Not converged", StabilityText: "Not assessed", InstabilitiesText: "—", HasRows: false, IsExpanded: true } &&
              item.Details.Any(field => field is { Label: "Nonlinear convergence", Value: "Not converged" }) && item.Details.Any(field => field is { Label: "Iterations", Value: "1" }) &&
              item.Details.Any(field => field is { Label: "Convergence tolerance", Value: "Not satisfied" }),
            "a case stopped at its iteration limit reads Not Converged, with its iterations and the tolerance not satisfied; its numerical stability is not assessed, and it is not called an instability");
        Check(only.Log.Any(entry => entry.Text == "Numerical stability not assessed: the iteration reached no converged state.") &&
              only.Log[^1].Text == "Analysis ended without a result: the equilibrium iteration did not converge." && stopped.Run.Status == FemRunStatus.Failed,
            "… and its log ends on the iteration that did not converge");
        var solved = Solve(geometry, frames, cables, ["A", "C", "D"], [Loads("Down", "B", 0, -50)]);
        Check(solved.Results is not null && solved.Shown.Cases.Single() is { StatusText: "Converged", ConvergenceText: "Passed", StabilityText: "Passed", InstabilitiesText: "0" } done &&
              done.Details.Any(field => field is { Label: "Nonlinear convergence", Value: "Converged" }) && done.Details.Any(field => field is { Label: "Convergence tolerance", Value: "Satisfied" }) &&
              done.Details.Any(field => field.Label == "Out-of-balance" && !field.Value.StartsWith("0 ", StringComparison.Ordinal) && field.Value.Contains("E-", StringComparison.Ordinal)),
            "given its iterations the same case reads Converged, with its nonlinear convergence, the tolerance satisfied and the out-of-balance as measured — within the tolerance, not written as zero");
    }

    /// <summary>Cable target forces that cannot be met: converged, with a warning.</summary>
    private static void TargetsNotMet()
    {
        var geometry = Geometry([("A", -5, 0), ("B", 0, 0), ("C", 5, 0), ("D", 0, 5), ("E", 2, 5)],
            ("AB", "A", "B", MemberKind.Frame), ("BC", "B", "C", MemberKind.Frame), ("Cable-BD", "B", "D", MemberKind.Cable), ("Cable-BE", "B", "E", MemberKind.Cable));
        var run = Solve(geometry, Sections(Frame("AB"), Frame("BC")), Sections(Cable("Cable-BD"), Cable("Cable-BE")), ["A", "C", "D", "E"],
            [Loads("Service", "B", 0, -10, pretension: 1)], new() { ["Cable-BD"] = 100, ["Cable-BE"] = 0.5 }, fitPercent: 1e-9, fitIterations: 1);
        var fit = run.Shown.Cases[0];
        Check(run.Results is not null && run.Run.Status == FemRunStatus.CompletedWithWarnings && run.Run.Cases.Select(item => item.Label).SequenceEqual(["Cable target-force fit", "Service"]) &&
              fit is { StatusText: "Converged with Warnings", ConvergenceText: "Passed", StabilityText: "Passed", InstabilitiesText: "0" } &&
              fit.Details.Any(field => field.Label == "Cable target forces" && field.Value.StartsWith("Not met — closest pretension used", StringComparison.Ordinal)) &&
              run.Run.Cases[0].Log.Any(entry => entry.Level == FemLogLevel.Warning && entry.Text.StartsWith("Cable target forces not met within the tolerance", StringComparison.Ordinal)) &&
              run.Shown.Cases[1] is { StatusText: "Converged with Warnings", ConvergenceText: "Passed", InstabilitiesText: "0" },
            "cable target forces that are not met within the tolerance: the fit reads Converged with Warnings — a warning, not a failure — and so does the case that carries that pretension: " +
            string.Join(" | ", run.Shown.Cases.Select(item => $"{item.Name}: {item.StatusText}, {string.Join("; ", item.Details.Where(field => field.Label == "Cable target forces").Select(field => field.Value))}")));
    }

    /// <summary>TEST 9: a cable joint that only slack cables reach when the beam is pushed up.</summary>
    private static void CaseIndependence()
    {
        var geometry = Geometry([("A", -5, 0), ("B", 0, 0), ("C", 5, 0), ("M", 0, 2.5), ("P", -2, 5), ("Q", 2, 5)],
            ("AB", "A", "B", MemberKind.Frame), ("BC", "B", "C", MemberKind.Frame),
            ("Cable-BM", "B", "M", MemberKind.Cable), ("Cable-MP", "M", "P", MemberKind.Cable), ("Cable-MQ", "M", "Q", MemberKind.Cable));
        var run = Solve(geometry, Sections(Frame("AB"), Frame("BC")), Sections(Cable("Cable-BM"), Cable("Cable-MP"), Cable("Cable-MQ")), ["A", "C", "P", "Q"],
            [Loads("Up", "B", 0, 80), Loads("Down", "B", 0, -50), Loads("Up again", "B", 0, 40)]);
        var (up, down, again) = (run.Shown.Cases[0], run.Shown.Cases[1], run.Shown.Cases[2]);
        Check(run.Results is null && run.Run.Cases.Select(item => item.Status).SequenceEqual([FemCaseStatus.NumericalInstability, FemCaseStatus.Converged, FemCaseStatus.NumericalInstability]) &&
              run.Run.Status == FemRunStatus.Failed && run.Run.Cases.Select(item => item.Number).SequenceEqual([1, 2, 3]),
            "of three independent load cases the first and the third fail and the second does not: each has its own status — Numerical Instability, Converged, Numerical Instability");
        Check(up.Rows.Single() is { Joint: "M", X: 0, Y: 0, Z: 2.5 } hung && Cells(hung).SequenceEqual(["∞", "-", "∞", "-", "-", "-"]) &&
              hung.Remarks == "UX, UZ: Singular (∞) — held by slack cables only" && down is { StabilityText: "Passed", InstabilitiesText: "0", HasRows: false, IsExpanded: false } &&
              up.IsExpanded && again.IsExpanded,
            "… pushed up, the joint M between the cables is held by slack cables only: UX and UZ singular; its rotation is not a degree of freedom of a cable joint. The failed cases open themselves, the converged one stays as it was");
        Check(run.Run.Cases.All(item => item.Started is not null && item.Ended is not null && item.Log.Count > 3) && run.Run.Cases[1].Started == run.Run.Cases[0].Ended,
            "… every case has its own times and its own log, one after the other");
    }

    /// <summary>TEST 9: a cable in two segments through a joint nothing holds sideways — the fit of its target forces
    /// fails, and so does the case that continues from it.</summary>
    private static void PrerequisiteFailure()
    {
        var geometry = Geometry([("A", -5, 0), ("B", 0, 0), ("C", 5, 0), ("M", 0, 2.5), ("D", 0, 5)],
            ("AB", "A", "B", MemberKind.Frame), ("BC", "B", "C", MemberKind.Frame), ("Cable-BM", "B", "M", MemberKind.Cable), ("Cable-MD", "M", "D", MemberKind.Cable));
        var run = Solve(geometry, Sections(Frame("AB"), Frame("BC")), Sections(Cable("Cable-BM"), Cable("Cable-MD")), ["A", "C", "D"],
            [Loads("Service", "B", 0, -50, pretension: 1), Loads("Unstressed", "B", 0, -5)], new() { ["Cable-BM"] = 100, ["Cable-MD"] = 100 }, fitPercent: 1);
        var (fit, service, unstressed) = (run.Run.Cases[0], run.Run.Cases[1], run.Run.Cases[2]);
        Check(run.Results is null && fit is { FitStage: true, Status: FemCaseStatus.NumericalInstability } && service is { Status: FemCaseStatus.Failed, Last.PrerequisiteFailed: true } &&
              run.Shown.Cases[1].FailureText!.StartsWith("The cable target-force fit this case continues from failed.", StringComparison.Ordinal) &&
              service.Log.Any(entry => entry.Level == FemLogLevel.Error && entry.Text.StartsWith("Not solved — the cable target-force fit this case continues from failed:", StringComparison.Ordinal)),
            "a case that continues from a cable target-force fit that failed reads Failed and says where it failed — in the fit, which has its own entry reading Numerical Instability");
        Check(unstressed is { Status: FemCaseStatus.NumericalInstability, Last.PrerequisiteFailed: false } && unstressed.Stability!.Joints.Single().Joint == "M",
            "… while a case that does not continue from the fit is judged on its own solution: the same joint M is unstable in it, and it says so itself");
    }

    /// <summary>The degrees of freedom of Analysis Options: one that is switched off is held at every joint and is no
    /// equation; a load on it is refused; out of the plane there is none.</summary>
    private static void DegreesOfFreedom()
    {
        var geometry = Geometry([("A", 0, 0), ("B", 3, 0), ("C", 6, 0)], ("AB", "A", "B", MemberKind.Frame), ("BC", "B", "C", MemberKind.Frame));
        var frames = Sections(Frame("AB"), Frame("BC"));
        FemCaseLoads[] load = [Loads("Load", "B", 0, -10)];
        var free = Solve(geometry, frames, null, ["A", "C"], load);
        // RY off: no joint turns, so each half of the beam is built in at one end and guided at the other, 12EI/L³ each.
        var guided = Solve(geometry, frames, null, ["A", "C"], load, activeDofs: Mcotis.Core.DofMask.UX | Mcotis.Core.DofMask.UZ, solver: new FemSolver { StabilityThresholds = new(1e-9, 11) });
        double expected = -10 * 27 / (24 * E * Inertia), moved = guided.Results!["Load"].Joints.Single(joint => joint.PointName == "B").UZM;
        Check(guided.Results is not null && Math.Abs(moved - expected) < 1e-12 && guided.Results["Load"].Joints.All(joint => joint.RotationRad == 0) &&
              Math.Abs(free.Results!["Load"].Joints.Single(joint => joint.PointName == "B").UZM - -10 * 216 / (48 * E * Inertia)) < 1e-12,
            $"with RY switched off the joints do not turn: the mid-span joint moves PL³/24EI = {expected * 1000:0.#####} mm ({moved * 1000:0.#####} mm), against PL³/48EI of the span with RY on");
        Check(guided.Run is { ActiveDofs: 6, SolvedDofs: 2, RestrainedDofs: 4 } && free.Run is { ActiveDofs: 9, SolvedDofs: 5, RestrainedDofs: 4 } &&
              guided.Shown.DofText == "6 active | 2 solved | 4 restrained",
            "… and the run monitor counts the degrees of freedom of the run as it was assembled: 6 active | 2 solved | 4 restrained, against 9 | 5 | 4");
        // where joints are listed — a stiffness contrast of 1e7 along the beam — the rotation that is off reads '-'
        var listed = Solve(Geometry([("A", 0, 0), ("B", 4, 0), ("C", 8, 0), ("D", 0, -3)],
                ("AB", "A", "B", MemberKind.Frame), ("BC", "B", "C", MemberKind.Frame), ("DA", "D", "A", MemberKind.Frame)),
            Sections(Frame("AB", 1e-4), Frame("BC", 1e-3 * 1e6), Frame("DA")), null, ["A", "D"], [Loads("Pull", "C", 5, -1)],
            activeDofs: Mcotis.Core.DofMask.UX | Mcotis.Core.DofMask.UZ);
        var rows = listed.Shown.Cases.Single().Rows;
        Check(listed.Results is not null && rows.Select(row => row.Joint).SequenceEqual(["B", "C"]) &&
              rows.All(row => row.UX is { Text: "7.0", Level: FemCellLevel.Warning } && row.RY is { Text: "-", Level: FemCellLevel.Muted } && row.UY.Text == "-" && Number(row.UZ.Text) is not null),
            "… where joints are listed, a degree of freedom that is off reads '-': nothing is reported against it — B and C, UX 7.0, RY -");

        string Refusal(Mcotis.Core.DofMask active, FemCaseLoads[] cases)
        {
            try { new FemSolver().SolveCases(geometry, frames, new Dictionary<string, FemSectionProperties>(), new Dictionary<string, double>(), FemLoadSet.Empty, cases, ["A", "C"], activeDofs: active); }
            catch (InvalidOperationException ex) { return ex.Message; }
            return "";
        }
        string turned = Refusal(Mcotis.Core.DofMask.UX | Mcotis.Core.DofMask.UZ, [new("Moment", new([new("B", 0, -10, 4)], [], []), 0)]);
        Check(turned.Contains("'B'", StringComparison.Ordinal) && turned.Contains("RY", StringComparison.Ordinal) && turned.Contains("inactive", StringComparison.OrdinalIgnoreCase),
            "a moment on a joint whose rotation is switched off is refused, not dropped: " + turned);
        string sideways = Refusal(Mcotis.Core.DofMask.UZ | Mcotis.Core.DofMask.RY, load);
        Check(sideways.Contains("lies outside the common plane/line required when UX is inactive", StringComparison.Ordinal),
            "a translation of the plane cannot be switched off for a model that spans it — which is why Analysis Options does not offer it: " + sideways[..Math.Min(sideways.Length, 110)] + "…");
        Check(Refusal(Mcotis.Core.DofMask.Full3D, load).Contains("planar", StringComparison.Ordinal) && Refusal(Mcotis.Core.DofMask.RY, load).Contains("planar", StringComparison.Ordinal),
            "degrees of freedom out of the plane, or no translation at all, are refused before anything is solved: this model is planar");
    }

    /// <summary>What the monitor does with a run as a whole: the solves it plans, a run that is cancelled, a run that stops.</summary>
    private static void Bookkeeping()
    {
        var time = new DateTime(2026, 10, 13, 10, 8, 15);
        var monitor = new FemRunMonitor(() => time);
        var model = new FemRunModelInfo("Tied arch.cotfem.db", 12, ["A", "D"], 9, 4, 0, 3, 5, 2, 3, 1, 0, []);
        monitor.Begin(model, [("Pretension", true), ("Pretension", false), ("SLS", false)]);
        var planned = monitor.Snapshot();
        Check(planned is { Status: FemRunStatus.Running, Fraction: 0, SolvedDofs: 0 } && planned.Started == time &&
              planned.Cases.Select(item => (item.Number, item.Label, item.Status)).SequenceEqual(
                  [(1, "Pretension — cable target-force fit", FemCaseStatus.Pending), (2, "Pretension", FemCaseStatus.Pending), (3, "SLS", FemCaseStatus.Pending)]) &&
              ReferenceEquals(planned.Model, model),
            "a run starts with every solve it will make waiting as Pending, in order, the fit of the cable target forces as a solve of its own");
        var shown = new FemRunMonitorViewModel();
        shown.Apply(planned, time);
        Check(shown is { StatusText: "Running", IsIndeterminate: true, DofText: "", ModelName: "Tied arch.cotfem.db" } && shown.Cases.All(item => item is { StatusText: "Pending", ElapsedText: "", IsExpanded: false }) &&
              shown.ModelLeft.Select(field => field.Label).SequenceEqual(["Number of Joints", "Number of Restrained Joints", "Number of Frame Members", "Number of Cable Members", "Number of Shell Elements"]) &&
              shown.ModelRight.Select(field => field.Label).SequenceEqual(["Number of Load Patterns", "Number of Joint Load Assignments", "Number of Member Load Assignments", "Number of Load Cases", "Number of Load Combinations"]) &&
              shown.ModelLeft.Select(field => field.Value).SequenceEqual(["12", "2", "9", "4", "0"]) && shown.ModelRight.Select(field => field.Value).SequenceEqual(["3", "5", "2", "3", "1"]) &&
              shown.SupportSummary == "2 pin supports (UX, UZ restrained; rotation free): A, D",
            "the model information is the model's file name and its counts in two columns, load cases and combinations counted apart, and the supports by name — no solver name, no source");

        FemEquilibriumProgress Report(string name, bool fitStage, FemEquilibriumState state, int iteration = 0, double? unbalance = null, double fraction = 0) =>
            new(name, fitStage, state, 30, 4, 26, fitStage && state == FemEquilibriumState.Running ? 1 : 0, iteration, false, unbalance, unbalance is null ? null : 1e-6, 10, 4, "B", "UZ", fraction);
        time = time.AddSeconds(2);
        monitor.Report(Report("Pretension", true, FemEquilibriumState.Running));
        monitor.Report(Report("Pretension", true, FemEquilibriumState.Running, 1, 3.5));
        time = time.AddSeconds(3);
        var going = monitor.Snapshot();
        shown.Apply(going, time);
        Check(going.Cases[0] is { Status: FemCaseStatus.Running } first && first.Started == planned.Started && going is { ActiveDofs: 30, SolvedDofs: 26, RestrainedDofs: 4 } &&
              shown.Cases[0] is { StatusText: "Running — fitting run 1", ElapsedText: "00:00:05" } && shown.ElapsedText == "00:00:05" && shown.DofText == "30 active | 26 solved | 4 restrained" &&
              ReferenceEquals(going.Cases[1], planned.Cases[1]),
            "while a case runs the monitor shows it Running with its time so far, and the degrees of freedom as the solver assembled them; a case that has not changed is not rebuilt");

        // a case merely reported, never finished, is not converged: cancel
        monitor.End(completed: false, cancelled: true, note: "The analysis was cancelled.");
        var cancelled = monitor.Snapshot();
        shown.Apply(cancelled, time);
        Check(cancelled.Status == FemRunStatus.Cancelled && cancelled.Cases.All(item => item.Status == FemCaseStatus.Cancelled) &&
              cancelled.Cases[0].Log[^1].Text == "Stopped: the analysis was cancelled." && cancelled.Cases[2].Note == "Not run: the analysis was cancelled." &&
              shown.StatusText == "Cancelled" && shown.Cases.All(item => item.StatusText == "Cancelled" && item.ConvergenceText == "Not completed" && !item.IsExpanded) && !shown.IsIndeterminate,
            "a cancelled run reads Cancelled: the case that was running is stopped, the ones waiting were not run — none of them is Converged because the solver returned");

        // without results a case that reported no end is not converged either
        var stopped = new FemRunMonitor(() => time);
        stopped.Begin(model, [("SLS", false), ("ULS", false)]);
        stopped.Report(Report("SLS", false, FemEquilibriumState.Running, 3, 0.2));
        stopped.End(completed: false, note: "Member load on 'X', which is not an analyzed frame or cable.");
        var ended = stopped.Snapshot();
        Check(ended.Status == FemRunStatus.Failed && ended.Message!.StartsWith("Member load on 'X'", StringComparison.Ordinal) &&
              ended.Cases[0] is { Status: FemCaseStatus.Cancelled, Note: "Stopped: the run ended before this case had finished." } &&
              ended.Cases[1] is { Status: FemCaseStatus.Cancelled, Note: "Not run: the run stopped before this case." },
            "a run that stops on an error outside a case reads Failed with the reason; its cases read Cancelled — stopped, or not run");

        // iteration lines are kept up to a limit
        var many = new FemRunMonitor(() => time);
        for (int iteration = 1; iteration <= FemRunMonitor.MaximumIterationLines + 40; iteration++) many.Report(Report("Long", false, FemEquilibriumState.Running, iteration, 1.0 / iteration));
        many.Report(Report("Long", false, FemEquilibriumState.NotConverged, FemRunMonitor.MaximumIterationLines + 40, 1e-3) with { Failure = "No convergence.", FailureCode = "static.nonlinear.noConvergence" });
        var log = many.Snapshot().Cases.Single().Log;
        Check(log.Count(entry => entry.Text.StartsWith("Iteration ", StringComparison.Ordinal)) == FemRunMonitor.MaximumIterationLines &&
              log.Any(entry => entry.Text == "40 further iteration lines are not shown."),
            $"the log of a case keeps {FemRunMonitor.MaximumIterationLines} iteration lines and says how many it left out");
    }
}
