using System.Collections.Immutable;

namespace Mcotis.Analysis;

/// <summary>What the stiffness of a case, factorized, says of the equations as a whole.</summary>
public enum StabilityState
{
    /// <summary>Factorized within the solver's pivot tolerance: the case can be solved, and every equation has an estimate.</summary>
    PositiveDefinite,
    /// <summary>A pivot fell below the solver's tolerance, so the case is refused; the stiffness is still positive
    /// definite at double precision and every equation has an estimate.</summary>
    NearlySingular,
    /// <summary>A degree of freedom has no stiffness at all, or the stiffness has a mode without stiffness at double precision.</summary>
    Singular,
    /// <summary>A pivot is negative by more than rounding: the stiffness is not positive definite.</summary>
    NotPositiveDefinite,
}

/// <summary>What is known of one solved degree of freedom.</summary>
public enum StabilityCondition
{
    /// <summary><see cref="StaticDofStability.DigitsLost"/> holds log10(Kᵢᵢ·(K⁻¹)ᵢᵢ).</summary>
    Estimated,
    /// <summary>Its diagonal stiffness is exactly zero: no member reaches this movement and no restraint holds it.</summary>
    NoStiffness,
    /// <summary>As <see cref="NoStiffness"/> in this case only: the cables that reach it are slack.</summary>
    SlackCablesOnly,
    /// <summary>It moves in a mode the stiffness does not resist at double precision.</summary>
    ZeroStiffnessMode,
    /// <summary>The pivot found negative is at this equation. Which equation that is depends on the elimination order.</summary>
    NegativePivot,
    /// <summary>No estimate: the stiffness could not be factorized, and this degree of freedom is in none of the modes found.</summary>
    NotEstimated,
}

public enum StabilitySeverity { None, Warning, Severe }

/// <summary>Where in a case the stiffness was assessed: at its solution — the converged state of an iterated case —
/// or during an equilibrium iteration that could not go on.</summary>
public enum StabilityStage { Solution, EquilibriumIteration }

/// <summary>The digits of accuracy lost from which a degree of freedom is reported: from
/// <paramref name="WarningDigits"/> as a warning, above <paramref name="SevereDigits"/> as severe. The defaults follow
/// the reporting convention of CSI's analysis programs (nothing below 6, a warning above 11); the estimate itself is
/// this solver's own (docs/solver/numerical-stability-diagnostics.md).</summary>
public sealed record StabilityThresholds(double WarningDigits = 6, double SevereDigits = 11)
{
    public static StabilityThresholds Default { get; } = new();

    public StabilitySeverity Classify(double digitsLost) => digitsLost > SevereDigits ? StabilitySeverity.Severe
        : digitsLost >= WarningDigits ? StabilitySeverity.Warning : StabilitySeverity.None;
}

/// <param name="DigitsLost">Only with <see cref="StabilityCondition.Estimated"/>; never a stand-in number.</param>
public sealed record StaticDofStability(DofKey Dof, StabilityCondition Condition, double? DigitsLost, StabilitySeverity Severity)
{
    /// <summary>A singularity was identified at this degree of freedom: the loss of accuracy has no bound.</summary>
    public bool IsInfinite => Condition is StabilityCondition.NoStiffness or StabilityCondition.SlackCablesOnly or StabilityCondition.ZeroStiffnessMode;
}

/// <summary>The numerical stability of one case, from the stiffness on its solved degrees of freedom.</summary>
/// <param name="Iteration">Of <see cref="StabilityStage.EquilibriumIteration"/>: the iteration, from 1; else 0.</param>
/// <param name="Equations">Solved degrees of freedom assessed.</param>
/// <param name="ZeroStiffnessModes">Independent movements without stiffness that were found.</param>
/// <param name="LargestDigitsLost">The largest estimate over every equation, reported or not; null when there is none.</param>
/// <param name="Warnings">Degrees of freedom at <see cref="StabilitySeverity.Warning"/>.</param>
/// <param name="Instabilities">Degrees of freedom at <see cref="StabilitySeverity.Severe"/>, singular ones included.</param>
/// <param name="Dofs">Every solved degree of freedom of the joints that have one above <see cref="StabilitySeverity.None"/>,
/// in the order of the model. Empty when nothing is reported.</param>
public sealed record StaticStability(StabilityState State, StabilityStage Stage, int Iteration, int Equations, int ZeroStiffnessModes,
    double? LargestDigitsLost, DofKey? LargestAt, int Warnings, int Instabilities, ImmutableArray<StaticDofStability> Dofs,
    StabilityThresholds Thresholds)
{
    /// <summary>Movements without stiffness that were held so that the case could be solved
    /// (<see cref="StaticOptions.HoldUnloadedMechanisms"/>): the loads of the case do no work on them, the case has a
    /// result, its displacements are determined up to those movements and its forces are not affected. 0 when none
    /// was held — then a singular stiffness has no result.</summary>
    public int MovementsHeld { get; init; }
}

