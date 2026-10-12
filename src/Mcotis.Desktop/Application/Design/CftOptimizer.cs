using System.Globalization;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application.Design;

/// <summary>A structural steel grade with its minimum yield strength by nominal thickness [mm → MPa].</summary>
public sealed record SteelGrade(string Name, string Standard, IReadOnlyList<(double MaxThickness, double Fy)> Bands)
{
    public double Fy(double thickness) => Bands.FirstOrDefault(band => thickness <= band.MaxThickness) is { Fy: > 0 } band ? band.Fy : Bands[^1].Fy;
    public double NominalFy => Bands[0].Fy;
    public double MaxThickness => Bands[^1].MaxThickness;
}

/// <summary>A concrete grade: fck (cylinder) and its name, C fck / fck,cube.</summary>
public sealed record ConcreteGrade(string Name, double Fck);

/// <summary>The grades the CFT optimisation steps through, lowest to highest.</summary>
public static class CftGrades
{
    /// <summary>Minimum yield strength by nominal thickness: S235 / S275 / S355 to EN 10025-2, S420 / S460 (M) to EN 10025-4,
    /// S500 – S690 (QL) to EN 10025-6. Confirm with the producer's data for the plate thickness ordered.</summary>
    public static IReadOnlyList<SteelGrade> Steel { get; } =
    [
        new("S235", "EN 10025-2", [(16, 235), (40, 225), (100, 215), (150, 195)]),
        new("S275", "EN 10025-2", [(16, 275), (40, 265), (63, 255), (80, 245), (100, 235), (150, 225)]),
        new("S355", "EN 10025-2", [(16, 355), (40, 345), (63, 335), (80, 325), (100, 315), (150, 295)]),
        new("S420", "EN 10025-4", [(16, 420), (40, 400), (63, 390), (80, 380), (100, 370), (120, 365)]),
        new("S460", "EN 10025-4", [(16, 460), (40, 440), (63, 430), (80, 410), (100, 400), (120, 385)]),
        new("S500", "EN 10025-6", [(50, 500), (100, 480), (150, 440)]),
        new("S550", "EN 10025-6", [(50, 550), (100, 530), (150, 490)]),
        new("S620", "EN 10025-6", [(50, 620), (100, 580), (150, 560)]),
        new("S690", "EN 10025-6", [(50, 690), (100, 650), (150, 630)]),
    ];

    /// <summary>EN 1992-1-1 Table 3.1 to C90/105, then the classes of the material library (EN 206, UHPC) to C185/200.</summary>
    public static IReadOnlyList<ConcreteGrade> Concrete { get; } =
    [
        new("C30/37", 30), new("C35/45", 35), new("C40/50", 40), new("C45/55", 45), new("C50/60", 50), new("C55/67", 55), new("C60/75", 60),
        new("C70/85", 70), new("C80/95", 80), new("C90/105", 90), new("C100/115", 100), new("C120/135", 120), new("C130/145", 130),
        new("C150/165", 150), new("C175/190", 175), new("C185/200", 185),
    ];

    /// <summary>Unit cost of a steel grade [SGD/t]: the model's material of that grade when it has a rate, else the default rate.</summary>
    public static double SteelRate(SteelGrade grade, IEnumerable<MaterialProperty> materials) =>
        Matching(materials, MaterialKind.Steel, grade.Name)?.UnitCost
        ?? MaterialCosts.Suggest(MaterialProperty.Default(MaterialKind.Steel, grade.Name) with { Grade = grade.Name, Fy = grade.NominalFy * 1000 }).PerTonne ?? 0;

    /// <summary>Unit cost of a concrete grade [SGD/m³].</summary>
    public static double ConcreteRate(ConcreteGrade grade, IEnumerable<MaterialProperty> materials)
    {
        var material = Matching(materials, MaterialKind.Concrete, grade.Name)
                       ?? MaterialLibrary.Standard.FirstOrDefault(item => item.Kind == MaterialKind.Concrete && item.Name == grade.Name)
                       ?? MaterialProperty.Default(MaterialKind.Concrete, grade.Name) with { Grade = grade.Name, Fc = grade.Fck * 1000 };
        var perTonne = material.UnitCost ?? MaterialCosts.Suggest(material).PerTonne ?? 0;
        return perTonne * material.MassPerVolume;
    }

