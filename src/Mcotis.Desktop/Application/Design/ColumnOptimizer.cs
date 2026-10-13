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

/// <summary>The grades the column optimisation steps through, lowest to highest.</summary>
public static class ColumnGrades
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
/// Design → column → Optimization: the ranges the search steps through for the column type — the concrete outline (RC,
/// steel encased, mega), the steel tube (CFT), the encased I section (steel encased, mega: h, b, tf, tw; mega also the number
/// of profiles and their edge distance), the steel and concrete grades stepped grade by grade, the bars — and the unit
/// cost of each material.
/// </summary>
public sealed record ColumnOptimizationInput
{
    public ColumnDesignType Type { get; init; } = ColumnDesignType.ConcreteFilledTube;
    // concrete outline (RC, steel encased, mega): h in-plane, b across
    public bool SquareOutline { get; init; } = true;
    public SizeRange OutlineDepth { get; init; } = new(600, 1200, 100);
    public SizeRange OutlineWidth { get; init; } = new(600, 1200, 100);
    // steel tube (CFT)
    public TubeShape Shape { get; init; } = TubeShape.Rectangular;
    public bool Square { get; init; } = true;
    public SizeRange Depth { get; init; } = new(600, 1200, 100);          // H, or D
    public SizeRange Width { get; init; } = new(600, 1200, 100);          // B (rectangular, not square)
    public SizeRange Thickness { get; init; } = new(12, 40, 4);
    // encased I section (steel encased, mega)
    public SizeRange ProfileDepth { get; init; } = new(400, 600, 100);
    public SizeRange ProfileWidth { get; init; } = new(300, 500, 100);
    public SizeRange FlangeThickness { get; init; } = new(25, 60, 10);
    public SizeRange WebThickness { get; init; } = new(20, 40, 10);
    public IReadOnlyList<int> ProfileCounts { get; init; } = [4];         // mega: 4 and / or 6
    public double ProfileEdge { get; init; } = 160;                       // mega: concrete face to the outer profile's face
    // grades
    public string SteelFrom { get; init; } = "S355";
    public string SteelTo { get; init; } = "S690";
    public string ConcreteFrom { get; init; } = "C50/60";
    public string ConcreteTo { get; init; } = "C90/105";
    // bars: always for RC / steel encased / mega; optional inside a CFT tube
    public bool Rebar { get; init; }
    public IReadOnlyList<double> BarDiameters { get; init; } = [25, 32];
    public int BarsPerFaceMin { get; init; } = 3;                          // per b face, corners included (CHS: 4 × (n − 1) on the circle)
    public int BarsPerFaceMax { get; init; } = 5;
    /// <summary>True: the h faces take bars at the b faces' spacing; false: <see cref="BarsDepthMin"/> … <see cref="BarsDepthMax"/> between the corners.</summary>
    public bool UniformBarSpacing { get; init; } = true;
    public int BarsDepthMin { get; init; } = 1;
    public int BarsDepthMax { get; init; } = 3;
    public double Cover { get; init; } = 50;                              // CFT: to the inside face of the tube; others: to the links
    // unit costs
    public IReadOnlyDictionary<string, double> SteelRates { get; init; } = new Dictionary<string, double>();    // SGD/t
    public IReadOnlyDictionary<string, double> ConcreteRates { get; init; } = new Dictionary<string, double>(); // SGD/m³
    public double RebarRate { get; init; } = 690;                         // SGD/t
    public int Keep { get; init; } = 10;
    public const int MaxCandidates = 250_000;

    public bool UsesSteel => Type != ColumnDesignType.ReinforcedConcrete;
    public bool UsesProfile => Type is ColumnDesignType.SteelEncased or ColumnDesignType.MegaComposite;
    public bool UsesOutline => Type != ColumnDesignType.ConcreteFilledTube;
    public bool AlwaysReinforced => Type != ColumnDesignType.ConcreteFilledTube;

