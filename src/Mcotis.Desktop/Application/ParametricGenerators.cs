using System.Globalization;
using System.IO;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application;

/// <summary>One object of a generated system that the model no longer holds as its generator made it.</summary>
public sealed record GeneratorFinding(string Object, string Reason)
{
    public override string ToString() => $"{Object}: {Reason}";
}

/// <summary>
/// A generator's verdict on a flagged model (task of 2026-10-14, §2): the system rebuilt from the flag's parameters and
/// compared with the model, object by object, by <see cref="Native.ObjectOrigin.GenerationKey"/>. Compatible edits —
/// sections, materials, assignments other than the supports' restraints, loads, cases, combinations, mass sources,
/// analysis options — are not compared: the model stays optimizable and a study starts from them. What is listed breaks
/// the system: a generated object deleted, a generated joint moved, a generated member reconnected, an object added to
/// the system, a restraint changed on a generated joint. <see cref="Refusal"/> says why there is nothing to compare.
/// </summary>
public sealed record GeneratorValidationReport(string GeneratorId, IReadOnlyList<GeneratorFinding> Breaking, string? Refusal = null)
{
    public bool IsValid => Refusal is null && Breaking.Count == 0;

    public string Text(int most = 6) => Refusal ?? (Breaking.Count == 0
        ? $"The model is the {GeneratorId} system of its parameters; the edits so far keep it."
        : $"{Breaking.Count} edit{(Breaking.Count == 1 ? "" : "s")} break the {GeneratorId} system — " +
          string.Join("; ", Breaking.Take(most)) + (Breaking.Count > most ? $"; and {Breaking.Count - most} more" : "") +
          ". Undo them, re-apply the generator, or detach the model from it.");
}

/// <summary>One parameter of a generator, as its schema lists it.</summary>
internal sealed record GeneratorParameter(string Name, string Unit, string Description);

/// <summary>What a generator makes of its parameters: the geometry by name, the restraints it gives, and the generation key
/// of each joint and member by name.</summary>
internal sealed record GeneratedSystem(GeometryModel Geometry, IReadOnlyDictionary<string, Native.Restraint> Restraints,
    IReadOnlyDictionary<string, string> JointKeys, IReadOnlyDictionary<string, string> MemberKeys);

/// <summary>
/// A parametric generator (task of 2026-10-14, §2): it generates a system from its parameters, lists them, and validates
/// a model against the flag it wrote. A model is optimizable by this and nothing else — never by its name or file.
/// </summary>
internal interface IParametricGenerator
{
    string Id { get; }
    int Version { get; }
    IReadOnlyList<GeneratorParameter> Schema { get; }
    GeneratedSystem Generate(IReadOnlyDictionary<string, string> parameters);
    GeneratorValidationReport Validate(Native.ModelDocument document);
}

internal static class ParametricGenerators
{
    public static IReadOnlyList<IParametricGenerator> All { get; } = [VcfGenerator.Instance, TasGenerator.Instance];

    public static IParametricGenerator? Find(string id) => All.FirstOrDefault(generator => generator.Id == id);

    /// <summary>The document validated against its own flag by the generator that wrote it; null for a document without a flag.</summary>
    public static GeneratorValidationReport? Validate(Native.ModelDocument document) =>
        document.Generator is not { } flag ? null
        : Find(flag.GeneratorId) is { } generator ? generator.Validate(document)
        : new(flag.GeneratorId, [], $"The model was made by a generator this version of Mcotis does not have ({flag.GeneratorId}).");

