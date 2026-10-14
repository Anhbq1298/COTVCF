using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Infrastructure;

namespace Mcotis.Desktop.Configuration;

/// <summary>
/// A saved FEM model (Tab 07 → Inputs → Save Model): everything the internal FEM run reads from SAP2000 and from
/// Tabs 01–05, so Load Model can restore it and run FEM without a SAP2000 connection or a project file.
/// Holds the SAP model identity, the geometry (session baseline, current, truss baseline), load patterns and load
/// case names, the Tabs 01–05 project, the member section/material properties, the SAP loading snapshot, the
/// edited Loads rows and the FEM Analysis settings. Nothing in it is ever written back to SAP2000.
/// </summary>
public sealed class FemModelFileDto
{
    public const string FormatName = "Mcotis.FemModel";
    /// <summary>What <see cref="FormatName"/> was before the app was renamed. Files saved under it still open; none is written.</summary>
    public const string LegacyFormatName = "COTVCF.FemModel";
    /// <summary>2 since the load cases state their kind and refer to each other by identity
    /// (<see cref="FemCaseSchema"/>). A file of version 1 is brought to version 2 in memory when it is read.</summary>
    public const int CurrentVersion = 2;
    /// <summary>The version a revision is identified in (<see cref="FemInputDatabase.RevisionHash"/>): the version of
    /// the file is not content, so a configuration and the same one after an upgrade are one revision.</summary>
    internal const int RevisionFormVersion = 1;

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public DateTime SavedUtc { get; set; }
    public ModelInfo? Model { get; set; }
    public GeometryModel? SessionBaseline { get; set; }
    public GeometryModel? Geometry { get; set; }
    public GeometryModel? Baseline { get; set; }
    public List<string> LoadPatterns { get; set; } = [];
    public List<string> LoadCaseNames { get; set; } = [];
    public McotisConfigDto? Project { get; set; }
    public List<FemSectionProperties> SectionProperties { get; set; } = [];
    /// <summary>Define → Materials library (optional; older files have none and it is rebuilt from the member properties).</summary>
    public List<MaterialProperty>? Materials { get; set; }
    /// <summary>Define → Section Properties libraries (optional, like <see cref="Materials"/>).</summary>
    public List<FrameSectionProperty>? FrameSections { get; set; }
    public List<StrandSectionProperty>? CableSections { get; set; }
    public List<StrandSectionProperty>? TendonSections { get; set; }
    public SapFemLoading? Loading { get; set; }
    public List<FemPatternRowDto> PatternRows { get; set; } = [];
    public List<FemDefinitionRowDto> CaseRows { get; set; } = [];
    public List<FemDefinitionRowDto> CombinationRows { get; set; } = [];
    /// <summary>Version 2. The masses modal cases read; null when there is none.</summary>
    public List<FemMassSourceDto>? MassSources { get; set; }
    /// <summary>The six-component joint restraints the model holds; null when it holds none, as in a version-2 file
    /// written before restraints were data. Such a model is analysed with the pins at its column bases.</summary>
    public List<FemJointRestraintDto>? JointRestraints { get; set; }
    /// <summary>The joint masses the model holds (M8); null when it holds none.</summary>
    public List<FemJointMassDto>? JointMasses { get; set; }
    /// <summary>The generator flag (<see cref="FemInputSnapshot.Generator"/>); absent when no generator made the model.</summary>
    public FemGeneratorDto? Generator { get; set; }
    public FemSettingsDto Settings { get; set; } = new();
}

public sealed record FemPatternRowDto(string Name, string Type, double SelfWeightMultiplier, string Role, string Loads);

