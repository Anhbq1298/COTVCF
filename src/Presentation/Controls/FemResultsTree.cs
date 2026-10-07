using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using COT_VCF.Application;

namespace COT_VCF.Presentation.Controls;

/// <summary>Presentation-only candidate and result navigation.</summary>
public sealed class FemResultsTree : TreeView
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.Register(nameof(Runs), typeof(IEnumerable<FemStudyRun>), typeof(FemResultsTree), new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty FitRowsProperty = DependencyProperty.Register(nameof(FitRows), typeof(IEnumerable<FemCableFitRow>), typeof(FemResultsTree), new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty SelectedRunProperty = DependencyProperty.Register(nameof(SelectedRun), typeof(FemStudyRun), typeof(FemResultsTree), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty ResultModeProperty = DependencyProperty.Register(nameof(ResultMode), typeof(FemResultMode), typeof(FemResultsTree), new FrameworkPropertyMetadata(FemResultMode.AxialForce, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty CanViewResultsProperty = DependencyProperty.Register(nameof(CanViewResults), typeof(bool), typeof(FemResultsTree), new PropertyMetadata(false));
    public IEnumerable<FemStudyRun>? Runs { get => (IEnumerable<FemStudyRun>?)GetValue(RunsProperty); set => SetValue(RunsProperty, value); }
    public IEnumerable<FemCableFitRow>? FitRows { get => (IEnumerable<FemCableFitRow>?)GetValue(FitRowsProperty); set => SetValue(FitRowsProperty, value); }
    public FemStudyRun? SelectedRun { get => (FemStudyRun?)GetValue(SelectedRunProperty); set => SetValue(SelectedRunProperty, value); }
    public FemResultMode ResultMode { get => (FemResultMode)GetValue(ResultModeProperty); set => SetValue(ResultModeProperty, value); }
    public bool CanViewResults { get => (bool)GetValue(CanViewResultsProperty); private set => SetValue(CanViewResultsProperty, value); }
    public bool CableFitSelected { get; private set; }
    private bool navigating;
    private sealed record Selection(FemStudyRun Run, FemResultMode? Mode = null, bool CableFit = false);
    public FemResultsTree()
    {
        Loaded += (_, _) => { Subscribe(true); Rebuild(); };
        Unloaded += (_, _) => Subscribe(false);
        SelectedItemChanged += (_, _) =>
        {
            if (SelectedItem is not TreeViewItem { Tag: Selection selection }) { CanViewResults = false; return; }
            navigating = true;
            try
            {
                SetCurrentValue(SelectedRunProperty, selection.Run);
                if (selection.Mode is { } mode) SetCurrentValue(ResultModeProperty, mode);
                CableFitSelected = selection.CableFit;
                CanViewResults = true;
            }
            finally { navigating = false; }
        };
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (navigating || (e.Property != SelectedRunProperty && e.Property != ResultModeProperty)) return;
        foreach (var candidate in Items.OfType<TreeViewItem>())
        {
            if (candidate.Tag is not Selection selection || selection.Run != SelectedRun) continue;
            var child = candidate.Items.OfType<TreeViewItem>().FirstOrDefault(item =>
                item.Tag is Selection value && (CableFitSelected ? value.CableFit : value.Mode == ResultMode));
            if (child is not null) { candidate.IsExpanded = true; child.IsSelected = true; }
            break;
        }
    }
    private static void SourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tree = (FemResultsTree)d;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= tree.Changed;
        if (tree.IsLoaded && e.NewValue is INotifyCollectionChanged current) current.CollectionChanged += tree.Changed;
        tree.Rebuild();
    }
    private void Subscribe(bool attach)
    {
        foreach (var source in new object?[] { Runs, FitRows })
            if (source is INotifyCollectionChanged collection)
            {
                collection.CollectionChanged -= Changed;
                if (attach) collection.CollectionChanged += Changed;
            }
    }
    private void Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, FitRows) && e.Action == NotifyCollectionChangedAction.Add &&
            e.NewItems?.Cast<FemCableFitRow>().All(row => !row.Failed) == true) return;
        Rebuild();
    }
    private void Rebuild()
    {
        var expanded = Items.OfType<TreeViewItem>().Where(item => item.IsExpanded).Select(item => (item.Tag as Selection)?.Run.Id).ToHashSet();
        var selected = (SelectedItem as TreeViewItem)?.Tag as Selection;
        Items.Clear(); CanViewResults = false;
        foreach (var run in Runs ?? [])
        {
            var candidate = new TreeViewItem { Header = run.Id + (run.Result.TargetFitConverged ? "" : " - Not fitted"), Tag = new Selection(run), IsExpanded = expanded.Contains(run.Id) };
            foreach (var (title, mode) in new[] { ("Axial forces / cables", FemResultMode.AxialForce), ("Bending moments", FemResultMode.BendingMoment), ("Shear forces", FemResultMode.Shear), ("Support reactions", FemResultMode.Reactions), ("Deflection", FemResultMode.DeflectedShape) })
                candidate.Items.Add(new TreeViewItem { Header = title, Tag = new Selection(run, mode) });
            candidate.Items.Add(new TreeViewItem { Header = "Cable target fit", Tag = new Selection(run, CableFit: true) });
            Items.Add(candidate);
            foreach (var item in candidate.Items.Cast<TreeViewItem>().Prepend(candidate))
                if (selected is not null && item.Tag is Selection value && value.Run.Id == selected.Run.Id && value.Mode == selected.Mode && value.CableFit == selected.CableFit) item.IsSelected = true;
        }
        foreach (var failed in (FitRows ?? []).Where(row => row.Failed))
            Items.Add(new TreeViewItem { Header = failed.RunId + " - Failed", ToolTip = failed.FitFailure, Foreground = (System.Windows.Media.Brush)FindResource("Error") });
        if (Items.Count == 0) Items.Add(new TreeViewItem { Header = "Run Analysis to populate results", IsEnabled = false });
    }
}
