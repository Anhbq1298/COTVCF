using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using COT_VCF.Application;
using COT_VCF.Domain;

namespace COT_VCF.Configuration;

/// <summary>
/// Facade the UI uses for Save / Load Config. Orchestrates extract → validate → apply and owns JSON IO.
/// Never shows dialogs and never mutates <see cref="ApplicationState"/> until both validation stages pass.
/// </summary>
public sealed class ConfigService(ISapGateway sap, ApplicationState state, WorkflowService workflow)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public bool CanUse => state.IsSapConnected && state.Model is not null;

    public string SuggestFileName() => state.Model is { } m && !string.IsNullOrWhiteSpace(m.Path)
        ? Path.GetFileNameWithoutExtension(m.Path) + "_COTVCF_Config.json"
        : "COTVCF_Config.json";

    /// <summary>Extract the current Tabs 02–04 configuration plus a SAP dependency snapshot. Read-only.</summary>
    public async Task<CotVcfConfigDto> BuildAsync()
    {
        if (!CanUse) throw new InvalidOperationException("Connect to a SAP2000 model before saving a configuration.");
        var cases = await sap.GetLoadCaseNamesAsync(state.Model!);
        return new ConfigurationExtractor().Extract(state, cases);
    }

    public void Save(string path, CotVcfConfigDto config) => File.WriteAllText(path, JsonSerializer.Serialize(config, Json));

    public CotVcfConfigDto Read(string path)
    {
        CotVcfConfigDto? config;
        try { config = JsonSerializer.Deserialize<CotVcfConfigDto>(File.ReadAllText(path), Json); }
        catch (JsonException) { throw new InvalidDataException("The selected file is not a valid COT VCF configuration file."); }
        return config ?? throw new InvalidDataException("The selected file is not a valid COT VCF configuration file.");
    }

    public async Task<ConfigValidationResult> ValidateAsync(CotVcfConfigDto config)
    {
        var model = state.Model;
        IReadOnlyList<string> cases = model is null ? Array.Empty<string>() : await sap.GetLoadCaseNamesAsync(model);
        return new ConfigCompatibilityValidator().Validate(config, state, cases);
    }

    /// <summary>Commit a validated config. Call only after <see cref="ValidateAsync"/> returns IsCompatible.</summary>
    public void Apply(CotVcfConfigDto config) => new ConfigurationApplicator(state, workflow).Apply(config);
}
