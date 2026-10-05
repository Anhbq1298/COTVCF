using System.Globalization;
using System.IO;
using System.Text.Json;
using COT_VCF.Domain;

namespace COT_VCF.Application.Results;

/// <summary>What a report needs to print itself: the header / footer texts, the logo (already embedded) and the
/// default sheet. Serialized (camelCase) into the page as <c>#print-data</c>.</summary>
public sealed record ReportPrintDto(
    string Title,               // "Cable Forces"
    string Model,               // baseline model name
    string Issued,              // date the report was generated, e.g. "05 Oct 2026"
    string Company,
    string Logo,                // data URI, or "" — the report stays self-contained
    string Project,
    string JobNumber,
    string PreparedBy,
    string CheckedBy,
    string Revision,
    string FooterNote,
    string Paper,               // "A4" | "A3" | "Letter"
    string Orientation,         // "landscape" | "portrait"
    bool ShowPageNumbers,
    bool ShowDate,
    bool IncludeSourceData);

/// <summary>The shared print engine of the study reports. A report stays one self-contained HTML file; printing
/// it (browser Print, or <c>ReportPdfExporter</c>) lays it out with CSS Paged Media: the sheet and its margins
/// come from an <c>@page</c> rule, the header and footer live in the page-margin boxes (logo top-left, page
/// numbers bottom-right) on every page, and charts stay inline SVG so they print as vectors at any resolution.
/// A report plugs in by emitting <see cref="Css"/>, <see cref="PageStyle"/>, <see cref="ToolbarButton"/>,
/// <see cref="Panel"/> and <see cref="Scripts"/>, and by registering a before / after hook to re-lay-out its
/// charts for the sheet.</summary>
public static class ReportPrintEngine
{
    /// <summary>Largest logo file that is embedded. Each report carries its own copy.</summary>
    public const int MaxLogoBytes = 1_500_000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly IReadOnlyDictionary<string, string> LogoMimeTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
            [".svg"] = "image/svg+xml", [".webp"] = "image/webp", [".bmp"] = "image/bmp",
        };

    /// <summary>File-dialog filter for the logo picker.</summary>
    public const string LogoFileFilter =
        "Logo image (*.png;*.jpg;*.jpeg;*.svg;*.gif;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.svg;*.gif;*.webp;*.bmp";

    /// <summary>Resolve the settings for one report. Null settings still give a usable printout: report title,
    /// model and page numbers.</summary>
    public static ReportPrintDto Describe(ReportPrintSettings? settings, string reportTitle, string modelName,
        DateTimeOffset generatedAt)
    {
        settings ??= new ReportPrintSettings();
        return new ReportPrintDto(
            reportTitle.Trim(), modelName.Trim(), generatedAt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            settings.CompanyName.Trim(), LogoDataUri(settings.LogoPath, out _) ?? "", settings.ProjectName.Trim(),
            settings.JobNumber.Trim(), settings.PreparedBy.Trim(), settings.CheckedBy.Trim(), settings.Revision.Trim(),
            settings.FooterNote.Trim(), settings.PaperSize.ToString(),
            settings.Orientation == ReportPageOrientation.Portrait ? "portrait" : "landscape",
            settings.ShowPageNumbers, settings.ShowDate, settings.IncludeSourceData);
    }

    /// <summary>The logo file as a data URI so the report needs no external resource, or null with the reason
    /// in <paramref name="problem"/>. An empty path is simply "no logo" (no problem).</summary>
    public static string? LogoDataUri(string? path, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var file = new FileInfo(path.Trim());
            if (!file.Exists) { problem = $"Logo file not found: {file.FullName}"; return null; }
            if (!LogoMimeTypes.TryGetValue(file.Extension, out var mime))
            {
                problem = $"Unsupported logo type '{file.Extension}'. Use PNG, JPG, SVG, GIF, WEBP or BMP.";
                return null;
            }
            if (file.Length == 0) { problem = "The logo file is empty."; return null; }
            if (file.Length > MaxLogoBytes)
            {
                problem = $"The logo is {file.Length / 1024:N0} KB; the limit is {MaxLogoBytes / 1024:N0} KB because every report embeds it.";
                return null;
            }
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(file.FullName))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = $"The logo could not be read: {ex.Message}";
            return null;
        }
    }

    /// <summary>One line for the Print Setup card: what will be printed top-left.</summary>
    public static string DescribeLogo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No logo — the company name is printed top-left instead.";
        if (LogoDataUri(path, out var problem) is null) return "⚠ " + problem;
        var file = new FileInfo(path.Trim());
        return $"✓ {file.Name} · {Math.Max(1, file.Length / 1024):N0} KB · embedded into each report, printed top-left on every page";
    }

    /// <summary>The <c>#print-data</c> block followed by the engine script. Emit before the report's own script
    /// so that script can register its hooks on <c>window.reportPrint</c>.</summary>
    public static string Scripts(ReportPrintDto print) =>
        $"<script id=\"print-data\" type=\"application/json\">{JsonSerializer.Serialize(print, Json)}</script>\n<script>{Script}</script>";

    /// <summary>Holds the generated <c>@page</c> rule. Emit once, in the document head.</summary>
    public const string PageStyle = """<style id="pr-page" media="print"></style>""";

    /// <summary>Toolbar button that opens <see cref="Panel"/>.</summary>
    public const string ToolbarButton =
        """<button id="pr-btn" aria-expanded="false" aria-controls="pr-panel" title="Print, or save as PDF, with the report header and footer">Print ▾</button>""";

    /// <summary>Print options. Place inside the report toolbar; it takes a full row when opened.</summary>
    public const string Panel = """
  <div id="pr-panel" class="prpanel" hidden>
    <label class="small">Paper <select id="pr-paper" aria-label="Paper size"><option value="A4">A4</option><option value="A3">A3</option><option value="Letter">Letter</option></select></label>
    <label class="small">Orientation <select id="pr-orient" aria-label="Page orientation"><option value="landscape">Landscape</option><option value="portrait">Portrait</option></select></label>
    <label class="small"><input type="checkbox" id="pr-src"> Include Source Data table</label>
    <button id="pr-go" class="pri">Print / Save as PDF</button>
    <span class="small" id="pr-info"></span>
  </div>
""";

    /// <summary>Shared print stylesheet. Report-specific print rules stay with the report.</summary>
    public const string Css = """
/* print engine */
.print-only{display:none}
.prpanel{flex-basis:100%;display:flex;align-items:center;gap:8px 14px;flex-wrap:wrap;border-top:1px solid var(--bd,#C7CCD2);margin-top:2px;padding:8px 0 4px}
.prpanel label{display:inline-flex;align-items:center;gap:6px}
.prpanel #pr-info{flex-basis:100%;line-height:1.45;max-width:120ch}
@media print{
  html,body{background:#fff!important}
  body{-webkit-print-color-adjust:exact;print-color-adjust:exact}
  .no-print,.prpanel,#pr-btn{display:none!important}
  .print-only{display:block}
  body.pr-nosrc [data-print="source"]{display:none!important}
  svg{max-width:100%;height:auto}
  h1,h2,h3{break-after:avoid}
  thead{display:table-header-group}tr{break-inside:avoid}
  a{color:inherit;text-decoration:none}
}
""";

    // Paged layout from CSS Paged Media. The @page rule is rebuilt whenever the paper or orientation changes, so
    // the browser's Print (Ctrl+P), the Print button and a headless print-to-PDF all get the same sheet.
    private const string Script = """
window.reportPrint = (function(){
  "use strict";
  const byId = id => document.getElementById(id);
  const dataNode = byId("print-data");
  const P = dataNode && dataNode.textContent ? JSON.parse(dataNode.textContent) : {};
  const PAPERS = { A4: [210, 297], A3: [297, 420], Letter: [215.9, 279.4] };     // mm, portrait
  const MARGIN = { top: 24, bottom: 16 };                                         // mm; header / footer bands live here
  const SIDE = 38;                                                                // px (10 mm) — whole pixels, see css()
  const PX = 96 / 25.4;                                                           // CSS px per mm
  const FONT = '"Segoe UI",system-ui,-apple-system,Arial,sans-serif';

  // "#print&paper=A3&orientation=portrait&source=1" — used by the PDF exporter; other keys belong to the report
  const options = {};
  ((window.location && window.location.hash) || "").replace(/^#/, "").split("&").forEach(part => {
    if (!part) return;
    const i = part.indexOf("=");
    try { options[decodeURIComponent(i < 0 ? part : part.slice(0, i))] = i < 0 ? "1" : decodeURIComponent(part.slice(i + 1)); } catch (e) { }
  });
  const S = {
    paper: PAPERS[options.paper] ? options.paper : PAPERS[P.paper] ? P.paper : "A4",
    orientation: options.orientation === "portrait" || options.orientation === "landscape" ? options.orientation
      : P.orientation === "portrait" ? "portrait" : "landscape",
    includeSource: options.source != null ? options.source === "1" : !!P.includeSourceData,
    active: false,
    auto: options.print != null        // opened for printing: lay the page out for paper as soon as it has loaded
  };
  const hooks = [], opened = [];

  // sheet geometry; the content width in CSS px is what a report lays its charts out against
  function page(){
    const d = PAPERS[S.paper], land = S.orientation === "landscape";
    const w = land ? d[1] : d[0], h = land ? d[0] : d[1];
    return { paper: S.paper, orientation: S.orientation, widthMm: w, heightMm: h,
      contentPx: Math.floor(w * PX - 2 * SIDE), contentHeightPx: Math.floor((h - MARGIN.top - MARGIN.bottom) * PX) };
  }
  const join = parts => parts.filter(Boolean).join(" · ");
  function header(){
    return {
      left: P.logo ? "" : (P.company || ""),
      center: [P.project || P.title, P.project ? join([P.title, P.model]) : P.model],
      right: [join([P.jobNumber && "Job No. " + P.jobNumber, P.revision && "Rev " + P.revision]), P.showDate !== false ? P.issued : ""]
    };
  }
  function footer(){
    return {
      left: [P.company, join([P.preparedBy && "Prepared by " + P.preparedBy, P.checkedBy && "Checked by " + P.checkedBy])],
      center: [P.footerNote],
      pages: P.showPageNumbers !== false
    };
  }
  // user text -> CSS string. Quotes and backslashes are escaped and line breaks removed, so it cannot leave the string.
  const q = t => '"' + String(t == null ? "" : t).replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/[\r\n\f]+/g, " ") + '"';
  const stack = lines => lines.filter(Boolean).map(q).join(' "\\A " ') || '""';
  const logoOk = () => typeof P.logo === "string" && /^data:image\/[a-z0-9.+-]+;base64,[A-Za-z0-9+\/=]+$/.test(P.logo);

  function css(){
    const pg = page(), h = header(), f = footer();
    // The three boxes of a band tile the full width, so their rules join into one line across the page. The side
    // boxes sit on whole pixels and the centre takes exactly what is left; fractional edges would be snapped
    // apart and leave a hairline gap in the rule.
    const side = share => Math.round(pg.contentPx * share) + "px";
    const centre = share => "calc(" + pg.widthMm + "mm - " + (2 * SIDE + 2 * Math.round(pg.contentPx * share)) + "px)";
    const top = "margin:6mm 0 3mm 0;padding:0 0 1.6mm 0;border-bottom:0.75pt solid #1F4788;vertical-align:bottom;white-space:pre-wrap;color:#1A1A1A;";
    const bottom = "margin:2.5mm 0 4.5mm 0;padding:1.2mm 0 0 0;border-top:0.5pt solid #9AA0A6;vertical-align:top;white-space:pre-wrap;color:#555;font:7.5pt " + FONT + ";";
    return "@page{size:" + (S.paper === "Letter" ? "letter" : S.paper) + " " + S.orientation
      + ";margin:" + MARGIN.top + "mm " + SIDE + "px " + MARGIN.bottom + "mm " + SIDE + "px;"
      + "@top-left{" + top + "width:" + side(.27) + ";text-align:left;font:600 10pt " + FONT + ";"
        + (logoOk() ? 'content:"";background:url("' + P.logo + '") no-repeat left bottom;background-size:contain;background-origin:content-box;'
                    : "content:" + q(h.left) + ";") + "}"
      + "@top-center{" + top + "width:" + centre(.27) + ";text-align:center;font:600 9pt " + FONT + ";content:" + stack(h.center) + ";}"
      + "@top-right{" + top + "width:" + side(.27) + ";text-align:right;font:8pt " + FONT + ";color:#444;content:" + stack(h.right) + ";}"
      + "@bottom-left{" + bottom + "width:" + side(.36) + ";text-align:left;content:" + stack(f.left) + ";}"
      + "@bottom-center{" + bottom + "width:" + centre(.36) + ";text-align:center;content:" + stack(f.center) + ";}"
      + "@bottom-right{" + bottom + "width:" + side(.36) + ";text-align:right;content:"
        + (f.pages ? '"Page " counter(page) " of " counter(pages)' : '""') + ";}"
      + "}";
  }
  function apply(){ const style = byId("pr-page"); if (style) style.textContent = css(); }

  // print layout on: reports re-lay-out their charts for the sheet width; off: back to the screen layout
  function enter(){
    if (S.active) return;
    S.active = true; apply();
    if (document.body && document.body.classList) document.body.classList.toggle("pr-nosrc", !S.includeSource);
    if (S.includeSource && document.querySelectorAll)
      Array.prototype.forEach.call(document.querySelectorAll('details[data-print="source"]'), d => { if (!d.open){ d.open = true; opened.push(d); } });
    const pg = page();
    hooks.forEach(hook => { if (hook.before) hook.before(pg); });
  }
  function leave(){
    if (!S.active || S.auto) return;
    S.active = false;
    opened.splice(0).forEach(d => { d.open = false; });
    hooks.forEach(hook => { if (hook.after) hook.after(); });
  }
  function print(){ enter(); window.print(); }

  const button = byId("pr-btn"), panel = byId("pr-panel"), paper = byId("pr-paper"), orient = byId("pr-orient"),
        source = byId("pr-src"), go = byId("pr-go"), info = byId("pr-info");
  function sync(){
    if (paper) paper.value = S.paper;
    if (orient) orient.value = S.orientation;
    if (source) source.checked = S.includeSource;
    if (info){
      const h = header(), f = footer();
      info.textContent = "Header: " + join([logoOk() ? "logo" : (h.left || "no logo"), h.center[0], h.right[0], h.right[1]])
        + ". Footer: " + (join([f.left[0], f.left[1], f.center[0], f.pages ? "page numbers" : ""]) || "empty")
        + ". Set in COT VCF tools → Tab 06 → Print Setup, then Regenerate. Charts print as vector graphics.";
    }
    apply();
  }
  if (button) button.onclick = () => { panel.hidden = !panel.hidden; button.setAttribute("aria-expanded", String(!panel.hidden)); };
  if (paper) paper.onchange = e => { if (PAPERS[e.target.value]) S.paper = e.target.value; sync(); };
  if (orient) orient.onchange = e => { S.orientation = e.target.value === "portrait" ? "portrait" : "landscape"; sync(); };
  if (source) source.onchange = e => { S.includeSource = !!e.target.checked; sync(); };
  if (go) go.onclick = print;
  window.addEventListener("beforeprint", enter);       // also covers Ctrl+P and the browser menu
  window.addEventListener("afterprint", leave);
  window.addEventListener("load", () => {
    if (!S.auto) return;
    enter();
    if (document.documentElement) document.documentElement.setAttribute("data-print-ready", "1");
  });
  sync();

  return { state: S, data: P, options, page, css, enter, leave, print, register: hook => { hooks.push(hook); } };
})();
""";
}
