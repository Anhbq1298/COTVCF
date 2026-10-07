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
    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(nameof(SearchText), typeof(string), typeof(FemResultsTree), new PropertyMetadata("", (d, _) => ((FemResultsTree)d).Rebuild()));
    public static readonly DependencyProperty IssuesOnlyProperty = DependencyProperty.Register(nameof(IssuesOnly), typeof(bool), typeof(FemResultsTree), new PropertyMetadata(false, (d, _) => ((FemResultsTree)d).Rebuild()));
    public static readonly DependencyProperty SelectedReasonProperty = DependencyProperty.Register(nameof(SelectedReason), typeof(string), typeof(FemResultsTree), new PropertyMetadata(""));
    public string SearchText { get => (string)GetValue(SearchTextProperty); set => SetValue(SearchTextProperty, value); }
    public bool IssuesOnly { get => (bool)GetValue(IssuesOnlyProperty); set => SetValue(IssuesOnlyProperty, value); }
    public string SelectedReason { get => (string)GetValue(SelectedReasonProperty); private set => SetValue(SelectedReasonProperty, value); }
    public bool CableFitSelected { get; private set; }
    private bool navigating;
    private sealed record Selection(FemStudyRun Run, FemResultMode? Mode = null, bool CableFit = false);
    public FemResultsTree()
    {
        Loaded += (_, _) => { Subscribe(true); Rebuild(); };
        Unloaded += (_, _) => Subscribe(false);
        SelectedItemChanged += (_, _) =>
        {
            if (SelectedItem is not TreeViewItem { Tag: Selection selection })
            {
                CanViewResults = false;
                SelectedReason = (SelectedItem as TreeViewItem)?.ToolTip as string ?? "";
                return;
            }
            SelectedReason = selection.Run.Result.TargetFitFailure ?? "";
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
        if (!navigating && (e.Property == SelectedRunProperty || e.Property == ResultModeProperty)) Rebuild();
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
    private bool Matches(string id) => id.Contains(SearchText?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
    private void Rebuild()
    {
        Items.Clear(); CanViewResults = false; SelectedReason = "";
        if (SelectedRun is not { } run)
        {
            Items.Add(new TreeViewItem { Header = "Choose a completed candidate", IsEnabled = false });
            return;
        }
        foreach (var (title, mode) in new[] { ("Axial forces / cables", FemResultMode.AxialForce), ("Bending moments", FemResultMode.BendingMoment), ("Shear forces", FemResultMode.Shear), ("Support reactions", FemResultMode.Reactions), ("Deflection", FemResultMode.DeflectedShape) })
        {
            var item = new TreeViewItem { Header = title, Tag = new Selection(run, mode) };
            Items.Add(item);
            if (!CableFitSelected && mode == ResultMode) item.IsSelected = true;
        }
        var fit = new TreeViewItem { Header = "Cable target fit", Tag = new Selection(run, CableFit: true) };
        Items.Add(fit);
        if (CableFitSelected) fit.IsSelected = true;
    }
}
