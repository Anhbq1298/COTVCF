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
    public const int CurrentVersion = 1;

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
    public FemSettingsDto Settings { get; set; } = new();
}

public sealed record FemPatternRowDto(string Name, string Type, double SelfWeightMultiplier, string Role, string Loads);
public sealed record FemDefinitionRowDto(string Name, string Type, string Definition, string? InitialCase, bool Run, bool Supported, string Source, string Note, bool? CableTargetForces = null);

public sealed class FemSettingsDto
{
    public double TargetFitPercent { get; set; } = 10;
    public int TargetFitIterations { get; set; } = 100;
    public int FrameMeshSegments { get; set; } = 10;
    public bool CableSag { get; set; } = true;
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

    public static void Write(string path, FemModelFileDto model) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(model, Json));

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
        return model;
    }
}
