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

/// <summary>One combination: the value picked on every axis (same order as <see cref="VcfOptimizationRunner.Axes"/>).</summary>
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
    OptimizationResponse? Response = null)
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
/// Run → Optimization VCF: walks the search space of <see cref="VcfOptimizationSettings"/> case by case. Each case is
/// built in memory from the working model — geometry (with another number of bays when asked), member sections, SAP
/// loading, Tab 01 point loads from qz × tributary length × B × NStories, the cable targets of the case's step
/// (<see cref="VcfOptimization.CableTargets"/>: every cable steps together, the fan cables by their length) — and analysed
/// with the same internal FEM as Run → Run FEM Analysis. The working model is never changed.
/// </summary>
public sealed class VcfOptimizationRunner : IOptimizationRunner
{
    public const int MaxCases = 10_000;
    public const string CableStepKey = "cable:step";
    private readonly VcfOptimizationInputs inputs;
    private readonly VcfOptimizationSettings settings;
    private readonly FemStudyService fem = new();
    private readonly Dictionary<int, (VcfGeometryAnalysis Model, VcfTopology? Topology)> byBays = [];

    public VcfOptimizationRunner(VcfOptimizationInputs inputs, VcfOptimizationSettings settings)
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
        return new(CableStepKey, $"{name} [kN]", steps);
    }

    private static string ShortLabel(string key, string label) => key switch
    {
        "Bays" => "Bays", "Bay" => "Bay [m]", "Depth" => "Depth [m]", "Clear" => "Clear [m]", "BackSpan" => "Back-span [m]", "BackStay" => "Back-stay [m]",
        _ => label,
    };

    public IReadOnlyList<VcfOptimizationAxis> Axes { get; }
    public long Count { get; }

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

    private double Number(VcfOptimizationCase item, string key) => Convert.ToDouble(item.Values[IndexOf(key)], CultureInfo.InvariantCulture);
    private int IndexOf(string key) => Axes.ToList().FindIndex(axis => axis.Key == key);
    private bool Has(string key) => IndexOf(key) >= 0;

    /// <summary>Builds and analyses one case. Never throws for an invalid or non-converging case — the result says why.</summary>
    public VcfOptimizationResult Run(VcfOptimizationCase item, CancellationToken cancellationToken = default)
    {
        try
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
            if (problems.Count > 0) return VcfOptimizationRunner.Failed(item, "Invalid geometry: " + string.Join(" ", problems));
            var geometry = VcfGeometry.Build(model, form);

            var properties = Properties(item, model, topology, geometry);
            var (cost, costNote) = Cost(geometry, properties);
            VcfOptimizationResult Failed(string reason) => VcfOptimizationRunner.Failed(item, reason) with { CostSgd = cost, CostNote = costNote };
            var loading = Loading(topology, geometry);
            var applied = AppliedPoints(model, topology, geometry);
            var loads = PointLoads(item, geometry, applied);
            var targets = Targets(item, model, topology, geometry, properties);
            var plan = FemLoadPlan.Build(inputs.Patterns, inputs.Cases, inputs.Combinations, loading, properties, geometry,
                inputs.Tab01Patterns, inputs.PretensionPatterns, applied);
            var supports = FemStudyService.FindPinSupportPoints(geometry);
            var candidate = new FemStudyCandidate($"OPT{item.Index + 1:00000}", "L", "H", "P",
                new TrussCandidate(1, depth, 0, "Bottom", 0, 0, 0), loads, targets);
            var failure = new FailureCapture();
            var runs = fem.Analyze(geometry, model.TopPoints, model.MiddlePoints, model.BottomPoints, supports, [candidate], properties,
                failure, cancellationToken, inputs.TargetFitPercent, inputs.TargetFitIterations, inputs.FrameMeshSegments, plan, inputs.CableSag,
                activeDofs: inputs.ActiveDofs, cableDofs: inputs.CableDofs);
            if (runs.Count == 0) return Failed(failure.Message ?? "The case did not reach equilibrium.");
            var run = runs[0];
            IReadOnlyDictionary<string, FemAnalysisResult> results = run.Results ?? new Dictionary<string, FemAnalysisResult> { [plan.PrimaryResultName] = run.Result };
            if (settings.DeflectionResult is { } wanted)
            {
                var match = results.Keys.FirstOrDefault(key => string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase));
                if (match is null) return Failed($"'{wanted}' was not solved — tick it to run on Define → Load Cases / Load Combinations.");
                results = new Dictionary<string, FemAnalysisResult> { [match] = results[match] };
            }
            // Both limits are read on the bottom chord points (BTrPt…) only — the points the deflection is monitored at. Over
            // every joint the downward maximum is the top of a mega column once the fan lifts the span, and a back-stay
            // pulls the outer column tops up.
            var bottom = model.BottomPoints.ToHashSet(StringComparer.Ordinal);
            var checkedJoints = results
                .SelectMany(pair => pair.Value.Joints.Where(joint => bottom.Contains(joint.PointName)).Select(joint => (Name: pair.Key, Point: joint.PointName, Mm: -joint.UZM * 1000)))
                .ToArray();
            if (checkedJoints.Length == 0) return Failed("No bottom chord point has a displacement to check.");
            var (name, point, deflection) = checkedJoints.MaxBy(entry => entry.Mm);
            var (_, upliftPoint, risen) = checkedJoints.MinBy(entry => entry.Mm);
            var uplift = -risen;
            var span = bays * form.BayWidth;
            return new(item, deflection, name, point, results.Values.All(result => result.TargetFitConverged), null, span, span * 1000 / settings.SpanRatio,
                cost, costNote, Math.Max(0, uplift), uplift > 0 ? upliftPoint : null, settings.UpliftLimitMm, OptimizationResponses.Of(geometry, properties, results[name]));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return VcfOptimizationRunner.Failed(item, ex.Message);
        }
    }

    private static VcfOptimizationResult Failed(VcfOptimizationCase item, string reason) => new(item, null, null, null, false, reason);

    /// <summary>Total cost of the case's members: Σ cost per metre × member length (frames and cables of the analysed model).
    /// Null with a note naming the sections / materials without a unit cost.</summary>
    private (double? Sgd, string? Note) Cost(GeometryModel geometry, IReadOnlyDictionary<string, FemSectionProperties> properties)
    {
        if (inputs.CostPerMetre is null) return (null, null);
        double total = 0;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var member in geometry.Members)
        {
            if (!properties.TryGetValue(member.Name, out var property)) continue;
            var (start, end) = (geometry.Points[member.Start].Position, geometry.Points[member.End].Position);
            var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2) + Math.Pow(end.Z - start.Z, 2));
            var (rate, why) = inputs.CostPerMetre(property);
            if (rate is { } sgdPerM) total += sgdPerM * length;
            else missing.Add(why ?? property.SectionName ?? member.Name);
        }
        return missing.Count == 0 ? (total, null) : (null, "No unit cost for " + string.Join(", ", missing));
    }

    private sealed class FailureCapture : IProgress<FemProgress>
    {
        public string? Message { get; private set; }
        public void Report(FemProgress value) { if (value.Failure is not null) Message = value.Failure; }
    }

    private (VcfGeometryAnalysis Model, VcfTopology? Topology) ModelFor(int bays)
    {
        if (byBays.TryGetValue(bays, out var cached)) return cached;
        var analysis = inputs.Analysis;
        if (bays == analysis.Bays) return byBays[bays] = (analysis, null);
        var topology = VcfGeometry.ChangeBays(analysis, bays);
        return byBays[bays] = (VcfGeometry.Analyze(topology), topology);
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
        var axis = IndexOf(CableStepKey);
        var step = axis >= 0 && item.Values[axis] is VcfCableStep cableStep ? cableStep.Index : 0;
        return VcfOptimization.CableTargets(settings.CableForces, step, model, geometry, topology?.AddedCables, inputs.Cables)
            .Where(pair => properties.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>Text of a case's value on an axis, for the results table.</summary>
    public static string Text(object value) => value switch
    {
        double number => number.ToString("0.###", CultureInfo.CurrentCulture),
        VcfCableStep step => step.ForceKN.ToString("0.###", CultureInfo.CurrentCulture),
        _ => value.ToString() ?? "",
    };
}
