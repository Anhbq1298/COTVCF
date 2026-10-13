namespace Mcotis.Desktop.Application.Design;

/// <summary>Four profiles at (±dsx, ±dsy), or six with two more on the bending axis (CTBUH Tables 15 / 17).</summary>
public enum ProfileArrangement { Four, Six }

/// <summary>
/// Input of the megacolumn simplified method, in N, mm, MPa (forces in kN, kNm). Bending is about x-x: h1 is the section
/// dimension parallel to x-x, h2 the bending depth. Ported from <c>mega-column/engine.ts</c> (MegaColumn2Input).
/// </summary>
public sealed record MegaColumnInput
{
    public double H1 { get; init; }
    public double H2 { get; init; }
    public ProfileArrangement Arrangement { get; init; } = ProfileArrangement.Four;
    /// <summary>Area of one profile [mm²].</summary>
    public double Aa { get; init; }
    /// <summary>Depth of one profile [mm].</summary>
    public double D { get; init; }
    /// <summary>Second moment of area of one profile about its own x axis [mm⁴].</summary>
    public double Ix { get; init; }
    /// <summary>Plastic modulus of one profile about its own x axis [mm³].</summary>
    public double Zx { get; init; }
    /// <summary>Section centroid to an outer profile centroid, y direction [mm].</summary>
    public double Dsy { get; init; }
    /// <summary>Horizontal (flange) rebar layers on the top face, mirrored on the bottom.</summary>
    public int I { get; init; } = 1;
    /// <summary>Bars on one horizontal layer and their spacing [mm].</summary>
    public int Nx { get; init; }
    public double Sx { get; init; }
    /// <summary>Mean distance from the centroid to the horizontal layers [mm].</summary>
    public double Ds1y { get; init; }
    /// <summary>Bars on one vertical (web) line and their spacing [mm].</summary>
    public int Ny { get; init; }
    public double Sy { get; init; }
    /// <summary>Mean distance from the centroid to the vertical lines, x direction [mm].</summary>
    public double Ds2x { get; init; }
    public double Db { get; init; }
    public double Fck { get; init; }
    public double Fy { get; init; }
    public double Fsy { get; init; }
    public double GammaC { get; init; } = 1.5;
    public double GammaA { get; init; } = 1.0;
    public double GammaS { get; init; } = 1.15;
    /// <summary>Concrete strength factor on 0.85 fcd: 1.0 as published; η of EC2 Table 3.1 for high-strength concrete (BC4).</summary>
    public double Eta { get; init; } = 1.0;
    public double Ecm { get; init; }
    public double Ea { get; init; } = 210_000;
    public double Es { get; init; } = 200_000;
    /// <summary>Design axial compression [kN] and moment about x-x [kNm].</summary>
    public double NEd { get; init; }
    public double MEdx { get; init; }
    /// <summary>NG,Ed / NEd and the creep coefficient φt.</summary>
    public double NGratio { get; init; }
    public double PhiT { get; init; }
    /// <summary>Buckling length [mm].</summary>
    public double L { get; init; }

    /// <summary>The section the report's §4.2.5 worked parameters describe (4 × HD 400×1299, d* = 600, b* = 275, dsy = 950).</summary>
    public static MegaColumnInput ReportDefault { get; } = new()
    {
        H1 = 2800, H2 = 2800, Arrangement = ProfileArrangement.Four, Aa = 165_000, D = 600, Ix = 7.5375e9, Zx = 2.9e7, Dsy = 950,
        I = 2, Nx = 25, Sx = 108, Ds1y = 1290, Ny = 25, Sy = 108, Ds2x = 1290, Db = 40, Fck = 60, Fy = 355, Fsy = 400,
        Ecm = 39_100, NEd = 120_000, MEdx = 30_000, NGratio = 0.75, PhiT = 1.5, L = 9000,
    };
}

/// <summary>Plastic neutral axis (Table 15 four profiles / Table 17 six profiles) and Mpl,Rd [kNm].</summary>
public sealed record HnxSolution(int CaseId, string CaseLabel, double Hnx, double Zcxn, double Zr2xn, double Zsxn, double MplRd,
    double? ZsxnAsPrinted, double? MplRdAsPrinted);

/// <summary>A strength ("capacity") or applicability ("validity") check of the method.</summary>
public sealed record MegaCheck(string Id, string Label, string Clause, string Demand, string Capacity, double Utilisation, bool Pass, string Note, bool IsCapacity);

