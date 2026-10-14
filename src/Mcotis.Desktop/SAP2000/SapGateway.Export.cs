using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using CSiAPIv1;

namespace Mcotis.Desktop.SAP2000;

/// <summary>
/// File → Export to SAP2000. Signatures checked against CSiAPIv1 of SAP2000 27: PropMaterial.SetMaterial / SetMPIsotropic /
/// SetMPUniaxial / SetWeightAndMass / SetOSteel_1 / SetOConcrete_1 / SetOTendon_1; PropFrame.SetRectangle / SetTube_1 /
/// SetISection_1 / SetPipe / SetCircle / SetChannel_2 / SetTee_1 / SetAngle_1 / SetGeneral / SetModifiers; PropCable.SetProp /
/// SetModifiers; PointObj.AddCartesian / SetRestraint / GetRestraint / DeleteSpecialPoint / SetLoadForce / DeleteLoadForce;
/// EditPoint.ChangeCoordinates_1; FrameObj.AddByPoint / Delete / SetSection / SetLocalAxes / SetReleases / SetModifiers;
/// CableObj.AddByPoint / Delete / SetProperty / SetModifiers / SetCableData / SetLoadTargetForce; LoadPatterns.Add /
/// SetSelfWTMultiplier; LoadCases.StaticLinear / StaticNonlinear SetCase / SetLoads / SetInitialCase,
/// StaticNonlinear.SetGeometricNonlinearity; RespCombo.Add / Delete / SetCaseList; Analyze.SetRunCaseFlag.
/// Everything is checked before the first write; the model is not saved.
/// </summary>
public sealed partial class SapGateway
{
    private const int NonlinearPDeltaLargeDisplacement = 2;

