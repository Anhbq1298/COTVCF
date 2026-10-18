using System.Globalization;
using Mcotis.Desktop.Application.Results;

namespace Mcotis.Desktop.Application.Quantities;

public enum QuantityMemberKind { Frame, Cable }
public enum QuantityMaterialClass { Concrete, Steel }

/// <summary>One material of a section: its physical area and, for steel, its mass density.</summary>
public sealed record QuantityComponent(string Material, string MaterialType, QuantityMaterialClass Class, double AreaM2, double? DensityTPerM3);

public readonly record struct QuantityFigurePoint(double X, double Y);

/// <summary>One drawn region of a section figure: its material, outline and whether it encloses other materials.</summary>
public sealed record QuantityFigureShape(string Name, string Material, QuantityMaterialClass? Class,
    IReadOnlyList<IReadOnlyList<QuantityFigurePoint>> Outlines, bool Shell = false, bool OutlineOnly = false);

/// <summary>The cross-section of an assigned section as the model's own data draws it — the stored Section Designer
/// shapes, or the stored dimensions of a standard section. Metres, X to the right and Y up, shapes back to front.
/// A picture of the section only: no quantity is taken from it.</summary>
public sealed record QuantitySectionFigure(IReadOnlyList<QuantityFigureShape> Shapes, double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
}

/// <summary>Quantities of the members assigned to one section. A null quantity is unavailable — it is never zero.</summary>
public sealed record QuantitySectionRow(
    string Section,
    QuantityMemberKind Kind,
    int MemberCount,
    double? TotalLengthM,
    double? ConcreteVolumeM3,
    double? SteelMassT,
    IReadOnlyList<string> Problems,
    string Basis,
    IReadOnlyList<QuantityComponent> Components)
{
    public bool Complete => TotalLengthM is not null && ConcreteVolumeM3 is not null && SteelMassT is not null;
    /// <summary>The section's cross-section, or null when its stored data does not describe one that is drawn.</summary>
    public QuantitySectionFigure? Figure { get; init; }
}

/// <summary>Sum over the sections. A quantity is null as soon as one section cannot give it; the sum of the sections
/// that can is kept beside it so the gap can be stated.</summary>
public sealed record QuantityTotal(
    int MemberCount,
    double? TotalLengthM,
    double? ConcreteVolumeM3,
    double? SteelMassT,
    double AvailableLengthM,
    double AvailableConcreteVolumeM3,
    double AvailableSteelMassT);

public sealed record QuantityTakeoff(IReadOnlyList<QuantitySectionRow> Sections, QuantityTotal Total, IReadOnlyList<string> Notes)
{
    public bool Complete => Sections.All(section => section.Complete);
}

