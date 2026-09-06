using COT_VCF.Application;
using COT_VCF.Application.Workers;
using COT_VCF.Domain;
using COT_VCF.Infrastructure;
using CSiAPIv1;

namespace COT_VCF.SAP2000;

/// <summary>Owns one background SAP2000 process, COM graph and STA lane.</summary>
public sealed class SapInstanceHost : ISapInstanceHost
{
    private readonly StaWorker worker = new();
    private cOAPI? api;
    private SapGateway? gateway;
    private bool stopped;

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
                cHelper helper = new Helper();
                api = helper.CreateObjectProgID("CSI.SAP2000.API.SapObject")
                    ?? throw new InvalidOperationException($"SAP2000 worker {InstanceId} could not create an API instance.");
                Check(api.ApplicationStart(), "ApplicationStart");
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
            if (currentApi is not null)
                await worker.Run(() =>
                {
                    Check(currentApi.ApplicationExit(false), "ApplicationExit");
                    return true;
                });
        }
        finally
        {
            gateway?.Dispose();
            gateway = null;
            api = null;
            worker.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

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
