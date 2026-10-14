using System.Collections.Immutable;
using Mcotis.Core;

namespace Mcotis.Analysis;

public enum ModalEigenStatus { Positive, NearZero, Negative }
public enum ModalProgressKind { Validating, AssemblingMass, ReducingDofs, ExtractingEigenpairs, ValidatingResults, Finished, Failed }

public sealed record ModalOptions(int MaximumFullDofs = 2400, int MaximumInertialDofs = 400,
    double RelativePivotTolerance = 1e-12, double EigenTolerance = 1e-10, double ResidualTolerance = 1e-8);

public sealed record ModalProgress(ObjectId CaseId, ModalProgressKind Kind, int CompletedModes = 0,
    int RequestedModes = 0, ValidationIssue? Issue = null);

public sealed record ModalParticipation(DofComponent Direction, double? Factor, double? EffectiveMass,
    double? RatioPercent, double? CumulativePercent);

public sealed record ModalJointAmplitude(ObjectId PointId, double UX, double UY, double UZ,
    double RX, double RY, double RZ)
{
    public ImmutableArray<double> Values => [UX, UY, UZ, RX, RY, RZ];
}

public sealed record ModalModeResult(int ModeNumber, double EigenvaluePerSecondSquared, double? FrequencyHz,
    double? PeriodSeconds, ModalEigenStatus Status, ImmutableArray<ModalJointAmplitude> Shape,
    ImmutableArray<ModalParticipation> Participation, double ScaledResidual);

public sealed record ModalMassInventory(double ElementMass, ImmutableArray<double> AdditionalByDof,
    ImmutableArray<double> FreeMassByDof, Xyz? CenterOfMass)
{
    /// <summary>Equivalent physical mass used for inventory and centre-of-mass reporting. Direction-dependent
    /// translational assignments are averaged; their individual modal operators remain unchanged.</summary>
    public double AdditionalTranslationalMass => AdditionalByDof.Take(3).Average();
}

public sealed record ModalSolverMetadata(string Method, string Version, int InertialDofs, int CondensedMasslessDofs,
    double EigenTolerance, double ResidualTolerance);

public sealed record ModalAnalysisResult(ObjectId DocumentId, ObjectId ModalCaseId, string InputHash, long Revision,
    ObjectId EffectiveMassSourceId, ObjectId? StiffnessProducerCaseId, string StiffnessFingerprint,
    ModalSolverMetadata Solver, ImmutableArray<ModalModeResult> Modes, ModalMassInventory MassSummary,
    ImmutableArray<ValidationIssue> Diagnostics)
{
    public const string FormulationVersion = "modal-eigen-lumped-1";
}

public sealed record ModalOutcome(ModalAnalysisResult? Result, ImmutableArray<ValidationIssue> Issues)
{
    public bool Success => Result is not null && Issues.All(issue => issue.Severity != ValidationSeverity.Error);
}

/// <summary>Dense, source-visible eigen solver for the verified lumped-mass MVP. It solves the symmetric standard
/// problem produced by diagonal mass whitening. It is deliberately isolated so a sparse/LAPACK implementation can
/// replace it without changing structural assembly or result contracts.</summary>
public interface IGeneralizedEigenSolver
{
    (double[] Eigenvalues, double[,] Eigenvectors) Solve(double[,] stiffness, double[] diagonalMass,
        CancellationToken cancellationToken = default);
}

public sealed class DenseSymmetricEigenSolver : IGeneralizedEigenSolver
{
    public const string Version = "cyclic-jacobi-1";

    public (double[] Eigenvalues, double[,] Eigenvectors) Solve(double[,] stiffness, double[] diagonalMass,
        CancellationToken cancellationToken = default)
    {
        int n = diagonalMass.Length;
        if (stiffness.GetLength(0) != n || stiffness.GetLength(1) != n)
            throw new ArgumentException("Stiffness and mass dimensions do not agree.");
        if (diagonalMass.Any(value => !(value > 0) || !double.IsFinite(value)))
            throw new ArgumentException("The reduced modal mass must be finite and positive definite.");
        var a = new double[n, n];
        var vectors = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            vectors[i, i] = 1;
            for (int j = 0; j < n; j++) a[i, j] = stiffness[i, j] / Math.Sqrt(diagonalMass[i] * diagonalMass[j]);
        }

