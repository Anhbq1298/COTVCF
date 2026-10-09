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
    /// <summary>Tiny pages that open this report straight on one group's Summary (Tab 06 modules 02 / 03 / 05).
    /// A URL fragment cannot be passed through the Windows shell, so the deep link lives in a file.</summary>
    public const string TopChordFileName = "Top_Chord_Report.html", BottomChordFileName = "Bottom_Chord_Report.html",
        MegaColumnFileName = "Mega_Column_Report.html";
    public const int IndividualProfileLimit = 50;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Generate(string studyFolder, IReadOnlyList<CandidateResultDto> results,
        string baselineName, IterationStudyMode studyMode, IReadOnlyList<string>? siblingReports = null,
        ReportPrintSettings? print = null, ReportScope? scope = null)
    {
        Directory.CreateDirectory(studyFolder);
        var path = Path.Combine(studyFolder, ReportFileName);
        var siblings = siblingReports ?? ReportNav.PresentIn(studyFolder, "member").ToArray();
        var dto = Build(results, baselineName, studyMode, siblings, scope);
        AtomicFile.WriteAllText(path, BuildHtml(dto,
            ReportPrintEngine.Describe(print, scope?.Title(ReportTitle) ?? ReportTitle, baselineName, DateTimeOffset.Now)), new System.Text.UTF8Encoding(false));
        WriteSummaryLauncher(studyFolder, TopChordFileName, MemberGroupSummary.Top, dto, baselineName);
        WriteSummaryLauncher(studyFolder, BottomChordFileName, MemberGroupSummary.Bottom, dto, baselineName);
        WriteSummaryLauncher(studyFolder, MegaColumnFileName, MemberGroupSummary.MegaColumn, dto, baselineName);
        return path;
    }

    /// <summary>Writes (or, when the study has no such group, removes) the page that opens the report on that
    /// group's Summary.</summary>
    private static void WriteSummaryLauncher(string studyFolder, string fileName, string kind, MemberForceReportDto dto, string baselineName)
    {
        var path = Path.Combine(studyFolder, fileName);
        if (!dto.Groups.Any(group => group.Summary?.Kind == kind))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var target = $"{ReportFileName}#summary={kind}";
        var title = MemberGroupSummary.Title(kind);
        AtomicFile.WriteAllText(path,
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{title} — {Esc(baselineName)}</title>" +
            $"<script>location.replace(\"{target}\")</script></head>" +
            $"<body style=\"font:13px Segoe UI,Arial;margin:24px\"><a href=\"{target}\">Open the {title} summary</a></body></html>",
            new System.Text.UTF8Encoding(false));
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
        IReadOnlyList<string>? siblingReports = null, ReportScope? scope = null)
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
            // A chord or mega-column group also carries its governing force per iteration (a chord: with a
            // consolidated station profile).
            var kind = MemberGroupSummary.Kind(key.GroupName);
            var summary = kind is null ? null : new MemberGroupSummaryDto(kind, MemberGroupSummary.Title(kind), MemberGroupSummary.HasStations(kind),
                series.Select(candidate => MemberGroupSummary.Summarise(kind, candidate.IterationKey, candidate.Stations)).ToArray());
            return new MemberForceGroupReportDto(key.GroupName, key.SapGroupName, key.OutputCase, series, summary);
        }).ToArray();
        var warnings = results.SelectMany(result => result.Warnings)
            .Where(warning => warning.Category.Equals("MemberForce", StringComparison.OrdinalIgnoreCase))
            .Select(warning => new ResultReportWarningDto(warning.IterationKey, warning.Category,
                warning.Severity.ToString(), warning.Message)).ToArray();
        return new MemberForceReportDto(new MemberForceStudyDto(baselineName, studyMode.ToString(), results.Count,
            DateTimeOffset.Now.ToString("o"), IndividualProfileLimit, scope?.ToDto()), groups,
            ReportCandidateDefinitions.Build(results), warnings, siblingReports ?? []);
    }

    public string BuildHtml(MemberForceReportDto dto, ReportPrintDto? print = null)
    {
        print ??= ReportPrintEngine.Describe(null, ReportTitle, dto.Study.BaselineModelName, DateTimeOffset.Now);
        var data = JsonSerializer.Serialize(dto, Json).Replace("</", "<\\/", StringComparison.Ordinal);
        var library = ReportNav.Library("member", new HashSet<string>(dto.SiblingReports, StringComparer.Ordinal));
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Member Forces — {{Esc(dto.Study.BaselineModelName)}}</title>
<style>{{ReportPrintEngine.Css}}{{Style}}{{ReportSummaryTheme.Css}}{{ReportCandidateDefinitions.Style}}{{ReportScope.Css}}</style>{{ReportPrintEngine.PageStyle}}</head><body><header><div data-pr="info"><h1>Member Forces Results Viewer</h1>
<div>{{Esc(dto.Study.BaselineModelName)}} · {{dto.Study.CandidateCount}} candidate(s) · {{Esc(dto.Study.StudyMode)}}</div>
{{ReportScope.Banner(dto.Study.Scope)}}
<div class="print-only" data-print-view></div>
<p data-pr="note">Top Chord, Bottom Chord and Mega Column open on their <b>Summary</b>: the governing axial force of every pretension candidate and truss height. The chords add <b>View by Station</b> for the force along the chord. Under <b>Display</b>, All profiles / Envelope / Station matrix show every component at the stored SAP stations — start, middle and end of each analysis element by default, or All SAP stations. P/V2/V3 are kN; T/M2/M3 are kNm.</p></div>{{library}}</header>
<nav data-print-controls><span id="groupFilter"></span><select id="group" hidden><option value="all">All groups</option><option value="multiple">Multiple groups</option></select>
<label>Component <select id="component"><option>P</option><option>V2</option><option>V3</option><option>T</option><option>M2</option><option>M3</option></select></label>
<label>Result step <select id="step"></select></label><label>Positions <select id="sampling"><option value="summary">Start + middle + end per element</option><option value="all">All SAP stations</option></select></label><label>Display <select id="view"><option value="summary">Summary</option><option value="profiles">All profiles</option><option value="envelope">Envelope</option><option value="matrix">Station matrix</option></select></label><button id="reset">Reset filters</button>{{ReportPrintEngine.ToolbarButton}}<span id="scope"></span>
 </nav>
<div id="filters" data-print-controls></div><div id="notice" data-pr="note" hidden></div>{{ReportCandidateDefinitions.Markup}}<div id="warnings" data-pr="note"></div><main id="panels"></main><div id="tip" hidden></div>
<script type="application/json" id="report-data">{{data}}</script>
{{ReportPrintEngine.Scripts(print)}}
<script>{{ReportCandidateDefinitions.Script}}</script>
<script>{{Script}}</script></body></html>
""";
    }

    private static string Esc(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");

    private const string Style = """
*{box-sizing:border-box}body{margin:0;font:13px/1.4 Segoe UI,Arial;color:#1a1a1a}header{display:flex;justify-content:space-between;align-items:flex-start;gap:16px;padding:10px 14px;border-bottom:2px solid #1f4788}h1{font-size:16px;margin:0}header>div>div,header p{margin:3px 0;color:#666;font-size:11.5px}.lib{font-size:12px}.lib summary{cursor:pointer;color:#1f4788;font-weight:600}.lib ul{list-style:none;margin:6px 0 0;padding:0;border:1px solid #c7ccd2;border-radius:3px;min-width:180px}.lib li{padding:4px 8px;border-bottom:1px solid #f3f4f6;color:#666}.lib li.on{color:#1a1a1a}.lib li span{float:right;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:#9aa0a6}.lib li.on span{color:#2e7d32}nav,#filters{display:flex;gap:9px;align-items:center;padding:6px 14px;border-bottom:1px solid #c7ccd2;flex-wrap:wrap}select,button{font:12px Segoe UI,Arial;padding:3px 7px;border:1px solid #bbb;border-radius:3px;background:white;cursor:pointer}button:hover{background:#f3f4f6}#scope{margin-left:auto;color:#666}.ms{position:relative}.ms>button{min-width:100px;text-align:left}.ms.active>button{color:#1f4788;border-color:#1f4788;font-weight:600}.pop{position:absolute;top:100%;left:0;z-index:30;margin-top:2px;min-width:240px;max-height:280px;overflow:auto;padding:4px 0;background:#fff;border:1px solid #c7ccd2;border-radius:3px;box-shadow:0 4px 14px #0002}.pop label{display:flex;align-items:center;gap:7px;padding:3px 10px;white-space:nowrap;font-size:11.5px;cursor:pointer}.pop label:hover{background:#f3f4f6}.pop .all{font-weight:600;border-bottom:1px solid #c7ccd2}#notice{margin:7px 14px;padding:6px;background:#fff8e1;border-left:3px solid #f57c00}.panel{padding:8px 10px;border-bottom:1px solid #ccc}.title{color:#1f4788;font-weight:600}.sub{color:#666;font-size:11px;margin-left:8px}.stage{display:grid;grid-template-columns:minmax(0,1fr) 220px}.chart{width:100%;height:350px}.station-marker{pointer-events:none}.station-hit{cursor:crosshair}.legend{border-left:1px solid #ccc;padding:6px;max-height:350px;overflow:auto}.legend button{display:block;width:100%;text-align:left;border:0;font:11px Consolas}.legend button.on{background:#e8f0fe}.matrix-wrap{overflow:auto;max-height:570px}.matrix{border-collapse:separate;border-spacing:1px;font:10px Consolas;white-space:nowrap}.matrix th{position:sticky;top:0;background:#eee;padding:4px}.matrix th:first-child,.matrix td:first-child{position:sticky;left:0;background:white;z-index:2}.matrix td{padding:3px 5px;text-align:right}.empty{padding:24px;color:#666}#warnings{padding:7px 14px;color:#8a6d00;font-size:11px}#tip{position:fixed;pointer-events:none;z-index:50;max-width:340px;padding:6px 8px;border-radius:2px;background:#212121;color:#fff;font:11px/1.45 Consolas,monospace;white-space:pre-line;box-shadow:0 8px 16px #0003}[hidden]{display:none!important}
#warnings:empty{display:none}
/* on a sheet of the Print Report screen: the sheet, header and footer come from the shared print engine; one chart per block, its candidate key underneath */
.pr-sheet #notice{margin:0 0 6px}.pr-sheet #warnings{padding:0 0 6px}.pr-sheet .panel{padding:0 0 8px;border-bottom:0}.pr-sheet .stage{display:block}.pr-sheet .chart{width:100%;height:auto}
.pr-sheet .legend{border-left:0;border-top:1px solid #ccc;max-height:none;overflow:visible;padding:5px 0 0;columns:7;column-gap:10px}.pr-sheet .legend button{font-size:9px;padding:0 2px;break-inside:avoid}.pr-sheet .legend hr{display:none}
.pr-sheet .matrix th,.pr-sheet .matrix th:first-child,.pr-sheet .matrix td:first-child{position:static}.pr-sheet .matrix thead th{white-space:normal;overflow-wrap:anywhere}
""";

    private const string Script = """
(()=>{const D=JSON.parse(document.getElementById('report-data').textContent),GR=document.getElementById('groupFilter'),G=document.getElementById('group'),C=document.getElementById('component'),S=document.getElementById('step'),A=document.getElementById('sampling'),V=document.getElementById('view'),P=document.getElementById('panels'),N=document.getElementById('notice'),Q=document.getElementById('scope'),TIP=document.getElementById('tip');
// "#print&group=TopChord&component=M3&view=envelope" from the PDF exporter preselects the toolbar
const OPT=(window.reportPrint&&window.reportPrint.options)||{},norm=x=>String(x).toLowerCase().replace(/[^a-z0-9]/g,''),pick=(select,value)=>{if(value!=null&&[...select.options].some(o=>o.value===value))select.value=value};
const chosen={loadId:new Set(),heightId:new Set(),pretensionId:new Set()},chosenGroups=new Set(),colors=['#1f77b4','#d62728','#2ca02c','#ff7f0e','#9467bd','#17becf'];let hi=null;
const esc=x=>String(x??'').replace(/[&<>"']/g,m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[m])),fmt=x=>Math.abs(x)>=100?x.toFixed(0):Math.abs(x)>=1?x.toFixed(2):x.toFixed(3),prop=()=>C.value[0].toLowerCase()+C.value.slice(1),unit=()=>['T','M2','M3'].includes(C.value)?'kNm':'kN',positionLabel=()=>A.value==='summary'?'start + middle + end per element':'all SAP stations';
D.groups.forEach((g,i)=>G.add(new Option(`${g.groupName} · ${g.outputCase}`,i)));const gs=()=>chosenGroups.size?D.groups.filter((_,i)=>chosenGroups.has(String(i))):D.groups,scope=g=>g.series.filter(x=>[...Object.keys(chosen)].every(k=>!chosen[k].size||chosen[k].has(x[k]))),step=r=>`${r.stepType||'Static'} · ${Number(r.stepNumber).toLocaleString()}`,key=r=>r.sampleLabel?`${r.frameName}|${r.elementName}|${r.sampleLabel}`:`${r.frameName}|${r.elementName}|${r.objectStationM}|${r.elementStationM}`;
let syncGroups=()=>{};
function groupFilter(){const w=document.createElement('span'),b=document.createElement('button'),pop=document.createElement('div');w.className='ms';pop.className='pop';pop.hidden=true;pop.onclick=e=>e.stopPropagation();const all=document.createElement('label');all.className='all';all.innerHTML='<input type="checkbox" checked> All groups';pop.append(all);const boxes=[];D.groups.forEach((g,i)=>{const row=document.createElement('label'),cb=document.createElement('input');cb.type='checkbox';cb.value=String(i);row.append(cb,document.createTextNode(`${g.groupName} · ${g.outputCase}`));pop.append(row);boxes.push(cb);cb.onchange=()=>{cb.checked?chosenGroups.add(cb.value):chosenGroups.delete(cb.value);if(chosenGroups.size===D.groups.length)chosenGroups.clear();sync();if(chosenGroups.size===1){const gi=+[...chosenGroups][0],selected=D.groups[gi];if(selected?.summary){V.value='summary';sumView[gi]='summary'}}hi=null;refreshSteps();render()}});function sync(){const n=chosenGroups.size;b.textContent=`Groups: ${n===0?'all':n<=2?[...chosenGroups].map(i=>D.groups[+i].groupName).join(', '):n+'/'+D.groups.length}`;w.classList.toggle('active',n>0);all.firstChild.checked=n===0;boxes.forEach(cb=>cb.checked=chosenGroups.has(cb.value));G.value=n===0?'all':n===1?[...chosenGroups][0]:'multiple'}syncGroups=sync;all.onclick=e=>{e.preventDefault();chosenGroups.clear();sync();hi=null;refreshSteps();render()};b.onclick=e=>{e.stopPropagation();document.querySelectorAll('.pop').forEach(p=>{if(p!==pop)p.hidden=true});pop.hidden=!pop.hidden};w.append(b,pop);sync();GR.append(w)}
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
function profilePanel(g){const s=scope(g);if(!s.length)return '';const auto=V.value!=='envelope'&&s.length>D.study.individualProfileLimit,env=V.value==='envelope'||auto;return `<section class="panel" data-pr="chart" data-pr-type="profile" data-pr-id="profile_${D.groups.indexOf(g)}"><span class="title">${esc(g.groupName)} · ${C.value}</span><span class="sub">${esc(g.outputCase)} · ${esc(S.value)} · ${positionLabel()} · ${s.length} candidates${auto?' · automatic envelope':''}</span><div class="stage">${chart(g,s,env)}<aside class="legend">${env?'Red = minimum<br>Green = maximum<hr>':''}${s.slice(0,100).map(x=>`<button data-key="${esc(x.iterationKey)}" class="${hi===x.iterationKey?'on':''}">${esc(x.iterationKey)}</button>`).join('')}</aside></div></section>`}
function show(html){P.innerHTML=html||'<p class="empty">No matching results.</p>';P.querySelectorAll('[data-key]').forEach(x=>x.onclick=()=>{hi=hi===x.dataset.key?null:x.dataset.key;render()});P.querySelectorAll('[data-sum]').forEach(x=>x.onclick=()=>sumGo(+x.dataset.g,x.dataset.sum));P.querySelectorAll('[data-sump]').forEach(x=>x.onchange=()=>sumGo(+x.dataset.sump,'station',x.value));wireTips()}
function profiles(list){show(list.map(profilePanel).join(''))}

// ---------- Top Chord / Bottom Chord / Mega Column: Summary by default; a chord adds By Station as a drill-down ----------
// The governing force of every iteration — and, for a chord, its one-point-per-station profile — come ready-made in
// group.summary (derived by the generator from the stored station rows), so this block only draws. A mega-column
// group is a set of separate columns: it has no By Station view and no station is shown for it. Height styling is the same
// in both views and study-wide — colour by height id (as the Support Reactions height bands) and line style by
// height (as the Deflection report) — so a filter never repaints a height and identity is never colour alone.
const PALETTE=['#1f77b4','#d62728','#2ca02c','#ff7f0e','#9467bd','#8c564b','#17becf','#bcbd22','#e377c2','#7f7f7f','#393b79','#637939'],DASH=['','8 5','2 4','10 4 2 4','16 6','16 5 3 5'];
const ALLS=D.groups.flatMap(g=>g.series),natural=(a,b)=>a.localeCompare(b,undefined,{numeric:true}),SCOPE=D.study.scope||null,NQ=new Set(SCOPE?SCOPE.excluded.map(x=>x.join('|')):[]),nq=(l,h,p)=>NQ.has(l+'|'+h+'|'+p),idsOf=k=>(SCOPE&&SCOPE.order[k])||[...new Set(ALLS.map(s=>s[k]))].sort(natural),HID=idsOf('heightId'),PID=idsOf('pretensionId'),LID=idsOf('loadId');
const hColour=id=>PALETTE[HID.indexOf(id)]||'#7f7f7f',hDash=id=>DASH[HID.indexOf(id)%DASH.length],hName=s=>'H'+Math.round(s.trussHeightM*1000)/1000;
const kN=v=>(v<0?'−':'')+String(Math.round(Math.abs(v))).replace(/\B(?=(\d{3})+(?!\d))/g,','),f1=v=>(v<0?'−':'')+Math.abs(v).toFixed(1),m3=v=>String(Math.round(v*1000)/1000);
const sumView={},sumP={};     // per group index: 'summary' | 'station', and the pretension candidate shown by station
const sumOf=(g,s)=>(g.cmap??=new Map(g.summary.candidates.map(c=>[c.iterationKey,c]))).get(s.iterationKey);
const measure=(g,kn)=>(g.summary.kind==='bottom'?kn:Math.abs(kn))/1000;   // MN: tension as stored (bottom chord), compression magnitude (top chord, mega column)
const words=g=>g.summary.kind==='bottom'
  ?{force:'tension',summary:'Governing Axial Tension',yGov:'Governing Tension P (MN)',station:'Axial Tension by Station',ySta:'Tension P (MN)',col:'P'}
  :{force:'compression',summary:'Governing Axial Compression',yGov:'Governing Compression |P| (MN)',station:'Axial Compression by Station',ySta:'Compression |P| (MN)',col:'|P|'};
function axis(lo,up,ticks){const raw=((up-lo)||1)/ticks,p=Math.pow(10,Math.floor(Math.log10(raw))),n=raw/p,step=(n<=1?1:n<=2?2:n<=2.5?2.5:n<=5?5:10)*p;return{lo:Math.floor(lo/step+1e-9)*step,up:Math.ceil(up/step-1e-9)*step,step}}
const tickText=(v,step)=>step%1?v.toFixed(1):String(Math.round(v)),textW=(t,px)=>String(t).length*px*.56;
function sumGo(gi,view,pid){sumView[gi]=view;if(pid!=null)sumP[gi]=pid;render()}
const swatch=(id,line)=>`<svg width="${line?30:38}" height="12">${line?'':`<rect x="0" y="1" width="10" height="10" fill="${hColour(id)}" fill-opacity=".82"/>`}<line x1="${line?0:14}" y1="6" x2="${line?30:38}" y2="6" stroke="${hColour(id)}" stroke-width="1.8" stroke-dasharray="${hDash(id)}"/>${line?'':`<circle cx="26" cy="6" r="2.8" fill="${hColour(id)}" stroke="#fff"/>`}</svg>`;

// Summary: per pretension candidate one bar per truss height (governing force, from 0), a small gap between
// candidates, and one trend line per height through the bar tops. A missing iteration has no bar, no marker,
// and breaks its line — it is never interpolated and never 0.
function summaryChart(g,series,hs,ps,top){
  const w=words(g),W=1100,ml=62,mr=14,pw=W-ml-mr,cw=pw/ps.length,slot=Math.min(40,(cw-Math.max(10,cw*.16))/hs.length),bw=Math.max(3,Math.min(30,slot-2));
  const vert=textW('000.0',9.5)>slot-1,hvert=Math.max(...hs.map(h=>textW(h.name,9)))>slot-1;      // labels turn upright when the bars are too narrow
  const mt=vert?44:24,ph=280,mb=(hvert?44:20)+26,H=mt+ph+mb,base=mt+ph,y=v=>base-Math.max(0,v)/top.up*ph;
  let grid='',bars='',lines='',labels='',hits='',ax='';const pts={};
  for(let v=0;v<=top.up+1e-9;v+=top.step){const Y=y(v).toFixed(1);grid+=`<line class="${v?'cgrid':'caxis'}" x1="${ml}" x2="${W-mr}" y1="${Y}" y2="${Y}"/><text class="ctick" x="${ml-6}" y="${+Y+3}" text-anchor="end">${tickText(v,top.step)}</text>`}
  ps.forEach((pid,i)=>{const x0=ml+i*cw+(cw-hs.length*slot)/2;
    hs.forEach((ht,j)=>{const s=series.find(x=>x.pretensionId===pid&&x.heightId===ht.id),c=s&&sumOf(g,s),ok=!!c&&c.governingKN!=null,v=ok?measure(g,c.governingKN):null,sx=x0+j*slot,cx=sx+slot/2,cy=ok?y(v):base,out=!ok&&series.length>0&&nq(series[0].loadId,ht.id,pid);
      (pts[ht.id]??=[]).push(ok?{cx,cy}:null);
      const tip=ok?`${pid} · ${ht.name}\nGoverning ${w.force}: ${f1(v)} MN\nSource P: ${kN(c.governingKN)} kN · ${c.step}\nAt ${c.frameName} / ${c.elementName}${g.summary.byStation?` · station ${m3(c.stationM)} m`:''}\nIteration ${s.iterationKey} · ${s.loadingLabel}\n${g.groupName} · ${g.outputCase}`:`${pid} · ${ht.name}\n${out?'Not qualified — outside the '+SCOPE.label.toLowerCase():'Result unavailable'}`;
      if(ok){bars+=`<rect data-bar="${esc(pid)}|${esc(ht.id)}" x="${(cx-bw/2).toFixed(1)}" y="${cy.toFixed(1)}" width="${bw.toFixed(1)}" height="${(base-cy).toFixed(1)}" fill="${hColour(ht.id)}" fill-opacity=".82"/>`;
        labels+=vert?`<text class="cval" x="${cx.toFixed(1)}" y="${(cy-4).toFixed(1)}" transform="rotate(-90 ${cx.toFixed(1)} ${(cy-7).toFixed(1)})">${f1(v)}</text>`:`<text class="cval" x="${cx.toFixed(1)}" y="${(cy-7).toFixed(1)}" text-anchor="middle">${f1(v)}</text>`}
      else labels+=`<text class="cna" x="${cx.toFixed(1)}" y="${base-4}" text-anchor="middle">${out?'NQ':'n/a'}</text>`;
      ax+=hvert?`<text class="chl" x="${cx.toFixed(1)}" y="${base+7}" text-anchor="end" transform="rotate(-90 ${cx.toFixed(1)} ${base+7})" dy="3">${esc(ht.name)}</text>`:`<text class="chl" x="${cx.toFixed(1)}" y="${base+12}" text-anchor="middle">${esc(ht.name)}</text>`;
      hits+=`<rect class="station-hit" x="${sx.toFixed(1)}" y="${mt}" width="${slot.toFixed(1)}" height="${ph+14}" fill="transparent" data-tip="${esc(tip)}"/>`});
    const by=base+(hvert?40:17);ax+=`<path class="cbr" d="M${(x0+2).toFixed(1)} ${by} v3 H${(x0+hs.length*slot-2).toFixed(1)} v-3"/><text class="cpl" x="${(x0+hs.length*slot/2).toFixed(1)}" y="${by+17}" text-anchor="middle">${esc(pid)}</text>`});
  hs.forEach(ht=>{let d='',pen=false;pts[ht.id].forEach(p=>{if(!p){pen=false;return}d+=`${pen?'L':'M'}${p.cx.toFixed(1)} ${p.cy.toFixed(1)}`;pen=true});
    lines+=`<path data-trend="${esc(ht.id)}" d="${d}" fill="none" stroke="${hColour(ht.id)}" stroke-width="1.6" stroke-dasharray="${hDash(ht.id)}" stroke-linejoin="round"/>`+pts[ht.id].filter(Boolean).map(p=>`<circle data-mark="${esc(ht.id)}" cx="${p.cx.toFixed(1)}" cy="${p.cy.toFixed(1)}" r="2.8" fill="${hColour(ht.id)}" stroke="#fff" stroke-width="1"/>`).join('')});
  return `<svg class="cchart" viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(g.summary.title)}: ${w.summary.toLowerCase()} per pretension candidate and truss height">${grid}<line class="caxis" x1="${ml}" x2="${ml}" y1="${mt}" y2="${base}"/>${bars}${lines}${labels}${ax}<text class="cax" transform="translate(15 ${mt+ph/2}) rotate(-90)" text-anchor="middle">${w.yGov}</text>${hits}</svg>`}

// By Station: for one pretension candidate, the chord force along the physical chord — one line per truss height
// through one governing point per station, left support to right support. Nothing is smoothed or interpolated.
function stationChart(g,series,hs,pid,xr,yr){
  const w=words(g),W=1100,H=380,ml=62,mr=18,mt=14,mb=56,pw=W-ml-mr,ph=H-mt-mb,base=mt+ph,x=v=>ml+(v-xr.lo)/((xr.up-xr.lo)||1)*pw,y=v=>mt+(yr.up-v)/(yr.up-yr.lo)*ph;
  let grid='',lines='',hits='';
  for(let v=yr.lo;v<=yr.up+1e-9;v+=yr.step){const Y=y(v).toFixed(1);grid+=`<line class="${Math.abs(v)<1e-9?'caxis':'cgrid'}" x1="${ml}" x2="${W-mr}" y1="${Y}" y2="${Y}"/><text class="ctick" x="${ml-6}" y="${+Y+3}" text-anchor="end">${tickText(v,yr.step)}</text>`}
  const xt=axis(xr.lo,xr.up,10);for(let v=xt.lo;v<=xr.up+1e-9;v+=xt.step){if(v<xr.lo-1e-9)continue;const X=x(v).toFixed(1);grid+=`<line class="cgrid" x1="${X}" x2="${X}" y1="${mt}" y2="${base}"/><text class="ctick" x="${X}" y="${base+13}" text-anchor="middle">${tickText(v,xt.step)}</text>`}
  hs.forEach(ht=>{const s=series.find(q=>q.pretensionId===pid&&q.heightId===ht.id),c=s&&sumOf(g,s);if(!c||!c.profile.length)return;
    lines+=`<path data-station="${esc(ht.id)}" d="${c.profile.map((p,i)=>`${i?'L':'M'}${x(p[0]).toFixed(1)} ${y(measure(g,p[1])).toFixed(1)}`).join('')}" fill="none" stroke="${hColour(ht.id)}" stroke-width="1.8" stroke-dasharray="${hDash(ht.id)}" stroke-linejoin="round"/>`;
    if(c.profile.length<=60)lines+=c.profile.map(p=>`<circle cx="${x(p[0]).toFixed(1)}" cy="${y(measure(g,p[1])).toFixed(1)}" r="2" fill="${hColour(ht.id)}"/>`).join('');
    hits+=c.profile.map(p=>`<circle class="station-hit" cx="${x(p[0]).toFixed(1)}" cy="${y(measure(g,p[1])).toFixed(1)}" r="5" fill="transparent" data-tip="${esc(`${pid} · ${ht.name}\nStation: ${m3(p[0])} m\n${w.force[0].toUpperCase()+w.force.slice(1)}: ${f1(measure(g,p[1]))} MN\nSource P: ${kN(p[1])} kN\nIteration ${s.iterationKey} · ${s.loadingLabel}\n${g.groupName} · ${g.outputCase}`)}"/>`).join('')});
  return `<svg class="cchart" viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(g.summary.title)}: ${w.station.toLowerCase()} for ${esc(pid)}">${grid}<line class="caxis" x1="${ml}" x2="${ml}" y1="${mt}" y2="${base}"/><line class="caxis" x1="${ml}" x2="${W-mr}" y1="${base}" y2="${base}"/>${lines}`
    +`<text class="chl" x="${ml}" y="${base+28}">Left support</text><text class="chl" x="${ml+pw/2}" y="${base+28}" text-anchor="middle">Midspan</text><text class="chl" x="${W-mr}" y="${base+28}" text-anchor="end">Right support</text>`
    +`<text class="cax" x="${ml+pw/2}" y="${H-6}" text-anchor="middle">Station along chord (m)</text><text class="cax" transform="translate(15 ${mt+ph/2}) rotate(-90)" text-anchor="middle">${w.ySta}</text>${hits}</svg>`}

function sumPanel(g){
  const gi=D.groups.indexOf(g),s=scope(g);if(!s.length)return '';
  const w=words(g),bySta=g.summary.byStation,station=bySta&&sumView[gi]==='station',inScope=k=>id=>s.some(x=>x[k]===id);
  const hs=HID.filter(inScope('heightId')).map(id=>({id,name:hName(s.find(x=>x.heightId===id))})),ps=PID.filter(inScope('pretensionId')),ls=LID.filter(inScope('loadId'));
  const got=s.map(x=>sumOf(g,x)).filter(c=>c&&c.governingKN!=null),loadOf=id=>s.filter(x=>x.loadId===id),loadName=id=>`${id} · ${loadOf(id)[0].loadingLabel}`;
  const head=`<span class="cname">${esc(g.summary.title)}</span><span class="cview">${station?'By Station':'Summary'}</span>`,trace=`<span class="sub">${esc(g.groupName)} · ${esc(g.outputCase)} · governing over ${bySta?'every station':'the whole group'} and every result step</span>`;
  if(!station){
    const top=axis(0,Math.max(1e-6,...got.map(c=>measure(g,c.governingKN)))*1.04,6);
    const legend=`<div class="clegend" data-pr="legend">${hs.map(h=>`<span>${swatch(h.id)}${esc(h.name)}</span>`).join('')}<span class="note">bar = governing force · line = trend across the pretension candidates · n/a = no result${SCOPE?' · NQ = not qualified':''}</span></div>`;
    const cards=ls.map(id=>`<div class="ccard" data-pr="chart" data-pr-type="${g.summary.kind}Summary" data-pr-id="${g.summary.kind}_summary_${esc(id)}"><div class="ctitle">${esc(g.summary.title)} – ${w.summary}<span>${esc(loadName(id))} · ${loadOf(id).length} iteration(s)</span></div>${summaryChart(g,loadOf(id),hs,ps,top)}</div>`).join('');
    const rows=ls.flatMap(id=>ps.flatMap(pid=>hs.map(h=>{const x=loadOf(id).find(q=>q.pretensionId===pid&&q.heightId===h.id),c=x&&sumOf(g,x);return c&&c.governingKN!=null
      ?`<tr><td>${esc(id)}</td><td>${esc(pid)}</td><td>${esc(h.name)}</td><td>${esc(x.iterationKey)}</td><td class="n">${f1(measure(g,c.governingKN))}</td><td class="n">${kN(c.governingKN)}</td><td>${esc(c.frameName)}</td><td>${esc(c.elementName)}</td>${bySta?`<td class="n">${m3(c.stationM)}</td>`:''}<td>${esc(c.step)}</td></tr>`
      :`<tr><td>${esc(id)}</td><td>${esc(pid)}</td><td>${esc(h.name)}</td><td>${x?esc(x.iterationKey):'—'}</td><td class="n na" colspan="${bySta?6:5}">${nq(id,h.id,pid)?'Not qualified':'Result unavailable'}</td></tr>`}))).join('');
    return `<section class="panel sumpanel" data-sum-kind="${g.summary.kind}" data-sum-view="summary"><div class="chead" data-pr="chart" data-pr-keep>${head}${bySta?`<button class="cgo no-print" data-sum="station" data-g="${gi}">View by Station</button>`:''}${trace}</div>${legend}${cards}`
      +`<details class="cdata" data-print="source" data-pr="source"><summary>Governing values — the source of every bar</summary><div class="matrix-wrap" data-print-fit><table class="ctable"><thead><tr><th>Load</th><th>Pretension</th><th>Height</th><th>Iteration</th><th class="n">Governing ${w.col} [MN]</th><th class="n">Source P [kN]</th><th>Frame object</th><th>Analysis element</th>${bySta?'<th class="n">Station [m]</th>':''}<th>Result step</th></tr></thead><tbody>${rows}</tbody></table></div></details></section>`}
  const pid=ps.includes(sumP[gi])?sumP[gi]:ps[0],shown=s.filter(x=>x.pretensionId===pid).map(x=>sumOf(g,x)).filter(c=>c&&c.profile.length),all=s.map(x=>sumOf(g,x)).filter(c=>c&&c.profile.length);
  const xs=all.flatMap(c=>[c.profile[0][0],c.profile[c.profile.length-1][0]]),vs=shown.flatMap(c=>c.profile.map(p=>measure(g,p[1])));
  const xr={lo:xs.length?Math.min(...xs):0,up:xs.length?Math.max(...xs):1},yr=axis(Math.min(0,...vs),Math.max(1e-6,...vs)*1.04,6);
  const legend=`<div class="clegend" data-pr="legend">${hs.map(h=>`<span>${swatch(h.id,true)}${esc(h.name)}</span>`).join('')}<span class="note">one governing point per physical station · not smoothed</span></div>`;
  const missing=id=>{const off=hs.filter(h=>{const x=loadOf(id).find(q=>q.pretensionId===pid&&q.heightId===h.id),c=x&&sumOf(g,x);return !(c&&c.profile.length)});
    return off.length?`<div class="clegend">${off.map(h=>`<span class="off">${esc(h.name)} — ${nq(id,h.id,pid)?'not qualified':'no result'} for ${esc(pid)}</span>`).join('')}</div>`:''};
  return `<section class="panel sumpanel" data-sum-kind="${g.summary.kind}" data-sum-view="station"><div class="chead" data-pr="chart" data-pr-keep>${head}<button class="cgo no-print" data-sum="summary" data-g="${gi}">← Summary</button><label class="cpick">Pretension Candidate <select data-sump="${gi}">${ps.map(p=>`<option value="${esc(p)}"${p===pid?' selected':''}>${esc(p)}</option>`).join('')}</select></label>${trace}</div>${legend}`
    +ls.map(id=>`<div class="ccard" data-pr="chart" data-pr-type="${g.summary.kind}ByStation" data-pr-id="${g.summary.kind}_station_${esc(pid)}_${esc(id)}"><div class="ctitle">${esc(g.summary.title)} – ${w.station} · ${esc(pid)}<span>${esc(loadName(id))}</span></div>${missing(id)}${stationChart(g,loadOf(id),hs,pid,xr,yr)}</div>`).join('')+`</section>`}
function summaries(list){show(list.map(g=>g.summary?sumPanel(g):profilePanel(g)).join(''))}

function matrix(list){let h='';list.forEach(g=>{const s=scope(g),cols=rows(s[0]||{stations:[]}),max=Math.max(1,...s.flatMap(x=>rows(x).map(r=>Math.abs(r[prop()]))));h+=`<section class="panel" data-pr="chart" data-pr-type="matrix" data-pr-id="matrix_${D.groups.indexOf(g)}"><span class="title">${esc(g.groupName)} · ${C.value} station matrix</span><span class="sub">${cols.length} displayed positions · ${positionLabel()}</span><div class="matrix-wrap" data-print-fit><table class="matrix"><thead><tr><th>Candidate</th>${cols.map(r=>`<th title="ObjSta ${fmt(r.objectStationM)} m; ElmSta ${fmt(r.elementStationM)} m">${esc(r.sampleLabel||r.frameName)} · ${esc(r.frameName)}/${esc(r.elementName)} @ ${fmt(r.objectStationM)}</th>`).join('')}</tr></thead><tbody>${s.map(x=>{const map=new Map(rows(x).map(r=>[key(r),r]));return `<tr><td>${esc(x.iterationKey)}</td>${cols.map(c=>{const r=map.get(key(c)),v=r?.[prop()];if(!Number.isFinite(v))return '<td>—</td>';const a=.1+.7*Math.min(1,Math.abs(v)/max),bg=v<0?`rgba(198,40,40,${a})`:`rgba(46,125,50,${a})`;return `<td style="background:${bg}" title="Frame ${esc(c.frameName)}; element ${esc(c.elementName)}; ObjSta ${fmt(c.objectStationM)} m; ElmSta ${fmt(c.elementStationM)} m">${fmt(v)}</td>`}).join('')}</tr>`}).join('')}</tbody></table></div></section>`});P.innerHTML=h||'<p class="empty">No matching results.</p>'}
function render(){const list=gs(),sum=V.value==='summary',plain=list.filter(g=>!(sum&&g.summary)),n=Math.max(0,...list.map(g=>scope(g).length)),np=Math.max(0,...plain.map(g=>scope(g).length)),auto=(sum||V.value==='profiles')&&np>D.study.individualProfileLimit;Q.textContent=`up to ${n} / ${D.study.candidateCount} candidates`;N.hidden=!auto;N.textContent=auto?`Showing a station-by-station envelope because ${np} candidates are in scope. Filter to ${D.study.individualProfileLimit} or fewer for every profile.`:'';
// Component / Result step / Positions belong to the generic views: a Summary always reads P over every station and step
[C,S,A].forEach(x=>{if(x.parentElement)x.parentElement.hidden=sum&&!plain.length});
V.value==='matrix'?matrix(list):sum?summaries(list):profiles(list)}
G.onchange=()=>{chosenGroups.clear();if(G.value!=='all'&&G.value!=='multiple')chosenGroups.add(G.value);const g=chosenGroups.size===1?D.groups[+[...chosenGroups][0]]:null;if(g?.summary){V.value='summary';sumView[+[...chosenGroups][0]]='summary'}syncGroups();hi=null;refreshSteps();render()};C.onchange=render;S.onchange=render;A.onchange=render;V.onchange=render;document.getElementById('reset').onclick=()=>{Object.values(chosen).forEach(x=>x.clear());chosenGroups.clear();hi=null;A.value='summary';GR.innerHTML='';document.getElementById('filters').innerHTML='';groupFilter();filters();refreshSteps();render()};document.addEventListener('click',()=>document.querySelectorAll('.pop').forEach(x=>x.hidden=true));
if(!D.groups.some(g=>g.summary)){V.remove(0);V.value='profiles'}      // no chord or mega-column group in this study: nothing to summarise
if(OPT.group!=null){const i=D.groups.findIndex(g=>norm(g.groupName)===norm(OPT.group));if(i>=0)chosenGroups.add(String(i))}pick(C,OPT.component);pick(A,OPT.sampling);pick(V,OPT.view);
// "#summary=top|bottom|mega" (Tab 06 modules 02 / 03 / 05) opens straight on that group; "&detail=station&pretension=P03" drills into a chord
if(OPT.summary){const i=D.groups.findIndex(g=>g.summary&&g.summary.kind===OPT.summary);if(i>=0){chosenGroups.clear();chosenGroups.add(String(i));V.value='summary';sumView[i]=OPT.detail==='station'?'station':'summary';if(OPT.pretension)sumP[i]=OPT.pretension}}
window.memberReport={summary:sumGo,render};      // handle for scripted checks
groupFilter();filters();refreshSteps();render();const w=document.getElementById('warnings');if(D.warnings.length)w.textContent=D.warnings.map(x=>`[${x.iterationKey||'study'}] ${x.message}`).join(' · ');
})();
""";
}
