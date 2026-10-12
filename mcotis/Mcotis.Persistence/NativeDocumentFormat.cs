using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mcotis.Application;
using Mcotis.Core;

namespace Mcotis.Persistence;

/// <summary>A file could not be read as a Mcotis document. Nothing was opened.</summary>
public sealed class DocumentFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Upgrades a file by one schema version. It works on the parsed copy in memory — the file on disk is never
/// written by Open — and it must not guess: data the older schema did not hold gets its documented default here.</summary>
public interface IDocumentMigration
{
    int FromVersion { get; }
    void Upgrade(JsonObject file);
}

/// <summary>The native file: one JSON document that is the whole model — the single authoritative source in the file.</summary>
public static class NativeDocumentFormat
{
    public const string FormatName = "Mcotis.Document";
    public const int CurrentSchemaVersion = 1;
    public const string Extension = ".mcotis";

    /// <summary>The upgrades this version applies, one per schema step. Schema 1 is the first, so there is none yet.</summary>
    public static IReadOnlyList<IDocumentMigration> Migrations { get; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A member this version does not know is not dropped on the quiet: the file is refused.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public static string Serialize(ModelDocument document, DateTime? savedUtc = null) =>
        JsonSerializer.Serialize(ToFile(document, savedUtc ?? DateTime.UtcNow), Options);

    /// <summary>The document a file holds, upgraded to the current schema when it is older. All or nothing: any
    /// unreadable, unknown, missing or inconsistent content throws <see cref="DocumentFileException"/>.</summary>
    public static ModelDocument Deserialize(string json, IReadOnlyList<IDocumentMigration>? migrations = null)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root ||
                root["format"] is not JsonValue format || !format.TryGetValue<string>(out var formatName) || formatName != FormatName)
                throw new DocumentFileException("This is not a Mcotis document.");
            if (root["schemaVersion"] is not JsonValue schema || !schema.TryGetValue<int>(out var version) || version < 0)
                throw new DocumentFileException("This Mcotis document does not state its schema version.");
            if (version > CurrentSchemaVersion)
                throw new DocumentFileException($"This document was saved by a newer version of Mcotis (schema {version}); this version reads up to schema {CurrentSchemaVersion}.");

            for (; version < CurrentSchemaVersion; version++)
            {
                var from = version;
                var migration = (migrations ?? Migrations).FirstOrDefault(item => item.FromVersion == from)
                    ?? throw new DocumentFileException($"This document uses schema {from}, which this version of Mcotis cannot upgrade.");
                migration.Upgrade(root);
                root["schemaVersion"] = from + 1;
            }

