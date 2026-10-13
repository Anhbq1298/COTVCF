using System.Collections.Immutable;
using Mcotis.Analysis;
using Mcotis.Core;
using static Check;

/// <summary>The numerical stability diagnostics of the static solver (docs/solver/numerical-stability-diagnostics.md):
/// the digits-lost estimate against closed forms, against an inverse worked out here and against the solver's own
/// displacements; then what is reported of stable, ill-conditioned, nearly singular and singular models, of a case
/// that does not converge, of degrees of freedom that are off, and of one case among several that fails.</summary>
internal static class StabilityDiagnosticsChecks
{
    private static ObjectId Id(int group, int number = 1) => ObjectId.Parse($"00000000-0000-0000-{group:D4}-{number:D12}");
    private static readonly ObjectId Steel = Id(41), Strand = Id(41, 2), Rope = Id(42, 1000), Prestress = Id(43, 1000);
    private const double E = 2e8, A = .01, I33 = .0008, I22 = .0004, J = .00001;
    /// <summary>Reports every degree of freedom of a model that loses anything at all, so that its estimates can be read.</summary>
    private static readonly StaticOptions Everything = new(Stability: new(1e-9, 11));
    private static readonly double Log4 = Math.Log10(4);

    public static void Run()
    {
        Thresholds();
        EstimateOfAMatrix();
        SingularMatrices();
        StablePlaneFrame();
        StableSpaceFrame();
        EstimateIsAStiffnessRatio();
        IllConditionedAndNearlySingular();
        PlaneMechanism();
        UnrestrainedBody();
        UnrestrainedRotation();
        NoStiffnessAtAll();
        NonConvergenceIsNotInstability();
        CasesAreIndependent();
        UnloadedMovementsHeld();
    }

    private sealed class Model
    {
        public readonly DocumentEditor Editor = ModelDocument.Create().Edit();
        private int points, frames, cables, loads, cases, sections, patterns;

        public Model(DofMask mask, DofMask cableDofs = DofMask.Translations)
        {
            Editor.PutMaterial(new(Steel, "Steel", MaterialType.Steel, E, .3, 0, 0));
            Editor.PutMaterial(new(Strand, "Strand", MaterialType.Tendon, 1.6e8, .3, 0, 0));
            Editor.PutCableSection(new(Rope, "Rope", Strand, .002));
            Editor.PutLoadPattern(new(Prestress, "PT", LoadPatternType.Prestress));
            Editor.SetSettings(new AnalysisSettings(1e-6, 100, 1, false) { ActiveDofs = mask, CableDofs = cableDofs });
        }

        public ObjectId Point(double x, double y, double z, Restraint? restraint = null)
        {
            var id = Id(44, ++points);
            Editor.AddPoint(new(x, y, z), "J" + points, id);
            if (restraint is { } value) Editor.SetRestraint([id], value);
            return id;
        }

        public ObjectId Frame(ObjectId start, ObjectId end, double area = A, bool pinned = false)
        {
            var section = Id(42, ++sections);
            Editor.PutFrameSection(new(section, "S" + sections, Steel, FrameSectionShape.General, new(area, I33, I22, J)));
            var id = Id(45, ++frames);
            Editor.AddFrame(start, end, "F" + frames, id);
            Editor.SetFrameSection([id], section);
            var release = new EndRelease(false, false, false, false, true, true);
            if (pinned) Editor.SetFrameReleases([id], new(release, release));
            return id;
        }

        public ObjectId Cable(ObjectId start, ObjectId end, double? target = null)
        {
            var id = Id(46, ++cables);
            Editor.AddCable(start, end, "C" + cables, id);
            Editor.SetCableSection([id], Rope);
            if (target is { } force) Editor.PutLoad(new CableTargetForce(Id(47, ++loads), Prestress, id, force));
            return id;
        }

        /// <summary>A pattern holding one load, and the case that applies it.</summary>
        public ObjectId Case(string name, bool linear, ObjectId point, Xyz force, Xyz moment = default, ObjectId? initial = null)
        {
            var pattern = Id(43, ++patterns);
            Editor.PutLoadPattern(new(pattern, "P" + patterns, LoadPatternType.Other));
            Editor.PutLoad(new PointLoad(Id(47, ++loads), pattern, point, force, moment));
            var id = Id(48, ++cases);
            Editor.PutLoadCase(new(id, name, linear ? LoadCaseType.LinearStatic : LoadCaseType.NonlinearStatic, [new(pattern, 1)]) { InitialCaseId = initial });
            return id;
        }

        public ObjectId FitCase(string name)
        {
            var id = Id(48, ++cases);
            Editor.PutLoadCase(new(id, name, LoadCaseType.NonlinearStatic, [new(Prestress, 1)]) { FitCableTargetForces = true });
            return id;
        }

        public ModelDocument Build() => Editor.Commit().Document;
    }

    private sealed class Reports : IProgress<StaticProgress>
    {
        public readonly List<StaticProgress> Items = [];
        public void Report(StaticProgress value) => Items.Add(value);
    }

    private static CompiledModel Compile(ModelDocument document)
    {
        var compiled = ModelCompiler.Compile(document, 3);
        Assert(compiled.Success, "The model of the check compiles: " + string.Join("; ", compiled.Issues));
        return compiled.Model!;
    }

    private static void Near(double actual, double expected, string description, double tolerance = 1e-9) =>
        Assert(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"{description}: {actual:G17} vs {expected:G17}");

    private static string Name(ModelDocument document, DofKey key) => $"{document.Points[key.PointId].Name} {key.Component}";
    private static string[] Where(ModelDocument document, StaticStability stability, Func<StaticDofStability, bool> which) =>
        stability.Dofs.Where(which).Select(dof => Name(document, dof.Dof)).ToArray();
    private static double Digits(ModelDocument document, StaticStability stability, string dof) =>
        stability.Dofs.Single(item => Name(document, item.Dof) == dof).DigitsLost ?? double.NaN;

    /// <summary>A result is returned only with a stiffness that was factorized, and a refusal never carries numbers.</summary>
    private static void Consistent(StaticOutcome outcome, Reports reports, string description)
    {
        var last = reports.Items[^1];
        Assert(outcome.Success == (last.Kind == StaticProgressKind.Finished) && ReferenceEquals(last.Stability, outcome.Stability) &&
               reports.Items.Count(item => item.Kind is StaticProgressKind.Finished or StaticProgressKind.Failed) == 1 &&
               (outcome.Success
                   ? outcome.Stability is { State: StabilityState.PositiveDefinite, Stage: StabilityStage.Solution } stable &&
                     ReferenceEquals(outcome.Result!.Diagnostics.Stability, stable) && stable.Equations == outcome.Result.Diagnostics.SolvedDofCount &&
                     stable.Dofs.All(dof => dof is { Condition: StabilityCondition.Estimated, DigitsLost: >= 0, IsInfinite: false }) && last.Issue is null
                   : outcome.Result is null && last.Issue is { Severity: ValidationSeverity.Error }),
            description + " — the report a view follows says what the outcome says: a result with a factorized stiffness, or a refusal without numbers");
    }

