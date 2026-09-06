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
                    g.Points.OrderBy(p => p.Sequence).Select(p => new DeflectionPointDto(
                        p.PointName, Axis(p, g.ProfileAxis), p.UzMm, p.Sequence, p.Status.ToString())).ToArray()));
            }
            if (series.Count > 0)
                groups.Add(new DeflectionGroupReportDto(gn, outputCase, axis, series));
        }

        // deterministic colour = load level (ascending); grey for null
        var allSeries = groups.SelectMany(g => g.Series).ToArray();
        var loadKeys = allSeries.Select(LoadKey).Distinct().OrderBy(NumericKey).ToArray();
        var loadColors = loadKeys.Select((k, i) => new LegendEntryDto(
            k, LoadKeyLabel(k, allSeries), k == "" ? "#9aa0a6" : Palette[i % Palette.Length])).ToList();

        var heightKeys = allSeries.Select(s => s.TrussHeightM.ToString("0.####")).Distinct()
            .OrderBy(k => double.Parse(k)).ToArray();
        var heightDashes = heightKeys.Select((k, i) => new LegendEntryDto(
            k, $"H = {double.Parse(k):0.###} m", Dashes[i % Dashes.Length])).ToList();

        var warnings = results.SelectMany(r => r.Warnings)
            .Select(w => new ResultReportWarningDto(w.IterationKey, w.Category, w.Severity.ToString(), w.Message))
            .ToArray();

        var study = new StudyMetadataDto(
            baselineName, baselinePath, studyFolder, studyMode.ToString(),
            results.Count, DateTimeOffset.Now.ToString("o"), "Uz", "m", "mm");

        return new DeflectionReportDto(study, groups, loadColors, heightDashes, warnings);
    }

    private static double Axis(MonitoredPointResultDto p, ProfileCoordinateAxis a) =>
        a switch { ProfileCoordinateAxis.Y => p.Y, ProfileCoordinateAxis.Z => p.Z, _ => p.X };

    private static string LoadKey(DeflectionCandidateSeriesDto s) =>
        s.LoadLevelKPa is { } q ? q.ToString("0.####") : "";
    private static string LoadKeyLabel(string key, DeflectionCandidateSeriesDto[] all) =>
        key == "" ? "mixed / n-a" : $"{double.Parse(key):0.###} kPa";
    private static double NumericKey(string k) => k == "" ? double.MaxValue : double.Parse(k);

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
      <li class="on">Deflection Profile <span>active</span></li>
      <li>Member Forces <span>future</span></li>
      <li>Candidate Summary <span>future</span></li>
      <li>Result Envelopes <span>future</span></li>
      <li>Custom Result <span>future</span></li>
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
  <span class="hint">wheel = zoom X · drag = pan · double-click = reset · click legend row = toggle</span>
