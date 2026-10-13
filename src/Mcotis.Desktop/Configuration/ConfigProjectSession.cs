namespace Mcotis.Desktop.Configuration;

/// <summary>
/// The active Mcotis project. Once <see cref="Open"/> is called the JSON file becomes a live document:
/// the in-memory <see cref="Project"/> is the source of truth, and per-tab saves overwrite only their
/// section of it before writing the whole file back to <see cref="FilePath"/>.
/// </summary>
public sealed class ConfigProjectSession
{
    public string? FilePath { get; private set; }
    public McotisConfigDto? Project { get; private set; }

    public bool Tab02Dirty { get; private set; }
    public bool Tab03Dirty { get; private set; }
    public bool Tab04Dirty { get; private set; }
    public bool Tab05Dirty { get; private set; }

    public bool HasActiveProject => Project is not null && !string.IsNullOrWhiteSpace(FilePath);
    public string? FileName => FilePath is null ? null : System.IO.Path.GetFileName(FilePath);

    public void Close()
    {
        FilePath = null;
        Project = null;
        ClearDirty();
    }

    /// <summary>Open a freshly loaded project — everything starts clean.</summary>
    public void Open(string path, McotisConfigDto project)
    {
        FilePath = path;
        Project = project;
        ClearDirty();
    }

    /// <summary>Point the session at the just-written project without touching dirty flags
    /// (a per-tab save clears only its own tab afterwards).</summary>
    public void SetProject(string path, McotisConfigDto project)
    {
        FilePath = path;
        Project = project;
    }

    public void MarkDirty(ConfigTabs tab)
    {
        if (tab.HasFlag(ConfigTabs.Loading)) Tab02Dirty = true;
        if (tab.HasFlag(ConfigTabs.Truss)) Tab03Dirty = true;
        if (tab.HasFlag(ConfigTabs.Cable)) Tab04Dirty = true;
        if (tab.HasFlag(ConfigTabs.Study)) Tab05Dirty = true;
    }

    public void ClearDirty(ConfigTabs tabs = ConfigTabs.All)
    {
        if (tabs.HasFlag(ConfigTabs.Loading)) Tab02Dirty = false;
        if (tabs.HasFlag(ConfigTabs.Truss)) Tab03Dirty = false;
        if (tabs.HasFlag(ConfigTabs.Cable)) Tab04Dirty = false;
        if (tabs.HasFlag(ConfigTabs.Study)) Tab05Dirty = false;
    }

    public bool IsDirty(ConfigTabs tab) => tab switch
    {
        ConfigTabs.Loading => Tab02Dirty,
        ConfigTabs.Truss => Tab03Dirty,
        ConfigTabs.Cable => Tab04Dirty,
        ConfigTabs.Study => Tab05Dirty,
        _ => Tab02Dirty || Tab03Dirty || Tab04Dirty || Tab05Dirty,
    };

    public bool ProjectHas(ConfigTabs tab) => Project is { } p && tab switch
    {
        ConfigTabs.Loading => p.Tab02Loading is not null,
        ConfigTabs.Truss => p.Tab03TrussHeight is not null,
        ConfigTabs.Cable => p.Tab04CableForce is not null,
        ConfigTabs.Study => p.Tab05IterationStudy is not null,
        _ => p.PresentTabs != ConfigTabs.None,
    };
}
