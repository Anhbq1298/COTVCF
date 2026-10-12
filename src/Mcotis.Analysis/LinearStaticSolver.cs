using System.Collections.Immutable;
using Mcotis.Core;

namespace Mcotis.Analysis;

public sealed record LinearStaticOptions(int MaximumFullDofs = 2400, int MaximumFreeDofs = 600,
    double RelativePivotTolerance = 1e-12, double ResidualTolerance = 1e-10);

public sealed record StaticJointResult(ObjectId PointId, ImmutableArray<double> Displacements,
    ImmutableArray<double> Reactions, ImmutableArray<double> SuppressedDofForces);

/// <summary>Local end actions ordered [F1,F2,F3,M1,M2,M3] at I then J; released member rotations are recovered separately from joint rotations.</summary>
public sealed record StaticFrameResult(ObjectId ParentObjectId, Xyz Axis1, Xyz Axis2, Xyz Axis3,
    ImmutableArray<double> LocalDisplacements, ImmutableArray<double> LocalEndActions);

public sealed record StaticDiagnostics(int SolvedDofCount, double ScaledResidual, double MinimumScaledPivot,
    ImmutableArray<DofKey> UnconnectedRotations);

public sealed record LinearStaticResult(ObjectId DocumentId, string InputHash, long Revision, ObjectId LoadCaseId,
    ImmutableArray<StaticJointResult> Joints, ImmutableArray<StaticFrameResult> Frames, StaticDiagnostics Diagnostics)
{
    public const string FormulationVersion = "spatial-eb-linear-1";
}

public sealed record LinearStaticOutcome(LinearStaticResult? Result, ImmutableArray<ValidationIssue> Issues)
{
    public bool Success => Result is not null;
}

/// <summary>Limited dense 3D frame validation engine. One prismatic element per native frame; nodal and linear member
/// loads, self-weight, moment releases and fixed zero-displacement supports. No nonlinear cable/P-Delta/modal/shell physics.</summary>
public static class LinearStaticSolver
{
    public static LinearStaticOutcome Solve(CompiledModel model, ObjectId caseId, LinearStaticOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new();
        if (options.MaximumFullDofs < 1 || options.MaximumFreeDofs < 1 ||
            !double.IsFinite(options.RelativePivotTolerance) || options.RelativePivotTolerance <= 0 || options.RelativePivotTolerance >= 1 ||
            !double.IsFinite(options.ResidualTolerance) || options.ResidualTolerance <= 0 || options.ResidualTolerance >= 1)
            throw new ArgumentException("Solver limits must be positive and numerical tolerances finite within (0,1).", nameof(options));
        var document = model.Input;
        var issues = new List<ValidationIssue>();
        void Error(string code, ObjectKind kind, ObjectId id, string message) => issues.Add(new(ValidationSeverity.Error, code, kind, id, message));
        LinearStaticOutcome Failed() => new(null, issues.ToImmutableArray());
        if (!document.LoadCases.TryGetValue(caseId, out var loadCase))
        {
            Error("static.case.missing", ObjectKind.LoadCase, caseId, "Select an existing load case.");
            return Failed();
        }
        if (loadCase.Type != LoadCaseType.LinearStatic || loadCase.InitialCaseId is not null || loadCase.FitCableTargetForces)
            Error("static.case.unsupported", ObjectKind.LoadCase, caseId, $"Case '{loadCase.Name}' requires physics or staging outside the linear static frame engine.");
        foreach (var cable in document.Cables.Values)
            Error("static.cable.unsupported", ObjectKind.Cable, cable.Id, $"Cable '{cable.Name}' requires a separately verified tension-only/sag solver; it is not replaced by an elastic frame.");
        if (model.Dofs.Length > options.MaximumFullDofs || model.FreeDofCount > options.MaximumFreeDofs)
            Error("static.size", ObjectKind.Settings, default, "This dense validation engine's size limit was exceeded; a sparse engine is required.");
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
            var start = document.Points[frame.StartPointId].Position;
            var delta = document.Points[frame.EndPointId].Position - start;
            foreach (var point in document.Points.Values.Where(p => p.Id != frame.StartPointId && p.Id != frame.EndPointId))
            {
                double relative = (point.Position - start).Dot(delta) / delta.Dot(delta);
                if (relative > 0 && relative < 1 && point.Position.DistanceTo(start + delta * relative) <= Tolerances.Length)
                    Error("static.interiorJoint.unsupported", ObjectKind.Frame, frame.Id,
                        $"Frame '{frame.Name}' contains point '{point.Name}' internally. Split the frame at that point before using this engine; it will not silently miss the connection.");
            }
        }
        if (issues.Count > 0) return Failed();

