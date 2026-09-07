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
        var groupNames = results.SelectMany(r => r.Groups).Select(g => g.GroupName).Distinct(StringComparer.Ordinal).ToArray();

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

        // View 1 colour = truss height (one colour per height). Line style (shared by both views)
        // separates the candidates that share a height: the load candidate if it varies, else pretension.
        var colorDim = heightDim;
        var dashDim = new[] { loadDim, preDim }
            .FirstOrDefault(d => flat.Select(d.Key).Distinct().Count() > 1) ?? loadDim;
        // View 2 colour = initial pretension force (same profile chart, pivoted encoding).
        var color2Dim = preDim;

        List<LegendEntryDto> LegendFor(Dim d) => flat.Select(d.Key).Distinct()
            .OrderBy(SortKey).ThenBy(k => k, StringComparer.Ordinal)
            .Select((k, i) => new LegendEntryDto(k, d.Label(k), k == "" ? "#9aa0a6" : Palette[i % Palette.Length]))
            .ToList();

        var colorLegend = LegendFor(colorDim);
        var color2Legend = LegendFor(color2Dim);
        var dashKeys = flat.Select(dashDim.Key).Distinct()
            .OrderBy(SortKey).ThenBy(k => k, StringComparer.Ordinal).ToArray();
        var dashLegend = dashKeys.Select((k, i) =>
            new LegendEntryDto(k, dashDim.Label(k), Dashes[i % Dashes.Length])).ToList();

        var groups = new List<DeflectionGroupReportDto>();
        foreach (var gn in groupNames)
        {
            var series = new List<DeflectionCandidateSeriesDto>();
            string outputCase = "", axis = "X";
            foreach (var r in results.OrderBy(r => r.Parameters.RunIndex))
            {
                var g = r.Groups.FirstOrDefault(x => x.GroupName == gn);
                if (g is null || g.Points.All(p => p.Status != ResultValueStatus.Valid)) continue; // only candidates with data
                outputCase = g.OutputCase; axis = g.ProfileAxis.ToString();
                series.Add(new DeflectionCandidateSeriesDto(
                    r.IterationKey, r.Parameters.RunIndex, r.Parameters.LoadLevelKPa, r.Parameters.LoadingLabel,
                    r.Parameters.TrussHeightM, r.Parameters.DeltaHM, r.Parameters.PretensionKN, r.Parameters.PretensionLabel,
                    r.ExtractionStatus.ToString(), r.DiagnosticReportRelativePath,
                    colorDim.Key(r.Parameters), dashDim.Key(r.Parameters), color2Dim.Key(r.Parameters),
                    g.Points.OrderBy(p => p.Sequence).Select(p => new DeflectionPointDto(
                        p.PointName, Axis(p, g.ProfileAxis), p.UzMm, p.Sequence, p.Status.ToString())).ToArray()));
            }
            if (series.Count > 0)
                groups.Add(new DeflectionGroupReportDto(gn, outputCase, axis, series));
        }

        var warnings = results.SelectMany(r => r.Warnings)
            .Select(w => new ResultReportWarningDto(w.IterationKey, w.Category, w.Severity.ToString(), w.Message))
            .ToArray();

        var study = new StudyMetadataDto(
            baselineName, baselinePath, studyFolder, studyMode.ToString(),
            results.Count, DateTimeOffset.Now.ToString("o"), "Uz", "m", "mm");

        return new DeflectionReportDto(study, groups, colorDim.Name, dashDim.Name,
            colorLegend, dashLegend, color2Dim.Name, color2Legend, warnings);
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
  <button id="showAll">Show all</button>
  <button id="hideAll">Hide all</button>
  <button id="reset">Reset zoom</button>
  <span class="sep"></span>
  <button id="srcBtn">View source data</button>
  <span class="sep"></span>
  <span class="hint">wheel = zoom X · drag = pan · double-click = reset · click a colour / line = solo · click a candidate = show/hide</span>
</div>
<div id="panels">
  <section class="panel" data-view="0">
    <h3>Deflection profile vs <b>truss height</b><span> — U<sub>z</sub> under the result load case</span></h3>
    <div class="stage"><svg class="chart" preserveAspectRatio="none"></svg><div class="legend"></div></div>
  </section>
  <section class="panel" data-view="1">
    <h3>Deflection profile vs <b>initial pretension force</b><span> — U<sub>z</sub> under the result load case</span></h3>
    <div class="stage"><svg class="chart" preserveAspectRatio="none"></svg><div class="legend"></div></div>
  </section>
