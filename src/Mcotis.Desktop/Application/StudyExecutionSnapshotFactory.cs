using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Application.Results;
using System.IO;

namespace Mcotis.Desktop.Application;

public sealed class StudyExecutionSnapshotFactory(StudyCandidateResolver? resolver = null)
{
    private readonly StudyCandidateResolver resolver = resolver ?? new();

    public StudyExecutionSnapshot Create(
        ApplicationState state,
        StudyCompositionResult composition,
        int requestedWorkerCount)
    {
        if (requestedWorkerCount is < 1 or > 3)
            throw new InvalidOperationException("Total SAP2000 instances must be between 1 and 3.");
        var baseline = state.SessionBaselineModel
            ?? throw new InvalidOperationException("The immutable session baseline is unavailable. Reconnect to SAP2000.");
        if (string.IsNullOrWhiteSpace(baseline.Path))
            throw new InvalidOperationException("Save the baseline SAP2000 model before building a study.");
        if (!state.BaselineCaptured)
            throw new InvalidOperationException("Capture the truss-height baseline before building a study.");

        var geometry = CloneGeometry(state.Baseline.Points.Count > 0 ? state.Baseline : state.SessionBaseline);
        return new(
            Path.GetFullPath(baseline.Path),
            baseline with { Path = Path.GetFullPath(baseline.Path) },
            geometry,
            state.Truss.TopPoints.ToArray(),
            state.Truss.MiddlePoints.ToArray(),
            state.Truss.BottomPoints.ToArray(),
            state.Study.Mode,
            resolver.Resolve(state, composition),
            requestedWorkerCount)
        {
            OutputGroups = state.OutputSettings.ToSapGroups()
                .Select(group => new OutputSapGroupDefinition(group.Name, group.ObjectKind, group.ObjectNames.ToArray()))
                .ToArray(),
            CableResultLoadCase = state.OutputSettings.CableResultLoadCase,
            CableResultCables = state.OutputSettings.CableNames.ToArray(),
            CableDesignCapacities = state.Cables
                .Where(c => !string.IsNullOrWhiteSpace(c.CableName) && c.DesignCapacityKN is { } cap && double.IsFinite(cap) && cap > 0)
                .GroupBy(c => c.CableName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().DesignCapacityKN!.Value, StringComparer.Ordinal),
            ConfiguredCableNames = state.Cables
                .Select(c => c.CableName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            DeflectionPoints = state.OutputSettings.DeflectionPoints.ToArray(),
            DeflectionOutputCase = state.OutputSettings.DeflectionOutputCaseName,
            MemberForceResultCase = state.OutputSettings.MemberForceResultLoadCase,
            FoundationLeftPoints = state.OutputSettings.FoundationLeftPoints.ToArray(),
            FoundationRightPoints = state.OutputSettings.FoundationRightPoints.ToArray(),
            FoundationReactionResultCase = state.OutputSettings.FoundationReactionResultLoadCase,
        };
    }

    private static GeometryModel CloneGeometry(GeometryModel geometry) => new(
        geometry.Points.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.Ordinal),
        geometry.Members.Select(member => member with { }).ToArray());
}
