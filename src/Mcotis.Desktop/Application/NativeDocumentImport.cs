using System.Collections.Immutable;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application;

/// <summary>A load case of a native document as the main window's Load Cases table holds it: its kind and what belongs to it.</summary>
internal sealed record NativeImportCase(string Name, FemCaseOptions Options, bool Run, bool FitCableTargetForces);

/// <summary>A native document in the terms of the main window's model (<see cref="NativeDocumentImport"/>): the geometry and
/// supports by name, the libraries, each member's section and assignments (its values are worked out by the window, as
/// for an import), the loading, the load cases and combinations, the mass sources and joint masses, the analysis settings.</summary>
internal sealed record NativeImportModel(
    GeometryModel Geometry,
    IReadOnlyDictionary<string, Native.Restraint> Restraints,
    IReadOnlyDictionary<string, FemJointMassDto> JointMasses,
    IReadOnlyList<MaterialProperty> Materials,
    IReadOnlyList<FrameSectionProperty> FrameSections,
    IReadOnlyList<StrandSectionProperty> CableSections,
    IReadOnlyList<FemSectionProperties> Members,
    IReadOnlyDictionary<string, double> MassDensities,
    SapFemLoading Loading,
    IReadOnlyList<(string Cable, string Pattern, double Force)> CableTargets,
    IReadOnlyDictionary<string, bool> CombinationRuns,
    IReadOnlyList<NativeImportCase> Cases,
    IReadOnlyList<FemMassSourceDto> MassSources,
    Native.AnalysisSettings Settings,
    IReadOnlyList<string> Notes);

/// <summary>
/// The main window takes a native document in (task of 2026-10-14, M7 of the task — M8 of the main-window migration: "one
/// window"): a model made by Edit → TemplateBuilding…, or a <c>.mcotis</c> file of the native workspace, which holds none of
/// the main window's configuration. Its objects keep their names; every value the main window's model holds is taken
/// as the document has it — sections with their A, I33, I22 and J, materials with their E, ν, weight and mass density,
/// releases, modifiers, restraints, joint masses, loads, cases, combinations, mass sources and the degrees of freedom.
/// What that model cannot hold yet is refused, all of it at once and each with its object, before the open model is
/// touched: shells, rotated local axes, releases other than the moments, shear areas, joint loads along Y or about
/// X / Z, member loads along Y or local 3 (or local 1 / 2 on a frame out of the XZ plane), a cable's target force read
/// other than at mid-length or more than one per cable, envelope combinations. Nothing is approximated. A cable target
/// force becomes the cable's pretension row (Tab 03), a fixed target in its pattern, as the tie of a tied arch has it.
/// </summary>
internal static class NativeDocumentImport
{
    public static NativeImportModel Read(Native.ModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var problems = new List<string>();
        var notes = new List<string>();
        var names = LegacyNames.Of(document);
        string Name(Native.ObjectId id) => names.TryGetName(id, out var name) ? name : document.TryGetNamed(id, out _, out var own) ? own : id.ToString();

        if (!document.Shells.IsEmpty) problems.Add($"{document.Shells.Count} shell(s): the main window holds frames and cables only");
        var geometry = Mcotis.Desktop.Presentation.NativeDocumentView.Geometry(document);
        var restraints = Mcotis.Desktop.Presentation.NativeDocumentView.Restraints(document);
        var masses = document.JointMassAssignments.Values.ToDictionary(mass => Name(mass.PointId),
            mass => new FemJointMassDto(Name(mass.PointId), mass.UX, mass.UY, mass.UZ, mass.RX, mass.RY, mass.RZ), StringComparer.Ordinal);

        // ---- libraries ----
        var materials = document.Materials.Values.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => MaterialProperty.Default(Kind(item.Type), item.Name) with
        {
            ElasticModulus = item.ElasticModulus, PoissonRatio = item.PoissonRatio, WeightPerVolume = item.WeightDensity, Source = "Native model",
        }).ToArray();
        string MaterialName(Native.ObjectId id) => document.Materials.TryGetValue(id, out var material) ? material.Name : "";
        var frameSections = document.FrameSections.Values.OrderBy(item => item.Name, StringComparer.Ordinal).Select(section =>
        {
            var p = section.Properties;
            if (p.ShearArea2 > 0 || p.ShearArea3 > 0) problems.Add($"Frame section {section.Name}: shear areas (shear deformation), which the main window's model does not hold yet");
            return new FrameSectionProperty
            {
                Name = section.Name, Shape = FrameShape.General, SapType = "Native", Material = MaterialName(section.MaterialId),
                Given = new SectionValues(p.Area, p.Inertia33, p.Inertia22, p.TorsionConstant, 0, 0), Source = "Native model",
            };
        }).ToArray();
        var cableSections = document.CableSections.Values.OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(section => new StrandSectionProperty { Name = section.Name, Material = MaterialName(section.MaterialId), Area = section.Area }).ToArray();

