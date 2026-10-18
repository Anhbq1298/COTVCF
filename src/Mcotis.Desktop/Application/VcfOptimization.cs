using System.Globalization;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application;

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

/// <summary>A cable's pretension [kN] from the first to the last step of the run. Every cable steps together: at step j of
/// n it is at Min + (Max − Min) × j / (n − 1), so Min = Max keeps it fixed.</summary>
public sealed record VcfForceSpan(double Min, double Max)
{
    public static VcfForceSpan Fixed(double value) => new(value, value);

    public bool IsFixed => Math.Abs(Max - Min) <= 1e-9;

    public double At(double fraction) => Min + (Max - Min) * fraction;

    public string? Problem() =>
        !double.IsFinite(Min) || !double.IsFinite(Max) ? "enter numbers for min and max."
        : Max < Min - 1e-9 ? "max must not be less than min." : null;
}

/// <summary>How the fan cables (top corner → bottom chord) get their pretension. A longer cable needs more force, and the
/// number of fan cables follows the number of bays, so the graded rules give every fan cable of a case its force from its
/// length.</summary>
public enum VcfFanGrading
{
    /// <summary>Each fan cable has a span of its own. A fan cable a larger number of bays adds takes the force of the cable
    /// it copies × its length / that cable's length.</summary>
    Manual,
    /// <summary>T = T shortest + gradient [kN/m] × (L − L shortest): every fan cable steps by the same force.</summary>
    Linear,
    /// <summary>T = T shortest × L / L shortest.</summary>
    Proportional,
}

/// <summary>
/// The cable pretensions of the search space. All cables step together, <see cref="Steps"/> values each
/// (<see cref="VcfForceSpan"/>); <see cref="Cables"/> holds the span of every cable with a force of its own — a Tab 03
/// follower that is not listed keeps following its reference. With a graded rule (<see cref="Grading"/>) the fan cables
/// are not listed: <see cref="Fan"/> is the span of the shortest one and the others follow by their length
/// (<see cref="VcfOptimization.CableTargets"/>).
/// </summary>
public sealed record VcfCableForces(
    int Steps,
    IReadOnlyDictionary<string, VcfForceSpan> Cables,
    VcfFanGrading Grading = VcfFanGrading.Manual,
    VcfForceSpan? Fan = null,
    double GradientKnPerM = 0)
{
    /// <summary>No cable has a force of its own (the settings before the form is read).</summary>
    public static VcfCableForces None { get; } = new(1, new Dictionary<string, VcfForceSpan>(StringComparer.Ordinal));

    public bool Graded => Grading != VcfFanGrading.Manual;

    /// <summary>True when a cable changes from the first step to the last.</summary>
    public bool Varies => Cables.Values.Any(span => !span.IsFixed) || Graded && Fan is { IsFixed: false };

    /// <summary>Force variants of the cables: the steps, or 1 when no cable varies.</summary>
    public int StepCount => Steps > 1 && Varies ? Steps : 1;

    /// <summary>Where step <paramref name="step"/> (0-based) lies between Min (0) and Max (1).</summary>
    public double Fraction(int step) => StepCount > 1 ? (double)Math.Clamp(step, 0, StepCount - 1) / (StepCount - 1) : 0;
}

