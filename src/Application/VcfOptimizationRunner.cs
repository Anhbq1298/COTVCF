using System.Globalization;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Application;

/// <summary>Everything a Run → Optimization VCF case needs from the working model, frozen when the run starts.
/// <see cref="SwapSection"/> gives a member's properties with another section (from a member that has it, or the section
/// library), as Edit → VCF Geometry assigns sections.</summary>
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
    bool CableSag);

/// <summary>One variable of the search space: its key ("geometry:Bay", "cable:Cable1a-2", "storey:LL", "role:TopChord",
/// "section:Cable1a-2"), a column label and its values (numbers, or section names).</summary>
public sealed record VcfOptimizationAxis(string Key, string Label, IReadOnlyList<object> Values)
{
    public bool Varies => Values.Count > 1;
}

/// <summary>One combination: the value picked on every axis (same order as <see cref="VcfOptimizationRunner.Axes"/>).</summary>
public sealed record VcfOptimizationCase(long Index, IReadOnlyList<object> Values);

/// <summary>A case's outcome: the largest downward deflection in the deflection result (or over every solved result when
/// none is chosen), where it occurs, the span and the limit span / ratio — or why the case could not be analysed.</summary>
public sealed record VcfOptimizationResult(
    VcfOptimizationCase Case,
    double? MaxDeflectionMm,
    string? GoverningResult,
    string? Point,
    bool PretensionFitted,
    string? Failure,
    double? SpanM = null,
    double? LimitMm = null)
{
    /// <summary>True when the deflection is within the limit; null when there is no deflection.</summary>
    public bool? Passes => MaxDeflectionMm is { } deflection && LimitMm is { } limit ? deflection <= limit : null;
}

/// <summary>
/// Run → Optimization VCF: walks the search space of <see cref="VcfOptimizationSettings"/> case by case. Each case is
/// built in memory from the working model — geometry (with another number of bays when asked), member sections, SAP
/// loading, Tab 01 point loads from qz × tributary length × B × NStories, Tab 03 cable targets (followers keep their sync
/// rule) — and analysed with the same internal FEM as Run → Run FEM Analysis. The working model is never changed.
/// </summary>
public sealed class VcfOptimizationRunner
{
    public const int MaxCases = 10_000;
    private readonly VcfOptimizationInputs inputs;
    private readonly VcfOptimizationSettings settings;
    private readonly FemStudyService fem = new();
    private readonly Dictionary<int, (VcfGeometryAnalysis Model, VcfTopology? Topology)> byBays = [];
    private readonly Dictionary<string, CablePretensionConfig> configs;

