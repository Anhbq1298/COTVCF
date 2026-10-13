using System.Globalization;
using System.IO;
using System.Text.Json;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Domain.Results;
using Mcotis.Desktop.Infrastructure;

namespace Mcotis.Desktop.Application.Results;

/// <summary>Builds <c>Cable_Forces_Report.html</c> — prestress target vs final analysed cable force: a
/// height overview, one detailed chart per pretension candidate and user-defined cable side, and a governing /
/// capacity review. Self-contained (inline SVG, no CDN) and SAP-free: every quantity is derived from results
/// that were already extracted, so a historical <c>Study_Raw_Results.json</c> or
/// <c>Deflection_Results_Source.xlsx</c> regenerates it as-is. Capacities are supplied separately (current
/// Tab 03 configuration first) and matched by exact cable name. A missing result is a gap /
/// "Result unavailable", never 0.</summary>
public sealed class CableForceReportGenerator
{
    public const string ReportFileName = "Cable_Forces_Report.html";

    /// <summary>Report name printed in the page header.</summary>
    public const string ReportTitle = "Cable Forces";

    /// <summary>Scope key of the study-wide rows in <see cref="CableForceReportDto.Governing"/>.</summary>
    public const string StudyScope = "*";

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
        IReadOnlyList<string>? siblingReports = null,
        IReadOnlyDictionary<string, string>? capacitySources = null,
        ReportPrintSettings? print = null, ReportScope? scope = null)
    {
        Directory.CreateDirectory(studyFolder);
        var siblings = siblingReports is null
            ? ReportNav.PresentIn(studyFolder, "cable")
            : new HashSet<string>(siblingReports, StringComparer.Ordinal);
        var dto = Build(results, baselineName, studyMode, cableResultCase, capacities, siblings.ToArray(), capacitySources, scope);
        var path = Path.Combine(studyFolder, ReportFileName);
        var printDto = ReportPrintEngine.Describe(print, scope?.Title(ReportTitle) ?? ReportTitle, baselineName, DateTimeOffset.Now);
        AtomicFile.WriteAllText(path, BuildHtml(dto, printDto), new System.Text.UTF8Encoding(false));
        return path;
    }

    /// <summary>Rebuild the report from the workbook alone — no SAP. <paramref name="capacities"/> are the
    /// capacities resolved now from Tab 03 and win over the ones stored in the workbook, so changing fpu / φ
    /// re-reads the limit and utilisation without a re-run. A cable named in <paramref name="configuredCables"/>
    /// without a current capacity is reported as unavailable; a cable Tab 03 does not know keeps the capacity
    /// stored for that same cable name. Pass null to keep whatever the workbook holds.</summary>
    public string RegenerateFromWorkbook(string studyFolder,
        IReadOnlyDictionary<string, double>? capacities = null,
        IReadOnlyCollection<string>? configuredCables = null,
        ReportPrintSettings? print = null)
    {
        var wbPath = Path.Combine(studyFolder, ConsolidatedResultWriter.WorkbookFileName);
        if (!File.Exists(wbPath))
            throw new FileNotFoundException($"No {ConsolidatedResultWriter.WorkbookFileName} to regenerate from.", wbPath);
        var results = ConsolidatedResultWriter.Read(wbPath);
        var mode = results.Count > 0 ? results[0].StudyMode : IterationStudyMode.Combined;
        var name = results.Count > 0 ? results[0].BaselineModelName : "";
        var resultCase = ConsolidatedResultWriter.ReadCableResultCase(wbPath);
        var capacitySet = CableCapacityResolver.Resolve(capacities, configuredCables,
            ConsolidatedResultWriter.ReadCableCapacities(wbPath));
        return Generate(studyFolder, results, name, mode, resultCase,
            capacitySet.Capacities, capacitySources: capacitySet.Sources, print: print);
    }

    // ---------- DTO assembly ----------

    public CableForceReportDto Build(
        IReadOnlyList<CandidateResultDto> results,
        string baselineName, IterationStudyMode studyMode, string cableResultCase,
        IReadOnlyDictionary<string, double>? capacities = null,
        IReadOnlyList<string>? siblingReports = null,
        IReadOnlyDictionary<string, string>? capacitySources = null, ReportScope? scope = null)
    {
        var withCf = results.Where(r => (r.CableForces?.Count ?? 0) > 0)
            .OrderBy(r => r.Parameters.RunIndex).ThenBy(r => r.IterationKey, StringComparer.Ordinal).ToArray();

        var cables = withCf.SelectMany(r => r.CableForces!).Select(c => c.CableName)
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, NaturalNameComparer.Instance).ToArray();

        var loads = withCf.Select(r => r.Parameters)
            .GroupBy(p => p.LoadingCandidateId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, NaturalNameComparer.Instance)
            .Select(g => { var p = g.First();
                return new CableForceAxisDto(p.LoadingCandidateId,
                    $"{p.LoadingCandidateId} · {(string.IsNullOrWhiteSpace(p.LoadingLabel) ? Num(p.LoadLevelKPa) + " kPa" : p.LoadingLabel)}",
                    p.LoadLevelKPa); })
            .ToArray();

        // Heights come from the results themselves — never a fixed 20 / 25 / 30.
        var heights = withCf.Select(r => r.Parameters)
            .GroupBy(p => p.HeightCandidateId, StringComparer.Ordinal)
            .OrderBy(g => g.First().TrussHeightM).ThenBy(g => g.Key, NaturalNameComparer.Instance)
            .Select(g => { var p = g.First();
                return new CableForceAxisDto(p.HeightCandidateId, $"{p.HeightCandidateId} · {Num(p.TrussHeightM)} m", p.TrussHeightM); })
            .ToArray();

        var pretensions = withCf.Select(r => r.Parameters)
            .GroupBy(p => p.PretensionCandidateId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, NaturalNameComparer.Instance)
            .Select(g => { var p = g.First();
                return new CableForceAxisDto(p.PretensionCandidateId, p.PretensionLabel, p.PretensionKN, Mixed: p.PretensionKN is null); })
            .ToArray();

        var rows = withCf.Select(BuildRow).ToArray();

        var warnings = results.SelectMany(r => r.Warnings)
            .Where(w => w.Category == "CableForce")
            .Select(w => new CableForceWarningDto(w.IterationKey, w.Message, w.Severity == ResultWarningSeverity.Error))
            .ToArray();

        var study = new CableForceStudyDto(baselineName, studyMode.ToString(), rows.Length,
            string.IsNullOrWhiteSpace(cableResultCase) ? "(not configured)" : cableResultCase,
            DateTimeOffset.Now.ToString("o"), scope?.ToDto());

        // Keep only positive, finite capacities for cables that are actually in this report — matched by exact
        // cable object name. A cable without one gets no limit line and no utilisation; nothing is borrowed.
        var caps = new Dictionary<string, double>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cable in cables)
        {
            var source = capacitySources is not null && capacitySources.TryGetValue(cable, out var s) ? s : null;
            if (capacities is not null && capacities.TryGetValue(cable, out var cap) && CableCapacityResolver.IsCapacity(cap))
            {
                caps[cable] = cap;
                sources[cable] = source == CableCapacityResolver.Stored ? CableCapacityResolver.Stored : CableCapacityResolver.Current;
            }
            else
                sources[cable] = source == CableCapacityResolver.Invalid ? CableCapacityResolver.Invalid : CableCapacityResolver.None;
        }

        return new CableForceReportDto(study, cables, loads, heights, pretensions, rows,
            ReportCandidateDefinitions.Build(results), warnings,
            caps, sources, CableGroupDetector.Suggest(cables),
            BuildOverview(rows, cables, loads, heights, pretensions, scope),
            BuildGoverning(rows, cables, loads, caps),
            (siblingReports ?? ["cable"]).ToArray());
    }

    /// <summary>One candidate's cable rows plus the derived force development. Only values that were already
    /// extracted are read, so a legacy result package needs no extra fields.</summary>
    private static CableForceCandidateDto BuildRow(CandidateResultDto r)
    {
        var p = r.Parameters;
        // Legacy packages may lack a per-row target; the candidate's own pretension assignment for the same
        // cable name is the same number the extractor would have stored.
        var assigned = (p.PretensionCables ?? [])
            .GroupBy(c => c.CableName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().TargetForceKN, StringComparer.Ordinal);

        var targets = new Dictionary<string, double>(StringComparer.Ordinal);
        var forces = new Dictionary<string, double?>(StringComparer.Ordinal);
        var deltas = new Dictionary<string, double?>(StringComparer.Ordinal);
        var deltaPercents = new Dictionary<string, double?>(StringComparer.Ordinal);
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in r.CableForces!)
        {
            if (forces.ContainsKey(c.CableName)) continue;
            var target = Finite(c.TargetForceKN) ?? (assigned.TryGetValue(c.CableName, out var a) ? Finite(a) : null);
            var final = c.Status == ResultValueStatus.Valid ? Finite(c.ForceKN) : null;   // unavailable stays null, never 0
            if (target is { } t) targets[c.CableName] = t;
            forces[c.CableName] = final;
            deltas[c.CableName] = CableForceMath.DeltaForce(target, final);
            deltaPercents[c.CableName] = CableForceMath.DeltaPercent(target, final);
            statuses[c.CableName] = c.Status.ToString();
        }
        return new CableForceCandidateDto(
            p.RunIndex, $"C{p.RunIndex:000}", r.IterationKey, Path.GetFileName(r.CandidateModelPath),
            p.LoadingCandidateId, p.LoadLevelKPa, p.HeightCandidateId, p.TrussHeightM,
            p.PretensionCandidateId, p.PretensionLabel, p.PretensionKN is null,
            targets, forces, deltas, deltaPercents, statuses);
    }

    /// <summary>Section 01: per loading candidate × truss height, each cable's target and final-force range
    /// across the pretension candidates that actually have a result there.</summary>
    private static IReadOnlyList<CableForceHeightOverviewDto> BuildOverview(
        IReadOnlyList<CableForceCandidateDto> rows, IReadOnlyList<string> cables,
        IReadOnlyList<CableForceAxisDto> loads, IReadOnlyList<CableForceAxisDto> heights,
        IReadOnlyList<CableForceAxisDto> pretensions, ReportScope? scope = null)
    {
        var overview = new List<CableForceHeightOverviewDto>();
        foreach (var load in loads)
            foreach (var height in heights)
            {
                var here = rows.Where(r => r.LoadId == load.Id && r.HeightId == height.Id).ToArray();
                if (here.Length == 0) continue;
                var available = here.Select(r => r.PretId).ToHashSet(StringComparer.Ordinal);
                var missing = pretensions.Select(p => p.Id).Where(id => !available.Contains(id)).ToArray();
                var heightM = height.Value ?? here[0].HeightM;

                var ranges = new List<CableForceRangeDto>(cables.Count);
                var gaps = 0;
                foreach (var cable in cables)
                {
                    var targets = here.Where(r => r.Targets.ContainsKey(cable)).Select(r => r.Targets[cable]).ToArray();
                    var finals = here.Select(r => (r.PretId, Force: r.Forces.GetValueOrDefault(cable)))
                        .Where(x => x.Force.HasValue).Select(x => (x.PretId, Force: x.Force!.Value)).ToArray();
                    gaps += here.Count(r => r.Forces.ContainsKey(cable)) - finals.Length;
                    ranges.Add(new CableForceRangeDto(cable,
                        targets.Length > 0 ? targets.Min() : null, targets.Length > 0 ? targets.Max() : null, targets.Length,
                        finals.Length > 0 ? finals.Min(x => x.Force) : null, finals.Length > 0 ? finals.Max(x => x.Force) : null, finals.Length,
                        finals.Length > 0 ? finals.MinBy(x => x.Force).PretId : null,
                        finals.Length > 0 ? finals.MaxBy(x => x.Force).PretId : null));
                }

                overview.Add(new CableForceHeightOverviewDto(load.Id, height.Id, heightM,
                    pretensions.Count, available.Count, missing,
                    RangeNote(available.Count, missing, heightM, gaps,
                        scope is null ? [] : missing.Where(id => scope.IsExcluded(load.Id, height.Id, id)).ToArray()), ranges));
            }
        return overview;
    }

    /// <param name="outside">The missing candidates that have results but are outside the report's scope
    /// (Tab 06A: not qualified) — named as such, not as unavailable.</param>
    private static string RangeNote(int available, IReadOnlyList<string> missing, double heightM, int cableGaps,
        IReadOnlyList<string> outside)
    {
        var candidates = $"{available} available pretension candidate{(available == 1 ? "" : "s")}";
        if (missing.Count == 0 && cableGaps == 0) return $"Range across {candidates}.";

        var note = $"Range based on {candidates}";
        void List(IReadOnlyList<string> ids, string what)
        {
            if (ids.Count == 0) return;
            const int listed = 6;
            var names = ids.Take(listed).Select(id => $"{id}-{CableForceMath.HeightLabel(heightM)}");
            note += $"; {string.Join(", ", names)}{(ids.Count > listed ? $" +{ids.Count - listed} more" : "")} {what}";
        }
        List(missing.Where(id => !outside.Contains(id)).ToArray(), "unavailable");
        List(outside, "not qualified");
        if (cableGaps > 0)
            note += $"; {cableGaps} cable result{(cableGaps == 1 ? "" : "s")} unavailable and excluded";
        return note + ".";
    }

    /// <summary>Section 03: the candidate with the largest final analysed force per cable, inside each loading
    /// candidate and across the whole study, checked against that exact cable's capacity.</summary>
    private static IReadOnlyList<CableForceGoverningDto> BuildGoverning(
        IReadOnlyList<CableForceCandidateDto> rows, IReadOnlyList<string> cables,
        IReadOnlyList<CableForceAxisDto> loads, IReadOnlyDictionary<string, double> capacities)
    {
        var governing = new List<CableForceGoverningDto>();
        foreach (var scope in loads.Select(l => l.Id).Append(StudyScope))
        {
            var scoped = scope == StudyScope ? rows : rows.Where(r => r.LoadId == scope).ToArray();
            foreach (var cable in cables)
            {
                double? capacity = capacities.TryGetValue(cable, out var cap) ? cap : null;
                var best = scoped.Where(r => r.Forces.GetValueOrDefault(cable).HasValue)
                    .OrderByDescending(r => r.Forces[cable]!.Value).ThenBy(r => r.Run).FirstOrDefault();
                if (best is null)
                {
                    governing.Add(new(scope, cable, null, null, null, null, null, null, null, null, capacity, null, "no-result"));
                    continue;
                }
                var force = best.Forces[cable]!.Value;
                var utilisation = CableForceMath.Utilisation(force, capacity);
                governing.Add(new(scope, cable, force,
                    best.Targets.TryGetValue(cable, out var target) ? target : null,
                    best.CandidateId, best.IterationKey, best.LoadId, best.HeightId, best.HeightM, best.PretId,
                    capacity, utilisation,
                    capacity is null ? "no-capacity" : force > capacity ? "exceed" : "pass"));
            }
        }
        return governing;
    }

    private static double? Finite(double? v) => v is { } d && double.IsFinite(d) ? d : null;
    private static string Num(double? v) => v is { } d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "n/a";
    private static string Esc(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");

    // ---------- HTML ----------

    /// <param name="print">Header / footer and sheet for printing; null prints the report title, model and page
    /// numbers only.</param>
    public string BuildHtml(CableForceReportDto dto, ReportPrintDto? print = null)
    {
        var s = dto.Study;
        var json = JsonSerializer.Serialize(dto, Json);
        print ??= ReportPrintEngine.Describe(null, ReportTitle, s.BaselineModelName,
            DateTimeOffset.TryParse(s.GeneratedAtIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var generated)
                ? generated : DateTimeOffset.Now);
        var library = ReportNav.Library("cable", new HashSet<string>(dto.SiblingReports, StringComparer.Ordinal));
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Cable Forces — {{Esc(s.BaselineModelName)}}</title>
<style>{{ReportPrintEngine.Css}}{{Style}}{{ReportCandidateDefinitions.Style}}{{ReportScope.Css}}</style>{{ReportPrintEngine.PageStyle}}</head>
<body>
<svg width="0" height="0" style="position:absolute" aria-hidden="true"><defs>
  <pattern id="cf-loss" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="3" height="6" fill="#2A78D6"/></pattern>
</defs></svg>
<header>
  <div data-pr="info">
    <h1>Cable Forces Viewer</h1>
    <p class="meta">{{Esc(s.BaselineModelName)}} · {{Esc(s.StudyMode)}} · {{s.CandidateCount}} candidate(s) ·
      cable result case: {{Esc(s.ResultCase)}} · generated {{Esc(s.GeneratedAtIso[..19].Replace('T', ' '))}}</p>
    {{ReportScope.Banner(s.Scope)}}
    <p class="meta print-only" id="pview"></p>
    <p class="intro" data-pr="note"><b>Yellow</b> is the prestress target, <b>blue</b> is the change from that target to the
      final analysed force under the cable result case, and the <b>red dashed</b> line is the cable-force limit
      from the current Tab 03 Pretension capacity (area × fpu × φ), matched by exact cable name. A missing result
      is a gap, never 0 kN. Switch <b>Value</b> to <b>% of capacity</b> for utilisation; use <b>Cable Grouping</b>
      to sort cables into Side A / Side B / Unassigned — a presentation choice that never changes a result.</p>
  </div>
  {{library}}
</header>

<div id="bar">
  <span class="small">Value</span><button id="vk" class="active">Force (kN)</button><button id="vu">% of capacity</button>
  <span class="sep"></span>
  <label class="small" id="loadwrap">Load <select id="load" aria-label="Loading candidate shown in every chart"></select></label>
  <span class="sep" id="loadsep"></span>
  <button id="gbtn" aria-expanded="false" aria-controls="gpanel">Cable Grouping ▾</button>
  {{ReportPrintEngine.ToolbarButton}}
  <span class="small" id="ginfo" aria-live="polite"></span>
  <span class="small" id="yinfo" aria-live="polite"></span>
  <div id="gpanel" class="gpanel" hidden>
    <p class="small">Assign each cable to a group. Report-side only: forces, capacities, the study files, SAP2000
      and Tab 03 are never changed. Auto Detect only suggests a side where a cable name has an unambiguous
      counterpart; everything else stays Unassigned.</p>
    <div class="ghead"><span>Cable</span><span>Group</span></div>
    <div id="glist" class="glist"></div>
    <div class="gact">
      <button id="gapply" class="pri">Apply Grouping</button>
      <button id="gauto" title="Fill the list with the suggestion from paired cable names — review it, then Apply Grouping">Auto Detect</button>
      <button id="greset" title="Reset Grouping — back to the report default and forget the grouping saved in this browser">Reset</button>
      <span class="small" id="gstate" aria-live="polite"></span>
    </div>
  </div>
</div>

<div class="leg" id="legend" data-pr="legend">
  <span><svg width="14" height="10"><rect width="14" height="10" fill="#EDA100"/></svg>Prestress target</span>
  <span><svg width="14" height="10"><rect width="14" height="10" fill="#2A78D6"/></svg>Force pickup · target → final</span>
  <span><svg width="14" height="10"><rect width="14" height="10" fill="#EDA100"/><rect width="14" height="10" fill="url(#cf-loss)"/></svg>Force loss · final below target</span>
  <span id="legnt" hidden><svg width="14" height="10"><rect width="14" height="10" fill="#2A78D6" opacity=".45"/></svg>Final force · cable has no prestress target</span>
  <span><svg width="18" height="10"><line x1="0" y1="5" x2="18" y2="5" stroke="#12305F" stroke-width="2"/></svg>Final analysed force</span>
  <span><svg width="24" height="10"><line x1="0" y1="5" x2="24" y2="5" stroke="#C62828" stroke-width="1.75" stroke-dasharray="6 4"/></svg><i id="leglim">Cable force limit</i></span>
  <span class="small">Every bar starts at 0 · H = truss height [m] · n/a = result unavailable{{(s.Scope is null ? "" : " · " + ReportScope.MarkerMeaning)}}</span>
</div>
<div id="warns" data-pr="note"></div>
{{ReportCandidateDefinitions.Markup}}

<section class="blk">
  <h2 data-pr="chart" data-pr-keep>01 · Cable Force Overview</h2>
  <p class="lead" id="ovlead" data-pr="chart" data-pr-keep></p>
  <div id="overview"></div>
</section>

<section class="blk">
  <h2 data-pr="chart" data-pr-keep>02 · Force Development by Pretension Candidate</h2>
  <p class="lead" id="dtlead" data-pr="chart" data-pr-keep></p>
  <div id="pnav" class="pnav"></div>
  <div id="detail"></div>
</section>

<section class="blk">
  <h2 data-pr="chart" data-pr-keep>03 · Governing / Capacity Review</h2>
  <p class="lead" id="govlead" data-pr="chart" data-pr-keep></p>
  <div id="gov" class="gov" data-pr="chart" hidden></div>
  <div class="gvwrap" data-pr="chart" data-pr-type="governing" data-pr-id="governing"><table id="govtable" class="gv"></table></div>
  <div id="capnotes" class="capnotes" data-pr="chart"></div>
</section>

<details class="block" data-print="source" data-pr="source"><summary>Source Data</summary><div class="body"><div class="tablewrap"><table><thead><tr>
  <th>Run</th><th>Candidate</th><th>Iteration Key</th><th>Load</th><th>Height</th><th>Pretension Candidate</th><th>Cable</th>
  <th>Current Group</th><th class="num">Target Prestress [kN]</th><th class="num">Final Analysed Force [kN]</th>
  <th class="num">Delta Force [kN]</th><th class="num">Delta [%]</th><th class="num">Current Capacity [kN]</th>
  <th class="num">Final Utilisation [%]</th><th>Result Status</th>
</tr></thead><tbody id="tb"></tbody></table></div></div></details>

<div id="tip" class="tip" hidden></div>

<script id="report-data" type="application/json">{{json}}</script>
{{ReportPrintEngine.Scripts(print)}}
<script>{{ReportCandidateDefinitions.Script}}</script>
<script>{{Script}}</script>
</body></html>
""";
    }

    private const string Style = """
:root{--acc:#1F4788;--bd:#C7CCD2;--su:#F3F4F6;--tx:#1A1A1A;--mu:#666;--tg:#EDA100;--fd:#2A78D6;--lim:#C62828}
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
button.pri{background:var(--acc);border-color:var(--acc);color:#fff;font-weight:600}button.pri:hover{background:#17376b}
.sep{width:1px;height:16px;background:var(--bd)}.small{font-size:11px;color:var(--mu)}
#yinfo{margin-left:auto}
/* cable grouping panel */
.gpanel{flex-basis:100%;border-top:1px solid var(--bd);margin-top:2px;padding:8px 0 4px}
.gpanel p{margin:0 0 6px;max-width:96ch;line-height:1.45}
.ghead{display:flex;justify-content:space-between;max-width:250px;font-size:10.5px;font-weight:600;letter-spacing:.03em;text-transform:uppercase;color:#586173;padding-bottom:2px}
.glist{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:3px 22px;max-height:38vh;overflow:auto;margin:0 0 8px}
.grow{display:flex;align-items:center;justify-content:space-between;gap:8px;font-size:11.5px}
.grow span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-weight:600}
.grow select{flex:none;padding:2px 6px}
.gact{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
/* legend */
.leg{display:flex;flex-wrap:wrap;gap:5px 18px;align-items:center;padding:7px 14px;border-bottom:1px solid var(--bd);font-size:11px;color:#475467}
.leg>span{display:inline-flex;align-items:center;gap:6px}.leg svg{flex:none}.leg i{font-style:normal}
section.blk{border-bottom:1px solid var(--bd);padding:12px 14px 16px}
section.blk h2{font-size:12.5px;color:var(--acc);margin:0 0 3px;letter-spacing:.01em}
.lead{font-size:11.5px;line-height:1.5;color:var(--mu);max-width:110ch;margin:0 0 10px}
.lead b{color:var(--tx)}
/* chart cards */
.card{border:1px solid var(--bd);border-radius:3px;margin:0 0 10px;background:#fff}
.ch{display:flex;justify-content:space-between;align-items:baseline;gap:12px;padding:5px 10px;background:var(--su);border-bottom:1px solid var(--bd)}
.ct{font-weight:600;color:var(--acc);font-size:12px;white-space:nowrap}
.cs{font-size:11px;color:var(--mu);text-align:right}.cs b{color:var(--lim)}
.cwrap{overflow-x:auto;padding:4px 6px 2px}
.empty{margin:0;padding:9px 10px;color:var(--mu);font-size:11.5px}
.pblk{margin:0 0 16px;scroll-margin-top:72px}
.ph{display:flex;align-items:baseline;gap:10px;margin:0 0 6px;padding-bottom:3px;border-bottom:1px solid var(--bd)}
.ph b{font-size:13px;color:var(--acc)}.ph span{font-size:11px;color:var(--mu)}
.pnav{display:flex;flex-wrap:wrap;align-items:center;gap:4px;margin:0 0 10px;font-size:11px;color:var(--mu)}
.pnav a{padding:2px 8px;border:1px solid var(--bd);border-radius:3px;text-decoration:none;background:#fff}
.pnav a:hover{background:var(--su)}
svg{display:block}svg text{font-family:"Segoe UI",system-ui,sans-serif}
.grid{stroke:#e8ebef;stroke-width:1}.axis{stroke:#8e98a3;stroke-width:1}
.tick{fill:#687386;font-size:9px}.al{fill:#475467;font-size:9.5px;font-weight:600}
.tb{fill:var(--tg)}.tp{fill:var(--tg);opacity:.3}
.fb{fill:var(--fd)}.fp{fill:var(--fd);opacity:.26}.nb{fill:var(--fd);opacity:.45}
.sg{stroke:#fff;stroke-width:1.5}
.fc{stroke:#12305F;stroke-width:2}.fc.ex{stroke:var(--lim)}
.lim{stroke:var(--lim);stroke-width:1.75;stroke-dasharray:6 4;fill:none}
.limt{fill:var(--lim);font-size:9px;font-weight:600}
.vl{fill:var(--tx);font-size:9px;text-anchor:middle;paint-order:stroke;stroke:#fff;stroke-width:2.5px;stroke-linejoin:round}
.vl.in{stroke:none}.vl.ex{fill:var(--lim);font-weight:700}
.dl{fill:#fff;font-size:8.5px;font-weight:600;text-anchor:middle}.dl.lo{fill:#12305F}
.dp{fill:#fff;opacity:.93}
.na{fill:#98a2b3;font-size:8.5px;font-style:italic;text-anchor:middle}
.hl{fill:#687386;font-size:9px;text-anchor:middle}
.cl{fill:var(--tx);font-size:10px;font-weight:600;text-anchor:middle}.cl.s{font-size:9.5px}
.gl{fill:var(--acc);font-size:10px;font-weight:600;text-anchor:middle}
.br{fill:none;stroke:#8e98a3;stroke-width:1}
.hit{fill:transparent}.hit:hover{fill:rgba(31,71,136,.07)}
.tip{position:fixed;z-index:30;pointer-events:none;background:#1f2937;color:#fff;border-radius:4px;padding:7px 9px;font-size:11px;line-height:1.5;box-shadow:0 4px 14px rgba(0,0,0,.25);max-width:360px}
.tip div{display:flex;justify-content:space-between;gap:14px}
.tip span{color:#cbd5e1;white-space:nowrap}.tip b{font-weight:600;text-align:right}
.tip .warn b{color:#ffb4ab}
/* 03 governing / capacity review */
.gov{display:grid;grid-template-columns:auto 1fr;gap:3px 16px;align-items:center;margin:0 0 12px;
  background:var(--su);border:1px solid var(--bd);border-left:3px solid #C62828;border-radius:3px;padding:10px 14px;max-width:980px}
.gov .pill{grid-row:1 / span 2;align-self:center;font-size:10px;font-weight:700;letter-spacing:.05em;text-transform:uppercase;color:#C62828;border:1px solid #C62828;border-radius:9px;padding:4px 10px}
.gov .l1{font:600 14px "Segoe UI",sans-serif}.gov .l2{font-size:11.5px;color:#586173}
.gvwrap{overflow-x:auto;border:1px solid var(--bd);border-radius:3px;max-width:1180px}
.gv{font-size:11px}.gv th{position:static}.gv td{padding:4px 8px}.gv tr:last-child td{border-bottom:0}
.gv .trk{position:relative;display:block;width:130px;height:12px;background:var(--su);border:1px solid var(--bd);border-radius:3px;overflow:hidden}
.gv .fil{position:absolute;left:0;top:0;bottom:0}
.gv .tk{position:absolute;top:0;bottom:0;width:2px;background:#C62828}
.st{font-weight:600;white-space:nowrap}.st.ok{color:#2E7D32}.st.bad{color:#C62828}.st.na{color:#8a6d00;font-weight:500}
.capnotes{margin-top:8px;font-size:11px;color:#586173;max-width:110ch;line-height:1.5}
.capnotes p{margin:0 0 3px}.capnotes .w{color:#8a6d00}
/* shared blocks */
details.block{margin:10px 12px;border:1px solid var(--bd);border-radius:3px}
details.block>summary{padding:7px 9px;cursor:pointer;color:var(--acc);font-weight:600;background:var(--su)}
.body{border-top:1px solid var(--bd);padding:8px 10px}
.tablewrap{max-height:340px;overflow:auto}
table{border-collapse:collapse;width:100%;font-size:10.5px}
th{position:sticky;top:0;background:#eef1f4;padding:6px;border-bottom:1px solid var(--bd);text-align:left;white-space:nowrap}
td{padding:5px 6px;border-bottom:1px solid #eee;white-space:nowrap}
.num{text-align:right;font-variant-numeric:tabular-nums}.missing{color:#b42318;font-weight:600}
#warns{padding:6px 14px;font-size:11px;color:#8a6d00}#warns .e{color:#C62828}
a{color:var(--acc)}
#warns:empty{display:none}
/* on a sheet of the Print Report screen: the sheet, header and footer come from the shared print engine */
.pr-sheet .intro{max-width:none;margin:0 0 6px}
.pr-sheet .leg{padding:0 0 6px;gap:3px 14px;font-size:10px;border:0}
.pr-sheet h2{margin:6px 0 3px}
.pr-sheet .lead{max-width:none;margin:0 0 6px;font-size:10.5px}
.pr-sheet .card,.pr-sheet .ph{margin:0 0 8px}.pr-sheet .ph{margin-bottom:4px}
.pr-sheet .cwrap{overflow:visible;padding:3px 4px 0}
.pr-sheet .gov,.pr-sheet .gvwrap,.pr-sheet .capnotes{max-width:none}
.pr-sheet .gvwrap{overflow:visible}
.pr-sheet details.block{margin:0;border:0}
.pr-sheet details.block>summary{padding:0 0 4px;background:none}
.pr-sheet .body{border-top:0;padding:0}
.pr-sheet .tablewrap{max-height:none;overflow:visible}
/* a table wider than the sheet would be cut off at the paper edge: let cells wrap instead */
.pr-sheet th{position:static}.pr-sheet th,.pr-sheet td{white-space:normal}
.pr-sheet .tablewrap table{font-size:8.5px}.pr-sheet .tablewrap th,.pr-sheet .tablewrap td{padding:2px 4px}
.pr-sheet #warns{padding:0 0 6px}
""";

    // Self-contained report script: everything is rendered client-side from the embedded #report-data, so the
    // value toggle, the load selector and the cable grouping all re-render without a server, SAP2000 or a study
    // re-run. The grouping is presentation state only; it never feeds back into a force, capacity or check.
    private const string Script = """
"use strict";
const D = JSON.parse(document.getElementById("report-data").textContent);
// A report on a subset of the study (Tab 06A): iterations that have results but are outside the set
const NQSET = new Set(((D.study.scope || {}).excluded || []).map(x => x.join("|"))), notQualified = (heightId, pretId) => NQSET.has(S.load + "|" + heightId + "|" + pretId);
const el = id => document.getElementById(id);
const CAP = D.capacities || {}, CAPSRC = D.capacitySources || {};
const CAPS_ANY = Object.keys(CAP).length > 0;
const SIDE = { A: "Side A", B: "Side B", "": "Unassigned" };
const SIDE_KEYS = ["A", "B", ""];
const PRINT = window.reportPrint || null;                 // shared print engine (sheet, header / footer)
const OPT = PRINT ? PRINT.options : {};                   // "#print&mode=ut&load=L02" from the PDF exporter
const S = { mode: "kn", load: D.loads.some(l => l.id === OPT.load) ? OPT.load : D.loads.length ? D.loads[0].id : "",
            groups: {}, draft: {}, saved: false };
const TIPS = [];

// ---------- formatting ----------
const finite = v => typeof v === "number" && isFinite(v);
const esc = t => String(t == null ? "" : t).replace(/[<&>"']/g, c => ({ "<": "&lt;", "&": "&amp;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const r1 = v => Math.round(v * 10) / 10;
function group3(s){ let out = ""; for (let i = 0; i < s.length; i++){ if (i && (s.length - i) % 3 === 0) out += ","; out += s[i]; } return out; }
function fnum(v, dp){
  if (!finite(v)) return "N/A";
  const a = Math.abs(v).toFixed(dp || 0), p = a.split(".");
  return (v < 0 && Number(a) !== 0 ? "−" : "") + group3(p[0]) + (p.length > 1 ? "." + p[1] : "");
}
const kn = v => finite(v) ? fnum(v, 0) + " kN" : "N/A";
const pc = v => finite(v) ? fnum(v, 1) + "%" : "N/A";
const signed = (v, dp) => finite(v) ? (v > 0 ? "+" : "") + fnum(v, dp) : "N/A";
const mText = m => String(Math.round(m * 1000) / 1000);          // a height in metres, no padding zeros
const hShort = m => "H" + mText(m);
// approximate rendered width of a Segoe UI label, used only to keep labels from colliding
function tw(text, px){
  let w = 0;
  for (const ch of String(text))
    w += /[0-9]/.test(ch) ? .56 : /[,.·:;'| ]/.test(ch) ? .28 : /[%MW@]/.test(ch) ? .86 : /[+−=\-]/.test(ch) ? .68
       : /[A-Z]/.test(ch) ? .66 : /[iljtfr]/.test(ch) ? .34 : .56;
  return w * px;
}
function clip(text, px, max){
  if (tw(text, px) <= max) return text;
  let s = String(text);
  while (s.length > 1 && tw(s + "…", px) > max) s = s.slice(0, -1);
  return s + "…";
}

// ---------- data access ----------
const rowIndex = {};
D.rows.forEach(r => { rowIndex[r.loadId + "|" + r.heightId + "|" + r.pretId] = r; });
const rowAt = (heightId, pretId) => rowIndex[S.load + "|" + heightId + "|" + pretId] || null;
const capOf = c => finite(CAP[c]) && CAP[c] > 0 ? CAP[c] : null;     // exact cable name; never borrowed
const utOn = () => S.mode === "ut";
// a force in the current value mode: kN as-is, or % of that cable's own capacity (null when it has none)
const val = (f, c) => !finite(f) ? null : utOn() ? (capOf(c) ? f / capOf(c) * 100 : null) : f;
const capText = c => capOf(c) ? kn(capOf(c)) + (CAPSRC[c] === "stored" ? " (stored with results)" : "")
  : CAPSRC[c] === "invalid" ? "Unavailable from current Tab 3" : "N/A";
const loadLabel = () => { const l = D.loads.find(x => x.id === S.load); return l ? l.label : S.load; };

// ---------- cable grouping (presentation only) ----------
const cleanGroups = g => { const out = {}; D.cables.forEach(c => { const s = g ? g[c] : ""; out[c] = s === "A" || s === "B" ? s : ""; }); return out; };
const defaultGroups = () => cleanGroups(D.autoGroups || {});
const cablesOf = side => D.cables.filter(c => S.groups[c] === side);
function counts(g){ let a = 0, b = 0, u = 0; D.cables.forEach(c => { if (g[c] === "A") a++; else if (g[c] === "B") b++; else u++; }); return { a, b, u }; }
const unassignedText = n => n + (n === 1 ? " cable" : " cables") + " currently unassigned.";
const STORE_KEY = "mcotis.cableGroups:" + D.study.baselineModelName;
function loadSaved(){
  try {
    const raw = window.localStorage.getItem(STORE_KEY);
    if (!raw) return null;
    const g = JSON.parse(raw), d = defaultGroups(), out = {};
    if (!g || typeof g !== "object") return null;
    D.cables.forEach(c => { out[c] = Object.prototype.hasOwnProperty.call(g, c) ? g[c] : d[c]; });
    return cleanGroups(out);
  } catch (e) { return null; }
}
function saveGroups(){ try { window.localStorage.setItem(STORE_KEY, JSON.stringify(S.groups)); S.saved = true; } catch (e) { S.saved = false; } }
function clearSaved(){ try { window.localStorage.removeItem(STORE_KEY); } catch (e) { } S.saved = false; }
// blocks to chart: Side A + Side B (+ Unassigned when any); or one "All cables" block while nothing is assigned
function blocks(){
  const a = cablesOf("A"), b = cablesOf("B"), u = cablesOf("");
  if (!a.length && !b.length) return [{ side: "", label: "All cables · Unassigned", cables: u, all: true }];
  const out = [{ side: "A", label: SIDE.A, cables: a }, { side: "B", label: SIDE.B, cables: b }];
  if (u.length) out.push({ side: "", label: SIDE[""], cables: u });
  return out;
}

// ---------- shared scale + limit ----------
function niceStep(max, ticks){
  const raw = max / ticks, p = Math.pow(10, Math.floor(Math.log(raw) / Math.LN10)), n = raw / p;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * p;
}
// one vertical scale for every chart of the selected load, always from 0
function scale(){
  let mx = 0;
  D.rows.forEach(r => { if (r.loadId !== S.load) return;
    D.cables.forEach(c => { const t = val(r.targets[c], c), f = val(r.forces[c], c);
      if (finite(t) && t > mx) mx = t; if (finite(f) && f > mx) mx = f; }); });
  if (utOn()) mx = Math.max(mx, 100);
  else D.cables.forEach(c => { if (capOf(c) && capOf(c) > mx) mx = capOf(c); });
  if (!(mx > 0)) mx = 1;
  // 12 % headroom keeps the labels and the limit clear of the frame: 100 % -> 0–120 %, 35,411 kN -> 0–40,000 kN
  const step = niceStep(mx * 1.12, 7);
  return { max: Math.ceil(mx * 1.12 / step - 1e-9) * step, step };
}
// equal valid capacities on every displayed cable -> one line; otherwise one segment per cable that has one
function limitPlan(cables){
  if (utOn()) return { kind: "util" };
  const caps = cables.map(capOf), have = caps.filter(v => v != null);
  if (!have.length) return { kind: "none" };
  const lo = Math.min.apply(null, have), hi = Math.max.apply(null, have);
  if (have.length === caps.length && hi - lo <= Math.max(0.5, hi * 1e-6)) return { kind: "common", value: hi };
  return { kind: "cable" };
}
function frame(o){
  const P = [], y0 = o.mT + o.plotH, Y = v => o.mT + o.plotH - v / o.sc.max * o.plotH;
  const n = Math.round(o.sc.max / o.sc.step);
  for (let i = 0; i <= n; i++){
    const v = i * o.sc.step, y = r1(Y(v));
    P.push(`<line x1="${o.mL}" y1="${y}" x2="${o.mL + o.plotW}" y2="${y}" class="${i ? "grid" : "axis"}"/>`);
    P.push(`<text x="${o.mL - 6}" y="${y + 3}" text-anchor="end" class="tick">${fnum(v, o.sc.step % 1 ? 1 : 0)}${utOn() ? "%" : ""}</text>`);
  }
  P.push(`<line x1="${o.mL}" y1="${o.mT}" x2="${o.mL}" y2="${y0}" class="axis"/>`);
  const ym = o.mT + o.plotH / 2;
  P.push(`<text x="11" y="${ym}" text-anchor="middle" class="al" transform="rotate(-90 11 ${ym})">${utOn() ? "Utilisation (% of capacity)" : "Cable force (kN)"}</text>`);
  return { P, Y, y0 };
}
function limitMarks(plan, slots, o, Y){
  const P = [], xr = o.mL + o.plotW;
  if (plan.kind === "util" || plan.kind === "common"){
    const y = r1(Y(plan.kind === "util" ? 100 : plan.value));
    P.push(`<line x1="${o.mL}" y1="${y}" x2="${xr}" y2="${y}" class="lim" data-limit="${plan.kind}"/>`);
    P.push(plan.kind === "util"
      ? `<text x="${xr + 6}" y="${y + 3}" class="limt">Design Limit · 100%</text>`
      : `<text x="${xr + 6}" y="${y - 2}" class="limt">Cable Force Limit</text><text x="${xr + 6}" y="${y + 9}" class="limt">${fnum(plan.value, 0)} kN</text>`);
  } else if (plan.kind === "cable"){
    let n = 0;
    slots.forEach(s => {
      const cap = capOf(s.cable); if (!cap) return;      // no capacity -> no limit drawn for this cable
      const y = r1(Y(cap)); n++;
      P.push(`<line x1="${r1(s.x0)}" y1="${y}" x2="${r1(s.x1)}" y2="${y}" class="lim" data-limit="cable"/>`);
    });
    if (n) P.push(`<text x="${xr + 6}" y="${o.mT + 8}" class="limt">Cable Force Limit</text><text x="${xr + 6}" y="${o.mT + 19}" class="limt">per cable</text>`);
  }
  return P;
}
const svgOpen = (W, H, label, data) => `<svg width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(label)}" ${data}>`;

// ---------- 01 · height overview ----------
function overviewChart(ov, bl, width, sc){
  const order = [];
  bl.forEach((b, bi) => b.cables.forEach(c => order.push({ c, bi })));
  const sided = !bl[0].all, n = order.length;
  const mL = 56, mR = 112, mT = 30, plotH = 150, gapG = 22, pad = 10, mB = sided ? 44 : 26;
  const nameW = Math.max.apply(null, order.map(x => tw(x.c, 9.5)));
  const avail = width - mL - mR - 2 * pad - (bl.length - 1) * gapG;
  const pitch = Math.max(46, Math.min(nameW + 10, 130), Math.min(72, Math.floor(avail / n)));
  const plotW = 2 * pad + n * pitch + (bl.length - 1) * gapG;
  const o = { mL, mT, plotW, plotH, sc }, f = frame(o), Y = f.Y, y0 = f.y0;
  const bw = Math.min(15, Math.floor((pitch - 14) / 2));
  const byCable = {}; ov.ranges.forEach(rg => { byCable[rg.cable] = rg; });
  const marks = [], caps = [], labels = [], hits = [], slots = [];
  let x = mL + pad, exceed = 0;
  bl.forEach((b, bi) => {
    const gx0 = x;
    b.cables.forEach(c => {
      const rg = byCable[c] || {}, xc = x + pitch / 2, xT = xc - bw - 1.5, xF = xc + 1.5, cap = capOf(c);
      const tMin = val(rg.targetMin, c), tMax = val(rg.targetMax, c), fMin = val(rg.finalMin, c), fMax = val(rg.finalMax, c);
      const over = finite(rg.finalMax) && cap && rg.finalMax > cap;
      if (over) exceed++;
      slots.push({ cable: c, x0: x + 3, x1: x + pitch - 3 });
      if (utOn() && !cap) labels.push(`<text x="${r1(xc)}" y="${r1(y0 - 6)}" class="na">N/A</text>`);
      else {
        if (finite(tMax)){
          const yA = Y(tMax), yB = Y(tMin);
          marks.push(`<rect x="${r1(xT)}" y="${r1(yB)}" width="${bw}" height="${r1(Math.max(0, y0 - yB))}" class="tp"/>`);
          // identical targets across the candidates: one target level, not a fake range
          marks.push(yB - yA >= 2
            ? `<rect x="${r1(xT)}" y="${r1(yA)}" width="${bw}" height="${r1(yB - yA)}" class="tb" data-tr="range"/>`
            : `<rect x="${r1(xT - 1.5)}" y="${r1(yA - 1.5)}" width="${bw + 3}" height="3" class="tb" data-tr="level"/>`);
        }
        if (finite(fMax)){
          const yA = Y(fMax), yB = Y(fMin);
          marks.push(`<rect x="${r1(xF)}" y="${r1(yB)}" width="${bw}" height="${r1(Math.max(0, y0 - yB))}" class="fp"/>`);
          marks.push(`<rect x="${r1(xF)}" y="${r1(yA)}" width="${bw}" height="${r1(Math.max(3, yB - yA))}" class="fb"/>`);
          caps.push(`<line x1="${r1(xF - 2)}" y1="${r1(yA)}" x2="${r1(xF + bw + 2)}" y2="${r1(yA)}" class="fc${over ? " ex" : ""}"/>`);
        }
      }
      // value strip above the plot: the largest final force of the range, one per cable
      let text = !finite(fMax) ? "n/a" : utOn() ? fnum(fMax, 1) + "%" : fnum(fMax, 0);
      if (finite(fMax) && tw(text, 9) > pitch - 4) text = utOn() ? fnum(fMax, 0) + "%" : fnum(fMax / 1000, 1) + "k";
      labels.push(`<text x="${r1(xc)}" y="14" class="${finite(fMax) ? "vl" + (over ? " ex" : "") : "na"}">${text}</text>`);
      labels.push(`<text x="${r1(xc)}" y="${y0 + 13}" class="cl s">${esc(clip(c, 9.5, pitch - 4))}</text>`);
      const tip = [["Cable", c], ["Group", SIDE[S.groups[c]]], ["Truss Height", mText(ov.heightM) + " m (" + ov.heightId + ")"],
        ["Prestress Target range", !rg.targetCount ? "N/A" : rg.targetMin === rg.targetMax ? kn(rg.targetMin) : fnum(rg.targetMin, 0) + " – " + kn(rg.targetMax)],
        ["Final Analysed Force range", !rg.finalCount ? "Result unavailable" : fnum(rg.finalMin, 0) + " – " + kn(rg.finalMax)]];
      if (rg.finalCount) tip.push(["Final max / min at", rg.finalMaxPretId + " / " + rg.finalMinPretId]);
      tip.push(["Candidates in range", (rg.finalCount || 0) + " of " + ov.expectedCount], ["Capacity", capText(c)],
        ["Max Utilisation", cap && finite(rg.finalMax) ? pc(rg.finalMax / cap * 100) : "N/A"], ["Result Case", D.study.resultCase]);
      if (over) tip.push(["Check", "Above cable-force limit", "warn"]);
      hits.push(`<rect class="hit" x="${r1(x)}" y="2" width="${pitch}" height="${mT + plotH + 14}" data-t="${TIPS.push(tip) - 1}"/>`);
      x += pitch;
    });
    if (sided){
      labels.push(`<path d="M ${r1(gx0 + 4)} ${y0 + 19} v 4 H ${r1(x - 4)} v -4" class="br"/>`);
      labels.push(`<text x="${r1((gx0 + x) / 2)}" y="${y0 + 37}" class="gl">${esc(b.label)}</text>`);
    }
    x += gapG;
  });
  const W = mL + plotW + mR, H = mT + plotH + mB;
  f.P.push(`<text x="${mL - 6}" y="14" text-anchor="end" class="tick">final max</text>`);
  const svg = svgOpen(W, H, "Height " + hShort(ov.heightM) + ": prestress target and final analysed force range per cable",
      `data-chart="overview" data-height="${esc(ov.heightId)}"`)
    + f.P.join("") + marks.join("") + limitMarks(limitPlan(order.map(z => z.c)), slots, o, Y).join("") + caps.join("") + labels.join("") + hits.join("") + "</svg>";
  return { svg, exceed, n };
}
function renderOverview(width, sc, bl){
  const list = D.overview.filter(o => o.loadId === S.load);
  el("ovlead").innerHTML = "One chart per truss height in this study. For every cable the <b>yellow</b> bar is the range of prestress targets and the <b>blue</b> bar the range of final analysed forces across the pretension candidates — pale below the minimum, solid from minimum to maximum, both from 0. Identical targets draw as one target level. The number above each cable is its largest final "
    + (utOn() ? "utilisation" : "force") + ". A candidate without a result is left out of the range, never read as 0.";
  if (!list.length || !D.cables.length){ el("overview").innerHTML = '<p class="empty">No cable-force results for this loading candidate.</p>'; return; }
  el("overview").innerHTML = list.map(ov => {
    const c = overviewChart(ov, bl.filter(b => b.cables.length), width, sc);
    return `<div class="card" data-pr="chart" data-pr-type="cableOverview" data-pr-id="overview_${esc(ov.loadId)}_${esc(ov.heightId)}"><div class="ch"><span class="ct">${esc(hShort(ov.heightM))} · truss height ${mText(ov.heightM)} m</span>`
      + `<span class="cs">Height candidate ${esc(ov.heightId)} · ${esc(ov.note)}${c.exceed ? ` · <b>${c.exceed} of ${c.n} cables above limit</b>` : ""}</span></div><div class="cwrap">${c.svg}</div></div>`;
  }).join("");
}

// ---------- 02 · force development per pretension candidate × side ----------
function detailChart(pret, block, width, sc){
  const cables = block.cables, hs = D.heights, nH = hs.length, nC = cables.length;
  const mL = 56, mR = 112, mT = 20, plotH = 190, mB = 46, gap = 18, pad = 10;
  const nameW = Math.max.apply(null, cables.map(c => tw(c, 10)));
  const avail = width - mL - mR - 2 * pad - (nC - 1) * gap;
  const pitch = Math.max(30, Math.min(46, Math.floor(avail / (nC * nH))));
  const barW = Math.min(30, pitch - 6);
  const clusterW = Math.max(nH * pitch, Math.min(nameW + 8, 150));
  const plotW = 2 * pad + nC * clusterW + (nC - 1) * gap;
  const o = { mL, mT, plotW, plotH, sc }, f = frame(o), Y = f.Y, y0 = f.y0;
  const bars = [], slots = [], axis = [];
  cables.forEach((c, ci) => {
    const cx0 = mL + pad + ci * (clusterW + gap), bx0 = cx0 + (clusterW - nH * pitch) / 2, cap = capOf(c);
    slots.push({ cable: c, x0: cx0 - gap / 2 + 3, x1: cx0 + clusterW + gap / 2 - 3 });
    hs.forEach((h, hi) => {
      const row = rowAt(h.id, pret.id), sx = bx0 + hi * pitch;
      const tKn = row && finite(row.targets[c]) ? row.targets[c] : null, fKn = row && finite(row.forces[c]) ? row.forces[c] : null;
      bars.push({ c, h, row, sx, x: sx + (pitch - barW) / 2, xc: sx + pitch / 2, cap, tKn, fKn,
        t: val(tKn, c), f: val(fKn, c), dp: row ? row.deltaPercents[c] : null, dk: row ? row.deltas[c] : null,
        over: fKn != null && cap != null && fKn > cap, blind: utOn() && !cap });
      axis.push(`<text x="${r1(sx + pitch / 2)}" y="${y0 + 12}" class="hl">${esc(hShort(h.value))}</text>`);
    });
    // two-level category axis: heights under each bar, the cable under a bracket spanning its cluster
    axis.push(`<path d="M ${r1(bx0 + 3)} ${y0 + 17} v 3 H ${r1(bx0 + nH * pitch - 3)} v -3" class="br"/>`);
    axis.push(`<text x="${r1(cx0 + clusterW / 2)}" y="${y0 + 34}" class="cl">${esc(clip(c, 10, clusterW + gap - 6))}</text>`);
    if (utOn() && !cap) axis.push(`<text x="${r1(cx0 + clusterW / 2)}" y="${r1(mT + plotH / 2)}" class="na">Capacity N/A</text>`);
  });

  // label formats are decided per chart so neighbouring bars never collide and stay consistent
  const valueText = (b, compact) => utOn() ? fnum(b.f, compact ? 0 : 1) + "%" : compact ? fnum(b.f / 1000, 1) + "k" : fnum(b.f, 0);
  const shown = bars.filter(b => finite(b.f));
  const compact = shown.some(b => tw(valueText(b, false), 9) > pitch - 3);
  const dText = b => (b.dp > 0 ? "+" : "−") + fnum(Math.abs(b.dp), Math.abs(b.dp) < 10 ? 1 : 0) + "%";
  const pick = shown.filter(b => finite(b.t) && b.f > b.t && finite(b.dp));
  const rotate = pick.some(b => tw(dText(b), 8.5) > barW - 2);

  const marks = [], caps = [], labels = [], hits = [];
  let exceed = 0, missing = 0;
  bars.forEach(b => {
    const tip = [["Pretension Candidate", pret.id], ["Cable", b.c], ["Group", SIDE[S.groups[b.c]]],
      ["Truss Height", mText(b.h.value) + " m (" + b.h.id + ")"]];
    if (!b.row){ tip.push(["Result", notQualified(b.h.id, pret.id) ? "Not qualified — outside the " + D.study.scope.label.toLowerCase() : "Result unavailable"]); }
    else {
      tip.push(["Prestress Target", kn(b.tKn)], ["Final Analysed Force", b.fKn == null ? "Result unavailable" : kn(b.fKn)],
        ["Force Change", finite(b.dk) ? signed(b.dk, 0) + " kN" : "N/A"], ["Force Change", finite(b.dp) ? signed(b.dp, 1) + "%" : "N/A"],
        ["Capacity", capText(b.c)], ["Utilisation", b.cap && b.fKn != null ? pc(b.fKn / b.cap * 100) : "N/A"],
        ["Result Case", D.study.resultCase]);
      if (b.over) tip.push(["Check", "Above cable-force limit", "warn"]);
    }
    hits.push(`<rect class="hit" x="${r1(b.sx)}" y="${mT - 14}" width="${pitch}" height="${plotH + 28}" data-t="${TIPS.push(tip) - 1}"/>`);
    if (b.fKn == null) missing++;
    if (b.over) exceed++;
    if (b.blind) return;                                   // % of capacity without a capacity: nothing to draw
    const x = r1(b.x), yT = finite(b.t) ? Y(b.t) : null, yF = finite(b.f) ? Y(b.f) : null;
    if (yT == null && yF == null){ labels.push(`<text x="${r1(b.xc)}" y="${y0 - 5}" class="na">${!b.row && notQualified(b.h.id, pret.id) ? "NQ" : "n/a"}</text>`); return; }   // a gap, never a zero bar
    if (yT != null && b.t > 0) marks.push(`<rect x="${x}" y="${r1(yT)}" width="${barW}" height="${r1(y0 - yT)}" class="tb"/>`);
    if (yF == null){ labels.push(`<text x="${r1(b.xc)}" y="${r1(yT - 5)}" class="na">n/a</text>`); return; }
    let yLabel = yF - 5, cls = "vl";
    if (yT == null) marks.push(`<rect x="${x}" y="${r1(yF)}" width="${barW}" height="${r1(y0 - yF)}" class="nb"/>`);
    else if (b.f > b.t){                                   // pickup: blue stacked on the target, top = final
      marks.push(`<rect x="${x}" y="${r1(yF)}" width="${barW}" height="${r1(yT - yF)}" class="fb" data-dev="pickup"/>`);
      if (b.t > 0) marks.push(`<line x1="${x}" y1="${r1(yT)}" x2="${x + barW}" y2="${r1(yT)}" class="sg"/>`);
      const text = finite(b.dp) ? dText(b) : "", h = yT - yF, w = tw(text, 8.5), ym = r1((yT + yF) / 2);
      if (text && !rotate && h >= 13) labels.push(`<text x="${r1(b.xc)}" y="${ym + 3}" class="dl">${text}</text>`);
      else if (text && rotate && h >= w + 8) labels.push(`<text x="${r1(b.xc)}" y="${ym + 3}" class="dl" transform="rotate(-90 ${r1(b.xc)} ${ym})">${text}</text>`);
    } else if (b.f < b.t){                                 // loss: band from final up to target, lower edge = final
      marks.push(`<rect x="${x}" y="${r1(yT)}" width="${barW}" height="${r1(yF - yT)}" fill="url(#cf-loss)" data-dev="loss"/>`);
      const text = finite(b.dp) ? dText(b) : "", h = yF - yT, w = tw(text, 8.5), ym = r1((yT + yF) / 2);
      if (text && h >= 13 && w + 6 <= pitch - 2){
        labels.push(`<rect x="${r1(b.xc - w / 2 - 3)}" y="${ym - 6}" width="${r1(w + 6)}" height="12" rx="2" class="dp"/>`);
        labels.push(`<text x="${r1(b.xc)}" y="${ym + 3}" class="dl lo">${text}</text>`);
      }
      if (y0 - yF >= 14){ yLabel = yF + 11; cls = "vl in"; } else yLabel = yT - 5;
    }
    caps.push(`<line x1="${x - 2}" y1="${r1(yF)}" x2="${x + barW + 2}" y2="${r1(yF)}" class="fc${b.over ? " ex" : ""}"/>`);
    // a label that would sit on this cable's limit line steps clear of it, so neither hides the other
    const yL = utOn() ? Y(100) : b.cap ? Y(b.cap) : null;
    if (yL != null && yL > yLabel - 10 && yL < yLabel + 3){
      if (cls === "vl") yLabel = yL - 4; else if (y0 - yL >= 16) yLabel = yL + 11;
    }
    const text = valueText(b, compact);
    if (tw(text, 9) <= pitch - 1) labels.push(`<text x="${r1(b.xc)}" y="${r1(yLabel)}" class="${cls}${b.over ? " ex" : ""}">${text}</text>`);
  });

  const W = mL + plotW + mR, H = mT + plotH + mB;
  // draw order: grid, bars, capacity line, final-force markers and labels
  const svg = svgOpen(W, H, pret.id + " — " + block.label + ": prestress target and final analysed force per cable and truss height",
      `data-chart="detail" data-pret="${esc(pret.id)}" data-side="${esc(block.side)}"`)
    + f.P.join("") + marks.join("") + limitMarks(limitPlan(cables), slots, o, Y).join("") + caps.join("") + labels.join("") + axis.join("") + hits.join("") + "</svg>";
  return { svg, exceed, missing, n: bars.length };
}
function pretNote(p){ return p.mixed ? "per-cable prestress targets" : finite(p.value) ? fnum(p.value, 0) + " kN prestress target" : ""; }
function renderDetail(width, sc, bl){
  const pres = D.pretensions.filter(p => D.rows.some(r => r.loadId === S.load && r.pretId === p.id));
  const n = counts(S.groups);
  el("dtlead").innerHTML = "One chart per pretension candidate and cable group. Bars are clustered by cable; each bar is one truss height (H = height in m), from 0. <b>Yellow</b> = prestress target, <b>solid blue</b> on top = force picked up to the final analysed force, <b>hatched blue</b> = force lost below the target (its lower edge is the final force). The dark cap marks the final force; the percentage is the change from the target."
    + (n.u && n.a + n.b ? " <b>" + unassignedText(n.u) + "</b> They are charted separately, never mixed into a side."
       : !(n.a + n.b) ? " <b>No side grouping is applied</b> — all cables are charted together until you assign them under Cable Grouping." : "");
  if (!pres.length || !D.cables.length){ el("pnav").innerHTML = ""; el("detail").innerHTML = '<p class="empty">No cable-force results for this loading candidate.</p>'; return; }
  el("pnav").innerHTML = "Jump to " + pres.map((p, i) => `<a href="#pc-${i}">${esc(p.id)}</a>`).join("");
  el("detail").innerHTML = pres.map((p, i) => `<div class="pblk" id="pc-${i}"><div class="ph" data-pr="chart" data-pr-keep><b>${esc(p.id)}</b><span>${esc(pretNote(p))}</span></div>`
    + bl.map(b => {
        const title = `<span class="ct">${esc(p.id)} — ${esc(b.label)}</span>`;
        const mark = `data-pr="chart" data-pr-type="cableDetail" data-pr-id="detail_${esc(p.id)}_${esc(b.key || b.label)}"`;
        if (!b.cables.length) return `<div class="card" ${mark}><div class="ch">${title}</div><p class="empty">No cables assigned to ${esc(b.label)}.</p></div>`;
        const c = detailChart(p, b, width, sc);
        return `<div class="card" ${mark}><div class="ch">${title}<span class="cs">${b.cables.length} cable${b.cables.length === 1 ? "" : "s"} × ${D.heights.length} height${D.heights.length === 1 ? "" : "s"}`
          + (c.missing ? ` · ${c.missing} result${c.missing === 1 ? "" : "s"} unavailable` : "")
          + (c.exceed ? ` · <b>${c.exceed} of ${c.n - c.missing} above limit</b>` : "") + `</span></div><div class="cwrap">${c.svg}</div></div>`;
      }).join("") + "</div>").join("");
}

// ---------- 03 · governing / capacity review ----------
const STATUS = { pass: ['ok', "✓ Within limit"], exceed: ['bad', "▲ Exceeds limit"], "no-capacity": ['na', "Capacity N/A"], "no-result": ['na', "Result unavailable"] };
function govLine(g){
  return esc(g.iterationKey) + " · load " + esc(g.loadId) + " · height " + mText(g.heightM) + " m (" + esc(g.heightId) + ") · pretension " + esc(g.pretId)
    + " · " + kn(g.finalForceKN) + (finite(g.utilisationPct) ? " = " + pc(g.utilisationPct) + " of " + kn(g.capacityKN) : " · capacity N/A");
}
function worst(list){
  const real = list.filter(g => finite(g.finalForceKN));
  const byUtil = real.filter(g => finite(g.utilisationPct));
  if (byUtil.length) return byUtil.reduce((a, b) => b.utilisationPct > a.utilisationPct ? b : a);
  return real.length ? real.reduce((a, b) => b.finalForceKN > a.finalForceKN ? b : a) : null;
}
function renderGov(){
  const list = D.governing.filter(g => g.scope === S.load);
  const over = list.filter(g => g.status === "exceed").length, checked = list.filter(g => g.status === "exceed" || g.status === "pass").length;
  el("govlead").innerHTML = "For each cable, the candidate with the largest final analysed force" + (D.loads.length > 1 ? " under <b>" + esc(loadLabel()) + "</b>" : "")
    + ", checked against that cable's own capacity. " + (checked ? "<b>" + over + " of " + checked + "</b> cable" + (checked === 1 ? "" : "s") + " with a capacity exceed" + (over === 1 ? "s" : "") + " the cable-force limit." : "No capacity is available, so no limit check is made.");
  const top = worst(list), gov = el("gov");
  if (!top){ gov.hidden = true; gov.innerHTML = ""; }
  else {
    const all = D.loads.length > 1 ? worst(D.governing.filter(g => g.scope === "*")) : null;
    gov.hidden = false;
    gov.innerHTML = `<span class="pill">Governing</span><span class="l1">${esc(top.cable)} · ${finite(top.utilisationPct) ? pc(top.utilisationPct) + " of capacity" : kn(top.finalForceKN)}</span>`
      + `<span class="l2">${govLine(top)}` + (all ? `<br>Study-wide, all loads: ${esc(all.cable)} · ${govLine(all)}` : "") + "</span>";
  }
  const maxU = Math.max.apply(null, [120].concat(list.map(g => finite(g.utilisationPct) ? g.utilisationPct * 1.05 : 0)));
  el("govtable").innerHTML = "<thead><tr><th>Cable</th><th>Current Group</th><th class=\"num\">Governing Final Force [kN]</th><th>Governing Candidate</th><th>Height</th><th>P Candidate</th>"
    + "<th class=\"num\">Current Capacity [kN]</th><th class=\"num\">Utilisation</th><th>0 → limit</th><th>Check</th></tr></thead><tbody>"
    + list.map(g => {
        const st = STATUS[g.status] || STATUS["no-result"], u = g.utilisationPct;
        const meter = finite(u) ? `<span class="trk"><i class="fil" style="width:${r1(Math.min(100, u / maxU * 100))}%;background:${u > 100 ? "#C62828" : "#2A78D6"}"></i><i class="tk" style="left:${r1(100 / maxU * 100)}%"></i></span>` : "";
        return `<tr><td><b>${esc(g.cable)}</b></td><td>${SIDE[S.groups[g.cable]]}</td>`
          + `<td class="num ${finite(g.finalForceKN) ? "" : "missing"}">${finite(g.finalForceKN) ? fnum(g.finalForceKN, 1) : "Result unavailable"}</td>`
          + `<td>${g.candidateId ? esc(g.candidateId) + " · " + esc(g.iterationKey) : "—"}</td><td>${finite(g.heightM) ? esc(hShort(g.heightM)) + " · " + mText(g.heightM) + " m" : "—"}</td><td>${g.pretId ? esc(g.pretId) : "—"}</td>`
          + `<td class="num">${finite(g.capacityKN) ? fnum(g.capacityKN, 1) : "N/A"}</td><td class="num">${finite(u) ? pc(u) : "N/A"}</td><td>${meter}</td><td><span class="st ${st[0]}">${st[1]}</span></td></tr>`;
      }).join("") + "</tbody>";
  const names = k => D.cables.filter(c => CAPSRC[c] === k), notes = [];
  if (CAPS_ANY) notes.push("<p>Capacity = section area × fpu × φ from the Tab 03 Pretension configuration current when this report was generated, matched by exact cable object name. Changing the grouping never changes a capacity, a utilisation or the governing candidate.</p>");
  if (names("invalid").length) notes.push(`<p class="w">Capacity unavailable from current Tab 3 configuration: ${esc(names("invalid").join(", "))} — check the section area, fpu and φ on the Pretension tab. No limit or utilisation is shown for ${names("invalid").length === 1 ? "this cable" : "these cables"}.</p>`);
  if (names("stored").length) notes.push(`<p>Not on the current Tab 3: ${esc(names("stored").join(", "))} — the capacity stored with the study results for the same cable name is used.</p>`);
  if (names("none").length) notes.push(`<p class="w">No capacity available for: ${esc(names("none").join(", "))} — no limit line and no utilisation; nothing is borrowed from another cable.</p>`);
  el("capnotes").innerHTML = notes.join("");
}

// ---------- source data + warnings ----------
function renderTable(){
  el("tb").innerHTML = D.rows.map(r => D.cables.filter(c => Object.prototype.hasOwnProperty.call(r.forces, c)).map(c => {
    const f = r.forces[c], t = r.targets[c], cap = capOf(c), d = r.deltas[c], dp = r.deltaPercents[c];
    return `<tr><td>${r.run}</td><td>${esc(r.candidateId)}</td><td>${esc(r.iterationKey)}</td><td>${esc(r.loadId)}</td>`
      + `<td>${esc(r.heightId)} · ${fnum(r.heightM, 2)} m</td><td>${esc(r.pretLabel || r.pretId)}</td><td>${esc(c)}</td><td>${SIDE[S.groups[c]]}</td>`
      + `<td class="num">${finite(t) ? fnum(t, 0) : "N/A"}</td>`
      + `<td class="num ${finite(f) ? "" : "missing"}">${finite(f) ? fnum(f, 1) : "Result unavailable"}</td>`
      + `<td class="num">${finite(d) ? signed(d, 1) : "N/A"}</td><td class="num">${finite(dp) ? signed(dp, 1) : "N/A"}</td>`
      + `<td class="num">${cap ? fnum(cap, 1) : "N/A"}</td><td class="num">${cap && finite(f) ? fnum(f / cap * 100, 1) : "N/A"}</td>`
      + `<td>${esc(r.statuses[c] || "")}</td></tr>`;
  }).join("")).join("");
}
function renderWarnings(){
  el("warns").innerHTML = D.warnings.length
    ? "<strong>Warnings:</strong> " + D.warnings.slice(0, 40).map(w => `<span class="${w.isError ? "e" : ""}">[${esc(w.iterationKey || "study")}] ${esc(w.message)}</span>`).join(" · ")
      + (D.warnings.length > 40 ? ` … (+${D.warnings.length - 40})` : "")
    : "";
}

// ---------- render ----------
// On paper the charts are laid out against the sheet instead of the window: at the sheet width when that is
// roomy, otherwise at a comfortable design width that the SVG scales down from — as vector, never below 80 %.
let printWidth = 0;
function chartWidth(){
  if (printWidth) return printWidth;
  const w = el("detail").clientWidth || (document.documentElement.clientWidth || 0) - 28;
  return Math.max(760, w > 0 ? w - 16 : 1180);
}
function renderCharts(){
  TIPS.length = 0;
  const width = chartWidth(), sc = scale(), bl = blocks();
  el("yinfo").textContent = !D.rows.length ? "No cable-force results"
    : "Shared Y-scale: 0 → " + fnum(sc.max, 0) + (utOn() ? " % of capacity" : " kN");
  el("leglim").textContent = utOn() ? "Design limit · 100% of capacity" : "Cable force limit · capacity";
  const n = counts(S.groups);
  el("pview").textContent = "Printed view: " + (utOn() ? "% of capacity" : "Force (kN)") + (S.load ? " · load " + loadLabel() : "")
    + " · grouping: Side A " + n.a + " · Side B " + n.b + (n.u ? " · Unassigned " + n.u : "");
  renderOverview(width, sc, bl);
  renderDetail(width, sc, bl);
}
function groupInfo(){
  const n = counts(S.groups);
  el("ginfo").textContent = "Side A " + n.a + " · Side B " + n.b + (n.u ? " · " + unassignedText(n.u) : "")
    + (S.saved ? (n.u ? " " : ". ") + "Grouping saved in this browser." : "");
}
function renderAll(){ groupInfo(); renderCharts(); renderGov(); renderTable(); }

// ---------- controls ----------
function renderEditor(){
  el("glist").innerHTML = D.cables.map((c, i) => `<label class="grow"><span title="${esc(c)}">${esc(c)}</span><select data-i="${i}">`
    + SIDE_KEYS.map(k => `<option value="${k}"${S.draft[c] === k ? " selected" : ""}>${SIDE[k]}</option>`).join("") + "</select></label>").join("");
}
const pending = () => D.cables.some(c => S.draft[c] !== S.groups[c]);
function applyGroups(g){
  S.groups = cleanGroups(g || S.draft); S.draft = Object.assign({}, S.groups);
  saveGroups(); renderEditor(); renderAll();
  el("gstate").textContent = "Grouping applied — charts re-rendered from the data embedded in this report.";
}
function autoDetect(){
  S.draft = defaultGroups(); renderEditor();
  const n = counts(S.draft);
  el("gstate").textContent = (n.a + n.b
      ? "Suggested from paired cable names: Side A " + n.a + " · Side B " + n.b + " · Unassigned " + n.u + "."
      : "No unambiguous side pattern in the cable names — every cable stays Unassigned.")
    + (pending() ? " Review, then press Apply Grouping." : " This is the grouping already shown.");
}
function resetGroups(){
  clearSaved(); S.groups = defaultGroups(); S.draft = Object.assign({}, S.groups);
  renderEditor(); renderAll();
  el("gstate").textContent = "Grouping reset to the report default.";
}
function setMode(mode){
  if (mode === "ut" && !CAPS_ANY) return;
  S.mode = mode === "ut" ? "ut" : "kn";
  el("vk").classList.toggle("active", S.mode === "kn"); el("vu").classList.toggle("active", S.mode === "ut");
  renderCharts(); renderGov();
}
function setLoad(id){ if (!D.loads.some(l => l.id === id)) return; S.load = id; el("load").value = id; renderAll(); }

el("load").innerHTML = D.loads.map(l => `<option value="${esc(l.id)}">${esc(l.label)}</option>`).join("");
el("loadwrap").hidden = D.loads.length < 2; el("loadsep").hidden = D.loads.length < 2;   // one load: nothing to choose
el("load").value = S.load;
el("load").onchange = e => setLoad(e.target.value);
el("vk").onclick = () => setMode("kn");
el("vu").onclick = () => setMode("ut");
if (!CAPS_ANY){ el("vu").disabled = true; el("vu").title = "No cable capacity is available — set the section area, fpu and φ on the Pretension tab, then Regenerate"; }
el("gbtn").onclick = () => { const p = el("gpanel"); p.hidden = !p.hidden; el("gbtn").setAttribute("aria-expanded", String(!p.hidden)); };
el("glist").addEventListener("change", e => {
  const i = e.target && e.target.getAttribute ? e.target.getAttribute("data-i") : null;
  if (i == null) return;
  S.draft[D.cables[+i]] = e.target.value === "A" || e.target.value === "B" ? e.target.value : "";
  el("gstate").textContent = pending() ? "Unapplied changes — press Apply Grouping." : "";
});
el("gapply").onclick = () => applyGroups();
el("gauto").onclick = autoDetect;
el("greset").onclick = resetGroups;

// one tooltip for every bar; the Source Data table carries the same values without hovering
const tipBox = el("tip");
let tipShown = -1;
document.addEventListener("mousemove", e => {
  const raw = e.target && e.target.getAttribute ? e.target.getAttribute("data-t") : null;
  if (raw == null || !TIPS[+raw]){ tipBox.hidden = true; tipShown = -1; return; }
  if (tipShown !== +raw){
    tipShown = +raw; tipBox.textContent = "";
    TIPS[tipShown].forEach(row => {
      const line = document.createElement("div"), k = document.createElement("span"), v = document.createElement("b");
      if (row[2]) line.className = row[2];
      k.textContent = row[0]; v.textContent = row[1]; line.appendChild(k); line.appendChild(v); tipBox.appendChild(line);
    });
    tipBox.hidden = false;
  }
  const box = tipBox.getBoundingClientRect(), vw = window.innerWidth || 1200, vh = window.innerHeight || 800;
  tipBox.style.left = Math.max(4, Math.min(e.clientX + 14, vw - box.width - 8)) + "px";
  tipBox.style.top = (e.clientY + 16 + box.height > vh ? Math.max(4, e.clientY - box.height - 12) : e.clientY + 16) + "px";
});
document.addEventListener("scroll", () => { tipBox.hidden = true; tipShown = -1; }, true);

let resizeTimer = 0;
window.addEventListener("resize", () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(renderCharts, 120); });

const savedGroups = loadSaved();
S.groups = savedGroups || defaultGroups(); S.saved = !!savedGroups; S.draft = Object.assign({}, S.groups);
el("legnt").hidden = !D.rows.some(r => D.cables.some(c => finite(r.forces[c]) && !finite(r.targets[c])));
renderEditor(); renderWarnings(); renderAll();
if (OPT.mode === "ut") setMode("ut");
if (PRINT) PRINT.register({
  before: pg => { printWidth = Math.max(pg.contentPx, Math.min(1210, Math.round(pg.contentPx / 0.8))) - 10; renderCharts(); },
  after: () => { printWidth = 0; renderCharts(); }
});
// handle for scripted checks; the report itself needs nothing from outside this file
window.cableReport = { state: S, setMode, setLoad, applyGroups, autoDetect, resetGroups, limitPlan, blocks, scale };
""";
}