    /// <summary>The key of each member: frame or cable, and the keys of its two joints in either order. Two members in one
    /// place are told apart by their order of name.</summary>
    public static Dictionary<string, string> MemberKeys(GeometryModel geometry, IReadOnlyDictionary<string, string> jointKeys)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in geometry.Members.OrderBy(member => member.Name, StringComparer.Ordinal))
        {
            if (!jointKeys.TryGetValue(member.Start, out var a) || !jointKeys.TryGetValue(member.End, out var b)) continue;
            var (low, high) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
            var key = $"{(member.Kind == MemberKind.Frame ? "F" : "C")}:{low}|{high}";
            var n = count.GetValueOrDefault(key) + 1;
            count[key] = n;
            keys[member.Name] = n == 1 ? key : $"{key}#{n}";
        }
        return keys;
    }

    /// <summary>The flag a generator writes on a model it made: its parameters and the key of every object, by name.</summary>
    public static FemGeneratorDto Flag(IParametricGenerator generator, IReadOnlyDictionary<string, string> parameters,
        GeometryModel geometry, IReadOnlyDictionary<string, string> jointKeys) =>
        new(generator.Id, generator.Version, new Dictionary<string, string>(parameters, StringComparer.Ordinal))
        {
            Joints = jointKeys.Where(item => geometry.Points.ContainsKey(item.Key)).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            Members = MemberKeys(geometry, jointKeys),
        };

    /// <summary>A model of the application validated against a flag before the flag is written — how a configuration
    /// saved before flags existed is given one (only if it validates).</summary>
    public static GeneratorValidationReport Validate(FemGeneratorDto flag, GeometryModel geometry, IReadOnlyDictionary<string, Native.Restraint> restraints)
    {
        if (Find(flag.Id) is not ParametricGenerator generator)
            return new(flag.Id, [], $"The model was made by a generator this version of Mcotis does not have ({flag.Id}).");
        var joints = geometry.Points.Values.Select(point => new ModelJoint(point.Name, flag.Joints?.GetValueOrDefault(point.Name),
            new(point.Position.X, point.Position.Y, point.Position.Z), restraints.GetValueOrDefault(point.Name, Native.Restraint.Free))).ToArray();
        var members = geometry.Members.Select(member => new ModelMember(member.Kind == MemberKind.Frame ? "Frame" : "Cable", member.Name,
            flag.Members?.GetValueOrDefault(member.Name), member.Start, member.End)).ToArray();
        return generator.Validate(flag.Version, flag.Parameters, new(joints, members));
    }

    internal sealed record ModelJoint(string Name, string? Key, Native.Xyz Position, Native.Restraint Restraint);
    internal sealed record ModelMember(string Type, string Name, string? Key, string Start, string End);
    internal sealed record ModelObjects(IReadOnlyList<ModelJoint> Joints, IReadOnlyList<ModelMember> Members, int Shells = 0);

    internal static string Text(Native.Restraint restraint)
    {
        if (restraint == Native.Restraint.Fixed) return "fixed";
        if (restraint == Native.Restraint.Pinned) return "pinned";
        var held = new[] { (restraint.Ux, "UX"), (restraint.Uy, "UY"), (restraint.Uz, "UZ"), (restraint.Rx, "RX"), (restraint.Ry, "RY"), (restraint.Rz, "RZ") }
            .Where(item => item.Item1).Select(item => item.Item2).ToArray();
        return held.Length == 0 ? "free" : string.Join(" ", held);
    }

    internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    internal static double Number(IReadOnlyDictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : throw new InvalidDataException($"The generator parameter {name} is missing or not a number.");

    internal static int Whole(IReadOnlyDictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw new InvalidDataException($"The generator parameter {name} is missing or not a whole number.");
}

/// <summary>The comparison every generator makes: the system rebuilt from the parameters against the model, by key.</summary>
internal abstract class ParametricGenerator : IParametricGenerator
{
    /// <summary>A generated joint further than this from its place has been moved [m].</summary>
    public const double PositionTolerance = 1e-5;

    public abstract string Id { get; }
    public abstract int Version { get; }
    public abstract IReadOnlyList<GeneratorParameter> Schema { get; }
    public abstract GeneratedSystem Generate(IReadOnlyDictionary<string, string> parameters);

    public GeneratorValidationReport Validate(Native.ModelDocument document)
    {
        if (document.Generator is not { } flag || flag.GeneratorId != Id)
            return new(Id, [], $"The model was not made by the {Id} generator.");
        var prefix = Id + ":";
        string? Key(Native.ObjectOrigin? origin) =>
            origin is { Kind: Native.OriginKind.Generated, GenerationKey: { } key } && key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : null;
        var joints = document.Points.Values.Select(point => new ParametricGenerators.ModelJoint(point.Name, Key(point.Origin), point.Position,
            document.RestraintOf(point.Id))).ToArray();
        string Name(Native.ObjectId id) => document.Points.TryGetValue(id, out var point) ? point.Name : "?";
        var members = document.Frames.Values.Select(frame => new ParametricGenerators.ModelMember("Frame", frame.Name, Key(frame.Origin),
                Name(frame.StartPointId), Name(frame.EndPointId)))
            .Concat(document.Cables.Values.Select(cable => new ParametricGenerators.ModelMember("Cable", cable.Name, Key(cable.Origin),
                Name(cable.StartPointId), Name(cable.EndPointId)))).ToArray();
        return Validate(flag.Version, flag.Parameters, new(joints, members, document.Shells.Count));
    }

    internal GeneratorValidationReport Validate(int version, IReadOnlyDictionary<string, string> parameters, ParametricGenerators.ModelObjects model)
    {
        if (version != Version)
            return new(Id, [], $"The model was made by version {version} of the {Id} generator; this version of Mcotis has version {Version}.");
        GeneratedSystem expected;
        try { expected = Generate(parameters); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FormatException or KeyNotFoundException)
        {
            return new(Id, [], $"The {Id} generator cannot rebuild the model from its parameters: {ex.Message}");
        }

        var findings = new List<GeneratorFinding>();
        var byKey = new Dictionary<string, ParametricGenerators.ModelJoint>(StringComparer.Ordinal);
        foreach (var joint in model.Joints.Where(joint => joint.Key is not null).OrderBy(joint => joint.Name, StringComparer.Ordinal))
            if (!byKey.TryAdd(joint.Key!, joint)) findings.Add(new($"Joint {joint.Name}", $"a second joint in the place of generated joint {byKey[joint.Key!].Name}"));
        var keyOfJoint = model.Joints.Where(joint => joint.Key is not null).GroupBy(joint => joint.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key!, StringComparer.Ordinal);

        foreach (var (name, key) in expected.JointKeys.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!byKey.TryGetValue(key, out var joint)) { findings.Add(new($"Joint {name}", "deleted")); continue; }
            var place = expected.Geometry.Points[name].Position;
            var distance = Math.Sqrt(Math.Pow(joint.Position.X - place.X, 2) + Math.Pow(joint.Position.Y - place.Y, 2) + Math.Pow(joint.Position.Z - place.Z, 2));
            if (distance > PositionTolerance)
                findings.Add(new($"Joint {joint.Name}", $"moved {distance:0.###} m from where the generator places it"));
            var generated = expected.Restraints.GetValueOrDefault(name, Native.Restraint.Free);
            if (joint.Restraint != generated)
                findings.Add(new($"Joint {joint.Name}", $"restraint {ParametricGenerators.Text(joint.Restraint)} where the generator has {ParametricGenerators.Text(generated)}"));
        }

        var members = expected.Geometry.Members.Where(member => expected.MemberKeys.ContainsKey(member.Name))
            .ToDictionary(member => expected.MemberKeys[member.Name], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in model.Members.OrderBy(member => member.Type, StringComparer.Ordinal).ThenBy(member => member.Name, StringComparer.Ordinal))
        {
            if (member.Key is null || !members.TryGetValue(member.Key, out var generated))
            {
                findings.Add(new($"{member.Type} {member.Name}", "added — the generator does not make it"));
                continue;
            }
            if (!seen.Add(member.Key)) { findings.Add(new($"{member.Type} {member.Name}", $"a second member in the place of generated member {generated.Name}")); continue; }
            var (start, end) = (keyOfJoint.GetValueOrDefault(member.Start), keyOfJoint.GetValueOrDefault(member.End));
            var (wantStart, wantEnd) = (expected.JointKeys.GetValueOrDefault(generated.Start), expected.JointKeys.GetValueOrDefault(generated.End));
            if (!(start == wantStart && end == wantEnd) && !(start == wantEnd && end == wantStart))
                findings.Add(new($"{member.Type} {member.Name}", $"reconnected — it joins {member.Start}–{member.End} where the generator joins {generated.Start}–{generated.End}"));
        }
        foreach (var (key, member) in members.OrderBy(item => item.Key, StringComparer.Ordinal))
            if (!seen.Contains(key)) findings.Add(new($"{(member.Kind == MemberKind.Frame ? "Frame" : "Cable")} {member.Name}", "deleted"));
        if (model.Shells > 0) findings.Add(new($"{model.Shells} shell{(model.Shells == 1 ? "" : "s")}", "added — the generator makes no shells"));
        return new(Id, findings);
    }
}

