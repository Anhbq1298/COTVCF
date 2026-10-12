using System.Globalization;
using System.Text;
using COT_VCF.Domain;

namespace COT_VCF.Application.Design;

/// <summary>FEM forces at one station of a member: axial tension positive (SAP convention), in-plane shear and moment.</summary>
public sealed record DesignStation(double AxialKN, double ShearKN, double MomentKNm);
/// <summary>The stations of one member under one load case / combination.</summary>
public sealed record DesignCaseForces(string Case, IReadOnlyList<DesignStation> Stations);
/// <summary>A member to design: its FEM section name, member length and forces per case.</summary>
public sealed record DesignMemberInput(string Member, string Section, double LengthM, IReadOnlyList<DesignCaseForces> Cases);

/// <summary>One case of one member: design actions (compression positive) and utilisations.</summary>
public sealed record DesignCaseResult(string Case, double NEdKN, double M0EdKNm, double MEdYKNm, double MEdZKNm, double VEdKN,
    double AxialUtilization, double MajorUtilization, double MinorUtilization, double ShearUtilization)
{
    public double MaxUtilization => new[] { AxialUtilization, MajorUtilization, MinorUtilization, ShearUtilization }.Max();
}

/// <summary>One curve of an N–M interaction diagram: N in kN (compression +), M in kNm (≥ 0).</summary>
public sealed record InteractionSeries(string Name, IReadOnlyList<(double NKN, double MKNm)> Points, bool Dashed = false, bool Minor = false);

/// <summary>The cross-section N–M interaction of a column design (design resistances; no buckling, no αM), with its anchors.</summary>
public sealed record InteractionDiagram(IReadOnlyList<InteractionSeries> Series, double NplRdKN, double NtRdKN, double MplYRdKNm, double MplZRdKNm, string Basis);

/// <summary>A detailing or scope rule: pass / fail with the values it compared.</summary>
public sealed record DetailingCheck(string Name, string Clause, bool Pass, string Text);

/// <summary>The design of one member: every case, the governing one, the detailing rules and the step-by-step report.</summary>
public sealed record ColumnMemberDesign(string Member, string Section, double LengthM, IReadOnlyList<DesignCaseResult> Cases,
    DesignCaseResult? Governing, IReadOnlyList<DetailingCheck> Detailing, string Detail, string? Error = null)
{
    public double MaxUtilization => Governing?.MaxUtilization ?? double.PositiveInfinity;
    public bool DetailingOk => Detailing.All(check => check.Pass);
    public string Status => Error is not null ? "ERROR" : MaxUtilization > 1.0 ? "FAIL" : DetailingOk ? "OK" : "CHECK";
}

/// <summary>
/// Column design to SS EN 1992-1-1 (RC, nominal curvature) and SS EN 1994-1-1 simplified method (CFT, steel encased),
/// extended for high-strength concrete as BCA BC4 does (η of EC2 Table 3.1 on the concrete block above C50/60). The FEM
/// is 2D: its moment bends the major (y-y) axis; the minor (z-z) axis is checked for buckling with imperfections only.
/// Units inside: N, mm, MPa.
/// </summary>
public static class ColumnDesignEngine
{
    public static ColumnMemberDesign Design(ColumnDesignBasis basis, DesignMemberInput member)
    {
        var l0y = (basis.EffectiveLengthY ?? 0) * 1000;
        var l0z = (basis.EffectiveLengthZ ?? 0) * 1000;
        var length = member.LengthM > 0 ? member.LengthM * 1000 : Math.Max(l0y, l0z);
        if (member.Cases.Count == 0 || member.Cases.All(item => item.Stations.Count == 0))
            return new(member.Member, member.Section, member.LengthM, [], null, [], "", "No FEM forces for this member in the chosen load cases.");
        var designer = Designer(basis);
        var results = new List<(DesignCaseResult Result, string Log)>();
        foreach (var forces in member.Cases.Where(item => item.Stations.Count > 0))
        {
            var compression = forces.Stations.Max(station => -station.AxialKN) * 1000;
            var tension = forces.Stations.Min(station => -station.AxialKN) * 1000;
            var moment = forces.Stations.Max(station => Math.Abs(station.MomentKNm)) * 1e6;
            var shear = forces.Stations.Max(station => Math.Abs(station.ShearKN)) * 1000;
            double[] scenarios = tension < 0 && tension < compression ? [compression, tension] : [compression];
            foreach (var axial in scenarios)
            {
                var log = new StringBuilder();
                var result = designer.DesignCase(forces.Case, axial, moment, shear, length, l0y, l0z, log);
                results.Add((result, log.ToString()));
            }
        }
        // one row per case: its governing scenario
        var perCase = results.GroupBy(item => item.Result.Case).Select(group => group.MaxBy(item => item.Result.MaxUtilization)).ToList();
        var governing = perCase.MaxBy(item => item.Result.MaxUtilization);
        var maxCompression = perCase.Max(item => item.Result.NEdKN) * 1000;
        var detailing = designer.Detailing(maxCompression, length, l0y, l0z);

        var report = new StringBuilder();
        report.AppendLine($"MEMBER {member.Member}   ·   FEM section {member.Section}   ·   L = {F(length / 1000, 3)} m");
        report.AppendLine($"Design code: {basis.DesignCode}");
        report.AppendLine($"Method:      {basis.Method}");
        report.AppendLine($"Effective lengths: l0,y = {F(l0y / 1000, 3)} m (major, in-plane) · l0,z = {F(l0z / 1000, 3)} m (minor)");
        report.AppendLine();
        designer.Describe(report);
        report.AppendLine();
        report.AppendLine($"GOVERNING CASE: {governing.Result.Case}   (max utilisation {F(governing.Result.MaxUtilization, 3)})");
        report.AppendLine(new string('─', 92));
        report.Append(governing.Log);
        report.AppendLine();
        report.AppendLine("ALL CASES (NEd compression +)");
        report.AppendLine($"{"Case",-24}{"NEd kN",11}{"M0Ed kNm",11}{"MEd,y kNm",11}{"MEd,z kNm",11}{"VEd kN",10}{"N",7}{"M-y",7}{"M-z",7}{"V",7}");
        foreach (var (row, _) in perCase)
            report.AppendLine($"{Trim(row.Case, 23),-24}{F(row.NEdKN),11}{F(row.M0EdKNm),11}{F(row.MEdYKNm),11}{F(row.MEdZKNm),11}{F(row.VEdKN),10}" +
                              $"{U(row.AxialUtilization),7}{U(row.MajorUtilization),7}{U(row.MinorUtilization),7}{U(row.ShearUtilization),7}");
        report.AppendLine();
        report.AppendLine("DETAILING AND SCOPE");
        foreach (var check in detailing)
            report.AppendLine($"  [{(check.Pass ? "OK  " : "FAIL")}] {check.Name} ({check.Clause}): {check.Text}");
        return new(member.Member, member.Section, member.LengthM, perCase.Select(item => item.Result).ToList(), governing.Result, detailing, report.ToString());
    }

