using COT_VCF.Domain.Results;
using System.IO;

namespace COT_VCF.Application.Results;

public interface IStudyArtifactPublisher
{
    StudyArtifactOutcome Publish(StudyExecutionSnapshot snapshot, IReadOnlyList<CandidateResultDto> results);
}

/// <summary>Creates the authoritative source workbook first, then derives the self-contained HTML reports from it.</summary>
public sealed class StudyArtifactPublisher : IStudyArtifactPublisher
{
    private readonly StudyPathService paths = new();

    public StudyArtifactOutcome Publish(StudyExecutionSnapshot snapshot, IReadOnlyList<CandidateResultDto> results)
    {
        if (results.Count == 0) return StudyArtifactOutcome.NotProduced;
        var studyFolder = Path.GetFullPath(paths.GetStudyRoot(snapshot.BaselineModelPath));
        var consolidator = new ConsolidatedResultWriter();
        foreach (var result in results) consolidator.Add(result);
        var workbook = consolidator.Write(studyFolder, snapshot.DeflectionPoints, snapshot.DeflectionOutputCase,
            snapshot.BaselineModel.Name, snapshot.BaselineModelPath, snapshot.StudyMode,
            cableCapacities: snapshot.CableDesignCapacities,
            cableResultCase: snapshot.CableResultLoadCase,
            memberGroups: snapshot.OutputGroups.Where(group =>
                group.ObjectKind == COT_VCF.Domain.OutputGroupObjectKind.Frame).ToArray(),
            memberForceCase: snapshot.MemberForceResultCase,
            foundationLeftPoints: snapshot.FoundationLeftPoints,
            foundationRightPoints: snapshot.FoundationRightPoints,
            foundationReactionCase: snapshot.FoundationReactionResultCase);
        var ordered = consolidator.Snapshot();
        var hasCableReport = ordered.Any(r => (r.CableForces?.Count ?? 0) > 0);
        var hasMemberReport = ordered.Any(r => (r.MemberForceGroups?.Count ?? 0) > 0);
        var hasSupportReport = ordered.Any(r => (r.SupportReactionGroups?.Count ?? 0) > 0);
        var currentReports = new List<string> { "deflection" };
        if (hasMemberReport) currentReports.Add("member");
        if (hasSupportReport) currentReports.Add("support");
        if (hasCableReport) currentReports.Add("cable");
        currentReports.Add("shortlist");
        var decisions = new StudyDecisionService().Evaluate(ordered, snapshot.DecisionCriteria);

        var deflectionGen = new DeflectionReportGenerator();
        var deflection = deflectionGen.Generate(studyFolder, ordered,
            snapshot.BaselineModel.Name, snapshot.BaselineModelPath, snapshot.StudyMode, currentReports);
        string? member = null;
        if (hasMemberReport)
            member = new MemberForceReportGenerator().Generate(studyFolder, ordered,
                snapshot.BaselineModel.Name, snapshot.StudyMode, currentReports);
        string? support = null;
        if (hasSupportReport)
            support = new SupportReactionReportGenerator().Generate(studyFolder, ordered,
                snapshot.BaselineModel.Name, snapshot.StudyMode, currentReports);
        string? cable = null;
        if (hasCableReport)
        {
            cable = new CableForceReportGenerator().Generate(studyFolder, ordered,
                snapshot.BaselineModel.Name, snapshot.StudyMode, snapshot.CableResultLoadCase,
                snapshot.CableDesignCapacities, currentReports);
        }
        var shortlist = new StudyShortlistExporter().Export(studyFolder, decisions,
            snapshot.DecisionCriteria, siblingReports: currentReports);
        return new(StudyArtifactState.Ready, workbook, deflection, member, support, cable, null,
            shortlist.WorkbookPath, shortlist.HtmlPath);
    }
}
