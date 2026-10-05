using System.IO;
using System.Text.Json;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Results;

/// <summary>Self-contained comparison report for every SAP frame analysis-element/result-station row.</summary>
public sealed class MemberForceReportGenerator
{
    public const string ReportFileName = "Member_Forces_Report.html";
    /// <summary>Report name printed in the page header.</summary>
    public const string ReportTitle = "Member Forces";
    public const int IndividualProfileLimit = 50;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Generate(string studyFolder, IReadOnlyList<CandidateResultDto> results,
        string baselineName, IterationStudyMode studyMode, IReadOnlyList<string>? siblingReports = null,
        ReportPrintSettings? print = null)
    {
        Directory.CreateDirectory(studyFolder);
        var path = Path.Combine(studyFolder, ReportFileName);
        var siblings = siblingReports ?? ReportNav.PresentIn(studyFolder, "member").ToArray();
        AtomicFile.WriteAllText(path, BuildHtml(Build(results, baselineName, studyMode, siblings),
            ReportPrintEngine.Describe(print, ReportTitle, baselineName, DateTimeOffset.Now)), new System.Text.UTF8Encoding(false));
        return path;
    }

    public string RegenerateFromWorkbook(string studyFolder)
    {
        var workbook = Path.Combine(studyFolder, ConsolidatedResultWriter.WorkbookFileName);
        if (!File.Exists(workbook)) throw new FileNotFoundException($"No {ConsolidatedResultWriter.WorkbookFileName} to regenerate from.", workbook);
        var results = ConsolidatedResultWriter.Read(workbook);
        if (!results.Any(result => (result.MemberForceGroups?.Count ?? 0) > 0)) return "";
        return Generate(studyFolder, results, results.FirstOrDefault()?.BaselineModelName ?? "",
            results.FirstOrDefault()?.StudyMode ?? IterationStudyMode.Combined);
    }

    public MemberForceReportDto Build(IReadOnlyList<CandidateResultDto> results, string baselineName, IterationStudyMode studyMode,
        IReadOnlyList<string>? siblingReports = null)
    {
        var keys = results.SelectMany(result => result.MemberForceGroups ?? [])
            .Select(group => (group.SapGroupName, group.GroupName, group.OutputCase)).Distinct()
            .OrderBy(key => key.GroupName, StringComparer.Ordinal).ThenBy(key => key.OutputCase, StringComparer.Ordinal);
        var groups = keys.Select(key =>
        {
            var series = results.Select(result => (Result: result, Group: (result.MemberForceGroups ?? []).FirstOrDefault(group =>
                    group.SapGroupName == key.SapGroupName && group.OutputCase == key.OutputCase)))
                .Where(item => item.Group is not null).OrderBy(item => item.Result.Parameters.RunIndex)
                .Select(item =>
                {
                    var p = item.Result.Parameters;
                    var stations = item.Group!.Stations.Select(row => new MemberForceStationDto(
                        row.FrameName, row.FrameSequence, row.ElementName, row.ObjectStationM, row.ElementStationM,
                        row.ProfileCoordinateM, row.StepType, row.StepNumber, row.P, row.V2, row.V3,
                        row.T, row.M2, row.M3, row.Status.ToString())).ToArray();
                    return new MemberForceCandidateDto(item.Result.IterationKey, p.RunIndex, p.LoadingCandidateId,
                        p.HeightCandidateId, p.PretensionCandidateId, p.LoadingLabel, p.TrussHeightM,
                        p.PretensionLabel, stations);
                }).ToArray();
            return new MemberForceGroupReportDto(key.GroupName, key.SapGroupName, key.OutputCase, series);
        }).ToArray();
        var warnings = results.SelectMany(result => result.Warnings)
            .Where(warning => warning.Category.Equals("MemberForce", StringComparison.OrdinalIgnoreCase))
            .Select(warning => new ResultReportWarningDto(warning.IterationKey, warning.Category,
                warning.Severity.ToString(), warning.Message)).ToArray();
        return new MemberForceReportDto(new MemberForceStudyDto(baselineName, studyMode.ToString(), results.Count,
            DateTimeOffset.Now.ToString("o"), IndividualProfileLimit), groups, warnings, siblingReports ?? []);
    }