    private static ColumnDesigner Designer(ColumnDesignBasis basis) => basis.Type switch
    {
        ColumnDesignType.ReinforcedConcrete => new RcDesigner(basis),
        ColumnDesignType.MegaComposite => new MegaDesigner(basis),
        _ => new CompositeDesigner(basis),
    };

    /// <summary>The N–M interaction of the basis' section, from the design algorithm of its column type.</summary>
    public static InteractionDiagram Interaction(ColumnDesignBasis basis) => Designer(basis).Interaction();

    internal static string F(double value, int decimals = 1) =>
        double.IsFinite(value) ? value.ToString("F" + decimals, CultureInfo.InvariantCulture) : "∞";
    private static string U(double value) => double.IsFinite(value) ? value.ToString("0.00", CultureInfo.InvariantCulture) : ">9.99";
    private static string Trim(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    /// <summary>EC2 Table 3.1 rectangular block factors and strains for <paramref name="fck"/> [MPa].</summary>
    internal static (double Eta, double Lambda, double Ecu3, double Ec3) BlockFactors(double fck) => fck <= 50
        ? (1.0, 0.8, 0.0035, 0.00175)
        : (1.0 - (fck - 50) / 200, 0.8 - (fck - 50) / 400, (2.6 + 35 * Math.Pow((90 - fck) / 100, 4)) / 1000, (1.75 + 0.55 * (fck - 50) / 40) / 1000);

    /// <summary>Mean secant modulus Ecm = 22 (fcm/10)^0.3 GPa, fcm = fck + 8 (EC2 Table 3.1) [MPa].</summary>
    internal static double Ecm(double fck) => 22_000 * Math.Pow((fck + 8) / 10, 0.3);

    /// <summary>EN 1993-1-1 6.3.1.2 reduction factor χ for imperfection factor α.</summary>
    internal static double Chi(double slenderness, double alpha)
    {
        var phi = 0.5 * (1 + alpha * (slenderness - 0.2) + slenderness * slenderness);
        return Math.Min(1.0, 1 / (phi + Math.Sqrt(Math.Max(0, phi * phi - slenderness * slenderness))));
    }
}

// ============================ cross-section fibres ============================

internal enum Part { Concrete, Steel }

/// <summary>A rectangle (centre U, V; W along u, H along v) or a circle (radius R); Sign −1 deducts it. Shear marks the
/// steel shear area (web) whose strength the shear interaction reduces.</summary>
internal sealed record Region(Part Part, double Sign, double U, double V, double W, double H, double R = 0, bool Shear = false)
{
    public bool IsCircle => R > 0;
    public double Area => Sign * (IsCircle ? Math.PI * R * R : W * H);
    public double Inertia => Sign * (IsCircle ? Math.PI * Math.Pow(R, 4) / 4 + Math.PI * R * R * V * V : W * H * H * H / 12 + W * H * V * V);
    public double Top => V + (IsCircle ? R : H / 2);
    public double Bottom => V - (IsCircle ? R : H / 2);
    public Region Rotated() => this with { U = V, V = U, W = H, H = W };

    /// <summary>Signed area of the region between v = lo and v = hi.</summary>
    public double AreaBetween(double lo, double hi)
    {
        if (!IsCircle) return Sign * W * Math.Max(0, Math.Min(hi, Top) - Math.Max(lo, Bottom));
        double Integral(double y)
        {
            y = Math.Clamp(y, -R, R);
            return y * Math.Sqrt(R * R - y * y) + R * R * Math.Asin(y / R);
        }
        return Sign * Math.Max(0, Integral(hi - V) - Integral(lo - V));
    }
}

internal sealed record Bar(double U, double V, double Area)
{
    public Bar Rotated() => new(V, U, Area);
}

/// <summary>A doubly symmetric section centred on the origin, cut into layers along v for bending about the u axis.</summary>
internal sealed class FibreSection
{
    private const int LayerCount = 400;
    public IReadOnlyList<Region> Regions { get; }
    public IReadOnlyList<Bar> Bars { get; }
    public double Top { get; }
    public double Bottom { get; }
    public double[] Mid { get; } = new double[LayerCount];
    public double[] Concrete { get; } = new double[LayerCount];
    public double[] Steel { get; } = new double[LayerCount];
    public double[] SteelShear { get; } = new double[LayerCount];

    public FibreSection(IReadOnlyList<Region> regions, IReadOnlyList<Bar> bars)
    {
        Regions = regions;
        Bars = bars;
        Top = regions.Max(region => region.Top);
        Bottom = regions.Min(region => region.Bottom);
        var step = (Top - Bottom) / LayerCount;
        for (var index = 0; index < LayerCount; index++)
        {
            var hi = Top - index * step;
            var lo = hi - step;
            Mid[index] = (hi + lo) / 2;
            foreach (var region in regions)
            {
                var area = region.AreaBetween(lo, hi);
                if (region.Part == Part.Concrete) Concrete[index] += area;
                else if (region.Shear) SteelShear[index] += area;
                else Steel[index] += area;
            }
        }
    }

    public double Depth => Top - Bottom;
    public double Area(Part part) => Regions.Where(region => region.Part == part).Sum(region => region.Area);
    public double Inertia(Part part) => Regions.Where(region => region.Part == part).Sum(region => region.Inertia);
    public double ShearArea => Regions.Where(region => region.Shear).Sum(region => region.Area);
    public double BarArea => Bars.Sum(bar => bar.Area);
    public double BarInertia => Bars.Sum(bar => bar.Area * bar.V * bar.V);
    /// <summary>Net concrete: the concrete regions less the bars they hold.</summary>
    public double ConcreteArea => Area(Part.Concrete) - BarArea;
    public double ConcreteInertia => Inertia(Part.Concrete) - BarInertia;
    public FibreSection Rotated() => new(Regions.Select(region => region.Rotated()).ToList(), Bars.Select(bar => bar.Rotated()).ToList());
}

/// <summary>N–M interaction (N compression positive, M ≥ 0) sorted by N; <see cref="MomentAt"/> interpolates.</summary>
internal sealed class InteractionCurve
{
    private readonly (double N, double M)[] points;

    public InteractionCurve(IEnumerable<(double N, double M)> points) =>
        this.points = points.Select(point => (point.N, Math.Abs(point.M))).OrderBy(point => point.N).ToArray();

    public double MaxN => points[^1].N;
    public double MinN => points[0].N;
    public double MaxM => points.Max(point => point.M);
    public IReadOnlyList<(double N, double M)> Points => points;

    /// <summary>Moment resistance at axial force <paramref name="n"/>; 0 outside the curve.</summary>
    public double MomentAt(double n)
    {
        if (n > MaxN || n < MinN) return 0;
        for (var index = 1; index < points.Length; index++)
            if (n <= points[index].N)
            {
                var (n0, m0) = points[index - 1];
                var (n1, m1) = points[index];
                return n1 - n0 < 1e-9 ? Math.Max(m0, m1) : m0 + (m1 - m0) * (n - n0) / (n1 - n0);
            }
        return points[^1].M;
    }

