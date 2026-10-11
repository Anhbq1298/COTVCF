using System.Globalization;
using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>One optimization variable: Min, Min + Step, … up to Max — the rule of 01 Loading and 03 Pretension, so a
/// step that does not divide the range stops at the last value below Max (Max itself is not added).</summary>
public sealed record VcfRange(double Min, double Max, double Step)
{
    private const double Tolerance = 1e-9;

    public static VcfRange Fixed(double value, double step) => new(value, value, step);

    public bool IsFixed => Math.Abs(Max - Min) <= Tolerance;

    /// <summary>The problem with the range, or null. <paramref name="whole"/> requires whole numbers (number of bays).</summary>
    public string? Problem(bool whole = false)
    {
        if (!double.IsFinite(Min) || !double.IsFinite(Max) || !double.IsFinite(Step)) return "enter numbers for min, max and step.";
        if (Max < Min - Tolerance) return "max must not be less than min.";
        if (!IsFixed && Step <= Tolerance) return "step must be greater than zero.";
        if (whole && (Min != Math.Floor(Min) || Max != Math.Floor(Max) || (!IsFixed && Step != Math.Floor(Step))))
            return "min, max and step must be whole numbers.";
        return null;
    }

    public int Count => Values().Count;

    public IReadOnlyList<double> Values()
    {
        if (Problem() is not null) return [];
        if (IsFixed) return [Min];
        var values = new List<double>();
        for (var k = 0; ; k++)
        {
            var value = Min + k * Step;
            if (value > Max + Tolerance * Math.Max(1, Math.Abs(Max))) break;
            values.Add(Math.Abs(value - Max) <= 1e-6 * Math.Max(1, Step) ? Max : value);
            if (values.Count > 100_000) break;    // a runaway step: the count shows it, the run refuses it
        }
        return values;
    }

    public override string ToString() => IsFixed
        ? Min.ToString("0.###", CultureInfo.CurrentCulture)
        : $"{Min.ToString("0.###", CultureInfo.CurrentCulture)} … {Max.ToString("0.###", CultureInfo.CurrentCulture)} @ {Step.ToString("0.###", CultureInfo.CurrentCulture)}";
}

/// <summary>
/// Run → Optimization VCF: the search space of the VCF system — a range per geometry parameter (keys as in
/// <see cref="VcfOptimization.Parameters"/>), the pretension range of every independent cable (<see cref="CableForces"/>,
/// kN; a Tab 03 follower keeps following its reference), the storey force inputs (<see cref="StoreyLoads"/>: qz per Tab 01
/// load definition [kPa] by load name, plus <see cref="VcfOptimization.StoriesKey"/> and
/// <see cref="VcfOptimization.TributaryKey"/>) and the candidate sections per member role and per cable. The run loop that
/// walks the space is not built yet; the settings are kept by the view model.
/// </summary>
public sealed record VcfOptimizationSettings(
    IReadOnlyDictionary<string, VcfRange> Geometry,
    IReadOnlyDictionary<string, VcfRange> CableForces,
    IReadOnlyDictionary<string, VcfRange> StoreyLoads,
    IReadOnlyDictionary<VcfMemberRole, IReadOnlyList<string>> RoleSections,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CableSections)
{
    /// <summary>Geometry variants (product of the range counts).</summary>
    public long GeometryCount => Geometry.Values.Aggregate(1L, (product, range) => Saturating(product, range.Count));

    /// <summary>Section variants (product of the candidate counts; a role or cable with none keeps its section).</summary>
    public long SectionCount => RoleSections.Values.Concat(CableSections.Values)
        .Aggregate(1L, (product, names) => Saturating(product, Math.Max(1, names.Count)));

    /// <summary>The load case / combination whose deflection is reported and checked (null: the largest over every solved
    /// result).</summary>
    public string? DeflectionResult { get; init; }

    /// <summary>Deflection limit = span / <see cref="SpanRatio"/> (span between the mega columns = bays × bay width).</summary>
    public double SpanRatio { get; init; } = VcfOptimization.DefaultSpanRatio;

    /// <summary>Force variants: product of the cable pretension and storey force range counts.</summary>
    public long ForceCount => CableForces.Values.Concat(StoreyLoads.Values).Aggregate(1L, (product, range) => Saturating(product, range.Count));

    public long CombinationCount => Saturating(Saturating(GeometryCount, SectionCount), ForceCount);

    private static long Saturating(long a, long b) => a == 0 || b == 0 ? 0 : a > long.MaxValue / b ? long.MaxValue : a * b;
}

