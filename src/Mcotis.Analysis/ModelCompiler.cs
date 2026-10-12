using System.Collections.Immutable;
using Mcotis.Core;

namespace Mcotis.Analysis;

public enum DofState { Free, Restrained, Inactive }
public enum InactiveDofReason { None, AnalysisMask, ElementUnused }
public readonly record struct DofKey(ObjectId PointId, DofComponent Component);

/// <summary>FullIndex retains all six components for loads/reactions. ReducedIndex exists only for a free DOF.</summary>
public sealed record CompiledDof(DofKey Key, int FullIndex, int? ReducedIndex, DofState State, InactiveDofReason InactiveReason);

public sealed record CompiledPoint(ObjectId Id, Xyz Position, DofMask ElementDofs);
public sealed record CompiledMember(ObjectId ParentObjectId, ObjectKind Kind, ObjectId StartPointId, ObjectId EndPointId,
    DofMask SupportedDofs);

/// <summary>Immutable topology/DOF snapshot. Not a stiffness assembly or a declaration that a numerical solver exists.</summary>
public sealed class CompiledModel
{
    internal CompiledModel(ModelDocument input, long revision, ImmutableArray<CompiledPoint> points,
        ImmutableArray<CompiledMember> members, ImmutableArray<CompiledDof> dofs,
        ImmutableDictionary<ObjectId, ImmutableArray<double>> pointLoads)
    {
        Input = input;
        Revision = revision;
        Points = points;
        Members = members;
        Dofs = dofs;
        DofIndex = dofs.ToImmutableDictionary(dof => dof.Key);
        PointLoadsByPattern = pointLoads;
    }

    public const string CompilerVersion = "topology-dofs-1";
    public ModelDocument Input { get; }
    public ObjectId DocumentId => Input.DocumentId;
    public string InputHash => Input.AnalysisInputHash;
    public long Revision { get; }
    public DofMask ActiveDofs => Input.Settings.ActiveDofs;
    public ImmutableArray<CompiledPoint> Points { get; }
    public ImmutableArray<CompiledMember> Members { get; }
    public ImmutableArray<CompiledDof> Dofs { get; }
    public ImmutableDictionary<DofKey, CompiledDof> DofIndex { get; }
    public int FreeDofCount => Dofs.Count(dof => dof.State == DofState.Free);
    /// <summary>Full six-component nodal force vectors, including support loads. Member equivalent loads are not assembled yet.</summary>
    public ImmutableDictionary<ObjectId, ImmutableArray<double>> PointLoadsByPattern { get; }
}

public sealed record CompilationResult(CompiledModel? Model, ImmutableArray<ValidationIssue> Issues)
{
    public bool Success => Model is not null;
}

/// <summary>Reads only the native snapshot. Validates DOF compatibility without modifying/projecting the model,
/// guessing supports, meshing, or evaluating an unverified element kernel.</summary>
public static class ModelCompiler
{
    public static CompilationResult Compile(ModelDocument document, long revision = 0)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        var issues = DocumentValidator.Validate(document).ToList();
        var mask = document.Settings.ActiveDofs;
        var used = document.Points.Keys.ToDictionary(id => id, _ => DofMask.None);
        var members = ImmutableArray.CreateBuilder<CompiledMember>();
        void Issue(ValidationSeverity severity, string code, ObjectKind kind, ObjectId id, string message) =>
            issues.Add(new(severity, code, kind, id, message));
        void Error(string code, ObjectKind kind, ObjectId id, string message) => Issue(ValidationSeverity.Error, code, kind, id, message);

        if (document.Points.IsEmpty)
            Error("analysis.empty", ObjectKind.Settings, default, "Add connected points and members before compiling an analysis model.");
        if ((mask & DofMask.Translations) == 0)
            Error("dof.translations", ObjectKind.Settings, default, "The frame/cable topology compiler requires at least one active translation.");

        foreach (var frame in document.Frames.Values.OrderBy(item => item.Id))
        {
            members.Add(new(frame.Id, ObjectKind.Frame, frame.StartPointId, frame.EndPointId, DofMask.Full3D));
            used[frame.StartPointId] |= DofMask.Full3D;
            used[frame.EndPointId] |= DofMask.Full3D;
            if (document.AssignmentOfFrame(frame.Id).SectionId is null)
                Error("analysis.section", ObjectKind.Frame, frame.Id, $"Frame '{frame.Name}' needs a section for analysis.");
        }
        foreach (var cable in document.Cables.Values.OrderBy(item => item.Id))
        {
            members.Add(new(cable.Id, ObjectKind.Cable, cable.StartPointId, cable.EndPointId, DofMask.Translations));
            used[cable.StartPointId] |= DofMask.Translations;
            used[cable.EndPointId] |= DofMask.Translations;
            if (document.AssignmentOfCable(cable.Id).SectionId is null)
                Error("analysis.section", ObjectKind.Cable, cable.Id, $"Cable '{cable.Name}' needs a cable section for analysis.");
        }
        foreach (var shell in document.Shells.Values.OrderBy(item => item.Id))
            Error("analysis.shell.unsupported", ObjectKind.Shell, shell.Id,
                $"Shell '{shell.Name}' is stored in the document, but this frame/cable compiler has no verified shell kernel.");

