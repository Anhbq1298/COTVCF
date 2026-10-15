using Mcotis.Desktop.Application;

namespace Mcotis.Desktop.Presentation;

// ---- Menu bar navigation (presentation only) ----
// The left pane shows one workflow page at a time, chosen from the menu bar: MB opens Tabs 01–06A and 08; the FEM page
// is split into panes opened from Define, Assign, Run, Result and Display. Each FEM pane keeps the FEM page it belongs to
// (Inputs / Analysis & Results / View), so the shared viewer behaves as before.
public sealed partial class MainViewModel
{
    public const int FemPaneSections = 0, FemPaneLoadPatterns = 1, FemPaneLoadCases = 2, FemPaneRestraints = 3,
        FemPaneResultSelection = 4, FemPaneAnalysisOptions = 5, FemPaneResults = 6, FemPaneDisplay = 7, FemPaneOutputSettings = 8;

    private int femPane = FemPaneLoadPatterns;
    private bool showDimensions = true;
    private bool showFrameLocalAxes;
    private bool showExtruded3D = true;

    /// <summary>Display → Dimensions / View Options: bay spans, level heights and node elevations on the model viewer.</summary>
    public bool ShowDimensions { get => showDimensions; set => Set(ref showDimensions, value); }
    /// <summary>Display the assigned local 1/2/3 axes on frame members in the model view.</summary>
    public bool ShowFrameLocalAxes { get => showFrameLocalAxes; set => Set(ref showFrameLocalAxes, value); }
    /// <summary>Use actual section outlines in 3D; off draws member centreline rods instead. Presentation only.</summary>
    public bool ShowExtruded3D { get => showExtruded3D; set => Set(ref showExtruded3D, value); }
    private bool femCableFitView;

    /// <summary>The FEM pane shown in the left column (Define / Assign / Run / Result / Display menus).</summary>
    public int FemPane
    {
        get => femPane;
        set
        {
            if (!Set(ref femPane, value)) return;
            SelectedFemPage = FemPageOf(value);
            NotifyPaneHeader();
        }
    }

    /// <summary>Result → Cable target fit: the result pane and table show the cable fit instead of a diagram. Only a
    /// model with cables has one (<see cref="HasCableMembers"/>).</summary>
    public bool FemCableFitView
    {
        get => femCableFitView && HasCableMembers;
        set { if (Set(ref femCableFitView, value)) NotifyPaneHeader(); }
    }

    /// <summary>The model has cables. What belongs to the fit of cable target forces — the settings of Analysis
    /// Options, the result and its table, the chart of the run monitor — is shown only then (user, 2026-10-13: "the
    /// cable fitting force only visible for system that has cabled model"). A model without cables has nothing to fit.</summary>
    public bool HasCableMembers => State.Geometry.Members.Any(member => member.Kind == Domain.MemberKind.Cable);

    private static int FemPageOf(int pane) => pane switch
    {
        <= FemPaneRestraints or FemPaneOutputSettings => FemInputsPage,
        FemPaneDisplay => FemViewPage,
        _ => FemAnalysisPage,
    };

    /// <summary>Keeps the FEM pane on the page when the page is set directly.</summary>
    private void SyncFemPane()
    {
        if (FemPageOf(femPane) == selectedFemPage) return;
        femPane = selectedFemPage switch
        {
            FemInputsPage => FemPaneLoadPatterns,
            FemViewPage => FemPaneDisplay,
            _ => FemPaneAnalysisOptions,
        };
        Notify(nameof(FemPane));
        NotifyPaneHeader();
    }

    /// <summary>Menu of the pane shown in the left column, e.g. "MB" or "Define".</summary>
    public string PaneMenu => selectedTab == 5 ? "Post-Processing" : selectedTab != TabFem ? "MB" : femPane switch
    {
        FemPaneRestraints => "Assign",
        FemPaneAnalysisOptions => "Run",
        FemPaneResults => "Result",
        FemPaneOutputSettings => "Post-Processing",
        FemPaneDisplay => "Display",
        _ => "Define",
    };

