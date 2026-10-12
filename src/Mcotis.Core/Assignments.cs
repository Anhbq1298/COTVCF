namespace Mcotis.Core;

/// <summary>The restrained degrees of freedom of a point (true = restrained).</summary>
public readonly record struct Restraint(bool Ux, bool Uy, bool Uz, bool Rx, bool Ry, bool Rz)
{
    public static readonly Restraint Free = default;
    public static readonly Restraint Pinned = new(true, true, true, false, false, false);
    public static readonly Restraint Fixed = new(true, true, true, true, true, true);
    public bool IsFree => this == Free;
}

/// <summary>The released degrees of freedom at one end of a frame (true = released).</summary>
public readonly record struct EndRelease(bool Axial, bool Shear2, bool Shear3, bool Torsion, bool Moment2, bool Moment3)
{
    public static readonly EndRelease None = default;
}

public readonly record struct EndReleases(EndRelease Start, EndRelease End)
{
    public static readonly EndReleases None = default;
}

/// <summary>Multipliers on a frame's section properties (1 = unchanged).</summary>
public readonly record struct FrameModifiers(double Area, double Inertia33, double Inertia22, double Torsion, double Mass, double Weight)
{
    public static readonly FrameModifiers Identity = new(1, 1, 1, 1, 1, 1);
}

/// <summary>What is assigned to one frame. Kept apart from the frame's geometry; a frame without an entry has
/// <see cref="Default"/>. <paramref name="LocalAxisAngle"/> is in degrees about local 1.</summary>
public sealed record FrameAssignment(ObjectId? SectionId, EndReleases Releases, double LocalAxisAngle, FrameModifiers Modifiers)
{
    public static FrameAssignment Default { get; } = new(null, EndReleases.None, 0, FrameModifiers.Identity);
}

/// <summary>Multipliers on a cable's section properties (1 = unchanged).</summary>
public readonly record struct CableModifiers(double Area, double Mass, double Weight)
{
    public static readonly CableModifiers Identity = new(1, 1, 1);
}

/// <summary>What is assigned to one cable. A cable without an entry has <see cref="Default"/>.</summary>
public sealed record CableAssignment(ObjectId? SectionId, CableModifiers Modifiers)
{
    public static CableAssignment Default { get; } = new(null, CableModifiers.Identity);
}

/// <summary>What is assigned to one shell. <paramref name="LocalAxisAngle"/> is in degrees about the shell normal.</summary>
public sealed record ShellAssignment(ObjectId? SectionId, double LocalAxisAngle)
{
    public static ShellAssignment Default { get; } = new(null, 0);
}