    public IReadOnlyList<SteelGrade> SteelGrades => Between(ColumnGrades.Steel, SteelFrom, SteelTo, grade => grade.Name);
    public IReadOnlyList<ConcreteGrade> ConcreteGrades => Between(ColumnGrades.Concrete, ConcreteFrom, ConcreteTo, grade => grade.Name);

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
public sealed record ColumnCandidate(ColumnDesignType Type, ColumnSectionInput Section, string SteelGrade, string ConcreteGrade, string Rebar,
    double SteelKgPerM, double ConcreteM3PerM, double RebarKgPerM, double SteelCostPerM, double ConcreteCostPerM, double RebarCostPerM)
{
    public double CostPerM => SteelCostPerM + ConcreteCostPerM + RebarCostPerM;
    public string Size => Type switch
    {
        ColumnDesignType.ConcreteFilledTube when Section.Tube == TubeShape.Circular => $"CHS {Section.TubeDepth:0} × {Section.TubeThickness:0.#}",
        ColumnDesignType.ConcreteFilledTube => $"RHS {Section.TubeDepth:0} × {Section.TubeWidth:0} × {Section.TubeThickness:0.#}",
        ColumnDesignType.ReinforcedConcrete => $"{Section.Width:0} × {Section.Depth:0}",
        ColumnDesignType.SteelEncased => $"{Section.Width:0} × {Section.Depth:0} + I {Profile}",
        _ => $"{Section.Width:0} × {Section.Depth:0} + {Section.ProfileCount} × I {Profile}",
    };
    private string Profile => $"{Section.SteelDepth:0}×{Section.SteelWidth:0}×{Section.FlangeThickness:0.#}/{Section.WebThickness:0.#}";
}

public sealed record ColumnOptimizationResult(int Rank, ColumnCandidate Candidate, DesignCheckSummary Check);

/// <summary>The cheapest passing sections, cheapest first, and how much of the search was needed to find them.</summary>
public sealed record ColumnOptimizationRun(IReadOnlyList<ColumnOptimizationResult> Best, int Candidates, int Evaluated, bool Cancelled);

/// <summary>
/// Cheapest column section for the chosen members and load cases, for any column type: every combination of the input
/// ranges is priced per metre, sorted cheapest first, and designed in that order with the type's own algorithm (as Run
/// Design: EC2 for RC, EN 1994-1-1 / BC4 for CFT and steel encased, the mega column method) until the requested number pass —
/// so the first one is the cheapest section that passes. Candidates that cannot be built (a wall or flange too slender for
/// its steel, a profile that does not fit the outline, bars without room) are left out before pricing. Costs: steel and bars
/// by mass, concrete by net volume, links of RC / encased / mega columns included with the bars; formwork is not priced.
/// </summary>
public static class ColumnOptimizer
{
    private const double SteelDensity = 7850;   // kg/m³