    /// <summary>Rigid-plastic interaction (EC4 6.7.3.2): concrete in compression at <paramref name="fc"/>, steel and bars at
    /// ± design yield; bars displace concrete.</summary>
    public static InteractionCurve Plastic(FibreSection section, double fc, double fyd, double fydShear, double fsd)
    {
        const int steps = 400;
        var result = new List<(double, double)>();
        for (var k = -1; k <= steps + 1; k++)
        {
            var axis = k < 0 ? section.Top + 1 : k > steps ? section.Bottom - 1 : section.Top - section.Depth * k / steps;
            double n = 0, m = 0;
            for (var index = 0; index < section.Mid.Length; index++)
            {
                var steel = section.Steel[index] * fyd + section.SteelShear[index] * fydShear;
                var force = section.Mid[index] > axis ? section.Concrete[index] * fc + steel : -steel;
                n += force;
                m += force * section.Mid[index];
            }
            foreach (var bar in section.Bars)
            {
                var force = bar.V > axis ? bar.Area * (fsd - fc) : -bar.Area * fsd;
                n += force;
                m += force * bar.V;
            }
            result.Add((n, m));
        }
        return new InteractionCurve(result);
    }

    /// <summary>EC2 strain-compatibility interaction (6.1): rectangular block η fcd over λx, bars elastic–plastic, strain
    /// limit εcu3 for x ≤ h and pivoting about εc3 beyond.</summary>
    public static InteractionCurve Reinforced(FibreSection section, double fcd, double fck, double fyd, double es)
    {
        var (eta, lambda, ecu3, ec3) = ColumnDesignEngine.BlockFactors(fck);
        var h = section.Depth;
        var fc = eta * fcd;
        var result = new List<(double, double)>
        {
            (-section.BarArea * fyd, 0),
            (section.Concrete.Sum() * fc + section.Bars.Sum(bar => bar.Area * (Math.Min(fyd, es * ec3) - fc)), 0)
        };
        var depths = Enumerable.Range(1, 200).Select(j => h * j / 200.0).Concat(Enumerable.Range(1, 80).Select(j => h * (1 + j * 0.25)));
        foreach (var x in depths)
        {
            var block = Math.Min(lambda * x, h);
            double n = 0, m = 0;
            for (var index = 0; index < section.Mid.Length; index++)
                if (section.Top - section.Mid[index] < block)
                {
                    var force = section.Concrete[index] * fc;
                    n += force;
                    m += force * section.Mid[index];
                }
            foreach (var bar in section.Bars)
            {
                var depth = section.Top - bar.V;
                var strain = x <= h ? ecu3 * (x - depth) / x : ec3 * (x - depth) / (x - (1 - ec3 / ecu3) * h);
                var stress = Math.Clamp(es * strain, -fyd, fyd);
                if (depth < block) stress -= fc;
                n += stress * bar.Area;
                m += stress * bar.Area * bar.V;
            }
            result.Add((n, m));
        }
        return new InteractionCurve(result);
    }
}

// ============================ designers ============================

internal abstract class ColumnDesigner(ColumnDesignBasis basis)
{
    protected ColumnDesignBasis Basis { get; } = basis;
    protected ColumnSectionInput S => Basis.Section;
    protected static string F(double value, int decimals = 1) => ColumnDesignEngine.F(value, decimals);
    protected static string Ok(bool pass) => pass ? "OK" : "NOT OK";

    public abstract DesignCaseResult DesignCase(string name, double nEd, double m0, double vEd, double length, double l0y, double l0z, StringBuilder log);
    public abstract IReadOnlyList<DetailingCheck> Detailing(double maxNEd, double length, double l0y, double l0z);
    public abstract void Describe(StringBuilder report);
    public abstract InteractionDiagram Interaction();

    /// <summary>A diagram from the y-y and z-z curves (N, mm), in kN and kNm; <paramref name="extra"/> curves come first.</summary>
    protected static InteractionDiagram Diagram(InteractionCurve y, InteractionCurve z, string basis, params InteractionSeries[] extra)
    {
        static IReadOnlyList<(double, double)> Kn(InteractionCurve curve) => curve.Points.Select(point => (point.N / 1000, point.M / 1e6)).ToArray();
        return new([.. extra, new("y-y (in-plane)" + (extra.Length > 0 ? ", exact plastic" : ""), Kn(y), extra.Length > 0), new("z-z", Kn(z), Minor: true)],
            y.MaxN / 1000, -y.MinN / 1000, y.MomentAt(0) / 1e6, z.MomentAt(0) / 1e6, basis);
    }

    /// <summary>Bars around a b × h outline: <see cref="ColumnSectionInput.BarsAlongWidth"/> on each b face (corners included)
    /// and <see cref="ColumnSectionInput.BarsAlongDepth"/> on each h face between them.</summary>
    protected List<Bar> PerimeterBars()
    {
        var area = Math.PI * S.BarDiameter * S.BarDiameter / 4;
        var inset = S.Cover + S.LinkDiameter + S.BarDiameter / 2;
        double u = S.Width / 2 - inset, v = S.Depth / 2 - inset;
        var bars = new List<Bar>();
        var across = Math.Max(2, S.BarsAlongWidth);
        for (var index = 0; index < across; index++)
        {
            var position = -u + 2 * u * index / (across - 1);
            bars.Add(new(position, v, area));
            bars.Add(new(position, -v, area));
        }
        for (var index = 1; index <= S.BarsAlongDepth; index++)
        {
            var position = -v + 2 * v * index / (S.BarsAlongDepth + 1);
            bars.Add(new(-u, position, area));
            bars.Add(new(u, position, area));
        }
        return bars;
    }

    protected static double GradeNumber(string grade)
    {
        var digits = new string(grade.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.PositiveInfinity;
    }
}

/// <summary>RC column: SS EN 1992-1-1 6.1 (N–M), 5.8.8 nominal curvature, 6.2 shear, 9.5 detailing.</summary>
internal sealed class RcDesigner : ColumnDesigner
{
    private readonly FibreSection major, minor;
    private readonly InteractionCurve majorCurve, minorCurve;
    private readonly double fcd, fyd;

    public RcDesigner(ColumnDesignBasis basis) : base(basis)
    {
        major = new FibreSection([new Region(Part.Concrete, 1, 0, 0, S.Width, S.Depth)], PerimeterBars());
        minor = major.Rotated();
        fcd = S.AlphaCc * S.Fck / Basis.GammaC;
        fyd = S.Fyk / Basis.GammaS;
        majorCurve = InteractionCurve.Reinforced(major, fcd, S.Fck, fyd, S.Es);
        minorCurve = InteractionCurve.Reinforced(minor, fcd, S.Fck, fyd, S.Es);
    }

    private double Ac => S.Width * S.Depth;
    private double As => major.BarArea;

    public override InteractionDiagram Interaction() =>
        Diagram(majorCurve, minorCurve, $"EN 1992-1-1 6.1 strain compatibility: η fcd block, bars elastic–plastic (fcd = {F(fcd, 1)}, fyd = {F(fyd, 1)} MPa)");
    private bool NominalStiffness => Basis.Method.Contains("stiffness", StringComparison.OrdinalIgnoreCase);

