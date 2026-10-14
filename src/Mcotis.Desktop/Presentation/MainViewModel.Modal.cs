using Mcotis.Desktop.Application;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Presentation;

// ---- Result → Modal Results: the modes of the run's modal cases, and a mode's shape in the shared viewer ----
// The run solves a modal case of the Load Cases table with the modal solver of the standalone system (FemDocumentRun);
// its modes are kept with the run (FemStudyRun.Native) and persisted by output schema v2.
public sealed partial class MainViewModel
{
    /// <summary>The modes of the run shown (Result → Modal Results).</summary>
    public MainModalResults ModalResults { get; } = new();

    private Mcotis.Application.AnalysisResultTracker? femResultTracker;
    /// <summary>Whether the results on hand belong to the model (task of 2026-10-14, M1): Not run, Running, Current, Stale or
    /// Failed, by the analysis input hash of the native document they were computed from — the library's rule for both
    /// windows. A change of the model makes them stale; one that changes nothing analysed leaves them current.</summary>
    public Mcotis.Application.AnalysisResultTracker FemResultTracker
    {
        get
        {
            if (femResultTracker is not null) return femResultTracker;
            femResultTracker = new(NativeDocumentSession);
            femResultTracker.StateChanged += (_, _) => { Notify(nameof(FemResultState)); Notify(nameof(FemResultStateText)); };
            return femResultTracker;
        }
    }

    public Mcotis.Application.ResultState FemResultState => FemResultTracker.State;

    /// <summary>What the results are, said where results are used: the run they are of, and whether the model has changed since.</summary>
    public string FemResultStateText => FemResultTracker.State switch
    {
        Mcotis.Application.ResultState.Current => $"Results current · revision {FemResultTracker.ResultSource?.Revision}",
        Mcotis.Application.ResultState.Stale => "Results stale · the model has changed since they were computed",
        Mcotis.Application.ResultState.Running => "Running",
        Mcotis.Application.ResultState.Failed => "The last run failed",
        _ => "Not run",
    };

    public bool HasFemModalResults => ModalResults.HasResults;

    private bool showModeShape;

    /// <summary>The viewer draws a mode's shape — relative amplitudes, said as such — instead of a static result.</summary>
    public bool SharedViewerIsModeShape => showModeShape && SharedViewerFemResult is not null;

    private void ShowRunModes(FemStudyRun? run)
    {
        ModalResults.ModeChanged -= ModeShown;
        ModalResults.Show(run?.Native);
        ModalResults.ModeChanged += ModeShown;
        if (!ModalResults.HasResults) showModeShape = false;
        Notify(nameof(HasFemModalResults));
        NotifySharedViewer();
    }

    private void ModeShown()
    {
        if (showModeShape)
        {
            Notify(nameof(FemResultDiagramTitle));
            NotifySharedViewer();
        }
    }

    /// <summary>Draws the mode chosen in Modal Results in the shared viewer, as a deflected shape that says it is a
    /// mass-normalized eigenvector. Showing any static result takes it away again.</summary>
    internal void ShowModeShape()
    {
        if (!ModalResults.HasResults) return;
        ShowFemResult(FemResultMode.DeflectedShape);
        showModeShape = true;
        Message = $"Mode {ModalResults.SelectedMode} of '{ModalResults.SelectedCase}': mass-normalized eigenvector, drawn magnified — relative amplitude, not a displacement.";
        Notify(nameof(FemResultDiagramTitle));
        NotifySharedViewer();
    }
}
