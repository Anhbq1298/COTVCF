"""Self-contained SAP/FEM axial and cable comparison reports from offline benchmark outputs.

python tests/generate_fem_sap_reports.py --current <report-folder> --legacy <report-folder>
    --current-fixture <fixture-folder> --legacy-fixture <fixture-folder> --output <folder>

The legacy input is used only for megacolumns, absent from the newer saved study.
No SAP2000 connection or third-party Python packages are needed.
"""
import argparse
import csv
from datetime import datetime, timedelta, timezone
import html
import json
from pathlib import Path
import re
import textwrap

ROOT = Path(__file__).resolve().parent.parent


def literal(name):
    """Reuse the project's print engine verbatim, without maintaining a second copy."""
    source = (ROOT / 'src/Application/Results/ReportPrintEngine.cs').read_text(encoding='utf-8')
    match = re.search(r'const string ' + name + r'\s*=\s*"""(.*?)""";', source, re.S)
    if not match:
        raise ValueError('Cannot find print engine literal ' + name)
    return textwrap.dedent(match.group(1)).strip()


CSS = """
*{box-sizing:border-box}body{margin:0;background:#e6e8eb;color:#1a1a1a;font:12px/1.5 'Segoe UI',Arial,sans-serif}
main{max-width:1320px;margin:auto;padding:16px}h1{font-size:19px;margin:0}h2{font-size:13px;color:#1f4788;margin:0 0 8px}
.sub,.note{color:#667085}.card{background:white;border:1px solid #c7ccd2;padding:12px;margin-bottom:12px}
.toolbar{display:flex;flex-wrap:wrap;align-items:center;gap:9px;padding:9px;background:#f3f4f6;border:1px solid #c7ccd2;margin:12px 0}
select,button{font:12px 'Segoe UI';padding:4px 8px;background:white;border:1px solid #c7ccd2;color:#1a1a1a;min-height:27px}
a{color:#1f4788}.nav{display:flex;gap:16px;margin-bottom:10px}.kpis{display:grid;grid-template-columns:repeat(4,1fr);gap:12px}
.kpi b{display:block;font-size:21px}.kpi{border-left:3px solid #1f4788}.charts{display:grid;grid-template-columns:1fr 1fr;gap:12px}
svg{width:100%;height:auto;display:block}.legend span{display:inline-block;margin-right:18px}.sap{color:#1f4788}.fem{color:#d97706}
table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}th,td{padding:4px 8px;text-align:right;border-bottom:1px solid #e4e7ec;white-space:nowrap}
th{background:#f3f4f6}th:first-child,td:first-child{text-align:left}.wrap{max-height:450px;overflow:auto}.pr-sheet .wrap{max-height:none;overflow:visible}
details summary{cursor:pointer;color:#1f4788;font-weight:600}.pr-sheet .charts{display:block}.pr-sheet svg{max-height:95mm}
@media(max-width:850px){.charts{grid-template-columns:1fr}.kpis{grid-template-columns:1fr 1fr}}
"""

