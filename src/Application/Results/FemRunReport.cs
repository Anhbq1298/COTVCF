using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Application.Results;

/// <summary>A live HTML result viewer for the current model's completed FEM load cases.</summary>
public static class FemRunReport
{
    public static string BuildHtml(FemStudyRun run, string model, string? selectedCase = null, ReportPrintSettings? print = null,
        GeometryModel? geometry = null) => FemLiveReport.Build(run, model, selectedCase, print, geometry);
}