    public static IReadOnlyList<ColumnCandidate> Candidates(ColumnOptimizationInput input, ColumnSectionInput template)
    {
        var steel = input.UsesSteel ? input.SteelGrades : [new SteelGrade("—", "", [(1e9, template.Fy)])];
        var concrete = input.ConcreteGrades;
        if (steel.Count == 0 || concrete.Count == 0) throw new ArgumentException("Choose the steel and concrete grade ranges.");

        // the geometry variants of the type (before grades)
        var shapes = Shapes(input, template).ToList();
        var bars = BarOptions(input);
        var total = (long)shapes.Count * steel.Count * concrete.Count * bars.Count;
        if (total == 0) throw new ArgumentException("The ranges give no section.");
        if (total > ColumnOptimizationInput.MaxCandidates)
            throw new ArgumentException($"The ranges give {total:N0} combinations; the limit is {ColumnOptimizationInput.MaxCandidates:N0}. Narrow a range or use larger steps.");

        var list = new List<ColumnCandidate>();
        foreach (var shape in shapes)
            foreach (var grade in steel)
            {
                var plate = input.Type == ColumnDesignType.ConcreteFilledTube ? shape.TubeThickness : shape.FlangeThickness;
                if (input.UsesSteel && plate > grade.MaxThickness) continue;
                var fy = grade.Fy(plate);
                if (input.UsesSteel && Slender(input.Type, shape, fy)) continue;
                foreach (var (diameter, perFace, depthBars) in bars)
                {
                    var section = WithBars(input, shape with { Fy = fy }, diameter, perFace, depthBars);
                    var barCount = BarCount(input.Type, section);
                    if (barCount > 0 && !BarsFit(input.Type, section)) continue;
                    var (steelArea, grossConcrete) = Areas(input.Type, section);
                    var barArea = barCount * Math.PI * diameter * diameter / 4 / 1e6;
                    var linkKg = input.AlwaysReinforced ? LinkKgPerM(section) : 0;
                    foreach (var mix in concrete)
                    {
                        var steelKg = steelArea * SteelDensity;
                        var concreteM3 = grossConcrete - barArea;
                        var rebarKg = barArea * SteelDensity + linkKg;
                        list.Add(new ColumnCandidate(input.Type, section with { Fck = mix.Fck }, input.UsesSteel ? grade.Name : "—", mix.Name,
                            barCount > 0 ? $"{barCount} Ø{diameter:0}" : "—", steelKg, concreteM3, rebarKg,
                            steelKg / 1000 * input.SteelRates.GetValueOrDefault(grade.Name),
                            concreteM3 * input.ConcreteRates.GetValueOrDefault(mix.Name),
                            rebarKg / 1000 * input.RebarRate));
                    }
                }
            }
        return list;
    }

    /// <summary>The geometry variants of the input's column type, before grades and bars.</summary>
    private static IEnumerable<ColumnSectionInput> Shapes(ColumnOptimizationInput input, ColumnSectionInput template)
    {
        if (input.Type == ColumnDesignType.ConcreteFilledTube)
        {
            var circular = input.Shape == TubeShape.Circular;
            var sizes = circular || input.Square
                ? input.Depth.Values().Select(depth => (Depth: depth, Width: depth))
                : input.Depth.Values().SelectMany(depth => input.Width.Values().Select(width => (Depth: depth, Width: width)));
            foreach (var (depth, width) in sizes)
                foreach (var t in input.Thickness.Values())
                    if (2 * t < Math.Min(depth, width))
                        yield return template with { Tube = input.Shape, TubeDepth = depth, TubeWidth = circular ? depth : width, TubeThickness = t };
            yield break;
        }
        var outlines = input.SquareOutline
            ? input.OutlineDepth.Values().Select(depth => (Depth: depth, Width: depth))
            : input.OutlineDepth.Values().SelectMany(depth => input.OutlineWidth.Values().Select(width => (Depth: depth, Width: width)));
        foreach (var (depth, width) in outlines)
        {
            var outline = template with { Depth = depth, Width = width };
            if (!input.UsesProfile) { yield return outline; continue; }
            foreach (var hs in input.ProfileDepth.Values())
                foreach (var bf in input.ProfileWidth.Values())
                    foreach (var tf in input.FlangeThickness.Values())
                        foreach (var tw in input.WebThickness.Values())
                        {
                            if (2 * tf >= hs || tw >= bf) continue;
                            var profile = outline with { SteelDepth = hs, SteelWidth = bf, FlangeThickness = tf, WebThickness = tw, ProfileName = "" };
                            if (input.Type == ColumnDesignType.SteelEncased)
                            {
                                if (hs < depth && bf < width) yield return profile;
                                continue;
                            }
                            foreach (var count in input.ProfileCounts.Where(value => value is 4 or 6).Distinct())
                            {
                                var dsy = depth / 2 - input.ProfileEdge - hs / 2;
                                var dsz = width / 2 - input.ProfileEdge - bf / 2;
                                if (dsy > 0 && dsz > 0) yield return profile with { ProfileCount = count, ProfileOffsetY = dsy, ProfileOffsetZ = dsz };
                            }
                        }
        }
    }

