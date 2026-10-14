using Mcotis.Analysis;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Result → Modal Results of the main window: the modes of the modal cases the run solved (task of 2026-10-14, M1 —
/// the main window runs its document, and with it the modal solver). The rows are those of the native workspace
/// (<see cref="ModalResultRows"/>) under the same names, so the same table control shows both.
/// </summary>
public sealed class MainModalResults : Observable
{
    private FemDocumentResults? run;
    private string? selectedCase;
    private int selectedMode = 1;

    /// <summary>The modal cases of the run, in the order they were solved.</summary>
    public IReadOnlyList<string> CaseNames => run?.Modal.Keys.ToArray() ?? [];
    public bool HasResults => CaseNames.Count > 0;

    public string? SelectedCase
    {
        get => selectedCase;
        set
        {
            if (!Set(ref selectedCase, value)) return;
            selectedMode = Result?.Modes.FirstOrDefault()?.ModeNumber ?? 1;
            Refresh();
        }
    }

    private ModalAnalysisResult? Result => run is not null && selectedCase is not null && run.Modal.TryGetValue(selectedCase, out var result) ? result : null;
    private string Name(Mcotis.Core.ObjectId id) => run!.Names.TryGetName(id, out var name) ? name : id.ToString();

    public IEnumerable<NativeModeRow> Modes => ModalResultRows.Modes(Result);
    public IEnumerable<NativeParticipationRow> Participation => ModalResultRows.Participation(Result);
    public IEnumerable<NativeModalMassRow> ModalMass => ModalResultRows.Mass(Result);
    public IEnumerable<int> ModeNumbers => Result?.Modes.Select(mode => mode.ModeNumber) ?? [];
    public int SelectedMode
    {
        get => selectedMode;
        set { if (Set(ref selectedMode, value)) { Notify(nameof(ModeShape)); ModeChanged?.Invoke(); } }
    }
    public IEnumerable<NativeModeShapeRow> ModeShape => run is null ? [] : ModalResultRows.Shape(Result, SelectedMode, Name);

    /// <summary>The selected mode for the viewer: relative amplitudes it magnifies and says so — not a displacement.</summary>
    public FemAnalysisResult? Display => run is null ? null : ModalResultRows.Display(Result, SelectedMode, Name);

    /// <summary>The mode shown changed: a viewer drawing it draws the new one.</summary>
    public event Action? ModeChanged;

    /// <summary>The run whose modes are shown; null clears them.</summary>
    internal void Show(FemDocumentResults? results)
    {
        run = results;
        selectedCase = CaseNames.FirstOrDefault();
        selectedMode = Result?.Modes.FirstOrDefault()?.ModeNumber ?? 1;
        Notify(nameof(CaseNames));
        Notify(nameof(HasResults));
        Notify(nameof(SelectedCase));
        Refresh();
    }

    private void Refresh()
    {
        foreach (var name in new[] { nameof(Modes), nameof(Participation), nameof(ModalMass), nameof(ModeNumbers), nameof(SelectedMode), nameof(ModeShape) }) Notify(name);
        ModeChanged?.Invoke();
    }
}
