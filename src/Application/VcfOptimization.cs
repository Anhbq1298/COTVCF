using System.Globalization;

namespace COT_VCF.Application;

/// <summary>One optimization variable: Min … Max in steps of <see cref="Step"/>. The values are Min, Min + Step, … up to
/// Max; Max itself is always the last value (a step that does not divide the range ends short on Max).</summary>
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
        if (values.Count == 0 || values[^1] < Max) values.Add(Max);
        return values;
    }

    public override string ToString() => IsFixed
        ? Min.ToString("0.###", CultureInfo.CurrentCulture)
        : $"{Min.ToString("0.###", CultureInfo.CurrentCulture)} … {Max.ToString("0.###", CultureInfo.CurrentCulture)} @ {Step.ToString("0.###", CultureInfo.CurrentCulture)}";
}

/// <summary>
/// Run → Optimization VCF: the search space of the VCF system — a range per geometry parameter (keys as in
/// <see cref="VcfOptimization.Parameters"/>) and the candidate sections per member role and per cable. The run loop that
/// walks the space is not built yet; the settings are kept by the view model.
/// </summary>
public sealed record VcfOptimizationSettings(
    IReadOnlyDictionary<string, VcfRange> Geometry,
    IReadOnlyDictionary<VcfMemberRole, IReadOnlyList<string>> RoleSections,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CableSections)
{
    /// <summary>Geometry variants (product of the range counts).</summary>
    public long GeometryCount => Geometry.Values.Aggregate(1L, (product, range) => Saturating(product, range.Count));

    /// <summary>Section variants (product of the candidate counts; a role or cable with none keeps its section).</summary>
    public long SectionCount => RoleSections.Values.Concat(CableSections.Values)
        .Aggregate(1L, (product, names) => Saturating(product, Math.Max(1, names.Count)));

    public long CombinationCount => Saturating(GeometryCount, SectionCount);

    private static long Saturating(long a, long b) => a == 0 || b == 0 ? 0 : a > long.MaxValue / b ? long.MaxValue : a * b;
}

public static class VcfOptimization
{
    /// <summary>A geometry variable of the VCF system: key (shared with Edit → VCF Geometry), label, unit and default step.</summary>
    public sealed record Parameter(string Key, string Label, string Unit, double DefaultStep, bool Whole = false);

    public static readonly Parameter Bays = new("Bays", "Number of bays", "", 1, Whole: true);
    public static readonly Parameter BayWidth = new("Bay", "Bay width (span per bay)", "m", 0.5);
    public static readonly Parameter Depth = new("Depth", "Truss depth (bottom → top chord)", "m", 0.5);
    public static readonly Parameter Middle = new("Middle", "Middle chord above bottom chord", "m", 0.5);
    public static readonly Parameter Clear = new("Clear", "Clear height (ground → bottom chord)", "m", 0.5);
    public static readonly Parameter BackSpan = new("BackSpan", "Back-span (mega column → outer column)", "m", 0.5);
    public static readonly Parameter BackStay = new("BackStay", "Back-stay node height (above ground)", "m", 0.5);

    /// <summary>The geometry variables the model has, with its current value.</summary>
    public static IReadOnlyList<(Parameter Parameter, double Current)> Parameters(VcfGeometryAnalysis analysis)
    {
        var p = analysis.Parameters;
        var list = new List<(Parameter, double)> { (Bays, analysis.Bays), (BayWidth, p.BayWidth), (Depth, p.TrussDepth) };
        if (analysis.HasMiddleChord) list.Add((Middle, p.MiddleChordHeight));
        list.Add((Clear, p.ClearHeight));
        if (p.BackSpan is { } span) list.Add((BackSpan, span));
        if (p.BackStayHeight is { } stay) list.Add((BackStay, stay));
        return list;
    }

    /// <summary>Settings that keep the model as it is: every range fixed at the current value, no section candidates.</summary>
    public static VcfOptimizationSettings Current(VcfGeometryAnalysis analysis) => new(
        Parameters(analysis).ToDictionary(item => item.Parameter.Key, item => VcfRange.Fixed(item.Current, item.Parameter.DefaultStep)),
        new Dictionary<VcfMemberRole, IReadOnlyList<string>>(),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    /// <summary>The problems of the settings for <paramref name="analysis"/>: unreadable ranges, bays out of 2–200, and the
    /// lowest / highest corner of the space failing <see cref="VcfGeometry.Validate"/>. Single combinations inside the space
    /// that are invalid (e.g. a middle chord above a shallow truss) are skipped by the run.</summary>
    public static IReadOnlyList<string> Validate(VcfGeometryAnalysis analysis, VcfOptimizationSettings settings)
    {
        var problems = new List<string>();
        foreach (var (parameter, _) in Parameters(analysis))
        {
            if (!settings.Geometry.TryGetValue(parameter.Key, out var range)) { problems.Add($"{parameter.Label}: no range."); continue; }
            if (range.Problem(parameter.Whole) is { } problem) problems.Add($"{parameter.Label}: {problem}");
        }
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

    /// <summary>The geometry parameters with each range reduced to one value by <paramref name="pick"/> (min, max, …).</summary>
    public static VcfGeometryParameters ParametersAt(VcfGeometryAnalysis analysis, VcfOptimizationSettings settings, Func<VcfRange, double> pick)
    {
        var p = analysis.Parameters;
        double? Value(Parameter parameter) => settings.Geometry.TryGetValue(parameter.Key, out var range) ? pick(range) : null;
        return new VcfGeometryParameters(Value(BayWidth) ?? p.BayWidth, Value(Depth) ?? p.TrussDepth, Value(Middle) ?? p.MiddleChordHeight,
            Value(Clear) ?? p.ClearHeight, p.BackSpan is null ? null : Value(BackSpan) ?? p.BackSpan,
            p.BackStayHeight is null ? null : Value(BackStay) ?? p.BackStayHeight);
    }
}
