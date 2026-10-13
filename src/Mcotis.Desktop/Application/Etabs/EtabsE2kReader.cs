using System.IO;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application.Etabs;

/// <summary>
/// Reads ETABS's .e2k text file into an <see cref="EtabsModel"/>. Records are lines of quoted names and KEY value pairs,
/// grouped under "$ SECTION" headers, in the units of the CONTROLS UNITS line. Point objects are plan locations (POINT
/// label X Y) that exist at a story when something connects to them there; a LINE joins its I point a number of stories
/// below its J point, the J point being at the story each LINEASSIGN puts it on. Sections the import does not use (grids,
/// design preferences …) are skipped; records it cannot use are listed in <see cref="EtabsModel.Notes"/>.
/// </summary>
public static class EtabsE2kReader
{
    // Keys of the records read here whose meaning is known and that do not change the 2D analysis.
    private static readonly HashSet<string> HarmlessLineAssignKeys = new(StringComparer.OrdinalIgnoreCase)
        { "SECTION", "RELEASE", "ANG", "MINNUMSTA", "AUTOMESH", "MESHATINTERSECTIONS", "MAXSTASPC", "PROPMOD", "NUMSEGS",
          "CARDINALPT", "LENGTHOFFI", "LENGTHOFFJ", "RIGIDZONE", "OFFSETXI", "OFFSETYI", "OFFSETZI", "OFFSETXJ", "OFFSETYJ", "OFFSETZJ" };
    private static readonly string[] ModifierKeys = ["AMOD", "A2MOD", "A3MOD", "JMOD", "I2MOD", "I3MOD", "MMOD", "WMOD"];

