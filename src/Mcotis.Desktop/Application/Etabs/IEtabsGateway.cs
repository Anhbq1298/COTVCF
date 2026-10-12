namespace Mcotis.Desktop.Application.Etabs;

/// <summary>The model open in ETABS, as its database tables (see <see cref="EtabsTableReader"/>). <see cref="FilePath"/>
/// is empty for a model ETABS has not saved.</summary>
public sealed record EtabsModelExport(string FilePath, string ProgramVersion, string TablesText);

/// <summary>The running ETABS. It attaches to the instance that is open — it never starts ETABS. Reading never edits,
/// unlocks, saves or changes the units of its model; <see cref="ExportModelAsync"/> (File → Export to ETABS, after its
/// preview and the user's confirmation) is the only write, and it does not save.</summary>
public interface IEtabsGateway
{
    Task<EtabsModelExport> ExportActiveModelAsync();

    /// <summary>Checks the export against the model open in ETABS and lists what it would change. Writes nothing.</summary>
    Task<EtabsExportReport> PreviewExportAsync(EtabsExportModel model) =>
        throw new NotSupportedException("Export to ETABS is not available in this session.");

    /// <summary>Writes the export into the ETABS model it was previewed against (unlocking it when <paramref name="unlock"/>).
    /// Not saved.</summary>
    Task<EtabsExportReport> ExportModelAsync(EtabsExportModel model, EtabsExportTarget expected, bool unlock) =>
        throw new NotSupportedException("Export to ETABS is not available in this session.");
}

/// <summary>No ETABS is running (or none has a model open), so there is nothing to import from.</summary>
public sealed class EtabsNotRunningException(string message) : InvalidOperationException(message);
