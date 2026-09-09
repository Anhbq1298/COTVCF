using COT_VCF.Domain;
using System.IO;

namespace COT_VCF.Application;

public sealed class OutputSettingsValidator
{
    public IReadOnlyList<ValidationIssue> Validate(
        ApplicationState state,
        IReadOnlyCollection<string>? loadCases = null)
    {
        var issues = new List<ValidationIssue>();
        var output = state.OutputSettings;
        var geometry = state.SessionBaseline.Points.Count > 0 ? state.SessionBaseline : state.Geometry;
        var frames = geometry.Members.Where(m => m.Kind == MemberKind.Frame).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var cables = geometry.Members.Where(m => m.Kind == MemberKind.Cable).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        if (output.DeflectionPoints.Count == 0)
            issues.Add(new("Output Settings", "Select at least one deflection monitoring point."));
        foreach (var name in output.DeflectionPoints.Distinct(StringComparer.Ordinal))
            if (!geometry.Points.ContainsKey(name)) issues.Add(new("Output Settings", $"Deflection point {name} is missing from the baseline model."));
        if (output.DeflectionPoints.Count != output.DeflectionPoints.Distinct(StringComparer.Ordinal).Count())
            issues.Add(new("Output Settings", "Deflection monitoring points contain duplicates."));
        if (string.IsNullOrWhiteSpace(output.DeflectionOutputCaseName))
            issues.Add(new("Output Settings", "Select a deflection output case (U3 · Global Z).",
                output.DeflectionPoints.Count > 0 ? ValidationSeverity.Warning : ValidationSeverity.Error));
        else if (loadCases is not null && !loadCases.Contains(output.DeflectionOutputCaseName, StringComparer.OrdinalIgnoreCase))
            issues.Add(new("Output Settings", $"Deflection output case {output.DeflectionOutputCaseName} does not exist in the baseline model."));

        if (output.MemberForceGroups.Count == 0)
            issues.Add(new("Output Settings", "Add at least one member-force output group."));
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignedFrames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in output.MemberForceGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                issues.Add(new("Output Settings", "Every member-force group needs a name."));
                continue;
            }
            if (group.Name.StartsWith(OutputGroupNaming.Prefix, StringComparison.OrdinalIgnoreCase))
                issues.Add(new("Output Settings", $"{group.Name}: enter only the group suffix; [O]- is added automatically."));
            if (OutputGroupNaming.ReservedNames.Contains(group.SapGroupName))
                issues.Add(new("Output Settings", $"{group.SapGroupName} is reserved for a fixed output group."));
            if (!groupNames.Add(group.SapGroupName))
                issues.Add(new("Output Settings", $"Duplicate member-force group name {group.SapGroupName}."));
            if (group.Members.Count == 0)
                issues.Add(new("Output Settings", $"{group.SapGroupName} contains no frames."));
            if (group.Members.Count != group.Members.Distinct(StringComparer.Ordinal).Count())
                issues.Add(new("Output Settings", $"{group.SapGroupName} contains duplicate frames."));
            foreach (var member in group.Members)
            {
                if (!frames.Contains(member))
                    issues.Add(new("Output Settings", $"{group.SapGroupName}: frame {member} is missing from the baseline model."));
                if (assignedFrames.TryGetValue(member, out var prior) && !string.Equals(prior, group.SapGroupName, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new("Output Settings", $"Frame {member} is assigned to both {prior} and {group.SapGroupName}."));
                else assignedFrames[member] = group.SapGroupName;
            }
        }
        if (output.MemberForceGroups.Count > 0 && string.IsNullOrWhiteSpace(output.MemberForceResultLoadCase))
            issues.Add(new("Output Settings", "Select a member-force result load case."));
        else if (!string.IsNullOrWhiteSpace(output.MemberForceResultLoadCase) && loadCases is not null &&
                 !loadCases.Contains(output.MemberForceResultLoadCase, StringComparer.OrdinalIgnoreCase))
            issues.Add(new("Output Settings", $"Member-force result load case {output.MemberForceResultLoadCase} does not exist in the baseline model."));

