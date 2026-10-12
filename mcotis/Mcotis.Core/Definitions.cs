using System.Collections.Immutable;

namespace Mcotis.Core;

public enum MaterialType { Steel, Concrete, Rebar, Tendon, Other }

/// <summary>Elastic modulus [kN/m²], weight density [kN/m³], mass density [t/m³].</summary>
public sealed record MaterialDefinition(ObjectId Id, string Name, MaterialType Type, double ElasticModulus,
    double PoissonRatio, double WeightDensity, double MassDensity) : INamedObject;

public enum FrameSectionShape { General, Rectangle, Box, ISection, Pipe, Circle, Channel, Tee, Angle }

/// <summary>What the analysis uses of a frame section: area [m²], inertias and torsion constant [m⁴], shear areas [m²].</summary>
public readonly record struct FrameSectionProperties(double Area, double Inertia33, double Inertia22,
    double TorsionConstant = 0, double ShearArea2 = 0, double ShearArea3 = 0);

/// <summary>A frame section. <see cref="Dimensions"/> [m] describe the shape by name (t3, t2, tf …);
/// <see cref="Properties"/> are the values the analysis reads.</summary>
public sealed record FrameSectionDefinition(ObjectId Id, string Name, ObjectId MaterialId, FrameSectionShape Shape,
    FrameSectionProperties Properties) : INamedObject
{
    public static readonly ImmutableSortedDictionary<string, double> NoDimensions =
        ImmutableSortedDictionary.Create<string, double>(StringComparer.Ordinal);

    public ImmutableSortedDictionary<string, double> Dimensions { get; init; } = NoDimensions;

    public bool Equals(FrameSectionDefinition? other) => other is not null && Id == other.Id && Name == other.Name &&
        MaterialId == other.MaterialId && Shape == other.Shape && Properties == other.Properties &&
        Dimensions.SequenceEqual(other.Dimensions);
    public override int GetHashCode() => HashCode.Combine(Id, Name, MaterialId, Shape, Properties);
}

/// <summary>A cable section: its material and its steel area [m²].</summary>
public sealed record CableSectionDefinition(ObjectId Id, string Name, ObjectId MaterialId, double Area) : INamedObject;

public enum ShellSectionType { Shell, Membrane, Plate }

/// <summary>Thickness [m].</summary>
public sealed record ShellSectionDefinition(ObjectId Id, string Name, ObjectId MaterialId, ShellSectionType Type,
    double Thickness) : INamedObject;

/// <summary>Settings of the analysis run that belong to the model, not to a view of it.</summary>
public sealed record AnalysisSettings(double CableFitTolerancePercent = 10, int CableFitMaxIterations = 100,
    int FrameMeshSegments = 10, bool CableSag = true)
{
    public static AnalysisSettings Default { get; } = new();
}
