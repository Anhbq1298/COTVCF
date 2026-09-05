using COT_VCF.Domain;

namespace COT_VCF.Configuration;

// Pure data contract for Save/Load Config. No UI, no SAP COM types, no generated candidates,
// no analysis results. Internal units are fixed (m / kN / kPa) regardless of SAP present units.

public enum CableForceMode { Fixed, Range }

public sealed class CotVcfConfigDto
{
    public string ConfigVersion { get; set; } = "1.0";
    public UnitsDto Units { get; set; } = new();
    public ModelSnapshotDto Model { get; set; } = new();
    public LoadingConfigDto Tab02Loading { get; set; } = new();
    public TrussHeightConfigDto Tab03TrussHeight { get; set; } = new();
    public CableForceConfigDto Tab04CableForce { get; set; } = new();
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
    public string FileName { get; set; } = "";
    public List<PointSnapshotDto> Points { get; set; } = new();
    public List<CableSnapshotDto> Cables { get; set; } = new();
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
    public string PretensionLoadCase { get; set; } = "";
    public CableForceMode Mode { get; set; }
    public double? FixedForce { get; set; }
    public double? MinForce { get; set; }
    public double? MaxForce { get; set; }
    public double? StepForce { get; set; }
}
