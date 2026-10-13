namespace Mcotis.Desktop.Domain;

/// <summary>The column designs offered under the Design menu.</summary>
public enum ColumnDesignType { ReinforcedConcrete, ConcreteFilledTube, SteelEncased, MegaComposite }

/// <summary>A standard or guide the design is based on.</summary>
public sealed record DesignReference(string Code, string Title);

/// <summary>
/// The design basis confirmed in the Design Reference dialog before a column design runs: the governing code, the
/// method, the partial factors and the high-strength material limits (Singapore practice: SS EN Eurocodes with the
/// Singapore National Annexes, extended by the BCA guide BC4 for high-strength composite columns).
/// </summary>
public sealed record ColumnDesignBasis
{
    public required ColumnDesignType Type { get; init; }
    public required string DesignCode { get; init; }
    public required string Method { get; init; }
    public double GammaC { get; init; } = 1.5;          // concrete, γC
    public double GammaS { get; init; } = 1.15;         // reinforcement, γS
    public double GammaA { get; init; } = 1.0;          // structural steel, γM0 (γa)
    public string MaxConcreteGrade { get; init; } = "C90/105";
    public string MaxSteelGrade { get; init; } = "S550";
    /// <summary>Effective (buckling) length about the major axis, l0,y [m]; null until keyed in.</summary>
    public double? EffectiveLengthY { get; init; }
    /// <summary>Effective (buckling) length about the minor axis, l0,z [m]; null until keyed in.</summary>
    public double? EffectiveLengthZ { get; init; }
    /// <summary>The cross-section and materials the design checks.</summary>
    public ColumnSectionInput Section { get; init; } = new();
    /// <summary>Frame members to design; empty until chosen.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];
    /// <summary>Load cases / combinations whose forces are designed for; empty = all.</summary>
    public IReadOnlyList<string> LoadCases { get; init; } = [];
}

public enum TubeShape { Rectangular, Circular }

/// <summary>
/// Column cross-section and materials, in mm and MPa. Depth runs in the plane of the 2D model, so the FEM in-plane
/// moment bends the section about its major (y-y) axis; the minor (z-z) axis carries imperfection moments only.
/// RC uses the concrete outline and bars; CFT the steel tube; steel encased the concrete outline, bars and the I section;
/// a mega composite column the concrete outline, bars and 4 or 6 copies of that I section at (±dsx, ±dsy) [and (±dsx, 0)].
/// </summary>
public sealed record ColumnSectionInput
{
    // concrete outline and reinforcement (RC, steel encased)
    public double Width { get; init; } = 800;            // b, along the minor axis
    public double Depth { get; init; } = 800;            // h, in-plane
    public double Cover { get; init; } = 40;             // nominal cover to the links
    public double BarDiameter { get; init; } = 32;
    public int BarsAlongWidth { get; init; } = 5;        // bars on each b face, corners included
    public int BarsAlongDepth { get; init; } = 3;        // bars on each h face, corners excluded
    public double LinkDiameter { get; init; } = 12;
    public double LinkSpacing { get; init; } = 300;
    // steel tube (CFT)
    public TubeShape Tube { get; init; } = TubeShape.Rectangular;
    public double TubeDepth { get; init; } = 800;        // H, or outside diameter D
    public double TubeWidth { get; init; } = 800;        // B (rectangular)
    public double TubeThickness { get; init; } = 20;
    /// <summary>CFT with bars inside the tube: rectangular — <see cref="BarsAlongWidth"/> on each B face (corners included)
    /// and <see cref="BarsAlongDepth"/> on each H face; circular — <see cref="BarsAlongWidth"/> bars on a circle. Cover is
    /// measured from the inside face of the tube; no links.</summary>
    public bool TubeReinforced { get; init; }
    // encased I section (steel encased), strong axis in-plane
    public double SteelDepth { get; init; } = 500;
    public double SteelWidth { get; init; } = 400;
    public double FlangeThickness { get; init; } = 40;
    public double WebThickness { get; init; } = 25;
    // mega composite column: the I section above at (±dsx, ±dsy); 6 profiles add two at (±dsx, 0), on the bending axis
    public int ProfileCount { get; init; } = 4;
    public double ProfileOffsetY { get; init; } = 700;    // dsy, section centroid to an outer profile centroid, in-plane
    public double ProfileOffsetZ { get; init; } = 700;    // dsx, the same across the section (minor axis)
    public string ProfileName { get; init; } = "";       // designation for the report, e.g. "HD 400x551"
    public int BarLayers { get; init; } = 1;              // bar layers on each b face (flange layers, CTBUH §4.2.1)
    // materials
    public double Fck { get; init; } = 50;               // concrete cylinder strength
    public double Fy { get; init; } = 355;               // structural steel yield
    public double Fyk { get; init; } = 500;              // reinforcement yield
    public double Ea { get; init; } = 210_000;
    public double Es { get; init; } = 200_000;
    public double AlphaCc { get; init; } = 0.85;         // EC2 αcc (RC)
    public double CreepRatio { get; init; } = 1.0;       // φef (EC2 5.8.4) or φt (EC4 6.7.3.3(4))
    public double PermanentLoadRatio { get; init; } = 0.6; // NG,Ed / NEd (EC4 6.7.3.3(4))

