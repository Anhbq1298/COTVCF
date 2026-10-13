using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Design → column → Optimization: the ranges that drive the cost of the column type — concrete outline (RC, steel encased,
/// mega), steel tube (CFT), encased I section (steel encased, mega), steel and concrete grade stepped grade by grade, bars —
/// the unit cost of each material, and Run: the cheapest sections that pass every ticked member and load case
/// (<see cref="ColumnOptimizer"/>), cost per metre of column.
/// </summary>
public partial class DesignReferenceWindow
{
    /// <summary>A unit cost the optimisation prices with: steel per grade [SGD/t], concrete per grade [SGD/m³], rebar [SGD/t].</summary>
    public sealed class RateRow(string kind, string grade, double rate, string unit) : Observable
    {
        private double rate = rate;
        public string Kind { get; } = kind;
        public string Grade { get; } = grade;
        public string Unit { get; } = unit;
        public double Rate { get => rate; set => Set(ref rate, double.IsFinite(value) && value >= 0 ? value : rate); }
    }

    /// <summary>One result row: the optimiser's result with the steel shown as grade and the fy used for its thickness.</summary>
    public sealed record OptimizationRow(int Rank, ColumnCandidate Candidate, DesignCheckSummary Check)
    {
        public string Steel => Candidate.SteelGrade == "—" ? "—" : $"{Candidate.SteelGrade} ({Candidate.Section.Fy:0})";
    }

    private Func<IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<DesignMemberInput>>? memberForces;
    private IReadOnlyList<MaterialProperty> optimizationMaterials = [];
    private readonly ObservableCollection<RateRow> rates = [];
    private readonly Dictionary<string, double> editedRates = new(StringComparer.Ordinal);
    private readonly ObservableCollection<OptimizationRow> optimizationRows = [];
    private CancellationTokenSource? optimizationCancel;
    private bool syncingLengths;

    private void InitOptimization(ColumnDesignBasis basis, Func<IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<DesignMemberInput>>? forces,
        IReadOnlyList<MaterialProperty>? materials)
    {
        Width = Math.Max(Width, 1020);
        Height = Math.Max(Height, 800);
        memberForces = forces;
        optimizationMaterials = materials ?? [];
        OptCaseList.ItemsSource = cases;
        OptResults.ItemsSource = optimizationRows;
        OptRates.ItemsSource = rates;
        OptShapeBox.ItemsSource = Enum.GetValues<TubeShape>();
        var steel = ColumnGrades.Steel.Select(grade => grade.Name).ToArray();
        var concrete = ColumnGrades.Concrete.Select(grade => grade.Name).ToArray();
        OptSteelFrom.ItemsSource = steel; OptSteelTo.ItemsSource = steel;
        OptConcreteFrom.ItemsSource = concrete; OptConcreteTo.ItemsSource = concrete;

        // what the column type varies
        var cft = type == Cft;
        var profile = type is Src or Mega;
        OptTubeGroup.Visibility = cft ? Visibility.Visible : Visibility.Collapsed;
        OptOutlineGroup.Visibility = cft ? Visibility.Collapsed : Visibility.Visible;
        OptProfileGroup.Visibility = profile ? Visibility.Visible : Visibility.Collapsed;
        OptMegaRow.Visibility = type == Mega ? Visibility.Visible : Visibility.Collapsed;
        foreach (var control in new FrameworkElement[] { OptSteelLabel, OptSteelFrom, OptSteelToLabel, OptSteelTo, OptSteelNote })
            control.Visibility = type == Rc ? Visibility.Collapsed : Visibility.Visible;
        OptBarsGroup.Header = cft ? "Bars inside the tube" : "Longitudinal bars";
        OptRebarBox.Visibility = cft ? Visibility.Visible : Visibility.Collapsed;
        OptDepthBarsRow.Visibility = cft ? Visibility.Collapsed : Visibility.Visible;
        OptCoverLabel.Text = cft ? "cover to tube" : "cover to links";
        OptBarsNote.Text = cft ? "corners included; CHS: 4(n−1) bars" : "per b face, corners included";

        // start around the current section
        var s = basis.Section;
        static double Round50(double value) => Math.Max(50, Math.Round(value / 50) * 50);
        void Range(TextBox min, TextBox max, TextBox step, double low, double high, double by) => (min.Text, max.Text, step.Text) = (Text(low), Text(high), Text(by));
        var defaults = new ColumnOptimizationInput();
        OptShapeBox.SelectedItem = s.Tube;
        OptSquareBox.IsChecked = Math.Abs(s.TubeDepth - s.TubeWidth) < 1;
        Range(OptDepthMin, OptDepthMax, OptDepthStep, Math.Max(300, Round50(s.TubeDepth * 0.6)), Round50(s.TubeDepth * 1.5), 50);
        Range(OptWidthMin, OptWidthMax, OptWidthStep, Math.Max(300, Round50(s.TubeWidth * 0.6)), Round50(s.TubeWidth * 1.5), 50);
        Range(OptThickMin, OptThickMax, OptThickStep, defaults.Thickness.Min, Math.Max(defaults.Thickness.Max, s.TubeThickness), defaults.Thickness.Step);
        OptOutlineSquareBox.IsChecked = Math.Abs(s.Depth - s.Width) < 1;
        var outlineStep = s.Depth >= 1500 ? 100 : 50;
        Range(OptOutlineDepthMin, OptOutlineDepthMax, OptOutlineDepthStep, Math.Max(300, Round50(s.Depth * 0.7)), Round50(s.Depth * 1.3), outlineStep);
        Range(OptOutlineWidthMin, OptOutlineWidthMax, OptOutlineWidthStep, Math.Max(300, Round50(s.Width * 0.7)), Round50(s.Width * 1.3), outlineStep);
        Range(OptProfileDepthMin, OptProfileDepthMax, OptProfileDepthStep, Round50(s.SteelDepth * 0.8), Round50(s.SteelDepth * 1.2), 50);
        Range(OptProfileWidthMin, OptProfileWidthMax, OptProfileWidthStep, Round50(s.SteelWidth * 0.8), Round50(s.SteelWidth * 1.2), 50);
        Range(OptFlangeMin, OptFlangeMax, OptFlangeStep, Math.Max(10, s.FlangeThickness - 20), s.FlangeThickness + 20, 10);
        Range(OptWebMin, OptWebMax, OptWebStep, Math.Max(8, s.WebThickness - 10), s.WebThickness + 10, 10);
        OptFourProfiles.IsChecked = s.ProfileCount != 6;
        OptSixProfiles.IsChecked = s.ProfileCount == 6;
        if (type == Mega) OptProfileEdge.Text = Text(Math.Max(40, Math.Round(Math.Min(s.Depth / 2 - s.ProfileOffsetY - s.SteelDepth / 2, s.Width / 2 - s.ProfileOffsetZ - s.SteelWidth / 2))));
        OptSteelFrom.SelectedItem = type == Mega ? "S460" : defaults.SteelFrom; OptSteelTo.SelectedItem = defaults.SteelTo;
        (OptConcreteFrom.SelectedItem, OptConcreteTo.SelectedItem) = type switch
        {
            Rc => ("C30/37", "C90/105"),
            Mega => ("C70/85", "C120/135"),
            _ => (defaults.ConcreteFrom, defaults.ConcreteTo),
        };
        OptRebarBox.IsChecked = s.TubeReinforced;
        if (!cft)
        {
            OptBarDiameters.Text = type == Mega ? "32, 40" : "20, 25, 32";
            OptCover.Text = Text(s.Cover);
            (OptBarsMin.Text, OptBarsMax.Text) = (Text(Math.Max(2, s.BarsAlongWidth - 2)), Text(s.BarsAlongWidth + 2));
        }

        // the effective lengths are the Design Basis ones, editable from either tab
        OptLengthYBox.Text = LengthYBox.Text;
        OptLengthZBox.Text = LengthZBox.Text;
        void Sync(TextBox from, TextBox to) => from.TextChanged += (_, _) =>
        {
            if (syncingLengths) return;
            syncingLengths = true;
            to.Text = from.Text;
            syncingLengths = false;
        };
        Sync(OptLengthYBox, LengthYBox); Sync(LengthYBox, OptLengthYBox);
        Sync(OptLengthZBox, LengthZBox); Sync(LengthZBox, OptLengthZBox);
        OptShape_Changed(this, new RoutedEventArgs());
        RefreshRates();
        UpdateOptimizationMembers();
        foreach (var member in members) member.PropertyChanged += (_, _) => UpdateOptimizationMembers();
        Closed += (_, _) => optimizationCancel?.Cancel();
    }

    private void UpdateOptimizationMembers()
    {
        var ticked = members.Where(member => member.IsChecked).Select(member => member.Name).ToArray();
        OptMembersText.Text = ticked.Length == 0
            ? "Tick the members on Members & Loads; one section is sized for all of them."
            : $"One section for {ticked.Length} member(s): {string.Join(", ", ticked.Take(6))}{(ticked.Length > 6 ? ", …" : "")}";
    }

    private void OptShape_Changed(object sender, RoutedEventArgs e)
    {
        if (OptShapeBox is null || OptSquareBox is null || OptOutlineSquareBox is null) return;
        var circular = OptShapeBox.SelectedItem is TubeShape.Circular;
        OptSquareBox.IsEnabled = !circular;
        var width = !circular && OptSquareBox.IsChecked != true;
        OptDepthLabel.Text = circular ? "D  diameter" : "H  depth (in-plane)";
        foreach (var control in new FrameworkElement[] { OptWidthLabel, OptWidthMin, OptWidthMax, OptWidthStep }) control.IsEnabled = width;
        var outlineWidth = OptOutlineSquareBox.IsChecked != true;
        foreach (var control in new FrameworkElement[] { OptOutlineWidthLabel, OptOutlineWidthMin, OptOutlineWidthMax, OptOutlineWidthStep }) control.IsEnabled = outlineWidth;
    }

    private void OptGrades_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (OptRates is not null && OptSteelFrom?.SelectedItem is not null && OptSteelTo?.SelectedItem is not null &&
            OptConcreteFrom?.SelectedItem is not null && OptConcreteTo?.SelectedItem is not null) RefreshRates();
    }

    /// <summary>The rate table for the grades in range; a rate the user edited is kept.</summary>
    private void RefreshRates()
    {
        foreach (var row in rates) editedRates[row.Kind + "|" + row.Grade] = row.Rate;
        var input = ReadRanges(requireValid: false);
        rates.Clear();
        double Rate(string key, double fallback) => editedRates.TryGetValue(key, out var value) ? value : fallback;
        if (input.UsesSteel)
            foreach (var grade in input.SteelGrades)
                rates.Add(new RateRow("Steel", grade.Name, Rate("Steel|" + grade.Name, ColumnGrades.SteelRate(grade, optimizationMaterials)), "SGD / t"));
        foreach (var grade in input.ConcreteGrades)
            rates.Add(new RateRow("Concrete", grade.Name, Rate("Concrete|" + grade.Name, ColumnGrades.ConcreteRate(grade, optimizationMaterials)), "SGD / m³"));
        rates.Add(new RateRow("Rebar", "B500", Rate("Rebar|B500", ColumnGrades.RebarRate(optimizationMaterials)), "SGD / t"));
    }

    /// <summary>The optimisation input as the tab holds it; throws with the problem when a value is invalid.</summary>
    private ColumnOptimizationInput ReadRanges(bool requireValid = true)
    {
        static double Number(TextBox box) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
        static SizeRange Range(TextBox min, TextBox max, TextBox step) => new(Number(min), Number(max), Number(step));
        static int Whole(TextBox box, int fallback) => double.IsFinite(Number(box)) ? (int)Number(box) : fallback;
        var circular = OptShapeBox.SelectedItem is TubeShape.Circular;
        var diameters = OptBarDiameters.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN).ToArray();
        var counts = new List<int>();
        if (OptFourProfiles.IsChecked == true) counts.Add(4);
        if (OptSixProfiles.IsChecked == true) counts.Add(6);
        var input = new ColumnOptimizationInput
        {
            Type = type,
            Shape = circular ? TubeShape.Circular : TubeShape.Rectangular,
            Square = circular || OptSquareBox.IsChecked == true,
            Depth = Range(OptDepthMin, OptDepthMax, OptDepthStep),
            Width = Range(OptWidthMin, OptWidthMax, OptWidthStep),
            Thickness = Range(OptThickMin, OptThickMax, OptThickStep),
            SquareOutline = OptOutlineSquareBox.IsChecked == true,
            OutlineDepth = Range(OptOutlineDepthMin, OptOutlineDepthMax, OptOutlineDepthStep),
            OutlineWidth = Range(OptOutlineWidthMin, OptOutlineWidthMax, OptOutlineWidthStep),
            ProfileDepth = Range(OptProfileDepthMin, OptProfileDepthMax, OptProfileDepthStep),
            ProfileWidth = Range(OptProfileWidthMin, OptProfileWidthMax, OptProfileWidthStep),
            FlangeThickness = Range(OptFlangeMin, OptFlangeMax, OptFlangeStep),
            WebThickness = Range(OptWebMin, OptWebMax, OptWebStep),
            ProfileCounts = counts,
            ProfileEdge = Number(OptProfileEdge),
            SteelFrom = OptSteelFrom.SelectedItem as string ?? "S355",
            SteelTo = OptSteelTo.SelectedItem as string ?? "S690",
            ConcreteFrom = OptConcreteFrom.SelectedItem as string ?? "C50/60",
            ConcreteTo = OptConcreteTo.SelectedItem as string ?? "C90/105",
            Rebar = OptRebarBox.IsChecked == true,
            BarDiameters = diameters.Where(value => value > 0).ToArray(),
            BarsPerFaceMin = Whole(OptBarsMin, 3),
            BarsPerFaceMax = Whole(OptBarsMax, 5),
            UniformBarSpacing = OptUniformBars.IsChecked == true,
            BarsDepthMin = Whole(OptDepthBarsMin, 1),
            BarsDepthMax = Whole(OptDepthBarsMax, 3),
            Cover = Number(OptCover),
            SteelRates = rates.Where(row => row.Kind == "Steel").ToDictionary(row => row.Grade, row => row.Rate),
            ConcreteRates = rates.Where(row => row.Kind == "Concrete").ToDictionary(row => row.Grade, row => row.Rate),
            RebarRate = rates.FirstOrDefault(row => row.Kind == "Rebar")?.Rate ?? 690,
        };
        if (!requireValid) return input;
        var ranges = new List<(string Name, SizeRange Range)>();
        if (type == Cft)
        {
            ranges.Add(("H / D", input.Depth));
            ranges.Add(("t", input.Thickness));
            if (!input.Square) ranges.Add(("B", input.Width));
        }
        else
        {
            ranges.Add(("h (outline)", input.OutlineDepth));
            if (!input.SquareOutline) ranges.Add(("b (outline)", input.OutlineWidth));
        }
        if (input.UsesProfile)
            ranges.AddRange([("profile h", input.ProfileDepth), ("flange b", input.ProfileWidth), ("tf", input.FlangeThickness), ("tw", input.WebThickness)]);
        foreach (var (name, range) in ranges)
            if (!(range.Min > 0) || !(range.Max >= range.Min) || !(range.Step > 0))
                throw new ArgumentException($"{name}: enter min > 0, max ≥ min and a step > 0 (mm).");
        if (type == Mega && (counts.Count == 0 || !(input.ProfileEdge > 0))) throw new ArgumentException("Profiles: tick 4 and / or 6, and enter the edge distance (mm).");
        var bars = input.AlwaysReinforced || input.Rebar;
        if (bars && (diameters.Length == 0 || diameters.Any(value => !(value > 0)) || input.BarsPerFaceMin < 2 || input.BarsPerFaceMax < input.BarsPerFaceMin || !(input.Cover >= 0)))
            throw new ArgumentException("Bars: enter bar diameters, 2 ≤ bars per face min ≤ max, and the cover.");
        if (bars && input.AlwaysReinforced && !input.UniformBarSpacing && (input.BarsDepthMin < 0 || input.BarsDepthMax < input.BarsDepthMin))
            throw new ArgumentException("Bars per h face: enter 0 ≤ min ≤ max.");
        return input;
    }

    private async void OptRun_Click(object sender, RoutedEventArgs e)
    {
        if (memberForces is null) { OptStatus.Text = "The optimisation needs the FEM results of the model."; return; }
        if (ReadBasis() is not { } basis) return;
        ColumnOptimizationInput input;
        try { input = ReadRanges(); }
        catch (ArgumentException ex) { OptStatus.Text = ex.Message; return; }
        var forces = memberForces(basis.Members, basis.LoadCases);
        if (forces.Count == 0 || forces.All(member => member.Cases.All(item => item.Stations.Count == 0)))
        {
            OptStatus.Text = "No FEM forces for the ticked members and load cases — run the FEM analysis first.";
            return;
        }
        optimizationRows.Clear();
        optimizationCancel = new CancellationTokenSource();
        OptRunButton.IsEnabled = false;
        OptStopButton.IsEnabled = true;
        OptUseButton.IsEnabled = false;
        OptStatus.Text = "Pricing the candidates…";
        var started = DateTime.Now;
        var progress = new Progress<(int Evaluated, int Total, int Found)>(state =>
            OptStatus.Text = $"Designed {state.Evaluated:N0} of {state.Total:N0} candidates (cheapest first) · {state.Found} passing found");
        try
        {
            var token = optimizationCancel.Token;
            var run = await Task.Run(() => ColumnOptimizer.Run(input, basis, forces, progress, token), token);
            foreach (var result in run.Best) optimizationRows.Add(new OptimizationRow(result.Rank, result.Candidate, result.Check));
            var seconds = (DateTime.Now - started).TotalSeconds;
            var first = run.Best.FirstOrDefault()?.Candidate;
            OptStatus.Text = first is null
                ? $"No section in the ranges passes ({run.Evaluated:N0} of {run.Candidates:N0} designed) — widen the sizes, thicknesses or grades."
                : $"{(run.Cancelled ? "Stopped — " : "")}Cheapest: {first.Size} {(first.SteelGrade == "—" ? "" : first.SteelGrade + " / ")}{first.ConcreteGrade}" +
                  $"{(first.Rebar != "—" ? " + " + first.Rebar : "")} at {first.CostPerM:N0} SGD/m · {run.Evaluated:N0} of {run.Candidates:N0} designed in {seconds:0.0} s for {forces.Count} member(s)";
            if (optimizationRows.Count > 0) OptResults.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { OptStatus.Text = "Stopped."; }
        catch (ArgumentException ex) { OptStatus.Text = ex.Message; }
        finally
        {
            OptRunButton.IsEnabled = true;
            OptStopButton.IsEnabled = false;
            optimizationCancel = null;
        }
    }

    private void OptStop_Click(object sender, RoutedEventArgs e) => optimizationCancel?.Cancel();

    private void OptResults_SelectionChanged(object sender, SelectionChangedEventArgs e) => OptUseButton.IsEnabled = OptResults.SelectedItem is OptimizationRow;

    /// <summary>The selected section becomes the Section tab's; Run Design then gives its full report.</summary>
    private void OptUse_Click(object sender, RoutedEventArgs e)
    {
        if (OptResults.SelectedItem is not OptimizationRow row) return;
        ShowSection(row.Candidate.Section);
        if (ColumnGrades.Steel.FirstOrDefault(grade => grade.Name == row.Candidate.SteelGrade) is { } steel &&
            MaterialCosts.GradeStrength(SteelGradeBox.Text) is { } limit && steel.NominalFy > limit) SteelGradeBox.Text = steel.Name;
        if (MaterialCosts.GradeStrength(ConcreteGradeBox.Text) is { } concreteLimit && row.Candidate.Section.Fck > concreteLimit) ConcreteGradeBox.Text = row.Candidate.ConcreteGrade;
        Tabs.SelectedIndex = 1;
        StatusText.Text = $"Section set to #{row.Rank}: {row.Candidate.Size} {(row.Candidate.SteelGrade == "—" ? "" : row.Candidate.SteelGrade + " / ")}{row.Candidate.ConcreteGrade}" +
                          $"{(row.Candidate.Rebar != "—" ? " + " + row.Candidate.Rebar : "")} — Run Design for the full report.";
    }
}
