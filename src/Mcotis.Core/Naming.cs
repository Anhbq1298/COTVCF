using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Mcotis.Core;

/// <summary>A name is unique within its scope. Points, frames, cables and shells share one scope, so a name
/// identifies one object of the model whatever its type.</summary>
public enum NameScope { Object, Material, FrameSection, CableSection, ShellSection, LoadPattern, AnalysisCase }

/// <summary>The one place that decides what a valid name is, how two names compare and which name a new object gets.
/// Nothing else — no view model, no importer — makes up or checks names.</summary>
public static class NamingService
{
    private static readonly ObjectKind[] NamedKinds =
    [
        ObjectKind.Point, ObjectKind.Frame, ObjectKind.Cable, ObjectKind.Shell, ObjectKind.Material, ObjectKind.FrameSection,
        ObjectKind.CableSection, ObjectKind.ShellSection, ObjectKind.LoadPattern, ObjectKind.LoadCase, ObjectKind.LoadCombination,
    ];

    /// <summary>Names that differ only in case are the same name.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>The kinds of object that carry a name.</summary>
    public static IReadOnlyList<ObjectKind> Kinds => NamedKinds;

    /// <summary>The stored form of a name: surrounding white space removed, Unicode NFC. False for a name that is
    /// empty, blank, holds control characters or is not valid Unicode.</summary>
    public static bool TryNormalize(string? name, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        if (trimmed.Any(char.IsControl)) return false;
        try { normalized = trimmed.IsNormalized(NormalizationForm.FormC) ? trimmed : trimmed.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return false; }
        return true;
    }

    public static NameScope ScopeOf(ObjectKind kind) => kind switch
    {
        ObjectKind.Point or ObjectKind.Frame or ObjectKind.Cable or ObjectKind.Shell => NameScope.Object,
        ObjectKind.Material => NameScope.Material,
        ObjectKind.FrameSection => NameScope.FrameSection,
        ObjectKind.CableSection => NameScope.CableSection,
        ObjectKind.ShellSection => NameScope.ShellSection,
        ObjectKind.LoadPattern => NameScope.LoadPattern,
        ObjectKind.LoadCase or ObjectKind.LoadCombination => NameScope.AnalysisCase,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This kind of object has no name."),
    };

    /// <summary>The prefix of an automatic name: P1, F1, C1 (cable), S1 …</summary>
    public static string Prefix(ObjectKind kind) => kind switch
    {
        ObjectKind.Point => "P",
        ObjectKind.Frame => "F",
        ObjectKind.Cable => "C",
        ObjectKind.Shell => "S",
        ObjectKind.Material => "MAT",
        ObjectKind.FrameSection => "FSEC",
        ObjectKind.CableSection => "CSEC",
        ObjectKind.ShellSection => "SSEC",
        ObjectKind.LoadPattern => "LP",
        ObjectKind.LoadCase => "LC",
        ObjectKind.LoadCombination => "COMB",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This kind of object has no name."),
    };

    /// <summary>The place of a name in the automatic sequence of a kind: 12 for "F12" among frames. Null for a name
    /// that is not that kind's prefix followed by a number.</summary>
    public static int? SequenceNumber(string name, ObjectKind kind)
    {
        var prefix = Prefix(kind);
        var digits = name.Length - prefix.Length;
        if (digits is < 1 or > 9 || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var number = 0;
        foreach (var character in name.AsSpan(prefix.Length))
        {
            if (!char.IsAsciiDigit(character)) return null;
            number = number * 10 + (character - '0');
        }
        return number;
    }

    /// <summary>The first free name <c>prefix + n</c> counting up from <paramref name="start"/>.</summary>
    public static string NextFree(string prefix, Func<string, bool> isTaken, ref int start)
    {
        string candidate;
        while (isTaken(candidate = prefix + start.ToString(CultureInfo.InvariantCulture))) start++;
        return candidate;
    }
}

/// <summary>Who holds which name in a document, per scope, and how far the automatic names of each kind have got.
/// Part of the document: it changes only with it.</summary>
public sealed class NameIndex
{
    internal static readonly ImmutableDictionary<string, ObjectId> EmptyScope =
        ImmutableDictionary.Create<string, ObjectId>(NamingService.Comparer);
    internal static NameIndex Empty { get; } = new(ImmutableDictionary<NameScope, ImmutableDictionary<string, ObjectId>>.Empty,
        ImmutableDictionary<ObjectKind, int>.Empty);

    internal NameIndex(ImmutableDictionary<NameScope, ImmutableDictionary<string, ObjectId>> scopes, ImmutableDictionary<ObjectKind, int> sequence)
    {
        Scopes = scopes;
        Sequence = sequence;
    }

    internal ImmutableDictionary<NameScope, ImmutableDictionary<string, ObjectId>> Scopes { get; }
    internal ImmutableDictionary<ObjectKind, int> Sequence { get; }

    internal ImmutableDictionary<string, ObjectId> Scope(NameScope scope) => Scopes.TryGetValue(scope, out var names) ? names : EmptyScope;

    /// <summary>The holder of a name. The name is compared as <see cref="NamingService"/> stores it: trimmed, NFC, any case.</summary>
    public bool TryGet(NameScope scope, string? name, out ObjectId id)
    {
        id = default;
        return NamingService.TryNormalize(name, out var normalized) && Scope(scope).TryGetValue(normalized, out id);
    }

    public bool Contains(NameScope scope, string? name) => TryGet(scope, name, out _);

    public int Count(NameScope scope) => Scope(scope).Count;

    /// <summary>The highest number a name of this kind's sequence has ever had in this document: 7 once there has been
    /// an "F7". The next automatic name comes after it. Deleting an object does not lower it, so a deleted object's
    /// name is not issued again and the sequence of the names that remain is never disturbed.</summary>
    public int LastNumber(ObjectKind kind) => Sequence.TryGetValue(kind, out var number) ? number : 0;
}