/// <summary>The conditioning of a symmetric stiffness, equation by equation. It reads the matrix and changes nothing
/// of a solution.
/// <para><b>Estimate.</b> cᵢ = Kᵢᵢ·(K⁻¹)ᵢᵢ ≥ 1 is the stiffness of degree of freedom i with every other one held, over
/// its stiffness with every other one free. Forming the second from the first cancels log10(cᵢ) decimal digits; it is
/// the pivot ratio equation i would have were it eliminated last, so it does not depend on the elimination order. With
/// H the stiffness scaled to a unit diagonal, κ₂(H)/n² ≤ maxᵢ cᵢ ≤ κ₂(H), and κ₂(H) bounds the relative error of the
/// scaled solution of a Cholesky factorization (Demmel 1989; Higham 2002, §10.1).</para>
/// <para><b>Singularity.</b> A stiffness that the natural-order factorization refuses is factorized again with
/// diagonal pivoting (Higham 1990; LAPACK xPSTRF), which stops when what remains is no larger than rounding error
/// (<see cref="RoundingLevel"/>). The equations left then span the modes without stiffness; a degree of freedom moves
/// in one when its component there, in the energy scaling √Kᵢᵢ·uᵢ, exceeds <see cref="ModeParticipation"/> of the
/// largest.</para></summary>
internal static class StiffnessConditioning
{
    public const double ModeParticipation = 1e-6;
    /// <summary>Allowance, in units of ε, for the rounding the stiffness already carries when it is factorized: its
    /// entries are assembled and scaled in floating point, to about ten ε each, in rows whose absolute sum is about ten.</summary>
    public const double AssemblyAllowance = 100;
    private const double Epsilon = 2.220446049250313e-16;

    /// <param name="Digits">NaN where the condition is not <see cref="StabilityCondition.Estimated"/>.</param>
    /// <param name="SmallestPivot">Of the pivoted factorization, as a fraction of the diagonal stiffness: the smallest
    /// pivot taken, or what remained when it stopped. 1 when no factorization was made here.</param>
    /// <param name="SmallestKeptPivot">The smallest pivot the pivoted factorization took before it stopped — what
    /// is left of the stiffness when its movements without stiffness are held. 1 when none was taken.</param>
    internal sealed record Assessment(StabilityState State, StabilityCondition[] Conditions, double[] Digits, int ZeroStiffnessModes,
        double SmallestPivot = 1, double SmallestKeptPivot = 1)
    {
        /// <summary>Singular only by movements the stiffness does not resist — nothing negative, and no equation
        /// that stands alone without any stiffness: holding those movements leaves a stiffness that can be solved.</summary>
        public bool CanBeHeld => State == StabilityState.Singular && ZeroStiffnessModes > 0 &&
            !Conditions.Any(condition => condition is StabilityCondition.NoStiffness or StabilityCondition.NegativePivot);
    }

    /// <summary>What remains of a unit-diagonal matrix of this size below this cannot be told from zero: n·ε, the
    /// stopping tolerance of LAPACK's pivoted Cholesky for a matrix that is given exactly, and
    /// <see cref="AssemblyAllowance"/>·ε for one that is not.</summary>
    public static double RoundingLevel(int n) => (n + AssemblyAllowance) * Epsilon;

    /// <summary>The estimates of a stiffness the solver has factorized.</summary>
    public static Assessment Estimate(ScaledCholesky factor, CancellationToken cancellationToken = default)
    {
        var inverse = factor.ScaledInverseDiagonal(cancellationToken);
        var conditions = new StabilityCondition[inverse.Length];
        var digits = new double[inverse.Length];
        for (int i = 0; i < inverse.Length; i++) Set(conditions, digits, i, inverse[i]);
        return new(StabilityState.PositiveDefinite, conditions, digits, 0);
    }

