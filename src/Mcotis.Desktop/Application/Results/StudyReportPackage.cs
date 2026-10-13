using System.IO;
using System.Net;
using System.Text;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application.Results;

/// <summary>One report of a package: the file in the report folder and the view of it to print.</summary>
/// <param name="Options">Report options for the URL fragment, e.g. <c>summary=top</c>.</param>
public sealed record ReportPackageItem(string Title, string FileName, string? Options = null);

/// <summary>Every report of a folder as one printed document. The package is a small page that loads each report
/// in a frame, opened as <c>#print=package</c>: the report composes its own sheets with the shared
/// <see cref="ReportPrintEngine"/> — the same sheets its Print Report screen shows — and the package only stacks
/// them, hands every report the same Print Setup and numbers the pages through. Nothing of a report is drawn
/// twice or differently here.</summary>
public static class StudyReportPackage
{
    private static readonly (string Launcher, string Kind, string Title)[] Summaries =
    [
        (MemberForceReportGenerator.TopChordFileName, MemberGroupSummary.Top, "Top Chord"),
        (MemberForceReportGenerator.BottomChordFileName, MemberGroupSummary.Bottom, "Bottom Chord"),
        (MemberForceReportGenerator.MegaColumnFileName, MemberGroupSummary.MegaColumn, "Mega Column"),
    ];

    /// <summary>The reports generated in <paramref name="folder"/> (a study folder or its Optimized set), in the
    /// order of the Tab 06 modules. The member-force report is printed once per group Summary it has; without
    /// any, as it opens.</summary>
    public static IReadOnlyList<ReportPackageItem> Items(string folder)
    {
        var items = new List<ReportPackageItem>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return items;
        bool Has(string fileName) => File.Exists(Path.Combine(folder, fileName));

        if (Has(OptimizationSummaryReport.FileName)) items.Add(new("Optimization Summary", OptimizationSummaryReport.FileName));
        if (Has(DeflectionReportGenerator.ReportFileName)) items.Add(new("Deflection", DeflectionReportGenerator.ReportFileName));
        if (Has(MemberForceReportGenerator.ReportFileName))
        {
            var summaries = Summaries.Where(s => Has(s.Launcher))
                .Select(s => new ReportPackageItem(s.Title, MemberForceReportGenerator.ReportFileName, "summary=" + s.Kind)).ToList();
            if (summaries.Count == 0) summaries.Add(new("Member Forces", MemberForceReportGenerator.ReportFileName));
            items.AddRange(summaries);
        }
        if (Has(SupportReactionReportGenerator.ReportFileName)) items.Add(new("Support Reactions", SupportReactionReportGenerator.ReportFileName));
        if (Has(CableForceReportGenerator.ReportFileName)) items.Add(new("Cable Forces", CableForceReportGenerator.ReportFileName));
        return items;
    }

    /// <summary>The package page for <paramref name="items"/> of <paramref name="folder"/>. Reports are addressed
    /// by absolute file URL, so the page can be written anywhere. <paramref name="settings"/> is the Print Setup
    /// every report prints with — as it is now, not as it was when a report was generated.</summary>
    public static string Html(string folder, IReadOnlyList<ReportPackageItem> items, ReportPrintSettings? settings, string title)
    {
        var setup = ReportPrintEngine.SetupJson(ReportPrintEngine.Describe(settings, title, "", DateTimeOffset.Now));
        var html = new StringBuilder()
            .Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n")
            .Append("<style id=\"pg\">@page{size:297mm 210mm;margin:0}</style>\n")
            .Append("<style>html,body{margin:0;padding:0;background:#fff}iframe{display:block;border:0;width:297mm;height:210mm}")
            .Append("iframe+iframe{break-before:page}iframe[hidden]{display:none}")
            .Append("#wait{margin:0;padding:20mm;font:12pt \"Segoe UI\",Arial,sans-serif;break-after:page}#wait[hidden]{display:none}</style>\n")
            .Append("<script id=\"setup\" type=\"application/json\">").Append(setup).Append("</script>\n")
            .Append("<script>").Append(Script).Append("</script></head><body>\n")
            // printed only if a report never answers: a package that is not complete says so on its first page
            .Append("<p id=\"wait\">Not every report could be laid out for printing: <span></span>. Regenerate the reports and print again.</p>\n");
        foreach (var item in items)
        {
            var url = new Uri(Path.GetFullPath(Path.Combine(folder, item.FileName))).AbsoluteUri + "#print=package"
                      + (string.IsNullOrWhiteSpace(item.Options) ? "" : "&" + item.Options.Trim().TrimStart('#', '&'));
            html.Append("<iframe title=\"").Append(WebUtility.HtmlEncode(item.Title)).Append("\" src=\"").Append(WebUtility.HtmlEncode(url)).Append("\"></iframe>\n");
        }
        return html.Append("</body></html>\n").ToString();
    }

    // Each report says when it is ready, is given the setup, composes its sheets and says how many there are; the
    // frame is then exactly that many pages tall. Once every report has answered, the pages are numbered through.
    private const string Script = """
(function(){
  "use strict";
  const setup = JSON.parse(document.getElementById("setup").textContent), counts = new Map();
  const frames = () => Array.prototype.slice.call(document.querySelectorAll("iframe"));
  function waiting(){
    const wait = document.getElementById("wait"), missing = frames().filter(f => !counts.has(f)).map(f => f.title);
    if (!wait) return;
    wait.querySelector("span").textContent = missing.join(", "); wait.hidden = !missing.length;
  }
  window.addEventListener("DOMContentLoaded", waiting);
  window.addEventListener("message", e => {
    const m = e.data, all = frames(), frame = all.filter(f => f.contentWindow === e.source)[0];
    if (!frame || !m || m.mcotis !== "print") return;
    if (m.type === "ready") e.source.postMessage({ mcotis: "print", type: "setup", setup: setup }, "*");
    else if (m.type === "sheets"){
      counts.set(frame, m.count | 0);
      document.getElementById("pg").textContent = "@page{size:" + m.widthMm + "mm " + m.heightMm + "mm;margin:0}";
      frame.style.width = m.widthMm + "mm"; frame.style.height = (m.count * m.heightMm) + "mm"; frame.hidden = !m.count;
      waiting();
      if (counts.size < all.length) return;
      const total = all.reduce((n, f) => n + counts.get(f), 0); let offset = 0;
      all.forEach(f => { f.contentWindow.postMessage({ mcotis: "print", type: "number", offset: offset, total: total }, "*"); offset += counts.get(f); });
      document.documentElement.setAttribute("data-print-ready", String(total));
    }
  });
})();
""";
}