    /// <summary>Unit cost of reinforcement [SGD/t].</summary>
    public static double RebarRate(IEnumerable<MaterialProperty> materials) =>
        materials.FirstOrDefault(item => item.Kind == MaterialKind.Rebar && item.UnitCost is > 0)?.UnitCost
        ?? MaterialCosts.Suggest(MaterialProperty.Default(MaterialKind.Rebar, "Rebar")).PerTonne ?? 690;

    private static MaterialProperty? Matching(IEnumerable<MaterialProperty> materials, MaterialKind kind, string grade) =>
        materials.FirstOrDefault(item => item.Kind == kind && item.UnitCost is > 0 &&
            (item.Name.Equals(grade, StringComparison.OrdinalIgnoreCase) || item.Grade.StartsWith(grade, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>A min … max range with a step (all in mm); a single value when min = max.</summary>
public sealed record SizeRange(double Min, double Max, double Step)
{
    public IEnumerable<double> Values()
    {
        if (!(Min > 0) || Max < Min) yield break;
        var step = Step > 0 ? Step : Max - Min + 1;
        for (var value = Min; value <= Max + 1e-9; value += step) yield return Math.Round(value, 6);
    }
}

/// <summary>
/// File → Design → CFT → Optimization: the ranges the search steps through — tube shape and size, wall thickness, steel and
/// concrete grade (by grade), bars inside the tube — and the unit costs of each material.
/// </summary>
public sealed record CftOptimizationInput
{
    public TubeShape Shape { get; init; } = TubeShape.Rectangular;
    public bool Square { get; init; } = true;
    public SizeRange Depth { get; init; } = new(600, 1200, 100);          // H, or D
    public SizeRange Width { get; init; } = new(600, 1200, 100);          // B (rectangular, not square)
    public SizeRange Thickness { get; init; } = new(12, 40, 4);
    public string SteelFrom { get; init; } = "S355";
    public string SteelTo { get; init; } = "S690";
    public string ConcreteFrom { get; init; } = "C50/60";
    public string ConcreteTo { get; init; } = "C90/105";
    public bool Rebar { get; init; }
    public IReadOnlyList<double> BarDiameters { get; init; } = [25, 32];
    public int BarsPerFaceMin { get; init; } = 3;                          // rectangular: on each face, corners included
    public int BarsPerFaceMax { get; init; } = 5;                          // circular: 4 × (n − 1) bars on the circle
    public double Cover { get; init; } = 50;                              // to the inside face of the tube
    public IReadOnlyDictionary<string, double> SteelRates { get; init; } = new Dictionary<string, double>();    // SGD/t
    public IReadOnlyDictionary<string, double> ConcreteRates { get; init; } = new Dictionary<string, double>(); // SGD/m³
    public double RebarRate { get; init; } = 690;                         // SGD/t
    public int Keep { get; init; } = 10;
    public const int MaxCandidates = 250_000;

    public IReadOnlyList<SteelGrade> SteelGrades => Between(CftGrades.Steel, SteelFrom, SteelTo, grade => grade.Name);
    public IReadOnlyList<ConcreteGrade> ConcreteGrades => Between(CftGrades.Concrete, ConcreteFrom, ConcreteTo, grade => grade.Name);

    private static IReadOnlyList<T> Between<T>(IReadOnlyList<T> list, string from, string to, Func<T, string> name)
    {
        var first = list.ToList().FindIndex(item => name(item) == from);
        var last = list.ToList().FindIndex(item => name(item) == to);
        if (first < 0 || last < 0) return [];
        if (first > last) (first, last) = (last, first);
        return list.Skip(first).Take(last - first + 1).ToArray();
    }
}

/// <summary>One section the optimisation can choose, with its quantities and cost per metre of column.</summary>
public sealed record CftCandidate(ColumnSectionInput Section, string SteelGrade, string ConcreteGrade, string Rebar,
    double SteelKgPerM, double ConcreteM3PerM, double RebarKgPerM, double SteelCostPerM, double ConcreteCostPerM, double RebarCostPerM)
{
    public double CostPerM => SteelCostPerM + ConcreteCostPerM + RebarCostPerM;
    public string Size => Section.Tube == TubeShape.Circular
        ? $"CHS {Section.TubeDepth:0} × {Section.TubeThickness:0.#}"
        : $"RHS {Section.TubeDepth:0} × {Section.TubeWidth:0} × {Section.TubeThickness:0.#}";
}

public sealed record CftOptimizationResult(int Rank, CftCandidate Candidate, DesignCheckSummary Check);

/// <summary>The cheapest passing sections, cheapest first, and how much of the search was needed to find them.</summary>
public sealed record CftOptimizationRun(IReadOnlyList<CftOptimizationResult> Best, int Candidates, int Evaluated, bool Cancelled);

/// <summary>
/// Cheapest CFT section for the chosen members and load cases: every combination of the input ranges is priced per metre,
/// sorted cheapest first, and designed in that order (EN 1994-1-1 / BC4, as Run Design) until the requested number of
/// passing sections is found — so the first one is the cheapest section that passes. A candidate whose wall is too
/// slender for its steel (EC4 Table 6.3) or whose bars do not fit is left out before pricing.
/// </summary>
public static class CftOptimizer
{
    private const double SteelDensity = 7850;   // kg/m³

    public static IReadOnlyList<CftCandidate> Candidates(CftOptimizationInput input, ColumnSectionInput template)
    {
        var steel = input.SteelGrades;
        var concrete = input.ConcreteGrades;
        if (steel.Count == 0 || concrete.Count == 0) throw new ArgumentException("Choose the steel and concrete grade ranges.");
        var sizes = input.Shape == TubeShape.Circular || input.Square
            ? input.Depth.Values().Select(depth => (Depth: depth, Width: depth)).ToArray()
            : input.Depth.Values().SelectMany(depth => input.Width.Values().Select(width => (Depth: depth, Width: width))).ToArray();
        var thicknesses = input.Thickness.Values().ToArray();
        var rebars = new List<(double Diameter, int PerFace)> { (0, 0) };
        if (input.Rebar)
            foreach (var diameter in input.BarDiameters.Where(value => value > 0).Distinct())
                for (var count = Math.Max(2, input.BarsPerFaceMin); count <= input.BarsPerFaceMax; count++) rebars.Add((diameter, count));
        var total = (long)sizes.Length * thicknesses.Length * steel.Count * concrete.Count * rebars.Count;
        if (total == 0) throw new ArgumentException("The size and thickness ranges give no section.");
        if (total > CftOptimizationInput.MaxCandidates)
            throw new ArgumentException($"The ranges give {total:N0} combinations; the limit is {CftOptimizationInput.MaxCandidates:N0}. Narrow a range or use larger steps.");

        var list = new List<CftCandidate>();
        foreach (var (depth, width) in sizes)
            foreach (var t in thicknesses)
            {
                var circular = input.Shape == TubeShape.Circular;
                if (2 * t >= Math.Min(depth, width)) continue;
                var steelArea = (circular ? Math.PI / 4 * (depth * depth - Math.Pow(depth - 2 * t, 2)) : depth * width - (depth - 2 * t) * (width - 2 * t)) / 1e6;
                var innerArea = (circular ? Math.PI / 4 * Math.Pow(depth - 2 * t, 2) : (depth - 2 * t) * (width - 2 * t)) / 1e6;
                foreach (var grade in steel)
                {
                    if (t > grade.MaxThickness) continue;
                    var fy = grade.Fy(t);
                    var slender = circular ? depth / t > 90 * 235 / fy : Math.Max(depth, width) / t > 52 * Math.Sqrt(235 / fy);
                    if (slender) continue;
                    foreach (var (diameter, perFace) in rebars)
                    {
                        var bars = diameter <= 0 ? 0 : circular ? 4 * (perFace - 1) : 2 * perFace + 2 * Math.Max(0, perFace - 2);
                        var section = template with
                        {
                            Tube = input.Shape, TubeDepth = depth, TubeWidth = circular ? depth : width, TubeThickness = t, Fy = fy,
                            TubeReinforced = bars > 0, BarDiameter = diameter > 0 ? diameter : template.BarDiameter,
                            BarsAlongWidth = circular ? bars : perFace, BarsAlongDepth = circular ? 0 : Math.Max(0, perFace - 2), Cover = input.Cover,
                        };
                        if (bars > 0 && !BarsFit(section, circular, bars)) continue;
                        var barArea = bars * Math.PI * diameter * diameter / 4 / 1e6;
                        foreach (var mix in concrete)
                        {
                            var steelKg = steelArea * SteelDensity;
                            var concreteM3 = innerArea - barArea;
                            var rebarKg = barArea * SteelDensity;
                            list.Add(new CftCandidate(section with { Fck = mix.Fck }, grade.Name, mix.Name,
                                bars > 0 ? $"{bars} Ø{diameter:0}" : "—", steelKg, concreteM3, rebarKg,
                                steelKg / 1000 * input.SteelRates.GetValueOrDefault(grade.Name),
                                concreteM3 * input.ConcreteRates.GetValueOrDefault(mix.Name),
                                rebarKg / 1000 * input.RebarRate));
                        }
                    }
                }
            }
        return list;
    }

    /// <summary>Bars inside the tube need a clear gap of at least max(Ø, 25 mm) between them.</summary>
    private static bool BarsFit(ColumnSectionInput s, bool circular, int bars)
    {
        var inset = s.TubeThickness + s.Cover + s.BarDiameter / 2;
        var gap = Math.Max(s.BarDiameter, 25);
        if (circular)
        {
            var radius = s.TubeDepth / 2 - inset;
            return radius > 0 && 2 * Math.PI * radius / bars - s.BarDiameter >= gap;
        }
        double u = s.TubeWidth - 2 * inset, v = s.TubeDepth - 2 * inset;
        return u > 0 && v > 0 && u / (s.BarsAlongWidth - 1) - s.BarDiameter >= gap && v / (s.BarsAlongDepth + 1) - s.BarDiameter >= gap;
    }

    /// <summary>Designs the candidates cheapest first until <see cref="CftOptimizationInput.Keep"/> pass.</summary>
    public static CftOptimizationRun Run(CftOptimizationInput input, ColumnDesignBasis basis, IReadOnlyList<DesignMemberInput> members,
        IProgress<(int Evaluated, int Total, int Found)>? progress = null, CancellationToken cancellation = default)
    {
        if (members.Count == 0 || members.All(member => member.Cases.All(item => item.Stations.Count == 0)))
            throw new ArgumentException("Choose members and load cases that have FEM forces.");
        var candidates = Candidates(input, basis.Section).OrderBy(candidate => candidate.CostPerM).ToArray();
        // grade scope: the chosen ranges are the design basis' limits for the search
        var scope = basis with
        {
            MaxConcreteGrade = input.ConcreteGrades[^1].Name,
            MaxSteelGrade = input.SteelGrades[^1].Name,
        };
        var best = new List<CftOptimizationResult>();
        var evaluated = 0;
        const int chunk = 128;
        for (var start = 0; start < candidates.Length && best.Count < input.Keep; start += chunk)
        {
            if (cancellation.IsCancellationRequested) return new(best, candidates.Length, evaluated, true);
            var slice = candidates.Skip(start).Take(chunk).ToArray();
            var checks = new DesignCheckSummary[slice.Length];
            Parallel.For(0, slice.Length, new ParallelOptions { CancellationToken = cancellation }, index =>
                checks[index] = ColumnDesignEngine.Check(scope with { Section = slice[index].Section }, members));
            for (var index = 0; index < slice.Length && best.Count < input.Keep; index++)
            {
                evaluated++;
                if (checks[index].Passes) best.Add(new(best.Count + 1, slice[index], checks[index]));
            }
            progress?.Report((evaluated, candidates.Length, best.Count));
        }
        return new(best, candidates.Length, evaluated, false);
    }

    internal static string Money(double value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
