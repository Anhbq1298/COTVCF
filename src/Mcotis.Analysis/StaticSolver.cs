using System.Collections.Immutable;
using Mcotis.Core;

namespace Mcotis.Analysis;

/// <param name="ResidualTolerance">Accepted scaled backward error of a linear solution.</param>
/// <param name="NonlinearForceTolerance">Accepted out-of-balance force of a cable equilibrium, as a fraction of the
/// largest applied load or cable pretension (at least 1 kN).</param>
/// <param name="EquilibriumTolerance">Accepted resultant of the internal forces of the whole model, which must vanish.</param>
/// <param name="AcceptClosestFit">Cable target forces that are not met within the tolerance: false refuses the case;
/// true returns the equilibrium of the closest pretension found, marked as not fitted (<see cref="StaticCableFit.Converged"/>),
/// so that the deviation can be reviewed.</param>
public sealed record StaticOptions(int MaximumFullDofs = 2400, int MaximumFreeDofs = 600,
    double RelativePivotTolerance = 1e-12, double ResidualTolerance = 1e-10,
    double NonlinearForceTolerance = 1e-9, int MaximumNonlinearIterations = 100, double EquilibriumTolerance = 1e-8,
    bool AcceptClosestFit = false);

public sealed record StaticJointResult(ObjectId PointId, ImmutableArray<double> Displacements,
    ImmutableArray<double> Reactions, ImmutableArray<double> SuppressedDofForces);

/// <summary>The frame at one section. <paramref name="Relative"/> is the distance along the whole frame;
/// <paramref name="LocalDisplacements"/> are [u1,u2,u3,θ1,θ2,θ3]; <paramref name="Actions"/> are [F1,F2,F3,M1,M2,M3] of the
/// part beyond the section on the part before it — F1 is the axial force, tension positive; at the frame's far end
/// they are its J-end actions, at its start the opposite of its I-end actions.</summary>
public sealed record StaticFrameStation(double Relative, ImmutableArray<double> LocalDisplacements, ImmutableArray<double> Actions);

/// <summary>One piece of a frame between two points on it, placed by its relative distances along the frame.
/// <paramref name="Stations"/> are equally spaced along the piece, both ends included: as many intervals as the
/// model's frame mesh setting. They are output only — no result at a joint depends on their number.</summary>
public sealed record StaticFrameSegment(ObjectId StartPointId, ObjectId EndPointId, double RelativeStart, double RelativeEnd,
    ImmutableArray<double> LocalDisplacements, ImmutableArray<double> LocalEndActions, ImmutableArray<StaticFrameStation> Stations);

/// <summary>Local end actions ordered [F1,F2,F3,M1,M2,M3] at I then J; released member rotations are recovered separately
/// from joint rotations. For a frame split at points lying on it these are its own two ends, and
/// <paramref name="Segments"/> holds every piece.</summary>
public sealed record StaticFrameResult(ObjectId ParentObjectId, Xyz Axis1, Xyz Axis2, Xyz Axis3,
    ImmutableArray<double> LocalDisplacements, ImmutableArray<double> LocalEndActions, ImmutableArray<StaticFrameSegment> Segments);

/// <param name="Tension">Along the chord [kN]; 0 for a slack cable.</param>
/// <param name="TargetForce">What the fit stage had to reach in this cable, if it had a target.</param>
/// <param name="AppliedPretension">The tension the cable would have at the model geometry as a straight bar — its lack of fit × EA / L.</param>
/// <param name="FitStageTension">Its tension at the end of the case that fitted the targets.</param>
/// <param name="Sag">Mid-span sag below the chord [m] of a cable loaded along its length; null for a straight cable.</param>
/// <param name="TangentStiffnessRatio">Axial stiffness the sagging cable still has, as a fraction of EA / L.</param>
/// <param name="FitHistory">Its tension at the end of the fit stage after each fitting run, the first with the target
/// itself as pretension; empty when no target forces were fitted.</param>
public sealed record StaticCableResult(ObjectId ParentObjectId, Xyz Axis1, double Tension, bool Active,
    double? TargetForce, double AppliedPretension, double? FitStageTension, double? Sag, double? TangentStiffnessRatio,
    ImmutableArray<double> FitHistory);

/// <param name="Converged">False only with <see cref="StaticOptions.AcceptClosestFit"/>: the targets were not met and
/// the result is that of the closest pretension found; <paramref name="Failure"/> then says why.</param>
public sealed record StaticCableFit(ObjectId FitCaseId, int Iterations, double WorstDeviationPercent, bool Converged = true, string? Failure = null);

/// <param name="ScaledResidual">Linear solution: scaled backward error. Cable equilibrium: largest out-of-balance
/// force over the force scale of <see cref="StaticOptions.NonlinearForceTolerance"/>.</param>
/// <param name="NonlinearIterations">0 for a linear solution.</param>
/// <param name="EquilibriumError">Resultant force and moment of all internal forces, relative to their size.</param>
public sealed record StaticDiagnostics(int SolvedDofCount, double ScaledResidual, double MinimumScaledPivot,
    ImmutableArray<DofKey> UnconnectedRotations, int NonlinearIterations, double EquilibriumError, StaticCableFit? CableFit);

public sealed record StaticResult(ObjectId DocumentId, string InputHash, long Revision, ObjectId LoadCaseId,
    ImmutableArray<StaticJointResult> Joints, ImmutableArray<StaticFrameResult> Frames, ImmutableArray<StaticCableResult> Cables,
    StaticDiagnostics Diagnostics)
{
    public const string FormulationVersion = "spatial-static-2";
}

public sealed record StaticOutcome(StaticResult? Result, ImmutableArray<ValidationIssue> Issues)
{
    public bool Success => Result is not null;
}

public enum StaticProgressKind { Started, Equilibrium, Finished, Failed }

/// <summary>How far the model is from equilibrium at one moment of a solution, joint by joint.
/// <para>A cable equilibrium is iterated: <paramref name="Iteration"/> counts from 1, <paramref name="OutOfBalance"/> is
/// the largest out-of-balance force or moment on a solved degree of freedom [kN, kN·m] and <paramref name="Tolerance"/>
/// the one the solver accepts. A linear case is solved directly (<paramref name="Linear"/>, iteration 0): the two are
/// then its scaled backward error and the accepted one, which have no unit.</para></summary>
/// <param name="Joints">Joints with a solved degree of freedom.</param>
/// <param name="JointsInBalance">Those of them whose every solved degree of freedom is within the tolerance.</param>
/// <param name="Worst">Where the out-of-balance is largest.</param>
public sealed record StaticBalance(int Iteration, double OutOfBalance, double Tolerance, int Joints, int JointsInBalance,
    DofKey? Worst, bool Linear = false);

