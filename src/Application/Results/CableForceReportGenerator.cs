using System.Globalization;
using System.IO;
using System.Text.Json;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Results;

/// <summary>Builds <c>Cable_Forces_Report.html</c> — one chart per cable, shared Y-scale, Combined mode
/// re-organised by Truss Height or Pretension on X. Self-contained (inline SVG, no CDN); regenerable from
/// <c>Deflection_Results_Source.xlsx</c> alone. Missing results are shown as a gap / "Result unavailable",
/// never 0. A mixed pretension candidate stays categorical on the X axis — no invented kN value.</summary>
public sealed class CableForceReportGenerator
{
    public const string ReportFileName = "Cable_Forces_Report.html";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public string Generate(
        string studyFolder,
        IReadOnlyList<CandidateResultDto> results,
        string baselineName, IterationStudyMode studyMode, string cableResultCase,
        IReadOnlyDictionary<string, double>? capacities = null,
        IReadOnlyList<string>? siblingReports = null)
    {
        Directory.CreateDirectory(studyFolder);
        var siblings = siblingReports is null
            ? ReportNav.PresentIn(studyFolder, "cable")
            : new HashSet<string>(siblingReports, StringComparer.Ordinal);
        var dto = Build(results, baselineName, studyMode, cableResultCase, capacities, siblings.ToArray());
        var path = Path.Combine(studyFolder, ReportFileName);
        AtomicFile.WriteAllText(path, BuildHtml(dto), new System.Text.UTF8Encoding(false));
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
        var resultCase = ConsolidatedResultWriter.ReadCableResultCase(wbPath);
        var capacities = ConsolidatedResultWriter.ReadCableCapacities(wbPath);
        return Generate(studyFolder, results, name, mode, resultCase, capacities);
    }

    // ---------- DTO assembly ----------

