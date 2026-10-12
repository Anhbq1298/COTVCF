using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Workers;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Infrastructure;
using CSiAPIv1;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mcotis.Desktop.SAP2000;

/// <summary>Owns one background SAP2000 process, COM graph and STA lane.</summary>
public sealed class SapInstanceHost : ISapInstanceHost
{
    private readonly StaWorker worker = new();
    private cOAPI? api;
    private SapGateway? gateway;
    private bool stopped;
    private bool applicationStarted;
    private int spawnedProcessId;

    public SapInstanceHost(int instanceId) => InstanceId = instanceId;
    public int InstanceId { get; }
    public ISapGateway Gateway => gateway ?? throw new InvalidOperationException($"Study worker {InstanceId} has not started.");

    public async Task<ModelInfo> StartAsync(string baselineModelPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (gateway is not null) throw new InvalidOperationException($"Study worker {InstanceId} is already started.");
        try
        {
            await worker.Run(() =>
            {
                var runningBefore = SapProcessIds();
                cHelper helper = new Helper();
                try
                {
                    api = helper.CreateObjectProgID("CSI.SAP2000.API.SapObject")
                        ?? throw new InvalidOperationException($"SAP2000 worker {InstanceId} could not create an API instance.");
                }
                finally
                {
                    if (Marshal.IsComObject(helper)) Marshal.FinalReleaseComObject(helper);
                }
                Check(api.ApplicationStart(), "ApplicationStart");
                applicationStarted = true;
                // Remember the process we started so StopAsync can guarantee it dies. Workers start
                // one at a time, so the delta is unambiguous; if it is not, stay untracked rather
                // than risk targeting an instance this host does not own.
                var spawned = SapProcessIds().Except(runningBefore).ToArray();
                if (spawned.Length == 1) spawnedProcessId = spawned[0];
                var model = api.SapModel
                    ?? throw new InvalidOperationException($"SAP2000 worker {InstanceId} did not expose a model API.");
                gateway = new SapGateway(worker, api, model);
                return true;
            });
            token.ThrowIfCancellationRequested();
            return await Gateway.OpenModelAsync(baselineModelPath);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (stopped) return;
        stopped = true;
        var currentApi = api;
        try
        {
            if (currentApi is not null && applicationStarted)
                await worker.Run(() =>
                {
                    Check(currentApi.ApplicationExit(false), "ApplicationExit");
                    return true;
                });
        }
        finally
        {
            try
            {
                if (gateway is not null)
                {
                    await gateway.ReleaseComReferencesAsync();
                }
                else if (currentApi is not null)
                {
                    await worker.Run(() =>
                    {
                        if (Marshal.IsComObject(currentApi)) Marshal.FinalReleaseComObject(currentApi);
                        return true;
                    });
                }
            }
            finally
            {
                gateway?.Dispose();
                gateway = null;
                api = null;
                worker.Dispose();
                TerminateSpawnedProcess();
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>
    /// ApplicationExit is advisory: a wedged SAP2000 can outlive it and stay registered in the COM
    /// running-object table, where a later Connect (which attaches via GetObject) can bind to it
    /// instead of the model the user actually has open. Only ever targets a process this host
    /// started itself — the connected instance is owned by <see cref="ConnectedSapInstanceHost"/>
    /// and is never tracked here.
    /// </summary>
    private void TerminateSpawnedProcess()
    {
        var processId = spawnedProcessId;
        spawnedProcessId = 0;
        if (processId == 0) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.WaitForExit(10000)) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException) { /* already exited */ }
        catch (InvalidOperationException) { /* already exited */ }
        catch { /* cleanup must never fail a shutdown */ }
    }

    private static int[] SapProcessIds()
    {
        var processes = Process.GetProcessesByName("SAP2000");
        try { return processes.Select(process => process.Id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private void Check(int result, string operation)
    {
        if (result != 0)
            throw new InvalidOperationException($"SAP2000 worker {InstanceId} returned code {result} during {operation}.");
    }
}

public sealed class SapInstanceHostFactory : ISapInstanceHostFactory
{
    public ISapInstanceHost Create(int instanceId) => new SapInstanceHost(instanceId);
}
