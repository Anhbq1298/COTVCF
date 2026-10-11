using COT_VCF.Application;
using COT_VCF.Application.Results;
using COT_VCF.Geometry;
using Jint;
using System.Text.RegularExpressions;

internal static class FemRunReportChecks
{
    public static void Run()
    {
        var result = new FemAnalysisResult([new("P<1", 0.001, -0.012, 0.002)], [new("Pin", 45, 100)],
            [new("Frame", 1, 2, 3, 4, 5, 6, [new(0.5, 7, 8, 999, 0, 0)])],
            [new("Cable", 120, true, 100, 90, 95)], 3, 2, "target < tolerance");
        var run = new FemStudyRun("Model", "", "", "", result, Results: new Dictionary<string, FemAnalysisResult>
            { ["SLS <test>"] = result, ["ULS"] = result });
        var html = FemRunReport.BuildHtml(run, "Model <script>", "ULS");
        if (!html.Contains("value=\"1\" selected") || !html.Contains("SLS &lt;test&gt;") ||
            !html.Contains("P&lt;1") || !html.Contains("<td>-12</td>") || !html.Contains("<td>999</td>") ||
            !html.Contains("Not fitted: target &lt; tolerance") || html.Contains("Model <script>"))
            throw new Exception("FEM HTML must preserve case identity, units, internal stations and fitting warnings, and encode model data.");
        var engine = new Engine();
        engine.Execute("""
            const cases={value:'1',selectedIndex:1,options:[{text:'SLS'},{text:'ULS'}]};
            const topics={value:'0',selectedIndex:0,options:[{text:'Deflection'},{text:'Forces'}]};
            const panels=[{dataset:{case:'0',topic:'0'}},{dataset:{case:'1',topic:'0'}},{dataset:{case:'1',topic:'1'}}];
            const view={};const document={getElementById:id=>id==='case'?cases:id==='topic'?topics:view,querySelectorAll:()=>panels};
            """);
        engine.Execute(Regex.Match(html, "<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value);
        if (!engine.Evaluate("panels[0].hidden && !panels[1].hidden && panels[2].hidden").AsBoolean())
            throw new Exception("HTML must show only the selected completed case and result topic.");
        engine.Execute("topics.value='1';topics.selectedIndex=1;topics.onchange();");
        if (!engine.Evaluate("panels[0].hidden && panels[1].hidden && !panels[2].hidden").AsBoolean())
            throw new Exception("Changing HTML result topic must switch the visible results.");
        Console.WriteLine("PASS: FEM HTML uses completed cases, converts units, preserves stations and warnings, and switches case/topic without external data.");
    }
}
