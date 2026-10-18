using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;

/// <summary>
/// Design → Steel Beam / Column against independent values: the published section properties of UB 457×191×82 (SCI Blue Book), EN 1993-1-1
/// formulas worked outside the engine (flexural buckling 6.3.1, lateral-torsional buckling 6.3.2.3, Annex B interaction), an effective class 4
/// section (EN 1993-1-5) worked in a separate script, and the hollow sections' closed forms. See docs/analysis/steel-member-design-verification.md.
/// </summary>
internal static class SteelDesignChecks
{
    private static readonly ColumnSectionInput Ub = new()
    {
        SteelKind = SteelShape.I, Fabrication = SteelFabrication.Rolled, SteelDepth = 460, SteelWidth = 191.3, FlangeThickness = 16, WebThickness = 9.9, RootRadius = 10.2, Fy = 355,
    };

    private static readonly ColumnSectionInput Uc = new()
    {
        SteelKind = SteelShape.I, Fabrication = SteelFabrication.Rolled, SteelDepth = 260.3, SteelWidth = 256.3, FlangeThickness = 17.3, WebThickness = 10.3, RootRadius = 12.7, Fy = 355,
    };

    public static void Run()
    {
        // UB 457×191×82 against the Blue Book: A 104.0 cm², Iy 37 100 cm⁴, Iz 1870 cm⁴, Wpl,y 1830 cm³, Wpl,z 304 cm³, Iw 0.922 dm⁶; It is on the safe side
        var ub = new SteelGeometry(Ub);
        Check(Near(ub.A / 1e2, 104.0, 0.01), $"UB 457×191×82 area {ub.A / 1e2:0.0} ≈ 104.0 cm²");
        Check(Near(ub.Iy / 1e4, 37100, 0.01) && Near(ub.Iz / 1e4, 1870, 0.01), $"UB second moments {ub.Iy / 1e4:0} / {ub.Iz / 1e4:0} ≈ 37 100 / 1870 cm⁴");
        Check(Near(ub.Wply / 1e3, 1830, 0.005) && Near(ub.Wplz / 1e3, 304, 0.01), $"UB plastic moduli {ub.Wply / 1e3:0} / {ub.Wplz / 1e3:0} ≈ 1830 / 304 cm³");
        Check(Near(ub.Iw / 1e12, 0.922, 0.01) && ub.It / 1e4 is > 60 and < 69.2, $"UB warping constant {ub.Iw / 1e12:0.000} ≈ 0.922 dm⁶, It {ub.It / 1e4:0.0} cm⁴ ≤ 69.2 (root fillets left out)");

        // classes (S355, ε = 0.814): the UB is class 1 in bending; a 6 mm web of 960 mm is class 4; CHS 508×16 class 1, CHS 610×8 class 4
        Check(ub.Classify(0, 1, 0, 355).Class == 1, "UB 457×191×82 is class 1 in bending");
        var girder = new SteelGeometry(new ColumnSectionInput { SteelKind = SteelShape.I, Fabrication = SteelFabrication.Welded, SteelDepth = 1000, SteelWidth = 300, FlangeThickness = 20, WebThickness = 6, Fy = 355 });
        Check(girder.Classify(0, 1, 0, 355).Class == 4, "welded 1000×300×20×6 girder is class 4 in bending (web c/t = 160 > 124ε = 100.9)");
        Check(new SteelGeometry(Chs(508, 16)).Classify(0, 1, 0, 355).Class == 1 && new SteelGeometry(Chs(610, 8)).Classify(0, 1, 0, 355).Class == 4, "CHS 508×16 class 1, CHS 610×8 class 4 (d/t against 50ε² and 90ε²)");

        // class 4 effective section (EN 1993-1-5): bending ψ = −1, ρ = 0.651; compression ρ = 0.270 — worked separately
        Check(Near(girder.EffectiveModulus(true, 0, 1e9, 355) / 1e6, 6.2822, 0.005), $"class 4 Weff,y = {girder.EffectiveModulus(true, 0, 1e9, 355) / 1e6:0.000} ≈ 6.282 × 10⁶ mm³ (gross {girder.Wely / 1e6:0.000})");
        Check(Near(girder.EffectiveArea(355), 13558, 0.005), $"class 4 Aeff = {girder.EffectiveArea(355):0} ≈ 13 558 mm² (gross {girder.A:0})");

        // hollow sections: closed forms
        var chs = new SteelGeometry(Chs(508, 16));
        Check(Near(chs.A, Math.PI * (508 * 508 - 476 * 476) / 4, 1e-9) && Near(chs.Wply, (Math.Pow(508, 3) - Math.Pow(476, 3)) / 6, 1e-9), "CHS 508×16 area and plastic modulus are the closed forms");
        var rhsInput = new ColumnSectionInput { SteelKind = SteelShape.Rhs, Fabrication = SteelFabrication.ColdFormed, TubeDepth = 200, TubeWidth = 100, TubeThickness = 8, Fy = 355 };
        var rhs = new SteelGeometry(rhsInput);
        double ro = 2.5 * 8, ri = ro - 8;
        var sharp = 200.0 * 100 - 184 * 84;
        Check(Near(rhs.A, sharp - (4 - Math.PI) * (ro * ro - ri * ri), 1e-9), $"RHS 200×100×8 cold-formed area {rhs.A / 1e2:0.0} cm² is the sharp-cornered {sharp / 1e2:0.0} less the corner radii");

        // resistances: Mc,Rd = Wpl fy = 650 kNm, tension Npl,Rd = A fy
        var basis = ColumnDesignCodes.Default(ColumnDesignType.Steel) with { EffectiveLengthY = 6, EffectiveLengthZ = 6, Section = Ub };
        var tension = ColumnDesignEngine.Design(basis, Member("T1", 6, [(500, 0, 0), (500, 0, 0)]));
        Check(Near(tension.Governing!.AxialUtilization, 500e3 / (ub.A * 355), 0.001) && tension.Detail.Contains("Mpl,y,Rd = 650."), $"UB in tension: NEd / (A fy) = {tension.Governing.AxialUtilization:0.000}; Mpl,y,Rd = 650 kNm");

        // beam 6 m, uniform load, MEd = 300 kNm, VEd = 100 kN: 6.3.2.3 with curve c (h/b = 2.40 > 2), C1 = Cb = 1.136 → χLT,mod = 0.479, Mb,Rd = 311.6 kNm
        var parabola = Enumerable.Range(0, 9).Select(index => 4.0 * index / 8 * (1 - index / 8.0) * 300).ToArray();
        var beam = ColumnDesignEngine.Design(basis, new DesignMemberInput("B1", "UB", 6,
            [new("ULS1", parabola.Select(moment => new DesignStation(0, 100, moment)).ToArray())]));
        Check(Near(beam.Governing!.MajorUtilization, 0.9627, 0.03) && beam.Detail.Contains("curve c"), $"UB beam LTB utilisation {beam.Governing.MajorUtilization:0.000} ≈ 0.963 (MEd / Mb,Rd, independent calculation)");
        Check(beam.Governing.ShearUtilization is > 0.09 and < 0.115, $"UB beam shear utilisation {beam.Governing.ShearUtilization:0.000} ≈ VEd / Vpl,Rd = 0.10");
        var braced = ColumnDesignEngine.Design(basis with { Section = Ub with { LateralLength = 1500 } }, new DesignMemberInput("B2", "UB", 6,
            [new("ULS1", parabola.Select(moment => new DesignStation(0, 100, moment)).ToArray())]));
        Check(braced.Governing!.MajorUtilization < beam.Governing.MajorUtilization && Near(braced.Governing.MajorUtilization, 300 / 650.1, 0.15), $"restraints every 1.5 m: {braced.Governing.MajorUtilization:0.000}, towards MEd / Mc,Rd = {300 / 650.1:0.000}");

        // column UC 254×254×89, l0 = 4 m, NEd = 1000 kN, My 120 → 60 kNm (ψ = 0.5): curves b / c, χy = 0.899, χz = 0.662; MEd/Mcr = 0.093 ≤ λLT,0² = 0.16,
        // so χLT = 1 (6.3.2.3(1)); Cmy = 0.8, kyy = 0.859, kzy = 0.945 → 6.61 = 0.277 + 0.237 = 0.514, 6.62 = 0.375 + 0.261 = 0.636
        var column = ColumnDesignEngine.Design(basis with { EffectiveLengthY = 4, EffectiveLengthZ = 4, Section = Uc }, new DesignMemberInput("C1", "UC", 4,
            [new("ULS1", Enumerable.Range(0, 5).Select(index => new DesignStation(-1000, 10, 120 - 15 * index)).ToArray())]));
        Check(column.Detail.Contains("χ = 0.899") && column.Detail.Contains("χ = 0.662"), "UC 254×254×89 flexural buckling: curve b χy = 0.899, curve c χz = 0.662 (6.3.1)");
        Check(Near(column.Governing!.MajorUtilization, 0.5139, 0.005) && Near(column.Governing.MinorUtilization, 0.6364, 0.005),
            $"UC beam-column Annex B: 6.61 = {column.Governing.MajorUtilization:0.000} ≈ 0.514, 6.62 = {column.Governing.MinorUtilization:0.000} ≈ 0.636");
        Check(Near(column.Governing.AxialUtilization, 1000e3 / (0.6624 * new SteelGeometry(Uc).A * 355), 0.01), $"UC axial NEd / Nb,Rd = {column.Governing.AxialUtilization:0.000}");

        // overload fails; class 4 CHS is out of scope; hollow beam-columns run both Table B.1 paths with finite results
        var heavy = ColumnDesignEngine.Design(basis with { EffectiveLengthY = 4, EffectiveLengthZ = 4, Section = Uc }, Member("C2", 4, [(-4000, 10, 300), (-4000, 10, 150)]));
        Check(heavy.Status == "FAIL", $"overloaded UC fails (max {heavy.MaxUtilization:0.00})");
        if (Environment.GetEnvironmentVariable("COT_PRINT_DESIGN") == "1") { Console.WriteLine(column.Detail); Console.WriteLine(heavy.Detail); Console.WriteLine(beam.Detail); }
        var thin = ColumnDesignEngine.Design(basis with { Section = Chs(610, 8) }, Member("C3", 4, [(-500, 5, 20), (-500, 5, 10)]));
        Check(double.IsInfinity(thin.MaxUtilization) && thin.Detailing.Any(check => !check.Pass && check.Name == "Cross-section class"), "a class 4 CHS is refused with its reason, never designed");
        foreach (var section in new[] { rhsInput, Chs(323.9, 10), Uc with { SteelKind = SteelShape.I } })
        {
            var result = ColumnDesignEngine.Design(basis with { EffectiveLengthY = 4, EffectiveLengthZ = 4, Section = section }, new DesignMemberInput("H1", "X", 4,
                [new("ULS1", [new DesignStation(-300, 20, 30, 5, 6), new DesignStation(-300, 20, 15, 5, 3)])]));
            Check(result.Error is null && double.IsFinite(result.MaxUtilization) && result.MaxUtilization > 0, $"{section.SteelKind} beam-column with moments about both axes designs: max {result.MaxUtilization:0.00}");
        }

        // a class 4 I girder designs through the effective section
        var plate = ColumnDesignEngine.Design(basis with { Section = new ColumnSectionInput { SteelKind = SteelShape.I, Fabrication = SteelFabrication.Welded, SteelDepth = 1000, SteelWidth = 300, FlangeThickness = 20, WebThickness = 6, Fy = 355 } },
            new DesignMemberInput("G1", "PG", 6, [new("ULS1", parabola.Select(moment => new DesignStation(0, 100, moment * 4)).ToArray())]));
        Check(plate.Detail.Contains("class 4") && plate.Detail.Contains("Weff,y") && double.IsFinite(plate.MaxUtilization), $"class 4 girder designs on its effective section: max {plate.MaxUtilization:0.00}");
        RunOnModel();
        Console.WriteLine("Steel design checks done.");
    }

