using COT_VCF.Domain;

namespace COT_VCF.Configuration;

public sealed record PointCoordinateMismatch(string PointName, CoordinateDto Saved, CoordinateDto Current);
public sealed record CableGeometryMismatch(string CableName, string Detail);

/// <summary>Every problem found while checking a loaded config against the current SAP model.
/// The validator collects them all; the caller shows one consolidated message.</summary>
public sealed class ConfigValidationResult
{
    public List<string> SchemaErrors { get; } = new();
    public bool FileNameMatches { get; set; }
    public List<string> MissingPoints { get; } = new();
    public List<PointCoordinateMismatch> PointCoordinateMismatches { get; } = new();
    public List<string> MissingCables { get; } = new();
    public List<CableGeometryMismatch> CableGeometryMismatches { get; } = new();
    public List<string> MissingLoadPatterns { get; } = new();
    public List<string> MissingLoadCases { get; } = new();

    public bool SchemaValid => SchemaErrors.Count == 0;
    public bool ModelValid => MissingPoints.Count == 0 && PointCoordinateMismatches.Count == 0
        && MissingCables.Count == 0 && CableGeometryMismatches.Count == 0
        && MissingLoadPatterns.Count == 0 && MissingLoadCases.Count == 0;
    public bool IsCompatible => SchemaValid && ModelValid;
    public bool ModelAppearsRenamed => IsCompatible && !FileNameMatches;
}

/// <summary>
/// Two stages: schema/range validation of the DTO itself, then a deep read-only comparison of every
/// referenced SAP object (points, coordinates, cable endpoints, load patterns / cases) against the
/// currently connected model. A different file name is not a hard block — only a content mismatch is.
/// </summary>
public sealed class ConfigCompatibilityValidator
{
    public const double CoordinateToleranceM = 0.001; // 1 mm
    private static readonly string[] SupportedVersions = { "1.0" };

    public ConfigValidationResult Validate(CotVcfConfigDto cfg, ApplicationState state, IReadOnlyCollection<string> modelLoadCases)
    {
        var r = new ConfigValidationResult();
        ValidateSchema(cfg, r);
        if (!r.SchemaValid) return r;

        var model = cfg.Model!;
        var currentFile = state.Model is { } m ? System.IO.Path.GetFileName(m.Path) : "";
        r.FileNameMatches = string.Equals(currentFile, model.FileName, StringComparison.OrdinalIgnoreCase);

        var points = state.Geometry.Points;
        foreach (var sp in model.Points ?? [])
        {
            if (!points.TryGetValue(sp.Name, out var cur)) { r.MissingPoints.Add(sp.Name); continue; }
            if (!Close(cur.Position.X, sp.X) || !Close(cur.Position.Y, sp.Y) || !Close(cur.Position.Z, sp.Z))
                r.PointCoordinateMismatches.Add(new(sp.Name,
                    new() { X = sp.X, Y = sp.Y, Z = sp.Z },
                    new() { X = cur.Position.X, Y = cur.Position.Y, Z = cur.Position.Z }));
        }

        var cables = state.Geometry.Members.Where(m => m.Kind == MemberKind.Cable).ToDictionary(m => m.Name, StringComparer.Ordinal);
        foreach (var sc in model.Cables ?? [])
        {
            if (!cables.TryGetValue(sc.Name, out var cm)) { r.MissingCables.Add(sc.Name); continue; }
            // I/J orientation is significant for the target-force assignment — require exact endpoint identity.
            if (cm.Start != sc.PointI || cm.End != sc.PointJ)
            {
                r.CableGeometryMismatches.Add(new(sc.Name, $"endpoints changed (saved {sc.PointI}–{sc.PointJ}, now {cm.Start}–{cm.End})"));
                continue;
            }
            if (points.TryGetValue(cm.Start, out var pi) && !CloseCoord(pi.Position, sc.PointICoordinate))
                r.CableGeometryMismatches.Add(new(sc.Name, $"I-end {cm.Start} moved"));
            if (points.TryGetValue(cm.End, out var pj) && !CloseCoord(pj.Position, sc.PointJCoordinate))
                r.CableGeometryMismatches.Add(new(sc.Name, $"J-end {cm.End} moved"));
        }

        var patterns = new HashSet<string>(state.LoadPatterns, StringComparer.Ordinal);
        foreach (var lp in model.LoadPatterns ?? [])
            if (!patterns.Contains(lp)) r.MissingLoadPatterns.Add(lp);

        var caseOrPattern = new HashSet<string>(modelLoadCases, StringComparer.Ordinal);
        caseOrPattern.UnionWith(state.LoadPatterns); // the app assigns PRETENSION as a load pattern
        foreach (var lc in model.LoadCases ?? [])
            if (!caseOrPattern.Contains(lc)) r.MissingLoadCases.Add(lc);

        return r;
    }

