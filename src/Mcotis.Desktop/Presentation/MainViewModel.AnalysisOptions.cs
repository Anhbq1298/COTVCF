using Mcotis.Core;

namespace COT_VCF.Presentation;

public sealed partial class MainViewModel
{
    internal AnalysisSettings PlanarAnalysisSettings => new(FemTargetFitPercent, FemTargetFitIterations, FemFrameMeshSegments, FemCableSag) { ActiveDofs = DofMask.XZ };
    internal void ApplyPlanarAnalysisSettings(AnalysisSettings settings)
    {
        if (!CanEditFemModel) throw new InvalidOperationException("Unlock the model and finish any active run before changing analysis settings.");
        if (settings.ActiveDofs != DofMask.XZ) throw new ArgumentException("The planar cable/frame solver requires the XZ DOF preset.");
        if (!double.IsFinite(settings.CableFitTolerancePercent) || settings.CableFitTolerancePercent <= 0 || settings.CableFitTolerancePercent >= 100 ||
            settings.CableFitMaxIterations < 1 || settings.CableFitMaxIterations > 10000 || settings.FrameMeshSegments < 1 ||
            settings.FrameMeshSegments > Geometry.PlanarFrameSolver.MaximumFrameMeshSegments)
            throw new ArgumentException("Invalid cable fitting or frame mesh settings.");
        if (settings == PlanarAnalysisSettings) return;
        FemTargetFitPercent = settings.CableFitTolerancePercent;
        FemTargetFitIterations = settings.CableFitMaxIterations;
        FemFrameMeshSegments = settings.FrameMeshSegments;
        FemCableSag = settings.CableSag;
        ClearFemResults();
        femWorkingDirty = true;
        NotifyFemDatabaseState();
        ScheduleFemWorkingSave();
    }
}
