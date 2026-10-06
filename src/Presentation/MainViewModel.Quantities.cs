using System.Globalization;
using System.IO;
using COT_VCF.Application.Quantities;

namespace COT_VCF.Presentation;

/// <summary>One line of the Tab 08 table. A null quantity is unavailable and is shown as n/a — never as 0.</summary>
public sealed record QuantityRow(
    string Section,
    int MemberCount,
    double? TotalLengthM,
    double? ConcreteVolumeM3,
    double? SteelMassT,
    bool IsTotal,
    string Detail,
    string? Problem,
    QuantitySectionFigure? Figure = null)
{
    /// <summary>Overall width × depth of the section figure in mm. Each figure is scaled to fit its cell, so this
    /// is what tells a 3 m column from a 0.2 m cable.</summary>
    public string FigureSize => Figure is { } figure
        ? string.Create(CultureInfo.InvariantCulture, $"{figure.Width * 1000:0.#} × {figure.Height * 1000:0.#} mm")
        : "";
}

public enum QuantityViewState { NoCandidate, Loading, Ready, Unavailable, Error }

// Tab 08 — Quantity Take-off: concrete volume and steel mass of one study candidate, by assigned section, from that
// candidate's own SAP2000 model data. Reads only; never the connected model, never a write.
public sealed partial class MainViewModel
{
    private const int TabQuantities = 8;
    private readonly QuantityCandidateCatalog quantityCatalog = new();
    private readonly QuantityTakeoffService quantityTakeoff;
    private IReadOnlyList<QuantityCandidate> quantityCandidates = [];
    private QuantityCandidate? selectedQuantityCandidate;
    private IReadOnlyList<QuantityRow> quantityRows = [];
    private IReadOnlyList<string> quantityNotes = [];
    private QuantityViewState quantityState = QuantityViewState.NoCandidate;
    private string quantityProblem = "", quantitySource = "";
    private string? quantityStudyRoot;
    private int quantityRequest;
    private CancellationTokenSource? quantityCancellation;
    private Task quantitySapRead = Task.CompletedTask;
    private bool quantitySapReading;

    public IReadOnlyList<QuantityCandidate> QuantityCandidates => quantityCandidates;
    public IReadOnlyList<QuantityRow> QuantityRows => quantityRows;
    public IReadOnlyList<string> QuantityNotes => quantityNotes;
    public QuantityViewState QuantityState => quantityState;
    public bool QuantityLoading => quantityState == QuantityViewState.Loading;
    /// <summary>A separate SAP2000 instance is reading a private copy of the candidate's model.</summary>
    public bool QuantitySapReading => quantitySapReading;
    public string QuantitySource => quantitySource;
    /// <summary>Why there is no take-off to show; empty when there is one.</summary>
    public string QuantityProblem => quantityProblem;
    public bool HasQuantityProblem => quantityProblem.Length > 0;
    public bool QuantityProblemIsError => quantityState == QuantityViewState.Error;

    public QuantityCandidate? SelectedQuantityCandidate
    {
        get => selectedQuantityCandidate;
        set
        {
            if (Equals(selectedQuantityCandidate, value)) return;
            selectedQuantityCandidate = value;
            Notify();
            LoadQuantities();
        }
    }

    /// <summary>Second line of the tab header.</summary>
    public string QuantityTabStatus =>
        !HasSapConnection ? "Connect SAP"
        : quantityState switch
        {
            QuantityViewState.Loading => "Reading…",
            QuantityViewState.Ready => selectedQuantityCandidate?.Id + (QuantityIncomplete ? " · incomplete" : ""),
            QuantityViewState.Unavailable => "▲ Unavailable",
            QuantityViewState.Error => "× Read failed",
            _ => quantityCandidates.Count == 0 ? "No candidate models" : $"{quantityCandidates.Count:N0} candidates",
        };

    /// <summary>The line beside the candidate dropdown.</summary>
    public string QuantityStatus => quantityState switch
    {
        QuantityViewState.Loading => quantitySapReading
            ? $"Reading {selectedQuantityCandidate?.Id} in a separate SAP2000 instance…"
            : $"Reading {selectedQuantityCandidate?.Id}…",
        QuantityViewState.Ready => $"{quantityRows.Count(row => !row.IsTotal):N0} sections · {quantityRows.Where(row => !row.IsTotal).Sum(row => row.MemberCount):N0} members" +
            (QuantityIncomplete ? " · ▲ some quantities are unavailable — see the notes" : ""),
        QuantityViewState.Unavailable or QuantityViewState.Error => "",
        _ => !HasSapConnection ? "Connect to SAP2000 to list the candidate models."
            : quantityCandidates.Count == 0 ? "No candidate model found in the study folder of the connected baseline — build the study on Tab 05."
            : "Select a candidate model.",
    };

