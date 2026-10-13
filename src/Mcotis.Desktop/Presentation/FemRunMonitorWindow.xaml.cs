using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

namespace Mcotis.Desktop.Presentation;

/// <summary>Non-modal window opened by Run Analysis: progress, then three views of the run — the analysis log (laid
/// out like SAP2000's, and what the window opens on), the equilibrium of every load case as a table, and the cable
/// convergence chart.</summary>
public partial class FemRunMonitorWindow : Window
{
    private readonly MainViewModel viewModel;
    private int shownLines;
    private bool rewrite = true, queued;

    public FemRunMonitorWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        ((INotifyCollectionChanged)viewModel.FemLog).CollectionChanged += LogChanged;
        Closed += (_, _) => ((INotifyCollectionChanged)viewModel.FemLog).CollectionChanged -= LogChanged;
        ShowLog();
    }

    private void LogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Lines are only ever added at the end while a run goes; anything else (a new run, old lines dropped) means
        // the text has to be written again. The log can change many times in one go, so the box is brought up to date
        // once, afterwards.
        if (e.Action != NotifyCollectionChangedAction.Add) rewrite = true;
        if (queued) return;
        queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ShowLog);
    }

    /// <summary>Brings the log box up to date with the run's log and keeps its newest line in view.</summary>
    private void ShowLog()
    {
        queued = false;
        var lines = viewModel.FemLog;
        if (rewrite || lines.Count < shownLines)
        {
            LogBox.Text = string.Concat(lines.Select(line => line + Environment.NewLine));
            rewrite = false;
        }
        else if (lines.Count > shownLines)
            LogBox.AppendText(string.Concat(lines.Skip(shownLines).Select(line => line + Environment.NewLine)));
        else return;
        shownLines = lines.Count;
        LogBox.ScrollToEnd();
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
