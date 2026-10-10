namespace COT_VCF.Domain;

/// <summary>SAP2000 material types (eMatType), plus <see cref="Other"/> for a material whose type is not known.</summary>
public enum MaterialKind { Steel, Concrete, NoDesign, Aluminum, ColdFormed, Rebar, Tendon, Masonry, Other }

/// <summary>
/// A material as Define → Materials shows it (SAP2000's Material Property Data), in kN, m, C. Uniaxial materials (tendon,
/// rebar) have no Poisson's ratio or shear modulus. Type-specific strengths are null where they do not apply.
/// <see cref="Source"/> says where the data came from: SAP2000, the model's member properties, or the app.
/// </summary>
public sealed record MaterialProperty
{
    public required string Name { get; init; }
    public MaterialKind Kind { get; init; } = MaterialKind.Other;
    public string Grade { get; init; } = "";
    public string Notes { get; init; } = "";
    /// <summary>Display colour as 0xRRGGBB.</summary>
    public int Color { get; init; } = 0x808080;
    public double WeightPerVolume { get; init; }          // kN/m³
    public double ElasticModulus { get; init; }           // kN/m²
    public double PoissonRatio { get; init; }
    public double ThermalCoefficient { get; init; }       // 1/C
    public double? Fc { get; init; }                      // concrete, kN/m²
    public double? ExpectedFc { get; init; }
    public bool IsLightweight { get; init; }
    public double? ShearStrengthReductionFactor { get; init; }
    public double? Fy { get; init; }                      // steel, rebar, tendon, cold formed, kN/m²
    public double? Fu { get; init; }
    public double? ExpectedFy { get; init; }
    public double? ExpectedFu { get; init; }
    public string Source { get; init; } = "App";

    public const double Gravity = 9.80665;
    public bool IsUniaxial => Kind is MaterialKind.Tendon or MaterialKind.Rebar;
    /// <summary>Mass per volume [kN·s²/m⁴ = t/m³] = weight / g, as SAP2000 derives it.</summary>
    public double MassPerVolume => WeightPerVolume / Gravity;
    /// <summary>Shear modulus G = E / 2(1 + ν) for an isotropic material; null for a uniaxial one.</summary>
    public double? ShearModulus => IsUniaxial ? null : ElasticModulus / (2 * (1 + PoissonRatio));

    public static string Title(MaterialKind kind) => kind switch
    {
        MaterialKind.NoDesign => "No Design",
        MaterialKind.ColdFormed => "Cold Formed",
        _ => kind.ToString(),
    };

    /// <summary>A new material of <paramref name="kind"/> with typical values (EN grades), for Add New Material.</summary>
    public static MaterialProperty Default(MaterialKind kind, string name) => kind switch
    {
        MaterialKind.Concrete => new() { Name = name, Kind = kind, Grade = "C30/37", Color = 0xFF0000, WeightPerVolume = 24.9926, ElasticModulus = 33_000_000,
            PoissonRatio = 0.2, ThermalCoefficient = 1e-5, Fc = 30_000, ExpectedFc = 30_000 },
        MaterialKind.Steel => new() { Name = name, Kind = kind, Grade = "S355", Color = 0xFFFF00, WeightPerVolume = 76.9729, ElasticModulus = 210_000_000,
            PoissonRatio = 0.3, ThermalCoefficient = 1.17e-5, Fy = 355_000, Fu = 510_000, ExpectedFy = 390_500, ExpectedFu = 561_000 },
        MaterialKind.Tendon => new() { Name = name, Kind = kind, Color = 0x00FFFF, WeightPerVolume = 76.9729, ElasticModulus = 196_500_600,
            ThermalCoefficient = 1.17e-5, Fy = 1_689_905.159, Fu = 1_861_584.63 },
        MaterialKind.Rebar => new() { Name = name, Kind = kind, Color = 0x00FF00, WeightPerVolume = 76.9729, ElasticModulus = 200_000_000,
            ThermalCoefficient = 1.17e-5, Fy = 500_000, Fu = 550_000, ExpectedFy = 550_000, ExpectedFu = 605_000 },
        MaterialKind.ColdFormed => new() { Name = name, Kind = kind, Color = 0xFFA500, WeightPerVolume = 76.9729, ElasticModulus = 203_395_000,
            PoissonRatio = 0.3, ThermalCoefficient = 1.17e-5, Fy = 345_000, Fu = 450_000 },
        MaterialKind.Aluminum => new() { Name = name, Kind = kind, Color = 0xC0C0C0, WeightPerVolume = 26.6018, ElasticModulus = 69_637_000,
            PoissonRatio = 0.33, ThermalCoefficient = 2.36e-5 },
        _ => new() { Name = name, Kind = kind, Color = 0x808080, WeightPerVolume = 0, ElasticModulus = 200_000_000, PoissonRatio = 0.3, ThermalCoefficient = 1.17e-5 },
    };
}
