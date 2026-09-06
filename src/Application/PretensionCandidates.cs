using System.Numerics;
using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>
/// Pure combinatorics over the Tab 3 cable pretension configs. The total number of PRETENSION
/// candidates is the <b>Cartesian product</b> of every cable's candidate count — never the sum.
/// Counting is exact and allocation-free (BigInteger); the combinations themselves are produced
/// lazily and only when a caller (such as the frozen study snapshot factory) asks for them.
/// </summary>
public static class PretensionCandidates
{
    // Compatibility name retained; target-force assignments consume a SAP load pattern.
    public const string LoadCase = CablePretensionConfig.PretensionLoadCase;

    /// <summary>Total PRETENSION candidates = ∏ CandidateCount. 0 when there are no cables or any cable is invalid.</summary>
    public static BigInteger TotalCount(IEnumerable<CablePretensionConfig> cables)
    {
        BigInteger total = BigInteger.One;
        bool any = false;
        foreach (var cable in cables)
        {
            any = true;
            int n = cable.CandidateCount;
            if (n <= 0) return BigInteger.Zero;
            total *= n;
        }
        return any ? total : BigInteger.Zero;
    }

    /// <summary>Cables that contribute more than one candidate (i.e. a real range).</summary>
    public static int VariableCount(IEnumerable<CablePretensionConfig> cables) =>
        cables.Count(c => c.CandidateCount > 1);

    /// <summary>
    /// Lazily yields every Cartesian combination as an odometer over each cable's candidate forces.
    /// One dictionary is allocated per yielded combination; nothing is buffered. Yields nothing when
    /// there are no cables or any cable currently has no valid candidates.
    /// </summary>
    public static IEnumerable<PretensionCombination> Enumerate(IReadOnlyList<CablePretensionConfig> cables)
    {
        if (cables.Count == 0) yield break;
        var forces = new IReadOnlyList<double>[cables.Count];
        for (int i = 0; i < cables.Count; i++)
        {
            forces[i] = cables[i].GetCandidateForces();
            if (forces[i].Count == 0) yield break;
        }

        var cursor = new int[cables.Count];
        long index = 0;
        while (true)
        {
            var slice = new Dictionary<string, double>(cables.Count, StringComparer.Ordinal);
            for (int i = 0; i < cables.Count; i++) slice[cables[i].CableName] = forces[i][cursor[i]];
            yield return new PretensionCombination(index++, slice);

            int k = cables.Count - 1;
            while (k >= 0 && ++cursor[k] >= forces[k].Count) { cursor[k] = 0; k--; }
            if (k < 0) yield break;
        }
    }
}