    private static List<(double Diameter, int PerFace, int DepthBars)> BarOptions(ColumnOptimizationInput input)
    {
        var options = new List<(double, int, int)>();
        if (!input.AlwaysReinforced) options.Add((0, 0, 0));
        if (!input.AlwaysReinforced && !input.Rebar) return options;
        foreach (var diameter in input.BarDiameters.Where(value => value > 0).Distinct())
            for (var count = Math.Max(2, input.BarsPerFaceMin); count <= input.BarsPerFaceMax; count++)
                if (input.UniformBarSpacing || input.Type == ColumnDesignType.ConcreteFilledTube) options.Add((diameter, count, -1));
                else
                    for (var depthBars = Math.Max(0, input.BarsDepthMin); depthBars <= input.BarsDepthMax; depthBars++) options.Add((diameter, count, depthBars));
        return options;
    }

    /// <summary>The section with its bars: per b face, and on the h faces at the same spacing (depthBars &lt; 0) or as given.</summary>
    private static ColumnSectionInput WithBars(ColumnOptimizationInput input, ColumnSectionInput s, double diameter, int perFace, int depthBars)
    {
        if (input.Type == ColumnDesignType.ConcreteFilledTube)
        {
            var circular = s.Tube == TubeShape.Circular;
            var count = diameter <= 0 ? 0 : circular ? 4 * (perFace - 1) : perFace;
            return s with
            {
                TubeReinforced = count > 0, BarDiameter = diameter > 0 ? diameter : s.BarDiameter, Cover = input.Cover,
                BarsAlongWidth = circular ? count : perFace, BarsAlongDepth = circular ? 0 : Math.Max(0, perFace - 2),
            };
        }
        var between = depthBars >= 0 ? depthBars
            : (int)Math.Max(0, Math.Round((perFace - 1) * (s.Depth - 2 * input.Cover) / Math.Max(1, s.Width - 2 * input.Cover)) - 1);
        return s with { BarDiameter = diameter, BarsAlongWidth = perFace, BarsAlongDepth = between, Cover = input.Cover };
    }

    private static int BarCount(ColumnDesignType type, ColumnSectionInput s) => type switch
    {
        ColumnDesignType.ConcreteFilledTube => !s.TubeReinforced ? 0 : s.Tube == TubeShape.Circular ? s.BarsAlongWidth : 2 * s.BarsAlongWidth + 2 * s.BarsAlongDepth,
        ColumnDesignType.MegaComposite => CompositeSection.PerimeterBars(s, s.BarLayers).Count,
        _ => 2 * Math.Max(2, s.BarsAlongWidth) + 2 * s.BarsAlongDepth,
    };

    /// <summary>Structural steel and gross concrete area [m²] of the section.</summary>
    private static (double Steel, double Concrete) Areas(ColumnDesignType type, ColumnSectionInput s)
    {
        if (type == ColumnDesignType.ConcreteFilledTube)
        {
            double d = s.TubeDepth, b = s.TubeWidth, t = s.TubeThickness;
            return s.Tube == TubeShape.Circular
                ? (Math.PI / 4 * (d * d - Math.Pow(d - 2 * t, 2)) / 1e6, Math.PI / 4 * Math.Pow(d - 2 * t, 2) / 1e6)
                : ((d * b - (d - 2 * t) * (b - 2 * t)) / 1e6, (d - 2 * t) * (b - 2 * t) / 1e6);
        }
        var profiles = type switch { ColumnDesignType.SteelEncased => 1, ColumnDesignType.MegaComposite => s.ProfileCount == 6 ? 6 : 4, _ => 0 };
        var steel = profiles * (2 * s.SteelWidth * s.FlangeThickness + (s.SteelDepth - 2 * s.FlangeThickness) * s.WebThickness) / 1e6;
        return (steel, s.Width * s.Depth / 1e6 - steel);
    }

