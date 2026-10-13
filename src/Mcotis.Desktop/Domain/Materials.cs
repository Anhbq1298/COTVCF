namespace Mcotis.Desktop.Domain;

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
    /// <summary>Characteristic strength [MPa]: f'c of a concrete, Fy otherwise; read from the grade or name ("C70/85",
    /// "S460") when the value is not set.</summary>
    public double? StrengthMpa => (Kind == MaterialKind.Concrete ? Fc : Fy) is > 0 and var value ? value / 1000
        : MaterialCosts.GradeStrength(Grade.Length > 0 ? Grade : Name);

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
    internal static double? GradeStrength(string grade)
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

/// <summary>
/// The standard materials Define → Materials offers in every model (source "Library"), with Singapore unit costs from
/// <see cref="MaterialCosts"/>.
/// Steel (E 210 GPa, ν 0.3, α 1.2e-5 /°C, 7850 kg/m³ — EN 1993-1-1 3.2.6): S355 = S355J2 to EN 10025-2, S460 = S460M to
/// EN 10025-4 (heavy / jumbo sections), S690 = S690QL to EN 10025-6 (built-up plate); fy and fu are the nominal values of the
/// thinnest band, the notes give the reduction with thickness (producer data sheets to the EN standards).
/// Concrete (ν 0.2, α 1e-5 /°C): C70/85 – C90/105 from EN 1992-1-1 Table 3.1 and C100/115 from EN 206; C120/135 is an
/// intermediate grade for the project's 120 MPa rate; C130/145, C150/165, C175/190 are the UHPC classes of the German
/// DAfStb guideline / NF P18-470, and C185/200 a 200 MPa cube UHPC (cylinder by the standard's indicative 15 MPa offset).
/// Ecm = 22 [(fck + 8)/10]^0.3 GPa throughout — the EC2 expression the column design uses; beyond C90/105 it is an
/// extrapolation and test data or supplier values should replace it.
/// </summary>
public static class MaterialLibrary
{
    private const double SteelWeight = 76.98;      // kN/m³ (7850 kg/m³)

    public static IReadOnlyList<MaterialProperty> Standard { get; } = Build();

    private static List<MaterialProperty> Build()
    {
        MaterialProperty Steel(string name, string grade, double fy, double fu, string standard, string bands, int color) =>
            MaterialCosts.WithDefaultCost(new MaterialProperty
            {
                Name = name, Kind = MaterialKind.Steel, Grade = grade, Color = color, WeightPerVolume = SteelWeight, ElasticModulus = 210_000_000,
                PoissonRatio = 0.3, ThermalCoefficient = 1.2e-5, Fy = fy * 1000, Fu = fu * 1000, ExpectedFy = 1.1 * fy * 1000, ExpectedFu = 1.1 * fu * 1000,
                Source = "Library", Notes = $"{grade} to {standard}. Minimum fy by nominal thickness: {bands}. fu = {fu:0} MPa (thinnest band). E 210 GPa, ν 0.3, α 1.2e-5 /°C (EN 1993-1-1 3.2.6).",
            });
        MaterialProperty Concrete(int fck, int cube, string basis, double weight = 25.0) =>
            MaterialCosts.WithDefaultCost(new MaterialProperty
            {
                Name = $"C{fck}/{cube}", Kind = MaterialKind.Concrete, Grade = $"C{fck}/{cube}", Color = fck > 120 ? 0xC00000 : 0xFF0000,
                WeightPerVolume = weight, ElasticModulus = Math.Round(22e3 * Math.Pow((fck + 8) / 10.0, 0.3)) * 1000, PoissonRatio = 0.2, ThermalCoefficient = 1e-5,
                Fc = fck * 1000, ExpectedFc = fck * 1000, Source = "Library",
                Notes = $"fck = {fck} MPa (cylinder), fck,cube = {cube} MPa — {basis}. Ecm = 22 [(fck + 8)/10]^0.3 = {22 * Math.Pow((fck + 8) / 10.0, 0.3):0.0} GPa (EC2 Table 3.1 expression)" +
                        (fck > 90 ? "; beyond EC2's range — confirm E with test or supplier data." : "."),
            });
        return
        [
            Steel("S355", "S355J2", 355, 470, "EN 10025-2", "355 (≤ 16 mm), 345 (≤ 40), 335 (≤ 63), 325 (≤ 80), 315 (≤ 100), 295 (≤ 150)", 0xFFFF00),
            Steel("S460", "S460M", 460, 540, "EN 10025-4", "460 (≤ 16 mm), 440 (≤ 40), 430 (≤ 63), 410 (≤ 80), 400 (≤ 100), 385 (≤ 125); jumbo sections to HISTAR 460 keep 460 to 140 mm", 0xFFA500),
            Steel("S690", "S690QL", 690, 770, "EN 10025-6", "690 (≤ 50 mm), 650 (≤ 100), 630 (≤ 150)", 0xFF6000),
            Concrete(70, 85, "EN 1992-1-1 Table 3.1"),
            Concrete(80, 95, "EN 1992-1-1 Table 3.1"),
            Concrete(90, 105, "EN 1992-1-1 Table 3.1 (highest EC2 class)"),
            Concrete(100, 115, "EN 206 (highest EN 206 class)"),
            Concrete(120, 135, "intermediate high-strength grade (project 120 MPa rate)"),
            Concrete(130, 145, "UHPC, DAfStb guideline / NF P18-470", 25.5),
            Concrete(150, 165, "UHPC, DAfStb guideline / NF P18-470", 25.5),
            Concrete(175, 190, "UHPC, DAfStb guideline / NF P18-470", 25.5),
            Concrete(185, 200, "UHPC with a 200 MPa cube strength; cylinder by the NF P18-470 indicative 15 MPa offset", 25.5),
        ];
    }

    /// <summary><paramref name="material"/> with the strengths and grade it lacks taken from the standard material of the same
    /// name; its own E, unit weight and cost are kept.</summary>
    public static MaterialProperty FillGaps(MaterialProperty material)
    {
        if (Standard.FirstOrDefault(item => string.Equals(item.Name, material.Name, StringComparison.OrdinalIgnoreCase) && item.Kind == material.Kind) is not { } standard)
            return material;
        return material with
        {
            Grade = material.Grade.Length > 0 && material.Grade != material.Name ? material.Grade : standard.Grade,
            Fc = material.Fc ?? standard.Fc, ExpectedFc = material.ExpectedFc ?? standard.ExpectedFc,
            Fy = material.Fy ?? standard.Fy, Fu = material.Fu ?? standard.Fu, ExpectedFy = material.ExpectedFy ?? standard.ExpectedFy, ExpectedFu = material.ExpectedFu ?? standard.ExpectedFu,
        };
    }
}