    private static void Thresholds()
    {
        var standard = StabilityThresholds.Default;
        Assert(standard is { WarningDigits: 6, SevereDigits: 11 } && standard.Classify(5.999999) == StabilitySeverity.None &&
               standard.Classify(6) == StabilitySeverity.Warning && standard.Classify(11) == StabilitySeverity.Warning &&
               standard.Classify(11.000001) == StabilitySeverity.Severe && standard.Classify(double.NaN) == StabilitySeverity.None,
            "Digits lost are classified as they are, fractions included: nothing below 6, a warning from 6 to 11, severe above 11");
        var strict = new StabilityThresholds(3, 4.5);
        Assert(strict.Classify(2.99) == StabilitySeverity.None && strict.Classify(3) == StabilitySeverity.Warning && strict.Classify(4.51) == StabilitySeverity.Severe,
            "The thresholds are options of the run, not constants");
        var m = new Model(DofMask.XZ);
        var tip = m.Point(4, 0, 0);
        m.Frame(m.Point(0, 0, 0, Restraint.Fixed), tip);
        var load = m.Case("Load", true, tip, new(0, 0, -10));
        var compiled = Compile(m.Build());
        Throws<ArgumentException>(() => StaticSolver.Solve(compiled, load, new(Stability: new(0, 11))), "A warning threshold of zero digits is refused");
        Throws<ArgumentException>(() => StaticSolver.Solve(compiled, load, new(Stability: new(8, 7))), "A severe threshold below the warning threshold is refused");
    }

