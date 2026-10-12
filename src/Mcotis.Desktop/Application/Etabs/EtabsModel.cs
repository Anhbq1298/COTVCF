using System.Globalization;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Application.Etabs;

// ---------------------------------------------------------------------------------------------------------------
// An ETABS model as File → Import From ETABS reads it, in kN, m and degrees, whatever units ETABS wrote it in. Two
// readers fill it: EtabsE2kReader from ETABS's .e2k text file, and EtabsTableReader from the database tables ETABS
// exports through its API (the live import — ETABS refuses an .e2k export through the API for an unsaved model, the
// tables export works for every model). EtabsImport turns it into the Mcotis model. Nothing here talks to ETABS.
// ---------------------------------------------------------------------------------------------------------------

public sealed record EtabsMaterial(string Name, MaterialKind Kind, string Grade, double UnitWeightKNPerM3, double ElasticModulusKNPerM2,
    double PoissonRatio, double ThermalCoefficient, double? Fc, double? Fy, double? Fu, double? ExpectedFy, double? ExpectedFu);

public enum EtabsShape { I, Tube, Pipe, Rectangle, Circle, FilledTube, FilledPipe, Encasement, SectionDesigner, Other }

/// <summary>Longitudinal bars and ties of a concrete or encased column [m]. <see cref="Bars3Dir"/> is the number of bars
/// (corners included) on each face parallel to the local 3-axis, <see cref="Bars2Dir"/> on each face parallel to the
/// local 2-axis (CSI's NumberR3Bars / NumberR2Bars; the .e2k pattern "R-n2-n3").</summary>
public sealed record EtabsColumnRebar(string LongitudinalMaterial, string TieMaterial, double Cover, double BarDiameter,
    int Bars3Dir, int Bars2Dir, double TieDiameter, double TieSpacing);

public enum EtabsSdShapeKind { Rectangle, Circle, Pipe }

/// <summary>A Section Designer shape [m]: centre (<see cref="X"/> along the local 3-axis, <see cref="Y"/> along the
/// local 2-axis), depth along Y and width along X (a rectangle, rotated by <see cref="RotationDeg"/>), the diameter of a
/// circle or pipe in <see cref="Depth"/> and a pipe's wall in <see cref="Thickness"/>. A null <see cref="Material"/> is
/// an opening. A shape higher in <see cref="ZOrder"/> replaces what it covers.</summary>
public sealed record EtabsSdShape(int ZOrder, EtabsSdShapeKind Kind, string? Material, double X, double Y,
    double Depth, double Width, double Thickness, double RotationDeg);

/// <summary>A section's properties as ETABS states them [m², m⁴], transformed to the section's own material.</summary>
public sealed record EtabsSectionValues(double Area, double I33, double I22, double Torsion, double ShearArea2, double ShearArea3);

public sealed record EtabsFrameSection
{
    public required string Name { get; init; }
    /// <summary>The shape as ETABS names it, e.g. "Filled Steel Tube".</summary>
    public required string ShapeName { get; init; }
    public EtabsShape Shape { get; init; } = EtabsShape.Other;
    public string Material { get; init; } = "";
    public double Depth { get; init; }
    public double Width { get; init; }
    public double FlangeThickness { get; init; }
    public double WebThickness { get; init; }
    public double BottomWidth { get; init; }
    public double BottomFlangeThickness { get; init; }
    /// <summary>Wall of a pipe or filled pipe.</summary>
    public double Thickness { get; init; }
    public string FillMaterial { get; init; } = "";
    public string EncasementMaterial { get; init; } = "";
    public string EmbeddedSection { get; init; } = "";
    public EtabsColumnRebar? Rebar { get; init; }
    public IReadOnlyList<EtabsSdShape> SdShapes { get; init; } = [];
    /// <summary>ETABS's own A, I33, I22 … (the tables export has them; an .e2k does not).</summary>
    public EtabsSectionValues? Reported { get; init; }
    /// <summary>Section property modifiers in SAP order: A, As2, As3, J, I22, I33, mass, weight.</summary>
    public IReadOnlyList<double> Modifiers { get; init; } = EtabsModel.NoModifiers;
}