        if (output.FoundationLeftPoints.Count != 2)
            issues.Add(new("Output Settings", "Foundation Left requires exactly 2 points."));
        if (output.FoundationRightPoints.Count != 2)
            issues.Add(new("Output Settings", "Foundation Right requires exactly 2 points."));
        foreach (var name in output.FoundationLeftPoints.Concat(output.FoundationRightPoints).Distinct(StringComparer.Ordinal))
            if (!geometry.Points.ContainsKey(name)) issues.Add(new("Output Settings", $"Foundation point {name} is missing from the baseline model."));
        if (output.FoundationLeftPoints.Intersect(output.FoundationRightPoints, StringComparer.Ordinal).Any())
            issues.Add(new("Output Settings", "A foundation point cannot belong to both Left and Right groups."));

        if (output.CableNames.Count == 0)
            issues.Add(new("Output Settings", "Select at least one cable to monitor."));
        foreach (var name in output.CableNames.Distinct(StringComparer.Ordinal))
            if (!cables.Contains(name)) issues.Add(new("Output Settings", $"Cable {name} is missing from the baseline model."));
        if (string.IsNullOrWhiteSpace(output.CableResultLoadCase))
            issues.Add(new("Output Settings", "Select a cable result load case."));
        else if (loadCases is not null && !loadCases.Contains(output.CableResultLoadCase, StringComparer.OrdinalIgnoreCase))
            issues.Add(new("Output Settings", $"Cable result load case {output.CableResultLoadCase} does not exist in the baseline model."));

        return issues.Distinct().ToArray();
    }
}

public sealed class OutputGroupPreparationService(ISapGateway sap)
{
    public async Task PrepareParentAsync(
        ApplicationState state,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var baseline = state.SessionBaselineModel
            ?? throw new InvalidOperationException("The immutable session baseline is unavailable. Reconnect to SAP2000.");
        if (string.IsNullOrWhiteSpace(baseline.Path))
            throw new InvalidOperationException("Save the baseline SAP2000 model before starting the study.");

        var active = await sap.GetCurrentModelInfoAsync();
        if (!SamePath(active.Path, baseline.Path))
            throw new InvalidOperationException(
                $"Output groups can only be prepared on the parent baseline model.\nExpected: {baseline.Path}\nActive: {active.Path}");
        if (active.UnitsCode != baseline.UnitsCode)
            throw new InvalidOperationException($"SAP units changed from {baseline.Units} to {active.Units}. Restore the baseline units before running.");
        if (active.IsLocked)
            throw new InvalidOperationException("The baseline SAP2000 model is locked. Unlock it in SAP2000 before running the study.");

        var loadCases = await sap.GetLoadCaseNamesAsync(active);
        var issues = new OutputSettingsValidator().Validate(state, loadCases);
        if (issues.Any(i => i.Severity == ValidationSeverity.Error))
            throw new InvalidOperationException(
                "Output Settings are not ready:\n" + string.Join("\n", issues.Select(i => "• " + i.Message)));

        var desired = state.OutputSettings.ToSapGroups();
        if (desired.Count == 0)
            throw new InvalidOperationException("No output groups are configured in Tab 04.");

        cancellationToken.ThrowIfCancellationRequested();
        await sap.ReplaceOutputGroupsAsync(desired, active);

        cancellationToken.ThrowIfCancellationRequested();
        var readBack = await sap.ReadOutputGroupsAsync(desired.Select(g => g.Name).ToArray(), active);
        foreach (var expected in desired)
        {
            var actual = readBack.FirstOrDefault(g => string.Equals(g.Name, expected.Name, StringComparison.OrdinalIgnoreCase));
            if (actual is null || !actual.Exists)
                throw new InvalidOperationException($"Output group {expected.Name} was not created in the parent model.");
            var expectedNames = expected.ObjectNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var actualNames = actual.Names(expected.ObjectKind).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (!expectedNames.SequenceEqual(actualNames, StringComparer.Ordinal))
                throw new InvalidOperationException($"Output group {expected.Name} membership verification failed.");
            var foreignCount = actual.PointNames.Count + actual.FrameNames.Count + actual.CableNames.Count - actualNames.Length;
            if (foreignCount != 0)
                throw new InvalidOperationException($"Output group {expected.Name} contains an unexpected SAP object type.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await sap.SaveAsync(active);
        var saved = await sap.GetCurrentModelInfoAsync();
        if (!SamePath(saved.Path, baseline.Path))
            throw new InvalidOperationException("The active SAP model changed while saving prepared output groups.");
        state.Model = saved;
        state.SessionBaselineModel = saved;
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
