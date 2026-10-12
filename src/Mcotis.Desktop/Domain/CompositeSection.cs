namespace COT_VCF.Domain;

/// <summary>The parts of a composite column section, in m: areas and second moments about the 3-axis (in-plane bending,
/// depth h) and the 2-axis, for the concrete, the structural steel and the reinforcement (bars + links per metre).</summary>
public sealed record CompositeParts(
    double ConcreteArea, double SteelArea, double RebarArea, double LinkAreaPerMetre,
    double ConcreteI33, double SteelI33, double RebarI33,
    double ConcreteI22, double SteelI22, double RebarI22,
    IReadOnlyList<(double Y, double Z, double Diameter)> Bars);

/// <summary>Material quantities and cost per metre of a composite section (from the material unit costs, SGD).</summary>
public sealed record CompositeCost(double ConcreteM3PerM, double SteelTPerM, double RebarTPerM, double? CostPerM, IReadOnlyList<string> Missing);

/// <summary>
/// Concrete filled tube (CFT) and steel encased composite sections, from the Design module's <see cref="ColumnSectionInput"/>
/// (mm, MPa): their parts, the properties transformed to the concrete (A = Ac + (Ea/Ecm)·Aa + (Es/Ecm)·As, likewise I — the
/// same transformation SAP2000 uses for a Section Designer / filled-tube section), the self-weight area and the cost.
/// Depth h / H runs in the plane of the 2D model (bending about the 3-axis).
/// </summary>
public static class CompositeSection
{
    private const double Mm = 0.001;

    public static CompositeParts Parts(FrameShape shape, ColumnSectionInput s) => shape switch
    {
        FrameShape.FilledTube when s.Tube == TubeShape.Circular => CircularTube(s),
        FrameShape.FilledTube => RectangularTube(s),
        FrameShape.Encased => Encased(s),
        FrameShape.MegaComposite => Mega(s),
        _ => throw new ArgumentException("Not a composite section shape.", nameof(shape)),
    };

    /// <summary>One rolled / welded I profile from its plates (root fillets ignored), in mm: area, second moments and plastic
    /// moduli about its strong (depth) and weak axes, and its mass per metre [kg/m] at 7850 kg/m³.</summary>
    public static (double Area, double IStrong, double IWeak, double ZStrong, double ZWeak, double KgPerM) Profile(ColumnSectionInput s)
    {
        double ha = s.SteelDepth, ba = s.SteelWidth, tf = s.FlangeThickness, tw = s.WebThickness, hw = Math.Max(0, ha - 2 * tf);
        var area = 2 * ba * tf + hw * tw;
        return (area, (ba * ha * ha * ha - (ba - tw) * hw * hw * hw) / 12, (2 * tf * ba * ba * ba + hw * tw * tw * tw) / 12,
            ba * tf * (ha - tf) + tw * hw * hw / 4, tf * ba * ba / 2 + hw * tw * tw / 4, area * 7.85e-3);
    }

    /// <summary>Centres (Y across, Z in-plane; mm) of a mega column's profiles: (±dsx, ±dsy), and (±dsx, 0) for 6 profiles.</summary>
    public static IReadOnlyList<(double Y, double Z)> ProfileCentres(ColumnSectionInput s)
    {
        double y = s.ProfileOffsetZ, z = s.ProfileOffsetY;
        List<(double, double)> centres = [(-y, z), (y, z), (-y, -z), (y, -z)];
        if (s.ProfileCount == 6) { centres.Add((-y, 0)); centres.Add((y, 0)); }
        return centres;
    }