/// <summary>A point object at a story; <see cref="Name"/> is the Mcotis joint name, label@story.</summary>
public sealed record EtabsJoint(string Label, string Story, double X, double Y, double Z)
{
    public string Name => EtabsModel.JointName(Label, Story);
}

/// <summary>A frame object (beam, column or brace) between two joints (Mcotis joint names). <see cref="Key"/> =
/// label@story identifies it; its Mcotis name is the label alone when no other story uses that label.</summary>
public sealed record EtabsFrame(string Label, string Story, string Kind, string StartJoint, string EndJoint, string Section,
    IReadOnlySet<string> Releases, double AngleDeg, IReadOnlyList<double> Modifiers)
{
    public string Key => EtabsModel.JointName(Label, Story);
}

/// <summary>Restrained degrees of freedom of a joint: UX, UY, UZ, RX, RY, RZ.</summary>
public sealed record EtabsRestraint(string Joint, IReadOnlyList<bool> Dofs);

public sealed record EtabsJointLoad(string Joint, string Pattern, double Fx, double Fy, double Fz, double Mx, double My, double Mz);

/// <summary>A force on a frame (by <see cref="EtabsFrame.Key"/>) in kN/m (distributed) or kN (concentrated, at
/// <see cref="RelStart"/>). <see cref="Direction"/> is null for a direction out of the XZ plane or not understood.</summary>
public sealed record EtabsFrameLoad(string Frame, string Pattern, bool Concentrated, FemLoadDirection? Direction, string DirectionText,
    double RelStart, double RelEnd, double ValueStart, double ValueEnd);

public sealed record EtabsLoadPattern(string Name, string Type, double SelfWeightMultiplier);

/// <summary>A load case: ETABS's type ("Linear Static", "Nonlinear Static", "Modal - Eigen" …), its pattern loads, the
/// case it continues from (null: zero initial conditions) and what the internal FEM leaves out of it.</summary>
public sealed record EtabsLoadCase(string Name, string Type, IReadOnlyList<(string Pattern, double Factor)> Loads, string? InitialCase,
    IReadOnlyList<string> Notes);

public sealed record EtabsCombination(string Name, string Type, IReadOnlyList<(string Name, double Factor)> Items);

public sealed record EtabsModel(
    string Source,
    string Title,
    string ProgramVersion,
    IReadOnlyList<EtabsMaterial> Materials,
    IReadOnlyList<EtabsFrameSection> Sections,
    IReadOnlyList<EtabsJoint> Joints,
    IReadOnlyList<EtabsFrame> Frames,
    IReadOnlyList<EtabsRestraint> Restraints,
    IReadOnlyList<EtabsJointLoad> JointLoads,
    IReadOnlyList<EtabsFrameLoad> FrameLoads,
    IReadOnlyList<EtabsLoadPattern> Patterns,
    IReadOnlyList<EtabsLoadCase> Cases,
    IReadOnlyList<EtabsCombination> Combinations,
    IReadOnlyList<string> Notes)
{
    public static IReadOnlyList<double> NoModifiers { get; } = [1, 1, 1, 1, 1, 1, 1, 1];
    public static string JointName(string label, string story) => $"{label}@{story}";
}

