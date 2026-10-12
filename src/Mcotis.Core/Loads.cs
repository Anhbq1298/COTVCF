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

/// <summary>A line load [kN/m] on a frame between two relative distances from its start, varying linearly.</summary>
public sealed record FrameDistributedLoad(ObjectId Id, ObjectId PatternId, ObjectId TargetId, LoadDirection Direction,
    double RelativeStart, double RelativeEnd, double ValueStart, double ValueEnd) : LoadAssignment(Id, PatternId, TargetId);

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

public enum LoadCaseType { LinearStatic, NonlinearStatic }

public readonly record struct LoadCaseComponent(ObjectId PatternId, double Factor);

/// <summary>A load case: load patterns with factors, optionally continuing from the end of another case.</summary>
public sealed record LoadCase(ObjectId Id, string Name, LoadCaseType Type, ImmutableArray<LoadCaseComponent> Components) : INamedObject
{
    public ImmutableArray<LoadCaseComponent> Components { get; init; } = Components.IsDefault ? [] : Components;
    public ObjectId? InitialCaseId { get; init; }
    public bool Run { get; init; } = true;
    /// <summary>This case adjusts the cables until their target forces are met.</summary>
    public bool FitCableTargetForces { get; init; }

    public bool Equals(LoadCase? other) => other is not null && Id == other.Id && Name == other.Name && Type == other.Type &&
        InitialCaseId == other.InitialCaseId && Run == other.Run && FitCableTargetForces == other.FitCableTargetForces &&
        Components.SequenceEqual(other.Components);
    public override int GetHashCode() => HashCode.Combine(Id, Name, Type, InitialCaseId, Components.Length);
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
