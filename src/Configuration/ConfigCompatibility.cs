using COT_VCF.Domain;

namespace COT_VCF.Configuration;

public sealed record PointCoordinateMismatch(string PointName, CoordinateDto Saved, CoordinateDto Current)
{
    public override string ToString() => $"{PointName}\n  Saved:   {Saved}\n  Current: {Current}";
}

/// <summary>Schema + model-compatibility result for one tab section. A tab loads only when it is present and clean.</summary>
public sealed class TabCheck
{
    public bool Present { get; set; }
    public List<string> Problems { get; } = new();
    public bool Ok => Present && Problems.Count == 0;
}

/// <summary>
/// Per-tab validation of a loaded config against the connected SAP model. Whole-file problems
/// (bad version, not a config) go in <see cref="FileErrors"/> and abort the load; otherwise each present
/// tab is checked independently so the compatible tabs can still load.
/// </summary>
public sealed class ConfigValidationResult
{
    public List<string> FileErrors { get; } = new();
    public bool FileNameMatches { get; set; }
    public TabCheck Loading { get; } = new();
    public TabCheck Truss { get; } = new();
    public TabCheck Cable { get; } = new();

    public bool FileValid => FileErrors.Count == 0;
    public bool AnyPresent => Loading.Present || Truss.Present || Cable.Present;
    public bool AnyOk => Loading.Ok || Truss.Ok || Cable.Ok;
    public bool AllPresentOk => AnyPresent
        && (!Loading.Present || Loading.Ok) && (!Truss.Present || Truss.Ok) && (!Cable.Present || Cable.Ok);
    public bool ModelAppearsRenamed => FileValid && AnyOk && !FileNameMatches;
}

public sealed class ConfigCompatibilityValidator
{
    public const double CoordinateToleranceM = 0.001; // 1 mm
    private static readonly string[] SupportedVersions = { "1.0" };