    public string PaneTitle => selectedTab switch
    {
        TabLoading => "01  LOADING",
        TabTruss => "02  TRUSS HEIGHT",
        TabPretension => "03  PRETENSION",
        TabOutput => "04  OUTPUT SETTINGS",
        TabIteration => "05  ITERATION",
        5 => "06  POST-PROCESSING",
        TabOptimization => "06A  OPTIMIZATION",
        TabQuantities => "08  QUANTITIES",
        TabFem => femPane switch
        {
            FemPaneSections => "SECTIONS",
            FemPaneLoadPatterns => "LOAD PATTERNS",
            FemPaneLoadCases => "LOAD CASES / LOAD COMBINATIONS",
            FemPaneOutputSettings => "OUTPUT SETTINGS",
            FemPaneRestraints => "JOINT RESTRAINTS",
            FemPaneResultSelection => "RESULT LOAD CASE",
            FemPaneAnalysisOptions => "ANALYSIS OPTIONS",
            FemPaneResults => FemCableFitView ? "CABLE TARGET FIT" : FemResultMode switch
            {
                FemResultMode.AxialForce => "AXIAL FORCES / CABLES",
                FemResultMode.BendingMoment => "BENDING MOMENTS",
                FemResultMode.Shear => "SHEAR FORCES",
                FemResultMode.Reactions => "SUPPORT REACTIONS",
                _ => "DEFLECTION",
            },
            _ => "VIEW OPTIONS",
        },
        _ => "",
    };

    /// <summary>The status line the left tab rail used to show for this page.</summary>
    public string PaneStatus => selectedTab switch
    {
        TabLoading => LoadStatus,
        TabTruss => TrussStatus,
        TabPretension => CableStatus,
        TabOutput => OutputStatus,
        TabIteration => StudyBuildStateText,
        5 => "Raw data → reports",
        TabOptimization => OptimizationTabStatus,
        TabQuantities => QuantityTabStatus,
        TabFem => $"07 FEM · {FemStatus}",
        _ => "",
    };

    /// <summary>Why the run's pretension was not fitted (empty when it was).</summary>
    public string FemResultIssue => SelectedFemRun?.Result.TargetFitFailure ?? "";

    public bool HasFemResults => FemRuns.Count > 0;

    /// <summary>Re-raises <see cref="PaneStatus"/> when the status of the page it shows changes.</summary>
    private void ForwardPaneStatus(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LoadStatus) or nameof(TrussStatus) or nameof(CableStatus) or nameof(OutputStatus) or
            nameof(StudyBuildStateText) or nameof(OptimizationTabStatus) or nameof(QuantityTabStatus) or nameof(FemStatus))
            Notify(nameof(PaneStatus));
        if (e.PropertyName is nameof(FemStatus)) Notify(nameof(HasFemResults));
    }

    private void NotifyPaneHeader()
    {
        Notify(nameof(PaneMenu));
        Notify(nameof(PaneTitle));
        Notify(nameof(PaneStatus));
    }

    private bool showUndeformed;

    /// <summary>Display → Show Undeformed Shape (SAP2000's F4): the viewer draws the model without any result overlay
    /// (forces, deformed shape, reactions) until a result is shown again. The results themselves are kept.</summary>
    public bool ShowUndeformed
    {
        get => showUndeformed;
        private set
        {
            if (showUndeformed == value) return;
            showUndeformed = value;
            Notify(nameof(ShowUndeformed));
            Notify(nameof(SharedViewerFemResult));
        }
    }

    public void ShowUndeformedShape()
    {
        FemCableFitView = false;
        ShowUndeformed = true;
        Message = "Undeformed shape — Show Results draws a result again.";
    }

    /// <summary>Result menu: show <paramref name="mode"/> (or the cable target fit) of the run on the FEM result pane
    /// and the shared viewer — what selecting it in the former result tree did.</summary>
    public void ShowFemResult(FemResultMode? mode)
    {
        showModeShape = false;
        ShowUndeformed = false;
        SelectedTab = TabFem;
        if (SelectedFemRun is null && FemRuns.Count > 0) SelectedFemRun = FemRuns[0];
        FemCableFitView = mode is null;
        if (mode is { } value) FemResultMode = value;
        FemPane = FemPaneResults;
        NotifyPaneHeader();
    }
}
