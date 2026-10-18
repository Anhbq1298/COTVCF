using Mcotis.Desktop.Domain.Results;

namespace Mcotis.Desktop.Application.Results;

/// <summary>Study-wide L/H/P definitions shared by every interactive HTML result report.</summary>
public sealed record ReportCandidateDefinitionsDto(
    IReadOnlyList<ReportLoadingCandidateDto> Loading,
    IReadOnlyList<ReportHeightCandidateDto> Heights,
    IReadOnlyList<ReportPretensionCandidateDto> Pretensions,
    IReadOnlyList<string> PretensionCables);

public sealed record ReportLoadingCandidateDto(
    string Id,
    string Label,
    IReadOnlyDictionary<string, double> Components);

public sealed record ReportHeightCandidateDto(string Id, double HeightM, double DeltaHM);

public sealed record ReportPretensionCandidateDto(
    string Id,
    string Label,
    double? UniformForceKN,
    IReadOnlyDictionary<string, double> CableForces);

public static class ReportCandidateDefinitions
{
    public static ReportCandidateDefinitionsDto Build(IReadOnlyList<CandidateResultDto> results)
    {
        var ordered = results.OrderBy(result => result.Parameters.RunIndex).ToArray();
        var loading = ordered.GroupBy(result => result.Parameters.LoadingCandidateId, StringComparer.Ordinal)
            .Select(group =>
            {
                var parameters = group.First().Parameters;
                var components = (parameters.LoadingComponents ?? [])
                    .GroupBy(component => component.PatternName, StringComparer.Ordinal)
                    .ToDictionary(component => component.Key, component => component.First().LevelKPa,
                        StringComparer.Ordinal);
                return new ReportLoadingCandidateDto(group.Key, parameters.LoadingLabel, components);
            })
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();

        var heights = ordered.GroupBy(result => result.Parameters.HeightCandidateId, StringComparer.Ordinal)
            .Select(group =>
            {
                var parameters = group.First().Parameters;
                return new ReportHeightCandidateDto(group.Key, parameters.TrussHeightM, parameters.DeltaHM);
            })
            .OrderBy(candidate => candidate.HeightM)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();

        var pretensions = ordered.GroupBy(result => result.Parameters.PretensionCandidateId, StringComparer.Ordinal)
            .Select(group =>
            {
                var result = group.First();
                var parameters = result.Parameters;
                var cableForces = (parameters.PretensionCables ?? [])
                    .GroupBy(cable => cable.CableName, StringComparer.Ordinal)
                    .ToDictionary(cable => cable.Key, cable => cable.First().TargetForceKN, StringComparer.Ordinal);
                if (cableForces.Count == 0 && result.CableForces is { Count: > 0 })
                {
                    cableForces = result.CableForces
                        .Where(cable => cable.TargetForceKN.HasValue)
                        .GroupBy(cable => cable.CableName, StringComparer.Ordinal)
                        .ToDictionary(cable => cable.Key, cable => cable.First().TargetForceKN!.Value,
                            StringComparer.Ordinal);
                }
                return new ReportPretensionCandidateDto(group.Key, parameters.PretensionLabel,
                    parameters.PretensionKN, cableForces);
            })
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
        var cables = pretensions.SelectMany(candidate => candidate.CableForces.Keys)
            .Distinct(StringComparer.Ordinal).OrderBy(cable => cable, StringComparer.Ordinal).ToArray();
        return new(loading, heights, pretensions, cables);
    }

    public const string Markup = """
<details id="candKey" data-pr="legend">
  <summary id="candidateDefinitionsSummary">Candidate definitions</summary>
  <div id="candidateDefinitions" class="candidate-definitions-body"></div>
</details>
""";