/// <summary>What a run tells a view that follows it: a case started, where its equilibrium iteration stands, how it
/// ended. Nothing of the solution depends on it. Reports are made on the solving thread, one after the other.</summary>
/// <param name="CaseId">The case asked for.</param>
/// <param name="ActiveDofs">Degrees of freedom that are on: those solved, those restrained and any rotation left out
/// as unconnected.</param>
/// <param name="FitRun">While the pretension of the case's fit stage is being fitted: the fitting run, from 1, and
/// <paramref name="Balance"/> is that of the fit stage. 0 while the case's own equilibrium is solved. On
/// <see cref="StaticProgressKind.Finished"/>: the number of fitting runs its pretension took.</param>
/// <param name="Balance">The last equilibrium measured; null before the first.</param>
/// <param name="Issue">On <see cref="StaticProgressKind.Failed"/>: the first error.</param>
public sealed record StaticProgress(ObjectId CaseId, StaticProgressKind Kind, int ActiveDofs, int RestrainedDofs, int SolvedDofs,
    int FitRun, StaticBalance? Balance, ValidationIssue? Issue = null);

/// <summary>The static solver of the standalone system: 3D frames and cables on the compiled model, with the DOFs the
/// model selects. Dense; sized for verification and for models up to its limits.
/// <para><b>Frames</b> are prismatic Euler–Bernoulli elements, one per piece of a frame between the points lying on
/// it; nodal and linear member loads, self-weight, moment releases, fixed zero-displacement supports.</para>
/// <para><b>Cables</b> carry tension only (<see cref="CableElement"/>), straight or sagging under their self-weight.
/// A model with cables is solved as the minimum of its potential energy — convex, so there is one equilibrium and it
/// does not depend on the load path — by Newton's method with a backtracking line search and a Levenberg shift
/// (Nocedal &amp; Wright, <i>Numerical Optimization</i>, 2nd ed., 2006). Joint displacements stay small.</para>
/// <para><b>Cases.</b> A linear static case stands alone. A nonlinear static case carries the loads of the cases it
/// continues from. The case that fits cable target forces adjusts each targeted cable's pretension until its tension
/// is within the model's tolerance of the target at the end of that case; cases continuing from it keep that
/// pretension.</para>
/// <para>It returns a result or diagnostics, never both an error and numbers: what it does not cover it refuses, and a
/// result is returned only after the residual, stability and equilibrium checks at the end of <see cref="Solve"/>.
/// No shells, P-Delta, large displacements or modal analysis.</para></summary>
public static class StaticSolver
{
    /// <summary>The part of an unstable-model message that points to the degrees of freedom of cable joints. A window
    /// that does not offer them leaves it out of what it shows.</summary>
    public const string CableJointOption = ", or switch it off for cable joints (Analysis Options → Degrees of Freedom)";

    public static StaticOutcome Solve(CompiledModel model, ObjectId caseId, StaticOptions? options = null,
        CancellationToken cancellationToken = default, IProgress<StaticProgress>? progress = null) =>
        Solve(model, [caseId], options, cancellationToken, progress)[caseId];

    /// <summary>Several cases of one model. Each is solved as it would be alone; cases that continue from the same fit
    /// case share the one fit of its target forces instead of repeating it.</summary>
    public static ImmutableDictionary<ObjectId, StaticOutcome> Solve(CompiledModel model, IEnumerable<ObjectId> caseIds,
        StaticOptions? options = null, CancellationToken cancellationToken = default, IProgress<StaticProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(caseIds);
        options ??= new();
        static bool Fraction(double value) => double.IsFinite(value) && value > 0 && value < 1;
        if (options.MaximumFullDofs < 1 || options.MaximumFreeDofs < 1 || options.MaximumNonlinearIterations < 1 ||
            !Fraction(options.RelativePivotTolerance) || !Fraction(options.ResidualTolerance) ||
            !Fraction(options.NonlinearForceTolerance) || !Fraction(options.EquilibriumTolerance))
            throw new ArgumentException("Solver limits must be positive and numerical tolerances finite within (0,1).", nameof(options));
        var run = new Run(model, options, progress, cancellationToken);
        return caseIds.Distinct().ToImmutableDictionary(caseId => caseId, run.Solve);
    }

    private sealed record Assembly(List<(SpatialFrameElement Element, int[] Indices)> Frames, double[,] Stiffness,
        double[] External, CableElement[] Cables);

    private sealed class Run(CompiledModel model, StaticOptions options, IProgress<StaticProgress>? progress, CancellationToken cancellationToken)
    {
        // 0 first: a well-posed tangent takes the exact Newton step; larger shifts only when that step fails.
        private static readonly double[] LevenbergShifts = [0, 1e-12, 1e-8, 1e-5, 1e-3, 1e-1, 1];
        private const double SlackStiffnessRatio = 1e-6;
        private const double FitRankTolerance = 1e-7;

        private readonly ModelDocument document = model.Input;
        private readonly List<ValidationIssue> issues = [];
        private readonly Dictionary<ObjectId, (Fit Fit, ValidationIssue? Note)> fits = [];
        private readonly int n = model.Dofs.Length;
        private readonly ILookup<ObjectId, LoadAssignment> loadsByTarget =
            model.Input.Loads.Values.OrderBy(load => load.Id).ToLookup(load => load.TargetId);

        // What a view following the run is told (StaticProgress): the case asked for and where its solution stands.
        private readonly int activeDofs = model.Dofs.Count(dof => dof.State != DofState.Inactive);
        private readonly int restrainedDofs = model.Dofs.Count(dof => dof.State == DofState.Restrained);
        private ObjectId requested;
        private int solvedDofs, fitRun;
        private StaticBalance? balance;

        private void Error(string code, ObjectKind kind, ObjectId id, string message) =>
            issues.Add(new(ValidationSeverity.Error, code, kind, id, message));
        private StaticOutcome Failed()
        {
            Report(StaticProgressKind.Failed, issues.FirstOrDefault(issue => issue.Severity == ValidationSeverity.Error));
            return new(null, issues.ToImmutableArray());
        }

        private void Report(StaticProgressKind kind, ValidationIssue? issue = null, int? fitRuns = null) =>
            progress?.Report(new(requested, kind, activeDofs, restrainedDofs, solvedDofs, fitRuns ?? fitRun, balance, issue));

