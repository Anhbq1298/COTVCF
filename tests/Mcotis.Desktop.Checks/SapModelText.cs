using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

/// <summary>
/// Reads SAP2000's own text copy of a model (the <c>.$2k</c> / <c>.s2k</c> file written beside the <c>.sdb</c>) into
/// the inputs of the internal FEM — geometry, section and material properties, load patterns, joint loads, static
/// load cases and supports — the way <c>SapGateway</c> reads them from a running SAP2000. It lets the FEM be run on
/// exactly the model SAP2000 analysed, offline, so the two can be compared like for like.
/// <para>Whatever in the file affects the analysis and is not taken over is refused, not skipped: member loads,
/// property modifiers and releases on objects, springs, constraints, links, areas, tendons and combinations.</para>
/// </summary>
internal sealed class SapModelText
{
    private static readonly Regex Pair = new("""([A-Za-z0-9_]+)=("[^"]*"|\S+)""", RegexOptions.Compiled);

    // Tables that change the analysis but that this reader does not map onto the FEM inputs.
    private static readonly string[] Unmapped =
    [
        "FRAME LOADS -", "CABLE LOADS - DISTRIBUTED", "CABLE LOADS - GRAVITY", "CABLE LOADS - TEMPERATURE", "CABLE LOADS - STRAIN",
        "CABLE LOADS - DEFORMATION", "FRAME PROPERTY MODIFIERS", "CABLE PROPERTY MODIFIERS", "FRAME RELEASE ASSIGNMENTS",
        "FRAME INSERTION POINT", "FRAME END LENGTH OFFSETS", "FRAME MATERIAL OVERWRITES", "CABLE MATERIAL OVERWRITES",
        "JOINT SPRING", "JOINT CONSTRAINT", "CONSTRAINT DEFINITIONS", "JOINT LOADS - GROUND", "JOINT ADDED MASS",
        "CONNECTIVITY - TENDON", "CONNECTIVITY - AREA", "CONNECTIVITY - LINK", "CONNECTIVITY - SOLID", "COMBINATION DEFINITIONS",
        "CASE - STATIC 5", "CASE - STATIC 6", "FRAME LOCAL AXES ASSIGNMENTS 2",
    ];

    private readonly Dictionary<string, List<Dictionary<string, string>>> tables = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Notes { get; } = [];