    public ConfigValidationResult Validate(CotVcfConfigDto cfg, ApplicationState state, IReadOnlyCollection<string> modelLoadCases)
    {
        var r = new ConfigValidationResult();
        if (cfg is null) { r.FileErrors.Add("The file is not a COT VCF configuration."); return r; }
        if (string.IsNullOrWhiteSpace(cfg.ConfigVersion)) r.FileErrors.Add("Missing configVersion.");
        else if (Array.IndexOf(SupportedVersions, cfg.ConfigVersion) < 0) r.FileErrors.Add($"Unsupported configVersion '{cfg.ConfigVersion}'.");
        if (cfg.PresentTabs == ConfigTabs.None) r.FileErrors.Add("The file contains no tab configuration to load.");
        if (!r.FileValid) return r;

        var currentFile = state.Model is { } m ? System.IO.Path.GetFileName(m.Path) : "";
        r.FileNameMatches = string.Equals(currentFile, cfg.Model?.FileName ?? "", StringComparison.OrdinalIgnoreCase);

        var savedPoints = (cfg.Model?.Points ?? []).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var savedCables = (cfg.Model?.Cables ?? []).ToDictionary(c => c.Name, StringComparer.Ordinal);
        var current = state.Geometry.Points;
        var currentCables = state.Geometry.Members.Where(mm => mm.Kind == MemberKind.Cable).ToDictionary(mm => mm.Name, StringComparer.Ordinal);
        var patterns = new HashSet<string>(state.LoadPatterns, StringComparer.Ordinal);
        var caseOrPattern = new HashSet<string>(modelLoadCases, StringComparer.Ordinal);
        caseOrPattern.UnionWith(state.LoadPatterns); // the app assigns PRETENSION as a load pattern

        void CheckPoint(TabCheck tc, string name)
        {
            if (!current.TryGetValue(name, out var cur)) { tc.Problems.Add($"missing point {name}"); return; }
            if (savedPoints.TryGetValue(name, out var sp) && !CloseXyz(cur.Position, sp.X, sp.Y, sp.Z))
                tc.Problems.Add(new PointCoordinateMismatch(name,
                    new() { X = sp.X, Y = sp.Y, Z = sp.Z },
                    new() { X = cur.Position.X, Y = cur.Position.Y, Z = cur.Position.Z }).ToString());
        }

        // ---- Tab 02 Loading ----
        if (cfg.Tab02Loading is { } t2)
        {
            var tc = r.Loading; tc.Present = true;
            foreach (var row in t2.LoadDefinitions ?? [])
            {
                var tag = Name(row.LoadName);
                if (row.IncrementEnabled)
                {
                    if (row.MinQz is null || row.MaxQz is null || row.StepQz is null) tc.Problems.Add($"{tag}: range needs Min, Max and Step qz.");
                    else if (row.StepQz <= 0) tc.Problems.Add($"{tag}: step qz must be greater than zero.");
                    else if (row.MaxQz < row.MinQz) tc.Problems.Add($"{tag}: max qz must be at least min qz.");
                }
                else if (row.FixedQz is null || !double.IsFinite(row.FixedQz.Value)) tc.Problems.Add($"{tag}: a fixed qz value is required.");
            }
            foreach (var name in (t2.AppliedPoints ?? []).Distinct(StringComparer.Ordinal)) CheckPoint(tc, name);
            foreach (var pat in savedPoints.Count == 0 ? [] : (cfg.Model?.LoadPatterns ?? []))
                if (!patterns.Contains(pat)) tc.Problems.Add($"missing load pattern {pat}");
        }

        // ---- Tab 03 Truss Height ----
        if (cfg.Tab03TrussHeight is { } t3)
        {
            var tc = r.Truss; tc.Present = true;
            if (!Enum.IsDefined(t3.ReferenceElevation)) tc.Problems.Add("reference elevation is invalid.");
            if ((t3.TopPoints?.Count ?? 0) == 0 || (t3.MiddlePoints?.Count ?? 0) == 0 || (t3.BottomPoints?.Count ?? 0) == 0)
                tc.Problems.Add("top, middle and bottom point groups are all required.");
            if (!(t3.StepDeltaH > 0)) tc.Problems.Add("step ΔH must be greater than zero.");
            if (t3.MaxDeltaH < t3.MinDeltaH) tc.Problems.Add("max ΔH must be at least min ΔH.");
            foreach (var name in Concat(t3.TopPoints, t3.MiddlePoints, t3.BottomPoints)) CheckPoint(tc, name);
        }

        // ---- Tab 04 Cable Force ----
        if (cfg.Tab04CableForce is { } t4)
        {
            var tc = r.Cable; tc.Present = true;
            foreach (var row in t4.Cables ?? [])
            {
                var tag = Name(row.CableName);
                if (string.IsNullOrWhiteSpace(row.PretensionLoadCase)) tc.Problems.Add($"{tag}: pretension load case is required.");
                else if (!caseOrPattern.Contains(row.PretensionLoadCase)) tc.Problems.Add($"{tag}: load case {row.PretensionLoadCase} does not exist in the model.");
                if (row.Mode == CableForceMode.Range)
                {
                    if (row.MinForce is null || row.MaxForce is null || row.StepForce is null) tc.Problems.Add($"{tag}: range needs Min, Max and Step force.");
                    else if (row.StepForce <= 0) tc.Problems.Add($"{tag}: step force must be greater than zero.");
                    else if (row.MaxForce < row.MinForce) tc.Problems.Add($"{tag}: max force must be at least min force.");
                }
                else if (row.FixedForce is null || !double.IsFinite(row.FixedForce.Value)) tc.Problems.Add($"{tag}: a fixed target force is required.");

                if (!currentCables.TryGetValue(row.CableName, out var cm)) { tc.Problems.Add($"missing cable {row.CableName}"); continue; }
                if (savedCables.TryGetValue(row.CableName, out var sc))
                {
                    if (cm.Start != sc.PointI || cm.End != sc.PointJ)
                        tc.Problems.Add($"{row.CableName}: endpoints changed (saved {sc.PointI}–{sc.PointJ}, now {cm.Start}–{cm.End})");
                    else
                    {
                        if (current.TryGetValue(cm.Start, out var pi) && !CloseCoord(pi.Position, sc.PointICoordinate)) tc.Problems.Add($"{row.CableName}: I-end {cm.Start} moved");
                        if (current.TryGetValue(cm.End, out var pj) && !CloseCoord(pj.Position, sc.PointJCoordinate)) tc.Problems.Add($"{row.CableName}: J-end {cm.End} moved");
                    }
                }
            }
        }

        return r;
    }

    private static IEnumerable<string> Concat(params List<string>?[] groups) =>
        groups.SelectMany(g => g ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal);
    private static string Name(string s) => string.IsNullOrWhiteSpace(s) ? "(unnamed)" : s;
    private static bool Close(double a, double b) => Math.Abs(a - b) <= CoordinateToleranceM;
    private static bool CloseXyz(Position p, double x, double y, double z) => Close(p.X, x) && Close(p.Y, y) && Close(p.Z, z);
    private static bool CloseCoord(Position p, CoordinateDto c) => Close(p.X, c.X) && Close(p.Y, c.Y) && Close(p.Z, c.Z);
}
