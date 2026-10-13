using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Domain.Results;
using System.IO;

namespace Mcotis.Desktop.Application.Results;

public interface IStudyArtifactPublisher
{
    StudyArtifactOutcome Publish(StudyExecutionSnapshot snapshot, IReadOnlyList<CandidateResultDto> results);
}

/// <summary>
/// Publishes the canonical raw JSON/CSV first, then automatically derives the user-facing Excel and HTML
/// reports from that raw package. Tab 06 can regenerate the same outputs later without rerunning SAP2000.
/// </summary>
public sealed class StudyArtifactPublisher : IStudyArtifactPublisher
{
    private readonly StudyPathService paths = new();
    private readonly Func<DeflectionLimitBand> deflectionBand;

    public StudyArtifactPublisher(Func<DeflectionLimitBand>? deflectionBand = null)
    {
        this.deflectionBand = deflectionBand ?? (() => new DeflectionLimitBand(null, null));
    }

    public StudyArtifactOutcome Publish(StudyExecutionSnapshot snapshot, IReadOnlyList<CandidateResultDto> results)
    {
        if (results.Count == 0) return StudyArtifactOutcome.NotProduced;
        var studyFolder = Path.GetFullPath(paths.GetStudyRoot(snapshot.BaselineModelPath));
        var raw = new StudyRawDataStore().Write(studyFolder, snapshot, results);
        try
        {
            return new StudyPostProcessor().Generate(studyFolder,
                snapshot.CableDesignCapacities, deflectionBand(),
                snapshot.ConfiguredCableNames);
        }
        catch (Exception ex)
        {
            return new(StudyArtifactState.Failed, null, null, null, null, null,
                $"Raw data is ready, but automatic report generation failed: {ex.Message}",
                RawJsonPath: raw.JsonPath, RawCsvPath: raw.CsvPath);
        }
    }
}
