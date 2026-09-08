using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using COT_VCF.Application;
using COT_VCF.Domain;
using COT_VCF.Infrastructure;

namespace COT_VCF.Configuration;

public enum SaveResult { Saved, Cancelled, Blocked }
public readonly record struct SaveOutcome(SaveResult Result, string Message);

/// <summary>
/// Owns the active COT VCF <b>project</b> (see <see cref="Session"/>). Load = open a project;
/// per-tab save overwrites only its section of the in-memory project, then writes the whole file back;
/// Save All extracts every tab from the UI and rewrites the project atomically. Never shows dialogs —
/// the caller supplies a Save-As path picker.
/// </summary>
public sealed class ConfigService(ISapGateway sap, ApplicationState state, WorkflowService workflow)
{
    public ConfigProjectSession Session { get; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public bool CanUse => state.IsSapConnected && state.Model is not null;

    public string SuggestFileName(ConfigTabs tabs)
    {
        var stem = state.Model is { } m && !string.IsNullOrWhiteSpace(m.Path) ? Path.GetFileNameWithoutExtension(m.Path) : "COTVCF";
        var suffix = tabs switch
        {
            ConfigTabs.Loading => "Tab01_Loading",
            ConfigTabs.Truss => "Tab02_TrussHeight",
            ConfigTabs.Cable => "Tab03_CableForce",
            ConfigTabs.Output => "Tab04_OutputSettings",
            ConfigTabs.Study => "Tab05_Study",
            _ => "Config",
        };
        return $"{stem}_COTVCF_{suffix}.json";
    }

    // ---------- open ----------
    public CotVcfConfigDto Read(string path)
    {
        CotVcfConfigDto? project;
        try { project = JsonSerializer.Deserialize<CotVcfConfigDto>(File.ReadAllText(path), Json); }
        catch (JsonException) { throw new InvalidDataException("The selected file is not a valid COT VCF configuration file."); }
        return project ?? throw new InvalidDataException("The selected file is not a valid COT VCF configuration file.");
    }

    public async Task<ConfigValidationResult> ValidateAsync(CotVcfConfigDto project)
    {
        var model = state.Model;
        IReadOnlyList<string> cases = model is null ? Array.Empty<string>() : await sap.GetLoadCaseNamesAsync(model);
        return new ConfigCompatibilityValidator().Validate(project, state, cases);
    }

    public void SetActiveProject(string path, CotVcfConfigDto project) => Session.Open(path, project);
    public void Apply(CotVcfConfigDto project, ConfigValidationResult check) =>
        new ConfigurationApplicator(state, workflow).Apply(project, check);

    // ---------- save (per-tab or all) ----------
    /// <param name="uiTabs">Which tab sections come from the current UI. All others are kept from the active project.</param>
    /// <param name="pickPath">Returns a Save-As path (or null to cancel) when there is no active project.</param>
    public async Task<SaveOutcome> SaveAsync(ConfigTabs uiTabs, Func<string?>? pickPath)
    {
        if (!CanUse) return new(SaveResult.Blocked, "Connect to a SAP2000 model before saving a configuration.");
        var cases = await sap.GetLoadCaseNamesAsync(state.Model!);
        var extractor = new ConfigurationExtractor();

        var project = Session.Project is { } active ? Clone(active) : new CotVcfConfigDto { ConfigVersion = "1.0", Units = new() };
        if (uiTabs.HasFlag(ConfigTabs.Loading)) project.Tab02Loading = extractor.ExtractLoadingSection(state);
        if (uiTabs.HasFlag(ConfigTabs.Truss)) project.Tab03TrussHeight = extractor.ExtractTrussSection(state);
        if (uiTabs.HasFlag(ConfigTabs.Cable)) project.Tab04CableForce = extractor.ExtractCableSection(state);
        if (uiTabs.HasFlag(ConfigTabs.Output)) project.Tab05OutputSettings = extractor.ExtractOutputSection(state);
        if (uiTabs.HasFlag(ConfigTabs.Study)) project.Tab05IterationStudy = extractor.ExtractStudySection(state);

        var effective = ConfiguredIn(project);
        if (uiTabs == ConfigTabs.Study && !effective.HasFlag(ConfigTabs.Study))
            return new(SaveResult.Blocked, "Save the Loading, Truss Height and Cable Force sections before saving the iteration study.");
        if (effective == ConfigTabs.None) return new(SaveResult.Cancelled, "Nothing configured to save yet.");
        project.Model = extractor.RebuildSnapshot(project, state, cases);

        // Whole active project must stay compatible with the connected model (§9 / §29 / §16).
        var check = new ConfigCompatibilityValidator().Validate(project, state, cases);
        if (!check.FileValid || !check.AllPresentOk)
            return new(SaveResult.Blocked, BlockReport(check));

        string path;
        if (Session.HasActiveProject) path = Session.FilePath!;
        else
        {
            var picked = pickPath?.Invoke();
            if (string.IsNullOrWhiteSpace(picked)) return new(SaveResult.Cancelled, "");
            path = picked!;
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(project, Json));
        // In-memory project == what is on disk; only the tabs written from the UI become clean.
        Session.SetProject(path, project);
        Session.ClearDirty(uiTabs & effective);
        return new(SaveResult.Saved, "");
    }

    private static ConfigTabs ConfiguredIn(CotVcfConfigDto p)
    {
        var t = ConfigTabs.None;
        if (p.Tab02Loading is { AppliedPoints.Count: > 0 } or { LoadDefinitions.Count: > 0 }) t |= ConfigTabs.Loading;
        if (p.Tab03TrussHeight is { } h && ((h.TopPoints?.Count ?? 0) > 0 || (h.MiddlePoints?.Count ?? 0) > 0 || (h.BottomPoints?.Count ?? 0) > 0)) t |= ConfigTabs.Truss;
        if (p.Tab04CableForce is { Cables.Count: > 0 }) t |= ConfigTabs.Cable;
        if (p.Tab05OutputSettings is { } output &&
            ((output.DeflectionPointNames?.Count ?? 0) > 0 || (output.MemberForceGroups?.Count ?? 0) > 0 ||
             (output.FoundationLeftPointNames?.Count ?? 0) > 0 || (output.FoundationRightPointNames?.Count ?? 0) > 0 ||
             (output.CableNames?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(output.CableResultLoadCase)))
            t |= ConfigTabs.Output;
        if (p.Tab05IterationStudy is not null && t.HasFlag(ConfigTabs.Loading) && t.HasFlag(ConfigTabs.Truss) && t.HasFlag(ConfigTabs.Cable))
            t |= ConfigTabs.Study;
        // Drop empty sections so the file only carries configured tabs.
        if (!t.HasFlag(ConfigTabs.Loading)) p.Tab02Loading = null;
        if (!t.HasFlag(ConfigTabs.Truss)) p.Tab03TrussHeight = null;
        if (!t.HasFlag(ConfigTabs.Cable)) p.Tab04CableForce = null;
        if (!t.HasFlag(ConfigTabs.Output)) p.Tab05OutputSettings = null;
        if (!t.HasFlag(ConfigTabs.Study)) p.Tab05IterationStudy = null;
        return t;
    }

    private static string BlockReport(ConfigValidationResult r)
    {
        var lines = new List<string>();
        if (r.FileErrors.Count > 0) lines.Add(string.Join("\n", r.FileErrors.Select(e => "• " + e)));
        void Add(string name, TabCheck tc) { if (tc.Present && !tc.Ok) lines.Add($"{name}:\n" + string.Join("\n", tc.Problems.Select(p => "  • " + p))); }
        Add("Tab 01 – Loading", r.Loading);
        Add("Tab 02 – Truss Height", r.Truss);
        Add("Tab 03 – Cable Force", r.Cable);
        Add("Tab 04 – Output Settings", r.Output);
        Add("Tab 05 - Iteration Study", r.Study);
        return "Unable to save the configuration:\n\n" + string.Join("\n\n", lines) + "\n\nNo changes have been written to the project file.";
    }

    private static CotVcfConfigDto Clone(CotVcfConfigDto d) =>
        JsonSerializer.Deserialize<CotVcfConfigDto>(JsonSerializer.Serialize(d, Json), Json)!;
}