    public override void Describe(StringBuilder r)
    {
        var (eta, lambda, ecu3, _) = ColumnDesignEngine.BlockFactors(S.Fck);
        r.AppendLine("SECTION AND MATERIALS");
        r.AppendLine($"  b × h = {F(S.Width, 0)} × {F(S.Depth, 0)} mm, cover {F(S.Cover, 0)} mm to links Ø{F(S.LinkDiameter, 0)} @ {F(S.LinkSpacing, 0)}");
        r.AppendLine($"  Bars: {major.Bars.Count} Ø{F(S.BarDiameter, 0)} ({S.BarsAlongWidth} per b face, {S.BarsAlongDepth} intermediate per h face) " +
                     $"As = {F(As, 0)} mm² (ρ = {F(As / Ac * 100, 2)} %)");
        r.AppendLine($"  fck = {F(S.Fck, 0)} MPa, fcd = αcc fck / γC = {F(S.AlphaCc, 2)} × {F(S.Fck, 0)} / {F(Basis.GammaC, 2)} = {F(fcd, 2)} MPa (3.1.6)");
        r.AppendLine($"  η = {F(eta, 3)}, λ = {F(lambda, 3)}, εcu3 = {F(ecu3 * 1000, 2)} ‰ (3.1.7, Table 3.1)");
        r.AppendLine($"  fyk = {F(S.Fyk, 0)} MPa, fyd = fyk / γS = {F(fyd, 1)} MPa, Es = {F(S.Es, 0)} MPa");
        r.AppendLine($"  NRd,max (pure compression) = {F(majorCurve.MaxN / 1000, 0)} kN · NRd,t = {F(-majorCurve.MinN / 1000, 0)} kN");
        r.AppendLine($"  MRd,y (N = 0) = {F(majorCurve.MomentAt(0) / 1e6, 1)} kNm · MRd,z (N = 0) = {F(minorCurve.MomentAt(0) / 1e6, 1)} kNm");
    }

    public override DesignCaseResult DesignCase(string name, double nEd, double m0, double vEd, double length, double l0y, double l0z, StringBuilder log)
    {
        log.AppendLine($"Design actions: NEd = {F(nEd / 1000)} kN ({(nEd >= 0 ? "compression" : "tension")}), M0Ed,max = {F(m0 / 1e6)} kNm, VEd = {F(vEd / 1000)} kN");
        var axial = nEd >= 0 ? nEd / majorCurve.MaxN : -nEd / -majorCurve.MinN;
        log.AppendLine($"Axial: NEd / NRd = {F(Math.Abs(nEd) / 1000)} / {F((nEd >= 0 ? majorCurve.MaxN : -majorCurve.MinN) / 1000)} = {F(axial, 3)}");
        log.AppendLine();
        log.AppendLine("MAJOR AXIS y-y (in-plane, FEM moment)");
        var (mEdY, majorUtil) = Axis(nEd, m0, S.Depth, S.Width, l0y, majorCurve, log);
        log.AppendLine();
        log.AppendLine("MINOR AXIS z-z (imperfection only, 5.8.9(2))");
        var (mEdZ, minorUtil) = Axis(nEd, 0, S.Width, S.Depth, l0z, minorCurve, log);
        if (nEd > 0)
        {
            // 5.8.9(4) Eq. 5.39, taken conservatively with the imperfection in both directions
            var ratio = nEd / majorCurve.MaxN;
            var a = ratio <= 0.1 ? 1 : ratio <= 0.7 ? 1 + (ratio - 0.1) / 0.6 * 0.5 : Math.Min(2, 1.5 + (ratio - 0.7) / 0.3 * 0.5);
            var biaxial = Math.Pow(minorUtil, a) + Math.Pow(majorUtil, a);
            log.AppendLine($"  Biaxial (5.8.9(4), Eq. 5.39): NEd/NRd = {F(ratio, 3)} → a = {F(a, 2)}; (MEd,z/MRd,z)^a + (MEd,y/MRd,y)^a = {F(biaxial, 3)} {Ok(biaxial <= 1)}");
            minorUtil = Math.Max(minorUtil, biaxial);
        }
        log.AppendLine();
        var shearUtil = Shear(nEd, vEd, log);
        return new(name, nEd / 1000, m0 / 1e6, mEdY / 1e6, mEdZ / 1e6, vEd / 1000, axial, majorUtil, minorUtil, shearUtil);
    }

    private (double MEd, double Utilization) Axis(double nEd, double m0, double h, double b, double l0, InteractionCurve curve, StringBuilder log)
    {
        double mEd;
        if (nEd > 0)
        {
            var i = h / Math.Sqrt(12);
            var slenderness = l0 / i;
            var n = nEd / (Ac * fcd);
            var limit = 20 * 0.7 * 1.1 * 0.7 / Math.Sqrt(n);
            var ei = l0 / 400;
            var e0 = Math.Max(h / 30, 20);
            var m0Ed = m0 + nEd * ei;
            log.AppendLine($"  λ = l0 / i = {F(l0, 0)} / {F(i, 1)} = {F(slenderness, 1)}; n = NEd / (Ac fcd) = {F(n, 3)}");
            log.AppendLine($"  λlim = 20·A·B·C / √n = 20 × 0.7 × 1.1 × 0.7 / √{F(n, 3)} = {F(limit, 1)} (5.8.3.1, A, B, C default)");
            log.AppendLine($"  ei = l0 / 400 = {F(ei, 1)} mm (5.2(9)); M0Ed = {F(m0 / 1e6)} + NEd·ei = {F(m0Ed / 1e6)} kNm");
            double m2 = 0;
            if (slenderness > limit)
            {
                var d = h - S.Cover - S.LinkDiameter - S.BarDiameter / 2;
                if (NominalStiffness)
                {
                    var (eiNominal, ncr) = NominalStiffnessValues(h, b, l0, nEd);
                    var magnifier = nEd < ncr ? 1 / (1 - nEd / ncr) : double.PositiveInfinity;
                    log.AppendLine($"  Slender → nominal stiffness (5.8.7): EI = Kc Ecd Ic + Ks Es Is = {F(eiNominal / 1e12, 2)} × 10¹² Nmm², NB = π² EI / l0² = {F(ncr / 1000, 0)} kN");
                    log.AppendLine($"  MEd = M0Ed / (1 − NEd/NB) = {F(m0Ed / 1e6)} × {F(magnifier, 3)} (β ≈ 1, 5.8.7.3)");
                    m2 = m0Ed * (magnifier - 1);
                }
                else
                {
                    var omega = As * fyd / (Ac * fcd);
                    var nu = 1 + omega;
                    var kr = Math.Clamp((nu - n) / (nu - 0.4), 0, 1);
                    var beta = 0.35 + S.Fck / 200 - slenderness / 150;
                    var kphi = Math.Max(1, 1 + beta * S.CreepRatio);
                    var eyd = fyd / S.Es;
                    var curvature = kr * kphi * eyd / (0.45 * d);
                    var e2 = curvature * l0 * l0 / 10;
                    m2 = nEd * e2;
                    log.AppendLine($"  Slender (λ > λlim) → nominal curvature (5.8.8): d = {F(d, 0)} mm, ω = {F(omega, 3)}, Kr = {F(kr, 3)}, Kφ = 1 + β φef = {F(kphi, 3)}");
                    log.AppendLine($"  1/r = Kr Kφ εyd / (0.45 d) = {curvature:0.000E+0} /mm; e2 = (1/r) l0² / 10 = {F(e2, 1)} mm; M2 = NEd e2 = {F(m2 / 1e6)} kNm");
                }
            }
            else log.AppendLine("  λ ≤ λlim → second-order effects may be ignored (5.8.2(6))");
            mEd = Math.Max(m0Ed + m2, nEd * e0);
            log.AppendLine($"  e0 = max(h/30, 20) = {F(e0, 1)} mm (6.1(4)); MEd = max(M0Ed + M2, NEd e0) = {F(mEd / 1e6)} kNm");
        }
        else
        {
            mEd = m0;
            log.AppendLine($"  Tension: MEd = M0Ed = {F(mEd / 1e6)} kNm (no second-order effects)");
        }
        var mRd = curve.MomentAt(nEd);
        var utilization = mRd > 0 ? mEd / mRd : double.PositiveInfinity;
        log.AppendLine($"  MRd at NEd (strain compatibility, 6.1) = {F(mRd / 1e6)} kNm → MEd / MRd = {F(utilization, 3)} {Ok(utilization <= 1)}");
        return (mEd, utilization);
    }