SCRIPT = r"""
(()=>{'use strict';
const D=JSON.parse(document.getElementById('comparison-data').textContent),$=id=>document.getElementById(id);
const esc=x=>String(x).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const num=x=>Number.isFinite(x)?x.toLocaleString('en-US',{maximumFractionDigits:1}):'n/a';
const pc=(s,f)=>Math.abs(s)>1e-9?100*(f-s)/Math.abs(s):null;
const colors=['#1f4788','#d97706'];
let rows=D.rows;
function options(id,values,all){$(id).innerHTML=(all?'<option value="">All</option>':'')+values.map(x=>`<option>${esc(x)}</option>`).join('');}
options('loading',[...new Set(rows.map(r=>r.candidate.split('_')[0]))],true);
options('height',[...new Set(rows.map(r=>r.height))],true);
options('pretension',[...new Set(rows.map(r=>r.candidate.split('_')[2]))],true);
options('member',[...new Set(rows.map(r=>r.name))],false);
function chosen(){return rows.filter(r=>(!$('loading').value||r.candidate.startsWith($('loading').value+'_'))&&(!$('height').value||r.height===$('height').value)&&(!$('pretension').value||r.candidate.endsWith('_'+$('pretension').value)));}
function svg(series,xlabel,ylabel,scatter=false){
 const all=series.flatMap(s=>s.points);if(!all.length)return '<p>No results for this selection.</p>';
 let xs=all.map(p=>p.x),ys=all.map(p=>p.y),xmin=Math.min(...xs),xmax=Math.max(...xs),ymin=Math.min(0,...ys),ymax=Math.max(0,...ys);
 if(scatter){xmin=Math.min(xmin,ymin);xmax=Math.max(xmax,ymax);ymin=xmin;ymax=xmax;}
 if(xmax===xmin)xmax=xmin+1;if(ymax===ymin)ymax=ymin+1;
 let pad=(ymax-ymin)*.07;ymin-=pad;ymax+=pad;
 if(scatter){xmin=ymin;xmax=ymax;}
 const w=580,h=340,l=76,t=20,b=55,r=20,X=x=>l+(x-xmin)/(xmax-xmin)*(w-l-r),Y=y=>h-b-(y-ymin)/(ymax-ymin)*(h-t-b);
 let s=`<svg viewBox="0 0 ${w} ${h}" role="img" aria-label="${esc(xlabel+' versus '+ylabel)}">`;
 for(let i=0;i<=4;i++){let x=xmin+(xmax-xmin)*i/4,y=ymin+(ymax-ymin)*i/4;s+=`<path d="M${X(x)} ${t}V${h-b}M${l} ${Y(y)}H${w-r}" stroke="#e4e7ec" fill="none"/><text x="${X(x)}" y="${h-b+18}" text-anchor="middle" font-size="10">${num(x)}</text><text x="${l-7}" y="${Y(y)+4}" text-anchor="end" font-size="10">${num(y)}</text>`;}
 if(scatter)s+=`<path d="M${X(xmin)} ${Y(xmin)}L${X(xmax)} ${Y(xmax)}" stroke="#667085" stroke-dasharray="5 4"/>`;
 series.forEach((ser,i)=>{if(!scatter)s+=`<polyline points="${ser.points.map(p=>X(p.x)+','+Y(p.y)).join(' ')}" fill="none" stroke="${colors[i%2]}" stroke-width="1.8" ${i===1?'stroke-dasharray="6 3"':''}/>`;
 ser.points.forEach(p=>{s+=`<circle cx="${X(p.x)}" cy="${Y(p.y)}" r="${scatter?2:2.8}" fill="${colors[i%2]}" fill-opacity=".65"><title>${esc(p.label)}: ${num(p.y)} kN</title></circle>`;});});
 return s+`<text x="${(l+w-r)/2}" y="${h-8}" text-anchor="middle" font-size="12">${esc(xlabel)}</text><text transform="translate(14 ${(t+h-b)/2}) rotate(-90)" text-anchor="middle" font-size="12">${esc(ylabel)}</text></svg>`;
}
function render(){
 const selected=chosen(),member=selected.filter(r=>r.name===$('member').value),scale=Math.max(0,...selected.map(r=>Math.abs(r.sap))),errors=selected.map(r=>Math.abs(r.fem-r.sap)),worst=Math.max(0,...errors);
 $('view').textContent=`Loading: ${$('loading').value||'all'} · Height: ${$('height').value||'all'} · Pretension: ${$('pretension').value||'all'} · Member: ${$('member').value}`;
 $('kpis').innerHTML=[['Candidates',new Set(selected.map(r=>r.candidate)).size],['Compared values',selected.length],['Mean |FEM − SAP|',num(errors.length?errors.reduce((a,b)=>a+b,0)/errors.length:0)+' kN'],['Worst / largest |SAP|',num(scale?100*worst/scale:0)+' %']].map(([a,b])=>`<div class="card kpi"><span>${esc(a)}</span><b>${b}</b></div>`).join('');
 $('scatter').innerHTML=svg([{points:selected.map(r=>({x:r.sap,y:r.fem,label:r.candidate+' · '+r.name+(r.station===null?'':' · '+r.station+' m')}))}],'SAP2000 [kN]','In-house FEM [kN]',true);
 const byCandidate=new Map();member.forEach(r=>{let old=byCandidate.get(r.candidate);if(!old||Math.abs(r.sap)>Math.abs(old.sap))byCandidate.set(r.candidate,r);});
 const governing=[...byCandidate.values()].sort((a,b)=>a.candidate.localeCompare(b.candidate));
 $('progress').innerHTML=svg(['sap','fem'].map(key=>({points:governing.map((r,i)=>({x:i+1,y:r[key],label:r.candidate+' · '+r.name+(r.station===null?'':' · SAP governing station '+r.station+' m')}))})),'Candidate sequence (L → H → P)','Axial force [kN]');
 $('progress-note').textContent=D.kind==='cable'?'One cable force per candidate, in the selected loading/height/pretension range.':'Per candidate: the station with largest |SAP axial force|; FEM is sampled at that same station. Station-level data remain in the source table.';
 options('candidate',governing.map(r=>r.candidate),false);
 drawProfile(member);
 const worstRows=[...selected].sort((a,b)=>Math.abs(b.fem-b.sap)-Math.abs(a.fem-a.sap)).slice(0,20);
 $('worst').innerHTML=table(worstRows);
 $('source').innerHTML=table(selected);
 $('coverage').textContent=`${new Set(selected.map(r=>r.candidate)).size} candidates · ${new Set(selected.map(r=>r.name)).size} members · ${selected.length} paired values. Δ = FEM − SAP. Percent uses |SAP|; zero SAP values are n/a. Frame values are rounded to 1 kN in the benchmark output.`;
}
function table(data){return '<table><thead><tr><th>Candidate / member</th><th>Station m</th><th>SAP kN</th><th>FEM kN</th><th>Δ kN</th><th>Δ %</th></tr></thead><tbody>'+data.map(r=>`<tr><td>${esc(r.candidate+' / '+r.name)}</td><td>${r.station===null?'—':num(r.station)}</td><td>${num(r.sap)}</td><td>${num(r.fem)}</td><td>${num(r.fem-r.sap)}</td><td>${num(pc(r.sap,r.fem))}</td></tr>`).join('')+'</tbody></table>';}
function drawProfile(member){const p=(member||chosen().filter(r=>r.name===$('member').value)).filter(r=>r.candidate===$('candidate').value);
 $('profile-title').textContent=D.kind==='cable'?'Cable forces in selected candidate':'Axial force along selected member';
 if(D.kind==='cable'){const p=chosen().filter(r=>r.candidate===$('candidate').value);$('profile').innerHTML=svg(['sap','fem'].map(key=>({points:p.map((r,i)=>({x:i+1,y:r[key],label:r.name}))})),'Cable index (source table lists names)','Tension [kN]');}
 else $('profile').innerHTML=svg(['sap','fem'].map(key=>({points:p.map(r=>({x:r.station,y:r[key],label:r.name+' · '+r.station+' m'}))})),'Station from I-end [m]','Axial P [kN]');
 $('profile-view').textContent=$('candidate').value+' · '+(D.kind==='cable'?'all monitored cables':$('member').value);
}
['loading','height','pretension','member'].forEach(id=>$(id).addEventListener('change',render));$('candidate').addEventListener('change',()=>drawProfile());
window.comparisonReport={data:D,render,selected:chosen};render();
})();
"""


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def member_rows(folder, group):
    data = read_json(folder / 'fem-vs-sap-members.json')
    rows = []
    for candidate in data['candidates']:
        for frame in candidate['frames']:
            if frame['group'] != group:
                continue
            assert len(frame['s']) == len(frame['sap'][0]) == len(frame['fem'][0])
            rows.extend(dict(candidate=candidate['id'], height=candidate['height'], name=frame['name'],
                             station=s, sap=p, fem=f) for s, p, f in zip(frame['s'], frame['sap'][0], frame['fem'][0]))
    return rows, data['case']