        bool converged = n < 2;
        for (int sweep = 0; sweep < 80 && !converged; sweep++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double diagonalScale = 1, largest = 0;
            for (int i = 0; i < n; i++) diagonalScale = Math.Max(diagonalScale, Math.Abs(a[i, i]));
            for (int p = 0; p < n - 1; p++)
            for (int q = p + 1; q < n; q++)
            {
                double apq = a[p, q];
                largest = Math.Max(largest, Math.Abs(apq));
                if (Math.Abs(apq) <= 1e-14 * diagonalScale) continue;
                double tau = (a[q, q] - a[p, p]) / (2 * apq);
                double t = tau == 0 ? 1 : Math.CopySign(1, tau) / (Math.Abs(tau) + Math.Sqrt(1 + tau * tau));
                double c = 1 / Math.Sqrt(1 + t * t), s = t * c;
                double app = a[p, p], aqq = a[q, q];
                a[p, p] = app - t * apq;
                a[q, q] = aqq + t * apq;
                a[p, q] = a[q, p] = 0;
                for (int k = 0; k < n; k++)
                {
                    if (k != p && k != q)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = a[p, k] = c * akp - s * akq;
                        a[k, q] = a[q, k] = s * akp + c * akq;
                    }
                    double vkp = vectors[k, p], vkq = vectors[k, q];
                    vectors[k, p] = c * vkp - s * vkq;
                    vectors[k, q] = s * vkp + c * vkq;
                }
            }
            converged = largest <= 1e-12 * diagonalScale;
        }
        if (!converged) throw new InvalidOperationException("The dense symmetric eigensolver did not converge.");

        var order = Enumerable.Range(0, n).OrderBy(i => a[i, i]).ToArray();
        var values = order.Select(i => a[i, i]).ToArray();
        var modes = new double[n, n];
        for (int column = 0; column < n; column++)
        for (int row = 0; row < n; row++) modes[row, column] = vectors[row, order[column]] / Math.Sqrt(diagonalMass[row]);
        return (values, modes);
    }
}

