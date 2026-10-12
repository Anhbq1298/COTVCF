using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Results;
using Mcotis.Desktop.Geometry;

internal static class FemRunReportChecks
{
    public static void Run()
    {
        var saved = SapFemLoading.Empty with { JointLoads = [new("DL", new("P1", 0, -1)), new("DL", new("P2", 0, -1))] };
        var carried = FemLoadPlan.Describe(saved, "DL", 0, ["DL"], [], ["P1", "P2", "P3"]);
        if (carried.Loads != "VCF storey loads: 3 joints")
            throw new Exception("Replaced SAP assignments must not be counted as extra VCF joint loads.");
        var changed = FemLoadPlan.Describe(saved, "DL", 0, ["DL"], [], ["P1"]);
        if (!changed.Loads.Contains("VCF storey loads: 1 joints") || !changed.Loads.Contains("1 SAP joint assignments"))
            throw new Exception("Load descriptions must track changed VCF points and retained SAP assignments.");

        var result = new FemAnalysisResult([new("P<1", 0.001, -0.012, 0.002)], [new("Pin", 45, 100)],
            [new("Frame", 1, 2, 3, 4, 5, 6, [new(0.5, 7, 8, 999, 0, 0)])],
            [new("Cable", 120, true, 100, 90, 95)], 3, 2, "target < tolerance");
        var run = new FemStudyRun("Model", "", "", "", result, Results: new Dictionary<string, FemAnalysisResult>
            { ["SLS <test>"] = result, ["ULS"] = result });
        var html = FemRunReport.BuildHtml(run, "Model <script>", "ULS");
        if (!html.Contains("id=\"fem-data\"") || !html.Contains("id=\"panels\"") ||
            !html.Contains("id=\"pr-btn\"") || html.Contains("Model <script>"))
            throw new Exception("FEM results must be a self-contained live report with the shared print engine and safe model text.");

        var page = new ReportPage(html);
        if (!page.Html("panels").Contains("Vertical deflection by level") ||
            !page.Html("panels").Contains("P&lt;1") || !page.Text("scope").StartsWith("ULS · Deflection"))
            throw new Exception("The selected load case must render its deflection chart when the HTML opens.");
        page.Run("window.femReport.selectTopic('frame')");
        page.Run("document.getElementById('component').value='m'; document.getElementById('component').onchange()");
        if (!page.Html("panels").Contains("Bending moment · Frame") ||
            !page.Html("panels").Contains("999") || !page.Html("source-table").Contains("999"))
            throw new Exception("The live frame plot must show the selected component and interior mesh-station extreme.");
        page.Run("document.getElementById('zoom-in').onclick()");
        if (page.Eval("window.femReport.state().zoom") != "1.5")
            throw new Exception("Zoom controls must redraw the live frame plot.");
        page.Run("document.getElementById('reset').onclick()");
        if (page.Eval("window.femReport.state().zoom") != "1")
            throw new Exception("Reset must restore the plotted station range.");
        page.Run("window.femReport.selectTopic('reaction')");
        if (!page.Html("panels").Contains("Support reactions") || !page.Html("source-table").Contains("Pin"))
            throw new Exception("The reaction chart and rows must update on the same page.");
        page.Run("window.femReport.selectTopic('cable')");
        if (!page.Html("panels").Contains("Cable forces / target fit") ||
            !page.Html("panels").Contains("target &lt; tolerance"))
            throw new Exception("The cable chart must retain target-fit diagnostics.");
        page.Run("window.femReport.selectCase(0)");
        if (!page.Text("scope").StartsWith("SLS <test> · Cable forces"))
            throw new Exception("Changing the case must update the live report and print-view label.");
        page.Run("document.getElementById('source-toggle').onclick()");
        if (page.Hidden("source") || !page.Html("source-table").Contains("Cable"))
            throw new Exception("Source data must open for the currently selected case and result family.");

        var oneCaseHtml = FemRunReport.BuildHtml(run with
        {
            Results = new Dictionary<string, FemAnalysisResult> { ["SLS <test>"] = result }
        }, "Single model", "SLS <test>");
        var single = new ReportPage(oneCaseHtml);
        foreach (var topic in new[] { "deflection", "frame", "reaction", "cable" })
        {
            single.Run($"window.femReport.selectTopic('{topic}')");
            if (!single.Html("panels").Contains("<svg class=\"chart\""))
                throw new Exception($"One completed FEM case must render a live {topic} chart.");
        }
        Console.WriteLine("PASS: FEM HTML renders live case/force selectors, plots, source data, zoom and print view; one case is sufficient.");
    }
}
