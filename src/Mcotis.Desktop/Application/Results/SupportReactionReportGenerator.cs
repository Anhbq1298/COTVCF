using System.IO;
using System.Text.Json;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Domain.Results;
using Mcotis.Desktop.Infrastructure;

namespace Mcotis.Desktop.Application.Results;

/// <summary>Self-contained comparison report for the Left and Right foundation points selected in Output Settings.
/// Opens on a Summary — per foundation side the inner support, the outer support and their net, Fz and Fx — with
/// a By Candidate drill-down; the range-band and matrix views of every component stay under View.</summary>
public sealed class SupportReactionReportGenerator
{
    public const string ReportFileName = "Support_Reactions_Report.html";
    /// <summary>Report name printed in the page header.</summary>
    public const string ReportTitle = "Support Reactions";
    public const int IndividualProfileLimit = 50;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Generate(string studyFolder, IReadOnlyList<CandidateResultDto> results,
        string baselineName, IterationStudyMode studyMode, IReadOnlyList<string>? siblingReports = null,
        ReportPrintSettings? print = null, ReportScope? scope = null)
    {
        Directory.CreateDirectory(studyFolder);
        var path = Path.Combine(studyFolder, ReportFileName);
        var siblings = siblingReports ?? ReportNav.PresentIn(studyFolder, "support").ToArray();
        AtomicFile.WriteAllText(path, BuildHtml(Build(results, baselineName, studyMode, siblings, scope),
                ReportPrintEngine.Describe(print, scope?.Title(ReportTitle) ?? ReportTitle, baselineName, DateTimeOffset.Now)),
            new System.Text.UTF8Encoding(false));
        return path;
    }

    public string RegenerateFromWorkbook(string studyFolder)
    {
        var workbook = Path.Combine(studyFolder, ConsolidatedResultWriter.WorkbookFileName);
        if (!File.Exists(workbook))
            throw new FileNotFoundException($"No {ConsolidatedResultWriter.WorkbookFileName} to regenerate from.", workbook);
        var results = ConsolidatedResultWriter.Read(workbook);
        if (!results.Any(result => (result.SupportReactionGroups?.Count ?? 0) > 0)) return "";
        return Generate(studyFolder, results, results.FirstOrDefault()?.BaselineModelName ?? "",
            results.FirstOrDefault()?.StudyMode ?? IterationStudyMode.Combined);
    }