/// <summary>Everything the method computes (mm, N·mm based; N in kN, M in kNm).</summary>
public sealed record MegaColumnResult
{
    // Table 11: rebar lines as equivalent plates
    public double Asri, As1, Hs1, Bs1, As2, Hs2, Bs2, Asr, Zsr1x, Isr1x, Zsr2x, Isr2x, Isr;
    // Tables 12 / 13: profiles as equivalent rectangles
    public double DStar, BStar, IStar, As, Zsx, Isx;
    public double Ag, Ac, Ic, Fcd, Fyd, Fsd;
    // Table 14 anchor points
    public double NplRd, NpmRd, Zcx, MmaxRd, MplRd;
    public required HnxSolution Hn;
    public required IReadOnlyList<(string Label, double N, double M)> Points;
    // §4.2.6 stiffness, buckling and second order
    public double EcEff, EIeff, C1, EIeffAisc, NplRk, Ncr, LambdaBar, Chi, NbRd, EIeffII, NcrEff, KAmp, MEdII;
    // §6.7.3.6 interaction
    public double MuD, AlphaM, MRd;
    public double Delta, WebN, WebInertiaError, WebPlasticError;
    public required IReadOnlyList<MegaCheck> Checks;
    public required MegaCheck Governing;
    public required IReadOnlyList<string> Warnings;
}

/// <summary>
/// Composite megacolumn — the Simplified (Adapted Plastic Distribution) Design Method of Trabucco, Gérardy, Xiao &amp; Davies
/// (2016), "Composite Megacolumns", §4, Tables 11–17: EN 1994-1-1 §6.7.3 plastic distribution extended to several encased
/// profiles by replacing bar lines with equivalent plates and the profiles with equivalent rectangles. A C# port of
/// <c>mega-column/engine.ts</c> (calculateMegaColumn2), formula for formula; the only addition is <see cref="MegaColumnInput.Eta"/>.
/// </summary>
public static class MegaColumnEngine
{
    /// <summary>The interaction polygon's M at <paramref name="n"/> (linear between the anchors).</summary>
    public static double MomentAtN(IReadOnlyList<(string Label, double N, double M)> points, double n)
    {
        var sorted = points.OrderByDescending(point => point.N).ToArray();
        if (n >= sorted[0].N) return sorted[0].M;
        if (n <= sorted[^1].N) return sorted[^1].M;
        for (var k = 0; k < sorted.Length - 1; k++)
        {
            var (hi, lo) = (sorted[k], sorted[k + 1]);
            if (n <= hi.N && n >= lo.N)
            {
                var span = hi.N - lo.N;
                if (Math.Abs(span) < 1e-9) return Math.Max(hi.M, lo.M);
                return hi.M + (hi.N - n) / span * (lo.M - hi.M);
            }
        }
        return 0;
    }

