using System.Numerics;
using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>
/// Tab 02 loading candidate space. A loading scenario needs a value for <b>every</b> load definition,
/// so the candidate count is the <b>Cartesian product</b> of each definition's candidate values —
/// N_L = ∏ CandidateValues.Count — not the sum. Same shape as <see cref="PretensionCandidates"/>.
/// </summary>
public static class LoadingCandidates
{
    public static BigInteger TotalCount(IEnumerable<LoadDefinition> definitions)
    {
        BigInteger total = BigInteger.One;
        bool any = false;
        foreach (var d in definitions)
        {
            any = true;
            int n = d.CandidateValues.Count;
            if (n <= 0) return BigInteger.Zero;
            total *= n;
        }
        return any ? total : BigInteger.Zero;
    }

    /// <summary>Lazily yields every Cartesian combination (odometer, one list per item).</summary>
    public static IEnumerable<LoadingCombination> Enumerate(IReadOnlyList<LoadDefinition> definitions)
    {
        if (definitions.Count == 0) yield break;
        var values = new IReadOnlyList<double>[definitions.Count];
        var names = new string[definitions.Count];
        for (int i = 0; i < definitions.Count; i++)
        {
            values[i] = definitions[i].CandidateValues;
            names[i] = string.IsNullOrWhiteSpace(definitions[i].LoadName) ? $"Load_{i + 1}" : definitions[i].LoadName.Trim();
            if (values[i].Count == 0) yield break;
        }

        var cursor = new int[definitions.Count];
        long index = 0;
        while (true)
        {
            var loads = new (string, double)[definitions.Count];
            for (int i = 0; i < definitions.Count; i++) loads[i] = (names[i], values[i][cursor[i]]);
            yield return new LoadingCombination(index++, loads);

            int k = definitions.Count - 1;
            while (k >= 0 && ++cursor[k] >= values[k].Count) { cursor[k] = 0; k--; }
            if (k < 0) yield break;
        }
    }
}
