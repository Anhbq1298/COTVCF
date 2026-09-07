using System.Numerics;
using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>
/// Pure combinatorics over the Tab 3 cable pretension configs. The total number of PRETENSION
/// candidates is the <b>Cartesian product</b> of every <i>independent</i> cable's candidate count.
/// A cable whose <see cref="CablePretensionConfig.SyncMode"/> is Same Values or Same Step, referencing a
/// valid independent cable, is a <i>follower</i>: it adds no axis and is zipped to its reference by
/// candidate index — Same Values copies the reference's force, Same Step keeps its own Min / Max / Step
/// range. Only Independent → Independent references are honoured (no chains, no cycles); anything else is
/// treated as an independent axis. Counting is exact and allocation-free (BigInteger).
/// </summary>
public static class PretensionCandidates
{
    // Compatibility name retained; target-force assignments consume a SAP load pattern.
    public const string LoadCase = CablePretensionConfig.PretensionLoadCase;

    private static string? ReferenceOf(CablePretensionConfig c) =>
        c.SyncMode == CableSyncMode.Independent || string.IsNullOrEmpty(c.ReferenceCableName)
            ? null : c.ReferenceCableName;

    /// <summary>True when <paramref name="cable"/> follows another cable that exists in <paramref name="all"/>
    /// and is itself Independent (no chains, no self-reference).</summary>
    public static bool IsFollower(CablePretensionConfig cable, IReadOnlyList<CablePretensionConfig> all)
    {
        var name = ReferenceOf(cable);
        return name is not null
            && all.Any(other => !ReferenceEquals(other, cable)
                                && other.CableName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                && other.SyncMode == CableSyncMode.Independent);
    }

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

    /// <summary>Cables that currently reference this one (via Same Values or Same Step).</summary>
    public static IReadOnlyList<CablePretensionConfig> FollowersOf(
        CablePretensionConfig master, IReadOnlyList<CablePretensionConfig> cables) =>
        cables.Where(c => !ReferenceEquals(c, master)
                          && ReferenceOf(c) is { } r
                          && r.Equals(master.CableName, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Returns the node names of the first dependency cycle found among the cable references, or
    /// an empty list when the graph is acyclic. Defensive — the UI already prevents chains, but a
    /// hand-edited / migrated config can still carry one.</summary>
    public static IReadOnlyList<string> FindCycle(IReadOnlyList<CablePretensionConfig> cables)
    {
        var refOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cables) refOf[c.CableName] = ReferenceOf(c);

        foreach (var start in cables)
        {
            var path = new List<string>();
            var node = start.CableName;
            while (node is not null && refOf.TryGetValue(node, out var next))
            {
                int at = path.FindIndex(p => p.Equals(node, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) return path.Skip(at).ToList();   // node repeats → cycle is path[at..]
                path.Add(node);
                node = next;
            }
        }
        return [];
    }

    /// <summary>
    /// Lazily yields every Cartesian combination as an odometer over the independent cables' candidate
    /// forces. At the reference's step index j, a Same Values follower takes the reference's j-th force; a
    /// Same Step follower takes its <b>own</b> j-th force. One dictionary is allocated per yielded
    /// combination. Yields nothing when any independent cable — or a Same Step follower's own range — has
    /// no valid candidates.
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

        // Same Step followers carry their own force list — Min..Max spread over the reference's value count,
        // indexed by the reference's cursor. The follower supplies only Min / Max; its spacing is derived.
        var stepForces = new Dictionary<CablePretensionConfig, IReadOnlyList<double>>();
        foreach (var f in followers)
            if (f.SyncMode == CableSyncMode.SameStep)
            {
                var refCount = leaderForces[leaderIndex[ReferenceOf(f)!]].Count;
                var own = f.GetCandidateForces(refCount);
                if (own.Count == 0) yield break;
                stepForces[f] = own;
            }

        var cursor = new int[leaders.Count];
        long index = 0;
        while (true)
        {
            var slice = new Dictionary<string, double>(cables.Count, StringComparer.Ordinal);
            for (int i = 0; i < leaders.Count; i++) slice[leaders[i].CableName] = leaderForces[i][cursor[i]];
            foreach (var follower in followers)
            {
                int li = leaderIndex[ReferenceOf(follower)!];
                int j = cursor[li];
                slice[follower.CableName] = stepForces.TryGetValue(follower, out var own)
                    ? own[Math.Min(j, own.Count - 1)]
                    : leaderForces[li][j];
            }
            yield return new PretensionCombination(index++, slice);

            int k = leaders.Count - 1;
            while (k >= 0 && ++cursor[k] >= leaderForces[k].Count) { cursor[k] = 0; k--; }
            if (k < 0) yield break;
        }
    }
}
