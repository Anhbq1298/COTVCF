using System.IO;
using System.Text.RegularExpressions;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application.Etabs;

/// <summary>
/// Reads the database tables ETABS exports through its API (<c>File.ExportFile</c>, <c>DBTablesText</c>) into an
/// <see cref="EtabsModel"/>: <c>TABLE: "NAME"</c> headers, one record per line of <c>Key=Value</c> fields (a key or value
/// with spaces is quoted; a line ending in " _" continues), in the units PROGRAM CONTROL states. Joints come with their
/// global coordinates and frames with their end joints, so nothing is derived from the stories. ETABS's own section
/// properties (FRAME SECTION PROPERTY DEFINITIONS - SUMMARY) come along as <see cref="EtabsFrameSection.Reported"/>.
/// </summary>
public static class EtabsTableReader
{
    private static readonly Regex Field = new("""("[^"]*"|[^\s="]+)=("[^"]*"|\S*)""", RegexOptions.Compiled);
    private static readonly string[] ModifierFields =
        ["Area Modifier", "As2 Modifier", "As3 Modifier", "J Modifier", "I22 Modifier", "I33 Modifier", "Mass Modifier", "Weight Modifier"];

    public sealed class Row(Dictionary<string, string> fields)
    {
        public string? Text(string key) => fields.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
        public double? Number(string key) => EtabsText.Number(Text(key));
        public bool Yes(string key) => string.Equals(Text(key), "Yes", StringComparison.OrdinalIgnoreCase);
        public bool Has(string key) => fields.ContainsKey(key);
    }

