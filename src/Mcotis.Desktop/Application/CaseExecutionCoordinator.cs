using System.Collections.Immutable;
using Mcotis.Analysis.Cases;
using Mcotis.Core;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application;

/// <summary>What the run of a case hands back, by the kind of its result. A static case and a modal case do not have
/// the same result, and neither is to be forced into the shape of the other: a modal case will bring a record of its
/// own (docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md, PR-06). Until then the only result is the static one.</summary>
public interface IFemCaseExecutionResult
{
    Guid CaseId { get; }
    CaseKind Kind { get; }
}

/// <summary>The result of a static case, as the workspace has always had it.</summary>
public sealed record FemStaticCaseExecutionResult : IFemCaseExecutionResult
{
    public Guid CaseId { get; }
    public CaseKind Kind { get; }
    public FemAnalysisResult StaticResult { get; }

    public FemStaticCaseExecutionResult(Guid caseId, CaseKind kind, FemAnalysisResult staticResult)
    {
        if (kind == CaseKind.ModalEigen) throw new ArgumentException("A modal case has no static result.", nameof(kind));
        CaseId = caseId; Kind = kind; StaticResult = staticResult ?? throw new ArgumentNullException(nameof(staticResult));
    }
}

/// <summary>One case of a run, in the order the run plan solves it.</summary>
/// <param name="Dependencies">What it needs, each by its kind, with the name of the case or mass source.</param>
/// <param name="UnavailableReason">Why the case has no result yet (<see cref="FemCaseOptions.NotSolvedYet"/>); null
/// when it is solved.</param>
public sealed record FemPlannedCase(FemCaseRow Row, CaseRunRole Role,
    ImmutableArray<(CaseDependencyKind Kind, string Source)> Dependencies, string? UnavailableReason);

