using System.Globalization;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application;

/// <summary>One case of a parametric study made into a system to analyse: what its generator built for the case's
/// parameters, with the case's sections and loads, the joints its deflection and uplift are read on and its span.</summary>
/// <param name="CheckedWhat">What <paramref name="CheckedJoints"/> are, for the message of a case without them
/// ("bottom chord point", "top chord joint").</param>
public sealed record ParametricCandidate(
    string Name,
    GeometryModel Geometry,
    IReadOnlyDictionary<string, FemSectionProperties> Properties,
    FemCandidateSetup Setup,
    IReadOnlyList<FemAppliedLoad> Loads,
    IReadOnlyDictionary<string, double> Targets,
    IReadOnlyDictionary<string, string>? TargetPatterns,
    IReadOnlyCollection<string> CheckedJoints,
    string CheckedWhat,
    double SpanM,
    IReadOnlyDictionary<string, double>? EffectiveLengthsM = null,
    IReadOnlyDictionary<string, string>? MemberRoles = null);

/// <summary>A case that is no system — its geometry is impossible — with the reason, which its row shows.</summary>
public sealed class ParametricCandidateException(string reason) : Exception(reason);

/// <summary>
/// One configuration of <see cref="ParametricOptimization"/> (task of 2026-10-14, §2): the search space of a generator's
/// system and how a case of it becomes a candidate. Optimization VCF (<see cref="VcfOptimizationStudy"/>) and Optimization
/// TAS (<see cref="TasOptimizationStudy"/>) are two; a generator becomes optimizable by giving one.
/// </summary>
public interface IParametricStudy
{
    IReadOnlyList<VcfOptimizationAxis> Axes { get; }
    long Count { get; }
    /// <summary>The candidate of <paramref name="item"/>; throws <see cref="ParametricCandidateException"/> for a case that
    /// is no system.</summary>
    ParametricCandidate Candidate(VcfOptimizationCase item);
}

/// <summary>What the cases are checked against: the result the deflection is read in (null: every solved result), the
/// limit span / ratio, and the uplift limit [mm].</summary>
public sealed record OptimizationCriteria(string? DeflectionResult, double SpanRatio, double UpliftLimitMm);

/// <summary>A criterion of the study: it reads the analysed candidate and adds its verdict to the case's result. The
/// engine runs them in order; one that finds no answer returns the result with its <see cref="VcfOptimizationResult.Failure"/>.</summary>
public interface IOptimizationEvaluator
{
    VcfOptimizationResult Evaluate(VcfOptimizationResult result, ParametricCandidate candidate, IReadOnlyDictionary<string, FemAnalysisResult> results);
}

/// <summary>The serviceability check: the largest downward deflection and the largest uplift on the candidate's checked
/// joints, over the results the criteria name, against span / ratio and the uplift limit.</summary>
public sealed class DeflectionEvaluator(OptimizationCriteria criteria) : IOptimizationEvaluator
{
    public VcfOptimizationResult Evaluate(VcfOptimizationResult result, ParametricCandidate candidate, IReadOnlyDictionary<string, FemAnalysisResult> results)
    {
        var joints = candidate.CheckedJoints as IReadOnlySet<string> ?? candidate.CheckedJoints.ToHashSet(StringComparer.Ordinal);
        var checkedJoints = results
            .SelectMany(pair => pair.Value.Joints.Where(joint => joints.Contains(joint.PointName)).Select(joint => (Name: pair.Key, Point: joint.PointName, Mm: -joint.UZM * 1000)))
            .ToArray();
        if (checkedJoints.Length == 0)
            return ParametricOptimization.Failed(result.Case, $"No {candidate.CheckedWhat} has a displacement to check.") with { CostSgd = result.CostSgd, CostNote = result.CostNote };
        var (name, point, deflection) = checkedJoints.MaxBy(entry => entry.Mm);
        var (_, upliftPoint, risen) = checkedJoints.MinBy(entry => entry.Mm);
        var uplift = -risen;
        return result with
        {
            MaxDeflectionMm = deflection, GoverningResult = name, Point = point,
            SpanM = candidate.SpanM, LimitMm = candidate.SpanM * 1000 / criteria.SpanRatio,
            MaxUpliftMm = Math.Max(0, uplift), UpliftPoint = uplift > 0 ? upliftPoint : null, UpliftLimitMm = criteria.UpliftLimitMm,
        };
    }
}

/// <summary>The structural responses of the governing result (<see cref="OptimizationResponses"/>): base reactions, the
/// largest frame forces and cable tension, the mass.</summary>
public sealed class ResponseEvaluator : IOptimizationEvaluator
{
    public VcfOptimizationResult Evaluate(VcfOptimizationResult result, ParametricCandidate candidate, IReadOnlyDictionary<string, FemAnalysisResult> results) =>
        result.GoverningResult is { } name && results.TryGetValue(name, out var governing)
            ? result with { Response = OptimizationResponses.Of(candidate.Geometry, candidate.Properties, governing) }
            : result;
}

/// <summary>
/// The parametric study engine (task of 2026-10-14, §2): the cases of a study's search space, each built by its generator
/// (<see cref="IParametricStudy.Candidate"/>), costed, analysed with the general solver through the same document path as
/// Run FEM Analysis (<see cref="FemStudyService.AnalyzeCandidate"/>) — no solver of its own — and judged by its
/// evaluators. The open model is never touched: every candidate is a document of its own.
/// </summary>
public sealed class ParametricOptimization : IOptimizationRunner
{
    /// <summary>The most cases one run takes.</summary>
    public const int MaxCases = 10_000;
    /// <summary>The axis the cable forces step on (every cable together).</summary>
    public const string CableStepKey = "cable:step";