    private (double EI, double Ncr) NominalStiffnessValues(double h, double b, double l0, double nEd)
    {
        var ecd = ColumnDesignEngine.Ecm(S.Fck) / 1.2;
        var n = nEd / (Ac * fcd);
        var slenderness = l0 / (h / Math.Sqrt(12));
        var k1 = Math.Sqrt(S.Fck / 20);
        var k2 = Math.Min(0.2, n * slenderness / 170);
        var rho = As / Ac;
        var kc = rho >= 0.002 ? k1 * k2 / (1 + S.CreepRatio) : 0.3 / (1 + 0.5 * S.CreepRatio);
        var ks = rho >= 0.002 ? 1.0 : 0.0;
        var ic = b * h * h * h / 12;
        var section = h == S.Depth ? major : minor;
        var ei = kc * ecd * ic + ks * S.Es * section.BarInertia;
        return (ei, Math.PI * Math.PI * ei / (l0 * l0));
    }

    private double Shear(double nEd, double vEd, StringBuilder log)
    {
        log.AppendLine("SHEAR (6.2, in-plane)");
        var d = S.Depth - S.Cover - S.LinkDiameter - S.BarDiameter / 2;
        var bw = S.Width;
        var k = Math.Min(2, 1 + Math.Sqrt(200 / d));
        var asl = Math.Max(2, S.BarsAlongWidth) * Math.PI * S.BarDiameter * S.BarDiameter / 4;
        var rho = Math.Min(0.02, asl / (bw * d));
        var sigma = Math.Min(nEd / Ac, 0.2 * fcd);
        var vmin = 0.035 * Math.Pow(k, 1.5) * Math.Sqrt(S.Fck);
        var vrdc = Math.Max(0.18 / Basis.GammaC * k * Math.Cbrt(100 * rho * S.Fck) + 0.15 * sigma, vmin + 0.15 * sigma) * bw * d;
        vrdc = Math.Max(0, vrdc);
        var z = 0.9 * d;
        var asw = 2 * Math.PI * S.LinkDiameter * S.LinkDiameter / 4;
        const double cot = 2.5;
        var vrds = asw / S.LinkSpacing * z * (S.Fyk / Basis.GammaS) * cot;
        var nu1 = 0.6 * (1 - S.Fck / 250);
        var ratio = Math.Max(0, sigma) / fcd;
        var alphaCw = ratio <= 0 ? 1 : ratio <= 0.25 ? 1 + ratio : ratio <= 0.5 ? 1.25 : 2.5 * (1 - ratio);
        var vrdmax = alphaCw * bw * z * nu1 * fcd / (cot + 1 / cot);
        log.AppendLine($"  d = {F(d, 0)} mm, k = {F(k, 3)}, ρl = {F(rho, 4)}, σcp = {F(sigma, 2)} MPa");
        log.AppendLine($"  VRd,c = [CRd,c k (100 ρl fck)^⅓ + k1 σcp] bw d ≥ (vmin + k1 σcp) bw d = {F(vrdc / 1000)} kN (6.2.2)");
        log.AppendLine($"  Links 2 legs Ø{F(S.LinkDiameter, 0)} @ {F(S.LinkSpacing, 0)}, cot θ = 2.5: VRd,s = Asw/s z fywd cot θ = {F(vrds / 1000)} kN (6.2.3)");
        log.AppendLine($"  VRd,max = αcw bw z ν1 fcd / (cot θ + tan θ) = {F(vrdmax / 1000)} kN");
        var vrd = vEd <= vrdc ? vrdc : Math.Min(vrds, vrdmax);
        var utilization = vrd > 0 ? vEd / vrd : double.PositiveInfinity;
        log.AppendLine($"  VEd / VRd = {F(vEd / 1000)} / {F(vrd / 1000)} = {F(utilization, 3)} {Ok(utilization <= 1)}" +
                       (vEd <= vrdc ? " (no shear reinforcement needed by calculation)" : ""));
        return utilization;
    }

    public override IReadOnlyList<DetailingCheck> Detailing(double maxNEd, double length, double l0y, double l0z)
    {
        var minimum = Math.Max(0.10 * Math.Max(0, maxNEd) / fyd, 0.002 * Ac);
        var linkMax = Math.Min(Math.Min(20 * S.BarDiameter, Math.Min(S.Width, S.Depth)), 400);
        var linkMin = Math.Max(6, S.BarDiameter / 4);
        var inset = S.Cover + S.LinkDiameter + S.BarDiameter / 2;
        var clearWidth = (S.Width - 2 * inset) / Math.Max(1, Math.Max(2, S.BarsAlongWidth) - 1) - S.BarDiameter;
        var clearDepth = (S.Depth - 2 * inset) / (S.BarsAlongDepth + 1) - S.BarDiameter;
        var clearMin = Math.Max(S.BarDiameter, 20 + 5);   // k1 φ, dg + k2 (dg = 20 mm assumed)
        var maxGrade = GradeNumber(Basis.MaxConcreteGrade);
        return
        [
            new("Minimum longitudinal steel", "9.5.2(2)", As >= minimum, $"As = {F(As, 0)} ≥ max(0.10 NEd/fyd, 0.002 Ac) = {F(minimum, 0)} mm²"),
            new("Maximum longitudinal steel", "9.5.2(3) NA", As <= 0.04 * Ac, $"As = {F(As, 0)} ≤ 0.04 Ac = {F(0.04 * Ac, 0)} mm²"),
            new("Minimum bar diameter", "9.5.2(1) NA", S.BarDiameter >= 12, $"Ø{F(S.BarDiameter, 0)} ≥ Ø12"),
            new("Clear bar spacing", "8.2(2)", Math.Min(clearWidth, clearDepth) >= clearMin, $"{F(Math.Min(clearWidth, clearDepth), 0)} mm ≥ max(φ, dg + 5) = {F(clearMin, 0)} mm"),
            new("Link diameter", "9.5.3(1)", S.LinkDiameter >= linkMin, $"Ø{F(S.LinkDiameter, 0)} ≥ max(6, φ/4) = {F(linkMin, 1)} mm"),
            new("Link spacing", "9.5.3(3)", S.LinkSpacing <= linkMax, $"s = {F(S.LinkSpacing, 0)} ≤ min(20φ, b, 400) = {F(linkMax, 0)} mm"),
            new("Concrete grade in scope", "3.1.2 / design basis", S.Fck <= Math.Min(90, maxGrade), $"fck = {F(S.Fck, 0)} MPa ≤ {Basis.MaxConcreteGrade} (EC2 up to C90/105)"),
        ];
    }
}

/// <summary>Composite column: SS EN 1994-1-1 6.7.3 simplified method with the BCA BC4 high-strength extension.</summary>
internal sealed class CompositeDesigner : ColumnDesigner
{
    private readonly bool filled, circular;
    private readonly FibreSection major, minor;
    private readonly double fcd, fyd, fsd, eta, alphaC, fc;
    private readonly InteractionCurve majorCurve, minorCurve;

