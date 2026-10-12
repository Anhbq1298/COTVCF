using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application.Etabs;

/// <summary>
/// What File → Import From ETABS puts into Mcotis: the geometry (joints label@story, frames by label), the material and
/// frame-section libraries, each frame's section, local axis, in-plane end releases and modifiers (its FEM properties are
/// worked out from the library section by the view model, as for Define → Section Properties), the loading (patterns,
/// joint and frame loads, static cases, linear additive combinations), ETABS's supports and what was left out.
/// <see cref="SectionWeights"/> holds the true weight / mass per metre of general sections whose self-weight is not
/// their base material × area (Section Designer composites).
/// </summary>
public sealed record EtabsImportModel(
    GeometryModel Geometry,
    IReadOnlyList<MaterialProperty> Materials,
    IReadOnlyList<FrameSectionProperty> Sections,
    IReadOnlyList<FemSectionProperties> Members,
    IReadOnlyDictionary<string, SectionDesignerValues> SectionWeights,
    SapFemLoading Loading,
    IReadOnlyList<string> SupportedJoints,
    IReadOnlyList<string> FixedJoints,
    IReadOnlyList<string> Notes);

public static class EtabsImport
{
    private const double PlanarToleranceM = 0.001;
    public const string Source = "ETABS";

    public static EtabsImportModel Convert(EtabsModel model)
    {
        var notes = new List<string>(model.Notes);
        var problems = new List<string>();
        var warnings = new List<string>();
        void Warn(string message) { if (warnings.Count < 200) warnings.Add(message); }

        // ---- geometry: the frames and the joints they connect ----
        var jointsByName = model.Joints.GroupBy(joint => joint.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var labelStories = model.Frames.GroupBy(frame => frame.Label, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var frameNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var frame in model.Frames) frameNames[frame.Key] = labelStories[frame.Label] == 1 ? frame.Label : frame.Key;
        var frames = new List<EtabsFrame>();
        foreach (var frame in model.Frames)
        {
            if (!jointsByName.TryGetValue(frame.StartJoint, out var start) || !jointsByName.TryGetValue(frame.EndJoint, out var end))
                throw new InvalidOperationException($"ETABS frame {frame.Label} at {frame.Story} connects a joint without coordinates.");
            if (Math.Abs(start.X - end.X) + Math.Abs(start.Y - end.Y) + Math.Abs(start.Z - end.Z) < 1e-9)
            {
                notes.Add($"Frame {frameNames[frame.Key]} has zero length and is left out.");
                continue;
            }
            frames.Add(frame);
        }
        if (frames.Count == 0) throw new InvalidOperationException("The ETABS model has no frame objects (beams, columns or braces) to import.");
        var used = frames.SelectMany(frame => new[] { frame.StartJoint, frame.EndJoint }).ToHashSet(StringComparer.Ordinal);
        var points = used.Select(name => jointsByName[name]).ToDictionary(joint => joint.Name,
            joint => new StructuralPoint(joint.Name, new Position(joint.X, joint.Y, joint.Z)), StringComparer.Ordinal);
        var ys = points.Values.Select(point => point.Position.Y).ToArray();
        if (ys.Max() - ys.Min() > PlanarToleranceM)
        {
            var xs = points.Values.Select(point => point.Position.X).ToArray();
            throw new InvalidOperationException(ys.Max() - ys.Min() > xs.Max() - xs.Min() && xs.Max() - xs.Min() <= PlanarToleranceM
                ? "The ETABS frame lies in the YZ plane. Mcotis analyses a 2D frame in the XZ plane; rotate the model in ETABS so the frame lies along X, then import again."
                : $"The ETABS model is not a plane frame: its joints span Y = {ys.Min():0.###} … {ys.Max():0.###} m. Mcotis analyses a 2D frame in the XZ plane " +
                  "(all joints at one Y). Import a single frame line of the building.");
        }
        var duplicates = frameNames.Values.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicates.Length > 0) throw new InvalidOperationException($"ETABS frame names clash after import: {string.Join(", ", duplicates.Take(5))}.");
        var geometry = new GeometryModel(points,
            frames.Select(frame => new StructuralMember(frameNames[frame.Key], frame.StartJoint, frame.EndJoint, MemberKind.Frame)).ToArray());

        // ---- materials ----
        var materials = model.Materials.Select(material => new MaterialProperty
        {
            Name = material.Name,
            Kind = material.Kind,
            Grade = material.Grade,
            Color = MaterialProperty.Default(material.Kind, material.Name).Color,
            WeightPerVolume = material.UnitWeightKNPerM3,
            ElasticModulus = material.ElasticModulusKNPerM2,
            PoissonRatio = material.PoissonRatio,
            ThermalCoefficient = material.ThermalCoefficient,
            Fc = material.Fc,
            ExpectedFc = material.Fc,
            Fy = material.Fy,
            Fu = material.Fu,
            ExpectedFy = material.ExpectedFy,
            ExpectedFu = material.ExpectedFu,
            Source = Source,
        }).ToArray();
        var materialByName = model.Materials.ToDictionary(material => material.Name, StringComparer.Ordinal);

        // ---- sections: Mcotis's own form where it has one, else ETABS's properties as a general section ----
        var assigned = frames.Select(frame => frame.Section).ToHashSet(StringComparer.Ordinal);
        var etabsSections = model.Sections.ToDictionary(section => section.Name, StringComparer.Ordinal);
        // weight and mass per metre of a general section: its material on its area
        SectionDesignerValues? Unit(FrameSectionProperty section) =>
            materialByName.TryGetValue(section.Material, out var material) && section.Given is { Area: > 0 } given
                ? new SectionDesignerValues(given.Area, given.I33, given.I22, material.UnitWeightKNPerM3 * given.Area,
                    material.UnitWeightKNPerM3 / MaterialProperty.Gravity * given.Area, true)
                : null;
        var sections = new List<FrameSectionProperty>();
        var weights = new Dictionary<string, SectionDesignerValues>(StringComparer.Ordinal);
        // the sections the frames use and the I sections embedded in them (a new ETABS model lists a whole steel library)
        var embedded = model.Sections.Where(section => assigned.Contains(section.Name) && section.EmbeddedSection.Length > 0)
            .Select(section => section.EmbeddedSection).ToHashSet(StringComparer.Ordinal);
        var unused = model.Sections.Count(section => !assigned.Contains(section.Name) && !embedded.Contains(section.Name));
        if (unused > 0) notes.Add($"{unused} frame section(s) no frame uses are left out of the library.");
        foreach (var section in model.Sections.Where(section => assigned.Contains(section.Name) || embedded.Contains(section.Name)))
        {
            try
            {
                var (converted, weight, note) = Section(section, etabsSections, materialByName);
                sections.Add(converted);
                // a general section's weight / mass modifiers are part of its self-weight in ETABS (Export to ETABS writes a
                // composite's true self-weight that way); Mcotis applies section modifiers to stiffness only, so carry them here
                double weightModifier = section.Modifiers.ElementAtOrDefault(7) is > 0 and var w ? w : 1;
                double massModifier = section.Modifiers.ElementAtOrDefault(6) is > 0 and var mm ? mm : 1;
                if (converted.Shape == FrameShape.General && (weight is not null || weightModifier != 1 || massModifier != 1) &&
                    (weight ?? Unit(converted)) is { } own)
                    weights[section.Name] = own with { WeightPerLengthKNPerM = own.WeightPerLengthKNPerM * weightModifier, MassPerLengthTPerM = own.MassPerLengthTPerM * massModifier };
                else if (weight is not null) weights[section.Name] = weight;
                if (note is not null && assigned.Contains(section.Name)) notes.Add(note);
            }
            catch (InvalidOperationException ex)
            {
                if (assigned.Contains(section.Name)) problems.Add(ex.Message);
                else notes.Add($"Section {section.Name} is not used and could not be read ({ex.Message}) — left out of the library.");
            }
        }
        foreach (var missing in assigned.Where(name => !etabsSections.ContainsKey(name)))
            problems.Add($"Section {missing} is assigned to frames but ETABS does not define it.");

        // ---- members: section, local axis, in-plane end releases, modifiers ----
        var members = new List<FemSectionProperties>();
        foreach (var frame in frames)
        {
            var name = frameNames[frame.Key];
            var radians = frame.AngleDeg * Math.PI / 180;
            double cosine = Math.Cos(radians), sine = Math.Sin(radians);
            var releases = frame.Releases;
            var majorInPlane = Math.Abs(cosine) >= Math.Abs(sine);
            var moment = majorInPlane ? "M3" : "M2";
            var shear = majorInPlane ? "V2" : "V3";
            if (releases.Contains("PI") || releases.Contains("PJ"))
                problems.Add($"Frame {name} has an axial end release, which the 2D FEM solver does not support.");
            else if (Math.Abs(cosine * sine) >= 1e-7 && releases.Any(dof => dof.StartsWith('V') || dof.StartsWith('M')))
                problems.Add($"Frame {name} has a rotated local axis ({frame.AngleDeg:0.#}°) and an end release; the 2D FEM solver supports releases on axes at 0° / 90° only.");
            else if (releases.Contains(shear + "I") || releases.Contains(shear + "J"))
                problems.Add($"Frame {name} has an in-plane shear release, which the Euler-Bernoulli FEM solver does not support.");
            var section = etabsSections.GetValueOrDefault(frame.Section);
            var material = section is null ? "" : section.Shape switch
            {
                EtabsShape.FilledTube or EtabsShape.FilledPipe => section.FillMaterial,
                EtabsShape.Encasement => section.EncasementMaterial,
                _ => section.Material,
            };
            var modifiers = frame.Modifiers.Count == 8 ? frame.Modifiers : EtabsModel.NoModifiers;
            members.Add(new FemSectionProperties(name, 0, 0, 0, releases.Contains(moment + "I"), releases.Contains(moment + "J"),
                frame.Section, material, frame.AngleDeg, Modifiers: modifiers.ToArray()));
        }
        if (problems.Count > 0)
            throw new InvalidOperationException("The ETABS model cannot be analysed by Mcotis as it stands:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Distinct().Take(12).Select(problem => "  – " + problem)) +
                (problems.Count > 12 ? $"{Environment.NewLine}  … and {problems.Count - 12} more." : ""));

        // ---- loading ----
        var patternNames = model.Patterns.Select(pattern => pattern.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var patterns = model.Patterns.Select(pattern => new SapLoadPattern(pattern.Name, pattern.Type, pattern.SelfWeightMultiplier)).ToArray();
        var jointLoads = new List<SapPatternJointLoad>();
        foreach (var load in model.JointLoads)
        {
            if (!points.ContainsKey(load.Joint)) { Warn($"Joint load on {load.Joint} ({load.Pattern}) — the joint is not on a frame; ignored."); continue; }
            if (!patternNames.Contains(load.Pattern)) { Warn($"Joint load on {load.Joint} names load pattern {load.Pattern}, which is not defined; ignored."); continue; }
            if (Math.Abs(load.Fy) > 1e-9 || Math.Abs(load.Mx) > 1e-9 || Math.Abs(load.Mz) > 1e-9)
                Warn($"Joint load on {load.Joint} ({load.Pattern}) has out-of-plane components (FY / MX / MZ) — ignored; FX, FZ and MY used.");
            jointLoads.Add(new(load.Pattern, new FemJointLoad(load.Joint, load.Fx, load.Fz, load.My)));
        }
        var memberLoads = new List<SapPatternMemberLoad>();
        var memberPointLoads = new List<SapPatternMemberPointLoad>();
        var angles = frames.ToDictionary(frame => frame.Key, frame => frame.AngleDeg, StringComparer.Ordinal);
        foreach (var load in model.FrameLoads)
        {
            if (!frameNames.TryGetValue(load.Frame, out var name) || !angles.TryGetValue(load.Frame, out var angle)) continue;
            if (load.Direction is not { } direction) { Warn($"Frame load on {name} ({load.Pattern}, direction {load.DirectionText}) acts out of the XZ plane — ignored."); continue; }
            if (!patternNames.Contains(load.Pattern)) { Warn($"Frame load on {name} names load pattern {load.Pattern}, which is not defined; ignored."); continue; }
            if (direction == FemLoadDirection.Local2 && Math.Abs(Math.Sin(angle * Math.PI / 180)) > 1e-6)
                Warn($"Local-2 load on {name} ({load.Pattern}) with local axis angle {angle:0.#}° — only its in-plane part is used.");
            if (load.Concentrated) memberPointLoads.Add(new(load.Pattern, new FemMemberPointLoad(name, direction, load.RelStart, load.ValueStart)));
            else memberLoads.Add(new(load.Pattern, new FemMemberLoad(name, direction, load.RelStart, load.RelEnd, load.ValueStart, load.ValueEnd)));
        }

        var cases = model.Cases.Select(item =>
        {
            var isStatic = item.Type.Equals("Linear Static", StringComparison.OrdinalIgnoreCase) || item.Type.Equals("Nonlinear Static", StringComparison.OrdinalIgnoreCase);
            if (!isStatic) return new SapLoadCase(item.Name, item.Type, false, [], null, "Not a static case — not used by the internal FEM.");
            var caseNotes = item.Notes.ToList();
            var loads = new List<(string, double)>();
            foreach (var (pattern, factor) in item.Loads)
                if (patternNames.Contains(pattern)) loads.Add((pattern, factor));
                else caseNotes.Add($"Load pattern {pattern} is not defined; left out.");
            var type = item.Type.Equals("Linear Static", StringComparison.OrdinalIgnoreCase) ? "Linear Static" : "Nonlinear Static";
            if (type == "Linear Static" && item.InitialCase is not null)
                caseNotes.Add($"Continues from the stiffness of '{item.InitialCase}' in ETABS; here its loads are included.");
            return new SapLoadCase(item.Name, type, true, loads, item.InitialCase, caseNotes.Count == 0 ? null : string.Join(" ", caseNotes.Distinct()));
        }).ToArray();
        var comboNames = model.Combinations.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combinations = model.Combinations.Select(item =>
        {
            var linear = item.Type.Replace(" ", "").Equals("LinearAdd", StringComparison.OrdinalIgnoreCase) ||
                         item.Type.Replace(" ", "").Equals("LinearAdditive", StringComparison.OrdinalIgnoreCase);
            return new SapLoadCombination(item.Name, linear ? "Linear Additive" : item.Type, linear,
                item.Items.Select(term => (term.Name, comboNames.Contains(term.Name), term.Factor)).ToArray(),
                linear ? null : "Only linear additive combinations are evaluated.");
        }).ToArray();
        var loading = new SapFemLoading(patterns, jointLoads, memberLoads, memberPointLoads, [], cases, combinations, warnings);

        // ---- supports as ETABS has them (Mcotis finds its pins at the column bases; the view model compares) ----
        var restrained = model.Restraints.Where(item => points.ContainsKey(item.Joint) && item.Dofs.Count >= 6 && item.Dofs[0] && item.Dofs[2])
            .Select(item => item.Joint).Distinct(StringComparer.Ordinal).OrderBy(name => points[name].Position.X).ToArray();
        var fixedJoints = model.Restraints.Where(item => points.ContainsKey(item.Joint) && item.Dofs.Count >= 6 && item.Dofs[0] && item.Dofs[2] && item.Dofs[4])
            .Select(item => item.Joint).Distinct(StringComparer.Ordinal).ToArray();
        var partial = model.Restraints.Where(item => points.ContainsKey(item.Joint) && item.Dofs.Count >= 6 && (item.Dofs[0] ^ item.Dofs[2]))
            .Select(item => item.Joint).ToArray();
        if (partial.Length > 0)
            notes.Add($"ETABS restrains only one in-plane direction at {string.Join(", ", partial.Take(4))}{(partial.Length > 4 ? " …" : "")} — Mcotis has no roller supports; these are left free.");

        return new EtabsImportModel(geometry, materials, sections, members, weights, loading, restrained, fixedJoints, notes);
    }

    /// <summary>One ETABS frame section as a Mcotis library section, with the true weight per metre when its self-weight
    /// is not material × area, and a note on how it was taken over.</summary>
    private static (FrameSectionProperty Section, SectionDesignerValues? Weight, string? Note) Section(EtabsFrameSection section,
        IReadOnlyDictionary<string, EtabsFrameSection> all, IReadOnlyDictionary<string, EtabsMaterial> materials)
    {
        var common = new FrameSectionProperty { Name = section.Name, Material = section.Material, Modifiers = section.Modifiers.ToArray(), Source = Source, Notes = $"ETABS {section.ShapeName}" };
        EtabsMaterial Material(string name, string role) => materials.GetValueOrDefault(name)
            ?? throw new InvalidOperationException($"Section {section.Name}: its {role} material '{name}' is not defined in the model.");
        static double Mpa(double? kNPerM2, double fallback) => kNPerM2 is > 0 and var value ? value / 1000 : fallback;
        FrameSectionProperty General(EtabsSectionValues values, string material) => common with
        {
            Shape = FrameShape.General, SapType = section.Shape == EtabsShape.SectionDesigner ? "SD" : section.ShapeName, Material = material,
            Given = new SectionValues(values.Area, values.I33, values.I22, values.Torsion, values.ShearArea2, values.ShearArea3),
        };
        (FrameSectionProperty, SectionDesignerValues?, string?) Designed(IReadOnlyList<EtabsSdShape> shapes, string baseMaterial, string? how)
        {
            var basis = Material(baseMaterial, "base");
            var values = SectionDesignerSection.Compute(shapes, basis.ElasticModulusKNPerM2,
                name => materials.TryGetValue(name, out var item) ? (item.ElasticModulusKNPerM2, item.UnitWeightKNPerM3, item.UnitWeightKNPerM3 / MaterialProperty.Gravity) : null);
            var own = new EtabsSectionValues(values.Area, values.I33, values.I22, values.I33 + values.I22, 5.0 / 6 * values.Area, 5.0 / 6 * values.Area);
            string? note = values.Exact ? null : $"{section.Name}: shapes overlapping several materials were integrated numerically.";
            if (section.Reported is { Area: > 0 } reported)
            {
                // ETABS's own figures for the stiffness; the shapes for the self-weight (ETABS weighs each shape with its material)
                var deviation = new[] { (own.Area, reported.Area), (own.I33, reported.I33), (own.I22, reported.I22) }
                    .Max(pair => Math.Abs(pair.Item1 - pair.Item2) / Math.Max(Math.Abs(pair.Item2), 1e-12));
                if (deviation > 0.005) note = $"{section.Name}: A / I33 / I22 as ETABS reports them ({deviation:P1} from the shapes' values).";
                return (General(reported with { Torsion = reported.Torsion > 0 ? reported.Torsion : own.Torsion }, baseMaterial), values, note);
            }
            return (General(own, baseMaterial), values, note ?? how);
        }

        switch (section.Shape)
        {
            case EtabsShape.I:
                return (common with { Shape = FrameShape.I, Depth = section.Depth, Width = section.Width, FlangeThickness = section.FlangeThickness,
                    WebThickness = section.WebThickness, BottomWidth = section.BottomWidth > 0 ? section.BottomWidth : section.Width,
                    BottomFlangeThickness = section.BottomFlangeThickness > 0 ? section.BottomFlangeThickness : section.FlangeThickness }, null, null);
            case EtabsShape.Tube:
                return (common with { Shape = FrameShape.Box, Depth = section.Depth, Width = section.Width, FlangeThickness = section.FlangeThickness,
                    WebThickness = section.WebThickness }, null, null);
            case EtabsShape.Pipe:
                return (common with { Shape = FrameShape.Pipe, Depth = section.Depth, WebThickness = section.Thickness }, null, null);
            case EtabsShape.Rectangle:
                return (common with { Shape = FrameShape.Rectangle, Depth = section.Depth, Width = section.Width }, null,
                    section.Rebar is not null ? $"{section.Name}: the reinforcement of a concrete column is not in the analysis stiffness (as in ETABS)." : null);
            case EtabsShape.Circle:
                return (common with { Shape = FrameShape.Circle, Depth = section.Depth }, null, null);
            case EtabsShape.FilledTube when section.Depth > 0 && section.Width > 0 && Math.Abs(section.FlangeThickness - section.WebThickness) <= 1e-6:
            case EtabsShape.FilledPipe when section.Depth > 0 && section.Thickness > 0:
            {
                var steel = Material(section.Material, "steel");
                var concrete = Material(section.FillMaterial, "fill");
                var circular = section.Shape == EtabsShape.FilledPipe;
                var input = new ColumnSectionInput
                {
                    Tube = circular ? TubeShape.Circular : TubeShape.Rectangular,
                    TubeDepth = section.Depth * 1000,
                    TubeWidth = (circular ? section.Depth : section.Width) * 1000,
                    TubeThickness = (circular ? section.Thickness : section.FlangeThickness) * 1000,
                    Fck = Mpa(concrete.Fc, 50), Fy = Mpa(steel.Fy, 355), Ea = Mpa(steel.ElasticModulusKNPerM2, 210_000),
                };
                return ((common with { Shape = FrameShape.FilledTube, SapType = "FilledTube", Material = concrete.Name, SteelMaterial = steel.Name, Composite = input
                    }).WithCompositeOutline(), null, null);
            }
            case EtabsShape.FilledTube when section.Depth > 0 && section.Width > 0:
                // walls of two thicknesses: the tube and its core as Section Designer shapes
                return Designed([
                    new EtabsSdShape(1, EtabsSdShapeKind.Rectangle, section.Material, 0, 0, section.Depth, section.Width, 0, 0),
                    new EtabsSdShape(2, EtabsSdShapeKind.Rectangle, section.FillMaterial, 0, 0, section.Depth - 2 * section.FlangeThickness,
                        section.Width - 2 * section.WebThickness, 0, 0)], section.Material,
                    $"{section.Name}: a filled tube with flanges and webs of different thickness is a general section in Mcotis.");
            case EtabsShape.Encasement when all.GetValueOrDefault(section.EmbeddedSection) is { Shape: EtabsShape.I } embedded:
            {
                var steel = Material(section.Material, "steel");
                var concrete = Material(section.EncasementMaterial, "encasement");
                var rebar = section.Rebar;
                var bars = rebar is null ? null : materials.GetValueOrDefault(rebar.LongitudinalMaterial);
                var input = new ColumnSectionInput
                {
                    Width = section.Width * 1000,
                    Depth = section.Depth * 1000,
                    SteelDepth = embedded.Depth * 1000,
                    SteelWidth = embedded.Width * 1000,
                    FlangeThickness = embedded.FlangeThickness * 1000,
                    WebThickness = embedded.WebThickness * 1000,
                    Cover = (rebar?.Cover ?? 0.04) * 1000,
                    BarDiameter = (rebar?.BarDiameter ?? 0) * 1000,
                    BarsAlongWidth = Math.Max(2, rebar?.Bars3Dir ?? 2),
                    BarsAlongDepth = Math.Max(0, (rebar?.Bars2Dir ?? 2) - 2),
                    LinkDiameter = (rebar?.TieDiameter ?? 0) * 1000,
                    LinkSpacing = (rebar?.TieSpacing ?? 0) * 1000,
                    Fck = Mpa(concrete.Fc, 50), Fy = Mpa(steel.Fy, 355), Fyk = Mpa(bars?.Fy, 500),
                    Ea = Mpa(steel.ElasticModulusKNPerM2, 210_000), Es = Mpa(bars?.ElasticModulusKNPerM2, 200_000),
                };
                var unequal = Math.Abs(embedded.BottomWidth - embedded.Width) > 1e-6 || Math.Abs(embedded.BottomFlangeThickness - embedded.FlangeThickness) > 1e-6;
                var note = rebar is { BarDiameter: > 0 }
                    ? $"{section.Name}: Mcotis's steel encased section counts its {2 * input.BarsAlongWidth + 2 * input.BarsAlongDepth} Ø{input.BarDiameter:0} bars in the stiffness and self-weight; ETABS's section properties leave them out."
                    : null;
                if (unequal) note = (note is null ? "" : note + " ") + $"{section.Name}: the embedded I section's bottom flange is taken equal to its top flange.";
                return ((common with { Shape = FrameShape.Encased, SapType = "Encased", Material = concrete.Name, SteelMaterial = steel.Name,
                    RebarMaterial = bars?.Name ?? "", Composite = input }).WithCompositeOutline(), null, note);
            }
            case EtabsShape.SectionDesigner when section.SdShapes.Count > 0:
                return Designed(section.SdShapes, section.Material, null);
        }
        if (section.Reported is { Area: > 0 } values)
            return (General(values, section.Material), null,
                $"{section.Name} ({section.ShapeName}): A / I33 / I22 as ETABS reports them; its self-weight is the {section.Material} unit weight on that area" +
                (section.Modifiers.ElementAtOrDefault(7) is var w && w > 0 && w != 1 ? $" × its weight modifier {w:0.####}." : "."));
        throw new InvalidOperationException(section.Reported is null
            ? $"Section {section.Name} ({section.ShapeName}) cannot be read from an .e2k file — import it from the running ETABS, which reports its properties."
            : $"Section {section.Name} ({section.ShapeName}): ETABS reports no properties for it (e.g. an auto-select list); assign a section with properties in ETABS.");
    }
}
