using System.Net;
using System.Text.Json;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application.Results;

/// <summary>Single-model counterpart of the MB reports: embedded results and browser-side chart rendering.</summary>
internal static class FemLiveReport
{
    public static string Build(FemStudyRun run, string model, string? selectedCase, ReportPrintSettings? print,
        GeometryModel? geometry)
    {
        if (run.Results is not { Count: > 0 } results)
            throw new InvalidOperationException("No completed load-case results are available.");

        var cases = results.Select(pair =>
        {
            var r = pair.Value;
            return new
            {
                name = pair.Key,
                joints = r.Joints.Select((j, index) => new
                {
                    name = j.PointName,
                    x = geometry?.Points.GetValueOrDefault(j.PointName)?.Position.X ?? index,
                    z = geometry?.Points.GetValueOrDefault(j.PointName)?.Position.Z ?? 0,
                    ux = j.UXM * 1000,
                    uz = j.UZM * 1000,
                    rotation = j.RotationRad,
                }).ToArray(),
                frames = r.Frames.Select(f => new
                {
                    name = f.MemberName,
                    stations = (f.Stations is { Count: > 0 } stations ? stations :
                        [new FemFrameStation(0, f.AxialStartKN, f.ShearStartKN, f.MomentStartKNm, 0, 0),
                         new FemFrameStation(1, f.AxialEndKN, f.ShearEndKN, f.MomentEndKNm, 0, 0)])
                        .OrderBy(s => s.Fraction)
                        .Select(s => new { x = s.Fraction * 100, n = s.AxialKN, v = s.ShearKN, m = s.MomentKNm })
                        .ToArray(),
                }).ToArray(),
                reactions = r.SupportReactions.Select(s => new { name = s.PointName, rx = s.ReactionXKN, rz = s.ReactionZKN, my = s.MomentYKNm }).ToArray(),
                cables = r.Cables.Select(c => new
                {
                    name = c.MemberName,
                    target = c.TargetForceKN > 0 ? (double?)c.TargetForceKN : null,
                    stage = c.PretensionStageForceKN,
                    result = c.AxialForceKN,
                    active = c.Active,
                    sagMm = c.SagM * 1000,
                    stiffnessPercent = c.TangentStiffnessRatio * 100,
                }).ToArray(),
                fitFailure = r.TargetFitFailure,
            };
        }).ToArray();
        var selectedIndex = Array.FindIndex(cases, item => item.name == selectedCase);
        var data = JsonSerializer.Serialize(new { cases, selectedIndex = Math.Max(0, selectedIndex), hasGeometry = geometry is not null });
        var printScripts = ReportPrintEngine.Scripts(ReportPrintEngine.Describe(print, "FEM Results", model, DateTimeOffset.Now));
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>FEM Results — {{WebUtility.HtmlEncode(model)}}</title>
<style>{{ReportPrintEngine.Css}}{{FemLiveReportAssets.Style}}</style>{{ReportPrintEngine.PageStyle}}</head><body>
<header data-pr="info"><h1>FEM Results Viewer</h1><div>{{WebUtility.HtmlEncode(model)}} · {{cases.Length}} completed load case(s)</div>
<p data-pr="note">Choose a case and a result family. Charts, source rows, tooltips and the printed view follow the selection.</p>
<p class="print-only" data-print-view id="print-view"></p></header>
<nav data-print-controls>
 <label>Load case / combination <select id="case" aria-label="FEM load case"></select></label>
 <label>Result <select id="topic" aria-label="FEM result family"><option value="deflection">Deflection</option><option value="frame">Frame forces</option><option value="reaction">Support reactions</option><option value="cable">Cable forces</option></select></label>
 <label id="frame-wrap" hidden>Frame <select id="frame" aria-label="Frame member"></select></label>
 <label id="component-wrap" hidden>Component <select id="component" aria-label="Frame force component"><option value="n">Axial N</option><option value="v">Shear V</option><option value="m">Moment M</option></select></label>
 <button id="zoom-in" type="button" title="Zoom into the line chart">Zoom +</button>
 <button id="zoom-out" type="button" title="Zoom out of the line chart">Zoom −</button>
 <button id="reset" type="button" title="Reset chart zoom and pan">Reset view</button>
 <button id="source-toggle" type="button" aria-expanded="false">Source data</button>
 {{ReportPrintEngine.ToolbarButton}}
 <span class="hint">Hover for values · wheel to zoom · drag to pan · double-click to reset</span>
</nav>
<div id="scope" data-pr="note"></div><main id="panels"></main>
<section id="source" data-pr="source" data-print="source" hidden><h2>Source data</h2><div id="source-table"></div></section>
<div id="tip" hidden></div>
<script type="application/json" id="fem-data">{{data}}</script>
{{printScripts}}
<script>{{FemLiveReportAssets.Script}}</script></body></html>
""";
    }
}