    public CompositeDesigner(ColumnDesignBasis basis) : base(basis)
    {
        filled = Basis.Type == ColumnDesignType.ConcreteFilledTube;
        circular = filled && S.Tube == TubeShape.Circular;
        major = new FibreSection(Regions(), filled ? [] : PerimeterBars());
        minor = major.Rotated();
        fcd = S.Fck / Basis.GammaC;
        fyd = S.Fy / Basis.GammaA;
        fsd = S.Fyk / Basis.GammaS;
        eta = ColumnDesignEngine.BlockFactors(S.Fck).Eta;
        alphaC = filled ? 1.0 : 0.85;
        fc = alphaC * eta * fcd;
        majorCurve = InteractionCurve.Plastic(major, fc, fyd, fyd, fsd);
        minorCurve = InteractionCurve.Plastic(minor, fc, fyd, fyd, fsd);
    }

    private List<Region> Regions()
    {
        if (filled && circular)
        {
            double ro = S.TubeDepth / 2, ri = ro - S.TubeThickness;
            return [new(Part.Steel, 1, 0, 0, 0, 0, ro, true), new(Part.Steel, -1, 0, 0, 0, 0, ri, true), new(Part.Concrete, 1, 0, 0, 0, 0, ri)];
        }
        if (filled)
        {
            double h = S.TubeDepth, b = S.TubeWidth, t = S.TubeThickness;
            return
            [
                new(Part.Steel, 1, 0, (h - t) / 2, b, t), new(Part.Steel, 1, 0, -(h - t) / 2, b, t),
                new(Part.Steel, 1, (b - t) / 2, 0, t, h - 2 * t, Shear: true), new(Part.Steel, 1, -(b - t) / 2, 0, t, h - 2 * t, Shear: true),
                new(Part.Concrete, 1, 0, 0, b - 2 * t, h - 2 * t)
            ];
        }
        double hs = S.SteelDepth, bf = S.SteelWidth, tf = S.FlangeThickness, tw = S.WebThickness;
        Region[] steel =
        [
            new(Part.Steel, 1, 0, (hs - tf) / 2, bf, tf), new(Part.Steel, 1, 0, -(hs - tf) / 2, bf, tf),
            new(Part.Steel, 1, 0, 0, tw, hs - 2 * tf, Shear: true)
        ];
        return [new(Part.Concrete, 1, 0, 0, S.Width, S.Depth), .. steel.Select(plate => plate with { Part = Part.Concrete, Sign = -1, Shear = false }), .. steel];
    }

    public override InteractionDiagram Interaction() =>
        Diagram(majorCurve, minorCurve, $"EN 1994-1-1 6.7.3.2 plastic distribution: concrete {F(alphaC, 2)} η fcd = {F(fc, 1)}, fyd = {F(fyd, 1)}" +
                                        (filled ? " MPa" : $", fsd = {F(fsd, 1)} MPa"));

    private double Aa => major.Area(Part.Steel);
    private double Ac => major.ConcreteArea;
    private double As => major.BarArea;
    private double Ecm => ColumnDesignEngine.Ecm(S.Fck);
    private double NplRd => majorCurve.MaxN;
    private double NplRk => Aa * S.Fy + alphaC * eta * S.Fck * Ac + As * S.Fyk;
    private double AlphaM => S.Fy <= 355 ? 0.9 : 0.8;
    private double Dimension => filled ? S.TubeDepth : S.Depth;

    public override void Describe(StringBuilder r)
    {
        r.AppendLine("SECTION AND MATERIALS");
        if (circular) r.AppendLine($"  Circular tube D × t = {F(S.TubeDepth, 0)} × {F(S.TubeThickness, 1)} mm, concrete filled");
        else if (filled) r.AppendLine($"  Rectangular tube H × B × t = {F(S.TubeDepth, 0)} × {F(S.TubeWidth, 0)} × {F(S.TubeThickness, 1)} mm, concrete filled");
        else
        {
            r.AppendLine($"  Concrete b × h = {F(S.Width, 0)} × {F(S.Depth, 0)} mm encasing I {F(S.SteelDepth, 0)} × {F(S.SteelWidth, 0)} × tf {F(S.FlangeThickness, 1)} × tw {F(S.WebThickness, 1)} mm");
            r.AppendLine($"  Bars: {major.Bars.Count} Ø{F(S.BarDiameter, 0)}, links Ø{F(S.LinkDiameter, 0)} @ {F(S.LinkSpacing, 0)}, cover {F(S.Cover, 0)} mm");
        }
        r.AppendLine($"  Aa = {F(Aa, 0)} mm², Ac = {F(Ac, 0)} mm², As = {F(As, 0)} mm²");
        r.AppendLine($"  fck = {F(S.Fck, 0)} MPa, fcd = fck / γC = {F(fcd, 2)} MPa; concrete block {F(alphaC, 2)} × η × fcd, η = {F(eta, 3)}" +
                     (eta < 1 ? " (high-strength concrete, BC4 / EC2 Table 3.1)" : ""));
        r.AppendLine($"  fy = {F(S.Fy, 0)} MPa, fyd = fy / γM0 = {F(fyd, 1)} MPa" + (filled ? "" : $"; fsk = {F(S.Fyk, 0)} MPa, fsd = {F(fsd, 1)} MPa"));
        r.AppendLine($"  Ecm = {F(Ecm, 0)} MPa, Ea = {F(S.Ea, 0)} MPa, φt = {F(S.CreepRatio, 2)}, NG,Ed/NEd = {F(S.PermanentLoadRatio, 2)}");
        r.AppendLine($"  Npl,Rd = {F(NplRd / 1000, 0)} kN, Npl,Rk = {F(NplRk / 1000, 0)} kN (6.7.3.2)");
        r.AppendLine($"  Mpl,y,Rd = {F(majorCurve.MomentAt(0) / 1e6, 1)} kNm, Mpl,z,Rd = {F(minorCurve.MomentAt(0) / 1e6, 1)} kNm (plastic, 6.7.3.2(2))");
    }

