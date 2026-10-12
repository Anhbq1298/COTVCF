using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Design → CFT → Optimization: the ranges that drive the cost of a concrete filled tube (size, wall thickness, steel and
/// concrete grade stepped grade by grade, bars inside the tube), the unit cost of each material, and Run — the cheapest
/// sections that pass every ticked member and load case (<see cref="CftOptimizer"/>), cost per metre of column.
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
    public sealed record OptimizationRow(int Rank, CftCandidate Candidate, DesignCheckSummary Check)
    {
        public string Steel => $"{Candidate.SteelGrade} ({Candidate.Section.Fy:0})";
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
        if (type != Cft)
        {
            OptimizationTab.Visibility = Visibility.Collapsed;
            return;
        }
        Width = Math.Max(Width, 1020);
        Height = Math.Max(Height, 780);
        memberForces = forces;
        optimizationMaterials = materials ?? [];
        OptCaseList.ItemsSource = cases;
        OptResults.ItemsSource = optimizationRows;
        OptRates.ItemsSource = rates;
        OptShapeBox.ItemsSource = Enum.GetValues<TubeShape>();
        var steel = CftGrades.Steel.Select(grade => grade.Name).ToArray();
        var concrete = CftGrades.Concrete.Select(grade => grade.Name).ToArray();
        OptSteelFrom.ItemsSource = steel; OptSteelTo.ItemsSource = steel;
        OptConcreteFrom.ItemsSource = concrete; OptConcreteTo.ItemsSource = concrete;

        // start around the current section
        var s = basis.Section;
        var input = new CftOptimizationInput();
        OptShapeBox.SelectedItem = s.Tube;
        OptSquareBox.IsChecked = Math.Abs(s.TubeDepth - s.TubeWidth) < 1;
        var low = Math.Max(300, Math.Round(s.TubeDepth * 0.6 / 50) * 50);
        var high = Math.Round(s.TubeDepth * 1.5 / 50) * 50;
        (OptDepthMin.Text, OptDepthMax.Text, OptDepthStep.Text) = (Text(low), Text(high), "50");
        var lowB = Math.Max(300, Math.Round(s.TubeWidth * 0.6 / 50) * 50);
        (OptWidthMin.Text, OptWidthMax.Text, OptWidthStep.Text) = (Text(lowB), Text(Math.Round(s.TubeWidth * 1.5 / 50) * 50), "50");
        (OptThickMin.Text, OptThickMax.Text, OptThickStep.Text) = (Text(input.Thickness.Min), Text(Math.Max(input.Thickness.Max, s.TubeThickness)), Text(input.Thickness.Step));
        OptSteelFrom.SelectedItem = input.SteelFrom; OptSteelTo.SelectedItem = input.SteelTo;
        OptConcreteFrom.SelectedItem = input.ConcreteFrom; OptConcreteTo.SelectedItem = input.ConcreteTo;
        OptRebarBox.IsChecked = s.TubeReinforced;

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
        if (OptShapeBox is null || OptSquareBox is null) return;
        var circular = OptShapeBox.SelectedItem is TubeShape.Circular;
        OptSquareBox.IsEnabled = !circular;
        var width = !circular && OptSquareBox.IsChecked != true;
        OptDepthLabel.Text = circular ? "D  diameter" : "H  depth (in-plane)";
        foreach (var control in new FrameworkElement[] { OptWidthLabel, OptWidthMin, OptWidthMax, OptWidthStep }) control.IsEnabled = width;
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
        foreach (var grade in input.SteelGrades)
            rates.Add(new RateRow("Steel", grade.Name, Rate("Steel|" + grade.Name, CftGrades.SteelRate(grade, optimizationMaterials)), "SGD / t"));
        foreach (var grade in input.ConcreteGrades)
            rates.Add(new RateRow("Concrete", grade.Name, Rate("Concrete|" + grade.Name, CftGrades.ConcreteRate(grade, optimizationMaterials)), "SGD / m³"));
        rates.Add(new RateRow("Rebar", "B500", Rate("Rebar|B500", CftGrades.RebarRate(optimizationMaterials)), "SGD / t"));
    }

    /// <summary>The optimisation input as the tab holds it; null (with the problem shown) when a value is invalid.</summary>
    private CftOptimizationInput ReadRanges(bool requireValid = true)
    {
        static double Number(TextBox box) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
        var circular = OptShapeBox.SelectedItem is TubeShape.Circular;
        var diameters = OptBarDiameters.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN).ToArray();
        var input = new CftOptimizationInput
        {
            Shape = circular ? TubeShape.Circular : TubeShape.Rectangular,
            Square = circular || OptSquareBox.IsChecked == true,
            Depth = new(Number(OptDepthMin), Number(OptDepthMax), Number(OptDepthStep)),
            Width = new(Number(OptWidthMin), Number(OptWidthMax), Number(OptWidthStep)),
            Thickness = new(Number(OptThickMin), Number(OptThickMax), Number(OptThickStep)),
            SteelFrom = OptSteelFrom.SelectedItem as string ?? "S355",
            SteelTo = OptSteelTo.SelectedItem as string ?? "S690",
            ConcreteFrom = OptConcreteFrom.SelectedItem as string ?? "C50/60",
            ConcreteTo = OptConcreteTo.SelectedItem as string ?? "C90/105",
            Rebar = OptRebarBox.IsChecked == true,
            BarDiameters = diameters.Where(value => value > 0).ToArray(),
            BarsPerFaceMin = (int)(double.IsFinite(Number(OptBarsMin)) ? Number(OptBarsMin) : 3),
            BarsPerFaceMax = (int)(double.IsFinite(Number(OptBarsMax)) ? Number(OptBarsMax) : 5),
            Cover = Number(OptCover),
            SteelRates = rates.Where(row => row.Kind == "Steel").ToDictionary(row => row.Grade, row => row.Rate),
            ConcreteRates = rates.Where(row => row.Kind == "Concrete").ToDictionary(row => row.Grade, row => row.Rate),
            RebarRate = rates.FirstOrDefault(row => row.Kind == "Rebar")?.Rate ?? 690,
        };
        if (!requireValid) return input;
        var ranges = new List<(string Name, SizeRange Range)> { ("H / D", input.Depth), ("t", input.Thickness) };
        if (!input.Square) ranges.Add(("B", input.Width));
        foreach (var (name, range) in ranges)
            if (!(range.Min > 0) || !(range.Max >= range.Min) || !(range.Step > 0))
                throw new ArgumentException($"{name}: enter min > 0, max ≥ min and a step > 0 (mm).");
        if (input.Rebar && (diameters.Length == 0 || diameters.Any(value => !(value > 0)) || input.BarsPerFaceMin < 2 || input.BarsPerFaceMax < input.BarsPerFaceMin || !(input.Cover >= 0)))
            throw new ArgumentException("Bars: enter bar diameters, 2 ≤ bars per face min ≤ max, and the cover.");
        return input;
    }

    private async void OptRun_Click(object sender, RoutedEventArgs e)
    {
        if (memberForces is null) { OptStatus.Text = "The optimisation needs the FEM results of the model."; return; }
        if (ReadBasis() is not { } basis) return;
        CftOptimizationInput input;
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
            var run = await Task.Run(() => CftOptimizer.Run(input, basis, forces, progress, token), token);
            foreach (var result in run.Best) optimizationRows.Add(new OptimizationRow(result.Rank, result.Candidate, result.Check));
            var seconds = (DateTime.Now - started).TotalSeconds;
            OptStatus.Text = run.Best.Count == 0
                ? $"No section in the ranges passes ({run.Evaluated:N0} of {run.Candidates:N0} designed) — widen the sizes, thicknesses or grades."
                : $"{(run.Cancelled ? "Stopped — " : "")}Cheapest: {run.Best[0].Candidate.Size} {run.Best[0].Candidate.SteelGrade} / {run.Best[0].Candidate.ConcreteGrade}" +
                  $" at {run.Best[0].Candidate.CostPerM:N0} SGD/m · {run.Evaluated:N0} of {run.Candidates:N0} designed in {seconds:0.0} s for {forces.Count} member(s)";
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
        var steelMax = CftGrades.Steel.FirstOrDefault(grade => grade.Name == row.Candidate.SteelGrade);
        if (MaterialCosts.GradeStrength(SteelGradeBox.Text) is { } limit && steelMax is not null && steelMax.NominalFy > limit) SteelGradeBox.Text = steelMax.Name;
        if (MaterialCosts.GradeStrength(ConcreteGradeBox.Text) is { } concreteLimit && row.Candidate.Section.Fck > concreteLimit) ConcreteGradeBox.Text = row.Candidate.ConcreteGrade;
        Tabs.SelectedIndex = 1;
        StatusText.Text = $"Section set to #{row.Rank}: {row.Candidate.Size} {row.Candidate.SteelGrade} / {row.Candidate.ConcreteGrade}" +
                          $"{(row.Candidate.Rebar != "—" ? " + " + row.Candidate.Rebar : "")} — Run Design for the full report.";
    }
}