/// <summary>
/// Run → Optimization VCF: the search space of the VCF system — a range per geometry parameter (keys as in
/// <see cref="VcfOptimization.Parameters"/>), the cable pretensions (<see cref="CableForces"/>, kN: one shared step for
/// every cable, the fan cables by their length), the storey force inputs (<see cref="StoreyLoads"/>: qz per Tab 01
/// load definition [kPa] by load name, plus <see cref="VcfOptimization.StoriesKey"/> and
/// <see cref="VcfOptimization.TributaryKey"/>) and the candidate sections per member role and per cable. The settings are
/// kept by the view model and walked by <see cref="ParametricOptimization"/> (<see cref="VcfOptimizationStudy"/>).
/// </summary>
public sealed record VcfOptimizationSettings(
    IReadOnlyDictionary<string, VcfRange> Geometry,
    VcfCableForces CableForces,
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

    /// <summary>Uplift limit [mm]: the largest upward displacement a bottom chord point (BTrPt…) may have in the deflection
    /// result. 0 allows none — the check that stops a pretension that lifts the span above its unloaded position, which the
    /// downward limit cannot see. Both limits are read on the bottom chord points only.</summary>
    public double UpliftLimitMm { get; init; } = VcfOptimization.DefaultUpliftLimitMm;

    /// <summary>Effective (buckling) length l0 [m] of the members of each frame role, for the Design check of the Member Utilization
    /// sheet; a role not listed has <see cref="VcfOptimization.DefaultEffectiveLengthM"/>.</summary>
    public IReadOnlyDictionary<VcfMemberRole, double> EffectiveLengths { get; init; } = new Dictionary<VcfMemberRole, double>();

    /// <summary>Force variants: the cable steps (all cables step together) × the storey force range counts.</summary>
    public long ForceCount => StoreyLoads.Values.Aggregate((long)CableForces.StepCount, (product, range) => Saturating(product, range.Count));

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
        VcfCableForces.None,
        new Dictionary<string, VcfRange>(StringComparer.Ordinal),
        new Dictionary<VcfMemberRole, IReadOnlyList<string>>(),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    public const double DefaultSpanRatio = 250;
    public const double DefaultUpliftLimitMm = 0;
    /// <summary>Effective length l0 of every frame member until the user changes it [m].</summary>
    public const double DefaultEffectiveLengthM = 10;
    /// <summary>An uplift within this of the limit passes: it reads the same at the 0.1 mm the results show.</summary>
    public const double UpliftToleranceMm = 0.05;

    /// <summary>The deflection result to start with: the first solved result named SLS, else the first combination, else the
    /// first case.</summary>
    public static string? DefaultDeflectionResult(IReadOnlyList<string> cases, IReadOnlyList<string> combinations) =>
        cases.Concat(combinations).FirstOrDefault(name => name.Contains("SLS", StringComparison.OrdinalIgnoreCase))
        ?? combinations.FirstOrDefault() ?? cases.FirstOrDefault();

    public const string StoriesKey = "Stories";
    public const string TributaryKey = "B";
    public const double DefaultForceStep = 1000;    // kN
    public const double DefaultQzStep = 1;          // kPa

    /// <summary>Forces a rule works out (a fan cable graded by its length) are rounded to this [kN].</summary>
    public const double ForceRoundingKn = 100;

    public static double RoundForce(double kN) => Math.Round(kN / ForceRoundingKn, MidpointRounding.AwayFromZero) * ForceRoundingKn;

    /// <summary>A cable's Tab 03 pretension as a span: the fixed target, or Min … Max — for a cable with a force of its own
    /// (independent, or a Same Step follower with its own end points). Null for a cable that takes another's value or an
    /// unreadable row.</summary>
    public static VcfForceSpan? CableSpan(CablePretensionConfig cable) =>
        cable.IsSameStepFollower ? cable.MinForce is { } low && cable.MaxForce is { } high ? new VcfForceSpan(low, high) : null
        : cable.IsFollower ? null
        : cable.ApplyRange && cable.MinForce is { } min && cable.MaxForce is { } max ? new VcfForceSpan(min, max)
        : cable.TargetForce is { } target ? VcfForceSpan.Fixed(target) : null;

    /// <summary>The number of steps the form starts with: the most values a Tab 03 range has (1 when every cable is fixed).</summary>
    public static int CableSteps(IEnumerable<CablePretensionConfig> cables) => cables
        .Where(cable => !cable.IsFollower && cable.ApplyRange && cable.MinForce is not null && cable.MaxForce is not null)
        .Select(cable => new VcfRange(cable.MinForce!.Value, cable.MaxForce!.Value, cable.StepForce is { } step && step > 0 ? step : DefaultForceStep).Count)
        .Append(1).Max();

    /// <summary>The cable forces of the model as it is: every cable at its Tab 03 span, the fan not graded.</summary>
    public static VcfCableForces ModelCableForces(IReadOnlyList<CablePretensionConfig> cables)
    {
        var spans = new Dictionary<string, VcfForceSpan>(StringComparer.Ordinal);
        foreach (var cable in cables)
            if (CableSpan(cable) is { } span) spans.TryAdd(cable.CableName, span);
        return new VcfCableForces(CableSteps(cables), spans);
    }

    /// <summary>The increase per metre [kN/m] the model's own fan forces show: from the shortest to the longest fan cable of
    /// the first side, a whole number, never negative (0 when there is one fan cable or the forces fall with the length).</summary>
    public static double FitGradient(VcfGeometryAnalysis analysis, IReadOnlyDictionary<string, double> forces)
    {
        var fan = VcfGeometry.FanCables(analysis).GroupBy(cable => cable.Side).FirstOrDefault()?.Where(cable => forces.ContainsKey(cable.Name)).ToArray();
        if (fan is not { Length: > 1 }) return 0;
        var (shortest, longest) = (fan[0].Name, fan[^1].Name);
        var run = CableLength(analysis.Geometry, longest) - CableLength(analysis.Geometry, shortest);
        return run > 1e-6 ? Math.Max(0, Math.Round((forces[longest] - forces[shortest]) / run, MidpointRounding.AwayFromZero)) : 0;
    }

    /// <summary>Chord length of a member [m] (0 when it or an end point is missing).</summary>
    public static double CableLength(GeometryModel geometry, string member)
    {
        var cable = geometry.Members.FirstOrDefault(item => item.Name == member);
        if (cable is null || !geometry.Points.TryGetValue(cable.Start, out var a) || !geometry.Points.TryGetValue(cable.End, out var b)) return 0;
        var (dx, dy, dz) = (b.Position.X - a.Position.X, b.Position.Y - a.Position.Y, b.Position.Z - a.Position.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// The pretension [kN] of every cable of one case, at step <paramref name="step"/> (0-based) of the shared steps.
    /// <paramref name="model"/> is the case's system (another number of bays when asked) and <paramref name="geometry"/> its
    /// built geometry — the lengths are taken there; <paramref name="addedCables"/> are the fan cables that number of bays
    /// adds, with the cable each copies; <paramref name="configs"/> are the Tab 03 rows (sync rules).
    /// A cable with a span of its own is at that span; a Tab 03 follower applies its rule to its reference. With a graded
    /// rule every fan cable takes T shortest (+ gradient × extra length, or × length ratio) of its side; without one, an
    /// added fan cable takes the force of the cable it copies × its length ratio. Forces from a length rule are rounded to
    /// <see cref="ForceRoundingKn"/>. A cable with no readable force is left out.
    /// </summary>
    public static IReadOnlyDictionary<string, double> CableTargets(VcfCableForces forces, int step, VcfGeometryAnalysis model, GeometryModel geometry,
        IReadOnlyDictionary<string, string>? addedCables, IReadOnlyList<CablePretensionConfig> configs)
    {
        var fraction = forces.Fraction(step);
        var byName = configs.GroupBy(cable => cable.CableName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var resolved = new Dictionary<string, double?>(StringComparer.Ordinal);
        double? Own(string name, int depth)
        {
            if (resolved.TryGetValue(name, out var done)) return done;
            if (forces.Cables.TryGetValue(name, out var span)) return resolved[name] = span.At(fraction);
            if (depth > 32 || !byName.TryGetValue(name, out var cable) || !cable.IsFollower || Own(cable.ReferenceCableName!, depth + 1) is not { } reference)
                return resolved[name] = null;
            return resolved[name] = cable.SyncMode switch
            {
                CableSyncMode.SameValues => reference,
                CableSyncMode.SameRatio => reference * (cable.SyncFactor ?? 1),
                CableSyncMode.SameOffset => reference + (cable.SyncFactor ?? 0),
                CableSyncMode.SameStep when cable.MinForce is { } low && cable.MaxForce is { } high => low + (high - low) * fraction,
                _ => null,
            };
        }

        var lengths = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var member in geometry.Members.Where(member => member.Kind != MemberKind.Frame))
            if (geometry.Points.TryGetValue(member.Start, out var a) && geometry.Points.TryGetValue(member.End, out var b))
            {
                var (dx, dy, dz) = (b.Position.X - a.Position.X, b.Position.Y - a.Position.Y, b.Position.Z - a.Position.Z);
                lengths[member.Name] = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

        var targets = new Dictionary<string, double>(StringComparer.Ordinal);
        if (forces.Graded && forces.Fan is { } fan)
        {
            var shortestForce = fan.At(fraction);
            foreach (var side in VcfGeometry.FanCables(model).Where(cable => lengths.ContainsKey(cable.Name)).GroupBy(cable => cable.Side))
            {
                var shortest = lengths[side.First().Name];   // the nearest cable of the side
                foreach (var cable in side)
                    targets[cable.Name] = RoundForce(forces.Grading == VcfFanGrading.Linear
                        ? shortestForce + forces.GradientKnPerM * (lengths[cable.Name] - shortest)
                        : shortest > 0 ? shortestForce * lengths[cable.Name] / shortest : shortestForce);
            }
        }
        foreach (var name in lengths.Keys)
        {
            if (targets.ContainsKey(name)) continue;
            if (addedCables is not null && addedCables.TryGetValue(name, out var template))
            {
                if (Own(template, 0) is not { } copied) continue;
                targets[name] = RoundForce(lengths.TryGetValue(template, out var templateLength) && templateLength > 0 ? copied * lengths[name] / templateLength : copied);
            }
            else if (Own(name, 0) is { } own) targets[name] = own;
        }
        return targets;
    }

    /// <summary>A Tab 01 load definition's qz [kPa] as a range (null when it has no readable value).</summary>
    public static VcfRange? LoadRange(LoadDefinition load) =>
        load.ApplyIncrementStep && load.MinQz is { } min && load.MaxQz is { } max
            ? new VcfRange(min, max, load.IncrementStep is { } step && step > 0 ? step : DefaultQzStep)
        : !load.ApplyIncrementStep && load.Qz is { } qz ? VcfRange.Fixed(qz, DefaultQzStep) : null;

    /// <summary>The cable force problems: steps below 1, an unreadable span, a graded fan without a span for its shortest
    /// cable or with an increase per metre that is not a number ≥ 0.</summary>
    public static IReadOnlyList<string> CableProblems(VcfCableForces forces)
    {
        var problems = new List<string>();
        if (forces.Steps < 1) problems.Add("Cable force steps: enter a whole number of at least 1.");
        foreach (var (cable, span) in forces.Cables)
            if (span.Problem() is { } problem) problems.Add($"Cable force {cable}: {problem}");
        if (!forces.Graded) return problems;
        if (forces.Fan is not { } fan) problems.Add("Fan cable force: the shortest cable has no range.");
        else if (fan.Problem() is { } problem) problems.Add($"Fan cable force: {problem}");
        if (forces.Grading == VcfFanGrading.Linear && !(double.IsFinite(forces.GradientKnPerM) && forces.GradientKnPerM >= 0))
            problems.Add("Fan cable force: the increase per metre must be a number of at least 0.");
        return problems;
    }

    /// <summary>The cable force and storey force problems: <see cref="CableProblems"/>, unreadable ranges, a negative qz,
    /// storeys that are not whole numbers ≥ 1, a tributary width ≤ 0.</summary>
    public static IReadOnlyList<string> ValidateForces(VcfOptimizationSettings settings)
    {
        var problems = new List<string>(CableProblems(settings.CableForces));
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
        if (!double.IsFinite(settings.UpliftLimitMm) || settings.UpliftLimitMm < 0) problems.Add("Uplift limit: enter a number of at least 0 mm.");
        foreach (var (role, length) in settings.EffectiveLengths)
            if (!double.IsFinite(length) || length <= 0) problems.Add($"Effective length of {VcfGeometry.Title(role)}: enter a length greater than zero.");
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