    /// <summary>The design on the FEM forces of the default model: the members its steel sections suit, the Override Design Procedure assignment and its run.</summary>
    private static void RunOnModel()
    {
        var (vm, _, _, _) = FemModelFileChecks.DefaultModelApp();
        FemModelFileChecks.Analyze(vm);
        var suggested = vm.DesignMemberOptions(ColumnDesignType.Steel).Where(option => option.Suggested).ToArray();
        var basis = vm.DesignBasis(ColumnDesignType.Steel) with { LoadCases = vm.DesignDefaultCases(), EffectiveLengthY = 10, EffectiveLengthZ = 10 };
        var designs = vm.RunColumnDesign(basis);
        Check(suggested.Length > 0 && designs.Count == basis.Members.Count && designs.All(design => design.Error is null && design.Governing is not null && !double.IsNaN(design.MaxUtilization)),
            $"the default model's {suggested.Length} steel members are designed on their FEM forces ({string.Join(", ", designs.GroupBy(design => design.Status).Select(group => $"{group.Key} × {group.Count()}"))})");
        // its mega columns are SHS 1200×1200×120, beyond the 80 mm of EC3 Table 3.1: said in the detailing, never silently designed
        Check(designs.Any(design => design.Detailing.Any(check => !check.Pass && check.Name == "Thickness in scope")) && designs.All(design => design.Status != "OK" || design.DetailingOk),
            "a section thicker than Table 3.1 allows is flagged in the detailing and the member is not reported OK");
        // the optimization's Member Utilization check takes a steel section to the steel design instead of listing it as not designed
        var utilization = vm.BuildMemberUtilizationCheck();
        Check(suggested.All(option => utilization.Basis(vm.FemSectionProperties.First(property => property.MemberName == option.Member)) is { Type: ColumnDesignType.Steel }),
            "Optimization → Member Utilization designs the model's steel sections with the steel engine");
        var members = suggested.Take(2).Select(option => option.Member).ToArray();
        Check(vm.AssignDesignProcedure(members, ColumnDesignType.Steel).Count == 0, "Override Design Procedure accepts the steel design for members whose section gives its size");
        var overridden = vm.RunOverriddenDesign(members, vm.DesignDefaultCases(), 10);
        Check(overridden.Count == 2 && overridden.All(design => design.Procedure == "Steel Beam / Column Design" && design.Error is null && design.Governing is not null),
            "Override Design Procedure designs them with the steel engine on the FEM forces");
    }