/// <summary>
/// The tied arch system's generator (Edit → Tied Arch System, File → Load TAS…): the bundled CoT 120 m model's
/// materials and sections, the geometry and deck loads of <see cref="TiedArchParameters"/>
/// (<see cref="TiedArchSystem.Build"/>), the supports the model has, and the tie cable of an elevated system. A joint's
/// key is its name — the system names each joint by its line and level; a member's key is its two joints.
/// </summary>
internal sealed class TasGenerator : ParametricGenerator
{
    public const string GeneratorId = "TAS";
    /// <summary>The bundled ETABS model the generator builds on.</summary>
    public const string Resource = "Mcotis.TAS-CoT120.e2k";
    public static TasGenerator Instance { get; } = new();

    public override string Id => GeneratorId;
    public override int Version => 1;
    public override IReadOnlyList<GeneratorParameter> Schema { get; } =
    [
        new("ArchRise", "m", "H1: support to arch crown"),
        new("DeckHeight", "m", "H2: support to top chord"),
        new("Bays", "", "number of bays: even, 6 to 40"),
        new("BaySpacing", "m", "bay spacing"),
        new("TributaryWidth", "m", "B: tributary width of the storeys above"),
        new("SelfWeight", "kPa", "SW: slab self-weight"),
        new("SuperimposedDead", "kPa", "SDL: superimposed dead load"),
        new("Live", "kPa", "LL: live load"),
        new("Floors", "", "storeys the deck carries"),
        new("ElevatedCable", "", "true: the arch springs from two columns tied by a cable"),
        new("CableHeight", "m", "H0: height of the springing (elevated cable)"),
        new("CablePretension", "kN", "the tie's target force (elevated cable)"),
    ];