public static class VcfOptimization
{
    /// <summary>A geometry variable of the VCF system: key (shared with Edit → VCF Geometry), label, unit and default step.</summary>
    public sealed record Parameter(string Key, string Label, string Unit, double DefaultStep, bool Whole = false);

    public static readonly Parameter Bays = new("Bays", "Number of bays", "", 1, Whole: true);
    public static readonly Parameter BayWidth = new("Bay", "Bay width (span per bay)", "m", 0.5);
    public static readonly Parameter Depth = new("Depth", "Truss depth (bottom → top chord)", "m", 0.5);
    /// <summary>Not a variable: the middle chord always sits at <see cref="MiddleChordRatio"/> × the truss depth.</summary>
    public static readonly Parameter Middle = new("Middle", "Middle chord above bottom chord", "m", 0.5);
    public const double MiddleChordRatio = 0.5;
    public static readonly Parameter Clear = new("Clear", "Clear height (ground → bottom chord)", "m", 0.5);
    public static readonly Parameter BackSpan = new("BackSpan", "Back-span (mega column → outer column)", "m", 0.5);
    public static readonly Parameter BackStay = new("BackStay", "Back-stay node height (above ground)", "m", 0.5);

    /// <summary>The geometry variables the model has, with its current value. The middle chord is not one of them — it
    /// follows the truss depth (<see cref="MiddleChordRatio"/>).</summary>
    public static IReadOnlyList<(Parameter Parameter, double Current)> Parameters(VcfGeometryAnalysis analysis)
    {
        var p = analysis.Parameters;
        var list = new List<(Parameter, double)> { (Bays, analysis.Bays), (BayWidth, p.BayWidth), (Depth, p.TrussDepth) };
        list.Add((Clear, p.ClearHeight));
        if (p.BackSpan is { } span) list.Add((BackSpan, span));
        if (p.BackStayHeight is { } stay) list.Add((BackStay, stay));
        return list;
    }