        /// <summary>Records how far each joint is from equilibrium and reports it. <paramref name="size"/> holds one
        /// value per solved degree of freedom, in the order of <paramref name="free"/>.</summary>
        private void Measure(int[] free, double[] size, int iteration, double tolerance, bool linear = false)
        {
            var joints = new Dictionary<ObjectId, bool>();
            int worst = -1;
            for (int i = 0; i < free.Length; i++)
            {
                var point = model.Dofs[free[i]].Key.PointId;
                joints[point] = joints.GetValueOrDefault(point, true) && size[i] <= tolerance;
                if (worst < 0 || size[i] > size[worst]) worst = i;
            }
            balance = new(iteration, worst < 0 ? 0 : size[worst], tolerance, joints.Count, joints.Values.Count(within => within),
                worst < 0 ? null : model.Dofs[free[worst]].Key, linear);
            Report(StaticProgressKind.Equilibrium);
        }

        public StaticOutcome Solve(ObjectId caseId)
        {
            issues.Clear();
            requested = caseId;
            solvedDofs = fitRun = 0;
            balance = null;
            if (!document.LoadCases.TryGetValue(caseId, out var loadCase))
            {
                Error("static.case.missing", ObjectKind.LoadCase, caseId, "Select an existing load case.");
                return Failed();
            }
            // The case and the cases it continues from, first to last. The document has no cycle among them.
            var chain = new List<LoadCase>();
            for (LoadCase? current = loadCase; current is not null;
                 current = current.InitialCaseId is { } initial ? document.LoadCases[initial] : null) chain.Insert(0, current);
            bool linear = loadCase.Type == LoadCaseType.LinearStatic;
            if (linear && (loadCase.InitialCaseId is not null || loadCase.FitCableTargetForces))
                Error("static.case.unsupported", ObjectKind.LoadCase, caseId,
                    $"Case '{loadCase.Name}' is linear static: it cannot continue from another case or fit cable target forces. Make it a nonlinear static case.");
            if (linear && !document.Cables.IsEmpty)
                Error("static.cable.linearCase", ObjectKind.LoadCase, caseId,
                    $"The model has cables, which carry tension only: case '{loadCase.Name}' must be a nonlinear static case. A cable is not replaced by an elastic bar.");
            if (!linear)
                foreach (var item in chain.Where(item => item.Type != LoadCaseType.NonlinearStatic))
                    Error("static.case.initialLinear", ObjectKind.LoadCase, caseId,
                        $"Case '{loadCase.Name}' continues from '{item.Name}', which is linear static; a nonlinear case continues only from nonlinear cases.");

            var targetPatterns = document.Loads.Values.OfType<CableTargetForce>().Select(load => load.PatternId).ToHashSet();
            var fitCases = chain.Where(item => item.FitCableTargetForces).ToArray();
            if (fitCases.Length > 1)
                Error("static.fit.nested", ObjectKind.LoadCase, caseId,
                    $"Case '{loadCase.Name}' continues through more than one case that fits cable target forces ({string.Join(", ", fitCases.Select(item => $"'{item.Name}'"))}); one fit stage per chain is supported.");
            foreach (var item in chain.Where(item => !item.FitCableTargetForces &&
                         item.Components.Any(component => component.Factor != 0 && targetPatterns.Contains(component.PatternId))))
                Error("static.targetForce.unfitted", ObjectKind.LoadCase, item.Id,
                    $"Case '{item.Name}' applies a load pattern with cable target forces but is not set to fit them; the targets would be ignored.");
            var fitCase = fitCases.Length == 1 && !linear ? fitCases[0] : null;
            var targets = new Dictionary<ObjectId, double>();
            if (fitCase is not null)
            {
                var own = fitCase.Components.GroupBy(component => component.PatternId).ToDictionary(group => group.Key, group => group.Sum(component => component.Factor));
                int before = issues.Count;
                foreach (var target in document.Loads.Values.OfType<CableTargetForce>().OrderBy(load => load.Id))
                {
                    if (!own.TryGetValue(target.PatternId, out var factor) || factor == 0) continue;
                    string cable = document.Cables[target.TargetId].Name;
                    if (targets.ContainsKey(target.TargetId))
                        Error("static.fit.duplicateTarget", ObjectKind.Cable, target.TargetId, $"Cable '{cable}' has more than one target force in case '{fitCase.Name}'.");
                    else if (!(factor * target.Force > 0) || !double.IsFinite(factor * target.Force))
                        Error("static.fit.target", ObjectKind.Cable, target.TargetId, $"The target force of cable '{cable}' in case '{fitCase.Name}' must be a positive tension; its pattern factor gives {factor * target.Force:G6} kN.");
                    else targets[target.TargetId] = factor * target.Force;
                }
                if (targets.Count == 0 && issues.Count == before)
                    Error("static.fit.noTargets", ObjectKind.LoadCase, fitCase.Id, $"Case '{fitCase.Name}' fits cable target forces but applies no pattern that holds one.");
            }

            if (model.Dofs.Length > options.MaximumFullDofs || model.FreeDofCount > options.MaximumFreeDofs)
                Error("static.size", ObjectKind.Settings, default, "This dense engine's size limit was exceeded; a sparse engine is required.");
            foreach (var frame in document.Frames.Values)
            {
                var a = document.AssignmentOfFrame(frame.Id);
                var properties = document.FrameSections[a.SectionId!.Value].Properties;
                bool plane = model.ActiveDofs is DofMask.XZ or DofMask.XY or DofMask.YZ;
                if (properties.Inertia22 * a.Modifiers.Inertia22 <= 0 || properties.Inertia33 * a.Modifiers.Inertia33 <= 0 ||
                    (!plane && properties.TorsionConstant * a.Modifiers.Torsion <= 0))
                    Error("static.section.stiffness", ObjectKind.Frame, frame.Id,
                        $"Frame '{frame.Name}' needs positive bending stiffnesses and, outside a standard plane preset, positive torsional stiffness.");
                if (new[] { a.Releases.Start, a.Releases.End }.Any(r => r.Axial || r.Shear2 || r.Shear3 || r.Torsion))
                    Error("static.release.unsupported", ObjectKind.Frame, frame.Id, $"Frame '{frame.Name}': only local M2/M3 releases have been implemented and tested.");
            }
            foreach (var cable in document.Cables.Values)
            {
                var a = document.AssignmentOfCable(cable.Id);
                var section = document.CableSections[a.SectionId!.Value];
                double ea = document.Materials[section.MaterialId].ElasticModulus * section.Area * a.Modifiers.Area;
                if (!(ea > 0) || !double.IsFinite(ea))
                    Error("static.cable.stiffness", ObjectKind.Cable, cable.Id, $"Cable '{cable.Name}' needs a positive axial stiffness (E × area × area modifier).");
            }
            if (issues.Count > 0) return Failed();

            static Dictionary<ObjectId, double> Factors(IEnumerable<LoadCase> cases) => cases.SelectMany(item => item.Components)
                .GroupBy(component => component.PatternId).ToDictionary(group => group.Key, group => group.Sum(component => component.Factor));
            var factors = Factors(linear ? [loadCase] : chain);
            if (factors.Values.Any(value => !double.IsFinite(value)))
            {
                Error("static.load.nonfinite", ObjectKind.LoadCase, caseId, "Load pattern factors overflowed when combined.");
                return Failed();
            }
            if (Assemble(factors, caseId) is not { } assembly) return Failed();
            var stiffness = assembly.Stiffness;
            // Symmetry is checked before the lower-triangle factorization. Do not hide a transform/assembly sign defect.
            for (int i = 0; i < n; i++)
            for (int j = 0; j < i; j++)
            {
                double scale = Math.Max(1, Math.Sqrt(Math.Abs(stiffness[i, i])) * Math.Sqrt(Math.Abs(stiffness[j, j])));
                if (Math.Abs(stiffness[i, j] - stiffness[j, i]) > 1e-12 * scale)
                {
                    Error("static.matrix.asymmetric", ObjectKind.Settings, default, "Assembled stiffness is not symmetric within the numerical tolerance.");
                    return Failed();
                }
            }
            var (free, unconnectedRotations) = FreeDofs(assembly, report: true);
            solvedDofs = free.Length;
            Report(StaticProgressKind.Started);

            double[] displacement;
            double residual, minimumPivot = 1;
            int nonlinearIterations = 0;
            var pretension = new double[assembly.Cables.Length];
            double[]? stageTensions = null;
            StaticCableFit? cableFit = null;
            List<double[]> fitHistory = [];
            if (assembly.Cables.Length == 0)
            {
                var reduced = new double[free.Length, free.Length];
                var rhs = free.Select(i => assembly.External[i]).ToArray();
                for (int i = 0; i < free.Length; i++)
                for (int j = 0; j < free.Length; j++) reduced[i, j] = stiffness[free[i], free[j]];
                double[] solved;
                try
                {
                    var factor = new ScaledCholesky(reduced, options.RelativePivotTolerance, cancellationToken);
                    solved = factor.Solve(rhs);
                    minimumPivot = factor.MinimumPivot;
                }
                catch (FactorizationFailure failure)
                {
                    Unstable(free[failure.Index], failure.Pivot);
                    return Failed();
                }
                residual = DenseAlgebra.ScaledResidual(reduced, solved, rhs);
                if (progress is not null) Measure(free, DenseAlgebra.ScaledResiduals(reduced, solved, rhs), 0, options.ResidualTolerance, linear: true);
                if (solved.Any(value => !double.IsFinite(value)) || !double.IsFinite(residual) || residual > options.ResidualTolerance)
                {
                    Error("static.residual", ObjectKind.LoadCase, caseId, $"The scaled backward error ({residual:G6}) exceeds the accepted tolerance ({options.ResidualTolerance:G6}); no results returned.");
                    return Failed();
                }
                displacement = new double[n];
                for (int i = 0; i < free.Length; i++) displacement[free[i]] = solved[i];
            }
            else
            {
                if (fitCase is not null)
                {
                    // The targets are met under the loads of the fit case itself, with what it continues from.
                    var stage = assembly;
                    var stageFree = free;
                    if (fitCase.Id != loadCase.Id)
                    {
                        if (Assemble(Factors(chain.Take(chain.IndexOf(fitCase) + 1)), fitCase.Id) is not { } built) return Failed();
                        stage = built;
                        stageFree = FreeDofs(stage, report: false).Free;
                    }
                    var cableTargets = stage.Cables.Select(cable => targets.GetValueOrDefault(cable.Cable.Id)).ToArray();
                    if (!fits.TryGetValue(fitCase.Id, out var kept))
                    {
                        if (FitPretension(stage, stageFree, cableTargets, fitCase) is not { } found) return Failed();
                        fitRun = 0;
                        var note = found.Failure is null ? null : new ValidationIssue(ValidationSeverity.Warning, "static.fit.closest", ObjectKind.LoadCase, fitCase.Id,
                            $"Case '{fitCase.Name}': {found.Failure} The results are those of the closest pretension found.");
                        fits[fitCase.Id] = kept = (found, note);
                    }
                    if (kept.Note is { } closest) issues.Add(closest);
                    var fitted = kept.Fit;
                    pretension = fitted.Applied;
                    stageTensions = fitted.Tensions;
                    fitHistory = fitted.History;
                    cableFit = new(fitCase.Id, fitted.Iterations, fitted.WorstDeviationPercent, fitted.Failure is null, fitted.Failure);
                }
                if (Equilibrium(assembly, pretension, free, caseId) is not { } equilibrium) return Failed();
                (displacement, nonlinearIterations, residual) = equilibrium;

                // The equilibrium has to be a stable one: the tangent there, without any regularization, is positive
                // definite. A movement that only slack cables reach has nothing holding it in this case.
                var tangent = Tangent(assembly, pretension, displacement, free, regularize: false);
                var reached = Tangent(assembly, pretension, displacement, free, regularize: true);
                var unheld = Enumerable.Range(0, free.Length).Where(i => !(tangent[i, i] > 0)).ToArray();
                foreach (var point in unheld.GroupBy(i => (model.Dofs[free[i]].Key.PointId, Slack: reached[i, i] > 0)))
                {
                    string where = $"'{document.Points[point.Key.PointId].Name}' {string.Join("/", point.Select(i => model.Dofs[free[i]].Key.Component))}";
                    Error("static.matrix.unstable", ObjectKind.Point, point.Key.PointId, point.Key.Slack
                        ? $"{where} is held by slack cables only in case '{loadCase.Name}': nothing holds it there, and the model is unstable. No results returned."
                        : $"{where} has no stiffness and no restraint: the model is unstable there. A cable gives stiffness along its own line only: hold that movement with another member or a restraint{CableJointOption}. No results returned.");
                }
                if (unheld.Length > 0) return Failed();
                try { minimumPivot = new ScaledCholesky(tangent, options.RelativePivotTolerance, cancellationToken).MinimumPivot; }
                catch (FactorizationFailure failure)
                {
                    Unstable(free[failure.Index], failure.Pivot);
                    return Failed();
                }
            }

            var tensions = assembly.Cables.Select((cable, index) => cable.Tension(pretension[index], displacement)).ToArray();
            var internalForce = InternalForce(assembly, tensions, displacement);
            double equilibriumError = EquilibriumError(internalForce);
            if (!(equilibriumError <= options.EquilibriumTolerance))
            {
                Error("static.equilibrium", ObjectKind.LoadCase, caseId,
                    $"The internal forces of the model do not balance (relative resultant {equilibriumError:G6}, accepted {options.EquilibriumTolerance:G6}); no results returned.");
                return Failed();
            }
            var residualFull = internalForce;
            for (int i = 0; i < n; i++) residualFull[i] -= assembly.External[i];
            var joints = model.Points.Select(point =>
            {
                var dofs = DegreesOfFreedom.Components.Select(c => model.DofIndex[new(point.Id, c)]).ToArray();
                return new StaticJointResult(point.Id, dofs.Select(d => displacement[d.FullIndex]).ToImmutableArray(),
                    dofs.Select(d => d.State == DofState.Restrained ? residualFull[d.FullIndex] : 0).ToImmutableArray(),
                    dofs.Select(d => d.State == DofState.Inactive ? residualFull[d.FullIndex] : 0).ToImmutableArray());
            }).ToImmutableArray();
            var frames = assembly.Frames.GroupBy(item => item.Element.Frame.Id).Select(pieces =>
            {
                var segments = pieces.Select(item =>
                {
                    var (local, forces) = item.Element.Recover(item.Indices.Select(i => displacement[i]).ToArray());
                    var member = model.Members.Single(m => m.ParentObjectId == item.Element.Frame.Id && m.RelativeStart == item.Element.RelativeStart);
                    int intervals = document.Settings.FrameMeshSegments;
                    var stations = Enumerable.Range(0, intervals + 1).Select(index =>
                    {
                        double along = (double)index / intervals;
                        var (displacements, actions) = item.Element.Evaluate(along, local, forces);
                        return new StaticFrameStation(member.RelativeStart + (member.RelativeEnd - member.RelativeStart) * along,
                            displacements.ToImmutableArray(), actions.ToImmutableArray());
                    }).ToImmutableArray();
                    return new StaticFrameSegment(member.StartPointId, member.EndPointId, member.RelativeStart, member.RelativeEnd,
                        local.ToImmutableArray(), forces.ToImmutableArray(), stations);
                }).ToImmutableArray();
                var element = pieces.First().Element;
                return new StaticFrameResult(pieces.Key, element.Axis1, element.Axis2, element.Axis3,
                    segments[0].LocalDisplacements.Take(6).Concat(segments[^1].LocalDisplacements.Skip(6)).ToImmutableArray(),
                    segments[0].LocalEndActions.Take(6).Concat(segments[^1].LocalEndActions.Skip(6)).ToImmutableArray(), segments);
            }).ToImmutableArray();
            var cables = assembly.Cables.Select((cable, index) =>
            {
                bool active = cable.IsActive(tensions[index]);
                double tension = active ? tensions[index] : 0;
                bool sagging = cable.SagParameter > 0;
                return new StaticCableResult(cable.Cable.Id, cable.Axis, tension, active,
                    targets.TryGetValue(cable.Cable.Id, out var target) ? target : null, pretension[index], stageTensions?[index],
                    // parabola: f = w_n·L² / 8T
                    sagging ? cable.NormalLoad * cable.Length * cable.Length / (8 * tension) : null,
                    sagging ? cable.Tangent(tension) / cable.AxialStiffness : null,
                    fitHistory.Select(run => run[index]).ToImmutableArray());
            }).ToImmutableArray();
            if (joints.SelectMany(j => j.Displacements.Concat(j.Reactions).Concat(j.SuppressedDofForces))
                .Concat(frames.SelectMany(f => f.Segments.SelectMany(s => s.LocalEndActions.Concat(s.LocalDisplacements)
                    .Concat(s.Stations.SelectMany(station => station.LocalDisplacements.Concat(station.Actions))))))
                .Concat(cables.Select(c => c.Tension)).Any(value => !double.IsFinite(value)))
            {
                Error("static.recovery.nonfinite", ObjectKind.LoadCase, caseId, "Force/reaction recovery overflowed; no results returned.");
                return Failed();
            }
            Report(StaticProgressKind.Finished, fitRuns: cableFit?.Iterations ?? 0);
            return new(new(model.DocumentId, model.InputHash, model.Revision, caseId, joints, frames, cables,
                new(free.Length, residual, minimumPivot, unconnectedRotations.ToImmutableArray(), nonlinearIterations,
                    equilibriumError, cableFit)), issues.ToImmutableArray());
        }

