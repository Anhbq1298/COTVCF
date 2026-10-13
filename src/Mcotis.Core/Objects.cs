using System.Collections.Immutable;

namespace Mcotis.Core;

/// <summary>Anything with a user-facing name. The name is unique within its <see cref="NameScope"/>; the id is what refers to it.</summary>
public interface INamedObject
{
    ObjectId Id { get; }
    string Name { get; }
}

public enum OriginKind { User, Generated, Imported }

/// <summary>Where an object came from. <paramref name="GenerationKey"/> is the place of a generated object in its
/// generator (role, side, bay …): regenerating matches on it so the object keeps its id and assignments. The source
/// fields keep the name an imported object had in the program it came from. Never part of the analysis input.</summary>
public sealed record ObjectOrigin(OriginKind Kind, string? GenerationKey = null, string? SourceProduct = null,
    string? SourceType = null, string? SourceName = null);

public sealed record PointObject(ObjectId Id, string Name, Xyz Position) : INamedObject
{
    public ObjectOrigin? Origin { get; init; }
}

/// <summary>A frame object between two points: it carries axial force, shear and bending.</summary>
public sealed record FrameObject(ObjectId Id, string Name, ObjectId StartPointId, ObjectId EndPointId) : INamedObject
{
    public ObjectOrigin? Origin { get; init; }
}

/// <summary>A cable object between two points: tension only. An object type of its own — not a kind of frame — with
/// its own sections (<see cref="CableSectionDefinition"/>), assignments (<see cref="CableAssignment"/>) and loads
/// (<see cref="CableTargetForce"/>).</summary>
public sealed record CableObject(ObjectId Id, string Name, ObjectId StartPointId, ObjectId EndPointId) : INamedObject
{
    public ObjectOrigin? Origin { get; init; }
}

/// <summary>An area object: its points in order around the boundary.</summary>
public sealed record ShellObject(ObjectId Id, string Name, ImmutableArray<ObjectId> PointIds) : INamedObject
{
    public ImmutableArray<ObjectId> PointIds { get; init; } = PointIds.IsDefault ? [] : PointIds;
    public ObjectOrigin? Origin { get; init; }

    public bool Equals(ShellObject? other) => other is not null && Id == other.Id && Name == other.Name &&
        Equals(Origin, other.Origin) && PointIds.SequenceEqual(other.PointIds);
    public override int GetHashCode() => HashCode.Combine(Id, Name, PointIds.Length);
}