/// <summary>The run plan of the load tables: the cases a run holds — asked for, or needed first — each after what it
/// needs; the combinations it evaluates; and what keeps it from being a plan at all.</summary>
public sealed record FemCaseExecutionPlan(IReadOnlyList<FemPlannedCase> Cases, IReadOnlyList<FemCombinationRow> Combinations,
    IReadOnlyList<string> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>Puts the load tables of the main window before <see cref="CaseDependencyPlanner"/>, the planner of the
/// native model, so that both windows plan a run by one set of rules: Set Load Cases to Run shows this plan, and
/// <see cref="FemLoadPlan.Build"/> runs by it.
/// <para>It plans; it solves nothing. The case a case continues from and the case whose stiffness a case uses stay two
/// kinds of dependency: a linear case is planned after the case whose stiffness it uses, and that case's loads do not
/// become its own.</para>
/// <para>Only what decides the plan is read from a row: its kind, its two references, whether it has loads, its
/// mass source, whether it is set to run. A load pattern a definition names and the model does not have is not the
/// planner's matter — <see cref="FemLoadPlan.Build"/> says so of a case that is part of the run — so a row that
/// is not part of a run cannot keep the others from running.</para></summary>
public static class CaseExecutionCoordinator
{
    /// <param name="massSources">The mass sources of the configuration. Null: the caller does not hold them, and a
    /// modal case is planned on the one it names.</param>
    /// <param name="requested">The identities of the cases and combinations to run. Null: those set to run.</param>
    public static FemCaseExecutionPlan Plan(IReadOnlyList<FemCaseRow> cases, IReadOnlyList<FemCombinationRow> combinations,
        IReadOnlyList<(Guid Id, string Name)>? massSources = null, IEnumerable<Guid>? requested = null)
    {
        try
        {
            var comparer = StringComparer.OrdinalIgnoreCase;
            var caseIds = new Dictionary<string, ObjectId>(comparer);
            foreach (var row in cases) caseIds.TryAdd(row.Name, new(row.Id));
            var combinationIds = new Dictionary<string, ObjectId>(comparer);
            foreach (var row in combinations) combinationIds.TryAdd(row.Name, new(row.Id));
            var sourceIds = new Dictionary<string, ObjectId>(comparer);
            foreach (var (id, name) in massSources ?? []) sourceIds.TryAdd(name, new(id));
            if (massSources is null)
                foreach (var name in cases.Select(row => row.Options.MassSource).Where(name => !string.IsNullOrWhiteSpace(name)))
                    sourceIds.TryAdd(name!, ObjectId.New());

            ObjectId? CaseId(FemCaseRow row, string? name, string how) => FemLoadPlan.IsNoInitialCase(name) ? null
                : caseIds.TryGetValue(name!.Trim(), out var id) ? id
                : throw new InvalidOperationException($"Load case '{row.Name}' {how} '{name!.Trim()}', which is not a load case of the model.");

            var editor = ModelDocument.Create().Edit();
            // what a case carries does not decide the plan, only whether it carries anything: one pattern stands for all
            var loads = new LoadPattern(ObjectId.New(), "Loads", LoadPatternType.Other, 0);
            editor.PutLoadPattern(loads);
            foreach (var source in sourceIds) editor.PutMassSource(new(source.Value, source.Key));
            foreach (var row in cases)
            {
                ModalEigenOptions? modal = row.Options.Kind == CaseKind.ModalEigen && row.Options.MassSource is { Length: > 0 } source &&
                    sourceIds.TryGetValue(source, out var sourceId) ? new(sourceId, row.Options.RequestedModes ?? 0) : null;
                editor.PutLoadCase(new(caseIds[row.Name], row.Name, row.Options.Kind,
                    string.IsNullOrWhiteSpace(row.Definition) ? [] : [new LoadCaseComponent(loads.Id, 1)])
                {
                    InitialCaseId = CaseId(row, row.InitialCase, "continues from"),
                    StiffnessSourceCaseId = CaseId(row, row.Options.StiffnessSourceCase, "uses the stiffness of"),
                    GeometricMode = row.Options.GeometricMode,
                    NonlinearControls = row.Options.Controls,
                    ModalOptions = modal,
                    Run = row.Run,
                    FitCableTargetForces = row.CableTargetForces,
                });
            }
            foreach (var row in combinations)
            {
                // a term that names nothing is said by FemLoadPlan.Build when the combination is run; it adds nothing to the plan
                IReadOnlyList<(string Name, double Factor)> terms;
                try { terms = string.IsNullOrWhiteSpace(row.Definition) ? [] : FemLoadDefinition.Parse(row.Definition); }
                catch (ArgumentException) { terms = []; }
                editor.PutLoadCombination(new(combinationIds[row.Name], row.Name, CombinationType.LinearAdd,
                    [.. terms.Where(term => double.IsFinite(term.Factor))
                        .Select(term => caseIds.TryGetValue(term.Name, out var id) || combinationIds.TryGetValue(term.Name, out id) ? new CombinationTerm(id, term.Factor) : (CombinationTerm?)null)
                        .Where(term => term is not null).Select(term => term!.Value)]) { Run = row.Run });
            }

            var document = editor.Commit().Document;
            var native = CaseDependencyPlanner.Plan(document, requested?.Distinct().Select(id => new ObjectId(id)).ToArray());
            var byId = cases.ToDictionary(row => new ObjectId(row.Id));
            var byCombinationId = combinations.ToDictionary(row => new ObjectId(row.Id));
            var planned = native.Cases.Select(item =>
            {
                var row = byId[item.CaseId];
                return new FemPlannedCase(row, item.Role,
                    [.. item.Dependencies.Select(dependency => (dependency.Kind,
                        document.TryGetNamed(dependency.SourceId, out _, out var name) ? name : dependency.SourceId.ToString()))],
                    row.Supported ? row.Options.NotSolvedYet(row.Name) : $"Load case '{row.Name}' ({row.Type}) is not supported by the internal FEM.");
            }).ToArray();
            return new(planned, native.Combinations.Select(id => byCombinationId[id]).ToArray(),
                native.Issues.Where(issue => issue.Severity == ValidationSeverity.Error).Select(issue => issue.Message).ToArray());
        }
        catch (DocumentException exception)
        {
            return new([], [], exception.Issues.Where(issue => issue.Severity == ValidationSeverity.Error).Select(issue => issue.Message).DefaultIfEmpty(exception.Message).ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return new([], [], [exception.Message]);
        }
    }
}
