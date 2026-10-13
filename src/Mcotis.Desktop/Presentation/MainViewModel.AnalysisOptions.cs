using Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

public sealed partial class MainViewModel
{
    /// <summary>The degrees of freedom a model starts with — the default model, a blank one — for the model and for a
    /// joint only cables reach: the solver's own default, full 3D with the translations for cable joints (user,
    /// 2026-10-13: "make the planar use 3d as 3d is default"). A model of this window lies in one plane on pins that
    /// are on one line, so in 3D nothing holds it out of that plane. Its loads do no work on that movement: the solver
    /// holds it, solves, and the run monitor lists it as a warning (user, same day: "ETABS can run regardless"). The
    /// results are those of the XZ plane, which the user can also choose.</summary>
    internal static (DofMask Model, DofMask CableJoints) StartingDofs { get; set; } = (DofMask.Full3D, DofMask.Translations);
    /// <summary>The degrees of freedom of a configuration saved before they were an option: the XZ plane it was
    /// analysed in, so that what it holds — a lock, results — still belongs to its settings.</summary>
    internal static readonly (DofMask Model, DofMask CableJoints) PlaneDofs = (DofMask.XZ, DofMask.UX | DofMask.UZ);

    private DofMask femActiveDofs = StartingDofs.Model, femCableDofs = StartingDofs.CableJoints;
    /// <summary>The degrees of freedom of the model that are on (Run → Analysis Options → Degrees of Freedom). The
    /// set is the user's: whether the model can be analysed with it is for the run to say (user, 2026-10-13: "if not
    /// proper then report in the log"), not for this window to prevent. One that is off is held at every joint.</summary>
    internal DofMask FemActiveDofs => femActiveDofs;
    /// <summary>The degrees of freedom of a joint only cables reach, among those that are on.</summary>
    internal DofMask FemCableDofs => femCableDofs;

    internal AnalysisSettings PlanarAnalysisSettings => new(FemTargetFitPercent, FemTargetFitIterations, FemFrameMeshSegments, FemCableSag)
        { ActiveDofs = femActiveDofs, CableDofs = femCableDofs };

    /// <summary>Why these degrees of freedom cannot be kept; null when they can. Only what no model could be run with
    /// is refused here — no translation at all. A set that this model cannot be analysed with is kept, and the run says so.</summary>
    internal static string? DofProblem(DofMask active, DofMask cable) =>
        !DegreesOfFreedom.IsValid(active) || (active & DofMask.Translations) == 0 ? "Select at least one translational DOF."
        : !DegreesOfFreedom.IsValid(cable) || (cable & DofMask.Translations) == 0 ? "Select at least one translational DOF for cable joints."
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

    /// <summary>The degrees of freedom of a model that is opened. What its file holds, as it holds it. A file without
    /// them — or with names it does not know, or a set no model can have — gives <paramref name="starting"/> for a
    /// model that starts here (the default model) and the XZ plane for a configuration saved before they were an
    /// option: that is what it was analysed in.</summary>
    private void RestoreFemDofs(IEnumerable<string>? active, IEnumerable<string>? cable, bool starting)
    {
        static DofMask Read(IEnumerable<string> names) => names.Aggregate(DofMask.None,
            (mask, name) => Enum.TryParse<DofComponent>(name, ignoreCase: true, out var component) && Enum.IsDefined(component) ? mask | component.Mask() : mask);
        var kept = starting ? StartingDofs : PlaneDofs;
        (femActiveDofs, femCableDofs) = active is not null && cable is not null && Read(active) is var model && Read(cable) is var joints && DofProblem(model, joints) is null
            ? (model, joints) : kept;
    }

    /// <summary>A model that starts here, without a file: the degrees of freedom every model starts with.</summary>
    private void ResetFemDofs() => (femActiveDofs, femCableDofs) = StartingDofs;
}
