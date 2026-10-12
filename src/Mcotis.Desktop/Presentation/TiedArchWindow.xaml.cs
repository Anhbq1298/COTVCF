using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Edit → Tied Arch System. Shows the parameters of the open tied arch system (the TAS defaults for another model), with
/// a live preview and the SLS / ULS point load on each top chord joint; Apply / OK rebuilds the model from them
/// (<see cref="MainViewModel.ApplyTiedArchSystem"/>).
/// </summary>
public partial class TiedArchWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool loading;

    public TiedArchWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        Fill(SelfWeightBox, TiedArchParameters.SelfWeightChoices);
        Fill(SuperimposedDeadBox, TiedArchParameters.SuperimposedDeadChoices);
        Fill(LiveBox, TiedArchParameters.LiveChoices);
        Fill(FloorsBox, TiedArchParameters.FloorChoices);
        LoadFromModel();
    }

    private static void Fill<T>(ComboBox box, IEnumerable<T> values)
    {
        foreach (var value in values) box.Items.Add(value);
    }

    /// <summary>The form set to the open tied arch system, or to the TAS defaults.</summary>
    internal void LoadFromModel() => Show(viewModel.TiedArchParameters ?? TiedArchParameters.Default);

    private void Show(TiedArchParameters p)
    {
        loading = true;
        try
        {
            ArchRiseBox.Text = Text(p.ArchRise);
            DeckHeightBox.Text = Text(p.DeckHeight);
            BaysBox.Text = p.Bays.ToString(CultureInfo.CurrentCulture);
            SpacingBox.Text = Text(p.BaySpacing);
            WidthBox.Text = Text(p.TributaryWidth);
            SelfWeightBox.SelectedItem = p.SelfWeight;
            SuperimposedDeadBox.SelectedItem = p.SuperimposedDead;
            LiveBox.SelectedItem = p.Live;
            FloorsBox.SelectedItem = p.Floors;
        }
        finally { loading = false; }
        Refresh();
    }

    private static string Text(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static double? Number(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) ? value : null;

    /// <summary>The parameters in the form, or null with the problems.</summary>
    internal TiedArchParameters? FormParameters(out IReadOnlyList<string> problems)
    {
        var unreadable = new (TextBox Box, string Name)[] { (ArchRiseBox, "H1"), (DeckHeightBox, "H2"), (BaysBox, "Number of bays"), (SpacingBox, "Bay spacing"), (WidthBox, "Tributary width B") }
            .Where(item => Number(item.Box) is null).Select(item => $"{item.Name}: enter a number.").ToArray();
        if (unreadable.Length > 0) { problems = unreadable; return null; }
        var bays = Number(BaysBox)!.Value;
        if (bays != Math.Floor(bays)) { problems = ["Number of bays: enter a whole number."]; return null; }
        if (SelfWeightBox.SelectedItem is not double sw || SuperimposedDeadBox.SelectedItem is not double sdl || LiveBox.SelectedItem is not double ll || FloorsBox.SelectedItem is not int floors)
        {
            problems = ["Choose SW, SDL, LL and the number of floors."];
            return null;
        }
        var p = new TiedArchParameters(Number(ArchRiseBox)!.Value, Number(DeckHeightBox)!.Value, (int)bays, Number(SpacingBox)!.Value, Number(WidthBox)!.Value, sw, sdl, ll, floors);
        problems = p.Problems();
        return problems.Count == 0 ? p : null;
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (!loading && IsInitialized) Refresh();
    }

    private void Refresh()
    {
        var p = FormParameters(out var problems);
        SpanText.Text = Number(BaysBox) is { } bays && Number(SpacingBox) is { } spacing ? Text(bays * spacing) : "—";
        OkButton.IsEnabled = ApplyButton.IsEnabled = p is not null;
        if (p is null)
        {
            ProblemText.Text = string.Join(Environment.NewLine, problems);
            SlsText.Text = UlsText.Text = LoadNote.Text = "";
            return;
        }
        var q = p.SelfWeight + p.SuperimposedDead + p.Live;
        var area = $"{Text(p.BaySpacing)} × {Text(p.TributaryWidth)} × {p.Floors}";
        SlsText.Text = $"SLS = (SW + SDL + LL) × s × B × n{Environment.NewLine}    = {Text(q)} × {area} = {p.SlsPointLoad:N0} kN";
        UlsText.Text = $"ULS = (1.35 SW + 1.35 SDL + 1.5 LL) × s × B × n{Environment.NewLine}    = {Text(1.35 * (p.SelfWeight + p.SuperimposedDead) + 1.5 * p.Live)} × {area} = {p.UlsPointLoad:N0} kN";
        LoadNote.Text = $"Applied to the model as DEAD_above (SW + SDL) {p.DeadPointLoad:N0} kN and LL_above (LL) {p.LivePointLoad:N0} kN on each " +
                        "inner top chord joint, half at the two end joints (half a bay each); LL_left / LL_right load each half. " +
                        "The ULS and SLS combinations of the model apply the factors.";
        try
        {
            var (geometry, supports, sections) = viewModel.PreviewTiedArch(p);
            var deck = geometry.Points.Values.Where(point => Math.Abs(point.Position.Z - p.DeckHeight) < 1e-6).OrderBy(point => point.Position.X).ToArray();
            Preview.Model = geometry;
            Preview.PinSupportPoints = supports;
            Preview.MemberSections = sections;
            Preview.AppliedPoints = deck.Select(point => new AppliedPoint { PointName = point.Name, X = point.Position.X, Y = point.Position.Y, Z = point.Position.Z }).ToArray();
            Preview.LoadValues = deck.ToDictionary(point => point.Name, point => point == deck[0] || point == deck[^1] ? -p.SlsPointLoad / 2 : -p.SlsPointLoad, StringComparer.Ordinal);
            ProblemText.Text = "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.InvalidDataException)
        {
            ProblemText.Text = ex.Message;
            OkButton.IsEnabled = ApplyButton.IsEnabled = false;
        }
    }

    private bool Apply()
    {
        if (FormParameters(out var problems) is not { } p)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, problems), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (viewModel.TiedArchParameters is null && viewModel.HasWorkingModel && MessageBox.Show(this,
                "Replace the current model with the tied arch system? Save the configuration first to keep it.",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK) return false;
        try
        {
            var summary = viewModel.ApplyTiedArchSystem(p);
            if (!summary.ReadyToRun)
                MessageBox.Show(this, string.Join(Environment.NewLine, summary.Lines.Select(line => "• " + line)), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The tied arch system could not be applied: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Apply()) Close();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Reset_Click(object sender, RoutedEventArgs e) => Show(TiedArchParameters.Default);
}
