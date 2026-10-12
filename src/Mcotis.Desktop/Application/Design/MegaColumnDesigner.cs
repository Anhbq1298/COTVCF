using System.Text;
using COT_VCF.Domain;

namespace COT_VCF.Application.Design;

/// <summary>
/// Mega composite column (several concrete-encased I profiles): the major (in-plane) axis by the CTBUH / ArcelorMittal
/// simplified method (<see cref="MegaColumnEngine"/>: equivalent rebar plates and profile rectangles in the EN 1994-1-1
/// §6.7.3 plastic distribution, the A–C–D–B polygon, buckling curve b, second-order amplification); the minor axis, which
/// the method does not cover, by the exact plastic distribution of the fibre section (buckling curve c, imperfection
/// moment, biaxial 6.7.3.7). Member imperfections e0 = l0/200 (y) and l0/150 (z) are added to the FEM moment (Table 6.5),
/// and μd is limited to 1.0 (6.7.3.6(1)). Units inside: N, mm, MPa; the engine works in kN.
/// </summary>
internal sealed class MegaDesigner : ColumnDesigner
{
    private readonly FibreSection major, minor;
    private readonly InteractionCurve majorCurve, minorCurve;
    private readonly double fcd, fyd, fsd, eta, fc;
    private readonly (double Area, double IStrong, double IWeak, double ZStrong, double ZWeak, double KgPerM) profile;
    private readonly int count;

    public MegaDesigner(ColumnDesignBasis basis) : base(basis)
    {
        profile = CompositeSection.Profile(S);
        count = S.ProfileCount == 6 ? 6 : 4;
        var bars = CompositeSection.PerimeterBars(S, S.BarLayers);
        var barArea = Math.PI * S.BarDiameter * S.BarDiameter / 4;
        major = new FibreSection(Regions(), bars.Select(bar => new Bar(bar.Y, bar.Z, barArea)).ToList());
        minor = major.Rotated();
        fcd = S.Fck / Basis.GammaC;
        fyd = S.Fy / Basis.GammaA;
        fsd = S.Fyk / Basis.GammaS;
        eta = ColumnDesignEngine.BlockFactors(S.Fck).Eta;
        fc = 0.85 * eta * fcd;
        majorCurve = InteractionCurve.Plastic(major, fc, fyd, fyd, fsd);
        minorCurve = InteractionCurve.Plastic(minor, fc, fyd, fyd, fsd);
    }

    private List<Region> Regions()
    {
        double hs = S.SteelDepth, bf = S.SteelWidth, tf = S.FlangeThickness, tw = S.WebThickness;
        var steel = CompositeSection.ProfileCentres(S).SelectMany(c => new Region[]
        {
            new(Part.Steel, 1, c.Y, c.Z + (hs - tf) / 2, bf, tf), new(Part.Steel, 1, c.Y, c.Z - (hs - tf) / 2, bf, tf),
            new(Part.Steel, 1, c.Y, c.Z, tw, hs - 2 * tf, Shear: true),
        }).ToList();
        return [new(Part.Concrete, 1, 0, 0, S.Width, S.Depth), .. steel.Select(plate => plate with { Part = Part.Concrete, Sign = -1, Shear = false }), .. steel];
    }

    private double Inset => S.Cover + S.LinkDiameter + S.BarDiameter / 2;
    private double Ecm => ColumnDesignEngine.Ecm(S.Fck);
    private double AlphaM => S.Fy <= 355 ? 0.9 : 0.8;
    private double Aa => major.Area(Part.Steel);
    private double As => major.BarArea;
    private double Ac => major.ConcreteArea;
    private double NplRk => Aa * S.Fy + 0.85 * eta * S.Fck * Ac + As * S.Fyk;
    private double ShearArea => count * (S.SteelDepth - 2 * S.FlangeThickness) * S.WebThickness;

