using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcotis.Desktop.Application;

public enum DevelopmentStatus { Done, InProgress, Planned }

/// <summary>One function added or changed in Mcotis, or planned: <c>## date · title</c> and its fields.</summary>
public sealed record DevelopmentLogEntry(DateOnly Date, string Title, DevelopmentStatus Status, string Who, string Where, string What, string Checks);

/// <summary>
/// The development log (<c>docs/DEVELOPMENT_LOG.md</c>, embedded in the app as <see cref="ResourceName"/>), shown under
/// Help → Development Plans. Keeping it is compulsory — see the file's own header, the commit hook in
/// <c>.githooks/commit-msg</c> and AGENTS.md. <see cref="Problems"/> is what the checks hold every entry to.
/// </summary>
public static class DevelopmentLog
{
    public const string ResourceName = "Mcotis.DevelopmentLog.md";
    public static readonly string[] Fields = ["Status", "Who", "Where", "What", "Checks"];
    private static readonly Regex Heading = new(@"^## (\d{4}-\d{2}-\d{2}) · (.+)$", RegexOptions.Compiled);
    private static readonly Regex Field = new(@"^- (\w+): (.*)$", RegexOptions.Compiled);

    /// <summary>The log's text as built into the app.</summary>
    public static string Embedded()
    {
        using var stream = typeof(DevelopmentLog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The development log is not built into the app.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<DevelopmentLogEntry> Parse(string text) => Read(text).Entries;

    /// <summary>What is wrong with the log's entries: a heading that is not <c>## yyyy-mm-dd · title</c>, a field missing,
    /// empty or unknown, a status other than Done / In progress / Planned, entries not newest first, a done entry dated
    /// after <paramref name="today"/>.</summary>
    public static IReadOnlyList<string> Problems(string text, DateOnly today) => Read(text, today).Problems;

    private static (IReadOnlyList<DevelopmentLogEntry> Entries, IReadOnlyList<string> Problems) Read(string text, DateOnly? today = null)
    {
        var entries = new List<DevelopmentLogEntry>();
        var problems = new List<string>();
        string? heading = null;
        Dictionary<string, string>? fields = null;
        string? last = null;
        void Close()
        {
            if (heading is null || fields is null) return;
            var match = Heading.Match(heading);
            var title = match.Success ? match.Groups[2].Value.Trim() : heading;
            if (!match.Success || !DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                problems.Add($"'{heading}': the heading must be '## yyyy-mm-dd · title'.");
                return;
            }
            foreach (var name in Fields.Where(name => !fields.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)))
                problems.Add($"'{title}': '- {name}:' is missing or empty.");
            var status = fields.GetValueOrDefault("Status", "").Trim().ToLowerInvariant() switch
            {
                "done" => DevelopmentStatus.Done, "in progress" => DevelopmentStatus.InProgress, "planned" => DevelopmentStatus.Planned,
                _ => (DevelopmentStatus?)null,
            };
            if (status is null && fields.ContainsKey("Status")) problems.Add($"'{title}': Status must be Done, In progress or Planned.");
            if (status == DevelopmentStatus.Done && today is { } now && date > now) problems.Add($"'{title}': a done entry cannot be dated after today.");
            entries.Add(new DevelopmentLogEntry(date, title, status ?? DevelopmentStatus.Planned, fields.GetValueOrDefault("Who", ""),
                fields.GetValueOrDefault("Where", ""), fields.GetValueOrDefault("What", ""), fields.GetValueOrDefault("Checks", "")));
        }
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                heading = line;
                fields = new(StringComparer.Ordinal);
                last = null;
                continue;
            }
            if (fields is null || line.Length == 0) continue;
            if (Field.Match(line) is { Success: true } field)
            {
                last = field.Groups[1].Value;
                if (!Fields.Contains(last)) problems.Add($"'{heading}': unknown field '{last}' (use {string.Join(", ", Fields)}).");
                fields[last] = field.Groups[2].Value.Trim();
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal) && last is not null) fields[last] += " " + line.Trim();
            else problems.Add($"'{heading}': '{line.Trim()}' is not a '- Field: value' line (continue a value on a line indented by two spaces).");
        }
        Close();
        for (var i = 1; i < entries.Count; i++)
            if (entries[i].Date > entries[i - 1].Date)
                problems.Add($"'{entries[i].Title}' ({entries[i].Date:yyyy-MM-dd}) comes after an older entry; the log is newest first.");
        if (entries.Count == 0) problems.Add("The log has no entries.");
        return (entries, problems);
    }
}
