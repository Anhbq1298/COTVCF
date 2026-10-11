namespace COT_VCF.Application.Etabs;

/// <summary>The model open in ETABS, as its database tables (see <see cref="EtabsTableReader"/>). <see cref="FilePath"/>
/// is empty for a model ETABS has not saved.</summary>
public sealed record EtabsModelExport(string FilePath, string ProgramVersion, string TablesText);

/// <summary>Read-only access to the running ETABS. It attaches to the instance that is open — it never starts ETABS — and
/// never edits, unlocks, saves or changes the units of its model.</summary>
public interface IEtabsGateway
{
    Task<EtabsModelExport> ExportActiveModelAsync();
}

/// <summary>No ETABS is running (or none has a model open), so there is nothing to import from.</summary>
public sealed class EtabsNotRunningException(string message) : InvalidOperationException(message);
