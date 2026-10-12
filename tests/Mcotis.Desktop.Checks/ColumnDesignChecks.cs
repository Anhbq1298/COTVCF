using Mcotis.Desktop.Application.Design;
using Mcotis.Desktop.Domain;

/// <summary>
/// Design → column design engine against hand calculations: plastic moduli of steel tubes, EC4 Npl,Rd of a filled tube,
/// EC2 pure tension / compression and bending of an RC section, and pass / fail of a full member design.
/// </summary>
internal static class ColumnDesignChecks
{
    public static void Run()
    {
        // RHS 400 × 400 × 10, steel only: Wpl = B H²/4 − b h²/4 = 2 282 000 mm³ → Mpl = 810.1 kNm at 355 MPa
        Region[] box =
        [
            new(Part.Steel, 1, 0, 195, 400, 10), new(Part.Steel, 1, 0, -195, 400, 10),
            new(Part.Steel, 1, 195, 0, 10, 380, Shear: true), new(Part.Steel, 1, -195, 0, 10, 380, Shear: true),
        ];
        var boxCurve = InteractionCurve.Plastic(new FibreSection(box, []), 0, 355, 355, 0);
        Check(Near(boxCurve.MomentAt(0) / 1e6, 810.11, 0.005), $"RHS plastic moment {boxCurve.MomentAt(0) / 1e6:0.0} ≈ 810.1 kNm");
        Check(Near(boxCurve.MaxN / 1000, 5538, 0.001), "RHS squash load Aa fy = 5538 kN");

        // CHS 500 × 12: Wpl = (D³ − d³)/6 = 2 858 304 mm³ → 1014.7 kNm
        Region[] ring = [new(Part.Steel, 1, 0, 0, 0, 0, 250, true), new(Part.Steel, -1, 0, 0, 0, 0, 238, true)];
        var ringCurve = InteractionCurve.Plastic(new FibreSection(ring, []), 0, 355, 355, 0);
        Check(Near(ringCurve.MomentAt(0) / 1e6, 1014.7, 0.005), $"CHS plastic moment {ringCurve.MomentAt(0) / 1e6:0.0} ≈ 1014.7 kNm");

        // CFT 400 × 400 × 10, C40, S355: Npl,Rd = Aa fyd + Ac fcd = 5538 + 144 400 × 40 / 1.5 = 9389 kN
        var cft = ColumnDesignCodes.Default(ColumnDesignType.ConcreteFilledTube) with
        {
            EffectiveLengthY = 4, EffectiveLengthZ = 4,
            Section = new ColumnSectionInput { Tube = TubeShape.Rectangular, TubeDepth = 400, TubeWidth = 400, TubeThickness = 10, Fck = 40, Fy = 355 },
        };
        var light = ColumnDesignEngine.Design(cft, Member("C1", 4, -1000, 50, 100));
        Check(light.Detail.Contains("Npl,Rd = 9389 kN"), "CFT Npl,Rd = Aa fyd + Ac fcd = 9389 kN");
        Check(light.Status == "OK" && light.MaxUtilization < 1, $"Lightly loaded CFT passes (max {light.MaxUtilization:0.00})");
        var heavy = ColumnDesignEngine.Design(cft, Member("C2", 4, -9000, 200, 600));
        Check(heavy.Status == "FAIL", $"Overloaded CFT fails (max {heavy.MaxUtilization:0.00})");
        var tension = ColumnDesignEngine.Design(cft, Member("C3", 4, 2000, 10, 20));
        Check(tension.Governing is { NEdKN: < 0 } && tension.Status == "OK", "CFT in tension is checked against Aa fyd");

        // RC 300 × 500, 3 + 3 Ø20 (cover 30, link 10 → d' = 50), C30, B500
        var rc = ColumnDesignCodes.Default(ColumnDesignType.ReinforcedConcrete) with
        {
            EffectiveLengthY = 3, EffectiveLengthZ = 3,
            Section = new ColumnSectionInput { Width = 300, Depth = 500, Cover = 30, LinkDiameter = 10, BarDiameter = 20, BarsAlongWidth = 3, BarsAlongDepth = 0, LinkSpacing = 240, Fck = 30, Fyk = 500 },
        };
        var rcResult = ColumnDesignEngine.Design(rc, Member("R1", 3, -800, 30, 80));
        // NRd,t = As fyd = 1885 × 434.8 = 820 kN; NRd,max = Ac η fcd + As (Es εc3 − η fcd) = 2550 + 1885 × 333 = 3178 kN
        Check(rcResult.Detail.Contains("NRd,t = 820 kN"), "RC pure tension As fyd = 820 kN");
        Check(rcResult.Detail.Contains("NRd,max (pure compression) = 3178 kN"), "RC pure compression = 3178 kN");
        var bars = new List<Bar>();
        foreach (var u in new[] { -100.0, 0, 100 }) { bars.Add(new(u, 200, 314.159)); bars.Add(new(u, -200, 314.159)); }
        var rcCurve = InteractionCurve.Reinforced(new FibreSection([new Region(Part.Concrete, 1, 0, 0, 300, 500)], bars), 17, 30, 434.78, 200_000);
        // hand: 4080 x + 942.5 (700 (x − 50)/x − 17) = As fyd = 409.8 kN → x = 65.7 mm, a = 52.6 mm, Cc = 268.1 kN;
        // M = As fyd (d − d') + Cc (h/2 − d' − a/2) = 163.9 + 268.1 × 0.0237 = 170.3 kNm
        Check(Near(rcCurve.MomentAt(0) / 1e6, 170.3, 0.01), $"RC pure bending {rcCurve.MomentAt(0) / 1e6:0.0} kNm ≈ 170.3 kNm (strain compatibility by hand)");
        Check(rcResult.Status is "OK" or "CHECK" && rcResult.MaxUtilization < 1, $"Moderately loaded RC column passes (max {rcResult.MaxUtilization:0.00})");

        // Steel encased: 800 × 800, I 500 × 400 × 40 × 25, 16 Ø32
        var src = ColumnDesignCodes.Default(ColumnDesignType.SteelEncased) with { EffectiveLengthY = 8, EffectiveLengthZ = 8 };
        var srcResult = ColumnDesignEngine.Design(src, Member("S1", 8, -20000, 300, 1500));
        Check(srcResult.Governing is not null && double.IsFinite(srcResult.MaxUtilization) && srcResult.Detail.Contains("curve b"), "Steel encased column designs with curve b (y-y)");
        if (Environment.GetEnvironmentVariable("COT_PRINT_DESIGN") == "1") { Console.WriteLine(srcResult.Detail); Console.WriteLine(rcResult.Detail); }
        var none = ColumnDesignEngine.Design(src, new DesignMemberInput("S2", "SRC", 8, []));
        Check(none.Status == "ERROR", "Member without forces reports an error");
        RunCftOptimization();
    }

