using System.Globalization;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application;

/// <summary>A parametric study the Optimization menu runs case by case (Optimization VCF, Optimization TAS): its axes, its
/// number of cases, the cases, and one case built and analysed. The run loop and the results table are shared.</summary>
public interface IOptimizationRunner
{
    IReadOnlyList<VcfOptimizationAxis> Axes { get; }
    long Count { get; }
    IEnumerable<VcfOptimizationCase> Cases();
    VcfOptimizationResult Run(VcfOptimizationCase item, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optimization → Optimization TAS: the search space of the tied arch system — a range per geometry input (H1, H2, bays,
/// spacing; keys as in <see cref="TasOptimization.GeometryParameters"/>), a range per load of the storeys above (SW, SDL,
/// LL, floors n, tributary width B; <see cref="TasOptimization.LoadParameters"/>) and the candidate sections per member
/// role — with the deflection check of Optimization VCF, read on the top chord (deck) joints. A system with an elevated
/// cable (<see cref="ElevatedCable"/>) also has H0 among its geometry ranges and the tie's pretension, stepped as the VCF
/// cables are: <see cref="CableSteps"/> values from <see cref="CableForce"/> Min to Max.
/// </summary>
public sealed record TasOptimizationSettings(
    IReadOnlyDictionary<string, VcfRange> Geometry,
    IReadOnlyDictionary<string, VcfRange> Loads,
    IReadOnlyDictionary<TiedArchRole, IReadOnlyList<string>> RoleSections)
{
    public long GeometryCount => Product(Geometry.Values.Select(range => (long)range.Count));
    public long ForceCount => Product(Loads.Values.Select(range => (long)range.Count).Append(CableStepCount));

    /// <summary>The open system has the elevated cable: H0 and the tie's pretension are part of the space.</summary>
    public bool ElevatedCable { get; init; }
    /// <summary>The tie's pretension [kN] from the first to the last step.</summary>
    public VcfForceSpan CableForce { get; init; } = VcfForceSpan.Fixed(0);
    /// <summary>The number of pretension steps (1 keeps it at Min).</summary>
    public int CableSteps { get; init; } = 1;
    /// <summary>Pretension variants: the steps when the force varies, else 1.</summary>
    public long CableStepCount => ElevatedCable && CableSteps > 1 && !CableForce.IsFixed ? CableSteps : 1;
    /// <summary>The tie's pretension at step <paramref name="step"/> (0-based).</summary>
    public double CableForceAt(int step) => CableForce.At(CableStepCount > 1 ? (double)Math.Clamp(step, 0, (int)CableStepCount - 1) / (CableStepCount - 1) : 0);
    public long SectionCount => Product(RoleSections.Values.Select(names => (long)Math.Max(1, names.Count)));
    public long CombinationCount => Product([GeometryCount, ForceCount, SectionCount]);

    /// <summary>The load case / combination whose deflection is checked (null: the largest over every solved result).</summary>
    public string? DeflectionResult { get; init; }
    /// <summary>Deflection limit = span (bays × spacing) / <see cref="SpanRatio"/>.</summary>
    public double SpanRatio { get; init; } = VcfOptimization.DefaultSpanRatio;
    /// <summary>The largest upward displacement a top chord joint may have [mm].</summary>
    public double UpliftLimitMm { get; init; } = VcfOptimization.DefaultUpliftLimitMm;

    private static long Product(IEnumerable<long> counts) =>
        counts.Aggregate(1L, (product, count) => product == 0 || count == 0 ? 0 : product > long.MaxValue / count ? long.MaxValue : product * count);
}

public static class TasOptimization
{
    public static readonly VcfOptimization.Parameter ArchRise = new("H1", "H1 — support → arch crown", "m", 1);
    /// <summary>The top chord's height above the arch crown: H2 = H1 + h, so the deck is always above the arch.</summary>
    public static readonly VcfOptimization.Parameter DeckRise = new("h", "h — arch crown → top chord (H2 = H1 + h)", "m", 1);
    /// <summary>The h range the form starts with: 2 … 5 m in steps of 1 m.</summary>
    public static VcfRange DefaultDeckRise { get; } = new(2, 5, 1);
    public static readonly VcfOptimization.Parameter Bays = new("Bays", "Number of bays (even)", "", 2, Whole: true);
    public static readonly VcfOptimization.Parameter Spacing = new("Spacing", "Spacing of vertical lines", "m", 1);
    /// <summary>The elevated cable's height (a geometry range only for a system with the elevated cable).</summary>
    public static readonly VcfOptimization.Parameter CableHeight = new("H0", "H0 — ground → cable and springing", "m", 1);
    public static readonly VcfOptimization.Parameter SelfWeight = new("SW", "SW — slab self-weight", "kPa", 1);
    public static readonly VcfOptimization.Parameter SuperimposedDead = new("SDL", "SDL — superimposed dead", "kPa", 1);
    public static readonly VcfOptimization.Parameter Live = new("LL", "LL — live load", "kPa", 2.5);
    public static readonly VcfOptimization.Parameter Floors = new("Floors", "Number of floors n", "", 1, Whole: true);
    public static readonly VcfOptimization.Parameter Width = new("B", "Tributary width B", "m", 1);

    public static IReadOnlyList<VcfOptimization.Parameter> GeometryParameters { get; } = [ArchRise, DeckRise, Bays, Spacing];
    public static IReadOnlyList<VcfOptimization.Parameter> LoadParameters { get; } = [SelfWeight, SuperimposedDead, Live, Floors, Width];

    private static double Value(TiedArchParameters p, VcfOptimization.Parameter parameter) => parameter.Key switch
    {
        "H1" => p.ArchRise, "h" => p.DeckHeight - p.ArchRise, "Bays" => p.Bays, "Spacing" => p.BaySpacing, "H0" => p.CableHeight,
        "SW" => p.SelfWeight, "SDL" => p.SuperimposedDead, "LL" => p.Live, "Floors" => p.Floors, _ => p.TributaryWidth,
    };

    /// <summary>The form's start: the geometry fixed at <paramref name="p"/>, the loads over the ranges of the storeys above
    /// (SW 7–15 kPa @ 1, SDL 3–10 kPa @ 1, LL 5–10 kPa @ 2.5, n 5–10 @ 1) with B fixed, no section candidates; with an
    /// elevated cable also H0 and the tie's pretension, fixed at the system's.</summary>
    public static TasOptimizationSettings Start(TiedArchParameters p) => new(
        GeometryParametersOf(p.ElevatedCable).ToDictionary(parameter => parameter.Key,
            parameter => parameter == DeckRise ? DefaultDeckRise : VcfRange.Fixed(Value(p, parameter), parameter.DefaultStep), StringComparer.Ordinal),
        new Dictionary<string, VcfRange>(StringComparer.Ordinal)
        {
            [SelfWeight.Key] = new(7, 15, 1), [SuperimposedDead.Key] = new(3, 10, 1), [Live.Key] = new(5, 10, 2.5),
            [Floors.Key] = new(5, 10, 1), [Width.Key] = VcfRange.Fixed(p.TributaryWidth, Width.DefaultStep),
        },
        new Dictionary<TiedArchRole, IReadOnlyList<string>>())
    {
        ElevatedCable = p.ElevatedCable,
        CableForce = VcfForceSpan.Fixed(p.CablePretension),
    };

    /// <summary>The geometry ranges of a system: H1, H2, bays, spacing, and H0 with the elevated cable.</summary>
    public static IReadOnlyList<VcfOptimization.Parameter> GeometryParametersOf(bool elevatedCable) =>
        elevatedCable ? [.. GeometryParameters, CableHeight] : GeometryParameters;

    /// <summary>The parameters with each range reduced to one value by <paramref name="pick"/> (the tie's pretension too).</summary>
    public static TiedArchParameters ParametersAt(TasOptimizationSettings settings, Func<VcfRange, double> pick)
    {
        double V(VcfOptimization.Parameter parameter) =>
            (LoadParameters.Contains(parameter) ? settings.Loads : settings.Geometry).TryGetValue(parameter.Key, out var range) ? pick(range) : double.NaN;
        var force = settings.CableForce;
        return new TiedArchParameters(V(ArchRise), V(ArchRise) + V(DeckRise), (int)Math.Round(V(Bays)), V(Spacing), V(Width), V(SelfWeight), V(SuperimposedDead), V(Live),
            (int)Math.Round(V(Floors)), settings.ElevatedCable,
            settings.ElevatedCable ? V(CableHeight) : TiedArchParameters.DefaultCableHeight,
            settings.ElevatedCable ? pick(new VcfRange(force.Min, force.Max, Math.Max(force.Max - force.Min, 1))) : TiedArchParameters.DefaultCablePretension);
    }

    /// <summary>Unreadable ranges, the check limits, and the lowest / highest corner failing
    /// <see cref="TiedArchParameters.GeometryProblems"/>. Single cases inside the space that cannot be built (an odd number
    /// of bays) are skipped by the run, as Optimization VCF skips its invalid ones. H2 = H1 + h with h &gt; 0, so the top chord
    /// is always above the arch crown.</summary>
    public static IReadOnlyList<string> Validate(TasOptimizationSettings settings)
    {
        var problems = new List<string>();
        foreach (var (parameter, ranges) in GeometryParametersOf(settings.ElevatedCable).Select(item => (item, settings.Geometry)).Concat(LoadParameters.Select(item => (item, settings.Loads))))
        {
            if (!ranges.TryGetValue(parameter.Key, out var range)) { problems.Add($"{parameter.Label}: no range."); continue; }
            if (range.Problem(parameter.Whole) is { } problem) problems.Add($"{parameter.Label}: {problem}");
        }
        if (!double.IsFinite(settings.SpanRatio) || settings.SpanRatio <= 0) problems.Add("Deflection limit: span / ratio needs a ratio greater than zero.");
        if (!double.IsFinite(settings.UpliftLimitMm) || settings.UpliftLimitMm < 0) problems.Add("Uplift limit: enter a number of at least 0 mm.");
        if (settings.Geometry.TryGetValue(DeckRise.Key, out var rise) && rise.Problem() is null && rise.Min <= 0)
            problems.Add($"{DeckRise.Label}: min must be greater than 0 m, so the top chord stays above the arch crown.");
        if (settings.ElevatedCable)
        {
            if (settings.CableSteps < 1) problems.Add("Cable force steps: enter a whole number of at least 1.");
            if (settings.CableForce.Problem() is { } problem) problems.Add($"Cable force {TiedArchSystem.CableName}: {problem}");
            else if (settings.CableForce.Min < 0) problems.Add($"Cable force {TiedArchSystem.CableName}: must not be negative.");
        }
        if (problems.Count > 0) return problems;
        foreach (var (corner, pick) in new (string, Func<VcfRange, double>)[] { ("minimum", range => range.Min), ("maximum", range => range.Max) })
            problems.AddRange(ParametersAt(settings, pick).GeometryProblems().Select(problem => $"At the {corner} of every range: {problem}"));
        return problems;
    }
}

/// <summary>Everything an Optimization TAS case needs, frozen when the run starts: the TAS model the system is built from
/// (materials, sections, patterns, cases, combinations), a member's properties with a given library section, the load
/// case rows of the open model, the analysis options and the cost per metre.</summary>
public sealed record TasOptimizationInputs(
    EtabsModel Template,
    Func<FemSectionProperties, string, FemSectionProperties> Properties,
    IReadOnlyList<FemPatternRow> Patterns,
    IReadOnlyList<FemCaseRow> Cases,
    IReadOnlyList<FemCombinationRow> Combinations,
    IReadOnlyList<string> PretensionPatterns,
    double TargetFitPercent,
    int TargetFitIterations,
    int FrameMeshSegments,
    bool CableSag,
    Func<FemSectionProperties, (double? SgdPerM, string? Missing)>? CostPerMetre = null)
{
    /// <summary>The degrees of freedom of Analysis Options: those of the model, and those of a joint only cables reach.</summary>
    public Mcotis.Core.DofMask ActiveDofs { get; init; } = Mcotis.Core.DofMask.XZ;
    public Mcotis.Core.DofMask CableDofs { get; init; } = Mcotis.Core.DofMask.Translations;
    /// <summary>The mass sources of the model, which its load case rows name.</summary>
    public IReadOnlyList<Configuration.FemMassSourceDto> MassSources { get; init; } = [];
}

/// <summary>
/// Optimization → Optimization TAS, as a configuration of <see cref="ParametricOptimization"/>. Each case is the tied arch
/// system the TAS generator builds for its inputs (<see cref="TasGenerator.Build"/>: the parabolic arch, the TAS layout,
/// its supports and the deck loads (SW + SDL) and LL × spacing × B × n), with the case's sections per role. The deflection
/// and uplift are read on the top chord (deck) joints, where the storeys sit. The open model is never changed.
/// </summary>
public sealed class TasOptimizationStudy : IParametricStudy
{
    private readonly TasOptimizationInputs inputs;
    private readonly TasOptimizationSettings settings;

    public TasOptimizationStudy(TasOptimizationInputs inputs, TasOptimizationSettings settings)
    {
        this.inputs = inputs;
        this.settings = settings;
        var axes = new List<VcfOptimizationAxis>();
        foreach (var parameter in TasOptimization.GeometryParametersOf(settings.ElevatedCable))
            axes.Add(new("geometry:" + parameter.Key, Label(parameter), settings.Geometry[parameter.Key].Values().Cast<object>().ToArray()));
        // the tie's pretension steps, as the VCF cables step (one axis; its column shows the force)
        if (settings.CableStepCount > 1)
            axes.Add(new(ParametricOptimization.CableStepKey, $"{TiedArchSystem.CableName} [kN]",
                Enumerable.Range(0, (int)settings.CableStepCount).Select(step => (object)new VcfCableStep(step, settings.CableForceAt(step))).ToArray()));
        foreach (var parameter in TasOptimization.LoadParameters)
            axes.Add(new("load:" + parameter.Key, Label(parameter), settings.Loads[parameter.Key].Values().Cast<object>().ToArray()));
        foreach (var (role, sections) in settings.RoleSections.OrderBy(pair => pair.Key))
            if (sections.Count > 0) axes.Add(new("role:" + role, TiedArchSystem.Title(role), sections.Cast<object>().ToArray()));
        Axes = axes;
        Count = settings.CombinationCount;
    }

    private static string Label(VcfOptimization.Parameter parameter) => parameter.Key switch
    {
        "Bays" => "Bays", "Floors" => "n", _ => parameter.Unit.Length > 0 ? $"{parameter.Key} [{parameter.Unit}]" : parameter.Key,
    };

    public IReadOnlyList<VcfOptimizationAxis> Axes { get; }
    public long Count { get; }


    /// <summary>The tied arch system of a case.</summary>
    public TiedArchParameters ParametersOf(VcfOptimizationCase item)
    {
        double V(VcfOptimization.Parameter parameter, string prefix) =>
            Convert.ToDouble(item.Values[Axes.ToList().FindIndex(axis => axis.Key == prefix + parameter.Key)], CultureInfo.InvariantCulture);
        double G(VcfOptimization.Parameter parameter) => V(parameter, "geometry:");
        double L(VcfOptimization.Parameter parameter) => V(parameter, "load:");
        var stepAxis = Axes.ToList().FindIndex(axis => axis.Key == ParametricOptimization.CableStepKey);
        var pretension = stepAxis >= 0 && item.Values[stepAxis] is VcfCableStep step ? step.ForceKN : settings.CableForceAt(0);
        return new TiedArchParameters(G(TasOptimization.ArchRise), G(TasOptimization.ArchRise) + G(TasOptimization.DeckRise), (int)Math.Round(G(TasOptimization.Bays)), G(TasOptimization.Spacing),
            L(TasOptimization.Width), L(TasOptimization.SelfWeight), L(TasOptimization.SuperimposedDead), L(TasOptimization.Live), (int)Math.Round(L(TasOptimization.Floors)),
            settings.ElevatedCable, settings.ElevatedCable ? G(TasOptimization.CableHeight) : TiedArchParameters.DefaultCableHeight,
            settings.ElevatedCable ? pretension : TiedArchParameters.DefaultCablePretension);
    }

    /// <summary>The tied arch system of a case, with its sections per role and the tie's target.</summary>
    public ParametricCandidate Candidate(VcfOptimizationCase item)
    {
        var p = ParametersOf(item);
        if (p.GeometryProblems() is { Count: > 0 } problems) throw new ParametricCandidateException("Invalid geometry: " + string.Join(" ", problems));
        var (imported, geometry) = TasGenerator.Build(inputs.Template, p);

        var chosen = new Dictionary<TiedArchRole, string>();
        for (var axis = 0; axis < Axes.Count; axis++)
            if (Axes[axis].Key.StartsWith("role:", StringComparison.Ordinal) && Enum.TryParse<TiedArchRole>(Axes[axis].Key[5..], out var role))
                chosen[role] = (string)item.Values[axis];
        var properties = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal);
        foreach (var member in imported.Members)
        {
            var section = TiedArchSystem.RoleOf(member.SectionName) is { } role && chosen.TryGetValue(role, out var candidate) ? candidate : member.SectionName!;
            properties[member.MemberName] = inputs.Properties(member, section) with { MemberName = member.MemberName };
        }
        // the tie: the VCF cable's strand section, or the case's cable candidate
        if (p.ElevatedCable)
            properties[TiedArchSystem.CableName] = inputs.Properties(
                new FemSectionProperties(TiedArchSystem.CableName, 0, 0, null, SectionName: TiedArchSystem.CableSection),
                chosen.TryGetValue(TiedArchRole.Cable, out var cable) ? cable : TiedArchSystem.CableSection) with { MemberName = TiedArchSystem.CableName };

        // the role of a member is that of the section the system was built with, whatever section the case tries
        var roles = imported.Members.Where(member => TiedArchSystem.RoleOf(member.SectionName) is not null)
            .ToDictionary(member => member.MemberName, member => TiedArchSystem.Title(TiedArchSystem.RoleOf(member.SectionName)!.Value), StringComparer.Ordinal);
        if (p.ElevatedCable) roles[TiedArchSystem.CableName] = TiedArchSystem.Title(TiedArchRole.Cable);
        // the case is analysed as Run FEM Analysis analyses the model: as a native document, by the same solver path
        var setup = new FemCandidateSetup(imported.Loading, inputs.Patterns, inputs.Cases, inputs.Combinations, [], inputs.PretensionPatterns, [],
            inputs.TargetFitPercent, inputs.TargetFitIterations, inputs.FrameMeshSegments, inputs.CableSag, inputs.ActiveDofs, inputs.CableDofs)
            { MassSources = inputs.MassSources };
        var targets = p.ElevatedCable ? new Dictionary<string, double>(StringComparer.Ordinal) { [TiedArchSystem.CableName] = p.CablePretension }
            : new Dictionary<string, double>(StringComparer.Ordinal);
        // both limits are read on the top chord (deck) joints, where the storeys above are carried
        return new($"TAS{item.Index + 1:00000}", geometry, properties, setup, [], targets, null,
            TopChord(geometry, p).ToHashSet(StringComparer.Ordinal), "top chord joint", p.Span, null, roles);
    }
    /// <summary>The top chord (deck) joints of a built system: those at its deck elevation (H2, or H0 + H2 with an elevated cable).</summary>
    public static IEnumerable<string> TopChord(GeometryModel geometry, TiedArchParameters p) =>
        geometry.Points.Values.Where(point => Math.Abs(point.Position.Z - p.DeckElevation) < 1e-6).Select(point => point.Name);
}
