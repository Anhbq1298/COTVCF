using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Mcotis.Desktop.Presentation.Controls;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Result → Modal Results: the modes of the run's modal cases — the tables of the native workspace
/// (<see cref="ModalResultsView"/>) over <see cref="MainViewModel.ModalResults"/> — and Show Mode Shape, which draws the
/// chosen mode on the model as a mass-normalized eigenvector (relative amplitude, said as such).
/// </summary>
public sealed class ModalResultsWindow : Window
{
    private readonly MainViewModel viewModel;

    internal ModalResultsWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        Title = "Modal Results";
        Icon = AppIcon.Window;
        Width = 780; Height = 520; MinWidth = 560; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        DataContext = viewModel.ModalResults;

        var top = new DockPanel { Margin = new Thickness(10, 8, 10, 6), LastChildFill = false };
        var label = new TextBlock { Text = "Modal case", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var cases = new ComboBox { Width = 220 };
        System.Windows.Automation.AutomationProperties.SetName(cases, "Modal case");
        cases.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainModalResults.CaseNames)));
        cases.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(MainModalResults.SelectedCase)) { Mode = BindingMode.TwoWay });
        var show = new Button { Content = "Show Mode Shape", Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0),
            ToolTip = "Draw the mode chosen under Mode shape on the model (mass-normalized eigenvector, magnified; not a displacement)" };
        show.Click += (_, _) => viewModel.ShowModeShape();
        var close = new Button { Content = "Close", Width = 80, IsCancel = true };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(label);
        top.Children.Add(cases);
        top.Children.Add(show);
        top.Children.Add(close);

        var note = new TextBlock
        {
            Text = "Modes of the elastic frame, or a frame-and-cable model on a converged nonlinear static state. Mass is lumped from the case's mass source; mode amplitudes are relative, not load deflections. Stored with output schema v2 until the model is unlocked.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 10, 6),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(note);
        root.Children.Add(new ModalResultsView { Margin = new Thickness(10, 0, 10, 10) });
        Content = root;
    }
}