        // ---- members ----
        var members = new List<FemSectionProperties>();
        var densities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var frame in document.Frames.Values.OrderBy(item => Name(item.Id), StringComparer.Ordinal))
        {
            var name = Name(frame.Id);
            var a = document.AssignmentOfFrame(frame.Id);
            if (a.SectionId is not { } sectionId || !document.FrameSections.TryGetValue(sectionId, out var section)) { problems.Add($"Frame {name}: no section"); continue; }
            if (a.LocalAxisAngle != 0) problems.Add($"Frame {name}: local axes rotated by {a.LocalAxisAngle:G6}°, which the main window's model does not carry yet");
            foreach (var (end, release) in new[] { ("start", a.Releases.Start), ("end", a.Releases.End) })
                if (release.Axial || release.Shear2 || release.Shear3 || release.Torsion)
                    problems.Add($"Frame {name}: a release at its {end} other than the moments about local 2 and 3");
            var m = a.Modifiers;
            members.Add(new FemSectionProperties(name, 0, 0, 0, a.Releases.Start.Moment3, a.Releases.End.Moment3, section.Name, MaterialName(section.MaterialId),
                Modifiers: [m.Area, 1, 1, m.Torsion, m.Inertia22, m.Inertia33, m.Mass, m.Weight])
            {
                PoissonRatio = document.Materials.TryGetValue(section.MaterialId, out var material) ? material.PoissonRatio : null,
                ReleaseStartOutOfPlane = a.Releases.Start.Moment2, ReleaseEndOutOfPlane = a.Releases.End.Moment2,
            });
            if (material is not null) densities[name] = material.MassDensity;
        }
        foreach (var cable in document.Cables.Values.OrderBy(item => Name(item.Id), StringComparer.Ordinal))
        {
            var name = Name(cable.Id);
            var a = document.AssignmentOfCable(cable.Id);
            if (a.SectionId is not { } sectionId || !document.CableSections.TryGetValue(sectionId, out var section)) { problems.Add($"Cable {name}: no section"); continue; }
            members.Add(new FemSectionProperties(name, 0, 0, null, SectionName: section.Name, MaterialName: MaterialName(section.MaterialId),
                Modifiers: [a.Modifiers.Area, a.Modifiers.Mass, a.Modifiers.Weight]));
            if (document.Materials.TryGetValue(section.MaterialId, out var material)) densities[name] = material.MassDensity;
        }

        // ---- loading ----
        string PatternName(Native.ObjectId id) => document.LoadPatterns.TryGetValue(id, out var pattern) ? pattern.Name : id.ToString();
        var patterns = document.LoadPatterns.Values.OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => new SapLoadPattern(item.Name, item.Type.ToString(), item.SelfWeightMultiplier)).ToArray();
        var jointLoads = new List<SapPatternJointLoad>();
        var memberLoads = new List<SapPatternMemberLoad>();
        var memberPointLoads = new List<SapPatternMemberPointLoad>();
        var targets = new List<(string Cable, string Pattern, double Force)>();
        FemLoadDirection? Direction(Native.LoadDirection direction, Native.FrameObject frame, string what)
        {
            var start = document.Points[frame.StartPointId].Position;
            var end = document.Points[frame.EndPointId].Position;
            bool inXz = Math.Abs(end.Y - start.Y) <= Native.Tolerances.Length;
            FemLoadDirection? mapped = direction switch
            {
                Native.LoadDirection.GlobalX => FemLoadDirection.GlobalX,
                Native.LoadDirection.GlobalZ => FemLoadDirection.GlobalZ,
                Native.LoadDirection.Gravity => FemLoadDirection.Gravity,
                Native.LoadDirection.Local1 when inXz => FemLoadDirection.Local1,
                Native.LoadDirection.Local2 when inXz => FemLoadDirection.Local2,
                _ => null,
            };
            if (mapped is null) problems.Add($"Frame {Name(frame.Id)}: a {what} along {direction}{(direction is Native.LoadDirection.Local1 or Native.LoadDirection.Local2 ? " of a frame out of the XZ plane" : "")}, which the main window's model does not hold yet");
            return mapped;
        }
        foreach (var load in document.Loads.Values.OrderBy(item => item.Id))
        {
            var pattern = PatternName(load.PatternId);
            switch (load)
            {
                case Native.PointLoad point:
                    if (point.Force.Y != 0 || point.Moment.X != 0 || point.Moment.Z != 0)
                        problems.Add($"Joint {Name(point.TargetId)}: a load along Y or a moment about X or Z, which the main window's model does not hold yet");
                    else jointLoads.Add(new(pattern, new FemJointLoad(Name(point.TargetId), point.Force.X, point.Force.Z, point.Moment.Y)));
                    break;
                case Native.FrameDistributedLoad line when document.Frames.TryGetValue(line.TargetId, out var frame):
                    if (Direction(line.Direction, frame, "line load") is { } lineDirection)
                        memberLoads.Add(new(pattern, new FemMemberLoad(Name(frame.Id), lineDirection, line.RelativeStart, line.RelativeEnd, line.ValueStart, line.ValueEnd)));
                    break;
                case Native.FramePointLoad force when document.Frames.TryGetValue(force.TargetId, out var frame):
                    if (Direction(force.Direction, frame, "point load") is { } pointDirection)
                        memberPointLoads.Add(new(pattern, new FemMemberPointLoad(Name(frame.Id), pointDirection, force.RelativeDistance, force.Value)));
                    break;
                case Native.CableTargetForce target:
                    if (Math.Abs(target.RelativeDistance - 0.5) > 1e-12)
                        problems.Add($"Cable {Name(target.TargetId)}: a target force read at {target.RelativeDistance:G6} of its length; the main window's pretension rows read it at mid-length");
                    else if (targets.Any(item => item.Cable == Name(target.TargetId)))
                        problems.Add($"Cable {Name(target.TargetId)}: more than one target force; the main window holds one pretension row per cable");
                    else targets.Add((Name(target.TargetId), pattern, target.Force));
                    break;
                default:
                    problems.Add($"A {load.GetType().Name} on {Name(load.TargetId)}, which the main window's model does not hold");
                    break;
            }
        }

        // ---- cases, combinations, masses ----
        string CaseName(Native.ObjectId id) => document.LoadCases.TryGetValue(id, out var item) ? item.Name
            : document.LoadCombinations.TryGetValue(id, out var combination) ? combination.Name : id.ToString();
        var cases = document.LoadCases.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var sapCases = cases.Select(item => new SapLoadCase(item.Name, FemCaseOptions.KindName(item.Kind), true,
            item.Components.Select(component => (PatternName(component.PatternId), component.Factor)).ToArray(),
            item.InitialCaseId is { } initial ? CaseName(initial) : null, null)).ToArray();
        var caseSetups = cases.Select(item => new NativeImportCase(item.Name, new FemCaseOptions(item.Kind, item.GeometricMode,
                item.StiffnessSourceCaseId is { } source ? CaseName(source) : null, item.NonlinearControls,
                item.ModalOptions is { } modal && document.MassSources.TryGetValue(modal.MassSourceId, out var mass) ? mass.Name : null,
                item.ModalOptions?.RequestedModes), item.Run, item.FitCableTargetForces)).ToArray();
        var combinations = new List<SapLoadCombination>();
        foreach (var combination in document.LoadCombinations.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (combination.Type == Native.CombinationType.Envelope)
                problems.Add($"Combination {combination.Name}: an envelope, which the main window does not combine yet (its combinations add their terms)");
            combinations.Add(new(combination.Name, "Linear Add", true,
                combination.Terms.Select(term => (CaseName(term.SourceId), document.LoadCombinations.ContainsKey(term.SourceId), term.Factor)).ToArray(), null));
        }
        var massSources = document.MassSources.Values.OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => new FemMassSourceDto(item.Id.Value.ToString("D"), item.Name, item.IncludeSelfMass, item.IncludeAdditionalMass)).ToArray();
        if (document.Generator is { } flag) notes.Add($"The model carries the flag of the {flag.GeneratorId} generator; it is not carried: the model opens as an ordinary model.");

        if (problems.Count > 0)
            throw new InvalidOperationException($"The main window cannot hold this model yet — {problems.Count} item(s): " +
                string.Join("; ", problems.Distinct().Take(8)) + (problems.Distinct().Count() > 8 ? $"; and {problems.Distinct().Count() - 8} more" : "") + ".");
        var loading = new SapFemLoading(patterns, jointLoads, memberLoads, memberPointLoads, [], sapCases, combinations, []);
        var runs = document.LoadCombinations.Values.ToDictionary(item => item.Name, item => item.Run, StringComparer.Ordinal);
        return new(geometry, restraints, masses, materials, frameSections, cableSections, members, densities, loading, targets, runs, caseSetups, massSources,
            document.Settings, notes);
    }

    private static MaterialKind Kind(Native.MaterialType type) => type switch
    {
        Native.MaterialType.Steel => MaterialKind.Steel,
        Native.MaterialType.Concrete => MaterialKind.Concrete,
        Native.MaterialType.Rebar => MaterialKind.Rebar,
        Native.MaterialType.Tendon => MaterialKind.Tendon,
        _ => MaterialKind.Other,
    };
}
