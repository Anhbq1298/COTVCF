using Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

public sealed partial class MainViewModel
{
    /// <summary>The degrees of freedom this window's model can have: it is planar, in XZ.</summary>
    internal const DofMask PlaneDofs = DofMask.XZ;
    /// <summary>Those a joint only cables reach can have: a cable holds no rotation.</summary>
    internal const DofMask PlaneCableDofs = DofMask.UX | DofMask.UZ;

    private DofMask femActiveDofs = PlaneDofs, femCableDofs = PlaneCableDofs;
    /// <summary>The degrees of freedom of the model that are on (Run → Analysis Options → Degrees of Freedom): UX and
    /// UZ, which a model that spans its plane always has, and RY unless it is switched off — the rotation is then held
    /// at every joint.</summary>
    internal DofMask FemActiveDofs => femActiveDofs;
    /// <summary>The degrees of freedom of a joint only cables reach, among UX and UZ.</summary>
    internal DofMask FemCableDofs => femCableDofs;

    internal AnalysisSettings PlanarAnalysisSettings => new(FemTargetFitPercent, FemTargetFitIterations, FemFrameMeshSegments, FemCableSag)
        { ActiveDofs = femActiveDofs, CableDofs = femCableDofs };

    /// <summary>Why these degrees of freedom cannot be those of this window's model; null when they can.</summary>
    internal static string? DofProblem(DofMask active, DofMask cable) =>
        (active & ~PlaneDofs) != 0 ? "This workspace's model is planar: its degrees of freedom are UX, UZ and RY."
        // the solver refuses a translation that is off unless every joint has the same coordinate along it
        : (active & PlaneCableDofs) != PlaneCableDofs ? "This workspace's model spans its XZ plane: UX and UZ stay on. RY can be switched off."
        : (cable & ~PlaneCableDofs) != 0 ? "A joint that only cables reach moves in UX and UZ: a cable holds no rotation."
        : (cable & active) == 0 ? "Keep at least one of UX and UZ on for cable joints, among those that are on for the model."
        : null;

    internal void ApplyPlanarAnalysisSettings(AnalysisSettings settings)
    {
        if (!CanEditFemModel) throw new InvalidOperationException("Unlock the model and finish any active run before changing analysis settings.");
        if (DofProblem(settings.ActiveDofs, settings.CableDofs) is { } problem) throw new ArgumentException(problem);
        if (!double.IsFinite(settings.CableFitTolerancePercent) || settings.CableFitTolerancePercent <= 0 || settings.CableFitTolerancePercent >= 100 ||
            settings.CableFitMaxIterations < 1 || settings.CableFitMaxIterations > 10000 || settings.FrameMeshSegments < 1 ||
            settings.FrameMeshSegments > Application.FemSolver.MaximumFrameMeshSegments)
            throw new ArgumentException("Invalid cable fitting or frame mesh settings.");
        if (settings == PlanarAnalysisSettings) return;
        FemTargetFitPercent = settings.CableFitTolerancePercent;
        FemTargetFitIterations = settings.CableFitMaxIterations;
        FemFrameMeshSegments = settings.FrameMeshSegments;
        FemCableSag = settings.CableSag;
        (femActiveDofs, femCableDofs) = (settings.ActiveDofs, settings.CableDofs);
        ClearFemResults();
        femWorkingDirty = true;
        NotifyFemDatabaseState();
        ScheduleFemWorkingSave();
    }

    private static List<string> DofNames(DofMask mask) =>
        DegreesOfFreedom.Components.Where(component => mask.Contains(component)).Select(component => component.ToString()).ToList();

    /// <summary>The degrees of freedom a file holds. Names it does not know, or a set this window cannot analyse, give
    /// the degrees of freedom of the plane back: a file is never opened into a model that cannot be run.</summary>
    private void RestoreFemDofs(IEnumerable<string>? active, IEnumerable<string>? cable)
    {
        static DofMask Read(IEnumerable<string>? names) => (names ?? []).Aggregate(DofMask.None,
            (mask, name) => Enum.TryParse<DofComponent>(name, ignoreCase: true, out var component) && Enum.IsDefined(component) ? mask | component.Mask() : mask);
        var (model, joints) = (Read(active), Read(cable));
        (femActiveDofs, femCableDofs) = DofProblem(model, joints) is null ? (model, joints) : (PlaneDofs, PlaneCableDofs);
    }
}