def cable_rows(folder):
    with (folder / 'fem-vs-sap.csv').open(encoding='utf-8-sig', newline='') as stream:
        return [dict(candidate=r['candidate'], height=r['height'], name=r['name'], station=None,
                     sap=float(r['sap']), fem=float(r['fem_sap_cable_lengths']))
                for r in csv.DictReader(stream) if r['quantity'] == 'Cable force [kN]']


def page(title, rows, case, reference, legacy, kind):
    if not rows:
        raise ValueError('No comparison values for ' + title)
    payload = dict(kind=kind, rows=rows, case=case)
    basis = ('Older study: 5 Oct 2026. Megacolumn results are absent from the newer saved study. '
             'FEM reproduces the undeformed cable lengths recorded in the old SAP child models. '
             'This comparison does not validate the corrected study’s megacolumn forces.' if legacy else
             'Newer study: 6 Oct 2026. All 135 child-model cable lengths were read from saved SAP text files; '
             'the undeformed lengths follow the candidate chords.')
    defaults = dict(title=title, model=reference['modelName'], issued=datetime.now(timezone(timedelta(hours=8))).strftime('%d %b %Y'), company='', logo='', project='',
                    jobNumber='', preparedBy='', checkedBy='', revision='', footerNote='', paper='A4',
                    orientation='landscape', showPageNumbers=True, showDate=True, includeSourceData=False)
    safe_json = lambda x: json.dumps(x, ensure_ascii=True, separators=(',', ':')).replace('<', '\\u003c')
    return f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{html.escape(title)} — SAP2000 vs FEM</title><style>{literal('Css')}\n{CSS}</style>{literal('PageStyle')}</head><body><main>