    private static void ValidateSchema(CotVcfConfigDto cfg, ConfigValidationResult r)
    {
        if (cfg is null) { r.SchemaErrors.Add("The file is not a COT VCF configuration."); return; }
        if (string.IsNullOrWhiteSpace(cfg.ConfigVersion)) r.SchemaErrors.Add("Missing configVersion.");
        else if (Array.IndexOf(SupportedVersions, cfg.ConfigVersion) < 0) r.SchemaErrors.Add($"Unsupported configVersion '{cfg.ConfigVersion}'.");
        if (cfg.Model is null) r.SchemaErrors.Add("Missing model section.");
        if (cfg.Units is null) r.SchemaErrors.Add("Missing units section.");
        if (cfg.Tab02Loading is null) r.SchemaErrors.Add("Missing tab02Loading section.");
        if (cfg.Tab03TrussHeight is null) r.SchemaErrors.Add("Missing tab03TrussHeight section.");
        if (cfg.Tab04CableForce is null) r.SchemaErrors.Add("Missing tab04CableForce section.");
        if (!r.SchemaValid) return;

        foreach (var row in cfg.Tab02Loading!.LoadDefinitions ?? [])
        {
            var tag = $"Tab 02 · {Name(row.LoadName)}";
            if (row.IncrementEnabled)
            {
                if (row.MinQz is null || row.MaxQz is null || row.StepQz is null) r.SchemaErrors.Add($"{tag}: range needs Min, Max and Step qz.");
                else if (row.StepQz <= 0) r.SchemaErrors.Add($"{tag}: step qz must be greater than zero.");
                else if (row.MaxQz < row.MinQz) r.SchemaErrors.Add($"{tag}: max qz must be at least min qz.");
            }
            else if (row.FixedQz is null || !double.IsFinite(row.FixedQz.Value)) r.SchemaErrors.Add($"{tag}: a fixed qz value is required.");
        }

        var t3 = cfg.Tab03TrussHeight!;
        if (!Enum.IsDefined(t3.ReferenceElevation)) r.SchemaErrors.Add("Tab 03: reference elevation is invalid.");
        if ((t3.TopPoints?.Count ?? 0) == 0 || (t3.MiddlePoints?.Count ?? 0) == 0 || (t3.BottomPoints?.Count ?? 0) == 0) r.SchemaErrors.Add("Tab 03: top, middle and bottom point groups are all required.");
        if (!(t3.StepDeltaH > 0)) r.SchemaErrors.Add("Tab 03: step ΔH must be greater than zero.");
        if (t3.MaxDeltaH < t3.MinDeltaH) r.SchemaErrors.Add("Tab 03: max ΔH must be at least min ΔH.");

        foreach (var row in cfg.Tab04CableForce!.Cables ?? [])
        {
            var tag = $"Tab 04 · {Name(row.CableName)}";
            if (string.IsNullOrWhiteSpace(row.PretensionLoadCase)) r.SchemaErrors.Add($"{tag}: pretension load case is required.");
            if (row.Mode == CableForceMode.Range)
            {
                if (row.MinForce is null || row.MaxForce is null || row.StepForce is null) r.SchemaErrors.Add($"{tag}: range needs Min, Max and Step force.");
                else if (row.StepForce <= 0) r.SchemaErrors.Add($"{tag}: step force must be greater than zero.");
                else if (row.MaxForce < row.MinForce) r.SchemaErrors.Add($"{tag}: max force must be at least min force.");
            }
            else if (row.FixedForce is null || !double.IsFinite(row.FixedForce.Value)) r.SchemaErrors.Add($"{tag}: a fixed target force is required.");
        }
    }

    private static string Name(string s) => string.IsNullOrWhiteSpace(s) ? "(unnamed)" : s;
    private static bool Close(double a, double b) => Math.Abs(a - b) <= CoordinateToleranceM;
    private static bool CloseCoord(Position p, CoordinateDto c) => Close(p.X, c.X) && Close(p.Y, c.Y) && Close(p.Z, c.Z);
}