</div>
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
#panels{padding-bottom:8px}
.panel{border-bottom:1px solid var(--bd)}
.panel h3{margin:0;padding:8px 14px 2px;font-size:12px;font-weight:600}
.panel h3 b{color:var(--acc)}.panel h3 span{font-weight:400;color:var(--mu)}
.stage{display:flex;height:390px;padding:4px 6px 10px}
.chart{flex:1;height:100%;min-width:0;background:#fff}
.legend{width:250px;flex:none;overflow:auto;border-left:1px solid var(--bd);padding:8px;font-size:11.5px}
.legend h4{margin:8px 0 4px;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:var(--mu)}
.legend .hint2{font-weight:400;text-transform:none;letter-spacing:0;color:#9aa0a6;font-size:9.5px}
.legend .row{display:flex;align-items:center;gap:6px;padding:2px 0;cursor:pointer}
.legend .row.key:hover,.legend .row.dkey:hover{background:#f0f3f7;border-radius:2px}
.legend .row.off{opacity:.35}
.legend .row.hi{background:#e8f0fe;border-radius:2px}
.legend .sw{width:24px;height:0;border-top-width:3px;border-top-style:solid;flex:none}
.legend .dash{width:26px;height:0;border-top:2px solid #444;flex:none}
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
  const dashName = k => (D.dashLegend.find(e=>e.key===k)||{}).value || 'solid';
  const dashArr  = k => dashMap[dashName(k)] ?? '';
  const dashCss  = k => { const v=dashName(k); return v==='dot'?'dotted':(v==='solid'?'solid':'dashed'); };
  const legendColor = (legend,k) => (legend.find(e=>e.key===k)||{}).value || '#9aa0a6';

  // two views of the same deflection profile: 0 = colour by truss height, 1 = colour by initial
  // pretension force. Line style (dashKey = load candidate) and candidate visibility are shared.
  const VIEWS = [
    {field:'colorKey',  legend:D.colorLegend,  dim:D.colorDimension },
    {field:'colorKey2', legend:D.colorLegend2, dim:D.colorDimension2},
  ];

  let gi = 0, showLegend = true, hidden = new Set(), hi = null;
  const panels = [];

  D.groups.forEach((g,i)=>{ const o=document.createElement('option'); o.value=i;
    o.textContent=`${g.groupName}  ·  ${g.outputCase}  ·  ${g.series.length} candidate(s)`; sel.appendChild(o); });

  function grp(){ return D.groups[gi]; }
  function visibleSeries(){ return grp().series.filter(s=>!hidden.has(s.iterationKey)); }
  function xExtent(){ const xs=grp().series.flatMap(s=>s.points.filter(p=>p.uzMm!=null).map(p=>p.coord)); return xs.length?[Math.min(...xs),Math.max(...xs)]:[0,1]; }
  function groupOff(field,k){ const l=grp().series.filter(s=>s[field]===k); return l.length>0 && l.every(s=>hidden.has(s.iterationKey)); }
  // "solo" a legend group: show only its series, hide the rest. Click the same row again to restore all.
  function soloGroup(field,k){
    const all=grp().series, tgt=all.filter(s=>s[field]===k), other=all.filter(s=>s[field]!==k);
    const iso = other.length>0 && other.every(s=>hidden.has(s.iterationKey)) && tgt.every(s=>!hidden.has(s.iterationKey));
    hidden.clear(); if(!iso) other.forEach(s=>hidden.add(s.iterationKey)); hi=null; renderAll();
  }
  function toggleCand(k){ hidden.has(k)?hidden.delete(k):hidden.add(k); renderAll(); }

  function ticks(a,b,n){ const step=nice((b-a)/n); const t0=Math.ceil(a/step)*step,o=[]; for(let v=t0;v<=b+1e-9;v+=step)o.push(+v.toFixed(10)); return o; }
  function nice(x){ const e=Math.pow(10,Math.floor(Math.log10(Math.abs(x)||1))); const f=x/e; return (f<1.5?1:f<3?2:f<7?5:10)*e; }
  function fmt(v){ return Math.abs(v)>=100?v.toFixed(0):Math.abs(v)>=1?v.toFixed(2):v.toFixed(3); }

  function makePanel(host, cfg){
    const chart = host.querySelector('.chart'), legendEl = host.querySelector('.legend');
    let zoomX = null;
    // Clamp an X window to the data extent: never wider than the full span, never < 4% of it,
    // never panned past an end. null => un-zoomed / full view.
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

    function render(){
      const g = grp(); if(!g){ chart.innerHTML=''; legendEl.innerHTML=''; return; }
      legendEl.hidden = !showLegend;
      const W = chart.clientWidth||900, H = chart.clientHeight||380, m={l:58,r:14,t:14,b:40};
      const all = g.series.flatMap(s=>s.points.filter(p=>p.uzMm!=null));
      const [dx0,dx1] = xExtent();
      let x0=dx0, x1=dx1;
      if(zoomX){ x0=zoomX[0]; x1=zoomX[1]; }
      // never render outside the data extent — no blank gutters from an over-pan / over-zoom
      if(x0<dx0) x0=dx0; if(x1>dx1) x1=dx1; if(x1-x0 < (dx1-dx0)*0.02){ x0=dx0; x1=dx1; }
      let ys = all.map(p=>p.uzMm);
      let y0 = Math.min(...ys,0), y1 = Math.max(...ys,0);
      if(x0===x1){x0-=1;x1+=1;} if(y0===y1){y0-=1;y1+=1;}
      const py = 0.06*(y1-y0); y0-=py; y1+=py;
      const sx = v => m.l + (v-x0)/(x1-x0)*(W-m.l-m.r);
      const sy = v => m.t + (y1-v)/(y1-y0)*(H-m.t-m.b);   // standard orientation: +Uz up

      const P=[`<rect x="0" y="0" width="${W}" height="${H}" fill="#fff"/>`];
      ticks(x0,x1,8).forEach(t=>{ const X=sx(t); P.push(`<line x1="${X}" y1="${m.t}" x2="${X}" y2="${H-m.b}" stroke="#eef0f2"/><text x="${X}" y="${H-m.b+16}" font-size="10" fill="#666" text-anchor="middle">${fmt(t)}</text>`); });
      ticks(y0,y1,6).forEach(t=>{ const Y=sy(t); P.push(`<line x1="${m.l}" y1="${Y}" x2="${W-m.r}" y2="${Y}" stroke="${t===0?'#c7ccd2':'#eef0f2'}"/><text x="${m.l-6}" y="${Y+3}" font-size="10" fill="#666" text-anchor="end">${fmt(t)}</text>`); });
      P.push(`<line x1="${m.l}" y1="${m.t}" x2="${m.l}" y2="${H-m.b}" stroke="#333"/><line x1="${m.l}" y1="${H-m.b}" x2="${W-m.r}" y2="${H-m.b}" stroke="#333"/>`);
      P.push(`<text x="${(m.l+W-m.r)/2}" y="${H-6}" font-size="11" fill="#333" text-anchor="middle">${g.profileAxis} coordinate [m]</text>`);
      P.push(`<text transform="translate(14,${(m.t+H-m.b)/2}) rotate(-90)" font-size="11" fill="#333" text-anchor="middle">Uz [mm]  (− = downward)</text>`);

      visibleSeries().slice().sort((a,b)=>(a.iterationKey===hi?1:0)-(b.iterationKey===hi?1:0)).forEach(s=>{
        const vp = s.points.filter(p=>p.uzMm!=null);
        const pts = vp.map(p=>`${sx(p.coord).toFixed(1)},${sy(p.uzMm).toFixed(1)}`).join(' ');
        if(!pts) return;
        const c = legendColor(cfg.legend, s[cfg.field]);
        const op = (!hi || s.iterationKey===hi) ? 1 : 0.18, wd = s.iterationKey===hi ? 3.4 : 1.8;
        P.push(`<polyline points="${pts}" fill="none" stroke="${c}" stroke-width="${wd}" stroke-dasharray="${dashArr(s.dashKey)}" opacity="${op}" data-k="${s.iterationKey}"/>`);
        vp.forEach(p=>P.push(`<circle cx="${sx(p.coord).toFixed(1)}" cy="${sy(p.uzMm).toFixed(1)}" r="${s.iterationKey===hi?3.4:2.4}" fill="${c}" opacity="${op}" data-k="${s.iterationKey}" data-pt="${p.pointName}" data-x="${p.coord}" data-uz="${p.uzMm}"/>`));
      });
      chart.setAttribute('viewBox',`0 0 ${W} ${H}`);
      chart.innerHTML = P.join('');
      drawLegend();
    }

    function drawLegend(){
      const g = grp(); const h=[];
      h.push(`<h4>${cfg.dim} — colour <span class="hint2">click = solo</span></h4>`);
      cfg.legend.forEach(e=>{ const off=groupOff(cfg.field,e.key)?' off':'';
        h.push(`<div class="row key${off}" data-k="${e.key}"><span class="sw" style="border-top-color:${e.value}"></span>${e.label}</div>`); });
      if(D.dashLegend.length){
        h.push(`<h4>${D.dashDimension} — line <span class="hint2">click = solo</span></h4>`);
        D.dashLegend.forEach(e=>{ const off=groupOff('dashKey',e.key)?' off':'';
          h.push(`<div class="row dkey${off}" data-k="${e.key}"><span class="dash" style="border-top-style:${e.value==='dot'?'dotted':(e.value==='solid'?'solid':'dashed')}"></span>${e.label}</div>`); });
      }
      const shown = cfg.legend.map(e=>e.key).filter(k=>!groupOff(cfg.field,k));
      const cands = shown.length ? g.series.filter(s=>shown.includes(s[cfg.field])) : g.series;
      h.push(`<h4>Candidates <span class="hint2">${cands.length}/${g.series.length} · click = show/hide</span></h4>`);
      cands.forEach(s=>{ const off=hidden.has(s.iterationKey)?' off':'', on=s.iterationKey===hi?' hi':'';
        h.push(`<div class="row cand${off}${on}" data-k="${s.iterationKey}"><span class="sw" style="border-top-color:${legendColor(cfg.legend,s[cfg.field])};border-top-style:${dashCss(s.dashKey)}"></span>${s.iterationKey} · ${s.loadingLabel} · H ${(+s.trussHeightM).toFixed(2)}m${s.pretensionKN!=null?' · '+(+s.pretensionKN).toFixed(0)+' kN':''}</div>`); });
      legendEl.innerHTML = h.join('');
      legendEl.querySelectorAll('.row.key').forEach(r=>r.onclick=()=>soloGroup(cfg.field, r.dataset.k));
      legendEl.querySelectorAll('.row.dkey').forEach(r=>r.onclick=()=>soloGroup('dashKey', r.dataset.k));
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

    return { render, reset(){ zoomX=null; } };
  }

  function drawMatrix(){
    const vis = visibleSeries();
    const rowsMap = new Map();
    vis.forEach(s=>s.points.forEach(p=>{ const key=p.pointName+'|'+p.coord; if(!rowsMap.has(key)) rowsMap.set(key,{pt:p.pointName,x:p.coord,vals:{}}); }));
    vis.forEach(s=>s.points.forEach(p=>{ const r=rowsMap.get(p.pointName+'|'+p.coord); if(r) r.vals[s.iterationKey]=p.uzMm; }));
    const rows=[...rowsMap.values()].sort((a,b)=>a.x-b.x);
    const th = vis.map(s=>`<th class="ch${s.iterationKey===hi?' hi':''}" data-k="${s.iterationKey}" title="click to highlight in both charts">${s.iterationKey}<br><span style="font-weight:400;color:#666">${s.loadingLabel} · H${(+s.trussHeightM).toFixed(2)}${s.pretensionKN!=null?' · '+(+s.pretensionKN).toFixed(0)+'kN':''}</span></th>`).join('');
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

  const mw = document.getElementById('matrixWrap'), srcBtn = document.getElementById('srcBtn');
  function renderAll(){ panels.forEach(p=>p.render()); if(!mw.hidden) drawMatrix(); drawWarnings(); }

  if(!D.groups.length){
    document.getElementById('panels').innerHTML='<p style="padding:20px;color:#666">No deflection results in this study.</p>';
    srcBtn.hidden = true;
  } else {
    document.querySelectorAll('#panels .panel').forEach(host=>panels.push(makePanel(host, VIEWS[+host.dataset.view])));
  }

  sel.onchange=()=>{ gi=+sel.value; hi=null; panels.forEach(p=>p.reset()); renderAll(); };
  document.getElementById('legendToggle').onchange=e=>{ showLegend=e.target.checked; renderAll(); };
  document.getElementById('showAll').onclick=()=>{ hidden.clear(); renderAll(); };
  document.getElementById('hideAll').onclick=()=>{ if(!D.groups.length) return; grp().series.forEach(s=>hidden.add(s.iterationKey)); renderAll(); };
  document.getElementById('reset').onclick=()=>{ panels.forEach(p=>p.reset()); renderAll(); };
  function toggleSrc(show){ mw.hidden=!show; srcBtn.textContent = show?'Hide source data':'View source data'; if(show) drawMatrix(); }
  srcBtn.onclick=()=>toggleSrc(mw.hidden);
  document.getElementById('mClose').onclick=()=>toggleSrc(false);
  window.addEventListener('resize',()=>renderAll());
  renderAll();
})();
""";
}