<nav class="nav no-print"><a href="index.html">Comparison reports</a></nav>
<section class="card" data-pr="info"><h1>{html.escape(title)} — SAP2000 vs in-house FEM</h1>
<div class="sub">{html.escape(reference['modelName'])} · {len(reference['candidates'])} candidates · Force units: kN</div>
<p>{html.escape(basis)}</p><p>Result case: <b>{html.escape(case)}</b>. Same candidate geometry, loads, supports and pretension stage; pretension fitted under the Pretension case loads.</p></section>
<div class="toolbar no-print" data-print-controls><label>Loading <select id="loading"></select></label><label>Height <select id="height"></select></label>
<label>Pretension <select id="pretension"></select></label><label>Member <select id="member"></select></label>{literal('ToolbarButton')}</div>
<p id="view" data-print-view></p><div id="kpis" class="kpis" data-pr="summary"></div>
<div class="card legend" data-pr="legend"><span class="sap">● SAP2000 — solid blue</span><span class="fem">● In-house FEM — dashed orange</span><span>Frame P: positive tension / negative compression. Cables: positive tension.</span></div>
<div class="card" data-pr="note"><p id="coverage"></p><p class="note">SAP2000 uses P-Delta / large-displacement geometry, catenary cables and shear-deformable frames; FEM uses small displacements, parabolic cable sag and Euler–Bernoulli frames. SAP cable force is the maximum along the cable; FEM reports chord tension. Repeated frame stations preserve the two sides of an element boundary.</p><p class="note">These reports compare axial and cable forces only. The broader newer-study benchmark passed its axial and cable bands, but its shear check exceeded the 5% regression band (11.07% of the largest SAP shear); the overall benchmark is not a pass.</p></div>
<div class="charts"><section class="card" data-pr="chart" data-pr-id="agreement"><h2>Value-by-value agreement</h2><div id="scatter"></div><p class="note">Dashed grey line = exact equality.</p></section>
<section class="card" data-pr="chart" data-pr-id="progress"><h2>Force progression across candidates</h2><div id="progress"></div><p id="progress-note" class="note"></p></section></div>
<section class="card" data-pr="chart" data-pr-id="profile"><h2 id="profile-title"></h2><label class="no-print">Candidate <select id="candidate"></select></label><p id="profile-view"></p><div id="profile"></div></section>
<section class="card" data-pr="chart" data-pr-id="worst"><h2>20 largest absolute differences — current filters</h2><div id="worst" class="wrap" data-print-fit></div></section>
<details class="card" data-pr="source"><summary>Paired source data — current filters</summary><div id="source" class="wrap" data-print-fit></div></details>
</main><script id="comparison-data" type="application/json">{safe_json(payload)}</script>
<script id="print-data" type="application/json">{safe_json(defaults)}</script><script>{literal('Script')}</script><script>{SCRIPT}</script></body></html>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for arg in ('current', 'legacy', 'current-fixture', 'legacy-fixture', 'output'):
        parser.add_argument('--' + arg, required=True, type=Path)
    args = parser.parse_args()
    current = read_json(args.current_fixture / 'sap-results.json')
    legacy = read_json(args.legacy_fixture / 'sap-results.json')
    args.output.mkdir(parents=True, exist_ok=True)
    specs = [('Top_Chord_Comparison.html', 'Top chord axial loads', 'TopChord', False),
             ('Bottom_Chord_Comparison.html', 'Bottom chord axial loads', 'BottomChord', False),
             ('Mega_Column_Comparison.html', 'Megacolumn axial loads', 'MegaColumns', True),
             ('Cable_Forces_Comparison.html', 'Cable forces', None, False)]
    links = []
    for filename, title, group, old in specs:
        ref = legacy if old else current
        folder = args.legacy if old else args.current
        if group:
            rows, case = member_rows(folder, group)
        else:
            rows, case = cable_rows(folder), ref['cableCase']
        expected = {c['key'] for c in ref['candidates']}
        if {r['candidate'] for r in rows} != expected:
            raise ValueError(title + ': missing or unexpected candidates')
        (args.output / filename).write_text(page(title, rows, case, ref, old, 'frame' if group else 'cable'), encoding='utf-8')
        with (args.output / filename.replace('.html', '.csv')).open('w', newline='', encoding='utf-8') as stream:
            writer = csv.DictWriter(stream, fieldnames=['candidate', 'height', 'name', 'station', 'sap', 'fem'])
            writer.writeheader()
            writer.writerows(rows)
        links.append(f'<li><a href="{filename}">{title}</a> — {len(expected)} candidates, {len(rows):,} paired values; {"older study, recorded cable lengths" if old else "newer study, corrected cable lengths"}</li>')
        print(filename, len(rows), 'paired values')
    (args.output / 'index.html').write_text('<!doctype html><html lang="en"><meta charset="utf-8"><title>SAP2000 / FEM comparison reports</title><style>'+CSS+'</style><main><section class="card"><h1>SAP2000 / in-house FEM comparison reports</h1><p>Candidate filters, force progression, station profiles, paired data and the project Print Report engine.</p><ul>'+''.join(links)+'</ul><p>Megacolumns were not recorded in the newer saved study. Their report uses the older study with its cable lengths reproduced in FEM; every report states its basis.</p></section></main></html>', encoding='utf-8')


if __name__ == '__main__':
    main()