    public string BuildHtml(MemberForceReportDto dto, ReportPrintDto? print = null)
    {
        print ??= ReportPrintEngine.Describe(null, ReportTitle, dto.Study.BaselineModelName, DateTimeOffset.Now);
        var data = JsonSerializer.Serialize(dto, Json).Replace("</", "<\\/", StringComparison.Ordinal);
        var library = ReportNav.Library("member", new HashSet<string>(dto.SiblingReports, StringComparer.Ordinal));
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Member Forces — {{Esc(dto.Study.BaselineModelName)}}</title>
<style>{{ReportPrintEngine.Css}}{{Style}}</style>{{ReportPrintEngine.PageStyle}}</head><body><header><div><h1>Member Forces Results Viewer</h1>
<div>{{Esc(dto.Study.BaselineModelName)}} · {{dto.Study.CandidateCount}} candidate(s) · {{Esc(dto.Study.StudyMode)}}</div>
<div class="print-only" data-print-view></div>
<p>All SAP stations remain stored. The chart defaults to the start, middle and end of every SAP analysis element; choose All SAP stations when the complete station detail is needed. P/V2/V3 are kN; T/M2/M3 are kNm.</p></div>{{library}}</header>
<nav data-print-controls><label>Group <select id="group"><option value="all">All groups</option></select></label>
<label>Component <select id="component"><option>P</option><option>V2</option><option>V3</option><option>T</option><option>M2</option><option>M3</option></select></label>
<label>Result step <select id="step"></select></label><label>Positions <select id="sampling"><option value="summary">Start + middle + end per element</option><option value="all">All SAP stations</option></select></label><label>Display <select id="view"><option value="profiles">All profiles</option><option value="envelope">Envelope</option><option value="matrix">Station matrix</option></select></label><button id="reset">Reset filters</button><span id="scope"></span>{{ReportPrintEngine.ToolbarButton}}
{{ReportPrintEngine.Panel}}</nav>
<div id="filters" data-print-controls></div><div id="notice" hidden></div><main id="panels"></main><div id="warnings"></div><div id="tip" hidden></div>
<script type="application/json" id="data">{{data}}</script>
{{ReportPrintEngine.Scripts(print)}}
<script>{{Script}}</script></body></html>
""";
    }

    private static string Esc(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");

    private const string Style = """
*{box-sizing:border-box}body{margin:0;font:13px/1.4 Segoe UI,Arial;color:#1a1a1a}header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;padding:10px 14px;border-bottom:2px solid #1f4788}h1{font-size:16px;margin:0}header>div>div,header p{margin:3px 0;color:#666;font-size:11.5px}.lib{font-size:12px}.lib summary{cursor:pointer;color:#1f4788;font-weight:600}.lib ul{list-style:none;margin:6px 0 0;padding:0;border:1px solid #c7ccd2;border-radius:3px;min-width:180px}.lib li{padding:4px 8px;border-bottom:1px solid #f3f4f6;color:#666}.lib li.on{color:#1a1a1a}.lib li span{float:right;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:#9aa0a6}.lib li.on span{color:#2e7d32}nav,#filters{display:flex;gap:9px;align-items:center;padding:6px 14px;border-bottom:1px solid #c7ccd2;flex-wrap:wrap}select,button{font:12px Segoe UI,Arial;padding:3px 7px;border:1px solid #bbb;border-radius:3px;background:white;cursor:pointer}button:hover{background:#f3f4f6}#scope{margin-left:auto;color:#666}.ms{position:relative}.ms>button{min-width:100px;text-align:left}.ms.active>button{color:#1f4788;border-color:#1f4788;font-weight:600}.pop{position:absolute;top:100%;left:0;z-index:30;margin-top:2px;min-width:240px;max-height:280px;overflow:auto;padding:4px 0;background:#fff;border:1px solid #c7ccd2;border-radius:3px;box-shadow:0 4px 14px #0002}.pop label{display:flex;align-items:center;gap:7px;padding:3px 10px;white-space:nowrap;font-size:11.5px;cursor:pointer}.pop label:hover{background:#f3f4f6}.pop .all{font-weight:600;border-bottom:1px solid #c7ccd2}#notice{margin:7px 14px;padding:6px;background:#fff8e1;border-left:3px solid #f57c00}.panel{padding:8px 10px;border-bottom:1px solid #ccc}.title{color:#1f4788;font-weight:600}.sub{color:#666;font-size:11px;margin-left:8px}.stage{display:grid;grid-template-columns:minmax(0,1fr) 220px}.chart{width:100%;height:350px}.station-marker{pointer-events:none}.station-hit{cursor:crosshair}.legend{border-left:1px solid #ccc;padding:6px;max-height:350px;overflow:auto}.legend button{display:block;width:100%;text-align:left;border:0;font:11px Consolas}.legend button.on{background:#e8f0fe}.matrix-wrap{overflow:auto;max-height:570px}.matrix{border-collapse:separate;border-spacing:1px;font:10px Consolas;white-space:nowrap}.matrix th{position:sticky;top:0;background:#eee;padding:4px}.matrix th:first-child,.matrix td:first-child{position:sticky;left:0;background:white;z-index:2}.matrix td{padding:3px 5px;text-align:right}.empty{padding:24px;color:#666}#warnings{padding:7px 14px;color:#8a6d00;font-size:11px}#tip{position:fixed;pointer-events:none;z-index:50;max-width:290px;padding:6px 8px;border-radius:2px;background:#212121;color:#fff;font:11px/1.45 Consolas,monospace;white-space:pre-line;box-shadow:0 8px 16px #0003}[hidden]{display:none!important}
/* paper: the sheet, margins, header and footer come from the shared print engine; one chart per block, legend underneath */
@media print{header{display:block;padding:0 0 5px}header p,.lib,nav,#filters,#tip{display:none!important}#notice{margin:6px 0}.panel{padding:8px 0;border-bottom:0;break-inside:avoid}.panel:has(.matrix-wrap){break-inside:auto}.stage{display:block}.chart{width:100%;height:auto}.legend{border-left:0;border-top:1px solid #ccc;max-height:none;overflow:visible;padding:5px 0 0;columns:7;column-gap:10px}.legend button{font-size:9px;padding:0 2px;break-inside:avoid}.legend hr{display:none}.matrix th,.matrix th:first-child,.matrix td:first-child{position:static}#warnings{padding:7px 0}}
body.pr-on .matrix thead th{white-space:normal;overflow-wrap:anywhere}
""";

    private const string Script = """
(()=>{const D=JSON.parse(document.getElementById('data').textContent),G=document.getElementById('group'),C=document.getElementById('component'),S=document.getElementById('step'),A=document.getElementById('sampling'),V=document.getElementById('view'),P=document.getElementById('panels'),N=document.getElementById('notice'),Q=document.getElementById('scope'),TIP=document.getElementById('tip');
// "#print&group=TopChord&component=M3&view=envelope" from the PDF exporter preselects the toolbar
const OPT=(window.reportPrint&&window.reportPrint.options)||{},norm=x=>String(x).toLowerCase().replace(/[^a-z0-9]/g,''),pick=(select,value)=>{if(value!=null&&[...select.options].some(o=>o.value===value))select.value=value};
const chosen={loadId:new Set(),heightId:new Set(),pretensionId:new Set()},colors=['#1f77b4','#d62728','#2ca02c','#ff7f0e','#9467bd','#17becf'];let hi=null;
const esc=x=>String(x??'').replace(/[&<>"']/g,m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[m])),fmt=x=>Math.abs(x)>=100?x.toFixed(0):Math.abs(x)>=1?x.toFixed(2):x.toFixed(3),prop=()=>C.value[0].toLowerCase()+C.value.slice(1),unit=()=>['T','M2','M3'].includes(C.value)?'kNm':'kN',positionLabel=()=>A.value==='summary'?'start + middle + end per element':'all SAP stations';
D.groups.forEach((g,i)=>G.add(new Option(`${g.groupName} · ${g.outputCase}`,i)));const gs=()=>G.value==='all'?D.groups:[D.groups[+G.value]],scope=g=>g.series.filter(x=>[...Object.keys(chosen)].every(k=>!chosen[k].size||chosen[k].has(x[k]))),step=r=>`${r.stepType||'Static'} · ${Number(r.stepNumber).toLocaleString()}`,key=r=>r.sampleLabel?`${r.frameName}|${r.elementName}|${r.sampleLabel}`:`${r.frameName}|${r.elementName}|${r.objectStationM}|${r.elementStationM}`;
function filters(){const root=document.getElementById('filters'),labels={loadId:'Loading',heightId:'Height',pretensionId:'Pretension'},series=D.groups.flatMap(g=>g.series);Object.keys(labels).forEach(k=>{const vals=[...new Set(series.map(x=>x[k]))].sort((a,b)=>a.localeCompare(b,undefined,{numeric:true})),w=document.createElement('span'),b=document.createElement('button'),pop=document.createElement('div');w.className='ms';pop.className='pop';pop.hidden=true;const all=document.createElement('label');all.className='all';all.innerHTML='<input type="checkbox" checked> All';pop.append(all);const boxes=[];vals.forEach(v=>{const sample=series.find(x=>x[k]===v),detail=k==='loadId'?sample.loadingLabel:k==='heightId'?`H ${(+sample.trussHeightM).toFixed(3)} m`:sample.pretensionLabel,row=document.createElement('label'),cb=document.createElement('input');cb.type='checkbox';cb.value=v;row.append(cb,document.createTextNode(`${v} · ${detail}`));pop.append(row);boxes.push(cb);cb.onchange=()=>{cb.checked?chosen[k].add(v):chosen[k].delete(v);sync();render()}});function sync(){const n=chosen[k].size;b.textContent=`${labels[k]}: ${n===0?'all':n<=2?[...chosen[k]].join(', '):n+'/'+vals.length}`;w.classList.toggle('active',n>0);all.firstChild.checked=n===0;boxes.forEach(cb=>cb.checked=chosen[k].has(cb.value))}all.onclick=e=>{e.preventDefault();chosen[k].clear();sync();render()};b.onclick=e=>{e.stopPropagation();document.querySelectorAll('.pop').forEach(p=>{if(p!==pop)p.hidden=true});pop.hidden=!pop.hidden};w.append(b,pop);sync();root.append(w)})}
function refreshSteps(){const old=S.value,all=[...new Set(gs().flatMap(g=>g.series.flatMap(x=>x.stations.map(step))))].sort((a,b)=>a.localeCompare(b,undefined,{numeric:true}));S.innerHTML='';all.forEach(x=>S.add(new Option(x,x)));S.value=all.includes(old)?old:all.at(-1)||''}
const rawRows=x=>x.stations.filter(r=>step(r)===S.value).sort((a,b)=>a.profileCoordinateM-b.profileCoordinateM||a.frameSequence-b.frameSequence||a.elementStationM-b.elementStationM);
function rows(x){const all=rawRows(x);if(A.value==='all')return all;const elements=Object.values(all.reduce((m,r)=>{(m[r.frameName+'|'+r.elementName]??=[]).push(r);return m},{}));return elements.flatMap(element=>{element.sort((a,b)=>a.profileCoordinateM-b.profileCoordinateM||a.elementStationM-b.elementStationM);if(element.length<=3)return element.map((r,i)=>({...r,sampleLabel:element.length===1?'Only station':element.length===2?(i===0?'Start':'End'):(i===0?'Start':i===1?'Middle':'End')}));const lo=element[0].profileCoordinateM,up=element[element.length-1].profileCoordinateM,targets=[['Start',lo],['Middle',(lo+up)/2],['End',up]];return targets.map(([label,target])=>{const distance=Math.min(...element.map(r=>Math.abs(r.profileCoordinateM-target))),ties=element.filter(r=>Math.abs(Math.abs(r.profileCoordinateM-target)-distance)<1e-9),picked=ties.sort((a,b)=>Math.abs(b[prop()])-Math.abs(a[prop()])||a.elementStationM-b.elementStationM)[0];return {...picked,sampleLabel:label}})}).sort((a,b)=>a.profileCoordinateM-b.profileCoordinateM||a.frameSequence-b.frameSequence||a.elementStationM-b.elementStationM)}
const segs=rs=>Object.values(rs.reduce((m,r)=>{(m[r.frameName+'|'+r.elementName]??=[]).push(r);return m},{}));
const stationTip=(candidate,r)=>`${candidate}${r.sampleLabel?' · '+r.sampleLabel:''}\n${C.value}: ${fmt(r[prop()])} ${unit()}\nFrame object: ${r.frameName}\nAnalysis element: ${r.elementName}\nObjSta: ${fmt(r.objectStationM)} m\nElmSta: ${fmt(r.elementStationM)} m`;
function wireTips(){P.querySelectorAll('.station-hit').forEach(x=>{x.onmouseenter=()=>{TIP.textContent=x.dataset.tip;TIP.hidden=false};x.onmousemove=e=>{TIP.style.left=Math.min(innerWidth-310,e.clientX+12)+'px';TIP.style.top=Math.min(innerHeight-130,e.clientY+12)+'px'};x.onmouseleave=()=>TIP.hidden=true})}
function chart(g,series,envelope){const W=1000,H=350,m={l:66,r:15,t:22,b:46},rr=series.flatMap(rows),vv=rr.map(r=>r[prop()]).filter(Number.isFinite),xx=rr.map(r=>r.profileCoordinateM);let lo=vv.length?Math.min(0,...vv):-1,up=vv.length?Math.max(0,...vv):1;if(lo===up){lo--;up++}const pad=(up-lo)*.06;lo-=pad;up+=pad;const xmin=xx.length?Math.min(...xx):0,xmax=xx.length?Math.max(...xx):1,x=v=>m.l+(v-xmin)/Math.max(.0001,xmax-xmin)*(W-m.l-m.r),y=v=>m.t+(up-v)/(up-lo)*(H-m.t-m.b);let h=`<svg class="chart" viewBox="0 0 ${W} ${H}"><rect x="${m.l}" y="${m.t}" width="${W-m.l-m.r}" height="${H-m.t-m.b}" fill="white" stroke="#ccc"/>`;for(let i=0;i<=5;i++){const v=lo+(up-lo)*i/5,Y=y(v);h+=`<line x1="${m.l}" x2="${W-m.r}" y1="${Y}" y2="${Y}" stroke="#eee"/><text x="${m.l-6}" y="${Y+3}" text-anchor="end" font-size="10">${fmt(v)}</text>`}const path=(a,p)=>a.map((r,i)=>`${i?'L':'M'}${x(r.profileCoordinateM)},${y(p(r))}`).join(' ');
if(envelope){const map=new Map();series.forEach(s=>rows(s).forEach(r=>{const e=map.get(key(r))||{...r,v:[]};e.v.push(r[prop()]);map.set(key(r),e)}));segs([...map.values()]).forEach(a=>{h+=`<path d="${path(a,r=>Math.min(...r.v))}" fill="none" stroke="#c62828" stroke-width="2"/><path d="${path(a,r=>Math.max(...r.v))}" fill="none" stroke="#2e7d32" stroke-width="2"/>`;a.forEach(r=>{const mn=Math.min(...r.v),mx=Math.max(...r.v),tip=`Envelope\n${C.value}: ${fmt(mn)} to ${fmt(mx)} ${unit()}\nFrame object: ${r.frameName}\nAnalysis element: ${r.elementName}\nObjSta: ${fmt(r.objectStationM)} m\nElmSta: ${fmt(r.elementStationM)} m`;h+=`<circle class="station-hit" cx="${x(r.profileCoordinateM)}" cy="${y(mn)}" r="7" fill="transparent" data-tip="${esc(tip)}"/><circle class="station-hit" cx="${x(r.profileCoordinateM)}" cy="${y(mx)}" r="7" fill="transparent" data-tip="${esc(tip)}"/>`})})}else series.forEach((s,i)=>segs(rows(s)).forEach(a=>{const col=colors[i%colors.length];h+=`<path data-key="${esc(s.iterationKey)}" d="${path(a,r=>r[prop()])}" fill="none" stroke="${col}" stroke-width="${hi===s.iterationKey?3:1.3}" opacity="${hi&&hi!==s.iterationKey ? .13 : .75}"><title>${esc(s.iterationKey)} · ${esc(a[0]?.frameName)} / ${esc(a[0]?.elementName)}</title></path>`;a.forEach(r=>h+=`<circle class="station-marker" cx="${x(r.profileCoordinateM)}" cy="${y(r[prop()])}" r="2" fill="${col}" opacity="${hi&&hi!==s.iterationKey ? .13 : .8}"/><circle class="station-hit" cx="${x(r.profileCoordinateM)}" cy="${y(r[prop()])}" r="7" fill="transparent" data-tip="${esc(stationTip(s.iterationKey,r))}"/>`) }));
const frames=[...new Map(rr.map(r=>[r.frameName,r])).values()].sort((a,b)=>a.frameSequence-b.frameSequence);frames.forEach((r,i)=>{if(i%Math.max(1,Math.ceil(frames.length/14))===0)h+=`<text x="${x(r.profileCoordinateM)}" y="${H-m.b+16}" text-anchor="middle" font-size="9">${esc(r.frameName)}</text>`});return h+`<text x="500" y="344" text-anchor="middle" font-size="11">Distance along selected group [m]</text><text transform="translate(15 185) rotate(-90)" text-anchor="middle" font-size="11">${C.value} [${unit()}]</text></svg>`}
function profiles(list){let h='';list.forEach(g=>{const s=scope(g);if(!s.length)return;const auto=V.value==='profiles'&&s.length>D.study.individualProfileLimit,env=V.value==='envelope'||auto;h+=`<section class="panel"><span class="title">${esc(g.groupName)} · ${C.value}</span><span class="sub">${esc(g.outputCase)} · ${esc(S.value)} · ${positionLabel()} · ${s.length} candidates${auto?' · automatic envelope':''}</span><div class="stage">${chart(g,s,env)}<aside class="legend">${env?'Red = minimum<br>Green = maximum<hr>':''}${s.slice(0,100).map(x=>`<button data-key="${esc(x.iterationKey)}" class="${hi===x.iterationKey?'on':''}">${esc(x.iterationKey)}</button>`).join('')}</aside></div></section>`});P.innerHTML=h||'<p class="empty">No matching results.</p>';P.querySelectorAll('[data-key]').forEach(x=>x.onclick=()=>{hi=hi===x.dataset.key?null:x.dataset.key;render()});wireTips()}
function matrix(list){let h='';list.forEach(g=>{const s=scope(g),cols=rows(s[0]||{stations:[]}),max=Math.max(1,...s.flatMap(x=>rows(x).map(r=>Math.abs(r[prop()]))));h+=`<section class="panel"><span class="title">${esc(g.groupName)} · ${C.value} station matrix</span><span class="sub">${cols.length} displayed positions · ${positionLabel()}</span><div class="matrix-wrap" data-print-fit><table class="matrix"><thead><tr><th>Candidate</th>${cols.map(r=>`<th title="ObjSta ${fmt(r.objectStationM)} m; ElmSta ${fmt(r.elementStationM)} m">${esc(r.sampleLabel||r.frameName)} · ${esc(r.frameName)}/${esc(r.elementName)} @ ${fmt(r.objectStationM)}</th>`).join('')}</tr></thead><tbody>${s.map(x=>{const map=new Map(rows(x).map(r=>[key(r),r]));return `<tr><td>${esc(x.iterationKey)}</td>${cols.map(c=>{const r=map.get(key(c)),v=r?.[prop()];if(!Number.isFinite(v))return '<td>—</td>';const a=.1+.7*Math.min(1,Math.abs(v)/max),bg=v<0?`rgba(198,40,40,${a})`:`rgba(46,125,50,${a})`;return `<td style="background:${bg}" title="Frame ${esc(c.frameName)}; element ${esc(c.elementName)}; ObjSta ${fmt(c.objectStationM)} m; ElmSta ${fmt(c.elementStationM)} m">${fmt(v)}</td>`}).join('')}</tr>`}).join('')}</tbody></table></div></section>`});P.innerHTML=h||'<p class="empty">No matching results.</p>'}
function render(){const list=gs(),n=Math.max(0,...list.map(g=>scope(g).length)),auto=V.value==='profiles'&&n>D.study.individualProfileLimit;Q.textContent=`up to ${n} / ${D.study.candidateCount} candidates`;N.hidden=!auto;N.textContent=auto?`Showing a station-by-station envelope because ${n} candidates are in scope. Filter to ${D.study.individualProfileLimit} or fewer for every profile.`:'';V.value==='matrix'?matrix(list):profiles(list)}
G.onchange=()=>{hi=null;refreshSteps();render()};C.onchange=render;S.onchange=render;A.onchange=render;V.onchange=render;document.getElementById('reset').onclick=()=>{Object.values(chosen).forEach(x=>x.clear());hi=null;A.value='summary';document.getElementById('filters').innerHTML='';filters();render()};document.addEventListener('click',()=>document.querySelectorAll('.pop').forEach(x=>x.hidden=true));
if(OPT.group!=null){const i=D.groups.findIndex(g=>norm(g.groupName)===norm(OPT.group));if(i>=0)G.value=i}pick(C,OPT.component);pick(A,OPT.sampling);pick(V,OPT.view);
filters();refreshSteps();render();const w=document.getElementById('warnings');if(D.warnings.length)w.textContent=D.warnings.map(x=>`[${x.iterationKey||'study'}] ${x.message}`).join(' · ');
})();
""";
}