/// <summary>Eigenvector analysis of the native frame model. First release: elastic frame stiffness, lumped element
/// self mass and explicit joint mass. It never inserts restraints or fictitious mass. Cables require a converged
/// prestressed tangent and are therefore refused until that separate capability is verified.</summary>
public static class ModalSolver
{
    public static ModalOutcome Solve(CompiledModel model, ObjectId caseId, ModalOptions? options = null,
        CancellationToken cancellationToken = default, IProgress<ModalProgress>? progress = null,
        IGeneralizedEigenSolver? eigenSolver = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new();
        eigenSolver ??= new DenseSymmetricEigenSolver();
        var issues = new List<ValidationIssue>();
        LoadCase? loadCase = null;
        void Report(ModalProgressKind kind, int done = 0, ValidationIssue? issue = null) =>
            progress?.Report(new(caseId, kind, done, loadCase?.ModalOptions?.RequestedModes ?? 0, issue));
        ModalOutcome Fail(string code, ObjectKind kind, ObjectId id, string message)
        {
            var issue = new ValidationIssue(ValidationSeverity.Error, code, kind, id, message);
            issues.Add(issue); Report(ModalProgressKind.Failed, issue: issue);
            return new(null, issues.ToImmutableArray());
        }

        Report(ModalProgressKind.Validating);
        if (!model.Input.LoadCases.TryGetValue(caseId, out loadCase) || loadCase.Kind != CaseKind.ModalEigen || loadCase.ModalOptions is not { } modal)
            return Fail("modal.case", ObjectKind.LoadCase, caseId, "Select a Modal Eigen case with a mass source and requested mode count.");
        if (loadCase.StiffnessSourceCaseId is not null)
            return Fail("modal.stiffness.source", ObjectKind.LoadCase, caseId,
                $"Case '{loadCase.Name}' requires end-of-nonlinear-case stiffness. A verified frozen tangent state is not available; elastic stiffness is not substituted.");
        if (model.Input.Shells.Count > 0)
            return Fail("modal.shell.unsupported", ObjectKind.LoadCase, caseId, "Modal analysis has no verified shell stiffness or mass operator.");
        if (model.Input.Cables.Count > 0)
            return Fail("modal.cable.state", ObjectKind.LoadCase, caseId,
                "A cable modal model requires a converged prestressed tangent and frozen taut/slack active set. Unstressed cable stiffness is not invented.");
        if (!model.Input.MassSources.TryGetValue(modal.MassSourceId, out var source))
            return Fail("modal.mass.source", ObjectKind.LoadCase, caseId, $"Case '{loadCase.Name}' uses a mass source that does not exist.");
        if (model.Dofs.Length > options.MaximumFullDofs)
            return Fail("modal.limit.full", ObjectKind.LoadCase, caseId, $"The model has {model.Dofs.Length} DOFs; this modal solver supports {options.MaximumFullDofs}.");
        if (modal.RequestedModes < 1)
            return Fail("modal.modes", ObjectKind.LoadCase, caseId, "The requested number of modes must be positive.");

        Report(ModalProgressKind.AssemblingMass);
        int n = model.Dofs.Length;
        var stiffness = new double[n, n];
        var mass = new double[n];
        double elementMass = 0;
        var weightedCenter = new Xyz();
        foreach (var member in model.Members.Where(member => member.Kind == ObjectKind.Frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = model.Input.Frames[member.ParentObjectId];
            var assignment = model.Input.AssignmentOfFrame(frame.Id);
            if (assignment.SectionId is not { } sectionId)
                return Fail("modal.section", ObjectKind.Frame, frame.Id, $"Frame '{frame.Name}' needs a section for modal analysis.");
            var section = model.Input.FrameSections[sectionId];
            var material = model.Input.Materials[section.MaterialId];
            if (FrameStiffnessDemand.Missing(model.Input, frame, model.ActiveDofs) is { } missing)
                return Fail("modal.section.stiffness", ObjectKind.Frame, frame.Id, missing);
            var start = model.Input.Points[member.StartPointId].Position;
            var end = model.Input.Points[member.EndPointId].Position;
            SpatialFrameElement element;
            try { element = new(model.Input, frame, start, end, member.RelativeStart, member.RelativeEnd); }
            catch (Exception ex) when (ex is ArgumentException or FactorizationFailure)
            { return Fail("modal.element", ObjectKind.Frame, frame.Id, $"Frame '{frame.Name}': {ex.Message}"); }
            int[] indices = [.. DegreesOfFreedom.Components.Select(c => model.DofIndex[new(member.StartPointId, c)].FullIndex),
                .. DegreesOfFreedom.Components.Select(c => model.DofIndex[new(member.EndPointId, c)].FullIndex)];
            var ke = element.GlobalStiffness();
            for (int i = 0; i < 12; i++)
            for (int j = 0; j < 12; j++) stiffness[indices[i], indices[j]] += ke[i, j];

            if (source.IncludeSelfMass)
            {
                double value = material.MassDensity * section.Properties.Area * (end - start).Length * assignment.Modifiers.Mass;
                if (!double.IsFinite(value) || value < 0)
                    return Fail("modal.mass.value", ObjectKind.Frame, frame.Id, $"Frame '{frame.Name}' produces invalid self mass.");
                elementMass += value;
                weightedCenter += (start + end) * (value / 2);
                for (int component = 0; component < 3; component++)
                { mass[indices[component]] += value / 2; mass[indices[6 + component]] += value / 2; }
            }
        }
        var additional = new double[6];
        if (source.IncludeAdditionalMass)
            foreach (var assigned in model.Input.JointMassAssignments.Values)
            {
                var values = assigned.Values;
                double physicalJointMass = values.Take(3).Average();
                weightedCenter += model.Input.Points[assigned.PointId].Position * physicalJointMass;
                for (int component = 0; component < 6; component++)
                {
                    mass[model.DofIndex[new(assigned.PointId, (DofComponent)component)].FullIndex] += values[component];
                    additional[component] += values[component];
                }
            }
        if (stiffness.Cast<double>().Any(value => !double.IsFinite(value)) || mass.Any(value => !double.IsFinite(value) || value < 0))
            return Fail("modal.assembly.nonfinite", ObjectKind.LoadCase, caseId, "Modal stiffness or mass contains a nonfinite/negative value.");

        var free = model.Dofs.Where(dof => dof.State == DofState.Free).Select(dof => dof.FullIndex).ToArray();
        var dynamic = free.Where(index => mass[index] > 0).ToArray();
        var zero = free.Where(index => mass[index] == 0).ToArray();
        if (dynamic.Length == 0)
            return Fail("modal.mass.none", ObjectKind.MassSource, source.Id, $"Mass source '{source.Name}' gives no positive mass to a free active DOF.");
        if (dynamic.Length > options.MaximumInertialDofs)
            return Fail("modal.limit.inertial", ObjectKind.LoadCase, caseId,
                $"The model has {dynamic.Length} inertial DOFs; this dense modal solver supports {options.MaximumInertialDofs}.");

        Report(ModalProgressKind.ReducingDofs);
        var condensed = new double[dynamic.Length, dynamic.Length];
        for (int i = 0; i < dynamic.Length; i++)
        for (int j = 0; j < dynamic.Length; j++) condensed[i, j] = stiffness[dynamic[i], dynamic[j]];
        double[,] recovery = new double[zero.Length, dynamic.Length];
        if (zero.Length > 0)
        {
            var kzz = new double[zero.Length, zero.Length];
            for (int i = 0; i < zero.Length; i++)
            for (int j = 0; j < zero.Length; j++) kzz[i, j] = stiffness[zero[i], zero[j]];
            ScaledCholesky factor;
            try { factor = new(kzz, options.RelativePivotTolerance, cancellationToken); }
            catch (FactorizationFailure failure)
            {
                var dof = model.Dofs[zero[Math.Clamp(failure.Index, 0, zero.Length - 1)]].Key;
                return Fail("modal.massless.uncondensable", ObjectKind.Point, dof.PointId,
                    $"Massless free DOF '{model.Input.Points[dof.PointId].Name}' {dof.Component} cannot be condensed: its stiffness block is singular or unstable. No restraint or artificial mass was added.");
            }
            for (int column = 0; column < dynamic.Length; column++)
            {
                var rhs = zero.Select(row => stiffness[row, dynamic[column]]).ToArray();
                var solved = factor.Solve(rhs);
                for (int row = 0; row < zero.Length; row++) recovery[row, column] = -solved[row];
            }
            for (int i = 0; i < dynamic.Length; i++)
            for (int j = 0; j < dynamic.Length; j++)
                for (int z = 0; z < zero.Length; z++) condensed[i, j] += stiffness[dynamic[i], zero[z]] * recovery[z, j];
        }
        var diagonalMass = dynamic.Select(index => mass[index]).ToArray();

        Report(ModalProgressKind.ExtractingEigenpairs);
        double[] eigenvalues;
        double[,] eigenvectors;
        try { (eigenvalues, eigenvectors) = eigenSolver.Solve(condensed, diagonalMass, cancellationToken); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { return Fail("modal.eigensolver", ObjectKind.LoadCase, caseId, $"Eigenvalue extraction failed: {ex.Message}"); }
        int count = Math.Min(modal.RequestedModes, dynamic.Length);
        if (count < modal.RequestedModes)
            issues.Add(new(ValidationSeverity.Warning, "modal.modes.fewer", ObjectKind.LoadCase, caseId,
                $"Case '{loadCase.Name}' requested {modal.RequestedModes} modes; only {dynamic.Length} inertial DOFs exist, so {count} modes can be returned."));

        Report(ModalProgressKind.ValidatingResults);
        var cumulative = new double[3];
        var freeMass = new double[6];
        foreach (var dof in model.Dofs.Where(dof => dof.State == DofState.Free)) freeMass[(int)dof.Key.Component] += mass[dof.FullIndex];
        double physicalMass = elementMass + (source.IncludeAdditionalMass ? additional.Take(3).Average() : 0);
        var center = physicalMass > 0 ? weightedCenter * (1 / physicalMass) : (Xyz?)null;
        var modes = ImmutableArray.CreateBuilder<ModalModeResult>();
        double stiffnessNorm = Frobenius(stiffness, free), massNorm = mass.Max();
        double eigenScale = Math.Max(1, eigenvalues.Select(Math.Abs).DefaultIfEmpty().Max());
        for (int mode = 0; mode < count; mode++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = new double[n];
            for (int i = 0; i < dynamic.Length; i++) full[dynamic[i]] = eigenvectors[i, mode];
            for (int i = 0; i < zero.Length; i++)
                for (int j = 0; j < dynamic.Length; j++) full[zero[i]] += recovery[i, j] * eigenvectors[j, mode];
            double lambda = eigenvalues[mode];
            var status = lambda < -options.EigenTolerance * eigenScale ? ModalEigenStatus.Negative
                : Math.Abs(lambda) <= options.EigenTolerance * eigenScale ? ModalEigenStatus.NearZero : ModalEigenStatus.Positive;
            double residual = Residual(stiffness, mass, full, lambda, free, stiffnessNorm, massNorm);
            if (!(residual <= options.ResidualTolerance))
                return Fail("modal.residual", ObjectKind.LoadCase, caseId,
                    $"Mode {mode + 1} scaled residual {residual:G6} exceeds {options.ResidualTolerance:G6}; no modal result is accepted.");
            var participation = ImmutableArray.CreateBuilder<ModalParticipation>();
            for (int direction = 0; direction < 3; direction++)
            {
                double denominator = freeMass[direction], numerator = 0;
                foreach (var dof in model.Dofs.Where(dof => dof.State == DofState.Free && (int)dof.Key.Component == direction))
                    numerator += full[dof.FullIndex] * mass[dof.FullIndex];
                if (!(denominator > 0)) participation.Add(new((DofComponent)direction, null, null, null, null));
                else
                {
                    double effective = numerator * numerator; // phi^T M phi = 1
                    double ratio = 100 * effective / denominator;
                    cumulative[direction] += ratio;
                    participation.Add(new((DofComponent)direction, numerator, effective, ratio, cumulative[direction]));
                }
            }
            var shape = model.Points.Select(point =>
            {
                double At(DofComponent component) => full[model.DofIndex[new(point.Id, component)].FullIndex];
                return new ModalJointAmplitude(point.Id, At(DofComponent.UX), At(DofComponent.UY), At(DofComponent.UZ),
                    At(DofComponent.RX), At(DofComponent.RY), At(DofComponent.RZ));
            }).ToImmutableArray();
            double? frequency = status == ModalEigenStatus.Positive ? Math.Sqrt(lambda) / (2 * Math.PI) : null;
            modes.Add(new(mode + 1, lambda, frequency, frequency is > 0 ? 1 / frequency : null, status,
                shape, participation.ToImmutable(), residual));
            Report(ModalProgressKind.ValidatingResults, mode + 1);
        }
        if (modes.Any(mode => mode.Status != ModalEigenStatus.Positive))
            issues.Add(new(ValidationSeverity.Error, "modal.stiffness.unstable", ObjectKind.LoadCase, caseId,
                $"Case '{loadCase.Name}' contains a zero/negative eigenvalue. It is an unstable or near-critical eigenproblem; frequency and period are not reported for that mode."));

        var inventory = new ModalMassInventory(elementMass, additional.ToImmutableArray(), freeMass.ToImmutableArray(), center);
        var metadata = new ModalSolverMetadata(eigenSolver is DenseSymmetricEigenSolver ? "Dense symmetric Jacobi after diagonal mass whitening" : eigenSolver.GetType().Name,
            DenseSymmetricEigenSolver.Version, dynamic.Length, zero.Length, options.EigenTolerance, options.ResidualTolerance);
        var result = new ModalAnalysisResult(model.DocumentId, caseId, model.InputHash, model.Revision, source.Id, null,
            "elastic:" + model.InputHash, metadata, modes.ToImmutable(), inventory, issues.ToImmutableArray());
        Report(issues.Any(issue => issue.Severity == ValidationSeverity.Error) ? ModalProgressKind.Failed : ModalProgressKind.Finished, modes.Count,
            issues.FirstOrDefault(issue => issue.Severity == ValidationSeverity.Error));
        return new(result, issues.ToImmutableArray());
    }

    private static double Frobenius(double[,] matrix, int[] indices)
    {
        double sum = 0;
        foreach (int i in indices) foreach (int j in indices) sum += matrix[i, j] * matrix[i, j];
        return Math.Sqrt(sum);
    }

    private static double Residual(double[,] stiffness, double[] mass, double[] vector, double eigenvalue,
        int[] free, double stiffnessNorm, double massNorm)
    {
        double numerator = 0, vectorNorm = 0;
        foreach (int i in free)
        {
            double value = -eigenvalue * mass[i] * vector[i];
            foreach (int j in free) value += stiffness[i, j] * vector[j];
            numerator += value * value;
            vectorNorm += vector[i] * vector[i];
        }
        double denominator = (stiffnessNorm + Math.Abs(eigenvalue) * massNorm) * Math.Sqrt(vectorNorm);
        return denominator > 0 ? Math.Sqrt(numerator) / denominator : double.PositiveInfinity;
    }
}
