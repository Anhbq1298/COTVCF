using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

namespace Mcotis.Desktop.Presentation;

/// <summary>Non-modal window opened by Run Analysis: progress, live FEM log and cable convergence chart.</summary>
public partial class FemRunMonitorWindow : Window
{
    public FemRunMonitorWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Keep the newest log line in view.
        ((INotifyCollectionChanged)viewModel.FemLog).CollectionChanged += ScrollLogToEnd;
        Closed += (_, _) => ((INotifyCollectionChanged)viewModel.FemLog).CollectionChanged -= ScrollLogToEnd;
    }

    private bool scrollQueued;

    private void ScrollLogToEnd(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Never touch the ListBox inside the collection's CollectionChanged: this handler can run before the ListBox has
        // processed the change, and scrolling then makes WPF see an inconsistent item count (it throws and the app
        // terminates). Scroll once, after the binding has caught up.
        if (e.Action != NotifyCollectionChangedAction.Add || scrollQueued) return;
        scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            scrollQueued = false;
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        });
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
