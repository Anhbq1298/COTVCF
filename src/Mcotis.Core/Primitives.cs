namespace Mcotis.Core;

/// <summary>Immutable identity of anything a document holds. It survives Save/Open and Rename, and it is what objects
/// use to refer to each other — never a name.</summary>
public readonly record struct ObjectId(Guid Value) : IComparable<ObjectId>
{
    public static ObjectId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public int CompareTo(ObjectId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("D");
    public static ObjectId Parse(string text) => new(Guid.Parse(text));
}

public enum ObjectKind
{
    Point, Frame, Cable, Shell, Material, FrameSection, CableSection, ShellSection, LoadPattern, LoadCase, LoadCombination, Load, Settings,
    MassSource,
}

/// <summary>Every number a document holds is in these units. Conversion belongs to the edges (UI, import), never to the model.</summary>
public static class BaseUnits
{
    public const string Force = "kN";
    public const string Length = "m";
    public const string Mass = "t";
}

public static class Tolerances
{
    /// <summary>Two positions closer than this [m] are one position.</summary>
    public const double Length = 1e-6;
    /// <summary>A shell whose area [m²] is below this has no area.</summary>
    public const double Area = 1e-9;
    /// <summary>A shell vertex further than this [m] from the shell's mean plane makes the shell warped.</summary>
    public const double Planarity = 1e-3;
}

/// <summary>A position or a vector in the global X, Y, Z axes.</summary>
public readonly record struct Xyz(double X, double Y, double Z)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public double Length => Math.Sqrt(Dot(this));
    public double Dot(Xyz other) => X * other.X + Y * other.Y + Z * other.Z;
    public Xyz Cross(Xyz other) => new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);
    public double DistanceTo(Xyz other) => (this - other).Length;
    public static Xyz operator +(Xyz a, Xyz b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Xyz operator -(Xyz a, Xyz b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Xyz operator *(Xyz a, double factor) => new(a.X * factor, a.Y * factor, a.Z * factor);
}
