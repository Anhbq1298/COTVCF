using System.IO;
using System.Text.Json;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;

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
        string baselineName, string baselinePath, IterationStudyMode studyMode)
    {
        Directory.CreateDirectory(studyFolder);
        var dto = Build(results, studyFolder, baselineName, baselinePath, studyMode);
        var path = Path.Combine(studyFolder, ReportFileName);
        File.WriteAllText(path, BuildHtml(dto), new System.Text.UTF8Encoding(false));
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
        string studyFolder, string baselineName, string baselinePath, IterationStudyMode studyMode)
    {
        // One report group per (monitored point group × result load case) — so a study that extracts Uz
        // under several cases yields one selectable chart source per case.
        var groupKeys = results.SelectMany(r => r.Groups)
            .Select(g => (g.GroupName, g.OutputCase))
            .Distinct().OrderBy(k => k.GroupName, StringComparer.Ordinal).ThenBy(k => k.OutputCase, StringComparer.Ordinal).ToArray();

        var flat = results.Where(r => r.Groups.Any(g => g.Points.Any(p => p.Status == ResultValueStatus.Valid)))
            .OrderBy(r => r.Parameters.RunIndex).Select(r => r.Parameters).ToArray();
        Dim heightDim = new("Truss height",
            p => p.TrussHeightM.ToString("0.####"),
            k => $"H = {double.Parse(k):0.###} m");
        // The "load candidate" is the loading scenario itself — key off its label so two loadings with
        // the same nominal kPa but different make-up still separate. Falls back to the single-load level.
        Dim loadDim = new("Load candidate",
            p => !string.IsNullOrWhiteSpace(p.LoadingLabel) ? p.LoadingLabel!.Trim() : Num(p.LoadLevelKPa),
            k => k == "" ? "n/a" : double.TryParse(k, out var v) ? $"{v:0.###} kPa" : k);
        Dim preDim = new("Initial pretension",
            p => Num(p.PretensionKN),
            k => k == "" ? "none" : $"{double.Parse(k):0.###} kN");

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
                    g.Points.OrderBy(p => p.Sequence).Select(p => new DeflectionPointDto(
                        p.PointName, Axis(p, g.ProfileAxis), p.UzMm, p.Sequence, p.Status.ToString())).ToArray()));
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

        return new DeflectionReportDto(study, groups, views, warnings);
    }

    private sealed record Dim(string Name, Func<CandidateParametersDto, string> Key, Func<string, string> Label);

    private static double Axis(MonitoredPointResultDto p, ProfileCoordinateAxis a) =>
        a switch { ProfileCoordinateAxis.Y => p.Y, ProfileCoordinateAxis.Z => p.Z, _ => p.X };

    private static string Num(double? v) => v is { } d ? d.ToString("0.####") : "";
    private static double SortKey(string k) => k == "" ? double.MaxValue : double.TryParse(k, out var v) ? v : double.MaxValue;

    // ---------- HTML ----------

    public string BuildHtml(DeflectionReportDto dto)
    {
        var json = JsonSerializer.Serialize(dto, Json);
        var s = dto.Study;
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8">
<title>Deflection Report — {{Esc(s.BaselineModelName)}}</title>
<style>{{Style}}</style></head><body>
<header>
  <div>
    <h1>Deflection Results Viewer</h1>
    <p class="meta">{{Esc(s.BaselineModelName)}} · mode {{Esc(s.StudyMode)}} · {{s.CandidateCount}} candidate(s) ·
      U<sub>z</sub> [mm] vs coordinate [m] · generated {{Esc(s.GeneratedAtIso[..19].Replace('T', ' '))}}</p>
  </div>
  <details class="lib"><summary>Result Library</summary>
    <ul>
      <li class="on">01 · Deflection · U<sub>z</sub> <span>active</span></li>
      <li>02 · Top Chords — member forces <span>future</span></li>
      <li>03 · Bottom Chords — member forces <span>future</span></li>
      <li>04 · Vierendeel Members — member forces <span>future</span></li>
      <li>05 · Cable Forces <span>future</span></li>
    </ul>
  </details>
</header>
<div id="toolbar">
  <label>Group <select id="group"></select></label>
  <span class="sep"></span>
  <label><input type="checkbox" id="legendToggle" checked> Plot legend</label>
  <button id="reset">Reset zoom</button>
  <button id="srcBtn">View source data</button>
  <span class="sep"></span>
  <span class="hint">each chart has its own name + L / H / P filter · click a legend row = highlight · wheel = zoom X · drag = pan · dbl-click = reset · Duplicate clones the chart</span>
</div>
<div id="panels"></div>
<template id="panelTpl">
  <section class="panel">
    <div class="pbar">
      <input class="pv-name" title="Chart name — click to edit" spellcheck="false">
      <span class="pv-sub"></span>
      <span class="pv-ctl"></span>
      <button class="pv-dup" title="Duplicate this chart with all its current settings">＋ Duplicate</button>
      <button class="pv-rm" title="Remove this chart" hidden>✕ Remove</button>
    </div>
    <div class="stage"><svg class="chart" preserveAspectRatio="none"></svg><div class="legend"></div></div>
  </section>
</template>
<div id="tip" hidden></div>
<div id="matrixWrap" hidden>
  <div class="mhdr"><strong>Source Data Matrix</strong><button id="mClose">Close</button></div>
  <div id="matrix"></div>
</div>
<div id="warns"></div>
<script type="application/json" id="report-data">{{json}}</script>
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
.legend .row.key:hover,.legend .row.dkey:hover,.legend .row.fkey:hover{background:#f0f3f7;border-radius:2px}
.legend .fdot{width:8px;height:8px;border-radius:50%;background:#9aa0a6;flex:none;margin:0 8px 0 8px}
.legend .row.off{opacity:.35}
.legend .row.hi{background:#e8f0fe;border-radius:2px}
.legend .sw{width:24px;height:0;border-top-width:3px;border-top-style:solid;flex:none}
.legend .dash{width:26px;height:0;border-top:2px solid #444;flex:none}
.legend .mk{flex:none;overflow:visible}
#tip{position:fixed;pointer-events:none;background:#fffffff2;border:1px solid var(--acc);border-radius:3px;padding:6px 8px;font:11px/1.4 Consolas,monospace;z-index:20;max-width:280px}
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
#legend .row.hi{background:#e8f0fe;border-radius:2px}
#warns{padding:6px 14px;font-size:11px;color:#8a6d00}
#warns .e{color:#C62828}
a{color:var(--acc)}
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
  const esc = t => String(t).replace(/[<&>]/g, c=>({'<':'&lt;','&':'&amp;','>':'&gt;'}[c]));
  // point markers — used when the line dimension is the pretension candidate (dashes alone repeat past 6)
  const MSHAPES = ['circle','square','triangle','diamond','plus','ring'];
  function markerSvg(shape, x, y, r, fill, op, kd){
    const a = `fill="${fill}" opacity="${op}"${kd||''}`;
    switch(shape){
      case 'square':   return `<rect x="${(x-r).toFixed(1)}" y="${(y-r).toFixed(1)}" width="${(2*r).toFixed(1)}" height="${(2*r).toFixed(1)}" ${a}/>`;
      case 'triangle': return `<polygon points="${x.toFixed(1)},${(y-r*1.25).toFixed(1)} ${(x-r*1.1).toFixed(1)},${(y+r).toFixed(1)} ${(x+r*1.1).toFixed(1)},${(y+r).toFixed(1)}" ${a}/>`;
      case 'diamond':  return `<polygon points="${x.toFixed(1)},${(y-r*1.35).toFixed(1)} ${(x+r*1.35).toFixed(1)},${y.toFixed(1)} ${x.toFixed(1)},${(y+r*1.35).toFixed(1)} ${(x-r*1.35).toFixed(1)},${y.toFixed(1)}" ${a}/>`;
      case 'plus':     return `<path d="M${(x-r*1.4).toFixed(1)},${y.toFixed(1)} h${(2.8*r).toFixed(1)} M${x.toFixed(1)},${(y-r*1.4).toFixed(1)} v${(2.8*r).toFixed(1)}" stroke="${fill}" stroke-width="1.9" opacity="${op}"/>`;
      case 'ring':     return `<circle cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="${r.toFixed(1)}" fill="none" stroke="${fill}" stroke-width="1.9" opacity="${op}"/>`;
      default:         return `<circle cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="${r.toFixed(1)}" ${a}/>`;
    }
  }

  // Each chart panel is a self-contained view: editable name, its own L / H / P candidate filter,
  // colour/line relationship, load case and zoom. Duplicate clones the whole config; one chart by default.
  // `hidden` = per-candidate show/hide (shared), `hi` = matrix-highlighted candidate (shared).
  let gi = 0, showLegend = true, hidden = new Set(), hi = null;
  const panels = [];
  const tpl = document.getElementById('panelTpl'), panelsEl = document.getElementById('panels');
  const mw = document.getElementById('matrixWrap'), srcBtn = document.getElementById('srcBtn');

  D.groups.forEach((g,i)=>{ const o=document.createElement('option'); o.value=i;
    o.textContent=`${g.groupName}  ·  ${g.outputCase}  ·  ${g.series.length} candidate(s)`; sel.appendChild(o); });

  // study-wide candidate option lists (L / H / P) — identity is the same across groups / cases
  const allSeries = D.groups.flatMap(g=>g.series);
  const uniqOpts = (keyFn, labFn) => { const m=new Map(); allSeries.forEach(s=>{ const k=keyFn(s); if(k && !m.has(k)) m.set(k,{id:k,label:labFn(s)}); });
    return [...m.values()].sort((a,b)=>a.id.localeCompare(b.id, undefined, {numeric:true})); };
  const CAND = {
    L: uniqOpts(s=>s.loadId, s=>`${s.loadId} · ${s.loadingLabel}`),
    H: uniqOpts(s=>s.heightId, s=>`${s.heightId} · ${(+s.trussHeightM).toFixed(2)} m`),
    P: uniqOpts(s=>s.pretensionId, s=>`${s.pretensionId} · ${s.pretensionKN!=null?(+s.pretensionKN).toFixed(0)+' kN':s.pretensionLabel}`),
  };

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
    const nameEl = host.querySelector('.pv-name');

    let vi = init.vi|0, linked = !!init.linked;
    let localGi = init.gi != null ? init.gi : gi;
    let localGroupSet = init.gi != null;   // a copy carries its own group/case; a linked panel follows the toolbar until its "case:" selector is touched
    let localHidden = new Set(init.hidden || hidden);
    let zoomX = null, hiSel = null, api;
    // per-panel candidate filter + editable name
    const selL = new Set(init.selL||[]), selH = new Set(init.selH||[]), selP = new Set(init.selP||[]);
    let name = init.name || null, nameEdited = !!init.name;

    const curGi = () => (linked && !localGroupSet) ? gi : localGi;
    const curHidden = () => linked ? hidden : localHidden;
    const grp = () => D.groups[curGi()];
    const view = () => D.views[vi];
    const colorKey = s => s.colorKeys[vi], dashKey = s => s.dashKeys[vi];
    const keyForDim = (dim,s) => dim==='c' ? colorKey(s) : dashKey(s);
    const passes = s => (selL.size===0||selL.has(s.loadId)) && (selH.size===0||selH.has(s.heightId)) && (selP.size===0||selP.has(s.pretensionId));
    // in scope after this panel's L/H/P filter, then the per-candidate show/hide
    const scoped = () => grp().series.filter(passes);
    const visible = () => scoped().filter(s=>!curHidden().has(s.iterationKey));
    const emph = s => (hi && s.iterationKey===hi) || (hiSel && keyForDim(hiSel.dim,s)===hiSel.key);
    const anyEmph = () => hi!=null || hiSel!=null;
    const xExtent = () => { const xs=grp().series.flatMap(s=>s.points.filter(p=>p.uzMm!=null).map(p=>p.coord)); return xs.length?[Math.min(...xs),Math.max(...xs)]:[0,1]; };

    function afterMutate(){ render(); if(!mw.hidden) drawMatrix(); }
    // legend colour / line row click: highlight that whole group (dim the rest). Click again to clear.
    function toggleHiSel(dim,k){ hiSel = (hiSel && hiSel.dim===dim && hiSel.key===k) ? null : {dim,key:k}; hi=null; render(); }
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
          hiSel=null; zoomX=null; syncHeader(); render(); };
        ctlEl.append(gs);
      }
      // load-case selector — pick which result load case this chart's Uz is read under.
      {
        const cs=document.createElement('select'); cs.title='Result load case for this chart';
        D.groups.forEach((g,i)=>{ if(g.groupName!==grp().groupName) return;
          const o=new Option(`case: ${g.outputCase}`, i); if(i===curGi()) o.selected=true; cs.add(o); });
        cs.onchange=()=>{ localGi=+cs.value; localGroupSet=true; hiSel=null; zoomX=null; syncHeader(); render(); };
        ctlEl.append(cs);
      }
      // relationship (colour × line) selector.
      if(D.views.length > 1){
        const vs=document.createElement('select'); vs.title='Which relationship this chart plots';
        D.views.forEach((v,i)=>{ const o=new Option(`view: ${viewLabel(v)}`, i); if(i===vi) o.selected=true; vs.add(o); });
        vs.onchange=()=>{ vi=+vs.value; hiSel=null; zoomX=null; syncHeader(); render(); };
        ctlEl.append(vs);
      }
      // per-chart candidate filter (L / H / P) — multi-select, empty = all.
      ctlEl.append(
        buildMS('L', CAND.L, selL, ()=>{ hiSel=null; render(); if(!mw.hidden) drawMatrix(); }),
        buildMS('H', CAND.H, selH, ()=>{ hiSel=null; render(); if(!mw.hidden) drawMatrix(); }),
        buildMS('P', CAND.P, selP, ()=>{ hiSel=null; render(); if(!mw.hidden) drawMatrix(); }));
    }

    function render(){
      const g = grp(), V = view(); if(!g){ chart.innerHTML=''; legendEl.innerHTML=''; return; }
      legendEl.hidden = !showLegend;
      const W = chart.clientWidth||900, H = chart.clientHeight||360, m={l:58,r:14,t:34,b:40};
      const all = g.series.flatMap(s=>s.points.filter(p=>p.uzMm!=null));
      const [dx0,dx1] = xExtent();
      let x0=dx0, x1=dx1;
      if(zoomX){ x0=zoomX[0]; x1=zoomX[1]; }
      if(x0<dx0) x0=dx0; if(x1>dx1) x1=dx1; if(x1-x0 < (dx1-dx0)*0.02){ x0=dx0; x1=dx1; }
      let ys = all.map(p=>p.uzMm);
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
      P.push(`<line x1="${m.l}" y1="${m.t}" x2="${m.l}" y2="${H-m.b}" stroke="#333"/><line x1="${m.l}" y1="${H-m.b}" x2="${W-m.r}" y2="${H-m.b}" stroke="#333"/>`);
      P.push(`<text x="${(m.l+W-m.r)/2}" y="${H-6}" font-size="11" fill="#333" text-anchor="middle">${g.profileAxis} coordinate [m]</text>`);
      P.push(`<text transform="translate(14,${(m.t+H-m.b)/2}) rotate(-90)" font-size="11" fill="#333" text-anchor="middle">Uz [mm]  (− = downward)</text>`);

      const mk = V.dashMarkers;
      visible().slice().sort((a,b)=>(emph(a)?1:0)-(emph(b)?1:0)).forEach(s=>{
        const vp = s.points.filter(p=>p.uzMm!=null);
        const pts = vp.map(p=>`${sx(p.coord).toFixed(1)},${sy(p.uzMm).toFixed(1)}`).join(' ');
        if(!pts) return;
        const c = legendColor(V.colorLegend, colorKey(s));
        const on = emph(s), op = (!em || on) ? 1 : 0.15, wd = on ? 3.4 : 1.8;
        const shape = mk ? MSHAPES[Math.max(0, V.dashLegend.findIndex(e=>e.key===dashKey(s))) % MSHAPES.length] : 'circle';
        // when markers carry the pretension candidate, draw the line solid (marker shape is the P identifier)
        P.push(`<polyline points="${pts}" fill="none" stroke="${c}" stroke-width="${wd}" stroke-dasharray="${mk?'':dashArr(V.dashLegend, dashKey(s))}" opacity="${op}" data-k="${s.iterationKey}"/>`);
        vp.forEach(p=>{
          const X=+sx(p.coord).toFixed(1), Y=+sy(p.uzMm).toFixed(1);
          const kd = ` data-k="${s.iterationKey}" data-pt="${p.pointName}" data-x="${p.coord}" data-uz="${p.uzMm}"`;
          if(mk){ P.push(`<circle cx="${X}" cy="${Y}" r="6" fill="transparent"${kd}/>` + markerSvg(shape, X, Y, on?4.4:3.3, c, op)); }
          else { P.push(`<circle cx="${X}" cy="${Y}" r="${on?3.4:2.4}" fill="${c}" opacity="${op}"${kd}/>`); }
        });
      });
      chart.setAttribute('viewBox',`0 0 ${W} ${H}`);
      chart.innerHTML = P.join('');
      drawLegend();
    }

    function drawLegend(){
      const V = view(); const h=[]; const inScope = scoped();
      const showDash = V.dashDimension !== V.colorDimension && V.dashLegend.length > 1;
      // legend lists only values that survive the global L / H / P filter
      const present = keyOf => new Set(inScope.map(keyOf));
      const selMark = (dim,k) => (hiSel && hiSel.dim===dim && hiSel.key===k) ? ' hi' : (hiSel && ' off' || '');
      h.push(`<h4>${V.colorDimension} — colour <span class="hint2">click = highlight</span></h4>`);
      const cp = present(colorKey);
      V.colorLegend.filter(e=>cp.has(e.key)).forEach(e=>{
        h.push(`<div class="row key${selMark('c',e.key)}" data-k="${e.key}"><span class="sw" style="border-top-color:${e.value}"></span>${e.label}</div>`); });
      if(showDash){
        const mk = V.dashMarkers;
        h.push(`<h4>${V.dashDimension} — ${mk?'marker':'line'} <span class="hint2">click = highlight</span></h4>`);
        const dp = present(dashKey);
        V.dashLegend.filter(e=>dp.has(e.key)).forEach(e=>{
          const di = V.dashLegend.findIndex(x=>x.key===e.key);
          const swatch = mk
            ? `<svg class="mk" width="18" height="16" viewBox="0 0 18 16">${markerSvg(MSHAPES[di%MSHAPES.length],9,8,4.6,'#555',1)}</svg>`
            : `<span class="dash" style="border-top-style:${e.value==='dot'?'dotted':(e.value==='solid'?'solid':'dashed')}"></span>`;
          h.push(`<div class="row dkey${selMark('d',e.key)}" data-k="${e.key}">${swatch}${e.label}</div>`); });
      }
      h.push(`<h4>Candidates <span class="hint2">${inScope.length}/${grp().series.length} in scope · click = show/hide</span></h4>`);
      inScope.forEach(s=>{ const off=curHidden().has(s.iterationKey)?' off':'', on=s.iterationKey===hi?' hi':'';
        const col = legendColor(V.colorLegend, colorKey(s));
        const swatch = V.dashMarkers
          ? `<svg class="mk" width="18" height="16" viewBox="0 0 18 16">${markerSvg(MSHAPES[Math.max(0,V.dashLegend.findIndex(e=>e.key===dashKey(s)))%MSHAPES.length],9,8,4.2,col,1)}</svg>`
          : `<span class="sw" style="border-top-color:${col};border-top-style:${dashCss(V.dashLegend,dashKey(s))}"></span>`;
        h.push(`<div class="row cand${off}${on}" data-k="${s.iterationKey}">${swatch}${s.iterationKey} · ${s.loadingLabel} · H ${(+s.trussHeightM).toFixed(2)}m${s.pretensionKN!=null?' · '+(+s.pretensionKN).toFixed(0)+' kN':''}</div>`); });
      legendEl.innerHTML = h.join('');
      legendEl.querySelectorAll('.row.key').forEach(r=>r.onclick=()=>toggleHiSel('c', r.dataset.k));
      legendEl.querySelectorAll('.row.dkey').forEach(r=>r.onclick=()=>toggleHiSel('d', r.dataset.k));
      legendEl.querySelectorAll('.row.cand').forEach(r=>r.onclick=()=>toggleCand(r.dataset.k));
    }

    chart.addEventListener('mousemove',ev=>{
      const t=ev.target;
      if(t.tagName==='circle'){
        const k=t.dataset.k, s=grp().series.find(x=>x.iterationKey===k);
        tip.hidden=false;
        tip.innerHTML = `${k}<br>point ${t.dataset.pt}<br>X ${(+t.dataset.x).toFixed(3)} m<br>Uz ${(+t.dataset.uz).toFixed(3)} mm<br>`
          + `${s.loadingLabel}<br>H ${(+s.trussHeightM).toFixed(3)} m (ΔH ${s.deltaHM>=0?'+':''}${(+s.deltaHM).toFixed(3)})<br>`
          + `${s.pretensionLabel}<br>case ${grp().outputCase}`;
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
    const th = vis.map(s=>`<th class="ch${s.iterationKey===hi?' hi':''}" data-k="${s.iterationKey}" title="click to highlight this candidate in every chart">${s.iterationKey}<br><span style="font-weight:400;color:#666">${s.loadingLabel} · H${(+s.trussHeightM).toFixed(2)}${s.pretensionKN!=null?' · '+(+s.pretensionKN).toFixed(0)+'kN':''}</span></th>`).join('');
    const body = rows.map(r=>`<tr><th class="k">${r.pt}</th><td>${fmt(r.x)}</td>${vis.map(s=>{const v=r.vals[s.iterationKey];return `<td${s.iterationKey===hi?' class="hi"':''}>${v==null?'—':(+v).toFixed(3)}</td>`;}).join('')}</tr>`).join('');
    document.getElementById('matrix').innerHTML = `<table><thead><tr><th class="k">Point</th><th>X [m]</th>${th}</tr></thead><tbody>${body}</tbody></table>`;
    document.querySelectorAll('#matrix thead th.ch').forEach(el=>el.onclick=()=>{ hi = (hi===el.dataset.k? null : el.dataset.k); renderAll(); });
  }

  function drawWarnings(){
    const w = document.getElementById('warns');
    if(!D.warnings.length){ w.textContent=''; return; }
    w.innerHTML = '<strong>Warnings:</strong> ' + D.warnings.slice(0,40).map(x=>`<span class="${x.severity==='Error'?'e':''}">[${x.iterationKey||'study'}] ${x.category}: ${x.message}</span>`).join(' · ')
      + (D.warnings.length>40?` … (+${D.warnings.length-40})`:'');
  }

  function renderAll(){ panels.forEach(p=>p.render()); if(!mw.hidden) drawMatrix(); drawWarnings(); }

  if(!D.groups.length){
    panelsEl.innerHTML='<p style="padding:20px;color:#666">No deflection results in this study.</p>';
    srcBtn.hidden = true;
  } else {
    // one chart by default — name it, filter it (L / H / P), pick its relationship & case; Duplicate clones it.
    panels.push(makePanel(null, { vi:0, linked:true }));
  }

  sel.onchange=()=>{ gi=+sel.value; hi=null; panels.forEach(p=>{ p.followGlobal(); p.reset(); }); renderAll(); };
  document.getElementById('legendToggle').onchange=e=>{ showLegend=e.target.checked; renderAll(); };
  document.getElementById('reset').onclick=()=>{ panels.forEach(p=>p.reset()); renderAll(); };
  function toggleSrc(show){ mw.hidden=!show; srcBtn.textContent = show?'Hide source data':'View source data'; if(show) drawMatrix(); }
  srcBtn.onclick=()=>toggleSrc(mw.hidden);
  document.getElementById('mClose').onclick=()=>toggleSrc(false);
  window.addEventListener('resize',()=>renderAll());
  renderAll();
})();
""";
}