            var file = root.Deserialize<FileDto>(Options) ?? throw new DocumentFileException("This Mcotis document is empty.");
            return ToDocument(file);
        }
        catch (Exception ex) when (ex is JsonException or DocumentException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new DocumentFileException("The document cannot be opened: " + ex.Message, ex);
        }
    }

    // ---- Document → file ----

    private static FileDto ToFile(ModelDocument d, DateTime savedUtc)
    {
        static IEnumerable<T> ByName<T>(IEnumerable<T> items) where T : INamedObject => items.OrderBy(item => item.Name, NaturalNameComparer.Instance);
        string NameOf(ObjectId id) => d.TryGetNamed(id, out _, out var name) ? name : "";

        return new FileDto
        {
            Format = FormatName,
            SchemaVersion = CurrentSchemaVersion,
            DocumentId = d.DocumentId.ToString(),
            SavedUtc = savedUtc,
            Units = new() { Force = BaseUnits.Force, Length = BaseUnits.Length, Mass = BaseUnits.Mass },
            Settings = new()
            {
                CableFitTolerancePercent = d.Settings.CableFitTolerancePercent, CableFitMaxIterations = d.Settings.CableFitMaxIterations,
                FrameMeshSegments = d.Settings.FrameMeshSegments, CableSag = d.Settings.CableSag,
            },
            NameSequence = NamingService.Kinds.Where(kind => d.Names.LastNumber(kind) > 0).ToDictionary(kind => kind.ToString(), d.Names.LastNumber),
            Materials = ByName(d.Materials.Values).Select(item => new MaterialDto
            {
                Id = item.Id.ToString(), Name = item.Name, Type = item.Type, ElasticModulus = item.ElasticModulus,
                PoissonRatio = item.PoissonRatio, WeightDensity = item.WeightDensity, MassDensity = item.MassDensity,
            }).ToList(),
            FrameSections = ByName(d.FrameSections.Values).Select(item => new FrameSectionDto
            {
                Id = item.Id.ToString(), Name = item.Name, MaterialId = item.MaterialId.ToString(), Shape = item.Shape,
                Area = item.Properties.Area, Inertia33 = item.Properties.Inertia33, Inertia22 = item.Properties.Inertia22,
                TorsionConstant = item.Properties.TorsionConstant, ShearArea2 = item.Properties.ShearArea2, ShearArea3 = item.Properties.ShearArea3,
                Dimensions = new(item.Dimensions, StringComparer.Ordinal),
            }).ToList(),
            CableSections = ByName(d.CableSections.Values).Select(item => new CableSectionDto
            {
                Id = item.Id.ToString(), Name = item.Name, MaterialId = item.MaterialId.ToString(), Area = item.Area,
            }).ToList(),
            ShellSections = ByName(d.ShellSections.Values).Select(item => new ShellSectionDto
            {
                Id = item.Id.ToString(), Name = item.Name, MaterialId = item.MaterialId.ToString(), Type = item.Type, Thickness = item.Thickness,
            }).ToList(),
            Points = ByName(d.Points.Values).Select(item => new PointDto
            {
                Id = item.Id.ToString(), Name = item.Name, X = item.Position.X, Y = item.Position.Y, Z = item.Position.Z, Origin = ToOrigin(item.Origin),
            }).ToList(),
            Frames = ByName(d.Frames.Values).Select(item => new FrameDto
            {
                Id = item.Id.ToString(), Name = item.Name, StartPointId = item.StartPointId.ToString(),
                EndPointId = item.EndPointId.ToString(), Origin = ToOrigin(item.Origin),
            }).ToList(),
            Cables = ByName(d.Cables.Values).Select(item => new CableDto
            {
                Id = item.Id.ToString(), Name = item.Name, StartPointId = item.StartPointId.ToString(),
                EndPointId = item.EndPointId.ToString(), Origin = ToOrigin(item.Origin),
            }).ToList(),
            Shells = ByName(d.Shells.Values).Select(item => new ShellDto
            {
                Id = item.Id.ToString(), Name = item.Name, PointIds = item.PointIds.Select(id => id.ToString()).ToList(), Origin = ToOrigin(item.Origin),
            }).ToList(),
            Restraints = d.Restraints.OrderBy(item => NameOf(item.Key), NaturalNameComparer.Instance).Select(item => new RestraintDto
            {
                PointId = item.Key.ToString(), Ux = item.Value.Ux, Uy = item.Value.Uy, Uz = item.Value.Uz,
                Rx = item.Value.Rx, Ry = item.Value.Ry, Rz = item.Value.Rz,
            }).ToList(),
            FrameAssignments = d.FrameAssignments.OrderBy(item => NameOf(item.Key), NaturalNameComparer.Instance).Select(item => new FrameAssignmentDto
            {
                FrameId = item.Key.ToString(), SectionId = item.Value.SectionId?.ToString(),
                StartRelease = ToRelease(item.Value.Releases.Start), EndRelease = ToRelease(item.Value.Releases.End),
                LocalAxisAngle = item.Value.LocalAxisAngle,
                Modifiers = new()
                {
                    Area = item.Value.Modifiers.Area, Inertia33 = item.Value.Modifiers.Inertia33, Inertia22 = item.Value.Modifiers.Inertia22,
                    Torsion = item.Value.Modifiers.Torsion, Mass = item.Value.Modifiers.Mass, Weight = item.Value.Modifiers.Weight,
                },
            }).ToList(),
            CableAssignments = d.CableAssignments.OrderBy(item => NameOf(item.Key), NaturalNameComparer.Instance).Select(item => new CableAssignmentDto
            {
                CableId = item.Key.ToString(), SectionId = item.Value.SectionId?.ToString(), AreaModifier = item.Value.Modifiers.Area,
                MassModifier = item.Value.Modifiers.Mass, WeightModifier = item.Value.Modifiers.Weight,
            }).ToList(),
            ShellAssignments = d.ShellAssignments.OrderBy(item => NameOf(item.Key), NaturalNameComparer.Instance).Select(item => new ShellAssignmentDto
            {
                ShellId = item.Key.ToString(), SectionId = item.Value.SectionId?.ToString(), LocalAxisAngle = item.Value.LocalAxisAngle,
            }).ToList(),
            LoadPatterns = ByName(d.LoadPatterns.Values).Select(item => new LoadPatternDto
            {
                Id = item.Id.ToString(), Name = item.Name, Type = item.Type, SelfWeightMultiplier = item.SelfWeightMultiplier,
            }).ToList(),
            Loads = d.Loads.Values.OrderBy(load => NameOf(load.TargetId), NaturalNameComparer.Instance).ThenBy(load => load.Id).Select(ToLoad).ToList(),
            LoadCases = ByName(d.LoadCases.Values).Select(item => new LoadCaseDto
            {
                Id = item.Id.ToString(), Name = item.Name, Type = item.Type, InitialCaseId = item.InitialCaseId?.ToString(),
                Run = item.Run, FitCableTargetForces = item.FitCableTargetForces,
                Components = item.Components.Select(component => new ComponentDto { PatternId = component.PatternId.ToString(), Factor = component.Factor }).ToList(),
            }).ToList(),
            LoadCombinations = ByName(d.LoadCombinations.Values).Select(item => new LoadCombinationDto
            {
                Id = item.Id.ToString(), Name = item.Name, Type = item.Type, Run = item.Run,
                Terms = item.Terms.Select(term => new TermDto { SourceId = term.SourceId.ToString(), Factor = term.Factor }).ToList(),
            }).ToList(),
        };
    }

    private static OriginDto? ToOrigin(ObjectOrigin? origin) => origin is null ? null : new()
    {
        Kind = origin.Kind, GenerationKey = origin.GenerationKey, SourceProduct = origin.SourceProduct,
        SourceType = origin.SourceType, SourceName = origin.SourceName,
    };

    private static ReleaseDto ToRelease(EndRelease release) => new()
    {
        Axial = release.Axial, Shear2 = release.Shear2, Shear3 = release.Shear3,
        Torsion = release.Torsion, Moment2 = release.Moment2, Moment3 = release.Moment3,
    };

    private static VectorDto ToVector(Xyz value) => new() { X = value.X, Y = value.Y, Z = value.Z };

    private static LoadDto ToLoad(LoadAssignment load)
    {
        var (id, pattern, target) = (load.Id.ToString(), load.PatternId.ToString(), load.TargetId.ToString());
        return load switch
        {
            PointLoad point => new() { Id = id, PatternId = pattern, TargetId = target, Type = LoadDto.Point, Force = ToVector(point.Force), Moment = ToVector(point.Moment) },
            FrameDistributedLoad line => new()
            {
                Id = id, PatternId = pattern, TargetId = target, Type = LoadDto.FrameDistributed, Direction = line.Direction,
                RelativeStart = line.RelativeStart, RelativeEnd = line.RelativeEnd, ValueStart = line.ValueStart, ValueEnd = line.ValueEnd,
            },
            FramePointLoad point => new()
            {
                Id = id, PatternId = pattern, TargetId = target, Type = LoadDto.FramePoint, Direction = point.Direction,
                RelativeDistance = point.RelativeDistance, Value = point.Value,
            },
            CableTargetForce force => new()
            {
                Id = id, PatternId = pattern, TargetId = target, Type = LoadDto.CableTargetForce, Value = force.Force, RelativeDistance = force.RelativeDistance,
            },
            ShellUniformLoad area => new() { Id = id, PatternId = pattern, TargetId = target, Type = LoadDto.ShellUniform, Direction = area.Direction, Value = area.Value },
            _ => throw new NotSupportedException($"The load type {load.GetType().Name} cannot be saved yet."),
        };
    }

    // ---- File → document: through the editor, so a file is held to the same rules as any edit ----

    private static ModelDocument ToDocument(FileDto file)
    {
        if (Member(file.Units, "units") is not { Force: BaseUnits.Force, Length: BaseUnits.Length, Mass: BaseUnits.Mass })
            throw new DocumentFileException($"This document is not in {BaseUnits.Force}, {BaseUnits.Length}, {BaseUnits.Mass} — the only units this version of Mcotis reads.");

        var editor = ModelDocument.Create(Id(file.DocumentId, "document id")).Edit();

        foreach (var item in Member(file.Materials, "materials"))
            editor.PutMaterial(new(Id(item.Id, "material id"), item.Name, item.Type, item.ElasticModulus, item.PoissonRatio, item.WeightDensity, item.MassDensity));
        foreach (var item in Member(file.FrameSections, "frameSections"))
            editor.PutFrameSection(new(Id(item.Id, "frame section id"), item.Name, Id(item.MaterialId, "material id"), item.Shape,
                new(item.Area, item.Inertia33, item.Inertia22, item.TorsionConstant, item.ShearArea2, item.ShearArea3))
            {
                Dimensions = FrameSectionDefinition.NoDimensions.AddRange(Member(item.Dimensions, "dimensions")),
            });
        foreach (var item in Member(file.CableSections, "cableSections"))
            editor.PutCableSection(new(Id(item.Id, "cable section id"), item.Name, Id(item.MaterialId, "material id"), item.Area));
        foreach (var item in Member(file.ShellSections, "shellSections"))
            editor.PutShellSection(new(Id(item.Id, "shell section id"), item.Name, Id(item.MaterialId, "material id"), item.Type, item.Thickness));

        foreach (var item in Member(file.Points, "points"))
            editor.AddPoint(new(item.X, item.Y, item.Z), Member(item.Name, "name"), Id(item.Id, "point id"), FromOrigin(item.Origin));
        foreach (var item in Member(file.Frames, "frames"))
            editor.AddFrame(Id(item.StartPointId, "point id"), Id(item.EndPointId, "point id"), Member(item.Name, "name"), Id(item.Id, "frame id"), FromOrigin(item.Origin));
        foreach (var item in Member(file.Cables, "cables"))
            editor.AddCable(Id(item.StartPointId, "point id"), Id(item.EndPointId, "point id"), Member(item.Name, "name"), Id(item.Id, "cable id"), FromOrigin(item.Origin));
        foreach (var item in Member(file.Shells, "shells"))
            editor.AddShell(Member(item.PointIds, "pointIds").Select(id => Id(id, "point id")), Member(item.Name, "name"), Id(item.Id, "shell id"), FromOrigin(item.Origin));

        foreach (var item in Member(file.Restraints, "restraints"))
            editor.SetRestraint([Id(item.PointId, "point id")], new(item.Ux, item.Uy, item.Uz, item.Rx, item.Ry, item.Rz));
        foreach (var item in Member(file.FrameAssignments, "frameAssignments"))
        {
            ObjectId[] frame = [Id(item.FrameId, "frame id")];
            var modifiers = Member(item.Modifiers, "modifiers");
            editor.SetFrameSection(frame, item.SectionId is null ? null : Id(item.SectionId, "frame section id"));
            editor.SetFrameReleases(frame, new(FromRelease(item.StartRelease), FromRelease(item.EndRelease)));
            editor.SetFrameLocalAxis(frame, item.LocalAxisAngle);
            editor.SetFrameModifiers(frame, new(modifiers.Area, modifiers.Inertia33, modifiers.Inertia22, modifiers.Torsion, modifiers.Mass, modifiers.Weight));
        }
        foreach (var item in Member(file.CableAssignments, "cableAssignments"))
        {
            ObjectId[] cable = [Id(item.CableId, "cable id")];
            editor.SetCableSection(cable, item.SectionId is null ? null : Id(item.SectionId, "cable section id"));
            editor.SetCableModifiers(cable, new(item.AreaModifier, item.MassModifier, item.WeightModifier));
        }
        foreach (var item in Member(file.ShellAssignments, "shellAssignments"))
        {
            ObjectId[] shell = [Id(item.ShellId, "shell id")];
            editor.SetShellSection(shell, item.SectionId is null ? null : Id(item.SectionId, "shell section id"));
            editor.SetShellLocalAxis(shell, item.LocalAxisAngle);
        }

        foreach (var item in Member(file.LoadPatterns, "loadPatterns"))
            editor.PutLoadPattern(new(Id(item.Id, "load pattern id"), item.Name, item.Type, item.SelfWeightMultiplier));
        foreach (var item in Member(file.Loads, "loads")) editor.PutLoad(FromLoad(item));
        foreach (var item in Member(file.LoadCases, "loadCases"))
            editor.PutLoadCase(new(Id(item.Id, "load case id"), item.Name, item.Type,
                [.. Member(item.Components, "components").Select(component => new LoadCaseComponent(Id(component.PatternId, "load pattern id"), component.Factor))])
            {
                InitialCaseId = item.InitialCaseId is null ? null : Id(item.InitialCaseId, "load case id"),
                Run = item.Run,
                FitCableTargetForces = item.FitCableTargetForces,
            });
        foreach (var item in Member(file.LoadCombinations, "loadCombinations"))
            editor.PutLoadCombination(new(Id(item.Id, "load combination id"), item.Name, item.Type,
                [.. Member(item.Terms, "terms").Select(term => new CombinationTerm(Id(term.SourceId, "load case id"), term.Factor))])
            {
                Run = item.Run,
            });

        var settings = Member(file.Settings, "settings");
        editor.SetSettings(new(settings.CableFitTolerancePercent, settings.CableFitMaxIterations, settings.FrameMeshSegments, settings.CableSag));

        // Where the automatic names had got to — beyond what the names still in the file show, when objects were deleted.
        foreach (var (key, number) in Member(file.NameSequence, "nameSequence"))
            editor.RaiseNameSequence(NamingService.Kinds.Cast<ObjectKind?>().FirstOrDefault(kind => kind.ToString() == key)
                ?? throw new DocumentFileException($"'{key}' is not a kind of object with a name sequence."), number);
        return editor.Commit().Document;
    }

    private static LoadAssignment FromLoad(LoadDto load)
    {
        var (id, pattern, target) = (Id(load.Id, "load id"), Id(load.PatternId, "load pattern id"), Id(load.TargetId, "load target id"));
        DocumentFileException Missing(string member) => new($"The load {load.Id} of type '{load.Type}' has no '{member}'.");
        return load.Type switch
        {
            LoadDto.Point => new PointLoad(id, pattern, target, FromVector(load.Force ?? throw Missing("force")), FromVector(load.Moment ?? throw Missing("moment"))),
            LoadDto.FrameDistributed => new FrameDistributedLoad(id, pattern, target, load.Direction ?? throw Missing("direction"),
                load.RelativeStart ?? throw Missing("relativeStart"), load.RelativeEnd ?? throw Missing("relativeEnd"),
                load.ValueStart ?? throw Missing("valueStart"), load.ValueEnd ?? throw Missing("valueEnd")),
            LoadDto.FramePoint => new FramePointLoad(id, pattern, target, load.Direction ?? throw Missing("direction"),
                load.RelativeDistance ?? throw Missing("relativeDistance"), load.Value ?? throw Missing("value")),
            LoadDto.CableTargetForce => new CableTargetForce(id, pattern, target, load.Value ?? throw Missing("value"),
                load.RelativeDistance ?? throw Missing("relativeDistance")),
            LoadDto.ShellUniform => new ShellUniformLoad(id, pattern, target, load.Direction ?? throw Missing("direction"), load.Value ?? throw Missing("value")),
            _ => throw new DocumentFileException($"The load {load.Id} has the unknown type '{load.Type}'."),
        };
    }

    private static ObjectOrigin? FromOrigin(OriginDto? origin) => origin is null ? null
        : new(origin.Kind, origin.GenerationKey, origin.SourceProduct, origin.SourceType, origin.SourceName);

    private static EndRelease FromRelease(ReleaseDto? release)
    {
        var value = Member(release, "release");
        return new(value.Axial, value.Shear2, value.Shear3, value.Torsion, value.Moment2, value.Moment3);
    }

    private static Xyz FromVector(VectorDto value) => new(value.X, value.Y, value.Z);

    /// <summary>A member the schema requires, which JSON may still carry as null.</summary>
    private static T Member<T>(T? value, string member) where T : class =>
        value ?? throw new DocumentFileException($"The document has no value for '{member}'.");

    private static ObjectId Id(string? text, string what) => Guid.TryParse(text, out var value) && value != Guid.Empty
        ? new ObjectId(value)
        : throw new DocumentFileException($"'{text}' is not a valid {what}.");
}