    private static void Set(StabilityCondition[] conditions, double[] digits, int index, double scaledInverse)
    {
        // cᵢ ≥ 1 in exact arithmetic; rounding can leave it a hair below
        double value = Math.Log10(Math.Max(1, scaledInverse));
        bool known = double.IsFinite(value);
        conditions[index] = known ? StabilityCondition.Estimated : StabilityCondition.NotEstimated;
        digits[index] = known ? value : double.NaN;
    }

    /// <summary>A stiffness the solver could not factorize: which equations are singular, and the estimates that
    /// still exist.</summary>
    public static Assessment Assess(double[,] stiffness, CancellationToken cancellationToken = default)
    {
        int n = stiffness.GetLength(0);
        var conditions = new StabilityCondition[n];
        var digits = new double[n];
        Array.Fill(conditions, StabilityCondition.NotEstimated);
        Array.Fill(digits, double.NaN);
        bool indefinite = false;
        int unstiffened = 0;
        var kept = new List<int>();
        for (int i = 0; i < n; i++)
        {
            double diagonal = stiffness[i, i];
            if (diagonal > 0 && double.IsFinite(diagonal)) kept.Add(i);
            else if (diagonal == 0)
            {
                // a zero diagonal of a positive semi-definite matrix has a zero row: the equation stands alone
                bool alone = true;
                for (int j = 0; j < n && alone; j++) alone = stiffness[i, j] == 0 && stiffness[j, i] == 0;
                conditions[i] = alone ? StabilityCondition.NoStiffness : StabilityCondition.NegativePivot;
                if (alone) unstiffened++; else indefinite = true;
            }
            else if (diagonal < 0)
            {
                conditions[i] = StabilityCondition.NegativePivot;
                indefinite = true;
            }
        }

        int m = kept.Count;
        var a = new double[m, m];
        for (int i = 0; i < m; i++)
        for (int j = 0; j < m; j++)
            a[i, j] = stiffness[kept[i], kept[j]] / Math.Sqrt(stiffness[kept[i], kept[i]]) / Math.Sqrt(stiffness[kept[j], kept[j]]);
        var order = Enumerable.Range(0, m).ToArray();
        double rounding = RoundingLevel(m), smallest = 1, smallestKept = 1;
        int rank = 0;
        // Cholesky with diagonal pivoting: columns before 'rank' hold L below the diagonal, the rest what remains.
        for (; rank < m; rank++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int pivot = rank;
            for (int i = rank + 1; i < m; i++)
                if (a[i, i] > a[pivot, pivot]) pivot = i;
            smallest = Math.Min(smallest, a[pivot, pivot]);
            if (!(a[pivot, pivot] > rounding)) break;
            smallestKept = Math.Min(smallestKept, a[pivot, pivot]);
            if (pivot != rank)
            {
                for (int c = 0; c < m; c++) (a[rank, c], a[pivot, c]) = (a[pivot, c], a[rank, c]);
                for (int r = 0; r < m; r++) (a[r, rank], a[r, pivot]) = (a[r, pivot], a[r, rank]);
                (order[rank], order[pivot]) = (order[pivot], order[rank]);
            }
            double root = Math.Sqrt(a[rank, rank]);
            a[rank, rank] = root;
            for (int i = rank + 1; i < m; i++) a[i, rank] /= root;
            for (int i = rank + 1; i < m; i++)
            for (int k = rank + 1; k < m; k++) a[i, k] -= a[i, rank] * a[k, rank];
        }

        if (rank == m)
        {
            // positive definite at double precision: the estimate exists, from the pivoted factor
            var column = new double[m];
            for (int p = 0; p < m; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double sum = 0;
                for (int k = p; k < m; k++)
                {
                    double value = k == p ? 1 : 0;
                    for (int j = p; j < k; j++) value -= a[k, j] * column[j];
                    column[k] = value / a[k, k];
                    sum += column[k] * column[k];
                }
                Set(conditions, digits, kept[order[p]], sum);
            }
        }
        else
        {
            for (int q = rank; q < m; q++)
            {
                // negative by more than the rounding of the factorization: not a matter of round-off
                if (a[q, q] < -10 * rounding)
                {
                    conditions[kept[order[q]]] = StabilityCondition.NegativePivot;
                    indefinite = true;
                }
            }
            if (!indefinite)
            {
                var mode = new double[m];
                for (int q = rank; q < m; q++)
                {
                    // the mode in which equation q moves by one: L11ᵀ·x = −(row q of L)
                    Array.Clear(mode);
                    mode[q] = 1;
                    double largest = 1;
                    for (int i = rank - 1; i >= 0; i--)
                    {
                        double value = -a[q, i];
                        for (int k = i + 1; k < rank; k++) value -= a[k, i] * mode[k];
                        mode[i] = value / a[i, i];
                        largest = Math.Max(largest, Math.Abs(mode[i]));
                    }
                    for (int i = 0; i < m; i++)
                        if (Math.Abs(mode[i]) > ModeParticipation * largest) conditions[kept[order[i]]] = StabilityCondition.ZeroStiffnessMode;
                }
            }
        }

        var state = indefinite ? StabilityState.NotPositiveDefinite
            : rank < m || unstiffened > 0 ? StabilityState.Singular : StabilityState.NearlySingular;
        return new(state, conditions, digits, indefinite ? 0 : m - rank + unstiffened, smallest, smallestKept);
    }