    /// <summary>The CTBUH input for this section (in-plane bending), NEd in kN, MEd in kNm, buckling length in mm.</summary>
    internal MegaColumnInput EngineInput(double nEdKN, double mEdKNm, double l0, double fy)
    {
        double zb = S.Depth / 2 - Inset, yb = S.Width / 2 - Inset;
        var layers = Math.Max(1, S.BarLayers);
        return new MegaColumnInput
        {
            H1 = S.Width, H2 = S.Depth, Arrangement = count == 6 ? ProfileArrangement.Six : ProfileArrangement.Four,
            Aa = profile.Area, D = S.SteelDepth, Ix = profile.IStrong, Zx = profile.ZStrong, Dsy = S.ProfileOffsetY,
            I = layers, Nx = Math.Max(2, S.BarsAlongWidth), Sx = 2 * yb / (Math.Max(2, S.BarsAlongWidth) - 1), Ds1y = zb - (layers - 1) * S.BarDiameter,
            Ny = S.BarsAlongDepth, Sy = 2 * zb / (S.BarsAlongDepth + 1), Ds2x = yb, Db = S.BarDiameter,
            Fck = S.Fck, Fy = fy, Fsy = S.Fyk, GammaC = Basis.GammaC, GammaA = Basis.GammaA, GammaS = Basis.GammaS, Eta = eta,
            Ecm = Ecm, Ea = S.Ea, Es = S.Es, NEd = nEdKN, MEdx = mEdKNm, NGratio = S.PermanentLoadRatio, PhiT = S.CreepRatio, L = l0,
        };
    }

    /// <summary>The section-level results (anchors, plates, stiffness) — NEd only matters for the member checks.</summary>
    private MegaColumnResult? Section(double l0) => MegaColumnEngine.Calculate(EngineInput(1, 0, Math.Max(l0, 1), S.Fy));

    /// <summary>y-y: the CTBUH A–C–D–B polygon (closed to −Nt,Rd in tension as the design does) and, dashed, the exact plastic
    /// curve; z-z: the exact plastic curve.</summary>
    public override InteractionDiagram Interaction()
    {
        var basis = $"CTBUH 2016 §4 simplified method (A–C–D–B), 0.85 η fcd = {F(fc, 1)}, fyd = {F(fyd, 1)}, fsd = {F(fsd, 1)} MPa";
        if (Section(1) is not { } m) return Diagram(majorCurve, minorCurve, basis);
        var ntRd = (Aa * fyd + As * fsd) / 1000;
        IReadOnlyList<(double, double)> polygon = [(-ntRd, 0), .. m.Points.OrderBy(point => point.N).Select(point => (point.N, point.M))];
        var diagram = Diagram(majorCurve, minorCurve, basis, new InteractionSeries("y-y (in-plane), CTBUH A–C–D–B", polygon));
        return diagram with { NplRdKN = m.NplRd, NtRdKN = ntRd, MplYRdKNm = m.MplRd };
    }

