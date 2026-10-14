using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application;

/// <summary>Temporary boundary while the desktop workspace is moved to the standalone document. It turns one complete
/// legacy FEM snapshot into the native 3D document that new editing, persistence and analysis use. Nothing is read from
/// SAP2000 or ETABS here; import adapters must first finish their read and hand over a frozen snapshot.</summary>
internal static class LegacyNativeDocumentMigration
{
    internal sealed record Source(Native.OriginKind Kind, string Product, string KeyPrefix, bool StartingModel = false)
    {
        public static Source Generated(string product) => new(Native.OriginKind.Generated, product, product, true);
        public static Source Imported(string product) => new(Native.OriginKind.Imported, product, product);
    }

    public static Source Infer(FemInputSnapshot snapshot, bool startingModel = false)
    {
        var text = $"{snapshot.Model?.Path} {snapshot.Model?.Version}";
        if (text.Contains("ETABS", StringComparison.OrdinalIgnoreCase) || text.Contains(".e2k", StringComparison.OrdinalIgnoreCase))
            return Source.Imported("ETABS") with { StartingModel = startingModel };
        if (text.Contains("SAP", StringComparison.OrdinalIgnoreCase) || text.Contains(".sdb", StringComparison.OrdinalIgnoreCase))
            return Source.Imported("SAP2000") with { StartingModel = startingModel };
        return new(Native.OriginKind.Imported, "Mcotis legacy config", "Legacy", startingModel);
    }

    /// <summary>The joint restraints a model of the main workspace is analysed, shown and exported with, by joint name:
    /// the six-component assignments it holds. A model that holds none — a configuration saved before restraints were
    /// data, and the VCF system, whose supports are not assignments yet — has the pins the application has always
    /// drawn at its column bases (<see cref="FemStudyService.FindPinSupportPoints"/>); where that rule finds no column
    /// base the model has no restraint, and the run reports what is not held. One rule for the run, the native
    /// document, the export and the viewer, so that none of them can differ from another.</summary>
    public static IReadOnlyDictionary<string, Native.Restraint> Restraints(
        IReadOnlyDictionary<string, Native.Restraint> assigned, GeometryModel geometry)
    {
        var held = assigned.Where(item => !item.Value.IsFree).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (held.Count > 0) return held;
        try
        {
            return FemStudyService.FindPinSupportPoints(geometry).ToDictionary(name => name, _ => Native.Restraint.Pinned, StringComparer.Ordinal);
        }
        catch (InvalidOperationException) { return held; }
    }

    public static Native.ModelDocument Convert(FemInputSnapshot snapshot, Source source)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(source);
        var geometry = snapshot.Geometry ?? throw new InvalidDataException("The FEM configuration has no geometry.");
        if (snapshot.DatabaseId == Guid.Empty) throw new InvalidDataException("The FEM configuration has no model identity.");

        var (caseRows, combinationRows) = FemCaseSchema.Complete(snapshot.CaseRows, snapshot.CombinationRows, snapshot.DatabaseId);
        FemCaseSchema.Validate(caseRows, combinationRows, snapshot.MassSources);

