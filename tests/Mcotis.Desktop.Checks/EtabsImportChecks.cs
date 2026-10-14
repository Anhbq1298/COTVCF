using System.IO;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Presentation;

/// <summary>
/// File → Import From ETABS, offline, on the CoT 120 m composite arch deck (<c>tests/etabs-import</c>): the .e2k file
/// ETABS 22.2 wrote (<c>CoT120.e2k</c>) and the database tables ETABS 23.2 exported through its API from the same model
/// after two braces (D1, D3) were added to it (<c>CoT120-live.tables.txt</c>, kip-in). The two readers must agree on
/// everything they share; the Section Designer, CFT and encased sections must give ETABS's own stiffness; the imported
/// model must be ready to run, and every case must be in equilibrium with the loads ETABS defines. ETABS and SAP2000 are
/// never called. <c>--etabs-live</c> (opt-in) imports from the ETABS that is running, read-only.
/// </summary>
internal static class EtabsImportChecks
{
    public const string Folder = "tests/etabs-import";
    private static string E2k => Path.Combine(Folder, "CoT120.e2k");
    private static string Tables => Path.Combine(Folder, "CoT120-live.tables.txt");

    public static void Run()
    {
        var e2k = EtabsE2kReader.Read(File.ReadAllText(E2k), E2k);
        var tables = EtabsTableReader.Read(File.ReadAllText(Tables), Tables);
        CheckReaders(e2k, tables);
        CheckSections(e2k, tables);
        CheckRefusals();
        CheckImport();
        CheckLoadTas();
        CheckTiedArch();
        TasOptimizationChecks.Run();
        CheckLiveImportWithStandIn();
    }

