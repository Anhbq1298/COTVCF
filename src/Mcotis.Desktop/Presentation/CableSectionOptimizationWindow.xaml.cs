using Mcotis.Desktop.Application;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Mcotis.Desktop.Presentation;

/// <summary>The reviewed, iterative Cable / Tendon Section Optimisation window.</summary>
public partial class CableSectionOptimizationWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel viewModel;
    private CancellationTokenSource? iterationCancellation;
    private CableSectionOptimizationRun? run;
    private bool isRunning;
    private bool loadingRunSettings;

    public ObservableCollection<CableSectionOptimizationRow> Rows { get; } = [];
    public ObservableCollection<CableSectionOptimizationIteration> Iterations { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public CableSectionOptimizationWindow(MainViewModel viewModel, CableSectionOptimizationSettings? initialSettings = null)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = this;
        var prior = viewModel.LoadCableSectionOptimization();
        if (prior is not null && prior.DocumentId == viewModel.NativeDocumentSession.Document.DocumentId.Value)
            ShowRun(prior);
        else
            RefreshApplyState();
        if (initialSettings is { } settings)
        {
            LoadRunSettings(settings);
            RefreshApplyState();
        }
    }

    private async void Iterate_Click(object sender, RoutedEventArgs e)
    {
        if (isRunning) return;
        HideError();
        if (!TryReadSettings(out var settings, out var problem))
        {
            ShowError(problem);
            return;
        }

        SetRunning(true);
        iterationCancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<CableSectionOptimizationProgress>(UpdateProgress);
            run = await viewModel.RunCableSectionOptimizationAsync(settings, progress, iterationCancellation.Token);
            ShowRun(run);
            if (run.Failure is { Length: > 0 } failure) ShowError(failure);
            else if (run.Cancelled) ShowError("Iteration was cancelled. The partial history is retained for review; its results cannot be applied.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            iterationCancellation?.Dispose();
            iterationCancellation = null;
            SetRunning(false);
        }
    }

    private bool TryReadSettings(out CableSectionOptimizationSettings settings, out string problem)
    {
        settings = CableSectionOptimizationSettings.Default;
        problem = "";
        if (!TryDouble(TargetUrBox.Text, out var targetUr))
        { problem = "Enter a numeric Target UR."; TargetUrBox.Focus(); return false; }
        if (!TryDouble(BreakingStressBox.Text, out var breakingStress))
        { problem = "Enter a numeric breaking stress in MPa."; BreakingStressBox.Focus(); return false; }
        if (!TryDouble(PreliminaryCoefficientBox.Text, out var coefficient))
        { problem = "Enter a numeric preliminary coefficient."; PreliminaryCoefficientBox.Focus(); return false; }
        if (!int.TryParse(MaximumIterationsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var maximumIterations) &&
            !int.TryParse(MaximumIterationsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out maximumIterations))
        { problem = "Enter a whole number of maximum iterations from 1 to 50."; MaximumIterationsBox.Focus(); return false; }
        if (!TryDouble(ConvergenceToleranceBox.Text, out var tolerance))
        { problem = "Enter a numeric convergence tolerance in percent."; ConvergenceToleranceBox.Focus(); return false; }

        settings = new(targetUr, breakingStress, coefficient, maximumIterations, tolerance);
        try { settings.Validate(); }
        catch (ArgumentOutOfRangeException ex)
        {
            problem = ex.Message;
            return false;
        }
        return true;
    }

    private static bool TryDouble(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void UpdateProgress(CableSectionOptimizationProgress progress)
    {
        Rows.Clear();
        foreach (var row in progress.Rows) Rows.Add(row);
        Iterations.Add(progress.Iteration);
        HistoryGrid.ItemsSource = Iterations;
        var max = ReadMaximumForSummary();
        SummaryText.Text = Summary(progress.Iteration.Number, max, progress.Iteration.MaximumForceChangePercent,
            progress.Iteration.MaximumDeflectionChangePercent, progress.Status);
        ResultsGrid.SelectedItem = Rows.FirstOrDefault();
        RefreshDetails();
        RefreshApplyState();
    }

    private void ShowRun(CableSectionOptimizationRun value)
    {
        run = value;
        LoadRunSettings(value.Settings);
        Rows.Clear();
        foreach (var row in value.Rows) Rows.Add(row);
        Iterations.Clear();
        foreach (var iteration in value.Iterations) Iterations.Add(iteration);
        HistoryGrid.ItemsSource = Iterations;
        var status = value.Failure is { Length: > 0 } failure ? $"Failed · {failure}"
            : value.Cancelled ? "Cancelled" : value.Converged ? "Converged"
            : value.IterationCount == 0 ? "Not run" : "Maximum iterations reached";
        SummaryText.Text = Summary(value.IterationCount, value.Settings.MaximumIterations,
            value.MaximumForceChangePercent, value.MaximumDeflectionChangePercent, status);
        ResultsGrid.SelectedItem = Rows.FirstOrDefault();
        RefreshDetails();
        RefreshApplyState();
    }

    private void RefreshApplyState()
    {
        var current = run is not null && viewModel.CableSectionOptimizationIsCurrent(run);
        var settingsMatch = run is not null && TryReadSettings(out var currentSettings, out _) && currentSettings == run.Settings;
        ApplyButton.IsEnabled = !isRunning && current && settingsMatch && viewModel.CanApplyCableSectionOptimization && run is { Failure: null, Cancelled: false } &&
            run.Rows.Any(row => row.CurrentStrands != row.SuggestedStrands);
        ApplyButton.ToolTip = !viewModel.CanApplyCableSectionOptimization ? "Finish the current operation and check that the model has usable geometry."
            : run is null ? "Run Iterate to calculate suggested strand counts."
            : !current ? "The model changed after this run. Iterate again before applying."
            : !settingsMatch ? "The optimization settings changed after this run. Iterate again before applying."
            : run.Failure is not null || run.Cancelled ? "Failed or cancelled results cannot be applied."
            : !run.Rows.Any(row => row.CurrentStrands != row.SuggestedStrands) ? "The suggested strand counts already match the current sections."
            : viewModel.IsFemModelLocked ? "Applying will unlock the model, clear current FEM results, and delete output databases registered to this locked revision."
            : "Apply the suggested section counts as one undoable edit. Unconverged results require confirmation.";
    }

    private void RefreshDetails()
    {
        if (ResultsGrid.SelectedItem is not CableSectionOptimizationRow selected)
        {
            SelectedDetailsText.Text = "Target Force and Calculated Force are available here when a tendon row is selected.";
            return;
        }
        SelectedDetailsText.Text = $"{selected.TendonName} · Target Force: {selected.TargetForceKN.ToString("N2", CultureInfo.CurrentCulture)} kN · " +
            $"Calculated Force: {selected.CalculatedForceKN.ToString("N2", CultureInfo.CurrentCulture)} kN";
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDetails();

    private void OptimizationSetting_Changed(object sender, TextChangedEventArgs e)
    {
        if (!loadingRunSettings && IsInitialized) RefreshApplyState();
    }

    private void LoadRunSettings(CableSectionOptimizationSettings settings)
    {
        loadingRunSettings = true;
        try
        {
            TargetUrBox.Text = settings.TargetUr.ToString("0.00##", CultureInfo.CurrentCulture);
            BreakingStressBox.Text = settings.BreakingStressMpa.ToString("0.##", CultureInfo.CurrentCulture);
            PreliminaryCoefficientBox.Text = settings.PreliminaryCoefficient.ToString("0.00##", CultureInfo.CurrentCulture);
            MaximumIterationsBox.Text = settings.MaximumIterations.ToString(CultureInfo.CurrentCulture);
            ConvergenceToleranceBox.Text = settings.ConvergenceTolerancePercent.ToString("0.00##", CultureInfo.CurrentCulture);
        }
        finally { loadingRunSettings = false; }
    }

    private void CancelIteration_Click(object sender, RoutedEventArgs e) => iterationCancellation?.Cancel();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (run is null) return;
        if (!TryReadSettings(out var settings, out _) || settings != run.Settings)
        {
            ShowError("The optimization settings changed after this run. Iterate again before applying.");
            RefreshApplyState();
            return;
        }
        if (!viewModel.CableSectionOptimizationIsCurrent(run))
        {
            ShowError("The model changed after this run. Iterate again on the current model before applying.");
            RefreshApplyState();
            return;
        }
        var unlockLockedModel = viewModel.IsFemModelLocked;
        if (!run.Converged || unlockLockedModel)
        {
            var reasons = new List<string>();
            if (!run.Converged)
            {
                var forceChange = Change(run.MaximumForceChangePercent);
                var deflectionChange = Change(run.MaximumDeflectionChangePercent);
                reasons.Add($"The convergence requirements were not met within {run.Settings.MaximumIterations} iterations. " +
                    $"Maximum force change: {forceChange}; maximum pretension deflection change: {deflectionChange}.");
            }
            if (unlockLockedModel)
                reasons.Add("Applying will unlock the model, clear its current FEM results, and delete output databases registered to this locked revision.");
            reasons.Add("Continue applying the suggested sections? Target cable forces will remain unchanged.");
            if (MessageBox.Show(this, string.Join("\n\n", reasons), "Apply Suggested Tendon Sections", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        try
        {
            if (!viewModel.ApplyCableSectionOptimization(run, confirmedUnconverged: !run.Converged))
            {
                if (viewModel.IsFemModelLocked) { RefreshApplyState(); return; }
                ShowError("The suggested strand counts already match the current sections.");
                return;
            }
            Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
            RefreshApplyState();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetRunning(bool value)
    {
        isRunning = value;
        TargetUrBox.IsEnabled = !value;
        BreakingStressBox.IsEnabled = !value;
        PreliminaryCoefficientBox.IsEnabled = !value;
        MaximumIterationsBox.IsEnabled = !value;
        ConvergenceToleranceBox.IsEnabled = !value;
        IterateButton.IsEnabled = !value;
        CancelIterationButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = false;
        if (value)
        {
            Rows.Clear();
            Iterations.Clear();
            HistoryGrid.ItemsSource = Iterations;
            SummaryText.Text = $"Iteration: 0 / {MaximumIterationsBox.Text}  ·  Maximum Force Change: —  ·  Pretension Deflection Change: —  ·  Starting";
        }
        else RefreshApplyState();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(isRunning)));
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private int ReadMaximumForSummary() =>
        int.TryParse(MaximumIterationsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : 30;

    private static string Summary(int iteration, int maximum, double? force, double? deflection, string status) =>
        $"Iteration: {iteration} / {maximum}  ·  Maximum Force Change: {Change(force)}  ·  Pretension Deflection Change: {Change(deflection)}  ·  {status}";

    private static string Change(double? value) => value is { } change
        ? change.ToString("0.00", CultureInfo.CurrentCulture) + "%" : "—";

    protected override void OnClosing(CancelEventArgs e)
    {
        if (isRunning)
        {
            e.Cancel = true;
            iterationCancellation?.Cancel();
            ShowError("Cancelling the current analysis. The window will remain open until the solver stops.");
        }
        base.OnClosing(e);
    }
}