    /// <summary>Bars (Y, Z, Ø; mm) around a b × h outline inside the cover and link: <see cref="ColumnSectionInput.BarsAlongWidth"/>
    /// on each b face (corners included), <see cref="ColumnSectionInput.BarsAlongDepth"/> on each h face (corners excluded),
    /// and <paramref name="layers"/> − 1 more b-face layers inward at 2 Ø centres.</summary>
    public static List<(double Y, double Z, double Diameter)> PerimeterBars(ColumnSectionInput s, int layers = 1)
    {
        double zb = s.Depth / 2 - s.Cover - s.LinkDiameter - s.BarDiameter / 2, yb = s.Width / 2 - s.Cover - s.LinkDiameter - s.BarDiameter / 2;
        var bars = new List<(double Y, double Z, double Diameter)>();
        var across = Math.Max(2, s.BarsAlongWidth);
        for (var layer = 0; layer < Math.Max(1, layers); layer++)
        {
            var z = zb - layer * 2 * s.BarDiameter;
            for (var i = 0; i < across; i++)
            {
                var y = -yb + 2 * yb * i / (across - 1);
                bars.Add((y, z, s.BarDiameter)); bars.Add((y, -z, s.BarDiameter));
            }
        }
        for (var i = 1; i <= Math.Max(0, s.BarsAlongDepth); i++)
        {
            var z = -zb + 2 * zb * i / (s.BarsAlongDepth + 1);
            bars.Add((yb, z, s.BarDiameter)); bars.Add((-yb, z, s.BarDiameter));
        }
        return bars;
    }

    private static CompositeParts Mega(ColumnSectionInput s)
    {
        double b = s.Width * Mm, h = s.Depth * Mm;
        var profile = Profile(s);
        var centres = ProfileCentres(s);
        var aa = centres.Count * profile.Area * Mm * Mm;
        var ia33 = centres.Sum(c => profile.IStrong + profile.Area * c.Z * c.Z) * Math.Pow(Mm, 4);
        var ia22 = centres.Sum(c => profile.IWeak + profile.Area * c.Y * c.Y) * Math.Pow(Mm, 4);
        var barsMm = PerimeterBars(s, s.BarLayers);
        var bars = barsMm.Select(bar => (bar.Y * Mm, bar.Z * Mm, bar.Diameter)).ToList();
        double phi = s.BarDiameter * Mm, link = s.LinkDiameter * Mm, cover = s.Cover * Mm;
        var barArea = Math.PI / 4 * phi * phi;
        var ar = bars.Count * barArea;
        var ir33 = bars.Sum(bar => barArea * bar.Item2 * bar.Item2);
        var ir22 = bars.Sum(bar => barArea * bar.Item1 * bar.Item1);
        var linkPerimeter = 2 * (b - 2 * cover - link) + 2 * (h - 2 * cover - link);
        var linkArea = s.LinkSpacing > 0 ? Math.PI / 4 * link * link * linkPerimeter / (s.LinkSpacing * Mm) : 0;
        return new(b * h - aa - ar, aa, ar, linkArea, b * h * h * h / 12 - ia33 - ir33, ia33, ir33, h * b * b * b / 12 - ia22 - ir22, ia22, ir22, bars);
    }

    private static CompositeParts RectangularTube(ColumnSectionInput s)
    {
        double h = s.TubeDepth * Mm, b = s.TubeWidth * Mm, t = s.TubeThickness * Mm;
        double hi = Math.Max(0, h - 2 * t), bi = Math.Max(0, b - 2 * t);
        var ic33 = bi * hi * hi * hi / 12;
        var ic22 = hi * bi * bi * bi / 12;
        return new(bi * hi, b * h - bi * hi, 0, 0, ic33, b * h * h * h / 12 - ic33, 0, ic22, h * b * b * b / 12 - ic22, 0, []);
    }

    private static CompositeParts CircularTube(ColumnSectionInput s)
    {
        double d = s.TubeDepth * Mm, t = s.TubeThickness * Mm, di = Math.Max(0, d - 2 * t);
        var ic = Math.PI / 64 * Math.Pow(di, 4);
        var ia = Math.PI / 64 * Math.Pow(d, 4) - ic;
        var ac = Math.PI / 4 * di * di;
        return new(ac, Math.PI / 4 * d * d - ac, 0, 0, ic, ia, 0, ic, ia, 0, []);
    }