    // File → Load TAS…: the bundled model is this .e2k, and loading it gives the model Import From ETABS gives, ready to run
    private static void CheckLoadTas()
    {
        using (var resource = typeof(MainViewModel).Assembly.GetManifestResourceStream(MainViewModel.TasResource))
        using (var reader = new StreamReader(resource!))
            Check(reader.ReadToEnd().ReplaceLineEndings() == File.ReadAllText(E2k).ReplaceLineEndings(), "the TAS model bundled with the app is tests/etabs-import/CoT120.e2k (line endings aside, which git sets per checkout)");
        var imported = NewViewModel(out _);
        imported.ImportFromE2kFile(E2k);
        var vm = NewViewModel(out _);
        var summary = vm.LoadTasModel();
        var a = vm.State.Geometry;
        var b = imported.State.Geometry;
        Check(summary.ReadyToRun && vm.CanRunFemAnalysis && vm.IsOfflineModel && vm.CanEditVcfGeometry && vm.NeedsVcfModelReplacement,
            "Load TAS leaves an offline model ready to run; VCF System recognises that the TAS arch must be replaced rather than edited as VCF — " + string.Join(" | ", summary.Lines));
        Check(vm.OfflineModelName == "TAS - Tied Arch System" && vm.Message.StartsWith("Loaded TAS - Tied Arch System", StringComparison.Ordinal),
            $"the model is named TAS - Tied Arch System in the title bar ({vm.OfflineModelName}; {vm.Message})");
        var nativeTas = vm.NativeDocumentSession.Document;
        Check(nativeTas.Points.Count == a.Points.Count && nativeTas.Frames.Count == a.Members.Count(member => member.Kind == MemberKind.Frame) &&
              nativeTas.LoadCases.Count == vm.FemCaseRows.Count &&
              nativeTas.Points.Values.All(point => point.Origin is { Kind: Mcotis.Core.OriginKind.Generated, GenerationKey: not null }),
            "Load TAS initializes the central native model as generated 3D geometry with its load cases; TAS remains a template, not a second model type");

        var replacement = NewViewModel(out var replacementSap);
        replacement.LoadTasModel();
        replacement.ReplaceCurrentWithVcfModel();
        Check(replacement.IsDefaultModel && replacement.CanEditVcfGeometry && !replacement.NeedsVcfModelReplacement &&
              VcfGeometry.CanAnalyze(replacement.State.Geometry, replacement.State.Truss) && replacementSap.Writes == 0,
            "after confirmation, VCF System replaces all TAS working geometry and inputs with the built-in VCF model, ready to edit, without writing SAP2000");

        // the Optimization menu offers the optimization of the system that is open, and is told when the system changes
        var switching = NewViewModel(out _);
        var told = new List<(string Name, bool Value)>();
        switching.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CanOptimizeVcf)) told.Add((e.PropertyName, switching.CanOptimizeVcf));
            if (e.PropertyName == nameof(MainViewModel.CanOptimizeTas)) told.Add((e.PropertyName, switching.CanOptimizeTas));
        };
        bool Last(string name, bool value) => told.LastOrDefault(item => item.Name == name) is { Name: not null } last && last.Value == value;
        switching.LoadTasModel();
        bool onTas = switching.CanOptimizeTas && !switching.CanOptimizeVcf && Last(nameof(MainViewModel.CanOptimizeTas), true) && Last(nameof(MainViewModel.CanOptimizeVcf), false);
        bool noCables = !switching.HasCableMembers;
        switching.ReplaceCurrentWithVcfModel();
        bool onVcf = switching.CanOptimizeVcf && !switching.CanOptimizeTas && Last(nameof(MainViewModel.CanOptimizeVcf), true) && Last(nameof(MainViewModel.CanOptimizeTas), false);
        bool cables = switching.HasCableMembers;
        switching.ApplyTiedArchSystem(TiedArchParameters.Default with { ElevatedCable = true });
        Check(noCables && cables && switching.HasCableMembers && switching.CanOptimizeTas,
            "the model has cables — what the cable force fitting is shown for — on the VCF system and on the tied arch system with its elevated cable, not on the tied arch system without it");
        switching.ApplyTiedArchSystem(TiedArchParameters.Default with { ArchRise = 32 });
        bool edited = switching.CanOptimizeTas && !switching.CanOptimizeVcf && Last(nameof(MainViewModel.CanOptimizeTas), true);
        switching.ImportFromE2kFile(E2k);
        bool imported3 = !switching.CanOptimizeTas && !switching.CanOptimizeVcf && Last(nameof(MainViewModel.CanOptimizeTas), false);
        switching.InitializeBlankMcotisConfiguration();
        Check(onTas && onVcf && edited && imported3 && !switching.CanOptimizeTas && !switching.CanOptimizeVcf,
            "the Optimization menu offers Optimization TAS on the tied arch system (loaded, or applied from Edit → Tied Arch System) and Optimization VCF on a VCF system — " +
            "never both — and neither on a model that is another system (an imported ETABS model) or blank; the menu is told each time the system changes");
        Check(vm.FrameSections.Select(section => (section.Name, section.Shape, section.Values())).SequenceEqual(
                  imported.FrameSections.Select(section => (section.Name, section.Shape, section.Values()))) &&
              vm.Materials.Select(material => (material.Name, material.ElasticModulus)).SequenceEqual(imported.Materials.Select(material => (material.Name, material.ElasticModulus))) &&
              vm.FemCaseRows.Select(row => (row.Name, row.Run)).SequenceEqual(imported.FemCaseRows.Select(row => (row.Name, row.Run))) &&
              vm.FemCombinationRows.Select(row => row.Name).SequenceEqual(imported.FemCombinationRows.Select(row => row.Name)) &&
              a.Points.All(point => b.Points.TryGetValue(point.Key, out var other) && point.Value.Position == other.Position),
            "Load TAS keeps the materials, sections, load cases and combinations Import From ETABS gives, and its joints are ETABS joints");

        // each straight member is one frame: no joint is left between two frames of one straight line
        var ends = a.Members.SelectMany(member => new[] { member.Start, member.End }).GroupBy(name => name).Where(group => group.Count() == 2).Select(group => group.Key);
        var straight = ends.Where(joint =>
        {
            var pair = a.Members.Where(member => member.Start == joint || member.End == joint).ToArray();
            var p = a.Points[joint].Position;
            var (q, r) = (a.Points[pair[0].Start == joint ? pair[0].End : pair[0].Start].Position, a.Points[pair[1].Start == joint ? pair[1].End : pair[1].Start].Position);
            var cross = (q.X - p.X) * (r.Z - p.Z) - (q.Z - p.Z) * (r.X - p.X);
            return Math.Abs(cross) < 1e-6 * Math.Max(1, Math.Abs((q.X - p.X) * (r.X - p.X) + (q.Z - p.Z) * (r.Z - p.Z)));
        }).ToArray();
        var coll = a.Members.Where(member => member.Name.StartsWith("COLL", StringComparison.Ordinal)).Select(member => $"{member.Start}→{member.End}").ToArray();
        Check(straight.Length == 0 && a.Members.Count < b.Members.Count && a.Points.Count < b.Points.Count &&
              coll.SequenceEqual(["1@Base→1@EL16.667", "1@EL16.667→1@EL33"]) &&
              a.Members.Single(member => member.Name == "STR10-1") is { Start: "4@EL16.667", End: "4@EL33" } &&
              vm.FemSectionProperties.Single(property => property.MemberName == "STR10-1") is { ReleaseStartRotation: true, ReleaseEndRotation: true },
            $"each straight member is one frame ({b.Points.Count} → {a.Points.Count} joints, {b.Members.Count} → {a.Members.Count} frames; left column {string.Join(", ", coll)}; " +
            $"the strut at 10 m runs from the arch to the deck with its two end releases){(straight.Length == 0 ? "" : "; still split at " + string.Join(", ", straight))}");

        // the viewer's arch height: from the supports to the crown of the arch rib
        string? SectionOf(MainViewModel model, string member) => model.FemSectionProperties.FirstOrDefault(property => property.MemberName == member)?.SectionName;
        var rise = ArchRise.Find(a, vm.FemPinSupportPoints, member => SectionOf(vm, member));
        Check(rise is { Support: "1@Base", Crown: "8@EL30", Rise: var h } && Near(h, 30, 1e-6),
            $"the viewer dimensions the arch from its supports to its crown (H = {rise?.Rise:0.###} m at {rise?.Crown})");
        var portal = new GeometryModel(new Dictionary<string, StructuralPoint>
        {
            ["A"] = new("A", new(0, 0, 0)), ["B"] = new("B", new(10, 0, 0)), ["C"] = new("C", new(0, 0, 5)), ["D"] = new("D", new(10, 0, 5)),
        }, [new("CA", "A", "C", MemberKind.Frame), new("CB", "B", "D", MemberKind.Frame), new("BM", "C", "D", MemberKind.Frame),
            new("D1", "A", "D", MemberKind.Frame), new("D2", "B", "C", MemberKind.Frame)]);
        Check(ArchRise.Find(portal, ["A", "B"], member => member.StartsWith('D') ? "brace" : "column") is null,
            "a braced portal has no arch: its braces leave the supports inclined but do not join them in one chain");

        // joining the frames changes nothing in the results
        var joinedRuns = FemModelFileChecks.Analyze(vm);
        var splitRuns = FemModelFileChecks.Analyze(imported);
        foreach (var name in new[] { "DEAD", "DEAD_above", "LL_left", "ULS1 1.35G+1.5Q full" })
        {
            var (joined, split) = (joinedRuns[0].ResultFor(name), splitRuns[0].ResultFor(name));
            var differences = new[] { "8@EL33", "3@EL33", "13@EL33", "5@EL22.5" }.Select(joint =>
                Math.Abs(joined.Joints.Single(item => item.PointName == joint).UZM - split.Joints.Single(item => item.PointName == joint).UZM)).Max();
            var reactions = Math.Abs(joined.SupportReactions.Sum(item => item.ReactionZKN) - split.SupportReactions.Sum(item => item.ReactionZKN));
            Check(differences < 1e-6 && reactions < 1e-3,
                $"'{name}': the joined model deflects and reacts as the ETABS model split at every story (largest UZ difference {differences * 1000:0.######} mm)");
        }
    }

    // Edit → Tied Arch System: with the TAS defaults it builds the TAS model ETABS wrote; other inputs build the system they describe
    private static void CheckTiedArch()
    {
        var d = TiedArchParameters.Default;
        Check(d.Span == 120 && d.SlsPointLoad == 18_000 && Near(d.UlsPointLoad, 25_200, 1e-9) && d.Problems().Count == 0,
            $"the TAS defaults: L = 12 × 10 = {d.Span} m, SLS (7 + 3 + 5) × 10 × 12 × 10 = {d.SlsPointLoad:N0} kN, ULS (1.35 × 10 + 1.5 × 5) × 1200 = {d.UlsPointLoad:N0} kN");

        var tas = NewViewModel(out _);
        tas.LoadTasModel();
        var built = NewViewModel(out _);
        var summary = built.ApplyTiedArchSystem(d);
        var (a, b) = (built.State.Geometry, tas.State.Geometry);
        Check(summary.ReadyToRun && built.TiedArchParameters == d && built.OfflineModelName == "TAS - Tied Arch System" &&
              a.Points.Count == b.Points.Count && a.Points.All(point => b.Points.TryGetValue(point.Key, out var other) &&
                  Math.Abs(point.Value.Position.X - other.Position.X) < 1e-6 && Math.Abs(point.Value.Position.Z - other.Position.Z) < 1e-6) &&
              a.Members.Count == b.Members.Count,
            $"with the defaults, Edit → Tied Arch System builds the TAS model's {a.Points.Count} joints (same names and places) and {a.Members.Count} frames, ready to run");
        var e2kLoads = EtabsE2kReader.Read(File.ReadAllText(E2k), E2k).JointLoads.ToDictionary(load => (load.Joint, load.Pattern.ToUpperInvariant()), load => load.Fz);
        var builtLoads = TiedArchSystem.Build(EtabsE2kReader.Read(File.ReadAllText(E2k), E2k), d).JointLoads.ToDictionary(load => (load.Joint, load.Pattern.ToUpperInvariant()), load => load.Fz);
        Check(builtLoads.Count == e2kLoads.Count && builtLoads.All(load => e2kLoads.TryGetValue(load.Key, out var fz) && Near(fz, load.Value, 1e-9)),
            $"… and the 40 deck joint loads of the ETABS model (DEAD_above 12,000 / LL 6,000 kN inside, half at the ends and for LL at mid-span): {builtLoads.Count}");
        var (builtRuns, tasRuns) = (FemModelFileChecks.Analyze(built), FemModelFileChecks.Analyze(tas));
        foreach (var name in new[] { "DEAD", "DEAD_above", "LL_left", "ULS1 1.35G+1.5Q full" })
        {
            var (x, y) = (builtRuns[0].ResultFor(name), tasRuns[0].ResultFor(name));
            var worst = x.Joints.Max(joint => Math.Abs(joint.UZM - y.Joints.Single(other => other.PointName == joint.PointName).UZM));
            Check(worst < 1e-6, $"'{name}': the built system deflects as the TAS model at every joint (largest difference {worst * 1000:0.######} mm)");
        }

        // another system: the arch on the parabola through the supports and the crown, the deck loads from the formula
        var p = new TiedArchParameters(25, 30, 10, 12, 10, 10, 5, 7.5, 6);
        var vm = NewViewModel(out _);
        Check(vm.ApplyTiedArchSystem(p).ReadyToRun && vm.TiedArchParameters == p, "Edit → Tied Arch System builds H1 25, H2 30, 10 bays of 12 m ready to run");
        var g = vm.State.Geometry;
        var left = g.Points.Values.Min(point => point.Position.X);
        var archJoints = g.Points.Values.Where(point => point.Position.Z > 0 && point.Position.Z < 30 &&
            g.Members.Any(member => member.Name.StartsWith("ARCH", StringComparison.Ordinal) && (member.Start == point.Name || member.End == point.Name))).ToArray();
        Check(archJoints.Length == 9 && archJoints.All(point => Near(point.Position.Z, p.ArchHeightAt(point.Position.X - left), 1e-9)) &&
              Near(g.Points.Values.Max(point => point.Position.X) - left, 120, 1e-9) && g.Points.Values.Count(point => Near(point.Position.Z, 30, 1e-9)) == 11,
            $"the arch joints lie on z = 4·H1·x·(L − x)/L² (9 inner arch joints), the span is 120 m and the deck has 11 joints at H2");
        var rise = ArchRise.Find(g, vm.FemPinSupportPoints, member => vm.FemSectionProperties.FirstOrDefault(property => property.MemberName == member)?.SectionName);
        Check(rise is { Rise: var h } && Near(h, 25, 1e-9), $"the viewer dimensions H = {rise?.Rise:0.###} m to the crown {rise?.Crown}");
        var run = FemModelFileChecks.Analyze(vm)[0];
        foreach (var (name, total) in new[] { ("DEAD_above", 10 * p.DeadPointLoad), ("LL_above", 10 * p.LivePointLoad), ("LL_left", 5 * p.LivePointLoad),
                     ("SLS1 G+Q full", run.ResultFor("DEAD").SupportReactions.Sum(item => item.ReactionZKN) + 10 * p.SlsPointLoad) })
        {
            var reactions = run.ResultFor(name).SupportReactions.Sum(item => item.ReactionZKN);
            Check(Near(reactions, total, 1e-6 * total), $"'{name}': the supports carry {total:N0} kN = bays × the point load ({reactions:N1} kN)");
        }

        // what the form refuses
        Check(d with { Bays = 11 } is { } odd && odd.Problems().Any(problem => problem.Contains("even", StringComparison.Ordinal)) &&
              (d with { DeckHeight = 30 }).Problems().Any(problem => problem.StartsWith("H2", StringComparison.Ordinal)) &&
              (d with { SelfWeight = 7.5 }).Problems().Any(problem => problem.StartsWith("SW", StringComparison.Ordinal)) &&
              (d with { Floors = 11 }).Problems().Count == 1,
            "odd bays, H2 not above H1, SW off its 1 kPa steps and more than 10 floors are refused");
    }

    private static void CheckReaders(EtabsModel e2k, EtabsModel tables)
    {
        Check(e2k.Frames.Count == 143 && e2k.Materials.Count == 3 && e2k.Sections.Count == 7 && e2k.Patterns.Count == 6 &&
              e2k.JointLoads.Count == 40 && e2k.Cases.Count == 10 && e2k.Combinations.Count == 8,
            $"the .e2k reader finds 143 frames, 3 materials, 7 sections, 6 patterns, 40 joint loads, 10 cases, 8 combinations " +
            $"({e2k.Frames.Count}, {e2k.Materials.Count}, {e2k.Sections.Count}, {e2k.Patterns.Count}, {e2k.JointLoads.Count}, {e2k.Cases.Count}, {e2k.Combinations.Count})");

        // a LINE joins its I point a number of stories below the story its LINEASSIGN names (worked out by hand from the file)
        EtabsJoint Joint(EtabsModel model, string name) => model.Joints.Single(joint => joint.Name == name);
        var arch1 = e2k.Frames.Single(frame => frame.Label == "ARCH1");
        var arch7 = e2k.Frames.Single(frame => frame.Label == "ARCH7");
        var column = e2k.Frames.Single(frame => frame.Label == "COLL-7");
        Check(arch1.StartJoint == "1@Base" && arch1.EndJoint == "3@EL9.167" && arch7.StartJoint == "9@EL29.167" && arch7.EndJoint == "8@EL30" &&
              column.StartJoint == "1@EL30" && column.EndJoint == "1@EL33" &&
              Near(Joint(e2k, "3@EL9.167").Z, 9.166666667) && Near(Joint(e2k, "9@EL29.167").Z, 29.1666666663) && Near(Joint(e2k, "1@EL33").Z, 33) &&
              Near(Joint(e2k, "9@EL29.167").X, 60) && Near(Joint(e2k, "14@EL22.5").X, -6.428571),
            "frames join the right joints at the right story elevations (ARCH1 1@Base → 3@EL9.167, ARCH7 9@EL29.167 → 8@EL30, COLL-7 1@EL30 → 1@EL33)");
        Check(e2k.Frames.Single(frame => frame.Label == "STR50").Releases.SetEquals(["M2I", "M3I", "M2J", "M3J"]) &&
              e2k.Frames.Single(frame => frame.Label == "DECK1").Releases.Count == 0,
            "end releases are read per frame");

        // the live tables (kip, in) and the .e2k (kN, m) describe the same model, plus the braces D1 and D3 added in ETABS
        var extra = tables.Frames.Select(frame => frame.Key).Except(e2k.Frames.Select(frame => frame.Key)).OrderBy(key => key).ToArray();
        Check(tables.Frames.Count == 145 && extra.SequenceEqual(["D1@EL33", "D3@EL33"]) &&
              !e2k.Frames.Select(frame => frame.Key).Except(tables.Frames.Select(frame => frame.Key)).Any(),
            $"the table reader finds the .e2k's 143 frames and the two braces added since (D1, D3): {string.Join(", ", extra)}");
        var byKey = tables.Frames.ToDictionary(frame => frame.Key);
        var tableJoints = tables.Joints.ToDictionary(joint => joint.Name);
        var mismatch = e2k.Frames.FirstOrDefault(frame =>
        {
            var other = byKey[frame.Key];
            var (a, b) = (Joint(e2k, frame.StartJoint), tableJoints[other.StartJoint]);
            var (c, d) = (Joint(e2k, frame.EndJoint), tableJoints[other.EndJoint]);
            return other.StartJoint != frame.StartJoint || other.EndJoint != frame.EndJoint || other.Section != frame.Section ||
                   !other.Releases.SetEquals(frame.Releases) || other.AngleDeg != frame.AngleDeg ||
                   !Near(a.X, b.X, 1e-6) || !Near(a.Z, b.Z, 1e-6) || !Near(c.X, d.X, 1e-6) || !Near(c.Z, d.Z, 1e-6);
        });
        Check(mismatch is null, $"every frame has the same joints, coordinates (kip-in → kN-m), section and releases in both readers{(mismatch is null ? "" : ": " + mismatch.Key)}");
        var materialDifferences = e2k.Materials.SelectMany(material =>
        {
            var other = tables.Materials.Single(item => item.Name == material.Name);
            var fields = new (string Field, double E2k, double Tables, double Tolerance)[]
            {
                ("E", material.ElasticModulusKNPerM2, other.ElasticModulusKNPerM2, 1e-9 * material.ElasticModulusKNPerM2),
                ("unit weight", material.UnitWeightKNPerM3, other.UnitWeightKNPerM3, 1e-6),
                ("thermal coefficient", material.ThermalCoefficient, other.ThermalCoefficient, 1e-12),
                ("Fy", material.Fy ?? 0, other.Fy ?? 0, 1e-3), ("f'c", material.Fc ?? 0, other.Fc ?? 0, 1e-3),
            };
            return fields.Where(field => !Near(field.E2k, field.Tables, field.Tolerance)).Select(field => $"{material.Name} {field.Field} {field.E2k} vs {field.Tables}")
                .Concat(other.Kind == material.Kind ? [] : [$"{material.Name} type {material.Kind} vs {other.Kind}"]);
        }).ToArray();
        Check(materialDifferences.Length == 0,
            "materials agree: type, E, unit weight, thermal coefficient (°F → °C), f'c and Fy" + string.Concat(materialDifferences.Select(item => " | " + item)));
        var e2kLoads = e2k.JointLoads.ToDictionary(load => (load.Joint, load.Pattern), load => load.Fz);
        Check(tables.JointLoads.Count == 40 && tables.JointLoads.All(load => e2kLoads.TryGetValue((load.Joint, load.Pattern), out var fz) && Near(load.Fz, fz, 1e-6)),
            "the 40 joint loads agree (FZ −6,000 / −12,000 kN … from kip)");
        Check(e2k.Cases.Select(item => (item.Name, item.Type, Loads: string.Join(",", item.Loads))).SequenceEqual(
                  tables.Cases.Select(item => (item.Name, item.Type, Loads: string.Join(",", item.Loads)))) &&
              e2k.Combinations.OrderBy(item => item.Name).Select(item => (item.Name, Items: string.Join(",", item.Items))).SequenceEqual(
                  tables.Combinations.OrderBy(item => item.Name).Select(item => (item.Name, Items: string.Join(",", item.Items)))),
            "load cases (type, pattern loads) and combinations agree");
        var restrained = e2k.Restraints.Where(item => item.Dofs[0] && item.Dofs[2]).Select(item => item.Joint).OrderBy(name => name).ToArray();
        Check(restrained.SequenceEqual(["1@Base", "2@Base"]) &&
              tables.Restraints.Where(item => item.Dofs[0] && item.Dofs[2]).Select(item => item.Joint).OrderBy(name => name).SequenceEqual(restrained),
            "ETABS's supports (UX UY UZ at 1@Base, 2@Base) are read by both readers");
    }

    private static void CheckSections(EtabsModel e2k, EtabsModel tables)
    {
        // Section Designer: the shapes give ETABS's own A, I33, I22 (the table export states them)
        var rib = tables.Sections.Single(section => section.Name == "RSC 3.52x2.64 P");
        var materials = e2k.Materials.ToDictionary(material => material.Name);
        (double, double, double)? Material(string name) => materials.TryGetValue(name, out var item)
            ? (item.ElasticModulusKNPerM2, item.UnitWeightKNPerM3, item.UnitWeightKNPerM3 / MaterialProperty.Gravity) : null;
        var designed = SectionDesignerSection.Compute(e2k.Sections.Single(section => section.Name == rib.Name).SdShapes, 44_000_000, Material);
        var fromTables = SectionDesignerSection.Compute(rib.SdShapes, 44_000_000, Material);
        var reported = rib.Reported!;
        Check(designed.Exact && Rel(designed.Area, reported.Area) < 1e-4 && Rel(designed.I33, reported.I33) < 1e-4 && Rel(designed.I22, reported.I22) < 1e-4 &&
              Rel(fromTables.Area, designed.Area) < 1e-9 && Rel(fromTables.I33, designed.I33) < 1e-9,
            $"the arch rib's Section Designer shapes give ETABS's A {reported.Area:0.00000} m², I33 {reported.I33:0.00000} m⁴, I22 {reported.I22:0.00000} m⁴ " +
            $"(computed {designed.Area:0.00000}, {designed.I33:0.00000}, {designed.I22:0.00000}; from both readers)");
        // its true self-weight: concrete outline − opening − four steel rings (32-gons, as ETABS draws circles), each with its own unit weight
        static double Polygon(double radius) => 16 * radius * radius * Math.Sin(2 * Math.PI / 32);
        var ring = Polygon(0.275) - Polygon(0.24);
        var weight = (3.52 * 2.64 - 2.09 * 1.87 - 4 * ring) * 24.992619 + 4 * ring * 76.97286;
        Check(Rel(designed.WeightPerLengthKNPerM, weight) < 1e-9,
            $"its self-weight is {weight:0.###} kN/m (concrete and steel each with its unit weight), not base unit weight × transformed area ({24.992619 * designed.Area:0.###} kN/m)");

        // CFT and steel encased in Mcotis's composite form: the axial and bending stiffness ETABS states (the encased
        // column adds its bars, which ETABS's section properties leave out)
        var vm = NewViewModel(out _);
        vm.ImportFromE2kFile(E2k);
        var esteel = 210_000_000.0;
        foreach (var name in new[] { "CFT Brace 700x700x25", "CFT Deck 700x400x20", "CFT Strut 650x650x25", "CFT Strut-E 850x850x35" })
        {
            var mcotis = vm.FrameSections.Single(section => section.Name == name);
            var etabs = tables.Sections.Single(section => section.Name == name).Reported!;
            var ecm = vm.Materials.Single(material => material.Name == mcotis.Material).ElasticModulus;
            Check(mcotis.Shape == FrameShape.FilledTube && mcotis.SteelMaterial == "S690" && mcotis.Material == "C90/105" &&
                  Rel(ecm * mcotis.Given!.Area, esteel * etabs.Area) < 1e-6 && Rel(ecm * mcotis.Given.I33, esteel * etabs.I33) < 1e-6 &&
                  Rel(ecm * mcotis.Given.I22, esteel * etabs.I22) < 1e-6,
                $"{name} is a CFT section with ETABS's EA and EI (EA {ecm * mcotis.Given!.Area / 1e6:0.###} GN)");
        }
        var encased = vm.FrameSections.Single(section => section.Name == "SRC MegaCol 1000x1200");
        var etabsColumn = tables.Sections.Single(section => section.Name == encased.Name).Reported!;
        var input = encased.Composite!;
        var bars = 2 * input.BarsAlongWidth + 2 * input.BarsAlongDepth;
        var barArea = bars * Math.PI / 4 * Math.Pow(input.BarDiameter / 1000, 2);
        var ec = 44_000_000.0;
        Check(encased.Shape == FrameShape.Encased && input.BarsAlongWidth == 7 && input.BarsAlongDepth == 6 && Near(input.BarDiameter, 32, 0.01) &&
              Near(input.SteelDepth, 700) && Near(input.Width, 1200) && Near(input.Depth, 1000) && Near(input.Cover, 75) && Near(input.LinkSpacing, 150) &&
              Rel(ec * encased.Given!.Area, esteel * etabsColumn.Area + barArea * (200_000_000 - ec)) < 1e-6,
            $"the mega column is a steel encased section (R-8-7 → 7 bars per 1200 face, 8 per 1000 face, Ø32) with ETABS's EA plus its {bars} bars");

        // standard steel shapes keep their dimensions and give ETABS's values
        var plate = vm.FrameSections.Single(section => section.Name == "SRC MegaCol 1000x1200-I");
        var plateEtabs = tables.Sections.Single(section => section.Name == plate.Name).Reported!;
        Check(plate.Shape == FrameShape.I && Rel(plate.Values().Area, plateEtabs.Area) < 1e-9 && Rel(plate.Values().I33, plateEtabs.I33) < 1e-6 &&
              Rel(plate.Values().I22, plateEtabs.I22) < 1e-6, "the steel I section keeps its plates and ETABS's A, I33, I22");
    }

    private static void CheckRefusals()
    {
        var text = File.ReadAllText(E2k);
        var vm = NewViewModel(out _);
        vm.ImportFromE2kFile(E2k);
        var before = vm.State.Geometry;
        void Refused(string variant, string expected, string name)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mcotis-etabs-{Guid.NewGuid():N}.e2k");
            File.WriteAllText(path, variant);
            try
            {
                vm.ImportFromE2kFile(path);
                Check(false, name);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains(expected, StringComparison.Ordinal))
            {
                Check(ReferenceEquals(vm.State.Geometry, before) && vm.HasWorkingModel, name);
            }
            finally { File.Delete(path); }
        }
        Refused(text.Replace("POINT \"14\"  -6.428571 0", "POINT \"14\"  -6.428571 2"), "not a plane frame",
            "a model whose joints are not at one Y is refused, and the model held is kept");
        Refused(text.Replace("RELEASE \"M2I M3I M2J M3J\"", "RELEASE \"PI M3I\""), "axial end release",
            "a frame with an axial end release is refused by name, before anything changes");
    }

    private static void CheckImport()
    {
        var vm = NewViewModel(out var sap);
        var summary = vm.ImportFromE2kFile(E2k);
        var state = vm.State;
        Check(summary.ReadyToRun && vm.CanRunFemAnalysis && vm.IsOfflineModel && !state.IsSapConnected,
            "Import From ETABS (.e2k) leaves an offline model ready to run — " + string.Join(" | ", summary.Lines));
        Check(state.Geometry.Members.Count == 143 && vm.FemSectionProperties.Count == 143 && vm.Materials.Count == 3 && vm.FrameSections.Count == 7 &&
              state.Geometry.Points.Values.All(point => point.Position.Y == 0),
            $"it brings in every frame with its properties, the libraries and the joints ({state.Geometry.Points.Count}) in the XZ plane");
        var str50 = vm.FemSectionProperties.Single(property => property.MemberName == "STR50");
        var deck = vm.FemSectionProperties.Single(property => property.MemberName == "DECK1");
        Check(str50.ReleaseStartRotation && str50.ReleaseEndRotation && !deck.ReleaseStartRotation && !deck.ReleaseEndRotation,
            "in-plane (M3) end releases reach the FEM members; out-of-plane (M2) ones do not matter in 2D");
        var arch = vm.FemSectionProperties.Single(property => property.MemberName == "ARCH1");
        var rib = SectionDesignerSection.Compute(EtabsE2kReader.Read(File.ReadAllText(E2k), E2k).Sections.Single(section => section.Name == "RSC 3.52x2.64 P").SdShapes,
            44_000_000, name => name == "S690" ? (210_000_000, 76.97286, 76.97286 / MaterialProperty.Gravity) : (44_000_000, 24.992619, 24.992619 / MaterialProperty.Gravity));
        Check(Rel(arch.WeightPerLengthKNPerM, rib.WeightPerLengthKNPerM) < 1e-9 && Rel(arch.AreaM2, rib.Area) < 1e-9 && arch.ElasticModulusKNPerM2 == 44_000_000,
            $"the arch rib members carry the transformed A with E of C90/105 and the rib's true self-weight ({arch.WeightPerLengthKNPerM:0.##} kN/m)");
        Check(FemStudyService.FindPinSupportPoints(state.Baseline).SequenceEqual(["1@Base", "2@Base"]) && state.Cables.Count == 0 && !state.BaselineCaptured,
            "Mcotis pins the two column bases ETABS restrains; no cables and no truss chords are needed to run");

        // Tab 01 reproduces the deck loads it takes over, point by point
        var tab01 = StudyCompositionService.BuildPointLoads(state, LoadingCandidates.Enumerate(state.LoadDefinitions.ToArray()).First());
        var etabsLoads = EtabsE2kReader.Read(File.ReadAllText(E2k), E2k).JointLoads
            .ToDictionary(load => (load.Joint, load.Pattern.ToUpperInvariant()), load => load.Fz);
        Check(state.AppliedPoints.Count == 13 && state.LoadDefinitions.Select(definition => definition.LoadName).OrderBy(name => name).SequenceEqual(["DEAD_above", "LL_above"]) &&
              tab01.Count == 26 && tab01.All(row => etabsLoads.TryGetValue((row.AppliedPt, row.Case.ToUpperInvariant()), out var fz) && Near(fz, row.FzKn, 1e-6)),
            $"Tab 01 ({string.Join(", ", state.LoadDefinitions.Select(definition => $"{definition.LoadName} {definition.Qz} kPa"))} on the 13 deck joints) reproduces the ETABS joint loads");
        Check(vm.FemCaseRows.Count(row => row.Run) == 9 && vm.FemCaseRows.Single(row => row.Name == "MODAL") is { Run: false, Supported: false } &&
              vm.FemCombinationRows.Count == 8 && vm.FemCombinationRows.All(row => row.Run),
            "the 9 static ETABS cases are set to run (MODAL is not static) and the 8 linear additive combinations are evaluated");
        var native = vm.NativeDocumentSession.Document;
        var expectedRestraints = EtabsImport.Convert(EtabsE2kReader.Read(File.ReadAllText(E2k), E2k)).Restraints;
        Check(native.Points.Count == state.Geometry.Points.Count && native.Frames.Count == state.Geometry.Members.Count &&
              native.LoadCases.Count == vm.FemCaseRows.Count && native.LoadCombinations.Count == vm.FemCombinationRows.Count &&
              native.Points.Values.All(point => point.Origin is { Kind: Mcotis.Core.OriginKind.Imported, SourceProduct: "ETABS" }) &&
              expectedRestraints.All(item => native.RestraintOf(native.Points.Values.Single(point => point.Name == item.Key).Id) == item.Value),
            "ETABS import hands geometry, exact six-DOF restraints, assignments, loads and cases to the central native document before analysis");

        // the run: every case and combination in equilibrium with the loads ETABS defines
        var runs = FemModelFileChecks.Analyze(vm);
        Check(runs.Count == 1 && sap.Reads + sap.Writes == 0, $"the imported model runs FEM without SAP2000 ({runs.Count} run; {vm.FemProgressText})");
        var run = runs[0];
        var selfWeight = vm.FemSectionProperties.Sum(property =>
        {
            var member = state.Geometry.Members.Single(item => item.Name == property.MemberName);
            var (a, b) = (state.Geometry.Points[member.Start].Position, state.Geometry.Points[member.End].Position);
            return property.WeightPerLengthKNPerM * Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
        });
        var loads = new Dictionary<string, double>
        {
            ["DEAD"] = selfWeight, ["DEAD_above"] = 144_000, ["LL_above"] = 72_000, ["LL_left"] = 36_000, ["LL_right"] = 36_000, ["Pretension"] = 0,
            ["NL ULS1 full"] = 1.35 * selfWeight + 1.35 * 144_000 + 1.5 * 72_000,
            ["NL ULS2 left"] = 1.35 * selfWeight + 1.35 * 144_000 + 1.5 * 36_000,
            ["ULS1 1.35G+1.5Q full"] = 1.35 * selfWeight + 1.35 * 144_000 + 1.5 * 72_000,
            ["SLS2 G+Q left"] = selfWeight + 144_000 + 36_000,
            ["DES ULS3 right (P-Delta)"] = 1.35 * selfWeight + 1.35 * 144_000 + 1.5 * 36_000,
        };
        foreach (var (name, total) in loads)
        {
            var result = run.ResultFor(name);
            Check(run.Results!.ContainsKey(name) && Near(result.SupportReactions.Sum(reaction => reaction.ReactionZKN), total, 1e-6 * Math.Max(1, total)) &&
                  Math.Abs(result.SupportReactions.Sum(reaction => reaction.ReactionXKN)) < 1e-6 * Math.Max(1, total),
                $"'{name}': the support reactions balance the {total:N0} kN it applies (ΣRz {result.SupportReactions.Sum(reaction => reaction.ReactionZKN):N1} kN, ΣRx 0)");
        }
        var mid = run.ResultFor("DEAD_above").Joints.Single(joint => joint.PointName == "8@EL33");
        Check(mid.UZM < 0 && run.ResultFor("LL_left").Joints.Single(joint => joint.PointName == "3@EL33").UZM <
              run.ResultFor("LL_left").Joints.Single(joint => joint.PointName == "13@EL33").UZM,
            $"the deck deflects under its load (DEAD_above at mid-span {mid.UZM * 1000:0.#} mm) and the loaded half more under LL_left");

        // the imported model saves as a MCOTiS config and reopens to the same results
        var path = Path.Combine(Path.GetTempPath(), $"mcotis-etabs-{Guid.NewGuid():N}{FemInputDatabase.Extension}");
        try
        {
            vm.UnlockFemModel.Execute(null);
            Check(vm.CanSaveFemModel, "the imported model (no truss chords) can be saved as a MCOTiS config");
            vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var reopened = NewViewModel(out var silent);
            reopened.OpenFemModelFile(path);
            var opened = $"{reopened.Message} · {reopened.FemProgressText}";
            var again = FemModelFileChecks.Analyze(reopened);
            Check(again.Count == 1 && silent.Reads + silent.Writes == 0 &&
                  Near(again[0].ResultFor("DEAD_above").Joints.Single(joint => joint.PointName == "8@EL33").UZM, mid.UZM, 1e-9) &&
                  Near(again[0].ResultFor("DEAD").SupportReactions.Sum(reaction => reaction.ReactionZKN), selfWeight, 1e-6 * selfWeight),
                $"saved and reopened, it runs to the same results ({again.Count} run; opened: {opened})");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>The live path with an ETABS stand-in serving the table export: the app imports the model ETABS has open,
    /// asks ETABS for nothing but the export, and runs it.</summary>
    private static void CheckLiveImportWithStandIn()
    {
        var vm = NewViewModel(out var sap);
        var etabs = new EtabsStandIn(new EtabsModelExport("", "ETABS 23.2.0", File.ReadAllText(Tables)));
        vm.Etabs = etabs;
        var summary = vm.ImportFromEtabsAsync().GetAwaiter().GetResult();
        Check(summary.ReadyToRun && etabs.Exports == 1 && vm.State.Geometry.Members.Count == 145 &&
              vm.State.Geometry.Members.Any(member => member.Name == "D1") && vm.OfflineModelName == "ETABS (Untitled) · ETABS",
            "Import From ETABS reads the running model once (its table export) and is ready to run with the braces added in ETABS — " + string.Join(" | ", summary.Lines));
        var runs = FemModelFileChecks.Analyze(vm);
        Check(runs.Count == 1 && etabs.Exports == 1 && sap.Reads + sap.Writes == 0, "it runs FEM without calling ETABS or SAP2000 again");

        var none = NewViewModel(out _);
        none.Etabs = new EtabsStandIn(null);
        try
        {
            none.ImportFromEtabsAsync().GetAwaiter().GetResult();
            Check(false, "without a running ETABS the import says so");
        }
        catch (EtabsNotRunningException) { Check(!none.IsBusy, "without a running ETABS the import says so (the window then offers an .e2k file)"); }
    }

    /// <summary>File → Import From ETABS on the ETABS that is running now (read-only).</summary>
    public static void RunLive()
    {
        var vm = NewViewModel(out var sap);
        using var etabs = new Mcotis.Desktop.SAP2000.EtabsGateway();
        vm.Etabs = etabs;
        var summary = vm.ImportFromEtabsAsync().GetAwaiter().GetResult();
        foreach (var line in summary.Lines) Console.WriteLine("  " + line);
        Check(summary.ReadyToRun, "the model open in ETABS imports ready to run");
        var runs = FemModelFileChecks.Analyze(vm);
        Check(runs.Count == 1 && sap.Reads + sap.Writes == 0, $"and runs FEM ({vm.FemProgressText})");
    }

    private sealed class EtabsStandIn(EtabsModelExport? export) : IEtabsGateway
    {
        public int Exports { get; private set; }
        public Task<EtabsModelExport> ExportActiveModelAsync()
        {
            if (export is null) throw new EtabsNotRunningException("No running ETABS was found. Open the model in ETABS, then import again.");
            Exports++;
            return Task.FromResult(export);
        }
    }

    internal static MainViewModel NewViewModel(out FemModelFileChecks.SapDouble sap)
    {
        sap = FemModelFileChecks.SapDouble.Create(null!, GeometryModel.Empty, refuseAll: true);
        var state = new ApplicationState();
        var workflow = new WorkflowService(sap.Gateway, state);
        return new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap.Gateway, state, workflow));
    }

    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }

    private static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;
    private static double Rel(double a, double b) => Math.Abs(a - b) / Math.Max(Math.Abs(b), 1e-12);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
