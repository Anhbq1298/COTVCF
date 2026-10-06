using System.Globalization;
using System.IO;
using System.Text.Json;
using COT_VCF.Domain;

namespace COT_VCF.Application.Results;

/// <summary>What a report starts its Print Report setup from: the header / footer texts, the logo (already
/// embedded) and the default sheet, as set on Tab 06 when the report was generated. Serialized (camelCase) into the
/// page as <c>#print-data</c>.</summary>
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

/// <summary>The shared Print Report mechanism of the study reports. A report stays one self-contained HTML file
/// and prints itself: its <b>Print Report</b> button opens a setup screen — company, project, job number,
/// revision, prepared / checked by, footer note, logo, paper, orientation, page numbers, date, source data — beside
/// a live preview. The preview is the printout: the engine composes the report into sheets (header band, body,
/// footer band, "Page n of N") and the browser prints exactly those sheets, so nothing has to be checked in the
/// native print dialog. Charts stay inline SVG and print as vectors. PDF is "Save as PDF" in that dialog; the
/// application's <b>Print All Reports</b> writes the same sheets of every report into one PDF
/// (<see cref="StudyReportPackage"/>, <c>#print=package</c>).
/// <para>The sheets are composed from the report's blocks in a fixed order, whatever their order on screen:
/// information, legends, general notes, summary, charts, source tables. A report plugs in by emitting
/// <see cref="Css"/>, <see cref="PageStyle"/>, <see cref="ToolbarButton"/> (or <see cref="Bar"/>) and
/// <see cref="Scripts"/>, and by marking its blocks:</para>
/// <list type="bullet">
/// <item><c>data-pr="info|legend|note|summary|chart|source"</c> — one printable block and its place in the order.
/// A legend or note that repeats under several charts is printed once. A block is kept whole on a sheet.</item>
/// <item><c>data-pr-keep</c> — a heading that stays with the block after it.</item>
/// <item><c>data-pr-split="selector"</c> — a block that may continue on the next sheet between the matched
/// parts, its remaining content (title, table head) repeated; a single large table splits between rows anyway.</item>
/// <item><c>data-print-controls</c> on a toolbar plus a <c>data-print-view</c> element — the engine records what
/// the filters were set to; <c>data-print-fit</c> — a wide table is scaled to the sheet.</item>
/// <item><c>window.reportPrint.register({ before(page), after() })</c> — re-lay-out charts that are drawn against
/// the window width for the sheet, and back.</item>
/// </list>
/// A report's paper-only rules are written against <c>.pr-sheet</c>, not <c>@media print</c>: the preview is on
/// screen, and it must look like the paper.</summary>
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
    /// <param name="embedLogo">False for documents written once per candidate: they print the company name
    /// top-left instead, so a study with hundreds of candidates does not carry hundreds of logo copies.</param>
    public static ReportPrintDto Describe(ReportPrintSettings? settings, string reportTitle, string modelName,
        DateTimeOffset generatedAt, bool embedLogo = true)
    {
        settings ??= new ReportPrintSettings();
        return new ReportPrintDto(
            reportTitle.Trim(), (modelName ?? "").Trim(), generatedAt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            settings.CompanyName.Trim(), embedLogo ? LogoDataUri(settings.LogoPath, out _) ?? "" : "", settings.ProjectName.Trim(),
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
        $"<script id=\"print-data\" type=\"application/json\">{SetupJson(print)}</script>\n<script>{Script}</script>";

    /// <summary>A setup as the engine script reads it — safe inside a <c>&lt;script&gt;</c> element.</summary>
    public static string SetupJson(ReportPrintDto print) => JsonSerializer.Serialize(print, Json);

    // how the engine script reads "#print=package" — what tells a report that can join a package from an older one
    private const string PackageMarker = "options.print === \"package\"";

    /// <summary>Whether a generated report can be printed as one of several in a package
    /// (<see cref="StudyReportPackage"/>). A report written before that existed cannot: it has to be regenerated.</summary>
    public static bool SupportsPackage(string reportPath)
    {
        try
        {
            var marker = System.Text.Encoding.ASCII.GetBytes(PackageMarker);
            using var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            var buffer = new byte[1 << 20];
            int kept = 0, read;
            while ((read = stream.Read(buffer, kept, buffer.Length - kept)) > 0)
            {
                var length = kept + read;
                if (buffer.AsSpan(0, length).IndexOf(marker) >= 0) return true;
                kept = Math.Min(marker.Length - 1, length);       // a marker cut by the end of the block
                buffer.AsSpan(length - kept, kept).CopyTo(buffer);
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Holds the generated <c>@page</c> rule (the sheet size). Emit once, in the document head.</summary>
    public const string PageStyle = """<style id="pr-page" media="print"></style>""";

    /// <summary>Opens the Print Report screen. Place it directly after the report's main controls — never behind
    /// trailing hint or status text, where it is easy to miss.</summary>
    public const string ToolbarButton =
        """<button id="pr-btn" class="no-print" title="Set up the printout — company, project, logo, paper — beside a live preview, then print or save as PDF">Print Report</button>""";

    /// <summary>The Print Report button for a report that has no toolbar of its own.</summary>
    public const string Bar = """<div class="prbar no-print">""" + ToolbarButton + "</div>";

    /// <summary>Shared stylesheet of the Print Report screen and its sheets — self-contained, so a printout looks
    /// the same from every report. Report-specific paper rules stay with the report, scoped to <c>.pr-sheet</c>.</summary>
    public const string Css = """
/* print engine: the Print Report screen, its sheets, and paper */
.print-only{display:none}
#pr-btn{border-color:#1F4788;color:#1F4788;font-weight:600}
.prbar{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin:0 0 12px}
.prbar button{font:12px "Segoe UI",system-ui,sans-serif;padding:3px 8px;border:1px solid #C7CCD2;border-radius:3px;background:#fff;cursor:pointer}
body.pr-open{overflow:hidden}
.pr-screen{position:fixed;left:0;top:0;right:0;bottom:0;z-index:1000;display:flex;flex-direction:column;background:#DDE1E6}
.pr-screen[hidden]{display:none}
/* composed without being shown: Ctrl+P, or a page opened for printing */
.pr-screen.pr-ghost{position:absolute;left:-100000px;right:auto;bottom:auto;width:10px;height:10px;overflow:hidden;display:block}
.pr-top,.pr-setup{font:12px/1.4 "Segoe UI",system-ui,sans-serif;color:#1A1A1A}
.pr-top{flex:none;display:flex;align-items:center;gap:12px;padding:8px 14px;background:#fff;border-bottom:2px solid #1F4788}
.pr-top b{font-size:14px;letter-spacing:.04em;text-transform:uppercase}.pr-top .pr-what{color:#666}.pr-top .pr-grow{flex:1}.pr-top .pr-count{color:#444;font-variant-numeric:tabular-nums}
.pr-screen button,.pr-screen select{font:12px "Segoe UI",system-ui,sans-serif;padding:4px 9px;border:1px solid #C7CCD2;border-radius:3px;background:#fff;color:#1A1A1A;cursor:pointer}
.pr-screen button:hover{background:#F3F4F6}
.pr-screen button.pri{background:#1F4788;border-color:#1F4788;color:#fff;font-weight:600}.pr-screen button.pri:hover{background:#17376b}
.pr-main{flex:1;min-height:0;display:flex}
.pr-setup{flex:none;box-sizing:border-box;width:306px;overflow:auto;background:#fff;border-right:1px solid #C7CCD2;padding:12px 14px 16px}
.pr-setup h3{margin:0 0 8px;font-size:11px;letter-spacing:.06em;text-transform:uppercase;color:#1F4788}
.pr-setup h3+p,.pr-setup .pr-hint{margin:0 0 9px;color:#666;font-size:11px}
.pr-setup label{display:block;margin:0 0 7px;font-size:11px;color:#555}
.pr-setup input[type=text]{display:block;box-sizing:border-box;width:100%;margin-top:2px;padding:4px 7px;border:1px solid #C7CCD2;border-radius:3px;font:12px "Segoe UI",system-ui,sans-serif;color:#1A1A1A}
.pr-setup input[type=text]:focus,.pr-setup select:focus{outline:2px solid #2E90FA;outline-offset:-1px}
.pr-setup .pr-two{display:grid;grid-template-columns:1fr 1fr;gap:0 8px}
.pr-setup .pr-two select{display:block;width:100%;margin-top:2px}
.pr-setup label.pr-check{display:flex;align-items:center;gap:7px;color:#1A1A1A;font-size:12px;margin:0 0 5px}
.pr-setup hr{border:0;border-top:1px solid #E4E7EC;margin:10px 0}
.pr-logo{display:flex;align-items:center;gap:8px;margin:2px 0 4px}
.pr-logo .pr-thumb{flex:none;width:92px;height:34px;border:1px dashed #C7CCD2;border-radius:3px;display:flex;align-items:center;justify-content:center;color:#98A2B3;font-size:10px;overflow:hidden}
.pr-logo .pr-thumb img{max-width:100%;max-height:100%}
.pr-setup .pr-go{display:block;width:100%;margin:12px 0 7px;padding:8px 9px;font-size:13px}
.pr-view{flex:1;min-width:0;overflow:auto;padding:18px 18px 6px}
.pr-wrap{margin:0 auto}
.pr-pages{transform-origin:0 0}
.pr-sheet{box-sizing:border-box;position:relative;display:flex;flex-direction:column;overflow:hidden;background:#fff;color:#1A1A1A;padding:5mm 10mm 4mm;margin:0 0 16px;box-shadow:0 1px 3px #0003,0 8px 22px #0002}
.pr-head{flex:none;box-sizing:border-box;height:9mm;display:grid;grid-template-columns:27fr 46fr 27fr;column-gap:4mm;align-items:end;padding:0 0 1mm;border-bottom:.75pt solid #1F4788;margin:0 0 2mm;white-space:nowrap}
.pr-head>div{overflow:hidden;text-overflow:ellipsis}
.pr-head .l{font:600 9.5pt/1.25 "Segoe UI",system-ui,Arial,sans-serif}.pr-head .l img{display:block;max-width:100%;max-height:7mm}
.pr-head .c{text-align:center;font:600 8.5pt/1.25 "Segoe UI",system-ui,Arial,sans-serif}.pr-head .c span{display:block;font-weight:400;color:#444}
.pr-head .r{text-align:right;font:7.5pt/1.25 "Segoe UI",system-ui,Arial,sans-serif;color:#444}.pr-head .r b{display:block;color:#1A1A1A;font-weight:600}
.pr-body{flex:1 1 0;min-height:0;overflow:hidden}
.pr-foot{flex:none;box-sizing:border-box;height:7mm;margin:2mm 0 0;padding:.8mm 0 0;border-top:.5pt solid #9AA0A6;display:grid;grid-template-columns:36fr 28fr 36fr;column-gap:4mm;font:7pt/1.2 "Segoe UI",system-ui,Arial,sans-serif;color:#555;white-space:nowrap}
.pr-foot>div{overflow:hidden;text-overflow:ellipsis}.pr-foot .c{text-align:center}.pr-foot .r{text-align:right}
/* an ancestor of a block, repeated around its copy so the report's own rules still match — it draws nothing */
.pr-shell{display:contents!important}
.pr-sec{font:600 9px "Segoe UI",system-ui,Arial,sans-serif;letter-spacing:.07em;text-transform:uppercase;color:#475467;border-bottom:1px solid #C7CCD2;margin:7px 0 5px;padding:0 0 2px}
.pr-sheet .no-print{display:none!important}.pr-sheet .print-only{display:block}
/* what is left of a control on paper reads as plain text */
.pr-sheet button{border:0!important;background:none!important;box-shadow:none!important;cursor:default;color:inherit}.pr-sheet .pr-val{font-weight:600}
.pr-sheet [data-print-fit]{overflow:hidden!important;max-height:none!important}
.pr-sheet svg{max-width:100%;height:auto}.pr-sheet a{color:inherit;text-decoration:none}
.pr-sheet [data-pr="info"]{padding:0 0 5px;margin:0 0 7px;border-bottom:1px solid #C7CCD2}.pr-sheet [data-pr="info"] h1{font-size:15px;margin:0}
.pr-sheet [data-pr="note"]{max-width:none}
.pr-sheet details>summary{list-style:none}.pr-sheet details>summary::-webkit-details-marker{display:none}
@media print{
  html,body{background:#fff!important;margin:0!important;padding:0!important;height:auto!important;overflow:visible!important}
  body{-webkit-print-color-adjust:exact;print-color-adjust:exact}
  /* the report itself is clipped to nothing, not removed: its SVG patterns must stay available to the sheets */
  body.pr-active>*:not(#pr-screen){position:absolute!important;height:0!important;min-height:0!important;max-height:0!important;overflow:hidden!important;contain:paint!important;margin:0!important;padding:0!important;border:0!important}
  #pr-screen{position:static!important;display:block!important;width:auto!important;height:auto!important;overflow:visible!important;background:none!important}
  #pr-screen .pr-top,#pr-screen .pr-setup{display:none!important}
  #pr-screen .pr-main,#pr-screen .pr-view,#pr-screen .pr-wrap{display:block!important;overflow:visible!important;padding:0!important;margin:0!important;width:auto!important;height:auto!important}
  #pr-screen .pr-pages{transform:none!important}
  .pr-sheet{box-shadow:none!important;margin:0!important;break-after:page;break-inside:avoid}
  .pr-sheet:last-child{break-after:auto}
}
""";

    // The Print Report screen and the composer. Sheets are real elements: what the preview shows is what the
    // browser prints, one sheet per page, so there is a single layout to get right.
    private const string Script = """
window.reportPrint = (function(){
  "use strict";
  const byId = id => document.getElementById(id);
  const dataNode = byId("print-data");
  const P = dataNode && dataNode.textContent ? JSON.parse(dataNode.textContent) : {};
  const PAPERS = { A4: [210, 297], A3: [297, 420], Letter: [215.9, 279.4] };     // mm, portrait
  const BAND = { top: 16, bottom: 13, side: 10 };     // mm of a sheet taken by the header band, the footer band, each side margin
  const PX = 96 / 25.4;                               // CSS px per mm
  const KEY = "cotvcf.printSetup.v1";                 // the user's latest setup, kept by this browser
  const TEXTS = ["company", "project", "jobNumber", "revision", "preparedBy", "checkedBy", "footerNote"];
  const ORDER = [["info", "info"], ["legend", "legends"], ["note", "generalAnnotations"], ["summary", "summary"], ["chart", "charts"], ["source", "sourceTables"]];

  // "#print&paper=A3&orientation=portrait&source=1" opens the page laid out for printing; "#print=screen" opens
  // the Print Report screen; "#print=package" is one report of several printed as one document; other keys
  // belong to the report
  const options = {};
  ((window.location && window.location.hash) || "").replace(/^#/, "").split("&").forEach(part => {
    if (!part) return;
    const i = part.indexOf("=");
    try { options[decodeURIComponent(i < 0 ? part : part.slice(0, i))] = i < 0 ? "1" : decodeURIComponent(part.slice(i + 1)); } catch (e) { }
  });
  const packaged = options.print === "package" && !!window.parent && window.parent !== window;
  const logoOk = v => typeof v === "string" && /^data:image\/[a-z0-9.+-]+;base64,[A-Za-z0-9+\/=]+$/.test(v);

  // ---- setup: this report's defaults (Tab 06 Print Setup at generation), then what this browser last used
  function defaults(source){
    const p = source || P;
    const d = { logo: logoOk(p.logo) ? p.logo : "", paper: PAPERS[p.paper] ? p.paper : "A4",
      orientation: p.orientation === "portrait" ? "portrait" : "landscape",
      showPageNumbers: p.showPageNumbers !== false, showDate: p.showDate !== false, includeSource: !!p.includeSourceData };
    TEXTS.forEach(k => { d[k] = typeof p[k] === "string" ? p[k] : ""; });
    return d;
  }
  function stored(){
    try { const raw = window.localStorage ? window.localStorage.getItem(KEY) : null, v = raw ? JSON.parse(raw) : null; return v && typeof v === "object" ? v : null; }
    catch (e) { return null; }
  }
  const S = { active: false, open: false, saved: false,
    auto: options.print != null && options.print !== "screen" };     // opened for printing: laid out for paper as soon as it has loaded
  function load(useStored){
    const d = defaults(), s = useStored ? stored() : null;
    S.saved = !!s;
    Object.keys(d).forEach(k => { S[k] = s && typeof s[k] === typeof d[k] ? s[k] : d[k]; });
    if (!PAPERS[S.paper]) S.paper = d.paper;
    if (S.orientation !== "portrait") S.orientation = "landscape";
    if (S.logo && !logoOk(S.logo)) S.logo = "";
    if (PAPERS[options.paper]) S.paper = options.paper;
    if (options.orientation === "portrait" || options.orientation === "landscape") S.orientation = options.orientation;
    if (options.source != null) S.includeSource = options.source === "1";
  }
  function save(){
    const v = {}; Object.keys(defaults()).forEach(k => { v[k] = S[k]; });
    try { window.localStorage.setItem(KEY, JSON.stringify(v)); S.saved = true; return true; }
    catch (e) { try { v.logo = ""; window.localStorage.setItem(KEY, JSON.stringify(v)); S.saved = true; } catch (e2) { } return false; }   // a logo too large to keep
  }
  load(true);

  // sheet geometry; the content width in CSS px is what a report lays its charts out against
  function page(){
    const d = PAPERS[S.paper], land = S.orientation === "landscape";
    const w = land ? d[1] : d[0], h = land ? d[0] : d[1];
    return { paper: S.paper, orientation: S.orientation, widthMm: w, heightMm: h,
      sheetWidthPx: Math.floor(w * PX), sheetHeightPx: Math.floor(h * PX),
      contentPx: Math.floor((w - 2 * BAND.side) * PX), contentHeightPx: Math.floor((h - BAND.top - BAND.bottom) * PX) };
  }
  // in a package the sheets are exactly the paper, to the fraction of a pixel: they are cut into pages by position
  const css = () => { const pg = page(); return "@page{size:" + pg.widthMm + "mm " + pg.heightMm + "mm;margin:0}"
    + (packaged ? ".pr-sheet{width:" + pg.widthMm + "mm!important;height:" + pg.heightMm + "mm!important}" : ""); };
  function apply(){ const style = byId("pr-page"); if (style) style.textContent = css(); }

  // ---- header and footer of a sheet. User text is escaped, so it cannot leave its cell.
  const esc = t => String(t == null ? "" : t).replace(/[&<>"']/g, m => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[m]));
  const join = parts => parts.filter(Boolean).join(" · ");
  function headHtml(){
    const ref = join([S.jobNumber && "Job No. " + S.jobNumber, S.revision && "Rev " + S.revision]);
    return '<div class="l">' + (S.logo ? '<img alt="" src="' + S.logo + '">' : esc(S.company)) + '</div>'
      + '<div class="c">' + esc(P.title || "") + (P.model ? '<span>' + esc(P.model) + '</span>' : '') + '</div>'
      + '<div class="r">' + (S.project ? '<b>' + esc(S.project) + '</b>' : '') + esc(ref) + '</div>';
  }
  function footHtml(number, count){
    return '<div class="l">' + esc(S.company) + '<br>' + esc(join([S.preparedBy && "Prepared by " + S.preparedBy, S.checkedBy && "Checked by " + S.checkedBy])) + '</div>'
      + '<div class="c">' + esc(S.footerNote) + '</div>'
      + '<div class="r">' + esc(S.showDate ? P.issued || "" : "") + '<br>' + (S.showPageNumbers ? "Page " + number + " of " + count : "") + '</div>';
  }

  // One line recording what the (hidden) toolbar was set to: "Group: Top · Component: Fz · Loading: all".
  function viewSummary(){
    const parts = [];
    Array.prototype.forEach.call(document.querySelectorAll("[data-print-controls] select, [data-print-controls] .ms > button"), control => {
      if (control.offsetParent === null) return;       // not shown
      if (control.tagName === "SELECT"){
        const label = control.closest ? control.closest("label") : null, option = control.options[control.selectedIndex];
        const name = label ? Array.prototype.filter.call(label.childNodes, node => node.nodeType === 3).map(node => node.textContent).join(" ").trim() : "";
        if (option) parts.push((name ? name + ": " : "") + option.textContent.trim());
      } else parts.push(control.textContent.trim());
    });
    return parts.join(" · ");
  }

  // ---- the report model: every printable block, by role, in the order the report lists it
  const hooks = [];
  const canCompose = () => !!(document.body && document.body.insertAdjacentHTML && document.querySelectorAll);
  const each = (list, f) => Array.prototype.forEach.call(list, f);
  const inScreen = el => !!(el.closest && el.closest("#pr-screen"));
  const shown = el => { const box = el.getBoundingClientRect(); return box.width > 0 && box.height > 0; };
  function model(){
    const m = { metadata: { title: P.title || "", model: P.model || "", issued: P.issued || "", paper: S.paper, orientation: S.orientation } };
    ORDER.forEach(o => { m[o[1]] = []; });
    if (!canCompose()) return m;
    let list = Array.prototype.filter.call(document.querySelectorAll("[data-pr]"), el => !inScreen(el) && shown(el));
    // a document that marks nothing prints its top-level blocks in the order they stand
    if (!list.length) list = Array.prototype.filter.call(document.body.children, el => !/^(SCRIPT|STYLE|TEMPLATE|LINK)$/.test(el.tagName)
      && el.id !== "pr-screen" && !(el.classList && (el.classList.contains("no-print") || el.classList.contains("lib"))) && shown(el));
    const once = {};
    list.forEach((el, i) => {
      const role = el.getAttribute("data-pr"), slot = ORDER.filter(o => o[0] === role)[0] || ORDER[4];
      if (slot[0] === "source" && !S.includeSource) return;
      if (slot[0] === "legend" || slot[0] === "note"){       // the same legend under several charts is stated once
        const sig = slot[0] + "|" + el.innerHTML; if (once[sig]) return; once[sig] = 1;
      }
      m[slot[1]].push({ id: el.getAttribute("data-pr-id") || slot[0] + "_" + (i + 1), type: el.getAttribute("data-pr-type") || (el.hasAttribute("data-pr-keep") ? "heading" : slot[0]),
        title: el.getAttribute("data-pr-title") || "", duplicatedFrom: el.getAttribute("data-pr-from") || null,
        keep: el.hasAttribute("data-pr-keep"), split: el.getAttribute("data-pr-split"), role: slot[0], el: el });
    });
    return m;
  }

  // ---- composing the sheets
  let screen = null, pagesEl = null, cur = null, sheets = [];
  const unfolded = [];                                 // collapsed sections of the report, opened while it is laid out for paper
  function build(){
    if (screen) return;
    document.body.insertAdjacentHTML("beforeend",
      '<div id="pr-screen" class="pr-screen" hidden><div class="pr-top"><b>Print Report</b><span class="pr-what"></span><span class="pr-grow"></span>'
      + '<span class="pr-count" id="pr-count"></span><select id="pr-zoom" aria-label="Preview size"><option value="width">Fit width</option><option value="page">Whole page</option><option value="1">100 %</option></select>'
      + '<button id="pr-close">✕ Close</button></div><div class="pr-main"><aside class="pr-setup" id="pr-setup"></aside>'
      + '<div class="pr-view" id="pr-view"><div class="pr-wrap" id="pr-wrap"><div class="pr-pages" id="pr-pages"></div></div></div></div></div>');
    screen = byId("pr-screen"); pagesEl = byId("pr-pages");
    screen.querySelector(".pr-what").textContent = join([P.title, P.model]);
    byId("pr-setup").innerHTML = setupHtml();
    wire();
  }
  function newSheet(pg){
    const el = document.createElement("div");
    el.className = "pr-sheet"; el.style.width = pg.sheetWidthPx + "px"; el.style.height = pg.sheetHeightPx + "px";
    el.innerHTML = '<div class="pr-head"></div><div class="pr-body"></div><div class="pr-foot"></div>';
    pagesEl.appendChild(el);
    cur = { el: el, body: el.children[1] };
    sheets.push(cur);
  }
  const over = () => cur.body.scrollHeight > cur.body.clientHeight + 1;

  // A copy of a block for the sheet, wrapped in copies of its ancestors that draw nothing ("shells") so that the
  // report's own style rules keep matching. Blocks marked inside it are placed on their own.
  function copyOf(entry){
    const el = entry.el, node = el.cloneNode(true);
    // a control prints as what it says, not as a control: a choice or a text field becomes its current text
    const from = el.querySelectorAll("select, textarea, input"), to = node.querySelectorAll("select, textarea, input");
    each(from, (control, i) => {
      const copy = to[i], kind = (control.getAttribute("type") || "text").toLowerCase();
      if (!copy || !copy.parentNode || (control.tagName === "INPUT" && kind !== "text")) return;
      const text = document.createElement("span"), option = control.tagName === "SELECT" ? control.options[control.selectedIndex] : null;
      text.className = (control.className ? control.className + " " : "") + "pr-val";
      text.textContent = control.tagName === "SELECT" ? (option ? option.textContent.trim() : "") : control.value;
      copy.parentNode.replaceChild(text, copy);
    });
    each(node.querySelectorAll("[data-pr]"), n => { if (n.parentNode) n.parentNode.removeChild(n); });
    if (node.tagName === "DETAILS") node.open = true;
    each(node.querySelectorAll("details"), d => { d.open = true; });
    let top = node;
    for (let a = el.parentElement; a && a !== document.body && a !== document.documentElement; a = a.parentElement){
      // a <details> ancestor would draw its own "Details" line: stand a plain element in for it
      let shell = a.cloneNode(false);
      if (shell.tagName === "DETAILS"){ shell = document.createElement("div"); if (a.id) shell.id = a.id; shell.className = a.className; }
      ["hidden", "style", "data-pr", "data-pr-id", "data-pr-type", "data-pr-keep", "data-pr-split"].forEach(name => shell.removeAttribute(name));
      shell.classList.add("pr-shell");
      shell.appendChild(top); top = shell;
    }
    return { top: top, node: node, keep: entry.keep, split: entry.split };
  }
  // a wide table is scaled inside its own box to the width it has on the sheet — down to 35 %, below which it
  // would be unreadable (the workbook is the place for a matrix that large)
  function fitWide(node){
    const boxes = Array.prototype.slice.call(node.querySelectorAll("[data-print-fit]"));
    if (node.hasAttribute && node.hasAttribute("data-print-fit")) boxes.push(node);
    boxes.forEach(box => {
      const room = box.parentNode && box.parentNode.clientWidth ? box.parentNode.clientWidth : cur.body.clientWidth;
      let zoom = 1;
      for (let pass = 0; pass < 5 && zoom > .35; pass++){
        const need = (box.scrollWidth || 0) * zoom;            // hairline cell borders do not shrink with the rest: re-measure
        if (need <= room) break;
        zoom = Math.max(.35, Math.floor(zoom * room / need * 1000) / 1000 - .002);
        box.style.zoom = String(zoom);
      }
    });
  }
  // where a block may continue on the next sheet: the parts it names, or the rows of its one large table
  function partsOf(c){
    if (c.split){ const named = Array.prototype.slice.call(c.node.querySelectorAll(c.split)); return named.length > 1 ? named : null; }
    const tables = c.node.querySelectorAll("table");
    if (tables.length !== 1 || !tables[0].tBodies.length) return null;
    const rows = Array.prototype.slice.call(tables[0].tBodies[0].rows);
    return rows.length > 1 ? rows : null;
  }
  const pathTo = (root, target) => { const path = []; for (let n = target; n !== root; n = n.parentNode) path.unshift(Array.prototype.indexOf.call(n.parentNode.childNodes, n)); return path; };
  const follow = (root, path) => path.reduce((n, i) => n.childNodes[i], root);
  function place(c, pg){
    cur.body.appendChild(c.top); fitWide(c.node);
    if (!over()) return;
    const parts = partsOf(c);
    if (parts) return splitInto(c, parts, pg);
    if (cur.body.children.length > 1){ carry(c, pg); if (!over()) return; }
    shrink(c.node);
  }
  // start a new sheet with this block, taking along the headings that must stay with it
  function carry(c, pg){
    const body = cur.body, lead = [];
    for (let n = c.top.previousElementSibling; n && n.__keep; n = n.previousElementSibling) lead.unshift(n);
    if (lead.length && !lead[0].previousElementSibling) lead.length = 0;      // they already open the sheet: moving them gains nothing
    body.removeChild(c.top); lead.forEach(n => body.removeChild(n));
    newSheet(pg);
    lead.forEach(n => cur.body.appendChild(n));
    cur.body.appendChild(c.top);
  }
  // a block taller than a whole sheet is scaled to it rather than cut off
  let scaleValue = 1;                                           // the preview scale; layout sizes are read free of it
  function shrink(node){
    const height = node.offsetHeight || node.getBoundingClientRect().height / (scaleValue || 1);
    const room = cur.body.clientHeight - (cur.body.scrollHeight - height);
    if (height > 0 && room > 40) node.style.zoom = String(Math.max(.3, Math.floor(room / height * 1000) / 1000 - .01));
  }
  function splitInto(c, parts, pg){
    const home = parts[0].parentNode, path = pathTo(c.top, home), after = parts[parts.length - 1].nextSibling;
    parts.forEach(part => part.parentNode.removeChild(part));
    const tail = after ? pathTo(c.top, after) : null;
    const frame = c.top.cloneNode(true);                       // the block without its parts: title, table head
    let target = c.top, i = 0, guard = 0;
    while (i < parts.length && guard++ < 5000){
      const into = follow(target, path), stop = tail ? follow(target, tail) : null;
      let placed = 0;
      while (i < parts.length){
        into.insertBefore(parts[i], stop && stop.parentNode === into ? stop : null);
        if (over()){
          if (placed > 0){ into.removeChild(parts[i]); break; }
          if (cur.body.children.length > 1){            // nothing fits under what is already here: take the block to a new sheet
            into.removeChild(parts[i]);
            const moved = { top: target, node: c.node, keep: c.keep };
            carry(moved, pg); continue;
          }
          shrink(parts[i].nodeType === 1 ? parts[i] : target);    // a single part taller than the sheet
        }
        placed++; i++;
      }
      if (i < parts.length){ newSheet(pg); target = frame.cloneNode(true); cur.body.appendChild(target); }
    }
  }
  function label(text){ const el = document.createElement("div"); el.className = "pr-sec"; el.textContent = text; el.__keep = true; return { top: el, node: el, keep: true }; }
  function compose(){
    if (!screen) return;
    const pg = page(), m = model();
    pagesEl.innerHTML = ""; sheets = []; cur = null;
    newSheet(pg);
    const put = c => { c.top.__keep = !!c.keep; place(c, pg); };
    ORDER.forEach(o => {
      const entries = m[o[1]];
      if (!entries.length) return;
      if (o[0] === "legend" || (o[0] === "note" && !m.legends.length)) put(label(m.legends.length && m.generalAnnotations.length ? "Legend · general notes" : m.legends.length ? "Legend" : "General notes"));
      if (o[0] === "source") put(label("Source data"));
      entries.forEach(entry => put(copyOf(entry)));
    });
    while (sheets.length > 1 && !sheets[sheets.length - 1].body.children.length) pagesEl.removeChild(sheets.pop().el);
    chrome(); scale();
    return m;
  }
  // the header and footer of every sheet — all a text field of the setup changes
  function chrome(){
    const head = headHtml();
    sheets.forEach((s, i) => { s.el.children[0].innerHTML = head; s.el.children[2].innerHTML = footHtml((S.pageOffset || 0) + i + 1, S.pageTotal || sheets.length); });
    const count = byId("pr-count"); if (count) count.textContent = sheets.length + (sheets.length === 1 ? " page · " : " pages · ") + S.paper + " " + S.orientation;
  }
  // the preview is the sheets at a scale that suits the window; paper prints them at full size
  function scale(){
    const view = byId("pr-view"), wrap = byId("pr-wrap"), zoom = byId("pr-zoom");
    if (!view || !sheets.length) return;
    const pg = page(), mode = zoom ? zoom.value : "width";
    const fitWidth = (view.clientWidth - 40) / pg.sheetWidthPx, fitPage = Math.min(fitWidth, (view.clientHeight - 30) / pg.sheetHeightPx);
    scaleValue = S.open ? Math.max(.2, mode === "1" ? 1 : mode === "page" ? fitPage : Math.min(1.5, fitWidth)) : 1;
    pagesEl.style.transform = scaleValue === 1 ? "" : "scale(" + scaleValue + ")";
    pagesEl.style.width = pg.sheetWidthPx + "px";
    wrap.style.width = Math.ceil(pg.sheetWidthPx * scaleValue) + "px";
    wrap.style.height = Math.ceil(pagesEl.offsetHeight * scaleValue) + "px";
  }

  // ---- the setup form
  const field = (key, text, hint) => '<label>' + text + '<input type="text" data-k="' + key + '" maxlength="90" autocomplete="off"' + (hint ? ' placeholder="' + hint + '"' : '') + '></label>';
  function setupHtml(){
    return '<h3>Report setup</h3><p>Printed on every page. The preview on the right is the printout.</p>'
      + field("company", "Company") + field("project", "Project")
      + '<div class="pr-two">' + field("jobNumber", "Job No.") + field("revision", "Revision") + '</div>'
      + '<div class="pr-two">' + field("preparedBy", "Prepared by") + field("checkedBy", "Checked by") + '</div>'
      + field("footerNote", "Footer note", "e.g. Preliminary — for design review")
      + '<label>Logo<div class="pr-logo"><span class="pr-thumb" id="pr-thumb"></span><button id="pr-logo-pick">Choose…</button><button id="pr-logo-clear">Remove</button></div></label>'
      + '<input type="file" id="pr-logo-file" accept="image/png,image/jpeg,image/svg+xml,image/gif,image/webp,image/bmp" hidden>'
      + '<p class="pr-hint" id="pr-logo-info"></p><hr>'
      + '<div class="pr-two"><label>Paper<select id="pr-paper"><option value="A4">A4</option><option value="A3">A3</option><option value="Letter">Letter</option></select></label>'
      + '<label>Orientation<select id="pr-orient"><option value="landscape">Landscape</option><option value="portrait">Portrait</option></select></label></div>'
      + '<label class="pr-check"><input type="checkbox" id="pr-numbers"> Page numbers</label>'
      + '<label class="pr-check"><input type="checkbox" id="pr-date"> Date</label>'
      + '<label class="pr-check"><input type="checkbox" id="pr-src"> Include source data table</label>'
      + '<button id="pr-go" class="pri pr-go">Print / Save as PDF</button>'
      + '<p class="pr-hint" id="pr-saved"></p><button id="pr-reset">Use this report\'s defaults</button>';
  }
  function sync(){
    if (!screen) return;
    each(screen.querySelectorAll("input[data-k]"), input => { if (input.value !== S[input.getAttribute("data-k")]) input.value = S[input.getAttribute("data-k")]; });
    byId("pr-paper").value = S.paper; byId("pr-orient").value = S.orientation;
    byId("pr-numbers").checked = S.showPageNumbers; byId("pr-date").checked = S.showDate; byId("pr-src").checked = S.includeSource;
    byId("pr-thumb").innerHTML = S.logo ? '<img alt="Logo" src="' + S.logo + '">' : "no logo";
    byId("pr-logo-info").textContent = S.logo ? "Printed top-left on every page." : "Without a logo the company name is printed top-left.";
    byId("pr-logo-clear").disabled = !S.logo;
    byId("pr-saved").textContent = S.saved ? "Your setup is kept by this browser and used for every report you open."
      : "From the Print Setup this report was generated with. Your changes are kept by this browser.";
    byId("pr-reset").hidden = !S.saved;
  }
  function wire(){
    each(screen.querySelectorAll("input[data-k]"), input => { input.oninput = () => { S[input.getAttribute("data-k")] = input.value; save(); sync(); chrome(); }; });
    byId("pr-paper").onchange = e => { if (PAPERS[e.target.value]) S.paper = e.target.value; save(); relay(); };
    byId("pr-orient").onchange = e => { S.orientation = e.target.value === "portrait" ? "portrait" : "landscape"; save(); relay(); };
    byId("pr-numbers").onchange = e => { S.showPageNumbers = !!e.target.checked; save(); sync(); chrome(); };
    byId("pr-date").onchange = e => { S.showDate = !!e.target.checked; save(); sync(); chrome(); };
    byId("pr-src").onchange = e => { S.includeSource = !!e.target.checked; save(); relay(); };
    byId("pr-logo-pick").onclick = () => byId("pr-logo-file").click();
    byId("pr-logo-clear").onclick = () => { S.logo = ""; save(); sync(); chrome(); };
    byId("pr-logo-file").onchange = e => {
      const file = e.target.files && e.target.files[0]; e.target.value = "";
      if (!file) return;
      if (file.size > 1500000){ byId("pr-logo-info").textContent = "That file is " + Math.round(file.size / 1024) + " KB; a logo may be up to 1,500 KB."; return; }
      const reader = new FileReader();
      reader.onload = () => {
        if (!logoOk(reader.result)){ byId("pr-logo-info").textContent = "That file is not an image this report can print. Use PNG, JPG, SVG, GIF, WEBP or BMP."; return; }
        S.logo = reader.result; const kept = save(); sync(); chrome();
        if (!kept) byId("pr-logo-info").textContent = "Printed top-left on every page. This logo is too large for the browser to remember: choose it again next time.";
      };
      reader.readAsDataURL(file);
    };
    byId("pr-reset").onclick = () => { try { window.localStorage.removeItem(KEY); } catch (e) { } load(false); relay(); };
    byId("pr-zoom").onchange = scale;
    byId("pr-close").onclick = close;
    byId("pr-go").onclick = print;
  }

  // ---- entering and leaving the paper layout
  function enter(){
    if (S.active) return;
    S.active = true; apply();
    // "pr-on" lets a report apply rules that must already hold while its blocks are laid out for the sheet
    if (document.body && document.body.classList){ document.body.classList.add("pr-on"); document.body.classList.add("pr-active"); document.body.classList.toggle("pr-nosrc", !S.includeSource); }
    const pg = page();
    hooks.forEach(hook => { if (hook.before) hook.before(pg); });
    if (!canCompose()) return;
    const view = document.querySelector("[data-print-view]");
    if (view){ const text = viewSummary(); view.textContent = text ? "Printed view: " + text : ""; }
    // a collapsed section still belongs on paper: open it so that its blocks are laid out and can be measured
    each(document.querySelectorAll("details"), d => {
      if (d.open || inScreen(d) || d.getAttribute("data-pr") === "source") return;
      if (d.hasAttribute("data-pr") || d.querySelector("[data-pr]")){ d.open = true; unfolded.push(d); }
    });
    build();
    screen.hidden = false; screen.classList.toggle("pr-ghost", !S.open);
    sync(); compose();
  }
  // paper, orientation or the source option changed: the report lays its charts out again, then the sheets
  function relay(){
    if (!S.active) return;
    apply();
    if (document.body && document.body.classList) document.body.classList.toggle("pr-nosrc", !S.includeSource);
    const pg = page();
    hooks.forEach(hook => { if (hook.before) hook.before(pg); });
    sync(); compose();
  }
  function leave(){
    if (!S.active || S.auto) return;
    S.active = false; S.open = false;
    if (document.body && document.body.classList) ["pr-on", "pr-active", "pr-open", "pr-nosrc"].forEach(name => document.body.classList.remove(name));
    if (screen){ screen.hidden = true; pagesEl.innerHTML = ""; sheets = []; cur = null; }
    unfolded.splice(0).forEach(d => { d.open = false; });
    hooks.forEach(hook => { if (hook.after) hook.after(); });
  }
  // the Print Report button: the setup beside the preview — printing is a button on that screen
  function open(){
    if (S.open) return;
    S.open = true;
    if (document.body && document.body.classList) document.body.classList.add("pr-open");
    if (S.active){ screen && screen.classList.remove("pr-ghost"); scale(); } else enter();
    if (screen){ const first = screen.querySelector("input[data-k]"); if (first && first.focus) first.focus(); }
  }
  function close(){ S.auto = false; leave(); }
  function print(){ enter(); window.print(); }

  // ---- one report of a package: a page that prints several reports as one document loads each in a frame
  // ("#print=package"). It hands over the setup to print with, is told how many sheets this report makes, and
  // answers with this report's place in the page numbers of the whole document.
  const tell = message => { try { window.parent.postMessage(Object.assign({ cotvcf: "print" }, message), "*"); } catch (e) { } };
  if (packaged) window.addEventListener("message", e => {
    const m = e.data;
    if (!m || m.cotvcf !== "print" || e.source !== window.parent) return;
    if (m.type === "setup"){
      if (m.setup && typeof m.setup === "object"){ const d = defaults(m.setup); Object.keys(d).forEach(k => { S[k] = d[k]; }); }
      if (S.active) relay(); else enter();
      const pg = page(); tell({ type: "sheets", count: sheets.length, widthMm: pg.widthMm, heightMm: pg.heightMm });
    } else if (m.type === "number"){ S.pageOffset = m.offset | 0; S.pageTotal = m.total | 0; chrome(); }
  });

  const button = byId("pr-btn");
  if (button) button.onclick = open;
  window.addEventListener("beforeprint", enter);       // Ctrl+P and the browser menu print the same sheets
  window.addEventListener("afterprint", () => { if (!S.open) leave(); });
  window.addEventListener("resize", () => { if (S.open) scale(); });
  window.addEventListener("keydown", e => { if (S.open && e.key === "Escape") close(); });
  window.addEventListener("load", () => {
    if (packaged) tell({ type: "ready" });               // the package answers with the setup; then the sheets are composed
    else if (options.print === "screen") open();
    else if (S.auto) enter();
    if ((S.auto || options.print === "screen") && document.documentElement) document.documentElement.setAttribute("data-print-ready", "1");
  });
  apply();

  return { state: S, data: P, options: options, page: page, css: css, headHtml: headHtml, footHtml: footHtml, model: model,
    enter: enter, leave: leave, open: open, close: close, print: print, compose: compose,
    reset: () => { load(false); relay(); }, register: hook => { hooks.push(hook); } };
})();
""";
}
