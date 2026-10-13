using System.Collections.Immutable;
using Mcotis.Core;

namespace Mcotis.Analysis.Cases;

/// <summary>Why a case is part of a run: it was asked for, or something that was asked for needs it first.</summary>
public enum CaseRunRole { Requested, Prerequisite }

/// <summary>One case of a run, in the place it is solved at.</summary>
/// <param name="Dependencies">What it needs, each by its kind: a state it continues, a stiffness or a mass it reads.</param>
public sealed record PlannedCase(ObjectId CaseId, CaseRunRole Role, ImmutableArray<AnalysisCaseDependency> Dependencies);

/// <summary>The order a run solves its cases in, and what follows from a case that fails.
/// <para><see cref="Cases"/> holds every case once: each after the cases it continues from or takes its stiffness
/// from. Two cases that continue from the same parent are independent branches — neither is before the other for any
/// reason but its id. <see cref="Combinations"/> are evaluated after the cases, inner ones first; a combination adds
/// results, it is not a loading and is never solved.</para></summary>
public sealed record CaseRunPlan(ImmutableArray<PlannedCase> Cases, ImmutableArray<ObjectId> Combinations, ImmutableArray<ValidationIssue> Issues)
{
    private readonly ImmutableDictionary<ObjectId, ImmutableArray<ObjectId>> needs = ImmutableDictionary<ObjectId, ImmutableArray<ObjectId>>.Empty;

    internal CaseRunPlan(ImmutableArray<PlannedCase> cases, ImmutableArray<ObjectId> combinations, ImmutableArray<ValidationIssue> issues,
        ImmutableDictionary<ObjectId, ImmutableArray<ObjectId>> needs) : this(cases, combinations, issues) => this.needs = needs;

    /// <summary>The plan can be run: nothing asked for is missing, and no case depends on itself.</summary>
    public bool IsValid => Issues.All(issue => issue.Severity != ValidationSeverity.Error);

    /// <summary>The cases and combinations of the plan that cannot have a result once these cases have none: every one
    /// that needs one of them, directly or through another. In plan order, cases first. A blocked case is not solved
    /// and is not reported as converged.</summary>
    public ImmutableArray<ObjectId> BlockedBy(IEnumerable<ObjectId> failed)
    {
        var without = failed.ToHashSet();
        var blocked = ImmutableArray.CreateBuilder<ObjectId>();
        foreach (var id in Cases.Select(item => item.CaseId).Concat(Combinations))
        {
            if (without.Contains(id) || !needs.TryGetValue(id, out var sources) || !sources.Any(without.Contains)) continue;
            without.Add(id);
            blocked.Add(id);
        }
        return blocked.ToImmutable();
    }
}

