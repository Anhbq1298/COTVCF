using System.IO;
using System.Text.Json;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Results;

/// <summary>Builds the final self-contained <c>Deflection_Report.html</c>: an interactive deflection-profile
/// viewer with an embedded JSON payload and a dependency-free inline SVG chart (portable, offline, no CDN).
/// Can be regenerated from <c>Deflection_Results_Source.xlsx</c> alone — no SAP.</summary>
public sealed class DeflectionReportGenerator
{
    public const string ReportFileName = "Deflection_Report.html";

    private static readonly string[] Palette =
        ["#1f77b4", "#d62728", "#2ca02c", "#ff7f0e", "#9467bd", "#8c564b", "#17becf", "#bcbd22",
         "#e377c2", "#7f7f7f", "#393b79", "#637939"];
    private static readonly string[] Dashes =
        ["solid", "dash", "dot", "dashdot", "longdash", "longdashdot"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // ---------- public entry points ----------

    public string Generate(
        string studyFolder,
        IReadOnlyList<CandidateResultDto> results,
        string baselineName, string baselinePath, IterationStudyMode studyMode,
        IReadOnlyList<string>? siblingReports = null, DeflectionLimitBand? limitBand = null,
        ReportPrintSettings? print = null)
    {
        Directory.CreateDirectory(studyFolder);
        var dto = Build(results, studyFolder, baselineName, baselinePath, studyMode, siblingReports, limitBand);
        var path = Path.Combine(studyFolder, ReportFileName);
        AtomicFile.WriteAllText(path, BuildHtml(dto, ReportPrintEngine.Describe(print, ReportTitle, baselineName, DateTimeOffset.Now)),
            new System.Text.UTF8Encoding(false));
        return path;
    }

    public string RegenerateFromWorkbook(string studyFolder)
    {
        var wbPath = Path.Combine(studyFolder, ConsolidatedResultWriter.WorkbookFileName);
        if (!File.Exists(wbPath))
            throw new FileNotFoundException($"No {ConsolidatedResultWriter.WorkbookFileName} to regenerate from.", wbPath);
        var results = ConsolidatedResultWriter.Read(wbPath);
        var mode = results.Count > 0 ? results[0].StudyMode : IterationStudyMode.Combined;
        var name = results.Count > 0 ? results[0].BaselineModelName : "";
        var bpath = results.Count > 0 ? results[0].BaselineModelPath : "";
        return Generate(studyFolder, results, name, bpath, mode);
    }

    // ---------- DTO assembly ----------

    public DeflectionReportDto Build(
        IReadOnlyList<CandidateResultDto> results,
        string studyFolder, string baselineName, string baselinePath, IterationStudyMode studyMode,
        IReadOnlyList<string>? siblingReports = null, DeflectionLimitBand? limitBand = null)
    {
        // One report group per (monitored point group × result load case) — so a study that extracts Uz
        // under several cases yields one selectable chart source per case.
        var groupKeys = results.SelectMany(r => r.Groups)
            .Select(g => (g.GroupName, g.OutputCase))
            .Distinct().OrderBy(k => k.GroupName, StringComparer.Ordinal).ThenBy(k => k.OutputCase, StringComparer.Ordinal).ToArray();

        var flat = results.Where(r => r.Groups.Any(g => g.Points.Any(p => p.Status == ResultValueStatus.Valid)))
            .OrderBy(r => r.Parameters.RunIndex).Select(r => r.Parameters).ToArray();
        // Legend rows are just the candidate id (L## / H## / P##) — the full make-up of every id is
        // spelled out in the "Candidate definitions" block under the charts, so the legend stays compact.
        Dim heightDim = new("Truss height", p => p.HeightCandidateId, k => k);
        Dim loadDim = new("Load candidate", p => p.LoadingCandidateId, k => k);
        Dim preDim = new("Initial pretension", p => p.PretensionCandidateId, k => k);

        // Always the SAME two complementary charts, whatever the study mode — colour = load candidate,
        // then the other two dimensions swap between the line-style and the filter slot. A dimension the
        // study holds fixed simply shows a single legend / filter row.
        //   A: colour load, line truss height,      filter initial pretension
        //   B: colour load, line initial pretension, filter truss height
        var viewSpecs = new List<(Dim Color, Dim Dash, Dim? Filter)>
        {
            (loadDim, heightDim, preDim),
            (loadDim, preDim, heightDim),
        };

        List<LegendEntryDto> ColorLegendFor(Dim d) => flat.Select(d.Key).Distinct()
            .OrderBy(SortKey).ThenBy(k => k, StringComparer.Ordinal)
            .Select((k, i) => new LegendEntryDto(k, d.Label(k), k == "" ? "#9aa0a6" : Palette[i % Palette.Length]))
            .ToList();
        List<LegendEntryDto> DashLegendFor(Dim d) => flat.Select(d.Key).Distinct()
            .OrderBy(SortKey).ThenBy(k => k, StringComparer.Ordinal)
            .Select((k, i) => new LegendEntryDto(k, d.Label(k), Dashes[i % Dashes.Length]))
            .ToList();
        List<LegendEntryDto> FilterLegendFor(Dim? d) => d is null ? [] : flat.Select(d.Key).Distinct()
            .OrderBy(SortKey).ThenBy(k => k, StringComparer.Ordinal)
            .Select(k => new LegendEntryDto(k, d.Label(k), "")).ToList();

        var views = viewSpecs.Select(vs => new ChartViewDto(
            vs.Color.Name, ColorLegendFor(vs.Color),
            vs.Dash.Name, DashLegendFor(vs.Dash), ReferenceEquals(vs.Dash, preDim),
            vs.Filter?.Name ?? "", FilterLegendFor(vs.Filter))).ToList();

        var groups = new List<DeflectionGroupReportDto>();
        foreach (var (gn, oc) in groupKeys)
        {
            var series = new List<DeflectionCandidateSeriesDto>();
            string axis = "X";
            foreach (var r in results.OrderBy(r => r.Parameters.RunIndex))
            {
                var g = r.Groups.FirstOrDefault(x => x.GroupName == gn && x.OutputCase == oc);
                if (g is null || g.Points.All(p => p.Status != ResultValueStatus.Valid)) continue; // only candidates with data
                axis = g.ProfileAxis.ToString();
                series.Add(new DeflectionCandidateSeriesDto(
                    r.IterationKey, r.Parameters.RunIndex,
                    r.Parameters.LoadingCandidateId, r.Parameters.HeightCandidateId, r.Parameters.PretensionCandidateId,
                    r.Parameters.LoadLevelKPa, r.Parameters.LoadingLabel,
                    r.Parameters.TrussHeightM, r.Parameters.DeltaHM, r.Parameters.PretensionKN, r.Parameters.PretensionLabel,
                    r.ExtractionStatus.ToString(), r.DiagnosticReportRelativePath,
                    viewSpecs.Select(vs => vs.Color.Key(r.Parameters)).ToArray(),
                    viewSpecs.Select(vs => vs.Dash.Key(r.Parameters)).ToArray(),
                    viewSpecs.Select(vs => vs.Filter?.Key(r.Parameters) ?? "").ToArray(),
                    (r.Parameters.PretensionCables ?? []).Select(c => new PretensionCableEntryDto(c.CableName, c.TargetForceKN)).ToArray(),
                    (r.Parameters.LoadingComponents ?? []).Select(c => new LoadComponentEntryDto(c.PatternName, c.LevelKPa)).ToArray(),
                    g.Points.OrderBy(p => p.Sequence).Select(p => new DeflectionPointDto(
                        p.PointName, Axis(p, g.ProfileAxis), p.UzMm, p.Sequence, p.Status.ToString())).ToArray(),
                    DeflectionLimitBand.PeakDownMm(g.Points
                        .Where(p => p.Status == ResultValueStatus.Valid && p.UzMm.HasValue).Select(p => p.UzMm!.Value))));
            }
            if (series.Count > 0)
                groups.Add(new DeflectionGroupReportDto(gn, oc, axis, series));
        }

        var warnings = results.SelectMany(r => r.Warnings)
            .Select(w => new ResultReportWarningDto(w.IterationKey, w.Category, w.Severity.ToString(), w.Message))
            .ToArray();

        var study = new StudyMetadataDto(
            baselineName, baselinePath, studyFolder, studyMode.ToString(),
            results.Count, DateTimeOffset.Now.ToString("o"), "Uz", "m", "mm");

        var siblings = siblingReports?.ToArray() ?? ReportNav.PresentIn(studyFolder, "deflection").ToArray();
        var band = limitBand is { IsActive: true } ? new DeflectionLimitBandDto(limitBand.MinMm, limitBand.MaxMm) : null;
        return new DeflectionReportDto(study, groups, views, warnings, siblings, band);
    }

    private sealed record Dim(string Name, Func<CandidateParametersDto, string> Key, Func<string, string> Label);

    private static double Axis(MonitoredPointResultDto p, ProfileCoordinateAxis a) =>
        a switch { ProfileCoordinateAxis.Y => p.Y, ProfileCoordinateAxis.Z => p.Z, _ => p.X };

    private static double SortKey(string k) => k == "" ? double.MaxValue : double.TryParse(k, out var v) ? v : double.MaxValue;

    // ---------- HTML ----------

    /// <summary>Report name printed in the page header.</summary>
    public const string ReportTitle = "Deflection · Uz";

    public string BuildHtml(DeflectionReportDto dto, ReportPrintDto? print = null)
    {
        var json = JsonSerializer.Serialize(dto, Json);
        var s = dto.Study;
        print ??= ReportPrintEngine.Describe(null, ReportTitle, s.BaselineModelName, DateTimeOffset.Now);
        var library = ReportNav.Library("deflection", new HashSet<string>(dto.SiblingReports, StringComparer.Ordinal));
        var bandNote = dto.LimitBand is { } b
            ? $"<br>Shaded strip = deflection <b>limit band</b> {Esc(new DeflectionLimitBand(b.MinMm, b.MaxMm).Label)} on the peak " +
              "downward U<sub>z</sub> (Tab 04 Output Settings): a candidate passes when the lowest point of its line sits inside the strip."
            : "";
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8">
<title>Deflection Report — {{Esc(s.BaselineModelName)}}</title>
<style>{{ReportPrintEngine.Css}}{{Style}}</style>{{ReportPrintEngine.PageStyle}}</head><body>
<header>
  <div>
    <h1>Deflection Results Viewer</h1>
    <p class="meta">{{Esc(s.BaselineModelName)}} · mode {{Esc(s.StudyMode)}} · {{s.CandidateCount}} candidate(s) ·
      generated {{Esc(s.GeneratedAtIso[..19].Replace('T', ' '))}}</p>
    <p class="meta print-only" data-print-view></p>
    <p class="intro">Each line is one study candidate: the vertical deflection U<sub>z</sub> [mm] of every monitored
      point plotted against its position X [m] along the truss span, under the selected result load case.
      <b>Colour</b> = load candidate · <b>line style</b> = truss height · <b>marker</b> = pretension candidate.
      See <i>Candidate definitions</i> below for what each L## / H## / P## is.{{bandNote}}</p>
  </div>
  {{library}}
</header>
<div id="toolbar" data-print-controls>
  <label>Group <select id="group"></select></label>
  <span class="sep"></span>
  <label><input type="checkbox" id="legendToggle" checked> Plot legend</label>
  <label id="bandToggleWrap" title="Show the deflection limit band and the pass count"><input type="checkbox" id="bandToggle" checked> Limit band</label>
  <button id="reset">Reset zoom</button>
  <button id="srcBtn">View source data</button>
  <span class="sep"></span>
  <span class="hint">each chart has its own name + L / H / P filter · click a legend row = highlight · wheel = zoom X · drag = pan · dbl-click = reset · Duplicate clones the chart</span>
  {{ReportPrintEngine.ToolbarButton}}
{{ReportPrintEngine.Panel}}</div>
<div id="panels"></div>
<template id="panelTpl">
  <section class="panel">
    <div class="pbar">
      <input class="pv-name" title="Chart name — click to edit" spellcheck="false">
      <span class="pv-sub"></span>
      <span class="pv-band" hidden></span>
      <span class="pv-ctl"></span>
      <button class="pv-dup" title="Duplicate this chart with all its current settings">＋ Duplicate</button>
      <button class="pv-rm" title="Remove this chart" hidden>✕ Remove</button>
    </div>
    <div class="stage"><svg class="chart" preserveAspectRatio="none"></svg><div class="legend"></div></div>
  </section>
</template>
<div id="tip" hidden></div>
<details id="candKey" open><summary>Candidate definitions — what each L## / H## / P## is</summary><div id="candKeyBody"></div></details>
<div id="matrixWrap" data-print="source" hidden>
  <div class="mhdr"><strong>Source Data Matrix</strong><button id="mClose">Close</button></div>
  <div id="matrix" data-print-fit></div>
</div>
<div id="warns"></div>
<script type="application/json" id="report-data">{{json}}</script>
{{ReportPrintEngine.Scripts(print)}}
<script>{{Script}}</script>
</body></html>
""";
    }

    private static string Esc(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");

    // ---------- inline assets ----------

    private const string Style = """
:root{--acc:#1F4788;--bd:#C7CCD2;--su:#F3F4F6;--tx:#1A1A1A;--mu:#666}
*{box-sizing:border-box}html,body{margin:0}
body{font:13px/1.45 "Segoe UI",system-ui,sans-serif;color:var(--tx);background:#fff}
[hidden]{display:none!important}
header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;padding:10px 14px;border-bottom:2px solid var(--acc)}
h1{font-size:16px;margin:0}.meta{color:var(--mu);margin:3px 0 0;font-size:11.5px}
.intro{margin:6px 0 0;font-size:11.5px;line-height:1.5;max-width:820px}.intro b{color:var(--acc)}
.lib{font-size:12px}.lib summary{cursor:pointer;color:var(--acc);font-weight:600}
.lib ul{list-style:none;margin:6px 0 0;padding:0;border:1px solid var(--bd);border-radius:3px;min-width:180px}
.lib li{padding:4px 8px;border-bottom:1px solid var(--su);color:var(--mu)}
.lib li.on{color:var(--tx)}.lib li span{float:right;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:#9aa0a6}
.lib li.on span{color:#2E7D32}
#toolbar{display:flex;align-items:center;gap:10px;padding:6px 14px;border-bottom:1px solid var(--bd);flex-wrap:wrap;position:sticky;top:0;background:#fff;z-index:6}
#toolbar .sep{width:1px;height:16px;background:var(--bd)}
#toolbar .hint{color:var(--mu);font-size:11px}
select,button{font:12px "Segoe UI",sans-serif;padding:3px 8px;border:1px solid var(--bd);background:#fff;border-radius:3px}
button{cursor:pointer}button:hover{background:var(--su)}
#srcBtn{border-color:var(--acc);color:var(--acc);font-weight:600}
/* candidate multi-select dropdowns (L / H / P global filter) */
#candFilter{display:flex;gap:6px}
.ms{position:relative}
.ms>button{font:12px "Segoe UI",sans-serif;padding:3px 8px;min-width:88px;text-align:left}
.ms.act>button{border-color:var(--acc);color:var(--acc);font-weight:600}
.ms-pop{position:absolute;top:100%;left:0;z-index:30;margin-top:2px;background:#fff;border:1px solid var(--bd);border-radius:3px;box-shadow:0 4px 14px #0002;max-height:280px;overflow:auto;min-width:200px;padding:4px 0}
.ms-pop label{display:flex;align-items:center;gap:6px;padding:3px 10px;font-size:11.5px;cursor:pointer;white-space:nowrap}
.ms-pop label:hover{background:var(--su)}
.ms-pop .all{border-bottom:1px solid var(--bd);font-weight:600}
#panels{padding-bottom:8px}
.panel{border-bottom:1px solid var(--bd)}
.pbar{display:flex;align-items:center;gap:8px;padding:8px 14px 2px;flex-wrap:wrap}
.pv-name{font:600 12.5px "Segoe UI",sans-serif;color:var(--acc);border:1px solid transparent;background:transparent;padding:2px 5px;border-radius:3px;min-width:200px;max-width:360px}
.pv-name:hover{border-color:var(--bd)}
.pv-name:focus{border-color:var(--acc);background:#fff;outline:none}
.pv-sub{font-size:11px;color:var(--mu)}
/* limit-band pass count: text stays in ink, the check icon carries the status colour */
.pv-band{font-size:11px;color:var(--tx);border:1px solid var(--bd);border-radius:3px;padding:1px 7px;background:#F3F8F3}
.pv-band .ok{color:#2E7D32;font-weight:700}
.pv-ctl{display:flex;gap:6px;flex-wrap:wrap}
.pv-ctl select{font-size:11px;padding:2px 4px;max-width:230px}
.pv-dup{margin-left:auto;font-size:11px;padding:2px 8px}
.pv-rm{font-size:11px;padding:2px 8px;border-color:#C62828;color:#C62828}
.panel.copy{background:#fafbfd}
.panel.copy .pbar{border-left:3px solid var(--acc);padding-left:11px}
.stage{display:flex;height:390px;padding:4px 6px 10px}
.chart{flex:1;height:100%;min-width:0;background:#fff}
.legend{width:250px;flex:none;overflow:auto;border-left:1px solid var(--bd);padding:8px;font-size:11.5px}
.legend h4{margin:8px 0 4px;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:var(--mu)}
.legend .hint2{font-weight:400;text-transform:none;letter-spacing:0;color:#9aa0a6;font-size:9.5px}
.legend .row{display:flex;align-items:center;gap:6px;padding:2px 0;cursor:pointer}
.legend .lcb{flex:none;margin:0 2px 0 0;cursor:pointer}
.legend .row.key:hover,.legend .row.dkey:hover,.legend .row.pkey:hover{background:#f0f3f7;border-radius:2px}
.legend .fdot{width:8px;height:8px;border-radius:50%;background:#9aa0a6;flex:none;margin:0 8px 0 8px}
.legend .row.off{opacity:.35}
.legend .row.hi{background:#e8f0fe;border-radius:2px}
.legend .sw{width:24px;height:0;border-top-width:3px;border-top-style:solid;flex:none}
.legend .dash{width:26px;height:0;border-top:2px solid #444;flex:none}
.legend .mk{flex:none;overflow:visible}
.legend .bd{margin-left:auto;padding-left:4px;font-weight:700;font-size:11px}
.bd.ok{color:#2E7D32}.bd.no{color:#C62828}
#tip{position:fixed;pointer-events:none;background:#fffffff2;border:1px solid var(--acc);border-radius:3px;padding:6px 8px;font:11px/1.4 Consolas,monospace;z-index:20;max-width:280px}
#candKey{border-top:1px solid var(--bd);font-size:11.5px}
#candKey>summary{cursor:pointer;padding:6px 14px;background:var(--su);font-weight:600;color:var(--acc)}
#candKeyBody{display:flex;gap:36px;flex-wrap:wrap;padding:12px 14px 16px;align-items:flex-start}
#candKeyBody .grp{min-width:200px}
#candKeyBody .pgrp{min-width:340px}
#candKeyBody h5{margin:0 0 6px;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:var(--mu)}
#candKeyBody h5 .cnt{color:var(--acc);font-weight:700;font-size:11px;margin-left:3px}
#candKeyBody .kr{display:flex;align-items:flex-start;gap:8px;padding:2px 0}
#candKeyBody .kid{font:600 11px Consolas,monospace;color:var(--tx);flex:none}
#candKeyBody .kval{line-height:1.5}
#candKeyBody .ksw{display:inline-block;width:22px;height:0;border-top-width:3px;border-top-style:solid;flex:none;vertical-align:middle}
#candKeyBody .kmk{flex:none}
#candKeyBody table.kt{border-collapse:collapse;font-size:11px}
#candKeyBody table.kt th{font-weight:400;color:var(--mu);text-align:right;padding:0 8px 3px;font-size:10px;white-space:nowrap}
#candKeyBody table.kt td{padding:2px 8px;text-align:right;white-space:nowrap}
#candKeyBody table.kt td.kid{text-align:left;padding-left:2px}
#candKeyBody table.kt td.csw{padding-right:0;width:26px}
#candKeyBody .pblk{margin-bottom:8px;padding-bottom:8px;border-bottom:1px solid var(--bd)}
#candKeyBody .pblk:last-child{border-bottom:none;margin-bottom:0;padding-bottom:0}
#candKeyBody .pgh{display:flex;align-items:center;gap:8px}
#candKeyBody .pgh .pgn{color:var(--mu);font-size:10.5px}
#candKeyBody .ptbl{margin:3px 0 0 26px}
#candKeyBody .ptbl td:nth-child(1),#candKeyBody .ptbl td:nth-child(4){text-align:left;font-family:Consolas,monospace;color:var(--tx)}
#candKeyBody .ptbl td.pgap{width:1px;min-width:1px;padding:0;background:var(--bd)}
#matrixWrap{border-top:1px solid var(--bd);border-bottom:1px solid var(--bd)}
.mhdr{display:flex;justify-content:space-between;align-items:center;padding:5px 14px;background:var(--su)}
#matrix{overflow:auto;max-height:42vh}
#matrix table{border-collapse:separate;border-spacing:0;font-size:11px;white-space:nowrap}
#matrix th,#matrix td{border:1px solid var(--bd);padding:3px 8px;text-align:right;background:#fff}
/* the two frozen key columns get identical fixed widths on header AND body rows so the
   sticky offsets stay exact and the columns never drift out from under their headers */
#matrix th:first-child,#matrix td:first-child{width:88px;min-width:88px;max-width:88px;text-align:left;overflow:hidden;text-overflow:ellipsis}
#matrix th:nth-child(2),#matrix td:nth-child(2){width:66px;min-width:66px;max-width:66px}
#matrix thead th{position:sticky;top:0;background:var(--su);z-index:3}
#matrix th:first-child{position:sticky;left:0;background:var(--su);z-index:2}
#matrix th:nth-child(2),#matrix td:nth-child(2){position:sticky;left:88px;z-index:2}
#matrix tbody td:nth-child(2){background:#fff}
#matrix thead th:first-child,#matrix thead th:nth-child(2){z-index:4;background:var(--su)}
#matrix th.k{font-weight:600;color:var(--mu)}
#matrix thead th.ch{cursor:pointer}#matrix thead th.ch:hover{background:#e8f0fe}
#matrix .hi{background:#e8f0fe !important}
#matrix tfoot th,#matrix tfoot td{background:var(--su);font-weight:600}
#legend .row.hi{background:#e8f0fe;border-radius:2px}
#warns{padding:6px 14px;font-size:11px;color:#8a6d00}
#warns .e{color:#C62828}
a{color:var(--acc)}
/* paper: the sheet, margins, header and footer come from the shared print engine. Each chart is redrawn at the
   full sheet width with its legend underneath in columns (a side column would run off the page), and keeps its
   own case / view / filter controls as a record of what is shown. */
@media print{
  header{display:block;padding:0 0 5px}
  /* the intro repeats what the printed legend headings and the limit-band labels already say */
  .lib,.intro,#toolbar,#tip,.pv-dup,.pv-rm,.mhdr button,.legend .lcb,.legend .hint2{display:none!important}
  .panel{border-bottom:0}.panel.copy{background:none}
  .pbar{padding:6px 0 2px;break-after:avoid}
  .stage{display:block;height:auto;padding:4px 0 8px}
  .chart{display:block;width:100%}
  .legend{width:auto;border-left:0;border-top:1px solid var(--bd);overflow:visible;padding:6px 0 0;font-size:10px;columns:7;column-gap:14px;break-inside:avoid}
  .legend h4{margin:0 0 3px;break-after:avoid}.legend .row{padding:1px 0;break-inside:avoid}
  #candKey{border-top:0;margin-top:6px}#candKey>summary{list-style:none;padding:5px 0;background:none}
  #candKey>summary::-webkit-details-marker{display:none}
  #candKeyBody{padding:6px 0 10px;gap:24px}#candKeyBody .grp,#candKeyBody .pblk{break-inside:avoid}
  #matrixWrap{border:0}.mhdr{padding:5px 0;background:none}
  #matrix thead th,#matrix th:first-child,#matrix th:nth-child(2),#matrix td:nth-child(2){position:static}
  #warns{padding:6px 0}
}
/* laid out for paper: a matrix column is as wide as its values, headed by the candidate id alone (what each id
   is, is printed under Candidate definitions) — so the table needs far less scaling to fit the sheet */
body.pr-on #matrix thead th span{display:none}
body.pr-on #matrix th,body.pr-on #matrix td{padding:2px 4px}
body.pr-on #matrix thead th{white-space:normal;overflow-wrap:anywhere}
""";

    private const string Script = """
(function(){
  const D = JSON.parse(document.getElementById('report-data').textContent);
  const sel = document.getElementById('group'), tip = document.getElementById('tip');
  const dashMap = {solid:'',dash:'8 5',dot:'2 4',dashdot:'10 4 2 4',longdash:'16 6',longdashdot:'16 5 3 5'};
  const dashValue = (lg,k) => (lg.find(e=>e.key===k)||{}).value || 'solid';
  const dashArr = (lg,k) => dashMap[dashValue(lg,k)] ?? '';
  const dashCss = (lg,k) => { const v=dashValue(lg,k); return v==='dot'?'dotted':(v==='solid'?'solid':'dashed'); };
  const legendColor = (lg,k) => (lg.find(e=>e.key===k)||{}).value || '#9aa0a6';
  const esc = t => String(t).replace(/[<&>"]/g, c=>({'<':'&lt;','&':'&amp;','>':'&gt;','"':'&quot;'}[c]));
  // point markers — used when the line dimension is the pretension candidate (dashes alone repeat past 6)
  const MSHAPES = ['circle','square','triangle','diamond','plus','ring'];
  function markerSvg(shape, x, y, r, fill, op, kd){
    const a = `fill="${fill}" opacity="${op}"${kd||''}`;
    switch(shape){
      case 'square':   return `<rect x="${(x-r).toFixed(1)}" y="${(y-r).toFixed(1)}" width="${(2*r).toFixed(1)}" height="${(2*r).toFixed(1)}" ${a}/>`;
      case 'triangle': return `<polygon points="${x.toFixed(1)},${(y-r*1.25).toFixed(1)} ${(x-r*1.1).toFixed(1)},${(y+r).toFixed(1)} ${(x+r*1.1).toFixed(1)},${(y+r).toFixed(1)}" ${a}/>`;
      case 'diamond':  return `<polygon points="${x.toFixed(1)},${(y-r*1.35).toFixed(1)} ${(x+r*1.35).toFixed(1)},${y.toFixed(1)} ${x.toFixed(1)},${(y+r*1.35).toFixed(1)} ${(x-r*1.35).toFixed(1)},${y.toFixed(1)}" ${a}/>`;
      case 'plus':     return `<path d="M${(x-r*1.4).toFixed(1)},${y.toFixed(1)} h${(2.8*r).toFixed(1)} M${x.toFixed(1)},${(y-r*1.4).toFixed(1)} v${(2.8*r).toFixed(1)}" stroke="${fill}" stroke-width="2.6" stroke-linecap="round" opacity="${op}"/>`;
      case 'ring':     return `<circle cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="${r.toFixed(1)}" fill="none" stroke="${fill}" stroke-width="2.6" opacity="${op}"/>`;
      default:         return `<circle cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="${r.toFixed(1)}" ${a}/>`;
    }
  }

  // Each chart panel is a self-contained view: editable name, its own L / H / P candidate filter,
  // colour/line relationship, load case and zoom. Duplicate clones the whole config; one chart by default.
  // `hidden` = per-candidate show/hide (shared), `hi` = matrix-highlighted candidate (shared).
  let gi = 0, showLegend = true, hidden = new Set(), hi = null;
  let printW = 0, printH = 0;      // > 0 while laid out for paper: the chart size on the sheet, not in the window
  const PRINT = window.reportPrint || null, OPT = PRINT ? PRINT.options : {};
  const panels = [];
  const tpl = document.getElementById('panelTpl'), panelsEl = document.getElementById('panels');
  const mw = document.getElementById('matrixWrap'), srcBtn = document.getElementById('srcBtn');

  D.groups.forEach((g,i)=>{ const o=document.createElement('option'); o.value=i;
    o.textContent=`${g.groupName}  ·  ${g.outputCase}  ·  ${g.series.length} candidate(s)`; sel.appendChild(o); });

  // study-wide candidate option lists (L / H / P) — identity is the same across groups / cases
  const allSeries = D.groups.flatMap(g=>g.series);
  const uniqOpts = (keyFn, labFn) => { const m=new Map(); allSeries.forEach(s=>{ const k=keyFn(s); if(k && !m.has(k)) m.set(k,{id:k,label:labFn(s)}); });
    return [...m.values()].sort((a,b)=>a.id.localeCompare(b.id, undefined, {numeric:true})); };
  // legend / filter rows carry only the candidate id — the "Candidate definitions" block below the
  // charts is the single place that spells out what each L## / H## / P## contains.
  const CAND = {
    L: uniqOpts(s=>s.loadId, s=>s.loadId),
    H: uniqOpts(s=>s.heightId, s=>s.heightId),
    P: uniqOpts(s=>s.pretensionId, s=>s.pretensionId),
  };
  // Pretension candidate is ALWAYS shown as a point marker (shape per P id) on every chart, whenever the
  // study sweeps more than one pretension candidate — independent of which relationship a chart plots.
  const P_IDS = CAND.P.map(o=>o.id);
  const P_ON = P_IDS.length > 1;
  const pShape = s => MSHAPES[Math.max(0, P_IDS.indexOf(s.pretensionId)) % MSHAPES.length];

  // Deflection limit band (Tab 04 Output Settings) on the peak downward deflection δ = −min(Uz) [mm].
  // A null bound is open. Same test as the Study Shortlist, so the count here matches its Pass/Fail.
  const BAND = D.limitBand && (D.limitBand.minMm!=null || D.limitBand.maxMm!=null) ? D.limitBand : null;
  let showBand = !!BAND;
  const bandOn = () => showBand && !!BAND;
  const fmtMm = v => (+v).toLocaleString('en-US',{maximumFractionDigits:3, useGrouping:false});
  const inBand = s => s.peakDownMm!=null && (BAND.minMm==null || s.peakDownMm>=BAND.minMm) && (BAND.maxMm==null || s.peakDownMm<=BAND.maxMm);
  const bandLabel = () => BAND.minMm!=null && BAND.maxMm!=null ? `${fmtMm(BAND.minMm)}–${fmtMm(BAND.maxMm)} mm`
    : BAND.maxMm!=null ? `≤ ${fmtMm(BAND.maxMm)} mm` : `≥ ${fmtMm(BAND.minMm)} mm`;
  const peakText = s => s.peakDownMm==null ? '—' : `${fmtMm(s.peakDownMm)} mm`;

  // one-off glossary of every candidate id -> its meaning (colour for L, dash for H, marker shape for P)
  // Candidate definitions block — mirrors the Tab 05 "Candidate Definitions" panel:
  // Loading = one column per load pattern; Truss height = ΔH + height columns; Pretension = per-cable list.
  function buildCandKey(){
    const body = document.getElementById('candKeyBody');
    const bySeries = id => allSeries.find(s => s.loadId===id || s.heightId===id || s.pretensionId===id);
    const dashOf = k => { const v = (D.views[0].dashLegend.find(e=>e.key===k)||{}).value; return v==='dot'?'dotted':(v==='solid'?'solid':'dashed'); };
    const colOf = s => (D.views[0].colorLegend.find(e=>e.key===s.colorKeys[0])||{}).value || '#9aa0a6';
    const kfmt = n => (+n).toLocaleString('en-US',{maximumFractionDigits:0});
    const h5 = (t,n) => `<h5>${t} <span class="cnt">${n}</span></h5>`;

    // ---- Loading candidates: a column per load pattern ----
    const LCOLS = [...new Set(allSeries.flatMap(s => (s.loadingComponents||[]).map(c=>c.pattern)))];
    let L;
    if(LCOLS.length){
      const head = `<tr><th></th><th></th>${LCOLS.map(p=>`<th>${esc(p)}</th>`).join('')}</tr>`;
      const rows = CAND.L.map(o=>{
        const s = bySeries(o.id);
        const map = {}; (s.loadingComponents||[]).forEach(c=>map[c.pattern]=c.levelKPa);
        const cells = LCOLS.map(p=> p in map ? `<td>${(+map[p]).toFixed(2)} kPa</td>` : `<td>—</td>`).join('');
        return `<tr><td class="csw"><span class="ksw" style="border-top-color:${colOf(s)}"></span></td><td class="kid">${o.id}</td>${cells}</tr>`;
      }).join('');
      L = `<div class="grp">${h5('Loading candidates', CAND.L.length)}<table class="kt">${head}${rows}</table></div>`;
    } else {
      L = `<div class="grp">${h5('Loading candidates', CAND.L.length)}` + CAND.L.map(o=>{
        const s = bySeries(o.id);
        return `<div class="kr"><span class="ksw" style="border-top-color:${colOf(s)}"></span><span class="kid">${o.id}</span><span class="kval">${esc(s.loadingLabel)}</span></div>`;
      }).join('') + `</div>`;
    }

    // ---- Truss-height candidates: ΔH baseline + truss height ----
    const Hhead = `<tr><th></th><th></th><th>ΔH baseline</th><th>Truss height</th></tr>`;
    const Hrows = CAND.H.map(o=>{
      const s = bySeries(o.id);
      const dh = s.deltaHM!=null ? `${s.deltaHM>=0?'+':''}${(+s.deltaHM).toFixed(3)} m` : '—';
      return `<tr><td class="csw"><span class="ksw" style="border-top-style:${dashOf(s.dashKeys[0])};border-top-color:#444"></span></td><td class="kid">${o.id}</td><td>${dh}</td><td>${(+s.trussHeightM).toFixed(2)} m</td></tr>`;
    }).join('');
    const H = `<div class="grp">${h5('Truss-height candidates', CAND.H.length)}<table class="kt">${Hhead}${Hrows}</table></div>`;

    // ---- Pretension candidates: per-P block, one row per cable ----
    let P = '';
    if(P_ON){
      P = `<div class="grp pgrp">${h5('Pretension candidates', CAND.P.length)}` + CAND.P.map(o=>{
        const shape = MSHAPES[P_IDS.indexOf(o.id) % MSHAPES.length];
        const s = bySeries(o.id);
        const cabs = s.pretensionCables || [];
        const note = cabs.length
          ? `${cabs.length} cable target${cabs.length===1?'':'s'}`
          : esc(s.pretensionKN!=null ? `${kfmt(s.pretensionKN)} kN` : (s.pretensionLabel||'—'));
        const hdr = `<div class="pgh"><svg class="kmk" width="22" height="18" viewBox="0 0 22 18">${markerSvg(shape,11,9,5.6,'#555',1)}</svg><span class="kid">${o.id}</span><span class="pgn">${note}</span></div>`;
        let list = '';
        if(cabs.length){
          const cell = c => c ? `<td>${esc(c.cable)}</td><td>${kfmt(c.forceKN)} kN</td>` : `<td></td><td></td>`;
          const rows = [];
          for(let i=0;i<cabs.length;i+=2) rows.push(`<tr>${cell(cabs[i])}<td class="pgap"></td>${cell(cabs[i+1])}</tr>`);
          list = `<table class="kt ptbl">${rows.join('')}</table>`;
        }
        return `<div class="pblk">${hdr}${list}</div>`;
      }).join('') + `</div>`;
    }
    body.innerHTML = L + H + P;
  }

  // one multi-select dropdown over `opts`; `set` is the backing Set (empty = all); onChange runs after edits.
  function buildMS(tag, opts, set, onChange){
    const wrap=document.createElement('span'); wrap.className='ms';
    const btn=document.createElement('button'); wrap.appendChild(btn);
    const pop=document.createElement('div'); pop.className='ms-pop'; pop.hidden=true; pop.onclick=e=>e.stopPropagation(); wrap.appendChild(pop);
    const allL=document.createElement('label'); allL.className='all';
    const allCb=document.createElement('input'); allCb.type='checkbox';
    allL.append(allCb, document.createTextNode(' All ' + tag));
    pop.appendChild(allL);
    const boxes=[];
    opts.forEach(o=>{ const l=document.createElement('label'); const cb=document.createElement('input'); cb.type='checkbox'; cb.value=o.id;
      l.append(cb, document.createTextNode(' ' + o.label)); pop.appendChild(l); boxes.push(cb);
      cb.onchange=()=>{ cb.checked?set.add(o.id):set.delete(o.id); if(set.size===opts.length) set.clear(); sync(); onChange(); };
    });
    allCb.onclick=e=>{ e.preventDefault(); set.clear(); sync(); onChange(); };
    function sync(){
      const n=set.size;
      btn.textContent = `${tag}: ` + (n===0 ? 'all' : n<=2 ? [...set].join(', ') : `${n}/${opts.length}`);
      wrap.classList.toggle('act', n>0);
      allCb.checked = n===0;
      boxes.forEach(cb=>cb.checked = n>0 && set.has(cb.value));
    }
    btn.onclick=e=>{ e.stopPropagation(); document.querySelectorAll('.ms-pop').forEach(p=>{ if(p!==pop) p.hidden=true; }); pop.hidden=!pop.hidden; };
    sync();
    return wrap;
  }
  document.addEventListener('click',()=>document.querySelectorAll('.ms-pop').forEach(p=>p.hidden=true));

  function ticks(a,b,n){ const step=nice((b-a)/n); const t0=Math.ceil(a/step)*step,o=[]; for(let v=t0;v<=b+1e-9;v+=step)o.push(+v.toFixed(10)); return o; }
  function nice(x){ const e=Math.pow(10,Math.floor(Math.log10(Math.abs(x)||1))); const f=x/e; return (f<1.5?1:f<3?2:f<7?5:10)*e; }
  function fmt(v){ return Math.abs(v)>=100?v.toFixed(0):Math.abs(v)>=1?v.toFixed(2):v.toFixed(3); }
  function gVisible(){ return D.groups[gi].series.filter(s=>!hidden.has(s.iterationKey)); }

  function makePanel(anchorAfter, init){
    // init: { vi, linked, gi, hidden(Set), name, selL[], selH[], selP[] }
    const host = tpl.content.firstElementChild.cloneNode(true);
    if(anchorAfter) anchorAfter.after(host); else panelsEl.appendChild(host);
    const chart = host.querySelector('.chart'), legendEl = host.querySelector('.legend');
    const dupBtn = host.querySelector('.pv-dup'), rmBtn = host.querySelector('.pv-rm'), ctlEl = host.querySelector('.pv-ctl');
    const nameEl = host.querySelector('.pv-name'), bandEl = host.querySelector('.pv-band');

    let vi = init.vi|0, linked = !!init.linked;
    let localGi = init.gi != null ? init.gi : gi;
    let localGroupSet = init.gi != null;   // a copy carries its own group/case; a linked panel follows the toolbar until its "case:" selector is touched
    let localHidden = new Set(init.hidden || hidden);
    let zoomX = null, api;
    const hiSel = new Set();   // multi-select highlight: "c:L01" / "d:H02" / "p:P01" — tick legend rows to add/remove
    // per-panel candidate filter + editable name
    const selL = new Set(init.selL||[]), selH = new Set(init.selH||[]), selP = new Set(init.selP||[]);
    let name = init.name || null, nameEdited = !!init.name;

    const curGi = () => (linked && !localGroupSet) ? gi : localGi;
    const curHidden = () => linked ? hidden : localHidden;
    const grp = () => D.groups[curGi()];
    const view = () => D.views[vi];
    const colorKey = s => s.colorKeys[vi], dashKey = s => s.dashKeys[vi];
    const keyForDim = (dim,s) => dim==='c' ? colorKey(s) : dim==='p' ? s.pretensionId : dashKey(s);
    const passes = s => (selL.size===0||selL.has(s.loadId)) && (selH.size===0||selH.has(s.heightId)) && (selP.size===0||selP.has(s.pretensionId));
    // in scope after this panel's L/H/P filter, then the per-candidate show/hide
    const scoped = () => grp().series.filter(passes);
    const visible = () => scoped().filter(s=>!curHidden().has(s.iterationKey));
    const emph = s => (hi && s.iterationKey===hi)
      || hiSel.has('c:'+colorKey(s)) || hiSel.has('d:'+dashKey(s)) || hiSel.has('p:'+s.pretensionId);
    const anyEmph = () => hi!=null || hiSel.size>0;
    const xExtent = () => { const xs=grp().series.flatMap(s=>s.points.filter(p=>p.uzMm!=null).map(p=>p.coord)); return xs.length?[Math.min(...xs),Math.max(...xs)]:[0,1]; };

    function afterMutate(){ render(); if(!mw.hidden) drawMatrix(); }
    // legend colour / line row click: highlight that whole group (dim the rest). Click again to clear.
    function toggleHiSel(dim,k){ const t=dim+':'+k; hiSel.has(t)?hiSel.delete(t):hiSel.add(t); hi=null; render(); if(!mw.hidden) drawMatrix(); }
    function toggleCand(k){ const H=curHidden(); H.has(k)?H.delete(k):H.add(k); afterMutate(); }
    function filtSummary(){ return [selL.size&&`${selL.size} L`, selH.size&&`${selH.size} H`, selP.size&&`${selP.size} P`].filter(Boolean).join(' · '); }

    function clamp(z){
      const [dx0,dx1]=xExtent(), span=(dx1-dx0)||1, minSpan=span*0.04;
      let a=z[0], b=z[1], w=b-a;
      if(!(w>0) || w>=span) return null;
      if(w<minSpan){ const c=(a+b)/2; a=c-minSpan/2; b=c+minSpan/2; }
      if(a<dx0){ b+=dx0-a; a=dx0; }
      if(b>dx1){ a-=b-dx1; b=dx1; }
      if(a<dx0) a=dx0;
      return [a,b];
    }

    const viewLabel = v => v.dashDimension && v.dashDimension !== v.colorDimension
      ? `${v.colorDimension} × ${v.dashDimension}` : v.colorDimension;

    const defaultName = () => `Deflection Uz — ${viewLabel(view())}`;
    nameEl.value = name || defaultName();
    nameEl.oninput = () => { name = nameEl.value; nameEdited = name !== defaultName(); render(); };

    function syncHeader(){
      host.classList.toggle('copy', !linked);
      if(!nameEdited){ name = defaultName(); nameEl.value = name; }
      host.querySelector('.pv-sub').textContent = `Uz under ${grp().outputCase}`;
      rmBtn.hidden = linked;
      ctlEl.innerHTML = '';
      // monitored-group selector: only when more than one monitored group exists.
      const groupNames = [...new Set(D.groups.map(g=>g.groupName))];
      if(groupNames.length > 1){
        const gs=document.createElement('select'); gs.title='Monitored point group';
        groupNames.forEach(n=>{ const o=new Option(n, n); if(n===grp().groupName) o.selected=true; gs.add(o); });
        gs.onchange=()=>{ const i=D.groups.findIndex(g=>g.groupName===gs.value && g.outputCase===grp().outputCase);
          localGi = i>=0 ? i : D.groups.findIndex(g=>g.groupName===gs.value); localGroupSet=true;
          hiSel.clear(); zoomX=null; syncHeader(); render(); };
        ctlEl.append(gs);
      }
      // load-case selector — pick which result load case this chart's Uz is read under.
      {
        const cs=document.createElement('select'); cs.title='Result load case for this chart';
        D.groups.forEach((g,i)=>{ if(g.groupName!==grp().groupName) return;
          const o=new Option(`case: ${g.outputCase}`, i); if(i===curGi()) o.selected=true; cs.add(o); });
        cs.onchange=()=>{ localGi=+cs.value; localGroupSet=true; hiSel.clear(); zoomX=null; syncHeader(); render(); };
        ctlEl.append(cs);
      }
      // relationship (colour × line) selector.
      if(D.views.length > 1){
        const vs=document.createElement('select'); vs.title='Which relationship this chart plots';
        D.views.forEach((v,i)=>{ const o=new Option(`view: ${viewLabel(v)}`, i); if(i===vi) o.selected=true; vs.add(o); });
        vs.onchange=()=>{ vi=+vs.value; hiSel.clear(); zoomX=null; syncHeader(); render(); };
        ctlEl.append(vs);
      }
      // per-chart candidate filter (L / H / P) — multi-select, empty = all.
      ctlEl.append(
        buildMS('L', CAND.L, selL, ()=>{ hiSel.clear(); render(); if(!mw.hidden) drawMatrix(); }),
        buildMS('H', CAND.H, selH, ()=>{ hiSel.clear(); render(); if(!mw.hidden) drawMatrix(); }),
        buildMS('P', CAND.P, selP, ()=>{ hiSel.clear(); render(); if(!mw.hidden) drawMatrix(); }));
    }

    function render(){
      const g = grp(), V = view(); if(!g){ chart.innerHTML=''; legendEl.innerHTML=''; return; }
      legendEl.hidden = !showLegend;
      chart.style.height = printW ? printH + 'px' : '';
      const W = printW || chart.clientWidth||900, H = printW ? printH : (chart.clientHeight||360), m={l:58,r:14,t:34,b:40};
      const all = g.series.flatMap(s=>s.points.filter(p=>p.uzMm!=null));
      const [dx0,dx1] = xExtent();
      let x0=dx0, x1=dx1;
      if(zoomX){ x0=zoomX[0]; x1=zoomX[1]; }
      if(x0<dx0) x0=dx0; if(x1>dx1) x1=dx1; if(x1-x0 < (dx1-dx0)*0.02){ x0=dx0; x1=dx1; }
      let ys = all.map(p=>p.uzMm);
      const band = bandOn();
      if(band){ if(BAND.minMm!=null) ys.push(-BAND.minMm); if(BAND.maxMm!=null) ys.push(-BAND.maxMm); }
      let y0 = Math.min(...ys,0), y1 = Math.max(...ys,0);
      if(x0===x1){x0-=1;x1+=1;} if(y0===y1){y0-=1;y1+=1;}
      const py = 0.06*(y1-y0); y0-=py; y1+=py;
      const sx = v => m.l + (v-x0)/(x1-x0)*(W-m.l-m.r);
      const sy = v => m.t + (y1-v)/(y1-y0)*(H-m.t-m.b);   // standard orientation: +Uz up
      const em = anyEmph();

      const P=[`<rect x="0" y="0" width="${W}" height="${H}" fill="#fff"/>`];
      const fs = filtSummary();
      P.push(`<text x="${(m.l+W-m.r)/2}" y="18" font-size="12" font-weight="600" fill="#1F4788" text-anchor="middle">${esc(name||defaultName())}${fs?`  ·  ${fs}`:''}</text>`);
      P.push(`<text x="${(m.l+W-m.r)/2}" y="30" font-size="9.5" fill="#666" text-anchor="middle">${g.groupName} · ${g.outputCase} · ${viewLabel(V)}</text>`);
      ticks(x0,x1,8).forEach(t=>{ const X=sx(t); P.push(`<line x1="${X}" y1="${m.t}" x2="${X}" y2="${H-m.b}" stroke="#eef0f2"/><text x="${X}" y="${H-m.b+16}" font-size="10" fill="#666" text-anchor="middle">${fmt(t)}</text>`); });
      ticks(y0,y1,6).forEach(t=>{ const Y=sy(t); P.push(`<line x1="${m.l}" y1="${Y}" x2="${W-m.r}" y2="${Y}" stroke="${t===0?'#c7ccd2':'#eef0f2'}"/><text x="${m.l-6}" y="${Y+3}" font-size="10" fill="#666" text-anchor="end">${fmt(t)}</text>`); });
      const bandLabels = [];
      if(band){
        // pass zone: a light wash between the limits (an open bound runs to the plot edge) + dashed limit lines.
        // +Uz is up, so the Min limit (smaller sag) is the upper line and Max the lower one.
        const yTop = BAND.minMm!=null ? sy(-BAND.minMm) : m.t, yBot = BAND.maxMm!=null ? sy(-BAND.maxMm) : H-m.b;
        P.push(`<rect x="${m.l}" y="${yTop.toFixed(1)}" width="${W-m.l-m.r}" height="${Math.max(0,yBot-yTop).toFixed(1)}" fill="#2E7D32" fill-opacity="0.09"/>`);
        const limit = (v, text, above) => { const Y = sy(-v);
          P.push(`<line x1="${m.l}" y1="${Y.toFixed(1)}" x2="${W-m.r}" y2="${Y.toFixed(1)}" stroke="#2E7D32" stroke-width="1.5" stroke-dasharray="7 4"/>`);
          bandLabels.push(`<text x="${W-m.r-4}" y="${(above ? Y-5 : Y+12).toFixed(1)}" font-size="10" font-weight="600" fill="#333" text-anchor="end" stroke="#fff" stroke-width="3" paint-order="stroke">${text}</text>`); };
        if(BAND.minMm!=null) limit(BAND.minMm, `limit min ${fmtMm(BAND.minMm)} mm`, true);
        if(BAND.maxMm!=null) limit(BAND.maxMm, `limit max ${fmtMm(BAND.maxMm)} mm`, false);
      }
      P.push(`<line x1="${m.l}" y1="${m.t}" x2="${m.l}" y2="${H-m.b}" stroke="#333"/><line x1="${m.l}" y1="${H-m.b}" x2="${W-m.r}" y2="${H-m.b}" stroke="#333"/>`);
      P.push(`<text x="${(m.l+W-m.r)/2}" y="${H-6}" font-size="11" fill="#333" text-anchor="middle">${g.profileAxis} coordinate [m]</text>`);
      P.push(`<text transform="translate(14,${(m.t+H-m.b)/2}) rotate(-90)" font-size="11" fill="#333" text-anchor="middle">Uz [mm]  (− = downward)</text>`);

      const lineSolid = V.dashMarkers;   // this view's line dim IS pretension -> solid line, marker carries P
      const vis = visible();
      vis.slice().sort((a,b)=>(emph(a)?1:0)-(emph(b)?1:0)).forEach(s=>{
        const vp = s.points.filter(p=>p.uzMm!=null);
        const pts = vp.map(p=>`${sx(p.coord).toFixed(1)},${sy(p.uzMm).toFixed(1)}`).join(' ');
        if(!pts) return;
        const c = legendColor(V.colorLegend, colorKey(s));
        const on = emph(s), op = (!em || on) ? 1 : 0.15, wd = on ? 3.4 : 1.8;
        P.push(`<polyline points="${pts}" fill="none" stroke="${c}" stroke-width="${wd}" stroke-dasharray="${lineSolid?'':dashArr(V.dashLegend, dashKey(s))}" opacity="${op}" data-k="${s.iterationKey}"/>`);
        vp.forEach(p=>{
          const X=+sx(p.coord).toFixed(1), Y=+sy(p.uzMm).toFixed(1);
          const kd = ` data-k="${s.iterationKey}" data-pt="${p.pointName}" data-x="${p.coord}" data-uz="${p.uzMm}"`;
          if(P_ON){ const r = on?7.5:5.6;
            P.push(`<circle cx="${X}" cy="${Y}" r="${r+2}" fill="transparent"${kd}/>`
              + markerSvg(pShape(s), X, Y, r, '#fff', op)          // white halo for contrast over the line
              + markerSvg(pShape(s), X, Y, r-1.4, c, op)); }
          else { P.push(`<circle cx="${X}" cy="${Y}" r="${on?3.4:2.4}" fill="${c}" opacity="${op}"${kd}/>`); }
        });
      });
      P.push(...bandLabels);
      chart.setAttribute('viewBox',`0 0 ${W} ${H}`);
      chart.innerHTML = P.join('');
      // pass count over the candidates this chart currently shows (L / H / P filter + show/hide ticks)
      bandEl.hidden = !band;
      if(band){
        bandEl.innerHTML = `<span class="ok">✓</span> <b>${vis.filter(inBand).length}</b> / ${vis.length} in limit band ${esc(bandLabel())}`;
        bandEl.title = 'Candidates shown in this chart whose peak downward Uz lies inside the limit band';
      }
      drawLegend();
    }

    function drawLegend(){
      const V = view(); const h=[]; const inScope = scoped();
      const showDash = V.dashDimension !== V.colorDimension && V.dashLegend.length > 1;
      // legend lists only values that survive the global L / H / P filter
      const present = keyOf => new Set(inScope.map(keyOf));
      const selMark = (dim,k) => hiSel.has(dim+':'+k) ? ' hi' : (hiSel.size ? ' off' : '');
      const cb = on => `<input type="checkbox" class="lcb"${on?' checked':''}>`;
      h.push(`<h4>${V.colorDimension} — colour <span class="hint2">tick = highlight (multi)</span></h4>`);
      const cp = present(colorKey);
      V.colorLegend.filter(e=>cp.has(e.key)).forEach(e=>{
        h.push(`<div class="row key${selMark('c',e.key)}" data-k="${e.key}">${cb(hiSel.has('c:'+e.key))}<span class="sw" style="border-top-color:${e.value}"></span>${e.label}</div>`); });
      // LINE section — only when the line dimension is NOT pretension (pretension is shown as a marker below)
      if(showDash && !V.dashMarkers){
        h.push(`<h4>${V.dashDimension} — line <span class="hint2">tick = highlight (multi)</span></h4>`);
        const dp = present(dashKey);
        V.dashLegend.filter(e=>dp.has(e.key)).forEach(e=>{
          h.push(`<div class="row dkey${selMark('d',e.key)}" data-k="${e.key}">${cb(hiSel.has('d:'+e.key))}<span class="dash" style="border-top-style:${e.value==='dot'?'dotted':(e.value==='solid'?'solid':'dashed')}"></span>${e.label}</div>`); });
      }
      // PRETENSION MARKER section — always present when the study sweeps more than one pretension candidate
      if(P_ON){
        const pPresent = new Set(inScope.map(s=>s.pretensionId));
        h.push(`<h4>Pretension candidate — marker <span class="hint2">tick = highlight (multi)</span></h4>`);
        CAND.P.filter(o=>pPresent.has(o.id)).forEach((o,i)=>{
          const shape = MSHAPES[P_IDS.indexOf(o.id) % MSHAPES.length];
          h.push(`<div class="row pkey${selMark('p',o.id)}" data-k="${o.id}">${cb(hiSel.has('p:'+o.id))}<svg class="mk" width="22" height="18" viewBox="0 0 22 18">${markerSvg(shape,11,9,5.6,"#555",1)}</svg>${o.label}</div>`); });
      }
      h.push(`<h4>Candidates <span class="hint2">${inScope.length}/${grp().series.length} in scope · tick = show</span></h4>`);
      inScope.forEach(s=>{ const shown=!curHidden().has(s.iterationKey), off=shown?'':' off', on=s.iterationKey===hi?' hi':'';
        const col = legendColor(V.colorLegend, colorKey(s));
        const swatch = P_ON
          ? `<svg class="mk" width="22" height="18" viewBox="0 0 22 18">${markerSvg(pShape(s),11,9,5.2,col,1)}</svg>`
          : `<span class="sw" style="border-top-color:${col};border-top-style:${dashCss(V.dashLegend,dashKey(s))}"></span>`;
        const mark = !bandOn() ? '' : inBand(s)
          ? `<span class="bd ok" title="peak ↓Uz ${peakText(s)} · inside limit band">✓</span>`
          : `<span class="bd no" title="peak ↓Uz ${peakText(s)} · outside limit band">✗</span>`;
        h.push(`<div class="row cand${off}${on}" data-k="${esc(s.iterationKey)}" title="${esc(s.iterationKey)} · ${esc(s.loadingLabel)} · H ${(+s.trussHeightM).toFixed(2)} m${s.pretensionKN!=null?' · '+(+s.pretensionKN).toFixed(0)+' kN':''}">${cb(shown)}${swatch}${esc(s.iterationKey)}${mark}</div>`); });
      legendEl.innerHTML = h.join('');
      const wire = (cls, fn) => legendEl.querySelectorAll('.row.'+cls).forEach(r=>{
        const act = () => fn(r.dataset.k);
        r.onclick = act;
        const box = r.querySelector('.lcb'); if(box) box.onclick = e => { e.stopPropagation(); act(); };
      });
      wire('key',  k => toggleHiSel('c', k));
      wire('dkey', k => toggleHiSel('d', k));
      wire('pkey', k => toggleHiSel('p', k));
      wire('cand', k => toggleCand(k));
    }

    chart.addEventListener('mousemove',ev=>{
      const t=ev.target;
      if(t.tagName==='circle'){
        const k=t.dataset.k, s=grp().series.find(x=>x.iterationKey===k);
        tip.hidden=false;
        tip.innerHTML = `${esc(k)}<br>point ${esc(t.dataset.pt)}<br>X ${(+t.dataset.x).toFixed(3)} m<br>Uz ${(+t.dataset.uz).toFixed(3)} mm<br>`
          + `${esc(s.loadingLabel)}<br>H ${(+s.trussHeightM).toFixed(3)} m (ΔH ${s.deltaHM>=0?'+':''}${(+s.deltaHM).toFixed(3)})<br>`
          + `${esc(s.pretensionLabel)}<br>case ${esc(grp().outputCase)}<br>peak ↓Uz ${peakText(s)}`
          + (bandOn() ? (inBand(s) ? ' · in limit band' : ' · outside limit band') : '');
        tip.style.left = Math.min(ev.clientX+12, window.innerWidth - tip.offsetWidth - 8) + 'px';
        tip.style.top  = Math.min(ev.clientY+12, window.innerHeight - tip.offsetHeight - 8) + 'px';
      } else tip.hidden=true;
    });
    chart.addEventListener('mouseleave',()=>tip.hidden=true);
    chart.addEventListener('wheel',ev=>{ ev.preventDefault();
      const [dx0,dx1]=xExtent(); let x0=zoomX?zoomX[0]:dx0, x1=zoomX?zoomX[1]:dx1;
      const r=chart.getBoundingClientRect(); const fx=(ev.clientX-r.left-58)/(r.width-72);
      const cx=x0+(x1-x0)*Math.max(0,Math.min(1,fx)); const f=ev.deltaY<0?0.85:1/0.85;
      zoomX=clamp([cx-(cx-x0)*f, cx+(x1-cx)*f]); render();
    },{passive:false});
    let drag=null;
    chart.addEventListener('mousedown',ev=>{ const [dx0,dx1]=xExtent();
      drag={x:ev.clientX, x0:zoomX?zoomX[0]:dx0, x1:zoomX?zoomX[1]:dx1, w:chart.clientWidth-72}; });
    window.addEventListener('mousemove',ev=>{ if(!drag)return; const d=(ev.clientX-drag.x)/drag.w*(drag.x1-drag.x0); zoomX=clamp([drag.x0-d,drag.x1-d])||zoomX; render(); });
    window.addEventListener('mouseup',()=>drag=null);
    chart.addEventListener('dblclick',()=>{ zoomX=null; render(); });

    dupBtn.onclick=()=>{
      const p = makePanel(host, { vi, linked:false, gi:curGi(), hidden:curHidden(),
        name:(name||defaultName())+' (copy)', selL:[...selL], selH:[...selH], selP:[...selP] });
      panels.push(p); p.render();
    };
    rmBtn.onclick=()=>{ const ix=panels.indexOf(api); if(ix>=0) panels.splice(ix,1); host.remove(); };

    syncHeader();
    api = { render, reset(){ zoomX=null; }, resync(){ syncHeader(); },
            followGlobal(){ if(linked){ localGroupSet=false; localGi=gi; syncHeader(); } } };
    return api;
  }

  function drawMatrix(){
    const vis = gVisible();
    const rowsMap = new Map();
    vis.forEach(s=>s.points.forEach(p=>{ const key=p.pointName+'|'+p.coord; if(!rowsMap.has(key)) rowsMap.set(key,{pt:p.pointName,x:p.coord,vals:{}}); }));
    vis.forEach(s=>s.points.forEach(p=>{ const r=rowsMap.get(p.pointName+'|'+p.coord); if(r) r.vals[s.iterationKey]=p.uzMm; }));
    const rows=[...rowsMap.values()].sort((a,b)=>a.x-b.x);
    const th = vis.map(s=>`<th class="ch${s.iterationKey===hi?' hi':''}" data-k="${esc(s.iterationKey)}" title="click to highlight this candidate in every chart">${esc(s.iterationKey)}<br><span style="font-weight:400;color:#666">${esc(s.loadingLabel)} · H${(+s.trussHeightM).toFixed(2)}${s.pretensionKN!=null?' · '+(+s.pretensionKN).toFixed(0)+'kN':''}</span></th>`).join('');
    const body = rows.map(r=>`<tr><th class="k">${esc(r.pt)}</th><td>${fmt(r.x)}</td>${vis.map(s=>{const v=r.vals[s.iterationKey];return `<td${s.iterationKey===hi?' class="hi"':''}>${v==null?'—':(+v).toFixed(3)}</td>`;}).join('')}</tr>`).join('');
    const foot = `<tr><th class="k">Peak ↓Uz</th><td>[mm]</td>${vis.map(s=>`<td${s.iterationKey===hi?' class="hi"':''}>${s.peakDownMm==null?'—':(+s.peakDownMm).toFixed(3)}${bandOn()?(inBand(s)?' ✓':' ✗'):''}</td>`).join('')}</tr>`;
    document.getElementById('matrix').innerHTML = `<table><thead><tr><th class="k">Point</th><th>X [m]</th>${th}</tr></thead><tbody>${body}</tbody><tfoot>${foot}</tfoot></table>`;
    document.querySelectorAll('#matrix thead th.ch').forEach(el=>el.onclick=()=>{ hi = (hi===el.dataset.k? null : el.dataset.k); renderAll(); });
  }

  function drawWarnings(){
    const w = document.getElementById('warns');
    if(!D.warnings.length){ w.textContent=''; return; }
    w.innerHTML = '<strong>Warnings:</strong> ' + D.warnings.slice(0,40).map(x=>`<span class="${x.severity==='Error'?'e':''}">[${esc(x.iterationKey||'study')}] ${esc(x.category)}: ${esc(x.message)}</span>`).join(' · ')
      + (D.warnings.length>40?` … (+${D.warnings.length-40})`:'');
  }

  function renderAll(){ panels.forEach(p=>p.render()); if(!mw.hidden) drawMatrix(); drawWarnings(); }

  // "#print&group=1" from the PDF exporter chooses the monitored group / result case to print
  if(OPT.group != null && D.groups[+OPT.group]){ gi = +OPT.group; sel.value = gi; }
  if(!D.groups.length){
    panelsEl.innerHTML='<p style="padding:20px;color:#666">No deflection results in this study.</p>';
    srcBtn.hidden = true;
  } else {
    buildCandKey();
    // one chart by default — name it, filter it (L / H / P), pick its relationship & case; Duplicate clones it.
    panels.push(makePanel(null, { vi:0, linked:true }));
  }

  sel.onchange=()=>{ gi=+sel.value; hi=null; panels.forEach(p=>{ p.followGlobal(); p.reset(); }); renderAll(); };
  document.getElementById('legendToggle').onchange=e=>{ showLegend=e.target.checked; renderAll(); };
  document.getElementById('bandToggleWrap').hidden = !BAND;
  document.getElementById('bandToggle').onchange=e=>{ showBand=e.target.checked; renderAll(); };
  document.getElementById('reset').onclick=()=>{ panels.forEach(p=>p.reset()); renderAll(); };
  function toggleSrc(show){ mw.hidden=!show; srcBtn.textContent = show?'Hide source data':'View source data'; if(show) drawMatrix(); }
  srcBtn.onclick=()=>toggleSrc(mw.hidden);
  document.getElementById('mClose').onclick=()=>toggleSrc(false);
  window.addEventListener('resize',()=>renderAll());
  // paper: every chart is redrawn against the sheet — full width, and short enough that the title block, the chart
  // and its legend share one landscape page — and the Source Data Matrix follows the print option
  if(PRINT){ let opened = false; PRINT.register({
    before: pg => { printW = Math.max(400, pg.contentPx); printH = Math.max(280, Math.min(430, pg.contentHeightPx - 320));
      opened = PRINT.state.includeSource && mw.hidden && D.groups.length > 0; if(opened) toggleSrc(true); renderAll(); },
    after: () => { printW = 0; if(opened) toggleSrc(false); opened = false; renderAll(); } }); }
  renderAll();
})();
""";
}
