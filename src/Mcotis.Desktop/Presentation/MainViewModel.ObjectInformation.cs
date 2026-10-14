using System.Globalization;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Presentation;

/// <summary>The assignments of a member that its information form lets the user change in place.</summary>
public enum InformationEdit { None, Section, LocalAxisAngle, ReleaseStart, ReleaseEnd, TargetPattern, TargetForce }

/// <summary>One name / value line of an object's information. <paramref name="Edit"/> marks a line the form shows as an
/// editor while the model can be edited.</summary>
public sealed record InformationRow(string Name, string Value, InformationEdit Edit = InformationEdit.None);

/// <summary>A titled group of lines (Identification, End I, Section …).</summary>
public sealed record InformationSection(string Title, IReadOnlyList<InformationRow> Rows);

/// <summary>One tab of the information form: Geometry, Assignments or Loads.</summary>
public sealed record InformationTab(string Title, IReadOnlyList<InformationSection> Sections);

// ---- Frame / Cable Information: what a right-click on a member of the model view shows, as ETABS's object information ----
// Everything is taken from the model Mcotis holds (geometry, FEM assignments, 03 Pretension, loading). The lines marked
// with an InformationEdit are the assignments the form changes in place (ObjectInformationWindow.Apply).
public sealed partial class MainViewModel
{
    /// <summary>The information of <paramref name="member"/>: Geometry (identification, length, end joints), Assignments
    /// (section, analysis properties, modifiers, local axis and releases or the cable's pretension) and Loads (self-weight,
    /// member loads and the cable target force, by load pattern). <paramref name="shown"/> is the geometry the member was
    /// picked in; the model's own when omitted.</summary>
    internal IReadOnlyList<InformationTab> MemberInformation(StructuralMember member, GeometryModel? shown = null)
    {
        var culture = CultureInfo.CurrentCulture;
        string Number(double value, string format = "0.###") => double.IsFinite(value) ? value.ToString(format, culture) : "—";
        static InformationRow Row(string name, string value, InformationEdit edit = InformationEdit.None) => new(name, value, edit);
        var cable = member.Kind != MemberKind.Frame;
        var geometry = shown ?? State.Geometry;   // the model the view shows (a tab may preview another geometry)
        var property = FemSectionProperties.FirstOrDefault(item => item.MemberName == member.Name);

        // ---- Geometry ----
        var identification = new List<InformationRow> { Row("Label", member.Name), Row("Object type", member.Kind.ToString()) };
        try
        {
            if (!cable && VcfGeometry.Analyze(geometry, State.Truss).Roles.TryGetValue(member.Name, out var role))
                identification.Add(Row("Role in the VCF system", VcfGeometry.Title(role)));
        }
        catch (InvalidOperationException) { /* not a VCF system: no role to show */ }
        var sections = new List<InformationSection> { new("Identification", identification) };
        double? length = null;
        if (geometry.Points.TryGetValue(member.Start, out var start) && geometry.Points.TryGetValue(member.End, out var end))
        {
            var (dx, dy, dz) = (end.Position.X - start.Position.X, end.Position.Y - start.Position.Y, end.Position.Z - start.Position.Z);
            length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var slope = Math.Atan2(Math.Abs(dz), Math.Sqrt(dx * dx + dy * dy)) * 180 / Math.PI;
            sections.Add(new("Geometry",
            [
                Row("Line type", "Straight"), Row("Length", $"{Number(length.Value)} m"), Row("Slope from horizontal", $"{Number(slope, "0.##")}°"),
                Row("ΔX / ΔY / ΔZ", $"{Number(dx)} / {Number(dy)} / {Number(dz)} m"),
            ]));
            foreach (var (title, point) in new[] { ("End I", start), ("End J", end) })
                sections.Add(new(title,
                [
                    Row("Point", point.Name), Row("X", $"{Number(point.Position.X)} m"), Row("Y", $"{Number(point.Position.Y)} m"), Row("Z", $"{Number(point.Position.Z)} m"),
                ]));
        }
        else sections.Add(new("End joints", [Row("I → J", $"{member.Start} → {member.End}"), Row("Coordinates", "not in the model geometry")]));
        var tabs = new List<InformationTab> { new("Geometry", sections) };

        // ---- Assignments ----
        var assignments = new List<InformationSection>();
        var target = cable ? State.Cables.FirstOrDefault(item => item.CableName == member.Name) : null;
        if (property is null)
            assignments.Add(new("Section", [Row("Section property", "none — File → Read FEM Properties from SAP, or Assign → Frame / Cable Sections")]));
        else
        {
            var section = new List<InformationRow> { Row("Section property", property.SectionName ?? "—", InformationEdit.Section) };
            // the real section where the model has it or its name and properties give it; else the shape the properties stand for
            if (!cable && property.SectionName is { } sectionName && SectionDrawingOf(sectionName) is { } drawing)
                section.Add(Row("Shape", drawing.Description + (drawing.Recovered ? " — from the section name, checked against A, I33 and I22" : "")));
            else if (property.Shape is { } shape) section.Add(Row("Shape", ShapeText(shape, Number)));
            section.Add(Row("Material", property.MaterialName ?? "—"));
            assignments.Add(new("Section", section));

            var analysis = new List<InformationRow> { Row("Modulus of elasticity E", $"{Number(property.ElasticModulusGPa)} GPa"), Row("Area A", $"{Number(property.AreaM2, "0.######")} m²") };
            if (property.GrossAreaM2 > 0 && Math.Abs(property.GrossAreaM2 - property.AreaM2) > 1e-12)
                analysis.Add(Row("Gross area (self-weight)", $"{Number(property.GrossAreaM2, "0.######")} m²"));
            if (property.InPlaneInertiaM4 is { } inertia) analysis.Add(Row("In-plane inertia I", $"{Number(inertia, "0.########")} m⁴"));
            analysis.Add(Row("Unit weight γ", $"{Number(property.UnitWeightKNPerM3)} kN/m³"));
            analysis.Add(Row("Self-weight per length", $"{Number(property.WeightPerLengthKNPerM)} kN/m"));
            analysis.Add(Row("Mass per length", $"{Number(property.MassPerLengthTPerM, "0.####")} t/m"));
            assignments.Add(new("Analysis properties", analysis));

            var modifiers = new List<InformationRow> { Row("Area", Number(property.AreaModifier)) };
            if (property.InertiaModifierOrNull is { } inertiaModifier) modifiers.Add(Row("Moment of inertia (I33)", Number(inertiaModifier)));
            modifiers.Add(Row("Mass", Number(property.MassModifier)));
            modifiers.Add(Row("Weight", Number(property.WeightModifier)));
            assignments.Add(new("Property modifiers", modifiers));

            if (!cable)
                assignments.Add(new("Local axes and releases",
                [
                    Row("Local-axis angle", $"{Number(property.LocalAngleDeg)}° about local 1", InformationEdit.LocalAxisAngle),
                    Row("Moment release at End I", property.ReleaseStartRotation ? "Released" : "Continuous", InformationEdit.ReleaseStart),
                    Row("Moment release at End J", property.ReleaseEndRotation ? "Released" : "Continuous", InformationEdit.ReleaseEnd),
                ]));
        }
        if (cable)
            assignments.Add(new("Pretension (03 Pretension)", target is null
                ? [Row("Target force", "none — add this cable on 03 Pretension")]
                :
                [
                    Row("Target-force load pattern", target.LoadPattern, InformationEdit.TargetPattern),
                    Row("Target force", TargetText(target, Number), InformationEdit.TargetForce), Row("Force sync", target.SyncSummary),
                    Row("Design capacity", target.DesignCapacityKN is { } capacity ? $"{Number(capacity, "N0")} kN" : "—"),
                ]));
        tabs.Add(new("Assignments", assignments));

        // ---- Loads ----
        var loads = new List<InformationSection>();
        if (property is { WeightPerLengthKNPerM: > 0 } weighed)
        {
            var selfWeight = FemPatternRows.Where(pattern => Math.Abs(pattern.SelfWeightMultiplier) > 1e-12)
                .Select(pattern => Row(pattern.Name, $"× {Number(pattern.SelfWeightMultiplier)} = {Number(weighed.WeightPerLengthKNPerM * pattern.SelfWeightMultiplier)} kN/m, gravity")).ToArray();
            if (selfWeight.Length > 0) loads.Add(new("Self-weight", selfWeight));
        }
        var distributed = femSapLoading.MemberLoads.Where(load => load.Load.MemberName == member.Name).Select(load => Row(load.Pattern,
            $"{Direction(load.Load.Direction)}: {Number(load.Load.ValueStart)}" + (Math.Abs(load.Load.ValueEnd - load.Load.ValueStart) > 1e-12 ? $" … {Number(load.Load.ValueEnd)}" : "") +
            $" kN/m over {Number(load.Load.RelStart)} … {Number(load.Load.RelEnd)} of the length")).ToArray();
        if (distributed.Length > 0) loads.Add(new("Distributed loads", distributed));
        var concentrated = femSapLoading.MemberPointLoads.Where(load => load.Load.MemberName == member.Name).Select(load => Row(load.Pattern,
            $"{Direction(load.Load.Direction)}: {Number(load.Load.ValueKN)} kN at {Number(load.Load.Rel)} of the length")).ToArray();
        if (concentrated.Length > 0) loads.Add(new("Point loads", concentrated));
        var gravity = femSapLoading.GravityLoads.Where(load => load.MemberName == member.Name).Select(load => Row(load.Pattern,
            $"self-weight × {Number(load.MultiplierX)} along X, × {Number(load.MultiplierZ)} along Z")).ToArray();
        if (gravity.Length > 0) loads.Add(new("Gravity loads", gravity));
        if (target is not null) loads.Add(new("Target force", [Row(target.LoadPattern, TargetText(target, Number))]));
        if (loads.Count == 0) loads.Add(new("Loads", [Row("Assigned loads", "none")]));
        tabs.Add(new("Loads", loads));
        return tabs;
    }

    /// <summary>The information of <paramref name="point"/>: Geometry (identification, coordinates, the frames and cables
    /// that meet at it), Assignments (its support) and Loads (the storey forces and joint loads on it, by load pattern).
    /// <paramref name="shown"/> is the geometry the point was picked in; the model's own when omitted.</summary>
    internal IReadOnlyList<InformationTab> PointInformation(StructuralPoint point, GeometryModel? shown = null)
    {
        var culture = CultureInfo.CurrentCulture;
        string Number(double value, string format = "0.###") => double.IsFinite(value) ? value.ToString(format, culture) : "—";
        static InformationRow Row(string name, string value) => new(name, value);
        var geometry = shown ?? State.Geometry;

        // ---- Geometry ----
        var identification = new List<InformationRow> { Row("Label", point.Name), Row("Object type", "Point") };
        var chord = State.Truss.TopPoints.Contains(point.Name) ? "Top chord point"
            : State.Truss.MiddlePoints.Contains(point.Name) ? "Middle chord point"
            : State.Truss.BottomPoints.Contains(point.Name) ? "Bottom chord point" : null;
        if (chord is not null) identification.Add(Row("Role in the VCF system", chord));
        var meeting = geometry.Members.Where(member => member.Start == point.Name || member.End == point.Name).ToArray();
        string Names(IEnumerable<StructuralMember> members) => string.Join(", ", members.Select(member => member.Name)) is { Length: > 0 } names ? names : "none";
        var tabs = new List<InformationTab>
        {
            new("Geometry",
            [
                new("Identification", identification),
                new("Coordinates", [Row("X", $"{Number(point.Position.X)} m"), Row("Y", $"{Number(point.Position.Y)} m"), Row("Z", $"{Number(point.Position.Z)} m")]),
                new("Connectivity",
                [
                    Row("Frames", Names(meeting.Where(member => member.Kind == MemberKind.Frame))),
                    Row("Cables", Names(meeting.Where(member => member.Kind != MemberKind.Frame))),
                ]),
            ]),
            // ---- Assignments ----
            new("Assignments",
            [
                new("Restraint", [Row("Support", !FemJointRestraints.TryGetValue(point.Name, out var restraint) ? "None — free"
                    : restraint == Mcotis.Core.Restraint.Pinned ? "Pin — translations restrained, rotations free"
                    : restraint == Mcotis.Core.Restraint.Fixed ? "Fixed — translations and rotations restrained"
                    : $"Restrained: {RestraintText(restraint)}")]),
                new("Mass", [Row("Additional mass", !FemJointMasses.TryGetValue(point.Name, out var mass) ? "None"
                    : $"UX {Number(mass.UX)}, UY {Number(mass.UY)}, UZ {Number(mass.UZ)} t; RX {Number(mass.RX)}, RY {Number(mass.RY)}, RZ {Number(mass.RZ)} t·m²")]),
            ]),
        };

        // ---- Loads ----
        var loads = new List<InformationSection>();
        if (State.AppliedPoints.Any(applied => applied.PointName == point.Name))
        {
            // the forces the model is analysed with: a range on 01 Loading enters with its first value
            try
            {
                if (LoadingCandidates.Enumerate(State.LoadDefinitions.ToArray()).FirstOrDefault() is { } loading)
                {
                    var storey = StudyCompositionService.BuildPointLoads(State, loading).Where(row => row.AppliedPt == point.Name)
                        .Select(row => Row(row.Case, $"Fz {Number(row.FzKn)} kN = −{Number(row.Qz)} kPa × {Number(row.TribArea)} m² × {row.NStories} storey(s)")).ToArray();
                    if (storey.Length > 0) loads.Add(new("Storey force", storey));
                }
            }
            catch (InvalidOperationException ex) { loads.Add(new("Storey force", [Row("Loaded point", "its forces cannot be worked out: " + ex.Message)])); }
        }
        var joint = femSapLoading.JointLoads.Where(load => load.Load.PointName == point.Name).Select(load => Row(load.Pattern,
            $"Fx {Number(load.Load.FxKN)} kN, Fz {Number(load.Load.FzKN)} kN" + (Math.Abs(load.Load.MyKNm) > 1e-12 ? $", My {Number(load.Load.MyKNm)} kN·m" : ""))).ToArray();
        if (joint.Length > 0) loads.Add(new("Joint loads", joint));
        if (loads.Count == 0) loads.Add(new("Loads", [Row("Assigned loads", "none")]));
        tabs.Add(new("Loads", loads));
        return tabs;
    }

    private static string Direction(FemLoadDirection direction) => direction switch
    {
        FemLoadDirection.Local1 => "Local 1",
        FemLoadDirection.Local2 => "Local 2",
        FemLoadDirection.GlobalX => "Global X",
        FemLoadDirection.GlobalZ => "Global Z",
        FemLoadDirection.ProjectedX => "Global X, projected",
        FemLoadDirection.ProjectedZ => "Global Z, projected",
        FemLoadDirection.ProjectedGravity => "Gravity, projected",
        _ => "Gravity",
    };

    /// <summary>A section outline in words: its kind and dimensions [mm]. An equivalent solid is named as one.</summary>
    private static string ShapeText(FemSectionShape shape, Func<double, string, string> number)
    {
        string Mm(double metres) => number(metres * 1000, "0.#");
        var plates = shape.FlangeThickness > 0 || shape.WebThickness > 0 ? $", tf {Mm(shape.FlangeThickness)}, tw {Mm(shape.WebThickness)}" : "";
        return shape.Kind switch
        {
            SectionShapeKind.Equivalent => $"Equivalent solid {Mm(shape.Depth)} × {Mm(shape.Width)} mm (same A, I33, I22 — no dimensions read)",
            SectionShapeKind.Circle => $"Circle Ø {Mm(shape.Depth)} mm",
            SectionShapeKind.Pipe => $"Pipe Ø {Mm(shape.Depth)} mm, t {Mm(shape.WebThickness)}",
            _ => $"{shape.Kind} {Mm(shape.Depth)} × {Mm(shape.Width)} mm{plates}",
        };
    }

    /// <summary>A cable's 03 Pretension force: the target, or its range.</summary>
    private static string TargetText(CablePretensionConfig cable, Func<double, string, string> number) =>
        cable.ApplyRange && cable.MinForce is { } min && cable.MaxForce is { } max
            ? $"{number(min, "N0")} … {number(max, "N0")} kN" + (cable.StepForce is { } step ? $" @ {number(step, "N0")}" : "")
        : cable.TargetForce is { } force ? $"{number(force, "N0")} kN" : "—";
}
