using Mcotis.Analysis;
using Mcotis.Core;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using System.Security.Cryptography;
using System.Text;

namespace Mcotis.Desktop.Application;

/// <summary>The inputs of Cable / Tendon Section Optimisation. Strand diameter and steel area are fixed at 15.7 mm and
/// 150 mm²; the optimizer changes strand count only.
/// <para><see cref="StayBasis"/>, when given, rounds each group up to the VSL SSI 2000 assembly Tension Cable Design would choose
/// for its tension and length (units × strands per unit, Ø15.7 mm), which also names the product and its cost.
/// <see cref="DesignCases"/>, when given, sizes each group for its largest tension over those load cases / combinations
/// instead of its pretension-stage force.</para></summary>
public sealed record CableSectionOptimizationSettings(double TargetUr = 0.90, double BreakingStressMpa = 2160,
    double PreliminaryCoefficient = 0.50, int MaximumIterations = 30, double ConvergenceTolerancePercent = 1.00,
    TensionCableBasis? StayBasis = null, IReadOnlyList<string>? DesignCases = null)
{
    /// <summary>The VSL basis the iteration sizes with: only VSL SSI 2000 offered, the rest as Design Basis sets it.</summary>
    public TensionCableBasis? VslBasis => StayBasis is null ? null : StayBasis with { Systems = [CableSystem.VslSsi2000] };

    /// <summary>The VSL assembly for a group carrying <paramref name="forceKN"/> over <paramref name="lengthM"/>; null without a stay basis.</summary>
    public TensionCableSelection? StayFor(double forceKN, double lengthM)
    {
        if (VslBasis is not { } basis) return null;
        var force = Math.Max(Math.Abs(forceKN), 1e-6);
        return TensionCableDesigner.Candidates(basis, force, Math.Max(lengthM, 1e-3)).FirstOrDefault()
            ?? throw new InvalidOperationException($"No VSL SSI 2000 assembly of at most {basis.MaxUnits} units carries {force:#,0} kN. Raise Most units per stay on the Design Basis tab.");
    }

    public static CableSectionOptimizationSettings Default { get; } = new();

    public const double StrandDiameterMm = 15.7;
    public const double StrandAreaMm2 = 150;

    /// <summary>Breaking resistance per strand [kN], including the preliminary coefficient.</summary>
    public double StrandCapacityKN => StrandAreaMm2 * BreakingStressMpa * PreliminaryCoefficient / 1000.0;

    public void Validate()
    {
        if (!(TargetUr > 0 && TargetUr <= 1) || !double.IsFinite(TargetUr))
            throw new ArgumentOutOfRangeException(nameof(TargetUr), "Target UR must be greater than 0 and no greater than 1.");
        if (!(BreakingStressMpa > 0) || !double.IsFinite(BreakingStressMpa))
            throw new ArgumentOutOfRangeException(nameof(BreakingStressMpa), "Breaking stress must be a finite value greater than 0 MPa.");
        if (!(PreliminaryCoefficient > 0 && PreliminaryCoefficient <= 1) || !double.IsFinite(PreliminaryCoefficient))
            throw new ArgumentOutOfRangeException(nameof(PreliminaryCoefficient), "Preliminary coefficient must be greater than 0 and no greater than 1.");
        if (MaximumIterations is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(MaximumIterations), "Maximum iterations must be between 1 and 50.");
        if (!(ConvergenceTolerancePercent > 0 && ConvergenceTolerancePercent <= 100) || !double.IsFinite(ConvergenceTolerancePercent))
            throw new ArgumentOutOfRangeException(nameof(ConvergenceTolerancePercent), "Convergence tolerance must be greater than 0 and no greater than 100%.");
        if (!(StrandCapacityKN > 0) || !double.IsFinite(StrandCapacityKN))
            throw new ArgumentOutOfRangeException(nameof(BreakingStressMpa), "The strand capacity is not finite.");
    }

    public int SuggestedStrands(double calculatedForceKN)
    {
        Validate();
        if (!double.IsFinite(calculatedForceKN)) throw new ArgumentOutOfRangeException(nameof(calculatedForceKN));
        var count = Math.Ceiling(Math.Abs(calculatedForceKN) / (TargetUr * StrandCapacityKN) - 1e-12);
        if (count > int.MaxValue) throw new InvalidOperationException("The calculated force requires more strands than the optimizer can represent.");
        return Math.Max(1, (int)count);
    }

    public double Utilisation(double calculatedForceKN, int strandCount)
    {
        Validate();
        if (strandCount <= 0) throw new ArgumentOutOfRangeException(nameof(strandCount));
        return Math.Abs(calculatedForceKN) / (strandCount * StrandCapacityKN);
    }
}