    public override DesignCaseResult DesignCase(string name, double nEd, double m0, double vEd, double length, double l0y, double l0z, StringBuilder log)
    {
        log.AppendLine($"Design actions: NEd = {F(nEd / 1000)} kN ({(nEd >= 0 ? "compression" : "tension")}), M0Ed,max = {F(m0 / 1e6)} kNm, VEd = {F(vEd / 1000)} kN");
        var ecEff = Ecm / (1 + S.PermanentLoadRatio * S.CreepRatio);
        log.AppendLine($"Ec,eff = Ecm / (1 + (NG,Ed/NEd) φt) = {F(ecEff, 0)} MPa (6.7.3.3(4))");

        // shear first: a high shear reduces the steel strength in the shear area (6.7.3.2(3))
        var shearUtil = Shear(vEd, out var rho, log);
        var curveY = majorCurve;
        if (rho > 0)
        {
            curveY = InteractionCurve.Plastic(major, fc, fyd, (1 - rho) * fyd, fsd);
            log.AppendLine($"  VEd > 0.5 Vpl,a,Rd → (1 − ρ) fyd = {F((1 - rho) * fyd, 1)} MPa in the shear area for the N–M curve");
        }
        log.AppendLine();

        double axialUtil;
        double majorUtil, minorUtil, mEdY, mEdZ;
        if (nEd > 0)
        {
            log.AppendLine("MEMBER IN COMPRESSION (6.7.3.5)");
            var (chiY, ncrEffY, lambdaY) = Buckling("y-y", major, l0y, ecEff, filled ? 0.21 : 0.34, log);
            var (chiZ, ncrEffZ, lambdaZ) = Buckling("z-z", minor, l0z, ecEff, filled ? 0.21 : 0.49, log);
            var nplRd = NplRd;
            if (circular && Math.Max(lambdaY, lambdaZ) < 0.5)
            {
                var d = S.TubeDepth;
                var e = m0 / nEd;
                if (e / d <= 0.1)
                {
                    var lambdaBar = Math.Max(lambdaY, lambdaZ);
                    var etaA0 = Math.Min(1, 0.25 * (3 + 2 * lambdaBar));
                    var etaC0 = Math.Max(0, 4.9 - 18.5 * lambdaBar + 17 * lambdaBar * lambdaBar);
                    var etaA = etaA0 + (1 - etaA0) * 10 * e / d;
                    var etaC = etaC0 * (1 - 10 * e / d);
                    nplRd = etaA * Aa * fyd + Ac * fcd * (1 + etaC * S.TubeThickness / d * S.Fy / S.Fck) + As * fsd;
                    log.AppendLine($"  Confinement (6.7.3.2(6)): λ̄ < 0.5, e/d = {F(e / d, 3)} → ηa = {F(etaA, 3)}, ηc = {F(etaC, 3)}, Npl,Rd = {F(nplRd / 1000, 0)} kN");
                }
            }
            var chi = Math.Min(chiY, chiZ);
            axialUtil = nEd / (chi * nplRd);
            log.AppendLine($"  NEd / (χmin Npl,Rd) = {F(nEd / 1000)} / ({F(chi, 3)} × {F(nplRd / 1000, 0)}) = {F(axialUtil, 3)} {Ok(axialUtil <= 1)} (6.7.3.5(2))");
            log.AppendLine();
            log.AppendLine("MAJOR AXIS y-y (in-plane, FEM moment) — 6.7.3.4 / 6.7.3.6");
            (mEdY, majorUtil, var kY, var resistanceY) = Bending(nEd, m0, l0y, filled ? 300 : 200, ncrEffY, curveY, log);
            log.AppendLine();
            log.AppendLine("MINOR AXIS z-z (imperfection only)");
            (mEdZ, minorUtil, _, var resistanceZ) = Bending(nEd, 0, l0z, filled ? 300 : 150, ncrEffZ, minorCurve, log);
            // 6.7.3.7(2): imperfection in the z plane (the more critical out-of-plane buckling) with the in-plane moment
            var myNoImperfection = Math.Max(m0, kY * m0);
            var biaxial = myNoImperfection / resistanceY + mEdZ / resistanceZ;
            log.AppendLine($"  Biaxial (6.7.3.7(2)): My,Ed/(μdy Mpl,y,Rd) + Mz,Ed/(μdz Mpl,z,Rd) = {F(myNoImperfection / 1e6)} / {F(resistanceY / 1e6)} + " +
                           $"{F(mEdZ / 1e6)} / {F(resistanceZ / 1e6)} = {F(biaxial, 3)} {Ok(biaxial <= 1)} (My without imperfection)");
            minorUtil = Math.Max(minorUtil, biaxial);
        }
        else
        {
            var ntRd = Aa * fyd + As * fsd;
            axialUtil = -nEd / ntRd;
            log.AppendLine($"MEMBER IN TENSION: NEd / Nt,Rd = {F(-nEd / 1000)} / {F(ntRd / 1000, 0)} = {F(axialUtil, 3)} {Ok(axialUtil <= 1)}");
            mEdY = m0;
            mEdZ = 0;
            var mRd = curveY.MomentAt(nEd);
            majorUtil = mRd > 0 ? m0 / (AlphaM * mRd) : double.PositiveInfinity;
            log.AppendLine($"  Mpl,N,y,Rd at NEd = {F(mRd / 1e6)} kNm → MEd / (αM Mpl,N,Rd) = {F(majorUtil, 3)} {Ok(majorUtil <= 1)}");
            minorUtil = 0;
        }
        return new(name, nEd / 1000, m0 / 1e6, mEdY / 1e6, mEdZ / 1e6, vEd / 1000, axialUtil, majorUtil, minorUtil, shearUtil);
    }

    private (double Chi, double NcrEff, double Lambda) Buckling(string axis, FibreSection section, double l0, double ecEff, double alpha, StringBuilder log)
    {
        var ia = section.Inertia(Part.Steel);
        var ic = section.ConcreteInertia;
        var isBars = section.BarInertia;
        var ei = S.Ea * ia + S.Es * isBars + 0.6 * ecEff * ic;
        var ncr = Math.PI * Math.PI * ei / (l0 * l0);
        var lambda = Math.Sqrt(NplRk / ncr);
        var chi = ColumnDesignEngine.Chi(lambda, alpha);
        var eiII = 0.9 * (S.Ea * ia + S.Es * isBars + 0.5 * ecEff * ic);
        var ncrEff = Math.PI * Math.PI * eiII / (l0 * l0);
        var curve = alpha switch { 0.21 => "a", 0.34 => "b", _ => "c" };
        log.AppendLine($"  {axis}: (EI)eff = Ea Ia + Es Is + 0.6 Ec,eff Ic = {F(ei / 1e12, 2)} × 10¹² Nmm², Ncr = {F(ncr / 1000, 0)} kN, λ̄ = {F(lambda, 3)} (6.7.3.3)");
        log.AppendLine($"        curve {curve} (α = {F(alpha, 2)}, Table 6.5) → χ = {F(chi, 3)}; (EI)eff,II = 0.9(Ea Ia + Es Is + 0.5 Ec,eff Ic) → Ncr,eff = {F(ncrEff / 1000, 0)} kN (6.7.3.4)");
        return (chi, ncrEff, lambda);
    }

