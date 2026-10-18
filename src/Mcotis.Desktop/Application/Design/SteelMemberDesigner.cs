using System.Text;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application.Design;

// Steel beam / column design to SS EN 1993-1-1 (cross-section classification 5.5, resistances 6.2, member buckling 6.3 with the
// interaction of Annex B) and SS EN 1993-1-5 (effective widths of class 4 sections, shear buckling of webs). Units: N, mm, MPa.
// The axes are the column engine's: y-y is the major axis (depth H along local 2), z-z the minor axis; compression is positive.

/// <summary>A part of a section for the section properties: area, centroid (U across, V along the depth) and its own second moments
/// and first moments of area |v| dA, |u| dA (for the plastic moduli). A negative sign takes material out.</summary>
internal sealed record SteelPiece(double Area, double U, double V, double Iy, double Iz, double Sy, double Sz)
{
    /// <summary>Offset of the centroid of a corner fillet's material from the corner, as a fraction of its radius: (10 − 3π) / (3 (4 − π)).</summary>
    public const double FilletCentroid = 0.2234;

    public static SteelPiece Rect(double w, double h, double u, double v, double sign = 1) => new(sign * w * h, u, v,
        sign * w * h * h * h / 12, sign * h * w * w * w / 12,
        sign * (v == 0 ? w * h * h / 4 : w * h * Math.Abs(v)), sign * (u == 0 ? h * w * w / 4 : w * h * Math.Abs(u)));

    /// <summary>The material of a fillet of radius <paramref name="r"/> in a corner: (1 − π/4) r², its own inertia neglected.</summary>
    public static SteelPiece Corner(double r, double u, double v, double sign = 1)
    {
        var area = sign * (1 - Math.PI / 4) * r * r;
        return new(area, u, v, 0, 0, area * Math.Abs(v), area * Math.Abs(u));
    }
}

/// <summary>The cross-section class (EN 1993-1-1 5.5) and the part that decides it.</summary>
internal readonly record struct SteelClass(int Class, string Governing);

/// <summary>The geometry of an I, rectangular hollow or circular hollow section and its properties.</summary>
internal sealed class SteelGeometry
{
    public SteelShape Kind { get; }
    public double H { get; }            // overall depth (along local 2)
    public double B { get; }            // overall width
    public double Tf { get; }
    public double Tw { get; }
    public double R { get; }            // root radius of a rolled I
    public bool Welded { get; }
    public bool ColdFormed { get; }
    public double Hw { get; }           // clear depth between the flanges
    public double Cw { get; }           // flat width of the web (class and effective width)
    public double Cf { get; }           // flat width of a flange outstand (I) or of the b walls of a hollow section
    public double A { get; }
    public double Iy { get; }
    public double Iz { get; }
    public double Wely => Iy / (H / 2);
    public double Welz => Iz / (B / 2);
    public double Wply { get; }
    public double Wplz { get; }
    public double It { get; }
    public double Iw { get; }
    public double Avy { get; }          // shear area for the shear along local 2
    public double Avz { get; }          // shear area for the shear along local 3
    public double WebThicknessTotal { get; }   // Σ tw of the webs carrying the shear along local 2
    public double MaxThickness => Math.Max(Tf, Tw);
    public IReadOnlyList<SteelPiece> Pieces { get; }
    public bool Hollow => Kind != SteelShape.I;

