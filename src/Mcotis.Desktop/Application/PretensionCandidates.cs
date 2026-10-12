using System.Numerics;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application;

/// <summary>
/// Pure combinatorics over the Tab 3 cable pretension configs. The total number of PRETENSION candidates
/// is the <b>Cartesian product</b> of every <i>Independent</i> cable's candidate count. A cable in any
/// other <see cref="CableSyncMode"/> is a <i>follower</i>: it adds no axis and is zipped, by candidate
/// index, to the Independent cable at the end of its reference chain (Same Values copies the resolved
/// value, Same Step derives its own spacing over that count, Same Ratio / Same Offset scale or shift it).
/// Chains resolve transitively to their Independent root; a broken or cyclic chain resolves to nothing.
/// All graph logic lives in <see cref="CableSyncGraph"/>. Counting is exact and allocation-free (BigInteger).
/// </summary>
public static class PretensionCandidates
{
    // Compatibility name retained; target-force assignments consume a SAP load pattern.
    public const string LoadCase = CablePretensionConfig.PretensionLoadCase;

    public static bool IsFollower(CablePretensionConfig cable, IReadOnlyList<CablePretensionConfig> all) =>
        CableSyncGraph.IsFollower(cable, all);

    private static List<CablePretensionConfig> Leaders(IReadOnlyList<CablePretensionConfig> cables) =>
        cables.Where(c => !CableSyncGraph.IsFollower(c, cables)).ToList();

    /// <summary>Total PRETENSION candidates = ∏ CandidateCount over the Independent cables. 0 when there are
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

    /// <summary>Distinct sync groups (one per Independent cable that has ≥ 1 follower) and the total follower count.</summary>
    public static (int Groups, int Followers) SyncGroupCounts(IReadOnlyList<CablePretensionConfig> cables)
    {
        var followers = cables.Where(c => CableSyncGraph.IsFollower(c, cables)).ToList();
        var roots = followers.Select(f => CableSyncGraph.RootOf(f, cables))
            .Where(r => r is not null).Distinct().Count();
        return (roots, followers.Count);
    }

    /// <summary>Cables that reference <paramref name="master"/> directly.</summary>
    public static IReadOnlyList<CablePretensionConfig> FollowersOf(
        CablePretensionConfig master, IReadOnlyList<CablePretensionConfig> cables) =>
        cables.Where(c => !ReferenceEquals(c, master)
                          && CableSyncGraph.ReferenceOf(c) is { } r
                          && r.Equals(master.CableName, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Node names of the first reference cycle, or an empty list when the graph is acyclic.</summary>
    public static IReadOnlyList<string> FindCycle(IReadOnlyList<CablePretensionConfig> cables)
    {
        var refOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cables) refOf[c.CableName] = CableSyncGraph.ReferenceOf(c);
        foreach (var start in cables)
        {
            var path = new List<string>();
            var node = start.CableName;
            while (node is not null && refOf.TryGetValue(node, out var next))
            {
                int at = path.FindIndex(p => p.Equals(node, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) return path.Skip(at).ToList();
                path.Add(node);
                node = next;
            }
        }
        return [];
    }

    /// <summary>Lazily yields every Cartesian combination as an odometer over the Independent cables' candidate
    /// forces. Each follower's value at the combination is its chain-resolved force list indexed by its
    /// Independent root's cursor. Yields nothing when any Independent cable — or a follower's resolved list —
    /// has no valid candidates.</summary>
    public static IEnumerable<PretensionCombination> Enumerate(IReadOnlyList<CablePretensionConfig> cables)
    {
        if (cables.Count == 0) yield break;
        var leaders = cables.Where(c => !CableSyncGraph.IsFollower(c, cables)).ToList();
        var followers = cables.Where(c => CableSyncGraph.IsFollower(c, cables)).ToList();

        var leaderForces = new IReadOnlyList<double>[leaders.Count];
        for (int i = 0; i < leaders.Count; i++)
        {
            leaderForces[i] = leaders[i].GetCandidateForces();
            if (leaderForces[i].Count == 0) yield break;
        }
        var leaderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < leaders.Count; i++) leaderIndex[leaders[i].CableName] = i;

        // Each follower's fully resolved value list + which Independent root's cursor indexes it.
        var followerForces = new Dictionary<CablePretensionConfig, IReadOnlyList<double>>();
        var followerRootIndex = new Dictionary<CablePretensionConfig, int>();
        foreach (var f in followers)
        {
            var resolved = CableSyncGraph.ResolvedForces(f, cables);
            var root = CableSyncGraph.RootOf(f, cables);
            if (resolved.Count == 0 || root is null || !leaderIndex.TryGetValue(root.CableName, out var ri)) yield break;
            followerForces[f] = resolved;
            followerRootIndex[f] = ri;
        }

        var cursor = new int[leaders.Count];
        long index = 0;
        while (true)
        {
            var slice = new Dictionary<string, double>(cables.Count, StringComparer.Ordinal);
            for (int i = 0; i < leaders.Count; i++) slice[leaders[i].CableName] = leaderForces[i][cursor[i]];
            foreach (var f in followers)
            {
                var own = followerForces[f];
                slice[f.CableName] = own[Math.Min(cursor[followerRootIndex[f]], own.Count - 1)];
            }
            yield return new PretensionCombination(index++, slice);

            int k = leaders.Count - 1;
            while (k >= 0 && ++cursor[k] >= leaderForces[k].Count) { cursor[k] = 0; k--; }
            if (k < 0) yield break;
        }
    }
}