    public VcfOptimizationRunner(VcfOptimizationInputs inputs, VcfOptimizationSettings settings)
    {
        this.inputs = inputs;
        this.settings = settings;
        configs = inputs.Cables.GroupBy(cable => cable.CableName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var axes = new List<VcfOptimizationAxis>();
        foreach (var (key, range) in settings.Geometry)
        {
            var label = new[] { VcfOptimization.Bays, VcfOptimization.BayWidth, VcfOptimization.Depth, VcfOptimization.Clear,
                VcfOptimization.BackSpan, VcfOptimization.BackStay }.FirstOrDefault(parameter => parameter.Key == key)?.Label ?? key;
            axes.Add(new("geometry:" + key, ShortLabel(key, label), range.Values().Cast<object>().ToArray()));
        }
        foreach (var (cable, range) in settings.CableForces) axes.Add(new("cable:" + cable, $"{cable} [kN]", range.Values().Cast<object>().ToArray()));
        foreach (var (key, range) in settings.StoreyLoads)
            axes.Add(new("storey:" + key, key switch { VcfOptimization.StoriesKey => "NStories", VcfOptimization.TributaryKey => "B [m]", _ => $"qz {key} [kPa]" },
                range.Values().Cast<object>().ToArray()));
        foreach (var (role, sections) in settings.RoleSections) axes.Add(new("role:" + role, VcfGeometry.Title(role), sections.Cast<object>().ToArray()));
        foreach (var (cable, sections) in settings.CableSections) axes.Add(new("section:" + cable, $"{cable} section", sections.Cast<object>().ToArray()));
        Axes = axes;
        Count = settings.CombinationCount;
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
            if (problems.Count > 0) return Failed(item, "Invalid geometry: " + string.Join(" ", problems));
            var geometry = VcfGeometry.Build(model, form);

            var properties = Properties(item, model, topology, geometry);
            var loading = Loading(topology, geometry);
            var applied = AppliedPoints(model, topology, geometry);
            var loads = PointLoads(item, geometry, applied);
            var targets = Targets(item, topology, geometry, properties);
            var plan = FemLoadPlan.Build(inputs.Patterns, inputs.Cases, inputs.Combinations, loading, properties, geometry,
                inputs.Tab01Patterns, inputs.PretensionPatterns, applied);
            var supports = FemStudyService.FindPinSupportPoints(geometry);
            var candidate = new FemStudyCandidate($"OPT{item.Index + 1:00000}", "L", "H", "P",
                new TrussCandidate(1, depth, 0, "Bottom", 0, 0, 0), loads, targets);
            var failure = new FailureCapture();
            var runs = fem.Analyze(geometry, model.TopPoints, model.MiddlePoints, model.BottomPoints, supports, [candidate], properties,
                failure, cancellationToken, inputs.TargetFitPercent, inputs.TargetFitIterations, inputs.FrameMeshSegments, plan, inputs.CableSag);
            if (runs.Count == 0) return Failed(item, failure.Message ?? "The case did not reach equilibrium.");
            var run = runs[0];
            IReadOnlyDictionary<string, FemAnalysisResult> results = run.Results ?? new Dictionary<string, FemAnalysisResult> { [plan.ResultNames.FirstOrDefault() ?? "Result"] = run.Result };
            if (settings.DeflectionResult is { } wanted)
            {
                var match = results.Keys.FirstOrDefault(key => string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase));
                if (match is null) return Failed(item, $"'{wanted}' was not solved — tick it to run on Define → Load Cases / Load Combinations.");
                results = new Dictionary<string, FemAnalysisResult> { [match] = results[match] };
            }
            var (name, point, deflection) = results
                .SelectMany(pair => pair.Value.Joints.Select(joint => (Name: pair.Key, Point: joint.PointName, Mm: -joint.UZM * 1000)))
                .DefaultIfEmpty(("", "", 0)).MaxBy(entry => entry.Item3);
            var span = bays * form.BayWidth;
            return new(item, deflection, name, point, results.Values.All(result => result.TargetFitConverged), null, span, span * 1000 / settings.SpanRatio);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return Failed(item, ex.Message);
        }
    }

    private static VcfOptimizationResult Failed(VcfOptimizationCase item, string reason) => new(item, null, null, null, false, reason);

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

    /// <summary>Target force of every analysed cable: an independent cable takes the case's value, a follower its Tab 03
    /// sync rule applied to its reference's value; a new fan cable takes the value of the cable it copies.</summary>
    private Dictionary<string, double> Targets(VcfOptimizationCase item, VcfTopology? topology, GeometryModel geometry,
        IReadOnlyDictionary<string, FemSectionProperties> properties)
    {
        var resolved = new Dictionary<string, (double Value, double Fraction)?>(StringComparer.Ordinal);
        (double Value, double Fraction)? Resolve(string name, int depth)
        {
            if (resolved.TryGetValue(name, out var done)) return done;
            if (depth > 32 || !configs.TryGetValue(name, out var cable)) return resolved[name] = null;
            var axis = IndexOf("cable:" + name);
            if (axis >= 0)
            {
                var values = Axes[axis].Values;
                var value = Convert.ToDouble(item.Values[axis], CultureInfo.InvariantCulture);
                var position = values.ToList().IndexOf(item.Values[axis]);
                return resolved[name] = (value, values.Count > 1 ? (double)position / (values.Count - 1) : 0);
            }
            if (!cable.IsFollower || Resolve(cable.ReferenceCableName!, depth + 1) is not { } reference) return resolved[name] = null;
            var factor = cable.SyncFactor;
            return resolved[name] = cable.SyncMode switch
            {
                CableSyncMode.SameValues => reference,
                CableSyncMode.SameRatio => (reference.Value * (factor ?? 1), reference.Fraction),
                CableSyncMode.SameOffset => (reference.Value + (factor ?? 0), reference.Fraction),
                CableSyncMode.SameStep when cable.MinForce is { } low && cable.MaxForce is { } high => (low + (high - low) * reference.Fraction, reference.Fraction),
                _ => null,
            };
        }
        var targets = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var member in geometry.Members.Where(member => member.Kind != MemberKind.Frame && properties.ContainsKey(member.Name)))
        {
            var source = topology is not null && topology.AddedCables.TryGetValue(member.Name, out var template) ? template : member.Name;
            if (Resolve(source, 0) is { } target) targets[member.Name] = target.Value;
        }
        return targets;
    }

    /// <summary>Text of a case's value on an axis, for the results table.</summary>
    public static string Text(object value) => value switch
    {
        double number => number.ToString("0.###", CultureInfo.CurrentCulture),
        _ => value.ToString() ?? "",
    };
}