    public static SapModelText Read(string path)
    {
        var model = new SapModelText();
        List<Dictionary<string, string>>? rows = null;
        var pending = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("TABLE:", StringComparison.Ordinal))
            {
                var name = line[6..].Trim().Trim('"');
                model.tables[name] = rows = [];
                pending = "";
                continue;
            }
            if (rows is null || line.Trim().Length == 0) continue;
            if (line.EndsWith(" _", StringComparison.Ordinal)) { pending += line[..^2] + " "; continue; }      // row continues
            var text = pending + line;
            pending = "";
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Pair.Matches(text)) row[match.Groups[1].Value] = match.Groups[2].Value.Trim('"');
            if (row.Count > 0) rows.Add(row);
        }
        var unmapped = model.tables.Where(table => table.Value.Count > 0 && Unmapped.Any(prefix => table.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(table => table.Key).ToArray();
        if (unmapped.Length > 0)
            throw new NotSupportedException("The model text holds analysis input this reader does not take over: " + string.Join(", ", unmapped));
        var units = model.Rows("PROGRAM CONTROL").Select(row => Text(row, "CurrUnits")).FirstOrDefault() ?? "";
        if (!Regex.IsMatch(units, @"^\s*KN\s*,\s*m\s*,", RegexOptions.IgnoreCase))
            throw new NotSupportedException($"The model text must be saved in kN, m units; it is in '{units}'.");
        var active = model.Rows("ACTIVE DEGREES OF FREEDOM").FirstOrDefault();
        if (active is not null && (Yes(active, "UY") || Yes(active, "RX") || Yes(active, "RZ")))
            model.Notes.Add("SAP2000 analysed the model with out-of-plane degrees of freedom active; the FEM is planar (UX, UZ, RY).");
        return model;
    }

    private IReadOnlyList<Dictionary<string, string>> Rows(string table) =>
        tables.TryGetValue(table, out var rows) ? rows : [];

    private static string Text(IReadOnlyDictionary<string, string> row, string key, string fallback = "") =>
        row.TryGetValue(key, out var value) ? value : fallback;
    private static double Number(IReadOnlyDictionary<string, string> row, string key, double fallback = 0) =>
        row.TryGetValue(key, out var value) ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;
    private static bool Yes(IReadOnlyDictionary<string, string> row, string key) => Text(row, key).Equals("Yes", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> LoadPatternNames => Rows("LOAD PATTERN DEFINITIONS").Select(row => Text(row, "LoadPat")).ToArray();
    public IReadOnlyList<string> LoadCaseNames => Rows("LOAD CASE DEFINITIONS").Select(row => Text(row, "Case")).ToArray();

    /// <summary>Geometric nonlinearity SAP2000 used per nonlinear static case: None, P-Delta or Large Displ.</summary>
    public IReadOnlyDictionary<string, string> GeometricNonlinearity => Rows("CASE - STATIC 4 - NONLINEAR PARAMETERS")
        .ToDictionary(row => Text(row, "Case"), row => Text(row, "GeoNonLin", "None"), StringComparer.OrdinalIgnoreCase);

    /// <summary>SAP2000's own Target-Force tolerance per nonlinear case (TFTol), as a percentage.</summary>
    public IReadOnlyDictionary<string, double> TargetForceTolerancePercent => Rows("CASE - STATIC 4 - NONLINEAR PARAMETERS")
        .ToDictionary(row => Text(row, "Case"), row => Number(row, "TFTol", 0.01) * 100, StringComparer.OrdinalIgnoreCase);

    public GeometryModel Geometry()
    {
        var points = new Dictionary<string, StructuralPoint>(StringComparer.Ordinal);
        foreach (var row in Rows("JOINT COORDINATES"))
        {
            if (!Text(row, "CoordSys", "GLOBAL").Equals("GLOBAL", StringComparison.OrdinalIgnoreCase) ||
                !Text(row, "CoordType", "Cartesian").Equals("Cartesian", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Joint '{Text(row, "Joint")}' is not given in global Cartesian coordinates.");
            var name = Text(row, "Joint");
            points[name] = new(name, new(Number(row, "XorR"), Number(row, "Y"), Number(row, "Z")));
        }
        var members = Rows("CONNECTIVITY - FRAME").Select(row =>
            {
                if (Yes(row, "IsCurved")) throw new NotSupportedException($"Frame '{Text(row, "Frame")}' is curved.");
                return new StructuralMember(Text(row, "Frame"), Text(row, "JointI"), Text(row, "JointJ"), MemberKind.Frame);
            })
            .Concat(Rows("CONNECTIVITY - CABLE").Select(row => new StructuralMember(Text(row, "Cable"), Text(row, "JointI"), Text(row, "JointJ"), MemberKind.Cable)))
            .ToArray();
        return new GeometryModel(points, members);
    }

    /// <summary>Each cable object's shape definition as SAP2000 stores it: how the cable is defined, its undeformed
    /// length [m] and that length relative to the chord between its end joints.</summary>
    public IReadOnlyDictionary<string, (string Type, double UndeformedLengthM, double RelativeLength)> CableShapes() =>
        Rows("CABLE SHAPE DATA").ToDictionary(row => Text(row, "Cable"),
            row => (Text(row, "CableType"), Number(row, "UnDefLength"), Number(row, "UnDefRelLen", 1)), StringComparer.Ordinal);

    /// <summary>Joints SAP2000 restrains in X and Z — the pin supports of the FEM.</summary>
    public IReadOnlyList<string> PinSupports()
    {
        var supports = new List<string>();
        foreach (var row in Rows("JOINT RESTRAINT ASSIGNMENTS"))
        {
            var joint = Text(row, "Joint");
            if (Yes(row, "U1") && Yes(row, "U3")) supports.Add(joint);
            else if (Yes(row, "U1") || Yes(row, "U3")) Notes.Add($"Joint {joint} is restrained in one in-plane direction only (a roller); the FEM has pins only, so it is left free.");
            if (Yes(row, "R2")) Notes.Add($"Joint {joint} is fixed against in-plane rotation in SAP2000; the FEM pins leave rotation free.");
        }
        return supports;
    }

    /// <summary>Section and material of every frame and cable, as <c>SapGateway.GetFemSectionPropertiesAsync</c> returns
    /// them: section properties as defined, the object's own modifiers (none in a file this reader accepts), and the
    /// in-plane inertia from the local-axis angle.</summary>
    public IReadOnlyDictionary<string, FemSectionProperties> SectionProperties()
    {
        var materials = Rows("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES").ToDictionary(row => Text(row, "Material"), StringComparer.Ordinal);
        (double Modulus, double Weight, double Mass) Material(string name, string member)
        {
            if (!materials.TryGetValue(name, out var row)) throw new InvalidDataException($"Material '{name}' of '{member}' is not defined in the model text.");
            return (Number(row, "E1"), Number(row, "UnitWeight"), Number(row, "UnitMass"));
        }
        string Effective(Dictionary<string, string> assignment, string sectionMaterial) =>
            Text(assignment, "MatProp", "Default") is var over && !over.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
            !over.Equals("None", StringComparison.OrdinalIgnoreCase) && over.Length > 0 ? over : sectionMaterial;

        var result = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal);
        var frameSections = Rows("FRAME SECTION PROPERTIES 01 - GENERAL").ToDictionary(row => Text(row, "SectionName"), StringComparer.Ordinal);
        var angles = Rows("FRAME LOCAL AXES ASSIGNMENTS 1 - TYPICAL").ToDictionary(row => Text(row, "Frame"), row => Number(row, "Angle"), StringComparer.Ordinal);
        foreach (var assignment in Rows("FRAME SECTION ASSIGNMENTS"))
        {
            var frame = Text(assignment, "Frame");
            var sectionName = Text(assignment, "AnalSect");
            if (!frameSections.TryGetValue(sectionName, out var section)) throw new InvalidDataException($"Section '{sectionName}' of frame '{frame}' is not defined in the model text.");
            // Modifiers written into the section definition itself: SAP2000 applies them, the FEM's SAP read does not.
            if (new[] { "AMod", "I2Mod", "I3Mod", "WMod" }.Any(key => Math.Abs(Number(section, key, 1) - 1) > 1e-12))
                Notes.Add($"Section '{sectionName}' carries property modifiers in its definition; SAP2000 applies them, the FEM reads only modifiers assigned to objects.");
            var material = Effective(assignment, Text(section, "Material"));
            var (modulus, weight, mass) = Material(material, frame);
            if (Text(section, "Shape").Equals("SD Section", StringComparison.OrdinalIgnoreCase))
                (weight, mass) = CompositeUnitWeight(sectionName, Number(section, "Area"), material, name => Material(name, frame), weight);
            var angle = angles.GetValueOrDefault(frame);
            double cosine = Math.Cos(angle * Math.PI / 180), sine = Math.Sin(angle * Math.PI / 180);
            double area = Number(section, "Area"), i33 = Number(section, "I33"), i22 = Number(section, "I22");
            result[frame] = new FemSectionProperties(frame, area, modulus, i33 * cosine * cosine + i22 * sine * sine, false, false,
                sectionName, material, angle, weight, mass, area, [1, 1, 1, 1, 1, 1, 1, 1], FemSectionShape.EquivalentRectangle(area, i33, i22));
        }
        var cableSections = Rows("CABLE SECTION DEFINITIONS").ToDictionary(row => Text(row, "CableSect"), StringComparer.Ordinal);
        foreach (var assignment in Rows("CABLE SECTION ASSIGNMENTS"))
        {
            var cable = Text(assignment, "Cable");
            var sectionName = Text(assignment, "CableSect");
            if (!cableSections.TryGetValue(sectionName, out var section)) throw new InvalidDataException($"Section '{sectionName}' of cable '{cable}' is not defined in the model text.");
            if (new[] { "AMod", "WMod" }.Any(key => Math.Abs(Number(section, key, 1) - 1) > 1e-12))
                Notes.Add($"Cable section '{sectionName}' carries property modifiers in its definition; SAP2000 applies them, the FEM reads only modifiers assigned to objects.");
            var material = Effective(assignment, Text(section, "Material"));
            var (modulus, weight, mass) = Material(material, cable);
            var area = Number(section, "Area");
            result[cable] = new FemSectionProperties(cable, area, modulus, null, SectionName: sectionName, MaterialName: material,
                UnitWeightKNPerM3: weight, UnitMassTPerM3: mass, GrossAreaM2: area, Modifiers: [1, 1, 1]);
        }
        return result;
    }

    private readonly HashSet<string> compositeNoted = new(StringComparer.Ordinal);

    /// <summary>
    /// Unit weight and mass that give a Section Designer section its real self-weight when multiplied by the area
    /// SAP2000 reports for it. That area is transformed to the section's base material (a steel shape counts E_s/E_base
    /// times), so base-material unit weight × area overstates a composite section. SAP2000 weighs each shape with its
    /// own material: net base-material area × γ_base + Σ other shapes' area × their γ. The net base area follows from
    /// the reported area itself — area − Σ A_other·E_other/E_base — which already accounts for shapes that displace
    /// base material. Whether that is right is checked by the benchmark's total vertical reaction.
    /// </summary>
    private (double Weight, double Mass) CompositeUnitWeight(string sectionName, double transformedArea, string baseMaterial,
        Func<string, (double Modulus, double Weight, double Mass)> material, double baseWeight)
    {
        var shapes = new List<(string Material, double Area)>();
        foreach (var row in Rows("SECTION DESIGNER PROPERTIES 04 - SHAPE I/WIDE FLANGE").Where(row => Text(row, "SectionName") == sectionName))
            shapes.Add((Text(row, "ShapeMat"), Number(row, "TopWidth") * Number(row, "TopThick") + Number(row, "BotWidth") * Number(row, "BotThick") +
                (Number(row, "Height") - Number(row, "TopThick") - Number(row, "BotThick")) * Number(row, "WebThick")));
        foreach (var row in Rows("SECTION DESIGNER PROPERTIES 09 - SHAPE BOX/TUBE").Where(row => Text(row, "SectionName") == sectionName))
            shapes.Add((Text(row, "ShapeMat"), Number(row, "Height") * Number(row, "Width") -
                (Number(row, "Height") - 2 * Number(row, "FlngThick")) * (Number(row, "Width") - 2 * Number(row, "WebThick"))));
        foreach (var row in Rows("SECTION DESIGNER PROPERTIES 12 - SHAPE SOLID RECTANGLE").Where(row => Text(row, "SectionName") == sectionName))
            shapes.Add((Text(row, "ShapeMat"), Number(row, "Height") * Number(row, "Width")));
        foreach (var polygon in Rows("SECTION DESIGNER PROPERTIES 16 - SHAPE POLYGON").Where(row => Text(row, "SectionName") == sectionName)
                     .GroupBy(row => Text(row, "ShapeName"), StringComparer.Ordinal))
        {
            var vertices = polygon.Select(row => (X: Number(row, "X"), Y: Number(row, "Y"))).ToArray();
            var twiceArea = 0.0;
            for (var i = 0; i < vertices.Length; i++)
            {
                var next = vertices[(i + 1) % vertices.Length];
                twiceArea += vertices[i].X * next.Y - next.X * vertices[i].Y;
            }
            shapes.Add((polygon.Select(row => Text(row, "ShapeMat")).First(name => name.Length > 0), Math.Abs(twiceArea) / 2));
        }
        var known = new[] { "01 - GENERAL", "04 - SHAPE I/WIDE FLANGE", "09 - SHAPE BOX/TUBE", "12 - SHAPE SOLID RECTANGLE", "16 - SHAPE POLYGON",
            "21 - SHAPE REFERENCE LINE", "30 - FIBER GENERAL" };
        var unknown = tables.Keys.Where(table => table.StartsWith("SECTION DESIGNER PROPERTIES ", StringComparison.OrdinalIgnoreCase) &&
            !known.Any(suffix => table.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) &&
            Rows(table).Any(row => Text(row, "SectionName") == sectionName)).ToArray();
        if (unknown.Length > 0 || shapes.Count == 0)
            throw new NotSupportedException($"Section Designer section '{sectionName}' uses shapes this reader cannot weigh: {string.Join(", ", unknown)}");

        var (baseModulus, _, baseMass) = material(baseMaterial);
        var others = shapes.Where(shape => shape.Material != baseMaterial).ToArray();
        var netBase = transformedArea - others.Sum(shape => shape.Area * material(shape.Material).Modulus / baseModulus);
        var grossBase = shapes.Where(shape => shape.Material == baseMaterial).Sum(shape => shape.Area);
        if (netBase < -1e-6 || netBase > grossBase + 1e-6)
            throw new InvalidDataException($"Section Designer section '{sectionName}': the shapes do not add up to the area SAP2000 reports for it.");
        var weightPerLength = netBase * baseWeight + others.Sum(shape => shape.Area * material(shape.Material).Weight);
        var massPerLength = netBase * baseMass + others.Sum(shape => shape.Area * material(shape.Material).Mass);
        if (compositeNoted.Add(sectionName))
            Notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Composite Section Designer section '{sectionName}': self-weight {weightPerLength:0.0} kN/m as SAP2000 weighs it (each shape with its own material). " +
                $"The FEM's live SAP read takes {baseMaterial} unit weight × the transformed area = {transformedArea * baseWeight:0.0} kN/m, {(transformedArea * baseWeight / weightPerLength - 1) * 100:+0;-0} %."));
        return (weightPerLength / transformedArea, massPerLength / transformedArea);
    }

    /// <summary>Load patterns, joint loads and static load cases, as <c>SapGateway.GetFemLoadingAsync</c> returns them.</summary>
    public SapFemLoading Loading()
    {
        var patterns = Rows("LOAD PATTERN DEFINITIONS")
            .Select(row => new SapLoadPattern(Text(row, "LoadPat"), Text(row, "DesignType", "Other"), Number(row, "SelfWtMult"))).ToArray();
        var joints = new List<SapPatternJointLoad>();
        foreach (var row in Rows("JOINT LOADS - FORCE"))
        {
            if (!Text(row, "CoordSys", "GLOBAL").Equals("GLOBAL", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Joint load on '{Text(row, "Joint")}' is not in global coordinates.");
            if (Math.Abs(Number(row, "F2")) > 1e-9 || Math.Abs(Number(row, "M1")) > 1e-9 || Math.Abs(Number(row, "M3")) > 1e-9)
                Notes.Add($"Joint load on {Text(row, "Joint")} ({Text(row, "LoadPat")}) has out-of-plane components; only F1, F3 and M2 are used.");
            joints.Add(new(Text(row, "LoadPat"), new FemJointLoad(Text(row, "Joint"), Number(row, "F1"), Number(row, "F3"), Number(row, "M2"))));
        }
        var loads = Rows("CASE - STATIC 1 - LOAD ASSIGNMENTS").ToLookup(row => Text(row, "Case"), StringComparer.OrdinalIgnoreCase);
        var cases = Rows("LOAD CASE DEFINITIONS").Select(row =>
        {
            var name = Text(row, "Case");
            var type = Text(row, "Type");
            if (type is not ("LinStatic" or "NonStatic"))
                return new SapLoadCase(name, type, false, [], null, "Not a static case — not used by the internal FEM.");
            var terms = loads[name].Where(load => Text(load, "LoadType").StartsWith("Load", StringComparison.OrdinalIgnoreCase))
                .Select(load => (Text(load, "LoadName"), Number(load, "LoadSF"))).ToArray();
            var initial = Text(row, "InitialCond");
            return new SapLoadCase(name, type == "LinStatic" ? "Linear Static" : "Nonlinear Static", true, terms,
                FemLoadPlan.IsNoInitialCase(initial) ? null : initial, null);
        }).ToArray();
        return new SapFemLoading(patterns, joints, [], [], [], cases, [], []);
    }
}