    /// <summary>A solution of a stiffness that is singular by movements it does not resist: those movements held.
    /// <para>The stiffness is factorized with diagonal pivoting exactly as in <see cref="Assess"/>. The equations left
    /// when it stops — one for each movement without stiffness — are held at zero and the others are solved. Of the
    /// whole family u + Σ cₖ·yₖ (yₖ the movements, K·yₖ = 0) this is the member in which those equations do not move;
    /// every member has the same strains, so the same forces.</para>
    /// <para>What is returned satisfies the held equations too only if the loads do no work on the movements
    /// (yₖᵀ·f = 0): the residual of a held equation is exactly that work. This method does not judge it — the caller
    /// measures the backward error of the whole system, as for any solution.</para></summary>
    /// <returns>Null when the stiffness is not of that kind: a diagonal that is not positive, a pivot negative by
    /// more than rounding, or nothing left over.</returns>
    public static double[]? SolveHeld(double[,] stiffness, double[] load, CancellationToken cancellationToken = default)
    {
        int n = stiffness.GetLength(0);
        var scale = new double[n];
        for (int i = 0; i < n; i++)
        {
            double diagonal = stiffness[i, i];
            if (!(diagonal > 0) || !double.IsFinite(diagonal)) return null;
            scale[i] = 1 / Math.Sqrt(diagonal);
        }
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++) a[i, j] = stiffness[i, j] * scale[i] * scale[j];
        var order = Enumerable.Range(0, n).ToArray();
        double rounding = RoundingLevel(n);
        int rank = 0;
        for (; rank < n; rank++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int pivot = rank;
            for (int i = rank + 1; i < n; i++)
                if (a[i, i] > a[pivot, pivot]) pivot = i;
            if (!(a[pivot, pivot] > rounding)) break;
            if (pivot != rank)
            {
                for (int c = 0; c < n; c++) (a[rank, c], a[pivot, c]) = (a[pivot, c], a[rank, c]);
                for (int r = 0; r < n; r++) (a[r, rank], a[r, pivot]) = (a[r, pivot], a[r, rank]);
                (order[rank], order[pivot]) = (order[pivot], order[rank]);
            }
            double root = Math.Sqrt(a[rank, rank]);
            a[rank, rank] = root;
            for (int i = rank + 1; i < n; i++) a[i, rank] /= root;
            for (int i = rank + 1; i < n; i++)
            for (int k = rank + 1; k < n; k++) a[i, k] -= a[i, rank] * a[k, rank];
        }
        if (rank == n) return null;
        for (int q = rank; q < n; q++)
            if (a[q, q] < -10 * rounding) return null;

        // L11·y = b, then L11ᵀ·v = y, in the scaled and reordered equations; the equations left over stay at zero
        var v = new double[rank];
        for (int i = 0; i < rank; i++)
        {
            double value = load[order[i]] * scale[order[i]];
            for (int k = 0; k < i; k++) value -= a[i, k] * v[k];
            v[i] = value / a[i, i];
        }
        for (int i = rank - 1; i >= 0; i--)
        {
            double value = v[i];
            for (int k = i + 1; k < rank; k++) value -= a[k, i] * v[k];
            v[i] = value / a[i, i];
        }
        var solution = new double[n];
        for (int i = 0; i < rank; i++) solution[order[i]] = v[i] * scale[order[i]];
        return solution;
    }
}
