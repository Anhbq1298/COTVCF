using System.Collections.Immutable;

namespace Mcotis.Core;

public enum LoadPatternType { Dead, Live, Prestress, Other }

public sealed record LoadPattern(ObjectId Id, string Name, LoadPatternType Type, double SelfWeightMultiplier = 0) : INamedObject;

public enum LoadDirection { GlobalX, GlobalY, GlobalZ, Local1, Local2, Local3, Gravity }

/// <summary>A load on one object in one load pattern. A load has an id but no name; it goes when its object goes.</summary>
public abstract record LoadAssignment(ObjectId Id, ObjectId PatternId, ObjectId TargetId);

/// <summary>Force [kN] and moment [kN·m] on a point, in the global axes.</summary>
public sealed record PointLoad(ObjectId Id, ObjectId PatternId, ObjectId TargetId, Xyz Force, Xyz Moment = default)
    : LoadAssignment(Id, PatternId, TargetId);

/// <summary>What a distributed frame load is per unit length: a force [kN/m] or a moment [kN·m/m] (SAP2000 MyType 1, 2).</summary>
public enum FrameLoadType { Force, Moment }

/// <summary>How the distances of a frame load were entered: relative to the frame length (0 … 1) or absolute [m] from
/// End-I. The relative distances are what the model holds either way.</summary>
public enum LoadDistanceType { Relative, Absolute }

/// <summary>A line load on a frame between two relative distances from its start, varying linearly: one segment of
/// SAP2000's frame distributed load (one row of its "Frame Loads - Distributed" table).
/// <para><see cref="Type"/>: a force [kN/m] along <see cref="LoadAssignment"/>'s direction, or a moment [kN·m/m] about
/// it. <see cref="Projected"/>: the value is per unit length of the frame projected on the plane normal to the load
/// direction (SAP2000 directions 7–9 and 11) — a global direction only. Zero outside its two distances.</para></summary>
public sealed record FrameDistributedLoad(ObjectId Id, ObjectId PatternId, ObjectId TargetId, LoadDirection Direction,
    double RelativeStart, double RelativeEnd, double ValueStart, double ValueEnd) : LoadAssignment(Id, PatternId, TargetId)
{
    public FrameLoadType Type { get; init; } = FrameLoadType.Force;
    public bool Projected { get; init; }
    public LoadDistanceType EnteredAs { get; init; } = LoadDistanceType.Relative;
}

/// <summary>A force [kN] on a frame at a relative distance from its start.</summary>
public sealed record FramePointLoad(ObjectId Id, ObjectId PatternId, ObjectId TargetId, LoadDirection Direction,
    double RelativeDistance, double Value) : LoadAssignment(Id, PatternId, TargetId);

/// <summary>The tension [kN] a cable has to reach, read at a relative distance from its start, in the load case that
/// fits target forces.</summary>
public sealed record CableTargetForce(ObjectId Id, ObjectId PatternId, ObjectId TargetId, double Force,
    double RelativeDistance = 0.5) : LoadAssignment(Id, PatternId, TargetId);

/// <summary>A uniform load [kN/m²] on a shell.</summary>
public sealed record ShellUniformLoad(ObjectId Id, ObjectId PatternId, ObjectId TargetId, LoadDirection Direction,
    double Value) : LoadAssignment(Id, PatternId, TargetId);

/// <summary>What a user defines a load case as (Define → Load Cases → Add New Case).
/// <para><see cref="InitialPDelta"/> is a named preset, not another solver: it is a nonlinear static case whose
/// geometric nonlinearity is P-Delta, meant to give later linear and modal cases the stiffness of its converged end.
/// What the solver does with a case is its <see cref="LoadCase.Type"/>.</para></summary>
public enum CaseKind { LinearStatic, NonlinearStatic, InitialPDelta, ModalEigen }

/// <summary>The method a case is solved by. Three, for the four kinds a user chooses from (<see cref="CaseKind"/>).</summary>
public enum LoadCaseType { LinearStatic, NonlinearStatic, ModalEigen }

/// <summary>Second-order effects of a nonlinear static case. Large displacements are not a choice until they are
/// formulated and verified.</summary>
public enum GeometricNonlinearity { None, PDelta }