        int n = model.Dofs.Length;
        var stiffness = new double[n, n];
        var external = new double[n];
        var factors = loadCase.Components.GroupBy(component => component.PatternId)
            .ToDictionary(group => group.Key, group => group.Sum(component => component.Factor));
        if (factors.Values.Any(value => !double.IsFinite(value)))
        {
            Error("static.load.nonfinite", ObjectKind.LoadCase, caseId, "Load pattern factors overflowed when combined.");
            return Failed();
        }
        foreach (var (pattern, factor) in factors)
            for (int i = 0; i < n; i++) external[i] += model.PointLoadsByPattern[pattern][i] * factor;
        var elements = new List<(SpatialFrameElement Element, int[] Indices)>();
        foreach (var frame in document.Frames.Values.OrderBy(frame => frame.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpatialFrameElement element;
            try { element = new(document, frame); }
            catch (Exception ex) when (ex is ArgumentException or FactorizationFailure)
            {
                Error("static.element.invalid", ObjectKind.Frame, frame.Id, $"Frame '{frame.Name}': {ex.Message}");
                return Failed();
            }
            foreach (var load in document.Loads.Values.Where(load => load.TargetId == frame.Id).OrderBy(load => load.Id))
            {
                double factor = factors.GetValueOrDefault(load.PatternId);
                if (factor == 0) continue;
                if (load is FramePointLoad point)
                    element.AddPointLoad(point.RelativeDistance, element.LoadVector(point.Direction, point.Value * factor));
                else if (load is FrameDistributedLoad line)
                    element.AddDistributedLoad(line.RelativeStart, line.RelativeEnd, line.Direction, line.ValueStart * factor, line.ValueEnd * factor);
            }
            var assignment = document.AssignmentOfFrame(frame.Id);
            var section = document.FrameSections[assignment.SectionId!.Value];
            var material = document.Materials[section.MaterialId];
            double weight = material.WeightDensity * section.Properties.Area * assignment.Modifiers.Weight;
            double selfWeightFactor = factors.Sum(pair => document.LoadPatterns[pair.Key].SelfWeightMultiplier * pair.Value);
            if (weight * selfWeightFactor != 0)
                element.AddDistributedLoad(0, 1, LoadDirection.GlobalZ, -weight * selfWeightFactor, -weight * selfWeightFactor);
            element.FinalizeLoad();
            var indices = new[] { frame.StartPointId, frame.EndPointId }
                .SelectMany(id => DegreesOfFreedom.Components.Select(component => model.DofIndex[new(id, component)].FullIndex)).ToArray();
            var globalK = element.GlobalStiffness();
            var globalF = element.ToGlobal(element.Load);
            for (int i = 0; i < 12; i++)
            {
                external[indices[i]] += globalF[i];
                for (int j = 0; j < 12; j++) stiffness[indices[i], indices[j]] += globalK[i, j];
            }
            elements.Add((element, indices));
        }
        if (external.Any(value => !double.IsFinite(value)) || stiffness.Cast<double>().Any(value => !double.IsFinite(value)))
        {
            Error("static.assembly.nonfinite", ObjectKind.LoadCase, caseId, "Load/stiffness assembly contains nonfinite values; review magnitudes and units.");
            return Failed();
        }
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
        var unconnectedRotations = new List<DofKey>();
        var free = new List<int>();
        foreach (var dof in model.Dofs.Where(dof => dof.State == DofState.Free))
        {
            bool zeroRow = Enumerable.Range(0, n).All(j => stiffness[dof.FullIndex, j] == 0);
            if (zeroRow && dof.Key.Component >= DofComponent.RX && external[dof.FullIndex] == 0)
            {
                unconnectedRotations.Add(dof.Key);
                issues.Add(new(ValidationSeverity.Warning, "static.rotation.unconnected", ObjectKind.Point, dof.Key.PointId,
                    $"'{document.Points[dof.Key.PointId].Name}' {dof.Key.Component} has an exactly zero, unloaded stiffness row (e.g. all connected ends released). No joint rotation is determined; it is omitted, not stabilized."));
            }
            else free.Add(dof.FullIndex);
        }
        var reduced = new double[free.Count, free.Count];
        var rhs = free.Select(i => external[i]).ToArray();
        for (int i = 0; i < free.Count; i++)
        for (int j = 0; j < free.Count; j++) reduced[i, j] = stiffness[free[i], free[j]];
        double[] solved;
        double minimumPivot = 1;
        try
        {
            var factor = new ScaledCholesky(reduced, options.RelativePivotTolerance, cancellationToken);
            solved = factor.Solve(rhs);
            minimumPivot = factor.MinimumPivot;
        }
        catch (FactorizationFailure failure)
        {
            var dof = model.Dofs[free[failure.Index]];
            Error("static.matrix.unstable", ObjectKind.Point, dof.Key.PointId,
                $"Stiffness factorization failed near '{document.Points[dof.Key.PointId].Name}' {dof.Key.Component}: pivot {failure.Pivot:G6}, tolerance {options.RelativePivotTolerance:G6}. Possible mechanism or severe ill-conditioning; this pivot is not a complete physical diagnosis. No displacements returned.");
            return Failed();
        }
        double residual = DenseAlgebra.ScaledResidual(reduced, solved, rhs);
        if (solved.Any(value => !double.IsFinite(value)) || !double.IsFinite(residual) || residual > options.ResidualTolerance)
        {
            Error("static.residual", ObjectKind.LoadCase, caseId, $"The scaled backward error ({residual:G6}) exceeds the accepted tolerance ({options.ResidualTolerance:G6}); no results returned.");
            return Failed();
        }
        var displacement = new double[n];
        for (int i = 0; i < free.Count; i++) displacement[free[i]] = solved[i];
        var residualFull = DenseAlgebra.Multiply(stiffness, displacement);
        for (int i = 0; i < n; i++) residualFull[i] -= external[i];
        var joints = model.Points.Select(point =>
        {
            var dofs = DegreesOfFreedom.Components.Select(c => model.DofIndex[new(point.Id, c)]).ToArray();
            return new StaticJointResult(point.Id, dofs.Select(d => displacement[d.FullIndex]).ToImmutableArray(),
                dofs.Select(d => d.State == DofState.Restrained ? residualFull[d.FullIndex] : 0).ToImmutableArray(),
                dofs.Select(d => d.State == DofState.Inactive ? residualFull[d.FullIndex] : 0).ToImmutableArray());
        }).ToImmutableArray();
        var frames = elements.Select(item =>
        {
            var (local, forces) = item.Element.Recover(item.Indices.Select(i => displacement[i]).ToArray());
            return new StaticFrameResult(item.Element.Frame.Id, item.Element.Axis1, item.Element.Axis2, item.Element.Axis3,
                local.ToImmutableArray(), forces.ToImmutableArray());
        }).ToImmutableArray();
        if (joints.SelectMany(j => j.Reactions.Concat(j.SuppressedDofForces)).Concat(frames.SelectMany(f => f.LocalEndActions.Concat(f.LocalDisplacements)))
            .Any(value => !double.IsFinite(value)))
        {
            Error("static.recovery.nonfinite", ObjectKind.LoadCase, caseId, "Force/reaction recovery overflowed; no results returned.");
            return Failed();
        }
        return new(new(model.DocumentId, model.InputHash, model.Revision, caseId, joints, frames,
            new(free.Count, residual, minimumPivot, unconnectedRotations.ToImmutableArray())), issues.ToImmutableArray());
    }
}