    private static CompositeParts Encased(ColumnSectionInput s)
    {
        double b = s.Width * Mm, h = s.Depth * Mm;
        // encased I section, strong axis in-plane (depth along h)
        double ha = s.SteelDepth * Mm, ba = s.SteelWidth * Mm, tf = s.FlangeThickness * Mm, tw = s.WebThickness * Mm;
        var aa = 2 * ba * tf + Math.Max(0, ha - 2 * tf) * tw;
        var ia33 = (ba * ha * ha * ha - (ba - tw) * Math.Pow(Math.Max(0, ha - 2 * tf), 3)) / 12;
        var ia22 = (2 * tf * ba * ba * ba + Math.Max(0, ha - 2 * tf) * tw * tw * tw) / 12;
        // bars: on each b face (corners included) and each h face (corners excluded), inside the cover and the link
        double phi = s.BarDiameter * Mm, link = s.LinkDiameter * Mm, cover = s.Cover * Mm;
        var bars = PerimeterBars(s).Select(bar => (bar.Y * Mm, bar.Z * Mm, bar.Diameter)).ToList();
        var barArea = Math.PI / 4 * phi * phi;
        var ar = bars.Count * barArea;
        var ir33 = bars.Sum(bar => barArea * bar.Item2 * bar.Item2);
        var ir22 = bars.Sum(bar => barArea * bar.Item1 * bar.Item1);
        // links: one closed link around the bars per spacing
        var linkPerimeter = 2 * (b - 2 * cover - link) + 2 * (h - 2 * cover - link);
        var linkArea = s.LinkSpacing > 0 ? Math.PI / 4 * link * link * linkPerimeter / (s.LinkSpacing * Mm) : 0;
        var ac = b * h - aa - ar;
        return new(ac, aa, ar, linkArea, b * h * h * h / 12 - ia33 - ir33, ia33, ir33, h * b * b * b / 12 - ia22 - ir22, ia22, ir22, bars);
    }

    /// <summary>Properties transformed to the concrete: E = <paramref name="ecm"/> [kN/m²] with the steel and rebar moduli
    /// <paramref name="ea"/> / <paramref name="es"/> [kN/m²].</summary>
    public static SectionValues Transformed(CompositeParts p, double ecm, double ea, double es)
    {
        if (!(ecm > 0)) return new SectionValues(0, 0, 0, 0, 0, 0);
        double na = ea / ecm, ns = es / ecm;
        var area = p.ConcreteArea + na * p.SteelArea + ns * p.RebarArea;
        var i33 = p.ConcreteI33 + na * p.SteelI33 + ns * p.RebarI33;
        var i22 = p.ConcreteI22 + na * p.SteelI22 + ns * p.RebarI22;
        return new SectionValues(area, i33, i22, i33 + i22, 5.0 / 6 * area, 5.0 / 6 * area);
    }

    /// <summary>The area that, times the concrete's unit weight, gives the section's true self-weight (concrete + steel +
    /// reinforcement each with its own unit weight) — what the FEM multiplies by γ.</summary>
    public static double SelfWeightArea(CompositeParts p, double gammaConcrete, double gammaSteel, double gammaRebar) =>
        gammaConcrete > 0 ? p.ConcreteArea + (p.SteelArea * gammaSteel + (p.RebarArea + p.LinkAreaPerMetre) * gammaRebar) / gammaConcrete : p.ConcreteArea;

    /// <summary>Concrete volume, steel and rebar mass per metre and their cost from the materials' unit costs.</summary>
    public static CompositeCost Cost(CompositeParts p, MaterialProperty? concrete, MaterialProperty? steel, MaterialProperty? rebar)
    {
        var missing = new List<string>();
        double? total = 0;
        var steelT = p.SteelArea * (steel?.MassPerVolume ?? 7.849);
        var rebarT = (p.RebarArea + p.LinkAreaPerMetre) * (rebar?.MassPerVolume ?? 7.849);
        void Add(double quantity, double? rate, string what)
        {
            if (quantity <= 0) return;
            if (rate is { } r) total += quantity * r; else { total = null; missing.Add(what); }
        }
        Add(p.ConcreteArea, concrete?.UnitCostPerCubicMetre, "concrete");
        Add(steelT, steel?.UnitCost, "steel");
        Add(rebarT, rebar?.UnitCost, "reinforcement");
        return new CompositeCost(p.ConcreteArea, steelT, rebarT, missing.Count == 0 ? total : null, missing);
    }
}