    public SupportReactionReportDto Build(IReadOnlyList<CandidateResultDto> results, string baselineName,
        IterationStudyMode studyMode, IReadOnlyList<string>? siblingReports = null, ReportScope? scope = null)
    {
        var keys = results.SelectMany(result => result.SupportReactionGroups ?? [])
            .Select(group => (group.FoundationSide, group.SapGroupName, group.OutputCase)).Distinct()
            .OrderBy(key => key.FoundationSide.Equals("Left", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(key => key.FoundationSide, StringComparer.Ordinal);
        var groups = keys.Select(key => new SupportReactionGroupReportDto(
            key.FoundationSide, key.SapGroupName, key.OutputCase,
            results.Select(result => (Result: result, Group: (result.SupportReactionGroups ?? []).FirstOrDefault(group =>
                    group.FoundationSide == key.FoundationSide && group.OutputCase == key.OutputCase)))
                .Where(item => item.Group is not null)
                .OrderBy(item => item.Result.Parameters.RunIndex)
                .Select(item =>
                {
                    var p = item.Result.Parameters;
                    var rows = item.Group!.Points.Select(row => new SupportReactionRowDto(
                        row.PointName, row.PointSequence, row.X, row.Y, row.Z, row.StepType, row.StepNumber,
                        row.Fx, row.Fy, row.Fz, row.Mx, row.My, row.Mz, row.Status.ToString())).ToArray();
                    return new SupportReactionCandidateDto(item.Result.IterationKey, p.RunIndex,
                        p.LoadingCandidateId, p.HeightCandidateId, p.PretensionCandidateId,
                        p.LoadingLabel, p.TrussHeightM, p.PretensionLabel, rows);
                }).ToArray())).ToArray();
        // A side whose two supports can be told apart also carries their inner / outer / net reading per iteration.
        var roles = SupportReactionSummary.ResolveRoles(groups);
        groups = groups.Select(group => roles.TryGetValue(group.FoundationSide, out var role)
            ? group with
            {
                Summary = new SupportReactionSummaryDto(role.Inner, role.Outer, group.Series.Select(candidate =>
                    SupportReactionSummary.Summarise(candidate.IterationKey, candidate.Rows, role.Inner, role.Outer)).ToArray())
            }
            : group).ToArray();
        var warnings = results.SelectMany(result => result.Warnings)
            .Where(warning => warning.Category.Equals("SupportReaction", StringComparison.OrdinalIgnoreCase))
            .Select(warning => new ResultReportWarningDto(warning.IterationKey, warning.Category,
                warning.Severity.ToString(), warning.Message)).ToArray();
        return new(new(baselineName, studyMode.ToString(), results.Count, DateTimeOffset.Now.ToString("o"),
            IndividualProfileLimit, scope?.ToDto()), groups, warnings, siblingReports ?? []);
    }

    public string BuildHtml(SupportReactionReportDto dto, ReportPrintDto? print = null)
    {
        print ??= ReportPrintEngine.Describe(null, ReportTitle, dto.Study.BaselineModelName, DateTimeOffset.Now);
        var data = JsonSerializer.Serialize(dto, Json).Replace("</", "<\\/", StringComparison.Ordinal);
        var library = ReportNav.Library("support", new HashSet<string>(dto.SiblingReports, StringComparer.Ordinal));
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Support Reactions — {{Esc(dto.Study.BaselineModelName)}}</title>
<style>{{ReportPrintEngine.Css}}{{Style}}{{ReportSummaryTheme.Css}}{{SummaryStyle}}{{ReportScope.Css}}</style>{{ReportPrintEngine.PageStyle}}</head><body><header><div data-pr="info"><h1>Support Reactions Results Viewer</h1>
<div>{{Esc(dto.Study.BaselineModelName)}} · {{dto.Study.CandidateCount}} candidate(s) · {{Esc(dto.Study.StudyMode)}}</div>
{{ReportScope.Banner(dto.Study.Scope)}}
<div class="print-only" data-print-view></div>
<p data-pr="note">Global support reactions at the two Left and two Right foundation points selected in Output Settings. Opens on the <b>Summary</b>: for one foundation side, the vertical (Fz) and horizontal (Fx) reaction of the inner support, the outer support and their algebraic sum Σ Net, per pretension candidate and truss height, in MN — with <b>View by Candidate</b> for both sides of one iteration. Under <b>View</b>, Individual supports / Foundation totals / Candidate matrix show every component (forces kN, moments kNm): each column's shaded band is the range among the candidates in scope (min to max), its darker core the middle half (P25–P75) and its line the median; <b>Band by</b> splits it per load, height or pretension candidate. Points are discrete supports; chart markers are not joined into a structural curve.</p></div>{{library}}</header>
<nav data-print-controls><label>Foundation Side <select id="group"><option value="all">Left + Right</option></select></label>
<label>Component <select id="component"><option>Fx</option><option>Fy</option><option selected>Fz</option><option>Mx</option><option>My</option><option>Mz</option></select></label>
<label>Result step <select id="step"></select></label><label>View <select id="view"><option value="summary">Summary</option><option value="supports">Individual supports</option><option value="totals">Foundation totals</option><option value="matrix">Candidate matrix</option></select></label>
<label>Band by <select id="band"><option value="">All candidates</option><option value="loadId">Load candidate</option><option value="heightId">Truss height</option><option value="pretensionId">Pretension</option></select></label>
<button id="reset">Reset filters</button>{{ReportPrintEngine.ToolbarButton}}<span id="scope"></span></nav><div id="filters" data-print-controls></div><div id="notice" data-pr="note" hidden></div><div id="warnings" data-pr="note"></div>
<main id="panels"></main><div id="tip" hidden></div>
<script type="application/json" id="data">{{data}}</script>
{{ReportPrintEngine.Scripts(print)}}
<script>{{Script}}</script></body></html>
""";
    }

    private static string Esc(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");

    private const string Style = """
:root{--acc:#1f4788;--bd:#c7ccd2;--su:#f3f4f6;--tx:#1a1a1a;--mu:#666;--neg:#c62828;--pos:#2e7d32}*{box-sizing:border-box}body{margin:0;font:13px/1.4 Segoe UI,Arial;color:var(--tx)}header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;padding:10px 14px;border-bottom:2px solid var(--acc)}h1{font-size:16px;margin:0}header>div>div,header p{margin:3px 0;color:var(--mu);font-size:11.5px;max-width:900px}.lib{font-size:12px}.lib summary{cursor:pointer;color:var(--acc);font-weight:600}.lib ul{list-style:none;margin:6px 0 0;padding:0;border:1px solid var(--bd);border-radius:3px;min-width:180px}.lib li{padding:4px 8px;border-bottom:1px solid var(--su);color:var(--mu)}.lib li.on{color:var(--tx)}.lib li span{float:right;font-size:10px;text-transform:uppercase;color:#9aa0a6}.lib li.on span{color:var(--pos)}nav,#filters{display:flex;gap:9px;align-items:center;padding:6px 14px;border-bottom:1px solid var(--bd);flex-wrap:wrap}select,button{font:12px Segoe UI,Arial;padding:3px 7px;border:1px solid #bbb;border-radius:3px;background:white;cursor:pointer}#scope{margin-left:auto;color:var(--mu)}.ms{position:relative}.ms>button{min-width:100px;text-align:left}.ms.active>button{color:var(--acc);border-color:var(--acc);font-weight:600}.pop{position:absolute;top:100%;left:0;z-index:30;margin-top:2px;min-width:240px;max-height:280px;overflow:auto;padding:4px 0;background:#fff;border:1px solid var(--bd);border-radius:3px;box-shadow:0 4px 14px #0002}.pop label{display:flex;align-items:center;gap:7px;padding:3px 10px;white-space:nowrap;font-size:11.5px;cursor:pointer}.pop label:hover{background:var(--su)}.pop .all{font-weight:600;border-bottom:1px solid var(--bd)}#notice{margin:7px 14px;padding:6px;background:#fff8e1;border-left:3px solid #f57c00}.panel{padding:8px 10px;border-bottom:1px solid #ccc}.title{color:var(--acc);font-weight:600}.sub{color:var(--mu);font-size:11px;margin-left:8px}.stage{display:grid;grid-template-columns:minmax(0,1fr) 220px}.chart{width:100%;height:350px}.legend{border-left:1px solid #ccc;padding:6px;max-height:350px;overflow:auto}.legend button{display:block;width:100%;text-align:left;border:0;font:11px Consolas}.legend button.on{background:#e8f0fe}.hit{cursor:crosshair}.matrix-wrap{overflow:auto;max-height:570px}.matrix{border-collapse:separate;border-spacing:1px;font:10px Consolas;white-space:nowrap}.matrix th{position:sticky;top:0;background:#eee;padding:4px}.matrix th:first-child,.matrix td:first-child{position:sticky;left:0;background:white;z-index:2}.matrix td{padding:3px 5px;text-align:right}.empty{padding:24px;color:var(--mu)}#warnings{padding:7px 14px;color:#8a6d00;font-size:11px}.legend h4{margin:8px 0 4px;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:var(--mu)}.legend h4:first-child{margin-top:0}.legend h4 span{text-transform:none;letter-spacing:0;font-weight:400;color:#9aa0a6}.legend .key{display:flex;align-items:center;gap:6px;font-size:11px;padding:2px 0}.legend .grp{font-size:11px;padding:2px 0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.legend .sw{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:6px;vertical-align:-1px}.legend .kb,.legend .kc,.legend .km{width:22px;flex:none}.legend .kb{height:12px;background:#1f478824;border:1px solid #1f47888c;border-radius:2px}.legend .kc{height:12px;background:#1f478852}.legend .km{height:0;border-top:2px solid #1f4788}.matrix tfoot td,.matrix tfoot td:first-child{background:#eef2f5;font-weight:700}#tip{position:fixed;pointer-events:none;z-index:50;max-width:320px;padding:6px 8px;border-radius:2px;background:#212121;color:#fff;font:11px/1.45 Consolas,monospace;white-space:pre-line;box-shadow:0 8px 16px #0003}[hidden]{display:none!important}
/* on a sheet of the Print Report screen: the sheet, header and footer come from the shared print engine; a chart keeps its key beside it */
.pr-sheet #notice{margin:0 0 6px}.pr-sheet #warnings{padding:0 0 6px}.pr-sheet .panel{padding:0 0 8px;border-bottom:0}.pr-sheet .stage{grid-template-columns:minmax(0,1fr) 190px}.pr-sheet .chart{width:100%;height:auto}
.pr-sheet .legend{max-height:none;overflow:visible;font-size:10px}.pr-sheet .legend h4 span{display:none}.pr-sheet .legend button{font-size:9px;padding:0 2px}
.pr-sheet .matrix th,.pr-sheet .matrix th:first-child,.pr-sheet .matrix td:first-child{position:static}.pr-sheet .matrix thead th{white-space:normal;overflow-wrap:anywhere}
""";

    // Summary / By Candidate on top of the shared ReportSummaryTheme: KPI cards, role-coloured bars, the schematic
    private const string SummaryStyle = """
.cnote{margin:0 0 8px;color:#475467;font-size:11.5px;max-width:1280px}
[data-cand]{cursor:pointer}.kpi[data-cand]:hover{background:#f6f7f9}
.czero{stroke:#344054;stroke-width:1.2}
.rv{fill:#333;font-size:9.5px;paint-order:stroke;stroke:#fff;stroke-width:2.5px;stroke-linejoin:round}.rvn{fill:#101828;font-weight:700}
.rr{fill:#344054;font-size:8.5px;font-weight:600}.rj{fill:#687386;font-size:8.5px}
.chh{fill:#1a1a1a;font-size:9px;font-weight:600}.ccap{fill:#687386;font-size:9.5px}
.sside{fill:#1f4788;font-size:13px;font-weight:600}.srole{font-size:12px;font-weight:700}.sjoint{fill:#475467;font-size:11px}
.sval{fill:#1a1a1a;font-size:11.5px;font-weight:600}.sbeh{fill:#475467;font-size:10px}.sbeh.up{fill:#b42318;font-weight:600}
.sbeam{stroke:#98a2b3;stroke-width:5;stroke-linecap:round}.spin{fill:#e4e7ec;stroke:#667085}.sground{stroke:#667085;stroke-width:1.5}.snode{fill:#fff;stroke:#344054;stroke-width:1.5}
.snet{fill:#1a1a1a;font-size:12px;font-weight:700}.snetv{fill:#1a1a1a;font-size:12px}
.ctable tr.net td{font-weight:700;border-top:1px solid #98a2b3;background:#f6f7f9}
#warnings:empty{display:none}
""";

    private const string Script = """
(()=>{const D=JSON.parse(document.getElementById('data').textContent),G=document.getElementById('group'),C=document.getElementById('component'),S=document.getElementById('step'),V=document.getElementById('view'),B=document.getElementById('band'),P=document.getElementById('panels'),N=document.getElementById('notice'),Q=document.getElementById('scope'),TIP=document.getElementById('tip');
// "#print&group=Left&component=Fx&view=totals&band=heightId" from the PDF exporter preselects the toolbar
const OPT=(window.reportPrint&&window.reportPrint.options)||{},pick=(select,value)=>{if(value!=null&&[...select.options].some(o=>o.value===value&&!o.disabled))select.value=value};
const chosen={loadId:new Set(),heightId:new Set(),pretensionId:new Set()};let hi=null;
const esc=x=>String(x??'').replace(/[&<>"']/g,m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[m])),fmt=x=>Math.abs(x)>=100?x.toFixed(0):Math.abs(x)>=1?x.toFixed(2):x.toFixed(3),prop=()=>C.value[0].toLowerCase()+C.value.slice(1),unit=()=>C.value[0]==='M'?'kNm':'kN',step=r=>`${r.stepType||'Static'} · ${Number(r.stepNumber).toLocaleString()}`;
// Band colours: the Deflection report's categorical order, keyed by the study-wide sorted id list, so an id keeps its
// colour whatever the filters show (L01 is the same blue in both reports). "All candidates" uses the accent blue.
const PALETTE=['#1f77b4','#d62728','#2ca02c','#ff7f0e','#9467bd','#8c564b','#17becf','#bcbd22','#e377c2','#7f7f7f','#393b79','#637939'],ACC='#1f4788',DIMS={loadId:'Load candidate',heightId:'Truss height',pretensionId:'Pretension'};
const ALL=D.groups.flatMap(g=>g.series),IDS={};Object.keys(DIMS).forEach(k=>IDS[k]=[...new Set(ALL.map(x=>x[k]))].sort((a,b)=>a.localeCompare(b,undefined,{numeric:true})));
// A report on a subset of the study (Tab 06A) keeps the full study's id order for its colours, and knows which
// iterations are outside the set, so a blank slot reads "not qualified" instead of "no result".
const SCOPE=D.study.scope||null,ORD=SCOPE?SCOPE.order:IDS,NQ=new Set(SCOPE?SCOPE.excluded.map(x=>x.join('|')):[]),nq=(l,h,p)=>NQ.has(l+'|'+h+'|'+p);
const detail=(k,v)=>{const s=ALL.find(x=>x[k]===v);return k==='loadId'?s.loadingLabel:k==='heightId'?`H ${(+s.trussHeightM).toFixed(3)} m`:s.pretensionLabel},colourOf=(k,v)=>k?PALETTE[ORD[k].indexOf(v)]||ACC:ACC;
Object.keys(DIMS).forEach(k=>{const o=[...B.options].find(o=>o.value===k),n=ORD[k].length;if(n>PALETTE.length){o.disabled=true;o.textContent+=` (${n} values · too many to colour)`}});
D.groups.forEach((g,i)=>G.add(new Option(`${g.foundationSide} · ${g.outputCase}`,i)));const gs=()=>G.value==='all'?D.groups:[D.groups[+G.value]],scope=g=>g.series.filter(x=>Object.keys(chosen).every(k=>!chosen[k].size||chosen[k].has(x[k]))),rows=x=>x.rows.filter(r=>r.status==='Valid'&&step(r)===S.value&&Number.isFinite(r[prop()]));
function filters(){const root=document.getElementById('filters'),labels={loadId:'Loading',heightId:'Height',pretensionId:'Pretension'};Object.keys(labels).forEach(k=>{const vals=IDS[k],w=document.createElement('span'),b=document.createElement('button'),pop=document.createElement('div');w.className='ms';pop.className='pop';pop.hidden=true;const all=document.createElement('label');all.className='all';all.innerHTML='<input type="checkbox" checked> All';pop.append(all);const boxes=[];vals.forEach(v=>{const row=document.createElement('label'),cb=document.createElement('input');cb.type='checkbox';cb.value=v;row.append(cb,document.createTextNode(`${v} · ${detail(k,v)}`));pop.append(row);boxes.push(cb);cb.onchange=()=>{cb.checked?chosen[k].add(v):chosen[k].delete(v);sync();render()}});function sync(){const n=chosen[k].size;b.textContent=`${labels[k]}: ${n===0?'all':n<=2?[...chosen[k]].join(', '):n+'/'+vals.length}`;w.classList.toggle('active',n>0);all.firstChild.checked=n===0;boxes.forEach(cb=>cb.checked=chosen[k].has(cb.value))}all.onclick=e=>{e.preventDefault();chosen[k].clear();sync();render()};b.onclick=e=>{e.stopPropagation();document.querySelectorAll('.pop').forEach(p=>{if(p!==pop)p.hidden=true});pop.hidden=!pop.hidden};w.append(b,pop);sync();root.append(w)})}
function refreshSteps(){const old=S.value,all=[...new Set(gs().flatMap(g=>g.series.flatMap(x=>x.rows.filter(r=>r.status==='Valid').map(step))))].sort((a,b)=>a.localeCompare(b,undefined,{numeric:true}));S.innerHTML='';all.forEach(x=>S.add(new Option(x,x)));S.value=all.includes(old)?old:all.at(-1)||''}
const pointKey=r=>r.pointSequence+'|'+r.pointName,total=x=>rows(x).reduce((sum,r)=>sum+r[prop()],0),tip=(candidate,side,r)=>`${candidate} · ${side}\n${C.value}: ${fmt(r[prop()])} ${unit()}\nPoint: ${r.pointName}\nGlobal: (${fmt(r.x)}, ${fmt(r.y)}, ${fmt(r.z)}) m\n${S.value}`;
function wireTips(){P.querySelectorAll('.hit').forEach(x=>{x.onmouseenter=()=>{TIP.textContent=x.dataset.tip;TIP.hidden=false};x.onmousemove=e=>{TIP.style.left=Math.min(innerWidth-330,e.clientX+12)+'px';TIP.style.top=Math.min(innerHeight-150,e.clientY+12)+'px'};x.onmouseleave=()=>TIP.hidden=true})}
// Range band per column: min..max among the candidates in scope, core = middle half (P25..P75, linear
// interpolation), line = median. "Band by" splits each column into one band per load / height / pretension id.
const quant=(v,p)=>{const i=(v.length-1)*p,a=Math.floor(i),b=Math.ceil(i);return v[a]+(v[b]-v[a])*(i-a)};
function stats(items){const s=items.slice().sort((a,b)=>a.v-b.v),v=s.map(it=>it.v);return{n:v.length,lo:s[0],up:s.at(-1),p25:quant(v,.25),med:quant(v,.5),p75:quant(v,.75)}}
const bands=series=>{const k=B.value;return !k?[{id:'',label:'All candidates',colour:ACC,series}]:IDS[k].map(v=>({id:v,label:v,colour:colourOf(k,v),series:series.filter(s=>s[k]===v)})).filter(b=>b.series.length)};
const columnItems=(p,series,totals)=>totals?series.filter(s=>rows(s).length).map(s=>({s,v:total(s),r:null})):series.flatMap(s=>rows(s).filter(r=>pointKey(r)===pointKey(p)).map(r=>({s,v:r[prop()],r}))),jitter=s=>((s.runIndex*.618034)%1)*2-1;
function chart(g,series,totals){
 const W=1000,H=350,m={l:68,r:18,t:22,b:50},auto=series.length>D.study.individualProfileLimit,grouped=!!B.value;
 const points=totals?[{pointName:`${g.foundationSide} total`,pointSequence:1}]:[...new Map(series.flatMap(rows).map(r=>[pointKey(r),r])).values()].sort((a,b)=>a.pointSequence-b.pointSequence);
 const cols=points.map(p=>columnItems(p,series,totals)),values=cols.flat().map(it=>it.v);
 let lo=values.length?Math.min(0,...values):-1,up=values.length?Math.max(0,...values):1;if(lo===up){lo--;up++}const pad=(up-lo)*.06;lo-=pad;up+=pad;
 const slot=(W-m.l-m.r)/Math.max(1,points.length),x=i=>m.l+(i+.5)*slot,y=v=>m.t+(up-v)/(up-lo)*(H-m.t-m.b),bs=bands(series);
 let h=`<svg class="chart" viewBox="0 0 ${W} ${H}"><rect x="${m.l}" y="${m.t}" width="${W-m.l-m.r}" height="${H-m.t-m.b}" fill="white" stroke="#ccc"/>`;
 for(let i=0;i<=5;i++){const v=lo+(up-lo)*i/5,Y=y(v);h+=`<line x1="${m.l}" x2="${W-m.r}" y1="${Y}" y2="${Y}" stroke="#eee"/><text x="${m.l-6}" y="${Y+3}" text-anchor="end" font-size="10">${fmt(v)}</text>`}
 if(lo<0&&up>0)h+=`<line x1="${m.l}" x2="${W-m.r}" y1="${y(0)}" y2="${y(0)}" stroke="#c7ccd2"/>`;
 points.forEach((p,i)=>h+=`<text x="${x(i)}" y="${H-m.b+18}" text-anchor="middle" font-size="10">${esc(p.pointName)}</text>`);
 points.forEach((p,i)=>{
  const live=bs.map(b=>({...b,items:cols[i].filter(it=>b.series.includes(it.s))})).filter(b=>b.items.length),inner=slot*.84,sub=inner/Math.max(1,live.length),bw=Math.max(6,Math.min(64,sub*.62));
  live.forEach((b,bi)=>{
   const cx=x(i)-inner/2+sub*(bi+.5),st=stats(b.items),c=b.colour,x0=(cx-bw/2).toFixed(1),top=y(st.up.v),bot=y(st.lo.v),med=y(st.med).toFixed(1);
   const t=`${g.foundationSide} · ${p.pointName}${grouped?` · ${b.label} (${detail(B.value,b.id)})`:''}\n${C.value} range: ${fmt(st.lo.v)} to ${fmt(st.up.v)} ${unit()}\nmiddle half (P25–P75): ${fmt(st.p25)} to ${fmt(st.p75)}\nmedian: ${fmt(st.med)} · n = ${st.n}\nmax: ${st.up.s.iterationKey}\nmin: ${st.lo.s.iterationKey}\n${S.value}`;
   h+=`<rect class="hit" x="${x0}" y="${top.toFixed(1)}" width="${bw.toFixed(1)}" height="${Math.max(2,bot-top).toFixed(1)}" rx="2" fill="${c}" fill-opacity=".14" stroke="${c}" stroke-opacity=".55" data-tip="${esc(t)}"/>`
     +`<rect x="${x0}" y="${y(st.p75).toFixed(1)}" width="${bw.toFixed(1)}" height="${Math.max(1,y(st.p25)-y(st.p75)).toFixed(1)}" fill="${c}" fill-opacity=".32" pointer-events="none"/>`
     +`<line x1="${x0}" x2="${(cx+bw/2).toFixed(1)}" y1="${med}" y2="${med}" stroke="${c}" stroke-width="2" pointer-events="none"/>`;
   // selective direct labels: only the extremes, only when one band per column leaves room beside it
   if(!grouped){const lx=(cx+bw/2+6).toFixed(1);h+=bot-top<14?`<text x="${lx}" y="${((top+bot)/2+3).toFixed(1)}" font-size="10" fill="#333">${fmt(st.lo.v)} to ${fmt(st.up.v)}</text>`
     :`<text x="${lx}" y="${(top+3).toFixed(1)}" font-size="10" fill="#333">max ${fmt(st.up.v)}</text><text x="${lx}" y="${(bot+3).toFixed(1)}" font-size="10" fill="#333">min ${fmt(st.lo.v)}</text>`}
   if(!auto)b.items.forEach(it=>{const key=it.s.iterationKey,t2=totals?`${key} · ${g.foundationSide} total\n${C.value}: ${fmt(it.v)} ${unit()}\n${S.value}`:tip(key,g.foundationSide,it.r);
     h+=`<circle class="hit" cx="${(cx+jitter(it.s)*bw*.34).toFixed(1)}" cy="${y(it.v).toFixed(1)}" r="${hi===key?5:3}" fill="${c}" stroke="#fff" stroke-width="1" opacity="${hi&&hi!==key?.15:.9}" data-key="${esc(key)}" data-tip="${esc(t2)}"/>`})});
 });
 return h+`<text transform="translate(15 185) rotate(-90)" text-anchor="middle" font-size="11">${C.value} [${unit()}]</text></svg>`}
function legend(s,auto){const k=B.value;return `<h4>Range band</h4><div class="key"><span class="kb"></span>min – max</div><div class="key"><span class="kc"></span>middle half · P25–P75</div><div class="key"><span class="km"></span>median</div>`
 +(k?`<h4>Band by ${DIMS[k]}</h4>`+IDS[k].filter(v=>s.some(x=>x[k]===v)).map(v=>`<div class="grp" title="${esc(detail(k,v))}"><span class="sw" style="background:${colourOf(k,v)}"></span>${esc(v)} · ${esc(detail(k,v))}</div>`).join(''):'')
 +(auto?`<h4>Candidates</h4><div class="grp">${s.length} in scope · markers hidden</div>`:`<h4>Candidates <span>click = highlight</span></h4>`+s.slice(0,100).map(x=>`<button data-key="${esc(x.iterationKey)}" class="${hi===x.iterationKey?'on':''}"><span class="sw" style="background:${colourOf(k,x[k])}"></span>${esc(x.iterationKey)}</button>`).join(''))}
function charts(){let h='';gs().forEach(g=>{const s=scope(g);if(!s.length)return;const totals=V.value==='totals',auto=s.length>D.study.individualProfileLimit,k=B.value;h+=`<section class="panel" data-pr="chart" data-pr-type="${totals?'totals':'supports'}" data-pr-id="${V.value}_${D.groups.indexOf(g)}"><span class="title">${esc(g.foundationSide)} foundation · ${C.value}</span><span class="sub">${esc(g.outputCase)} · ${esc(S.value)} · ${totals?'sum of two selected points':'two selected support points'} · ${s.length} candidates · band = range among candidates${k?` per ${DIMS[k].toLowerCase()}`:''}${auto?' · min/max envelope':''}</span><div class="stage">${chart(g,s,totals)}<aside class="legend">${legend(s,auto)}</aside></div></section>`});P.innerHTML=h||'<p class="empty">No matching reaction results.</p>';P.querySelectorAll('[data-key]').forEach(x=>x.onclick=()=>{hi=hi===x.dataset.key?null:x.dataset.key;render()});wireTips()}
function matrix(){let h='';gs().forEach(g=>{const s=scope(g),points=[...new Map(s.flatMap(rows).map(r=>[pointKey(r),r])).values()].sort((a,b)=>a.pointSequence-b.pointSequence),max=Math.max(1,...s.flatMap(x=>rows(x).map(r=>Math.abs(r[prop()])))),colVals=p=>s.map(x=>rows(x).find(r=>pointKey(r)===pointKey(p))?.[prop()]).filter(Number.isFinite),totVals=s.filter(x=>rows(x).length).map(total),agg=(label,f)=>`<tr><td>${label}</td>${points.map(p=>{const v=colVals(p);return `<td>${v.length?fmt(f(v)):'—'}</td>`}).join('')}<td>${totVals.length?fmt(f(totVals)):'—'}</td></tr>`;h+=`<section class="panel" data-pr="chart" data-pr-type="matrix" data-pr-id="matrix_${D.groups.indexOf(g)}"><span class="title">${esc(g.foundationSide)} foundation · ${C.value} matrix</span><span class="sub">${esc(S.value)}</span><div class="matrix-wrap" data-print-fit><table class="matrix"><thead><tr><th>Candidate</th>${points.map(p=>`<th>${esc(p.pointName)}</th>`).join('')}<th>${esc(g.foundationSide)} total</th></tr></thead><tbody>${s.map(x=>{const map=new Map(rows(x).map(r=>[pointKey(r),r]));return `<tr><td>${esc(x.iterationKey)}</td>${points.map(p=>{const r=map.get(pointKey(p)),v=r?.[prop()];if(!Number.isFinite(v))return '<td>—</td>';const a=.1+.7*Math.min(1,Math.abs(v)/max),bg=v<0?`rgba(198,40,40,${a})`:`rgba(46,125,50,${a})`;return `<td style="background:${bg}" title="${esc(tip(x.iterationKey,g.foundationSide,r))}">${fmt(v)}</td>`}).join('')}<td>${fmt(total(x))}</td></tr>`}).join('')}</tbody><tfoot>${agg('Min',v=>Math.min(...v))}${agg('Max',v=>Math.max(...v))}${agg('Range',v=>Math.max(...v)-Math.min(...v))}</tfoot></table></div></section>`});P.innerHTML=h||'<p class="empty">No matching reaction results.</p>'}
// ---------- Summary (default view) and By Candidate (its drill-down) ----------
// group.summary reads a foundation's two supports as inner / outer and carries, per iteration and result step,
// each one's Fx / Fz with their algebraic sum (derived by the generator from the stored rows) — this block only
// draws. Colour is the role, the same in every chart: inner, outer, and the hatched Σ Net, which is a resultant
// and not a third support. Height and pretension come from position and labels, so no further colours are spent.
const SUM=D.groups.filter(g=>g.summary),INNER='#1f4788',OUTER='#ff7f0e',NET='#475467';
const ROLES=[{k:'inner',name:'Inner',colour:INNER},{k:'outer',name:'Outer',colour:OUTER},{k:'net',name:'Σ Net',colour:NET}],ROLE=Object.fromEntries(ROLES.map(r=>[r.k,r]));
const COMPS={fz:{id:'Fz',title:'Vertical Reaction Fz',note:'+ compression (the support pushes up) · − uplift (the support holds down)'},fx:{id:'Fx',title:'Horizontal Reaction Fx',note:'sign = global X direction · opposite reactions cancel in Σ Net'}};
const hName=s=>'H'+Math.round(s.trussHeightM*1000)/1000,f1=v=>Math.abs(v).toFixed(1),sgn=v=>(+f1(v)?(v<0?'−':'+'):'')+f1(v),mn=kn=>sgn(kn/1000);
const kNs=v=>{const r=Math.round(v);return(r<0?'−':r>0?'+':'')+String(Math.abs(r)).replace(/\B(?=(\d{3})+(?!\d))/g,',')},textW=(t,px)=>String(t).length*px*.54;
const sideName=g=>`${g.foundationSide} Foundation`,jointOf=(g,k)=>k==='net'?`${g.summary.innerPoint}+${g.summary.outerPoint}`:`Pt ${k==='inner'?g.summary.innerPoint:g.summary.outerPoint}`;
const stepOf=(g,s)=>{const c=(g.cmap??=new Map(g.summary.candidates.map(c=>[c.iterationKey,c]))).get(s.iterationKey);return c&&c.steps.find(t=>step(t)===S.value)||null};
const behaviour=kn=>kn<0?'Uplift':kn>0?'Compression':'None',loadLabel=id=>`${id} · ${ALL.find(x=>x.loadId===id).loadingLabel}`;
let sumMode='summary',printPg=null,hatchN=0;const cand={};     // 'summary' | 'candidate', the sheet while printing, and the iteration the drill-down shows
function go(mode,pick){sumMode=mode==='candidate'?'candidate':'summary';if(pick)Object.assign(cand,pick);render()}
const keyPick=key=>{const s=ALL.find(x=>x.iterationKey===key);return s?{loadId:s.loadId,pretensionId:s.pretensionId,heightId:s.heightId}:null};
const hatch=id=>`<pattern id="${id}" width="5" height="5" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="5" height="5" fill="#fff"/><line x1="1" y1="0" x2="1" y2="5" stroke="${NET}" stroke-width="1.5"/></pattern>`;
const swatch=k=>{if(k!=='net')return `<svg width="12" height="12"><rect x="1" y="1" width="10" height="10" fill="${ROLE[k].colour}"/></svg>`;const id='nh'+(++hatchN);return `<svg width="14" height="12"><defs>${hatch(id)}</defs><rect x="1" y="1" width="12" height="10" fill="url(#${id})" stroke="${NET}"/></svg>`};
// loads, heights and pretension candidates among the in-scope iterations of these groups, in study order
function scopeOf(groups){const all=groups.flatMap(g=>scope(g)),has=k=>id=>all.some(x=>x[k]===id);
  return{all,hs:IDS.heightId.filter(has('heightId')).map(id=>({id,name:hName(all.find(x=>x.heightId===id))})),ps:IDS.pretensionId.filter(has('pretensionId')),ls:IDS.loadId.filter(has('loadId'))}}
// How many pretension candidates fit one chart row with bars wide enough to label; the rest wrap to further rows.
// On paper the chart is laid out at the sheet's own width, so its text keeps its size.
// A truss-height slot is three bars and half a bar of air, so the heights of one candidate read as separate triples.
function layout(nP,nH){const W=printPg?Math.max(520,printPg.contentPx-24):1100,ml=52,mr=8,pw=W-ml-mr;
  for(let n=1;;n++){const per=Math.ceil(nP/n),cw=pw/per,pgap=Math.max(10,Math.min(20,cw*.08)),pitch=Math.min(36,(cw-pgap)/nH/3.5);
    if(pitch>=9.5||per===1)return{W,ml,mr,pw,hgap:pitch/2,per,cw,pitch,rows:Math.ceil(nP/per),flat:pitch>=33}}}
// one signed axis per component, shared by every card shown, so loads and foundation sides compare directly
function range(groups,comp){const v=groups.flatMap(g=>scope(g).map(s=>stepOf(g,s)).filter(t=>t&&t[comp]).flatMap(t=>[t[comp].inner,t[comp].outer,t[comp].net])).map(x=>x/1000);
  let lo=Math.min(0,...v),up=Math.max(0,...v);if(lo===up)up=lo+1;const raw=(up-lo)/5,p=Math.pow(10,Math.floor(Math.log10(raw))),n=raw/p;return{lo,up,step:(n<=1?1:n<=2?2:n<=2.5?2.5:n<=5?5:10)*p}}
const tickText=(v,st)=>st%1?v.toFixed(1):String(Math.round(v));

// One chart row: per pretension candidate one slot per truss height with three adjacent bars — Inner | Outer |
// Σ Net — about a visible zero line, then a gap before the next candidate. Every bar carries its role and its
// SAP joint. A missing iteration stays blank ("n/a"): never a zero, never an estimate.
function reactionRow(g,comp,series,hs,ps,yr,L,caption){
  const c=COMPS[comp],W=L.W,ml=L.ml,mr=L.mr,bw=Math.max(3,Math.min(26,L.pitch-2)),slot=3*L.pitch+L.hgap,gw=hs.length*slot-L.hgap,hid='nh'+(++hatchN);
  const lab=Math.max(...ROLES.map(r=>textW(`${r.name} ${jointOf(g,r.k)}`,8.5))),vlen=v=>L.flat?17:textW(sgn(v),9.5)+8;     // room beyond the bar end for its value label
  const mt=caption?17:8,padT=yr.up>0?vlen(yr.up):6,padB=yr.lo<0?vlen(yr.lo):0,labH=L.flat?27:lab+6,over=mt+padT+padB+labH+34;
  // screen: a fixed plot height. Paper: two rows of a wrapped chart per sheet, or one single-row chart under the page header
  const ph=printPg?Math.max(90,Math.min(230,(L.rows>1?Math.floor((printPg.contentHeightPx-70)/2):printPg.contentHeightPx-262)-over)):L.rows>1?150:210;
  const y=v=>mt+padT+(yr.up-v)/(yr.up-yr.lo)*ph,base=mt+padT+ph+padB,y0=y(0),hy=base+labH+10,by=hy+5,H=by+19;
  let grid='',bars='',vals='',ax='',hits='';
  for(let i=Math.ceil(yr.lo/yr.step-1e-9);i*yr.step<=yr.up+1e-9;i++){const v=i*yr.step,Y=y(v).toFixed(1);if(i)grid+=`<line class="cgrid" x1="${ml}" x2="${W-mr}" y1="${Y}" y2="${Y}"/>`;grid+=`<text class="ctick" x="${ml-6}" y="${(+Y+3).toFixed(1)}" text-anchor="end">${tickText(v,yr.step)}</text>`}
  ps.forEach((pid,i)=>{const x0=ml+i*L.cw+(L.cw-gw)/2;
    hs.forEach((ht,j)=>{const hx=x0+j*slot,mid=(hx+1.5*L.pitch).toFixed(1),s=series.find(x=>x.pretensionId===pid&&x.heightId===ht.id),st=s&&stepOf(g,s),pair=st&&st[comp],head=`${pid} · ${ht.name} · ${sideName(g)}`;
      ax+=`<text class="chh" x="${mid}" y="${hy.toFixed(1)}" text-anchor="middle">${esc(ht.name)}</text>`;
      if(!pair){const out=!s&&series.length>0&&nq(series[0].loadId,ht.id,pid);vals+=`<text class="cna" x="${mid}" y="${(y0-4).toFixed(1)}" text-anchor="middle">${out?'NQ':'n/a'}</text>`;
        hits+=`<rect class="hit" data-missing="${comp}|${esc(pid)}|${esc(ht.id)}" x="${hx.toFixed(1)}" y="${mt}" width="${(3*L.pitch).toFixed(1)}" height="${(base-mt).toFixed(1)}" fill="transparent" data-tip="${esc(head+'\n'+(out?'Not qualified — outside the '+SCOPE.label.toLowerCase():'Result unavailable'))}"/>`;return}
      ROLES.forEach((r,k)=>{const kn=pair[r.k],v=kn/1000,cx=hx+(k+.5)*L.pitch,Y=y(v),top=Math.min(Y,y0),hgt=Math.abs(Y-y0),up=kn>=0,net=r.k==='net',X=cx.toFixed(1),joint=jointOf(g,r.k);
        bars+=`<rect data-bar="${comp}|${esc(pid)}|${esc(ht.id)}|${r.k}" x="${(cx-bw/2).toFixed(1)}" y="${top.toFixed(1)}" width="${bw.toFixed(1)}" height="${hgt.toFixed(1)}" ${net?`fill="url(#${hid})" stroke="${NET}" stroke-width="1.1"`:`fill="${r.colour}"`}/>`;
        vals+=L.flat?`<text class="rv${net?' rvn':''}" x="${X}" y="${(up?top-5:top+hgt+12).toFixed(1)}" text-anchor="middle">${sgn(v)}</text>`
          :`<text class="rv${net?' rvn':''}" transform="translate(${(cx+3.3).toFixed(1)} ${(up?top-5:top+hgt+5).toFixed(1)}) rotate(-90)" text-anchor="${up?'start':'end'}">${sgn(v)}</text>`;
        ax+=L.flat?`<text class="rr" x="${X}" y="${(base+12).toFixed(1)}" text-anchor="middle">${esc(r.name)}</text><text class="rj" x="${X}" y="${(base+23).toFixed(1)}" text-anchor="middle">${esc(joint)}</text>`
          :`<text transform="translate(${(cx+3).toFixed(1)} ${(base+5).toFixed(1)}) rotate(-90)" text-anchor="end"><tspan class="rr">${esc(r.name)}</tspan><tspan class="rj"> ${esc(joint)}</tspan></text>`;
        const t=head+'\n'+(net?`Σ Net · Pt ${g.summary.innerPoint} + Pt ${g.summary.outerPoint}\n${c.id} net: ${sgn(v)} MN\nΣ ${c.id} = ${c.id}${g.summary.innerPoint} + ${c.id}${g.summary.outerPoint} = ${mn(pair.inner)} + (${mn(pair.outer)})`
          :`${r.name} · ${joint}\n${c.id}: ${sgn(v)} MN${comp==='fz'?`\nBehaviour: ${behaviour(kn)}`:''}`)+`\nSource: ${kNs(kn)} kN · ${S.value}\nIteration ${s.iterationKey} · ${s.loadingLabel}\n${g.outputCase}`;
        hits+=`<rect class="hit" data-cand="${esc(s.iterationKey)}" x="${(cx-L.pitch/2).toFixed(1)}" y="${mt}" width="${L.pitch.toFixed(1)}" height="${(base-mt).toFixed(1)}" fill="transparent" data-tip="${esc(t)}"/>`})});
    ax+=`<path class="cbr" d="M${(x0+2).toFixed(1)} ${by.toFixed(1)} v3 H${(x0+gw-2).toFixed(1)} v-3"/><text class="cpl" x="${(x0+gw/2).toFixed(1)}" y="${(by+15).toFixed(1)}" text-anchor="middle">${esc(pid)}</text>`});
  return `<svg class="cchart" data-chart="${comp}" viewBox="0 0 ${W} ${H.toFixed(0)}" role="img" aria-label="${esc(`${c.title}, ${sideName(g)}: inner support, outer support and their net per pretension candidate and truss height`)}"><defs>${hatch(hid)}</defs>${caption?`<text class="ccap" x="${ml}" y="11">${esc(caption)}</text>`:''}${grid}<line class="caxis" x1="${ml}" x2="${ml}" y1="${mt}" y2="${base.toFixed(1)}"/>${bars}<line class="czero" x1="${ml}" x2="${W-mr}" y1="${y0.toFixed(1)}" y2="${y0.toFixed(1)}"/>${vals}${ax}<text class="cax" transform="translate(13 ${(mt+padT+ph/2).toFixed(1)}) rotate(-90)" text-anchor="middle">${c.id} (MN)</text>${hits}</svg>`}
// one legend ahead of the charts: the three roles, and how to read the sign of each component
const roleLegend=()=>`<div class="clegend" data-pr="legend"><span>${swatch('inner')}Inner Support</span><span>${swatch('outer')}Outer Support</span><span>${swatch('net')}Σ Net = Inner + Outer</span><span class="note">Fz: ${COMPS.fz.note} · Fx: ${COMPS.fx.note} · n/a = no result${SCOPE?' · NQ = not qualified':''}</span></div>`;

// Governing values over both foundation sides and every iteration in scope, each traced to where it occurs.
function kpiCards(){
  const E=SUM.flatMap(g=>scope(g).map(s=>({g,s,st:stepOf(g,s)})).filter(e=>e.st)),many=IDS.loadId.length>1;
  const defs=[
    {name:'Max Inner Compression',comp:'fz',none:'No inner compression',of:e=>e.st.fz&&e.st.fz.inner>0?{v:e.st.fz.inner,k:'inner'}:null},
    {name:'Max Outer Uplift',comp:'fz',none:'No uplift',of:e=>e.st.fz&&e.st.fz.outer<0?{v:-e.st.fz.outer,k:'outer'}:null},
    {name:'Max Individual Horizontal Reaction',comp:'fx',none:'No result',of:e=>{if(!e.st.fx)return null;const k=Math.abs(e.st.fx.inner)>=Math.abs(e.st.fx.outer)?'inner':'outer';return{v:Math.abs(e.st.fx[k]),k}}},
    {name:'Max Net Horizontal Reaction per Side',comp:'fx',none:'No result',of:e=>e.st.fx?{v:Math.abs(e.st.fx.net),k:'net'}:null}];
  return `<div class="kpis" data-pr="summary">${defs.map((d,i)=>{let b=null;E.forEach(e=>{const m=d.of(e);if(m&&(!b||m.v>b.m.v))b={e,m}});
    if(!b)return `<div class="kpi" data-kpi="${i}"><div class="kl">${d.name}</div><div class="kv none">${d.none}</div><div class="kd">in the ${E.length} result(s) in scope</div></div>`;
    // the other foundation side of the same iteration governs too when it reads the same to the shown precision
    const tie=E.filter(e=>e.s.iterationKey===b.e.s.iterationKey).map(e=>({e,m:d.of(e)})).filter(x=>x.m&&f1(x.m.v/1000)===f1(b.m.v/1000)),s=b.e.s,id=COMPS[d.comp].id;
    const where=new Set(tie.map(x=>x.m.k)).size===1?`${ROLE[b.m.k].name} · ${tie.map(x=>jointOf(x.e.g,x.m.k)).join(' / ')}`:tie.map(x=>`${ROLE[x.m.k].name} ${jointOf(x.e.g,x.m.k)}`).join(' / ');
    const src=tie.map(x=>`${x.e.g.foundationSide}: ${id} = ${kNs(x.e.st[d.comp][x.m.k])} kN`).join(' · ');
    return `<div class="kpi" data-kpi="${i}" data-cand="${esc(s.iterationKey)}" style="border-left-color:${ROLE[b.m.k].colour}" title="${esc(`Iteration ${s.iterationKey} · ${src} · ${b.e.g.outputCase} · ${S.value} — click to view this candidate`)}"><div class="kl">${d.name}</div><div class="kv">${f1(b.m.v/1000)} <span>MN</span></div><div class="kw">${esc(`${many?s.loadId+' · ':''}${s.pretensionId} · ${hName(s)} · ${tie.map(x=>x.e.g.foundationSide).join('/')}`)}</div><div class="kd">${esc(where)} · ${id}</div></div>`}).join('')}</div>`}

function summaryPanel(){
  const shown=gs().filter(g=>g.summary&&scope(g).length);if(!shown.length)return '<p class="empty">No matching reaction results.</p>';
  const sc=scopeOf(shown),L=layout(sc.ps.length,sc.hs.length),loadOf=(g,id)=>scope(g).filter(x=>x.loadId===id),open=printPg&&window.reportPrint.state.includeSource?' open':'';
  const trace=`${shown.map(g=>`${sideName(g)}: Inner Pt ${g.summary.innerPoint}, Outer Pt ${g.summary.outerPoint}`).join(' · ')} · ${shown[0].outputCase} · ${S.value}`;
  let h=`<section class="panel sumpanel" data-sum-view="summary"><div class="chead" data-pr="summary" data-pr-keep><span class="cname">Support Reactions</span><span class="cview">Summary</span><button class="cgo no-print" data-go="candidate">View by Candidate</button><span class="sub">${esc(trace)}</span></div>`
    +`<p class="cnote" data-pr="note">Per truss height: the two joint reactions of the foundation side, then their resultant — <b>Inner | Outer | Σ Net</b>. Σ Net is the algebraic sum of the two joints, not a third support: read Inner and Outer for how the supports share the load (uplift and opposing horizontal reactions show there), and Σ Net for what the side carries overall.</p>${roleLegend()}${kpiCards()}`;
  ['fz','fx'].forEach(comp=>{const yr=range(shown,comp);sc.ls.forEach(id=>shown.forEach(g=>{const series=loadOf(g,id);if(!series.length)return;let charts='';
    for(let r=0;r<L.rows;r++){const part=sc.ps.slice(r*L.per,(r+1)*L.per);charts+=`<div class="crow">${reactionRow(g,comp,series,sc.hs,part,yr,L,L.rows>1?`${COMPS[comp].id} · ${id} · ${sideName(g)} · ${part[0]}${part.length>1?' – '+part.at(-1):''}`:'')}</div>`}
    h+=`<div class="ccard${L.rows>1?' multi':''}" data-card="${comp}|${esc(g.foundationSide)}|${esc(id)}" data-pr="chart" data-pr-type="${comp}Summary" data-pr-id="${comp}_${esc(g.foundationSide)}_${esc(id)}" data-pr-split=".crow"><div class="ctitle">${COMPS[comp].title} – ${esc(sideName(g))}<span>${esc(loadLabel(id))} · ${series.length} iteration(s)</span></div>${charts}</div>`}))});
  const cell=v=>`<td class="n">${kNs(v)}</td>`,body=shown.flatMap(g=>sc.ls.flatMap(id=>sc.ps.flatMap(pid=>sc.hs.map(ht=>{const s=loadOf(g,id).find(x=>x.pretensionId===pid&&x.heightId===ht.id),st=s&&stepOf(g,s);
    return `<tr><td>${esc(g.foundationSide)}</td><td class="n">${esc(g.summary.innerPoint)}</td><td class="n">${esc(g.summary.outerPoint)}</td><td>${esc(id)}</td><td>${esc(pid)}</td><td>${esc(ht.name)}</td><td>${s?esc(s.iterationKey):'—'}</td>`
      +(st?['fx','fz'].map(k=>st[k]?cell(st[k].inner)+cell(st[k].outer)+cell(st[k].net):'<td class="n na" colspan="3">Result unavailable</td>').join(''):`<td class="n na" colspan="6">${!s&&nq(id,ht.id,pid)?'Not qualified':'Result unavailable'}</td>`)+'</tr>'})))).join('');
  return h+`<details class="cdata" data-print="source" data-pr="source"${open}><summary>Reaction values — the source of every bar</summary><div class="matrix-wrap" data-print-fit><table class="ctable"><thead><tr><th>Foundation</th><th class="n">Inner Pt</th><th class="n">Outer Pt</th><th>Load</th><th>Pretension</th><th>Height</th><th>Iteration</th><th class="n">Fx inner [kN]</th><th class="n">Fx outer [kN]</th><th class="n">ΣFx [kN]</th><th class="n">Fz inner [kN]</th><th class="n">Fz outer [kN]</th><th class="n">ΣFz [kN]</th></tr></thead><tbody>${body}</tbody></table></div></details></section>`}

// By Candidate: both foundation sides of one iteration — outer and inner support in their order across the span,
// each with its Fz and Fx arrow as the reaction acting on the structure, and the side's net below. Arrow length
// follows the magnitude within Fz and within Fx; the layout is not to scale.
function schematic(sides){
  const W=1100,ml=24,pw=W-48,n=sides.length,yh=92,yn=132,ya=yn+34,yb=ya+78,H=yb+48;
  const big=k=>Math.max(1e-9,...sides.flatMap(x=>x.st&&x.st[k]?[Math.abs(x.st[k].inner),Math.abs(x.st[k].outer)]:[])),mz=big('fz'),mx=big('fx'),len=(v,m,a,b)=>a+(b-a)*Math.abs(v)/m;
  let h='';const xs=[];
  sides.forEach((x,i)=>{const g=x.g,x0=ml+i*pw/n,w=pw/n,at=x.order.map((k,q)=>x0+w*(q?.7:.3));xs.push(...at);
    h+=`<text class="sside" x="${(x0+w/2).toFixed(1)}" y="16" text-anchor="middle">${esc(sideName(g))}</text>`;
    x.order.forEach((k,q)=>{const cx=at[q],X=cx.toFixed(1),col=ROLE[k].colour;
      h+=`<text class="srole" x="${X}" y="40" text-anchor="middle" fill="${col}">${ROLE[k].name}</text><text class="sjoint" x="${X}" y="54" text-anchor="middle">${esc(jointOf(g,k))}</text>`
        +`<path class="spin" d="M${X} ${yn+5} l-11 17 h22 z"/><line class="sground" x1="${(cx-17).toFixed(1)}" x2="${(cx+17).toFixed(1)}" y1="${yn+25}" y2="${yn+25}"/><circle class="snode" cx="${X}" cy="${yn}" r="4.5"/>`;
      if(!x.st){h+=`<text class="cna" x="${X}" y="${yh+3}" text-anchor="middle">no result</text>`;return}
      const fx=x.st.fx?x.st.fx[k]:null,fz=x.st.fz?x.st.fz[k]:null;
      if(fx!=null){const d=fx<0?-1:1,l=+f1(fx/1000)?len(fx,mx,18,70):0,x2=cx+d*l/2;
        h+=(l?`<line x1="${(cx-d*l/2).toFixed(1)}" x2="${(x2-d*6).toFixed(1)}" y1="${yh}" y2="${yh}" stroke="${col}" stroke-width="2.4"/><path d="M${x2.toFixed(1)} ${yh} l${-d*9} -5 v10 z" fill="${col}"/>`:`<circle cx="${X}" cy="${yh}" r="2.5" fill="${col}"/>`)
          +`<text class="sval" data-arrow="fx|${esc(g.foundationSide)}|${k}|${l?(d>0?'right':'left'):'none'}" x="${X}" y="${yh-10}" text-anchor="middle">Fx = ${mn(fx)} MN</text>`}
      if(fz!=null){const l=+f1(fz/1000)?len(fz,mz,20,70):0,up=fz>=0,d=up?1:-1,head=up?ya:ya+l,tail=up?ya+l:ya;
        h+=(l?`<line x1="${X}" x2="${X}" y1="${tail.toFixed(1)}" y2="${(head+d*6).toFixed(1)}" stroke="${col}" stroke-width="2.4"/><path d="M${X} ${head.toFixed(1)} l-5 ${d*9} h10 z" fill="${col}"/>`:`<circle cx="${X}" cy="${ya+4}" r="2.5" fill="${col}"/>`)
          +`<text class="sval" data-arrow="fz|${esc(g.foundationSide)}|${k}|${l?(up?'up':'down'):'none'}" x="${(cx+12).toFixed(1)}" y="${(ya+l/2+1).toFixed(1)}">Fz = ${mn(fz)} MN</text><text class="sbeh${fz<0?' up':''}" x="${(cx+12).toFixed(1)}" y="${(ya+l/2+14).toFixed(1)}">${behaviour(fz)}</text>`}});
    if(x.st){const a=Math.min(...at)-40,b=Math.max(...at)+112,mid=((a+b)/2).toFixed(1),z=x.st.fz?x.st.fz.net:null,f=x.st.fx?x.st.fx.net:null;
      h+=`<path class="cbr" d="M${a.toFixed(1)} ${yb} v5 H${b.toFixed(1)} v-5"/><text class="snet" x="${mid}" y="${yb+22}" text-anchor="middle">Net ${esc(g.foundationSide)} · Pt ${esc(g.summary.innerPoint)} + Pt ${esc(g.summary.outerPoint)}</text>`
        +`<text class="snetv" data-net="${esc(g.foundationSide)}" x="${mid}" y="${yb+40}" text-anchor="middle">${[z==null?'':`${z<0?'↓ ':z>0?'↑ ':''}ΣFz = ${mn(z)} MN`,f==null?'':`${f<0?'← ':f>0?'→ ':''}ΣFx = ${mn(f)} MN`].filter(Boolean).join('  ·  ')}</text>`}});
  const x1=Math.min(...xs),x2=Math.max(...xs),gap=n>1?(xs[1]+xs[2])/2:null;
  return `<svg class="cchart" data-chart="schematic" viewBox="0 0 ${W} ${H}" role="img" aria-label="Support reaction schematic: Fz and Fx at the outer and inner support of each foundation side, with the net per side"><line class="sbeam" x1="${x1.toFixed(1)}" x2="${x2.toFixed(1)}" y1="${yn}" y2="${yn}"/>`
    +(gap==null?'':`<rect x="${(gap-13).toFixed(1)}" y="${yn-8}" width="26" height="16" fill="#fff"/><path d="M${(gap-10).toFixed(1)} ${yn+8} l7 -16 M${(gap+3).toFixed(1)} ${yn+8} l7 -16" stroke="#98a2b3" stroke-width="1.5" fill="none"/><text class="chl" x="${gap.toFixed(1)}" y="${yn-14}" text-anchor="middle">span · not to scale</text>`)+h+'</svg>'}

function candidatePanel(){
  const sc=scopeOf(SUM);if(!sc.all.length)return '<p class="empty">No matching reaction results.</p>';
  const L0=sc.ls.includes(cand.loadId)?cand.loadId:sc.ls[0],P0=sc.ps.includes(cand.pretensionId)?cand.pretensionId:sc.ps[0],H0=sc.hs.find(x=>x.id===cand.heightId)||sc.hs[0],name=`${P0} · ${H0.name}`;
  const sel=(k,label,opts,val)=>`<label class="cpick">${label} <select data-pick="${k}">${opts.map(o=>`<option value="${esc(o[0])}"${o[0]===val?' selected':''}>${esc(o[1])}</option>`).join('')}</select></label>`;
  // left to right across the span: the first side reads outer → inner, the next inner → outer
  const sides=SUM.map((g,i)=>{const s=scope(g).find(x=>x.loadId===L0&&x.pretensionId===P0&&x.heightId===H0.id);return{g,s,st:s?stepOf(g,s):null,order:(SUM.length>1?i>0:/right/i.test(g.foundationSide))?['inner','outer']:['outer','inner']}}),any=sides.find(x=>x.st);
  const h=`<section class="panel sumpanel" data-sum-view="candidate"><div class="chead" data-pr="chart" data-pr-keep><span class="cname">Support Reactions</span><span class="cview">By Candidate</span><button class="cgo no-print" data-go="summary">← Summary</button>`
    +(sc.ls.length>1?sel('loadId','Loading Candidate',sc.ls.map(id=>[id,loadLabel(id)]),L0):'')+sel('pretensionId','Pretension Candidate',sc.ps.map(id=>[id,id]),P0)+sel('heightId','Truss Height',sc.hs.map(x=>[x.id,x.name]),H0.id)
    +`<span class="sub">${esc(SUM[0].outputCase)} · ${esc(S.value)}</span></div>`;
  if(!any)return h+`<div class="ccard" data-pr="chart"><div class="ctitle">Support Reactions – ${esc(name)}<span>${esc(loadLabel(L0))}</span></div><p class="empty" data-missing="candidate">${nq(L0,H0.id,P0)?`${esc(`${L0} · ${name}`)} is not qualified — it is outside the ${esc(SCOPE.label.toLowerCase())}.`:`No result for ${esc(`${L0} · ${name}`)} — this iteration is not available.`}</p></div></section>`;
  const val=(pair,k)=>pair?`<td class="n" title="${kNs(pair[k])} kN">${mn(pair[k])}</td>`:'<td class="n na">—</td>',point=(g,k)=>esc(k==='inner'?g.summary.innerPoint:g.summary.outerPoint);
  const body=sides.flatMap(x=>x.st?x.order.map(k=>`<tr><td>${esc(x.g.foundationSide)}</td><td>${ROLE[k].name}</td><td class="n">${point(x.g,k)}</td>${val(x.st.fx,k)}${val(x.st.fz,k)}<td>${x.st.fz?behaviour(x.st.fz[k]):''}</td></tr>`):[`<tr><td>${esc(x.g.foundationSide)}</td><td class="na" colspan="5">Result unavailable</td></tr>`]).join('')
    +sides.filter(x=>x.st).map(x=>`<tr class="net"><td>${esc(x.g.foundationSide)}</td><td>Σ Net</td><td class="n">${point(x.g,'inner')} + ${point(x.g,'outer')}</td>${val(x.st.fx,'net')}${val(x.st.fz,'net')}<td>${x.st.fz?behaviour(x.st.fz.net):''}</td></tr>`).join('');
  return h+`<div class="ccard" data-pr="chart" data-pr-type="candidate" data-pr-id="candidate_${esc(any.s.iterationKey)}"><div class="ctitle">Support Reactions – ${esc(name)}<span>${esc(loadLabel(L0))} · Iteration ${esc(any.s.iterationKey)}</span></div>`
    +`<div class="clegend" data-pr="legend"><span>${swatch('inner')}Inner Support</span><span>${swatch('outer')}Outer Support</span><span class="note">arrows = the reaction acting on the structure · length follows the magnitude within Fz and within Fx · Σ = Inner + Outer, the algebraic sum</span></div>${schematic(sides)}`
    +`<table class="ctable" data-table="candidate"><thead><tr><th>Foundation</th><th>Role</th><th class="n">Point</th><th class="n">Fx [MN]</th><th class="n">Fz [MN]</th><th>Vertical behaviour</th></tr></thead><tbody>${body}</tbody></table></div></section>`}
function wireSummary(){P.querySelectorAll('[data-go]').forEach(b=>b.onclick=()=>go(b.dataset.go));P.querySelectorAll('[data-pick]').forEach(x=>x.onchange=()=>{cand[x.dataset.pick]=x.value;render()});
  P.querySelectorAll('[data-cand]').forEach(x=>x.onclick=()=>go('candidate',keyPick(x.dataset.cand)));wireTips()}

function render(){const sum=V.value==='summary';
// Component / Band by belong to the generic views: the Summary always reads Fz and Fx of the selected result step
[C,B].forEach(x=>{if(x.parentElement)x.parentElement.hidden=sum});
if(G.parentElement)G.parentElement.hidden=sum&&sumMode==='candidate';     // By Candidate always shows both foundation sides
if(sum){hatchN=0;Q.textContent=`up to ${Math.max(0,...SUM.map(g=>scope(g).length))} / ${D.study.candidateCount} candidates`;N.hidden=true;P.innerHTML=sumMode==='candidate'?candidatePanel():summaryPanel();wireSummary();return}
const n=Math.max(0,...gs().map(g=>scope(g).length)),auto=n>D.study.individualProfileLimit;Q.textContent=`up to ${n} / ${D.study.candidateCount} candidates`;N.hidden=!auto||V.value==='matrix';N.textContent=auto?`Showing min/max reaction envelopes (range bands only) because ${n} candidates are in scope. Filter to ${D.study.individualProfileLimit} or fewer to see every candidate marker.`:'';V.value==='matrix'?matrix():charts()}
G.onchange=()=>{hi=null;refreshSteps();render()};C.onchange=render;S.onchange=render;V.onchange=()=>{sumMode='summary';render()};B.onchange=render;document.getElementById('reset').onclick=()=>{Object.values(chosen).forEach(x=>x.clear());hi=null;document.getElementById('filters').innerHTML='';filters();render()};document.addEventListener('click',()=>document.querySelectorAll('.pop').forEach(x=>x.hidden=true));
// The Summary opens on one foundation side — the first (Left); without inner / outer supports there is nothing to summarise
if(!SUM.length){V.remove(0);V.value='supports'}else G.value=D.groups.indexOf(SUM[0]);
if(OPT.group!=null){const i=D.groups.findIndex(g=>g.foundationSide.toLowerCase()===String(OPT.group).toLowerCase());if(i>=0)G.value=i;else if(String(OPT.group).toLowerCase()==='all')G.value='all'}pick(C,OPT.component);pick(V,OPT.view);pick(B,OPT.band);
// "#detail=candidate&load=L01&pretension=P03&height=H02" opens the drill-down on that iteration
if(OPT.detail==='candidate'&&SUM.length){V.value='summary';sumMode='candidate';Object.assign(cand,{loadId:OPT.load,pretensionId:OPT.pretension,heightId:OPT.height})}
// paper: the Summary charts are laid out again for the sheet
if(window.reportPrint)window.reportPrint.register({before:pg=>{printPg=pg;if(V.value==='summary')render()},after:()=>{printPg=null;if(V.value==='summary')render()}});
window.reactionReport={summary:go,render};      // handle for scripted checks
filters();refreshSteps();render();const w=document.getElementById('warnings');if(D.warnings.length)w.textContent=D.warnings.map(x=>`[${x.iterationKey||'study'}] ${x.message}`).join(' · ');
})();
""";
}