        var editor = Native.ModelDocument.Create(new(snapshot.DatabaseId)).Edit();
        var usedIds = new HashSet<Guid>(caseRows.Concat(combinationRows).Select(row => Guid.Parse(row.Id!))
            .Concat(snapshot.MassSources.Select(row => Guid.Parse(row.Id))));
        Native.ObjectId Id(string kind, string name)
        {
            for (var salt = 0; ; salt++)
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{snapshot.DatabaseId:D}|native|{kind}|{name}|{salt}"));
                var value = new Guid(bytes.AsSpan(0, 16));
                if (value != Guid.Empty && usedIds.Add(value)) return new(value);
            }
        }

        var objectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string ObjectName(string requested, string kind)
        {
            var name = requested.Trim();
            if (objectNames.Add(name)) return name;
            var stem = $"{kind} {name}";
            for (var number = 1; ; number++)
            {
                var candidate = number == 1 ? stem : $"{stem} ({number})";
                if (objectNames.Add(candidate)) return candidate;
            }
        }
        // a model a parametric generator made: what it made carries its generation key, what was added is the user's
        var flag = snapshot.Generator;
        Native.ObjectOrigin Origin(string type, string name)
        {
            if (flag is not null)
                return (type == "Joint" ? flag.Joints : flag.Members)?.GetValueOrDefault(name) is { } key
                    ? new(Native.OriginKind.Generated, $"{flag.Id}:{key}", null, type, name)
                    : new(Native.OriginKind.User, null, null, type, name);
            return new(source.Kind,
                source.Kind == Native.OriginKind.Generated ? $"{source.KeyPrefix}/{type}/{name}" : null,
                source.Kind == Native.OriginKind.Imported ? source.Product : null, type, name);
        }

        var pointIds = new Dictionary<string, Native.ObjectId>(StringComparer.Ordinal);
        foreach (var point in geometry.Points.Values.OrderBy(point => point.Name, StringComparer.Ordinal))
        {
            var id = Id("Point", point.Name);
            pointIds.Add(point.Name, id);
            editor.AddPoint(new(point.Position.X, point.Position.Y, point.Position.Z), ObjectName(point.Name, "Joint"), id,
                Origin("Joint", point.Name));
        }

        var properties = snapshot.SectionProperties.GroupBy(item => item.MemberName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var memberIds = new Dictionary<string, Native.ObjectId>(StringComparer.Ordinal);
        var memberGeometry = new Dictionary<string, StructuralMember>(StringComparer.Ordinal);
        foreach (var member in geometry.Members.OrderBy(member => member.Name, StringComparer.Ordinal))
        {
            if (!pointIds.TryGetValue(member.Start, out var start) || !pointIds.TryGetValue(member.End, out var end))
                throw new InvalidDataException($"Member '{member.Name}' refers to a joint the configuration does not hold.");
            if (!properties.TryGetValue(member.Name, out var property))
                throw new InvalidDataException($"Member '{member.Name}' has no analysis section properties.");
            if (!(property.AreaM2 > 0) || !(property.ElasticModulusKNPerM2 > 0))
                throw new InvalidDataException($"Member '{member.Name}' needs positive finite E and area.");

            var objectId = Id(member.Kind == MemberKind.Frame ? "Frame" : "Cable", member.Name);
            memberIds.Add(member.Name, objectId);
            memberGeometry.Add(member.Name, member);
            var nativeName = ObjectName(member.Name, member.Kind == MemberKind.Frame ? "Frame" : "Cable");
            var materialId = Id("Material", member.Name);
            var area = property.AreaM2;
            // FemSectionProperties already contains effective stiffness. Express the exact line weight and mass with
            // densities on that effective area so native self weight is neither lost nor applied twice.
            var weightDensity = property.WeightPerLengthKNPerM / area;
            var massDensity = property.MassPerLengthTPerM / area;
            editor.PutMaterial(new(materialId, $"Material of {nativeName}",
                member.Kind == MemberKind.Frame ? Native.MaterialType.Other : Native.MaterialType.Tendon,
                property.ElasticModulusKNPerM2, .3, weightDensity, massDensity));

            if (member.Kind == MemberKind.Frame)
            {
                if (property.InPlaneInertiaM4 is not > 0)
                    throw new InvalidDataException($"Frame '{member.Name}' needs a positive in-plane inertia.");
                var sectionId = Id("FrameSection", member.Name);
                editor.PutFrameSection(new(sectionId, $"Section of {nativeName}", materialId, Native.FrameSectionShape.General,
                    new(area, property.InPlaneInertiaM4.Value, property.OutOfPlaneInertiaM4 ?? 0,
                        property.TorsionConstantM4 ?? 0)));
                editor.AddFrame(start, end, nativeName, objectId, Origin("Frame", member.Name));
                editor.SetFrameSection([objectId], sectionId);
                if (property.ReleaseStartRotation || property.ReleaseEndRotation)
                    editor.SetFrameReleases([objectId], new(
                        new(false, false, false, false, false, property.ReleaseStartRotation),
                        new(false, false, false, false, false, property.ReleaseEndRotation)));
            }
            else
            {
                var sectionId = Id("CableSection", member.Name);
                editor.PutCableSection(new(sectionId, $"Section of {nativeName}", materialId, area));
                editor.AddCable(start, end, nativeName, objectId, Origin(member.Kind == MemberKind.Tendon ? "Tendon" : "Cable", member.Name));
                editor.SetCableSection([objectId], sectionId);
            }
        }

        // The restraints the model is analysed with (Restraints, below): its own six-component assignments, or the
        // column-base pins of a configuration that holds none.
        var duplicate = snapshot.JointRestraints.GroupBy(item => item.JointName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Joint '{duplicate.Key}' has more than one restraint assignment.");
        var assigned = snapshot.JointRestraints.ToDictionary(item => item.JointName, item => item.ToNative(), StringComparer.Ordinal);
        foreach (var (joint, restraint) in Restraints(assigned, snapshot.Baseline ?? geometry))
        {
            if (pointIds.TryGetValue(joint, out var pointId)) editor.SetRestraint([pointId], restraint);
            else if (assigned.ContainsKey(joint)) throw new InvalidDataException($"A restraint refers to missing joint '{joint}'.");
        }

        var patternRows = snapshot.PatternRows.ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
        var patternNames = snapshot.PatternRows.Select(row => row.Name)
            .Concat(snapshot.Loading?.Patterns.Select(row => row.Name) ?? [])
            .Concat(snapshot.CableSetup?.Cables.Select(row => row.PretensionLoadCase) ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var patternIds = new Dictionary<string, Native.ObjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in patternNames)
        {
            patternRows.TryGetValue(name, out var row);
            var imported = snapshot.Loading?.Patterns.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var id = Id("LoadPattern", name);
            patternIds.Add(name, id);
            editor.PutLoadPattern(new(id, name, PatternType(row?.Type ?? imported?.Type), row?.SelfWeightMultiplier ?? imported?.SelfWeightMultiplier ?? 0));
        }

        void PointLoad(string pattern, string point, double fx, double fz, double my = 0)
        {
            if (!patternIds.TryGetValue(pattern, out var patternId) || !pointIds.TryGetValue(point, out var pointId)) return;
            if (Math.Abs(fx) + Math.Abs(fz) + Math.Abs(my) == 0) return;
            editor.PutLoad(new Native.PointLoad(Id("PointLoad", $"{pattern}|{point}|{editor.Loads.Count}"), patternId, pointId,
                new(fx, 0, fz), new(0, my, 0)));
        }

        var tab01Patterns = snapshot.LoadingSetup?.LoadDefinitions.Select(row => row.LoadName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var applied = snapshot.LoadingSetup?.AppliedPoints.ToHashSet(StringComparer.Ordinal) ?? [];
        var loading = snapshot.Loading ?? SapFemLoading.Empty;
        foreach (var item in loading.JointLoads)
            if (!(tab01Patterns.Contains(item.Pattern) && applied.Contains(item.Load.PointName)))
                PointLoad(item.Pattern, item.Load.PointName, item.Load.FxKN, item.Load.FzKN, item.Load.MyKNm);

        AddTab01Loads(snapshot, PointLoad);

        foreach (var item in loading.MemberLoads)
            AddMemberDistributed(item.Pattern, item.Load.MemberName, item.Load.Direction, item.Load.RelStart, item.Load.RelEnd,
                item.Load.ValueStart, item.Load.ValueEnd);
        foreach (var item in loading.MemberPointLoads)
            AddMemberPoint(item.Pattern, item.Load.MemberName, item.Load.Direction, item.Load.Rel, item.Load.ValueKN);
        foreach (var item in loading.GravityLoads)
        {
            if (!properties.TryGetValue(item.MemberName, out var property)) continue;
            var weight = property.WeightPerLengthKNPerM;
            if (item.MultiplierX != 0) AddMemberDistributed(item.Pattern, item.MemberName, FemLoadDirection.GlobalX, 0, 1,
                item.MultiplierX * weight, item.MultiplierX * weight);
            if (item.MultiplierZ != 0) AddMemberDistributed(item.Pattern, item.MemberName, FemLoadDirection.GlobalZ, 0, 1,
                item.MultiplierZ * weight, item.MultiplierZ * weight);
        }

        foreach (var target in CableTargets(snapshot.CableSetup))
            if (target.Force > 0 && patternIds.TryGetValue(target.Pattern, out var patternId) && memberIds.TryGetValue(target.Cable, out var cableId) &&
                editor.Cables.ContainsKey(cableId))
                editor.PutLoad(new Native.CableTargetForce(Id("CableTarget", $"{target.Pattern}|{target.Cable}"), patternId, cableId, target.Force));

        var massIds = new Dictionary<string, Native.ObjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in snapshot.MassSources)
        {
            var id = new Native.ObjectId(Guid.Parse(row.Id));
            massIds[row.Name] = id;
            editor.PutMassSource(new(id, row.Name, row.IncludeSelfMass));
        }

        var caseIds = caseRows.ToDictionary(row => row.Name, row => new Native.ObjectId(Guid.Parse(row.Id!)), StringComparer.OrdinalIgnoreCase);
        foreach (var row in caseRows)
        {
            var id = caseIds[row.Name];
            var components = string.IsNullOrWhiteSpace(row.Definition) ? [] : FemLoadDefinition.Parse(row.Definition)
                .Select(term => new Native.LoadCaseComponent(patternIds.TryGetValue(term.Name, out var patternId) ? patternId
                    : throw new InvalidDataException($"Load case '{row.Name}' refers to missing pattern '{term.Name}'."), term.Factor)).ToImmutableArray();
            Native.ModalEigenOptions? modal = null;
            if (row.Modal is { } options)
            {
                var massId = options.MassSourceId is { } text && Guid.TryParse(text, out var parsed) ? new Native.ObjectId(parsed)
                    : massIds.TryGetValue(options.MassSource, out var found) ? found
                    : throw new InvalidDataException($"Load case '{row.Name}' refers to missing mass source '{options.MassSource}'.");
                modal = new(massId, options.RequestedModes);
            }
            editor.PutLoadCase(new(id, row.Name, FemCaseSchema.KindOf(row), components)
            {
                InitialCaseId = ParseOptionalId(row.InitialCaseId),
                StiffnessSourceCaseId = ParseOptionalId(row.StiffnessSourceCaseId),
                GeometricMode = FemCaseSchema.GeometricOf(row),
                NonlinearControls = row.NonlinearControls is { } controls ? new(controls.MaxLoadSteps, controls.MaxIterationsPerStep,
                    controls.RelativeResidualTolerance, controls.RelativeDisplacementTolerance) : null,
                ModalOptions = modal,
                Run = row.Run,
                FitCableTargetForces = row.CableTargetForces == true,
            });
        }

        var combinationIds = combinationRows.ToDictionary(row => row.Name, row => new Native.ObjectId(Guid.Parse(row.Id!)), StringComparer.OrdinalIgnoreCase);
        foreach (var row in combinationRows)
        {
            var terms = FemLoadDefinition.Parse(row.Definition).Select(term => new Native.CombinationTerm(
                caseIds.TryGetValue(term.Name, out var caseId) ? caseId
                : combinationIds.TryGetValue(term.Name, out var combinationId) ? combinationId
                : throw new InvalidDataException($"Load combination '{row.Name}' refers to missing case or combination '{term.Name}'."), term.Factor)).ToImmutableArray();
            editor.PutLoadCombination(new(combinationIds[row.Name], row.Name,
                row.Type.Contains("Envelope", StringComparison.OrdinalIgnoreCase) ? Native.CombinationType.Envelope : Native.CombinationType.LinearAdd, terms)
            { Run = row.Run });
        }

        var fallback = source.StartingModel ? Native.AnalysisSettings.Default : Native.AnalysisSettings.Default with
        { ActiveDofs = Native.DofMask.XZ, CableDofs = Native.DofMask.UX | Native.DofMask.UZ };
        editor.SetSettings(new(snapshot.Settings.TargetFitPercent, snapshot.Settings.TargetFitIterations,
            snapshot.Settings.FrameMeshSegments, snapshot.Settings.CableSag)
        {
            ActiveDofs = ReadDofs(snapshot.Settings.ActiveDofs, fallback.ActiveDofs),
            CableDofs = ReadDofs(snapshot.Settings.CableJointDofs, fallback.CableDofs),
        });
        return editor.Commit().Document;

        void AddMemberDistributed(string pattern, string memberName, FemLoadDirection direction, double from, double to,
            double startValue, double endValue)
        {
            if (!patternIds.TryGetValue(pattern, out var patternId) || !memberIds.TryGetValue(memberName, out var memberId) ||
                !memberGeometry.TryGetValue(memberName, out var member)) return;
            var vectors = LoadVectors(direction, member, geometry, properties[memberName].LocalAngleDeg);
            if (editor.Frames.ContainsKey(memberId))
            {
                foreach (var (nativeDirection, factor) in vectors)
                    if (factor != 0) editor.PutLoad(new Native.FrameDistributedLoad(
                        Id("FrameDistributedLoad", $"{pattern}|{memberName}|{direction}|{editor.Loads.Count}"), patternId, memberId,
                        nativeDirection, from, to, factor * startValue, factor * endValue));
                return;
            }
            if (!editor.Cables.ContainsKey(memberId)) return;
            var length = Length(member, geometry);
            var a = Math.Clamp(Math.Min(from, to), 0, 1);
            var b = Math.Clamp(Math.Max(from, to), 0, 1);
            if (b - a <= 1e-12) return;
            double Intensity(double at) => startValue + (endValue - startValue) * (at - from) / (to - from);
            var qa = Intensity(a); var qb = Intensity(b); var slope = (qb - qa) / (b - a);
            var total = (qa + qb) / 2 * (b - a) * length;
            var far = length * (qa * (b * b - a * a) / 2 + slope * ((b * b * b - a * a * a) / 3 - a * (b * b - a * a) / 2));
            var near = total - far;
            foreach (var (nativeDirection, factor) in vectors)
            {
                if (nativeDirection == Native.LoadDirection.GlobalX)
                { PointLoad(pattern, member.Start, factor * near, 0); PointLoad(pattern, member.End, factor * far, 0); }
                else
                { PointLoad(pattern, member.Start, 0, factor * near); PointLoad(pattern, member.End, 0, factor * far); }
            }
        }

        void AddMemberPoint(string pattern, string memberName, FemLoadDirection direction, double at, double value)
        {
            if (!patternIds.TryGetValue(pattern, out var patternId) || !memberIds.TryGetValue(memberName, out var memberId) ||
                !memberGeometry.TryGetValue(memberName, out var member) || !editor.Frames.ContainsKey(memberId)) return;
            foreach (var (nativeDirection, factor) in LoadVectors(direction, member, geometry, properties[memberName].LocalAngleDeg))
                if (factor != 0) editor.PutLoad(new Native.FramePointLoad(
                    Id("FramePointLoad", $"{pattern}|{memberName}|{direction}|{editor.Loads.Count}"), patternId, memberId,
                    nativeDirection, at, factor * value));
        }
    }

    private static Native.LoadPatternType PatternType(string? text) => (text ?? "").Trim().ToUpperInvariant() switch
    {
        "DEAD" or "SUPER DEAD" or "SUPERDEAD" => Native.LoadPatternType.Dead,
        "LIVE" or "REDUCIBLE LIVE" or "REDUCIBLELIVE" => Native.LoadPatternType.Live,
        "PRESTRESS" or "PRETENSION" => Native.LoadPatternType.Prestress,
        _ => Native.LoadPatternType.Other,
    };

    private static Native.ObjectId? ParseOptionalId(string? text) => Guid.TryParse(text, out var id) ? new(id) : null;

    private static Native.DofMask ReadDofs(IEnumerable<string>? names, Native.DofMask fallback)
    {
        if (names is null) return fallback;
        var mask = names.Aggregate(Native.DofMask.None, (value, name) =>
            Enum.TryParse<Native.DofComponent>(name, true, out var component) && Enum.IsDefined(component)
                ? value | Native.DegreesOfFreedom.Mask(component) : value);
        return Native.DegreesOfFreedom.IsValid(mask) ? mask : fallback;
    }

    private static void AddTab01Loads(FemInputSnapshot snapshot, Action<string, string, double, double, double> add)
    {
        if (snapshot.LoadingSetup is not { } setup || snapshot.Geometry is not { } geometry) return;
        var points = setup.AppliedPoints.Where(geometry.Points.ContainsKey).Select(name => geometry.Points[name])
            .OrderBy(point => point.Position.X).ThenBy(point => point.Name, StringComparer.Ordinal).ToArray();
        var stories = double.IsFinite(setup.NumberOfStories) && setup.NumberOfStories >= 1 ? (int)Math.Truncate(setup.NumberOfStories) : 0;
        for (var index = 0; index < points.Length; index++)
        {
            var length = points.Length switch
            {
                1 => 0,
                _ when index == 0 => Math.Max(0, points[1].Position.X - points[0].Position.X) / 2,
                _ when index == points.Length - 1 => Math.Max(0, points[^1].Position.X - points[^2].Position.X) / 2,
                _ => Math.Max(0, points[index + 1].Position.X - points[index - 1].Position.X) / 2,
            };
            foreach (var row in setup.LoadDefinitions)
            {
                var qz = row.IncrementEnabled ? row.MinQz : row.FixedQz;
                if (qz is double value && double.IsFinite(value)) add(row.LoadName, points[index].Name, 0,
                    -value * length * setup.TributaryWidth * stories, 0);
            }
        }
    }

    private static IEnumerable<(string Cable, string Pattern, double Force)> CableTargets(CableForceConfigDto? setup)
    {
        if (setup is null) yield break;
        var rows = setup.Cables.ToDictionary(row => row.CableName, StringComparer.OrdinalIgnoreCase);
        double? Resolve(CableForceRowDto row, HashSet<string> visiting)
        {
            if (!visiting.Add(row.CableName)) return null;
            if (row.SyncMode == CableSyncMode.Independent || string.IsNullOrWhiteSpace(row.ReferenceCableName))
                return row.Mode == CableForceMode.Fixed ? row.FixedForce : row.MinForce;
            if (!rows.TryGetValue(row.ReferenceCableName, out var reference)) return null;
            var value = Resolve(reference, visiting);
            return row.SyncMode switch
            {
                CableSyncMode.SameStep => row.MinForce,
                CableSyncMode.SameRatio => value * (row.SyncFactor ?? 1),
                CableSyncMode.SameOffset => value + (row.SyncFactor ?? 0),
                _ => value,
            };
        }
        foreach (var row in setup.Cables)
            if (Resolve(row, new(StringComparer.OrdinalIgnoreCase)) is double force && double.IsFinite(force))
                yield return (row.CableName, row.PretensionLoadCase, force);
    }

    private static IReadOnlyList<(Native.LoadDirection Direction, double Factor)> LoadVectors(FemLoadDirection direction,
        StructuralMember member, GeometryModel geometry, double localAngleDeg)
    {
        var start = geometry.Points[member.Start].Position;
        var end = geometry.Points[member.End].Position;
        var length = Length(member, geometry);
        var c = (end.X - start.X) / length;
        var s = (end.Z - start.Z) / length;
        var angle = localAngleDeg * Math.PI / 180;
        var (x, z) = direction switch
        {
            FemLoadDirection.GlobalX => (1d, 0d),
            FemLoadDirection.GlobalZ => (0d, 1d),
            FemLoadDirection.Gravity => (0d, -1d),
            FemLoadDirection.ProjectedX => (Math.Abs(s), 0d),
            FemLoadDirection.ProjectedZ => (0d, Math.Abs(c)),
            FemLoadDirection.ProjectedGravity => (0d, -Math.Abs(c)),
            FemLoadDirection.Local1 => (c, s),
            _ => (-s * Math.Cos(angle) + c * Math.Sin(angle), c * Math.Cos(angle) + s * Math.Sin(angle)),
        };
        var result = new List<(Native.LoadDirection, double)>();
        if (x != 0) result.Add((Native.LoadDirection.GlobalX, x));
        if (z != 0) result.Add((Native.LoadDirection.GlobalZ, z));
        return result;
    }

    private static double Length(StructuralMember member, GeometryModel geometry)
    {
        var start = geometry.Points[member.Start].Position;
        var end = geometry.Points[member.End].Position;
        var dx = end.X - start.X; var dy = end.Y - start.Y; var dz = end.Z - start.Z;
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(length > 0) || !double.IsFinite(length)) throw new InvalidDataException($"Member '{member.Name}' has invalid length.");
        return length;
    }
}