/// <summary>One explicit strand-count relationship discovered from a shared assigned section or a shared section name
/// in the main-window model projection. Geometry and cable-force sync settings are not grouping keys.</summary>
public sealed record CableSectionOptimizationGroup(string Key, string SectionName, int CurrentStrands,
    IReadOnlyDictionary<string, int> CurrentStrandsByCable, IReadOnlyList<ObjectId> CableIds, IReadOnlyList<string> CableNames);

/// <summary>Prepared immutable inputs for one optimization run.</summary>
public sealed class CableSectionOptimizationPlan
{
    internal CableSectionOptimizationPlan(ModelDocument document, string revisionHash,
        IReadOnlyList<CableSectionOptimizationGroup> groups, IReadOnlyList<string> fitCaseNames,
        IReadOnlyDictionary<ObjectId, string> cableNames, IReadOnlyDictionary<string, double>? cableLengths = null)
    {
        CableLengths = cableLengths ?? new Dictionary<string, double>(StringComparer.Ordinal);
        Document = document;
        RevisionHash = revisionHash;
        Groups = groups;
        FitCaseNames = fitCaseNames;
        CableNames = cableNames;
    }

    internal ModelDocument Document { get; }
    internal IReadOnlyDictionary<ObjectId, string> CableNames { get; }
    /// <summary>Chord length of each cable [m], by name.</summary>
    public IReadOnlyDictionary<string, double> CableLengths { get; }
    public string RevisionHash { get; }
    public IReadOnlyList<CableSectionOptimizationGroup> Groups { get; }
    public IReadOnlyList<string> FitCaseNames { get; }
}

/// <summary>A cable's force at the end of one pretension fitting case [kN].</summary>
public sealed record CableSectionOptimizationForceSample(string CaseName, string CableName,
    double? TargetForceKN, double CalculatedForceKN);

/// <summary>The global translational displacement of one point at the end of a pretension fitting case [m].</summary>
public sealed record CableSectionOptimizationDeflectionSample(string CaseName, string JointName, double UXM, double UYM, double UZM);

/// <summary>One complete analysis pass, including the fitted cable forces and the monitored point displacements.</summary>
public sealed record CableSectionOptimizationIteration(int Number, IReadOnlyDictionary<string, int> TrialStrandCounts,
    IReadOnlyList<CableSectionOptimizationForceSample> CableForces,
    IReadOnlyList<CableSectionOptimizationDeflectionSample> PretensionDeflections,
    double? MaximumForceChangePercent, string? GoverningForce,
    double? MaximumDeflectionChangePercent, string? GoverningDeflection,
    string? AdjustedGroup, int? AdjustedToStrands)
{
    public string? Adjustment => AdjustedGroup is null ? null : $"{AdjustedGroup} → {AdjustedToStrands} × 15.7 mm";
}

/// <summary>One tendon row in the compact results table. Target and calculated forces remain available in the
/// selected-tendon details and in every iteration's saved history.</summary>
public sealed record CableSectionOptimizationRow(string TendonName, string GroupKey, int CurrentStrands,
    int SuggestedStrands, double TargetForceKN, double CalculatedForceKN, double CurrentUr, double SuggestedUr, string Status)
{
    /// <summary>The VSL assembly of the suggestion ("2 × VSL SSI 2000 6-61"); null without a stay basis.</summary>
    public string? Product { get; init; }
    /// <summary>The indicative installed cost of the suggested stay [S$]; null without a stay basis.</summary>
    public double? CostSgd { get; init; }
    /// <summary>The force the cable carried with its current section [kN].</summary>
    public double CurrentForceKN { get; init; }
    /// <summary>The load case / combination of <see cref="CalculatedForceKN"/>; empty for a pretension-stage force.</summary>
    public string GoverningCase { get; init; } = "";
}

/// <summary>The persisted optimization history stored beside the model in its results folder.</summary>
public sealed record CableSectionOptimizationRun(Guid DocumentId, string AnalysisInputHash, string RevisionHash,
    CableSectionOptimizationSettings Settings, string? PretensionCases, DateTime StartedUtc, DateTime CompletedUtc,
    IReadOnlyList<CableSectionOptimizationIteration> Iterations, IReadOnlyList<CableSectionOptimizationRow> Rows,
    bool Converged, bool Cancelled, string? Failure)
{
    public const string FileName = "cable-section-optimization.json";
    public int IterationCount => Iterations.Count;
    public double? MaximumForceChangePercent => Iterations.LastOrDefault()?.MaximumForceChangePercent;
    public double? MaximumDeflectionChangePercent => Iterations.LastOrDefault()?.MaximumDeflectionChangePercent;
}