/// <summary>A load case or a load combination as it is saved. The first nine members are version 1; the others are
/// version 2 (<see cref="FemCaseSchema"/>). A member of version 2 that is absent means what version 1 meant: a
/// nonlinear static case, without geometric nonlinearity, on its own stiffness.</summary>
/// <param name="Type">The type as text, as it is shown ("Nonlinear Static", or what SAP2000 called it). Descriptive:
/// <see cref="CaseKind"/> is what the case is.</param>
/// <param name="InitialCase">The case this one continues from, by name — what version 1 held, kept beside
/// <see cref="InitialCaseId"/> for the application and for files of version 1.</param>
public sealed record FemDefinitionRowDto(string Name, string Type, string Definition, string? InitialCase, bool Run, bool Supported, string Source, string Note, bool? CableTargetForces = null)
{
    /// <summary>The identity of the row: a GUID that a rename does not change.</summary>
    public string? Id { get; init; }
    /// <summary>LinearStatic, NonlinearStatic, InitialPDelta or ModalEigen. Absent: NonlinearStatic.</summary>
    public string? CaseKind { get; init; }
    /// <summary>None or PDelta. Absent: None — PDelta for an initial P-Delta case.</summary>
    public string? GeometricMode { get; init; }
    /// <summary>The case <see cref="InitialCase"/> names, by its identity.</summary>
    public string? InitialCaseId { get; init; }
    /// <summary>A linear static or modal case: the nonlinear case whose end stiffness it uses, by name. Absent: the unstressed stiffness.</summary>
    public string? StiffnessSourceCase { get; init; }
    /// <summary>The case <see cref="StiffnessSourceCase"/> names, by its identity.</summary>
    public string? StiffnessSourceCaseId { get; init; }
    public FemNonlinearControlsDto? NonlinearControls { get; init; }
    public FemModalOptionsDto? Modal { get; init; }
}

public sealed record FemNonlinearControlsDto(int MaxLoadSteps, int MaxIterationsPerStep, double RelativeResidualTolerance, double RelativeDisplacementTolerance);

/// <param name="MassSource">The mass source by name, beside <paramref name="MassSourceId"/>.</param>
public sealed record FemModalOptionsDto(string MassSource, string? MassSourceId, int RequestedModes);

/// <param name="IncludeAdditionalMass">The joint masses (Assign → Joint → Masses…) count too; absent in a file written
/// before joint masses were data: they do.</param>
public sealed record FemMassSourceDto(string Id, string Name, bool IncludeSelfMass = true, bool IncludeAdditionalMass = true);

/// <summary>Additional mass lumped at a joint, by the joint's name (M8): translational mass along UX, UY, UZ [t] and
/// rotational inertia about RX, RY, RZ [t·m²] — physical properties, not restraints. It follows the joint's renames and
/// goes with the joint.</summary>
public sealed record FemJointMassDto(string JointName, double UX, double UY, double UZ, double RX = 0, double RY = 0, double RZ = 0)
{
    public bool IsZero => UX == 0 && UY == 0 && UZ == 0 && RX == 0 && RY == 0 && RZ == 0;
}

/// <summary>The generator flag of a configuration: <see cref="Mcotis.Core.GeneratorOrigin"/> as it is saved, with the
/// generation key of each object the generator made, by name (<see cref="Joints"/>, <see cref="Members"/>). An object the
/// maps do not name was not made by the generator.</summary>
public sealed record FemGeneratorDto(string Id, int Version, Dictionary<string, string> Parameters)
{
    public Dictionary<string, string>? Joints { get; init; }
    public Dictionary<string, string>? Members { get; init; }

    public Mcotis.Core.GeneratorOrigin ToNative() => Mcotis.Core.GeneratorOrigin.Create(Id, Version, Parameters);
    public static FemGeneratorDto FromNative(Mcotis.Core.GeneratorOrigin origin) =>
        new(origin.GeneratorId, origin.Version, new Dictionary<string, string>(origin.Parameters, StringComparer.Ordinal));
}

