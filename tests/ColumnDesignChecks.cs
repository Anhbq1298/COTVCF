using COT_VCF.Application.Design;
using COT_VCF.Domain;

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
        Console.WriteLine(srcResult.Detail); Console.WriteLine(rcResult.Detail);
        var none = ColumnDesignEngine.Design(src, new DesignMemberInput("S2", "SRC", 8, []));
        Check(none.Status == "ERROR", "Member without forces reports an error");
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