/// <summary>
/// Concrete volume and steel mass of one SAP2000 model, by assigned section, from that model's own database tables.
/// <para>Per member: length between its two end joints as the model holds them; section as assigned in the model.
/// Per section: the physical area of each material — for a Section Designer section from its drawn shapes, a shape in
/// front (higher Z-order) replacing what it covers; for any other frame section and for a cable section the one area
/// of its one material. Concrete volume = concrete area × length; steel mass = steel area × length × the material's
/// own mass density.</para>
/// <para>What was established from stored models (SAP2000 27, COT Framing): Section Designer shapes I/wide flange,
/// box/tube, solid rectangle and polygon with straight edges; an embedded shape displaces the shape behind it; a
/// box's interior is empty unless another shape fills it; no shape carries reinforcement. Anything else — another
/// shape, reinforcement, rounded polygon corners, shapes that overlap at one Z-order — makes that section's
/// quantities unavailable, with the reason. The transformed area SAP2000 reports for a Section Designer section is
/// used only to check the shape areas, never as a quantity.</para>
/// </summary>
public static class QuantityTakeoffCalculator
{
    public const string UnassignedSection = "(no section assigned)";
    private const string SectionDesignerPrefix = "SECTION DESIGNER PROPERTIES ";
    private const string FrameSectionPrefix = "FRAME SECTION PROPERTIES ";
    private const string FrameSections = "FRAME SECTION PROPERTIES 01 - GENERAL";
    private const double AreaCheckTolerance = 1e-3;
    private const int ProblemsShown = 6;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The tables every take-off needs and that this table set does not hold; empty when it is complete.</summary>
    public static IReadOnlyList<string> MissingInputs(SapTableSet tables)
    {
        var missing = new List<string>();
        void Need(string table) { if (!tables.Has(table)) missing.Add(table); }
        Need("JOINT COORDINATES");
        Need("MATERIAL PROPERTIES 01 - GENERAL");
        Need("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES");
        bool frames = tables.Has("CONNECTIVITY - FRAME"), cables = tables.Has("CONNECTIVITY - CABLE");
        if (!frames && !cables) missing.Add("CONNECTIVITY - FRAME / CONNECTIVITY - CABLE");
        if (frames) { Need("FRAME SECTION ASSIGNMENTS"); Need(FrameSections); }
        if (cables) { Need("CABLE SECTION ASSIGNMENTS"); Need("CABLE SECTION DEFINITIONS"); }
        if (missing.Count > 0) return missing;

        var designed = tables.Rows(FrameSections).Where(IsSectionDesigner).Select(row => row.Text("SectionName")).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var drawn = tables.TableNames.Where(name => name.StartsWith(SectionDesignerPrefix, StringComparison.OrdinalIgnoreCase) && IsShapeTable(name))
            .SelectMany(name => tables.Rows(name)).Select(row => row.Text("SectionName")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var section in tables.Rows("FRAME SECTION ASSIGNMENTS").Select(row => row.Text("AnalSect")).OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase).Where(designed.Contains).Where(section => !drawn.Contains(section)))
            missing.Add($"Section Designer shapes of '{section}'");
        return missing;
    }

    public static QuantityTakeoff Calculate(SapTableSet tables) => new Run(tables).Calculate();

    private static bool IsSectionDesigner(SapTableRow section) =>
        string.Equals(section.Text("Shape"), "SD Section", StringComparison.OrdinalIgnoreCase);

    // Tables of a Section Designer section that describe no area: its general data, guide lines and fiber layout.
    private static bool IsShapeTable(string table) =>
        !Ends(table, "- GENERAL") && !Ends(table, "- SHAPE REFERENCE LINE") && !Ends(table, "- FIBER GENERAL");

    private static bool Ends(string text, string suffix) => text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    private static string Number(double value, string format = "0.####") => value.ToString(format, Invariant);

    private sealed record Material(string Name, string Type, double? DensityTPerM3, double? Modulus);
    private sealed record SectionData(IReadOnlyList<QuantityComponent> Components, string Basis, string? Problem, string? SteelProblem, bool Designed)
    {
        public QuantitySectionFigure? Figure { get; init; }
    }
    private sealed class Member(QuantityMemberKind kind, string name)
    {
        public QuantityMemberKind Kind { get; } = kind;
        public string Name { get; } = name;
        public string? JointI, JointJ, Section, MaterialOverwrite;
        public bool Connected, Curved;
        public List<string> Problems { get; } = [];
        public string Label => $"{(Kind == QuantityMemberKind.Frame ? "Frame" : "Cable")} '{Name}'";
    }

    private sealed class Run(SapTableSet tables)
    {
        private readonly Dictionary<string, Material> materials = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (double X, double Y, double Z)> joints = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> unusableJoints = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(QuantityMemberKind, string), SectionData> sections = [];
        private readonly List<string> notes = [];

        public QuantityTakeoff Calculate()
        {
            ReadMaterials();
            ReadJoints();
            var members = ReadMembers(QuantityMemberKind.Frame, "CONNECTIVITY - FRAME", "Frame", "FRAME SECTION ASSIGNMENTS", "AnalSect")
                .Concat(ReadMembers(QuantityMemberKind.Cable, "CONNECTIVITY - CABLE", "Cable", "CABLE SECTION ASSIGNMENTS", "CableSect")).ToArray();
            NoteTwins(members);

            var rows = members.GroupBy(member => (member.Kind, Section: member.Section ?? UnassignedSection))
                .OrderBy(group => group.Key.Kind).ThenBy(group => group.Key.Section == UnassignedSection)
                .ThenBy(group => group.Key.Section, NaturalNameComparer.Instance)
                .Select(group => Row(group.Key.Kind, group.Key.Section, group.ToArray())).ToArray();
            foreach (var row in rows)
            foreach (var problem in row.Problems) notes.Add($"{row.Section}: {problem}");

            var total = new QuantityTotal(
                rows.Sum(row => row.MemberCount),
                rows.All(row => row.TotalLengthM is not null) ? rows.Sum(row => row.TotalLengthM!.Value) : null,
                rows.All(row => row.ConcreteVolumeM3 is not null) ? rows.Sum(row => row.ConcreteVolumeM3!.Value) : null,
                rows.All(row => row.SteelMassT is not null) ? rows.Sum(row => row.SteelMassT!.Value) : null,
                rows.Sum(row => row.TotalLengthM ?? 0), rows.Sum(row => row.ConcreteVolumeM3 ?? 0), rows.Sum(row => row.SteelMassT ?? 0));
            return new(rows, total, notes);
        }

        private void ReadMaterials()
        {
            var mechanical = new Dictionary<string, SapTableRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in tables.Rows("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES"))
                if (row.Text("Material") is { } name) mechanical.TryAdd(name, row);          // first row = lowest temperature
            foreach (var row in tables.Rows("MATERIAL PROPERTIES 01 - GENERAL"))
            {
                if (row.Text("Material") is not { } name) continue;
                mechanical.TryGetValue(name, out var values);
                double? density = values?.Number("UnitMass") is { } mass && mass > 0 ? mass * tables.Units.TonnesPerCubicMetrePerDensity : null;
                double? modulus = values?.Number("E1") is { } stiffness && stiffness > 0 ? stiffness : null;
                materials.TryAdd(name, new(name, row.Text("Type") ?? "", density, modulus));
            }
        }

        private void ReadJoints()
        {
            foreach (var row in tables.Rows("JOINT COORDINATES"))
            {
                if (row.Text("Joint") is not { } name) continue;
                var global = string.Equals(row.Text("CoordSys") ?? "GLOBAL", "GLOBAL", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(row.Text("CoordType") ?? "Cartesian", "Cartesian", StringComparison.OrdinalIgnoreCase);
                if (global && row.Number("XorR") is { } x && row.Number("Y") is { } y && row.Number("Z") is { } z) joints.TryAdd(name, (x, y, z));
                else unusableJoints.Add(name);
            }
        }

        // One member per object name: a name that SAP2000's tables list twice is counted once, and a name whose
        // records disagree is not counted with either of them.
        private List<Member> ReadMembers(QuantityMemberKind kind, string connectivity, string nameField, string assignments, string sectionField)
        {
            var members = new Dictionary<string, Member>(StringComparer.OrdinalIgnoreCase);
            var order = new List<Member>();
            Member Of(string name)
            {
                if (members.TryGetValue(name, out var member)) return member;
                order.Add(members[name] = member = new(kind, name));
                return member;
            }
            foreach (var row in tables.Rows(connectivity))
            {
                if (row.Text(nameField) is not { } name) continue;
                var member = Of(name);
                var (jointI, jointJ) = (row.Text("JointI"), row.Text("JointJ"));
                if (!member.Connected) { member.Connected = true; member.JointI = jointI; member.JointJ = jointJ; member.Curved = row.Yes("IsCurved"); continue; }
                if (Same(member.JointI, jointI) && Same(member.JointJ, jointJ)) notes.Add($"{member.Label} is listed twice in {connectivity}; it is counted once.");
                else { member.JointI = member.JointJ = null; member.Problems.Add($"{member.Label} is listed in {connectivity} with different end joints"); }
            }
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in tables.Rows(assignments))
            {
                if (row.Text(nameField) is not { } name) continue;
                var member = Of(name);
                var section = row.Text(sectionField);
                if (assigned.Add(name)) { member.Section = section; member.MaterialOverwrite = Overwrite(row.Text("MatProp")); continue; }
                if (Same(member.Section, section)) notes.Add($"{member.Label} is listed twice in {assignments}; it is counted once.");
                else if (member.Section is not null)
                {
                    member.Problems.Add($"{member.Label} is assigned to more than one section ('{member.Section}' and '{section}')");
                    member.Section = null;
                }
            }
            foreach (var member in order.Where(member => member.Section is null && member.Problems.Count == 0))
                member.Problems.Add($"{member.Label} has no section assignment");
            return order;
        }

        private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        // SAP2000 writes "Default" (and the API "None") when the member uses its section's own material.
        private static string? Overwrite(string? material) =>
            material is null || Same(material, "Default") || Same(material, "None") ? null : material;

        private void NoteTwins(IReadOnlyList<Member> members)
        {
            foreach (var twins in members.Where(member => member.JointI is not null && member.JointJ is not null && member.Section is not null)
                         .GroupBy(member => (member.Kind, member.Section!.ToUpperInvariant(),
                             string.Join("|", new[] { member.JointI!.ToUpperInvariant(), member.JointJ!.ToUpperInvariant() }.Order(StringComparer.Ordinal))))
                         .Where(group => group.Count() > 1))
                notes.Add($"{string.Join(", ", twins.Select(member => member.Label))} span the same two joints with the same section; " +
                          "each is a separate object in the model and is counted — check the model for a duplicate.");
        }

        private double? Length(Member member)
        {
            if (!member.Connected) { member.Problems.Add($"{member.Label} has no end joints (it is not in the connectivity table)"); return null; }
            if (member.JointI is null || member.JointJ is null)
            {
                if (member.Problems.Count == 0) member.Problems.Add($"{member.Label} does not name both of its end joints");
                return null;
            }
            if (member.Curved) { member.Problems.Add($"{member.Label} is curved; its length is not the distance between its joints"); return null; }
            foreach (var joint in new[] { member.JointI, member.JointJ }.Where(joint => !joints.ContainsKey(joint)))
            {
                member.Problems.Add(unusableJoints.Contains(joint)
                    ? $"{member.Label}: joint '{joint}' is not given in global Cartesian coordinates"
                    : $"{member.Label}: joint '{joint}' has no coordinates");
                return null;
            }
            var (a, b) = (joints[member.JointI], joints[member.JointJ]);
            return Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2)) * tables.Units.MetresPerLength;
        }

        private QuantitySectionRow Row(QuantityMemberKind kind, string sectionName, Member[] members)
        {
            var data = sectionName == UnassignedSection ? null : Section(kind, sectionName);
            var problems = new List<string>();
            if (data?.Problem is { } whole) problems.Add(whole);
            if (data?.SteelProblem is { } steelOnly) problems.Add(steelOnly);
            double length = 0, concrete = 0, steel = 0;
            bool lengthKnown = true, concreteKnown = data is { Problem: null }, steelKnown = data is { Problem: null, SteelProblem: null };
            var components = data?.Components ?? [];
            foreach (var member in members)
            {
                var memberLength = Length(member);
                if (memberLength is { } metres) length += metres; else lengthKnown = false;
                var used = data?.Components ?? [];
                if (data is { Problem: null } && member.MaterialOverwrite is { } overwrite)
                {
                    if (data.Designed)
                    {
                        member.Problems.Add($"{member.Label} overwrites the material of a Section Designer section ('{overwrite}'); which shapes that replaces is not defined");
                        concreteKnown = steelKnown = false;
                        used = [];
                    }
                    else if (Component(overwrite, data.Components.Sum(component => component.AreaM2), out var replaced) is { } refused)
                    {
                        member.Problems.Add($"{member.Label}: {refused}");
                        concreteKnown = steelKnown = false;
                        used = [];
                    }
                    else
                    {
                        used = [replaced!];
                        components = components.Concat(used).Distinct().ToArray();
                    }
                }
                var concreteArea = used.Where(component => component.Class == QuantityMaterialClass.Concrete).Sum(component => component.AreaM2);
                var steelParts = used.Where(component => component.Class == QuantityMaterialClass.Steel).ToArray();
                if (concreteArea > 0) { if (memberLength is { } l) concrete += concreteArea * l; else concreteKnown = false; }
                if (steelParts.Length > 0)
                {
                    if (steelParts.FirstOrDefault(part => part.DensityTPerM3 is null) is { } weightless)
                    {
                        member.Problems.Add($"material '{weightless.Material}' has no mass density in the model");
                        steelKnown = false;
                    }
                    else if (memberLength is { } l) steel += steelParts.Sum(part => part.AreaM2 * part.DensityTPerM3!.Value) * l;
                    else steelKnown = false;
                }
                problems.AddRange(member.Problems);
            }
            problems = problems.Distinct(StringComparer.Ordinal).ToList();
            if (problems.Count > ProblemsShown)
                problems = problems.Take(ProblemsShown).Append($"… and {problems.Count - ProblemsShown} more of the same kind").ToList();
            return new(sectionName, kind, members.Length, lengthKnown ? length : null, concreteKnown ? concrete : null, steelKnown ? steel : null,
                problems, data?.Basis ?? "No section is assigned to these members in the model.", components) { Figure = data?.Figure };
        }

        private SectionData Section(QuantityMemberKind kind, string name)
        {
            if (sections.TryGetValue((kind, name.ToUpperInvariant()), out var known)) return known;
            return sections[(kind, name.ToUpperInvariant())] = kind == QuantityMemberKind.Cable ? CableSection(name) : FrameSection(name);
        }

        private static SectionData Unusable(string problem, string basis = "", QuantitySectionFigure? figure = null) =>
            new([], basis, problem, null, false) { Figure = figure };

        private static QuantityMaterialClass? Classify(string materialType) => materialType.ToUpperInvariant() switch
        {
            "CONCRETE" => QuantityMaterialClass.Concrete,
            "STEEL" or "TENDON" or "REBAR" => QuantityMaterialClass.Steel,
            _ => null,
        };

        /// <summary>The picture of a section from outlines in the model's own length unit, back to front.</summary>
        private QuantitySectionFigure? Figure(IEnumerable<(string Name, string Material, IReadOnlyList<PlanePoint[]> Outlines)> shapes)
        {
            var metres = tables.Units.MetresPerLength;
            var drawn = shapes.Select(shape => new QuantityFigureShape(shape.Name, shape.Material,
                materials.TryGetValue(shape.Material, out var material) ? Classify(material.Type) : null,
                shape.Outlines.Select(outline => (IReadOnlyList<QuantityFigurePoint>)outline.Select(point => new QuantityFigurePoint(point.X * metres, point.Y * metres)).ToArray())
                    .ToArray())).ToArray();
            var outside = drawn.SelectMany(shape => shape.Outlines[0]).ToArray();
            return outside.Length == 0 ? null : new(drawn, outside.Min(point => point.X), outside.Min(point => point.Y), outside.Max(point => point.X), outside.Max(point => point.Y));
        }

        /// <summary>A standard frame section from its stored dimensions: t3 is the outside depth, t2 the (top) width,
        /// tf / tw the flange and web thickness, t2b / tfb the bottom flange. Shapes whose dimensions are not read this
        /// way have no figure.</summary>
        private QuantitySectionFigure? StandardFigure(string name, string shape, string material, SapTableRow section)
        {
            double? t3 = section.Number("t3"), t2 = section.Number("t2"), tf = section.Number("tf"), tw = section.Number("tw");
            double? t2b = section.Number("t2b") ?? t2, tfb = section.Number("tfb") ?? tf;
            IReadOnlyList<PlanePoint[]>? outlines = shape.ToUpperInvariant() switch
            {
                "BOX/TUBE" when t3 > 0 && t2 > 0 && tf > 0 && tw > 0 && 2 * tf < t3 && 2 * tw < t2 =>
                    [SectionDesignerGeometry.Rectangle(0, 0, t2.Value, t3.Value), SectionDesignerGeometry.Rectangle(0, 0, t2.Value - 2 * tw.Value, t3.Value - 2 * tf.Value)],
                "I/WIDE FLANGE" when t3 > 0 && t2 > 0 && tf > 0 && tw > 0 && t2b > 0 && tfb > 0 && tf + tfb < t3 && tw <= Math.Min(t2.Value, t2b.Value) =>
                    [SectionDesignerGeometry.WideFlange(0, 0, t3.Value, t2.Value, tf.Value, tw.Value, t2b.Value, tfb.Value)],
                "RECTANGULAR" when t3 > 0 && t2 > 0 => [SectionDesignerGeometry.Rectangle(0, 0, t2.Value, t3.Value)],
                "CIRCLE" when t3 > 0 => [SectionDesignerGeometry.Circle(0, 0, t3.Value)],
                "PIPE" when t3 > 0 && tw > 0 && 2 * tw < t3 => [SectionDesignerGeometry.Circle(0, 0, t3.Value), SectionDesignerGeometry.Circle(0, 0, t3.Value - 2 * tw.Value)],
                _ => null,
            };
            return outlines is null ? null : Figure([(name, material, outlines)]);
        }

        /// <summary>Classifies one material area. Returns what stops it from being counted, or null.</summary>
        private string? Component(string materialName, double areaM2, out QuantityComponent? component)
        {
            component = null;
            if (!materials.TryGetValue(materialName, out var material)) return $"material '{materialName}' is not defined in the model";
            var kind = Classify(material.Type);
            if (kind is null) return $"material '{material.Name}' is of SAP2000 type '{material.Type}', which the take-off counts as neither concrete nor steel";
            component = new(material.Name, material.Type, kind.Value, areaM2, material.DensityTPerM3);
            return null;
        }

        private SectionData CableSection(string name)
        {
            var section = tables.Rows("CABLE SECTION DEFINITIONS").FirstOrDefault(row => Same(row.Text("CableSect"), name));
            if (section is null) return Unusable($"cable section '{name}' is not defined in the model");
            if (section.Text("Material") is not { } material) return Unusable($"cable section '{name}' names no material");
            if (section.Number("Area") is not > 0) return Unusable($"cable section '{name}' has no usable area");
            var areaM2 = section.Number("Area")!.Value * tables.Units.SquareMetresPerArea;
            var figure = section.Number("Diameter") is > 0 and var diameter ? Figure([(name, material, [SectionDesignerGeometry.Circle(0, 0, diameter)])]) : null;
            return Component(material, areaM2, out var component) is { } refused
                ? Unusable(refused, figure: figure)
                : new([component!], $"Cable section · {Describe(component!)}", null, null, false) { Figure = figure };
        }

        private SectionData FrameSection(string name)
        {
            var section = tables.Rows(FrameSections).FirstOrDefault(row => Same(row.Text("SectionName"), name));
            if (section is null) return Unusable($"frame section '{name}' is not defined in the model");
            if (IsSectionDesigner(section)) return DesignedSection(name, section);
            var shape = section.Text("Shape") ?? "";
            if (shape.Length == 0) return Unusable($"frame section '{name}' states no shape");
            if (shape.Contains("Nonprismatic", StringComparison.OrdinalIgnoreCase) || shape.StartsWith("Auto", StringComparison.OrdinalIgnoreCase))
                return Unusable($"frame section '{name}' is a {shape} section; only a prismatic section with one area is taken off");
            if (section.Text("Material") is not { } material) return Unusable($"frame section '{name}' names no material");
            if (section.Number("Area") is not > 0) return Unusable($"frame section '{name}' has no usable area");
            var areaM2 = section.Number("Area")!.Value * tables.Units.SquareMetresPerArea;
            if (Component(material, areaM2, out var component) is { } refused) return Unusable(refused);

            // Every other table that holds data for this section is data the take-off has not read.
            var unread = tables.TableNames.Where(table => table.StartsWith(FrameSectionPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !Ends(table, "- GENERAL") && !Ends(table, "- TIME DEPENDENT") &&
                    tables.Rows(table).Any(row => Same(row.Text("SectionName"), name)))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var basis = $"{shape} section · {Describe(component!)}";
            var figure = StandardFigure(name, shape, material, section);
            if (unread.Length == 0) return new([component!], basis, null, null, false) { Figure = figure };
            var where = string.Join(", ", unread);
            return component!.Class == QuantityMaterialClass.Concrete
                ? new([component], basis, null, $"concrete section '{name}' carries further data in {where}; reinforcement is not taken off, so its steel is unknown", false) { Figure = figure }
                : Unusable($"frame section '{name}' carries data the take-off does not read ({where})", basis, figure);
        }

        private SectionData DesignedSection(string name, SapTableRow section)
        {
            var problems = new List<string>();
            var shapes = new List<SectionDesignerShape>();
            foreach (var table in tables.TableNames.Where(table => table.StartsWith(SectionDesignerPrefix, StringComparison.OrdinalIgnoreCase))
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                var rows = tables.Rows(table).Where(row => Same(row.Text("SectionName"), name)).ToArray();
                if (rows.Length == 0 || !IsShapeTable(table)) continue;
                if (Ends(table, "- SHAPE I/WIDE FLANGE")) foreach (var row in rows) Add(WideFlange(row));
                else if (Ends(table, "- SHAPE BOX/TUBE")) foreach (var row in rows) Add(Box(row));
                else if (Ends(table, "- SHAPE SOLID RECTANGLE")) foreach (var row in rows) Add(SolidRectangle(row));
                else if (Ends(table, "- SHAPE POLYGON"))
                    foreach (var polygon in rows.GroupBy(row => row.Text("ShapeName") ?? "", StringComparer.OrdinalIgnoreCase)) Add(Polygon(polygon.Key, polygon.ToArray()));
                else problems.Add($"it uses data the take-off does not read ({table})");
            }
            void Add((SectionDesignerShape? Shape, string? Problem) read)
            {
                if (read.Problem is not null) problems.Add(read.Problem);
                else if (shapes.Any(shape => Same(shape.Name, read.Shape!.Name))) problems.Add($"two shapes are named '{read.Shape!.Name}'");
                else shapes.Add(read.Shape!);
            }
            if (shapes.Count == 0 && problems.Count == 0) problems.Add("no shape with an area is stored for it");
            string Refuse() => $"Section Designer section '{name}': {string.Join("; ", problems.Distinct(StringComparer.Ordinal))}";
            if (problems.Count > 0) return Unusable(Refuse(), "Section Designer section");

            // Every shape of the section was read, so it can be drawn as stored — whatever the checks below say.
            var figure = Figure(shapes.OrderBy(shape => shape.ZOrder).Select(shape => (shape.Name, shape.Material, shape.Outlines)));
            var visible = SectionDesignerGeometry.VisibleAreas(shapes, out var overlap);
            if (overlap is not null) { problems.Add(overlap); return Unusable(Refuse(), "Section Designer section", figure); }
            var components = new List<QuantityComponent>();
            foreach (var material in shapes.GroupBy(shape => shape.Material, StringComparer.OrdinalIgnoreCase))
            {
                var area = material.Sum(shape => visible[shape.Name]) * tables.Units.SquareMetresPerArea;
                if (Component(material.Key, area, out var component) is { } refused) problems.Add(refused);
                else components.Add(component!);
            }
            if (problems.Count > 0) return Unusable(Refuse(), "Section Designer section", figure);

            // The check: these areas, each weighted by its modulus over the base material's, must give the area
            // SAP2000 itself reports for the section. If they do not, the overlap rule or a shape is not what was assumed.
            var baseModulus = section.Text("Material") is { } baseMaterial && materials.TryGetValue(baseMaterial, out var found) ? found.Modulus : null;
            var reported = section.Number("Area");
            if (baseModulus is null || reported is not > 0 || components.Any(component => materials[component.Material].Modulus is null))
                problems.Add("its shape areas cannot be checked against the area SAP2000 reports (a modulus or the section area is missing)");
            else
            {
                var transformed = components.Sum(component => component.AreaM2 * materials[component.Material].Modulus!.Value / baseModulus.Value);
                var reportedM2 = reported.Value * tables.Units.SquareMetresPerArea;
                if (Math.Abs(transformed - reportedM2) > AreaCheckTolerance * reportedM2)
                    problems.Add($"its shapes give a transformed area of {Number(transformed)} m² but SAP2000 reports {Number(reportedM2)} m², " +
                                 "so a shape or an overlap is not what the take-off assumes");
            }
            var basis = $"Section Designer section · {string.Join(" · ", components.Select(Describe))} · shapes: {string.Join(", ", shapes.Select(shape => shape.Name))}";
            return problems.Count > 0 ? Unusable(Refuse(), basis, figure) : new(components, basis, null, null, true) { Figure = figure };
        }

        private static string Describe(QuantityComponent component) =>
            $"{component.Material} ({component.Class.ToString().ToLowerInvariant()}) {Number(component.AreaM2, "0.######")} m²" +
            (component.Class == QuantityMaterialClass.Steel ? $" × {(component.DensityTPerM3 is { } density ? Number(density, "0.###") + " t/m³" : "no density")}" : "");

        // ---- Section Designer shapes as SAP2000 stores them ----

        private static string? Header(SapTableRow row, out string name, out string material, out double zOrder, bool typed)
        {
            name = row.Text("ShapeName") ?? "(unnamed)";
            material = row.Text("ShapeMat") ?? "";
            zOrder = row.Number("ZOrder") ?? double.NaN;
            if (material.Length == 0) return $"shape '{name}' names no material";
            if (double.IsNaN(zOrder)) return $"shape '{name}' has no Z-order";
            if (row.Yes("Reinforcing")) return $"shape '{name}' carries reinforcement, which is not taken off";
            if (typed && row.Text("ShapeType") is { } type && !Same(type, "User Defined"))
                return $"shape '{name}' is a '{type}' shape; only user-defined dimensions are read";
            return null;
        }

        private static bool Read(SapTableRow row, string shape, out string? problem, params (string Field, bool Positive)[] fields)
        {
            problem = fields.Where(field => row.Number(field.Field) is not { } value || (field.Positive && value <= 0))
                .Select(field => $"shape '{shape}' has no usable {field.Field}").FirstOrDefault();
            return problem is null;
        }

        private static (SectionDesignerShape?, string?) WideFlange(SapTableRow row)
        {
            if (Header(row, out var name, out var material, out var zOrder, typed: true) is { } refused) return (null, refused);
            if (!Read(row, name, out var problem, ("XCenter", false), ("YCenter", false), ("Height", true), ("TopWidth", true), ("TopThick", true),
                    ("WebThick", true), ("BotWidth", true), ("BotThick", true))) return (null, problem);
            double x = row.Number("XCenter")!.Value, y = row.Number("YCenter")!.Value, height = row.Number("Height")!.Value;
            double topWidth = row.Number("TopWidth")!.Value, topThick = row.Number("TopThick")!.Value, web = row.Number("WebThick")!.Value;
            double bottomWidth = row.Number("BotWidth")!.Value, bottomThick = row.Number("BotThick")!.Value, rotation = row.Number("Rotation") ?? 0;
            if (topThick + bottomThick >= height || web > Math.Min(topWidth, bottomWidth)) return (null, $"shape '{name}' has dimensions that do not make an I-shape");
            PlanePoint[][] pieces =
            [
                SectionDesignerGeometry.Rectangle(x, y + height / 2 - topThick / 2, topWidth, topThick),
                SectionDesignerGeometry.Rectangle(x, y + (bottomThick - topThick) / 2, web, height - topThick - bottomThick),
                SectionDesignerGeometry.Rectangle(x, y - height / 2 + bottomThick / 2, bottomWidth, bottomThick),
            ];
            return (new(name, material, zOrder, pieces.Select(piece => SectionDesignerGeometry.Rotate(piece, x, y, rotation)).ToArray())
            {
                Outlines = [SectionDesignerGeometry.Rotate(SectionDesignerGeometry.WideFlange(x, y, height, topWidth, topThick, web, bottomWidth, bottomThick), x, y, rotation)]
            }, null);
        }

        // A tube: two flanges across the full width and two webs between them. Its interior is empty.
        private static (SectionDesignerShape?, string?) Box(SapTableRow row)
        {
            if (Header(row, out var name, out var material, out var zOrder, typed: true) is { } refused) return (null, refused);
            if (!Read(row, name, out var problem, ("XCenter", false), ("YCenter", false), ("Height", true), ("Width", true), ("FlngThick", true), ("WebThick", true)))
                return (null, problem);
            double x = row.Number("XCenter")!.Value, y = row.Number("YCenter")!.Value, height = row.Number("Height")!.Value, width = row.Number("Width")!.Value;
            double flange = row.Number("FlngThick")!.Value, web = row.Number("WebThick")!.Value, rotation = row.Number("Rotation") ?? 0;
            if (2 * flange >= height || 2 * web >= width) return (null, $"shape '{name}' has walls that leave no opening");
            PlanePoint[][] pieces =
            [
                SectionDesignerGeometry.Rectangle(x, y + height / 2 - flange / 2, width, flange),
                SectionDesignerGeometry.Rectangle(x, y - height / 2 + flange / 2, width, flange),
                SectionDesignerGeometry.Rectangle(x - width / 2 + web / 2, y, web, height - 2 * flange),
                SectionDesignerGeometry.Rectangle(x + width / 2 - web / 2, y, web, height - 2 * flange),
            ];
            return (new(name, material, zOrder, pieces.Select(piece => SectionDesignerGeometry.Rotate(piece, x, y, rotation)).ToArray())
            {
                Outlines =
                [
                    SectionDesignerGeometry.Rotate(SectionDesignerGeometry.Rectangle(x, y, width, height), x, y, rotation),
                    SectionDesignerGeometry.Rotate(SectionDesignerGeometry.Rectangle(x, y, width - 2 * web, height - 2 * flange), x, y, rotation),
                ]
            }, null);
        }

        private static (SectionDesignerShape?, string?) SolidRectangle(SapTableRow row)
        {
            if (Header(row, out var name, out var material, out var zOrder, typed: false) is { } refused) return (null, refused);
            if (!Read(row, name, out var problem, ("XCenter", false), ("YCenter", false), ("Height", true), ("Width", true))) return (null, problem);
            double x = row.Number("XCenter")!.Value, y = row.Number("YCenter")!.Value;
            var piece = SectionDesignerGeometry.Rectangle(x, y, row.Number("Width")!.Value, row.Number("Height")!.Value);
            return (new(name, material, zOrder, [SectionDesignerGeometry.Rotate(piece, x, y, row.Number("Rotation") ?? 0)]), null);
        }

        // One record per corner; the shape's own data (material, Z-order, reinforcement) is on the first.
        private static (SectionDesignerShape?, string?) Polygon(string shapeName, SapTableRow[] corners)
        {
            var first = corners.FirstOrDefault(row => row.Text("ShapeMat") is not null) ?? corners[0];
            if (Header(first, out _, out var material, out var zOrder, typed: false) is { } refused) return (null, refused);
            var name = shapeName.Length > 0 ? shapeName : "(unnamed)";
            if (corners.Any(row => row.Yes("Reinforcing"))) return (null, $"shape '{name}' carries reinforcement, which is not taken off");
            if (corners.Any(row => row.Number("X") is null || row.Number("Y") is null)) return (null, $"shape '{name}' has a corner without coordinates");
            if (corners.Any(row => Math.Abs(row.Number("Radius") ?? 0) > 0)) return (null, $"shape '{name}' has rounded corners, which are not read");
            var outline = corners.Select(row => new PlanePoint(row.Number("X")!.Value, row.Number("Y")!.Value)).ToArray();
            var pieces = SectionDesignerGeometry.ConvexPieces(outline);
            return pieces is null ? (null, $"shape '{name}' is not a simple polygon with an area") : (new(name, material, zOrder, pieces) { Outlines = [outline] }, null);
        }
    }
}
