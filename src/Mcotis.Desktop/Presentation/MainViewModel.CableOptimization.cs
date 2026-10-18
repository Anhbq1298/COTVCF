using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using System.IO;

namespace Mcotis.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private bool isCableSectionOptimizationRunning;
    private CableSectionOptimizationRun? lastCableSectionOptimization;

    public bool IsCableSectionOptimizationRunning
    {
        get => isCableSectionOptimizationRunning;
        private set
        {
            if (!Set(ref isCableSectionOptimizationRunning, value)) return;
            Notify(nameof(CanOptimizeCableSections));
        }
    }

    public bool CanOptimizeCableSections => !IsBusy && !IsCableSectionOptimizationRunning && HasUsableModelGeometry && NativeDocumentProblem is null &&
        NativeDocumentSession.Document.Cables.Count > 0;

    /// <summary>Applying a reviewed section result may unlock the FEM model as part of the edit.</summary>
    internal bool CanApplyCableSectionOptimization => !IsBusy && !IsCableSectionOptimizationRunning && HasUsableModelGeometry && NativeDocumentProblem is null;

    private string? CableSectionOptimizationStoragePath
    {
        get
        {
            var path = NativeDocumentSession.FilePath;
            if (path is null || !Path.IsPathRooted(path) || !McotisModelFile.IsModelFile(path)) path = offlineModelPath;
            return path is not null && Path.IsPathRooted(path) && McotisModelFile.IsModelFile(path) ? path : null;
        }
    }

    internal CableSectionOptimizationRun? LoadCableSectionOptimization()
    {
        if (lastCableSectionOptimization is { } memory && memory.DocumentId == NativeDocumentSession.Document.DocumentId.Value)
            return memory;
        if (CableSectionOptimizationStoragePath is not { } path) return null;
        return McotisModelFile.ReadBeside<CableSectionOptimizationRun>(path, CableSectionOptimizationRun.FileName);
    }

    internal async Task<CableSectionOptimizationRun> RunCableSectionOptimizationAsync(
        CableSectionOptimizationSettings settings, IProgress<CableSectionOptimizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsBusy || IsCableSectionOptimizationRunning)
            throw new InvalidOperationException("Another operation is running. Try again when it has finished.");
        FlushFemWorkingSession();
        if (femWorkingDirty) throw new InvalidOperationException("The latest model edits could not be saved into the working session. Resolve the save issue before iterating.");

        var document = NativeDocumentSession.Document;
        var snapshot = NativeToLegacyProjection.Read(document) ?? femInputSnapshot ??
            throw new InvalidOperationException("The main window has no saved configuration for this native model.");
        settings.Validate();
        var analyse = settings.DesignCases is { Count: > 0 } ? CandidateAnalysis() : null;
        IsCableSectionOptimizationRunning = true;
        try
        {
            var run = await Task.Run(() => CableSectionOptimizer.Run(document, snapshot, settings, progress, cancellationToken, analyse),
                CancellationToken.None).ConfigureAwait(true);
            lastCableSectionOptimization = run;
            SaveCableSectionOptimization(run);
            return run;
        }
        finally
        {
            IsCableSectionOptimizationRunning = false;
        }
    }

    /// <summary>How an iteration analyses a candidate document: with the load plan Run FEM Analysis uses, so its load cases and
    /// combinations are the ones the cables are designed for. Read on the UI thread; the analyses run on the worker.</summary>
    private Func<Mcotis.Core.ModelDocument, CancellationToken, FemStudyRun> CandidateAnalysis()
    {
        var candidateLoads = State.LoadDefinitions.Count == 0 ? [] : femStudy.ComposeModel(State, IsOfflineModel).Loads;
        var properties = HeldFemProperties();
        var baseline = State.Baseline;
        var loadPlan = FemLoadPlan.Build(FemPatternRows.ToArray(), FemCaseRows.ToArray(), FemCombinationRows.ToArray(), femSapLoading,
            properties, baseline, Tab01Patterns, PretensionPatterns, State.AppliedPoints.Select(point => point.PointName).ToArray(), FemMassSourceReferences);
        return (candidate, token) => femStudy.AnalyzeDocument(candidate, loadPlan, "cable-iteration", baseline, candidateLoads, null, token, modal: false);
    }

    /// <summary>Apply, then run FEM Analysis of the model with the new sections so its results — and every UR read from them — are
    /// those of the sections now assigned. True when the sections were applied and the analysis produced results.</summary>
    internal async Task<bool> ApplyCableSectionOptimizationAndAnalyzeAsync(CableSectionOptimizationRun run, bool confirmedUnconverged)
    {
        if (!ApplyCableSectionOptimization(run, confirmedUnconverged)) return false;
        await AnalyzeFemAsync();
        return HasFemResults;
    }

    private void SaveCableSectionOptimization(CableSectionOptimizationRun run)
    {
        if (CableSectionOptimizationStoragePath is not { } path) return;
        try { McotisModelFile.WriteBeside(path, CableSectionOptimizationRun.FileName, run); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Message = $"Cable section optimization finished, but its iteration history could not be saved beside the model: {ex.Message}";
        }
    }

    internal bool CableSectionOptimizationIsCurrent(CableSectionOptimizationRun run)
    {
        FlushFemWorkingSession();
        var document = NativeDocumentSession.Document;
        if (document.DocumentId.Value != run.DocumentId || document.AnalysisInputHash != run.AnalysisInputHash || femInputSnapshot is null)
            return false;
        return FemInputDatabase.RevisionHash(CaptureFemInputSnapshot(advanceRevision: false)) == run.RevisionHash;
    }

    /// <summary>Apply a reviewed result as one native document history step. A locked FEM model is unlocked first so its
    /// current output data is invalidated and the new sections become editable. Cable target-force rows, patterns,
    /// factors and sequence remain byte-for-byte the same in the configuration snapshot.</summary>
    internal bool ApplyCableSectionOptimization(CableSectionOptimizationRun run, bool confirmedUnconverged)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!CanApplyCableSectionOptimization)
            throw new InvalidOperationException("The model cannot be changed while another operation is running or when it has no usable geometry.");
        if (run.Failure is not null || run.Cancelled)
            throw new InvalidOperationException("A failed or cancelled optimization has no applicable result.");
        if (!run.Converged && !confirmedUnconverged)
            throw new InvalidOperationException("Confirm explicitly before applying an unconverged optimization.");
        if (!CableSectionOptimizationIsCurrent(run))
            throw new InvalidOperationException("The model changed after this optimization. Iterate again on the current model before applying it.");

        var document = NativeDocumentSession.Document;
        var snapshot = NativeToLegacyProjection.Read(document) ?? femInputSnapshot ??
            throw new InvalidOperationException("The model configuration is unavailable.");
        var plan = CableSectionOptimizer.Prepare(document, snapshot, run.Settings.SyncGroupsOnly);
        var resultByGroup = run.Rows.GroupBy(row => row.GroupKey, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (run.Settings.SyncGroupsOnly) return ApplyBySyncGroups(plan, resultByGroup);
        EnsureMaterials();
        EnsureSections();
        var library = CableSections.ToList();
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var reservedNames = library.Select(section => section.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int changedGroups = 0;

        foreach (var group in plan.Groups)
        {
            if (!resultByGroup.TryGetValue(group.Key, out var rows) || rows.Length != group.CableNames.Count ||
                rows.Select(row => row.SuggestedStrands).Distinct().Count() != 1)
                throw new InvalidOperationException($"The saved result no longer describes the complete section group '{group.SectionName}'.");
            var count = rows[0].SuggestedStrands;
            if (rows.All(row => row.CurrentStrands == count)) continue;

            var currentNames = group.CableNames.Select(name => FemSectionProperties.FirstOrDefault(property => property.MemberName == name)?.SectionName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (currentNames.Length == 0) currentNames = [group.SectionName];
            foreach (var oldName in currentNames)
            {
                var sourceSection = library.FirstOrDefault(section => section.Name == oldName)
                    ?? throw new InvalidOperationException($"The section library has no entry for '{oldName}'.");
                var sectionName = UniqueCableSectionName(StrandBundle.Name(count, CableSectionOptimizationSettings.StrandDiameterMm), reservedNames);
                reservedNames.Add(sectionName);
                library.Add(sourceSection with
                {
                    Name = sectionName,
                    Area = StrandBundle.Area(count, CableSectionOptimizationSettings.StrandAreaMm2),
                    Notes = string.IsNullOrWhiteSpace(sourceSection.Notes)
                        ? "15.7 mm strands · applied from Cable / Tendon Section Optimisation"
                        : sourceSection.Notes,
                    Source = "App",
                });
                if (renamed.TryGetValue(oldName, out var existing) && existing != sectionName)
                    throw new InvalidOperationException($"Section '{oldName}' belongs to more than one optimization group.");
                renamed[oldName] = sectionName;
            }
            changedGroups++;
        }

        if (changedGroups == 0) return false;
        if (IsFemModelLocked)
        {
            UnlockFemModelForEditing();
            if (IsFemModelLocked) return false;
        }
        CommitStrandSections(library, renamed, tendons: false);
        nextStepName = "Apply suggested tendon sections";
        femWorkingDirty = true;
        PersistFemWorkingSnapshot(advanceRevision: true);
        Message = $"Applied suggested 15.7 mm strand sections to {changedGroups} linked group{(changedGroups == 1 ? "" : "s")}. " +
            "Target cable forces and their load cases were preserved; Edit → Undo restores the previous sections.";
        return true;
    }

    /// <summary>Applies a result grouped by Sync links: each changed group is assigned an "nT15.7" section of its own count — groups
    /// with the same count share it — so cables that shared a section may now differ. The previous sections stay in the library.</summary>
    private bool ApplyBySyncGroups(CableSectionOptimizationPlan plan, IReadOnlyDictionary<string, CableSectionOptimizationRow[]> resultByGroup)
    {
        EnsureMaterials();
        EnsureSections();
        var library = CableSections.ToList();
        var reservedNames = library.Select(section => section.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byCount = new Dictionary<int, string>();
        var assignments = new List<(IReadOnlyList<string> Cables, string Section)>();
        foreach (var group in plan.Groups)
        {
            if (!resultByGroup.TryGetValue(group.Key, out var rows) || rows.Length != group.CableNames.Count ||
                rows.Select(row => row.SuggestedStrands).Distinct().Count() != 1)
                throw new InvalidOperationException($"The saved result no longer describes the cable group '{string.Join(", ", group.CableNames)}'.");
            var count = rows[0].SuggestedStrands;
            if (rows.All(row => row.CurrentStrands == count)) continue;
            if (!byCount.TryGetValue(count, out var sectionName))
            {
                var area = StrandBundle.Area(count, CableSectionOptimizationSettings.StrandAreaMm2);
                var stem = StrandBundle.Name(count, CableSectionOptimizationSettings.StrandDiameterMm);
                if (library.FirstOrDefault(section => section.Name == stem) is { } existing && Math.Abs(existing.Area - area) <= area * 1e-6)
                    sectionName = stem;
                else
                {
                    var current = FemSectionProperties.FirstOrDefault(property => property.MemberName == group.CableNames[0])?.SectionName;
                    var source = library.FirstOrDefault(section => section.Name == current) ?? library.FirstOrDefault()
                        ?? throw new InvalidOperationException("The section library has no cable section to base the new strand section on.");
                    sectionName = UniqueCableSectionName(stem, reservedNames);
                    reservedNames.Add(sectionName);
                    library.Add(source with
                    {
                        Name = sectionName,
                        Area = area,
                        Notes = "15.7 mm strands · applied from Tension Cable Design → Iterate",
                        Source = "App",
                    });
                }
                byCount[count] = sectionName;
            }
            assignments.Add((group.CableNames, sectionName));
        }

        if (assignments.Count == 0) return false;
        if (IsFemModelLocked)
        {
            UnlockFemModelForEditing();
            if (IsFemModelLocked) return false;
        }
        CommitStrandSections(library, new Dictionary<string, string>(StringComparer.Ordinal), tendons: false);
        foreach (var (cables, section) in assignments) AssignSection(cables.ToArray(), section, cable: true);
        nextStepName = "Apply suggested tendon sections";
        femWorkingDirty = true;
        PersistFemWorkingSnapshot(advanceRevision: true);
        Message = $"Applied suggested 15.7 mm strand sections to {assignments.Count} Sync group{(assignments.Count == 1 ? "" : "s")}. " +
            "Target cable forces and their load cases were preserved.";
        return true;
    }

    private static string UniqueCableSectionName(string stem, ISet<string> used)
    {
        if (!used.Contains(stem)) return stem;
        for (var number = 2; ; number++)
        {
            var candidate = $"{stem} ({number})";
            if (!used.Contains(candidate)) return candidate;
        }
    }
}
