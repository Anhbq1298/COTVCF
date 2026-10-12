using COT_VCF.Application.Etabs;
using COT_VCF.Infrastructure;
using CSiAPIv1;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace COT_VCF.SAP2000;

/// <summary>
/// File → Import From ETABS / Export to ETABS: attaches to the running ETABS through CSI's cross-product API (CSiAPIv1,
/// ProgID "CSI.ETABS.API.ETABSObject"; verified with SAP2000 27's CSiAPIv1 2.15 against ETABS 23.2), on an STA lane of its
/// own, for each operation — there is no lasting ETABS session. The import exports the model's database tables as text
/// (<c>File.ExportFile</c>, <c>eFileTypeIO.DBTablesText</c>) to a private temporary folder, which it reads and deletes:
/// ETABS returns an error for the .e2k export (<c>eFileTypeIO.TextFile</c>) of a model it has not saved, so the tables are
/// used for every model. Reading never edits, unlocks, saves or changes the units of the model; the export
/// (EtabsGateway.Export.cs) is the only write. ETABS is never started or closed, except by the checks' private instance
/// (<see cref="StartPrivateAsync"/>).
/// </summary>
public sealed partial class EtabsGateway : IEtabsGateway, IDisposable
{
    private const string ProgId = "CSI.ETABS.API.ETABSObject";
    private readonly StaWorker worker = new();
    // checks only: a hidden ETABS this gateway started and will close
    private cOAPI? owned;
    private int ownedProcess;

    public Task<EtabsModelExport> ExportActiveModelAsync() => worker.Run(() => WithModel(model =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "Mcotis", "EtabsImport", Guid.NewGuid().ToString("N"));
        try
        {
            var (path, version) = Identity(model);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "model-tables.txt");
            var result = model.File.ExportFile(file, eFileTypeIO.DBTablesText);
            if (result != 0 || !File.Exists(file))
                throw new InvalidOperationException($"ETABS could not export its model tables (File.ExportFile returned {result}). Close any open ETABS dialog and try again.");
            return new EtabsModelExport(path, version, File.ReadAllText(file));
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }));

    /// <summary>Runs <paramref name="work"/> on the model of the running ETABS (or the private instance), releasing the COM
    /// references it attached. Call on the worker lane.</summary>
    private T WithModel<T>(Func<cSapModel, T> work)
    {
        cHelper? helper = null;
        cOAPI? api = owned;
        cSapModel? model = null;
        try
        {
            if (api is null)
            {
                helper = new Helper();
                try { api = helper.GetObject(ProgId); }
                catch (COMException) { api = null; }
                if (api is null) throw new EtabsNotRunningException("No running ETABS was found. Open the model in ETABS, then try again.");
            }
            model = api.SapModel ?? throw new EtabsNotRunningException("The running ETABS has no model open.");
            return work(model);
        }
        finally
        {
            Release(model);
            if (!ReferenceEquals(api, owned)) Release(api);
            Release(helper);
        }
    }

    /// <summary>The model's file (empty when ETABS has not saved it — it reports "(Untitled)") and the ETABS version.</summary>
    private static (string Path, string Version) Identity(cSapModel model)
    {
        var path = model.GetModelFilename(true) ?? "";
        if (!Path.IsPathRooted(path)) path = "";
        string version = ""; double number = 0;
        model.GetVersion(ref version, ref number);
        return (path, string.IsNullOrWhiteSpace(version) ? "ETABS" : "ETABS " + version);
    }

    /// <summary>Checks only: starts a hidden ETABS of this gateway's own, with a new blank model in kN, m, C — never the
    /// user's. <see cref="ClosePrivateAsync"/> closes it without saving.</summary>
    internal static async Task<EtabsGateway> StartPrivateAsync(string etabsExe)
    {
        var gateway = new EtabsGateway();
        try
        {
            await gateway.worker.Run(() =>
            {
                var before = Process.GetProcessesByName("ETABS").Select(process => process.Id).ToHashSet();
                cHelper helper = new Helper();
                try { gateway.owned = helper.CreateObject(etabsExe) ?? throw new InvalidOperationException("ETABS could not be started."); }
                finally { Release(helper); }
                var spawned = Process.GetProcessesByName("ETABS").Select(process => process.Id).Where(id => !before.Contains(id)).ToArray();
                if (spawned.Length == 1) gateway.ownedProcess = spawned[0];
                Check(gateway.owned.ApplicationStart(eUnits.kN_m_C, false, ""), "ApplicationStart");
                var model = gateway.owned.SapModel;
                Check(model.InitializeNewModel(eUnits.kN_m_C), "InitializeNewModel");
                Check(model.File.NewBlank(), "File.NewBlank");
                return true;
            });
            return gateway;
        }
        catch
        {
            await gateway.ClosePrivateAsync();
            throw;
        }
    }

    /// <summary>Checks only: deletes every frame and joint of the private instance's model, leaving a model that has had
    /// objects (ETABS then refuses new stories). Returns the frames deleted.</summary>
    internal Task<int> EmptyPrivateModelAsync() => worker.Run(() => WithModel(model =>
    {
        if (owned is null) throw new InvalidOperationException("Only the checks' private ETABS may be emptied.");
        int n = 0; string[] names = [];
        Check(model.FrameObj.GetNameList(ref n, ref names), "FrameObj.GetNameList");
        foreach (var name in names.Take(n)) Check(model.FrameObj.Delete(name), "FrameObj.Delete", name);
        int p = 0; string[] points = [];
        Check(model.PointObj.GetNameList(ref p, ref points), "PointObj.GetNameList");
        foreach (var point in points.Take(p)) model.PointObj.DeleteSpecialPoint(point);
        return n;
    }));

    internal async Task ClosePrivateAsync()
    {
        if (owned is null && ownedProcess == 0) return;
        await worker.Run(() =>
        {
            try { owned?.ApplicationExit(false); } catch (COMException) { }
            Release(owned);
            owned = null;
            return true;
        });
        if (ownedProcess != 0)
        {
            try
            {
                using var process = Process.GetProcessById(ownedProcess);
                if (!process.WaitForExit(10000)) process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            ownedProcess = 0;
        }
    }

    private static void Check(int result, string operation, string name = "")
    {
        if (result != 0) throw new InvalidOperationException($"ETABS returned code {result} during {operation}{(name.Length > 0 ? " " + name : "")}.");
    }

    private static void Release(object? reference)
    {
        if (reference is not null && Marshal.IsComObject(reference)) Marshal.FinalReleaseComObject(reference);
    }

    public void Dispose() => worker.Dispose();
}