    private readonly IParametricStudy study;
    private readonly OptimizationCriteria criteria;
    private readonly Func<FemSectionProperties, (double? SgdPerM, string? Missing)>? costPerMetre;
    private readonly IReadOnlyList<IOptimizationEvaluator> evaluators;
    private readonly FemStudyService fem = new();
    private readonly MemberUtilizationCheck? memberCheck;

    public ParametricOptimization(IParametricStudy study, OptimizationCriteria criteria,
        Func<FemSectionProperties, (double? SgdPerM, string? Missing)>? costPerMetre, IReadOnlyList<IOptimizationEvaluator>? evaluators = null,
        MemberUtilizationCheck? memberCheck = null)
    {
        this.memberCheck = memberCheck;
        this.study = study;
        this.criteria = criteria;
        this.costPerMetre = costPerMetre;
        this.evaluators = evaluators ?? [new DeflectionEvaluator(criteria), new ResponseEvaluator()];
    }

    public IReadOnlyList<VcfOptimizationAxis> Axes => study.Axes;
    public long Count => study.Count;

    /// <summary>Case <paramref name="index"/> (0-based): mixed-radix over the axes, the last axis changing fastest.</summary>
    public VcfOptimizationCase CaseAt(long index)
    {
        var values = new object[Axes.Count];
        var rest = index;
        for (var axis = Axes.Count - 1; axis >= 0; axis--)
        {
            var count = Axes[axis].Values.Count;
            values[axis] = Axes[axis].Values[(int)(rest % count)];
            rest /= count;
        }
        return new(index, values);
    }

    public IEnumerable<VcfOptimizationCase> Cases()
    {
        for (long index = 0; index < Count; index++) yield return CaseAt(index);
    }

    /// <summary>Builds, analyses and judges one case. Never throws for an impossible or non-converging case — the result
    /// says why.</summary>
    public VcfOptimizationResult Run(VcfOptimizationCase item, CancellationToken cancellationToken = default)
    {
        try
        {
            ParametricCandidate candidate;
            try { candidate = study.Candidate(item); }
            catch (ParametricCandidateException ex) { return Failed(item, ex.Message); }
            var (cost, costNote) = Cost(candidate);
            VcfOptimizationResult CaseFailed(string reason) => Failed(item, reason) with { CostSgd = cost, CostNote = costNote };

            FemStudyRun run;
            try
            {
                run = fem.AnalyzeCandidate(candidate.Name, candidate.Geometry, candidate.Properties, candidate.Setup, candidate.Loads,
                    candidate.Targets, candidate.TargetPatterns, cancellationToken);
            }
            catch (InvalidOperationException ex) { return CaseFailed(ex.Message); }
            IReadOnlyDictionary<string, FemAnalysisResult> results = run.Results!;
            var solved = results;       // the member check reads every solved result, the deflection check only the one it names
            if (criteria.DeflectionResult is { } wanted)
            {
                var match = results.Keys.FirstOrDefault(key => string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase));
                if (match is null) return CaseFailed($"'{wanted}' was not solved — tick it to run on Define → Load Cases / Load Combinations.");
                results = new Dictionary<string, FemAnalysisResult> { [match] = results[match] };
            }
            var result = new VcfOptimizationResult(item, null, null, null, results.Values.All(value => value.TargetFitConverged), null,
                CostSgd: cost, CostNote: costNote);
            foreach (var evaluator in evaluators)
            {
                result = evaluator.Evaluate(result, candidate, results);
                if (result.Failure is not null) return result;
            }
            if (memberCheck is not null) result = result with { Members = memberCheck.Evaluate(candidate, solved) };
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return Failed(item, ex.Message);
        }
    }

    internal static VcfOptimizationResult Failed(VcfOptimizationCase item, string reason) => new(item, null, null, null, false, reason);

    /// <summary>Total cost of the candidate's members: Σ cost per metre × member length (frames and cables). Null with a note
    /// naming the sections / materials without a unit cost.</summary>
    private (double? Sgd, string? Note) Cost(ParametricCandidate candidate)
    {
        if (costPerMetre is null) return (null, null);
        double total = 0;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var geometry = candidate.Geometry;
        foreach (var member in geometry.Members)
        {
            if (!candidate.Properties.TryGetValue(member.Name, out var property)) continue;
            var (start, end) = (geometry.Points[member.Start].Position, geometry.Points[member.End].Position);
            var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2) + Math.Pow(end.Z - start.Z, 2));
            var (rate, why) = costPerMetre(property);
            if (rate is { } sgdPerM) total += sgdPerM * length;
            else missing.Add(why ?? property.SectionName ?? member.Name);
        }
        return missing.Count == 0 ? (total, null) : (null, "No unit cost for " + string.Join(", ", missing));
    }

    /// <summary>Text of a case's value on an axis, for the results table.</summary>
    public static string Text(object value) => value switch
    {
        double number => number.ToString("0.###", CultureInfo.CurrentCulture),
        VcfCableStep step => step.ForceKN.ToString("0.###", CultureInfo.CurrentCulture),
        _ => value.ToString() ?? "",
    };
}