/// <summary>One live pass reported to the modal optimizer while its analyses run.</summary>
public sealed record CableSectionOptimizationProgress(CableSectionOptimizationIteration Iteration,
    IReadOnlyList<CableSectionOptimizationRow> Rows, bool Converged, string Status);

/// <summary>
/// Runs strand sizing against immutable native-document snapshots. It preserves the document's target-force assignments
/// and load cases exactly; each candidate changes cable sections only and is analysed through FemDocumentRun.
/// </summary>
public static class CableSectionOptimizer
{
    private const double StrandAreaM2 = CableSectionOptimizationSettings.StrandAreaMm2 * 1e-6;

    public static CableSectionOptimizationPlan Prepare(ModelDocument document, FemInputSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Cables.IsEmpty) throw new InvalidOperationException("The model has no cable objects to optimise.");

        var names = LegacyNames.Of(document);
        var properties = (snapshot?.SectionProperties ?? []).GroupBy(item => item.MemberName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var cables = document.Cables.Values.OrderBy(cable => names[cable.Id], StringComparer.Ordinal).ToArray();
        var parent = cables.ToDictionary(cable => cable.Id, cable => cable.Id);
        ObjectId Root(ObjectId id)
        {
            var current = id;
            while (parent[current] != current) current = parent[current];
            while (parent[id] != id)
            {
                var next = parent[id];
                parent[id] = current;
                id = next;
            }
            return current;
        }
        void Union(ObjectId first, ObjectId next)
        {
            var a = Root(first);
            var b = Root(next);
            if (a != b) parent[b] = a;
        }

        foreach (var shared in cables.Select(cable => (Cable: cable, Section: document.AssignmentOfCable(cable.Id).SectionId))
                     .Where(item => item.Section is not null).GroupBy(item => item.Section))
        {
            var ids = shared.Select(item => item.Cable.Id).ToArray();
            foreach (var id in ids.Skip(1)) Union(ids[0], id);
        }
        foreach (var shared in cables.Select(cable => (Cable: cable, Property: properties.GetValueOrDefault(names[cable.Id])))
                     .Where(item => !string.IsNullOrWhiteSpace(item.Property?.SectionName))
                     .GroupBy(item => item.Property!.SectionName!, StringComparer.Ordinal))
        {
            var ids = shared.Select(item => item.Cable.Id).ToArray();
            foreach (var id in ids.Skip(1)) Union(ids[0], id);
        }

        // Cable Force setup defines which tendons are selected together. A follower and its reference (including
        // transitive follower chains) must receive one strand count even when their current section IDs differ.
        var cableIdsByName = cables.ToDictionary(cable => names[cable.Id], cable => cable.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var row in snapshot?.CableSetup?.Cables ?? [])
        {
            var (syncMode, referenceName) = ResolveSync(row);
            if (syncMode == CableSyncMode.Independent || string.IsNullOrWhiteSpace(referenceName) ||
                !cableIdsByName.TryGetValue(row.CableName, out var cableId) ||
                !cableIdsByName.TryGetValue(referenceName, out var referenceId)) continue;
            Union(cableId, referenceId);
        }

        var groups = new List<CableSectionOptimizationGroup>();
        foreach (var members in cables.GroupBy(cable => Root(cable.Id)))
        {
            var ordered = members.OrderBy(cable => names[cable.Id], StringComparer.Ordinal).ToArray();
            var sections = ordered.Select(cable =>
            {
                var assignment = document.AssignmentOfCable(cable.Id);
                if (assignment.SectionId is not { } sectionId || !document.CableSections.TryGetValue(sectionId, out var section))
                    throw new InvalidOperationException($"Cable '{names[cable.Id]}' has no assigned native cable section.");
                var property = properties.GetValueOrDefault(names[cable.Id]);
                return (Cable: cable, Name: names[cable.Id], Assignment: assignment, Section: section, Property: property,
                    GroupName: string.IsNullOrWhiteSpace(property?.SectionName) ? section.Name : property.SectionName!);
            }).ToArray();
            var currentStrands = sections.ToDictionary(item => item.Name,
                item => CurrentStrands(item.Section, item.GroupName, item.Name), StringComparer.Ordinal);
            var namesInGroup = sections.Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var sourceNames = sections.Select(item => item.GroupName).Distinct(StringComparer.Ordinal).ToArray();
            var sectionName = sourceNames.Length == 1 ? sourceNames[0] : string.Join(" / ", sourceNames.OrderBy(name => name, StringComparer.Ordinal));
            var key = string.Join("\u001f", namesInGroup);
            groups.Add(new(key, sectionName, currentStrands.Values.Max(), currentStrands,
                sections.Select(item => item.Cable.Id).ToArray(), namesInGroup));
        }

        var targets = document.Loads.Values.OfType<Mcotis.Core.CableTargetForce>().ToArray();
        var caseOrder = snapshot?.CaseRows.Select(row => row.Name).ToArray() ?? [];
        int CaseOrder(string name)
        {
            var index = Array.IndexOf(caseOrder, name);
            return index >= 0 ? index : int.MaxValue;
        }
        var fitCases = document.LoadCases.Values.Where(item => item.FitCableTargetForces)
            .Where(item => item.Components.Any(component => component.Factor != 0 && targets.Any(target => target.PatternId == component.PatternId)))
            .OrderBy(item => CaseOrder(item.Name))
            .ThenBy(item => item.Name, StringComparer.Ordinal).Select(item => item.Name).ToArray();
        if (fitCases.Length == 0)
            throw new InvalidOperationException("The model has no pretension load case that fits cable target forces. Define a pretension fitting case before optimising.");

        var lengths = cables.ToDictionary(cable => names[cable.Id], cable =>
        {
            var (a, b) = (document.Points[cable.StartPointId].Position, document.Points[cable.EndPointId].Position);
            return Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
        }, StringComparer.Ordinal);
        return new(document, snapshot is null ? "" : FemInputDatabase.RevisionHash(snapshot), groups, fitCases,
            cables.ToDictionary(cable => cable.Id, cable => names[cable.Id]), lengths);
    }