/// <summary>The force and length units ETABS wrote a model in, and the factors to kN and m.</summary>
public readonly record struct EtabsUnits(double KiloNewtons, double Metres, string Text)
{
    // ETABS converts a kip as 4.448222 kN (not the exact 4.4482216153): with its factor, a model kept in kN-m and exported
    // in kip-in comes back to its own values (E 44,000,000 kN/m², not 43,999,996)
    private static readonly Dictionary<string, double> Forces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["N"] = 0.001, ["kN"] = 1, ["kgf"] = 0.00980665, ["tonf"] = 9.80665, ["ton"] = 9.80665, ["kip"] = 4.448222,
        ["lb"] = 0.004448222, ["lbf"] = 0.004448222,
    };
    private static readonly Dictionary<string, double> Lengths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = 1, ["cm"] = 0.01, ["mm"] = 0.001, ["in"] = 0.0254, ["inch"] = 0.0254, ["ft"] = 0.3048, ["feet"] = 0.3048,
    };

    public static EtabsUnits Parse(string force, string length) =>
        Forces.TryGetValue(force.Trim(), out var kn) && Lengths.TryGetValue(length.Trim(), out var m)
            ? new(kn, m, $"{force.Trim()}, {length.Trim()}")
            : throw new InvalidOperationException($"ETABS units '{force}, {length}' are not supported (force N, kN, kgf, tonf, kip or lb; length m, cm, mm, in or ft).");

    public double Length(double value) => value * Metres;
    public double Area(double value) => value * Metres * Metres;
    public double Inertia(double value) => value * Math.Pow(Metres, 4);
    public double Force(double value) => value * KiloNewtons;
    public double Moment(double value) => value * KiloNewtons * Metres;
    public double LineForce(double value) => value * KiloNewtons / Metres;
    public double Stress(double value) => value * KiloNewtons / (Metres * Metres);
    public double UnitWeight(double value) => value * KiloNewtons / Math.Pow(Metres, 3);
}

internal static class EtabsText
{
    public static double? Number(string? text) =>
        text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    public static MaterialKind MaterialKind(string? type) => (type ?? "").Replace(" ", "").ToUpperInvariant() switch
    {
        "CONCRETE" => Domain.MaterialKind.Concrete,
        "STEEL" => Domain.MaterialKind.Steel,
        "REBAR" => Domain.MaterialKind.Rebar,
        "TENDON" => Domain.MaterialKind.Tendon,
        "COLDFORMED" => Domain.MaterialKind.ColdFormed,
        "ALUMINUM" => Domain.MaterialKind.Aluminum,
        "MASONRY" => Domain.MaterialKind.Masonry,
        "NODESIGN" => Domain.MaterialKind.NoDesign,
        _ => Domain.MaterialKind.Other,
    };

    /// <summary>The shape from ETABS's name for it ("Steel I/Wide Flange", "Filled Steel Tube", "SD Section" …).</summary>
    public static EtabsShape Shape(string name) => name.Trim().ToUpperInvariant() switch
    {
        "STEEL I/WIDE FLANGE" or "I/WIDE FLANGE" => EtabsShape.I,
        "STEEL TUBE" or "BOX/TUBE" or "STEEL BOX" => EtabsShape.Tube,
        "STEEL PIPE" or "PIPE" => EtabsShape.Pipe,
        "CONCRETE RECTANGULAR" or "CONCRETE RECTANGLE" or "RECTANGULAR" => EtabsShape.Rectangle,
        "CONCRETE CIRCLE" or "CONCRETE CIRCULAR" or "CIRCLE" => EtabsShape.Circle,
        "FILLED STEEL TUBE" => EtabsShape.FilledTube,
        "FILLED STEEL PIPE" => EtabsShape.FilledPipe,
        "CONCRETE ENCASEMENT RECTANGLE" or "CONC ENCASEMENT RECTANGLE" => EtabsShape.Encasement,
        "SD SECTION" or "SECTION DESIGNER" => EtabsShape.SectionDesigner,
        _ => EtabsShape.Other,
    };

    /// <summary>A frame load direction in the XZ plane, or null (out of plane or not understood).</summary>
    public static FemLoadDirection? Direction(string? text) => (text ?? "").Replace(" ", "").Replace("-", "").ToUpperInvariant() switch
    {
        "GRAV" or "GRAVITY" => FemLoadDirection.Gravity,
        "GRAVPROJ" or "GRAVITYPROJECTED" => FemLoadDirection.ProjectedGravity,
        "X" or "GLOBALX" => FemLoadDirection.GlobalX,
        "Z" or "GLOBALZ" => FemLoadDirection.GlobalZ,
        "XPROJ" or "XPROJECTED" => FemLoadDirection.ProjectedX,
        "ZPROJ" or "ZPROJECTED" => FemLoadDirection.ProjectedZ,
        "1" or "LOCAL1" => FemLoadDirection.Local1,
        "2" or "LOCAL2" => FemLoadDirection.Local2,
        _ => null,
    };
}