    public SteelGeometry(ColumnSectionInput s)
    {
        Kind = s.SteelKind;
        ColdFormed = s.Fabrication == SteelFabrication.ColdFormed;
        switch (Kind)
        {
            case SteelShape.I:
            {
                H = s.SteelDepth; B = s.SteelWidth; Tf = s.FlangeThickness; Tw = s.WebThickness;
                Welded = s.Fabrication == SteelFabrication.Welded;
                R = Welded ? 0 : Math.Max(0, s.RootRadius);
                Hw = H - 2 * Tf;
                Cw = Hw - 2 * R;
                Cf = (B - Tw - 2 * R) / 2;
                var pieces = new List<SteelPiece>
                {
                    SteelPiece.Rect(B, Tf, 0, (H - Tf) / 2), SteelPiece.Rect(B, Tf, 0, -(H - Tf) / 2), SteelPiece.Rect(Tw, Hw, 0, 0),
                };
                if (R > 0)
                    foreach (var (su, sv) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                        pieces.Add(SteelPiece.Corner(R, su * (Tw / 2 + SteelPiece.FilletCentroid * R), sv * (Hw / 2 - SteelPiece.FilletCentroid * R)));
                Pieces = pieces;
                WebThicknessTotal = Tw;
                // St Venant constant without the root fillets (on the safe side); warping constant of a doubly symmetric I
                It = 2.0 / 3 * (B - 0.63 * Tf) * Tf * Tf * Tf + (H - Tf) * Tw * Tw * Tw / 3;
                break;
            }
            case SteelShape.Rhs:
            {
                H = s.TubeDepth; B = s.TubeWidth; Tf = Tw = s.TubeThickness;
                var t = Tf;
                // EN 10219 cold-formed corners 2t / 2.5t / 3t outside, EN 10210 hot-finished 1.5t; the wall keeps its thickness round the corner
                var ro = ColdFormed ? (t <= 6 ? 2 : t <= 10 ? 2.5 : 3) * t : 1.5 * t;
                var ri = ro - t;
                Hw = H - 2 * t;
                Cw = H - 3 * t;
                Cf = B - 3 * t;
                var pieces = new List<SteelPiece>
                {
                    SteelPiece.Rect(B, t, 0, (H - t) / 2), SteelPiece.Rect(B, t, 0, -(H - t) / 2),
                    SteelPiece.Rect(t, Hw, (B - t) / 2, 0), SteelPiece.Rect(t, Hw, -(B - t) / 2, 0),
                };
                foreach (var (su, sv) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                {
                    pieces.Add(SteelPiece.Corner(ro, su * (B / 2 - SteelPiece.FilletCentroid * ro), sv * (H / 2 - SteelPiece.FilletCentroid * ro), -1));
                    if (ri > 0) pieces.Add(SteelPiece.Corner(ri, su * (B / 2 - t - SteelPiece.FilletCentroid * ri), sv * (H / 2 - t - SteelPiece.FilletCentroid * ri)));
                }
                Pieces = pieces;
                WebThicknessTotal = 2 * t;
                double am = (B - t) * (H - t), p = 2 * (B + H - 2 * t);
                It = 4 * am * am * t / p + p * t * t * t / 3;
                break;
            }
            default:
            {
                H = B = s.TubeDepth; Tf = Tw = s.TubeThickness;
                Hw = Cw = Cf = H;
                Pieces = [];
                WebThicknessTotal = 2 * Tf;
                break;
            }
        }
        if (Kind == SteelShape.Chs)
        {
            double d = H - 2 * Tf;
            A = Math.PI * (H * H - d * d) / 4;
            Iy = Iz = Math.PI * (Math.Pow(H, 4) - Math.Pow(d, 4)) / 64;
            Wply = Wplz = (H * H * H - d * d * d) / 6;
            It = 2 * Iy;
            Avy = Avz = 2 * A / Math.PI;
            Iw = 0;
            return;
        }
        A = Pieces.Sum(piece => piece.Area);
        Iy = Pieces.Sum(piece => piece.Iy + piece.Area * piece.V * piece.V);
        Iz = Pieces.Sum(piece => piece.Iz + piece.Area * piece.U * piece.U);
        Wply = Pieces.Sum(piece => piece.Sy);
        Wplz = Pieces.Sum(piece => piece.Sz);
        if (Kind == SteelShape.I)
        {
            Iw = Iz * (H - Tf) * (H - Tf) / 4;
            // EN 1993-1-1 6.2.6(3): rolled — A − 2 b tf + (tw + 2r) tf, not less than η hw tw; welded — hw tw; the other direction A − hw tw
            Avy = Welded ? Hw * Tw : Math.Max(A - 2 * B * Tf + (Tw + 2 * R) * Tf, 1.2 * Hw * Tw);
            Avz = A - Hw * Tw;
        }
        else
        {
            Iw = 0;
            Avy = A * H / (H + B);
            Avz = A * B / (H + B);
        }
    }

    /// <summary>The class of the section under the axial force <paramref name="n"/> (compression, ≥ 0) and the moments about y-y and z-z
    /// (EN 1993-1-1 Table 5.2). Outstand flanges and compressed walls are taken in uniform compression; the web of an I section and the
    /// walls of a hollow section lie in the combined bending and compression of the axis they are the web of.</summary>
    public SteelClass Classify(double n, double my, double mz, double fy)
    {
        var eps = Math.Sqrt(235 / fy);
        n = Math.Max(0, n);
        if (Kind == SteelShape.Chs)
        {
            var ratio = H / Tf;
            var e2 = eps * eps;
            return new(ratio <= 50 * e2 ? 1 : ratio <= 70 * e2 ? 2 : ratio <= 90 * e2 ? 3 : 4, $"d/t = {Text(ratio)} (limits {Text(50 * e2)} / {Text(70 * e2)} / {Text(90 * e2)})");
        }
        SteelClass Compressed(double c, double t, string name)
        {
            var ratio = c / t;
            return new(Kind == SteelShape.I
                ? ratio <= 9 * eps ? 1 : ratio <= 10 * eps ? 2 : ratio <= 14 * eps ? 3 : 4
                : ratio <= 33 * eps ? 1 : ratio <= 38 * eps ? 2 : ratio <= 42 * eps ? 3 : 4,
                $"{name} c/t = {Text(ratio)}");
        }
        SteelClass Web(double c, double t, double webs, double m, double inertia, string name)
        {
            var ratio = c / t;
            var alpha = Math.Min(1, 0.5 * (1 + n / (webs * t * c * fy)));
            var sigma1 = n / A + m * (c / 2) / inertia;
            var sigma2 = n / A - m * (c / 2) / inertia;
            var psi = sigma1 > 1e-12 ? sigma2 / sigma1 : 1;
            var limit1 = alpha > 0.5 ? 396 * eps / (13 * alpha - 1) : 36 * eps / alpha;
            var limit2 = alpha > 0.5 ? 456 * eps / (13 * alpha - 1) : 41.5 * eps / alpha;
            var limit3 = psi > -1 ? 42 * eps / (0.67 + 0.33 * psi) : 62 * eps * (1 - psi) * Math.Sqrt(-psi);
            var cls = sigma1 <= 1e-12 || ratio <= limit1 ? 1 : ratio <= limit2 ? 2 : ratio <= limit3 ? 3 : 4;
            return new(cls, $"{name} c/t = {Text(ratio)} (α = {Text(alpha, 2)}, ψ = {Text(psi, 2)})");
        }
        var parts = new List<SteelClass>();
        if (Kind == SteelShape.I)
        {
            parts.Add(Compressed(Cf, Tf, "flange outstand"));
            parts.Add(Web(Cw, Tw, 1, my, Iy, "web"));
        }
        else
        {
            parts.Add(Web(Cw, Tf, 2, my, Iy, "h walls as webs"));
            parts.Add(Compressed(Cf, Tf, "b walls as flanges"));
            if (mz > 0)
            {
                parts.Add(Web(Cf, Tf, 2, mz, Iz, "b walls as webs"));
                parts.Add(Compressed(Cw, Tf, "h walls as flanges"));
            }
        }
        return parts.MaxBy(part => part.Class);
    }

    private static string Text(double value, int decimals = 1) => ColumnDesignEngine.F(value, decimals);

    /// <summary>ρ of EN 1993-1-5 4.4 (internal element, stress ratio ψ) or 4.4 Table 4.2 (outstand, ψ = 1); 1 when the element is fully effective.</summary>
    public static double Rho(double ratio, double psi, bool outstand, double eps)
    {
        if (outstand)
        {
            var lambda = ratio / (28.4 * eps * Math.Sqrt(0.43));
            return lambda <= 0.748 ? 1 : Math.Min(1, (lambda - 0.188) / (lambda * lambda));
        }
        var k = psi >= 1 ? 4 : psi >= 0 ? 8.2 / (1.05 + psi) : psi > -1 ? 7.81 - 6.29 * psi + 9.78 * psi * psi : 23.9;
        var lambdaP = ratio / (28.4 * eps * Math.Sqrt(k));
        return lambdaP <= 0.5 + Math.Sqrt(0.085 - 0.055 * psi) ? 1 : Math.Min(1, (lambdaP - 0.055 * (3 + psi)) / (lambdaP * lambdaP));
    }

    /// <summary>The effective area of a class 4 section in uniform compression (EN 1993-1-5 4.3, ψ = 1); the section stays symmetric, so the
    /// centroid does not move.</summary>
    public double EffectiveArea(double fy)
    {
        var eps = Math.Sqrt(235 / fy);
        if (Kind == SteelShape.I)
            return A - (1 - Rho(Cw / Tw, 1, false, eps)) * Cw * Tw - 4 * (1 - Rho(Cf / Tf, 1, true, eps)) * Cf * Tf;
        if (Kind == SteelShape.Rhs)
            return A - 2 * (1 - Rho(Cw / Tf, 1, false, eps)) * Cw * Tf - 2 * (1 - Rho(Cf / Tf, 1, false, eps)) * Cf * Tf;
        return A;
    }

    /// <summary>The effective elastic modulus Weff of a class 4 section about y-y (<paramref name="major"/>) or z-z with the compression on one
    /// side: the compressed flange or tips lose their ineffective width, the web the part from be1 to be2 (EN 1993-1-5 Table 4.1), with the web's
    /// stress ratio ψ from the gross section under <paramref name="n"/> and the moment; the centroid moves, and the larger fibre distance gives
    /// the modulus.</summary>
    public double EffectiveModulus(bool major, double n, double m, double fy)
    {
        if (Kind == SteelShape.Chs) return major ? Wely : Welz;
        var eps = Math.Sqrt(235 / fy);
        n = Math.Max(0, n);
        var pieces = new List<SteelPiece>(Pieces);
        if (major)
        {
            var sigma1 = n / A + m * (Cw / 2) / Iy;
            var sigma2 = n / A - m * (Cw / 2) / Iy;
            var psi = sigma1 > 1e-12 ? Math.Clamp(sigma2 / sigma1, -1, 1) : 1;
            var web = Rho(Cw / Tw, psi, false, eps);
            var compressionDepth = psi >= 0 ? Cw : Cw / (1 - psi);
            var effective = web * compressionDepth;
            double be1 = psi >= 0 ? 2 * effective / (5 - psi) : 0.4 * effective;
            double be2 = effective - be1;
            if (web < 1)
            {
                double top = Cw / 2, y1 = top - be1, y2 = top - (compressionDepth - be2);
                foreach (var u in Kind == SteelShape.I ? new[] { 0.0 } : new[] { (B - Tw) / 2, -(B - Tw) / 2 })
                    pieces.Add(SteelPiece.Rect(Tw, y1 - y2, u, (y1 + y2) / 2, -1));
            }
            // the flange on the compressed side (v > 0)
            if (Kind == SteelShape.I)
            {
                var flange = Rho(Cf / Tf, 1, true, eps);
                foreach (var side in new[] { 1, -1 })
                    pieces.Add(SteelPiece.Rect((1 - flange) * Cf, Tf, side * (B / 2 - (1 - flange) * Cf / 2), (H - Tf) / 2, -1));
            }
            else
            {
                var flange = Rho(Cf / Tf, 1, false, eps);
                pieces.Add(SteelPiece.Rect((1 - flange) * Cf, Tf, 0, (H - Tf) / 2, -1));
            }
        }
        else
        {
            // bending about z-z: the compression is on the u > 0 side — tips of both I flanges, or the right wall of a hollow section
            if (Kind == SteelShape.I)
            {
                var flange = Rho(Cf / Tf, 1, true, eps);
                foreach (var side in new[] { 1, -1 })
                    pieces.Add(SteelPiece.Rect((1 - flange) * Cf, Tf, B / 2 - (1 - flange) * Cf / 2, side * (H - Tf) / 2, -1));
            }
            else
            {
                var sigma1 = n / A + m * (Cf / 2) / Iz;
                var sigma2 = n / A - m * (Cf / 2) / Iz;
                var psi = sigma1 > 1e-12 ? Math.Clamp(sigma2 / sigma1, -1, 1) : 1;
                var wall = Rho(Cf / Tf, psi, false, eps);
                var compressionDepth = psi >= 0 ? Cf : Cf / (1 - psi);
                var effective = wall * compressionDepth;
                double be1 = psi >= 0 ? 2 * effective / (5 - psi) : 0.4 * effective;
                double be2 = effective - be1;
                if (wall < 1)
                {
                    double top = Cf / 2, y1 = top - be1, y2 = top - (compressionDepth - be2);
                    foreach (var v in new[] { (H - Tf) / 2, -(H - Tf) / 2 })
                        pieces.Add(SteelPiece.Rect(y1 - y2, Tf, (y1 + y2) / 2, v, -1));
                }
                var other = Rho(Cw / Tf, 1, false, eps);
                if (other < 1) pieces.Add(SteelPiece.Rect(Tf, (1 - other) * Cw, B / 2 - Tf / 2, 0, -1));
            }
        }
        var area = pieces.Sum(piece => piece.Area);
        if (major)
        {
            var centroid = pieces.Sum(piece => piece.Area * piece.V) / area;
            var inertia = pieces.Sum(piece => piece.Iy + piece.Area * piece.V * piece.V) - area * centroid * centroid;
            return inertia / (H / 2 + Math.Abs(centroid));
        }
        else
        {
            var centroid = pieces.Sum(piece => piece.Area * piece.U) / area;
            var inertia = pieces.Sum(piece => piece.Iz + piece.Area * piece.U * piece.U) - area * centroid * centroid;
            return inertia / (B / 2 + Math.Abs(centroid));
        }
    }
}

/// <summary>Yield strength by thickness (EN 1993-1-1 Table 3.1 with the product standards): the nominal value up to 40 mm, the reduced one up to 80 mm.</summary>
internal static class SteelStrength
{
    public static double ForThickness(double nominal, double thickness)
    {
        if (thickness <= 40) return nominal;
        return nominal switch
        {
            235 => 215, 275 => 255, 355 => 335, 420 => 390, 450 => 410, 460 => 430,
            _ => nominal,
        };
    }
}

/// <summary>Steel beam / column: EN 1993-1-1 6.2 cross-section, 6.3.1 flexural buckling, 6.3.2 lateral-torsional buckling, 6.3.3 Annex B interaction.</summary>
internal sealed class SteelDesigner : ColumnDesigner
{
    private readonly SteelGeometry g;
    private readonly double fy;
    private readonly double gammaM0, gammaM1;
    private readonly bool generalLtb;
    private const double Poisson = 0.3;

    public SteelDesigner(ColumnDesignBasis basis) : base(basis)
    {
        g = new SteelGeometry(S);
        fy = SteelStrength.ForThickness(S.Fy, g.MaxThickness);
        gammaM0 = Basis.GammaA;
        gammaM1 = Basis.GammaM1;
        generalLtb = Basis.Method.Contains("6.3.2.2", StringComparison.Ordinal);
    }

    private double Epsilon => Math.Sqrt(235 / fy);
    private double G => S.Ea / (2 * (1 + Poisson));

    // ---- cross-section resistances by class ----
    private double Area(int cls) => cls == 4 ? g.EffectiveArea(fy) : g.A;
    private double Npl => g.A * fy / gammaM0;

    private double Modulus(bool major, int cls, double n, double m) => cls switch
    {
        <= 2 => major ? g.Wply : g.Wplz,
        3 => major ? g.Wely : g.Welz,
        _ => g.EffectiveModulus(major, n, m, fy),
    };

    public override InteractionDiagram Interaction()
    {
        // the cross-section N–M envelope of 6.2.9.1 with plastic moduli (class 1 and 2), n from −1 to 1; member buckling is not in it
        var npl = Npl;
        var curveY = new List<(double N, double M)>();
        var curveZ = new List<(double N, double M)>();
        for (var step = -50; step <= 50; step++)
        {
            var n = step / 50.0;
            curveY.Add((n * npl, ReducedMoment(true, Math.Abs(n))));
            curveZ.Add((n * npl, ReducedMoment(false, Math.Abs(n))));
        }
        return Diagram(new InteractionCurve(curveY), new InteractionCurve(curveZ),
            $"EN 1993-1-1 6.2.9.1 cross-section resistance, plastic moduli (fy = {F(fy, 0)} MPa, γM0 = {F(gammaM0, 2)}); member buckling not included");
    }

    /// <summary>MN,Rd about one axis at n = |NEd| / Npl,Rd for a class 1 or 2 section (EN 1993-1-1 6.2.9.1(5), (6)).</summary>
    private double ReducedMoment(bool major, double n)
    {
        var mpl = (major ? g.Wply : g.Wplz) * fy / gammaM0;
        n = Math.Min(1, n);
        switch (g.Kind)
        {
            case SteelShape.Chs:
                return mpl * (1 - Math.Pow(n, 1.7));
            case SteelShape.Rhs:
            {
                var a = Math.Min(0.5, (g.A - 2 * (major ? g.B : g.H) * g.Tf) / g.A);
                return Math.Min(mpl, mpl * (1 - n) / (1 - 0.5 * a));
            }
            default:
            {
                var a = Math.Min(0.5, (g.A - 2 * g.B * g.Tf) / g.A);
                if (major) return Math.Min(mpl, mpl * (1 - n) / (1 - 0.5 * a));
                return n <= a ? mpl : mpl * (1 - Math.Pow((n - a) / (1 - a), 2));
            }
        }
    }

    public override void Describe(StringBuilder r)
    {
        r.AppendLine("SECTION AND MATERIALS");
        switch (g.Kind)
        {
            case SteelShape.I:
                r.AppendLine($"  {(g.Welded ? "Welded" : "Rolled")} I section h × b × tf × tw = {F(g.H, 1)} × {F(g.B, 1)} × {F(g.Tf, 1)} × {F(g.Tw, 1)} mm" +
                             (g.R > 0 ? $", root radius r = {F(g.R, 1)} mm" : g.Welded ? "" : ", root fillets not counted (r = 0)"));
                break;
            case SteelShape.Rhs:
                r.AppendLine($"  {(g.ColdFormed ? "Cold-formed" : "Hot-finished")} RHS H × B × t = {F(g.H, 1)} × {F(g.B, 1)} × {F(g.Tf, 1)} mm");
                break;
            default:
                r.AppendLine($"  {(g.ColdFormed ? "Cold-formed" : "Hot-finished")} CHS D × t = {F(g.H, 1)} × {F(g.Tf, 1)} mm");
                break;
        }
        r.AppendLine($"  A = {F(g.A, 0)} mm², Iy = {F(g.Iy / 1e4, 0)} cm⁴, Iz = {F(g.Iz / 1e4, 0)} cm⁴, Wel,y = {F(g.Wely / 1e3, 0)} cm³, Wel,z = {F(g.Welz / 1e3, 0)} cm³, " +
                     $"Wpl,y = {F(g.Wply / 1e3, 0)} cm³, Wpl,z = {F(g.Wplz / 1e3, 0)} cm³");
        r.AppendLine($"  It = {F(g.It / 1e4, 1)} cm⁴" + (g.Kind == SteelShape.I ? $", Iw = {F(g.Iw / 1e12, 3)} dm⁶ (Iz (h − tf)² / 4), St Venant constant without root fillets" : "") +
                     $"; Av,y = {F(g.Avy, 0)} mm², Av,z = {F(g.Avz, 0)} mm²");
        var reduced = fy < S.Fy;
        r.AppendLine($"  fy = {F(fy, 0)} MPa" + (reduced ? $" (nominal {F(S.Fy, 0)} MPa reduced for the thickness {F(g.MaxThickness, 1)} mm, Table 3.1)" : "") +
                     $", ε = √(235/fy) = {F(Epsilon, 3)}; E = {F(S.Ea, 0)} MPa, G = E / 2(1 + ν) = {F(G, 0)} MPa");
        r.AppendLine($"  γM0 = {F(gammaM0, 2)}, γM1 = {F(gammaM1, 2)}, γM2 = {F(Basis.GammaM2, 2)} (Singapore NA values as keyed in the design basis)");
        r.AppendLine($"  Npl,Rd = A fy / γM0 = {F(Npl / 1000, 0)} kN; Mpl,y,Rd = {F(g.Wply * fy / gammaM0 / 1e6, 1)} kNm; Mpl,z,Rd = {F(g.Wplz * fy / gammaM0 / 1e6, 1)} kNm");
        var pure = g.Classify(0, 1, 0, fy);
        r.AppendLine($"  Class under y-y bending alone: {pure.Class} ({pure.Governing}); under compression alone: {g.Classify(1, 0, 0, fy).Class}");
        r.AppendLine($"  LTB method: {(generalLtb ? "general case, 6.3.2.2 (curves of Table 6.4)" : "rolled and equivalent welded sections, 6.3.2.3 (λLT,0 = 0.4, β = 0.75, Table 6.5)")}; interaction: Annex B (Method 2)");
    }

    public override DesignCaseResult DesignCase(string name, DesignActions actions, double length, double l0y, double l0z, StringBuilder log)
    {
        var (axial, my, mz, vy, vz) = actions;
        var nEd = axial + 0.0;   // −0 becomes 0
        Actions(log, actions with { NEd = nEd });
        var compression = Math.Max(0, nEd);
        var tensionForce = Math.Max(0, -nEd);
        var cls = g.Classify(compression, my, mz, fy);
        log.AppendLine($"CLASSIFICATION (EN 1993-1-1 5.5, Table 5.2): class {cls.Class} — {cls.Governing}");
        if (g.Kind == SteelShape.Chs && cls.Class == 4)
        {
            log.AppendLine("  A class 4 circular hollow section needs EN 1993-1-6 (shell buckling): outside the scope of this design — utilisation not defined.");
            return new(name, nEd / 1000, my / 1e6, my / 1e6, mz / 1e6, vy / 1000, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, vz / 1000, M0EdZKNm: mz / 1e6);
        }
        var c = cls.Class;
        var aeff = Area(c);
        var wy = Modulus(true, c, compression, my);
        var wz = Modulus(false, c, compression, mz);
        if (c == 4)
            log.AppendLine($"  Class 4: Aeff = {F(aeff, 0)} mm² (EN 1993-1-5 4.4), Weff,y = {F(wy / 1e3, 0)} cm³, Weff,z = {F(wz / 1e3, 0)} cm³ (the section is symmetric under compression: eN = 0)");
        var nRd = aeff * fy / gammaM0;
        var myRdCs = wy * fy / gammaM0;
        var mzRdCs = wz * fy / gammaM0;
        log.AppendLine();

        // ---- shear (6.2.6, EN 1993-1-5 5) ----
        var (shearY, vcY, rhoY) = Shear("along local 2 (with y-y bending)", vy, g.Avy, true, log);
        var shearZ = 0.0;
        var rhoZ = 0.0;
        if (vz > 0) (shearZ, _, rhoZ) = Shear("along local 3 (with z-z bending)", vz, g.Avz, false, log);
        log.AppendLine();

        // ---- cross-section under N, My, Mz (6.2.3, 6.2.5, 6.2.9) ----
        log.AppendLine("CROSS-SECTION RESISTANCE (6.2)");
        var axialUtil = 0.0;
        double csUtil;
        var nEdAbs = Math.Abs(nEd);
        var myCs = my;
        var mzCs = mz;
        if (nEd >= 0)
        {
            axialUtil = nEd / nRd;
            log.AppendLine($"  Compression: NEd / Nc,Rd = {F(nEd / 1000)} / {F(nRd / 1000, 0)} = {F(axialUtil, 3)} {Ok(axialUtil <= 1)} (6.2.4)");
        }
        else
        {
            var ntRd = Npl;
            axialUtil = tensionForce / ntRd;
            log.AppendLine($"  Tension: NEd / Nt,Rd = {F(tensionForce / 1000)} / {F(ntRd / 1000, 0)} = {F(axialUtil, 3)} {Ok(axialUtil <= 1)} (6.2.3, gross section; net section at bolt holes is not checked)");
        }
        if (c <= 2)
        {
            var n = nEdAbs / Npl;
            // a shear above half the plastic shear resistance lowers the bending resistance (6.2.8)
            var reductionY = ShearReduction(true, rhoY, log);
            var reductionZ = rhoZ > 0 ? 1 - rhoZ : 1.0;
            var mnY = Math.Min(ReducedMoment(true, n), myRdCs * reductionY);
            var mnZ = Math.Min(ReducedMoment(false, n), mzRdCs * reductionZ);
            log.AppendLine($"  n = |NEd| / Npl,Rd = {F(n, 3)}; MN,y,Rd = {F(mnY / 1e6)} kNm, MN,z,Rd = {F(mnZ / 1e6)} kNm (6.2.9.1)");
            if (g.Kind == SteelShape.Chs)
            {
                csUtil = mnY > 0 ? Math.Sqrt(my * my + mz * mz) / mnY : double.PositiveInfinity;
                log.AppendLine($"  Biaxial: √(My² + Mz²) / MN,Rd = {F(csUtil, 3)} {Ok(csUtil <= 1)} (6.2.9.1(6), CHS)");
            }
            else
            {
                var alpha = g.Kind == SteelShape.I ? 2.0 : Math.Min(6, 1.66 / (1 - 1.13 * n * n));
                var beta = g.Kind == SteelShape.I ? Math.Max(1, 5 * n) : alpha;
                var ratioY = mnY > 0 ? my / mnY : my > 0 ? double.PositiveInfinity : 0;
                var ratioZ = mnZ > 0 ? mz / mnZ : mz > 0 ? double.PositiveInfinity : 0;
                csUtil = Math.Max(Math.Max(ratioY, ratioZ), mz > 0 ? Math.Pow(ratioY, alpha) + Math.Pow(ratioZ, beta) : ratioY);
                log.AppendLine($"  (My,Ed / MN,y,Rd)^α + (Mz,Ed / MN,z,Rd)^β with α = {F(alpha, 2)}, β = {F(beta, 2)}: ({F(ratioY, 3)})^α + ({F(ratioZ, 3)})^β → {F(csUtil, 3)} {Ok(csUtil <= 1)} (6.2.9.1(6))");
            }
        }
        else
        {
            // class 3 and 4: the largest normal stress against fy / γM0 (6.2.9.2, 6.2.9.3); a shear above half Vpl,Rd lowers the strength in the section
            var reduction = Math.Min(1 - rhoY, rhoZ > 0 ? 1 - rhoZ : 1);
            var sigma = nEdAbs / aeff + my / wy + mz / wz;
            csUtil = reduction > 0 ? sigma / (fy / gammaM0) / reduction : double.PositiveInfinity;
            log.AppendLine($"  σx,Ed = |NEd| / {(c == 4 ? "Aeff" : "A")} + My,Ed / W{(c == 4 ? "eff" : "el")},y + Mz,Ed / W{(c == 4 ? "eff" : "el")},z = {F(sigma, 1)} MPa; σx,Ed / (fy / γM0){(reduction < 1 ? $" / (1 − ρ) = {F(reduction, 3)}" : "")} = {F(csUtil, 3)} {Ok(csUtil <= 1)}");
        }
        log.AppendLine();

        // ---- member checks (6.3) ----
        double ltbUtil = 0, interactionY = 0, interactionZ = 0;
        var chiLt = 1.0;
        if (my > 0 && g.Kind != SteelShape.Chs)
        {
            (chiLt, _) = LateralTorsionalBuckling(my, wy, length, actions.ShapeY, log);
            var mbRd = chiLt * wy * fy / gammaM1;
            ltbUtil = my / mbRd;
            log.AppendLine($"  Mb,Rd = χLT Wy fy / γM1 = {F(chiLt, 3)} × {F(wy / 1e3, 0)} cm³ × {F(fy, 0)} / {F(gammaM1, 2)} = {F(mbRd / 1e6)} kNm → MEd,y / Mb,Rd = {F(ltbUtil, 3)} {Ok(ltbUtil <= 1)} (6.3.2.1)");
            log.AppendLine();
        }
        if (nEd > 0)
        {
            log.AppendLine("FLEXURAL BUCKLING (6.3.1) AND INTERACTION (6.3.3, Annex B)");
            var (chiY, lambdaY) = Buckling("y-y", g.Iy, l0y, true, aeff, log);
            var (chiZ, lambdaZ) = Buckling("z-z", g.Iz, l0z, false, aeff, log);
            var chi = Math.Min(chiY, chiZ);
            var nb = chi * aeff * fy / gammaM1;
            axialUtil = Math.Max(axialUtil, nEd / nb);
            log.AppendLine($"  Nb,Rd = χmin A{(c == 4 ? "eff" : "")} fy / γM1 = {F(chi, 3)} × {F(aeff, 0)} × {F(fy, 0)} / {F(gammaM1, 2)} = {F(nb / 1000, 0)} kN → NEd / Nb,Rd = {F(nEd / nb, 3)} {Ok(nEd / nb <= 1)} (6.3.1.1)");
            if (my > 0 || mz > 0)
            {
                var nrk = aeff * fy;
                var nY = nEd / (chiY * nrk / gammaM1);
                var nZ = nEd / (chiZ * nrk / gammaM1);
                var cmY = Cm(actions.ShapeY);
                var cmZ = Cm(actions.ShapeZ);
                var cmLt = cmY;
                var plastic = c <= 2;
                double kyy, kyz, kzz, kzy;
                if (plastic)
                {
                    kyy = Math.Min(cmY * (1 + (lambdaY - 0.2) * nY), cmY * (1 + 0.8 * nY));
                    kzz = g.Kind == SteelShape.I
                        ? Math.Min(cmZ * (1 + (2 * lambdaZ - 0.6) * nZ), cmZ * (1 + 1.4 * nZ))
                        : Math.Min(cmZ * (1 + (lambdaZ - 0.2) * nZ), cmZ * (1 + 0.8 * nZ));
                    kyz = 0.6 * kzz;
                }
                else
                {
                    kyy = Math.Min(cmY * (1 + 0.6 * lambdaY * nY), cmY * (1 + 0.6 * nY));
                    kzz = Math.Min(cmZ * (1 + 0.6 * lambdaZ * nZ), cmZ * (1 + 0.6 * nZ));
                    kyz = kzz;
                }
                if (g.Kind == SteelShape.I)
                {
                    // Table B.2, members susceptible to torsional deformation
                    var scale = plastic ? 0.1 : 0.05;
                    var denominator = Math.Max(cmLt - 0.25, 0.05);
                    var floor = 1 - scale / denominator * nZ;
                    kzy = lambdaZ < 0.4 && plastic ? Math.Min(0.6 + lambdaZ, 1 - scale * lambdaZ / denominator * nZ) : Math.Max(1 - scale * lambdaZ / denominator * nZ, floor);
                }
                else kzy = (plastic ? 0.6 : 0.8) * kyy;
                var myEd = my;
                var mzEd = mz;
                var myRk = wy * fy;
                var mzRk = wz * fy;
                var chiLtUsed = g.Kind == SteelShape.Chs ? 1.0 : chiLt;
                interactionY = nY + kyy * myEd / (chiLtUsed * myRk / gammaM1) + kyz * mzEd / (mzRk / gammaM1);
                interactionZ = nZ + kzy * myEd / (chiLtUsed * myRk / gammaM1) + kzz * mzEd / (mzRk / gammaM1);
                log.AppendLine($"  Cmy = {F(cmY, 3)}, Cmz = {F(cmZ, 3)}, CmLT = {F(cmLt, 3)} (Table B.3: {MomentText(actions.ShapeY)} about y-y, {MomentText(actions.ShapeZ)} about z-z; non-sway, Cm = 0.9 for a sway mode is the user's to apply through the FEM)");
                log.AppendLine($"  kyy = {F(kyy, 3)}, kyz = {F(kyz, 3)}, kzy = {F(kzy, 3)}, kzz = {F(kzz, 3)} (Table B.{(g.Kind == SteelShape.I ? 2 : 1)}, class {(plastic ? "1/2" : "3/4")})");
                log.AppendLine($"  6.61: NEd/(χy NRk/γM1) + kyy My,Ed/(χLT My,Rk/γM1) + kyz Mz,Ed/(Mz,Rk/γM1) = {F(nY, 3)} + {F(kyy * myEd / (chiLtUsed * myRk / gammaM1), 3)} + {F(kyz * mzEd / (mzRk / gammaM1), 3)} = {F(interactionY, 3)} {Ok(interactionY <= 1)}");
                log.AppendLine($"  6.62: NEd/(χz NRk/γM1) + kzy My,Ed/(χLT My,Rk/γM1) + kzz Mz,Ed/(Mz,Rk/γM1) = {F(nZ, 3)} + {F(kzy * myEd / (chiLtUsed * myRk / gammaM1), 3)} + {F(kzz * mzEd / (mzRk / gammaM1), 3)} = {F(interactionZ, 3)} {Ok(interactionZ <= 1)}");
            }
        }
        else if (nEd < 0 && (my > 0 || mz > 0))
            log.AppendLine("MEMBER IN TENSION: the stability checks reduce to the lateral-torsional check above (the tension that relieves it is ignored, on the safe side) and the cross-section check.");
        else if (my > 0 && g.Kind == SteelShape.Chs)
            log.AppendLine("CHS: not susceptible to lateral-torsional buckling (6.3.2.1(2)).");

        var majorUtil = new[] { csUtil, ltbUtil, interactionY }.Max();
        // the cross-section check about z-z alone: Mz against its resistance
        var minorUtil = Math.Max(interactionZ, mz > 0 ? mz / mzRdCs : 0);
        var shearUtil = Math.Max(shearY, shearZ);
        return new(name, nEd / 1000, my / 1e6, my / 1e6, mz / 1e6, vy / 1000, axialUtil, majorUtil, minorUtil, shearUtil, vz / 1000, M0EdZKNm: mz / 1e6);
    }

    private double Lt(double length) => S.LateralLength > 0 ? S.LateralLength : length;

    /// <summary>Equivalent uniform moment factor Cm of Table B.3: 0.6 + 0.4 ψ (≥ 0.4) for end moments; 1.0, the upper bound of the 0.90 to
    /// 1.00 the table gives, for a diagram that peaks between the ends.</summary>
    private static double Cm(MomentShape shape) => shape.BetweenEnds ? 1.0 : Math.Max(0.4, 0.6 + 0.4 * shape.Psi);

    private static string MomentText(MomentShape shape) => shape.BetweenEnds ? "loads along the member" : $"ψ = {F(shape.Psi, 2)}";

    private (double Utilization, double VcRd, double Rho) Shear(string direction, double vEd, double av, bool web, StringBuilder log)
    {
        var vpl = av * fy / (Math.Sqrt(3) * gammaM0);
        var vc = vpl;
        var note = "";
        if (web && g.Kind != SteelShape.Chs)
        {
            // shear buckling of an unstiffened web (EN 1993-1-5 5.2, 5.3 with transverse stiffeners at the supports only)
            var eta = fy <= 460 ? 1.2 : 1.0;
            var hw = g.Kind == SteelShape.I ? g.Hw : g.H - 2 * g.Tf;
            var t = g.Kind == SteelShape.I ? g.Tw : g.Tf;
            if (hw / t > 72 * Epsilon / eta)
            {
                var lambdaW = hw / (86.4 * t * Epsilon);
                var chiW = lambdaW < 0.83 / eta ? eta : lambdaW < 1.08 ? 0.83 / lambdaW : 1.37 / (0.7 + lambdaW);
                var vb = chiW * fy * hw * g.WebThicknessTotal / (Math.Sqrt(3) * gammaM1);
                note = $"; web hw/tw = {F(hw / t, 1)} > 72ε/η = {F(72 * Epsilon / eta, 1)}: Vb,Rd = χw fyw hw tw / (√3 γM1) = {F(chiW, 3)} → {F(vb / 1000)} kN (EN 1993-1-5 5.2)";
                vc = Math.Min(vpl, vb);
            }
        }
        var utilization = vEd / vc;
        var rho = vEd > 0.5 * vc ? Math.Pow(2 * vEd / vc - 1, 2) : 0;
        log.AppendLine($"SHEAR {direction.ToUpperInvariant()} (6.2.6)");
        log.AppendLine($"  Av = {F(av, 0)} mm²; Vpl,Rd = Av fy / (√3 γM0) = {F(vpl / 1000)} kN{note}");
        log.AppendLine($"  VEd / Vc,Rd = {F(vEd / 1000)} / {F(vc / 1000)} = {F(utilization, 3)} {Ok(utilization <= 1)}" +
                       (rho > 0 ? $"; VEd > 0.5 Vc,Rd → ρ = (2 VEd / Vc,Rd − 1)² = {F(rho, 3)}, the bending resistance is reduced (6.2.8)" : ""));
        return (utilization, vc, rho);
    }

    /// <summary>The reduction of the major-axis bending resistance of a class 1 / 2 section by a high shear: 1 when ρ = 0; the reduced
    /// plastic modulus (Wpl − ρ Aw² / (4 tw)) over Wpl for an I or hollow section (6.2.8(5)), (1 − ρ) for a circular tube.</summary>
    private double ShearReduction(bool major, double rho, StringBuilder log)
    {
        if (rho <= 0 || !major) return 1;
        double factor;
        if (g.Kind == SteelShape.Chs) factor = 1 - rho;
        else
        {
            var hw = g.Hw;
            factor = Math.Max(0, (g.Wply - rho * hw * hw * g.WebThicknessTotal / 4) / g.Wply);
        }
        log.AppendLine($"  My,V,Rd = {F(factor, 3)} Mpl,y,Rd with the shear area at (1 − ρ) fy");
        return factor;
    }

    private (double Chi, double Lambda) Buckling(string axis, double inertia, double l0, bool major, double aeff, StringBuilder log)
    {
        var ncr = Math.PI * Math.PI * S.Ea * inertia / (l0 * l0);
        var lambda = Math.Sqrt(aeff * fy / ncr);
        var (curve, alpha) = BucklingCurve(major);
        var chi = ColumnDesignEngine.Chi(lambda, alpha);
        log.AppendLine($"  {axis}: Ncr = π² E I / l0² = {F(ncr / 1000, 0)} kN (l0 = {F(l0, 0)} mm), λ̄ = √(A fy / Ncr) = {F(lambda, 3)}; curve {curve} (α = {F(alpha, 2)}, Table 6.2) → χ = {F(chi, 3)}");
        return (chi, lambda);
    }

    /// <summary>The buckling curve of Table 6.2 for the section, its manufacture and the axis; the S460 column differs from S235–S420.</summary>
    private (string Curve, double Alpha) BucklingCurve(bool major)
    {
        var high = S.Fy >= 460;
        string curve;
        if (g.Kind != SteelShape.I) curve = g.ColdFormed ? "c" : high ? "a0" : "a";
        else
        {
            var thick = g.Tf;
            var deep = g.H / g.B > 1.2;
            if (g.Welded) curve = thick <= 40 ? (major ? "b" : "c") : (major ? "c" : "d");
            else if (deep) curve = thick <= 40 ? (high ? "a0" : major ? "a" : "b") : (high ? "a" : major ? "b" : "c");
            else curve = thick <= 100 ? (high ? "a" : major ? "b" : "c") : (high ? "c" : "d");
        }
        return (curve, CurveAlpha(curve));
    }

    private static double CurveAlpha(string curve) => curve switch { "a0" => 0.13, "a" => 0.21, "b" => 0.34, "c" => 0.49, _ => 0.76 };

    private (double Chi, double Lambda) LateralTorsionalBuckling(double my, double wy, double length, MomentShape shape, StringBuilder log)
    {
        var l = Lt(length);
        var e = S.Ea;
        var c1 = Math.Abs(l - length) < 0.01 * length ? Math.Min(2.7, shape.Cb) : 1.0;
        // elastic critical moment, simply supported ends with the load at the shear centre (zg = 0)
        var mcr = c1 * Math.PI * Math.PI * e * g.Iz / (l * l) * Math.Sqrt(g.Iw / g.Iz + l * l * G * g.It / (Math.PI * Math.PI * e * g.Iz));
        var lambda = Math.Sqrt(wy * fy / mcr);
        log.AppendLine("LATERAL-TORSIONAL BUCKLING (6.3.2)");
        log.AppendLine($"  LLT = {F(l, 0)} mm; C1 = {F(c1, 3)} ({(c1 == 1 ? "1.0: the lateral length is shorter than the member, or the moment is uniform" : "Cb of the quarter-point moments, an approximation of the tabulated C1")}); " +
                       $"Mcr = {F(mcr / 1e6)} kNm; λ̄LT = √(Wy fy / Mcr) = {F(lambda, 3)}");
        var curveText = LtbCurve();
        double chi;
        if (generalLtb)
        {
            var alpha = CurveAlpha(curveText);
            var exempt = lambda <= 0.2 || my / mcr <= 0.04;
            chi = exempt ? 1.0 : ColumnDesignEngine.Chi(lambda, alpha);
            log.AppendLine($"  General case (6.3.2.2): curve {curveText} (α = {F(alpha, 2)}, Table 6.4) → χLT = {F(chi, 3)}" + (exempt ? " (λ̄LT ≤ 0.2 or MEd/Mcr ≤ 0.04: no reduction, 6.3.2.2(4))" : ""));
        }
        else
        {
            var alpha = CurveAlpha(curveText);
            const double lambda0 = 0.4, beta = 0.75;
            if (lambda <= lambda0 || my / mcr <= lambda0 * lambda0)
            {
                chi = 1.0;
                log.AppendLine($"  λ̄LT ≤ λLT,0 = 0.4 or MEd / Mcr = {F(my / mcr, 3)} ≤ λLT,0² = 0.16: no lateral-torsional reduction (6.3.2.3(1))");
            }
            else
            {
                var phi = 0.5 * (1 + alpha * (lambda - lambda0) + beta * lambda * lambda);
                chi = Math.Min(1.0, Math.Min(1 / (phi + Math.Sqrt(phi * phi - beta * lambda * lambda)), 1 / (lambda * lambda)));
                var kc = 1 / Math.Sqrt(c1);
                var f = Math.Min(1.0, 1 - 0.5 * (1 - kc) * (1 - 2 * Math.Pow(lambda - 0.8, 2)));
                chi = Math.Min(1.0, chi / f);
                log.AppendLine($"  Rolled sections (6.3.2.3): curve {curveText} (α = {F(alpha, 2)}, Table 6.5), λLT,0 = 0.4, β = 0.75; kc = 1/√C1 = {F(kc, 3)}, f = {F(f, 3)}");
            }
            log.AppendLine($"  → χLT = {F(chi, 3)}");
        }
        return (chi, lambda);
    }

    /// <summary>The LTB curve: Table 6.4 for the general case (rolled I h/b ≤ 2: a, > 2: b; welded: c, d), Table 6.5 for 6.3.2.3 (b, c; c, d);
    /// a hollow section takes curve d, the table having no entry (its Mcr is high, so λ̄LT is small).</summary>
    private string LtbCurve()
    {
        if (g.Kind != SteelShape.I) return "d";
        var ratio = g.H / g.B;
        if (g.Welded) return ratio <= 2 ? "c" : "d";
        if (generalLtb) return ratio <= 2 ? "a" : "b";
        return ratio <= 2 ? "b" : "c";
    }

    public override IReadOnlyList<DetailingCheck> Detailing(double maxNEd, double length, double l0y, double l0z)
    {
        var maxSteel = GradeNumber(Basis.MaxSteelGrade);
        var pure = g.Classify(0, 1, 0, fy);
        var compressed = g.Classify(Math.Max(1, maxNEd), 0, 0, fy);
        var checks = new List<DetailingCheck>
        {
            new("Steel grade in scope", "3.2.1 / design basis", S.Fy <= maxSteel, $"fy = {F(S.Fy, 0)} MPa ≤ {Basis.MaxSteelGrade}" + (S.Fy > 460 ? " — beyond EC3 S460 (EN 1993-1-12 for higher grades)" : "")),
            new("Thickness in scope", "Table 3.1", g.MaxThickness <= 80,
                $"t = {F(g.MaxThickness, 1)} {(g.MaxThickness <= 80 ? "≤" : ">")} 80 mm" +
                (g.MaxThickness > 80 ? $" — beyond Table 3.1: the fy of the 80 mm row ({F(fy, 0)} MPa) is used here, the code gives none; take fy from the product standard or test data" :
                 g.MaxThickness > 40 ? $"; fy reduced to {F(fy, 0)} MPa" : "")),
            new("Section dimensions", "geometry", g.Kind == SteelShape.I ? 2 * g.Tf < g.H && g.Tw < g.B : 2 * g.Tf < Math.Min(g.H, g.B) && g.Tf > 0,
                "the walls must fit inside the outline"),
            new("Cross-section class", "5.5 Table 5.2", !(g.Kind == SteelShape.Chs && Math.Max(pure.Class, compressed.Class) == 4),
                $"class {pure.Class} in y-y bending, class {compressed.Class} in compression" + (Math.Max(pure.Class, compressed.Class) == 4 ? (g.Kind == SteelShape.Chs ? " — a class 4 CHS is outside the scope (EN 1993-1-6)" : " — effective section (EN 1993-1-5) used") : "")),
        };
        if (g.Kind != SteelShape.Chs)
        {
            var hw = g.Kind == SteelShape.I ? g.Hw : g.H - 2 * g.Tf;
            var t = g.Kind == SteelShape.I ? g.Tw : g.Tf;
            var eta = fy <= 460 ? 1.2 : 1.0;
            var limit = 72 * Epsilon / eta;
            checks.Add(new("Web shear buckling", "EN 1993-1-5 5.1", true, hw / t <= limit ? $"hw/tw = {F(hw / t, 1)} ≤ 72ε/η = {F(limit, 1)}: no shear buckling check needed" : $"hw/tw = {F(hw / t, 1)} > 72ε/η = {F(limit, 1)}: Vb,Rd of the unstiffened web governs the shear"));
        }
        return checks;
    }
}
