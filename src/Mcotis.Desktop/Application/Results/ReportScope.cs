namespace COT_VCF.Application.Results;

/// <summary>Marks a report as covering a named subset of the study's candidates (Tab 06A: the qualified set) and
/// carries what the subset alone cannot tell: the full study's candidate ids — so an id keeps the order, colour
/// and line style it has in the full reports — and the iterations that have results but are outside the set, so
/// a blank slot reads "not qualified" instead of "no result".</summary>
/// <param name="Label">Short name of the set, appended to the report title, e.g. "Optimized set".</param>
/// <param name="Note">What defines the set, e.g. "Qualified candidates: 137 of 1,000 · Deflection δ 50–150 mm".</param>
/// <param name="Excluded">[loadId, heightId, pretensionId] of every study iteration outside the set.</param>
/// <param name="StudyFolderPath">From the report's folder to the study folder, where the per-candidate
/// diagnostics live.</param>
public sealed record ReportScope(
    string Label,
    string Note,
    IReadOnlyList<string> LoadIds,
    IReadOnlyList<string> HeightIds,
    IReadOnlyList<string> PretensionIds,
    IReadOnlyList<string[]> Excluded,
    string StudyFolderPath = "../")
{
    /// <summary>What a blank slot shows for an iteration that is outside the set.</summary>
    public const string Marker = "NQ";
    public const string MarkerMeaning = "NQ = not qualified";

    public string Title(string reportTitle) => $"{reportTitle} · {Label}";

    public ReportScopeDto ToDto() => new(Label, Note, new(LoadIds, HeightIds, PretensionIds), Excluded);

    public bool IsExcluded(string loadId, string heightId, string pretensionId) =>
        Excluded.Any(key => key.Length == 3 && key[0] == loadId && key[1] == heightId && key[2] == pretensionId);

    /// <summary>The line under a report's header that says which candidates it covers; empty without a scope.</summary>
    public static string Banner(ReportScope? scope) => scope is null ? "" : Banner(scope.Label, scope.Note);

    public static string Banner(ReportScopeDto? scope) => scope is null ? "" : Banner(scope.Label, scope.Note);

    public static string Banner(string label, string note) => $"<div class=\"scope\"><b>{Text(label)}</b> · {Text(note)}</div>";

    /// <summary>Text for a UTF-8 HTML page: only the markup characters are escaped, so δ, ≤ and · stay readable
    /// in the source.</summary>
    public static string Text(string? value) => (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>Shared by every report; printed too.</summary>
    public const string Css = ".scope{margin:4px 0 2px;padding:3px 9px;border-left:3px solid #1f4788;background:#eef3fb;color:#1a1a1a;font-size:11.5px;max-width:1100px}";
}

public sealed record ReportScopeDto(string Label, string Note, ReportScopeOrderDto Order, IReadOnlyList<string[]> Excluded);

/// <summary>The full study's candidate ids in display order, keyed as the report series are.</summary>
public sealed record ReportScopeOrderDto(
    IReadOnlyList<string> LoadId,
    IReadOnlyList<string> HeightId,
    IReadOnlyList<string> PretensionId);