        private void Unstable(int fullIndex, double pivot)
        {
            var dof = model.Dofs[fullIndex];
            Error("static.matrix.unstable", ObjectKind.Point, dof.Key.PointId,
                $"Stiffness factorization failed near '{document.Points[dof.Key.PointId].Name}' {dof.Key.Component}: pivot {pivot:G6}, tolerance {options.RelativePivotTolerance:G6}. Possible mechanism or severe ill-conditioning; this pivot is not a complete physical diagnosis. No displacements returned.");
        }

        /// <summary>Frame stiffness, cable elements and the load vector of one set of pattern factors. Cable self-weight
        /// goes to the cable's two ends by statics, half each; what it does to the cable's own shape is its sag.</summary>
        private Assembly? Assemble(Dictionary<ObjectId, double> factors, ObjectId caseId)
        {
            var stiffness = new double[n, n];
            var external = new double[n];
            foreach (var (pattern, factor) in factors)
                for (int i = 0; i < n; i++) external[i] += model.PointLoadsByPattern[pattern][i] * factor;
            double selfWeightFactor = factors.Sum(pair => document.LoadPatterns[pair.Key].SelfWeightMultiplier * pair.Value);
            int[] Indices(ObjectId pointId, int components) => DegreesOfFreedom.Components.Take(components)
                .Select(component => model.DofIndex[new(pointId, component)].FullIndex).ToArray();
            var frames = new List<(SpatialFrameElement Element, int[] Indices)>();
            var cables = new List<CableElement>();
            foreach (var member in model.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (start, end) = (document.Points[member.StartPointId].Position, document.Points[member.EndPointId].Position);
                if (member.Kind == ObjectKind.Cable)
                {
                    var cable = document.Cables[member.ParentObjectId];
                    var assigned = document.AssignmentOfCable(cable.Id);
                    var cableSection = document.CableSections[assigned.SectionId!.Value];
                    var cableMaterial = document.Materials[cableSection.MaterialId];
                    double length = (end - start).Length;
                    var axis = (end - start) * (1 / length);
                    int[] ends = [.. Indices(member.StartPointId, 3), .. Indices(member.EndPointId, 3)];
                    double perLength = cableMaterial.WeightDensity * cableSection.Area * assigned.Modifiers.Weight * selfWeightFactor;
                    external[ends[2]] -= perLength * length / 2;
                    external[ends[5]] -= perLength * length / 2;
                    // Only the part of the weight normal to the chord makes the cable sag: w·cos θ.
                    double normal = document.Settings.CableSag ? Math.Abs(perLength) * Math.Sqrt(Math.Max(0, 1 - axis.Z * axis.Z)) : 0;
                    cables.Add(new(cable, axis, length, cableMaterial.ElasticModulus * cableSection.Area * assigned.Modifiers.Area / length, ends, normal));
                    continue;
                }
                var frame = document.Frames[member.ParentObjectId];
                SpatialFrameElement element;
                try { element = new(document, frame, start, end, member.RelativeStart, member.RelativeEnd); }
                catch (Exception ex) when (ex is ArgumentException or FactorizationFailure)
                {
                    Error("static.element.invalid", ObjectKind.Frame, frame.Id, $"Frame '{frame.Name}': {ex.Message}");
                    return null;
                }
                foreach (var load in loadsByTarget[frame.Id])
                {
                    double factor = factors.GetValueOrDefault(load.PatternId);
                    if (factor != 0) element.AddFrameLoad(load, factor);
                }
                var assignment = document.AssignmentOfFrame(frame.Id);
                var section = document.FrameSections[assignment.SectionId!.Value];
                var material = document.Materials[section.MaterialId];
                double weight = material.WeightDensity * section.Properties.Area * assignment.Modifiers.Weight;
                if (weight * selfWeightFactor != 0)
                    element.AddDistributedLoad(0, 1, LoadDirection.GlobalZ, -weight * selfWeightFactor, -weight * selfWeightFactor);
                element.FinalizeLoad();
                int[] indices = [.. Indices(member.StartPointId, 6), .. Indices(member.EndPointId, 6)];
                var globalK = element.GlobalStiffness();
                var globalF = element.ToGlobal(element.Load);
                for (int i = 0; i < 12; i++)
                {
                    external[indices[i]] += globalF[i];
                    for (int j = 0; j < 12; j++) stiffness[indices[i], indices[j]] += globalK[i, j];
                }
                frames.Add((element, indices));
            }
            if (external.Any(value => !double.IsFinite(value)) || stiffness.Cast<double>().Any(value => !double.IsFinite(value)) ||
                cables.Any(cable => !double.IsFinite(cable.AxialStiffness) || !double.IsFinite(cable.NormalLoad)))
            {
                Error("static.assembly.nonfinite", ObjectKind.LoadCase, caseId, "Load/stiffness assembly contains nonfinite values; review magnitudes and units.");
                return null;
            }
            return new(frames, stiffness, external, cables.ToArray());
        }

