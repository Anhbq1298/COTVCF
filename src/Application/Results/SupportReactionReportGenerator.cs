using System.IO;
using System.Text.Json;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Results;

/// <summary>Self-contained comparison report for the Left and Right foundation points selected in Output Settings.</summary>
public sealed class SupportReactionReportGenerator
{
    public const string ReportFileName = "Support_Reactions_Report.html";
    /// <summary>Report name printed in the page header.</summary>
    public const string ReportTitle = "Support Reactions";
    public const int IndividualProfileLimit = 50;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Generate(string studyFolder, IReadOnlyList<CandidateResultDto> results,
        string baselineName, IterationStudyMode studyMode, IReadOnlyList<string>? siblingReports = null,
        ReportPrintSettings? print = null)
    {
        Directory.CreateDirectory(studyFolder);
        var path = Path.Combine(studyFolder, ReportFileName);
        var siblings = siblingReports ?? ReportNav.PresentIn(studyFolder, "support").ToArray();
        AtomicFile.WriteAllText(path, BuildHtml(Build(results, baselineName, studyMode, siblings),
                ReportPrintEngine.Describe(print, ReportTitle, baselineName, DateTimeOffset.Now)),
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
        IterationStudyMode studyMode, IReadOnlyList<string>? siblingReports = null)
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
        var warnings = results.SelectMany(result => result.Warnings)
            .Where(warning => warning.Category.Equals("SupportReaction", StringComparison.OrdinalIgnoreCase))
            .Select(warning => new ResultReportWarningDto(warning.IterationKey, warning.Category,
                warning.Severity.ToString(), warning.Message)).ToArray();
        return new(new(baselineName, studyMode.ToString(), results.Count, DateTimeOffset.Now.ToString("o"),
            IndividualProfileLimit), groups, warnings, siblingReports ?? []);
    }