/// <summary>Where a nonlinear static case starts: from nothing, or from the complete converged end of another
/// nonlinear case — its displacements, loads, member forces, cable pretension and active set — to which the loads of
/// the case are then added.</summary>
public enum InitialStateKind { Zero, EndOfNonlinearCase }

/// <summary>The stiffness a linear static or a modal case uses: that of the unstressed structure, or the tangent
/// stiffness at the converged end of a nonlinear case, frozen. The loads of that case are not carried over.</summary>
public enum StiffnessSourceKind { Unstressed, EndOfNonlinearCase }

public enum ModalMassMatrixKind { Lumped }

public readonly record struct InitialState(InitialStateKind Kind, ObjectId? SourceCaseId);

public readonly record struct StiffnessSource(StiffnessSourceKind Kind, ObjectId? SourceCaseId);

/// <summary>How one case depends on something else of the model. The kinds are kept apart because they mean
/// different things: a state that is continued, a stiffness that is only read, a mass that is only read.</summary>
public enum CaseDependencyKind { ContinueState, FrozenStiffness, MassSource }

public readonly record struct AnalysisCaseDependency(CaseDependencyKind Kind, ObjectId SourceId);

/// <summary>The iteration of one nonlinear static case, when it is not left to the solver: load steps, iterations per
/// step, and the relative residual and displacement-correction tolerances.</summary>
public sealed record StaticSolveControls(int MaxLoadSteps, int MaxIterationsPerStep,
    double RelativeResidualTolerance, double RelativeDisplacementTolerance);

/// <summary>What a modal (eigenvector) case needs beyond a stiffness: the mass, and how many modes.</summary>
public sealed record ModalEigenOptions(ObjectId MassSourceId, int RequestedModes,
    ModalMassMatrixKind MassMatrixKind = ModalMassMatrixKind.Lumped);

/// <summary>The mass a modal case reads. A named object of its own, so that several cases can share one.</summary>
/// <param name="IncludeSelfMass">The mass of the frames, cables and shells from their materials and sections.</param>
public sealed record MassSource(ObjectId Id, string Name, bool IncludeSelfMass = true,
    bool IncludeAdditionalMass = true) : INamedObject;

/// <summary>Additional inertia assigned directly to one joint. Translational entries are [t]; rotational entries
/// are [t·m²]. The six entries follow UX, UY, UZ, RX, RY, RZ and are physical properties, not restraints.</summary>
public sealed record JointMassAssignment(ObjectId PointId, double UX, double UY, double UZ,
    double RX = 0, double RY = 0, double RZ = 0)
{
    public ImmutableArray<double> Values => [UX, UY, UZ, RX, RY, RZ];
}

public readonly record struct LoadCaseComponent(ObjectId PatternId, double Factor);

/// <summary>A load case of one of four kinds (<see cref="CaseKind"/>).
/// <para>Two references to another case exist, and they are not the same thing. A nonlinear case
/// <b>continues from</b> one (<see cref="InitialCaseId"/>): it resumes that case's whole converged state and adds its
/// own loads to it. A linear static or a modal case <b>uses the stiffness of</b> one
/// (<see cref="StiffnessSourceCaseId"/>): it reads the tangent stiffness at that case's converged end and nothing of
/// its loads. Only a nonlinear static or an initial P-Delta case can be referred to either way
/// (<see cref="ProducesState"/>).</para></summary>
public sealed record LoadCase(ObjectId Id, string Name, CaseKind Kind, ImmutableArray<LoadCaseComponent> Components) : INamedObject
{
    private readonly GeometricNonlinearity? geometricMode;

    /// <summary>A linear or nonlinear static case, by the method it is solved with.</summary>
    public LoadCase(ObjectId Id, string Name, LoadCaseType Type, ImmutableArray<LoadCaseComponent> Components)
        : this(Id, Name, Type switch
        {
            LoadCaseType.LinearStatic => CaseKind.LinearStatic,
            LoadCaseType.NonlinearStatic => CaseKind.NonlinearStatic,
            LoadCaseType.ModalEigen => CaseKind.ModalEigen,
            _ => throw new ArgumentOutOfRangeException(nameof(Type), Type, "Unknown load case type."),
        }, Components) { }