    private bool QuantityIncomplete => quantityRows.Any(row => row.TotalLengthM is null || row.ConcreteVolumeM3 is null || row.SteelMassT is null);

    private Command? reloadQuantities, cancelQuantityRead;
    /// <summary>Looks for candidate models again and reads the selected one again.</summary>
    public Command ReloadQuantities => reloadQuantities ??= new(_ => { RefreshQuantityCandidates(); LoadQuantities(reload: true); },
        () => !quantitySapReading);
    public Command CancelQuantityRead => cancelQuantityRead ??= new(_ => CancelQuantityRequest(), () => quantitySapReading);

    /// <summary>Brings the candidate list in line with the study folder of the connected baseline. The list object is
    /// kept while its contents are the same, so the dropdown keeps its selection.</summary>
    private void RefreshQuantityCandidates()
    {
        var connected = HasSapConnection;
        quantityStudyRoot = connected ? StudyRoot : null;
        SetQuantityCandidates(connected ? quantityCatalog.Discover(BaselineModelPath) : []);
    }

    private void SetQuantityCandidates(IReadOnlyList<QuantityCandidate> found)
    {
        if (!found.SequenceEqual(quantityCandidates))
        {
            var selectedId = selectedQuantityCandidate?.Id;
            quantityCandidates = found;
            Notify(nameof(QuantityCandidates));
            SelectedQuantityCandidate = selectedId is null ? null : found.FirstOrDefault(candidate => candidate.Id == selectedId);
        }
        NotifyQuantities();
    }

    /// <summary>The SAP connection, the model or the session baseline changed. Without a connection the tab holds
    /// nothing; with another study it starts again. The study folder is looked at only while the tab is shown.</summary>
    private void OnSapSessionChangedForQuantities()
    {
        if (!HasSapConnection || selectedTab == TabQuantities) RefreshQuantityCandidates();
        else if (quantityStudyRoot is not null && !SamePath(quantityStudyRoot, StudyRoot))
        {
            quantityStudyRoot = null;
            SetQuantityCandidates([]);
        }
        else NotifyQuantities();
    }