    /// <summary>cᵢ = Kᵢᵢ·(K⁻¹)ᵢᵢ on matrices whose inverse is known.</summary>
    private static void EstimateOfAMatrix()
    {
        // Two springs in series, k1 to the ground and k2 between the two movements: K⁻¹ = [[1/k1, 1/k1], [1/k1, 1/k1 + 1/k2]],
        // so c = 1 + k2/k1 for both.
        foreach (double ratio in new[] { 1.0, 1e3, 1e8, 1e11 })
        {
            double[,] k = { { 1 + ratio, -ratio }, { -ratio, ratio } };
            var estimate = StiffnessConditioning.Estimate(new ScaledCholesky(k, 1e-12));
            double expected = Math.Log10(1 + ratio);
            Assert(estimate.State == StabilityState.PositiveDefinite && estimate.Conditions.All(item => item == StabilityCondition.Estimated) &&
                   estimate.Digits.All(value => Math.Abs(value - expected) <= 1e-4),
                $"Two springs in series, stiffness ratio {ratio:G3}: both movements lose log10(1 + k2/k1) = {expected:0.####} digits ({estimate.Digits[0]:0.####}, {estimate.Digits[1]:0.####})");
        }

        // A full matrix against its inverse by Gauss–Jordan elimination, worked out here.
        const int n = 7;
        var b = new double[n, n];
        uint seed = 12345;
        double Next() => (seed = seed * 1664525 + 1013904223) / 4294967296.0 - .5;
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++) b[i, j] = Next();
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            for (int p = 0; p < n; p++) a[i, j] += b[p, i] * b[p, j] * Math.Pow(10, p - 3);   // rows of very different size
            if (i == j) a[i, j] += 1e-3;
        }
        var inverse = Invert(a);
        var full = StiffnessConditioning.Estimate(new ScaledCholesky(a, 1e-12));
        var pivoted = StiffnessConditioning.Assess(a);
        for (int i = 0; i < n; i++)
        {
            double expected = Math.Log10(a[i, i] * inverse[i, i]);
            Near(full.Digits[i], expected, $"Equation {i + 1} of a full matrix: the estimate is log10(Kii·(K⁻¹)ii) of its inverse", 1e-8);
            Near(pivoted.Digits[i], expected, $"… and the pivoted factorization gives the same number, whatever its elimination order", 1e-8);
        }
        Assert(pivoted.State == StabilityState.NearlySingular && pivoted.ZeroStiffnessModes == 0,
            "A matrix the pivoted factorization completes is positive definite at double precision: no mode without stiffness, every estimate finite");

        // The estimate is the same whichever equation is eliminated last: reverse the order.
        var reversed = new double[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++) reversed[i, j] = a[n - 1 - i, n - 1 - j];
        var mirrored = StiffnessConditioning.Estimate(new ScaledCholesky(reversed, 1e-12));
        Assert(Enumerable.Range(0, n).All(i => Math.Abs(mirrored.Digits[n - 1 - i] - full.Digits[i]) <= 1e-8),
            "Reordering the equations moves each estimate with its equation and changes none of them");

        // H = [[1, r], [r, 1]]: c = 1/(1 − r²), κ₂(H) = (1 + r)/(1 − r); κ₂/n² ≤ c ≤ κ₂.
        foreach (double r in new[] { 0.0, .5, .999, 1 - 1e-9 })
        {
            double[,] h = { { 1, r }, { r, 1 } };
            double c = Math.Pow(10, StiffnessConditioning.Estimate(new ScaledCholesky(h, 1e-12)).Digits[0]), kappa = (1 + r) / (1 - r);
            Assert(Math.Abs(c * (1 - r * r) - 1) <= 1e-6 && c <= kappa * (1 + 1e-9) && c >= kappa / 4 * (1 - 1e-9),
                $"Two coupled equations, coupling {r:G10}: the estimate {c:G6} lies between κ₂/n² and κ₂ = {kappa:G6} of the scaled stiffness");
        }
    }

    private static double[,] Invert(double[,] matrix)
    {
        int n = matrix.GetLength(0);
        var work = new double[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) work[i, j] = matrix[i, j];
            work[i, n + i] = 1;
        }
        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < n; row++)
                if (Math.Abs(work[row, column]) > Math.Abs(work[pivot, column])) pivot = row;
            for (int j = 0; j < 2 * n; j++) (work[column, j], work[pivot, j]) = (work[pivot, j], work[column, j]);
            double divisor = work[column, column];
            for (int j = 0; j < 2 * n; j++) work[column, j] /= divisor;
            for (int row = 0; row < n; row++)
            {
                if (row == column) continue;
                double factor = work[row, column];
                for (int j = 0; j < 2 * n; j++) work[row, j] -= factor * work[column, j];
            }
        }
        var inverse = new double[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++) inverse[i, j] = work[i, n + j];
        return inverse;
    }

    private static void SingularMatrices()
    {
        // one spring between two movements, nothing to the ground: both move together without resistance
        var free = StiffnessConditioning.Assess(new double[,] { { 3, -3 }, { -3, 3 } });
        Assert(free is { State: StabilityState.Singular, ZeroStiffnessModes: 1 } && free.Conditions.All(item => item == StabilityCondition.ZeroStiffnessMode) &&
               free.Digits.All(double.IsNaN) && free.SmallestPivot <= StiffnessConditioning.RoundingLevel(2),
            "A spring held by nothing: one mode without stiffness, both movements in it, and no digits invented for them");

        // a held movement beside it takes no part
        var beside = StiffnessConditioning.Assess(new double[,] { { 2, 0, 0 }, { 0, .7, -.7 }, { 0, -.7, .7 } });
        Assert(beside is { State: StabilityState.Singular, ZeroStiffnessModes: 1 } &&
               beside.Conditions.SequenceEqual([StabilityCondition.NotEstimated, StabilityCondition.ZeroStiffnessMode, StabilityCondition.ZeroStiffnessMode]),
            "A movement that is held, beside a mode without stiffness, is not counted in it and gets no estimate either");

        // a movement no stiffness reaches stands alone; what is left keeps its estimate
        var none = StiffnessConditioning.Assess(new double[,] { { 0, 0, 0 }, { 0, 12, 6 }, { 0, 6, 4 } });
        Assert(none is { State: StabilityState.Singular, ZeroStiffnessModes: 1 } && none.Conditions[0] == StabilityCondition.NoStiffness &&
               none.Conditions[1] == StabilityCondition.Estimated && Math.Abs(none.Digits[1] - Log4) <= 1e-12 && Math.Abs(none.Digits[2] - Log4) <= 1e-12,
            "A zero diagonal is a singularity by itself; the equations it does not touch keep their estimate (12, 6; 6, 4 → log10 4)");

        // nearly singular is not singular: a contrast of 1e13 is a number, 1e16 cannot be told from a mechanism
        var nearly = StiffnessConditioning.Assess(new double[,] { { 1 + 1e13, -1e13 }, { -1e13, 1e13 } });
        Assert(nearly is { State: StabilityState.NearlySingular, ZeroStiffnessModes: 0 } && nearly.Conditions.All(item => item == StabilityCondition.Estimated) &&
               nearly.Digits.All(value => Math.Abs(value - 13) < .01) && nearly.SmallestPivot > StiffnessConditioning.RoundingLevel(2),
            $"A stiffness contrast of 1e13 is nearly singular, with a number: {nearly.Digits[0]:0.###} digits, pivot {nearly.SmallestPivot:G3} — not infinity");
        var beyond = StiffnessConditioning.Assess(new double[,] { { 1 + 1e16, -1e16 }, { -1e16, 1e16 } });
        Assert(beyond.State == StabilityState.Singular && beyond.Conditions.All(item => item == StabilityCondition.ZeroStiffnessMode),
            "A contrast beyond what double precision holds (1e16) leaves nothing but rounding: it is reported as singular, which is the limit of the diagnostic");

        // a negative pivot is neither: the matrix is not positive definite
        var indefinite = StiffnessConditioning.Assess(new double[,] { { 1, 2 }, { 2, 1 } });
        Assert(indefinite is { State: StabilityState.NotPositiveDefinite, ZeroStiffnessModes: 0 } &&
               indefinite.Conditions.Count(item => item == StabilityCondition.NegativePivot) == 1 && indefinite.Digits.All(double.IsNaN) &&
               !indefinite.Conditions.Contains(StabilityCondition.ZeroStiffnessMode),
            "A pivot that is negative by more than rounding is loss of positive definiteness — not a singularity, and not a number of digits");
        var negative = StiffnessConditioning.Assess(new double[,] { { -1, 0 }, { 0, 1 } });
        Assert(negative.State == StabilityState.NotPositiveDefinite && negative.Conditions[0] == StabilityCondition.NegativePivot,
            "A negative diagonal stiffness is reported where it is");
    }

    /// <summary>TEST 1 and TEST 8: a cantilever in the XZ plane. Its tip: UX stands alone (c = 1); UZ and RY are
    /// coupled as 12EI/L³, 6EI/L², 4EI/L, so c = 4 for both, whatever E, I and L.</summary>
    private static void StablePlaneFrame()
    {
        var m = new Model(DofMask.XZ);
        var root = m.Point(0, 0, 0, Restraint.Fixed);
        var tip = m.Point(4, 0, 0);
        m.Frame(root, tip);
        var load = m.Case("Tip load", true, tip, new(3, 0, -10));
        var document = m.Build();
        var compiled = Compile(document);
        var reports = new Reports();
        var solved = StaticSolver.Solve(compiled, load, progress: reports);
        Consistent(solved, reports, "Stable plane frame");
        Assert(solved.Success && solved.Stability is { State: StabilityState.PositiveDefinite, Equations: 3, Warnings: 0, Instabilities: 0, ZeroStiffnessModes: 0, Dofs.IsEmpty: true } stable &&
               Math.Abs(stable.LargestDigitsLost!.Value - Log4) <= 1e-12 && stable.LargestAt!.Value.PointId == tip,
            "A stable plane frame converges with zero instabilities and nothing to report; its largest loss is log10 4 = 0.602 digits, at the tip");

        var listed = StaticSolver.Solve(compiled, load, Everything).Stability!;
        Assert(Where(document, listed, _ => true).SequenceEqual(["J2 UX", "J2 UZ", "J2 RY"]),
            "Only the degrees of freedom of the equations are assessed: UX, UZ, RY of the free joint — not UY, RX, RZ, which the plane switches off, and nothing of the fixed joint");
        Near(Digits(document, listed, "J2 UX"), 0, "Cantilever tip UX: the axial movement stands alone, no digit lost", 1e-12);
        Near(Digits(document, listed, "J2 UZ"), Log4, "Cantilever tip UZ: 12EI/L³ against 3EI/L³, log10 4", 1e-12);
        Near(Digits(document, listed, "J2 RY"), Log4, "Cantilever tip RY: 4EI/L against EI/L, log10 4", 1e-12);
        Assert(listed is { Warnings: 2, Instabilities: 0 } && solved.Result!.Joints.Zip(StaticSolver.Solve(compiled, load, Everything).Result!.Joints)
                   .All(pair => pair.First.Displacements.SequenceEqual(pair.Second.Displacements) && pair.First.Reactions.SequenceEqual(pair.Second.Reactions)),
            "Reporting from a lower threshold changes what is listed and no digit of the result");
    }

    /// <summary>TEST 2: the same cantilever in space. All six degrees of freedom of its tip are assessed.</summary>
    private static void StableSpaceFrame()
    {
        var m = new Model(DofMask.Full3D);
        var root = m.Point(0, 0, 0, Restraint.Fixed);
        var tip = m.Point(4, 0, 0);
        m.Frame(root, tip);
        var load = m.Case("Tip load", true, tip, new(3, 2, -10), new(1, 0, 0));
        var document = m.Build();
        var reports = new Reports();
        var solved = StaticSolver.Solve(Compile(document), load, progress: reports);
        Consistent(solved, reports, "Stable space frame");
        Assert(solved.Success && solved.Stability is { Equations: 6, Warnings: 0, Instabilities: 0, Dofs.IsEmpty: true },
            "A stable space frame converges with zero instabilities");
        var listed = StaticSolver.Solve(Compile(document), load, Everything).Stability!;
        Assert(Where(document, listed, _ => true).SequenceEqual(["J2 UX", "J2 UY", "J2 UZ", "J2 RX", "J2 RY", "J2 RZ"]),
            "In space all six degrees of freedom of a free joint are assessed");
        foreach (var (dof, expected) in new[] { ("J2 UX", 0.0), ("J2 UY", Log4), ("J2 UZ", Log4), ("J2 RX", 0), ("J2 RY", Log4), ("J2 RZ", Log4) })
            Near(Digits(document, listed, dof), expected, $"Space cantilever {dof}: {(expected == 0 ? "alone, no digit lost" : "bending pair, log10 4")}", 1e-12);
    }

    /// <summary>The estimate against the solver's own displacements: Kᵢᵢ·(K⁻¹)ᵢᵢ is the movement of a degree of freedom
    /// under a unit load with the model free, over its movement with every other degree of freedom held.</summary>
    private static void EstimateIsAStiffnessRatio()
    {
        // a frame in space: a column, a beam along X, a beam along Y, built in at the foot of the column
        (ModelDocument Document, ObjectId Case, ObjectId Corner) Build(DofComponent? only)
        {
            var m = new Model(DofMask.Full3D);
            var held = only is null ? (Restraint?)null : Restraint.Fixed;
            var foot = m.Point(0, 0, 0, Restraint.Fixed);
            var head = m.Point(0, 0, 3, held);
            Restraint? corner = only is { } free ? new Restraint(free != DofComponent.UX, free != DofComponent.UY, free != DofComponent.UZ,
                free != DofComponent.RX, free != DofComponent.RY, free != DofComponent.RZ) : null;
            var middle = m.Point(4, 0, 3, corner);
            var end = m.Point(4, 3, 3, held);
            m.Frame(foot, head);
            m.Frame(head, middle);
            m.Frame(middle, end);
            return (m.Build(), default, middle);
        }
        foreach (var component in new[] { DofComponent.UX, DofComponent.UZ, DofComponent.RX, DofComponent.RY })
        {
            double reported = double.NaN;
            double Movement(DofComponent? only)
            {
                var m = Build(only);
                var editor = m.Document.Edit();
                var (pattern, id) = (Id(43, 900), Id(48, 900));
                var unit = new double[6];
                unit[(int)component] = 1;
                editor.PutLoadPattern(new(pattern, "UNIT", LoadPatternType.Other));
                editor.PutLoad(new PointLoad(Id(47, 900), pattern, m.Corner, new(unit[0], unit[1], unit[2]), new(unit[3], unit[4], unit[5])));
                editor.PutLoadCase(new(id, "Unit", LoadCaseType.LinearStatic, [new(pattern, 1)]));
                var loaded = editor.Commit().Document;
                var outcome = StaticSolver.Solve(Compile(loaded), id, Everything);
                Assert(outcome.Success, "The unit-load case is solved: " + string.Join("; ", outcome.Issues));
                if (only is null) reported = Digits(loaded, outcome.Stability!, $"J3 {component}");
                return outcome.Result!.Joints.Single(joint => joint.PointId == m.Corner).Displacements[(int)component];
            }
            double free = Movement(null), held = Movement(component);
            Near(reported, Math.Log10(free / held),
                $"Space frame, J3 {component}: the estimate is log10 of its flexibility free over held, from the solver's displacements ({free:G6} / {held:G6})", 1e-7);
        }
    }

    /// <summary>TEST 5: two frames in line, a soft one to the support and a stiff one beyond it. Axially they are two
    /// springs in series: both joints lose log10(1 + k2/k1) digits in UX.</summary>
    private static void IllConditionedAndNearlySingular()
    {
        (StaticOutcome Outcome, ModelDocument Document, Reports Reports) Chain(double ratio)
        {
            var m = new Model(DofMask.XZ);
            var support = m.Point(0, 0, 0, Restraint.Fixed);
            var joint = m.Point(4, 0, 0);
            var end = m.Point(8, 0, 0);
            m.Frame(support, joint, 1e-4);
            m.Frame(joint, end, 1e-4 * ratio);
            var load = m.Case("Pull", true, end, new(5, 0, -1));
            var document = m.Build();
            var reports = new Reports();
            return (StaticSolver.Solve(Compile(document), load, progress: reports), document, reports);
        }

        var mild = Chain(1e3);
        Consistent(mild.Outcome, mild.Reports, "Stiffness contrast 1e3");
        Assert(mild.Outcome.Success && mild.Outcome.Stability is { Warnings: 0, Instabilities: 0 } && Math.Abs(mild.Outcome.Stability.LargestDigitsLost!.Value - Math.Log10(1001)) < 1e-9,
            "A stiffness contrast of 1e3 loses 3 digits: solved, nothing reported");

        var warned = Chain(1e7);
        Consistent(warned.Outcome, warned.Reports, "Stiffness contrast 1e7");
        var warning = warned.Outcome.Stability!;
        Assert(warned.Outcome.Success && warning is { State: StabilityState.PositiveDefinite, Warnings: 2, Instabilities: 0 } &&
               Where(warned.Document, warning, dof => dof.Severity == StabilitySeverity.Warning).SequenceEqual(["J2 UX", "J3 UX"]) &&
               Where(warned.Document, warning, dof => dof.IsInfinite).Length == 0,
            "A stiffness contrast of 1e7 is solved with a numerical accuracy warning at UX of both joints, and at no other degree of freedom");
        Near(Digits(warned.Document, warning, "J2 UX"), Math.Log10(1 + 1e7), "… J2 UX loses log10(1 + 1e7) = 7.0 digits", 1e-6);
        Near(Digits(warned.Document, warning, "J3 UX"), Math.Log10(1 + 1e7), "… J3 UX loses the same", 1e-6);
        Assert(Digits(warned.Document, warning, "J3 UZ") < 2 && Digits(warned.Document, warning, "J3 RY") < 2 && Digits(warned.Document, warning, "J2 UZ") < 2,
            "… while the bending of the same joints is listed with its own small loss, far below the threshold");
        Near(warned.Outcome.Result!.Joints.Single(joint => warned.Document.Points[joint.PointId].Name == "J3").Displacements[0],
            5 * 4 / (E * 1e-4) * (1 + 1e-7), "… and the pull still moves the end by PL/EA of the two frames in series", 1e-9);

        // What the estimate says shows in the solver's own results: the resultant of the internal forces, which
        // vanishes in exact arithmetic, grows with the digits lost and stays within ε·10^digits.
        double before = 0;
        foreach (double exponent in new[] { 3.0, 5, 6, 7, 8 })
        {
            var chain = Chain(Math.Pow(10, exponent));
            double error = chain.Outcome.Result!.Diagnostics.EquilibriumError, digits = chain.Outcome.Stability!.LargestDigitsLost!.Value;
            Assert(Math.Abs(digits - exponent) < 1e-3 && error <= 2.220446049250313e-16 * Math.Pow(10, digits) && error > before,
                $"Stiffness contrast 1e{exponent}: {digits:0.###} digits lost by the estimate; the internal forces of the result balance to {error:G3}, within ε·10^digits and more than at the contrast before");
            before = error;
        }

        // From there the solver's own check of the internal forces withholds the result; the estimate says why.
        var withheld = Chain(1e10);
        Consistent(withheld.Outcome, withheld.Reports, "Stiffness contrast 1e10");
        Assert(!withheld.Outcome.Success && withheld.Outcome.Issues.Any(issue => issue.Code == "static.equilibrium") &&
               withheld.Outcome.Stability is { State: StabilityState.PositiveDefinite, Warnings: 2, Instabilities: 0 } tenth &&
               Math.Abs(tenth.LargestDigitsLost!.Value - 10) < 1e-3 && tenth.Dofs.All(dof => !dof.IsInfinite),
            "A contrast of 1e10 is factorized, but its result is withheld by the check of the internal forces; the estimate reports 10 digits lost at UX of both joints — a warning, not a singularity");

        var severe = Chain(Math.Pow(10, 11.5));
        Consistent(severe.Outcome, severe.Reports, "Stiffness contrast 10^11.5");
        Assert(!severe.Outcome.Success && severe.Outcome.Issues.All(issue => issue.Code != "static.matrix.unstable") &&
               severe.Outcome.Stability is { State: StabilityState.PositiveDefinite, Warnings: 0, Instabilities: 2, ZeroStiffnessModes: 0 } harsh &&
               harsh.Dofs.Where(dof => dof.Severity == StabilitySeverity.Severe).All(dof => dof is { IsInfinite: false, DigitsLost: > 11.49 and < 11.51 }),
            "A contrast of 10^11.5 is above the 11 digits: severe, 11.5 digits, a number — its pivots are still above the solver's tolerance, so it is not called unstable");

        var refused = Chain(1e13);
        Consistent(refused.Outcome, refused.Reports, "Stiffness contrast 1e13");
        var nearly = refused.Outcome.Stability!;
        Assert(!refused.Outcome.Success && refused.Outcome.Issues.Any(issue => issue.Code == "static.matrix.unstable") &&
               nearly is { State: StabilityState.NearlySingular, ZeroStiffnessModes: 0, Instabilities: 2 } &&
               Where(refused.Document, nearly, dof => dof.Severity == StabilitySeverity.Severe).SequenceEqual(["J2 UX", "J3 UX"]) &&
               nearly.Dofs.All(dof => dof is { IsInfinite: false, Condition: StabilityCondition.Estimated }),
            "A contrast of 1e13 is refused — no result — as nearly singular: UX of both joints, every value finite, no infinity");
        Near(Digits(refused.Document, nearly, "J3 UX"), 13, "… with its estimate: 13 digits", .01);

        var beyond = Chain(1e15);
        Consistent(beyond.Outcome, beyond.Reports, "Stiffness contrast 1e15");
        Assert(!beyond.Outcome.Success && beyond.Outcome.Stability is { State: StabilityState.Singular, ZeroStiffnessModes: 1 } lost &&
               Where(beyond.Document, lost, dof => dof.IsInfinite).SequenceEqual(["J2 UX", "J3 UX"]),
            "A contrast of 1e15 leaves a pivot that cannot be told from rounding: reported as singular at UX of both joints — the limit of what double precision can tell apart");
    }

    /// <summary>TEST 3: a portal on two pins whose beam is hinged at both ends. The columns turn about their feet
    /// together: the feet and the heads rotate, the heads sway, nothing moves vertically.</summary>
    private static void PlaneMechanism()
    {
        var m = new Model(DofMask.XZ);
        var left = m.Point(0, 0, 0, Restraint.Pinned);
        var leftHead = m.Point(0, 0, 3);
        var rightHead = m.Point(4, 0, 3);
        var right = m.Point(4, 0, 0, Restraint.Pinned);
        m.Frame(left, leftHead);
        m.Frame(leftHead, rightHead, pinned: true);
        m.Frame(right, rightHead);
        var sway = m.Case("Sway", true, leftHead, new(10, 0, 0));
        var document = m.Build();
        var reports = new Reports();
        var outcome = StaticSolver.Solve(Compile(document), sway, progress: reports);
        Consistent(outcome, reports, "Plane mechanism");
        var found = outcome.Stability!;
        Assert(!outcome.Success && outcome.Result is null && outcome.Issues.Any(issue => issue.Code == "static.matrix.unstable") &&
               found is { State: StabilityState.Singular, ZeroStiffnessModes: 1, Equations: 8, Warnings: 0, Instabilities: 6, Stage: StabilityStage.Solution },
            "A portal that can sway freely is refused as singular: one mode without stiffness among its 8 equations");
        Assert(Where(document, found, dof => dof.IsInfinite).SequenceEqual(["J1 RY", "J2 UX", "J2 RY", "J3 UX", "J3 RY", "J4 RY"]) &&
               found.Dofs.Where(dof => dof.IsInfinite).All(dof => dof is { Condition: StabilityCondition.ZeroStiffnessMode, DigitsLost: null }),
            "… the degrees of freedom that move in it are named whatever the elimination order: the rotation of both feet, the sway and the rotation of both heads");
        Assert(Where(document, found, dof => !dof.IsInfinite).SequenceEqual(["J2 UZ", "J3 UZ"]) &&
               found.Dofs.Where(dof => !dof.IsInfinite).All(dof => dof is { Condition: StabilityCondition.NotEstimated, DigitsLost: null, Severity: StabilitySeverity.None }),
            "… the vertical movement of the heads takes no part and is given no number — an estimate does not exist for a singular stiffness");
        Assert(found.Dofs.All(dof => dof.Dof.Component is DofComponent.UX or DofComponent.UZ or DofComponent.RY),
            "… and nothing is reported against UY, RX or RZ, which the plane switches off");

        // hold the sway: the same portal is stable
        var braced = new Model(DofMask.XZ);
        var a = braced.Point(0, 0, 0, Restraint.Pinned);
        var b = braced.Point(0, 0, 3);
        var c = braced.Point(4, 0, 3, new Restraint(true, false, false, false, false, false));
        var d = braced.Point(4, 0, 0, Restraint.Pinned);
        braced.Frame(a, b);
        braced.Frame(b, c, pinned: true);
        braced.Frame(d, c);
        var held = braced.Case("Sway", true, b, new(10, 0, 0));
        Assert(StaticSolver.Solve(Compile(braced.Build()), held) is { Success: true, Stability: { Instabilities: 0, Warnings: 0 } },
            "With the sway held at one head the same portal is solved, with zero instabilities");
    }

    /// <summary>TEST 6: a beam on two rollers. Nothing holds it along its axis.</summary>
    private static void UnrestrainedBody()
    {
        var roller = new Restraint(false, false, true, false, false, false);
        var m = new Model(DofMask.XZ);
        var left = m.Point(0, 0, 0, roller);
        var middle = m.Point(3, 0, 0);
        var right = m.Point(6, 0, 0, roller);
        m.Frame(left, middle);
        m.Frame(middle, right);
        var load = m.Case("Load", true, middle, new(0, 0, -20));
        var document = m.Build();
        var reports = new Reports();
        var outcome = StaticSolver.Solve(Compile(document), load, progress: reports);
        Consistent(outcome, reports, "Beam on two rollers");
        Assert(!outcome.Success && outcome.Result is null && outcome.Stability is { State: StabilityState.Singular, ZeroStiffnessModes: 1 } found &&
               Where(document, found, dof => dof.IsInfinite).SequenceEqual(["J1 UX", "J2 UX", "J3 UX"]) &&
               Where(document, found, dof => !dof.IsInfinite).All(name => name.EndsWith("UZ", StringComparison.Ordinal) || name.EndsWith("RY", StringComparison.Ordinal)),
            "A beam on two rollers has no solution and none is made up: it slides along its axis — UX of every joint, and only UX");
    }

    /// <summary>TEST 4: a column in space on a ball joint, its head held sideways. It can spin about its own axis.</summary>
    private static void UnrestrainedRotation()
    {
        var m = new Model(DofMask.Full3D);
        var foot = m.Point(2, 5, 0, Restraint.Pinned);
        var head = m.Point(2, 5, 3, new Restraint(true, true, false, false, false, false));
        m.Frame(foot, head);
        var load = m.Case("Load", true, head, new(0, 0, -50));
        var document = m.Build();
        var reports = new Reports();
        var outcome = StaticSolver.Solve(Compile(document), load, progress: reports);
        Consistent(outcome, reports, "Column free to spin");
        var found = outcome.Stability!;
        Assert(!outcome.Success && found is { State: StabilityState.Singular, ZeroStiffnessModes: 1, Equations: 7, Instabilities: 2 } &&
               Where(document, found, dof => dof.IsInfinite).SequenceEqual(["J1 RZ", "J2 RZ"]),
            "A column that can spin about its axis is refused as singular: RZ of its two joints, of the 7 equations in space");
        Assert(found.Dofs.Where(dof => dof.IsInfinite).Select(dof => document.Points[dof.Dof.PointId].Position).SequenceEqual([new Xyz(2, 5, 0), new Xyz(2, 5, 3)]) &&
               Where(document, found, dof => !dof.IsInfinite).SequenceEqual(["J1 RX", "J1 RY", "J2 UZ", "J2 RX", "J2 RY"]),
            "… at the joints of the model, (2, 5, 0) and (2, 5, 3); its bending rotations and its axial movement are not in the mode");
    }

    /// <summary>Movements the stiffness does not resist, on which the loads of the case do no work. With
    /// <see cref="StaticOptions.HoldUnloadedMechanisms"/> they are held and the case is solved — to its closed form,
    /// and to the digit of the same model with that movement restrained; a movement the loads do act on has no
    /// result either way, and without the option nothing changes.</summary>
    private static void UnloadedMovementsHeld()
    {
        var hold = new StaticOptions(HoldUnloadedMechanisms: true);
        static bool Same(StaticOutcome first, StaticOutcome second, double tolerance = 1e-9) =>
            first.Result!.Joints.Zip(second.Result!.Joints).All(pair =>
                pair.First.Displacements.Zip(pair.Second.Displacements).All(value => Math.Abs(value.First - value.Second) <= tolerance * 1e-3) &&
                pair.First.Reactions.Zip(pair.Second.Reactions).All(value => Math.Abs(value.First - value.Second) <= tolerance)) &&
            first.Result.Frames.Zip(second.Result.Frames).All(pair =>
                pair.First.LocalEndActions.Zip(pair.Second.LocalEndActions).All(value => Math.Abs(value.First - value.Second) <= tolerance)) &&
            first.Result.Cables.Zip(second.Result.Cables).All(pair => Math.Abs(pair.First.Tension - pair.Second.Tension) <= tolerance);
        static double Moved(StaticOutcome outcome, ObjectId point, int component) =>
            outcome.Result!.Joints.Single(joint => joint.PointId == point).Displacements[component];
        static bool Refused(StaticOutcome outcome) => !outcome.Success && outcome.Result is null && outcome.Stability is { State: StabilityState.Singular, MovementsHeld: 0 } &&
            outcome.Issues.Any(issue => issue is { Severity: ValidationSeverity.Error, Code: "static.matrix.unstable" });
        static bool Marked(StaticOutcome outcome, int movements) => outcome.Success && outcome.Stability is { State: StabilityState.Singular } found &&
            found.ZeroStiffnessModes == movements && found.MovementsHeld == movements && ReferenceEquals(outcome.Result!.Diagnostics.Stability, found) &&
            outcome.Issues.Count(issue => issue.Severity == ValidationSeverity.Error) == 0 &&
            outcome.Issues.Single(issue => issue.Code == "static.mechanism.held").Severity == ValidationSeverity.Warning;

        // 1. A column in space on a ball joint, its head held sideways: free to spin about its own axis.
        (ModelDocument Document, ObjectId Axial, ObjectId Torque, ObjectId Head) Column(bool spinHeld)
        {
            var m = new Model(DofMask.Full3D);
            var foot = m.Point(2, 5, 0, Restraint.Pinned);
            var head = m.Point(2, 5, 3, new Restraint(true, true, false, false, false, spinHeld));
            m.Frame(foot, head);
            var axial = m.Case("Axial", true, head, new(0, 0, -50));
            var torque = m.Case("Torque", true, head, new(0, 0, -50), new(0, 0, 5));
            return (m.Build(), axial, torque, head);
        }
        var column = Column(false);
        var restrained = Column(true);
        var spinning = StaticSolver.Solve(Compile(column.Document), column.Axial, hold);
        var reference = StaticSolver.Solve(Compile(restrained.Document), restrained.Axial);
        Assert(Marked(spinning, 1) && Where(column.Document, spinning.Stability!, dof => dof.IsInfinite).SequenceEqual(["J1 RZ", "J2 RZ"]) && spinning.Stability!.Instabilities == 2,
            "A column free to spin about its axis, loaded along it: the load does no work on the spin, so with the option it is held and the case is solved — marked, with RZ of its two joints still reported");
        Near(Moved(spinning, column.Head, 2), -50 * 3 / (E * A), "… to the closed form: the head moves PL/EA", 1e-15);
        Assert(reference is { Success: true, Stability.MovementsHeld: 0 } && Same(spinning, reference) && Moved(spinning, column.Head, 5) == 0,
            "… and to the digit of the same column with the spin restrained at its head: displacements, reactions and end actions; the spin itself is reported as zero");
        Assert(Refused(StaticSolver.Solve(Compile(column.Document), column.Torque, hold)),
            "With a torque about its axis the load acts on the spin: no equilibrium exists, and the case is refused as unstable — option or not");
        Assert(Refused(StaticSolver.Solve(Compile(column.Document), column.Axial)),
            "Without the option the same column is refused whatever its load: the solver holds nothing unless it is asked to");

        // 2. A beam on two rollers: nothing holds it along its axis.
        (ModelDocument Document, ObjectId Down, ObjectId Along, ObjectId Middle) Beam(bool held)
        {
            var roller = new Restraint(false, false, true, false, false, false);
            var m = new Model(DofMask.XZ);
            var left = m.Point(0, 0, 0, held ? Restraint.Pinned : roller);
            var middle = m.Point(3, 0, 0);
            var right = m.Point(6, 0, 0, roller);
            m.Frame(left, middle);
            m.Frame(middle, right);
            var down = m.Case("Down", true, middle, new(0, 0, -20));
            var along = m.Case("Along", true, middle, new(7, 0, -20));
            return (m.Build(), down, along, middle);
        }
        var beam = Beam(false);
        var sliding = StaticSolver.Solve(Compile(beam.Document), beam.Down, hold);
        Assert(Marked(sliding, 1) && Where(beam.Document, sliding.Stability!, dof => dof.IsInfinite).SequenceEqual(["J1 UX", "J2 UX", "J3 UX"]),
            "A beam on two rollers under a transverse load: the slide along its axis is not acted on, is held, and the case is solved");
        Near(Moved(sliding, beam.Middle, 2), -20 * 216 / (48 * E * I33), "… to PL³/48EI at mid-span", 1e-15);
        var pinned = Beam(true);
        Assert(Same(sliding, StaticSolver.Solve(Compile(pinned.Document), pinned.Down)) && Refused(StaticSolver.Solve(Compile(beam.Document), beam.Along, hold)),
            "… as the same beam with one end pinned; with a load along its axis it has no equilibrium and is refused");

        // 3. The portal whose beam is hinged at both ends: its sway is a mechanism.
        var p = new Model(DofMask.XZ);
        var footA = p.Point(0, 0, 0, Restraint.Pinned);
        var headA = p.Point(0, 0, 3);
        var headB = p.Point(4, 0, 3);
        var footB = p.Point(4, 0, 0, Restraint.Pinned);
        p.Frame(footA, headA);
        p.Frame(headA, headB, pinned: true);
        p.Frame(footB, headB);
        var weight = p.Case("Weight", true, headA, new(0, 0, -10));
        var sway = p.Case("Sway", true, headA, new(10, 0, -10));
        var portal = p.Build();
        var standing = StaticSolver.Solve(Compile(portal), weight, hold);
        Assert(Marked(standing, 1) && standing.Stability!.Instabilities == 6, "The hinged portal under a weight on one head: the sway is not acted on, is held, and the case is solved");
        Near(Moved(standing, headA, 2), -10 * 3 / (E * A), "… the loaded column shortens PL/EA", 1e-15);
        Near(standing.Result!.Joints.Single(joint => joint.PointId == footA).Reactions[2], 10, "… and carries the whole weight to its foot");
        Assert(Refused(StaticSolver.Solve(Compile(portal), sway, hold)), "Pushed sideways the same portal is a mechanism under load: refused, as before");

        // 4. With a cable: the beam on rollers hung at mid-span. The equilibrium is iterated; its tangent does not resist the slide.
        (ModelDocument Document, ObjectId Case, ObjectId Middle) Hung(bool held)
        {
            var roller = new Restraint(false, false, true, false, false, false);
            var m = new Model(DofMask.XZ);
            var left = m.Point(0, 0, 0, held ? Restraint.Pinned : roller);
            var middle = m.Point(3, 0, 0);
            var right = m.Point(6, 0, 0, roller);
            var anchor = m.Point(3, 0, 4, Restraint.Pinned);
            m.Frame(left, middle);
            m.Frame(middle, right);
            m.Cable(middle, anchor);
            var down = m.Case("Down", false, middle, new(0, 0, -20));
            return (m.Build(), down, middle);
        }
        var hung = Hung(false);
        var hanging = StaticSolver.Solve(Compile(hung.Document), hung.Case, hold);
        double beamStiffness = 48 * E * I33 / 216, cableStiffness = 1.6e8 * .002 / 4;
        Assert(Marked(hanging, 1) && hanging.Stability!.Stage == StabilityStage.Solution && hanging.Result!.Diagnostics.NonlinearIterations > 0,
            "The same beam hung from a cable, an iterated equilibrium: its converged state meets every equilibrium equation, the slide's included, and is accepted with the slide marked as held");
        Near(Moved(hanging, hung.Middle, 2), -20 / (beamStiffness + cableStiffness), "… the beam and the cable share the load by their stiffnesses: deflection P/(k_beam + k_cable)", 1e-12);
        Near(hanging.Result!.Cables.Single().Tension, 20 * cableStiffness / (beamStiffness + cableStiffness), "… and the cable takes its share", 1e-8);
        var fixedEnd = Hung(true);
        Assert(Same(hanging, StaticSolver.Solve(Compile(fixedEnd.Document), fixedEnd.Case), 1e-7) && Refused(StaticSolver.Solve(Compile(hung.Document), hung.Case)),
            "… as with one end pinned; without the option the hung beam is refused");
    }

    /// <summary>A rotation no frame end holds, loaded by a moment: an exactly zero stiffness.</summary>
    private static void NoStiffnessAtAll()
    {
        var m = new Model(DofMask.XZ);
        // two hinged bars meeting at an apex: as a truss they hold its two translations
        var left = m.Point(0, 0, 0, Restraint.Pinned);
        var middle = m.Point(3, 0, 2);
        var right = m.Point(6, 0, 0, Restraint.Pinned);
        m.Frame(left, middle, pinned: true);
        m.Frame(middle, right, pinned: true);
        var turned = m.Case("Moment", true, middle, new(0, 0, -1), new(0, 5, 0));
        var document = m.Build();
        var reports = new Reports();
        var outcome = StaticSolver.Solve(Compile(document), turned, progress: reports);
        Consistent(outcome, reports, "Loaded rotation without stiffness");
        Assert(!outcome.Success && outcome.Stability is { State: StabilityState.Singular, ZeroStiffnessModes: 1 } found &&
               Where(document, found, dof => dof.Condition == StabilityCondition.NoStiffness).SequenceEqual(["J2 RY"]) &&
               Where(document, found, dof => dof.Condition == StabilityCondition.Estimated).SequenceEqual(["J2 UX", "J2 UZ"]) &&
               found.Dofs.Where(dof => dof.Condition == StabilityCondition.Estimated).All(dof => dof.DigitsLost < 1),
            "A moment on a joint whose every frame end is released: its rotation has no stiffness at all — an exact singularity, reported at that rotation; the translations of the joint, which the bars hold, keep their small estimate");
    }

    /// <summary>TEST 7: a cable equilibrium stopped at its iteration limit.</summary>
    private static void NonConvergenceIsNotInstability()
    {
        var m = new Model(DofMask.Full3D);
        var root = m.Point(-4, 0, 0, Restraint.Fixed);
        var tip = m.Point(0, 0, 0);
        var anchor = m.Point(0, 0, 3, Restraint.Pinned);
        m.Frame(root, tip);
        m.Cable(tip, anchor);
        var load = m.Case("Down", false, tip, new(0, 0, -100));
        var document = m.Build();
        var compiled = Compile(document);
        var reports = new Reports();
        var stopped = StaticSolver.Solve(compiled, load, new(MaximumNonlinearIterations: 1), progress: reports);
        Consistent(stopped, reports, "Iteration limit reached");
        Assert(!stopped.Success && stopped.Issues.Any(issue => issue.Code == "static.nonlinear.noConvergence") &&
               stopped.Issues.All(issue => issue.Code != "static.matrix.unstable") && stopped.Stability is null &&
               reports.Items[^1] is { Kind: StaticProgressKind.Failed, Stability: null, Balance: { Iteration: 1, Linear: false } last } && last.OutOfBalance > last.Tolerance,
            "A case stopped at its iteration limit is not converged — with its last iteration and out-of-balance — and nothing is said of a singular stiffness");
        var full = new Reports();
        var solved = StaticSolver.Solve(compiled, load, progress: full);
        Consistent(solved, full, "The same case with its iterations");
        Assert(solved.Success && solved.Result!.Diagnostics.NonlinearIterations > 1 && solved.Stability is { Instabilities: 0, Warnings: 0 },
            $"Given its iterations the same case converges in {solved.Result!.Diagnostics.NonlinearIterations}, with zero instabilities at the converged state");
    }

    /// <summary>TEST 9: one case fails, the next does not; a case that continues from a failed one says where it failed.</summary>
    private static void CasesAreIndependent()
    {
        // a cantilever hung from two cables in line through a joint only they reach, which moves vertically only
        var m = new Model(DofMask.Full3D, DofMask.UZ);
        var root = m.Point(-4, 0, 0, Restraint.Fixed);
        var tip = m.Point(0, 0, 0);
        var link = m.Point(0, 0, 3);
        var anchor = m.Point(0, 0, 6, Restraint.Pinned);
        m.Frame(root, tip);
        m.Cable(tip, link);
        m.Cable(link, anchor);
        var up = m.Case("Up", false, tip, new(0, 0, 80));
        var down = m.Case("Down", false, tip, new(0, 0, -50));
        var document = m.Build();
        var reports = new Reports();
        var outcomes = StaticSolver.Solve(Compile(document), [up, down], progress: reports);
        var slack = outcomes[up].Stability!;
        Assert(!outcomes[up].Success && outcomes[up].Issues.Any(issue => issue.Code == "static.matrix.unstable") &&
               slack is { State: StabilityState.Singular, ZeroStiffnessModes: 1, Instabilities: 1 } &&
               Where(document, slack, dof => dof.IsInfinite).SequenceEqual(["J3 UZ"]) && slack.Dofs.Single(dof => dof.IsInfinite).Condition == StabilityCondition.SlackCablesOnly,
            "Pushed up, both cables go slack and nothing holds the joint between them: the case is unstable there, by slack cables only");
        Assert(outcomes[down].Success && outcomes[down].Stability is { State: StabilityState.PositiveDefinite, Instabilities: 0 } &&
               reports.Items.Where(item => item.CaseId == down).Select(item => item.Kind).Last() == StaticProgressKind.Finished &&
               reports.Items.Count(item => item.Kind == StaticProgressKind.Failed) == 1,
            "The case after it is solved as if it were alone: a failed case does not fail the next");

        // a weight hanging on one cable, free to swing: the case that fits the target force fails, and so does the case continuing from it
        var h = new Model(DofMask.Full3D);
        var top = h.Point(0, 0, 5, Restraint.Pinned);
        var weight = h.Point(0, 0, 0);
        h.Cable(weight, top, 100);
        var fit = h.FitCase("Pretension");
        var service = h.Case("Service", false, weight, new(0, 0, -100), initial: fit);
        var hanging = h.Build();
        var followed = new Reports();
        var staged = StaticSolver.Solve(Compile(hanging), [fit, service], progress: followed);
        var first = followed.Items.Last(item => item.CaseId == fit);
        var second = followed.Items.Last(item => item.CaseId == service);
        Assert(!staged[fit].Success && !staged[service].Success && first is { Kind: StaticProgressKind.Failed, FitRun: > 0 } && second is { Kind: StaticProgressKind.Failed, FitRun: > 0 },
            "A case continuing from a case that could not be fitted fails too, and its report says it failed while that pretension was fitted");
        Assert(staged[service].Stability is { State: StabilityState.Singular, Stage: StabilityStage.EquilibriumIteration, Iteration: 1 } swing &&
               Where(hanging, swing, dof => dof.Condition == StabilityCondition.NoStiffness).SequenceEqual(["J2 UX", "J2 UY"]) &&
               Where(hanging, swing, dof => !dof.IsInfinite).SequenceEqual(["J2 UZ"]) && swing.Dofs.Single(dof => !dof.IsInfinite) is { Condition: StabilityCondition.NotEstimated, DigitsLost: null },
            "… with what stopped it: the weight has no stiffness sideways, UX and UY — found in the first iteration; along the cable nothing is estimated from a tangent made up for the search");
    }
}