    public override void Describe(StringBuilder r)
    {
        r.AppendLine("SECTION AND MATERIALS");
        r.AppendLine($"  Concrete b × h = {F(S.Width, 0)} × {F(S.Depth, 0)} mm, cover {F(S.Cover, 0)} mm, links Ø{F(S.LinkDiameter, 0)} @ {F(S.LinkSpacing, 0)}");
        r.AppendLine($"  {count} × {(S.ProfileName.Length > 0 ? S.ProfileName + " — " : "")}I {F(S.SteelDepth, 0)} × {F(S.SteelWidth, 0)} × tf {F(S.FlangeThickness, 1)} × tw {F(S.WebThickness, 1)} mm " +
                     $"({F(profile.KgPerM, 0)} kg/m each, fillets excluded) at dsy = ±{F(S.ProfileOffsetY, 0)}, dsx = ±{F(S.ProfileOffsetZ, 0)} mm" + (count == 6 ? " and two on the axis" : ""));
        r.AppendLine($"  One profile: Aa = {F(profile.Area, 0)} mm², Ix = {F(profile.IStrong / 1e6, 1)} × 10⁶ mm⁴, Zx = {F(profile.ZStrong / 1e3, 0)} × 10³ mm³");
        r.AppendLine($"  Bars: {major.Bars.Count} Ø{F(S.BarDiameter, 0)} — {S.BarsAlongWidth} per b face × {Math.Max(1, S.BarLayers)} layer(s), {S.BarsAlongDepth} per h face between");
        r.AppendLine($"  Aa = {F(Aa, 0)} mm², As = {F(As, 0)} mm² (ρs = {F(100 * As / Ac, 2)} %), Ac = {F(Ac, 0)} mm²");
        r.AppendLine($"  fck = {F(S.Fck, 0)} MPa, fcd = {F(fcd, 2)} MPa, concrete 0.85 η fcd with η = {F(eta, 3)}" + (eta < 1 ? " (EC2 Table 3.1 / BC4, high-strength concrete)" : ""));
        r.AppendLine($"  fy = {F(S.Fy, 0)} MPa, fyd = {F(fyd, 1)} MPa; fsk = {F(S.Fyk, 0)} MPa, fsd = {F(fsd, 1)} MPa; Ecm = {F(Ecm, 0)} MPa, Ea = {F(S.Ea, 0)}, Es = {F(S.Es, 0)} MPa");
        if (Section(1) is not { } m) { r.AppendLine("  The CTBUH method cannot be applied to this section (check the geometry and the bars: at least 2 per face line)."); return; }
        r.AppendLine();
        r.AppendLine("CTBUH SIMPLIFIED METHOD — EQUIVALENT SECTION (Tables 11–13)");
        r.AppendLine($"  Flange layers: As1 = i nx Asri = {F(m.As1, 0)} mm² as a plate hs1 × bs1 = {F(m.Hs1, 0)} × {F(m.Bs1, 2)} mm at ds1y = {F(EngineInput(1, 0, 1, S.Fy).Ds1y, 0)} mm");
        r.AppendLine($"  Web lines: As2 = ny Asri = {F(m.As2, 0)} mm² as a plate hs2 × bs2 = {F(m.Hs2, 0)} × {F(m.Bs2, 2)} mm; Asr = {F(m.Asr, 0)} mm², Isr = {F(m.Isr / 1e9, 2)} × 10⁹ mm⁴");
        r.AppendLine($"  Profiles: d* = {F(m.DStar, 0)} mm, b* = Aa / d* = {F(m.BStar, 1)} mm; As = {F(m.As, 0)} mm², Zsx = {F(m.Zsx / 1e6, 1)} × 10⁶ mm³, Isx = {F(m.Isx / 1e9, 1)} × 10⁹ mm⁴");
        r.AppendLine($"  Ag = {F(m.Ag, 0)} mm², Ac = {F(m.Ac, 0)} mm², Ic = {F(m.Ic / 1e9, 1)} × 10⁹ mm⁴");
        r.AppendLine("INTERACTION POLYGON (Table 14, 15 / 17)");
        r.AppendLine($"  A: Npl,Rd = {F(m.NplRd, 0)} kN · C: Npm,Rd = {F(m.NpmRd, 0)} kN, Mpl,Rd · D: ½ Npm,Rd, Mmax,Rd = {F(m.MmaxRd, 0)} kNm · B: N = 0, Mpl,Rd");
        r.AppendLine($"  Zcx = {F(m.Zcx / 1e6, 1)} × 10⁶ mm³; {m.Hn.CaseLabel}: hnx = {F(m.Hn.Hnx, 1)} mm → Mpl,Rd = {F(m.MplRd, 0)} kNm" +
                     (m.Hn.MplRdAsPrinted is { } printed ? $" (literal Table 15 case 2 print: {F(printed, 0)} kNm)" : ""));
        var exact = majorCurve.MomentAt(0) / 1e6;
        r.AppendLine($"  Cross-check, exact plastic distribution of the fibre section: Mpl,y,Rd = {F(exact, 0)} kNm; method / exact = {F(m.MplRd / exact, 3)}" +
                     (m.MplRd < exact ? " (the method is on the safe side)" : ""));
        r.AppendLine($"  Minor axis (fibre section): Mpl,z,Rd = {F(minorCurve.MomentAt(0) / 1e6, 0)} kNm, Npl,Rd = {F(minorCurve.MaxN / 1000, 0)} kN");
        r.AppendLine($"  δ = As fyd / Npl,Rd = {F(m.Delta, 3)}, Npl,Rk = {F(m.NplRk, 0)} kN, AISC C1 = {F(m.C1, 3)} (for comparison)");
    }