        /// <summary>The free DOFs to solve. The rotation of a frame joint that no frame stiffness reaches (every end there
        /// released) and no moment loads is left out. A degree of freedom the model gives a joint only cables reach is
        /// not: if nothing holds it, the model is unstable there and the analysis says so.</summary>
        private (int[] Free, List<DofKey> Unconnected) FreeDofs(Assembly assembly, bool report)
        {
            var unconnected = new List<DofKey>();
            var free = new List<int>();
            var framed = model.Members.Where(member => member.Kind == ObjectKind.Frame)
                .SelectMany(member => new[] { member.StartPointId, member.EndPointId }).ToHashSet();
            foreach (var dof in model.Dofs.Where(dof => dof.State == DofState.Free))
            {
                bool zeroRow = Enumerable.Range(0, n).All(j => assembly.Stiffness[dof.FullIndex, j] == 0);
                if (zeroRow && dof.Key.Component >= DofComponent.RX && assembly.External[dof.FullIndex] == 0 && framed.Contains(dof.Key.PointId))
                {
                    unconnected.Add(dof.Key);
                    if (report)
                        issues.Add(new(ValidationSeverity.Warning, "static.rotation.unconnected", ObjectKind.Point, dof.Key.PointId,
                            $"'{document.Points[dof.Key.PointId].Name}' {dof.Key.Component} has an exactly zero, unloaded stiffness row (e.g. all connected ends released). No joint rotation is determined; it is omitted, not stabilized."));
                }
                else free.Add(dof.FullIndex);
            }
            return (free.ToArray(), unconnected);
        }