    /// <summary>A mega composite column to start from: 2200 × 2200, C100, 4 × I 500 × 430 × 60 / 37 S460 and Ø40 bars.</summary>
    public static ColumnSectionInput MegaDefault { get; } = new()
    {
        Width = 2200, Depth = 2200, Cover = 50, BarDiameter = 40, BarsAlongWidth = 17, BarsAlongDepth = 15, LinkDiameter = 12, LinkSpacing = 200,
        SteelDepth = 500, SteelWidth = 430, FlangeThickness = 60, WebThickness = 37, ProfileCount = 4, ProfileOffsetY = 700, ProfileOffsetZ = 700,
        Fck = 100, Fy = 460, CreepRatio = 1.5, PermanentLoadRatio = 0.75,
    };
}

/// <summary>Design codes, methods and references for each column design.</summary>
public static class ColumnDesignCodes
{
    public const string Ec2 = "SS EN 1992-1-1 (EC2) + Singapore NA";
    public const string Ec4Bc4 = "SS EN 1994-1-1 (EC4) + Singapore NA, extended by BCA BC4:2025";

    public static string Title(ColumnDesignType type) => type switch
    {
        ColumnDesignType.ReinforcedConcrete => "RC Column Design",
        ColumnDesignType.ConcreteFilledTube => "CFT (Concrete Filled Tube) Design",
        ColumnDesignType.MegaComposite => "Mega Composite Column Design",
        _ => "Steel Encased Composite Column Design"
    };

    public static IReadOnlyList<string> DesignCodes(ColumnDesignType type) =>
        type == ColumnDesignType.ReinforcedConcrete ? [Ec2] : [Ec4Bc4];

    public static IReadOnlyList<string> Methods(ColumnDesignType type) => type switch
    {
        ColumnDesignType.ReinforcedConcrete => ["Nominal curvature (EN 1992-1-1 §5.8.8)", "Nominal stiffness (EN 1992-1-1 §5.8.7)"],
        ColumnDesignType.MegaComposite => ["Simplified method — adapted plastic distribution (CTBUH 2016 §4, EN 1994-1-1 §6.7.3)"],
        _ => ["Simplified method (EN 1994-1-1 §6.7.3, BC4)", "General method (EN 1994-1-1 §6.7.2)"],
    };

    public static IReadOnlyList<DesignReference> References(ColumnDesignType type)
    {
        DesignReference ec0 = new("SS EN 1990 + NA", "Eurocode — Basis of structural design");
        DesignReference ec1 = new("SS EN 1991-1-1 + NA", "Eurocode 1 — Actions on structures: densities, self-weight, imposed loads");
        DesignReference ec2 = new("SS EN 1992-1-1 + NA", "Eurocode 2 — Design of concrete structures: general rules and rules for buildings");
        DesignReference rebar = new("SS 560", "Steel for the reinforcement of concrete");
        DesignReference concrete = new("SS EN 206", "Concrete — Specification, performance, production and conformity");
        DesignReference ec3 = new("SS EN 1993-1-1 + NA", "Eurocode 3 — Design of steel structures: general rules and rules for buildings");
        DesignReference ec4 = new("SS EN 1994-1-1 + NA", "Eurocode 4 — Design of composite steel and concrete structures: general rules and rules for buildings");
        DesignReference bc4 = new("BCA BC4:2025", "Design Guide for Steel-Concrete Composite Columns with High Strength Materials");
        DesignReference bc1 = new("BCA BC1", "Design Guide on Use of Alternative Structural Steel (material qualification)");
        DesignReference ctbuh = new("CTBUH / ArcelorMittal 2016", "Composite Megacolumns: Testing Multiple, Concrete-Encased, Hot-Rolled Steel Sections — §4 Simplified Design Methods (Trabucco, Gérardy, Xiao, Davies)");
        return type switch
        {
            ColumnDesignType.ReinforcedConcrete => [ec2, ec0, ec1, concrete, rebar],
            ColumnDesignType.ConcreteFilledTube => [ec4, bc4, ec3, ec2, ec0, bc1, concrete],
            ColumnDesignType.MegaComposite => [ctbuh, ec4, bc4, ec3, ec2, ec0, bc1, rebar, concrete],
            _ => [ec4, bc4, ec3, ec2, ec0, bc1, rebar, concrete]
        };
    }

    /// <summary>One line on the material range the design code covers.</summary>
    public static string MaterialScope(ColumnDesignType type) => type switch
    {
        ColumnDesignType.ReinforcedConcrete =>
            "EC2 covers concrete up to C90/105; high-strength grades above C50/60 use the modified εcu2, n and η of Table 3.1.",
        ColumnDesignType.ConcreteFilledTube =>
            "EC4 covers C20/25–C50/60 and S235–S460; BC4 extends the method to high-strength concrete and steel (check the limits against the guide).",
        ColumnDesignType.MegaComposite =>
            "Several encased profiles: the CTBUH method replaces bar lines by plates and profiles by b* × d* rectangles in the EC4 plastic distribution; above C50/60 the concrete block takes η of EC2 Table 3.1, as BC4 does.",
        _ =>
            "EC4 covers C20/25–C50/60 and S235–S460; BC4 extends the method to high-strength concrete and steel, with encased-section reinforcement and confinement rules."
    };

    public static ColumnDesignBasis Default(ColumnDesignType type) => new()
    {
        Type = type,
        DesignCode = DesignCodes(type)[0],
        Method = Methods(type)[0],
        MaxConcreteGrade = "C90/105",
        MaxSteelGrade = type == ColumnDesignType.ReinforcedConcrete ? "B500B" : "S550",
        Section = type == ColumnDesignType.MegaComposite ? ColumnSectionInput.MegaDefault : new(),
    };
}