/// <summary>Works out what a run has to solve and in which order (docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md §3).
/// It reads the document and solves nothing. A valid document has no cycle and no wrong reference among its cases;
/// the planner checks again all the same, because a plan that is wrong would solve the wrong thing.</summary>
public static class CaseDependencyPlanner
{
    /// <param name="requested">Cases and combinations to run. Null: every one whose <c>Run</c> is on.</param>
    public static CaseRunPlan Plan(ModelDocument document, IEnumerable<ObjectId>? requested = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Error(string code, ObjectKind kind, ObjectId id, string message) => issues.Add(new(ValidationSeverity.Error, code, kind, id, message));

        var asked = (requested ?? document.LoadCases.Values.Where(item => item.Run).Select(item => item.Id)
            .Concat(document.LoadCombinations.Values.Where(item => item.Run).Select(item => item.Id))).Distinct().OrderBy(id => id).ToArray();
        var askedCases = new HashSet<ObjectId>();
        var combinations = new List<ObjectId>();
        var seenCombinations = new HashSet<ObjectId>();
        var open = new HashSet<ObjectId>();
        var needs = new Dictionary<ObjectId, List<ObjectId>>();

        // A combination: its inner combinations first, and the cases it adds up are needed by it.
        bool Combination(ObjectId id)
        {
            if (seenCombinations.Contains(id)) return true;
            if (!open.Add(id))
            {
                Error("plan.cycle", ObjectKind.LoadCombination, id, $"Load combination '{document.LoadCombinations[id].Name}' contains itself.");
                return false;
            }
            var sources = new List<ObjectId>();
            bool sound = true;
            foreach (var term in document.LoadCombinations[id].Terms.OrderBy(term => term.SourceId))
            {
                if (document.LoadCases.ContainsKey(term.SourceId)) sources.Add(term.SourceId);
                else if (document.LoadCombinations.ContainsKey(term.SourceId)) { sound &= Combination(term.SourceId); sources.Add(term.SourceId); }
                else
                {
                    Error("plan.missing", ObjectKind.LoadCombination, id, $"Load combination '{document.LoadCombinations[id].Name}' uses a load case or combination that does not exist.");
                    sound = false;
                }
            }
            open.Remove(id);
            if (!sound) return false;
            seenCombinations.Add(id);
            combinations.Add(id);
            needs[id] = sources;
            return true;
        }

        foreach (var id in asked)
        {
            if (document.LoadCases.ContainsKey(id)) askedCases.Add(id);
            else if (document.LoadCombinations.ContainsKey(id)) Combination(id);
            else Error("plan.missing", ObjectKind.LoadCase, id, "A load case or combination that was asked for does not exist.");
        }

        // Every case the run touches: those asked for, those the combinations add up, and what each of them needs.
        var cases = new Dictionary<ObjectId, CaseRunRole>();
        var pending = new Stack<ObjectId>(askedCases.Concat(combinations.SelectMany(id => needs[id]).Where(document.LoadCases.ContainsKey)).Distinct().OrderByDescending(id => id));
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (cases.ContainsKey(id)) continue;
            var loadCase = document.LoadCases[id];
            cases[id] = askedCases.Contains(id) ? CaseRunRole.Requested : CaseRunRole.Prerequisite;
            var sources = new List<ObjectId>();
            foreach (var dependency in loadCase.Dependencies)
            {
                if (dependency.Kind == CaseDependencyKind.MassSource)
                {
                    if (!document.MassSources.ContainsKey(dependency.SourceId))
                        Error("plan.massSource", ObjectKind.LoadCase, id, $"Load case '{loadCase.Name}' uses a mass source that does not exist.");
                    continue;
                }
                string how = dependency.Kind == CaseDependencyKind.ContinueState ? "continues from" : "uses the stiffness of";
                if (!document.LoadCases.TryGetValue(dependency.SourceId, out var source))
                {
                    Error("plan.missing", ObjectKind.LoadCase, id, $"Load case '{loadCase.Name}' {how} a load case that does not exist.");
                    continue;
                }
                if (!source.ProducesState)
                    Error("plan.producer", ObjectKind.LoadCase, id,
                        $"Load case '{loadCase.Name}' {how} '{source.Name}', which is not a nonlinear static or initial P-Delta case and ends in no state.");
                sources.Add(dependency.SourceId);
                pending.Push(dependency.SourceId);
            }
            needs[id] = sources;
        }

        // In order: a case after everything it needs; among cases that are ready, by id — the same plan every time.
        var waiting = cases.Keys.ToDictionary(id => id, id => needs[id].Distinct().Count(cases.ContainsKey));
        var ready = new SortedSet<ObjectId>(waiting.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var order = ImmutableArray.CreateBuilder<PlannedCase>();
        while (ready.Count > 0)
        {
            var id = ready.Min;
            ready.Remove(id);
            order.Add(new(id, cases[id], [.. document.LoadCases[id].Dependencies]));
            foreach (var dependent in cases.Keys.Where(other => needs[other].Contains(id)))
                if (--waiting[dependent] == 0) ready.Add(dependent);
            waiting.Remove(id);
        }
        foreach (var id in waiting.Keys.OrderBy(id => id))
            Error("plan.cycle", ObjectKind.LoadCase, id,
                $"Load case '{document.LoadCases[id].Name}' depends, through the cases it continues from or takes its stiffness from, on itself.");

        return new(order.ToImmutable(), [.. combinations], issues.ToImmutable(),
            needs.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Distinct().ToImmutableArray()));
    }

    /// <summary>Every case and combination of the document whose result rests on this case: those that continue from
    /// it, take its stiffness or add it up, directly or through another. When the case changes, their results are
    /// stale with it. By id.</summary>
    public static ImmutableArray<ObjectId> Dependents(ModelDocument document, ObjectId caseId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var found = new HashSet<ObjectId>();
        var pending = new Queue<ObjectId>([caseId]);
        while (pending.Count > 0)
        {
            var source = pending.Dequeue();
            foreach (var loadCase in document.LoadCases.Values)
                if (loadCase.Dependencies.Any(item => item.Kind != CaseDependencyKind.MassSource && item.SourceId == source) && found.Add(loadCase.Id))
                    pending.Enqueue(loadCase.Id);
            foreach (var combination in document.LoadCombinations.Values)
                if (combination.Terms.Any(term => term.SourceId == source) && found.Add(combination.Id))
                    pending.Enqueue(combination.Id);
        }
        found.Remove(caseId);
        return [.. found.OrderBy(id => id)];
    }
}
