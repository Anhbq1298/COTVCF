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
            snapshot.BaselineModel.Name, snapshot.BaselineModelPath, snapshot.StudyMode, snapshot.CableDesignCapacities);
        var ordered = consolidator.Snapshot();

        var deflectionGen = new DeflectionReportGenerator();
        var deflection = deflectionGen.Generate(studyFolder, ordered,
            snapshot.BaselineModel.Name, snapshot.BaselineModelPath, snapshot.StudyMode);

        string? cable = null;
        if (ordered.Any(r => (r.CableForces?.Count ?? 0) > 0))
        {
            cable = new CableForceReportGenerator().Generate(studyFolder, ordered,
                snapshot.BaselineModel.Name, snapshot.StudyMode, snapshot.CableResultLoadCase, snapshot.CableDesignCapacities);
            // Re-emit the deflection report now the cable report exists, so its Result Library links across.
            deflection = deflectionGen.Generate(studyFolder, ordered,
                snapshot.BaselineModel.Name, snapshot.BaselineModelPath, snapshot.StudyMode);
        }
        return new(StudyArtifactState.Ready, workbook, deflection, cable, null);
    }
}
