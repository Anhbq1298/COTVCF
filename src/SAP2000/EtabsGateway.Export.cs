using COT_VCF.Application;
using COT_VCF.Application.Etabs;
using COT_VCF.Domain;
using COT_VCF.Geometry;
using CSiAPIv1;

namespace COT_VCF.SAP2000;

/// <summary>
/// File → Export to ETABS: the Mcotis model written into the model open in ETABS, ready to run there. Calls checked
/// against CSiAPIv1 2.15 and tried on ETABS 23.2 (a private hidden instance, never the user's): Story.GetStories_2 /
/// SetStories_2 (accepted only while the model has no objects), FrameObj.AddByCoord (any elevation — ETABS attaches a
/// joint between levels to a story itself; the user name given is kept), Delete, SetSection, SetLocalAxes, SetReleases,
/// SetModifiers, SetTCLimits, SetLoadDistributed / SetLoadPoint / DeleteLoad…; PointObj.GetCoordCartesian, SetRestraint,
/// GetLoadForce / SetLoadForce / DeleteLoadForce, DeleteSpecialPoint; PropMaterial.SetMaterial / SetMPIsotropic /
/// SetMPUniaxial / SetWeightAndMass / SetO…; PropFrame.SetRectangle / SetTube_1 / SetISection_1 / SetPipe / SetCircle /
/// SetGeneral / SetModifiers / GetSectProps; LoadPatterns, LoadCases.StaticLinear / StaticNonlinear, RespCombo,
/// Analyze.SetActiveDOF / SetRunCaseFlag.
/// <para>ETABS names its objects itself, so Mcotis members are matched to ETABS frames by their end coordinates (1 mm):
/// a frame with the same ends (same I → J) is kept and reassigned, any other ETABS frame is removed, and a member without
/// a match is added. An empty model first gets one story per Mcotis joint level. ETABS has no cable objects and no API for
/// filled-tube / encased sections: cables become tension-only frames pinned at both ends (their Tab 03 target forces are
/// not exported), and composite / Section Designer sections Mcotis created are written as general sections with their
/// transformed properties and the weight / mass modifiers that keep their true self-weight; sections ETABS already has
/// are kept as ETABS defines them. The analysis is set to the XZ plane (UX, UZ, RY active), as Mcotis analyses it.
/// Everything is checked before the first write; the model is not saved.</para>
/// </summary>
public sealed partial class EtabsGateway
{
    private const double MatchTolerance = 0.001;
    private const int NonlinearPDeltaLargeDisplacement = 2;

    public Task<EtabsExportReport> PreviewExportAsync(EtabsExportModel model) =>
        worker.Run(() => WithModel(etabs => Export(etabs, model, null, unlock: false, preview: true)));

    public Task<EtabsExportReport> ExportModelAsync(EtabsExportModel model, EtabsExportTarget expected, bool unlock) =>
        worker.Run(() => WithModel(etabs => Export(etabs, model, expected, unlock, preview: false)));

    private sealed record Frame(string Name, string Start, string End, Position StartAt, Position EndAt);