    /// <summary>The tables of an export, by name (case-insensitive).</summary>
    public static Dictionary<string, List<Row>> Tables(string text)
    {
        var tables = new Dictionary<string, List<Row>>(StringComparer.OrdinalIgnoreCase);
        List<Row>? rows = null;
        var pending = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("TABLE:", StringComparison.Ordinal))
            {
                var name = line[6..].Trim().Trim('"');
                if (!tables.TryGetValue(name, out rows)) tables[name] = rows = [];
                pending = "";
                continue;
            }
            if (rows is null || line.Trim().Length == 0) continue;
            if (line.EndsWith(" _", StringComparison.Ordinal)) { pending += line[..^2] + " "; continue; }
            var record = pending + line;
            pending = "";
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Field.Matches(record)) fields[match.Groups[1].Value.Trim('"')] = match.Groups[2].Value.Trim('"');
            if (fields.Count > 0) rows.Add(new Row(fields));
        }
        return tables;
    }

    public static EtabsModel Read(string text, string source)
    {
        var tables = Tables(text);
        IReadOnlyList<Row> T(string name) => tables.TryGetValue(name, out var rows) ? rows : [];
        var notes = new List<string>();
        void Note(string note) { if (!notes.Contains(note)) notes.Add(note); }

        var control = T("PROGRAM CONTROL").FirstOrDefault() ?? throw new InvalidDataException("The ETABS table export has no PROGRAM CONTROL table.");
        var stated = (control.Text("CurrUnits") ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (stated.Length < 2) throw new InvalidDataException("The ETABS table export does not state its units.");
        var units = EtabsUnits.Parse(stated[0], stated[1]);
        // thermal coefficients are per degree of the stated temperature unit; Mcotis keeps them per °C
        var perDegree = stated.Length > 2 && stated[2].Equals("F", StringComparison.OrdinalIgnoreCase) ? 1.8 : 1;
        var version = control.Text("Version") is { } v ? "ETABS " + v : "ETABS";
        if (T("TOWER AND BASE STORY DEFINITIONS").Count > 1) Note("The model has more than one tower; every tower's frames are imported.");

        // materials
        var general = T("MATERIAL PROPERTIES - GENERAL").ToDictionary(row => row.Text("Material") ?? "", StringComparer.Ordinal);
        Row? Find(string table, string material) => T(table).FirstOrDefault(row => row.Text("Material") == material);
        var materials = T("MATERIAL PROPERTIES - BASIC MECHANICAL PROPERTIES").Select(row =>
        {
            var name = row.Text("Material") ?? "";
            var type = general.GetValueOrDefault(name);
            var kind = EtabsText.MaterialKind(type?.Text("Type"));
            var data = kind switch
            {
                MaterialKind.Concrete => Find("MATERIAL PROPERTIES - CONCRETE DATA", name),
                MaterialKind.Rebar => Find("MATERIAL PROPERTIES - REBAR DATA", name),
                MaterialKind.Tendon => Find("MATERIAL PROPERTIES - TENDON DATA", name),
                _ => Find("MATERIAL PROPERTIES - STEEL DATA", name),
            };
            double? Stress(string key) => data?.Number(key) is { } value ? units.Stress(value) : null;
            var weight = row.Number("UnitWeight") is { } w ? units.UnitWeight(w)
                : row.Number("UnitMass") is { } mass ? mass * units.KiloNewtons / Math.Pow(units.Metres, 4) * MaterialProperty.Gravity : 0;
            return new EtabsMaterial(name, kind, type?.Text("Grade") ?? "", weight, row.Number("E1") is { } e ? units.Stress(e) : 0,
                row.Number("U12") ?? 0, row.Number("A1") is { } a ? a * perDegree : 0,
                Stress("Fc"), Stress("Fy"), Stress("Fu"), Stress("Fye"), Stress("Fue"));
        }).ToArray();

        // sections: the summary (shape, material, ETABS's properties, modifiers), then each shape's own table
        var barSizes = T("REINFORCING BAR SIZES").Where(row => row.Text("Name") is not null && row.Number("Diameter") is not null)
            .ToDictionary(row => row.Text("Name")!, row => units.Length(row.Number("Diameter")!.Value), StringComparer.OrdinalIgnoreCase);
        var sdShapes = SdShapes(tables, units);
        var sections = T("FRAME SECTION PROPERTY DEFINITIONS - SUMMARY").Select(summary =>
        {
            var name = summary.Text("Name") ?? "";
            var shapeName = summary.Text("Shape") ?? "";
            var shape = EtabsText.Shape(shapeName);
            double Area(Row row, string key) => row.Number(key) is { } value ? units.Area(value) : 0;
            double Inertia(Row row, string key) => row.Number(key) is { } value ? units.Inertia(value) : 0;
            var section = new EtabsFrameSection
            {
                Name = name,
                ShapeName = shapeName,
                Shape = shape,
                Material = summary.Text("Material") ?? "",
                Reported = new EtabsSectionValues(Area(summary, "Area"), Inertia(summary, "I33"), Inertia(summary, "I22"), Inertia(summary, "J"),
                    Area(summary, "As2"), Area(summary, "As3")),
                Modifiers = ModifierFields.Select(key => summary.Number(key) ?? 1).ToArray(),
            };
            var definition = shape switch
            {
                EtabsShape.I => Definition(tables, "FRAME SECTION PROPERTY DEFINITIONS - STEEL I/WIDE FLANGE", name),
                EtabsShape.Tube => Definition(tables, "FRAME SECTION PROPERTY DEFINITIONS - STEEL TUBE", name),
                EtabsShape.Pipe => Definition(tables, "FRAME SECTION PROPERTY DEFINITIONS - STEEL PIPE", name),
                EtabsShape.Rectangle => Definition(tables, "FRAME SECTION PROPERTY DEFINITIONS - CONCRETE RECTANGULAR", name),
                EtabsShape.Circle => Definition(tables, "FRAME SECTION PROPERTY DEFINITIONS - CONCRETE CIRCLE", name),
                EtabsShape.FilledTube => Definition(tables, "FRAME SECTION PROPERTY DEFINITIONS - FILLED STEEL TUBE", name),
                EtabsShape.FilledPipe => Definition(tables, "FRAME SECTION PROPERTY DEFINITIONS - FILLED STEEL PIPE", name),
                EtabsShape.Encasement => Definition(tables, "FRAME SECTION PROPERTY DEFINITIONS - CONC ENCASEMENT RECTANGLE", name),
                EtabsShape.SectionDesigner => Definition(tables, "FRAME SECTION PROPERTY DEFINITIONS - SECTION DESIGNER", name),
                _ => null,
            };
            if (definition is null)
                return section with { Shape = EtabsShape.Other };
            double L(params string[] keys) => keys.Select(definition.Number).FirstOrDefault(value => value is not null) is { } value ? units.Length(value) : 0;
            section = shape switch
            {
                EtabsShape.I => section with
                {
                    Depth = L("Total Depth"), Width = L("Top Flange Width"), FlangeThickness = L("Top Flange Thickness"), WebThickness = L("Web Thickness"),
                    BottomWidth = L("Bottom Flange Width", "Top Flange Width"), BottomFlangeThickness = L("Bottom Flange Thickness", "Top Flange Thickness"),
                },
                EtabsShape.Tube or EtabsShape.FilledTube => section with
                {
                    Depth = L("Total Depth"), Width = L("Total Width"), FlangeThickness = L("Flange Thickness"), WebThickness = L("Web Thickness"),
                    FillMaterial = definition.Text("Fill Material") ?? "",
                },
                EtabsShape.Pipe or EtabsShape.FilledPipe => section with
                {
                    Depth = L("Outside Diameter", "Diameter"), Thickness = L("Wall Thickness", "Thickness"), FillMaterial = definition.Text("Fill Material") ?? "",
                },
                EtabsShape.Rectangle => section with { Depth = L("Depth"), Width = L("Width") },
                EtabsShape.Circle => section with { Depth = L("Diameter") },
                EtabsShape.Encasement => section with
                {
                    Depth = L("Depth"), Width = L("Width"), EncasementMaterial = definition.Text("Encasement Material") ?? "",
                    EmbeddedSection = definition.Text("Embedded I-Section") ?? definition.Text("Embedded Section") ?? "",
                },
                EtabsShape.SectionDesigner => section with { Material = definition.Text("Base Material") ?? section.Material },
                _ => section,
            };
            if (shape is EtabsShape.Rectangle or EtabsShape.Circle or EtabsShape.Encasement &&
                Definition(tables, "FRAME SECTION PROPERTY DEFINITIONS - CONCRETE COLUMN REINFORCING", name) is { } bars)
            {
                if (string.Equals(bars.Text("Reinforcement Configuration"), "Rectangular", StringComparison.OrdinalIgnoreCase))
                {
                    double Bar(string key) => bars.Text(key) is { } size ? barSizes.GetValueOrDefault(size, EtabsText.Number(size) is { } mm ? mm / 1000 : 0) : 0;
                    section = section with
                    {
                        Rebar = new EtabsColumnRebar(bars.Text("Longitudinal Bar Material") ?? "", bars.Text("Tie Bar Material") ?? "",
                            bars.Number("Clear Cover to Ties") is { } cover ? units.Length(cover) : 0, Bar("Longitudinal Bar Size"),
                            (int)(bars.Number("Number Bars 3-Dir") ?? 0), (int)(bars.Number("Number Bars 2-Dir") ?? 0), Bar("Tie Bar Size"),
                            bars.Number("Tie Bar Spacing") is { } spacing ? units.Length(spacing) : 0),
                    };
                }
                else Note($"Circular column reinforcement of {name} is not read.");
            }
            if (shape == EtabsShape.SectionDesigner)
            {
                var shapes = sdShapes.GetValueOrDefault(name) ?? [];
                section = section with { SdShapes = shapes };
                if (SdShapeCount(tables, name) != shapes.Count) section = section with { SdShapes = [] };
            }
            return section;
        }).ToArray();

        // joints with their coordinates, by unique name
        var joints = new Dictionary<string, EtabsJoint>(StringComparer.Ordinal);
        foreach (var row in T("POINT OBJECT CONNECTIVITY"))
        {
            var unique = row.Text("UniqueName") ?? row.Text("Unique Name");
            var label = row.Text("PointBay") ?? row.Text("Label") ?? unique;
            if (unique is null || label is null) continue;
            if (row.Number("X") is not { } x || row.Number("Y") is not { } y || row.Number("Z") is not { } z)
                throw new InvalidDataException($"ETABS point {label} has no coordinates.");
            joints[unique] = new EtabsJoint(label, row.Text("Story") ?? "", units.Length(x), units.Length(y), units.Length(z));
        }
        string JointName(string? unique, string what) =>
            unique is not null && joints.TryGetValue(unique, out var joint) ? joint.Name : throw new InvalidDataException($"ETABS {what} refers to point {unique}, which has no coordinates.");

        // frames, by unique name
        IReadOnlyDictionary<string, Row> ByUnique(string table) => T(table)
            .Where(row => (row.Text("UniqueName") ?? row.Text("Unique Name")) is not null)
            .GroupBy(row => (row.Text("UniqueName") ?? row.Text("Unique Name"))!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sectionOf = ByUnique("FRAME ASSIGNMENTS - SECTION PROPERTIES");
        var summaryOf = ByUnique("FRAME ASSIGNMENTS - SUMMARY");
        var releasesOf = ByUnique("FRAME ASSIGNMENTS - RELEASES AND PARTIAL FIXITY");
        var axesOf = ByUnique("FRAME ASSIGNMENTS - LOCAL AXES");
        var modifiersOf = ByUnique("FRAME ASSIGNMENTS - PROPERTY MODIFIERS");
        foreach (var row in T("FRAME ASSIGNMENTS - INSERTION POINT"))
            if (row.Text("Cardinal Point") is { } point && !point.StartsWith("10", StringComparison.Ordinal))
                Note($"Frame insertion points (frame {row.Text("Label")} at {row.Text("Story")} …) are not modelled; members act at their centroid.");
        foreach (var row in T("FRAME ASSIGNMENTS - END LENGTH OFFSETS"))
            if ((row.Number("Rigid Factor") ?? 0) > 0)
                Note($"Rigid end zones (frame {row.Text("Label")} at {row.Text("Story")} …) are not modelled.");

        var frames = new List<EtabsFrame>();
        var frameKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (table, bay, kind) in new[] { ("BEAM OBJECT CONNECTIVITY", "BeamBay", "Beam"), ("COLUMN OBJECT CONNECTIVITY", "ColumnBay", "Column"),
                     ("BRACE OBJECT CONNECTIVITY", "BraceBay", "Brace") })
        foreach (var row in T(table))
        {
            var unique = row.Text("Unique Name") ?? row.Text("UniqueName");
            var label = row.Text(bay) ?? row.Text("Label") ?? unique;
            if (unique is null || label is null) continue;
            var story = row.Text("Story") ?? "";
            var section = sectionOf.GetValueOrDefault(unique)?.Text("Section Property") ?? summaryOf.GetValueOrDefault(unique)?.Text("Analysis Section")
                ?? throw new InvalidDataException($"ETABS frame {label} at {story} has no section.");
            var releases = new HashSet<string>(StringComparer.Ordinal);
            if (releasesOf.GetValueOrDefault(unique) is { } released)
                foreach (var dof in new[] { "PI", "PJ", "V2I", "V2J", "V3I", "V3J", "TI", "TJ", "M2I", "M2J", "M3I", "M3J" })
                    if (released.Yes(dof)) releases.Add(dof);
            var angle = axesOf.GetValueOrDefault(unique)?.Number("Angle") ?? 0;
            var modifiers = modifiersOf.GetValueOrDefault(unique) is { } modified
                ? ModifierFields.Select(key => modified.Number(key) ?? 1).ToArray() : EtabsModel.NoModifiers;
            var frame = new EtabsFrame(label, story, kind, JointName(row.Text("UniquePtI"), $"frame {label}"), JointName(row.Text("UniquePtJ"), $"frame {label}"),
                section, releases, angle, modifiers);
            frames.Add(frame);
            frameKeys[unique] = frame.Key;
        }
        if (T("FRAME ASSIGNMENTS - SUMMARY").Count == 0 && frames.Count == 0) throw new InvalidDataException("The ETABS table export has no frame objects.");

        var restraints = T("JOINT ASSIGNMENTS - RESTRAINTS").Select(row => new EtabsRestraint(
            JointName(row.Text("UniqueName"), "restraint"), new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.Select(row.Yes).ToArray())).ToArray();
        if (T("JOINT ASSIGNMENTS - SPRINGS").Count > 0) Note("Joint springs are not imported — Mcotis supports pinned column bases.");

        var jointLoads = T("JOINT LOADS ASSIGNMENTS - FORCE").Select(row =>
        {
            double F(string key) => units.Force(row.Number(key) ?? 0);
            double M(string key) => units.Moment(row.Number(key) ?? 0);
            return new EtabsJointLoad(JointName(row.Text("UniqueName"), "joint load"), row.Text("Load Pattern") ?? "", F("FX"), F("FY"), F("FZ"), M("MX"), M("MY"), M("MZ"));
        }).ToArray();
        if (T("JOINT LOADS ASSIGNMENTS - GROUND DISPLACEMENT").Count > 0) Note("Ground displacements are not imported.");

        var frameLoads = new List<EtabsFrameLoad>();
        foreach (var row in T("FRAME LOADS ASSIGNMENTS - DISTRIBUTED"))
        {
            var unique = row.Text("UniqueName") ?? row.Text("Unique Name");
            if (unique is null || !frameKeys.TryGetValue(unique, out var key)) continue;
            var isForce = !string.Equals(row.Text("Load Type"), "Moment", StringComparison.OrdinalIgnoreCase);
            var relative = !string.Equals(row.Text("Distance Type"), "Absolute", StringComparison.OrdinalIgnoreCase);
            if (!isForce || !relative || row.Number("Force A") is not { } a || row.Number("Force B") is not { } b)
            { Note($"Distributed frame loads ETABS states as moments or at absolute distances (frame {row.Text("Label")} …) are not imported."); continue; }
            frameLoads.Add(new(key, row.Text("Load Pattern") ?? "", false, EtabsText.Direction(row.Text("Direction")), row.Text("Direction") ?? "",
                row.Number("Relative Distance A") ?? 0, row.Number("Relative Distance B") ?? 1, units.LineForce(a), units.LineForce(b)));
        }
        foreach (var row in T("FRAME LOADS ASSIGNMENTS - POINT"))
        {
            var unique = row.Text("UniqueName") ?? row.Text("Unique Name");
            if (unique is null || !frameKeys.TryGetValue(unique, out var key)) continue;
            var isForce = !string.Equals(row.Text("Load Type"), "Moment", StringComparison.OrdinalIgnoreCase);
            if (!isForce || row.Number("Force") is not { } force || row.Number("Relative Distance") is not { } at)
            { Note($"Frame point loads ETABS states as moments or at absolute distances (frame {row.Text("Label")} …) are not imported."); continue; }
            frameLoads.Add(new(key, row.Text("Load Pattern") ?? "", true, EtabsText.Direction(row.Text("Direction")), row.Text("Direction") ?? "", at, at,
                units.Force(force), units.Force(force)));
        }
        if (T("FRAME LOADS ASSIGNMENTS - TEMPERATURE").Count > 0) Note("Frame temperature loads are not imported.");

        foreach (var table in tables.Keys.Where(key => key.StartsWith("AREA OBJECT CONNECTIVITY", StringComparison.OrdinalIgnoreCase) ||
                                                        key.StartsWith("WALL OBJECT", StringComparison.OrdinalIgnoreCase) ||
                                                        key.StartsWith("FLOOR OBJECT", StringComparison.OrdinalIgnoreCase)))
            if (tables[table].Count > 0) Note("Floors and walls (area objects) are not imported — Mcotis analyses frames in the XZ plane only.");
        if (T("LINK OBJECT CONNECTIVITY").Count > 0) Note("Link objects are not imported.");

        var patterns = T("LOAD PATTERN DEFINITIONS").Select(row => new EtabsLoadPattern(row.Text("Name") ?? "", row.Text("Type") ?? "Other",
            row.Number("Self Weight Multiplier") ?? 0)).ToArray();

        var cases = new List<EtabsLoadCase>();
        foreach (var summary in T("LOAD CASE DEFINITIONS - SUMMARY"))
        {
            var name = summary.Text("Name") ?? "";
            var type = summary.Text("Type") ?? "";
            var table = type.Equals("Linear Static", StringComparison.OrdinalIgnoreCase) ? "LOAD CASE DEFINITIONS - LINEAR STATIC"
                : type.Equals("Nonlinear Static", StringComparison.OrdinalIgnoreCase) ? "LOAD CASE DEFINITIONS - NONLINEAR STATIC" : null;
            var rows = table is null ? [] : T(table).Where(row => row.Text("Name") == name).ToArray();
            var loads = new List<(string, double)>();
            var caseNotes = new List<string>();
            foreach (var row in rows)
            {
                if (row.Text("Load Name") is not { } load) continue;
                if (string.Equals(row.Text("Load Type") ?? "Load", "Load", StringComparison.OrdinalIgnoreCase)) loads.Add((load, row.Number("Load SF") ?? 1));
                else caseNotes.Add("Acceleration loads are not used.");
            }
            var first = rows.FirstOrDefault();
            string? initial = null;
            if (first is not null)
            {
                if (first.Text("Stiffness Type") is { } stiffness && !stiffness.Equals("Zero", StringComparison.OrdinalIgnoreCase) &&
                    !stiffness.Equals("Unstressed", StringComparison.OrdinalIgnoreCase))
                    caseNotes.Add(stiffness.Contains("Delta", StringComparison.OrdinalIgnoreCase)
                        ? "ETABS starts it from the preset P-Δ stiffness; here it starts unstressed." : $"ETABS stiffness '{stiffness}' is not modelled; here it starts unstressed.");
                if (first.Text("Geometric Nonlinearity") is { } geometric && !geometric.Equals("None", StringComparison.OrdinalIgnoreCase))
                    caseNotes.Add($"ETABS geometric nonlinearity ({geometric}) is not modelled by the internal solver.");
                var nonlinearCase = first.Text("Nonlinear Case");
                if (!FemLoadPlan.IsNoInitialCase(nonlinearCase)) initial = nonlinearCase;
            }
            cases.Add(new(name, type, loads, initial, caseNotes));
        }

        var combinations = new List<EtabsCombination>();
        foreach (var group in T("LOAD COMBINATION DEFINITIONS").Where(row => row.Text("Name") is not null).GroupBy(row => row.Text("Name")!, StringComparer.Ordinal))
            combinations.Add(new(group.Key, group.Select(row => row.Text("Type")).FirstOrDefault(type => type is not null) ?? "Linear Add",
                group.Where(row => row.Text("Load Name") is not null).Select(row => (row.Text("Load Name")!, row.Number("SF") ?? 1)).ToArray()));

        return new EtabsModel(source, "", version, materials, sections, joints.Values.ToArray(), frames, restraints, jointLoads, frameLoads,
            patterns, cases, combinations, notes);
    }

    private static Row? Definition(Dictionary<string, List<Row>> tables, string table, string name) =>
        tables.TryGetValue(table, out var rows) ? rows.FirstOrDefault(row => row.Text("Name") == name) : null;

    /// <summary>Every Section Designer shape table's rows for a section, to tell whether all of them were read.</summary>
    private static int SdShapeCount(Dictionary<string, List<Row>> tables, string section) => tables
        .Where(pair => pair.Key.StartsWith("SECTION DESIGNER SHAPES", StringComparison.OrdinalIgnoreCase))
        .Sum(pair => pair.Value.Count(row => row.Text("Section Name") == section));

    private static Dictionary<string, List<EtabsSdShape>> SdShapes(Dictionary<string, List<Row>> tables, EtabsUnits units)
    {
        var shapes = new Dictionary<string, List<EtabsSdShape>>(StringComparer.Ordinal);
        void Add(string table, Func<Row, EtabsSdShape> make)
        {
            if (!tables.TryGetValue(table, out var rows)) return;
            foreach (var row in rows)
            {
                if (row.Text("Section Name") is not { } section || !string.Equals(row.Text("Section Type") ?? "Frame", "Frame", StringComparison.OrdinalIgnoreCase)) continue;
                if (!shapes.TryGetValue(section, out var list)) shapes[section] = list = [];
                list.Add(make(row));
            }
        }
        double L(Row row, string key) => row.Number(key) is { } value ? units.Length(value) : 0;
        int Z(Row row) => (int)(row.Number("Z Order") ?? 0);
        // an opening has no material
        Add("SECTION DESIGNER SHAPES - CONCRETE RECTANGLE", row => new(Z(row), EtabsSdShapeKind.Rectangle, row.Text("Material"), L(row, "X Center"),
            L(row, "Y Center"), L(row, "Height"), L(row, "Width"), 0, row.Number("Rotation") ?? 0));
        Add("SECTION DESIGNER SHAPES - STEEL PLATE", row => new(Z(row), EtabsSdShapeKind.Rectangle, row.Text("Material"), L(row, "X Center"),
            L(row, "Y Center"), L(row, "Thickness"), L(row, "Width"), 0, row.Number("Rotation") ?? 0));
        Add("SECTION DESIGNER SHAPES - CONCRETE CIRCLE", row => new(Z(row), EtabsSdShapeKind.Circle, row.Text("Material"), L(row, "X Center"),
            L(row, "Y Center"), L(row, "Diameter"), L(row, "Diameter"), 0, 0));
        Add("SECTION DESIGNER SHAPES - STEEL PIPE", row => new(Z(row), EtabsSdShapeKind.Pipe, row.Text("Material"), L(row, "X Center"),
            L(row, "Y Center"), L(row, "Diameter"), L(row, "Diameter"), L(row, "Thickness"), 0));
        return shapes;
    }
}