    /// <returns>MEd, MEd / (αM μd Mpl,Rd), the amplification k and μd Mpl,Rd.</returns>
    private (double MEd, double Utilization, double K, double Resistance) Bending(double nEd, double m0, double length, double imperfectionRatio, double ncrEff, InteractionCurve curve, StringBuilder log)
    {
        var e0 = length / imperfectionRatio;
        var k = nEd < ncrEff ? Math.Max(1, 1 / (1 - nEd / ncrEff)) : double.PositiveInfinity;
        var mEd = Math.Max(m0, k * (m0 + nEd * e0));
        var mplRd = curve.MomentAt(0);
        var mplN = curve.MomentAt(nEd);
        var mu = mplRd > 0 ? Math.Min(1, mplN / mplRd) : 0;
        var utilization = mu > 0 ? mEd / (AlphaM * mu * mplRd) : double.PositiveInfinity;
        log.AppendLine($"  e0 = l0 / {F(imperfectionRatio, 0)} = {F(e0, 1)} mm (Table 6.5, over the buckling length); k = β / (1 − NEd / Ncr,eff) = {F(k, 3)} (β = 1.0, 6.7.3.4(5))");
        log.AppendLine($"  MEd = k (M0Ed + NEd e0) = {F(k, 3)} × ({F(m0 / 1e6)} + {F(nEd * e0 / 1e6)}) = {F(mEd / 1e6)} kNm");
        log.AppendLine($"  μd = Mpl,N,Rd / Mpl,Rd = {F(mplN / 1e6)} / {F(mplRd / 1e6)} = {F(mu, 3)} (≤ 1.0); αM = {F(AlphaM, 1)} (6.7.3.6(1))");
        log.AppendLine($"  MEd / (αM μd Mpl,Rd) = {F(utilization, 3)} {Ok(utilization <= 1)}");
        return (mEd, utilization, k, mu * mplRd);
    }

    private double Shear(double vEd, out double rho, StringBuilder log)
    {
        double av;
        string source;
        if (circular) { av = 2 * Aa / Math.PI; source = "2A/π (CHS)"; }
        else if (filled) { av = Aa * S.TubeDepth / (S.TubeDepth + S.TubeWidth); source = "A h / (b + h) (RHS)"; }
        else { av = (S.SteelDepth - 2 * S.FlangeThickness) * S.WebThickness; source = "hw tw (web)"; }
        var vpl = av * fyd / Math.Sqrt(3);
        var utilization = vEd / vpl;
        rho = vEd > 0.5 * vpl ? Math.Pow(2 * vEd / vpl - 1, 2) : 0;
        log.AppendLine("SHEAR (6.7.3.2(3)–(4), steel section, EN 1993-1-1 6.2.6)");
        log.AppendLine($"  Av = {source} = {F(av, 0)} mm²; Vpl,a,Rd = Av fyd / √3 = {F(vpl / 1000)} kN");
        log.AppendLine($"  VEd / Vpl,a,Rd = {F(vEd / 1000)} / {F(vpl / 1000)} = {F(utilization, 3)} {Ok(utilization <= 1)}");
        return utilization;
    }

    public override IReadOnlyList<DetailingCheck> Detailing(double maxNEd, double length, double l0y, double l0z)
    {
        var delta = Aa * fyd / NplRd;
        var ecEff = Ecm / (1 + S.PermanentLoadRatio * S.CreepRatio);
        double Slenderness(FibreSection section, double l0) =>
            Math.Sqrt(NplRk / (Math.PI * Math.PI * (S.Ea * section.Inertia(Part.Steel) + S.Es * section.BarInertia + 0.6 * ecEff * section.ConcreteInertia) / (l0 * l0)));
        var lambda = Math.Max(Slenderness(major, l0y), Slenderness(minor, l0z));
        var root = Math.Sqrt(235 / S.Fy);
        var checks = new List<DetailingCheck>
        {
            new("Steel contribution ratio", "6.7.1(4)", delta is >= 0.2 and <= 0.9, $"δ = Aa fyd / Npl,Rd = {F(delta, 3)} within 0.2 … 0.9"),
            new("Relative slenderness", "6.7.3.1(1)", lambda <= 2.0, $"λ̄max = {F(lambda, 3)} ≤ 2.0"),
        };
        if (circular)
            checks.Add(new("Local buckling (CHS)", "6.7.1(9) Table 6.3", S.TubeDepth / S.TubeThickness <= 90 * 235 / S.Fy,
                $"d/t = {F(S.TubeDepth / S.TubeThickness, 1)} ≤ 90 (235/fy) = {F(90 * 235 / S.Fy, 1)}"));
        else if (filled)
            checks.Add(new("Local buckling (RHS)", "6.7.1(9) Table 6.3", Math.Max(S.TubeDepth, S.TubeWidth) / S.TubeThickness <= 52 * root,
                $"h/t = {F(Math.Max(S.TubeDepth, S.TubeWidth) / S.TubeThickness, 1)} ≤ 52 √(235/fy) = {F(52 * root, 1)}"));
        else
        {
            var cz = (S.Depth - S.SteelDepth) / 2;
            var cy = (S.Width - S.SteelWidth) / 2;
            var coverOk = Math.Min(cz, cy) >= Math.Max(40, S.SteelWidth / 6);
            checks.Add(new("Concrete cover to steel section", "6.7.5.1(2)", coverOk,
                $"min(cy, cz) = {F(Math.Min(cy, cz), 0)} ≥ max(40, b/6) = {F(Math.Max(40, S.SteelWidth / 6), 0)} mm (otherwise check flange local buckling)"));
            if (!coverOk)
                checks.Add(new("Flange local buckling", "6.7.1(9) Table 6.3", S.SteelWidth / S.FlangeThickness <= 44 * root,
                    $"b/tf = {F(S.SteelWidth / S.FlangeThickness, 1)} ≤ 44 √(235/fy) = {F(44 * root, 1)}"));
            checks.Add(new("Minimum longitudinal steel", "6.7.5.2(1)", As >= 0.003 * Ac, $"As = {F(As, 0)} ≥ 0.3 % Ac = {F(0.003 * Ac, 0)} mm²"));
            checks.Add(new("Maximum longitudinal steel", "6.7.3.1(3)", As <= 0.06 * Ac, $"As = {F(As, 0)} ≤ 6 % Ac = {F(0.06 * Ac, 0)} mm²"));
        }
        var maxConcrete = GradeNumber(Basis.MaxConcreteGrade);
        var maxSteel = GradeNumber(Basis.MaxSteelGrade);
        checks.Add(new("Concrete grade in scope", "3.1(2) / BC4", S.Fck <= maxConcrete,
            $"fck = {F(S.Fck, 0)} MPa ≤ {Basis.MaxConcreteGrade}" + (S.Fck > 50 ? " — beyond EC4 C50/60, BC4 high-strength extension applied" : "")));
        checks.Add(new("Steel grade in scope", "3.3(2) / BC4", S.Fy <= maxSteel,
            $"fy = {F(S.Fy, 0)} MPa ≤ {Basis.MaxSteelGrade}" + (S.Fy > 460 ? " — beyond EC4 S460, BC4 high-strength extension applied" : "")));
        return checks;
    }
}