    /// <summary>Settings that keep the model as it is: every range fixed at the current value, no section candidates.</summary>
    public static VcfOptimizationSettings Current(VcfGeometryAnalysis analysis) => new(
        Parameters(analysis).ToDictionary(item => item.Parameter.Key, item => VcfRange.Fixed(item.Current, item.Parameter.DefaultStep)),
        new Dictionary<string, VcfRange>(StringComparer.Ordinal),
        new Dictionary<string, VcfRange>(StringComparer.Ordinal),
        new Dictionary<VcfMemberRole, IReadOnlyList<string>>(),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    public const double DefaultSpanRatio = 250;

    /// <summary>The deflection result to start with: the first solved result named SLS, else the first combination, else the
    /// first case.</summary>
    public static string? DefaultDeflectionResult(IReadOnlyList<string> cases, IReadOnlyList<string> combinations) =>
        cases.Concat(combinations).FirstOrDefault(name => name.Contains("SLS", StringComparison.OrdinalIgnoreCase))
        ?? combinations.FirstOrDefault() ?? cases.FirstOrDefault();

    public const string StoriesKey = "Stories";
    public const string TributaryKey = "B";
    public const double DefaultForceStep = 1000;    // kN
    public const double DefaultQzStep = 1;          // kPa

    /// <summary>A cable's Tab 03 pretension as a range: the fixed target, or Min … Max @ Step (null for a follower or an
    /// unreadable row — those are not optimization variables).</summary>
    public static VcfRange? CableRange(CablePretensionConfig cable) =>
        cable.IsFollower ? null
        : cable.ApplyRange && cable.MinForce is { } min && cable.MaxForce is { } max
            ? new VcfRange(min, max, cable.StepForce is { } step && step > 0 ? step : DefaultForceStep)
        : cable.TargetForce is { } target ? VcfRange.Fixed(target, DefaultForceStep) : null;

    /// <summary>A Tab 01 load definition's qz [kPa] as a range (null when it has no readable value).</summary>
    public static VcfRange? LoadRange(LoadDefinition load) =>
        load.ApplyIncrementStep && load.MinQz is { } min && load.MaxQz is { } max
            ? new VcfRange(min, max, load.IncrementStep is { } step && step > 0 ? step : DefaultQzStep)
        : !load.ApplyIncrementStep && load.Qz is { } qz ? VcfRange.Fixed(qz, DefaultQzStep) : null;

    /// <summary>The cable force and storey force problems: unreadable ranges, a negative qz, storeys that are not whole
    /// numbers ≥ 1, a tributary width ≤ 0.</summary>
    public static IReadOnlyList<string> ValidateForces(VcfOptimizationSettings settings)
    {
        var problems = new List<string>();
        foreach (var (cable, range) in settings.CableForces)
            if (range.Problem() is { } problem) problems.Add($"Cable force {cable}: {problem}");
        foreach (var (key, range) in settings.StoreyLoads)
        {
            var whole = key == StoriesKey;
            var label = key switch { StoriesKey => "Number of storeys", TributaryKey => "Tributary width B", _ => $"qz {key}" };
            if (range.Problem(whole) is { } problem) { problems.Add($"{label}: {problem}"); continue; }
            if (whole && range.Min < 1) problems.Add($"{label}: must be at least 1.");
            else if (key == TributaryKey && range.Min <= 0) problems.Add($"{label}: must be greater than zero.");
            else if (range.Min < 0) problems.Add($"{label}: must not be negative (qz acts downward).");
        }
        return problems;
    }

    /// <summary>The problems of the settings for <paramref name="analysis"/>: unreadable ranges, bays out of 2–200, and the
    /// lowest / highest corner of the space failing <see cref="VcfGeometry.Validate"/>. Single combinations inside the space
    /// that are invalid (e.g. a back-stay above a low bottom chord) are skipped by the run.</summary>
    public static IReadOnlyList<string> Validate(VcfGeometryAnalysis analysis, VcfOptimizationSettings settings)
    {
        var problems = new List<string>();
        foreach (var (parameter, _) in Parameters(analysis))
        {
            if (!settings.Geometry.TryGetValue(parameter.Key, out var range)) { problems.Add($"{parameter.Label}: no range."); continue; }
            if (range.Problem(parameter.Whole) is { } problem) problems.Add($"{parameter.Label}: {problem}");
        }
        problems.AddRange(ValidateForces(settings));
        if (!double.IsFinite(settings.SpanRatio) || settings.SpanRatio <= 0) problems.Add("Deflection limit: span / ratio needs a ratio greater than zero.");
        if (problems.Count > 0) return problems;
        var bays = settings.Geometry[Bays.Key];
        if (bays.Min < 2 || bays.Max > 200) problems.Add("Number of bays: the range must lie within 2 … 200.");
        foreach (var (corner, pick) in new (string, Func<VcfRange, double>)[] { ("minimum", range => range.Min), ("maximum", range => range.Max) })
        {
            var corners = VcfGeometry.Validate(analysis, ParametersAt(analysis, settings, pick));
            problems.AddRange(corners.Select(problem => $"At the {corner} of every range: {problem}"));
        }
        return problems;
    }

    /// <summary>The geometry parameters with each range reduced to one value by <paramref name="pick"/> (min, max, …);
    /// the middle chord at <see cref="MiddleChordRatio"/> × that truss depth.</summary>
    public static VcfGeometryParameters ParametersAt(VcfGeometryAnalysis analysis, VcfOptimizationSettings settings, Func<VcfRange, double> pick)
    {
        var p = analysis.Parameters;
        double? Value(Parameter parameter) => settings.Geometry.TryGetValue(parameter.Key, out var range) ? pick(range) : null;
        var depth = Value(Depth) ?? p.TrussDepth;
        return new VcfGeometryParameters(Value(BayWidth) ?? p.BayWidth, depth, analysis.HasMiddleChord ? MiddleChordRatio * depth : p.MiddleChordHeight,
            Value(Clear) ?? p.ClearHeight, p.BackSpan is null ? null : Value(BackSpan) ?? p.BackSpan,
            p.BackStayHeight is null ? null : Value(BackStay) ?? p.BackStayHeight);
    }
}