    public string BuildHtml(SupportReactionReportDto dto, ReportPrintDto? print = null)
    {
        print ??= ReportPrintEngine.Describe(null, ReportTitle, dto.Study.BaselineModelName, DateTimeOffset.Now);
        var data = JsonSerializer.Serialize(dto, Json).Replace("</", "<\\/", StringComparison.Ordinal);
        var library = ReportNav.Library("support", new HashSet<string>(dto.SiblingReports, StringComparer.Ordinal));
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Support Reactions — {{Esc(dto.Study.BaselineModelName)}}</title>
<style>{{ReportPrintEngine.Css}}{{Style}}</style>{{ReportPrintEngine.PageStyle}}</head><body><header><div><h1>Support Reactions Results Viewer</h1>
<div>{{Esc(dto.Study.BaselineModelName)}} · {{dto.Study.CandidateCount}} candidate(s) · {{Esc(dto.Study.StudyMode)}}</div>
<div class="print-only" data-print-view></div>
<p>Global support reactions at the two Left and two Right foundation points selected in Output Settings. Forces are kN and moments are kNm. Points are discrete supports; chart markers are not joined into a structural curve. Each column's shaded band is the range among the candidates in scope (min to max), its darker core the middle half (P25–P75) and its line the median; <b>Band by</b> splits it per load, height or pretension candidate.</p></div>{{library}}</header>
<nav data-print-controls><label>Foundation <select id="group"><option value="all">Left + Right</option></select></label>
<label>Component <select id="component"><option>Fx</option><option>Fy</option><option selected>Fz</option><option>Mx</option><option>My</option><option>Mz</option></select></label>
<label>Result step <select id="step"></select></label><label>View <select id="view"><option value="supports">Individual supports</option><option value="totals">Foundation totals</option><option value="matrix">Candidate matrix</option></select></label>
<label>Band by <select id="band"><option value="">All candidates</option><option value="loadId">Load candidate</option><option value="heightId">Truss height</option><option value="pretensionId">Pretension</option></select></label>
<button id="reset">Reset filters</button><span id="scope"></span>{{ReportPrintEngine.ToolbarButton}}
{{ReportPrintEngine.Panel}}</nav><div id="filters" data-print-controls></div><div id="notice" hidden></div>
<main id="panels"></main><div id="warnings"></div><div id="tip" hidden></div>
<script type="application/json" id="data">{{data}}</script>
{{ReportPrintEngine.Scripts(print)}}
<script>{{Script}}</script></body></html>
""";
    }

    private static string Esc(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");

    private const string Style = """
:root{--acc:#1f4788;--bd:#c7ccd2;--su:#f3f4f6;--tx:#1a1a1a;--mu:#666;--neg:#c62828;--pos:#2e7d32}*{box-sizing:border-box}body{margin:0;font:13px/1.4 Segoe UI,Arial;color:var(--tx)}header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;padding:10px 14px;border-bottom:2px solid var(--acc)}h1{font-size:16px;margin:0}header>div>div,header p{margin:3px 0;color:var(--mu);font-size:11.5px;max-width:900px}.lib{font-size:12px}.lib summary{cursor:pointer;color:var(--acc);font-weight:600}.lib ul{list-style:none;margin:6px 0 0;padding:0;border:1px solid var(--bd);border-radius:3px;min-width:180px}.lib li{padding:4px 8px;border-bottom:1px solid var(--su);color:var(--mu)}.lib li.on{color:var(--tx)}.lib li span{float:right;font-size:10px;text-transform:uppercase;color:#9aa0a6}.lib li.on span{color:var(--pos)}nav,#filters{display:flex;gap:9px;align-items:center;padding:6px 14px;border-bottom:1px solid var(--bd);flex-wrap:wrap}select,button{font:12px Segoe UI,Arial;padding:3px 7px;border:1px solid #bbb;border-radius:3px;background:white;cursor:pointer}#scope{margin-left:auto;color:var(--mu)}.ms{position:relative}.ms>button{min-width:100px;text-align:left}.ms.active>button{color:var(--acc);border-color:var(--acc);font-weight:600}.pop{position:absolute;top:100%;left:0;z-index:30;margin-top:2px;min-width:240px;max-height:280px;overflow:auto;padding:4px 0;background:#fff;border:1px solid var(--bd);border-radius:3px;box-shadow:0 4px 14px #0002}.pop label{display:flex;align-items:center;gap:7px;padding:3px 10px;white-space:nowrap;font-size:11.5px;cursor:pointer}.pop label:hover{background:var(--su)}.pop .all{font-weight:600;border-bottom:1px solid var(--bd)}#notice{margin:7px 14px;padding:6px;background:#fff8e1;border-left:3px solid #f57c00}.panel{padding:8px 10px;border-bottom:1px solid #ccc}.title{color:var(--acc);font-weight:600}.sub{color:var(--mu);font-size:11px;margin-left:8px}.stage{display:grid;grid-template-columns:minmax(0,1fr) 220px}.chart{width:100%;height:350px}.legend{border-left:1px solid #ccc;padding:6px;max-height:350px;overflow:auto}.legend button{display:block;width:100%;text-align:left;border:0;font:11px Consolas}.legend button.on{background:#e8f0fe}.hit{cursor:crosshair}.matrix-wrap{overflow:auto;max-height:570px}.matrix{border-collapse:separate;border-spacing:1px;font:10px Consolas;white-space:nowrap}.matrix th{position:sticky;top:0;background:#eee;padding:4px}.matrix th:first-child,.matrix td:first-child{position:sticky;left:0;background:white;z-index:2}.matrix td{padding:3px 5px;text-align:right}.empty{padding:24px;color:var(--mu)}#warnings{padding:7px 14px;color:#8a6d00;font-size:11px}.legend h4{margin:8px 0 4px;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:var(--mu)}.legend h4:first-child{margin-top:0}.legend h4 span{text-transform:none;letter-spacing:0;font-weight:400;color:#9aa0a6}.legend .key{display:flex;align-items:center;gap:6px;font-size:11px;padding:2px 0}.legend .grp{font-size:11px;padding:2px 0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.legend .sw{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:6px;vertical-align:-1px}.legend .kb,.legend .kc,.legend .km{width:22px;flex:none}.legend .kb{height:12px;background:#1f478824;border:1px solid #1f47888c;border-radius:2px}.legend .kc{height:12px;background:#1f478852}.legend .km{height:0;border-top:2px solid #1f4788}.matrix tfoot td,.matrix tfoot td:first-child{background:#eef2f5;font-weight:700}#tip{position:fixed;pointer-events:none;z-index:50;max-width:320px;padding:6px 8px;border-radius:2px;background:#212121;color:#fff;font:11px/1.45 Consolas,monospace;white-space:pre-line;box-shadow:0 8px 16px #0003}[hidden]{display:none!important}
/* paper: the sheet, margins, header and footer come from the shared print engine; chart keeps its key beside it */
@media print{header{display:block;padding:0 0 5px}header p,.lib,nav,#filters,#tip{display:none!important}#notice{margin:6px 0}.panel{padding:8px 0;border-bottom:0;break-inside:avoid}.panel:has(.matrix-wrap){break-inside:auto}.stage{grid-template-columns:minmax(0,1fr) 190px}.chart{width:100%;height:auto}.legend{max-height:none;overflow:visible;font-size:10px}.legend h4 span{display:none}.legend button{font-size:9px;padding:0 2px}.matrix th,.matrix th:first-child,.matrix td:first-child{position:static}#warnings{padding:7px 0}}
body.pr-on .matrix thead th{white-space:normal;overflow-wrap:anywhere}
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
const detail=(k,v)=>{const s=ALL.find(x=>x[k]===v);return k==='loadId'?s.loadingLabel:k==='heightId'?`H ${(+s.trussHeightM).toFixed(3)} m`:s.pretensionLabel},colourOf=(k,v)=>k?PALETTE[IDS[k].indexOf(v)]||ACC:ACC;
Object.keys(DIMS).forEach(k=>{const o=[...B.options].find(o=>o.value===k),n=IDS[k].length;if(n>PALETTE.length){o.disabled=true;o.textContent+=` (${n} values · too many to colour)`}});
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
function charts(){let h='';gs().forEach(g=>{const s=scope(g);if(!s.length)return;const totals=V.value==='totals',auto=s.length>D.study.individualProfileLimit,k=B.value;h+=`<section class="panel"><span class="title">${esc(g.foundationSide)} foundation · ${C.value}</span><span class="sub">${esc(g.outputCase)} · ${esc(S.value)} · ${totals?'sum of two selected points':'two selected support points'} · ${s.length} candidates · band = range among candidates${k?` per ${DIMS[k].toLowerCase()}`:''}${auto?' · min/max envelope':''}</span><div class="stage">${chart(g,s,totals)}<aside class="legend">${legend(s,auto)}</aside></div></section>`});P.innerHTML=h||'<p class="empty">No matching reaction results.</p>';P.querySelectorAll('[data-key]').forEach(x=>x.onclick=()=>{hi=hi===x.dataset.key?null:x.dataset.key;render()});wireTips()}
function matrix(){let h='';gs().forEach(g=>{const s=scope(g),points=[...new Map(s.flatMap(rows).map(r=>[pointKey(r),r])).values()].sort((a,b)=>a.pointSequence-b.pointSequence),max=Math.max(1,...s.flatMap(x=>rows(x).map(r=>Math.abs(r[prop()])))),colVals=p=>s.map(x=>rows(x).find(r=>pointKey(r)===pointKey(p))?.[prop()]).filter(Number.isFinite),totVals=s.filter(x=>rows(x).length).map(total),agg=(label,f)=>`<tr><td>${label}</td>${points.map(p=>{const v=colVals(p);return `<td>${v.length?fmt(f(v)):'—'}</td>`}).join('')}<td>${totVals.length?fmt(f(totVals)):'—'}</td></tr>`;h+=`<section class="panel"><span class="title">${esc(g.foundationSide)} foundation · ${C.value} matrix</span><span class="sub">${esc(S.value)}</span><div class="matrix-wrap" data-print-fit><table class="matrix"><thead><tr><th>Candidate</th>${points.map(p=>`<th>${esc(p.pointName)}</th>`).join('')}<th>${esc(g.foundationSide)} total</th></tr></thead><tbody>${s.map(x=>{const map=new Map(rows(x).map(r=>[pointKey(r),r]));return `<tr><td>${esc(x.iterationKey)}</td>${points.map(p=>{const r=map.get(pointKey(p)),v=r?.[prop()];if(!Number.isFinite(v))return '<td>—</td>';const a=.1+.7*Math.min(1,Math.abs(v)/max),bg=v<0?`rgba(198,40,40,${a})`:`rgba(46,125,50,${a})`;return `<td style="background:${bg}" title="${esc(tip(x.iterationKey,g.foundationSide,r))}">${fmt(v)}</td>`}).join('')}<td>${fmt(total(x))}</td></tr>`}).join('')}</tbody><tfoot>${agg('Min',v=>Math.min(...v))}${agg('Max',v=>Math.max(...v))}${agg('Range',v=>Math.max(...v)-Math.min(...v))}</tfoot></table></div></section>`});P.innerHTML=h||'<p class="empty">No matching reaction results.</p>'}
function render(){const n=Math.max(0,...gs().map(g=>scope(g).length)),auto=n>D.study.individualProfileLimit;Q.textContent=`up to ${n} / ${D.study.candidateCount} candidates`;N.hidden=!auto||V.value==='matrix';N.textContent=auto?`Showing min/max reaction envelopes (range bands only) because ${n} candidates are in scope. Filter to ${D.study.individualProfileLimit} or fewer to see every candidate marker.`:'';V.value==='matrix'?matrix():charts()}
G.onchange=()=>{hi=null;refreshSteps();render()};C.onchange=render;S.onchange=render;V.onchange=render;B.onchange=render;document.getElementById('reset').onclick=()=>{Object.values(chosen).forEach(x=>x.clear());hi=null;document.getElementById('filters').innerHTML='';filters();render()};document.addEventListener('click',()=>document.querySelectorAll('.pop').forEach(x=>x.hidden=true));
if(OPT.group!=null){const i=D.groups.findIndex(g=>g.foundationSide.toLowerCase()===String(OPT.group).toLowerCase());if(i>=0)G.value=i}pick(C,OPT.component);pick(V,OPT.view);pick(B,OPT.band);
filters();refreshSteps();render();const w=document.getElementById('warnings');if(D.warnings.length)w.textContent=D.warnings.map(x=>`[${x.iterationKey||'study'}] ${x.message}`).join(' · ');
})();
""";
}