        /// <summary>K·u of the frames plus the pull of every taut cable on its two ends.</summary>
        private double[] InternalForce(Assembly assembly, double[] tensions, double[] displacement)
        {
            var force = DenseAlgebra.Multiply(assembly.Stiffness, displacement);
            for (int index = 0; index < assembly.Cables.Length; index++)
            {
                var cable = assembly.Cables[index];
                if (!cable.IsActive(tensions[index])) continue;
                double[] pull = [cable.Axis.X * tensions[index], cable.Axis.Y * tensions[index], cable.Axis.Z * tensions[index]];
                for (int i = 0; i < 3; i++)
                {
                    force[cable.Indices[i]] -= pull[i];
                    force[cable.Indices[3 + i]] += pull[i];
                }
            }
            return force;
        }

        private double[] Residual(Assembly assembly, double[] pretension, double[] displacement)
        {
            var residual = InternalForce(assembly, assembly.Cables.Select((cable, index) => cable.Tension(pretension[index], displacement)).ToArray(), displacement);
            for (int i = 0; i < n; i++) residual[i] -= assembly.External[i];
            return residual;
        }

        private double Energy(Assembly assembly, double[] pretension, double[] displacement)
        {
            var product = DenseAlgebra.Multiply(assembly.Stiffness, displacement);
            double energy = 0;
            for (int i = 0; i < n; i++) energy += displacement[i] * (0.5 * product[i] - assembly.External[i]);
            for (int index = 0; index < assembly.Cables.Length; index++)
                energy += assembly.Cables[index].Energy(assembly.Cables[index].Tension(pretension[index], displacement));
            return energy;
        }