    private static (CableSyncMode Mode, string? Reference) ResolveSync(CableForceRowDto row)
    {
        if (Enum.IsDefined(row.SyncMode) && row.SyncMode != CableSyncMode.Independent &&
            !string.IsNullOrWhiteSpace(row.ReferenceCableName))
            return (row.SyncMode, row.ReferenceCableName);
        if (!string.IsNullOrWhiteSpace(row.SameStepCableName)) return (CableSyncMode.SameStep, row.SameStepCableName);
        if (!string.IsNullOrWhiteSpace(row.SameAsCableName)) return (CableSyncMode.SameValues, row.SameAsCableName);
        return (Enum.IsDefined(row.SyncMode) ? row.SyncMode : CableSyncMode.Independent, row.ReferenceCableName);
    }

    /// <param name="analyse">Analyses a candidate document with the model's full load plan, combinations included. Required
    /// when <see cref="CableSectionOptimizationSettings.DesignCases"/> is set: the groups are then sized for their design tension.</param>
    public static CableSectionOptimizationRun Run(ModelDocument document, FemInputSnapshot? snapshot,
        CableSectionOptimizationSettings settings, IProgress<CableSectionOptimizationProgress>? progress = null,
        CancellationToken cancellationToken = default, Func<ModelDocument, CancellationToken, FemStudyRun>? analyse = null)
    {
        var designCases = settings.DesignCases is { Count: > 0 } named ? named : null;
        if (designCases is not null && analyse is null)
            throw new ArgumentException("Sizing for design load cases needs the model's analysis.", nameof(analyse));
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var started = DateTime.UtcNow;
        var revisionHash = snapshot is null ? "" : FemInputDatabase.RevisionHash(snapshot);
        var iterations = new List<CableSectionOptimizationIteration>();
        IReadOnlyList<CableSectionOptimizationRow> lastRows = [];
        var plan = Prepare(document, snapshot);
        var trialCounts = plan.Groups.ToDictionary(group => group.Key, group => group.CurrentStrands, StringComparer.Ordinal);
        Dictionary<string, double>? previousForces = null;
        Dictionary<string, double>? previousDeflections = null;
        Dictionary<string, double>? baselineForces = null;
        IReadOnlyDictionary<string, double> forceTargets = new Dictionary<string, double>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, int> desiredCounts = trialCounts;
        Dictionary<string, string> governingCases = new(StringComparer.Ordinal);
        // One analysis of a document: the pretension-stage samples (target vs fitted force) and the forces the groups are sized
        // for — the pretension-stage force, or with design cases the largest tension over them, keyed "case␟cable".
        (IReadOnlyList<CableSectionOptimizationForceSample> Fit, FemDocumentResults Analysed, Dictionary<string, double> Sizing) Evaluate(ModelDocument candidate)
        {
            if (designCases is null)
            {
                var solved = FemDocumentRun.Solve(candidate, plan.FitCaseNames, cancellationToken: cancellationToken);
                var fit = ForceSamples(plan, solved);
                return (fit, solved, ForceValues(fit));
            }
            var run = analyse!(candidate, cancellationToken);
            var native = run.Native ?? throw new InvalidOperationException("The candidate analysis returned no native results.");
            var sizing = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var caseName in designCases)
            {
                if (run.Results is null || !run.Results.TryGetValue(caseName, out var result))
                    throw new InvalidOperationException($"Load case / combination '{caseName}' has no result in the candidate analysis.");
                foreach (var cable in result.Cables)
                {
                    if (!double.IsFinite(cable.AxialForceKN)) throw new InvalidOperationException($"Cable '{cable.MemberName}' has a non-finite force in '{caseName}'.");
                    sizing[$"{caseName}\u001f{cable.MemberName}"] = cable.AxialForceKN;
                }
            }
            return (ForceSamples(plan, native), native, sizing);
        }
        bool converged = false, cancelled = false;
        string? failure = null;
        string? caseSummary = string.Join(", ", plan.FitCaseNames);

