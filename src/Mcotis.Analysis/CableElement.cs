using Mcotis.Core;

namespace Mcotis.Analysis;

/// <summary>A cable between two points: axial, tension only, small displacements. CABLE-01/02/03 in
/// docs/analysis/cable-static-verification.md.
/// <para>Straight cable: T = max(0, T0 + k·e), with k = EA / L, e the change of the chord length and T0 the tension the
/// cable would have at the model geometry — its lack of fit × k.</para>
/// <para>Loaded along its length (its self-weight) it hangs in a shallow parabola: Irvine, <i>Cable Structures</i>, MIT
/// Press, 1981. The curve is w_n²L³ / 24T² (= a / T²) longer than the chord, so <c>T / k − a / T² = (T0 + k·e) / k</c>.
/// That root is unique and positive — a cable with weight sags further instead of going slack — and its slope dT/de
/// is the equivalent stiffness of Ernst, Der Bauingenieur 40(2), 1965: k / (1 + 2·k·a / T³).</para>
/// The same laws as the archived planar solver, in three dimensions.</summary>
internal sealed record CableElement(CableObject Cable, Xyz Axis, double Length, double AxialStiffness, int[] Indices, double NormalLoad)
{
    /// <summary>Below this tension [kN] a straight cable is slack.</summary>
    public const double SlackTolerance = 1e-8;

    /// <summary>a = w_n²·L³ / 24 [kN²·m]: the cable's curve is a / T² longer than its chord. Zero for a straight cable.</summary>
    public double SagParameter => NormalLoad * NormalLoad * Length * Length * Length / 24;

    /// <summary>Change of the chord length [m] at the given full displacement vector.</summary>
    public double Extension(double[] displacement) =>
        Axis.X * (displacement[Indices[3]] - displacement[Indices[0]]) +
        Axis.Y * (displacement[Indices[4]] - displacement[Indices[1]]) +
        Axis.Z * (displacement[Indices[5]] - displacement[Indices[2]]);

    /// <summary>The cable tension. A straight cable's may come out negative — the caller treats that as slack; a sagging
    /// cable's is always positive.</summary>
    public double Tension(double pretension, double[] displacement)
    {
        double straight = pretension + AxialStiffness * Extension(displacement);
        return SagParameter > 0 ? SaggingTension(AxialStiffness, SagParameter, straight) : straight;
    }

    public bool IsActive(double tension) => SagParameter > 0 || tension > SlackTolerance;

    /// <summary>dT/de at this tension.</summary>
    public double Tangent(double tension) => SagParameter > 0
        ? AxialStiffness / (1 + 2 * AxialStiffness * SagParameter / (tension * tension * tension))
        : AxialStiffness;

    /// <summary>∫T de from the slack state. Sagging: de = (1/k + 2a/T³)·dT gives T²/2k − 2a/T, convex in the extension
    /// like the straight law.</summary>
    public double Energy(double tension) => SagParameter > 0
        ? tension * tension / (2 * AxialStiffness) - 2 * SagParameter / tension
        : tension > 0 ? tension * tension / (2 * AxialStiffness) : 0;

    /// <summary>The positive root of T³ − s·T² − k·a = 0. Right of its root the cubic is increasing and convex, so
    /// Newton from the upper bound max(s, 0) + ∛(k·a) comes down onto the root monotonically.</summary>
    internal static double SaggingTension(double axialStiffness, double sagParameter, double straight)
    {
        double q = axialStiffness * sagParameter;
        double tension = Math.Max(straight, 0) + Math.Cbrt(q);
        for (int iteration = 0; iteration < 100; iteration++)
        {
            double value = tension * tension * (tension - straight) - q;
            double slope = tension * (3 * tension - 2 * straight);
            if (!(slope > 0)) break;
            double next = tension - value / slope;
            if (!(next > 0) || !double.IsFinite(next)) break;
            double change = Math.Abs(next - tension);
            tension = next;
            if (change <= 1e-13 * Math.Max(1, tension)) break;
        }
        return tension;
    }
}