    private static EtabsExportReport Export(cSapModel m, EtabsExportModel export, EtabsExportTarget? expected, bool unlock, bool preview)
    {
        var x = export.Base;
        if (!m.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Export writes kN, m values; ETABS is set to {m.GetPresentUnits()}. Set ETABS to kN, m, C and export again.");
        var lines = new List<string>();
        var notes = new List<string>();
        void Soft(int code, string what) { if (code != 0) notes.Add($"{what} (ETABS code {code})"); }

        // ---------------- read the model and check the export against it, before any write ----------------
        var points = ReadPoints(m);
        var frames = new List<Frame>();
        int n = 0; string[] names = [];
        Check(m.FrameObj.GetNameList(ref n, ref names), "FrameObj.GetNameList");
        foreach (var name in names.Take(n))
        {
            string start = "", end = "";
            Check(m.FrameObj.GetPoints(name, ref start, ref end), "FrameObj.GetPoints", name);
            frames.Add(new(name, start, end, points[start], points[end]));
        }
        var (path, version) = Identity(m);
        var target = new EtabsExportTarget(path, version, points.Count, frames.Count, m.GetModelIsLocked());
        if (expected is not null && (target.Path != expected.Path || target.Points != expected.Points || target.Frames != expected.Frames))
            throw new InvalidOperationException("The model open in ETABS is not the one the export was previewed against. Export again.");
        HashSet<string> NameSet(Func<(int, string[])> read) { var (count, list) = read(); return list.Take(count).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        var etabsMaterials = NameSet(() => { int c = 0; string[] l = []; Check(m.PropMaterial.GetNameList(ref c, ref l), "PropMaterial.GetNameList"); return (c, l); });
        var etabsSections = NameSet(() => { int c = 0; string[] l = []; Check(m.PropFrame.GetNameList(ref c, ref l, 0), "PropFrame.GetNameList"); return (c, l); });
        var etabsPatterns = NameSet(() => { int c = 0; string[] l = []; Check(m.LoadPatterns.GetNameList(ref c, ref l), "LoadPatterns.GetNameList"); return (c, l); });
        var etabsCases = NameSet(() => { int c = 0; string[] l = []; Check(m.LoadCases.GetNameList(ref c, ref l), "LoadCases.GetNameList"); return (c, l); });
        var etabsCombos = NameSet(() => { int c = 0; string[] l = []; Check(m.RespCombo.GetNameList(ref c, ref l), "RespCombo.GetNameList"); return (c, l); });
        double baseElevation = 0; int storyCount = 0; string[] storyNames = []; double[] storyElevations = [], heights = [], spliceHeights = [];
        bool[] master = [], splice = []; string[] similar = []; int[] colors = [];
        Check(m.Story.GetStories_2(ref baseElevation, ref storyCount, ref storyNames, ref storyElevations, ref heights, ref master, ref similar,
            ref splice, ref spliceHeights, ref colors), "Story.GetStories_2");

        var geometry = x.Geometry;
        var members = geometry.Members.Where(member => member.Kind is MemberKind.Frame or MemberKind.Cable).ToArray();
        var properties = x.Members.ToDictionary(member => member.MemberName, StringComparer.Ordinal);
        var frameLibrary = x.FrameSections.ToDictionary(section => section.Name, StringComparer.OrdinalIgnoreCase);
        var cableLibrary = x.CableSections.ToDictionary(section => section.Name, StringComparer.OrdinalIgnoreCase);
        var materials = etabsMaterials.Concat(x.Materials.Select(material => material.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ys = geometry.Points.Values.Select(point => point.Position.Y).ToArray();
        if (ys.Length > 0 && ys.Max() - ys.Min() > MatchTolerance)
            throw new InvalidOperationException("The Mcotis model is not in one XZ plane.");
        foreach (var member in members)
        {
            if (!geometry.Points.ContainsKey(member.Start) || !geometry.Points.ContainsKey(member.End))
                throw new InvalidOperationException($"{member.Kind} {member.Name} has an end joint that is not in the model.");
            if (!properties.TryGetValue(member.Name, out var property) || string.IsNullOrWhiteSpace(property.SectionName))
                throw new InvalidOperationException($"{member.Kind} {member.Name} has no section to export.");
            var known = (member.Kind == MemberKind.Frame ? frameLibrary.ContainsKey(property.SectionName) : cableLibrary.ContainsKey(property.SectionName)) ||
                        etabsSections.Contains(property.SectionName);
            if (!known) throw new InvalidOperationException($"Section '{property.SectionName}' of {member.Name} is neither in the Mcotis library nor in ETABS.");
        }
        var usedSections = members.Select(member => properties[member.Name].SectionName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newFrameSections = x.FrameSections.Where(section => usedSections.Contains(section.Name) && !etabsSections.Contains(section.Name)).ToArray();
        var newCableSections = x.CableSections.Where(section => usedSections.Contains(section.Name) && !etabsSections.Contains(section.Name)).ToArray();
        foreach (var material in newFrameSections.SelectMany(section => new[] { section.Material }).Concat(newCableSections.Select(section => section.Material)))
            if (!materials.Contains(material)) throw new InvalidOperationException($"Material '{material}' is not defined in Mcotis or ETABS.");
        var patterns = etabsPatterns.Concat(x.Patterns.Select(pattern => pattern.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in x.Cases)
            if (item.Loads.FirstOrDefault(load => !patterns.Contains(load.Pattern)) is { Pattern: { } missing })
                throw new InvalidOperationException($"Load case '{item.Name}' uses load pattern '{missing}', which does not exist.");
        var caseNames = etabsCases.Concat(x.Cases.Select(item => item.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var comboNames = etabsCombos.Concat(x.Combinations.Select(item => item.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var combo in x.Combinations)
            if (combo.Items.FirstOrDefault(item => !caseNames.Contains(item.Name) && !comboNames.Contains(item.Name)) is { Name: { } missing })
                throw new InvalidOperationException($"Combination '{combo.Name}' uses '{missing}', which is neither a load case nor a combination.");

        // matching by end coordinates; a frame met twice keeps one match
        (long, long, long) Key(Position p) => ((long)Math.Round(p.X / MatchTolerance), (long)Math.Round(p.Y / MatchTolerance), (long)Math.Round(p.Z / MatchTolerance));
        var byEnds = frames.GroupBy(frame => (Key(frame.StartAt), Key(frame.EndAt))).ToDictionary(group => group.Key, group => group.ToList());
        // ETABS turns a beam to run towards +X, so a frame between the same joints the other way round matches too
        var matched = new Dictionary<string, string>(StringComparer.Ordinal);   // Mcotis member → ETABS frame
        var reversed = new HashSet<string>(StringComparer.Ordinal);            // Mcotis members whose ETABS frame runs J → I
        foreach (var member in members)
        {
            var (start, end) = (Key(geometry.Points[member.Start].Position), Key(geometry.Points[member.End].Position));
            if (byEnds.TryGetValue((start, end), out var same) && same.Count > 0) { matched[member.Name] = same[0].Name; same.RemoveAt(0); }
            else if (byEnds.TryGetValue((end, start), out var other) && other.Count > 0) { matched[member.Name] = other[0].Name; other.RemoveAt(0); reversed.Add(member.Name); }
        }
        var keptFrames = matched.Values.ToHashSet(StringComparer.Ordinal);
        var removed = frames.Where(frame => !keptFrames.Contains(frame.Name)).ToArray();
        var added = members.Where(member => !matched.ContainsKey(member.Name)).ToArray();
        var keptPoints = frames.Where(frame => keptFrames.Contains(frame.Name)).SelectMany(frame => new[] { frame.Start, frame.End }).ToHashSet(StringComparer.Ordinal);
        var wanted = geometry.Points.Values.Select(point => Key(point.Position)).ToHashSet();
        var orphans = removed.SelectMany(frame => new[] { frame.Start, frame.End }).Distinct(StringComparer.Ordinal)
            .Where(point => !keptPoints.Contains(point) && !wanted.Contains(Key(points[point]))).ToArray();

        // stories: an empty model gets one per Mcotis joint level
        var levels = geometry.Points.Values.Select(point => Math.Round(point.Position.Z, 3)).Distinct().OrderBy(z => z).ToArray();
        var storyLevels = storyElevations.Take(storyCount).Append(baseElevation).ToArray();
        var offLevel = levels.Count(z => !storyLevels.Any(level => Math.Abs(level - z) <= MatchTolerance));
        var setStories = points.Count == 0 && frames.Count == 0 && offLevel > 0 && levels.Length > 0;

        // sections: those ETABS has are kept, unless a standard shape Mcotis holds differs; general ones get weight / mass modifiers
        var updated = x.FrameSections.Where(section => usedSections.Contains(section.Name) && etabsSections.Contains(section.Name) &&
            Standard(section) && !SameProperties(m, section)).ToArray();
        var cables = members.Where(member => member.Kind == MemberKind.Cable).ToArray();
        var targets = x.CableTargets.Count;

        if (preview)
        {
            int Missing(IEnumerable<string> wantedNames, HashSet<string> have) => wantedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count(name => !have.Contains(name));
            lines.Add($"Target: {target.Name} ({version}, kN-m{(target.IsLocked ? ", locked — ETABS deletes its analysis results when it is unlocked" : "")})");
            lines.Add(setStories
                ? $"Stories: the model is empty — {levels.Length - 1} stories at the Mcotis joint levels ({string.Join(", ", levels.Skip(1).Take(6).Select(z => $"EL{z:0.###}"))}{(levels.Length > 7 ? ", …" : "")}) above a base at {levels[0]:0.###} m " +
                  "(if the model ever had objects, ETABS keeps its stories and attaches the joints to them)"
                : offLevel > 0 ? $"Stories: kept; {offLevel} joint level(s) are not at a story (ETABS attaches those joints to a story)" : "Stories: kept (every joint level is a story)");
            lines.Add($"Geometry: {matched.Count} member(s) match ETABS frames by their ends; remove {removed.Length} frame(s)" +
                      (removed.Length > 0 ? " (" + string.Join(", ", removed.Take(8).Select(frame => Label(m, frame.Name))) + (removed.Length > 8 ? ", …" : "") + ")" : "") +
                      $", add {added.Length}" +
                      (orphans.Length > 0 ? $"; remove {orphans.Length} joint(s) left unconnected" : ""));
            var needed = newFrameSections.Concat(updated).Select(section => section.Material).Concat(newCableSections.Select(section => section.Material)).ToArray();
            lines.Add($"Libraries: add {Missing(x.Materials.Select(material => material.Name).Where(name => needed.Contains(name, StringComparer.OrdinalIgnoreCase)), etabsMaterials)} material(s), " +
                      $"{newFrameSections.Length + newCableSections.Length} section(s); update {updated.Length}; ETABS's own definition kept for {usedSections.Count - newFrameSections.Length - newCableSections.Length - updated.Length}");
            lines.Add($"Loading: add {Missing(x.Patterns.Select(p => p.Name), etabsPatterns)} load pattern(s); {export.JointLoads.Count} joint load(s), " +
                      $"{export.MemberLoads.Count + export.MemberPointLoads.Count} frame load(s) — each pattern's loads on the exported members replaced");
            lines.Add($"Analysis: add {Missing(x.Cases.Select(c => c.Name), etabsCases)} load case(s), update {x.Cases.Count - Missing(x.Cases.Select(c => c.Name), etabsCases)}; " +
                      $"add {Missing(x.Combinations.Select(c => c.Name), etabsCombos)} combination(s); active DOF UX, UZ, RY (plane frame)");
            if (cables.Length > 0) lines.Add($"Cables: {cables.Length} as tension-only frames pinned at both ends; {targets} Tab 03 target force(s) are not exported (ETABS has no target-force load)");
            if (export.GravityLoadsLeftOut > 0) lines.Add($"Note: {export.GravityLoadsLeftOut} gravity load assignment(s) are not exported (ETABS has none on frames).");
            return new EtabsExportReport(target, lines);
        }

        // ---------------- write ----------------
        if (target.IsLocked)
        {
            if (!unlock) throw new InvalidOperationException("The ETABS model is locked. Unlock it (ETABS deletes its analysis results) to export.");
            Check(m.SetModelIsLocked(false), "SetModelIsLocked");
            lines.Add("Unlocked the model (ETABS deleted its analysis results).");
        }

        // stories first, before anything else is written. ETABS accepts new stories only for a model that has never had
        // objects (one emptied by deleting them is refused); then its stories are kept and ETABS attaches each joint to one.
        if (setStories)
        {
            var above = levels.Skip(1).ToList();
            if (above.Count == 0) above.Add(levels[0] + 3);
            var count = above.Count;
            string[] newNames = above.Select(z => $"EL{z:0.###}").ToArray();
            double[] newHeights = above.Select((z, i) => z - (i == 0 ? levels[0] : above[i - 1])).ToArray();
            bool[] newMaster = Enumerable.Repeat(true, count).ToArray(), newSplice = new bool[count];
            string[] newSimilar = Enumerable.Repeat("None", count).ToArray();
            double[] newSpliceHeights = new double[count];
            int[] newColors = new int[count];
            if (m.Story.SetStories_2(levels[0], count, ref newNames, ref newHeights, ref newMaster, ref newSimilar, ref newSplice, ref newSpliceHeights, ref newColors) == 0)
                lines.Add($"Stories: {count} at the Mcotis joint levels above a base at {levels[0]:0.###} m.");
            else
                notes.Add($"ETABS kept its {storyCount} stories: it sets new stories only for a model that has never had objects (this one had some, since deleted). " +
                          $"ETABS attaches the joints off its story levels ({offLevel} level(s)) to its stories. For stories at the Mcotis levels, export into a new blank ETABS model.");
        }

        // materials ETABS does not have
        var addedMaterials = 0;
        var neededMaterials = newFrameSections.Concat(updated).Select(section => section.Material).Concat(newCableSections.Select(section => section.Material))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var material in x.Materials.Where(material => neededMaterials.Contains(material.Name) && !etabsMaterials.Contains(material.Name)))
        {
            Check(m.PropMaterial.SetMaterial(material.Name, MaterialType(material.Kind), -1, material.Notes), "PropMaterial.SetMaterial", material.Name);
            if (material.IsUniaxial) Check(m.PropMaterial.SetMPUniaxial(material.Name, material.ElasticModulus, material.ThermalCoefficient), "PropMaterial.SetMPUniaxial", material.Name);
            else Check(m.PropMaterial.SetMPIsotropic(material.Name, material.ElasticModulus, material.PoissonRatio, material.ThermalCoefficient), "PropMaterial.SetMPIsotropic", material.Name);
            Check(m.PropMaterial.SetWeightAndMass(material.Name, 1, material.WeightPerVolume), "PropMaterial.SetWeightAndMass", material.Name);
            if (material.Kind == MaterialKind.Steel && material.Fy is > 0 and var fy)
            {
                var fu = material.Fu ?? fy * 1.25;
                Soft(m.PropMaterial.SetOSteel_1(material.Name, fy, fu, material.ExpectedFy ?? fy * 1.1, material.ExpectedFu ?? fu * 1.1, 1, 1, 0.02, 0.1, 0.2, -0.1), $"steel data of {material.Name}");
            }
            else if (material.Kind == MaterialKind.Concrete && material.Fc is > 0 and var fc)
                Soft(m.PropMaterial.SetOConcrete_1(material.Name, fc, material.IsLightweight, material.ShearStrengthReductionFactor ?? 0, 1, 2, 0.0022, 0.0052, -0.1), $"concrete data of {material.Name}");
            else if (material.Kind == MaterialKind.Rebar && material.Fy is > 0 and var fyr)
                Soft(m.PropMaterial.SetORebar_1(material.Name, fyr, material.Fu ?? fyr * 1.08, material.ExpectedFy ?? fyr * 1.1, material.ExpectedFu ?? fyr * 1.19, 1, 1, 0.02, 0.1, -0.1, false), $"rebar data of {material.Name}");
            else if (material.Kind == MaterialKind.Tendon && material.Fy is > 0 and var fyt)
                Soft(m.PropMaterial.SetOTendon_1(material.Name, fyt, material.Fu ?? fyt / 0.9, 1, 1, -0.1), $"tendon data of {material.Name}");
            addedMaterials++;
        }

        // sections
        int addedSections = 0, updatedSections = 0;
        foreach (var section in newFrameSections.Concat(updated))
        {
            var general = !Standard(section);
            if (general) WriteGeneralSection(m, section);
            else WriteStandardSection(m, section);
            var modifiers = section.Modifiers.Count >= 8 ? section.Modifiers.Take(8).ToArray() : [1.0, 1, 1, 1, 1, 1, 1, 1];
            // a general section weighs its material × its transformed area in ETABS: scale it to the true self-weight
            if (general && export.SectionWeights.TryGetValue(section.Name, out var weight) && MaterialWeight(x, section.Material) is { } unit && section.Values().Area > 0)
            {
                if (unit.Weight > 0) modifiers[7] *= weight.WeightPerLength / (unit.Weight * section.Values().Area);
                if (unit.Mass > 0) modifiers[6] *= weight.MassPerLength / (unit.Mass * section.Values().Area);
            }
            Check(m.PropFrame.SetModifiers(section.Name, ref modifiers), "PropFrame.SetModifiers", section.Name);
            if (etabsSections.Contains(section.Name)) updatedSections++; else addedSections++;
        }
        foreach (var section in newCableSections)
        {
            // a cable is a solid round bar of its area (bending is released at both ends)
            Check(m.PropFrame.SetCircle(section.Name, section.Material, section.Diameter), "PropFrame.SetCircle", section.Name);
            double[] modifiers = [section.Modifiers.ElementAtOrDefault(0) is > 0 and var a ? a : 1, 1, 1, 1, 1, 1,
                section.Modifiers.ElementAtOrDefault(1) is > 0 and var mm ? mm : 1, section.Modifiers.ElementAtOrDefault(2) is > 0 and var w ? w : 1];
            Check(m.PropFrame.SetModifiers(section.Name, ref modifiers), "PropFrame.SetModifiers", section.Name);
            addedSections++;
        }
        lines.Add($"Libraries: {addedMaterials} material(s) added; {addedSections} section(s) added, {updatedSections} updated.");

        // geometry: frames that go, the joints they leave unconnected, new frames
        foreach (var frame in removed) Check(m.FrameObj.Delete(frame.Name), "FrameObj.Delete", frame.Name);
        var leftOver = orphans.Count(point => m.PointObj.DeleteSpecialPoint(point) != 0);
        var etabsName = new Dictionary<string, string>(matched, StringComparer.Ordinal);
        var taken = matched.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var member in added)
        {
            var (a, b) = (geometry.Points[member.Start].Position, geometry.Points[member.End].Position);
            var name = "";
            var user = taken.Contains(member.Name) ? "" : member.Name;
            Check(m.FrameObj.AddByCoord(a.X, a.Y, a.Z, b.X, b.Y, b.Z, ref name, properties[member.Name].SectionName!, user, "Global"), "FrameObj.AddByCoord", member.Name);
            etabsName[member.Name] = name;
            // ETABS may store the new frame J → I (beams run towards +X)
            string i = "", j = "";
            Check(m.FrameObj.GetPoints(name, ref i, ref j), "FrameObj.GetPoints", member.Name);
            double xi = 0, yi = 0, zi = 0;
            Check(m.PointObj.GetCoordCartesian(i, ref xi, ref yi, ref zi, "Global"), "PointObj.GetCoordCartesian", i);
            if (Key(new Position(xi, yi, zi)) != Key(a)) reversed.Add(member.Name);
            taken.Add(name);
        }
        lines.Add($"Geometry: {removed.Length} frame(s) removed, {added.Length} added, {matched.Count} kept" +
                  (orphans.Length > 0 ? $"; {orphans.Length - leftOver} unconnected joint(s) removed" : "") + ".");
        if (leftOver > 0) notes.Add($"{leftOver} joint(s) not in Mcotis are still in ETABS (connected to other objects).");

        // assignments: section, local axis, in-plane moment releases and modifiers of every member; cables tension-only
        var reassigned = 0;
        foreach (var member in members)
        {
            var name = etabsName[member.Name];
            var property = properties[member.Name];
            string section = "", auto = "";
            Check(m.FrameObj.GetSection(name, ref section, ref auto), "FrameObj.GetSection", member.Name);
            if (!section.Equals(property.SectionName, StringComparison.OrdinalIgnoreCase))
            {
                Check(m.FrameObj.SetSection(name, property.SectionName!), "FrameObj.SetSection", member.Name);
                reassigned++;
            }
            Check(m.FrameObj.SetLocalAxes(name, member.Kind == MemberKind.Cable ? 0 : property.LocalAngleDeg), "FrameObj.SetLocalAxes", member.Name);
            double[] modifiers = member.Kind == MemberKind.Cable
                ? [Modifier(property, 0), 1, 1, 1, 1, 1, Modifier(property, 1), Modifier(property, 2)]
                : property.Modifiers is { Count: >= 8 } given ? given.Take(8).ToArray() : [1.0, 1, 1, 1, 1, 1, 1, 1];
            Check(m.FrameObj.SetModifiers(name, ref modifiers), "FrameObj.SetModifiers", member.Name);
            bool[] ii = new bool[6], jj = new bool[6];
            double[] si = new double[6], sj = new double[6];
            if (member.Kind == MemberKind.Cable)
            {
                ii[4] = ii[5] = jj[4] = jj[5] = true;
                Check(m.FrameObj.SetReleases(name, ref ii, ref jj, ref si, ref sj), "FrameObj.SetReleases", member.Name);
                Check(m.FrameObj.SetTCLimits(name, true, 0, false, 0, eItemType.Objects), "FrameObj.SetTCLimits", member.Name);
                continue;
            }
            var radians = property.LocalAngleDeg * Math.PI / 180;
            var index = Math.Abs(Math.Cos(radians)) >= Math.Abs(Math.Sin(radians)) ? 5 : 4;
            var flip = reversed.Contains(member.Name);
            ii[index] = flip ? property.ReleaseEndRotation : property.ReleaseStartRotation;
            jj[index] = flip ? property.ReleaseStartRotation : property.ReleaseEndRotation;
            Check(m.FrameObj.SetReleases(name, ref ii, ref jj, ref si, ref sj), "FrameObj.SetReleases", member.Name);
        }

        // ETABS joints by position, now that the geometry is final
        var etabsPoints = ReadPoints(m).GroupBy(point => Key(point.Value)).ToDictionary(group => group.Key, group => group.First().Key);
        string? PointAt(string joint) => geometry.Points.TryGetValue(joint, out var point) && etabsPoints.TryGetValue(Key(point.Position), out var name) ? name : null;

        // supports: a support joint without any restraint becomes a pin; the analysis runs in the XZ plane
        var pinned = 0;
        foreach (var support in x.PinSupports.Select(PointAt).OfType<string>())
        {
            bool[] restraint = new bool[6];
            Check(m.PointObj.GetRestraint(support, ref restraint), "PointObj.GetRestraint", support);
            if (restraint.Any(value => value)) continue;
            bool[] pin = [true, true, true, false, false, false];
            Check(m.PointObj.SetRestraint(support, ref pin), "PointObj.SetRestraint", support);
            pinned++;
        }
        bool[] plane = [true, false, true, false, true, false];
        Check(m.Analyze.SetActiveDOF(ref plane), "Analyze.SetActiveDOF");
        lines.Add($"Assignments: {members.Length} members (section, local axis, releases, modifiers; {reassigned} section change(s))" +
                  (cables.Length > 0 ? $", {cables.Length} cable(s) as tension-only frames" : "") + $"; {pinned} new pin support(s); active DOF UX, UZ, RY.");

        // load patterns
        var addedPatterns = 0;
        foreach (var pattern in x.Patterns)
        {
            if (!etabsPatterns.Contains(pattern.Name))
            {
                Check(m.LoadPatterns.Add(pattern.Name, PatternType(pattern.Type), pattern.SelfWeightMultiplier, false), "LoadPatterns.Add", pattern.Name);
                addedPatterns++;
            }
            else Check(m.LoadPatterns.SetSelfWTMultiplier(pattern.Name, pattern.SelfWeightMultiplier), "LoadPatterns.SetSelfWTMultiplier", pattern.Name);
        }
        var exported = x.Patterns.Select(pattern => pattern.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // joint loads: at every Mcotis joint, each exported pattern's force is Mcotis's (or none)
        var wantedLoads = export.JointLoads.Select(load => (Point: PointAt(load.Load.PointName), load.Pattern, load.Load))
            .Where(load => load.Point is not null)
            .GroupBy(load => (load.Point!, Pattern: load.Pattern.ToUpperInvariant()))
            .ToDictionary(group => group.Key, group => (Pattern: group.First().Pattern, Fx: group.Sum(item => item.Load.FxKN), Fz: group.Sum(item => item.Load.FzKN),
                My: group.Sum(item => item.Load.MyKNm)));
        int jointLoads = 0, clearedLoads = 0;
        foreach (var point in geometry.Points.Keys.Select(PointAt).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            int count = 0; string[] pointNames = [], loadPatterns = [], csys = []; int[] steps = [];
            double[] f1 = [], f2 = [], f3 = [], m1 = [], m2 = [], m3 = [];
            var existing = m.PointObj.GetLoadForce(point, ref count, ref pointNames, ref loadPatterns, ref steps, ref csys, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3) == 0
                ? loadPatterns.Take(count).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            foreach (var pattern in existing.Where(exported.Contains).Where(pattern => !wantedLoads.ContainsKey((point, pattern.ToUpperInvariant()))))
            {
                Check(m.PointObj.DeleteLoadForce(point, pattern), "PointObj.DeleteLoadForce", $"{point} / {pattern}");
                clearedLoads++;
            }
        }
        foreach (var ((point, _), load) in wantedLoads)
        {
            double[] values = [load.Fx, 0, load.Fz, 0, load.My, 0];
            Check(m.PointObj.SetLoadForce(point, load.Pattern, ref values, true, "Global"), "PointObj.SetLoadForce", $"{point} / {load.Pattern}");
            jointLoads++;
        }
        // frame loads: each exported pattern's loads on an exported member are Mcotis's
        var memberLoads = 0;
        foreach (var member in members)
        {
            var name = etabsName[member.Name];
            foreach (var pattern in exported)
            {
                m.FrameObj.DeleteLoadDistributed(name, pattern);
                m.FrameObj.DeleteLoadPoint(name, pattern);
            }
        }
        // on a frame ETABS runs J → I, positions count from the other end and an axial (local 1) load changes sign
        foreach (var item in export.MemberLoads.Where(item => etabsName.ContainsKey(item.Load.MemberName)))
        {
            var (direction, csys) = Direction(item.Load.Direction);
            var load = item.Load;
            var flip = reversed.Contains(load.MemberName);
            var sign = flip && load.Direction == FemLoadDirection.Local1 ? -1 : 1;
            Check(flip
                    ? m.FrameObj.SetLoadDistributed(etabsName[load.MemberName], item.Pattern, 1, direction, 1 - load.RelEnd, 1 - load.RelStart,
                        sign * load.ValueEnd, sign * load.ValueStart, csys, true, false)
                    : m.FrameObj.SetLoadDistributed(etabsName[load.MemberName], item.Pattern, 1, direction, load.RelStart, load.RelEnd,
                        load.ValueStart, load.ValueEnd, csys, true, false), "FrameObj.SetLoadDistributed", load.MemberName);
            memberLoads++;
        }
        foreach (var item in export.MemberPointLoads.Where(item => etabsName.ContainsKey(item.Load.MemberName)))
        {
            var (direction, csys) = Direction(item.Load.Direction);
            var load = item.Load;
            var flip = reversed.Contains(load.MemberName);
            var sign = flip && load.Direction == FemLoadDirection.Local1 ? -1 : 1;
            Check(m.FrameObj.SetLoadPoint(etabsName[load.MemberName], item.Pattern, 1, direction, flip ? 1 - load.Rel : load.Rel, sign * load.ValueKN, csys, true, false),
                "FrameObj.SetLoadPoint", load.MemberName);
            memberLoads++;
        }
        lines.Add($"Loading: {addedPatterns} load pattern(s) added; {jointLoads} joint load(s) set, {clearedLoads} removed; {memberLoads} frame load(s).");
        if (targets > 0) notes.Add($"{targets} Tab 03 cable target force(s) are not exported — ETABS has no target-force load; apply the pretension in ETABS.");
        if (export.GravityLoadsLeftOut > 0) notes.Add($"{export.GravityLoadsLeftOut} gravity load assignment(s) are not exported (ETABS has none on frames).");

        // static load cases: new ones are created; every exported case takes its loads and initial case
        int addedCases = 0;
        foreach (var item in x.Cases)
        {
            var nonlinear = item.Type.Contains("Nonlinear", StringComparison.OrdinalIgnoreCase);
            if (etabsCases.Contains(item.Name))
            {
                var type = eLoadCaseType.LinearStatic; int subType = 0;
                Check(m.LoadCases.GetTypeOAPI(item.Name, ref type, ref subType), "LoadCases.GetTypeOAPI", item.Name);
                if (type != (nonlinear ? eLoadCaseType.NonlinearStatic : eLoadCaseType.LinearStatic))
                {
                    notes.Add($"Load case '{item.Name}' is {type} in ETABS and {item.Type} in Mcotis — left as it is.");
                    continue;
                }
            }
            else
            {
                Check(nonlinear ? m.LoadCases.StaticNonlinear.SetCase(item.Name) : m.LoadCases.StaticLinear.SetCase(item.Name), "LoadCases.SetCase", item.Name);
                if (nonlinear) Soft(m.LoadCases.StaticNonlinear.SetGeometricNonlinearity(item.Name, NonlinearPDeltaLargeDisplacement), $"geometric nonlinearity of {item.Name}");
                addedCases++;
            }
            var count = item.Loads.Count;
            string[] loadTypes = Enumerable.Repeat("Load", count).ToArray(), loadNames = item.Loads.Select(load => load.Pattern).ToArray();
            double[] factors = item.Loads.Select(load => load.Factor).ToArray();
            Check(nonlinear ? m.LoadCases.StaticNonlinear.SetLoads(item.Name, count, ref loadTypes, ref loadNames, ref factors)
                            : m.LoadCases.StaticLinear.SetLoads(item.Name, count, ref loadTypes, ref loadNames, ref factors), "LoadCases.SetLoads", item.Name);
            var initial = FemLoadPlan.IsNoInitialCase(item.InitialCase) ? "None" : item.InitialCase;
            Soft(nonlinear ? m.LoadCases.StaticNonlinear.SetInitialCase(item.Name, initial) : m.LoadCases.StaticLinear.SetInitialCase(item.Name, initial),
                $"initial case of {item.Name}");
        }
        // combinations: new ones are created, changed ones rebuilt (all created before their items are set)
        int addedCombos = 0, rebuiltCombos = 0;
        var pending = new List<SapExportCombination>();
        foreach (var combo in x.Combinations)
        {
            if (etabsCombos.Contains(combo.Name))
            {
                int count = 0; eCNameType[] types = []; string[] items = []; double[] factors = [];
                Check(m.RespCombo.GetCaseList(combo.Name, ref count, ref types, ref items, ref factors), "RespCombo.GetCaseList", combo.Name);
                var current = Enumerable.Range(0, count).Select(i => (items[i].ToUpperInvariant(), Math.Round(factors[i], 9))).OrderBy(item => item).ToArray();
                var wantedItems = combo.Items.Select(item => (item.Name.ToUpperInvariant(), Math.Round(item.Factor, 9))).OrderBy(item => item).ToArray();
                if (current.SequenceEqual(wantedItems)) continue;
                Check(m.RespCombo.Delete(combo.Name), "RespCombo.Delete", combo.Name);
                rebuiltCombos++;
            }
            else addedCombos++;
            Check(m.RespCombo.Add(combo.Name, 0), "RespCombo.Add", combo.Name);
            pending.Add(combo);
        }
        foreach (var combo in pending)
            foreach (var (name, factor) in combo.Items)
            {
                var type = caseNames.Contains(name) ? eCNameType.LoadCase : eCNameType.LoadCombo;
                Check(m.RespCombo.SetCaseList(combo.Name, ref type, name, factor), "RespCombo.SetCaseList", $"{combo.Name} / {name}");
            }
        foreach (var item in x.Cases) Soft(m.Analyze.SetRunCaseFlag(item.Name, item.Run), $"run flag of {item.Name}");
        lines.Add($"Analysis: {addedCases} load case(s) added, {x.Cases.Count - addedCases} updated; {addedCombos} combination(s) added, {rebuiltCombos} rebuilt; " +
                  $"{x.Cases.Count(item => item.Run)} case(s) set to run.");

        Soft(m.View.RefreshView(0, false), "view refresh");
        foreach (var note in notes) lines.Add("Note: " + note);
        var (finalPath, _) = Identity(m);
        return new EtabsExportReport(target with { Path = finalPath }, lines);
    }

    private static string Label(cSapModel m, string frame)
    {
        string label = "", story = "";
        return m.FrameObj.GetLabelFromName(frame, ref label, ref story) == 0 && label.Length > 0 ? $"{label}@{story}" : frame;
    }

    private static Dictionary<string, Position> ReadPoints(cSapModel m)
    {
        var points = new Dictionary<string, Position>(StringComparer.Ordinal);
        int n = 0; string[] names = [];
        Check(m.PointObj.GetNameList(ref n, ref names), "PointObj.GetNameList");
        foreach (var name in names.Take(n))
        {
            double x = 0, y = 0, z = 0;
            Check(m.PointObj.GetCoordCartesian(name, ref x, ref y, ref z, "Global"), "PointObj.GetCoordCartesian", name);
            points[name] = new Position(x, y, z);
        }
        return points;
    }

    private static bool Standard(FrameSectionProperty section) =>
        section.Shape is FrameShape.Rectangle or FrameShape.Box or FrameShape.I or FrameShape.Pipe or FrameShape.Circle;

    /// <summary>Whether ETABS's section has the area and inertias of Mcotis's (0.1 ‰).</summary>
    private static bool SameProperties(cSapModel m, FrameSectionProperty section)
    {
        double a = 0, as2 = 0, as3 = 0, j = 0, i22 = 0, i33 = 0, s22 = 0, s33 = 0, z22 = 0, z33 = 0, r22 = 0, r33 = 0;
        if (m.PropFrame.GetSectProps(section.Name, ref a, ref as2, ref as3, ref j, ref i22, ref i33, ref s22, ref s33, ref z22, ref z33, ref r22, ref r33) != 0) return true;
        var values = section.Values();
        static bool Close(double left, double right) => Math.Abs(left - right) <= 1e-4 * Math.Max(Math.Abs(right), 1e-12);
        return Close(a, values.Area) && Close(i33, values.I33) && Close(i22, values.I22);
    }

    private static void WriteStandardSection(cSapModel m, FrameSectionProperty s)
    {
        var code = s.Shape switch
        {
            FrameShape.Rectangle => m.PropFrame.SetRectangle(s.Name, s.Material, s.Depth, s.Width, -1, s.Notes),
            // ETABS 23 refuses a corner radius of exactly 0; 1 µm gives the sharp-cornered properties to every printed figure
            FrameShape.Box => m.PropFrame.SetTube_1(s.Name, s.Material, s.Depth, s.Width, s.FlangeThickness, s.WebThickness, s.CornerRadius > 0 ? s.CornerRadius : 1e-6, -1, s.Notes),
            FrameShape.I => m.PropFrame.SetISection_1(s.Name, s.Material, s.Depth, s.Width, s.FlangeThickness, s.WebThickness,
                s.BottomWidth > 0 ? s.BottomWidth : s.Width, s.BottomFlangeThickness > 0 ? s.BottomFlangeThickness : s.FlangeThickness, 0, -1, s.Notes),
            FrameShape.Pipe => m.PropFrame.SetPipe(s.Name, s.Material, s.Depth, s.WebThickness, -1, s.Notes),
            _ => m.PropFrame.SetCircle(s.Name, s.Material, s.Depth, -1, s.Notes),
        };
        Check(code, "PropFrame.Set" + s.Shape, s.Name);
    }

    /// <summary>A section ETABS gets by its properties (CFT, steel encased, Section Designer, general): A, shear areas, J,
    /// I22, I33 with its material (the concrete for a composite, whose properties Mcotis transforms to it).</summary>
    private static void WriteGeneralSection(cSapModel m, FrameSectionProperty s)
    {
        var v = s.Values();
        if (!(v.Area > 0) || !(v.I33 > 0) || !(v.I22 > 0)) throw new InvalidOperationException($"Section '{s.Name}' has no area or inertia to export.");
        var t3 = s.Depth > 0 ? s.Depth : Math.Sqrt(12 * v.I33 / v.Area);
        var t2 = s.Width > 0 ? s.Width : Math.Sqrt(12 * v.I22 / v.Area);
        double s33 = v.I33 / (t3 / 2), s22 = v.I22 / (t2 / 2);
        var as2 = v.ShearArea2 > 0 ? v.ShearArea2 : 5.0 / 6 * v.Area;
        var as3 = v.ShearArea3 > 0 ? v.ShearArea3 : 5.0 / 6 * v.Area;
        var torsion = v.Torsion > 0 ? v.Torsion : v.I22 + v.I33;
        Check(m.PropFrame.SetGeneral(s.Name, s.Material, t3, t2, v.Area, as2, as3, torsion, v.I22, v.I33, s22, s33, 1.5 * s22, 1.5 * s33,
            Math.Sqrt(v.I22 / v.Area), Math.Sqrt(v.I33 / v.Area), -1, $"Mcotis {FrameSectionProperty.Title(s.Shape)}: properties transformed to {s.Material}. {s.Notes}".Trim()),
            "PropFrame.SetGeneral", s.Name);
    }

    private static (double Weight, double Mass)? MaterialWeight(SapExportModel x, string material) =>
        x.Materials.FirstOrDefault(item => item.Name.Equals(material, StringComparison.OrdinalIgnoreCase)) is { } found
            ? (found.WeightPerVolume, found.MassPerVolume) : null;

    private static double Modifier(FemSectionProperties property, int index) =>
        property.Modifiers is { } values && index < values.Count && values[index] > 0 ? values[index] : 1;

    /// <summary>SAP / ETABS load direction code and coordinate system of a Mcotis member load direction.</summary>
    private static (int Direction, string CSys) Direction(FemLoadDirection direction) => direction switch
    {
        FemLoadDirection.Local1 => (1, "Local"), FemLoadDirection.Local2 => (2, "Local"), FemLoadDirection.GlobalX => (4, "Global"),
        FemLoadDirection.GlobalZ => (6, "Global"), FemLoadDirection.ProjectedX => (7, "Global"), FemLoadDirection.ProjectedZ => (9, "Global"),
        FemLoadDirection.Gravity => (10, "Global"), _ => (11, "Global"),
    };

    private static eMatType MaterialType(MaterialKind kind) => kind switch
    {
        MaterialKind.Steel => eMatType.Steel, MaterialKind.Concrete => eMatType.Concrete, MaterialKind.Aluminum => eMatType.Aluminum,
        MaterialKind.ColdFormed => eMatType.ColdFormed, MaterialKind.Rebar => eMatType.Rebar, MaterialKind.Tendon => eMatType.Tendon,
        MaterialKind.Masonry => eMatType.Masonry, _ => eMatType.NoDesign,
    };

    private static eLoadPatternType PatternType(string type)
    {
        var compact = type.Replace(" ", "");
        if (compact.Equals("SuperimposedDead", StringComparison.OrdinalIgnoreCase)) return eLoadPatternType.SuperDead;
        return Enum.TryParse<eLoadPatternType>(compact, ignoreCase: true, out var parsed) ? parsed : eLoadPatternType.Other;
    }
}