    public ImmutableArray<LoadCaseComponent> Components { get; init; } = Components.IsDefault ? [] : Components;
    /// <summary>The method the case is solved by. An initial P-Delta case is a nonlinear static one.</summary>
    public LoadCaseType Type => Kind switch
    {
        CaseKind.LinearStatic => LoadCaseType.LinearStatic,
        CaseKind.ModalEigen => LoadCaseType.ModalEigen,
        _ => LoadCaseType.NonlinearStatic,
    };
    /// <summary>A nonlinear case: the nonlinear case whose converged end it continues from. Null: it starts from zero.</summary>
    public ObjectId? InitialCaseId { get; init; }
    /// <summary>A linear static or modal case: the nonlinear case whose end stiffness it uses. Null: the unstressed stiffness.</summary>
    public ObjectId? StiffnessSourceCaseId { get; init; }
    /// <summary>None unless set; an initial P-Delta case is P-Delta unless set otherwise, which the document refuses.</summary>
    public GeometricNonlinearity GeometricMode
    {
        get => geometricMode ?? (Kind == CaseKind.InitialPDelta ? GeometricNonlinearity.PDelta : GeometricNonlinearity.None);
        init => geometricMode = value;
    }
    /// <summary>Null: the solver's own iteration.</summary>
    public StaticSolveControls? NonlinearControls { get; init; }
    /// <summary>A modal case only, which needs it.</summary>
    public ModalEigenOptions? ModalOptions { get; init; }
    public bool Run { get; init; } = true;
    /// <summary>This case adjusts the cables until their target forces are met.</summary>
    public bool FitCableTargetForces { get; init; }

    public InitialState InitialState => new(InitialCaseId is null ? InitialStateKind.Zero : InitialStateKind.EndOfNonlinearCase, InitialCaseId);
    public StiffnessSource StiffnessSource =>
        new(StiffnessSourceCaseId is null ? StiffnessSourceKind.Unstressed : StiffnessSourceKind.EndOfNonlinearCase, StiffnessSourceCaseId);
    /// <summary>Its converged end can be continued from, and its end stiffness can be used: a nonlinear static or an
    /// initial P-Delta case. A linear static or a modal case ends in no such state.</summary>
    public bool ProducesState => Kind is CaseKind.NonlinearStatic or CaseKind.InitialPDelta;

    /// <summary>What the case needs of the model before it can be solved, each by its kind.</summary>
    public IEnumerable<AnalysisCaseDependency> Dependencies
    {
        get
        {
            if (InitialCaseId is { } initial) yield return new(CaseDependencyKind.ContinueState, initial);
            if (StiffnessSourceCaseId is { } stiffness) yield return new(CaseDependencyKind.FrozenStiffness, stiffness);
            if (ModalOptions is { } modal) yield return new(CaseDependencyKind.MassSource, modal.MassSourceId);
        }
    }

    public bool Equals(LoadCase? other) => other is not null && Id == other.Id && Name == other.Name && Kind == other.Kind &&
        InitialCaseId == other.InitialCaseId && StiffnessSourceCaseId == other.StiffnessSourceCaseId && GeometricMode == other.GeometricMode &&
        NonlinearControls == other.NonlinearControls && ModalOptions == other.ModalOptions &&
        Run == other.Run && FitCableTargetForces == other.FitCableTargetForces && Components.SequenceEqual(other.Components);
    public override int GetHashCode() => HashCode.Combine(Id, Name, Kind, InitialCaseId, StiffnessSourceCaseId, Components.Length);
}

public enum CombinationType { LinearAdd, Envelope }

/// <summary>One term of a combination: a load case or another combination, with its factor.</summary>
public readonly record struct CombinationTerm(ObjectId SourceId, double Factor);

public sealed record LoadCombination(ObjectId Id, string Name, CombinationType Type, ImmutableArray<CombinationTerm> Terms) : INamedObject
{
    public ImmutableArray<CombinationTerm> Terms { get; init; } = Terms.IsDefault ? [] : Terms;
    public bool Run { get; init; } = true;

    public bool Equals(LoadCombination? other) => other is not null && Id == other.Id && Name == other.Name &&
        Type == other.Type && Run == other.Run && Terms.SequenceEqual(other.Terms);
    public override int GetHashCode() => HashCode.Combine(Id, Name, Type, Terms.Length);
}