    public static IReadOnlyDictionary<string, string> Parameters(TiedArchParameters p) => new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["ArchRise"] = ParametricGenerators.Number(p.ArchRise),
        ["DeckHeight"] = ParametricGenerators.Number(p.DeckHeight),
        ["Bays"] = p.Bays.ToString(CultureInfo.InvariantCulture),
        ["BaySpacing"] = ParametricGenerators.Number(p.BaySpacing),
        ["TributaryWidth"] = ParametricGenerators.Number(p.TributaryWidth),
        ["SelfWeight"] = ParametricGenerators.Number(p.SelfWeight),
        ["SuperimposedDead"] = ParametricGenerators.Number(p.SuperimposedDead),
        ["Live"] = ParametricGenerators.Number(p.Live),
        ["Floors"] = p.Floors.ToString(CultureInfo.InvariantCulture),
        ["ElevatedCable"] = p.ElevatedCable ? "true" : "false",
        ["CableHeight"] = ParametricGenerators.Number(p.CableHeight),
        ["CablePretension"] = ParametricGenerators.Number(p.CablePretension),
    };

    public static TiedArchParameters Read(IReadOnlyDictionary<string, string> parameters) => new(
        ParametricGenerators.Number(parameters, "ArchRise"), ParametricGenerators.Number(parameters, "DeckHeight"),
        ParametricGenerators.Whole(parameters, "Bays"), ParametricGenerators.Number(parameters, "BaySpacing"),
        ParametricGenerators.Number(parameters, "TributaryWidth"), ParametricGenerators.Number(parameters, "SelfWeight"),
        ParametricGenerators.Number(parameters, "SuperimposedDead"), ParametricGenerators.Number(parameters, "Live"),
        ParametricGenerators.Whole(parameters, "Floors"),
        parameters.TryGetValue("ElevatedCable", out var elevated) ? elevated == "true" : throw new InvalidDataException("The generator parameter ElevatedCable is missing."),
        ParametricGenerators.Number(parameters, "CableHeight"), ParametricGenerators.Number(parameters, "CablePretension"));

    public override GeneratedSystem Generate(IReadOnlyDictionary<string, string> parameters)
    {
        var p = Read(parameters);
        var (imported, geometry) = Build(Template(), p);
        var joints = JointKeys(geometry);
        var restraints = imported.Restraints.Where(item => !item.Value.IsFree && geometry.Points.ContainsKey(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return new(geometry, restraints, joints, ParametricGenerators.MemberKeys(geometry, joints));
    }

    /// <summary>The system of <paramref name="p"/> on <paramref name="template"/>: the model as imported (members with their
    /// sections, loading, restraints) and its geometry with the tie of an elevated system. What Generate, Edit → Tied
    /// Arch System and each Optimization TAS case build.</summary>
    public static (EtabsImportModel Imported, GeometryModel Geometry) Build(EtabsModel template, TiedArchParameters p)
    {
        var imported = EtabsImport.Convert(TiedArchSystem.Build(template, p));
        return (imported, TiedArchSystem.WithCable(imported.Geometry, p));
    }

    public static Dictionary<string, string> JointKeys(GeometryModel geometry) =>
        geometry.Points.Keys.ToDictionary(name => name, name => "J:" + name, StringComparer.Ordinal);

    /// <summary>The flag of a tied arch system this generator made with <paramref name="p"/>.</summary>
    public FemGeneratorDto Flag(TiedArchParameters p, GeometryModel geometry) =>
        ParametricGenerators.Flag(this, Parameters(p), geometry, JointKeys(geometry));

    private static EtabsModel? template;
    private static readonly object TemplateLock = new();

    /// <summary>The bundled ETABS model, read once.</summary>
    public static EtabsModel Template()
    {
        lock (TemplateLock)
        {
            if (template is not null) return template;
            using var resource = typeof(TasGenerator).Assembly.GetManifestResourceStream(Resource)
                ?? throw new InvalidDataException("The built-in TAS model is missing from the application.");
            using var reader = new StreamReader(resource);
            return template = EtabsE2kReader.Read(reader.ReadToEnd(), "CoT120.e2k");
        }
    }
}

/// <summary>
/// The VCF system's generator (File → Load VCF…, Edit → VCF System): the built-in VCF model's topology, given the number of
/// bays (<see cref="VcfGeometry.ChangeBays"/>) and the bay width, truss depth, middle chord, clear height, back-span and
/// back-stay height (<see cref="VcfGeometry.Build"/>), pinned at its column bases. A chord joint's key is its chord and
/// its line along the truss (T0 … Tn, M…, B…) — its name depends on how the bays were reached; any other joint keeps its
/// name, which is its key; a member's key is its two joints.
/// </summary>
internal sealed class VcfGenerator : ParametricGenerator
{
    public const string GeneratorId = "VCF";
    public static VcfGenerator Instance { get; } = new();

    public override string Id => GeneratorId;
    public override int Version => 1;
    public override IReadOnlyList<GeneratorParameter> Schema { get; } =
    [
        new("Bays", "", "number of truss bays"),
        new("BayWidth", "m", "bay width"),
        new("TrussDepth", "m", "bottom chord to top chord"),
        new("MiddleChordHeight", "m", "bottom chord to middle chord"),
        new("ClearHeight", "m", "ground to bottom chord"),
        new("BackSpan", "m", "end-frame back-span (absent: none)"),
        new("BackStayHeight", "m", "ground to back-stay level (absent: none)"),
    ];

    public static IReadOnlyDictionary<string, string> Parameters(int bays, VcfGeometryParameters p)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Bays"] = bays.ToString(CultureInfo.InvariantCulture),
            ["BayWidth"] = ParametricGenerators.Number(p.BayWidth),
            ["TrussDepth"] = ParametricGenerators.Number(p.TrussDepth),
            ["MiddleChordHeight"] = ParametricGenerators.Number(p.MiddleChordHeight),
            ["ClearHeight"] = ParametricGenerators.Number(p.ClearHeight),
        };
        if (p.BackSpan is { } span) parameters["BackSpan"] = ParametricGenerators.Number(span);
        if (p.BackStayHeight is { } stay) parameters["BackStayHeight"] = ParametricGenerators.Number(stay);
        return parameters;
    }

    public static (int Bays, VcfGeometryParameters Parameters) Read(IReadOnlyDictionary<string, string> parameters) =>
        (ParametricGenerators.Whole(parameters, "Bays"), new(
            ParametricGenerators.Number(parameters, "BayWidth"), ParametricGenerators.Number(parameters, "TrussDepth"),
            ParametricGenerators.Number(parameters, "MiddleChordHeight"), ParametricGenerators.Number(parameters, "ClearHeight"),
            parameters.ContainsKey("BackSpan") ? ParametricGenerators.Number(parameters, "BackSpan") : null,
            parameters.ContainsKey("BackStayHeight") ? ParametricGenerators.Number(parameters, "BackStayHeight") : null));

    public override GeneratedSystem Generate(IReadOnlyDictionary<string, string> parameters)
    {
        var (bays, p) = Read(parameters);
        var analysis = System(bays);
        var geometry = Candidate(analysis, p);
        var joints = JointKeys(geometry, analysis.TopPoints, analysis.MiddlePoints, analysis.BottomPoints);
        return new(geometry, LegacyNativeDocumentMigration.Restraints(new Dictionary<string, Native.Restraint>(), geometry),
            joints, ParametricGenerators.MemberKeys(geometry, joints));
    }

    /// <summary>The built-in system with <paramref name="bays"/> bays, at the built-in dimensions.</summary>
    public static VcfGeometryAnalysis System(int bays)
    {
        var (geometry, top, middle, bottom) = Template();
        return WithBays(VcfGeometry.Analyze(geometry, top, middle, bottom), bays).System;
    }

    /// <summary><paramref name="system"/> with <paramref name="bays"/> bays at its bay width and levels, and the topology
    /// that maps its objects to the new ones (null when the number does not change). What Generate and each Optimization
    /// VCF case use.</summary>
    public static (VcfGeometryAnalysis System, VcfTopology? Topology) WithBays(VcfGeometryAnalysis system, int bays)
    {
        if (bays == system.Bays) return (system, null);
        var topology = VcfGeometry.ChangeBays(system, bays);
        return (VcfGeometry.Analyze(topology), topology);
    }

    /// <summary>The geometry of <paramref name="system"/> at the dimensions <paramref name="p"/>.</summary>
    public static GeometryModel Candidate(VcfGeometryAnalysis system, VcfGeometryParameters p) => VcfGeometry.Build(system, p);

    public static Dictionary<string, string> JointKeys(GeometryModel geometry, IReadOnlyList<string> top, IReadOnlyList<string> middle,
        IReadOnlyList<string> bottom)
    {
        var keys = geometry.Points.Keys.ToDictionary(name => name, name => "J:" + name, StringComparer.Ordinal);
        void Chord(IReadOnlyList<string> group, string letter)
        {
            var ordered = group.Where(geometry.Points.ContainsKey).OrderBy(name => geometry.Points[name].Position.X).ToArray();
            for (var line = 0; line < ordered.Length; line++) keys[ordered[line]] = letter + line.ToString(CultureInfo.InvariantCulture);
        }
        Chord(top, "T");
        Chord(middle, "M");
        Chord(bottom, "B");
        return keys;
    }

    /// <summary>The flag of a VCF system this generator made: its parameters read back from the model and its chord groups.
    /// Throws (<see cref="InvalidOperationException"/>) for a model that is not a VCF system.</summary>
    public FemGeneratorDto Flag(GeometryModel geometry, IReadOnlyList<string> top, IReadOnlyList<string> middle, IReadOnlyList<string> bottom)
    {
        var analysis = VcfGeometry.Analyze(geometry, top, middle, bottom);
        return ParametricGenerators.Flag(this, Parameters(analysis.Bays, analysis.Parameters), geometry, JointKeys(geometry, top, middle, bottom));
    }

    private static (GeometryModel Geometry, string[] Top, string[] Middle, string[] Bottom)? template;
    private static readonly object TemplateLock = new();

    /// <summary>The built-in VCF model's geometry and chord groups, read once.</summary>
    private static (GeometryModel Geometry, string[] Top, string[] Middle, string[] Bottom) Template()
    {
        lock (TemplateLock)
        {
            if (template is { } read) return read;
            var legacy = FemInputDatabase.ReadDefault().Snapshot.ToLegacy();
            var setup = legacy.Project?.Tab03TrussHeight ?? throw new InvalidDataException("The built-in VCF model has no chord definition.");
            var geometry = legacy.Geometry ?? throw new InvalidDataException("The built-in VCF model has no geometry.");
            template = (geometry, setup.TopPoints.ToArray(), setup.MiddlePoints.ToArray(), setup.BottomPoints.ToArray());
            return template.Value;
        }
    }
}
