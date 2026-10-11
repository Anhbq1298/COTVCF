using COT_VCF.Application.Etabs;
using COT_VCF.Infrastructure;
using CSiAPIv1;
using System.IO;
using System.Runtime.InteropServices;

namespace COT_VCF.SAP2000;

/// <summary>
/// File → Import From ETABS: attaches to the running ETABS through CSI's cross-product API (CSiAPIv1, ProgID
/// "CSI.ETABS.API.ETABSObject"; verified with SAP2000 27's CSiAPIv1 2.15 against ETABS 23.2) and exports the model's
/// database tables as text (<c>File.ExportFile</c>, <c>eFileTypeIO.DBTablesText</c>) to a private temporary folder,
/// which it reads and deletes. ETABS returns an error for the .e2k export (<c>eFileTypeIO.TextFile</c>) of a model it
/// has not saved, so the tables are used for every model. Calls only GetObject, GetModelFilename, GetVersion and
/// ExportFile: the model is never edited, unlocked, saved or given other units, and ETABS is never started or closed.
/// </summary>
public sealed class EtabsGateway : IEtabsGateway, IDisposable
{
    private const string ProgId = "CSI.ETABS.API.ETABSObject";
    private readonly StaWorker worker = new();

    public Task<EtabsModelExport> ExportActiveModelAsync() => worker.Run(() =>
    {
        cHelper? helper = null;
        cOAPI? api = null;
        cSapModel? model = null;
        var folder = Path.Combine(Path.GetTempPath(), "Mcotis", "EtabsImport", Guid.NewGuid().ToString("N"));
        try
        {
            helper = new Helper();
            try { api = helper.GetObject(ProgId); }
            catch (COMException) { api = null; }
            if (api is null) throw new EtabsNotRunningException("No running ETABS was found. Open the model in ETABS, then import again.");
            model = api.SapModel ?? throw new EtabsNotRunningException("The running ETABS has no model open.");
            var path = model.GetModelFilename(true) ?? "";
            // ETABS names an unsaved model "(Untitled)"
            if (!Path.IsPathRooted(path)) path = "";
            string version = ""; double number = 0;
            model.GetVersion(ref version, ref number);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "model-tables.txt");
            var result = model.File.ExportFile(file, eFileTypeIO.DBTablesText);
            if (result != 0 || !File.Exists(file))
                throw new InvalidOperationException($"ETABS could not export its model tables (File.ExportFile returned {result}). Close any open ETABS dialog and try again.");
            return new EtabsModelExport(path, string.IsNullOrWhiteSpace(version) ? "ETABS" : "ETABS " + version, File.ReadAllText(file));
        }
        finally
        {
            Release(model);
            Release(api);
            Release(helper);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    });

    private static void Release(object? reference)
    {
        if (reference is not null && Marshal.IsComObject(reference)) Marshal.FinalReleaseComObject(reference);
    }

    public void Dispose() => worker.Dispose();
}
