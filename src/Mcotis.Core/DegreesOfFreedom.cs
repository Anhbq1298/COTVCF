using System.Collections.Immutable;

namespace Mcotis.Core;

/// <summary>Canonical joint order. These values are component indices, not flags.</summary>
public enum DofComponent { UX, UY, UZ, RX, RY, RZ }

[Flags]
public enum DofMask
{
    None = 0,
    UX = 1, UY = 2, UZ = 4, RX = 8, RY = 16, RZ = 32,
    Translations = UX | UY | UZ,
    Full3D = Translations | RX | RY | RZ,
    XZ = UX | UZ | RY,
    YZ = UY | UZ | RX,
    XY = UX | UY | RZ,
}

/// <summary>Model-wide analysis assumptions; independent of supports and camera projection.</summary>
public static class DegreesOfFreedom
{
    public static ImmutableArray<DofComponent> Components { get; } =
        [DofComponent.UX, DofComponent.UY, DofComponent.UZ, DofComponent.RX, DofComponent.RY, DofComponent.RZ];

    public static bool IsValid(DofMask mask) => mask != DofMask.None && (mask & ~DofMask.Full3D) == 0;

    public static DofMask Mask(this DofComponent component) => Enum.IsDefined(component)
        ? (DofMask)(1 << (int)component)
        : throw new ArgumentOutOfRangeException(nameof(component));

    public static bool Contains(this DofMask mask, DofComponent component) => (mask & component.Mask()) != 0;

    public static bool Restrains(this Restraint restraint, DofComponent component) => component switch
    {
        DofComponent.UX => restraint.Ux, DofComponent.UY => restraint.Uy, DofComponent.UZ => restraint.Uz,
        DofComponent.RX => restraint.Rx, DofComponent.RY => restraint.Ry, DofComponent.RZ => restraint.Rz,
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };
}