    public Task<SapExportReport> ExportModelAsync(SapExportModel x, ModelInfo expected, bool unlock, bool preview = false) => worker.Run(() =>
    {
        Verify(expected);
        if (!Model.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Export writes kN, m values; the model is in {ReadInfo().Units}. Set SAP2000 to kN, m, C and export again.");
        var lines = new List<string>();
        var warnings = new List<string>();
        void Soft(int code, string what) { if (code != 0) warnings.Add($"{what} (SAP2000 code {code})"); }

        // ---------------- read the model and check the export against it, before any write ----------------
        var sapPoints = new Dictionary<string, Position>(StringComparer.Ordinal);
        int n = 0; string[] names = [];
        Check(Model.PointObj.GetNameList(ref n, ref names), "PointObj.GetNameList");
        foreach (var name in names.Take(n)) sapPoints[name] = ReadPosition(name);
        var sapMembers = new Dictionary<string, StructuralMember>(StringComparer.Ordinal);
        foreach (var kind in new[] { MemberKind.Frame, MemberKind.Cable })
        {
            n = 0; names = [];
            Check(kind == MemberKind.Frame ? Model.FrameObj.GetNameList(ref n, ref names) : Model.CableObj.GetNameList(ref n, ref names), $"{kind}.GetNameList");
            foreach (var name in names.Take(n))
            {
                string start = "", end = "";
                Check(kind == MemberKind.Frame ? Model.FrameObj.GetPoints(name, ref start, ref end) : Model.CableObj.GetPoints(name, ref start, ref end), $"{kind}.GetPoints", name);
                sapMembers[name] = new(name, start, end, kind);
            }
        }
        HashSet<string> NameSet(Func<(int, string[])> read) { var (count, list) = read(); return list.Take(count).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        var sapMaterials = NameSet(() => { int c = 0; string[] l = []; Check(Model.PropMaterial.GetNameList(ref c, ref l), "PropMaterial.GetNameList"); return (c, l); });
        var sapFrameProps = NameSet(() => { int c = 0; string[] l = []; Check(Model.PropFrame.GetNameList(ref c, ref l, 0), "PropFrame.GetNameList"); return (c, l); });
        var sapCableProps = NameSet(() => { int c = 0; string[] l = []; Model.PropCable.GetNameList(ref c, ref l); return (c, l); });
        var sapPatterns = NameSet(() => { int c = 0; string[] l = []; Check(Model.LoadPatterns.GetNameList(ref c, ref l), "LoadPatterns.GetNameList"); return (c, l); });
        var sapCases = NameSet(() => { int c = 0; string[] l = []; Check(Model.LoadCases.GetNameList(ref c, ref l), "LoadCases.GetNameList"); return (c, l); });
        var sapCombos = NameSet(() => { int c = 0; string[] l = []; Check(Model.RespCombo.GetNameList(ref c, ref l), "RespCombo.GetNameList"); return (c, l); });

        var geometry = x.Geometry;
        var target = geometry.Members.Where(member => member.Kind is MemberKind.Frame or MemberKind.Cable).ToDictionary(member => member.Name, StringComparer.Ordinal);
        var properties = x.Members.ToDictionary(member => member.MemberName, StringComparer.Ordinal);
        var frameLibrary = x.FrameSections.ToDictionary(section => section.Name, StringComparer.OrdinalIgnoreCase);
        var cableLibrary = x.CableSections.ToDictionary(section => section.Name, StringComparer.OrdinalIgnoreCase);
        var materials = sapMaterials.Concat(x.Materials.Select(material => material.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var member in target.Values)
        {
            if (!geometry.Points.ContainsKey(member.Start) || !geometry.Points.ContainsKey(member.End))
                throw new InvalidOperationException($"{member.Kind} {member.Name} has an end joint that is not in the model.");
            if (!properties.TryGetValue(member.Name, out var property) || string.IsNullOrWhiteSpace(property.SectionName))
                throw new InvalidOperationException($"{member.Kind} {member.Name} has no section to export.");
            var known = member.Kind == MemberKind.Frame ? frameLibrary.ContainsKey(property.SectionName) || sapFrameProps.Contains(property.SectionName)
                                                       : cableLibrary.ContainsKey(property.SectionName) || sapCableProps.Contains(property.SectionName);
            if (!known) throw new InvalidOperationException($"Section '{property.SectionName}' of {member.Name} is neither in the Mcotis library nor in SAP2000.");
        }
        foreach (var section in x.FrameSections.Where(section => !sapFrameProps.Contains(section.Name)))
            if (!materials.Contains(section.Material)) throw new InvalidOperationException($"Material '{section.Material}' of section '{section.Name}' is not defined.");
        foreach (var section in x.CableSections.Where(section => !sapCableProps.Contains(section.Name)))
            if (!materials.Contains(section.Material)) throw new InvalidOperationException($"Material '{section.Material}' of cable section '{section.Name}' is not defined.");

        // members that go: not in Mcotis, or another kind / other end joints; members that come: the reverse
        var removed = sapMembers.Values.Where(member => !target.TryGetValue(member.Name, out var keep) ||
            keep.Kind != member.Kind || keep.Start != member.Start || keep.End != member.End).ToArray();
        var removedNames = removed.Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        var added = target.Values.Where(member => !sapMembers.ContainsKey(member.Name) || removedNames.Contains(member.Name)).ToArray();
        var moved = geometry.Points.Values.Where(point => sapPoints.TryGetValue(point.Name, out var old) && !Close(old, point.Position)).ToArray();
        var newPoints = geometry.Points.Values.Where(point => !sapPoints.ContainsKey(point.Name)).ToArray();
        var orphanPoints = sapPoints.Keys.Where(name => !geometry.Points.ContainsKey(name)).ToArray();
        double merge = 0;
        Check(Model.GetMergeTol(ref merge), "GetMergeTol");
        var finalPositions = geometry.Points.Values.ToArray();
        foreach (var point in moved.Concat(newPoints))
        {
            if (!point.Position.IsFinite) throw new InvalidOperationException($"Joint {point.Name} has non-finite coordinates.");
            if (finalPositions.FirstOrDefault(other => other.Name != point.Name && Near(other.Position, point.Position, merge)) is { } clash)
                throw new InvalidOperationException($"Joints {point.Name} and {clash.Name} coincide within the SAP2000 merge tolerance.");
        }
        var patterns = sapPatterns.Concat(x.Patterns.Select(pattern => pattern.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in x.Cases)
            if (item.Loads.FirstOrDefault(load => !patterns.Contains(load.Pattern)) is { Pattern: { } missing })
                throw new InvalidOperationException($"Load case '{item.Name}' uses load pattern '{missing}', which does not exist.");
        foreach (var load in x.JointLoads.Where(load => !patterns.Contains(load.Pattern) || !geometry.Points.ContainsKey(load.PointName)))
            throw new InvalidOperationException($"Joint load {load.PointName} / {load.Pattern}: the joint or the load pattern does not exist.");
        foreach (var joint in x.JointRestraints.Keys.Where(joint => !geometry.Points.ContainsKey(joint)))
            throw new InvalidOperationException($"A restraint refers to missing joint '{joint}'.");
        var caseNames = sapCases.Concat(x.Cases.Select(item => item.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var comboNames = sapCombos.Concat(x.Combinations.Select(item => item.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var combo in x.Combinations)
            if (combo.Items.FirstOrDefault(item => !caseNames.Contains(item.Name) && !comboNames.Contains(item.Name)) is { Name: { } missing })
                throw new InvalidOperationException($"Combination '{combo.Name}' uses '{missing}', which is neither a load case nor a combination.");

        // Supports. Restraints the model holds are written as they are, every component and at every joint: an imported
        // roller or fixed support is not reduced to a pin, and a joint the model leaves free is free in SAP2000 too. A
        // model that holds none has only the pins of its column bases: such a joint becomes a pin where SAP2000
        // restrains nothing, and what SAP2000 restrains is kept. The joints concerned are those of the exported
        // members and those the model restrains.
        var supportJoints = target.Values.SelectMany(member => new[] { member.Start, member.End }).Concat(x.JointRestraints.Keys)
            .Distinct(StringComparer.Ordinal).ToArray();
        bool[]? RestraintToWrite(string joint, bool[] held)
        {
            var wanted = x.JointRestraints.GetValueOrDefault(joint, Mcotis.Core.Restraint.Free);
            if (!x.RestraintsAssigned && (wanted.IsFree || held.Any(value => value))) return null;
            bool[] exact = [wanted.Ux, wanted.Uy, wanted.Uz, wanted.Rx, wanted.Ry, wanted.Rz];
            return held.SequenceEqual(exact) ? null : exact;
        }

        if (preview)
        {
            int Missing(IEnumerable<string> wanted, HashSet<string> have) => wanted.Count(name => !have.Contains(name));
            var info = ReadInfo();
            var supportChanges = 0;
            foreach (var joint in supportJoints)
            {
                bool[] held = new bool[6];   // a joint the export adds has no restraint yet
                if (sapPoints.ContainsKey(joint)) Check(Model.PointObj.GetRestraint(joint, ref held), "PointObj.GetRestraint", joint);
                if (RestraintToWrite(joint, held) is not null) supportChanges++;
            }
            lines.Add($"Target: {info.Path} ({info.Units}{(info.IsLocked ? ", locked — analysis results will be deleted" : "")})");
            lines.Add($"Geometry: remove {removed.Length} member(s){(removed.Length > 0 ? " (" + string.Join(", ", removed.Take(8).Select(member => member.Name)) + (removed.Length > 8 ? ", …" : "") + ")" : "")}, add {added.Length}; move {moved.Length} joint(s), add {newPoints.Length}, remove {orphanPoints.Length} unconnected");
            lines.Add($"Libraries: add {Missing(x.Materials.Select(m => m.Name), sapMaterials)} material(s), {Missing(x.FrameSections.Select(s => s.Name), sapFrameProps) + Missing(x.CableSections.Select(s => s.Name), sapCableProps)} section(s)");
            lines.Add($"Loading: add {Missing(x.Patterns.Select(p => p.Name), sapPatterns)} load pattern(s); {x.JointLoads.Count} Tab 01 joint load(s); {x.CableTargets.Count} cable target force(s)");
            lines.Add(x.RestraintsAssigned
                ? $"Supports: the {x.JointRestraints.Count} joint restraint(s) of the Mcotis model are written as they are, every component, and a joint it leaves free is freed — {supportChanges} joint(s) change"
                : $"Supports: the Mcotis model holds no restraint — {supportChanges} column base(s) that SAP2000 leaves free become pins; the restraints SAP2000 has are kept");
            lines.Add($"Analysis: active DOF set to {x.ActiveDofs}; " +
                      $"add {Missing(x.Cases.Select(c => c.Name), sapCases)} load case(s), update {x.Cases.Count - Missing(x.Cases.Select(c => c.Name), sapCases)}; add {Missing(x.Combinations.Select(c => c.Name), sapCombos)} combination(s)");
            return new SapExportReport(info.Path, lines);
        }

        // ---------------- write ----------------
        if (ReadInfo().IsLocked)
        {
            if (!unlock) throw new InvalidOperationException("The SAP2000 model is locked. Unlock it (SAP2000 deletes its analysis results) to export.");
            Check(Model.SetModelIsLocked(false), "SetModelIsLocked");
            lines.Add("Unlocked the model (SAP2000 deleted its analysis results).");
        }
        Verify(expected, true);
        if (!Model.GetPresentCoordSystem().Equals("Global", StringComparison.OrdinalIgnoreCase))
            Check(Model.SetPresentCoordSystem("Global"), "SetPresentCoordSystem", "Global");

        // materials SAP2000 does not have
        var addedMaterials = 0;
        foreach (var material in x.Materials.Where(material => !sapMaterials.Contains(material.Name)))
        {
            Check(Model.PropMaterial.SetMaterial(material.Name, MaterialType(material.Kind), Bgr(material.Color), material.Notes), "PropMaterial.SetMaterial", material.Name);
            if (material.IsUniaxial)
                Check(Model.PropMaterial.SetMPUniaxial(material.Name, material.ElasticModulus, material.ThermalCoefficient), "PropMaterial.SetMPUniaxial", material.Name);
            else
                Check(Model.PropMaterial.SetMPIsotropic(material.Name, material.ElasticModulus, material.PoissonRatio, material.ThermalCoefficient), "PropMaterial.SetMPIsotropic", material.Name);
            Check(Model.PropMaterial.SetWeightAndMass(material.Name, 1, material.WeightPerVolume), "PropMaterial.SetWeightAndMass", material.Name);
            // design data (not used by the analysis): typical stress-strain parameters of the SAP2000 examples
            if (material.Kind == MaterialKind.Steel && material.Fy is > 0 and var fy)
            {
                var fu = material.Fu ?? fy * 1.25;
                Soft(Model.PropMaterial.SetOSteel_1(material.Name, fy, fu, material.ExpectedFy ?? fy * 1.1, material.ExpectedFu ?? fu * 1.1, 1, 1, 0.02, 0.1, 0.2, -0.1), $"steel data of {material.Name}");
            }
            else if (material.Kind == MaterialKind.Concrete && material.Fc is > 0 and var fc)
                Soft(Model.PropMaterial.SetOConcrete_1(material.Name, fc, material.IsLightweight, material.ShearStrengthReductionFactor ?? 0, 1, 2, 0.0022, 0.0052, -0.1), $"concrete data of {material.Name}");
            else if (material.Kind == MaterialKind.Tendon && material.Fy is > 0 and var fyt)
                Soft(Model.PropMaterial.SetOTendon_1(material.Name, fyt, material.Fu ?? fyt / 0.9, 1, 1, -0.1), $"tendon data of {material.Name}");
            addedMaterials++;
        }

        // frame sections: new ones, and the standard shapes Mcotis holds (a SAP2000 general / Section Designer section is kept)
        int addedSections = 0, updatedSections = 0;
        foreach (var section in x.FrameSections)
        {
            var exists = sapFrameProps.Contains(section.Name);
            var standard = section.Shape is not (FrameShape.General or FrameShape.FilledTube or FrameShape.Encased);
            if (exists && !standard) continue;
            WriteFrameSection(section);
            var modifiers = section.Modifiers.Count >= 8 ? section.Modifiers.Take(8).ToArray() : [1.0, 1, 1, 1, 1, 1, 1, 1];
            Check(Model.PropFrame.SetModifiers(section.Name, ref modifiers), "PropFrame.SetModifiers", section.Name);
            if (exists) updatedSections++; else addedSections++;
        }
        foreach (var section in x.CableSections)
        {
            var exists = sapCableProps.Contains(section.Name);
            if (exists)
            {
                string material = "", notes = "", guid = ""; double area = 0; int color = 0;
                Check(Model.PropCable.GetProp(section.Name, ref material, ref area, ref color, ref notes, ref guid), "PropCable.GetProp", section.Name);
                if (Close(area, section.Area) && material.Equals(section.Material, StringComparison.OrdinalIgnoreCase)) continue;
            }
            Check(Model.PropCable.SetProp(section.Name, section.Material, section.Area, Bgr(section.Color), section.Notes), "PropCable.SetProp", section.Name);
            var modifiers = section.Modifiers.Count >= 3 ? section.Modifiers.Take(3).ToArray() : [1.0, 1, 1];
            Check(Model.PropCable.SetModifiers(section.Name, ref modifiers), "PropCable.SetModifiers", section.Name);
            if (exists) updatedSections++; else addedSections++;
        }
        lines.Add($"Libraries: {addedMaterials} material(s) added; {addedSections} section(s) added, {updatedSections} updated.");

        // geometry: members that go, their unconnected joints, joint moves (cable lengths kept relative), new joints and members
        var keptCables = target.Values.Where(member => member.Kind == MemberKind.Cable && sapMembers.ContainsKey(member.Name) && !removedNames.Contains(member.Name)).ToArray();
        var before = new GeometryModel(sapPoints.ToDictionary(point => point.Key, point => new StructuralPoint(point.Key, point.Value), StringComparer.Ordinal), sapMembers.Values.ToArray());
        var movedNames = moved.Select(point => point.Name).ToHashSet(StringComparer.Ordinal);
        var shapes = keptCables.Where(cable => movedNames.Contains(cable.Start) || movedNames.Contains(cable.End))
            .Select(cable => ReadCableShape(cable.Name, ChordLength(before, cable), ChordLength(geometry, cable)))
            .Where(shape => shape is not null && Math.Abs(shape.NewChord - shape.OldChord) > 1e-9).Select(shape => shape!).ToArray();
        var template = keptCables.Select(cable => ReadCableShape(cable.Name, ChordLength(before, cable), ChordLength(before, cable))).FirstOrDefault(shape => shape is not null);
        foreach (var member in removed)
            Check(member.Kind == MemberKind.Frame ? Model.FrameObj.Delete(member.Name) : Model.CableObj.Delete(member.Name), $"{member.Kind}.Delete", member.Name);
        var leftOver = 0;
        foreach (var name in orphanPoints)
            if (Model.PointObj.DeleteSpecialPoint(name) != 0) leftOver++;
        foreach (var point in moved)
        {
            Check(Model.EditPoint.ChangeCoordinates_1(point.Name, point.Position.X, point.Position.Y, point.Position.Z, true), "EditPoint.ChangeCoordinates_1", point.Name);
            if (!Close(ReadPosition(point.Name), point.Position)) throw new InvalidOperationException($"Joint {point.Name} did not reach its coordinates.");
        }
        foreach (var shape in shapes) WriteCableShape(shape, shape.NewUndeformedLength);
        foreach (var point in newPoints)
        {
            var name = "";
            Check(Model.PointObj.AddCartesian(point.Position.X, point.Position.Y, point.Position.Z, ref name, point.Name, "Global", true), "PointObj.AddCartesian", point.Name);
            if (name != point.Name) throw new InvalidOperationException($"SAP2000 named the new joint '{name}' instead of '{point.Name}'.");
        }
        foreach (var member in added)
        {
            var name = "";
            var section = properties[member.Name].SectionName!;
            Check(member.Kind == MemberKind.Frame ? Model.FrameObj.AddByPoint(member.Start, member.End, ref name, section, member.Name)
                                                  : Model.CableObj.AddByPoint(member.Start, member.End, ref name, section, member.Name), $"{member.Kind}.AddByPoint", member.Name);
            if (name != member.Name) throw new InvalidOperationException($"SAP2000 named the new {member.Kind.ToString().ToLowerInvariant()} '{name}' instead of '{member.Name}'.");
            if (member.Kind == MemberKind.Cable && template is not null)
            {
                var chord = ChordLength(geometry, member);
                var relative = template.UndeformedLength / template.OldChord;
                WriteCableShape(template with { Name = member.Name, OldChord = chord, NewChord = chord }, relative * chord);
            }
        }
        lines.Add($"Geometry: {removed.Length} member(s) removed, {added.Length} added; {moved.Length} joint(s) moved, {newPoints.Length} added" +
                  (orphanPoints.Length > 0 ? $", {orphanPoints.Length - leftOver} unconnected joint(s) removed" : "") +
                  (shapes.Length > 0 ? $"; {shapes.Length} cable length(s) kept relative to their chord" : "") + ".");
        if (leftOver > 0) warnings.Add($"{leftOver} joint(s) not in Mcotis are still in SAP2000 (connected to other objects).");

        // assignments: section, local axis, in-plane moment releases and modifiers of every member
        var reassigned = 0;
        foreach (var member in target.Values)
        {
            var property = properties[member.Name];
            if (member.Kind == MemberKind.Frame)
            {
                string section = "", auto = "";
                Check(Model.FrameObj.GetSection(member.Name, ref section, ref auto), "FrameObj.GetSection", member.Name);
                if (!section.Equals(property.SectionName, StringComparison.OrdinalIgnoreCase))
                {
                    Check(Model.FrameObj.SetSection(member.Name, property.SectionName!), "FrameObj.SetSection", member.Name);
                    reassigned++;
                }
                Check(Model.FrameObj.SetLocalAxes(member.Name, property.LocalAngleDeg), "FrameObj.SetLocalAxes", member.Name);
                var modifiers = property.Modifiers is { Count: >= 8 } given ? given.Take(8).ToArray() : [1.0, 1, 1, 1, 1, 1, 1, 1];
                Check(Model.FrameObj.SetModifiers(member.Name, ref modifiers), "FrameObj.SetModifiers", member.Name);
                bool[] ii = new bool[6], jj = new bool[6]; double[] si = new double[6], sj = new double[6];
                Check(Model.FrameObj.GetReleases(member.Name, ref ii, ref jj, ref si, ref sj), "FrameObj.GetReleases", member.Name);
                var radians = property.LocalAngleDeg * Math.PI / 180;
                var index = Math.Abs(Math.Cos(radians)) >= Math.Abs(Math.Sin(radians)) ? 5 : 4;
                if (ii[index] != property.ReleaseStartRotation || jj[index] != property.ReleaseEndRotation)
                {
                    ii[index] = property.ReleaseStartRotation;
                    jj[index] = property.ReleaseEndRotation;
                    Check(Model.FrameObj.SetReleases(member.Name, ref ii, ref jj, ref si, ref sj), "FrameObj.SetReleases", member.Name);
                }
            }
            else
            {
                string section = "";
                Check(Model.CableObj.GetProperty(member.Name, ref section), "CableObj.GetProperty", member.Name);
                if (!section.Equals(property.SectionName, StringComparison.OrdinalIgnoreCase))
                {
                    Check(Model.CableObj.SetProperty(member.Name, property.SectionName!), "CableObj.SetProperty", member.Name);
                    reassigned++;
                }
                var modifiers = property.Modifiers is { Count: >= 3 } given ? given.Take(3).ToArray() : [1.0, 1, 1];
                Check(Model.CableObj.SetModifiers(member.Name, ref modifiers), "CableObj.SetModifiers", member.Name);
            }
        }
        // supports, as the preview said (RestraintToWrite, above)
        var restraintChanges = 0;
        foreach (var joint in supportJoints)
        {
            bool[] held = new bool[6];
            Check(Model.PointObj.GetRestraint(joint, ref held), "PointObj.GetRestraint", joint);
            if (RestraintToWrite(joint, held) is not { } exact) continue;
            Check(Model.PointObj.SetRestraint(joint, ref exact), "PointObj.SetRestraint", joint);
            restraintChanges++;
        }
        bool[] activeDofs = Mcotis.Core.DegreesOfFreedom.Components.Select(component => Mcotis.Core.DegreesOfFreedom.Contains(x.ActiveDofs, component)).ToArray();
        Check(Model.Analyze.SetActiveDOF(ref activeDofs), "Analyze.SetActiveDOF");
        lines.Add($"Assignments: {target.Count} members (section, local axis, releases, modifiers; {reassigned} section change(s)); " +
                  (x.RestraintsAssigned ? $"{restraintChanges} joint restraint(s) set as the Mcotis model holds them" : $"{restraintChanges} new pin support(s)") +
                  $"; active DOF {x.ActiveDofs}.");

        // load patterns
        var addedPatterns = 0;
        foreach (var pattern in x.Patterns)
        {
            if (!sapPatterns.Contains(pattern.Name))
            {
                Check(Model.LoadPatterns.Add(pattern.Name, PatternType(pattern.Type), pattern.SelfWeightMultiplier, false), "LoadPatterns.Add", pattern.Name);
                addedPatterns++;
            }
            else Check(Model.LoadPatterns.SetSelfWTMultiplier(pattern.Name, pattern.SelfWeightMultiplier), "LoadPatterns.SetSelfWTMultiplier", pattern.Name);
        }

        // Tab 01 joint loads replace their patterns' joint loads at the loaded joints
        var jointLoads = 0;
        foreach (var load in x.JointLoads)
        {
            var existing = ReadPointForceAssignments(load.PointName).Where(item => item.LoadPattern.Equals(load.Pattern, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (Close(load.FzKN, 0))
            {
                if (existing.Length > 0) Check(Model.PointObj.DeleteLoadForce(load.PointName, load.Pattern, eItemType.Objects), "PointObj.DeleteLoadForce", $"{load.PointName} / {load.Pattern}");
                continue;
            }
            double[] values = [0, 0, load.FzKN, 0, 0, 0];
            Check(Model.PointObj.SetLoadForce(load.PointName, load.Pattern, ref values, true, "Global", eItemType.Objects), "PointObj.SetLoadForce", $"{load.PointName} / {load.Pattern}");
            jointLoads++;
        }
        // Tab 03 cable target forces
        foreach (var cable in x.CableTargets)
        {
            if (ReadCableTargetForces(cable.CableName).Any(item => item.LoadPattern.Equals(cable.Pattern, StringComparison.OrdinalIgnoreCase)))
                Check(Model.CableObj.DeleteLoadTargetForce(cable.CableName, cable.Pattern, eItemType.Objects), "CableObj.DeleteLoadTargetForce", cable.CableName);
            double p = cable.ForceKN, rd = PretensionRelDist;
            Check(Model.CableObj.SetLoadTargetForce(cable.CableName, cable.Pattern, ref p, ref rd, eItemType.Objects), "CableObj.SetLoadTargetForce", cable.CableName);
        }
        lines.Add($"Loading: {addedPatterns} load pattern(s) added; {jointLoads} Tab 01 joint load(s) on {x.Tab01Points.Count} joints; {x.CableTargets.Count} cable target force(s).");

        // static load cases: new ones are created; every exported case takes its loads and initial case
        int addedCases = 0;
        foreach (var item in x.Cases)
        {
            var nonlinear = item.Type.Contains("Nonlinear", StringComparison.OrdinalIgnoreCase);
            if (sapCases.Contains(item.Name))
            {
                var type = eLoadCaseType.LinearStatic; int subType = 0;
                Check(Model.LoadCases.GetTypeOAPI(item.Name, ref type, ref subType), "LoadCases.GetTypeOAPI", item.Name);
                if (type != (nonlinear ? eLoadCaseType.NonlinearStatic : eLoadCaseType.LinearStatic))
                {
                    warnings.Add($"Load case '{item.Name}' is {type} in SAP2000 and {item.Type} in Mcotis — left as it is.");
                    continue;
                }
            }
            else
            {
                Check(nonlinear ? Model.LoadCases.StaticNonlinear.SetCase(item.Name) : Model.LoadCases.StaticLinear.SetCase(item.Name), "LoadCases.SetCase", item.Name);
                if (nonlinear) Check(Model.LoadCases.StaticNonlinear.SetGeometricNonlinearity(item.Name, NonlinearPDeltaLargeDisplacement), "StaticNonlinear.SetGeometricNonlinearity", item.Name);
                addedCases++;
            }
            var count = item.Loads.Count;
            string[] loadTypes = Enumerable.Repeat("Load", count).ToArray(), loadNames = item.Loads.Select(load => load.Pattern).ToArray();
            double[] factors = item.Loads.Select(load => load.Factor).ToArray();
            Check(nonlinear ? Model.LoadCases.StaticNonlinear.SetLoads(item.Name, count, ref loadTypes, ref loadNames, ref factors)
                            : Model.LoadCases.StaticLinear.SetLoads(item.Name, count, ref loadTypes, ref loadNames, ref factors), "LoadCases.SetLoads", item.Name);
            var initial = FemLoadPlan.IsNoInitialCase(item.InitialCase) ? "None" : item.InitialCase;   // "None": zero initial conditions
            Check(nonlinear ? Model.LoadCases.StaticNonlinear.SetInitialCase(item.Name, initial)
                            : Model.LoadCases.StaticLinear.SetInitialCase(item.Name, initial), "LoadCases.SetInitialCase", item.Name);
        }
        // combinations: new ones are created, changed ones rebuilt (all created before their items are set)
        int addedCombos = 0, rebuiltCombos = 0;
        var pending = new List<SapExportCombination>();
        foreach (var combo in x.Combinations)
        {
            if (sapCombos.Contains(combo.Name))
            {
                int count = 0; eCNameType[] types = []; string[] items = []; double[] factors = [];
                Check(Model.RespCombo.GetCaseList(combo.Name, ref count, ref types, ref items, ref factors), "RespCombo.GetCaseList", combo.Name);
                var current = Enumerable.Range(0, count).Select(i => (items[i].ToUpperInvariant(), Math.Round(factors[i], 9))).OrderBy(item => item).ToArray();
                var wanted = combo.Items.Select(item => (item.Name.ToUpperInvariant(), Math.Round(item.Factor, 9))).OrderBy(item => item).ToArray();
                if (current.SequenceEqual(wanted)) continue;
                Check(Model.RespCombo.Delete(combo.Name), "RespCombo.Delete", combo.Name);
                rebuiltCombos++;
            }
            else addedCombos++;
            Check(Model.RespCombo.Add(combo.Name, 0), "RespCombo.Add", combo.Name);
            pending.Add(combo);
        }
        foreach (var combo in pending)
            foreach (var (name, factor) in combo.Items)
            {
                var type = caseNames.Contains(name) ? eCNameType.LoadCase : eCNameType.LoadCombo;
                Check(Model.RespCombo.SetCaseList(combo.Name, ref type, name, factor), "RespCombo.SetCaseList", $"{combo.Name} / {name}");
            }
        // run flags as Mcotis has them
        foreach (var item in x.Cases) Soft(Model.Analyze.SetRunCaseFlag(item.Name, item.Run), $"run flag of {item.Name}");
        lines.Add($"Analysis: {addedCases} load case(s) added, {x.Cases.Count - addedCases} updated; {addedCombos} combination(s) added, {rebuiltCombos} rebuilt; " +
                  $"{x.Cases.Count(item => item.Run)} case(s) set to run.");

        Check(Model.View.RefreshView(0, false), "View.RefreshView");
        Verify(expected, true);
        foreach (var warning in warnings) lines.Add("Note: " + warning);
        return new SapExportReport(expected.Path, lines);
    });

    private void WriteFrameSection(FrameSectionProperty s)
    {
        var color = Bgr(s.Color);
        var code = s.Shape switch
        {
            FrameShape.Rectangle => Model.PropFrame.SetRectangle(s.Name, s.Material, s.Depth, s.Width, color, s.Notes),
            FrameShape.Box => Model.PropFrame.SetTube_1(s.Name, s.Material, s.Depth, s.Width, s.FlangeThickness, s.WebThickness, s.CornerRadius, color, s.Notes),
            FrameShape.I => Model.PropFrame.SetISection_1(s.Name, s.Material, s.Depth, s.Width, s.FlangeThickness, s.WebThickness,
                s.BottomWidth > 0 ? s.BottomWidth : s.Width, s.BottomFlangeThickness > 0 ? s.BottomFlangeThickness : s.FlangeThickness, 0, color, s.Notes),
            FrameShape.Pipe => Model.PropFrame.SetPipe(s.Name, s.Material, s.Depth, s.WebThickness, color, s.Notes),
            FrameShape.Circle => Model.PropFrame.SetCircle(s.Name, s.Material, s.Depth, color, s.Notes),
            FrameShape.Channel => Model.PropFrame.SetChannel_2(s.Name, s.Material, s.Depth, s.Width, s.FlangeThickness, s.WebThickness, 0, false, color, s.Notes),
            FrameShape.Tee => Model.PropFrame.SetTee_1(s.Name, s.Material, s.Depth, s.Width, s.FlangeThickness, s.WebThickness, 0, false, color, s.Notes),
            FrameShape.Angle => Model.PropFrame.SetAngle_1(s.Name, s.Material, s.Depth, s.Width, s.FlangeThickness, s.WebThickness, 0, color, s.Notes),
            _ => WriteGeneralSection(s, color),
        };
        Check(code, "PropFrame.Set" + s.Shape, s.Name);
    }

    /// <summary>A section SAP2000 gets by its properties (general, filled tube, steel encased): A, shear areas, J, I22, I33;
    /// elastic / plastic moduli and radii of gyration from the outline (or the equivalent rectangle when there is none).</summary>
    private int WriteGeneralSection(FrameSectionProperty s, int color)
    {
        var v = s.Values();
        if (!(v.Area > 0) || !(v.I33 > 0) || !(v.I22 > 0)) throw new InvalidOperationException($"Section '{s.Name}' has no area or inertia to export.");
        var t3 = s.Depth > 0 ? s.Depth : Math.Sqrt(12 * v.I33 / v.Area);
        var t2 = s.Width > 0 ? s.Width : Math.Sqrt(12 * v.I22 / v.Area);
        double s33 = v.I33 / (t3 / 2), s22 = v.I22 / (t2 / 2);
        var as2 = v.ShearArea2 > 0 ? v.ShearArea2 : 5.0 / 6 * v.Area;
        var as3 = v.ShearArea3 > 0 ? v.ShearArea3 : 5.0 / 6 * v.Area;
        var torsion = v.Torsion > 0 ? v.Torsion : v.I22 + v.I33;
        return Model.PropFrame.SetGeneral(s.Name, s.Material, t3, t2, v.Area, as2, as3, torsion, v.I22, v.I33, s22, s33, 1.5 * s22, 1.5 * s33,
            Math.Sqrt(v.I22 / v.Area), Math.Sqrt(v.I33 / v.Area), color, s.Notes);
    }

    private static bool Near(Position a, Position b, double tolerance) =>
        Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance && Math.Abs(a.Z - b.Z) <= tolerance;

    /// <summary>0xRRGGBB → SAP2000's COLORREF 0x00BBGGRR.</summary>
    private static int Bgr(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static eMatType MaterialType(MaterialKind kind) => kind switch
    {
        MaterialKind.Steel => eMatType.Steel, MaterialKind.Concrete => eMatType.Concrete, MaterialKind.Aluminum => eMatType.Aluminum,
        MaterialKind.ColdFormed => eMatType.ColdFormed, MaterialKind.Rebar => eMatType.Rebar, MaterialKind.Tendon => eMatType.Tendon,
        MaterialKind.Masonry => eMatType.Masonry, _ => eMatType.NoDesign,
    };

    private static eLoadPatternType PatternType(string type) =>
        Enum.TryParse<eLoadPatternType>(type.Replace(" ", ""), ignoreCase: true, out var parsed) ? parsed : eLoadPatternType.Other;
}