</div>
<div id="stage"><svg id="chart" preserveAspectRatio="none"></svg><div id="legend"></div></div>
<div id="tip" hidden></div>
<div id="matrixWrap">
  <div class="mhdr"><strong>Source Data Matrix</strong><button id="mtoggle">Hide table</button></div>
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
*{box-sizing:border-box}html,body{margin:0;height:100%}
body{font:13px/1.45 "Segoe UI",system-ui,sans-serif;color:var(--tx);background:#fff;display:flex;flex-direction:column}
header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;padding:10px 14px;border-bottom:2px solid var(--acc)}
h1{font-size:16px;margin:0}.meta{color:var(--mu);margin:3px 0 0;font-size:11.5px}
.lib{font-size:12px}.lib summary{cursor:pointer;color:var(--acc);font-weight:600}
.lib ul{list-style:none;margin:6px 0 0;padding:0;border:1px solid var(--bd);border-radius:3px;min-width:180px}
.lib li{padding:4px 8px;border-bottom:1px solid var(--su);color:var(--mu)}
.lib li.on{color:var(--tx)}.lib li span{float:right;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:#9aa0a6}
.lib li.on span{color:#2E7D32}
#toolbar{display:flex;align-items:center;gap:10px;padding:6px 14px;border-bottom:1px solid var(--bd);flex-wrap:wrap}
#toolbar .sep{width:1px;height:16px;background:var(--bd)}
#toolbar .hint{color:var(--mu);font-size:11px}
select,button{font:12px "Segoe UI",sans-serif;padding:3px 8px;border:1px solid var(--bd);background:#fff;border-radius:3px}
button{cursor:pointer}button:hover{background:var(--su)}
#stage{flex:1;display:flex;min-height:340px;position:relative}
#chart{flex:1;height:100%;background:#fff}
#legend{width:260px;overflow:auto;border-left:1px solid var(--bd);padding:8px;font-size:11.5px}
#legend h4{margin:8px 0 4px;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:var(--mu)}
#legend .row{display:flex;align-items:center;gap:6px;padding:2px 0;cursor:pointer}
#legend .row.off{opacity:.35}
#legend .sw{width:22px;height:0;border-top-width:3px;border-top-style:solid;flex:none}
#legend .dash{width:26px;height:0;border-top:2px solid #444;flex:none}
#tip{position:absolute;pointer-events:none;background:#fffffff2;border:1px solid var(--acc);border-radius:3px;padding:6px 8px;font:11px/1.4 Consolas,monospace;z-index:5;max-width:280px}
#matrixWrap{border-top:1px solid var(--bd);max-height:38vh;display:flex;flex-direction:column}
.mhdr{display:flex;justify-content:space-between;align-items:center;padding:5px 14px;background:var(--su)}
#matrix{overflow:auto}
#matrix table{border-collapse:separate;border-spacing:0;font-size:11px;white-space:nowrap}
#matrix th,#matrix td{border:1px solid var(--bd);padding:3px 8px;text-align:right;background:#fff}
#matrix thead th{position:sticky;top:0;background:var(--su);z-index:3}
#matrix tbody th{position:sticky;left:0;background:var(--su);z-index:2;text-align:left}
#matrix tbody td:first-of-type{position:sticky;left:var(--c0,90px);background:var(--su);z-index:2}
#matrix th.k{font-weight:600;color:var(--mu)}
#warns{padding:6px 14px;font-size:11px;color:#8a6d00}
#warns .e{color:#C62828}
a{color:var(--acc)}
""";

    private const string Script = """
(function(){
  const D = JSON.parse(document.getElementById('report-data').textContent);
  const chart = document.getElementById('chart'), legend = document.getElementById('legend'),
        tip = document.getElementById('tip'), sel = document.getElementById('group');
  const dashMap = {solid:'',dash:'8 5',dot:'2 4',dashdot:'10 4 2 4',longdash:'16 6',longdashdot:'16 5 3 5'};
  const colorFor = k => (D.loadLevelColors.find(e=>e.key===k)||{}).value || '#9aa0a6';
  const dashFor = k => dashMap[(D.trussHeightDashes.find(e=>e.key===k)||{}).value] ?? '';
  const loadKey = s => s.loadLevelKPa==null ? '' : (+s.loadLevelKPa).toFixed(4).replace(/\.?0+$/,'') || '0';
  const heightKey = s => (+s.trussHeightM).toFixed(4).replace(/\.?0+$/,'') || '0';

  let gi = 0, showLegend = true, hidden = new Set(), zoomX = null;

  D.groups.forEach((g,i)=>{ const o=document.createElement('option'); o.value=i; o.textContent=`${g.groupName}  ·  ${g.outputCase}  ·  ${g.series.length} candidate(s)`; sel.appendChild(o); });
  if(!D.groups.length){ chart.outerHTML='<p style="padding:20px;color:#666">No deflection results in this study.</p>'; }

  function grp(){ return D.groups[gi]; }
  function visibleSeries(){ return grp().series.filter(s=>!hidden.has(s.iterationKey)); }

  function render(){
    const g = grp(); if(!g) return;
    const W = chart.clientWidth||900, H = chart.clientHeight||420;
    const m = {l:58,r:14,t:14,b:40};
    const all = g.series.flatMap(s=>s.points.filter(p=>p.uzMm!=null));
    let xs = all.map(p=>p.coord), ys = all.map(p=>p.uzMm);
    let x0 = Math.min(...xs), x1 = Math.max(...xs), y0 = Math.min(...ys,0), y1 = Math.max(...ys,0);
    if(zoomX){ x0 = zoomX[0]; x1 = zoomX[1]; }
    if(x0===x1){x0-=1;x1+=1;} if(y0===y1){y0-=1;y1+=1;}
    const py = 0.06*(y1-y0); y0-=py; y1+=py;
    const sx = v => m.l + (v-x0)/(x1-x0)*(W-m.l-m.r);
    const sy = v => m.t + (y1-v)/(y1-y0)*(H-m.t-m.b);

    const P=[]; P.push(`<rect x="0" y="0" width="${W}" height="${H}" fill="#fff"/>`);
    // gridlines + ticks
    const xt = ticks(x0,x1,8), yt = ticks(y0,y1,6);
    xt.forEach(t=>{ const X=sx(t); P.push(`<line x1="${X}" y1="${m.t}" x2="${X}" y2="${H-m.b}" stroke="#eef0f2"/><text x="${X}" y="${H-m.b+16}" font-size="10" fill="#666" text-anchor="middle">${fmt(t)}</text>`); });
    yt.forEach(t=>{ const Y=sy(t); P.push(`<line x1="${m.l}" y1="${Y}" x2="${W-m.r}" y2="${Y}" stroke="${t===0?'#c7ccd2':'#eef0f2'}"/><text x="${m.l-6}" y="${Y+3}" font-size="10" fill="#666" text-anchor="end">${fmt(t)}</text>`); });
    P.push(`<line x1="${m.l}" y1="${m.t}" x2="${m.l}" y2="${H-m.b}" stroke="#333"/><line x1="${m.l}" y1="${H-m.b}" x2="${W-m.r}" y2="${H-m.b}" stroke="#333"/>`);
    P.push(`<text x="${(m.l+W-m.r)/2}" y="${H-6}" font-size="11" fill="#333" text-anchor="middle">${g.profileAxis} coordinate [m]</text>`);
    P.push(`<text transform="translate(14,${(m.t+H-m.b)/2}) rotate(-90)" font-size="11" fill="#333" text-anchor="middle">Uz [mm]</text>`);

    visibleSeries().forEach(s=>{
      const pts = s.points.filter(p=>p.uzMm!=null).map(p=>`${sx(p.coord).toFixed(1)},${sy(p.uzMm).toFixed(1)}`).join(' ');
      if(!pts) return;
      P.push(`<polyline points="${pts}" fill="none" stroke="${colorFor(loadKey(s))}" stroke-width="1.8" stroke-dasharray="${dashFor(heightKey(s))}" data-k="${s.iterationKey}"/>`);
      s.points.filter(p=>p.uzMm!=null).forEach(p=>P.push(`<circle cx="${sx(p.coord).toFixed(1)}" cy="${sy(p.uzMm).toFixed(1)}" r="2.4" fill="${colorFor(loadKey(s))}" data-k="${s.iterationKey}" data-pt="${p.pointName}" data-x="${p.coord}" data-uz="${p.uzMm}"/>`));
    });
    chart.setAttribute('viewBox',`0 0 ${W} ${H}`);
    chart.innerHTML = P.join('');
    drawLegend(); drawMatrix(); drawWarnings();
    legend.style.display = showLegend ? '' : 'none';
  }

  function drawLegend(){
    const g = grp(); const h=[];
    h.push('<h4>Load level — colour</h4>');
    D.loadLevelColors.forEach(e=>h.push(`<div class="row"><span class="sw" style="border-top-color:${e.value};width:24px"></span>${e.label}</div>`));
    h.push('<h4>Truss height — line</h4>');
    D.trussHeightDashes.forEach(e=>h.push(`<div class="row"><span class="dash" style="border-top-style:${e.value==='dot'?'dotted':(e.value==='solid'?'solid':'dashed')}"></span>${e.label}</div>`));
    h.push('<h4>Candidates</h4>');
    g.series.forEach(s=>{
      const off = hidden.has(s.iterationKey)?' off':'';
      h.push(`<div class="row cand${off}" data-k="${s.iterationKey}"><span class="sw" style="border-top-color:${colorFor(loadKey(s))};border-top-style:${(D.trussHeightDashes.find(e=>e.key===heightKey(s))||{}).value==='dot'?'dotted':'solid'}"></span>${s.iterationKey} · ${s.loadingLabel} · H ${(+s.trussHeightM).toFixed(2)}m</div>`);
    });
    legend.innerHTML = h.join('');
    legend.querySelectorAll('.row.cand').forEach(r=>r.onclick=()=>{ const k=r.dataset.k; hidden.has(k)?hidden.delete(k):hidden.add(k); render(); });
  }

  function drawMatrix(){
    const g = grp(); const vis = visibleSeries();
    const rowsMap = new Map();
    vis.forEach(s=>s.points.forEach(p=>{ const key=p.pointName+'|'+p.coord; if(!rowsMap.has(key)) rowsMap.set(key,{pt:p.pointName,x:p.coord,vals:{}}); }));
    vis.forEach(s=>s.points.forEach(p=>{ const r=rowsMap.get(p.pointName+'|'+p.coord); if(r) r.vals[s.iterationKey]=p.uzMm; }));
    const rows=[...rowsMap.values()].sort((a,b)=>a.x-b.x);
    const th = vis.map(s=>`<th>${s.iterationKey}<br><span style="font-weight:400;color:#666">${s.loadingLabel} · H${(+s.trussHeightM).toFixed(2)}${s.pretensionKN!=null?' · '+(+s.pretensionKN).toFixed(0)+'kN':''}</span></th>`).join('');
    const body = rows.map(r=>`<tr><th class="k">${r.pt}</th><td>${fmt(r.x)}</td>${vis.map(s=>{const v=r.vals[s.iterationKey];return `<td>${v==null?'—':(+v).toFixed(3)}</td>`;}).join('')}</tr>`).join('');
    document.getElementById('matrix').innerHTML =
      `<table><thead><tr><th class="k">Point</th><th>X [m]</th>${th}</tr></thead><tbody>${body}</tbody></table>`;
  }

  function drawWarnings(){
    const w = document.getElementById('warns');
    if(!D.warnings.length){ w.textContent=''; return; }
    w.innerHTML = '<strong>Warnings:</strong> ' + D.warnings.slice(0,40).map(x=>`<span class="${x.severity==='Error'?'e':''}">[${x.iterationKey||'study'}] ${x.category}: ${x.message}</span>`).join(' · ')
      + (D.warnings.length>40?` … (+${D.warnings.length-40})`:'');
  }

  function ticks(a,b,n){ const step=nice((b-a)/n); const t0=Math.ceil(a/step)*step,o=[]; for(let v=t0;v<=b+1e-9;v+=step)o.push(+v.toFixed(10)); return o; }
  function nice(x){ const e=Math.pow(10,Math.floor(Math.log10(Math.abs(x)||1))); const f=x/e; return (f<1.5?1:f<3?2:f<7?5:10)*e; }
  function fmt(v){ return Math.abs(v)>=100?v.toFixed(0):Math.abs(v)>=1?v.toFixed(2):v.toFixed(3); }

  // interactions
  chart.addEventListener('mousemove',ev=>{
    const t=ev.target;
    if(t.tagName==='circle'){
      const k=t.dataset.k, s=grp().series.find(x=>x.iterationKey===k);
      tip.hidden=false;
      tip.innerHTML = `${k}<br>point ${t.dataset.pt}<br>X ${(+t.dataset.x).toFixed(3)} m<br>Uz ${(+t.dataset.uz).toFixed(3)} mm<br>`
        + `${s.loadingLabel}<br>H ${(+s.trussHeightM).toFixed(3)} m (ΔH ${s.deltaHM>=0?'+':''}${(+s.deltaHM).toFixed(3)})<br>`
        + `${s.pretensionLabel}<br>case ${grp().outputCase}`;
      const r=chart.getBoundingClientRect();
      tip.style.left=Math.min(ev.clientX-r.left+12, r.width-tip.offsetWidth-6)+'px';
      tip.style.top=(ev.clientY-r.top+12)+'px';
    } else tip.hidden=true;
  });
  chart.addEventListener('mouseleave',()=>tip.hidden=true);
  chart.addEventListener('wheel',ev=>{
    ev.preventDefault();
    const g=grp(); const all=g.series.flatMap(s=>s.points.filter(p=>p.uzMm!=null).map(p=>p.coord));
    let x0=zoomX?zoomX[0]:Math.min(...all), x1=zoomX?zoomX[1]:Math.max(...all);
    const r=chart.getBoundingClientRect(); const fx=(ev.clientX-r.left-58)/(r.width-72);
    const cx=x0+(x1-x0)*Math.max(0,Math.min(1,fx)); const f=ev.deltaY<0?0.85:1/0.85;
    zoomX=[cx-(cx-x0)*f, cx+(x1-cx)*f]; render();
  },{passive:false});
  let drag=null;
  chart.addEventListener('mousedown',ev=>{ const g=grp(); const all=g.series.flatMap(s=>s.points.filter(p=>p.uzMm!=null).map(p=>p.coord));
    drag={x:ev.clientX, x0:zoomX?zoomX[0]:Math.min(...all), x1:zoomX?zoomX[1]:Math.max(...all), w:chart.clientWidth-72}; });
  window.addEventListener('mousemove',ev=>{ if(!drag)return; const d=(ev.clientX-drag.x)/drag.w*(drag.x1-drag.x0); zoomX=[drag.x0-d,drag.x1-d]; render(); });
  window.addEventListener('mouseup',()=>drag=null);
  chart.addEventListener('dblclick',()=>{ zoomX=null; render(); });

  sel.onchange=()=>{ gi=+sel.value; zoomX=null; render(); };
  document.getElementById('legendToggle').onchange=e=>{ showLegend=e.target.checked; render(); };
  document.getElementById('showAll').onclick=()=>{ hidden.clear(); render(); };
  document.getElementById('hideAll').onclick=()=>{ grp().series.forEach(s=>hidden.add(s.iterationKey)); render(); };
  document.getElementById('reset').onclick=()=>{ zoomX=null; render(); };
  document.getElementById('mtoggle').onclick=e=>{ const m=document.getElementById('matrix'); const h=m.style.display==='none'; m.style.display=h?'':'none'; e.target.textContent=h?'Hide table':'Show table'; };
  window.addEventListener('resize',render);
  render();
})();
""";
}
