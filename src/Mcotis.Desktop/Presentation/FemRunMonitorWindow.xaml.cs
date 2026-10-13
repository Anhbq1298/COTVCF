using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mcotis.Desktop.Presentation;

/// <summary>Non-modal window opened by Run Analysis. The run on top — status, progress, the equations and the clock —
/// then the note on numerical stability, the model in numbers and one entry per load case, which opens on its
/// convergence, its numerical stability by joint and degree of freedom, and its own log. The cable convergence chart
/// is on the second tab. Everything shown comes from <see cref="MainViewModel.FemMonitor"/>.</summary>
public partial class FemRunMonitorWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly DispatcherTimer clock;

    public FemRunMonitorWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        // the elapsed time of a run that is going; the run reports only when the solver does
        clock = new(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => viewModel.FemMonitor.Tick(DateTime.Now), Dispatcher);
        Closed += (_, _) => clock.Stop();
    }

    /// <summary>The wheel moves a table or a log inside a load case only while that can still move; then the page.</summary>
    private void ScrollRun(object sender, MouseWheelEventArgs e)
    {
        for (var at = e.OriginalSource as DependencyObject; at is not null && !ReferenceEquals(at, RunScroll);
             at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is ScrollViewer inner && inner.ScrollableHeight > 0 &&
                (e.Delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight : inner.VerticalOffset > 0)) return;
        }
        RunScroll.ScrollToVerticalOffset(RunScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
