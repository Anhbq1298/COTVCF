using System.Globalization;

namespace Mcotis.Desktop.Application.Results;

// Report-side derivations for Cable_Forces_Report.html. Pure functions over values that were already extracted:
// no SAP, no I/O. Nothing here is persisted — a historical Study_Raw_Results.json stays a valid input.

/// <summary>Force development of one cable in one candidate: prestress target → final analysed force.</summary>
public static class CableForceMath
{
    /// <summary>Final − target [kN]. Null when either value is unavailable — never 0.</summary>
    public static double? DeltaForce(double? targetKN, double? finalKN) =>
        targetKN is { } t && finalKN is { } f && double.IsFinite(t) && double.IsFinite(f) ? f - t : null;

    /// <summary>(Final − target) / target × 100. Positive = force pickup, negative = force loss.
    /// Null (shown as N/A) when the target is 0 or unavailable, or the final force is unavailable.</summary>
    public static double? DeltaPercent(double? targetKN, double? finalKN) =>
        targetKN is { } t && finalKN is { } f && double.IsFinite(t) && double.IsFinite(f) && t != 0
            ? (f - t) / t * 100.0 : null;

    /// <summary>Force / capacity × 100. Null when the force or a positive capacity is unavailable.</summary>
    public static double? Utilisation(double? forceKN, double? capacityKN) =>
        forceKN is { } f && capacityKN is { } c && double.IsFinite(f) && double.IsFinite(c) && c > 0
            ? f / c * 100.0 : null;

    /// <summary>Short axis label of a truss height: the actual value, e.g. 20 m → "H20", 22.5 m → "H22.5".</summary>
    public static string HeightLabel(double heightM) =>
        "H" + heightM.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Deterministic natural-name order: digit runs compare as numbers, so Cable1, Cable2, Cable10 —
/// never Cable1, Cable10, Cable2. Names without digits fall back to a case-insensitive, then ordinal, order.</summary>
public sealed class NaturalNameComparer : IComparer<string>
{
    public static readonly NaturalNameComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var digits = a.SequenceCompareTo(b);
                if (digits != 0) return digits;
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}

/// <summary>Suggests a Side A / Side B split from cable names. This is a convenience for the report's
/// <b>Auto Detect</b> only — never the engineering definition of a side. A cable is suggested for a side only
/// when exactly one side marker in its name has a counterpart cable that differs by that marker alone
/// (Cable1a-2 ↔ Cable1b-2, LC-01 ↔ RC-01, LeftTie ↔ RightTie). Anything else stays Unassigned.</summary>
public static class CableGroupDetector
{
    public const string SideA = "A", SideB = "B", Unassigned = "";

    private static readonly (string First, string Second)[] WordPairs =
        [("left", "right"), ("Left", "Right"), ("LEFT", "RIGHT")];

    public static IReadOnlyDictionary<string, string> Suggest(IReadOnlyList<string> cables)
    {
        var names = new HashSet<string>(cables, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in cables)
        {
            var votes = new List<string>(2);
            for (var i = 0; i < name.Length; i++)
            {
                var partner = Partner(name[i]);
                if (partner == '\0' || !AtTokenEdge(name, i)) continue;
                if (!names.Contains(Swap(name, i, 1, partner.ToString()))) continue;
                // a, b, c … is an enumeration, not a pair of sides.
                if (char.ToUpperInvariant(name[i]) is 'A' or 'B' &&
                    names.Contains(Swap(name, i, 1, char.IsUpper(name[i]) ? "C" : "c"))) continue;
                votes.Add(char.ToUpperInvariant(name[i]) is 'A' or 'L' ? SideA : SideB);
            }
            foreach (var (first, second) in WordPairs)
            {
                for (var at = name.IndexOf(first, StringComparison.Ordinal); at >= 0;
                     at = name.IndexOf(first, at + 1, StringComparison.Ordinal))
                    if (names.Contains(Swap(name, at, first.Length, second))) votes.Add(SideA);
                for (var at = name.IndexOf(second, StringComparison.Ordinal); at >= 0;
                     at = name.IndexOf(second, at + 1, StringComparison.Ordinal))
                    if (names.Contains(Swap(name, at, second.Length, first))) votes.Add(SideB);
            }
            // Zero markers = nothing to go on; two or more = the name is ambiguous. Both stay Unassigned.
            result[name] = votes.Count == 1 ? votes[0] : Unassigned;
        }
        return result;
    }

    private static char Partner(char c) => c switch
    {
        'a' => 'b', 'b' => 'a', 'A' => 'B', 'B' => 'A',
        'l' => 'r', 'r' => 'l', 'L' => 'R', 'R' => 'L',
        _ => '\0'
    };

    /// <summary>A single-letter marker must sit at the edge of a word (Cable1<b>a</b>-2, <b>L</b>C-01),
    /// never inside one (C<b>a</b>b<b>l</b>e).</summary>
    private static bool AtTokenEdge(string name, int index) =>
        !(index > 0 && char.IsLetter(name[index - 1]) && index < name.Length - 1 && char.IsLetter(name[index + 1]));

    private static string Swap(string name, int start, int length, string replacement) =>
        string.Concat(name.AsSpan(0, start), replacement, name.AsSpan(start + length));
}

/// <summary>Per-cable design capacities [kN] for a report, with where each one came from.</summary>
public sealed record CableCapacitySet(
    IReadOnlyDictionary<string, double> Capacities,
    IReadOnlyDictionary<string, string> Sources);

/// <summary>Decides which capacity a report uses for each cable, by exact cable object name. The current
/// Tab 03 Pretension configuration (area × fpu × 1000 × φ) is the source of truth; a capacity is never
/// inferred from forces, borrowed from another cable, or replaced by a constant.</summary>
public static class CableCapacityResolver
{
    /// <summary>Resolved now from the current Tab 03 configuration.</summary>
    public const string Current = "current";
    /// <summary>Stored with the study results; used only for a cable that is not on the current Tab 03.</summary>
    public const string Stored = "stored";
    /// <summary>The cable is on the current Tab 03 but its area / fpu / φ cannot resolve a capacity.</summary>
    public const string Invalid = "invalid";
    /// <summary>No capacity is available anywhere for this cable.</summary>
    public const string None = "none";

    /// <param name="current">Capacities resolved now from Tab 03, or null when Tab 03 supplies none.</param>
    /// <param name="configuredCables">Every cable currently on Tab 03, including those whose capacity cannot
    /// be resolved — such a cable is reported as unavailable instead of falling back to a stored value.</param>
    /// <param name="stored">Capacities persisted with the historical study results.</param>
    public static CableCapacitySet Resolve(
        IReadOnlyDictionary<string, double>? current,
        IEnumerable<string>? configuredCables,
        IReadOnlyDictionary<string, double>? stored)
    {
        var capacities = new Dictionary<string, double>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in configuredCables ?? [])
            if (!string.IsNullOrWhiteSpace(name)) sources[name] = Invalid;
        foreach (var (name, value) in current ?? new Dictionary<string, double>())
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (IsCapacity(value)) { capacities[name] = value; sources[name] = Current; }
            else sources[name] = Invalid;
        }
        foreach (var (name, value) in stored ?? new Dictionary<string, double>())
        {
            if (string.IsNullOrWhiteSpace(name) || sources.ContainsKey(name) || !IsCapacity(value)) continue;
            capacities[name] = value;
            sources[name] = Stored;
        }
        return new(capacities, sources);
    }

    public static bool IsCapacity(double value) => double.IsFinite(value) && value > 0;
}
