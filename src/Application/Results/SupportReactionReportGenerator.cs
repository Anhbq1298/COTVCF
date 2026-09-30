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
    public const int IndividualProfileLimit = 50;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Generate(string studyFolder, IReadOnlyList<CandidateResultDto> results,
        string baselineName, IterationStudyMode studyMode, IReadOnlyList<string>? siblingReports = null)
    {
        Directory.CreateDirectory(studyFolder);
        var path = Path.Combine(studyFolder, ReportFileName);
        var siblings = siblingReports ?? ReportNav.PresentIn(studyFolder, "support").ToArray();
        AtomicFile.WriteAllText(path, BuildHtml(Build(results, baselineName, studyMode, siblings)),
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
            IndividualProfileLimit), groups, ReportCandidateDefinitions.Build(results), warnings, siblingReports ?? []);
    }

    public string BuildHtml(SupportReactionReportDto dto)
    {
        var data = JsonSerializer.Serialize(dto, Json).Replace("</", "<\\/", StringComparison.Ordinal);
        var library = ReportNav.Library("support", new HashSet<string>(dto.SiblingReports, StringComparer.Ordinal));
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Support Reactions — {{Esc(dto.Study.BaselineModelName)}}</title>
<style>{{Style}}{{ReportCandidateDefinitions.Style}}</style></head><body><header><div><h1>Support Reactions Results Viewer</h1>
<div>{{Esc(dto.Study.BaselineModelName)}} · {{dto.Study.CandidateCount}} candidate(s) · {{Esc(dto.Study.StudyMode)}}</div>
<p>Global support reactions at the two Left and two Right foundation points selected in Output Settings. Forces are kN and moments are kNm. Points are discrete supports; chart markers are not joined into a structural curve.</p></div>{{library}}</header>
<nav><label>Foundation <select id="group"><option value="all">Left + Right</option></select></label>
<label>Component <select id="component"><option>Fx</option><option>Fy</option><option selected>Fz</option><option>Mx</option><option>My</option><option>Mz</option></select></label>
<label>Result step <select id="step"></select></label><label>View <select id="view"><option value="supports">Individual supports</option><option value="totals">Foundation totals</option><option value="matrix">Candidate matrix</option></select></label>
<button id="reset">Reset filters</button><span id="scope"></span></nav><div id="filters"></div><div id="notice" hidden></div>
<main id="panels"></main>{{ReportCandidateDefinitions.Markup}}<div id="warnings"></div><div id="tip" hidden></div>
<script type="application/json" id="report-data">{{data}}</script><script>{{ReportCandidateDefinitions.Script}}</script><script>{{Script}}</script></body></html>
""";
    }

    private static string Esc(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");

    private const string Style = """
:root{--acc:#1f4788;--bd:#c7ccd2;--su:#f3f4f6;--tx:#1a1a1a;--mu:#666;--neg:#c62828;--pos:#2e7d32}*{box-sizing:border-box}body{margin:0;font:13px/1.4 Segoe UI,Arial;color:var(--tx)}header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;padding:10px 14px;border-bottom:2px solid var(--acc)}h1{font-size:16px;margin:0}header>div>div,header p{margin:3px 0;color:var(--mu);font-size:11.5px;max-width:900px}.lib{font-size:12px}.lib summary{cursor:pointer;color:var(--acc);font-weight:600}.lib ul{list-style:none;margin:6px 0 0;padding:0;border:1px solid var(--bd);border-radius:3px;min-width:180px}.lib li{padding:4px 8px;border-bottom:1px solid var(--su);color:var(--mu)}.lib li.on{color:var(--tx)}.lib li span{float:right;font-size:10px;text-transform:uppercase;color:#9aa0a6}.lib li.on span{color:var(--pos)}nav,#filters{display:flex;gap:9px;align-items:center;padding:6px 14px;border-bottom:1px solid var(--bd);flex-wrap:wrap}select,button{font:12px Segoe UI,Arial;padding:3px 7px;border:1px solid #bbb;border-radius:3px;background:white;cursor:pointer}#scope{margin-left:auto;color:var(--mu)}.ms{position:relative}.ms>button{min-width:100px;text-align:left}.ms.active>button{color:var(--acc);border-color:var(--acc);font-weight:600}.pop{position:absolute;top:100%;left:0;z-index:30;margin-top:2px;min-width:240px;max-height:280px;overflow:auto;padding:4px 0;background:#fff;border:1px solid var(--bd);border-radius:3px;box-shadow:0 4px 14px #0002}.pop label{display:flex;align-items:center;gap:7px;padding:3px 10px;white-space:nowrap;font-size:11.5px;cursor:pointer}.pop label:hover{background:var(--su)}.pop .all{font-weight:600;border-bottom:1px solid var(--bd)}#notice{margin:7px 14px;padding:6px;background:#fff8e1;border-left:3px solid #f57c00}.panel{padding:8px 10px;border-bottom:1px solid #ccc}.title{color:var(--acc);font-weight:600}.sub{color:var(--mu);font-size:11px;margin-left:8px}.stage{display:grid;grid-template-columns:minmax(0,1fr) 220px}.chart{width:100%;height:350px}.legend{border-left:1px solid #ccc;padding:6px;max-height:350px;overflow:auto}.legend button{display:block;width:100%;text-align:left;border:0;font:11px Consolas}.legend button.on{background:#e8f0fe}.hit{cursor:crosshair}.matrix-wrap{overflow:auto;max-height:570px}.matrix{border-collapse:separate;border-spacing:1px;font:10px Consolas;white-space:nowrap}.matrix th{position:sticky;top:0;background:#eee;padding:4px}.matrix th:first-child,.matrix td:first-child{position:sticky;left:0;background:white;z-index:2}.matrix td{padding:3px 5px;text-align:right}.empty{padding:24px;color:var(--mu)}#warnings{padding:7px 14px;color:#8a6d00;font-size:11px}#tip{position:fixed;pointer-events:none;z-index:50;max-width:320px;padding:6px 8px;border-radius:2px;background:#212121;color:#fff;font:11px/1.45 Consolas,monospace;white-space:pre-line;box-shadow:0 8px 16px #0003}[hidden]{display:none!important}
""";

    private const string Script = """
(()=>{const D=JSON.parse(document.getElementById('report-data').textContent),G=document.getElementById('group'),C=document.getElementById('component'),S=document.getElementById('step'),V=document.getElementById('view'),P=document.getElementById('panels'),N=document.getElementById('notice'),Q=document.getElementById('scope'),TIP=document.getElementById('tip');
const chosen={loadId:new Set(),heightId:new Set(),pretensionId:new Set()},colors=['#1f77b4','#d62728','#2ca02c','#ff7f0e','#9467bd','#17becf'];let hi=null;
const esc=x=>String(x??'').replace(/[&<>"']/g,m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[m])),fmt=x=>Math.abs(x)>=100?x.toFixed(0):Math.abs(x)>=1?x.toFixed(2):x.toFixed(3),prop=()=>C.value[0].toLowerCase()+C.value.slice(1),unit=()=>C.value[0]==='M'?'kNm':'kN',step=r=>`${r.stepType||'Static'} · ${Number(r.stepNumber).toLocaleString()}`;
D.groups.forEach((g,i)=>G.add(new Option(`${g.foundationSide} · ${g.outputCase}`,i)));const gs=()=>G.value==='all'?D.groups:[D.groups[+G.value]],scope=g=>g.series.filter(x=>Object.keys(chosen).every(k=>!chosen[k].size||chosen[k].has(x[k]))),rows=x=>x.rows.filter(r=>r.status==='Valid'&&step(r)===S.value&&Number.isFinite(r[prop()]));
function filters(){const root=document.getElementById('filters'),labels={loadId:'Loading',heightId:'Height',pretensionId:'Pretension'},series=D.groups.flatMap(g=>g.series);Object.keys(labels).forEach(k=>{const vals=[...new Set(series.map(x=>x[k]))].sort((a,b)=>a.localeCompare(b,undefined,{numeric:true})),w=document.createElement('span'),b=document.createElement('button'),pop=document.createElement('div');w.className='ms';pop.className='pop';pop.hidden=true;const all=document.createElement('label');all.className='all';all.innerHTML='<input type="checkbox" checked> All';pop.append(all);const boxes=[];vals.forEach(v=>{const sample=series.find(x=>x[k]===v),detail=k==='loadId'?sample.loadingLabel:k==='heightId'?`H ${(+sample.trussHeightM).toFixed(3)} m`:sample.pretensionLabel,row=document.createElement('label'),cb=document.createElement('input');cb.type='checkbox';cb.value=v;row.append(cb,document.createTextNode(`${v} · ${detail}`));pop.append(row);boxes.push(cb);cb.onchange=()=>{cb.checked?chosen[k].add(v):chosen[k].delete(v);sync();render()}});function sync(){const n=chosen[k].size;b.textContent=`${labels[k]}: ${n===0?'all':n<=2?[...chosen[k]].join(', '):n+'/'+vals.length}`;w.classList.toggle('active',n>0);all.firstChild.checked=n===0;boxes.forEach(cb=>cb.checked=chosen[k].has(cb.value))}all.onclick=e=>{e.preventDefault();chosen[k].clear();sync();render()};b.onclick=e=>{e.stopPropagation();document.querySelectorAll('.pop').forEach(p=>{if(p!==pop)p.hidden=true});pop.hidden=!pop.hidden};w.append(b,pop);sync();root.append(w)})}
function refreshSteps(){const old=S.value,all=[...new Set(gs().flatMap(g=>g.series.flatMap(x=>x.rows.filter(r=>r.status==='Valid').map(step))))].sort((a,b)=>a.localeCompare(b,undefined,{numeric:true}));S.innerHTML='';all.forEach(x=>S.add(new Option(x,x)));S.value=all.includes(old)?old:all.at(-1)||''}
const pointKey=r=>r.pointSequence+'|'+r.pointName,total=x=>rows(x).reduce((sum,r)=>sum+r[prop()],0),tip=(candidate,side,r)=>`${candidate} · ${side}\n${C.value}: ${fmt(r[prop()])} ${unit()}\nPoint: ${r.pointName}\nGlobal: (${fmt(r.x)}, ${fmt(r.y)}, ${fmt(r.z)}) m\n${S.value}`;
function wireTips(){P.querySelectorAll('.hit').forEach(x=>{x.onmouseenter=()=>{TIP.textContent=x.dataset.tip;TIP.hidden=false};x.onmousemove=e=>{TIP.style.left=Math.min(innerWidth-330,e.clientX+12)+'px';TIP.style.top=Math.min(innerHeight-150,e.clientY+12)+'px'};x.onmouseleave=()=>TIP.hidden=true})}
function chart(g,series,totals){const W=1000,H=350,m={l:68,r:18,t:22,b:50},points=totals?[{pointName:g.foundationSide,pointSequence:1}]:[...new Map(series.flatMap(rows).map(r=>[pointKey(r),r])).values()].sort((a,b)=>a.pointSequence-b.pointSequence),values=totals?series.map(total):series.flatMap(rows).map(r=>r[prop()]);let lo=values.length?Math.min(0,...values):-1,up=values.length?Math.max(0,...values):1;if(lo===up){lo--;up++}const pad=(up-lo)*.06;lo-=pad;up+=pad;const x=i=>m.l+(i+.5)/Math.max(1,points.length)*(W-m.l-m.r),y=v=>m.t+(up-v)/(up-lo)*(H-m.t-m.b),auto=series.length>D.study.individualProfileLimit;let h=`<svg class="chart" viewBox="0 0 ${W} ${H}"><rect x="${m.l}" y="${m.t}" width="${W-m.l-m.r}" height="${H-m.t-m.b}" fill="white" stroke="#ccc"/>`;for(let i=0;i<=5;i++){const v=lo+(up-lo)*i/5,Y=y(v);h+=`<line x1="${m.l}" x2="${W-m.r}" y1="${Y}" y2="${Y}" stroke="#eee"/><text x="${m.l-6}" y="${Y+3}" text-anchor="end" font-size="10">${fmt(v)}</text>`}points.forEach((p,i)=>h+=`<text x="${x(i)}" y="${H-m.b+18}" text-anchor="middle" font-size="10">${esc(p.pointName)}</text>`);
if(auto)points.forEach((p,i)=>{const vv=totals?series.map(total):series.flatMap(s=>rows(s).filter(r=>pointKey(r)===pointKey(p)).map(r=>r[prop()]));if(!vv.length)return;const mn=Math.min(...vv),mx=Math.max(...vv),tx=x(i);h+=`<line x1="${tx}" x2="${tx}" y1="${y(mn)}" y2="${y(mx)}" stroke="#1f4788" stroke-width="3"/><circle class="hit" cx="${tx}" cy="${y(mn)}" r="6" fill="#c62828" data-tip="${esc(`${g.foundationSide} envelope\n${C.value}: ${fmt(mn)} to ${fmt(mx)} ${unit()}\n${S.value}`)}"/><circle class="hit" cx="${tx}" cy="${y(mx)}" r="6" fill="#2e7d32" data-tip="${esc(`${g.foundationSide} envelope\n${C.value}: ${fmt(mn)} to ${fmt(mx)} ${unit()}\n${S.value}`)}"/>`})
else series.forEach((s,si)=>{const rr=totals?[{...points[0],[prop()]:total(s),x:0,y:0,z:0}]:rows(s);rr.forEach(r=>{const pi=totals?0:points.findIndex(p=>pointKey(p)===pointKey(r)),cx=x(pi)+(si-series.length/2)*Math.min(5,28/Math.max(1,series.length)),cy=y(r[prop()]),t=totals?`${s.iterationKey} · ${g.foundationSide} total\n${C.value}: ${fmt(r[prop()])} ${unit()}\n${S.value}`:tip(s.iterationKey,g.foundationSide,r);h+=`<circle class="hit" cx="${cx}" cy="${cy}" r="${hi===s.iterationKey?5:3}" fill="${colors[si%colors.length]}" opacity="${(hi&&hi!==s.iterationKey) ? .12 : .78}" data-key="${esc(s.iterationKey)}" data-tip="${esc(t)}"/>`})});return h+`<text transform="translate(15 185) rotate(-90)" text-anchor="middle" font-size="11">${C.value} [${unit()}]</text></svg>`}
function charts(){let h='';gs().forEach(g=>{const s=scope(g);if(!s.length)return;const totals=V.value==='totals',auto=s.length>D.study.individualProfileLimit;h+=`<section class="panel"><span class="title">${esc(g.foundationSide)} foundation · ${C.value}</span><span class="sub">${esc(g.outputCase)} · ${esc(S.value)} · ${totals?'sum of two selected points':'two selected support points'} · ${s.length} candidates${auto?' · min/max envelope':''}</span><div class="stage">${chart(g,s,totals)}<aside class="legend">${auto?'Red = minimum<br>Green = maximum<hr>':s.slice(0,100).map(x=>`<button data-key="${esc(x.iterationKey)}" class="${hi===x.iterationKey?'on':''}">${esc(x.iterationKey)}</button>`).join('')}</aside></div></section>`});P.innerHTML=h||'<p class="empty">No matching reaction results.</p>';P.querySelectorAll('[data-key]').forEach(x=>x.onclick=()=>{hi=hi===x.dataset.key?null:x.dataset.key;render()});wireTips()}
function matrix(){let h='';gs().forEach(g=>{const s=scope(g),points=[...new Map(s.flatMap(rows).map(r=>[pointKey(r),r])).values()].sort((a,b)=>a.pointSequence-b.pointSequence),max=Math.max(1,...s.flatMap(x=>rows(x).map(r=>Math.abs(r[prop()]))));h+=`<section class="panel"><span class="title">${esc(g.foundationSide)} foundation · ${C.value} matrix</span><span class="sub">${esc(S.value)}</span><div class="matrix-wrap"><table class="matrix"><thead><tr><th>Candidate</th>${points.map(p=>`<th>${esc(p.pointName)}</th>`).join('')}<th>${esc(g.foundationSide)} total</th></tr></thead><tbody>${s.map(x=>{const map=new Map(rows(x).map(r=>[pointKey(r),r]));return `<tr><td>${esc(x.iterationKey)}</td>${points.map(p=>{const r=map.get(pointKey(p)),v=r?.[prop()];if(!Number.isFinite(v))return '<td>—</td>';const a=.1+.7*Math.min(1,Math.abs(v)/max),bg=v<0?`rgba(198,40,40,${a})`:`rgba(46,125,50,${a})`;return `<td style="background:${bg}" title="${esc(tip(x.iterationKey,g.foundationSide,r))}">${fmt(v)}</td>`}).join('')}<td>${fmt(total(x))}</td></tr>`}).join('')}</tbody></table></div></section>`});P.innerHTML=h||'<p class="empty">No matching reaction results.</p>'}
function render(){const n=Math.max(0,...gs().map(g=>scope(g).length)),auto=n>D.study.individualProfileLimit;Q.textContent=`up to ${n} / ${D.study.candidateCount} candidates`;N.hidden=!auto||V.value==='matrix';N.textContent=auto?`Showing min/max reaction envelopes because ${n} candidates are in scope. Filter to ${D.study.individualProfileLimit} or fewer to inspect every candidate marker.`:'';V.value==='matrix'?matrix():charts()}
G.onchange=()=>{hi=null;refreshSteps();render()};C.onchange=render;S.onchange=render;V.onchange=render;document.getElementById('reset').onclick=()=>{Object.values(chosen).forEach(x=>x.clear());hi=null;document.getElementById('filters').innerHTML='';filters();render()};document.addEventListener('click',()=>document.querySelectorAll('.pop').forEach(x=>x.hidden=true));filters();refreshSteps();render();const w=document.getElementById('warnings');if(D.warnings.length)w.textContent=D.warnings.map(x=>`[${x.iterationKey||'study'}] ${x.message}`).join(' · ');
})();
""";
}
