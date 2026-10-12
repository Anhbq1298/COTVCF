using COT_VCF.Application.Design;
using COT_VCF.Domain;

/// <summary>
/// Design → Mega Composite Column: the C# port of the CTBUH / ArcelorMittal megacolumn engine against the report's §4.2.5
/// worked section and the vitest suite of <c>mega-column/engine.test.ts</c>, the mega section's parts, and full member designs.
/// </summary>
internal static class MegaColumnChecks
{
    public static void Run()
    {
        // ---- §4.2.5 / Table 12: 4 × HD 400×1299 (Aa = 165 000 mm², d = 600, dsy = 950, Ix back-solved 7.5375 × 10⁹) ----
        var x = MegaColumnInput.ReportDefault;
        var r = MegaColumnEngine.Calculate(x) ?? throw new Exception("FAIL: the report section gives a result");
        Check(Math.Abs(r.BStar - 275) < 1e-9 && Near(r.IStar, 4.95e9, 0.02) && r.As == 660_000 && r.Zsx == 627_000_000 && Near(r.Isx, 6.258e11, 0.02),
            "CTBUH Table 12: b* = 275 mm, I* = 4.95 × 10⁹ mm⁴, As = 660 000 mm², Zsx = 627 × 10⁶ mm³, Isx = 6.258 × 10¹¹ mm⁴");
        var equivalent = 4 * x.Aa * x.Dsy * x.Dsy + 4 * r.IStar;
        Check(Near(equivalent, 6.1545e11, 0.02) && Math.Abs(r.Isx - equivalent) / r.Isx < 0.02, "CTBUH §4.2.5: I*sx = 6.1545 × 10¹¹ mm⁴, within 2 % of Isx");
        Check(Near(Math.Min(0.7, 0.25 + 3 * ((21_296 + 1_608.5) / 202_500)), 0.589, 0.02), "CTBUH §4.2.6: AISC C1 = 0.589 for the 1:6 specimens");

        var (required, printed, continuous) = MegaColumnEngine.AuditZsxnContinuity();
        Check(Math.Abs(continuous - required) < 1e-6 && Math.Abs(printed - required - 165_000 * 600) < 1e-3,
            "Table 15 case 2: the continuity form closes onto Zsx at hnx = dsy + d/2; the literal print overshoots by Aa·d");

        // ---- engine.test.ts: calculateMegaColumn2 ----
        var p = r.Points;
        Check(p[0].N > p[1].N && p[1].N > p[2].N && p[2].N > p[3].N && p[3].N == 0 && p.Select(point => point.Label).SequenceEqual(["A", "C", "D", "B"]),
            "the interaction anchors run A > C > D > B in axial force, B at N = 0");
        Check(r.MmaxRd > r.MplRd, "Mmax,Rd at D exceeds Mpl,Rd at B and C");
        Check(Near(r.NpmRd, 0.85 * r.Ac * r.Fcd / 1000, 1e-9), "Npm,Rd is the concrete squash load 0.85 Ac fcd");
        var fc = 0.85 * r.Fcd;
        var a = x.Dsy - x.D / 2;
        var steel = r.Hn.CaseId switch { 2 => 4 * (r.Hn.Hnx - a) * r.BStar * (r.Fyd - fc), 3 => 4 * x.Aa * (r.Fyd - fc), _ => 0 };
        var zone = (2 * r.Hn.Hnx * ((x.H1 - 2 * r.Bs2) * fc + 2 * r.Bs2 * r.Fsd) + steel) / 1000;
        Check(Math.Abs(zone - r.NpmRd) < 1e-3, $"axial equilibrium over the 2 hn zone at the plastic neutral axis ({r.Hn.CaseLabel})");
        Check(MegaColumnEngine.Calculate(x with { H1 = 100, H2 = 100 }) is null && MegaColumnEngine.Calculate(x with { H2 = 0 }) is null,
            "a section whose plates exceed the gross area, or with no depth, gives no result");
        var slender = MegaColumnEngine.Calculate(x with { L = 120_000 })!;
        Check(slender.LambdaBar > 2 && !slender.Checks.Single(check => check.Id == "slenderness").Pass, "λ̄ > 2 is flagged as outside the simplified method");
        Check(!MegaColumnEngine.Calculate(x with { Ny = 7 })!.Checks.Single(check => check.Id == "webrebar").Pass, "fewer than 11 web bars fail the n ≥ 5 plate simplification");
        Check(r.Hn.CaseId != 2 ? r.Hn.MplRdAsPrinted is null : r.Hn.MplRdAsPrinted < r.Hn.MplRd, "the literal Table 15 case 2 alternative is reported only inside case 2");
        Check(r.Checks.Where(check => check.IsCapacity).Select(check => check.Id).SequenceEqual(["buckling", "interaction", "squash"]) &&
              r.Checks.Where(check => !check.IsCapacity).Select(check => check.Id).SequenceEqual(["delta", "slenderness", "webrebar"]) &&
              r.Governing.IsCapacity && Math.Abs(r.Governing.Utilisation - r.Checks.Where(check => check.IsCapacity).Max(check => check.Utilisation)) < 1e-12,
            "strength checks govern the utilisation; applicability checks never do");
        var six = MegaColumnEngine.Calculate(x with { Arrangement = ProfileArrangement.Six })!;
        Check(six.As == 6 * x.Aa && Math.Abs(six.Zsx - (4 * x.Aa * x.Dsy + 2 * x.Zx)) < 1e-6 && six.NplRd > r.NplRd, "six profiles: As = 6 Aa, Zsx = 4 Aa dsy + 2 Zx (Table 17)");
        var hsc = MegaColumnEngine.Calculate(x with { Eta = 0.75 })!;
        Check(hsc.NpmRd < r.NpmRd && Near(hsc.NpmRd, 0.75 * r.NpmRd, 1e-9), "η scales the concrete block (high-strength concrete, BC4)");

        // ---- the mega section: profiles, bars and areas ----
        var mega = ColumnSectionInput.MegaDefault;
        var profile = CompositeSection.Profile(mega);
        var parts = CompositeSection.Parts(FrameShape.MegaComposite, mega);
        Check(Math.Abs(profile.Area - (2 * 430 * 60 + 380 * 37)) < 1e-9 && Math.Abs(parts.SteelArea - 4 * profile.Area * 1e-6) < 1e-12 &&
              parts.Bars.Count == 2 * 17 + 2 * 15 && Math.Abs(parts.ConcreteArea + parts.SteelArea + parts.RebarArea - 2.2 * 2.2) < 1e-9,
            "a 2200 × 2200 mega column holds 4 I 500 × 430 × 60 / 37 profiles and 64 bars; its area splits into concrete, steel and bars");
        var layered = CompositeSection.Parts(FrameShape.MegaComposite, mega with { BarLayers = 2, ProfileCount = 6 });
        Check(layered.Bars.Count == 4 * 17 + 2 * 15 && Math.Abs(layered.SteelArea - 6 * profile.Area * 1e-6) < 1e-12, "two b-face bar layers and six profiles are counted");

        // ---- full member designs ----
        var basis = ColumnDesignCodes.Default(ColumnDesignType.MegaComposite) with { EffectiveLengthY = 8, EffectiveLengthZ = 8 };
        Check(basis.Section == ColumnSectionInput.MegaDefault && ColumnDesignCodes.Title(ColumnDesignType.MegaComposite) == "Mega Composite Column Design" &&
              ColumnDesignCodes.References(ColumnDesignType.MegaComposite)[0].Code.StartsWith("CTBUH", StringComparison.Ordinal),
            "Mega Composite Column Design starts from the default mega section with the CTBUH method first among the references");
        var light = ColumnDesignEngine.Design(basis, Member("M1", 8, -100_000, 2_000, 20_000));
        Check(light.Governing is { } g && g.MaxUtilization < 1 && light.Detail.Contains("CTBUH SIMPLIFIED METHOD", StringComparison.Ordinal) &&
              light.Detail.Contains("exact plastic distribution", StringComparison.Ordinal) && light.Detail.Contains("curve c", StringComparison.Ordinal),
            $"a 2200 × 2200 C100 mega column carries 100 MN with 20 MNm (max {light.MaxUtilization:0.00}); the report shows the method and the fibre cross-check");
        var heavy = ColumnDesignEngine.Design(basis, Member("M2", 8, -400_000, 2_000, 20_000));
        Check(heavy.MaxUtilization > 1 && heavy.Status == "FAIL", $"400 MN overloads it (max {heavy.MaxUtilization:0.00})");
        var tension = ColumnDesignEngine.Design(basis, Member("M3", 8, 20_000, 500, 5_000));
        Check(tension.Governing is { NEdKN: < 0 } t && double.IsFinite(t.MaxUtilization) && tension.Detail.Contains("MEMBER IN TENSION", StringComparison.Ordinal),
            "a mega column in tension is checked against As fyd + Asr fsd and the reduced plastic moment");
        if (Environment.GetEnvironmentVariable("COT_PRINT_DESIGN") == "1") Console.WriteLine(light.Detail);
        Check(light.Detailing.Any(check => check.Name == "Web rebar simplification" && check.Pass) && light.Detailing.Any(check => check.Name == "Concrete cover to the profiles" && check.Pass),
            "the default mega column meets the web-bar (n ≥ 5) and profile-cover rules");
    }

    private static DesignMemberInput Member(string name, double length, double axialKN, double shearKN, double momentKNm) =>
        new(name, "MEGA", length, [new DesignCaseForces("ULS1", [new(axialKN, shearKN, momentKNm), new(axialKN, shearKN, -momentKNm / 2)])]);

    private static bool Near(double value, double expected, double tolerance) => Math.Abs(value - expected) <= Math.Abs(expected) * tolerance;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