    /// <summary>The Steel Beam / Column dialog on its own (no main window): its fields, the factors it relabels, no Optimization tab, and a design run
    /// from it on the default model's FEM forces. Needs an STA thread; run by <c>--steel-design-ui</c>.</summary>
    public static void RunDialog()
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/Mcotis;component/Presentation/Theme.xaml", UriKind.Relative) });
        var (vm, _, _, _) = FemModelFileChecks.DefaultModelApp();
        FemModelFileChecks.Analyze(vm);
        var ran = 0;
        IReadOnlyList<ColumnMemberDesign> results = [];
        var dialog = new Mcotis.Desktop.Presentation.DesignReferenceWindow(vm.DesignBasis(ColumnDesignType.Steel), vm.DesignMemberOptions(ColumnDesignType.Steel),
            vm.DesignCaseNames(), vm.DesignDefaultCases(), vm.DesignRunId, member => vm.DesignSectionDefaults(ColumnDesignType.Steel, member),
            runDesign: basis => { ran++; return results = vm.RunColumnDesign(basis); });
        dialog.Show();
        dialog.Tabs.SelectedIndex = 1;
        dialog.UpdateLayout();
        var boxes = Visuals<System.Windows.Controls.TextBox>(dialog).Select(box => System.Windows.Automation.AutomationProperties.GetName(box)).ToArray();
        Check(dialog.Title == "Design Reference — Steel Beam / Column Design" && dialog.GammaCLabel.Text == "γM1  stability" && dialog.GammaSLabel.Text == "γM2  net section" &&
              dialog.GammaCBox.Text == "1" && dialog.GammaSBox.Text == "1.25" && dialog.GammaABox.Text == "1" && !dialog.ConcreteGradeBox.IsVisible,
            "the dialog carries γM0 / γM1 / γM2 (1.0 / 1.0 / 1.25) in place of the concrete factors");
        Check(!dialog.Tabs.Items.OfType<System.Windows.Controls.TabItem>().Any(tab => tab.Header as string == "Optimization") &&
              new[] { "h  I section depth", "tf  flange", "H  depth / D  diameter", "t  wall thickness", "LLT  between lateral restraints (0 = member length)", "fy  structural steel (nominal)", "Ea  structural steel" }.All(boxes.Contains),
            "the dialog asks for the I / hollow dimensions, the lateral-torsional length, fy and E, and has no Optimization tab");
        var run = Visuals<System.Windows.Controls.Button>(dialog).First(button => Equals(button.Content, "Run Design"));
        run.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(ran == 1 && results.Count > 0 && results.All(design => design.Error is null) && ReferenceEquals(dialog.Tabs.SelectedItem, dialog.DesignTab),
            $"Run Design designs the ticked members ({results.Count}) and shows them in the Design tab");
        dialog.Close();
        // the optimization results window (Member Utilization filters) used to throw while its XAML loaded
        var optimizationResults = new Mcotis.Desktop.Presentation.OptimizationResultsWindow(vm);
        optimizationResults.Show();
        Check(optimizationResults.MemberStatusBox.SelectedIndex == 0, "the Optimization results window opens (its filters no longer fire before the window is built)");
        optimizationResults.Close();
    }

    private static IEnumerable<T> Visuals<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        var logical = System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>().Concat(
            root is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? Enumerable.Range(0, System.Windows.Media.VisualTreeHelper.GetChildrenCount(root)).Select(index => System.Windows.Media.VisualTreeHelper.GetChild(root, index))
                : []);
        foreach (var child in logical.Distinct())
        {
            if (child is T match) yield return match;
            foreach (var inner in Visuals<T>(child)) yield return inner;
        }
    }

    private static ColumnSectionInput Chs(double diameter, double thickness) =>
        new() { SteelKind = SteelShape.Chs, Fabrication = SteelFabrication.HotFinished, TubeDepth = diameter, TubeThickness = thickness, Fy = 355 };

    /// <summary>A member with the given (axial kN with tension positive, shear kN, moment kNm) at its stations.</summary>
    private static DesignMemberInput Member(string name, double length, (double N, double V, double M)[] stations) =>
        new(name, "TEST", length, [new("ULS1", stations.Select(station => new DesignStation(station.N, station.V, station.M)).ToArray())]);

    private static bool Near(double value, double expected, double tolerance) => Math.Abs(value - expected) <= Math.Abs(expected) * tolerance;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
