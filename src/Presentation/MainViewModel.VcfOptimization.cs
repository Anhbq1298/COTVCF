using COT_VCF.Application;

namespace COT_VCF.Presentation;

// ---- Run → Optimization VCF: the search space of the VCF system (geometry ranges and candidate sections) ----
// Kept in memory for the optimization run loop, which is not built yet; the working model is not changed.
public sealed partial class MainViewModel
{
    /// <summary>The ranges confirmed in Run → Optimization VCF, or null before the first OK.</summary>
    public VcfOptimizationSettings? VcfOptimizationSettings { get; private set; }

    internal void SetVcfOptimization(VcfOptimizationSettings settings)
    {
        VcfOptimizationSettings = settings;
        Notify(nameof(VcfOptimizationSettings));
        Message = $"Optimization VCF: {settings.CombinationCount:N0} combinations kept " +
            $"({settings.GeometryCount:N0} geometry × {settings.SectionCount:N0} section variants). The run loop is not implemented yet.";
    }
}
