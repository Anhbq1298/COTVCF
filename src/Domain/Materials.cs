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
    /// <summary>Unit cost [SGD per tonne]; null when not set. Concrete is also shown per m³ (× mass per volume).</summary>
    public double? UnitCost { get; init; }
    /// <summary>Where <see cref="UnitCost"/> comes from (rate table, published average, estimate, quote).</summary>
    public string CostNote { get; init; } = "";

    public const double Gravity = 9.80665;
    /// <summary>Unit cost per m³ [SGD/m³] = cost per tonne × mass per volume.</summary>
    public double? UnitCostPerCubicMetre => UnitCost * MassPerVolume;
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

/// <summary>
/// Default unit costs (SGD) for Define → Materials, by material type and grade. Concrete rates are per m³ and are converted
/// to per tonne with the material's own mass per volume; steel, rebar and strand rates are per tonne.
/// Project rates: C80/95 240 /m³; 100 / 120 / 150 MPa 320 / 450 / 1,200 /m³; HD jumbo S460M 4,860 /t; built-up S690QL 7,500 /t.
/// Published: BCA market prices 2025 — ready-mixed concrete Grade 40 ≈ 121 /m³ and rebar 16–32 mm ≈ 690 /t (material only).
/// Estimates: other concrete grades interpolated by f'c between those points; S355 / S450 extrapolated by Fy from the S460M
/// and S690QL rates. Tendon / cable strand 12,000 /t (project rate).
/// </summary>
public static class MaterialCosts
{
    private static readonly (double Fc, double PerCubicMetre)[] Concrete = [(30, 121), (80, 240), (100, 320), (120, 450), (150, 1200)];
    private static readonly (double Fy, double PerTonne)[] Steel = [(460, 4860), (690, 7500)];

    /// <summary>The suggested unit cost [SGD/t] and its basis; null cost when there is no basis for the type.</summary>
    public static (double? PerTonne, string Note) Suggest(MaterialProperty material)
    {
        switch (material.Kind)
        {
            case MaterialKind.Concrete:
            {
                var fc = material.Fc is > 0 ? material.Fc.Value / 1000 : GradeStrength(material.Grade.Length > 0 ? material.Grade : material.Name);
                if (fc is not { } mpa || material.MassPerVolume <= 0) return (null, "Concrete grade unknown — enter a rate");
                var perCubicMetre = Interpolate(Concrete, mpa);
                var exact = Concrete.Any(point => Math.Abs(point.Fc - mpa) < 0.5);
                var note = Math.Abs(mpa - 30) < 0.5 ? "121 SGD/m³ — BCA 2025 average, ready-mixed concrete Grade 40 (material)"
                    : exact ? $"{perCubicMetre:0} SGD/m³ — project rate table"
                    : $"{perCubicMetre:0} SGD/m³ — estimate, interpolated by f'c between the project / BCA rates";
                return (perCubicMetre / material.MassPerVolume, note);
            }
            case MaterialKind.Steel:
            case MaterialKind.ColdFormed:
            {
                var fy = material.Fy is > 0 ? material.Fy.Value / 1000 : GradeStrength(material.Grade.Length > 0 ? material.Grade : material.Name);
                if (fy is not { } mpa) return (null, "Steel grade unknown — enter a rate");
                var cost = Interpolate(Steel, mpa);
                var note = Math.Abs(mpa - 460) < 1 ? "4,860 SGD/t — project rate (HD jumbo S460M)"
                    : Math.Abs(mpa - 690) < 1 ? "7,500 SGD/t — project rate (built-up S690QL)"
                    : $"{cost:0} SGD/t — estimate, from the S460M / S690QL project rates by Fy";
                return (cost, note);
            }
            case MaterialKind.Rebar:
                return (690, "690 SGD/t — BCA 2025 average, rebar 16–32 mm (material only)");
            case MaterialKind.Tendon:
                return (12000, "12,000 SGD/t — project rate (tendon / cable)");
            default:
                return (null, "No default rate for this material type — enter a rate");
        }
    }

    /// <summary>Fills a missing unit cost with the suggestion.</summary>
    public static MaterialProperty WithDefaultCost(MaterialProperty material)
    {
        // the earlier strand estimate (≈1,000 SGD/t) gives way to the project rate; an entered rate is kept
        var superseded = material.Kind == MaterialKind.Tendon && material.CostNote.StartsWith("≈1,000 SGD/t", StringComparison.Ordinal);
        if (material.UnitCost is not null && !superseded) return material;
        var (cost, note) = Suggest(material);
        return material with { UnitCost = cost, CostNote = note };
    }

    /// <summary>The characteristic strength in a grade name: "C30/37" → 30, "S460" → 460, "100 MPa" → 100.</summary>
    private static double? GradeStrength(string grade)
    {
        var match = System.Text.RegularExpressions.Regex.Match(grade, @"(\d+(?:\.\d+)?)");
        return match.Success ? double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static double Interpolate((double X, double Y)[] points, double x)
    {
        if (x <= points[0].X) return Lerp(points[0], points[1], x);
        for (var index = 1; index < points.Length; index++)
            if (x <= points[index].X) return Lerp(points[index - 1], points[index], x);
        return Lerp(points[^2], points[^1], x);
    }

    private static double Lerp((double X, double Y) a, (double X, double Y) b, double x) => a.Y + (x - a.X) * (b.Y - a.Y) / (b.X - a.X);
}
