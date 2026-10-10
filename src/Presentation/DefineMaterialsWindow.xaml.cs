using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using COT_VCF.Domain;

namespace COT_VCF.Presentation;

/// <summary>
/// Define → Materials, laid out like SAP2000's Define Materials: the material list, Get From SAP2000, Add New / Add Copy /
/// Modify/Show / Delete, Show Advanced Properties, OK / Cancel. The list is edited as a copy; OK hands it to the view model.
/// </summary>
public partial class DefineMaterialsWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly ObservableCollection<MaterialProperty> working;
    private readonly Dictionary<string, string> renamed = new(StringComparer.Ordinal);   // original name → current name

    /// <summary>Opens a material in the property form; returns the edited material or null (tests replace it).</summary>
    internal Func<MaterialProperty, bool, IReadOnlySet<string>, MaterialProperty?> EditMaterial { get; set; }

    public DefineMaterialsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        viewModel.EnsureMaterials();
        working = new(viewModel.Materials);
        MaterialList.ItemsSource = working;
        if (working.Count > 0) MaterialList.SelectedIndex = 0;
        EditMaterial = (material, isNew, taken) =>
        {
            var form = new MaterialPropertyWindow(material, isNew, ShowAdvanced.IsChecked == true, taken) { Owner = this };
            return form.ShowDialog() == true ? form.Result : null;
        };
        UpdateState();
    }

    internal IReadOnlyList<MaterialProperty> Working => working;
    private MaterialProperty? Selected => MaterialList.SelectedItem as MaterialProperty;
    private IReadOnlySet<string> Names(string? except = null) =>
        working.Select(material => material.Name).Where(name => name != except).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void UpdateState()
    {
        var selected = Selected;
        AddCopyButton.IsEnabled = ModifyButton.IsEnabled = selected is not null;
        var usage = selected is null ? 0 : viewModel.MaterialUsage(OriginalName(selected.Name));
        DeleteButton.IsEnabled = selected is not null && usage == 0;
        DeleteButton.ToolTip = usage > 0 ? $"In use by {usage} member{(usage == 1 ? "" : "s")} — cannot be deleted" : "Delete the selected material";
        GetFromSapButton.IsEnabled = !viewModel.IsBusy;
        GetFromSapButton.ToolTip = "Read every material of the model open in the running SAP2000 (read-only; works without Connect SAP)";
        if (selected is not null && StatusText.Tag is not "keep")
            StatusText.Text = $"{MaterialProperty.Title(selected.Kind)} · source: {selected.Source}" + (usage > 0 ? $" · used by {usage} member{(usage == 1 ? "" : "s")}" : "");
        StatusText.Tag = null;
    }

    private string OriginalName(string current) => renamed.FirstOrDefault(pair => pair.Value == current).Key ?? current;

    private string UniqueName(string stem)
    {
        var taken = Names();
        if (!taken.Contains(stem)) return stem;
        for (var index = 1; ; index++) if (!taken.Contains($"{stem} {index}")) return $"{stem} {index}";
    }

    // ---- actions ----
    internal async Task GetFromSapAsync()
    {
        try
        {
            var selected = Selected?.Name;
            var read = await viewModel.ReadMaterialsFromSapAsync();
            foreach (var material in read)
            {
                var index = working.ToList().FindIndex(item => item.Name.Equals(material.Name, StringComparison.Ordinal));
                if (index >= 0) working[index] = material; else working.Add(material);
            }
            // alphabetical, as SAP2000 lists them; keep the selection (or select the first)
            var sorted = working.OrderBy(material => material.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            working.Clear();
            foreach (var material in sorted) working.Add(material);
            MaterialList.SelectedItem = working.FirstOrDefault(material => material.Name == selected) ?? working.FirstOrDefault();
            StatusText.Text = $"Read {read.Count} materials from SAP2000 (read-only) — {read.Count(material => material.Kind == MaterialKind.Concrete)} concrete, " +
                $"{read.Count(material => material.Kind == MaterialKind.Steel)} steel, {read.Count(material => material.Kind is MaterialKind.Tendon or MaterialKind.Rebar)} tendon / rebar. OK keeps them.";
        }
        catch (Exception ex) { StatusText.Text = "Get From SAP2000 failed: " + ex.Message; }
        StatusText.Tag = "keep";
        UpdateState();
    }

    internal void AddNew(MaterialKind kind = MaterialKind.Concrete)
    {
        var name = UniqueName(kind == MaterialKind.Concrete ? "C30/37" : MaterialProperty.Title(kind));
        if (EditMaterial(MaterialProperty.Default(kind, name), true, Names()) is { } created) { working.Add(created); MaterialList.SelectedItem = created; }
    }

    internal void AddCopy()
    {
        if (Selected is not { } source) return;
        var copy = source with { Name = UniqueName(source.Name), Source = "App" };
        if (EditMaterial(copy, false, Names()) is { } created) { working.Add(created); MaterialList.SelectedItem = created; }
    }

    internal void Modify()
    {
        if (Selected is not { } source) return;
        if (EditMaterial(source, false, Names(except: source.Name)) is not { } edited) return;
        var index = working.IndexOf(source);
        working[index] = edited;
        if (edited.Name != source.Name)
        {
            var original = OriginalName(source.Name);
            renamed[original] = edited.Name;
        }
        MaterialList.SelectedItem = edited;
    }

    internal void Delete()
    {
        if (Selected is not { } source || viewModel.MaterialUsage(OriginalName(source.Name)) > 0) return;
        working.Remove(source);
        if (working.Count > 0) MaterialList.SelectedIndex = 0;
    }

    internal void Commit()
    {
        viewModel.CommitMaterials(working.ToArray(), renamed);
        Close();
    }

    private async void GetFromSap_Click(object sender, RoutedEventArgs e) => await GetFromSapAsync();
    private void AddNew_Click(object sender, RoutedEventArgs e) => AddNew();
    private void AddCopy_Click(object sender, RoutedEventArgs e) => AddCopy();
    private void Modify_Click(object sender, RoutedEventArgs e) => Modify();
    private void Delete_Click(object sender, RoutedEventArgs e) => Delete();
    private void Ok_Click(object sender, RoutedEventArgs e) => Commit();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void MaterialList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();
}
