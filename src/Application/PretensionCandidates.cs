using System.Numerics;
using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>
/// Pure combinatorics over the Tab 3 cable pretension configs. The total number of PRETENSION
/// candidates is the <b>Cartesian product</b> of every <i>independent</i> cable's candidate count.
/// A cable with a valid <see cref="CablePretensionConfig.SameAsCableName"/> is a <i>follower</i>: it
/// adds no axis and instead applies the exact same target force as its leader at every step of the
/// sweep. Counting is exact and allocation-free (BigInteger); combinations are
/// produced lazily and only when a caller (such as the frozen study snapshot factory) asks for them.
/// </summary>
public static class PretensionCandidates
{
    // Compatibility name retained; target-force assignments consume a SAP load pattern.
    public const string LoadCase = CablePretensionConfig.PretensionLoadCase;

    /// <summary>True when <paramref name="cable"/> follows another cable that exists in <paramref name="all"/>
    /// and is itself independent (no chains, no self-reference).</summary>
    public static bool IsFollower(CablePretensionConfig cable, IReadOnlyList<CablePretensionConfig> all) =>
        !string.IsNullOrEmpty(cable.SameAsCableName)
        && all.Any(other => !ReferenceEquals(other, cable)
                            && other.CableName.Equals(cable.SameAsCableName, StringComparison.OrdinalIgnoreCase)
                            && string.IsNullOrEmpty(other.SameAsCableName));

    private static List<CablePretensionConfig> Leaders(IReadOnlyList<CablePretensionConfig> cables) =>
        cables.Where(c => !IsFollower(c, cables)).ToList();

    /// <summary>Total PRETENSION candidates = ∏ CandidateCount over the independent cables. 0 when there are
    /// no cables or any cable (leader or follower) is invalid.</summary>
    public static BigInteger TotalCount(IReadOnlyList<CablePretensionConfig> cables)
    {
        if (cables.Count == 0) return BigInteger.Zero;
        if (cables.Any(c => c.CandidateCount <= 0)) return BigInteger.Zero;
        BigInteger total = BigInteger.One;
        foreach (var leader in Leaders(cables)) total *= leader.CandidateCount;
        return total;
    }

    /// <summary>Independent cables that contribute more than one candidate (i.e. a real range).</summary>
    public static int VariableCount(IReadOnlyList<CablePretensionConfig> cables) =>
        Leaders(cables).Count(c => c.CandidateCount > 1);

    /// <summary>
    /// Lazily yields every Cartesian combination as an odometer over the independent cables' candidate
    /// forces; each follower is assigned the identical target force as its leader for that combination.
    /// One dictionary is allocated per yielded combination — it carries every cable (leaders and
    /// followers). Yields nothing when any independent cable currently has no valid candidates.
    /// </summary>
    public static IEnumerable<PretensionCombination> Enumerate(IReadOnlyList<CablePretensionConfig> cables)
    {
        if (cables.Count == 0) yield break;
        var leaders = cables.Where(c => !IsFollower(c, cables)).ToList();
        var followers = cables.Where(c => IsFollower(c, cables)).ToList();

        var leaderForces = new IReadOnlyList<double>[leaders.Count];
        for (int i = 0; i < leaders.Count; i++)
        {
            leaderForces[i] = leaders[i].GetCandidateForces();
            if (leaderForces[i].Count == 0) yield break;
        }
        var leaderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < leaders.Count; i++) leaderIndex[leaders[i].CableName] = i;

        var cursor = new int[leaders.Count];
        long index = 0;
        while (true)
        {
            var slice = new Dictionary<string, double>(cables.Count, StringComparer.Ordinal);
            for (int i = 0; i < leaders.Count; i++) slice[leaders[i].CableName] = leaderForces[i][cursor[i]];
            foreach (var follower in followers)
                slice[follower.CableName] = leaderForces[leaderIndex[follower.SameAsCableName!]][cursor[leaderIndex[follower.SameAsCableName!]]];
            yield return new PretensionCombination(index++, slice);

            int k = leaders.Count - 1;
            while (k >= 0 && ++cursor[k] >= leaderForces[k].Count) { cursor[k] = 0; k--; }
            if (k < 0) yield break;
        }
    }
}