    private static HnxSolution SolveHnx(MegaColumnInput x, double bStar, double bs2, double mmaxRd, double npmRd, double fcd, double fyd, double fsd, double zsx)
    {
        double h1 = x.H1, d = x.D, dsy = x.Dsy, aa = x.Aa, zx = x.Zx;
        var fc = 0.85 * x.Eta * fcd;
        var nc = npmRd * 1000;          // kN → N
        var a = dsy - d / 2;            // inner face of the outer profiles
        double Mpl(double zcxn, double zr2xn, double zsxn) => mmaxRd - (zr2xn * fsd + zsxn * fyd + 0.5 * zcxn * fc) / 1e6;
        HnxSolution Solution(int id, string label, double hnx, double zr2xn, double zsxn, double? printed = null, double? mplPrinted = null)
        {
            var zcxn = h1 * hnx * hnx - zr2xn - zsxn;
            return new(id, label, hnx, zcxn, zr2xn, zsxn, Mpl(zcxn, zr2xn, zsxn), printed, mplPrinted);
        }

        if (x.Arrangement == ProfileArrangement.Four)
        {
            // Table 15, case 1: hnx ≤ dsy − d/2 — between the two profile pairs
            var hnx = nc / ((2 * h1 - 4 * bs2) * fc + 4 * bs2 * fsd);
            if (hnx <= a) return Solution(1, "Case 1 — hnx between the two profiles", hnx, 2 * bs2 * hnx * hnx, 0);
            // case 2: the neutral axis cuts the profiles
            hnx = (nc + 4 * a * bStar * (fyd - fc)) / ((2 * h1 - 4 * bStar - 4 * bs2) * fc + 4 * bStar * fyd + 4 * bs2 * fsd);
            if (hnx <= dsy + d / 2)
            {
                var zr2xn = 2 * bs2 * hnx * hnx;
                // continuity-consistent form, as Table 17 case 3: 2 b* (hnx − a)(hnx + a); the literal Table 15 print is kept for comparison
                var zsxn = 2 * bStar * (hnx - a) * (hnx + a);
                var printed = bStar * (hnx - a) * (3 * hnx + a);
                var zcxnPrinted = h1 * hnx * hnx - zr2xn - printed;
                return Solution(2, "Case 2 — hnx inside the steel profiles", hnx, zr2xn, zsxn, printed, Mpl(zcxnPrinted, zr2xn, printed));
            }
            // case 3: all profiles inside the zone
            hnx = (nc - 4 * aa * fyd + 4 * aa * fc) / ((2 * h1 - 4 * bs2) * fc + 4 * bs2 * fsd);
            return Solution(3, "Case 3 — hnx beyond the profiles", hnx, 2 * bs2 * hnx * hnx, zsx);
        }

        // Table 17: six profiles (four outer at ±dsy, two central on the axis); case 1: inside the two central profiles
        var h = nc / ((2 * h1 - 4 * bStar - 4 * bs2) * fc + 4 * bStar * fyd + 4 * bs2 * fsd);
        if (h <= d / 2) return Solution(1, "Case 1 — hnx inside the two central profiles", h, 2 * bs2 * h * h, 2 * bStar * h * h);
        // case 2: central profiles fully inside
        h = (nc + 2 * aa * fc - 2 * aa * fyd) / ((2 * h1 - 4 * bs2) * fc + 4 * bs2 * fsd);
        if (h <= a) return Solution(2, "Case 2 — hnx between central and outer profiles", h, 2 * bs2 * h * h, 2 * zx);
        // case 3: the zone cuts the outer profiles
        h = (nc + 2 * aa * fc - 4 * a * bStar * fc - 2 * aa * fyd + 4 * a * bStar * fyd) / ((2 * h1 - 4 * bStar - 4 * bs2) * fc + 4 * bStar * fyd + 4 * bs2 * fsd);
        if (h <= dsy + d / 2) return Solution(3, "Case 3 — hnx inside the outer profiles", h, 2 * bs2 * h * h, 2 * zx + 2 * bStar * (h - a) * (h + a));
        // case 4: all profiles inside the zone
        h = (nc - 4 * aa * fyd + 4 * aa * fc) / ((2 * h1 - 4 * bs2) * fc + 4 * bs2 * fsd);
        return Solution(4, "Case 4 — hnx beyond all profiles", h, 2 * bs2 * h * h, zsx);
    }