    /// <summary>Design → CFT → Optimization: fy by plate thickness, cost per metre, and the cheapest passing section.</summary>
    private static void RunCftOptimization()
    {
        var s355 = CftGrades.Steel.First(grade => grade.Name == "S355");
        var s690 = CftGrades.Steel.First(grade => grade.Name == "S690");
        Check(s355.Fy(16) == 355 && s355.Fy(50) == 335 && s690.Fy(50) == 690 && s690.Fy(60) == 650, "steel fy steps down with plate thickness (EN 10025 bands)");

        // one candidate priced by hand: RHS 400 × 400 × 10 → Aa = 15 600 mm² = 122.46 kg/m; Ac = 0.1444 m³/m
        var single = new CftOptimizationInput
        {
            Depth = new(400, 400, 50), Thickness = new(10, 10, 2), SteelFrom = "S355", SteelTo = "S355", ConcreteFrom = "C40/50", ConcreteTo = "C40/50",
            SteelRates = new Dictionary<string, double> { ["S355"] = 4000 }, ConcreteRates = new Dictionary<string, double> { ["C40/50"] = 150 },
        };
        var priced = CftOptimizer.Candidates(single, new ColumnSectionInput()).Single();
        Check(Near(priced.SteelKgPerM, 122.46, 0.001) && Near(priced.ConcreteM3PerM, 0.1444, 0.001) && Near(priced.CostPerM, 122.46 * 4 + 0.1444 * 150, 0.001),
            $"a candidate is priced per metre: {priced.SteelKgPerM:0.00} kg × 4000 SGD/t + {priced.ConcreteM3PerM:0.0000} m³ × 150 SGD/m³ = {priced.CostPerM:0.0} SGD/m");

        // bars inside the tube add to the squash load
        var plain = ColumnDesignCodes.Default(ColumnDesignType.ConcreteFilledTube) with
        {
            EffectiveLengthY = 4, EffectiveLengthZ = 4,
            Section = new ColumnSectionInput { Tube = TubeShape.Rectangular, TubeDepth = 600, TubeWidth = 600, TubeThickness = 16, Fck = 60, Fy = 355 },
        };
        var reinforced = plain with { Section = plain.Section with { TubeReinforced = true, BarsAlongWidth = 4, BarsAlongDepth = 2, BarDiameter = 32, Cover = 40 } };
        Check(ColumnDesignEngine.Interaction(reinforced).NplRdKN > ColumnDesignEngine.Interaction(plain).NplRdKN, "bars inside the tube add to the CFT's resistance");

        // the search returns the cheapest passing section: every cheaper candidate fails, and the results are in cost order
        var members = new[] { Member("C1", 6, -12_000, 300, 900), Member("C2", 6, -9_000, 500, 1_400) };
        var input = new CftOptimizationInput
        {
            Depth = new(400, 900, 100), Thickness = new(10, 30, 5), SteelFrom = "S355", SteelTo = "S460", ConcreteFrom = "C50/60", ConcreteTo = "C70/85",
            SteelRates = CftGrades.Steel.ToDictionary(grade => grade.Name, grade => CftGrades.SteelRate(grade, [])),
            ConcreteRates = CftGrades.Concrete.ToDictionary(grade => grade.Name, grade => CftGrades.ConcreteRate(grade, [])), Keep = 5,
        };
        var basis = plain with { EffectiveLengthY = 6, EffectiveLengthZ = 6 };
        var run = CftOptimizer.Run(input, basis, members);
        var all = CftOptimizer.Candidates(input, basis.Section).OrderBy(candidate => candidate.CostPerM).ToArray();
        var scope = basis with { MaxConcreteGrade = "C70/85", MaxSteelGrade = "S460" };
        var cheaper = all.TakeWhile(candidate => candidate.CostPerM < run.Best[0].Candidate.CostPerM).ToArray();
        Check(run.Best.Count == 5 && run.Best.All(result => result.Check.Passes) &&
              run.Best.Zip(run.Best.Skip(1)).All(pair => pair.First.Candidate.CostPerM <= pair.Second.Candidate.CostPerM) &&
              cheaper.All(candidate => !ColumnDesignEngine.Check(scope with { Section = candidate.Section }, members).Passes),
            $"the optimisation finds the cheapest passing CFT: {run.Best[0].Candidate.Size} {run.Best[0].Candidate.SteelGrade}/{run.Best[0].Candidate.ConcreteGrade} " +
            $"at {run.Best[0].Candidate.CostPerM:N0} SGD/m — all {cheaper.Length} cheaper candidates fail ({run.Evaluated} of {run.Candidates} designed)");
        var full = ColumnDesignEngine.Design(scope with { Section = run.Best[0].Candidate.Section }, members[1]);
        Check(full.Status == "OK", $"Run Design agrees: the chosen section passes with utilisation {full.MaxUtilization:0.00}");
    }

    private static DesignMemberInput Member(string name, double length, double axialKN, double shearKN, double momentKNm) =>
        new(name, "TEST", length, [new DesignCaseForces("ULS1", [new(axialKN, shearKN, momentKNm), new(axialKN, shearKN, -momentKNm / 2)])]);

    private static bool Near(double value, double expected, double tolerance) => Math.Abs(value - expected) <= Math.Abs(expected) * tolerance;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