    public override DesignCaseResult DesignCase(string name, double nEd, double m0, double vEd, double length, double l0y, double l0z, StringBuilder log)
    {
        log.AppendLine($"Design actions: NEd = {F(nEd / 1000)} kN ({(nEd >= 0 ? "compression" : "tension")}), M0Ed,max = {F(m0 / 1e6)} kNm, VEd = {F(vEd / 1000)} kN");
        // shear on the profile webs (6.7.3.2(3)–(4)); a high shear reduces the steel strength — taken on the web share of the profiles
        var vpl = ShearArea * fyd / Math.Sqrt(3);
        var shearUtil = vEd / vpl;
        var rho = vEd > 0.5 * vpl ? Math.Pow(2 * vEd / vpl - 1, 2) : 0;
        var fy = S.Fy * (1 - rho * ShearArea / Math.Max(1, Aa));
        log.AppendLine("SHEAR (6.7.3.2(3)–(4), profile webs, EN 1993-1-1 6.2.6)");
        log.AppendLine($"  Av = {count} hw tw = {F(ShearArea, 0)} mm²; Vpl,a,Rd = Av fyd / √3 = {F(vpl / 1000)} kN; VEd / Vpl,a,Rd = {F(shearUtil, 3)} {Ok(shearUtil <= 1)}");
        if (rho > 0) log.AppendLine($"  VEd > 0.5 Vpl,a,Rd → (1 − ρ) on the webs: fy,eff = {F(fy, 1)} MPa for the whole profile area (conservative)");
        log.AppendLine();

        var ecEff = Ecm / (1 + S.PermanentLoadRatio * S.CreepRatio);
        if (nEd <= 0)
        {
            var section = MegaColumnEngine.Calculate(EngineInput(1, 0, Math.Max(l0y, 1), fy));
            var ntRd = Aa * fy / Basis.GammaA + As * fsd;
            var axial = -nEd / ntRd;
            var mpl = section?.MplRd * 1e6 ?? majorCurve.MomentAt(0);
            var mRd = mpl * Math.Max(0, 1 + nEd / ntRd);
            var bending = mRd > 0 ? m0 / (AlphaM * mRd) : double.PositiveInfinity;
            log.AppendLine($"MEMBER IN TENSION: NEd / Nt,Rd = {F(-nEd / 1000)} / {F(ntRd / 1000, 0)} = {F(axial, 3)} {Ok(axial <= 1)}");
            log.AppendLine($"  M,Rd on the line B (0, Mpl,Rd) → (−Nt,Rd, 0) = {F(mRd / 1e6)} kNm → MEd / (αM M,Rd) = {F(bending, 3)} {Ok(bending <= 1)}");
            return new(name, nEd / 1000, m0 / 1e6, m0 / 1e6, 0, vEd / 1000, axial, bending, 0, shearUtil);
        }

        // ---- major axis y-y: CTBUH simplified method ----
        var e0y = l0y / 200;
        var m1 = m0 + nEd * e0y;
        log.AppendLine("MAJOR AXIS y-y (in-plane, FEM moment) — CTBUH simplified method, EN 1994-1-1 6.7.3.3–6.7.3.6");
        log.AppendLine($"  e0 = l0,y / 200 = {F(e0y, 1)} mm (Table 6.5, curve b) → first-order MEd = M0Ed + NEd e0 = {F(m0 / 1e6)} + {F(nEd * e0y / 1e6)} = {F(m1 / 1e6)} kNm");
        if (MegaColumnEngine.Calculate(EngineInput(nEd / 1000, m1 / 1e6, l0y, fy)) is not { } y)
        {
            log.AppendLine("  The CTBUH method cannot be applied to this section (check the geometry and at least 2 bars per face line).");
            return new(name, nEd / 1000, m0 / 1e6, double.PositiveInfinity, 0, vEd / 1000, double.PositiveInfinity, double.PositiveInfinity, 0, shearUtil);
        }
        log.AppendLine($"  Ec,eff = {F(y.EcEff, 0)} MPa; (EI)eff = Ea Isx + Es Isr + 0.6 Ec,eff Ic = {F(y.EIeff / 1e12, 1)} × 10¹² Nmm²; Ncr = {F(y.Ncr, 0)} kN");
        log.AppendLine($"  λ̄ = √(Npl,Rk / Ncr) = {F(y.LambdaBar, 3)} → χy = {F(y.Chi, 3)} (curve b); (EI)eff,II → Ncr,eff = {F(y.NcrEff, 0)} kN, k = {F(y.KAmp, 3)}");
        var muRaw = y.MplRd > 0 ? MegaColumnEngine.MomentAtN(y.Points, nEd / 1000) / y.MplRd : 0;
        var muY = Math.Min(1, muRaw);
        var resistanceY = muY * y.MplRd * 1e6;
        var mEdY = y.MEdII * 1e6;
        var majorUtil = resistanceY > 0 ? mEdY / (AlphaM * resistanceY) : double.PositiveInfinity;
        log.AppendLine($"  MEd = k MEd,1 = {F(y.MEdII)} kNm; polygon A–C–D–B at NEd: M = {F(muRaw * y.MplRd)} kNm, μd = {F(muRaw, 3)}" +
                       (muRaw > 1 ? " → limited to 1.0 (6.7.3.6(1))" : ""));
        log.AppendLine($"  MEd / (αM μd Mpl,y,Rd) = {F(y.MEdII)} / ({F(AlphaM, 1)} × {F(muY, 3)} × {F(y.MplRd, 0)}) = {F(majorUtil, 3)} {Ok(majorUtil <= 1)}");
        log.AppendLine();

        // ---- minor axis z-z: fibre section, imperfection only ----
        var iaZ = minor.Inertia(Part.Steel);
        var eiZ = S.Ea * iaZ + S.Es * minor.BarInertia + 0.6 * ecEff * minor.ConcreteInertia;
        var ncrZ = Math.PI * Math.PI * eiZ / (l0z * l0z);
        var lambdaZ = Math.Sqrt(NplRk / ncrZ);
        var chiZ = ColumnDesignEngine.Chi(lambdaZ, 0.49);
        var ncrEffZ = Math.PI * Math.PI * 0.9 * (S.Ea * iaZ + S.Es * minor.BarInertia + 0.5 * ecEff * minor.ConcreteInertia) / (l0z * l0z);
        var kZ = nEd < ncrEffZ ? Math.Max(1, 1 / (1 - nEd / ncrEffZ)) : double.PositiveInfinity;
        var e0z = l0z / 150;
        var mEdZ = kZ * nEd * e0z;
        var mplZ = minorCurve.MomentAt(0);
        var muZ = mplZ > 0 ? Math.Min(1, minorCurve.MomentAt(nEd) / mplZ) : 0;
        var minorUtil = muZ > 0 ? mEdZ / (AlphaM * muZ * mplZ) : double.PositiveInfinity;
        log.AppendLine("MINOR AXIS z-z (imperfection only; exact plastic distribution of the fibre section)");
        log.AppendLine($"  (EI)eff,z = {F(eiZ / 1e12, 1)} × 10¹² Nmm², Ncr,z = {F(ncrZ / 1000, 0)} kN, λ̄z = {F(lambdaZ, 3)} → χz = {F(chiZ, 3)} (curve c, α = 0.49)");
        log.AppendLine($"  e0 = l0,z / 150 = {F(e0z, 1)} mm; k = {F(kZ, 3)}; MEd,z = k NEd e0 = {F(mEdZ / 1e6)} kNm; μd,z = {F(muZ, 3)}, Mpl,z,Rd = {F(mplZ / 1e6, 0)} kNm");
        log.AppendLine($"  MEd,z / (αM μd Mpl,z,Rd) = {F(minorUtil, 3)} {Ok(minorUtil <= 1)}");
        var myNoImperfection = Math.Max(m0, y.KAmp * m0);
        var biaxial = myNoImperfection / resistanceY + mEdZ / (muZ * mplZ);
        log.AppendLine($"  Biaxial (6.7.3.7(2)): My,Ed/(μdy Mpl,y,Rd) + Mz,Ed/(μdz Mpl,z,Rd) = {F(myNoImperfection / 1e6)} / {F(resistanceY / 1e6)} + " +
                       $"{F(mEdZ / 1e6)} / {F(muZ * mplZ / 1e6)} = {F(biaxial, 3)} {Ok(biaxial <= 1)} (My without imperfection)");
        minorUtil = Math.Max(minorUtil, biaxial);
        log.AppendLine();

        var chi = Math.Min(y.Chi, chiZ);
        var axialUtil = nEd / 1000 / (chi * y.NplRd);
        log.AppendLine("MEMBER IN COMPRESSION (6.7.3.5)");
        log.AppendLine($"  NEd / (χmin Npl,Rd) = {F(nEd / 1000)} / ({F(chi, 3)} × {F(y.NplRd, 0)}) = {F(axialUtil, 3)} {Ok(axialUtil <= 1)}  (squash NEd / Npl,Rd = {F(nEd / 1000 / y.NplRd, 3)})");
        foreach (var warning in y.Warnings) log.AppendLine("  ⚠ " + warning);
        return new(name, nEd / 1000, m0 / 1e6, mEdY / 1e6, mEdZ / 1e6, vEd / 1000, axialUtil, majorUtil, minorUtil, shearUtil);
    }