        /// <summary>The tangent stiffness on the free DOFs. <paramref name="regularize"/> gives a slack cable a tiny
        /// stiffness, for a search direction only: a joint held by slack cables otherwise makes the tangent singular.</summary>
        private double[,] Tangent(Assembly assembly, double[] pretension, double[] displacement, int[] free, bool regularize)
        {
            var reducedIndex = new int[n];
            Array.Fill(reducedIndex, -1);
            for (int i = 0; i < free.Length; i++) reducedIndex[free[i]] = i;
            var tangent = new double[free.Length, free.Length];
            for (int i = 0; i < free.Length; i++)
            for (int j = 0; j < free.Length; j++) tangent[i, j] = assembly.Stiffness[free[i], free[j]];
            for (int index = 0; index < assembly.Cables.Length; index++)
            {
                var cable = assembly.Cables[index];
                double tension = cable.Tension(pretension[index], displacement);
                double k = cable.IsActive(tension) ? cable.Tangent(tension) : regularize ? SlackStiffnessRatio * cable.AxialStiffness : 0;
                if (k == 0) continue;
                double[] b = [-cable.Axis.X, -cable.Axis.Y, -cable.Axis.Z, cable.Axis.X, cable.Axis.Y, cable.Axis.Z];
                for (int row = 0; row < 6; row++)
                {
                    int i = reducedIndex[cable.Indices[row]];
                    if (i < 0) continue;
                    for (int col = 0; col < 6; col++)
                    {
                        int j = reducedIndex[cable.Indices[col]];
                        if (j >= 0) tangent[i, j] += k * b[row] * b[col];
                    }
                }
            }
            return tangent;
        }

        /// <summary>The minimum of the potential energy by damped Newton iteration. The residual and the energy are never
        /// regularized, so what it converges to is the equilibrium itself.</summary>
        private (double[] Displacement, int Iterations, double RelativeResidual)? Equilibrium(Assembly assembly, double[] pretension, int[] free, ObjectId caseId)
        {
            var displacement = new double[n];
            double scale = Math.Max(1, Math.Max(free.Select(i => Math.Abs(assembly.External[i])).DefaultIfEmpty().Max(), pretension.DefaultIfEmpty().Max()));
            double tolerance = options.NonlinearForceTolerance * scale, norm = double.PositiveInfinity;
            double Norm(double[] residual) => free.Select(i => Math.Abs(residual[i])).DefaultIfEmpty().Max();
            for (int iteration = 1; iteration <= options.MaximumNonlinearIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var residual = Residual(assembly, pretension, displacement);
                norm = Norm(residual);
                if (!double.IsFinite(norm)) break;
                if (progress is not null) Measure(free, free.Select(i => Math.Abs(residual[i])).ToArray(), iteration, tolerance);
                if (norm <= tolerance) return (displacement, iteration, norm / scale);

                var tangent = Tangent(assembly, pretension, displacement, free, regularize: true);
                var rhs = new double[free.Length];
                double maximumDiagonal = 0;
                bool unstable = false;
                for (int i = 0; i < free.Length; i++)
                {
                    if (tangent[i, i] > 0)
                    {
                        rhs[i] = -residual[free[i]];
                        maximumDiagonal = Math.Max(maximumDiagonal, tangent[i, i]);
                        continue;
                    }
                    // Neither a frame nor a cable, taut or slack, reaches this movement, and no restraint holds it.
                    var dof = model.Dofs[free[i]].Key;
                    Error("static.matrix.unstable", ObjectKind.Point, dof.PointId,
                        $"'{document.Points[dof.PointId].Name}' {dof.Component} has no stiffness and no restraint: the model is unstable there. " +
                        $"A cable gives stiffness along its own line only: hold that movement with another member or a restraint{CableJointOption}. No results returned.");
                    unstable = true;
                }
                if (unstable) return null;
                double[]? Step(double shift)
                {
                    var shifted = (double[,])tangent.Clone();
                    for (int i = 0; i < free.Length; i++) shifted[i, i] += shift * maximumDiagonal;
                    try { return new ScaledCholesky(shifted, options.RelativePivotTolerance, cancellationToken).Solve(rhs); }
                    catch (FactorizationFailure) { return null; }
                }
                double[] Moved(double[] increment, double step)
                {
                    var moved = (double[])displacement.Clone();
                    for (int i = 0; i < free.Length; i++) moved[free[i]] += step * increment[i];
                    return moved;
                }

                double energy = Energy(assembly, pretension, displacement);
                double[]? accepted = null;
                foreach (double shift in LevenbergShifts)
                {
                    if (Step(shift) is not { } increment) continue;
                    double slope = 0;
                    for (int i = 0; i < free.Length; i++) slope += residual[free[i]] * increment[i];
                    if (!double.IsFinite(slope) || slope >= 0) continue;
                    for (double step = 1; step >= 1.0 / 1_048_576 && accepted is null; step *= 0.5)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var candidate = Moved(increment, step);
                        double candidateEnergy = Energy(assembly, pretension, candidate);
                        if (!double.IsFinite(candidateEnergy)) continue;
                        if (candidateEnergy <= energy + 1e-4 * step * slope) accepted = candidate;
                        // Close to the equilibrium the full Newton step lowers the energy by less than the round-off of
                        // the energy sum, so the energy cannot confirm it. A full step that does not raise the energy
                        // beyond that round-off and at least halves the out-of-balance force is the Newton step doing
                        // its work; shortening it instead would stop the convergence.
                        else if (step == 1 && candidateEnergy <= energy + 1e-10 * Math.Max(1, Math.Abs(energy)) &&
                                 Norm(Residual(assembly, pretension, candidate)) <= 0.5 * norm) accepted = candidate;
                    }
                    if (accepted is not null) break;
                }
                if (accepted is null)
                {
                    // Next to the equilibrium a step changes the energy by less than the round-off of the energy sum, so
                    // the test above cannot confirm a Newton step that is right. The energy is convex — one equilibrium —
                    // so a full step that at least halves the residual is progress; convergence is still judged by the residual.
                    foreach (double shift in LevenbergShifts)
                    {
                        if (Step(shift) is not { } increment) continue;
                        var candidate = Moved(increment, 1);
                        if (candidate.Any(value => !double.IsFinite(value))) continue;
                        if (Norm(Residual(assembly, pretension, candidate)) <= 0.5 * norm)
                        {
                            accepted = candidate;
                            break;
                        }
                    }
                }
                if (accepted is null)
                {
                    Error("static.nonlinear.stalled", ObjectKind.LoadCase, caseId,
                        $"The cable equilibrium iteration made no further progress at an out-of-balance force of {norm:G6} kN (accepted {tolerance:G6} kN); no results returned.");
                    return null;
                }
                displacement = accepted;
            }
            Error("static.nonlinear.noConvergence", ObjectKind.LoadCase, caseId,
                $"The cable equilibrium did not converge in {options.MaximumNonlinearIterations} iterations (out-of-balance force {norm:G6} kN, accepted {tolerance:G6} kN); no results returned.");
            return null;
        }

