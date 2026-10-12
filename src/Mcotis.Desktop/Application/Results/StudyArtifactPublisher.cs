using COT_VCF.Domain.Results;
using System.IO;

namespace COT_VCF.Application.Results;

public interface IStudyArtifactPublisher
{
    StudyArtifactOutcome Publish(StudyExecutionSnapshot snapshot, IReadOnlyList<CandidateResultDto> results);
}

/// <summary>
/// Publishes raw, SAP-independent JSON/CSV only. Excel workbooks and HTML reports belong to the explicit
/// Tab 06 post-processing step and are never generated as a side effect of running models.
/// </summary>
public sealed class StudyArtifactPublisher : IStudyArtifactPublisher
{
    private readonly StudyPathService paths = new();

    public StudyArtifactOutcome Publish(StudyExecutionSnapshot snapshot, IReadOnlyList<CandidateResultDto> results)
    {
        if (results.Count == 0) return StudyArtifactOutcome.NotProduced;
        var studyFolder = Path.GetFullPath(paths.GetStudyRoot(snapshot.BaselineModelPath));
        var raw = new StudyRawDataStore().Write(studyFolder, snapshot, results);
        return new(StudyArtifactState.Ready, null, null, null, null, null, null,
            RawJsonPath: raw.JsonPath, RawCsvPath: raw.CsvPath);
    }
}