    /// <summary>The method for <paramref name="x"/>; null when the geometry or factors make no section.</summary>
    public static MegaColumnResult? Calculate(MegaColumnInput x)
    {
        if (x.H1 <= 0 || x.H2 <= 0 || x.Aa <= 0 || x.D <= 0 || x.L <= 0 || x.NEd <= 0) return null;
        if (x.Nx < 2 || x.Ny < 2 || x.I < 1) return null;
        if (x.GammaC <= 0 || x.GammaA <= 0 || x.GammaS <= 0) return null;
        var warnings = new List<string>();

        // Table 11: rebar lines replaced by equivalent plates
        var asri = Math.PI * x.Db * x.Db / 4;
        var as1 = x.I * x.Nx * asri;
        var hs1 = (x.Nx - 1) * x.Sx;
        var bs1 = as1 / hs1;
        var as2 = x.Ny * asri;
        var hs2 = (x.Ny - 1) * x.Sy;
        var bs2 = as2 / hs2;
        var asr = 2 * as1 + 2 * as2;
        var zsr1x = 2 * as1 * x.Ds1y;
        var isr1x = 2 * as1 * x.Ds1y * x.Ds1y;
        var zsr2x = bs2 * hs2 * hs2 / 2;
        var isr2x = bs2 * hs2 * hs2 * hs2 / 6;
        var isr = isr1x + isr2x;

        // Tables 12 / 13: profiles replaced by equivalent rectangles
        var dStar = x.D;
        var bStar = x.Aa / dStar;
        var iStar = bStar * dStar * dStar * dStar / 12;
        var four = x.Arrangement == ProfileArrangement.Four;
        var asTotal = (four ? 4 : 6) * x.Aa;
        var zsx = four ? 4 * x.Aa * x.Dsy : 4 * x.Aa * x.Dsy + 2 * x.Zx;
        var isx = four ? 4 * x.Aa * x.Dsy * x.Dsy + 4 * x.Ix : 4 * x.Aa * x.Dsy * x.Dsy + 6 * x.Ix;

        var ag = x.H1 * x.H2;
        var ac = ag - asTotal - asr;
        if (ac <= 0) return null;
        var ic = x.H1 * Math.Pow(x.H2, 3) / 12 - isx - isr;

        var fcd = x.Fck / x.GammaC;
        var fyd = x.Fy / x.GammaA;
        var fsd = x.Fsy / x.GammaS;
        var fc = 0.85 * x.Eta * fcd;

        // Table 14: anchor points
        var nplRd = (asTotal * fyd + asr * fsd + ac * fc) / 1000;
        var npmRd = ac * fc / 1000;
        var zcx = x.H1 * x.H2 * x.H2 / 4 - zsr1x - zsr2x - zsx;
        var mmaxRd = (zsx * fyd + (zsr1x + zsr2x) * fsd + 0.5 * zcx * fc) / 1e6;
        var hn = SolveHnx(x, bStar, bs2, mmaxRd, npmRd, fcd, fyd, fsd, zsx);
        var mplRd = hn.MplRd;
        (string, double, double)[] points = [("A", nplRd, 0), ("C", npmRd, mplRd), ("D", 0.5 * npmRd, mmaxRd), ("B", 0, mplRd)];

        // §4.2.6: effective flexural stiffness
        var ecEff = x.Ecm / (1 + x.NGratio * x.PhiT);
        var eiEff = x.Ea * isx + x.Es * isr + 0.6 * ecEff * ic;
        var c1 = Math.Min(0.7, 0.25 + 3 * ((asTotal + asr) / ag));
        var eiEffAisc = x.Ea * isx + x.Es * isr + c1 * x.Ecm * ic;
        var nplRk = (asTotal * x.Fy + asr * x.Fsy + 0.85 * x.Eta * ac * x.Fck) / 1000;
        var ncr = Math.PI * Math.PI * eiEff / (x.L * x.L) / 1000;
        var lambdaBar = Math.Sqrt(nplRk / ncr);
        // EN 1994-1-1 Table 6.5: encased sections buckle on curve b (α = 0.34)
        const double alpha = 0.34;
        var phi = 0.5 * (1 + alpha * (lambdaBar - 0.2) + lambdaBar * lambdaBar);
        var chi = Math.Min(1, 1 / (phi + Math.Sqrt(Math.Max(phi * phi - lambdaBar * lambdaBar, 0))));
        var nbRd = chi * nplRd;

        // EN 1994-1-1 §6.7.3.4: second-order effects
        var eiEffII = 0.9 * (x.Ea * isx + x.Es * isr + 0.5 * ecEff * ic);
        var ncrEff = Math.PI * Math.PI * eiEffII / (x.L * x.L) / 1000;
        var kAmp = x.NEd < ncrEff ? Math.Max(1, 1.0 / (1 - x.NEd / ncrEff)) : double.PositiveInfinity;
        var mEdII = double.IsFinite(kAmp) ? kAmp * x.MEdx : double.PositiveInfinity;

        // EN 1994-1-1 §6.7.3.6: compression and bending
        var muD = mplRd > 0 ? MomentAtN(points, x.NEd) / mplRd : 0;
        var alphaM = x.Fy <= 355 ? 0.9 : 0.8;
        var mRd = alphaM * muD * mplRd;

        var delta = asTotal * fyd / 1000 / nplRd;
        var webN = (x.Ny - 1) / 2.0;
        var webInertiaError = webN > 0 ? 1 / (2 * webN) : double.PositiveInfinity;
        var webPlasticError = webN > 0 ? 1 / (4 * (webN * webN + webN)) : double.PositiveInfinity;

        if (x.Dsy + x.D / 2 > x.H2 / 2) warnings.Add("The outer profiles project beyond the section depth (dsy + d/2 > h2/2). Check the profile position and cover.");
        if (hs2 > x.H2) warnings.Add("The equivalent vertical rebar plate is deeper than the section — check ny and sy.");
        if (hs1 > x.H1) warnings.Add("The equivalent horizontal rebar plate is wider than the section — check nx and sx.");
        if (zcx <= 0) warnings.Add("Concrete plastic modulus Zcx is not positive — the steel and rebar plates exceed the gross section.");
        if (!double.IsFinite(kAmp)) warnings.Add("NEd exceeds the effective elastic critical load Ncr,eff — the member is unstable under second-order effects.");

        static string F0(double value) => double.IsFinite(value) ? value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "∞";
        static string F3(double value) => value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        MegaCheck[] checks =
        [
            new("buckling", "Axial buckling resistance", "EN 1994-1-1 §6.7.3.5", $"NEd = {F0(x.NEd)} kN", $"χ·Npl,Rd = {F0(nbRd)} kN",
                nbRd > 0 ? x.NEd / nbRd : double.PositiveInfinity, x.NEd <= nbRd, $"Buckling curve b (α = 0.34), λ̄ = {F3(lambdaBar)}, χ = {F3(chi)}.", true),
            new("interaction", "Compression + uniaxial bending", "EN 1994-1-1 §6.7.3.6", $"MEd = {F0(mEdII)} kNm", $"αM·μd·Mpl,Rd = {F0(mRd)} kNm",
                mRd > 0 ? mEdII / mRd : double.PositiveInfinity, double.IsFinite(mEdII) && mEdII <= mRd, $"μd = {F3(muD)} read off the A–C–D–B polygon at NEd; αM = {alphaM}.", true),
            new("squash", "Cross-section squash resistance", "Table 14, point A", $"NEd = {F0(x.NEd)} kN", $"Npl,Rd = {F0(nplRd)} kN",
                nplRd > 0 ? x.NEd / nplRd : double.PositiveInfinity, x.NEd <= nplRd, "Full plastic axial resistance with 0.85·fcd on the concrete.", true),
            new("delta", "Steel contribution ratio δ", "EN 1994-1-1 §6.7.1(4)", $"δ = {F3(delta)}", "0.2 ≤ δ ≤ 0.9",
                delta < 0.2 ? 0.2 / Math.Max(delta, 1e-6) : delta / 0.9, delta is >= 0.2 and <= 0.9, "Outside this band the member is designed as reinforced concrete or as bare steel.", false),
            new("slenderness", "Relative slenderness λ̄", "EN 1994-1-1 §6.7.3.1(1)", $"λ̄ = {F3(lambdaBar)}", "λ̄ ≤ 2.0",
                lambdaBar / 2, lambdaBar <= 2, "Beyond λ̄ = 2.0 the simplified method of §6.7.3 is outside its stated scope.", false),
            new("webrebar", "Web rebar simplification (n ≥ 5)", "Report §4.2.3", $"n = {webN:0.0}", "n ≥ 5",
                webN > 0 ? 5 / webN : double.PositiveInfinity, webN >= 5,
                $"Equivalent-plate error on the rebar inertia is 1/(2n) = {webInertiaError * 100:0.0}%; the report accepts this while n ≥ 5.", false),
        ];
        var governing = checks.Where(check => check.IsCapacity).MaxBy(check => check.Utilisation)!;

        return new MegaColumnResult
        {
            Asri = asri, As1 = as1, Hs1 = hs1, Bs1 = bs1, As2 = as2, Hs2 = hs2, Bs2 = bs2, Asr = asr, Zsr1x = zsr1x, Isr1x = isr1x, Zsr2x = zsr2x, Isr2x = isr2x, Isr = isr,
            DStar = dStar, BStar = bStar, IStar = iStar, As = asTotal, Zsx = zsx, Isx = isx, Ag = ag, Ac = ac, Ic = ic, Fcd = fcd, Fyd = fyd, Fsd = fsd,
            NplRd = nplRd, NpmRd = npmRd, Zcx = zcx, MmaxRd = mmaxRd, Hn = hn, MplRd = mplRd, Points = points,
            EcEff = ecEff, EIeff = eiEff, C1 = c1, EIeffAisc = eiEffAisc, NplRk = nplRk, Ncr = ncr, LambdaBar = lambdaBar, Chi = chi, NbRd = nbRd,
            EIeffII = eiEffII, NcrEff = ncrEff, KAmp = kAmp, MEdII = mEdII, MuD = muD, AlphaM = alphaM, MRd = mRd,
            Delta = delta, WebN = webN, WebInertiaError = webInertiaError, WebPlasticError = webPlasticError,
            Checks = checks, Governing = governing, Warnings = warnings,
        };
    }

    /// <summary>Table 15 case 2 continuity audit: at hnx = dsy + d/2 the zone holds the whole profiles, so Zsxn must equal Zsx
    /// = 4 Aa dsy; the continuity form does, the literal print overshoots by Aa·d.</summary>
    public static (double Required, double AsPrinted, double Continuous) AuditZsxnContinuity(double aa = 165_000, double d = 600, double dsy = 950)
    {
        var bStar = aa / d;
        var a = dsy - d / 2;
        var hnx = dsy + d / 2;
        return (4 * aa * dsy, bStar * (hnx - a) * (3 * hnx + a), 2 * bStar * (hnx - a) * (hnx + a));
    }
}