        /// <summary>Newton iteration on the cable pretensions so that, at the end of the fit case, every targeted cable's
        /// tension is within the model's tolerance of its target. The Jacobian is built by finite differences over all
        /// targeted cables, so that tensioning one cable changing the others is accounted for. It follows the intent
        /// of a target-force load; it is not another program's algorithm.</summary>
        private Fit? FitPretension(Assembly stage, int[] free, double[] targets, LoadCase fitCase)
        {
            var history = new List<double[]>();
            double tolerancePercent = document.Settings.CableFitTolerancePercent;
            int maximumIterations = document.Settings.CableFitMaxIterations;
            var targeted = Enumerable.Range(0, targets.Length).Where(index => targets[index] > 0).ToArray();
            var applied = (double[])targets.Clone();
            double[]? StageTensions(double[] pretension) => Equilibrium(stage, pretension, free, fitCase.Id) is { } equilibrium
                ? stage.Cables.Select((cable, index) => Math.Max(0, cable.Tension(pretension[index], equilibrium.Displacement))).ToArray()
                : null;
            double Deviation(double[] tensions, int index) => Math.Abs(tensions[index] - targets[index]) / targets[index] * 100;

            (double[] Applied, double[] Tensions, double Worst) best = ([], [], double.PositiveInfinity);
            var needsCompression = new SortedSet<string>(StringComparer.Ordinal);
            for (int iteration = 1; ; iteration++)
            {
                fitRun = iteration;
                if (StageTensions(applied) is not { } tensions) return null;
                history.Add(tensions);
                int worst = targeted.MaxBy(index => Deviation(tensions, index));
                double worstDeviation = Deviation(tensions, worst);
                if (!double.IsFinite(worstDeviation))
                {
                    Error("static.fit.nonfinite", ObjectKind.LoadCase, fitCase.Id, $"Case '{fitCase.Name}': a cable tension overflowed while fitting the target forces; no results returned.");
                    return null;
                }
                if (worstDeviation < best.Worst) best = ((double[])applied.Clone(), tensions, worstDeviation);
                if (worstDeviation <= tolerancePercent) return new(applied, tensions, iteration, worstDeviation, history, null);
                string? reason = iteration >= maximumIterations ? $"{maximumIterations} pretension iterations used" : null;

                double[] correction = [];
                if (reason is null)
                {
                    var jacobian = new double[targeted.Length, targeted.Length];
                    for (int column = 0; column < targeted.Length; column++)
                    {
                        int cable = targeted[column];
                        double step = Math.Max(1, 1e-3 * targets[cable]);
                        var perturbed = (double[])applied.Clone();
                        perturbed[cable] += step;
                        if (StageTensions(perturbed) is not { } perturbedTensions) return null;
                        for (int row = 0; row < targeted.Length; row++)
                            jacobian[row, column] = (perturbedTensions[targeted[row]] - tensions[targeted[row]]) / step;
                    }
                    var deficit = new double[targeted.Length];
                    for (int row = 0; row < targeted.Length; row++)
                    {
                        deficit[row] = targets[targeted[row]] - tensions[targeted[row]];
                        // A slack cable does not respond until it becomes taut; push its own pretension directly.
                        if (Math.Abs(jacobian[row, row]) < 1e-6)
                        {
                            for (int column = 0; column < targeted.Length; column++) jacobian[row, column] = 0;
                            jacobian[row, row] = 1;
                        }
                    }
                    // Cables in series share one tension, so only their combined pretension is determined; the
                    // minimum-norm solution splits the correction evenly instead of failing or picking an extreme.
                    // The Jacobian comes from differences of converged equilibria, so it is known to about the
                    // equilibrium tolerance: a response that small is not a response.
                    correction = DenseAlgebra.MinimumNormSolve(jacobian, deficit, FitRankTolerance);
                    if (correction.Any(value => !double.IsFinite(value))) reason = "the cable pretension responses are not independent";
                }
                if (reason is not null)
                {
                    int closest = targeted.MaxBy(index => Deviation(best.Tensions, index));
                    string failure = $"Not fitted within {tolerancePercent:0.###}%: {reason}; closest has '{stage.Cables[closest].Cable.Name}' at {best.Tensions[closest]:0.##} kN against its {targets[closest]:0.##} kN target ({best.Worst:0.##}%)." +
                        (needsCompression.Count > 0 ? $" Meeting every target would need negative pretension in {string.Join(", ", needsCompression.Select(name => $"'{name}'"))}, so this combination of target forces is not achievable." : "");
                    if (options.AcceptClosestFit) return new(best.Applied, best.Tensions, iteration, best.Worst, history, failure);
                    Error("static.fit.notConverged", ObjectKind.LoadCase, fitCase.Id, $"Case '{fitCase.Name}': {failure} No results returned.");
                    return null;
                }
                needsCompression.Clear();
                for (int row = 0; row < targeted.Length; row++)
                {
                    double next = applied[targeted[row]] + correction[row];
                    if (next < 0) needsCompression.Add(stage.Cables[targeted[row]].Cable.Name);
                    applied[targeted[row]] = Math.Max(0, next);
                }
            }
        }

        /// <param name="Failure">Null when every target was met within the tolerance.</param>
        internal sealed record Fit(double[] Applied, double[] Tensions, int Iterations, double WorstDeviationPercent,
            List<double[]> History, string? Failure);

        /// <summary>Every element's forces on its own ends are in equilibrium, so the internal forces of the whole model
        /// have no resultant force and no resultant moment. A transformation or assembly defect shows here.</summary>
        private double EquilibriumError(double[] internalForce)
        {
            Xyz force = default, moment = default;
            double forceSize = 0, momentSize = 0;
            foreach (var point in model.Points)
            {
                var index = DegreesOfFreedom.Components.Select(component => model.DofIndex[new(point.Id, component)].FullIndex).ToArray();
                Xyz f = new(internalForce[index[0]], internalForce[index[1]], internalForce[index[2]]);
                Xyz m = new(internalForce[index[3]], internalForce[index[4]], internalForce[index[5]]);
                var arm = point.Position.Cross(f);
                force += f;
                moment += arm + m;
                forceSize += f.Length;
                momentSize += arm.Length + m.Length;
            }
            return Math.Max(force.Length / Math.Max(1, forceSize), moment.Length / Math.Max(1, momentSize));
        }
    }
}
