using System.Globalization;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application;

// ---------------------------------------------------------------------------------------------------------------
// Read-only snapshot of the SAP2000 loading used by the internal FEM (patterns, their assigned loads, static load
// cases and response combinations). Read through ISapGateway.GetFemLoadingAsync; never written back to SAP.
// ---------------------------------------------------------------------------------------------------------------

public sealed record SapLoadPattern(string Name, string Type, double SelfWeightMultiplier);
public sealed record SapPatternJointLoad(string Pattern, FemJointLoad Load);
public sealed record SapPatternMemberLoad(string Pattern, FemMemberLoad Load);
public sealed record SapPatternMemberPointLoad(string Pattern, FemMemberPointLoad Load);
/// <summary>SAP "gravity load" assignment: multipliers of the member's self-weight along global X and Z.</summary>
public sealed record SapPatternGravityLoad(string Pattern, string MemberName, double MultiplierX, double MultiplierZ);
public sealed record SapLoadCase(string Name, string Type, bool Supported, IReadOnlyList<(string Pattern, double Factor)> Loads,
    string? InitialCase, string? Note);
public sealed record SapLoadCombination(string Name, string Type, bool Supported,
    IReadOnlyList<(string Name, bool IsCombination, double Factor)> Items, string? Note);
public sealed record SapFemLoading(
    IReadOnlyList<SapLoadPattern> Patterns,
    IReadOnlyList<SapPatternJointLoad> JointLoads,
    IReadOnlyList<SapPatternMemberLoad> MemberLoads,
    IReadOnlyList<SapPatternMemberPointLoad> MemberPointLoads,
    IReadOnlyList<SapPatternGravityLoad> GravityLoads,
    IReadOnlyList<SapLoadCase> Cases,
    IReadOnlyList<SapLoadCombination> Combinations,
    IReadOnlyList<string> Warnings)
{
    public static SapFemLoading Empty { get; } = new([], [], [], [], [], [], [], []);
}

// ---------------------------------------------------------------------------------------------------------------
// Editable rows shown on FEM → Inputs → Loads. Values start from SAP and can be edited in the app only.
// ---------------------------------------------------------------------------------------------------------------

public sealed class FemPatternRow(string name, string type, double selfWeightMultiplier, string role, string loads) : Observable
{
    private double selfWeightMultiplier = selfWeightMultiplier;
    private string type = type, role = role, loads = loads;
    public string Name { get; } = name;
    /// <summary>Dead, Live, Other … — the default load cases factor a Tab 01 pattern as dead or live by it.</summary>
    public string Type
    {
        get => type;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Enter a type, e.g. Dead, Live or Other.");
            Set(ref type, value.Trim());
        }
    }
    /// <summary>Factor on member self-weight (γ·A·weight modifier) applied as gravity in this pattern.</summary>
    public double SelfWeightMultiplier
    {
        get => selfWeightMultiplier;
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentException("Enter a finite self-weight multiplier.");
            Set(ref selfWeightMultiplier, value);
        }
    }
    /// <summary>What the pattern is used for now, and the loads it carries (<see cref="FemLoadPlan.Describe"/>).</summary>
    public string Role { get => role; set => Set(ref role, value); }
    public string Loads { get => loads; set => Set(ref loads, value); }
}

public abstract class FemDefinitionRow(string name, string type, string definition, bool run, bool supported, string source, string note) : Observable
{
    private string name = name;
    private string definition = definition;
    private bool run = run && supported;
    /// <summary>Set by the owner of the table: what is wrong with a new name for this row (taken, …), or null.</summary>
    public Func<FemDefinitionRow, string, string?>? NameProblem { get; set; }
    /// <summary>Set by the owner of the table: called after a rename with the previous name, so references can follow.</summary>
    public Action<FemDefinitionRow, string>? Renamed { get; set; }
    public string Name
    {
        get => name;
        set
        {
            var next = (value ?? "").Trim();
            if (next == name) return;
            if ((FemLoadPlan.NameProblem(next) ?? NameProblem?.Invoke(this, next)) is { } problem) throw new ArgumentException(problem);
            var previous = name;
            Set(ref name, next);
            Renamed?.Invoke(this, previous);
        }
    }
    public string Type { get; } = type;
    /// <summary>Factored list, e.g. "1.2 DEAD + 1.5 LIVE". Names are checked when the FEM run starts.</summary>
    public string Definition
    {
        get => definition;
        set
        {
            FemLoadDefinition.Parse(value); // syntax check; throws for the binding's validation
            Set(ref definition, value.Trim());
        }
    }
    public bool Run { get => run; set => Set(ref run, value && Supported); }
    public bool Supported { get; } = supported;
    /// <summary>"SAP" when read from the model, "App" for a row the app added.</summary>
    public string Source { get; } = source;
    public string Note { get; } = note;
}

