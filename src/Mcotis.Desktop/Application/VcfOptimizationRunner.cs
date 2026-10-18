using System.Globalization;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application;

/// <summary>Everything a Run → Optimization VCF case needs from the working model, frozen when the run starts.
/// <see cref="SwapSection"/> gives a member's properties with another section (from a member that has it, or the section
/// library), as Edit → VCF Geometry assigns sections. <see cref="CostPerMetre"/> gives a member's cost per metre [SGD/m]
/// from its section and the materials' unit costs, or why it has none.</summary>
public sealed record VcfOptimizationInputs(
    VcfGeometryAnalysis Analysis,
    IReadOnlyDictionary<string, FemSectionProperties> Properties,
    Func<FemSectionProperties, string, FemSectionProperties> SwapSection,
    SapFemLoading Loading,
    IReadOnlyList<FemPatternRow> Patterns,
    IReadOnlyList<FemCaseRow> Cases,
    IReadOnlyList<FemCombinationRow> Combinations,
    IReadOnlyList<string> Tab01Patterns,
    IReadOnlyList<string> PretensionPatterns,
    IReadOnlyList<string> AppliedPoints,
    IReadOnlyList<CablePretensionConfig> Cables,
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

/// <summary>One variable of the search space: its key ("geometry:Bay", "cable:step", "storey:LL", "role:TopChord",
/// "section:Cable1a-2"), a column label and its values (numbers, cable steps, or section names).</summary>
public sealed record VcfOptimizationAxis(string Key, string Label, IReadOnlyList<object> Values)
{
    public bool Varies => Values.Count > 1;
}

/// <summary>One step of the cable forces — every cable steps together: its index and the force [kN] of the cable the
/// results column is named after.</summary>
public sealed record VcfCableStep(int Index, double ForceKN);

/// <summary>One combination: the value picked on every axis (same order as <see cref="IOptimizationRunner.Axes"/>).</summary>
public sealed record VcfOptimizationCase(long Index, IReadOnlyList<object> Values);

/// <summary>A case's outcome, read on the bottom chord points (BTrPt…) in the deflection result (or over every solved
/// result when none is chosen): the largest downward deflection, where it occurs, the span and the limit span / ratio; the
/// largest upward displacement (<see cref="MaxUpliftMm"/>, 0 when none rises) and its limit — or why the case could not be
/// analysed.</summary>
public sealed record VcfOptimizationResult(
    VcfOptimizationCase Case,
    double? MaxDeflectionMm,
    string? GoverningResult,
    string? Point,
    bool PretensionFitted,
    string? Failure,
    double? SpanM = null,
    double? LimitMm = null,
    double? CostSgd = null,
    string? CostNote = null,
    double? MaxUpliftMm = null,
    string? UpliftPoint = null,
    double? UpliftLimitMm = null,
    OptimizationResponse? Response = null,
    IReadOnlyList<MemberUtilization>? Members = null)
{
    /// <summary>True when the uplift is within its limit (<see cref="VcfOptimization.UpliftToleranceMm"/>); null when there is none to check.</summary>
    public bool? UpliftPasses => MaxUpliftMm is { } uplift && UpliftLimitMm is { } limit ? uplift <= limit + VcfOptimization.UpliftToleranceMm : null;

    /// <summary>True when the deflection is within the limit and the uplift within its own; null when there is no deflection.</summary>
    public bool? Passes => MaxDeflectionMm is { } deflection && LimitMm is { } limit ? deflection <= limit && UpliftPasses != false : null;
}

/// <summary>The structural responses of a case, read in the same result as the deflection: the base reactions at one end
/// (the sum over the supports on the left half — the two column bases of that end; over all four the horizontal force sums
/// to zero), the largest frame axial force and moment, the largest cable tension, and the mass of the system.</summary>
public sealed record OptimizationResponse(double BaseHorizontalKN, double BaseVerticalKN, double MaxFrameAxialKN, double MaxFrameMomentKNm,
    double MaxCableKN, double MassT);

public static class OptimizationResponses
{
    /// <summary>The responses of <paramref name="result"/> on <paramref name="geometry"/>. The horizontal base force is the
    /// magnitude of ΣRx over the supports left of the middle of the supports (one end).</summary>
    public static OptimizationResponse Of(GeometryModel geometry, IReadOnlyDictionary<string, FemSectionProperties> properties, FemAnalysisResult result)
    {
        var supports = result.SupportReactions.Where(reaction => geometry.Points.ContainsKey(reaction.PointName)).ToArray();
        var xs = supports.Select(reaction => geometry.Points[reaction.PointName].Position.X).ToArray();
        var middle = xs.Length == 0 ? 0 : (xs.Min() + xs.Max()) / 2;
        var oneEnd = supports.Where(reaction => geometry.Points[reaction.PointName].Position.X < middle - 1e-6).ToArray();
        double mass = 0;
        foreach (var member in geometry.Members)
        {
            if (!properties.TryGetValue(member.Name, out var property)) continue;
            var (a, b) = (geometry.Points[member.Start].Position, geometry.Points[member.End].Position);
            mass += property.MassPerLengthTPerM * Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
        }
        return new(
            Math.Abs(oneEnd.Sum(reaction => reaction.ReactionXKN)),
            oneEnd.Sum(reaction => reaction.ReactionZKN),
            result.Frames.SelectMany(frame => new[] { frame.AxialStartKN, frame.AxialEndKN }).Select(Math.Abs).DefaultIfEmpty().Max(),
            result.Frames.SelectMany(frame => new[] { frame.MomentStartKNm, frame.MomentEndKNm }).Select(Math.Abs).DefaultIfEmpty().Max(),
            result.Cables.Select(cable => cable.AxialForceKN).DefaultIfEmpty().Max(),
            mass);
    }
}

/// <summary>
/// Optimization → Optimization VCF, as a configuration of <see cref="ParametricOptimization"/>: the search space of
/// <see cref="VcfOptimizationSettings"/>, and each case built by the VCF generator from the open model — the generator's
/// system with another number of bays (<see cref="VcfGenerator.WithBays"/>) and the case's dimensions
/// (<see cref="VcfGenerator.Candidate"/>), the open model's member sections, SAP loading, Tab 01 point loads from qz ×
/// tributary length × B × NStories, and the cable targets of the case's step (<see cref="VcfOptimization.CableTargets"/>:
/// every cable steps together, the fan cables by their length). The deflection and uplift are read on the bottom chord
/// points. The open model is never changed.
/// </summary>
public sealed class VcfOptimizationStudy : IParametricStudy
{
    private readonly VcfOptimizationInputs inputs;
    private readonly VcfOptimizationSettings settings;
    private readonly Dictionary<int, (VcfGeometryAnalysis Model, VcfTopology? Topology)> byBays = [];

    public VcfOptimizationStudy(VcfOptimizationInputs inputs, VcfOptimizationSettings settings)
    {
        this.inputs = inputs;
        this.settings = settings;
        var axes = new List<VcfOptimizationAxis>();
        foreach (var (key, range) in settings.Geometry)
        {
            var label = new[] { VcfOptimization.Bays, VcfOptimization.BayWidth, VcfOptimization.Depth, VcfOptimization.Clear,
                VcfOptimization.BackSpan, VcfOptimization.BackStay }.FirstOrDefault(parameter => parameter.Key == key)?.Label ?? key;
            axes.Add(new("geometry:" + key, ShortLabel(key, label), range.Values().Cast<object>().ToArray()));
        }
        if (CableSteps(settings.CableForces, inputs.Analysis) is { } cableSteps) axes.Add(cableSteps);
        foreach (var (key, range) in settings.StoreyLoads)
            axes.Add(new("storey:" + key, key switch { VcfOptimization.StoriesKey => "NStories", VcfOptimization.TributaryKey => "B [m]", _ => $"qz {key} [kPa]" },
                range.Values().Cast<object>().ToArray()));
        foreach (var (role, sections) in settings.RoleSections) axes.Add(new("role:" + role, VcfGeometry.Title(role), sections.Cast<object>().ToArray()));
        foreach (var (cable, sections) in settings.CableSections) axes.Add(new("section:" + cable, $"{cable} section", sections.Cast<object>().ToArray()));
        Axes = axes;
        Count = settings.CombinationCount;
    }

    /// <summary>The one axis the cable forces step on, or null when no cable varies. Its column is named after the first
    /// cable that varies — the shortest fan cable when the fan is graded by length — and shows that cable's force.</summary>
    private static VcfOptimizationAxis? CableSteps(VcfCableForces forces, VcfGeometryAnalysis analysis)
    {
        if (forces.StepCount < 2) return null;
        var (name, span, rounded) = forces.Graded && forces.Fan is { IsFixed: false } fan
            ? (VcfGeometry.FanCables(analysis).FirstOrDefault()?.Name ?? "Fan cable", fan, true)
            : forces.Cables.Where(pair => !pair.Value.IsFixed).Select(pair => (pair.Key, pair.Value, false)).First();
        var steps = Enumerable.Range(0, forces.StepCount).Select(index =>
        {
            var force = span.At(forces.Fraction(index));
            return (object)new VcfCableStep(index, rounded ? VcfOptimization.RoundForce(force) : force);
        }).ToArray();
        return new(ParametricOptimization.CableStepKey, $"{name} [kN]", steps);
    }

    private static string ShortLabel(string key, string label) => key switch
    {
        "Bays" => "Bays", "Bay" => "Bay [m]", "Depth" => "Depth [m]", "Clear" => "Clear [m]", "BackSpan" => "Back-span [m]", "BackStay" => "Back-stay [m]",
        _ => label,
    };

    public IReadOnlyList<VcfOptimizationAxis> Axes { get; }
    public long Count { get; }

    private double Number(VcfOptimizationCase item, string key) => Convert.ToDouble(item.Values[IndexOf(key)], CultureInfo.InvariantCulture);
    private int IndexOf(string key) => Axes.ToList().FindIndex(axis => axis.Key == key);
    private bool Has(string key) => IndexOf(key) >= 0;

    /// <summary>The VCF system of a case, with its sections, loads and cable targets.</summary>
    public ParametricCandidate Candidate(VcfOptimizationCase item)
    {
        var analysis = inputs.Analysis;
        var bays = (int)Number(item, "geometry:" + VcfOptimization.Bays.Key);
        var (model, topology) = ModelFor(bays);
        double Geometry(VcfOptimization.Parameter parameter, double fallback) =>
            Has("geometry:" + parameter.Key) ? Number(item, "geometry:" + parameter.Key) : fallback;
        var p = analysis.Parameters;
        var depth = Geometry(VcfOptimization.Depth, p.TrussDepth);
        var form = new VcfGeometryParameters(Geometry(VcfOptimization.BayWidth, p.BayWidth), depth,
            analysis.HasMiddleChord ? VcfOptimization.MiddleChordRatio * depth : p.MiddleChordHeight, Geometry(VcfOptimization.Clear, p.ClearHeight),
            p.BackSpan is null ? null : Geometry(VcfOptimization.BackSpan, p.BackSpan.Value),
            p.BackStayHeight is null ? null : Geometry(VcfOptimization.BackStay, p.BackStayHeight.Value));
        var problems = VcfGeometry.Validate(model, form);
        if (problems.Count > 0) throw new ParametricCandidateException("Invalid geometry: " + string.Join(" ", problems));
        var geometry = VcfGenerator.Candidate(model, form);

        var properties = Properties(item, model, topology, geometry);
        var applied = AppliedPoints(model, topology, geometry);
        // the case is analysed as Run FEM Analysis analyses the model: as a native document, by the same solver path
        var setup = new FemCandidateSetup(Loading(topology, geometry), inputs.Patterns, inputs.Cases, inputs.Combinations, inputs.Tab01Patterns,
            inputs.PretensionPatterns, applied, inputs.TargetFitPercent, inputs.TargetFitIterations, inputs.FrameMeshSegments, inputs.CableSag,
            inputs.ActiveDofs, inputs.CableDofs) { MassSources = inputs.MassSources };
        // Both limits are read on the bottom chord points (BTrPt…) only — the points the deflection is monitored at. Over
        // every joint the downward maximum is the top of a mega column once the fan lifts the span, and a back-stay
        // pulls the outer column tops up.
        return new($"OPT{item.Index + 1:00000}", geometry, properties, setup, PointLoads(item, geometry, applied),
            Targets(item, model, topology, geometry, properties), TargetPatterns(topology),
            model.BottomPoints.ToHashSet(StringComparer.Ordinal), "bottom chord point", bays * form.BayWidth, EffectiveLengths(model, geometry),
            model.Roles.ToDictionary(pair => pair.Key, pair => VcfGeometry.Title(pair.Value), StringComparer.Ordinal));
    }
    /// <summary>l0 of every member of the case: its role's length of the settings, else the default.</summary>
    private Dictionary<string, double> EffectiveLengths(VcfGeometryAnalysis model, GeometryModel geometry) =>
        geometry.Members.ToDictionary(member => member.Name, member =>
            model.Roles.TryGetValue(member.Name, out var role) && settings.EffectiveLengths.TryGetValue(role, out var length) ? length : VcfOptimization.DefaultEffectiveLengthM,
            StringComparer.Ordinal);

    /// <summary>The load pattern of each cable's target force: its own Tab 03 pattern, or — a cable the bays add — that of
    /// the cable it copies.</summary>
    private Dictionary<string, string> TargetPatterns(VcfTopology? topology)
    {
        var patterns = inputs.Cables.Where(cable => !string.IsNullOrWhiteSpace(cable.LoadPattern))
            .GroupBy(cable => cable.CableName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().LoadPattern, StringComparer.Ordinal);
        if (topology is not null)
            foreach (var (added, source) in topology.AddedCables)
                if (patterns.TryGetValue(source, out var pattern)) patterns.TryAdd(added, pattern);
        return patterns;
    }

    /// <summary>The system with <paramref name="bays"/> bays, by the VCF generator from the open model (once per number).</summary>
    private (VcfGeometryAnalysis Model, VcfTopology? Topology) ModelFor(int bays)
    {
        if (byBays.TryGetValue(bays, out var cached)) return cached;
        return byBays[bays] = VcfGenerator.WithBays(inputs.Analysis, bays);
    }

    /// <summary>Member properties: a regenerated member copies its template's, then the case's sections are assigned.</summary>
    private Dictionary<string, FemSectionProperties> Properties(VcfOptimizationCase item, VcfGeometryAnalysis model, VcfTopology? topology, GeometryModel geometry)
    {
        var properties = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal);
        foreach (var member in geometry.Members)
            if (topology is not null && topology.Templates.TryGetValue(member.Name, out var template) && template != member.Name &&
                inputs.Properties.TryGetValue(template, out var copy))
                properties[member.Name] = copy with { MemberName = member.Name };
            else if (inputs.Properties.TryGetValue(member.Name, out var own)) properties[member.Name] = own;

        for (var axis = 0; axis < Axes.Count; axis++)
        {
            var key = Axes[axis].Key;
            if (item.Values[axis] is not string section) continue;
            IEnumerable<string> members = key.StartsWith("role:", StringComparison.Ordinal)
                ? model.Roles.Where(pair => pair.Value.ToString() == key[5..]).Select(pair => pair.Key)
                : key.StartsWith("section:", StringComparison.Ordinal)
                    ? geometry.Members.Select(member => member.Name).Where(name => name == key[8..] ||
                        topology is not null && topology.AddedCables.TryGetValue(name, out var source) && source == key[8..])
                    : [];
            foreach (var member in members)
                if (properties.TryGetValue(member, out var current) && current.SectionName != section)
                    properties[member] = inputs.SwapSection(current, section) with { MemberName = member };
        }
        return properties;
    }

    /// <summary>The SAP loading of the model: with another number of bays, loads move with renamed points and loads on
    /// removed objects are dropped (as Edit → VCF Geometry does).</summary>
    private SapFemLoading Loading(VcfTopology? topology, GeometryModel geometry)
    {
        if (topology is null) return inputs.Loading;
        var points = geometry.Points.Keys.ToHashSet(StringComparer.Ordinal);
        var members = geometry.Members.Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        string Map(string point) => topology.RenamedPoints.TryGetValue(point, out var renamed) ? renamed : point;
        var sap = inputs.Loading;
        return sap with
        {
            JointLoads = sap.JointLoads.Select(load => load with { Load = load.Load with { PointName = Map(load.Load.PointName) } })
                .Where(load => points.Contains(load.Load.PointName)).ToArray(),
            MemberLoads = sap.MemberLoads.Where(load => members.Contains(load.Load.MemberName)).ToArray(),
            MemberPointLoads = sap.MemberPointLoads.Where(load => members.Contains(load.Load.MemberName)).ToArray(),
            GravityLoads = sap.GravityLoads.Where(load => members.Contains(load.MemberName)).ToArray(),
        };
    }

    /// <summary>Tab 01 loaded points in the case's model: every top chord point when every one was loaded, else the loaded
    /// points that still exist (renamed with the end line).</summary>
    private IReadOnlyList<string> AppliedPoints(VcfGeometryAnalysis model, VcfTopology? topology, GeometryModel geometry)
    {
        var applied = inputs.AppliedPoints;
        var top = inputs.Analysis.TopPoints;
        var allTop = top.Count > 0 && applied.Count == top.Count && top.All(applied.Contains);
        IEnumerable<string> names = allTop ? model.TopPoints
            : applied.Select(name => topology is not null && topology.RenamedPoints.TryGetValue(name, out var renamed) ? renamed : name);
        return names.Where(geometry.Points.ContainsKey).Distinct(StringComparer.Ordinal)
            .OrderBy(name => geometry.Points[name].Position.X).ThenBy(name => name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Fz = −qz × tributary length × B × NStories at every loaded point, per load definition (as Tab 01).</summary>
    private IReadOnlyList<FemAppliedLoad> PointLoads(VcfOptimizationCase item, GeometryModel geometry, IReadOnlyList<string> applied)
    {
        var stories = Math.Truncate(Number(item, "storey:" + VcfOptimization.StoriesKey));
        var width = Number(item, "storey:" + VcfOptimization.TributaryKey);
        var qz = Axes.Select((axis, index) => (axis.Key, Index: index))
            .Where(entry => entry.Key.StartsWith("storey:", StringComparison.Ordinal) &&
                entry.Key != "storey:" + VcfOptimization.StoriesKey && entry.Key != "storey:" + VcfOptimization.TributaryKey)
            .Select(entry => (Name: entry.Key[7..], Value: Convert.ToDouble(item.Values[entry.Index], CultureInfo.InvariantCulture))).ToArray();
        double X(int i) => geometry.Points[applied[i]].Position.X;
        var loads = new List<FemAppliedLoad>(applied.Count * qz.Length);
        for (var i = 0; i < applied.Count; i++)
        {
            var length = applied.Count switch
            {
                1 => 0,
                _ when i == 0 => Math.Max(0, X(1) - X(0)) / 2,
                _ when i == applied.Count - 1 => Math.Max(0, X(applied.Count - 1) - X(applied.Count - 2)) / 2,
                _ => Math.Max(0, X(i + 1) - X(i - 1)) / 2,
            };
            foreach (var (name, value) in qz) loads.Add(new FemAppliedLoad(applied[i], 0, -value * length * width * stories, name));
        }
        return loads;
    }

    /// <summary>Target force of every analysed cable at the case's cable step (<see cref="VcfOptimization.CableTargets"/>).</summary>
    private Dictionary<string, double> Targets(VcfOptimizationCase item, VcfGeometryAnalysis model, VcfTopology? topology, GeometryModel geometry,
        IReadOnlyDictionary<string, FemSectionProperties> properties)
    {
        var axis = IndexOf(ParametricOptimization.CableStepKey);
        var step = axis >= 0 && item.Values[axis] is VcfCableStep cableStep ? cableStep.Index : 0;
        return VcfOptimization.CableTargets(settings.CableForces, step, model, geometry, topology?.AddedCables, inputs.Cables)
            .Where(pair => properties.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}