    public static EtabsModel Read(string text, string source)
    {
        var notes = new List<string>();
        void Note(string note) { if (!notes.Contains(note)) notes.Add(note); }
        var units = new EtabsUnits(1, 1, "kN, m");
        var unitsStated = false;
        string title = "", version = "";
        var stories = new List<(string Name, double? Height, double? Elevation)>();
        var materials = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var concrete = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var sdHeaders = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var sdShapes = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
        var points = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var lines = new Dictionary<string, (string Kind, string I, string J, int Below)>(StringComparer.Ordinal);
        var lineAssigns = new List<(string Label, string Story, Dictionary<string, string> Keys)>();
        var restraints = new List<(string Label, string Story, string Dofs)>();
        var patterns = new List<EtabsLoadPattern>();
        var pointLoads = new List<(string Label, string Story, Dictionary<string, string> Keys)>();
        var frameLoads = new List<(string Label, string Story, Dictionary<string, string> Keys)>();
        var caseOrder = new List<string>();
        var caseKeys = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var caseLoads = new Dictionary<string, List<(string, double)>>(StringComparer.Ordinal);
        var comboOrder = new List<string>();
        var comboTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var comboItems = new Dictionary<string, List<(string, double)>>(StringComparer.Ordinal);

        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('$'))
            {
                section = trimmed.TrimStart('$').Trim().ToUpperInvariant();
                continue;
            }
            var tokens = Tokens(trimmed);
            if (tokens.Count == 0) continue;
            var keyword = tokens[0].Text.ToUpperInvariant();
            switch (keyword)
            {
                case "UNITS" when tokens.Count >= 3:
                    units = EtabsUnits.Parse(tokens[1].Text, tokens[2].Text);
                    unitsStated = true;
                    break;
                case "TITLE2" when tokens.Count >= 2:
                    title = tokens[1].Text;
                    break;
                case "PROGRAM":
                    version = Pairs(tokens, 2).GetValueOrDefault("VERSION", "");
                    break;
                case "STORY" when tokens.Count >= 2:
                {
                    var keys = Pairs(tokens, 2);
                    stories.Add((tokens[1].Text, EtabsText.Number(keys.GetValueOrDefault("HEIGHT")), EtabsText.Number(keys.GetValueOrDefault("ELEV"))));
                    if (keys.ContainsKey("TOWER")) Note("ETABS towers are not read — the model is taken as one tower.");
                    break;
                }
                case "MATERIAL" when tokens.Count >= 2:
                    Merge(materials, tokens[1].Text, Pairs(tokens, 2));
                    break;
                case "FRAMESECTION" when tokens.Count >= 2:
                    Merge(sections, tokens[1].Text, Pairs(tokens, 2));
                    break;
                case "CONCRETESECTION" when tokens.Count >= 2:
                    Merge(concrete, tokens[1].Text, Pairs(tokens, 2));
                    break;
                case "SDSECTION" when tokens.Count >= 2:
                {
                    var keys = Pairs(tokens, 2);
                    if (keys.ContainsKey("SHAPE"))
                    {
                        if (!sdShapes.TryGetValue(tokens[1].Text, out var list)) sdShapes[tokens[1].Text] = list = [];
                        list.Add(keys);
                    }
                    else Merge(sdHeaders, tokens[1].Text, keys);
                    break;
                }
                case "POINT" when tokens.Count >= 4:
                {
                    var x = EtabsText.Number(tokens[2].Text);
                    var y = EtabsText.Number(tokens[3].Text);
                    if (x is null || y is null) throw new InvalidDataException($"The .e2k point {tokens[1].Text} has no X / Y coordinates.");
                    // a third number is a vertical offset this reader does not interpret; refuse rather than misplace the joint
                    if (tokens.Count > 4 && EtabsText.Number(tokens[4].Text) is { } dz && Math.Abs(dz) > 1e-12)
                        throw new InvalidDataException($"The .e2k point {tokens[1].Text} has a vertical offset ({tokens[4].Text}), which this import does not read. " +
                            "Import from the running ETABS instead (File → Import From ETABS reads ETABS's own joint coordinates).");
                    points[tokens[1].Text] = (units.Length(x.Value), units.Length(y.Value));
                    break;
                }
                case "LINE" when tokens.Count >= 5:
                {
                    var below = tokens.Count >= 6 && int.TryParse(tokens[5].Text, out var n) ? n : 0;
                    lines[tokens[1].Text] = (tokens[2].Text.ToUpperInvariant(), tokens[3].Text, tokens[4].Text, below);
                    break;
                }
                case "LINEASSIGN" when tokens.Count >= 3:
                    lineAssigns.Add((tokens[1].Text, tokens[2].Text, Pairs(tokens, 3)));
                    break;
                case "POINTASSIGN" when tokens.Count >= 3:
                {
                    var keys = Pairs(tokens, 3);
                    if (keys.TryGetValue("RESTRAINT", out var dofs)) restraints.Add((tokens[1].Text, tokens[2].Text, dofs));
                    if (keys.ContainsKey("SPRINGPROP") || keys.ContainsKey("SPRING")) Note($"Joint springs (point {tokens[1].Text} at {tokens[2].Text} …) are not imported — Mcotis supports pinned column bases.");
                    break;
                }
                case "LOADPATTERN" when tokens.Count >= 2:
                {
                    var keys = Pairs(tokens, 2);
                    patterns.Add(new(tokens[1].Text, keys.GetValueOrDefault("TYPE", "Other"), EtabsText.Number(keys.GetValueOrDefault("SELFWEIGHT")) ?? 0));
                    break;
                }
                case "POINTLOAD" when tokens.Count >= 3:
                    pointLoads.Add((tokens[1].Text, tokens[2].Text, Pairs(tokens, 3)));
                    break;
                case "LINELOAD" when tokens.Count >= 3:
                    frameLoads.Add((tokens[1].Text, tokens[2].Text, Pairs(tokens, 3)));
                    break;
                case "LOADCASE" when tokens.Count >= 2:
                {
                    var name = tokens[1].Text;
                    if (!caseKeys.ContainsKey(name)) { caseOrder.Add(name); caseKeys[name] = new(StringComparer.OrdinalIgnoreCase); caseLoads[name] = []; }
                    var keys = Pairs(tokens, 2);
                    if (keys.TryGetValue("LOADPAT", out var pattern)) caseLoads[name].Add((pattern, EtabsText.Number(keys.GetValueOrDefault("SF")) ?? 1));
                    else if (keys.ContainsKey("ACCEL")) Note($"Acceleration loads of load case {name} are not used.");
                    else foreach (var (key, value) in keys) caseKeys[name][key] = value;
                    break;
                }
                case "COMBO" when tokens.Count >= 2:
                {
                    var name = tokens[1].Text;
                    if (!comboItems.ContainsKey(name)) { comboOrder.Add(name); comboItems[name] = []; }
                    var keys = Pairs(tokens, 2);
                    if (keys.TryGetValue("TYPE", out var type)) comboTypes[name] = type;
                    var item = keys.GetValueOrDefault("LOADCASE") ?? keys.GetValueOrDefault("LOADCOMBO") ?? keys.GetValueOrDefault("COMBO");
                    if (item is not null) comboItems[name].Add((item, EtabsText.Number(keys.GetValueOrDefault("SF")) ?? 1));
                    break;
                }
                default:
                    if (section.StartsWith("AREA", StringComparison.Ordinal) || section.StartsWith("SHELL", StringComparison.Ordinal) ||
                        section.StartsWith("WALL", StringComparison.Ordinal) || section.StartsWith("SLAB", StringComparison.Ordinal) ||
                        section.StartsWith("DECK", StringComparison.Ordinal))
                        Note($"{Title(section)} are not imported — Mcotis analyses frames in the XZ plane only.");
                    else if (section.StartsWith("LINK", StringComparison.Ordinal) || section.Contains("SPRING", StringComparison.Ordinal))
                        Note($"{Title(section)} are not imported.");
                    break;
            }
        }
        if (!unitsStated) throw new InvalidDataException("The .e2k file does not state its units (CONTROLS UNITS line); it is not an ETABS model file.");

        // story elevations: listed from the top; the last one is the base with its elevation
        if (stories.Count == 0) throw new InvalidDataException("The .e2k file has no stories.");
        var ordered = Enumerable.Reverse(stories).ToList();
        var elevations = new Dictionary<string, double>(StringComparer.Ordinal);
        var storyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var elevation = units.Length(ordered[0].Elevation ?? 0);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0) elevation += units.Length(ordered[i].Height ?? throw new InvalidDataException($"Story {ordered[i].Name} has no height."));
            elevations[ordered[i].Name] = elevation;
            storyIndex[ordered[i].Name] = i;
        }

        var joints = new Dictionary<string, EtabsJoint>(StringComparer.Ordinal);
        string Joint(string label, string story)
        {
            if (!points.TryGetValue(label, out var plan)) throw new InvalidDataException($"The .e2k line or load refers to point {label}, which has no coordinates.");
            if (!elevations.TryGetValue(story, out var z)) throw new InvalidDataException($"The .e2k file refers to story {story}, which it does not define.");
            var joint = new EtabsJoint(label, story, plan.X, plan.Y, z);
            joints.TryAdd(joint.Name, joint);
            return joint.Name;
        }

        var frames = new List<EtabsFrame>();
        foreach (var (label, story, keys) in lineAssigns)
        {
            if (!lines.TryGetValue(label, out var connectivity)) throw new InvalidDataException($"LINEASSIGN {label} has no LINE connectivity.");
            if (!storyIndex.TryGetValue(story, out var top)) throw new InvalidDataException($"LINEASSIGN {label} names story {story}, which the file does not define.");
            var bottom = top - connectivity.Below;
            if (bottom < 0) throw new InvalidDataException($"Line {label} at {story} reaches {connectivity.Below} stories below the base.");
            var section_ = keys.GetValueOrDefault("SECTION") ?? throw new InvalidDataException($"Line {label} at {story} has no section.");
            var releases = (keys.GetValueOrDefault("RELEASE") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
            var modifiers = ModifierKeys.Select(key => EtabsText.Number(keys.GetValueOrDefault(key)) ?? 1).ToArray();
            foreach (var key in keys.Keys.Where(key => !HarmlessLineAssignKeys.Contains(key) && !ModifierKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
                Note($"Frame assignment {key} (line {label} at {story} …) is not imported.");
            if (keys.ContainsKey("CARDINALPT") && keys["CARDINALPT"] != "10") Note($"Frame insertion points (line {label} at {story} …) are not modelled; members act at their centroid.");
            if ((EtabsText.Number(keys.GetValueOrDefault("RIGIDZONE")) ?? 0) > 0) Note($"Rigid end zones (line {label} at {story} …) are not modelled.");
            frames.Add(new(label, story, Kind(connectivity.Kind), Joint(connectivity.I, ordered[bottom].Name), Joint(connectivity.J, story),
                section_, releases, EtabsText.Number(keys.GetValueOrDefault("ANG")) ?? 0, modifiers));
        }

        var restraintList = restraints.Select(item =>
        {
            var dofs = item.Dofs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(dof => dof.ToUpperInvariant()).ToHashSet();
            return new EtabsRestraint(EtabsModel.JointName(item.Label, item.Story),
                new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.Select(dofs.Contains).ToArray());
        }).ToArray();

        var jointLoads = new List<EtabsJointLoad>();
        foreach (var (label, story, keys) in pointLoads)
        {
            var type = keys.GetValueOrDefault("TYPE", "FORCE");
            if (!type.Equals("FORCE", StringComparison.OrdinalIgnoreCase)) { Note($"Point loads of type {type} (point {label} at {story} …) are not imported."); continue; }
            var pattern = keys.GetValueOrDefault("LC") ?? throw new InvalidDataException($"Point load at {label} {story} has no load pattern.");
            double Value(string key) => EtabsText.Number(keys.GetValueOrDefault(key)) ?? 0;
            jointLoads.Add(new(Joint(label, story), pattern, units.Force(Value("FX")), units.Force(Value("FY")), units.Force(Value("FZ")),
                units.Moment(Value("MX")), units.Moment(Value("MY")), units.Moment(Value("MZ"))));
        }

        var frameLoadList = new List<EtabsFrameLoad>();
        foreach (var (label, story, keys) in frameLoads)
        {
            var type = keys.GetValueOrDefault("TYPE", "").ToUpperInvariant();
            var pattern = keys.GetValueOrDefault("LC") ?? "";
            var directionText = keys.GetValueOrDefault("DIR", "");
            var direction = EtabsText.Direction(directionText);
            var key = EtabsModel.JointName(label, story);
            switch (type)
            {
                case "UNIFF" when EtabsText.Number(keys.GetValueOrDefault("FVAL")) is { } value:
                    frameLoadList.Add(new(key, pattern, false, direction, directionText, 0, 1, units.LineForce(value), units.LineForce(value)));
                    break;
                case "TRAPF" when EtabsText.Number(keys.GetValueOrDefault("FSTART")) is { } start && EtabsText.Number(keys.GetValueOrDefault("FEND")) is { } end:
                    frameLoadList.Add(new(key, pattern, false, direction, directionText, EtabsText.Number(keys.GetValueOrDefault("RDSTART")) ?? 0,
                        EtabsText.Number(keys.GetValueOrDefault("RDEND")) ?? 1, units.LineForce(start), units.LineForce(end)));
                    break;
                case "POINTF" when EtabsText.Number(keys.GetValueOrDefault("FVAL")) is { } force:
                {
                    var at = EtabsText.Number(keys.GetValueOrDefault("RDIST")) ?? 0.5;
                    frameLoadList.Add(new(key, pattern, true, direction, directionText, at, at, units.Force(force), units.Force(force)));
                    break;
                }
                default:
                    Note($"Frame loads of type {type} (line {label} at {story} …) are not imported.");
                    break;
            }
        }

        var materialList = materials.Select(pair => Material(pair.Key, pair.Value, units)).ToArray();
        var sectionList = sections.Select(pair => Section(pair.Key, pair.Value, concrete.GetValueOrDefault(pair.Key), sdHeaders.GetValueOrDefault(pair.Key),
            sdShapes.GetValueOrDefault(pair.Key), units, Note)).ToArray();

        var cases = caseOrder.Select(name =>
        {
            var keys = caseKeys[name];
            var type = keys.GetValueOrDefault("TYPE", "");
            var initial = keys.GetValueOrDefault("INITCOND");
            var caseNotes = new List<string>();
            if (initial is not null && initial.Equals("PRESET", StringComparison.OrdinalIgnoreCase))
                caseNotes.Add("ETABS starts it from the preset P-Δ stiffness; here it starts unstressed.");
            if (keys.TryGetValue("NLGEOMTYPE", out var geometric) && !geometric.Equals("None", StringComparison.OrdinalIgnoreCase))
                caseNotes.Add($"ETABS geometric nonlinearity ({geometric}) is not modelled by the internal solver.");
            var initialCase = initial is null || initial.Equals("PRESET", StringComparison.OrdinalIgnoreCase) || FemLoadPlan.IsNoInitialCase(initial) ? null : initial;
            return new EtabsLoadCase(name, type, caseLoads[name], initialCase, caseNotes);
        }).ToArray();
        var combinations = comboOrder.Select(name => new EtabsCombination(name, comboTypes.GetValueOrDefault(name, "Linear Add"), comboItems[name])).ToArray();

        return new EtabsModel(source, title, version.Length > 0 ? "ETABS " + version : "ETABS", materialList, sectionList, joints.Values.ToArray(), frames,
            restraintList, jointLoads, frameLoadList, patterns, cases, combinations, notes);
    }

    private static EtabsMaterial Material(string name, Dictionary<string, string> keys, EtabsUnits units)
    {
        double? Stress(string key) => EtabsText.Number(keys.GetValueOrDefault(key)) is { } value ? units.Stress(value) : null;
        var weight = EtabsText.Number(keys.GetValueOrDefault("WEIGHTPERVOLUME")) is { } w ? units.UnitWeight(w)
            // a mass density is force·s²/length⁴; × g [m/s²] in kN, m gives the unit weight
            : EtabsText.Number(keys.GetValueOrDefault("MASSPERVOLUME")) is { } mass ? mass * units.KiloNewtons / Math.Pow(units.Metres, 4) * MaterialProperty.Gravity
            : 0;
        return new(name, EtabsText.MaterialKind(keys.GetValueOrDefault("TYPE")), keys.GetValueOrDefault("GRADE", ""), weight,
            Stress("E") ?? 0, EtabsText.Number(keys.GetValueOrDefault("U")) ?? 0, EtabsText.Number(keys.GetValueOrDefault("A")) ?? 0,
            Stress("FC"), Stress("FY"), Stress("FU"), Stress("FYE"), Stress("FUE"));
    }

    private static EtabsFrameSection Section(string name, Dictionary<string, string> keys, Dictionary<string, string>? concrete,
        Dictionary<string, string>? sdHeader, List<Dictionary<string, string>>? sdShapes, EtabsUnits units, Action<string> note)
    {
        double L(string key) => EtabsText.Number(keys.GetValueOrDefault(key)) is { } value ? units.Length(value) : 0;
        var shapeName = keys.GetValueOrDefault("SHAPE", "");
        var shape = EtabsText.Shape(shapeName);
        var modifiers = ModifierKeys.Select(key => EtabsText.Number(keys.GetValueOrDefault(key)) ?? 1).ToArray();
        EtabsColumnRebar? rebar = null;
        if (concrete is not null && concrete.GetValueOrDefault("TYPE", "Column").Equals("Column", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = concrete.GetValueOrDefault("PATTERN", "");
            var parts = pattern.Split('-');
            if (parts.Length == 3 && parts[0].Equals("R", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[1], out var n2) && int.TryParse(parts[2], out var n3))
            {
                double Diameter(string key) => EtabsText.Number(concrete.GetValueOrDefault(key)) is { } area ? Math.Sqrt(4 * units.Area(area) / Math.PI) : 0;
                rebar = new(concrete.GetValueOrDefault("LONGBARMATERIAL", ""), concrete.GetValueOrDefault("CONFINEBARMATERIAL", ""),
                    EtabsText.Number(concrete.GetValueOrDefault("COVER")) is { } cover ? units.Length(cover) : 0, Diameter("LONGBARAREA"), n3, n2,
                    Diameter("CONFINEBARAREA"), EtabsText.Number(concrete.GetValueOrDefault("CONFINEBARSPACING")) is { } spacing ? units.Length(spacing) : 0);
            }
            else if (pattern.Length > 0) note($"Column reinforcement pattern {pattern} of {name} is not read (rectangular R-n2-n3 patterns are).");
        }
        var shapes = (sdShapes ?? []).Select(item => SdShape(name, item, units)).OfType<EtabsSdShape>().ToArray();
        if (sdShapes is not null && shapes.Length < sdShapes.Count)
        {
            note($"Section Designer section {name} has shapes this import does not read; its properties cannot be worked out from the .e2k file.");
            shape = EtabsShape.Other;
        }
        return new EtabsFrameSection
        {
            Name = name,
            ShapeName = shapeName,
            Shape = shape,
            Material = keys.GetValueOrDefault("MATERIAL", ""),
            Depth = L("D"),
            Width = L("B"),
            FlangeThickness = L("TF"),
            WebThickness = L("TW"),
            BottomWidth = keys.ContainsKey("BB") ? L("BB") : L("B"),
            BottomFlangeThickness = keys.ContainsKey("TFB") ? L("TFB") : L("TF"),
            Thickness = L("T"),
            FillMaterial = keys.GetValueOrDefault("FILLMATERIAL", ""),
            EncasementMaterial = keys.GetValueOrDefault("ENCASEMENTMATERIAL", ""),
            EmbeddedSection = keys.GetValueOrDefault("EMBEDDEDSECTION", ""),
            Rebar = rebar,
            SdShapes = shapes,
            Modifiers = modifiers,
        };
    }

    private static EtabsSdShape? SdShape(string section, Dictionary<string, string> keys, EtabsUnits units)
    {
        double L(string key) => EtabsText.Number(keys.GetValueOrDefault(key)) is { } value ? units.Length(value) : 0;
        var material = keys.GetValueOrDefault("MATERIAL");
        if (material is null || material.Equals("Opening", StringComparison.OrdinalIgnoreCase)) material = null;
        var order = int.TryParse(keys.GetValueOrDefault("SHAPE"), out var z) ? z : 0;
        var rotation = EtabsText.Number(keys.GetValueOrDefault("ROT")) ?? 0;
        return keys.GetValueOrDefault("SHAPETYPE", "").ToUpperInvariant() switch
        {
            "CONCRETE RECTANGULAR" or "CONCRETE RECTANGLE" or "STEEL PLATE" or "PLATE" =>
                new(order, EtabsSdShapeKind.Rectangle, material, L("XC"), L("YC"), L("D"), L("B"), 0, rotation),
            "CONCRETE CIRCLE" or "STEEL ROD" or "CIRCLE" => new(order, EtabsSdShapeKind.Circle, material, L("XC"), L("YC"), L("D"), L("D"), 0, rotation),
            "STEEL PIPE" or "PIPE" => new(order, EtabsSdShapeKind.Pipe, material, L("XC"), L("YC"), L("D"), L("D"), L("T"), rotation),
            _ => null,
        };
    }

    private static string Kind(string connectivity) => connectivity switch { "COLUMN" => "Column", "BEAM" => "Beam", "BRACE" => "Brace", _ => connectivity };

    private static string Title(string section) =>
        section.Length == 0 ? "Objects" : char.ToUpperInvariant(section[0]) + section[1..].ToLowerInvariant();

    private static void Merge(Dictionary<string, Dictionary<string, string>> target, string name, Dictionary<string, string> keys)
    {
        if (!target.TryGetValue(name, out var existing)) target[name] = existing = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in keys) existing[key] = value;
    }

    private readonly record struct Token(string Text, bool Quoted);

    /// <summary>The words of a record: a quoted name keeps its spaces.</summary>
    private static List<Token> Tokens(string line)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            if (line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0) end = line.Length;
                tokens.Add(new(line[(i + 1)..end], true));
                i = end + 1;
                continue;
            }
            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            tokens.Add(new(line[start..i], false));
        }
        return tokens;
    }

    /// <summary>KEY value pairs from <paramref name="start"/> on: a bare word is a key, the next word its value; a
    /// second value (JOINT "8" "EL33") is skipped.</summary>
    private static Dictionary<string, string> Pairs(List<Token> tokens, int start)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = start;
        while (i < tokens.Count)
        {
            if (!tokens[i].Quoted && i + 1 < tokens.Count && tokens[i].Text.Length > 0 && char.IsLetter(tokens[i].Text[0]))
            {
                pairs[tokens[i].Text] = tokens[i + 1].Text;
                i += 2;
            }
            else i++;
        }
        return pairs;
    }
}