public sealed record FemJointRestraintDto(string JointName, bool Ux, bool Uy, bool Uz, bool Rx, bool Ry, bool Rz)
{
    public Mcotis.Core.Restraint ToNative() => new(Ux, Uy, Uz, Rx, Ry, Rz);
    public static FemJointRestraintDto FromNative(string jointName, Mcotis.Core.Restraint restraint) =>
        new(jointName, restraint.Ux, restraint.Uy, restraint.Uz, restraint.Rx, restraint.Ry, restraint.Rz);
}

public sealed class FemSettingsDto
{
    public double TargetFitPercent { get; set; } = 10;
    public int TargetFitIterations { get; set; } = 100;
    public int FrameMeshSegments { get; set; } = 10;
    public bool CableSag { get; set; } = true;
    /// <summary>The degrees of freedom of the model, by name (UX … RZ). Null in a file written before they were an
    /// option: the application then takes the XZ plane the file was analysed in, or — for the bundled default model —
    /// the degrees of freedom a model starts with.</summary>
    public List<string>? ActiveDofs { get; set; }
    /// <summary>Those of a joint only cables reach. Null as <see cref="ActiveDofs"/>.</summary>
    public List<string>? CableJointDofs { get; set; }
}

public static class FemModelFile
{
    public const string Filter = "Mcotis FEM Model (*.cotfem.json)|*.cotfem.json|JSON Files (*.json)|*.json";
    public const string Extension = ".cotfem.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Load case / combination terms are value tuples (fields); cable section properties carry NaN modifiers.
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes the model as a file of the current version, whole: every row with its identity, every case
    /// that names another referring to it by that identity. What cannot be written so is refused, and nothing is written.</summary>
    public static void Write(string path, FemModelFileDto model)
    {
        (model.CaseRows, model.CombinationRows) = FemCaseSchema.Complete(model.CaseRows, model.CombinationRows, scope: null);
        FemCaseSchema.Validate(model.CaseRows, model.CombinationRows, model.MassSources ?? []);
        model.Version = FemModelFileDto.CurrentVersion;
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(model, Json));
    }

    public static string Serialize(FemModelFileDto model) => JsonSerializer.Serialize(model, Json);

    public static FemModelFileDto Read(string path) => Parse(File.ReadAllText(path));

    public static FemModelFileDto Parse(string text)
    {
        FemModelFileDto? model;
        try { model = JsonSerializer.Deserialize<FemModelFileDto>(text, Json); }
        catch (JsonException ex) { throw new InvalidDataException($"The selected file is not a valid Mcotis FEM model ({ex.Message})."); }
        if (model is null || model.Format is not (FemModelFileDto.FormatName or FemModelFileDto.LegacyFormatName))
            throw new InvalidDataException("The selected file is not a Mcotis FEM model. Use Save Model on 07 FEM → Inputs to create one.");
        if (model.Version > FemModelFileDto.CurrentVersion)
            throw new InvalidDataException($"The FEM model was saved by a newer version of the app (format {model.Version}).");
        if (model.Model is null || model.Geometry is not { Points.Count: > 0 } || model.Baseline is not { Points.Count: > 0 } || model.Project is null)
            throw new InvalidDataException("The FEM model file is incomplete: it has no SAP model, geometry or Tabs 01–05 project.");
        if (model.SectionProperties.Count == 0 || model.Loading is null)
            throw new InvalidDataException("The FEM model file has no section properties or SAP loading.");
        // A file of version 1 names the case a case continues from; version 2 refers to it by identity. The upgrade
        // is made on what was read — the file on disk is not written — and what it cannot resolve is refused.
        if (model.Version < FemModelFileDto.CurrentVersion)
            (model.CaseRows, model.CombinationRows) = FemCaseSchema.Complete(model.CaseRows, model.CombinationRows, scope: null);
        FemCaseSchema.Validate(model.CaseRows, model.CombinationRows, model.MassSources ?? []);
        model.Version = FemModelFileDto.CurrentVersion;
        return model;
    }
}
