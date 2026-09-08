using COT_VCF.Application;
using COT_VCF.Application.Workers;
using COT_VCF.Domain;
using COT_VCF.Infrastructure;
using CSiAPIv1;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace COT_VCF.SAP2000;

/// <summary>Owns one background SAP2000 process, COM graph and STA lane.</summary>
public sealed class SapInstanceHost : ISapInstanceHost
{
    private readonly StaWorker worker = new();
    private readonly string executablePath;
    private cOAPI? api;
    private SapGateway? gateway;
    private bool stopped;
    private bool applicationStarted;

    public SapInstanceHost(int instanceId, string executablePath)
    {
        InstanceId = instanceId;
        this.executablePath = executablePath;
    }

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
                cHelper helper = new Helper();
                try
                {
                    // CreateObject(exePath) always launches a fresh, independent SAP2000 process.
                    // CreateObjectProgID would instead bind to the instance already connected by the
                    // application, so ApplicationStart/OpenFile below would hijack the live baseline.
                    api = helper.CreateObject(executablePath)
                        ?? throw new InvalidOperationException($"SAP2000 worker {InstanceId} could not create an API instance from {executablePath}.");
                }
                finally
                {
                    if (Marshal.IsComObject(helper)) Marshal.FinalReleaseComObject(helper);
                }
                Check(api.ApplicationStart(), "ApplicationStart");
                applicationStarted = true;
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
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void Check(int result, string operation)
    {
        if (result != 0)
            throw new InvalidOperationException($"SAP2000 worker {InstanceId} returned code {result} during {operation}.");
    }
}

public sealed class SapInstanceHostFactory(Func<string>? executablePathResolver = null) : ISapInstanceHostFactory
{
    private readonly Func<string> resolveExecutablePath = executablePathResolver ?? ResolveExecutablePath;

    public ISapInstanceHost Create(int instanceId) => new SapInstanceHost(instanceId, resolveExecutablePath());

    /// <summary>
    /// Full path to the SAP2000 executable used to launch additional worker processes. Prefers a
    /// SAP2000 instance already running (so worker versions match the connected baseline), then
    /// falls back to a standard install directory.
    /// </summary>
    public static string ResolveExecutablePath()
    {
        foreach (var process in Process.GetProcessesByName("SAP2000"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            }
            catch { /* 32/64-bit or access mismatch — try the next candidate */ }
            finally { process.Dispose(); }
        }

        // Several SAP2000 majors commonly sit side by side; take the newest, because a worker whose
        // version differs from the connected baseline is rejected by SapWorkerInstance anyway.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var csRoot = Path.Combine(programFiles, "Computers and Structures");
        if (Directory.Exists(csRoot))
        {
            var newest = Directory.EnumerateDirectories(csRoot, "SAP2000*")
                .Select(dir => (Dir: dir, Version: int.TryParse(
                    Path.GetFileName(dir).AsSpan("SAP2000".Length).Trim(), out var major) ? major : 0))
                .OrderByDescending(candidate => candidate.Version)
                .Select(candidate => Path.Combine(candidate.Dir, "SAP2000.exe"))
                .FirstOrDefault(File.Exists);
            if (newest is not null) return newest;
        }

        throw new InvalidOperationException(
            "Could not locate SAP2000.exe to start an additional worker. Keep the connected SAP2000 " +
            "instance open, or set Total SAP2000 Instances to 1 to use only the connected instance.");
    }
}
