using Mcotis.Core;

namespace Mcotis.Analysis;

/// <summary>
/// Which stiffness terms of a frame the active degrees of freedom of a model call on — the capability check of the
/// frame sections (user, 2026-10-14: "a stiffness component is required only when the DOF mask makes it active").
/// <para>The element stiffness is a sum of four terms, each a rigidity times a fixed matrix: EA (axial), EI22 (bending
/// about local 2), EI33 (bending about local 3) and GJ (torsion). A term is called on when, turned to global axes, it
/// moves a degree of freedom that is on: its block on the active components of the two ends is not zero. Only then does
/// the section need that property. A frame of the XZ plane analysed in the XZ plane bends about local 3 alone — its I22
/// and J act on UY, RX and RZ, which are off — so a section that holds no out-of-plane inertia is analysed there as it
/// is; in full 3D the same terms act, and the frame is refused by name. The rule is the same for XY, YZ and any custom
/// set, and for a frame whose local axes are turned (then I22 may be the one that bends in the plane).</para>
/// <para>Nothing is assumed for a term that acts: no inertia is copied from another axis and none is made up.</para>
/// </summary>
public static class FrameStiffnessDemand
{
    [Flags]
    public enum Terms { None = 0, Axial = 1, Bending22 = 2, Bending33 = 4, Torsion = 8 }

    /// <summary>A term whose block on the active degrees of freedom is below this fraction of the whole term (Frobenius
    /// norms) moves none of them: what is left is the round-off of the axes, not a stiffness.</summary>
    public const double Threshold = 1e-9;

    /// <summary>The terms of <paramref name="frame"/> that move a degree of freedom of <paramref name="mask"/>.</summary>
    public static Terms Active(ModelDocument document, FrameObject frame, DofMask mask)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(frame);
        Xyz start = document.Points[frame.StartPointId].Position, end = document.Points[frame.EndPointId].Position;
        double length = (end - start).Length;
        if (!(length > Tolerances.Length)) return Terms.None;     // a frame without length is refused by the validator
        var (axis1, axis2, axis3) = SpatialFrameElement.LocalAxes(start, end, document.AssignmentOfFrame(frame.Id).LocalAxisAngle);
        Xyz[] rows = [axis1, axis2, axis3];
        var active = Enumerable.Range(0, 12).Where(index => mask.Contains((DofComponent)(index % 6))).ToArray();
        var terms = Terms.None;
        foreach (var (term, rigidities) in new (Terms, double[])[]
                 {
                     (Terms.Axial, [1, 0, 0, 0]), (Terms.Bending22, [0, 1, 0, 0]), (Terms.Bending33, [0, 0, 1, 0]), (Terms.Torsion, [0, 0, 0, 1]),
                 })
        {
            var global = Global(SpatialFrameElement.Terms(length, rigidities[0], rigidities[1], rigidities[2], rigidities[3]), rows);
            double whole = 0, acting = 0;
            for (int i = 0; i < 12; i++)
            for (int j = 0; j < 12; j++) whole += global[i, j] * global[i, j];
            foreach (var i in active)
            foreach (var j in active) acting += global[i, j] * global[i, j];
            if (whole > 0 && Math.Sqrt(acting) > Threshold * Math.Sqrt(whole)) terms |= term;
        }
        return terms;
    }

    /// <summary>Why <paramref name="frame"/> cannot be analysed with <paramref name="mask"/> — a term that acts and whose
    /// rigidity is not positive — or null when it can. A frame without a section is reported by the compiler.</summary>
    public static string? Missing(ModelDocument document, FrameObject frame, DofMask mask)
    {
        var assignment = document.AssignmentOfFrame(frame.Id);
        if (assignment.SectionId is not { } sectionId) return null;
        var p = document.FrameSections[sectionId].Properties;
        var m = assignment.Modifiers;
        var active = Active(document, frame, mask);
        var missing = new List<string>();
        if (active.HasFlag(Terms.Bending33) && !(p.Inertia33 * m.Inertia33 > 0)) missing.Add("I33 (bending about local 3)");
        if (active.HasFlag(Terms.Bending22) && !(p.Inertia22 * m.Inertia22 > 0)) missing.Add("I22 (bending about local 2)");
        if (active.HasFlag(Terms.Torsion) && !(p.TorsionConstant * m.Torsion > 0)) missing.Add("J (torsion)");
        if (missing.Count == 0) return null;
        var on = string.Join(" ", DegreesOfFreedom.Components.Where(component => mask.Contains(component)));
        return $"Frame '{frame.Name}' needs a positive {string.Join(", ", missing)} for the degrees of freedom that are on ({on}); " +
               "its section (times its modifier) gives none, and nothing is assumed for it. Give the section that property, or analyse with degrees of freedom that do not use it.";
    }

    /// <summary>Tᵀ K T: the local term <paramref name="local"/> in global axes; <paramref name="rows"/> are the local axes.
    /// T is block diagonal — the same rotation R for each of the four triples — so each 3 × 3 block is Rᵀ K R.</summary>
    private static double[,] Global(double[,] local, Xyz[] rows)
    {
        double[,] r = { { rows[0].X, rows[0].Y, rows[0].Z }, { rows[1].X, rows[1].Y, rows[1].Z }, { rows[2].X, rows[2].Y, rows[2].Z } };
        var global = new double[12, 12];
        var kr = new double[3, 3];
        for (int bi = 0; bi < 12; bi += 3)
        for (int bj = 0; bj < 12; bj += 3)
        {
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                kr[i, j] = local[bi + i, bj] * r[0, j] + local[bi + i, bj + 1] * r[1, j] + local[bi + i, bj + 2] * r[2, j];
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                global[bi + i, bj + j] = r[0, i] * kr[0, j] + r[1, i] * kr[1, j] + r[2, i] * kr[2, j];
        }
        return global;
    }
}