public sealed class FemCaseRow(string name, string type, string definition, string? initialCase, bool run, bool supported, string source, string note)
    : FemDefinitionRow(name, type, definition, run, supported, source, note)
{
    private string initialCase = FemLoadPlan.IsNoInitialCase(initialCase) ? "" : initialCase!.Trim();
    private bool cableTargetForces;
    public bool CableTargetForces { get => cableTargetForces; set => Set(ref cableTargetForces, value); }
    /// <summary>Nonlinear static case it continues from (its loads are included cumulatively); empty when it starts unstressed.</summary>
    public string InitialCase
    {
        get => initialCase;
        set
        {
            var next = FemLoadPlan.IsNoInitialCase(value) ? "" : value.Trim();
            if (next.Equals(Name, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A case cannot continue from itself.");
            Set(ref initialCase, next);
        }
    }
}

public sealed class FemCombinationRow(string name, string type, string definition, bool run, bool supported, string source, string note)
    : FemDefinitionRow(name, type, definition, run, supported, source, note);

/// <summary>"1.2 DEAD + 1.5 LIVE" ⇄ [(DEAD, 1.2), (LIVE, 1.5)]. A term without a factor means 1; "−" subtracts.</summary>
public static class FemLoadDefinition
{
    public static IReadOnlyList<(string Name, double Factor)> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter at least one term, e.g. 1.2 DEAD + 1.5 LIVE.");
        var terms = new List<(string, double)>();
        var normalized = text.Replace("−", "-").Trim();
        foreach (var raw in SplitTerms(normalized))
        {
            var sign = raw.Sign;
            var term = raw.Text.Trim();
            if (term.Length == 0) throw new ArgumentException($"Empty term in '{text}'.");
            var space = term.IndexOfAny([' ', '*', '×']);
            double factor = 1;
            var name = term;
            if (space > 0 && double.TryParse(term[..space], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                factor = parsed;
                name = term[(space + 1)..].TrimStart(' ', '*', '×');
            }
            if (name.Length == 0) throw new ArgumentException($"Term '{term}' has no pattern/case name.");
            terms.Add((name, sign * factor));
        }
        return terms;
    }

    private static IEnumerable<(double Sign, string Text)> SplitTerms(string text)
    {
        // Split on " + " / " - " separators (names may contain '-' without surrounding spaces).
        var sign = 1.0;
        var start = 0;
        if (text.StartsWith('-')) { sign = -1; start = 1; }
        for (var index = start; index < text.Length; index++)
        {
            if ((text[index] == '+' || text[index] == '-') && index > start && text[index - 1] == ' ' && index + 1 < text.Length && text[index + 1] == ' ')
            {
                yield return (sign, text[start..index]);
                sign = text[index] == '-' ? -1 : 1;
                start = index + 1;
            }
        }
        yield return (sign, text[start..]);
    }

    public static string Format(IEnumerable<(string Name, double Factor)> terms) => string.Join(" + ",
        terms.Select(term => $"{term.Factor.ToString("0.###", CultureInfo.InvariantCulture)} {term.Name}"));
}

// ---------------------------------------------------------------------------------------------------------------
// Load plan: the editable rows resolved into per-candidate solver input (case loads + fit stage) and combinations.
// ---------------------------------------------------------------------------------------------------------------

public sealed class FemLoadPlan
{
    private readonly IReadOnlyDictionary<string, FemLoadSet> patternLoads;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<(string Pattern, double Factor)>> resolvedCases;
    private readonly IReadOnlyList<(string Name, IReadOnlyList<(string Name, bool IsCombination, double Factor)> Items)> combinations;
    private readonly IReadOnlySet<string> tab01Patterns;
    private readonly IReadOnlySet<string> pretensionPatterns;
    private readonly IReadOnlyDictionary<string, string?> fitStageOf;

    public IReadOnlyList<string> CaseNames { get; }
    public IReadOnlyList<string> CombinationNames { get; }
    /// <summary>Case whose loads (other than the cable target-force pattern) act while the pretension is fitted —
    /// the first of <see cref="FitCases"/>.</summary>
    public string? FitCase { get; }
    /// <summary>Every pretension stage of the run. SAP meets a Target Force at the end of the case that applies the
    /// target-force pattern, so each such case is a stage of its own, and a case that continues from one (directly
    /// or through other cases) carries the pretension fitted in that stage — not in some other case.</summary>
    public IReadOnlyList<string> FitCases { get; }
    public IReadOnlyList<string> Notes { get; }
    /// <summary>Every result name in display order: run cases, then run combinations.</summary>
    public IReadOnlyList<string> ResultNames => [.. CaseNames, .. CombinationNames];

    private FemLoadPlan(IReadOnlyDictionary<string, FemLoadSet> patternLoads,
        IReadOnlyDictionary<string, IReadOnlyList<(string, double)>> resolvedCases, IReadOnlyList<string> caseNames,
        IReadOnlyList<(string, IReadOnlyList<(string, bool, double)>)> combinations, string? fitCase,
        IReadOnlySet<string> tab01Patterns, IReadOnlySet<string> pretensionPatterns, IReadOnlyList<string> notes,
        IReadOnlyDictionary<string, string?> fitStageOf)
    {
        this.fitStageOf = fitStageOf;
        FitCases = caseNames.Select(name => fitStageOf[name]).Where(stage => stage is not null).Select(stage => stage!)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(stage => string.Equals(stage, fitCase, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
        this.patternLoads = patternLoads;
        this.resolvedCases = resolvedCases;
        CaseNames = caseNames;
        this.combinations = combinations;
        CombinationNames = combinations.Select(item => item.Item1).ToArray();
        FitCase = fitCase;
        this.tab01Patterns = tab01Patterns;
        this.pretensionPatterns = pretensionPatterns;
        Notes = notes;
    }

    /// <summary>
    /// Builds the plan from the edited rows. Self-weight uses each member's γ·A·weight modifier; SAP joint forces in a
    /// Tab 01 pattern at the Tab 01 applied points are replaced per candidate by the Tab 01 loads (as the SAP study
    /// writes them); loads on joints or members outside the analysed model are dropped with a note.
    /// </summary>
    public static FemLoadPlan Build(
        IReadOnlyList<FemPatternRow> patterns, IReadOnlyList<FemCaseRow> cases, IReadOnlyList<FemCombinationRow> combinationRows,
        SapFemLoading sap, IReadOnlyDictionary<string, FemSectionProperties> sections, GeometryModel geometry,
        IReadOnlyCollection<string> tab01PatternNames, IReadOnlyCollection<string> pretensionPatternNames,
        IReadOnlyCollection<string> appliedPoints)
    {
        var notes = new List<string>();
        var comparer = StringComparer.OrdinalIgnoreCase;
        var patternNames = patterns.Select(pattern => pattern.Name).ToHashSet(comparer);
        var tab01 = tab01PatternNames.ToHashSet(comparer);
        var pretension = pretensionPatternNames.ToHashSet(comparer);
        var applied = appliedPoints.ToHashSet(StringComparer.Ordinal);
        var analysedMembers = geometry.Members.Where(member => member.Kind == MemberKind.Frame ||
            member.Kind == MemberKind.Cable && sections.ContainsKey(member.Name)).ToDictionary(member => member.Name, StringComparer.Ordinal);
        var analysedPoints = analysedMembers.Values.SelectMany(member => new[] { member.Start, member.End }).ToHashSet(StringComparer.Ordinal);

        // Pattern load sets.
        var loads = new Dictionary<string, FemLoadSet>(comparer);
        var dropped = 0;
        foreach (var pattern in patterns)
        {
            var joints = sap.JointLoads.Where(item => comparer.Equals(item.Pattern, pattern.Name))
                .Where(item => !(tab01.Contains(pattern.Name) && applied.Contains(item.Load.PointName)))
                .Select(item => item.Load).ToList();
            var members = sap.MemberLoads.Where(item => comparer.Equals(item.Pattern, pattern.Name)).Select(item => item.Load).ToList();
            var points = sap.MemberPointLoads.Where(item => comparer.Equals(item.Pattern, pattern.Name)).Select(item => item.Load).ToList();
            foreach (var gravity in sap.GravityLoads.Where(item => comparer.Equals(item.Pattern, pattern.Name)))
            {
                var weight = sections.TryGetValue(gravity.MemberName, out var section) ? section.WeightPerLengthKNPerM : 0;
                if (gravity.MultiplierX != 0) members.Add(new(gravity.MemberName, FemLoadDirection.GlobalX, 0, 1, gravity.MultiplierX * weight, gravity.MultiplierX * weight));
                if (gravity.MultiplierZ != 0) members.Add(new(gravity.MemberName, FemLoadDirection.GlobalZ, 0, 1, gravity.MultiplierZ * weight, gravity.MultiplierZ * weight));
            }
            if (pattern.SelfWeightMultiplier != 0)
                foreach (var member in analysedMembers.Values)
                    if (sections.TryGetValue(member.Name, out var section) && section.WeightPerLengthKNPerM > 0)
                    {
                        var w = pattern.SelfWeightMultiplier * section.WeightPerLengthKNPerM;
                        members.Add(new(member.Name, FemLoadDirection.Gravity, 0, 1, w, w));
                    }
            dropped += joints.RemoveAll(load => !analysedPoints.Contains(load.PointName));
            dropped += members.RemoveAll(load => !analysedMembers.ContainsKey(load.MemberName));
            dropped += points.RemoveAll(load => !analysedMembers.TryGetValue(load.MemberName, out var member) || member.Kind != MemberKind.Frame);
            loads[pattern.Name] = new FemLoadSet(joints, members, points);
        }
        if (dropped > 0) notes.Add($"{dropped} SAP load assignment(s) on joints/members outside the analysed 2D model were ignored.");

        // Cases (definitions edited in the app), resolved cumulatively through nonlinear initial cases.
        var caseRows = cases.ToDictionary(row => row.Name, comparer);
        var resolved = new Dictionary<string, IReadOnlyList<(string, double)>>(comparer);
        var ownTerms = new Dictionary<string, IReadOnlyList<(string Pattern, double Factor)>>(comparer);
        IReadOnlyList<(string, double)> Resolve(string name, HashSet<string> visiting)
        {
            if (resolved.TryGetValue(name, out var done)) return done;
            if (!caseRows.TryGetValue(name, out var row)) throw new InvalidOperationException($"Load case '{name}' is not defined.");
            if (!row.Supported) throw new InvalidOperationException($"Load case '{name}' ({row.Type}) is not supported by the internal FEM.");
            if (!visiting.Add(name)) throw new InvalidOperationException($"Load case '{name}' has a circular initial-case chain.");
            var terms = FemLoadDefinition.Parse(row.Definition).Select(term =>
                patternNames.Contains(term.Name) ? (patterns.First(pattern => comparer.Equals(pattern.Name, term.Name)).Name, term.Factor)
                    : throw new InvalidOperationException($"Load case '{name}' refers to unknown load pattern '{term.Name}'.")).ToList();
            ownTerms[name] = terms.ToArray();
            if (!IsNoInitialCase(row.InitialCase)) terms.InsertRange(0, Resolve(row.InitialCase, visiting));
            visiting.Remove(name);
            return resolved[name] = terms;
        }

        // Combinations; the cases they need are solved even when not ticked.
        var combinationLookup = combinationRows.ToDictionary(row => row.Name, comparer);
        var neededCases = cases.Where(row => row.Run).Select(row => row.Name).ToList();
        var runCombinations = new List<(string, IReadOnlyList<(string, bool, double)>)>();
        var parsedCombinations = new Dictionary<string, IReadOnlyList<(string, bool, double)>>(comparer);
        IReadOnlyList<(string, bool, double)> ResolveCombination(string name, HashSet<string> visiting)
        {
            if (parsedCombinations.TryGetValue(name, out var done)) return done;
            var row = combinationLookup[name];
            if (!row.Supported) throw new InvalidOperationException($"Load combination '{name}' ({row.Type}) is not supported; only linear additive combinations are.");
            if (!visiting.Add(name)) throw new InvalidOperationException($"Load combination '{name}' refers to itself.");
            var items = FemLoadDefinition.Parse(row.Definition).Select(term =>
            {
                if (caseRows.TryGetValue(term.Name, out var caseRow))
                {
                    if (!neededCases.Contains(caseRow.Name, comparer)) neededCases.Add(caseRow.Name);
                    return (caseRow.Name, false, term.Factor);
                }
                if (combinationLookup.TryGetValue(term.Name, out var nested))
                {
                    ResolveCombination(nested.Name, visiting);
                    return (nested.Name, true, term.Factor);
                }
                throw new InvalidOperationException($"Load combination '{name}' refers to unknown case or combination '{term.Name}'.");
            }).ToArray();
            visiting.Remove(name);
            return parsedCombinations[name] = items;
        }
        foreach (var row in combinationRows.Where(row => row.Run))
            runCombinations.Add((row.Name, ResolveCombination(row.Name, new HashSet<string>(comparer))));
        // Nested combinations that are not ticked are still evaluated (before their parents).
        var orderedCombinations = new List<(string, IReadOnlyList<(string, bool, double)>)>();
        void AddCombination(string name)
        {
            if (orderedCombinations.Any(item => comparer.Equals(item.Item1, name))) return;
            foreach (var item in parsedCombinations[name].Where(item => item.Item2)) AddCombination(item.Item1);
            orderedCombinations.Add((combinationLookup[name].Name, parsedCombinations[name]));
        }
        foreach (var (name, _) in runCombinations) AddCombination(name);

        if (neededCases.Count == 0) throw new InvalidOperationException("Tick at least one load case or combination to run on FEM → Inputs → Loads.");
        foreach (var name in neededCases) Resolve(name, new HashSet<string>(comparer));

        foreach (var name in neededCases.Where(name => caseRows[name].CableTargetForces))
        {
            var factors = resolved[name].Where(term => pretension.Contains(term.Item1)).Select(term => term.Item2).Distinct().ToArray();
            if (factors.Length > 1)
                throw new InvalidOperationException($"Load case '{name}' scales the cable target-force patterns by different factors; use one factor.");
        }
        // As SAP meets a Target Force at the end of the case in which the target-force pattern is applied, the fit stage
        // is the case whose own definition holds that pattern (with whatever other loads that case has); cases that
        // continue from it carry the fitted pretension cumulatively. Prefer the one with the fewest other loads.
        var fitCase = resolved.Keys
            .Where(name => caseRows[name].CableTargetForces && ownTerms[name].Any(term => pretension.Contains(term.Pattern)))
            .OrderBy(name => ownTerms[name].Count(term => !pretension.Contains(term.Pattern)))
            .ThenBy(name => neededCases.FindIndex(needed => comparer.Equals(needed, name)) is var index && index >= 0 ? index : int.MaxValue)
            .FirstOrDefault();
        if (fitCase is null && pretension.Count > 0)
            notes.Add("No load case that will run includes a Tab 03 target-force pattern, so no case carries the cable pretension.");
        if (tab01.Count > 0 && !neededCases.Any(name => resolved[name].Any(term => tab01.Contains(term.Item1))))
            notes.Add("No load case that will run includes a Tab 01 load pattern, so the Tab 01 candidate loads do not affect the results.");

        // The stage a case takes its pretension from: itself when its own definition applies the target-force pattern,
        // otherwise the nearest case it continues from that does. None = the case carries no cable pretension.
        var stages = new Dictionary<string, string?>(comparer);
        string? StageOf(string name)
        {
            if (stages.TryGetValue(name, out var known)) return known;
            var row = caseRows[name];
            if (row.CableTargetForces && !ownTerms[name].Any(term => pretension.Contains(term.Pattern)))
                throw new InvalidOperationException($"Cable Target Forces case '{name}' must include a cable target-force load pattern.");
            return stages[name] = row.CableTargetForces ? row.Name
                : IsNoInitialCase(row.InitialCase) ? null : StageOf(row.InitialCase);
        }
        foreach (var name in resolved.Keys.ToArray()) StageOf(name);
        if (stages.Values.Where(stage => stage is not null).Distinct(comparer).Count() > 1)
            notes.Add("Several load cases apply the cable target-force pattern; each is its own pretension stage, and a case uses the stage it continues from.");

        return new FemLoadPlan(loads, resolved, neededCases, orderedCombinations, fitCase,
            tab01, pretension, notes, stages);
    }

    /// <summary>SAP reports "start from zero / unstressed" as an empty name, "None" or "Zero".</summary>
    public static bool IsNoInitialCase(string? name) => string.IsNullOrWhiteSpace(name) ||
        name.Trim().Equals("None", StringComparison.OrdinalIgnoreCase) || name.Trim().Equals("Zero", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Solver input for one candidate, one group per pretension stage: the loads acting while that stage's pretension
    /// is fitted, and the cases that carry it (the stage itself and every case continuing from it). Cases without
    /// cable pretension ride with the first group. Each group is one <c>SolveCases</c> call.
    /// </summary>
    public IReadOnlyList<(string? FitCase, FemLoadSet FitLoads, IReadOnlyList<FemCaseLoads> Cases)> StagesFor(IReadOnlyList<FemAppliedLoad> candidateLoads)
    {
        var (_, all) = ForCandidate(candidateLoads);
        if (FitCases.Count == 0) return [(null, FemLoadSet.Empty, all)];
        return FitCases.Select((stage, index) => ((string?)stage, ForCandidate(candidateLoads, stage).FitLoads,
            (IReadOnlyList<FemCaseLoads>)all.Where(item => fitStageOf[item.Name] is { } own
                ? string.Equals(own, stage, StringComparison.OrdinalIgnoreCase) : index == 0).ToArray()))
            .Where(group => group.Item3.Count > 0).ToArray();
    }

    /// <summary>Solver input for one candidate with a single pretension stage (<see cref="FitCase"/>): fit-stage loads
    /// and every case with the candidate's Tab 01 loads. A run with several stages uses <see cref="StagesFor"/>.</summary>
    public (FemLoadSet FitLoads, IReadOnlyList<FemCaseLoads> Cases) ForCandidate(IReadOnlyList<FemAppliedLoad> candidateLoads) =>
        ForCandidate(candidateLoads, FitCase);

    private (FemLoadSet FitLoads, IReadOnlyList<FemCaseLoads> Cases) ForCandidate(IReadOnlyList<FemAppliedLoad> candidateLoads, string? fitCase)
    {
        FemLoadSet PatternSet(string pattern)
        {
            var set = patternLoads[pattern];
            if (!tab01Patterns.Contains(pattern)) return set;
            var extra = candidateLoads.Where(load => string.Equals(load.Pattern, pattern, StringComparison.OrdinalIgnoreCase))
                .Select(load => new FemJointLoad(load.PointName, load.FxKN, load.FzKN)).ToArray();
            return set with { Joints = [.. set.Joints, .. extra] };
        }
        FemLoadSet CaseSet(string name) => FemLoadSet.Sum(resolvedCases[name]
            .Where(term => !pretensionPatterns.Contains(term.Pattern))
            .Select(term => PatternSet(term.Pattern).Scaled(term.Factor)));
        double PretensionFactor(string name) => fitStageOf[name] is not { } stage ? 0 : resolvedCases[stage].Where(term => pretensionPatterns.Contains(term.Pattern))
            .Select(term => term.Factor).FirstOrDefault();

        var fit = fitCase is null ? FemLoadSet.Empty : CaseSet(fitCase);
        var caseLoads = CaseNames.Select(name => new FemCaseLoads(name, CaseSet(name), PretensionFactor(name))).ToArray();
        return (fit, caseLoads);
    }

    /// <summary>Adds the combinations (linear add of case / nested combination results) to the case results.</summary>
    public IReadOnlyDictionary<string, FemAnalysisResult> Complete(IReadOnlyDictionary<string, FemAnalysisResult> caseResults)
    {
        var all = new Dictionary<string, FemAnalysisResult>(caseResults, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, items) in combinations)
            all[name] = FemResultCombiner.Combine(items.Select(item => (all[item.Name], item.Factor)).ToArray());
        return all;
    }

    // ---- Default setup: the load cases SAP2000 needs for a cable structure, defined by the app ----
    public const string DefaultPretensionCase = "Pretension";
    public const string DefaultServiceCase = "SLS: 1.0DL+1.0LL";
    public const string DefaultUltimateCase = "ULS: 1.35DL+1.5LL";
    public const string DefaultSelfWeightPattern = "SW";
    public const string StagedCaseType = "Nonlinear Static";
    public const double UltimateDeadFactor = 1.35, UltimateLiveFactor = 1.5;
    private const string Tab01Role = "Tab 01 loads", PretensionRole = "Tab 03 cable target force", SelfWeightRole = "Self-weight";

    /// <summary>The problem with a name for a load pattern or case, or null. ' + ' and ' - ' separate definition terms.</summary>
    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Enter a name."
        : name.Contains(" + ", StringComparison.Ordinal) || name.Contains(" - ", StringComparison.Ordinal)
            ? "A name cannot contain ' + ' or ' - ': they separate the terms of a definition."
        : null;

    /// <summary>
    /// The load case setup of a SAP2000 model of a cable structure, as the app's own default — the same staging, solved by
    /// the internal FEM. The cables come first: <c>Pretension</c> is a nonlinear static case with the cable target forces
    /// and the self-weight, where the targets are met. The loaded cases continue from that state: <c>SLS</c> adds the
    /// Tab 01 loads (1.0 each), <c>ULS</c> adds them factored (1.35 dead, 1.5 live) plus 0.35 of the self-weight, which
    /// takes the 1.0 already on the structure to 1.35. Patterns the table does not list yet are added: the Tab 01 and
    /// Tab 03 patterns, and a self-weight pattern when none carries self-weight.
    /// </summary>
    public static (IReadOnlyList<FemPatternRow> Patterns, IReadOnlyList<FemCaseRow> Cases) DefaultSetup(
        IReadOnlyList<FemPatternRow> patterns, IReadOnlyCollection<string> tab01PatternNames, IReadOnlyCollection<string> pretensionPatternNames)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var rows = patterns.ToList();
        bool Listed(string name) => rows.Any(row => comparer.Equals(row.Name, name));
        foreach (var name in pretensionPatternNames.Where(name => !Listed(name)).ToArray())
            rows.Add(new FemPatternRow(name, "Other", 0, PretensionRole, "target forces"));
        foreach (var name in tab01PatternNames.Where(name => !Listed(name)).ToArray())
            rows.Add(new FemPatternRow(name, IsLive("", name) ? "Live" : "Dead", 0, Tab01Role, "Tab 01 loads"));
        if (rows.All(row => row.SelfWeightMultiplier == 0))
            rows.Insert(0, new FemPatternRow(Unique(DefaultSelfWeightPattern, rows.Select(row => row.Name)), "Dead", 1, SelfWeightRole, "self-weight"));

        var cases = new List<FemCaseRow>();
        var stage = PretensionCase(rows, tab01PatternNames, pretensionPatternNames, []);
        if (stage is not null) cases.Add(stage);
        cases.AddRange(LoadedCases(rows, tab01PatternNames, pretensionPatternNames, stage?.Name, stage is null ? 0 : 1, cases.Select(row => row.Name)));
        return (rows, cases);
    }

    /// <summary>What a load pattern is used for and the loads it carries now: the loads saved with the model (joint,
    /// member), the self-weight, the loads of 01 Loading and the cable target forces of 03 Pretension.</summary>
    public static (string Role, string Loads) Describe(SapFemLoading saved, string pattern, double selfWeightMultiplier,
        IReadOnlyCollection<string> tab01PatternNames, IReadOnlyCollection<string> pretensionPatternNames,
        IReadOnlyCollection<string>? appliedPointNames = null)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var tab01 = tab01PatternNames.Contains(pattern, comparer);
        var pretension = pretensionPatternNames.Contains(pattern, comparer);
        var joints = saved.JointLoads.Count(item => comparer.Equals(item.Pattern, pattern) &&
            !(tab01 && appliedPointNames?.Contains(item.Load.PointName, StringComparer.Ordinal) == true));
        var members = saved.MemberLoads.Count(item => comparer.Equals(item.Pattern, pattern)) +
            saved.MemberPointLoads.Count(item => comparer.Equals(item.Pattern, pattern)) +
            saved.GravityLoads.Count(item => comparer.Equals(item.Pattern, pattern));
        var loads = string.Join(" · ", new[]
        {
            joints > 0 ? $"{joints} SAP joint assignments" : null, members > 0 ? $"{members} member assignments" : null,
            selfWeightMultiplier != 0 ? "self-weight" : null,
            tab01 ? appliedPointNames is null ? "VCF storey loads" : $"VCF storey loads: {appliedPointNames.Count} joints" : null,
            pretension ? "cable target forces" : null
        }.Where(item => item is not null));
        return (tab01 ? Tab01Role : pretension ? PretensionRole : selfWeightMultiplier != 0 ? SelfWeightRole : "", loads.Length == 0 ? "none" : loads);
    }

    /// <summary>The pattern that carries the self-weight (not a Tab 01 or Tab 03 pattern), or null.</summary>
    private static FemPatternRow? SelfWeightPattern(IReadOnlyList<FemPatternRow> patterns, IReadOnlyCollection<string> tab01, IReadOnlyCollection<string> pretension) =>
        patterns.FirstOrDefault(row => row.SelfWeightMultiplier != 0 &&
            !tab01.Contains(row.Name, StringComparer.OrdinalIgnoreCase) && !pretension.Contains(row.Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>Live load by the pattern type, else by its name (LL…, …LIVE…); anything else is factored as dead load.</summary>
    private static bool IsLive(string type, string name) =>
        type.Contains("Live", StringComparison.OrdinalIgnoreCase) ||
        !type.Contains("Dead", StringComparison.OrdinalIgnoreCase) &&
        (name.StartsWith("LL", StringComparison.OrdinalIgnoreCase) || name.Contains("LIVE", StringComparison.OrdinalIgnoreCase));

    private static string Unique(string name, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = name;
        for (var number = 2; used.Contains(unique); number++) unique = $"{name} ({number})";
        return unique;
    }

    /// <summary>The pretension stage: the target-force patterns with the self-weight, or null without such a pattern.</summary>
    private static FemCaseRow? PretensionCase(IReadOnlyList<FemPatternRow> patterns, IReadOnlyCollection<string> tab01,
        IReadOnlyCollection<string> pretension, IEnumerable<string> takenNames)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var terms = patterns.Where(row => pretension.Contains(row.Name, comparer)).Select(row => (row.Name, 1.0)).ToList();
        if (terms.Count == 0) return null;
        if (SelfWeightPattern(patterns, tab01, pretension) is { } selfWeight) terms.Add((selfWeight.Name, 1.0));
        return new FemCaseRow(Unique(DefaultPretensionCase, takenNames), StagedCaseType, FemLoadDefinition.Format(terms), null, true, true, "App",
            "Cable target forces met here, under the self-weight.") { CableTargetForces = true };
    }

    /// <summary>SLS and ULS with the Tab 01 loads, continuing from <paramref name="stage"/> (the pretension case), which
    /// already has <paramref name="carriedSelfWeight"/> × the self-weight on the structure.</summary>
    private static IEnumerable<FemCaseRow> LoadedCases(IReadOnlyList<FemPatternRow> patterns, IReadOnlyCollection<string> tab01,
        IReadOnlyCollection<string> pretension, string? stage, double carriedSelfWeight, IEnumerable<string> takenNames)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var loads = patterns.Where(row => tab01.Contains(row.Name, comparer)).ToArray();
        var selfWeight = SelfWeightPattern(patterns, tab01, pretension);
        var taken = takenNames.ToList();
        FemCaseRow? Case(string name, double total, double dead, double live, string limitState)
        {
            var own = Math.Round(total - carriedSelfWeight, 9);
            var terms = new List<(string, double)>();
            if (selfWeight is not null && own != 0) terms.Add((selfWeight.Name, own));
            terms.AddRange(loads.Select(row => (row.Name, IsLive(row.Type, row.Name) ? live : dead)));
            if (terms.Count == 0) return null;
            var unique = Unique(name, taken);
            taken.Add(unique);
            var weight = selfWeight is null || own == 0 || carriedSelfWeight == 0 ? ""
                : $" · self-weight {carriedSelfWeight:0.##} + {own:0.##} = {total:0.##}";
            return new FemCaseRow(unique, StagedCaseType, FemLoadDefinition.Format(terms), stage, true, true, "App",
                (stage is null ? limitState : $"{limitState}, continues from {stage}") + weight);
        }
        if (Case(DefaultServiceCase, 1, 1, 1, "Service") is { } service) yield return service;
        if (Case(DefaultUltimateCase, UltimateDeadFactor, UltimateDeadFactor, UltimateLiveFactor, "Ultimate") is { } ultimate) yield return ultimate;
    }

    /// <summary>
    /// Editable rows from a SAP read. What SAP's own cases leave out is added as in <see cref="DefaultSetup"/>: when no
    /// SAP case includes the Tab 03 target-force pattern, the <c>Pretension</c> case (that pattern with the self-weight);
    /// when none includes a Tab 01 pattern, <c>SLS</c> and <c>ULS</c> continuing from the pretension case — SAP's or the
    /// added one — so the Tab 01 loads always reach the results on the prestressed structure.
    /// </summary>
    public static (IReadOnlyList<FemPatternRow> Patterns, IReadOnlyList<FemCaseRow> Cases, IReadOnlyList<FemCombinationRow> Combinations)
        RowsFromSap(SapFemLoading sap, IReadOnlyCollection<string> tab01PatternNames, IReadOnlyCollection<string> pretensionPatternNames)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var tab01 = tab01PatternNames.ToHashSet(comparer);
        var pretension = pretensionPatternNames.ToHashSet(comparer);
        var patterns = sap.Patterns.Select(pattern =>
        {
            var (role, loads) = Describe(sap, pattern.Name, pattern.SelfWeightMultiplier, tab01, pretension);
            return new FemPatternRow(pattern.Name, pattern.Type, pattern.SelfWeightMultiplier, role, loads);
        }).ToList();
        var cases = sap.Cases.Select(item => new FemCaseRow(item.Name, item.Type,
            item.Loads.Count > 0 ? FemLoadDefinition.Format(item.Loads) : "", item.InitialCase,
            run: item.Supported && item.Loads.Count > 0, item.Supported && item.Loads.Count > 0, "SAP", item.Note ?? "")
            { CableTargetForces = item.Loads.Any(load => pretension.Contains(load.Pattern)) }).ToList();
        var combinations = sap.Combinations.Select(item => new FemCombinationRow(item.Name, item.Type,
            FemLoadDefinition.Format(item.Items.Select(term => (term.Name, term.Factor))), run: item.Supported, item.Supported, "SAP", item.Note ?? "")).ToList();

        SapLoadCase? FirstCaseWith(HashSet<string> names) =>
            sap.Cases.FirstOrDefault(item => item.Supported && item.Loads.Any(load => names.Contains(load.Pattern)));
        // Pretension stage: SAP's own case with the target-force pattern, or the default one (that pattern + self-weight).
        var sapStage = pretension.Count > 0 ? FirstCaseWith(pretension) : null;
        var stage = sapStage?.Name;
        var selfWeight = SelfWeightPattern(patterns, tab01, pretension);
        var carried = sapStage is null || selfWeight is null ? 0
            : sapStage.Loads.Where(load => comparer.Equals(load.Pattern, selfWeight.Name)).Sum(load => load.Factor);
        if (sapStage is null && PretensionCase(patterns, tab01, pretension, cases.Select(row => row.Name)) is { } added)
        {
            cases.Add(added);
            stage = added.Name;
            carried = selfWeight is null ? 0 : 1;
        }
        // Tab 01 loads continue from the pretension stage (prestressed, then loaded), as staged nonlinear cases.
        if (tab01.Count > 0 && FirstCaseWith(tab01) is null)
            cases.AddRange(LoadedCases(patterns, tab01, pretension, stage, carried, cases.Select(row => row.Name)).ToArray());
        return (patterns, cases, combinations);
    }
}