    public const string Style = """
#candKey{border-top:1px solid #c7ccd2;border-bottom:1px solid #c7ccd2;font-size:11px}
#candKey>summary{cursor:pointer;padding:6px 14px;background:#f3f4f6;font-weight:600;color:#1f4788}
.candidate-definitions-body{display:grid;grid-template-columns:max-content max-content;gap:12px 28px;padding:10px 14px 14px;align-items:start}
#candKey .candidate-section{min-width:210px;break-inside:avoid}
#candKey .candidate-pretension{grid-column:1/-1;min-width:0}
#candKey h3{margin:0 0 5px;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:#666}
#candKey h3 span{color:#1f4788;font-size:11px;margin-left:3px}
#candKey .candidate-scroll{max-width:100%;overflow:auto}
#candKey table{border-collapse:collapse;width:auto;font-size:10.5px;white-space:nowrap}
#candKey th{position:static;background:#f3f4f6;color:#666;font-weight:600;text-align:right;padding:3px 7px;border:1px solid #dfe3e8}
#candKey td{padding:3px 7px;text-align:right;border:1px solid #e7eaee}
#candKey th:first-child,#candKey td:first-child{text-align:left;font-family:Consolas,monospace;font-weight:600;color:#1a1a1a}
#candKey .candidate-labels{display:grid;grid-template-columns:max-content minmax(120px,1fr);gap:3px 9px}
#candKey .candidate-labels b{font-family:Consolas,monospace}#candKey .candidate-labels span{color:#666}
#candKey .candidate-note{margin:0 0 5px;color:#666;font-size:10px}
.pr-sheet #candKey{border:0;break-inside:auto}
.pr-sheet #candKey>summary{padding:0 0 5px;background:transparent}
.pr-sheet #candKey .candidate-definitions-body{display:block;padding:0}
.pr-sheet #candKey .candidate-section{margin:0 0 8px;break-inside:avoid}
.pr-sheet #candKey .candidate-scroll{overflow:visible}
.pr-sheet #candKey table{font-size:8px;width:auto}
.pr-sheet #candKey th,.pr-sheet #candKey td{padding:2px 4px}
""";

    public const string Script = """
(()=>{
  const root=document.getElementById('candidateDefinitions');
  if(!root)return;
  const data=JSON.parse(document.getElementById('report-data').textContent),d=data.candidateDefinitions;
  if(!d){root.innerHTML='<p>Candidate definitions are unavailable in this report.</p>';return;}
  const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const num=v=>Number.isFinite(v)?Number(v).toLocaleString('en-US',{maximumFractionDigits:3}):'—';
  const has=(object,key)=>Object.prototype.hasOwnProperty.call(object||{},key);
  const heading=(name,count)=>`<h3>${name} <span>${count}</span></h3>`;
  const patterns=[...new Set(d.loading.flatMap(candidate=>Object.keys(candidate.components||{})))];
  let loading=heading('Loading candidates',d.loading.length);
  if(patterns.length){
    loading+=`<div class="candidate-scroll"><table><thead><tr><th>ID</th>${patterns.map(pattern=>`<th>${esc(pattern)} [kPa]</th>`).join('')}</tr></thead><tbody>`
      +d.loading.map(candidate=>`<tr><td>${esc(candidate.id)}</td>${patterns.map(pattern=>`<td>${has(candidate.components,pattern)?num(candidate.components[pattern]):'—'}</td>`).join('')}</tr>`).join('')+'</tbody></table></div>';
  }else loading+=`<div class="candidate-labels">${d.loading.map(candidate=>`<b>${esc(candidate.id)}</b><span>${esc(candidate.label)}</span>`).join('')}</div>`;
  const heights=heading('Truss-height candidates',d.heights.length)+`<table><thead><tr><th>ID</th><th>ΔH [m]</th><th>Height [m]</th></tr></thead><tbody>${d.heights.map(candidate=>`<tr><td>${esc(candidate.id)}</td><td>${candidate.deltaHM>=0?'+':''}${num(candidate.deltaHM)}</td><td>${num(candidate.heightM)}</td></tr>`).join('')}</tbody></table>`;
  let pretension=heading('Pretension candidates',d.pretensions.length);
  if(d.pretensionCables.length){
    pretension+='<p class="candidate-note">Target force [kN]. Cables are rows and pretension candidates are columns to keep printed reports compact.</p>'
      +`<div class="candidate-scroll"><table class="candidate-pretension-matrix"><thead><tr><th>Cable</th>${d.pretensions.map(candidate=>`<th title="${esc(candidate.label)}">${esc(candidate.id)}</th>`).join('')}</tr></thead><tbody>`
      +d.pretensionCables.map(cable=>`<tr><td>${esc(cable)}</td>${d.pretensions.map(candidate=>`<td>${has(candidate.cableForces,cable)?num(candidate.cableForces[cable]):(Number.isFinite(candidate.uniformForceKN)?num(candidate.uniformForceKN):'—')}</td>`).join('')}</tr>`).join('')+'</tbody></table></div>';
  }else pretension+=`<div class="candidate-labels">${d.pretensions.map(candidate=>`<b>${esc(candidate.id)}</b><span>${esc(candidate.label)}</span>`).join('')}</div>`;
  root.innerHTML=`<section class="candidate-section">${loading}</section><section class="candidate-section">${heights}</section><section class="candidate-section candidate-pretension">${pretension}</section>`;
  const summary=document.getElementById('candidateDefinitionsSummary');
  if(summary)summary.textContent=`Candidate definitions · ${d.loading.length} loading · ${d.heights.length} height · ${d.pretensions.length} pretension`;
})();
""";
}
