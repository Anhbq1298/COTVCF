using System.IO;
using COT_VCF.Domain;

namespace COT_VCF.Application;

public sealed record StudyPreflightMetric(string Label, string Value, string Detail = "");

public sealed record StudyPreflightReport(
    IReadOnlyList<StudyPreflightMetric> Metrics,
    IReadOnlyList<ValidationIssue> Issues,
    bool Ready,
    string Summary)
{
    public static StudyPreflightReport Empty { get; } = new([], [], false, "Run Validate Study to execute preflight.");
}

public sealed class StudyPreflightService
{
    public StudyPreflightReport Inspect(
        ApplicationState state,
        StudyCompositionResult composition,
        IReadOnlyList<ValidationIssue> issues,
        int requestedWorkers)
    {
        var selected = composition.Iterations.Count(item => item.RunIterate);
        var existing = composition.Iterations.Count(item => item.RunIterate && File.Exists(item.ModelFullPath));
        var baselinePath = state.SessionBaselineModel?.Path ?? state.Model?.Path ?? "";
        var baselineHash = TryFingerprint(baselinePath);
        var outputCoverage = OutputCoverage(state);
        var blocking = issues.Count(issue => issue.Severity == ValidationSeverity.Error);
        var metrics = new[]
        {
            new StudyPreflightMetric("L / H / P", $"{composition.LoadingCandidates.Count:N0} / {composition.HeightCandidates.Count:N0} / {composition.PretensionCandidates.Count:N0}", "Axis candidate counts before study-mode and row filters."),
            new StudyPreflightMetric("Rows", $"{composition.UnfilteredRunCount:N0} → {composition.ExpectedRunCount:N0}", $"{composition.FilterAudit.ExcludedCount:N0} excluded by candidate filters."),
            new StudyPreflightMetric("Selected", $"{selected:N0} / {composition.Iterations.Count:N0}", $"{composition.Iterations.Count - selected:N0} manually excluded."),
            new StudyPreflightMetric("SAP instances", requestedWorkers.ToString(), requestedWorkers == 1 ? "Connected instance only." : $"Connected instance + {requestedWorkers - 1} background."),
            new StudyPreflightMetric("Baseline SHA-256", baselineHash, baselinePath),
            new StudyPreflightMetric("Units", state.Model?.Units ?? "Unavailable", "Study writes use m / kN and report displacement in mm."),
            new StudyPreflightMetric("Output coverage", outputCoverage, "Deflection, member, foundation, and cable selections."),
            new StudyPreflightMetric("Existing children", existing.ToString("N0"), "Existing children are opened, verified, and re-extracted; never overwritten."),
            new StudyPreflightMetric("Blocking issues", blocking.ToString("N0"), blocking == 0 ? "Preflight passed." : "Resolve errors before any worker starts.")
        };
        var ready = blocking == 0 && selected > 0 && !composition.PreviewLimitExceeded;
        return new(metrics, issues, ready, ready
            ? $"Preflight ready · {selected:N0} selected · {existing:N0} existing · {requestedWorkers} SAP instance{(requestedWorkers == 1 ? "" : "s")}."
            : $"Preflight blocked · {blocking:N0} error{(blocking == 1 ? "" : "s")} · no child process will start.");
    }

    private static string TryFingerprint(string path)
    {
        try
        {
            var hash = Fingerprints.FileSha256(path);
            return hash[..Math.Min(12, hash.Length)];
        }
        catch { return "Unavailable"; }
    }

    private static string OutputCoverage(ApplicationState state)
    {
        var configured = 0;
        if (state.OutputSettings.DeflectionPoints.Count > 0 && !string.IsNullOrWhiteSpace(state.OutputSettings.DeflectionOutputCaseName)) configured++;
        if (state.OutputSettings.MemberForceGroups.Count > 0) configured++;
        if (state.OutputSettings.FoundationLeftPoints.Count == 2 && state.OutputSettings.FoundationRightPoints.Count == 2 &&
            !string.IsNullOrWhiteSpace(state.OutputSettings.FoundationReactionResultLoadCase)) configured++;
        if (state.OutputSettings.CableNames.Count > 0 && !string.IsNullOrWhiteSpace(state.OutputSettings.CableResultLoadCase)) configured++;
        return $"{configured} / 4 modules";
    }
}