    public CableForceReportDto Build(
        IReadOnlyList<CandidateResultDto> results,
        string baselineName, IterationStudyMode studyMode, string cableResultCase,
        IReadOnlyDictionary<string, double>? capacities = null,
        IReadOnlyList<string>? siblingReports = null)
    {
        var withCf = results.Where(r => (r.CableForces?.Count ?? 0) > 0)
            .OrderBy(r => r.Parameters.RunIndex).ToArray();

        var cables = withCf.SelectMany(r => r.CableForces!).Select(c => c.CableName)
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToArray();

        var loads = withCf.Select(r => r.Parameters)
            .GroupBy(p => p.LoadingCandidateId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => { var p = g.First();
                return new CableForceAxisDto(p.LoadingCandidateId,
                    $"{p.LoadingCandidateId} · {(string.IsNullOrWhiteSpace(p.LoadingLabel) ? Num(p.LoadLevelKPa) + " kPa" : p.LoadingLabel)}",
                    p.LoadLevelKPa); })
            .ToArray();

        var heights = withCf.Select(r => r.Parameters)
            .GroupBy(p => p.HeightCandidateId, StringComparer.Ordinal)
            .OrderBy(g => g.First().TrussHeightM)
            .Select(g => { var p = g.First();
                return new CableForceAxisDto(p.HeightCandidateId, $"{p.HeightCandidateId} · {p.TrussHeightM.ToString("0.###", CultureInfo.InvariantCulture)} m", p.TrussHeightM); })
            .ToArray();

        var pretensions = withCf.Select(r => r.Parameters)
            .GroupBy(p => p.PretensionCandidateId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => { var p = g.First();
                return new CableForceAxisDto(p.PretensionCandidateId, p.PretensionLabel, p.PretensionKN, Mixed: p.PretensionKN is null); })
            .ToArray();

        var rows = withCf.Select(r =>
        {
            var p = r.Parameters;
            var targets = r.CableForces!.Where(c => c.TargetForceKN is not null)
                .ToDictionary(c => c.CableName, c => c.TargetForceKN!.Value, StringComparer.Ordinal);
            var forces = r.CableForces!
                .ToDictionary(c => c.CableName, c => c.Status == ResultValueStatus.Valid ? c.ForceKN : null, StringComparer.Ordinal);
            return new CableForceCandidateDto(
                p.RunIndex, $"C{p.RunIndex:000}", r.IterationKey, Path.GetFileName(r.CandidateModelPath),
                p.LoadingCandidateId, p.LoadLevelKPa, p.HeightCandidateId, p.TrussHeightM,
                p.PretensionCandidateId, p.PretensionLabel, p.PretensionKN is null,
                targets, forces);
        }).ToArray();

        var allForces = rows.SelectMany(x => x.Forces.Values).Where(v => v is { } f && double.IsFinite(f)).Select(v => v!.Value).ToArray();
        double yMin = 0, yMax = 1;
        if (allForces.Length > 0)
        {
            var lo = allForces.Min();
            var hi = allForces.Max();
            var pad = Math.Max(25, (hi - lo) * 0.08);
            yMin = Math.Floor((lo - pad) / 50) * 50;
            yMax = Math.Ceiling((hi + pad) / 50) * 50;
            if (yMax <= yMin) yMax = yMin + 50;
        }

        var warnings = results.SelectMany(r => r.Warnings)
            .Where(w => w.Category == "CableForce")
            .Select(w => new CableForceWarningDto(w.IterationKey, w.Message, w.Severity == ResultWarningSeverity.Error))
            .ToArray();

        var study = new CableForceStudyDto(baselineName, studyMode.ToString(), rows.Length,
            string.IsNullOrWhiteSpace(cableResultCase) ? "(not configured)" : cableResultCase,
            DateTimeOffset.Now.ToString("o"));

        // Keep only positive, finite capacities for cables that are actually in this report.
        var cableSet = new HashSet<string>(cables, StringComparer.Ordinal);
        var caps = (capacities ?? new Dictionary<string, double>())
            .Where(kv => cableSet.Contains(kv.Key) && double.IsFinite(kv.Value) && kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        return new CableForceReportDto(study, cables, loads, heights, pretensions, rows, yMin, yMax, warnings,
            caps, (siblingReports ?? ["cable"]).ToArray());
    }

    private static string Num(double? v) => v is { } d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "n/a";
    private static string Esc(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");

    // ---------- HTML ----------

    public string BuildHtml(CableForceReportDto dto)
    {
        var s = dto.Study;
        var json = JsonSerializer.Serialize(dto, Json);
        var library = ReportNav.Library("cable", new HashSet<string>(dto.SiblingReports, StringComparer.Ordinal));
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Cable Forces — {{Esc(s.BaselineModelName)}}</title>
<style>{{Style}}</style></head>
<body>
<header>
  <div>
    <h1>Cable Forces Viewer</h1>
    <p class="meta">{{Esc(s.BaselineModelName)}} · {{Esc(s.StudyMode)}} · {{s.CandidateCount}} candidate(s) ·
      cable result case: {{Esc(s.ResultCase)}} · generated {{Esc(s.GeneratedAtIso[..19].Replace('T', ' '))}}</p>
    <p class="intro">Every chart carries one part of the story: <b>what drives the force</b>, <b>how it behaves</b>
      across load × pretension × height, and <b>where it governs</b>. A missing result is a gap, never 0 kN.
      Switch <b>Value</b> to <b>% of capacity</b> for utilisation.</p>
  </div>
  {{library}}
</header>

<div id="bar">
  <span class="small">Value</span><button id="vk" class="active">Force (kN)</button><button id="vu">% of capacity</button>
  <span class="sep"></span>
  <label class="small">Sensitivity cable <select id="govcable" aria-label="Cable used for sensitivity analysis"></select></label>
  <span class="small" id="yinfo" aria-live="polite"></span>
</div>

<section class="blk">
  <h2>01 · What drives the force</h2>
  <p class="lead" id="tornlead"></p>
  <div class="tgrid"><div id="tornado"></div><div id="tornread"></div></div>
</section>

<section class="blk">
  <h2>02 · How it behaves across the grid</h2>
  <p class="lead">Cable force vs truss height — one column per cable, one row per load level. The pretension
    candidates are drawn as a band (min–max) with the median as the line; a hairline band means pretension
    changed nothing. One shared vertical scale.</p>
  <div id="trlegend" class="tleg"></div>
  <div id="trellis" class="trellis"></div>
</section>

<section class="blk">
  <h2>03 · Where it governs</h2>
  <p class="lead" id="hmlead"></p>
  <div class="hmwrap"><table id="heatmap" class="hm"></table></div>
  <div id="hmkey" class="hmkey"></div>
  <div id="gov" class="gov" hidden></div>
  <div id="meters" class="meters"></div>
</section>

<details class="block"><summary>Source Data</summary><div class="body"><div class="tablewrap"><table><thead><tr>
  <th>Run</th><th>Candidate</th><th>Iteration Key</th><th>Load</th><th>Height</th><th>Pretension</th><th>Cable</th>
  <th class="num">Target PT</th><th class="num">Force</th><th class="num">% cap</th>
</tr></thead><tbody id="tb"></tbody></table></div></div></details>

<div id="warns"></div>

<script id="report-data" type="application/json">{{json}}</script>
<script>{{ScriptV2}}</script>
</body></html>
""";
    }

    private const string Style = """
:root{--acc:#1F4788;--bd:#C7CCD2;--su:#F3F4F6;--tx:#1A1A1A;--mu:#666;
  --h0:#eef4fb;--h1:#c9ddf5;--h2:#8fbce9;--h3:#4a90e0;--h4:#1f5aa8}
*{box-sizing:border-box}html,body{margin:0}
body{font:13px/1.45 "Segoe UI",system-ui,sans-serif;color:var(--tx);background:#fff}
[hidden]{display:none!important}
header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;padding:10px 14px;border-bottom:2px solid var(--acc)}
h1{font-size:16px;margin:0}.meta{color:var(--mu);margin:3px 0 0;font-size:11.5px}
.intro{margin:6px 0 0;font-size:11.5px;line-height:1.5;max-width:860px}.intro b{color:var(--acc)}
.lib{font-size:12px}.lib summary{cursor:pointer;color:var(--acc);font-weight:600}
.lib ul{list-style:none;margin:6px 0 0;padding:0;border:1px solid var(--bd);border-radius:3px;min-width:200px}
.lib li{padding:4px 8px;border-bottom:1px solid var(--su);color:var(--mu)}
.lib li.on{color:var(--tx)}.lib li span{float:right;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:#9aa0a6}
.lib li.on span{color:#2E7D32}
#bar{position:sticky;top:0;z-index:10;display:flex;align-items:center;gap:8px;flex-wrap:wrap;padding:6px 14px;border-bottom:1px solid var(--bd);background:#fff}
select,button{font:12px "Segoe UI",sans-serif;padding:3px 8px;border:1px solid var(--bd);background:#fff;border-radius:3px}
button{cursor:pointer}button:hover{background:var(--su)}button.active{border-color:var(--acc);color:var(--acc);font-weight:600;background:#eef4fb}
button:disabled{opacity:.4;cursor:not-allowed}
.sep{width:1px;height:16px;background:var(--bd)}.small{font-size:11px;color:var(--mu)}
#yinfo{margin-left:auto}
section.blk{border-bottom:1px solid var(--bd);padding:12px 14px 16px}
section.blk h2{font-size:12.5px;color:var(--acc);margin:0 0 3px;letter-spacing:.01em}
.lead{font-size:11.5px;line-height:1.5;color:var(--mu);max-width:96ch;margin:0 0 10px}
.lead b{color:var(--tx)}
svg{display:block}
.grid{stroke:#e8ebef;stroke-width:1}.axis{stroke:#8e98a3;stroke-width:1}
.tick{fill:#687386;font-size:9px}.al{fill:#475467;font-size:9.5px;font-weight:600}
.ref100{stroke:#C62828;stroke-width:1.2;stroke-dasharray:5 3}
.sband{fill:rgba(31,71,136,.14)}.sline{fill:none;stroke:var(--acc);stroke-width:1.8}
.sdot{fill:#fff;stroke:var(--acc);stroke-width:1.3}
.govdot{fill:none;stroke:#C62828;stroke-width:2}
/* 01 tornado */
.tgrid{display:grid;grid-template-columns:1.55fr 1fr;gap:24px;align-items:center;max-width:1000px}
@media(max-width:820px){.tgrid{grid-template-columns:1fr}}
.tbar{fill:var(--acc)}
.readrow{display:grid;grid-template-columns:1fr auto;gap:2px 12px;padding:9px 0;border-bottom:1px solid #eee}
.readrow:last-child{border-bottom:0}
.readrow .lab{font-size:11px;font-weight:600;letter-spacing:.03em;text-transform:uppercase;color:#586173}
.readrow .big{font:600 17px "Segoe UI",sans-serif}
.readrow .note{grid-row:1 / span 2;align-self:center;font-size:11px;color:var(--mu);text-align:right}
.readrow .mini{grid-column:1 / -1;height:4px;border-radius:3px;background:var(--su);overflow:hidden;margin-top:4px}
.readrow .mini i{display:block;height:100%;background:var(--acc)}
/* 02 trellis */
.tleg{display:flex;gap:16px;align-items:center;font-size:11px;color:#586173;margin-bottom:6px}
.tleg .ln{width:18px;height:0;border-top:2px solid var(--acc)}
.tleg .bd{width:18px;height:11px;background:rgba(31,71,136,.14);border:1px solid rgba(31,71,136,.3);border-radius:2px}
.trellis{display:grid;border:1px solid var(--bd);border-radius:3px;overflow:hidden;max-width:1100px}
.trellis>div{border-right:1px solid #eee;border-bottom:1px solid #eee}
.trellis .thc{background:var(--su)}
.trellis .th{background:var(--su);font-weight:600;color:var(--acc);text-align:center;padding:6px 3px;font-size:11px}
.trellis .th:last-child{border-right:0}
.trellis .rl{background:var(--su);display:flex;flex-direction:column;justify-content:center;padding:5px 8px;gap:1px}
.trellis .rl b{font-size:11.5px}.trellis .rl span{font-size:9.5px;color:var(--mu)}
.trellis .pan{min-width:220px;padding:3px;background:#fff}.trellis .empty{display:flex;align-items:center;justify-content:center;min-height:112px;color:var(--mu);font-size:10.5px}
.trellis .pan:last-child{border-right:0}
.trellis-lastrow>div{border-bottom:0}
.pan svg{width:100%;height:auto}
/* 03 heatmap */
.hmwrap{overflow-x:auto}
.hm{border-collapse:separate;border-spacing:0;font-size:11px;min-width:560px}
.hm th{font-weight:500;color:var(--mu);font-size:10px;padding:5px 4px;text-align:center;white-space:nowrap}
.hm th.cap{text-align:right;color:#586173;font-weight:600;padding-right:10px}
.hm th.lg{color:#586173;font-weight:600;font-size:10.5px;padding-top:10px}
.hm td{padding:0;text-align:center}
.hm td i{display:block;padding:9px 6px;font-style:normal;font-weight:500}
.hm .h0{background:var(--h0)}.hm .h1{background:var(--h1)}.hm .h2{background:var(--h2)}
.hm .h3{background:var(--h3);color:#fff}.hm .h4{background:var(--h4);color:#fff}
.hm td.gov i{box-shadow:inset 0 0 0 2px #C62828}
.hmkey{display:flex;align-items:center;gap:3px;margin-top:8px;font-size:10.5px;color:var(--mu)}
.hmkey b{font-weight:600;margin-right:6px}
.hmkey u{width:24px;height:11px;display:inline-block;text-decoration:none;border-radius:2px}
.gov{display:grid;grid-template-columns:auto 1fr;gap:3px 16px;align-items:center;margin-top:16px;
  background:var(--su);border:1px solid var(--bd);border-left:3px solid #C62828;border-radius:3px;padding:12px 14px;max-width:820px}
.gov .pill{grid-row:1 / span 2;align-self:center;font-size:10px;font-weight:700;letter-spacing:.05em;text-transform:uppercase;color:#C62828;border:1px solid #C62828;border-radius:9px;padding:4px 10px}
.gov .l1{font:600 14px "Segoe UI",sans-serif}.gov .l2{font-size:11.5px;color:#586173}
.meters{display:flex;flex-direction:column;gap:8px;margin-top:16px;max-width:820px}
.meter{display:grid;grid-template-columns:84px 1fr 130px;gap:10px;align-items:center;font-size:11px}
.meter .mc{font-weight:600;color:#586173}
.meter .trk{position:relative;height:16px;background:var(--su);border:1px solid var(--bd);border-radius:3px;overflow:hidden}
.meter .fil{position:absolute;left:0;top:0;bottom:0}
.meter .tk{position:absolute;top:-2px;bottom:-2px;width:2px;background:#586173;opacity:.6}
.meter .mv{text-align:right;color:#586173}.meter .mv b{color:var(--tx)}
.util.ok{color:#2E7D32}.util.warn{color:#B26A00}.util.bad{color:#C62828}
/* shared blocks */
details.block{margin:10px 12px;border:1px solid var(--bd);border-radius:3px}
details.block>summary{padding:7px 9px;cursor:pointer;color:var(--acc);font-weight:600;background:var(--su)}
.body{border-top:1px solid var(--bd);padding:8px 10px}
.tablewrap{max-height:340px;overflow:auto}
table{border-collapse:collapse;width:100%;font-size:10.5px}
th{position:sticky;top:0;background:#eef1f4;padding:6px;border-bottom:1px solid var(--bd);text-align:left}
td{padding:5px 6px;border-bottom:1px solid #eee;white-space:nowrap}
.num{text-align:right}.missing{color:#b42318;font-weight:600}
#warns{padding:6px 14px;font-size:11px;color:#8a6d00}#warns .e{color:#C62828}
a{color:var(--acc)}
""";

    private const string Script = """
const D = JSON.parse(document.getElementById("report-data").textContent);
const COL = ["#1f77b4","#d62728","#2ca02c","#ff7f0e","#9467bd","#8c564b","#17becf","#bcbd22"];
let load = D.loads.length ? D.loads[Math.min(1, D.loads.length - 1)].id : "", plot = "h", vmode = "kn";
const hidden = new Set(); let pinned = null, lastX = null;

// design capacity per cable [kN]; empty when none configured
const CAP = D.capacities || {};
const CAPS_ANY = Object.keys(CAP).length > 0;
const utOn = () => vmode === "ut";
// a value in the current mode: kN as-is, or % of capacity (null when the cable has no capacity in ut mode)
const tv = (f, cable) => { if (f == null) return null; if (utOn()) return CAP[cable] ? f / CAP[cable] * 100 : null; return f; };
const unit = cable => (utOn() && CAP[cable] ? "%" : "kN");
// governing utilisation for one cable = worst force over every candidate / its capacity
function govUtil(cable){ if (!CAP[cable]) return null;
  let mx = 0; D.rows.forEach(r => { const f = r.forces[cable]; if (f != null && f > mx) mx = f; });
  return mx / CAP[cable] * 100; }
function yDom(){
  if (!utOn()) return [D.yMin, D.yMax];
  let mx = 0;
  D.rows.forEach(r => D.cables.forEach(c => { const f = r.forces[c]; if (f != null && CAP[c]) mx = Math.max(mx, f / CAP[c] * 100); }));
  return [0, Math.max(110, Math.ceil(mx / 10) * 10 + 5)];
}
function yInfo(){
  const el = document.getElementById("yinfo");
  if (!D.rows.length){ el.textContent = "no cable-force results"; return; }
  const [a, b] = yDom();
  el.textContent = utOn()
    ? "Shared Y-scale: 0 → " + Math.round(b) + " % of capacity"
    : "Shared Y-scale: " + Math.round(a) + " → " + Math.round(b) + " kN";
}
document.getElementById("mixnote").hidden = !D.pretensions.some(p => p.mixed);

const ls = document.getElementById("load");
D.loads.forEach(x => { const o = document.createElement("option"); o.value = x.id;
  o.textContent = x.label; if (x.id === load) o.selected = true; ls.appendChild(o); });

function defs(){ return plot === "h"
  ? D.pretensions.map((x,i) => ({ key:x.id, label:x.label, color:COL[i % COL.length] }))
  : D.heights.map((x,i) => ({ key:x.id, label:x.label, color:COL[i % COL.length] })); }
function xvals(){ return plot === "h"
  ? D.heights.map(x => ({ key:x.id, label:(+x.value).toFixed(1), v:x.value }))
  : D.pretensions.map((x,i) => ({ key:x.id, label:x.id, v:i })); }
function cand(series, xkey){ return plot === "h"
  ? D.rows.find(r => r.loadId === load && r.pretId === series && r.heightId === xkey)
  : D.rows.find(r => r.loadId === load && r.heightId === series && r.pretId === xkey); }

function leg(){
  const b = document.getElementById("legend"); b.innerHTML = "";
  defs().forEach(sv => { const d = document.createElement("div");
    d.className = "li" + (hidden.has(sv.key) ? " off" : "");
    d.innerHTML = `<span class="sw" style="border-top-color:${sv.color}"></span>${esc(sv.label)}`;
    d.onclick = () => { hidden.has(sv.key) ? hidden.delete(sv.key) : hidden.add(sv.key); all(); };
    b.appendChild(d); });
}
function pinText(){
  const p = document.getElementById("pin");
  if (!pinned) { p.className = ""; p.textContent = "No pinned candidate"; return; }
  const r = D.rows.find(x => x.candidateId === pinned);
  p.className = "on"; p.textContent = "Pinned: " + r.candidateId + " · " + r.iterationKey;
}
const esc = t => String(t).replace(/[<&>"]/g, c => ({ "<":"&lt;", "&":"&amp;", ">":"&gt;", '"':"&quot;" }[c]));

function draw(card, cable){
  const svg = card.querySelector("svg"), tip = card.querySelector(".tip");
  const W = Math.max(520, svg.getBoundingClientRect().width || 560), H = 300, m = { l:56, r:14, t:14, b:42 };
  const pw = W - m.l - m.r, ph = H - m.t - m.b;
  const xv = xvals(), xmin = Math.min(...xv.map(z => z.v)), xmax = Math.max(...xv.map(z => z.v));
  const X = v => m.l + (xmax === xmin ? .5 : (v - xmin) / (xmax - xmin)) * pw;
  const [yLo, yHi] = yDom();
  const Y = v => m.t + (yHi - v) / (yHi - yLo) * ph;
  const P = [];
  for (let i = 0; i <= 5; i++){ const yy = m.t + i / 5 * ph, val = yHi - i / 5 * (yHi - yLo);
    P.push(`<line x1="${m.l}" y1="${yy}" x2="${W - m.r}" y2="${yy}" class="grid"/>`);
    P.push(`<text x="${m.l - 7}" y="${yy + 3}" text-anchor="end" class="tick">${Math.round(val)}</text>`); }
  if (utOn() && 100 <= yHi && 100 >= yLo)
    P.push(`<line x1="${m.l}" y1="${Y(100)}" x2="${W - m.r}" y2="${Y(100)}" class="ref100"/>`);
  xv.forEach(z => { const xx = X(z.v);
    P.push(`<line x1="${xx}" y1="${m.t}" x2="${xx}" y2="${H - m.b}" class="grid"/>`);
    P.push(`<text x="${xx}" y="${H - m.b + 16}" text-anchor="middle" class="tick">${esc(z.label)}</text>`); });
  P.push(`<line x1="${m.l}" y1="${H - m.b}" x2="${W - m.r}" y2="${H - m.b}" class="axis"/>`);
  P.push(`<line x1="${m.l}" y1="${m.t}" x2="${m.l}" y2="${H - m.b}" class="axis"/>`);
  P.push(`<text x="${m.l + pw / 2}" y="${H - 7}" text-anchor="middle" class="al">${plot === "h" ? "Truss Height (m)" : "Pretension Candidate"}</text>`);
  P.push(`<text x="13" y="${m.t + ph / 2}" text-anchor="middle" class="al" transform="rotate(-90 13 ${m.t + ph / 2})">${utOn() ? "Utilisation (% of capacity)" : "Cable Force (kN)"}</text>`);

  defs().forEach(sv => {
    if (hidden.has(sv.key)) return;
    let seg = [];
    const flush = () => { if (seg.length > 1)
      P.push(`<path d="${seg.map((p, i) => `${i ? "L" : "M"} ${X(p.x)} ${Y(p.f)}`).join(" ")}" fill="none" stroke="${sv.color}" stroke-width="1.8"/>`);
      seg = []; };
    xv.forEach(z => { const r = cand(sv.key, z.key), v = tv(r && r.forces[cable], cable);
      if (v == null) { flush(); return; }
      seg.push({ x:z.v, f:v });
      P.push(`<circle cx="${X(z.v)}" cy="${Y(v)}" r="2.6" fill="#fff" stroke="${sv.color}" stroke-width="1.4"/>`); });
    flush();
  });

  if (pinned){
    const r = D.rows.find(q => q.candidateId === pinned);
    if (r && r.loadId === load){
      const xk = plot === "h" ? r.heightId : r.pretId, z = xv.find(q => q.key === xk), v = tv(r.forces[cable], cable);
      if (z && v != null){
        P.push(`<line x1="${X(z.v)}" y1="${m.t}" x2="${X(z.v)}" y2="${H - m.b}" class="pinline"/>`);
        P.push(`<circle cx="${X(z.v)}" cy="${Y(v)}" r="5" fill="#fff" stroke="#1F4788" stroke-width="2.2"/>`);
      }
    }
  }
  P.push(`<line id="cx" x1="${m.l}" y1="${m.t}" x2="${m.l}" y2="${H - m.b}" class="cross" visibility="hidden"/>`);
  P.push(`<rect id="hov" x="${m.l}" y="${m.t}" width="${pw}" height="${ph}" fill="transparent" style="cursor:crosshair"/>`);
  svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
  svg.innerHTML = P.join("");

  const cross = svg.querySelector("#cx"), hov = svg.querySelector("#hov");
  card.show = xkey => {
    const z = xv.find(q => q.key === xkey); if (!z) return;
    cross.setAttribute("x1", X(z.v)); cross.setAttribute("x2", X(z.v)); cross.setAttribute("visibility", "visible");
    let h = `<b>${esc(cable)}</b><br><span style="color:#cbd5e1">${plot === "h" ? "Height" : "Pretension"} = ${esc(z.label)}</span>`;
    defs().filter(sv => !hidden.has(sv.key)).forEach(sv => {
      const r = cand(sv.key, xkey), f = r && r.forces[cable], v = tv(f, cable);
      if (r) h += `<div style="display:grid;grid-template-columns:1fr auto;gap:8px"><span>${esc(sv.key)} &middot; ${esc(r.candidateId)}</span><b>${v == null ? (f == null ? "n/a" : "no capacity") : (+v).toFixed(1) + " " + unit(cable)}</b></div>`;
    });
    tip.innerHTML = h; tip.style.display = "block"; tip.style.left = "12px"; tip.style.top = "12px";
  };
  card.hide = () => { cross.setAttribute("visibility", "hidden"); tip.style.display = "none"; };
  hov.onmousemove = e => { const rr = svg.getBoundingClientRect(), sx = (e.clientX - rr.left) * (W / rr.width);
    let best = xv[0], bd = 1e9;
    xv.forEach(z => { const d = Math.abs(X(z.v) - sx); if (d < bd){ bd = d; best = z; } });
    lastX = best.key; sync(best.key); };
  hov.onmouseleave = () => sync(null);
  hov.onclick = () => { if (!lastX) return;
    const sv = defs().find(s => !hidden.has(s.key)), r = sv && cand(sv.key, lastX);
    if (r){ pinned = r.candidateId; all(); } };
}

function sync(xkey){ document.querySelectorAll(".card").forEach(c => xkey ? c.show && c.show(xkey) : c.hide && c.hide()); }
function charts(){
  const b = document.getElementById("charts"); b.innerHTML = "";
  if (!D.rows.length){ b.innerHTML = '<p style="padding:20px;color:#666">No cable-force results in this study.</p>'; return; }
  D.cables.forEach((c, i) => { const x = document.createElement("section"); x.className = "card";
    const gu = govUtil(c);
    const chip = gu != null
      ? `<span class="util ${gu >= 100 ? "bad" : gu >= 85 ? "warn" : "ok"}">gov ${gu.toFixed(0)}% cap</span>`
      : (CAPS_ANY ? '<span class="capnote">no capacity set</span>' : '');
    x.innerHTML = `<div class="ch"><span class="ct">${String(i + 1).padStart(2, "0")} · ${esc(c)}${chip}</span>`
      + `<span class="cs">series = ${plot === "h" ? "Pretension" : "Height"} · shared Y-scale</span></div>`
      + `<div class="stage"><svg></svg><div class="tip"></div></div>`;
    b.appendChild(x); draw(x, c); });
}
function all(){ leg(); charts(); pinText(); yInfo(); }

document.getElementById("lk").innerHTML = D.loads.map(x => `<div class="kr"><b>${esc(x.id)}</b><span>${esc(x.label.replace(x.id + " · ", ""))}</span></div>`).join("");
document.getElementById("hk").innerHTML = D.heights.map(x => `<div class="kr"><b>${esc(x.id)}</b><span>${(+x.value).toFixed(2)} m</span></div>`).join("");
document.getElementById("pk").innerHTML = D.pretensions.map(x => `<div class="kr"><b>${esc(x.id)}</b><span>${x.mixed ? "mixed per cable" : (x.value != null ? (+x.value).toFixed(0) + " kN" : "—")}</span></div>`).join("");
document.getElementById("tb").innerHTML = D.rows.flatMap(r => D.cables.map(c => {
  const f = r.forces[c], t = r.targets[c], cap = CAP[c];
  return `<tr><td>${r.run}</td><td>${esc(r.candidateId)}</td><td>${esc(r.iterationKey)}</td><td>${esc(r.loadId)}</td>`
    + `<td>${esc(r.heightId)} · ${(+r.heightM).toFixed(2)}</td><td>${esc(r.pretLabel)}</td><td>${esc(c)}</td>`
    + `<td class="num">${t == null ? "—" : (+t).toFixed(0)}</td>`
    + `<td class="num ${f == null ? "missing" : ""}">${f == null ? "Result unavailable" : (+f).toFixed(1)}</td>`
    + `<td class="num">${cap && f != null ? (f / cap * 100).toFixed(0) + "%" : "—"}</td></tr>`;
})).join("");

const wb = document.getElementById("warns");
wb.innerHTML = D.warnings.length
  ? "<strong>Warnings:</strong> " + D.warnings.slice(0, 40).map(w => `<span class="${w.isError ? "e" : ""}">[${esc(w.iterationKey || "study")}] ${esc(w.message)}</span>`).join(" · ")
    + (D.warnings.length > 40 ? ` … (+${D.warnings.length - 40})` : "")
  : "";

const vkBtn = document.getElementById("vk"), vuBtn = document.getElementById("vu");
if (!CAPS_ANY){ vuBtn.disabled = true; vuBtn.title = "Set a design capacity on the Pretension tab to read utilisation"; }
function setVMode(mode){ vmode = mode;
  vkBtn.classList.toggle("active", mode === "kn"); vuBtn.classList.toggle("active", mode === "ut"); all(); }
vkBtn.onclick = () => setVMode("kn");
vuBtn.onclick = () => { if (!vuBtn.disabled) setVMode("ut"); };

ls.onchange = e => { load = e.target.value; pinned = null; all(); };
document.getElementById("bh").onclick = () => { plot = "h";
  document.getElementById("bh").classList.add("active"); document.getElementById("bp").classList.remove("active"); all(); };
document.getElementById("bp").onclick = () => { plot = "p";
  document.getElementById("bp").classList.add("active"); document.getElementById("bh").classList.remove("active"); all(); };
document.getElementById("clear").onclick = () => { pinned = null; all(); };
document.getElementById("reset").onclick = () => { hidden.clear(); pinned = null; plot = "h"; setVMode("kn");
  document.getElementById("bh").classList.add("active"); document.getElementById("bp").classList.remove("active"); all(); };
window.addEventListener("resize", charts);
all();
""";

    // Narrative report introduced after the original chart viewer. Keep this script self-contained so the
    // generated HTML remains portable and can be regenerated from the workbook without external assets.
    private const string ScriptV2 = """
const D = JSON.parse(document.getElementById("report-data").textContent);
const CAP = D.capacities || {};
const CAPS_ANY = Object.keys(CAP).length > 0;
let vmode = "kn", selectedCable = D.cables[0] || "";
const el = id => document.getElementById(id);
const esc = t => String(t == null ? "" : t).replace(/[<&>"]/g, c => ({"<":"&lt;","&":"&amp;",">":"&gt;",'"':"&quot;"}[c]));
const finite = v => typeof v === "number" && Number.isFinite(v);
const utOn = () => vmode === "ut";
const value = (row, cable) => {
  const force = row && row.forces ? row.forces[cable] : null;
  if (!finite(force)) return null;
  return utOn() ? (finite(CAP[cable]) && CAP[cable] > 0 ? force / CAP[cable] * 100 : null) : force;
};
const unit = () => utOn() ? "%" : "kN";
const fmt = v => finite(v) ? v.toFixed(1) : "n/a";
const mean = xs => xs.length ? xs.reduce((a,b) => a+b, 0) / xs.length : null;
const median = xs => {
  if (!xs.length) return null;
  const s = [...xs].sort((a,b) => a-b), m = Math.floor(s.length/2);
  return s.length%2 ? s[m] : (s[m-1]+s[m])/2;
};
function values(cable, rows=D.rows){ return rows.map(r => value(r,cable)).filter(finite); }
function yDom(){
  if (!utOn()) return [D.yMin,D.yMax];
  const all=D.rows.flatMap(r => D.cables.map(c => value(r,c))).filter(finite);
  const mx=all.length ? Math.max(...all) : 0;
  return [0,Math.max(110,Math.ceil(mx/10)*10+5)];
}
function yInfo(){
  const info=el("yinfo");
  if (!D.rows.length){ info.textContent="No cable-force results"; return; }
  const [a,b]=yDom();
  info.textContent=utOn()?`Shared Y-scale: 0 → ${Math.round(b)} % of capacity`:`Shared Y-scale: ${Math.round(a)} → ${Math.round(b)} kN`;
}

function factorStats(cable){
  return [
    {label:"Loading",key:"loadId",levels:D.loads},
    {label:"Pretension",key:"pretId",levels:D.pretensions},
    {label:"Truss height",key:"heightId",levels:D.heights}
  ].map(factor => {
    const groups=factor.levels.map(level => ({level,average:mean(values(cable,D.rows.filter(r => r[factor.key]===level.id)))})).filter(x => finite(x.average));
    if (!groups.length) return {...factor,spread:null,low:null,high:null};
    const low=groups.reduce((a,b) => a.average<=b.average?a:b), high=groups.reduce((a,b) => a.average>=b.average?a:b);
    return {...factor,spread:high.average-low.average,low,high};
  });
}
function drawDrivers(){
  const stats=factorStats(selectedCable), valid=stats.filter(x => finite(x.spread));
  const maxSpread=valid.length?Math.max(...valid.map(x => x.spread),1e-9):1;
  el("tornlead").innerHTML=selectedCable
    ? `Sensitivity for <b>${esc(selectedCable)}</b>, measured as the spread between factor-level mean ${utOn()?"utilisations":"forces"}. Larger bars have more influence in this study grid.`
    : "No cable-force results are available.";
  if (!valid.length){ el("tornado").innerHTML='<p class="lead">Not enough valid results for sensitivity analysis.</p>'; el("tornread").innerHTML=""; return; }
  const W=620,H=34+valid.length*38,left=102,right=72,width=W-left-right;
  const svg=[`<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Cable-force factor sensitivity">`];
  valid.forEach((s,i) => {
    const y=18+i*38,w=s.spread/maxSpread*width;
    svg.push(`<text x="${left-8}" y="${y+12}" text-anchor="end" class="al">${esc(s.label)}</text>`);
    svg.push(`<rect x="${left}" y="${y}" width="${width}" height="18" fill="#F3F4F6" rx="2"/>`);
    svg.push(`<rect x="${left}" y="${y}" width="${w}" height="18" class="tbar" rx="2"/>`);
    svg.push(`<text x="${left+w+7}" y="${y+13}" class="tick">Δ ${fmt(s.spread)} ${unit()}</text>`);
  });
  svg.push("</svg>"); el("tornado").innerHTML=svg.join("");
  el("tornread").innerHTML=valid.map(s => `<div class="readrow"><span class="lab">${esc(s.label)}</span><span class="big">Δ ${fmt(s.spread)} ${unit()}</span>`
    + `<span class="note">low: ${esc(s.low.level.label)}<br>high: ${esc(s.high.level.label)}</span><span class="mini"><i style="width:${Math.max(1,s.spread/maxSpread*100)}%"></i></span></div>`).join("");
}

function series(cable,loadId){
  return D.heights.map(h => {
    const rows=D.rows.filter(r => r.loadId===loadId && r.heightId===h.id), xs=values(cable,rows);
    return xs.length?{x:+h.value,label:h.id,lo:Math.min(...xs),mid:median(xs),hi:Math.max(...xs)}:null;
  }).filter(Boolean);
}
function drawPanel(panel,cable,loadId){
  const points=series(cable,loadId),svg=panel.querySelector("svg");
  if (!points.length){ panel.classList.add("empty"); panel.textContent=utOn()&&!CAP[cable]?"No design capacity":"No valid results"; return; }
  const W=260,H=132,m={l:39,r:8,t:8,b:25},pw=W-m.l-m.r,ph=H-m.t-m.b;
  const xmin=Math.min(...points.map(p=>p.x)),xmax=Math.max(...points.map(p=>p.x)),[ylo,yhi]=yDom();
  const X=x=>m.l+(xmax===xmin ? .5 : (x-xmin)/(xmax-xmin))*pw,Y=y=>m.t+(yhi-y)/(yhi-ylo)*ph,p=[];
  for(let i=0;i<=3;i++){const y=m.t+i/3*ph,v=yhi-i/3*(yhi-ylo);p.push(`<line x1="${m.l}" y1="${y}" x2="${W-m.r}" y2="${y}" class="grid"/><text x="${m.l-5}" y="${y+3}" text-anchor="end" class="tick">${Math.round(v)}</text>`);}
  if(utOn()&&ylo<=100&&yhi>=100)p.push(`<line x1="${m.l}" y1="${Y(100)}" x2="${W-m.r}" y2="${Y(100)}" class="ref100"/>`);
  if(points.length>1){
    const band=points.map(q=>`${X(q.x)},${Y(q.hi)}`).concat([...points].reverse().map(q=>`${X(q.x)},${Y(q.lo)}`)).join(" ");
    p.push(`<polygon points="${band}" class="sband"/><path d="${points.map((q,i)=>`${i?"L":"M"} ${X(q.x)} ${Y(q.mid)}`).join(" ")}" class="sline"/>`);
  }
  points.forEach(q=>{p.push(`<line x1="${X(q.x)}" y1="${Y(q.lo)}" x2="${X(q.x)}" y2="${Y(q.hi)}" stroke="#8fbce9"/><circle cx="${X(q.x)}" cy="${Y(q.mid)}" r="2.5" class="sdot"/><text x="${X(q.x)}" y="${H-8}" text-anchor="middle" class="tick">${esc(q.label)}</text>`);});
  svg.setAttribute("viewBox",`0 0 ${W} ${H}`);svg.setAttribute("role","img");svg.setAttribute("aria-label",`${cable}, ${loadId}: force by truss height`);svg.innerHTML=p.join("");
}
function drawTrellis(){
  const grid=el("trellis");
  if(!D.rows.length||!D.cables.length||!D.loads.length){grid.innerHTML='<p class="lead" style="padding:10px">No cable-force results are available.</p>';return;}
  grid.style.gridTemplateColumns=`84px repeat(${D.cables.length},minmax(220px,1fr))`;
  grid.innerHTML='<div class="thc"></div>'+D.cables.map(c=>`<div class="th">${esc(c)}</div>`).join("");
  D.loads.forEach((load,li)=>{
    const last=li===D.loads.length-1?" trellis-lastrow":"";
    grid.insertAdjacentHTML("beforeend",`<div class="rl${last}"><b>${esc(load.id)}</b><span>${esc(load.label.replace(load.id+" · ",""))}</span></div>`);
    D.cables.forEach(c=>{const panel=document.createElement("div");panel.className="pan"+last;panel.innerHTML="<svg></svg>";grid.appendChild(panel);drawPanel(panel,c,load.id);});
  });
  el("trlegend").innerHTML='<span class="ln"></span> median across pretension candidates <span class="bd"></span> min–max band <span>· X = truss-height candidate</span>'+(utOn()?'<span>· red dashed = 100% capacity</span>':'');
}

function governingRows(){
  return D.cables.map(cable=>{let best=null;D.rows.forEach(row=>{const v=value(row,cable);if(finite(v)&&(!best||v>best.v))best={cable,row,v};});return best;}).filter(Boolean);
}
function heatClass(v,max){if(!finite(v)||max<=0)return"h0";const q=v/max;return q>.8?"h4":q>.6?"h3":q>.4?"h2":q>.2?"h1":"h0";}
function drawGoverning(){
  const perCable=governingRows(),overall=perCable.length?perCable.reduce((a,b)=>a.v>=b.v?a:b):null,allCells=[];
  D.cables.forEach(c=>D.loads.forEach(l=>{let best=null;D.rows.filter(r=>r.loadId===l.id).forEach(row=>{const v=value(row,c);if(finite(v)&&(!best||v>best.v))best={cable:c,row,v};});if(best)allCells.push(best);}));
  const mx=allCells.length?Math.max(...allCells.map(x=>x.v)):1;
  el("heatmap").innerHTML='<thead><tr><th class="cap">Cable</th>'+D.loads.map(l=>`<th>${esc(l.id)}</th>`).join("")+'</tr></thead><tbody>'
    +D.cables.map(c=>'<tr><th class="cap">'+esc(c)+'</th>'+D.loads.map(l=>{const cell=allCells.find(x=>x.cable===c&&x.row.loadId===l.id);if(!cell)return'<td><i class="h0">n/a</i></td>';const gov=overall&&cell.row.candidateId===overall.row.candidateId&&c===overall.cable?' gov':'';return`<td class="${gov.trim()}" title="${esc(cell.row.iterationKey)}"><i class="${heatClass(cell.v,mx)}">${fmt(cell.v)}</i></td>`;}).join("")+'</tr>').join("")+'</tbody>';
  el("hmlead").innerHTML=utOn()?'Each cell is the worst utilisation for one cable × loading candidate. The outlined cell governs; 100% means design capacity is reached.':'Each cell is the maximum cable force for one cable × loading candidate. Colour is relative to the study maximum; the outlined cell governs.';
  el("hmkey").innerHTML='<b>low</b><u style="background:var(--h0)"></u><u style="background:var(--h1)"></u><u style="background:var(--h2)"></u><u style="background:var(--h3)"></u><u style="background:var(--h4)"></u><b>high</b>';
  const gov=el("gov");
  if(!overall){gov.hidden=true;gov.innerHTML="";}else{const r=overall.row,force=r.forces[overall.cable],util=CAP[overall.cable]?force/CAP[overall.cable]*100:null;gov.hidden=false;gov.innerHTML=`<span class="pill">Governing</span><span class="l1">${esc(overall.cable)} · ${fmt(overall.v)} ${unit()}</span><span class="l2">${esc(r.iterationKey)} · load ${esc(r.loadId)} · height ${esc(r.heightId)} (${(+r.heightM).toFixed(2)} m) · pretension ${esc(r.pretId)}`+(finite(util)?` · ${util.toFixed(1)}% capacity`:"")+"</span>";}
  const meterMax=Math.max(...perCable.map(x=>x.v),utOn()?110:1);
  el("meters").innerHTML=perCable.map(x=>{const pct=Math.min(100,x.v/meterMax*100),cls=utOn()?(x.v>=100?"bad":x.v>=85?"warn":"ok"):"ok",color=cls==="bad"?"#C62828":cls==="warn"?"#F57C00":"#2E7D32";return`<div class="meter"><span class="mc">${esc(x.cable)}</span><span class="trk"><i class="fil" style="width:${pct}%;background:${color}"></i>${utOn()?'<i class="tk" style="left:'+Math.min(100,100/meterMax*100)+'%"></i>':''}</span><span class="mv"><b class="util ${cls}">${fmt(x.v)} ${unit()}</b> · ${esc(x.row.candidateId)}</span></div>`;}).join("");
}

function drawTableAndWarnings(){
  el("tb").innerHTML=D.rows.flatMap(r=>D.cables.map(c=>{const f=r.forces[c],t=r.targets[c],cap=CAP[c];return`<tr><td>${r.run}</td><td>${esc(r.candidateId)}</td><td>${esc(r.iterationKey)}</td><td>${esc(r.loadId)}</td><td>${esc(r.heightId)} · ${(+r.heightM).toFixed(2)}</td><td>${esc(r.pretLabel)}</td><td>${esc(c)}</td><td class="num">${t==null?"—":(+t).toFixed(0)}</td><td class="num ${f==null?"missing":""}">${f==null?"Result unavailable":(+f).toFixed(1)}</td><td class="num">${cap&&f!=null?(f/cap*100).toFixed(0)+"%":"—"}</td></tr>`;})).join("");
  el("warns").innerHTML=D.warnings.length?"<strong>Warnings:</strong> "+D.warnings.slice(0,40).map(w=>`<span class="${w.isError?"e":""}">[${esc(w.iterationKey||"study")}] ${esc(w.message)}</span>`).join(" · ")+(D.warnings.length>40?` … (+${D.warnings.length-40})`:""):"";
}
function render(){yInfo();drawDrivers();drawTrellis();drawGoverning();}
const cableSelect=el("govcable");
D.cables.forEach(c=>{const o=document.createElement("option");o.value=c;o.textContent=c;cableSelect.appendChild(o);});
cableSelect.disabled=!D.cables.length;cableSelect.onchange=e=>{selectedCable=e.target.value;drawDrivers();};
const vkBtn=el("vk"),vuBtn=el("vu");
if(!CAPS_ANY){vuBtn.disabled=true;vuBtn.title="Set a design capacity on the Pretension tab to read utilisation";}
function setVMode(mode){vmode=mode;vkBtn.classList.toggle("active",mode==="kn");vuBtn.classList.toggle("active",mode==="ut");render();}
vkBtn.onclick=()=>setVMode("kn");vuBtn.onclick=()=>{if(!vuBtn.disabled)setVMode("ut");};
window.addEventListener("resize",drawTrellis);
drawTableAndWarnings();render();
""";
}
