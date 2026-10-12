using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application.Results;

/// <summary>A live HTML result viewer for the current model's completed FEM load cases.</summary>
public static class FemRunReport
{
    public static string BuildHtml(FemStudyRun run, string model, string? selectedCase = null, ReportPrintSettings? print = null,
        GeometryModel? geometry = null) => FemLiveReport.Build(run, model, selectedCase, print, geometry);
}
