using System.Text.RegularExpressions;

namespace Mcotis.Desktop.Geometry.Handlers;

/// <summary>One deterministic naming policy for parametric joints, frames and symmetric cable pairs.</summary>
public sealed partial class GeometryNamingService
{
    public string IncrementTrailingNumber(string template, int offset)
    {
        var match = TrailingNumber().Match(template);
        if (!match.Success) throw new InvalidOperationException($"Generated object template '{template}' has no sequence number.");
        var value = checked(int.Parse(match.Groups[2].Value) + offset);
        return match.Groups[1].Value + value.ToString(new string('0', match.Groups[2].Value.Length));
    }

    public string NextSequentialName(string template, IEnumerable<string> existing)
    {
        var used = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var match = TrailingNumber().Match(template);
        var prefix = match.Success ? match.Groups[1].Value : template + "_";
        var width = match.Success ? match.Groups[2].Value.Length : 3;
        var next = used.Select(name => Regex.Match(name, "^" + Regex.Escape(prefix) + @"(\d+)$", RegexOptions.IgnoreCase))
            .Where(item => item.Success).Select(item => int.Parse(item.Groups[1].Value)).DefaultIfEmpty(0).Max() + 1;
        string result;
        do result = prefix + (next++).ToString(new string('0', width)); while (used.Contains(result));
        return result;
    }

    public string SymmetricCableName(string template, int sequence, char side)
    {
        side = char.ToUpperInvariant(side);
        if (side is not ('A' or 'B' or 'C')) throw new ArgumentOutOfRangeException(nameof(side));
        var match = CablePair().Match(template);
        return match.Success
            ? $"{match.Groups[1].Value}{sequence.ToString(new string('0', match.Groups[2].Value.Length))}{char.ToLowerInvariant(side)}{match.Groups[4].Value}"
            : $"Cable{sequence:000}{side}";
    }

    public void EnsureUnique(IEnumerable<string> names)
    {
        var duplicate = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Generated object name '{duplicate.Key}' is duplicated.");
    }

    [GeneratedRegex(@"^(.*?)(\d+)$")]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"^(.*?)(\d+)([abAB])(.*)$")]
    private static partial Regex CablePair();
}
