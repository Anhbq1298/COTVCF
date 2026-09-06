using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>Resolves preview IDs into the complete, immutable assignments required by a worker.</summary>
public sealed class StudyCandidateResolver
{
    public IReadOnlyList<StudyIterationExecutionDto> Resolve(
        ApplicationState state,
        StudyCompositionResult composition)
    {
        var loadings = composition.LoadingCandidates.ToDictionary(
            candidate => candidate.Id,
            FreezeLoading,
            StringComparer.Ordinal);
        var heights = composition.HeightCandidates.ToDictionary(
            candidate => candidate.Id,
            candidate => candidate.Candidate with { },
            StringComparer.Ordinal);
        var pretensions = composition.PretensionCandidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var resolvedPretensions = pretensions.ToDictionary(
            pair => pair.Key,
            pair => new ResolvedPretensionCandidate(
                pair.Key,
                pair.Value.Assignments.Select(assignment => assignment with { }).ToArray()),
            StringComparer.Ordinal);

        var jobs = new List<StudyIterationExecutionDto>(composition.Iterations.Count);
        foreach (var row in composition.Iterations)
        {
            if (!loadings.TryGetValue(row.LoadingCandidateId, out var loading))
                throw new InvalidOperationException($"Loading candidate {row.LoadingCandidateId} cannot be resolved.");
            if (!heights.TryGetValue(row.HeightCandidateId, out var height))
                throw new InvalidOperationException($"Height candidate {row.HeightCandidateId} cannot be resolved.");
            if (!resolvedPretensions.TryGetValue(row.PretensionCandidateId, out var pretension))
                throw new InvalidOperationException($"Pretension candidate {row.PretensionCandidateId} cannot be resolved.");

            jobs.Add(new(
                row.RunIndex,
                row.IterationId,
                row.LoadingCandidateId,
                row.HeightCandidateId,
                row.PretensionCandidateId,
                loading,
                height,
                pretension,
                row.IterationDirectory,
                row.ModelName,
                row.ModelFullPath,
                row.Description,
                row.RunIterate));
        }
        return jobs;
    }

    private static LoadingStudyCandidate FreezeLoading(LoadingStudyCandidate candidate) => new()
    {
        Id = candidate.Id,
        Combination = new LoadingCombination(
            candidate.Combination.Index,
            candidate.Combination.Loads.Select(load => (load.Name, load.Qz)).ToArray()),
        PointLoads = candidate.PointLoads.Select(load => load with { }).ToArray(),
        Loads = candidate.Loads.Select(load => load with { }).ToArray()
    };
}