    public override IReadOnlyList<DetailingCheck> Detailing(double maxNEd, double length, double l0y, double l0z)
    {
        var checks = new List<DetailingCheck>();
        if (Section(l0y) is not { } m)
        {
            checks.Add(new("CTBUH method applicable", "Report §4.2", false, "The section cannot be reduced to plates and rectangles — check the geometry and the bars."));
            return checks;
        }
        var ecEff = Ecm / (1 + S.PermanentLoadRatio * S.CreepRatio);
        var lambdaZ = Math.Sqrt(NplRk / (Math.PI * Math.PI * (S.Ea * minor.Inertia(Part.Steel) + S.Es * minor.BarInertia + 0.6 * ecEff * minor.ConcreteInertia) / (l0z * l0z)));
        var lambda = Math.Max(m.LambdaBar, lambdaZ);
        checks.Add(new("Steel contribution ratio", "6.7.1(4)", m.Delta is >= 0.2 and <= 0.9, $"δ = As fyd / Npl,Rd = {F(m.Delta, 3)} within 0.2 … 0.9"));
        checks.Add(new("Relative slenderness", "6.7.3.1(1)", lambda <= 2.0, $"λ̄max = {F(lambda, 3)} ≤ 2.0 (y: {F(m.LambdaBar, 3)}, z: {F(lambdaZ, 3)})"));
        checks.Add(new("Web rebar simplification", "CTBUH §4.2.3", m.WebN >= 5,
            $"n = (ny − 1)/2 = {F(m.WebN, 1)} ≥ 5 (≥ 11 bars per h face); plate error on the bar inertia 1/(2n) = {F(100 * m.WebInertiaError, 1)} %"));
        var cz = S.Depth / 2 - (S.ProfileOffsetY + S.SteelDepth / 2);
        var cy = S.Width / 2 - (S.ProfileOffsetZ + S.SteelWidth / 2);
        var needed = Math.Max(40, S.SteelWidth / 6);
        checks.Add(new("Concrete cover to the profiles", "6.7.5.1(2)", Math.Min(cy, cz) >= needed,
            $"min(cy, cz) = {F(Math.Min(cy, cz), 0)} ≥ max(40, b/6) = {F(needed, 0)} mm"));
        var gapZ = count == 6 ? S.ProfileOffsetY - S.SteelDepth : 2 * S.ProfileOffsetY - S.SteelDepth;
        var gapY = 2 * S.ProfileOffsetZ - S.SteelWidth;
        checks.Add(new("Profiles clear of each other", "geometry", Math.Min(gapY, gapZ) > 0,
            $"clear gap between profiles: across {F(gapY, 0)} mm, in-plane {F(gapZ, 0)} mm"));
        var barFace = Math.Min(S.Depth / 2 - Inset - S.BarDiameter / 2 - (2 * Math.Max(1, S.BarLayers) - 2) * S.BarDiameter - (S.ProfileOffsetY + S.SteelDepth / 2),
            S.Width / 2 - Inset - S.BarDiameter / 2 - (S.ProfileOffsetZ + S.SteelWidth / 2));
        checks.Add(new("Profiles inside the bar cage", "geometry", barFace >= 0, $"clear distance from the bars to the profiles = {F(barFace, 0)} mm"));
        checks.Add(new("Minimum longitudinal steel", "6.7.5.2(1)", As >= 0.003 * Ac, $"As = {F(As, 0)} ≥ 0.3 % Ac = {F(0.003 * Ac, 0)} mm²"));
        checks.Add(new("Maximum longitudinal steel", "6.7.3.1(3)", As <= 0.06 * Ac, $"As = {F(As, 0)} ≤ 6 % Ac = {F(0.06 * Ac, 0)} mm²"));
        foreach (var warning in m.Warnings) checks.Add(new("CTBUH method warning", "Report §4.2", false, warning));
        var maxConcrete = GradeNumber(Basis.MaxConcreteGrade);
        var maxSteel = GradeNumber(Basis.MaxSteelGrade);
        checks.Add(new("Concrete grade in scope", "3.1(2) / BC4", S.Fck <= maxConcrete,
            $"fck = {F(S.Fck, 0)} MPa ≤ {Basis.MaxConcreteGrade}" + (S.Fck > 50 ? " — beyond EC4 C50/60, η of EC2 Table 3.1 applied (BC4)" : "")));
        checks.Add(new("Steel grade in scope", "3.3(2) / BC4", S.Fy <= maxSteel,
            $"fy = {F(S.Fy, 0)} MPa ≤ {Basis.MaxSteelGrade}" + (S.Fy > 460 ? " — beyond EC4 S460, BC4 high-strength extension" : "")));
        return checks;
    }
}