    private void CancelQuantityRequest()
    {
        try { quantityCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async void LoadQuantities(bool reload = false)
    {
        var request = ++quantityRequest;                    // only the latest request may put anything on the tab
        CancelQuantityRequest();
        var candidate = selectedQuantityCandidate;
        quantityRows = []; quantityNotes = []; quantityProblem = ""; quantitySource = "";
        if (candidate is null || !HasSapConnection)
        {
            quantityState = QuantityViewState.NoCandidate;
            NotifyQuantities();
            return;
        }
        var cancellation = quantityCancellation = new CancellationTokenSource();
        quantityState = QuantityViewState.Loading;
        NotifyQuantities();
        try
        {
            var attempt = await quantityTakeoff.TryTextExportAsync(candidate, cancellation.Token);
            if (request != quantityRequest) return;
            var result = attempt.Result ?? await ReadQuantitiesWithSapAsync(candidate, attempt.Rejected ?? "its text export cannot be used", request, cancellation.Token, reload);
            if (request != quantityRequest || result is null) return;
            ShowQuantities(result, attempt.Result is null ? attempt.Rejected : null);
        }
        catch (OperationCanceledException)
        {
            if (request == quantityRequest) ShowQuantityProblem(QuantityViewState.Unavailable, $"Reading {candidate.Id} was cancelled. Choose Reload to read it again.");
        }
        catch (Exception ex)
        {
            if (request == quantityRequest) ShowQuantityProblem(QuantityViewState.Error, $"The quantities of {candidate.Id} could not be read: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(quantityCancellation, cancellation)) quantityCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>The isolated SAP2000 read: one at a time, and never while another operation holds the application.
    /// Returns null when the request was superseded or the read cannot start — the reason is then on the tab.</summary>
    private async Task<QuantityLoadResult?> ReadQuantitiesWithSapAsync(QuantityCandidate candidate, string rejected, int request, CancellationToken token, bool reload)
    {
        var why = $"Quantities of {candidate.Id} are unavailable: {rejected}";
        if (!quantityTakeoff.CanReadWithSap)
        {
            ShowQuantityProblem(QuantityViewState.Unavailable, $"{why}. Reading the model itself needs SAP2000, which this session cannot start.");
            return null;
        }
        if (!File.Exists(candidate.ModelPath))
        {
            ShowQuantityProblem(QuantityViewState.Unavailable, $"{why}, and the model {Path.GetFileName(candidate.ModelPath)} is not on disk.");
            return null;
        }
        await quantitySapRead;                              // an earlier read is still closing its SAP2000
        if (request != quantityRequest) return null;
        token.ThrowIfCancellationRequested();
        if (IsBusy)
        {
            ShowQuantityProblem(QuantityViewState.Unavailable, $"{why}. The model has to be read with SAP2000, which waits for the operation that is running — choose Reload when it has finished.");
            return null;
        }
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        quantitySapRead = finished.Task;
        quantitySapReading = true;
        IsBusy = true;
        Message = $"Reading {candidate.Id} from a private copy of its model in a separate SAP2000 instance ({rejected}). The connected model is not touched.";
        NotifyQuantities();
        try { return await quantityTakeoff.ReadWithSapAsync(candidate, token, reload); }
        finally
        {
            quantitySapReading = false;
            IsBusy = false;
            finished.SetResult();
            NotifyQuantities();
        }
    }

    private void ShowQuantities(QuantityLoadResult result, string? exportRejected)
    {
        var takeoff = result.Takeoff;
        var total = takeoff.Total;
        string N(double value) => value.ToString("N3", CultureInfo.InvariantCulture);
        var gaps = takeoff.Sections.Where(section => !section.Complete).Select(section => section.Section).ToArray();
        quantityRows = takeoff.Sections.Select(section => new QuantityRow(section.Section, section.MemberCount, section.TotalLengthM, section.ConcreteVolumeM3,
                section.SteelMassT, false, section.Basis, section.Problems.Count == 0 ? null : string.Join(Environment.NewLine, section.Problems), section.Figure))
            .Append(new QuantityRow("Total", total.MemberCount, total.TotalLengthM, total.ConcreteVolumeM3, total.SteelMassT, true,
                gaps.Length == 0 ? "Sum of every section of the selected candidate." : "Sum over the sections — incomplete.",
                gaps.Length == 0 ? null
                    : $"Not a complete total: {string.Join(", ", gaps)} could not be taken off in full. " +
                      $"The sections that could add up to {N(total.AvailableLengthM)} m, {N(total.AvailableConcreteVolumeM3)} m³ concrete and {N(total.AvailableSteelMassT)} t steel."))
            .ToArray();
        var notes = new List<string>();
        if (exportRejected is not null) notes.Add($"The text export was not used: {exportRejected}.");
        if (result.Candidate.RecordWarning is { } warning) notes.Add(warning);
        notes.AddRange(takeoff.Notes);
        quantityNotes = notes;
        quantitySource = $"{result.Candidate.Id} · {result.SourceText}";
        quantityProblem = "";
        quantityState = QuantityViewState.Ready;
        Message = gaps.Length == 0
            ? $"Quantity take-off of {result.Candidate.Id} read from its own model data. No SAP2000 model was changed."
            : $"Quantity take-off of {result.Candidate.Id} read; {gaps.Length} section(s) could not be taken off in full — see Tab 08.";
        NotifyQuantities();
    }

    private void ShowQuantityProblem(QuantityViewState state, string problem)
    {
        quantityRows = []; quantityNotes = []; quantitySource = "";
        quantityProblem = problem;
        quantityState = state;
        Message = problem;
        NotifyQuantities();
    }

    private void NotifyQuantities()
    {
        foreach (var name in new[]
        {
            nameof(QuantityRows), nameof(QuantityNotes), nameof(QuantityState), nameof(QuantityLoading), nameof(QuantitySapReading),
            nameof(QuantitySource), nameof(QuantityProblem), nameof(HasQuantityProblem), nameof(QuantityProblemIsError),
            nameof(QuantityTabStatus), nameof(QuantityStatus)
        }) Notify(name);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