        var points = document.Points.Values.OrderBy(item => item.Id).ToArray();
        // A reduced translation mask is a physical dimensional assumption. Coordinates stay XYZ and are never projected.
        foreach (var component in DegreesOfFreedom.Components.Take(3).Where(component => !mask.Contains(component)))
        {
            double Coordinate(Xyz xyz) => component switch { DofComponent.UX => xyz.X, DofComponent.UY => xyz.Y, _ => xyz.Z };
            if (points.Length > 0)
            {
                var min = points.Min(point => Coordinate(point.Position));
                foreach (var point in points.Where(point => Coordinate(point.Position) - min > Tolerances.Length))
                    Error("dof.geometry", ObjectKind.Point, point.Id,
                        $"Point '{point.Name}' lies outside the common plane/line required when {component} is inactive (tolerance {Tolerances.Length} m).");
            }
        }

        var dofs = ImmutableArray.CreateBuilder<CompiledDof>();
        int free = 0;
        foreach (var point in points)
        {
            if (used[point.Id] == DofMask.None)
                Error("analysis.disconnected.point", ObjectKind.Point, point.Id, $"Point '{point.Name}' is not connected to a supported frame or cable.");
            var restraint = document.RestraintOf(point.Id);
            foreach (var component in DegreesOfFreedom.Components)
            {
                var reason = !mask.Contains(component) ? InactiveDofReason.AnalysisMask
                    : !used[point.Id].Contains(component) ? InactiveDofReason.ElementUnused : InactiveDofReason.None;
                var state = reason != InactiveDofReason.None ? DofState.Inactive
                    : restraint.Restrains(component) ? DofState.Restrained : DofState.Free;
                dofs.Add(new(new(point.Id, component), dofs.Count, state == DofState.Free ? free++ : null, state, reason));
                if (reason != InactiveDofReason.None && restraint.Restrains(component))
                    Issue(ValidationSeverity.Warning, "dof.restraint.inactive", ObjectKind.Point, point.Id,
                        $"The {component} restraint on '{point.Name}' is retained in the document but inactive in this analysis ({reason}).");
            }
        }
        var index = dofs.ToImmutableDictionary(dof => dof.Key);
        foreach (var load in document.Loads.Values.OrderBy(item => item.Id))
        {
            if (load is PointLoad pointLoad)
            {
                double[] values = [pointLoad.Force.X, pointLoad.Force.Y, pointLoad.Force.Z, pointLoad.Moment.X, pointLoad.Moment.Y, pointLoad.Moment.Z];
                foreach (var component in DegreesOfFreedom.Components)
                    if (values[(int)component] != 0 && index[new(pointLoad.TargetId, component)].State == DofState.Inactive)
                        Error("dof.load.inactive", ObjectKind.Load, load.Id,
                            $"Load {load.Id} on '{document.Points[load.TargetId].Name}' has a nonzero {component} component on an inactive DOF; choose compatible DOFs or edit the load.");
                continue;
            }
            var (direction, nonzero) = load switch
            {
                FrameDistributedLoad line => (line.Direction, line.ValueStart != 0 || line.ValueEnd != 0),
                FramePointLoad point => (point.Direction, point.Value != 0),
                _ => (LoadDirection.GlobalX, false), // Cable target force is axial, not a global nodal component.
            };
            if (!nonzero) continue;
            DofComponent? loadDof = direction switch
            {
                LoadDirection.GlobalX => DofComponent.UX, LoadDirection.GlobalY => DofComponent.UY,
                LoadDirection.GlobalZ or LoadDirection.Gravity => DofComponent.UZ, _ => null,
            };
            if (loadDof is { } global && !mask.Contains(global))
                Error("dof.load.inactive", ObjectKind.Load, load.Id, $"Member load {load.Id} acts in inactive {global}; it cannot be silently discarded.");
            if (loadDof is null && mask != DofMask.Full3D)
                Error("dof.load.local.unverified", ObjectKind.Load, load.Id,
                    $"Local member load {load.Id} requires a verified local-axis transformation before compatibility with reduced DOFs can be established.");
        }
        if (!mask.Contains(DofComponent.UZ))
            foreach (var pattern in document.LoadPatterns.Values.Where(pattern => pattern.SelfWeightMultiplier != 0).OrderBy(item => item.Id))
                Error("dof.selfweight.inactive", ObjectKind.LoadPattern, pattern.Id,
                    $"Pattern '{pattern.Name}' has global -Z self-weight while UZ is inactive.");

        if (issues.Any(issue => issue.Severity == ValidationSeverity.Error)) return new(null, issues.ToImmutableArray());

        var loads = document.LoadPatterns.Keys.ToDictionary(id => id, _ => new double[dofs.Count]);
        foreach (var pointLoad in document.Loads.Values.OfType<PointLoad>().OrderBy(load => load.Id))
        {
            double[] values = [pointLoad.Force.X, pointLoad.Force.Y, pointLoad.Force.Z, pointLoad.Moment.X, pointLoad.Moment.Y, pointLoad.Moment.Z];
            foreach (var component in DegreesOfFreedom.Components)
            {
                var fullIndex = index[new(pointLoad.TargetId, component)].FullIndex;
                loads[pointLoad.PatternId][fullIndex] += values[(int)component];
                if (!double.IsFinite(loads[pointLoad.PatternId][fullIndex]))
                    Error("analysis.load.overflow", ObjectKind.Load, pointLoad.Id, "Summing point loads overflowed; review the load magnitudes.");
            }
        }
        if (issues.Any(issue => issue.Severity == ValidationSeverity.Error)) return new(null, issues.ToImmutableArray());
        return new(new(document, revision, points.Select(point => new CompiledPoint(point.Id, point.Position, used[point.Id])).ToImmutableArray(),
            members.ToImmutable(), dofs.ToImmutable(), loads.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray())), issues.ToImmutableArray());
    }
}