/// <summary>Reads and writes .mcotis files. Open never writes the file; Save replaces it in one step.</summary>
public sealed class NativeDocumentStore(IReadOnlyList<IDocumentMigration>? migrations = null) : IDocumentStore
{
    public ModelDocument Load(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DocumentFileException($"The file '{path}' cannot be read: {ex.Message}", ex);
        }
        return NativeDocumentFormat.Deserialize(json, migrations);
    }

    public void Save(string path, ModelDocument document) => AtomicFile.WriteAllText(path, NativeDocumentFormat.Serialize(document));
}

/// <summary>P2 before P10, whatever the case: the order the file lists named things in.</summary>
internal sealed class NaturalNameComparer : IComparer<string>
{
    public static NaturalNameComparer Instance { get; } = new();

    public int Compare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var numberA = a.AsSpan(startA, i - startA).TrimStart('0');
                var numberB = b.AsSpan(startB, j - startB).TrimStart('0');
                var byNumber = numberA.Length != numberB.Length ? numberA.Length.CompareTo(numberB.Length) : numberA.SequenceCompareTo(numberB);
                if (byNumber != 0) return byNumber;
                continue;
            }
            var byCharacter = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (byCharacter != 0) return byCharacter;
            i++;
            j++;
        }
        var byLength = (a.Length - i).CompareTo(b.Length - j);
        return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
    }
}
