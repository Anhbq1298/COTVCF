using System.Text;

namespace COT_VCF.Application.Results;

/// <summary>The one immutable navigation zone shared by every study report — the <b>Result Library</b>.
/// Every report renders the same block in the same place; each entry links to a sibling report in the same
/// study folder. Entries whose report file is not present are omitted, so the menu only ever links to reports
/// that actually exist. The report currently open is marked active and is always shown.</summary>
public static class ReportNav
{
    public sealed record Module(string Key, string Label, string FileName);

    /// <summary>Every report module, in display order. Add a row here when a new module ships a report.</summary>
    public static readonly IReadOnlyList<Module> Modules =
    [
        new("deflection", "Deflection · Uz", DeflectionReportGenerator.ReportFileName),
        new("member",     "Member Forces",   MemberForceReportGenerator.ReportFileName),
        new("cable",      "Cable Forces",   CableForceReportGenerator.ReportFileName),
        new("shortlist",  "Study Shortlist", StudyShortlistExporter.HtmlFileName),
    ];

    /// <summary>Result-Library markup for the report identified by <paramref name="current"/>.
    /// <paramref name="present"/> is the set of module keys whose report file exists (or is being written in
    /// the same publish pass). A module that is neither current nor present is dropped entirely.</summary>
    public static string Library(string current, IReadOnlySet<string> present)
    {
        var items = new StringBuilder();
        foreach (var m in Modules)
        {
            var isCurrent = string.Equals(m.Key, current, StringComparison.Ordinal);
            if (!isCurrent && !present.Contains(m.Key)) continue;
            items.Append(isCurrent
                ? $"<li class=\"on\">{m.Label}<span>viewing</span></li>"
                : $"<li><a href=\"./{m.FileName}\">{m.Label}</a><span>open</span></li>");
        }
        return $"<details class=\"lib\" open><summary>Result Library</summary><ul>{items}</ul></details>";
    }

    /// <summary>Convenience for report generators: which sibling reports exist on disk in
    /// <paramref name="studyFolder"/>, plus <paramref name="alsoWriting"/> (the report about to be written in
    /// this pass, which should link as if it already exists).</summary>
    public static IReadOnlySet<string> PresentIn(string studyFolder, params string[] alsoWriting)
    {
        var set = new HashSet<string>(alsoWriting, StringComparer.Ordinal);
        foreach (var m in Modules)
            if (System.IO.File.Exists(System.IO.Path.Combine(studyFolder, m.FileName)))
                set.Add(m.Key);
        return set;
    }
}
