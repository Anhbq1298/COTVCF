using System.Text.Json.Serialization;
using COT_VCF.Domain;

namespace COT_VCF.Configuration;

// Pure data contract for Save/Load Config. No UI, no SAP COM types, no generated candidates,
// no analysis results. Internal units are fixed (m / kN / kPa) regardless of SAP present units.

public enum CableForceMode { Fixed, Range }

/// <summary>Which tab sections a save/load touches. A file may carry any subset.</summary>
[Flags]
public enum ConfigTabs { None = 0, Loading = 1, Truss = 2, Cable = 4, Study = 8, Output = 16, All = Loading | Truss | Cable | Output | Study }

public sealed class CotVcfConfigDto
{
    public string ConfigVersion { get; set; } = "1.0";
    public UnitsDto Units { get; set; } = new();
    public ModelSnapshotDto Model { get; set; } = new();
    // A per-tab save leaves the other sections null; load processes each present section independently.
    public LoadingConfigDto? Tab02Loading { get; set; }
    public TrussHeightConfigDto? Tab03TrussHeight { get; set; }
    public CableForceConfigDto? Tab04CableForce { get; set; }
    public OutputSettingsConfigDto? Tab05OutputSettings { get; set; }
    // Legacy v1 JSON property name retained even though the study is now displayed as Tab 05.
    public IterationStudyConfigDto? Tab05IterationStudy { get; set; }

    public ConfigTabs PresentTabs =>
        (Tab02Loading is not null ? ConfigTabs.Loading : 0)
        | (Tab03TrussHeight is not null ? ConfigTabs.Truss : 0)
        | (Tab04CableForce is not null ? ConfigTabs.Cable : 0)
        | (Tab05OutputSettings is not null ? ConfigTabs.Output : 0)
        | (Tab05IterationStudy is not null ? ConfigTabs.Study : 0);
}

public sealed class UnitsDto
{
    public string Length { get; set; } = "m";
    public string Force { get; set; } = "kN";
    public string Pressure { get; set; } = "kPa";
}

public sealed class CoordinateDto
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public override string ToString() => $"X={X:F3}, Y={Y:F3}, Z={Z:F3} m";
}

// Lightweight fingerprint of the SAP dependencies the config actually references — used ONLY for
// compatibility validation on load, never re-applied to SAP.
public sealed class ModelSnapshotDto
{
    /// <summary>Model the project was first created against. Preserved across Save-As / renamed-model saves.</summary>
    public string OriginalFileName { get; set; } = "";
    /// <summary>Model the project was last validated / saved against (the effective "current" name).</summary>
    public string FileName { get; set; } = "";
    public List<PointSnapshotDto> Points { get; set; } = new();
    public List<CableSnapshotDto> Cables { get; set; } = new();
    public List<FrameSnapshotDto> Frames { get; set; } = new();
    public List<string> LoadPatterns { get; set; } = new();
    public List<string> LoadCases { get; set; } = new();
}

public sealed class PointSnapshotDto
{
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class CableSnapshotDto
{
    public string Name { get; set; } = "";
    public string PointI { get; set; } = "";
    public string PointJ { get; set; } = "";
    public CoordinateDto PointICoordinate { get; set; } = new();
    public CoordinateDto PointJCoordinate { get; set; } = new();
}

public sealed class FrameSnapshotDto
{
    public string Name { get; set; } = "";
    public string PointI { get; set; } = "";
    public string PointJ { get; set; } = "";
    public CoordinateDto PointICoordinate { get; set; } = new();
    public CoordinateDto PointJCoordinate { get; set; } = new();
}

public sealed class LoadingConfigDto
{
    public double TributaryWidth { get; set; }
    public double NumberOfStories { get; set; }
    public List<string> AppliedPoints { get; set; } = new();
    public List<LoadingRowDto> LoadDefinitions { get; set; } = new();
}

public sealed class LoadingRowDto
{
    public string LoadName { get; set; } = "";
    public bool IncrementEnabled { get; set; }
    public double? FixedQz { get; set; }
    public double? MinQz { get; set; }
    public double? MaxQz { get; set; }
    public double? StepQz { get; set; }
}

// ΔH values are relative to the BASELINE model geometry (the geometry captured when the model was
// first connected), never to a previewed candidate.
public sealed class TrussHeightConfigDto
{
    public List<string> TopPoints { get; set; } = new();
    public List<string> MiddlePoints { get; set; } = new();
    public List<string> BottomPoints { get; set; } = new();
    public ReferenceChord ReferenceElevation { get; set; } = ReferenceChord.Top;
    public double MinDeltaH { get; set; }
    public double MaxDeltaH { get; set; }
    public double StepDeltaH { get; set; }
}

public sealed class CableForceConfigDto
{
    public List<CableForceRowDto> Cables { get; set; } = new();
}

public sealed class CableForceRowDto
{
    public string CableName { get; set; } = "";
    // JSON name retained for version-1.0 compatibility; SAP CableObj.SetLoadTargetForce expects a load pattern.
    public string PretensionLoadCase { get; set; } = "";
    public CableForceMode Mode { get; set; }
    public double? FixedForce { get; set; }
    public double? MinForce { get; set; }
    public double? MaxForce { get; set; }
    public double? StepForce { get; set; }
}

public sealed class OutputSettingsConfigDto
{
    public string OutputGroupPrefix { get; set; } = OutputGroupNaming.Prefix;
    public List<string> DeflectionPointNames { get; set; } = new();
    public List<MemberForceGroupConfigDto> MemberForceGroups { get; set; } = new();
    public List<string> FoundationLeftPointNames { get; set; } = new();
    public List<string> FoundationRightPointNames { get; set; } = new();
    public List<string> CableNames { get; set; } = new();
    public string CableResultLoadCase { get; set; } = "";
    public string DeflectionOutputCaseName { get; set; } = "";
}

public sealed class MemberForceGroupConfigDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SapGroupName { get; set; } = "";
    public List<string> FrameNames { get; set; } = new();
}

public sealed class IterationStudyConfigDto
{
    public IterationStudyMode StudyMode { get; set; } = IterationStudyMode.FixPretension;
    public string FixedHeightCandidateId { get; set; } = "H01";
    public string FixedPretensionCandidateId { get; set; } = "P01";
    // Retained for version-1.0 compatibility. Runtime output is derived from the connected baseline path.
    public string OutputDirectory { get; set; } = "";
    public List<string> ExcludedIterationKeys { get; set; } = new();
}