        // A Sync group may contain different initial section sizes. Analyse the unchanged model once so Current UR
        // still reports each cable's actual starting condition before trial candidates harmonise the group's size.
        if (plan.Groups.Any(group => group.CurrentStrandsByCable.Values.Distinct().Count() > 1))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (samples, _, initialForces) = Evaluate(document);
                baselineForces = initialForces;
                forceTargets = samples.GroupBy(sample => sample.CableName, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Select(sample => sample.TargetForceKN ?? 0).Max(), StringComparer.Ordinal);
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DocumentException or KeyNotFoundException)
            { failure = ex.Message; }
        }

        for (var number = 1; number <= settings.MaximumIterations && failure is null && !cancelled; number++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = Candidate(document, plan, trialCounts);
                var (forceSamples, analysed, forces) = Evaluate(candidate);
                var deflectionSamples = DeflectionSamples(candidate, plan.FitCaseNames, analysed);
                var deflections = DeflectionValues(deflectionSamples);
                if (baselineForces is null) baselineForces = forces;
                if (forceTargets.Count == 0)
                    forceTargets = forceSamples.GroupBy(sample => sample.CableName, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Select(sample => sample.TargetForceKN ?? 0).Max(), StringComparer.Ordinal);

                desiredCounts = SuggestedCounts(plan, forces, settings);
                governingCases = GoverningCases(forces);
                var pending = plan.Groups.Where(group => desiredCounts[group.Key] != trialCounts[group.Key])
                    .OrderBy(group => trialCounts[group.Key]).ThenBy(group => group.CableNames[0], StringComparer.Ordinal).ToArray();
                var forceChange = previousForces is null ? (Value: (double?)null, Governing: (string?)null)
                    : MaximumRelativeChange(previousForces, forces, designCases is null ? forceTargets : null, forceFloorKN: true);
                var deflectionChange = previousDeflections is null ? (Value: (double?)null, Governing: (string?)null)
                    : MaximumRelativeChange(previousDeflections, deflections, null, forceFloorKN: false,
                        modelLengthScale: ModelLengthScale(candidate));
                converged = MeetsDualConvergence(pending.Length > 0, forceChange.Value, deflectionChange.Value,
                    settings.ConvergenceTolerancePercent);

                // Strand by strand, one group a pass; a VSL basis moves every pending group to its next standard assembly at once.
                var stepAll = settings.StayBasis is not null;
                var changes = converged || number >= settings.MaximumIterations ? []
                    : stepAll ? pending : pending.Take(1).ToArray();
                int Next(CableSectionOptimizationGroup group) => stepAll ? desiredCounts[group.Key]
                    : trialCounts[group.Key] + Math.Sign(desiredCounts[group.Key] - trialCounts[group.Key]);
                var trialSnapshot = new Dictionary<string, int>(trialCounts, StringComparer.Ordinal);
                var iteration = new CableSectionOptimizationIteration(number, trialSnapshot, forceSamples, deflectionSamples,
                    forceChange.Value, forceChange.Governing, deflectionChange.Value, deflectionChange.Governing,
                    changes.Length == 0 ? null : string.Join("; ", changes.Select(group => string.Join(", ", group.CableNames))),
                    changes.Length == 1 ? Next(changes[0]) : null);
                iterations.Add(iteration);
                lastRows = Rows(plan, baselineForces, forces, forceTargets, desiredCounts, governingCases, settings,
                    converged ? "Converged" : number == settings.MaximumIterations ? "Maximum iterations" : "Iterating");
                progress?.Report(new(iteration, lastRows, converged,
                    converged ? "Converged" : number == settings.MaximumIterations ? "Maximum iterations reached" : "Iterating"));

                if (converged || number == settings.MaximumIterations) break;
                previousForces = forces;
                previousDeflections = deflections;
                foreach (var group in changes) trialCounts[group.Key] = Next(group);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DocumentException or KeyNotFoundException)
            {
                failure = ex.Message;
                break;
            }
        }

        var completeRows = lastRows.Select(row => row with
        {
            Status = failure is not null ? "Failed" : cancelled ? "Cancelled" : converged ? "Converged" : "Maximum iterations",
        }).ToArray();
        return new(document.DocumentId.Value, document.AnalysisInputHash, revisionHash, settings, caseSummary, started,
            DateTime.UtcNow, iterations, completeRows, converged, cancelled, failure);
    }

    private static int CurrentStrands(CableSectionDefinition section, string sectionName, string cableName)
    {
        if (StrandBundle.Parse(sectionName) is { } parsed)
        {
            if (Math.Abs(parsed.DiameterMm - CableSectionOptimizationSettings.StrandDiameterMm) > 1e-6)
                throw new InvalidOperationException($"Cable '{cableName}' uses {parsed.DiameterMm:0.###} mm strands. This optimizer keeps the diameter fixed at 15.7 mm.");
            var namedArea = parsed.Count * StrandAreaM2;
            // the name states the count; an area within 1 % of it is that count (catalogue strand areas round, 150 vs 150.01 mm²)
            if (Math.Abs(section.Area - namedArea) > Math.Max(1e-12, namedArea * 1e-2))
                throw new InvalidOperationException($"Cable '{cableName}' section '{sectionName}' names {parsed.Count} strands, but its native area does not equal {parsed.Count} × 150 mm². Resolve the section data before optimising.");
            return parsed.Count;
        }

        var ratio = section.Area / StrandAreaM2;
        var rounded = Math.Round(ratio);
        if (!(rounded >= 1) || !double.IsFinite(ratio) || Math.Abs(ratio - rounded) > Math.Max(1e-6, Math.Abs(ratio) * 1e-6) || rounded > int.MaxValue)
            throw new InvalidOperationException($"Cable '{cableName}' section '{sectionName}' does not define a whole number of 15.7 mm strands (150 mm² each).");
        return (int)rounded;
    }

    private static ModelDocument Candidate(ModelDocument source, CableSectionOptimizationPlan plan,
        IReadOnlyDictionary<string, int> trialCounts)
    {
        var editor = source.Edit();
        var usedNames = source.CableSections.Values.Select(section => section.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in plan.Groups.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var count = trialCounts[group.Key];
            if (count == group.CurrentStrands && group.CurrentStrandsByCable.Values.All(current => current == count)) continue;
            var perMaterial = group.CableIds.GroupBy(id => source.CableSections[source.AssignmentOfCable(id).SectionId!.Value].MaterialId);
            foreach (var sameMaterial in perMaterial)
            {
                var sourceSection = source.CableSections[source.AssignmentOfCable(sameMaterial.First()).SectionId!.Value];
                var sectionId = CandidateSectionId(source, group.Key, sameMaterial.Key, count);
                var name = UniqueName(StrandBundle.Name(count, CableSectionOptimizationSettings.StrandDiameterMm), usedNames);
                usedNames.Add(name);
                editor.PutCableSection(new(sectionId, name, sameMaterial.Key, StrandBundle.Area(count, CableSectionOptimizationSettings.StrandAreaMm2)));
                editor.SetCableSection(sameMaterial, sectionId);
            }
        }
        return editor.Commit().Document;
    }

    private static IReadOnlyList<CableSectionOptimizationForceSample> ForceSamples(CableSectionOptimizationPlan plan,
        FemDocumentResults results)
    {
        var samples = new List<CableSectionOptimizationForceSample>();
        foreach (var caseName in plan.FitCaseNames)
        {
            if (!results.Static.TryGetValue(caseName, out var result))
                throw new InvalidOperationException($"Pretension fitting case '{caseName}' has no static result for cable force convergence.");
            var observedCableIds = result.Cables.Select(cable => cable.ParentObjectId).ToHashSet();
            var missing = plan.CableNames.Keys.Where(id => !observedCableIds.Contains(id)).Select(id => plan.CableNames[id]).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"Pretension fitting case '{caseName}' returned no calculated force for: {string.Join(", ", missing)}.");
            foreach (var cable in result.Cables)
            {
                var name = plan.CableNames[cable.ParentObjectId];
                var force = cable.FitStageTension ?? cable.Tension;
                if (!double.IsFinite(force)) throw new InvalidOperationException($"Cable '{name}' returned a non-finite pretension-stage force.");
                samples.Add(new(caseName, name, cable.TargetForce, force));
            }
        }
        return samples;
    }

    private static IReadOnlyList<CableSectionOptimizationDeflectionSample> DeflectionSamples(ModelDocument document,
        IReadOnlyList<string> fitCaseNames, FemDocumentResults results)
    {
        var samples = new List<CableSectionOptimizationDeflectionSample>();
        var names = LegacyNames.Of(document);
        foreach (var caseName in fitCaseNames)
        {
            if (!results.Static.TryGetValue(caseName, out var result))
                throw new InvalidOperationException($"The pretension fitting case '{caseName}' has no static result to compare for deflection convergence.");
            foreach (var joint in result.Joints)
            {
                var d = joint.Displacements;
                if (d.Length < 3 || !double.IsFinite(d[0]) || !double.IsFinite(d[1]) || !double.IsFinite(d[2]))
                    throw new InvalidOperationException("The pretension-stage analysis returned a non-finite joint displacement.");
                samples.Add(new(caseName, names[joint.PointId], d[0], d[1], d[2]));
            }
        }
        return samples;
    }

    private static Dictionary<string, double> ForceValues(IEnumerable<CableSectionOptimizationForceSample> samples) =>
        samples.ToDictionary(sample => $"{sample.CaseName}\u001f{sample.CableName}", sample => sample.CalculatedForceKN, StringComparer.Ordinal);

    private static Dictionary<string, double> DeflectionValues(IEnumerable<CableSectionOptimizationDeflectionSample> samples)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            values[$"{sample.CaseName}\u001f{sample.JointName}\u001fUX"] = sample.UXM;
            values[$"{sample.CaseName}\u001f{sample.JointName}\u001fUY"] = sample.UYM;
            values[$"{sample.CaseName}\u001f{sample.JointName}\u001fUZ"] = sample.UZM;
        }
        return values;
    }

    internal static (double? Value, string? Governing) MaximumRelativeChange(IReadOnlyDictionary<string, double> previous,
        IReadOnlyDictionary<string, double> current, IReadOnlyDictionary<string, double>? targets, bool forceFloorKN,
        double modelLengthScale = 0)
    {
        if (previous.Count == 0 || previous.Count != current.Count || previous.Keys.Any(key => !current.ContainsKey(key)))
            throw new InvalidOperationException("The solver returned different force or deflection monitoring points between iterations.");
        var scale = Math.Max(modelLengthScale, previous.Values.Concat(current.Values).Select(Math.Abs).DefaultIfEmpty().Max());
        if (targets is not null) scale = Math.Max(scale, targets.Values.Select(Math.Abs).DefaultIfEmpty().Max());
        // Keep the denominator meaningful near zero: 1e-6 kN for force or 1e-9 m for deflection, raised to
        // one-billionth of the relevant target/result force or model/result displacement scale when larger.
        var floor = forceFloorKN ? Math.Max(1e-6, scale * 1e-9) : Math.Max(1e-9, scale * 1e-9);
        double maximum = 0;
        string? governing = null;
        foreach (var (key, before) in previous)
        {
            var after = current[key];
            var relative = Math.Abs(after - before) / Math.Max(Math.Abs(before), floor) * 100;
            if (!double.IsFinite(relative)) throw new InvalidOperationException($"The convergence change for '{key}' is not finite.");
            if (relative > maximum) (maximum, governing) = (relative, key);
        }
        return (maximum, governing);
    }

    internal static bool MeetsDualConvergence(bool hasPendingCountChange, double? forceChangePercent,
        double? deflectionChangePercent, double tolerancePercent) =>
        !hasPendingCountChange && forceChangePercent is { } force && force <= tolerancePercent &&
        deflectionChangePercent is { } deflection && deflection <= tolerancePercent;

    private static double ModelLengthScale(ModelDocument document)
    {
        if (document.Points.IsEmpty) return 0;
        var xs = document.Points.Values.Select(point => point.Position.X).ToArray();
        var ys = document.Points.Values.Select(point => point.Position.Y).ToArray();
        var zs = document.Points.Values.Select(point => point.Position.Z).ToArray();
        return Math.Sqrt(Math.Pow(xs.Max() - xs.Min(), 2) + Math.Pow(ys.Max() - ys.Min(), 2) + Math.Pow(zs.Max() - zs.Min(), 2));
    }

    private static IReadOnlyDictionary<string, int> SuggestedCounts(CableSectionOptimizationPlan plan,
        IReadOnlyDictionary<string, double> forces, CableSectionOptimizationSettings settings)
    {
        var byName = forces.GroupBy(pair => pair.Key[(pair.Key.IndexOf('\u001f') + 1)..], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(pair => Math.Abs(pair.Value)), StringComparer.Ordinal);
        if (settings.StayBasis is not null)
            return plan.Groups.ToDictionary(group => group.Key, group => GroupStay(plan, group, byName, settings)!.TotalStrands, StringComparer.Ordinal);
        return plan.Groups.ToDictionary(group => group.Key,
            group => group.CableNames.Select(name => byName.TryGetValue(name, out var force) ? settings.SuggestedStrands(force) : group.CurrentStrands).Max(),
            StringComparer.Ordinal);
    }

    /// <summary>The VSL assembly of a group: its largest force over its longest cable.</summary>
    private static TensionCableSelection? GroupStay(CableSectionOptimizationPlan plan, CableSectionOptimizationGroup group,
        IReadOnlyDictionary<string, double> forceByName, CableSectionOptimizationSettings settings) =>
        settings.StayFor(group.CableNames.Select(name => forceByName.GetValueOrDefault(name)).DefaultIfEmpty().Max(),
            group.CableNames.Select(name => plan.CableLengths.GetValueOrDefault(name)).DefaultIfEmpty().Max());

    /// <summary>The load case of each cable's largest force ("case␟cable" keys).</summary>
    private static Dictionary<string, string> GoverningCases(IReadOnlyDictionary<string, double> forces) =>
        forces.GroupBy(pair => pair.Key[(pair.Key.IndexOf('\u001f') + 1)..], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var key = group.MaxBy(pair => Math.Abs(pair.Value)).Key;
                return key[..key.IndexOf('\u001f')];
            }, StringComparer.Ordinal);

    private static IReadOnlyList<CableSectionOptimizationRow> Rows(CableSectionOptimizationPlan plan,
        IReadOnlyDictionary<string, double> baselineForces, IReadOnlyDictionary<string, double> finalForces,
        IReadOnlyDictionary<string, double> targets, IReadOnlyDictionary<string, int> desiredCounts,
        IReadOnlyDictionary<string, string> governingCases, CableSectionOptimizationSettings settings, string status)
    {
        var forceByName = finalForces.GroupBy(pair => pair.Key[(pair.Key.IndexOf('\u001f') + 1)..], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(pair => Math.Abs(pair.Value)), StringComparer.Ordinal);
        var baselineByName = baselineForces.GroupBy(pair => pair.Key[(pair.Key.IndexOf('\u001f') + 1)..], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(pair => Math.Abs(pair.Value)), StringComparer.Ordinal);
        var targetByName = targets.GroupBy(pair => pair.Key[(pair.Key.IndexOf('\u001f') + 1)..], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(pair => Math.Abs(pair.Value)), StringComparer.Ordinal);
        return plan.Groups.SelectMany(group =>
        {
            var stay = GroupStay(plan, group, forceByName, settings);
            return group.CableNames.Select(name =>
        {
            var calculated = forceByName.GetValueOrDefault(name);
            var current = baselineByName.GetValueOrDefault(name);
            var target = targetByName.GetValueOrDefault(name);
            var suggested = desiredCounts[group.Key];
            return new CableSectionOptimizationRow(name, group.Key, group.CurrentStrandsByCable[name], suggested, target, calculated,
                settings.Utilisation(current, group.CurrentStrandsByCable[name]), settings.Utilisation(calculated, suggested), status)
            {
                Product = stay is null ? null : stay.TotalStrands == suggested ? stay.Assembly : $"{suggested} × Ø15.7 strands",
                CostSgd = stay is null || stay.TotalStrands != suggested ? null
                    : stay.CostPerMSgd * plan.CableLengths.GetValueOrDefault(name),
                CurrentForceKN = current,
                GoverningCase = governingCases.GetValueOrDefault(name) ?? "",
            };
        });
        }).OrderBy(row => row.TendonName, StringComparer.Ordinal).ToArray();
    }

    private static ObjectId CandidateSectionId(ModelDocument document, string group, ObjectId materialId, int strands)
    {
        for (var salt = 0; ; salt++)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"cable-optimisation|{document.DocumentId.Value:D}|{group}|{materialId.Value:D}|{strands}|{salt}"));
            var id = new ObjectId(new Guid(hash.AsSpan(0, 16)));
            if (id.IsEmpty) continue;
            if (!document.TryGetNamed(id, out _, out _)) return id;
        }
    }

    private static string UniqueName(string stem, ISet<string> used)
    {
        if (!used.Contains(stem)) return stem;
        for (var number = 2; ; number++)
        {
            var candidate = $"{stem} ({number})";
            if (!used.Contains(candidate)) return candidate;
        }
    }
}