    /// <summary>One closed perimeter link per spacing [kg/m].</summary>
    private static double LinkKgPerM(ColumnSectionInput s)
    {
        if (!(s.LinkSpacing > 0) || !(s.LinkDiameter > 0)) return 0;
        var perimeter = 2 * (s.Width - 2 * s.Cover - s.LinkDiameter) + 2 * (s.Depth - 2 * s.Cover - s.LinkDiameter);
        return Math.PI * s.LinkDiameter * s.LinkDiameter / 4 * perimeter / s.LinkSpacing / 1e6 * 1000 * SteelDensity / 1000;
    }

    /// <summary>EC4 Table 6.3 / 6.7.1(9): a CFT wall or (unencased-cover) flange too slender for its steel.</summary>
    private static bool Slender(ColumnDesignType type, ColumnSectionInput s, double fy) => type switch
    {
        ColumnDesignType.ConcreteFilledTube => s.Tube == TubeShape.Circular ? s.TubeDepth / s.TubeThickness > 90 * 235 / fy
                                                                            : Math.Max(s.TubeDepth, s.TubeWidth) / s.TubeThickness > 52 * Math.Sqrt(235 / fy),
        _ => false,   // encased flanges: the design's cover / flange rule decides
    };

    /// <summary>Bars need a clear gap of at least max(Ø, 25 mm) between them.</summary>
    private static bool BarsFit(ColumnDesignType type, ColumnSectionInput s)
    {
        var gap = Math.Max(s.BarDiameter, 25);
        if (type == ColumnDesignType.ConcreteFilledTube)
        {
            var inset = s.TubeThickness + s.Cover + s.BarDiameter / 2;
            if (s.Tube == TubeShape.Circular)
            {
                var radius = s.TubeDepth / 2 - inset;
                return radius > 0 && 2 * Math.PI * radius / s.BarsAlongWidth - s.BarDiameter >= gap;
            }
            double ut = s.TubeWidth - 2 * inset, vt = s.TubeDepth - 2 * inset;
            return ut > 0 && vt > 0 && ut / (s.BarsAlongWidth - 1) - s.BarDiameter >= gap && vt / (s.BarsAlongDepth + 1) - s.BarDiameter >= gap;
        }
        var edge = s.Cover + s.LinkDiameter + s.BarDiameter / 2;
        double u = s.Width - 2 * edge, v = s.Depth - 2 * edge;
        return u > 0 && v > 0 && u / (Math.Max(2, s.BarsAlongWidth) - 1) - s.BarDiameter >= gap && v / (s.BarsAlongDepth + 1) - s.BarDiameter >= gap;
    }

    /// <summary>Designs the candidates cheapest first until <see cref="ColumnOptimizationInput.Keep"/> pass.</summary>
    public static ColumnOptimizationRun Run(ColumnOptimizationInput input, ColumnDesignBasis basis, IReadOnlyList<DesignMemberInput> members,
        IProgress<(int Evaluated, int Total, int Found)>? progress = null, CancellationToken cancellation = default)
    {
        if (members.Count == 0 || members.All(member => member.Cases.All(item => item.Stations.Count == 0)))
            throw new ArgumentException("Choose members and load cases that have FEM forces.");
        input = input with { Type = basis.Type };
        var candidates = Candidates(input, basis.Section).OrderBy(candidate => candidate.CostPerM).ToArray();
        // grade scope: the chosen ranges are the design basis' limits for the search
        var scope = basis with
        {
            MaxConcreteGrade = input.ConcreteGrades[^1].Name,
            MaxSteelGrade = input.UsesSteel ? input.SteelGrades[^1].Name : basis.MaxSteelGrade,
        };
        var best = new List<ColumnOptimizationResult>();
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
