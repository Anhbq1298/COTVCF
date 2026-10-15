using Mcotis.Desktop.Domain;
using System.Text.Json.Serialization;

namespace Mcotis.Desktop.Geometry;

// What an analysis of the application's model is given and what it gives back. The analysis itself is
// Application/FemSolver, which runs the general solver of the standalone system (Mcotis.Analysis).

public sealed record FemSectionProperties(
    string MemberName,
    double AreaM2,
    double ElasticModulusKNPerM2,
    double? InPlaneInertiaM4,
    bool ReleaseStartRotation = false,
    bool ReleaseEndRotation = false,
    string? SectionName = null,
    string? MaterialName = null,
    double LocalAngleDeg = 0,
    double UnitWeightKNPerM3 = 0,
    double UnitMassTPerM3 = 0,
    double GrossAreaM2 = 0,
    IReadOnlyList<double>? Modifiers = null,
    FemSectionShape? Shape = null,
    // A frame out of its plane — what an analysis with UY, RX or RZ on needs: the bending inertia about the axis that
    // lies in the plane, and the torsion constant, with their modifiers. Null where the section holds none.
    double? OutOfPlaneInertiaM4 = null,
    double? TorsionConstantM4 = null)
{
    /// <summary>The material's Poisson ratio for the shear modulus of the native frame, G = E / (2 (1 + ν)); null: 0.3,
    /// as the main window's members have always been analysed. Set for a member taken from a native document (M8),
    /// whose material states its own.</summary>
    public double? PoissonRatio { get; init; }

    /// <summary>The moment about the frame's other local axis — out of the plane of a plane model — is released at the
    /// start / end (ETABS's M2 of a frame on local axes at 0°, M3 at 90°). The analysis in the plane does not use it; in 3D
    /// the native frame takes it as its moment-2 release (M9), as the source model has it.</summary>
    // Keep false absent from the legacy JSON revision form: adding these fields must not orphan results of an older
    // planar model. True is engineering content and is serialized normally.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReleaseStartOutOfPlane { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReleaseEndOutOfPlane { get; init; }

    public bool IsCable => InPlaneInertiaM4 is null;
    // SAP modifier order — frames: A, As2, As3, J, I22, I33, Mass, Weight; cables: A, Mass, Weight.
    private double Modifier(int frameIndex, int cableIndex) =>
        Modifiers is { } values && (IsCable ? cableIndex : frameIndex) is var index && index < values.Count ? values[index] : 1;
    public double AreaModifier => Modifier(0, 0);
    public double InertiaModifier => IsCable ? double.NaN : Modifier(5, -1);
    public double MassModifier => Modifier(6, 1);
    public double WeightModifier => Modifier(7, 2);
    private double SelfWeightArea => GrossAreaM2 > 0 ? GrossAreaM2 : AreaM2;
    /// <summary>Self-weight per length = unit weight × gross area × weight modifier [kN/m].</summary>
    public double WeightPerLengthKNPerM => UnitWeightKNPerM3 * SelfWeightArea * WeightModifier;
    /// <summary>Mass per length = unit mass × gross area × mass modifier [t/m].</summary>
    public double MassPerLengthTPerM => UnitMassTPerM3 * SelfWeightArea * MassModifier;
    public double? InertiaModifierOrNull => IsCable ? null : InertiaModifier;
    public double ElasticModulusGPa => ElasticModulusKNPerM2 / 1_000_000;
    public string EndRotationReleases => (ReleaseStartRotation, ReleaseEndRotation) switch
    {
        (true, true) => "I + J",
        (true, false) => "I",
        (false, true) => "J",
        _ => "None"
    };
}

public enum SectionShapeKind { Rectangle, Box, I, Channel, Tee, Angle, Circle, Pipe, Equivalent }

/// <summary>
/// Cross-section outline for drawing, in SAP's section axes: <see cref="Depth"/> (t3) along local 2 and
/// <see cref="Width"/> (t2) along local 3 [m]. <see cref="SectionShapeKind.Equivalent"/> is a solid rectangle with the
/// section's A, I33 and I22 (used when SAP gives no dimensions, e.g. Section Designer or filled-tube sections).
/// </summary>
public sealed record FemSectionShape(SectionShapeKind Kind, double Depth, double Width, double FlangeThickness = 0, double WebThickness = 0,
    double BottomWidth = 0, double BottomFlangeThickness = 0)
{
    public static FemSectionShape? EquivalentRectangle(double area, double i33, double i22) =>
        area > 0 && i33 > 0 && i22 > 0 ? new(SectionShapeKind.Equivalent, Math.Sqrt(12 * i33 / area), Math.Sqrt(12 * i22 / area)) : null;
}

/// <summary>A Tab 01 study load at a joint; <see cref="Pattern"/> is its SAP load pattern.</summary>
public sealed record FemAppliedLoad(string PointName, double FxKN, double FzKN, string? Pattern = null);
public sealed record FemJointResponse(string PointName, double UXM, double UZM, double RotationRad)
{
    /// <summary>Global Y displacement for a spatial result or mode shape; older planar results have zero.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double UYM { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double RXRad { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double RZRad { get; init; }
    /// <summary>Global right-hand rotation about Y; the legacy XZ slope is its negative.</summary>
    [JsonIgnore]
    public double RYRad => -RotationRad;
}
/// <param name="MomentYKNm">The moment of a support that holds the rotation in the plane (RY): positive counter-clockwise in the
/// XZ elevation (X right, Z up), the sense of the joint slope — the opposite of the right-hand sense about +Y. 0 at a pin.</param>
public sealed record FemSupportReaction(string PointName, double ReactionXKN, double ReactionZKN, double MomentYKNm = 0);
/// <summary>Result at one frame mesh node: <see cref="Fraction"/> runs 0 (I-end) to 1 (J-end) along the frame.</summary>
public sealed record FemFrameStation(double Fraction, double AxialKN, double ShearKN, double MomentKNm, double UXM, double UZM)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double UYM { get; init; }
}
public sealed record FemFrameResponse(string MemberName, double AxialStartKN, double AxialEndKN,
    double ShearStartKN, double ShearEndKN,
    double MomentStartKNm, double MomentEndKNm,
    IReadOnlyList<FemFrameStation>? Stations = null);
/// <param name="SagM">Mid-span sag of the cable below its chord [m], when the case loads it along its length and
/// cable sag is analysed; null for a straight (weightless or sag-off) cable.</param>
/// <param name="TangentStiffnessRatio">Axial stiffness the sagging cable still has, as a fraction of EA / L —
/// Ernst's equivalent modulus over E. Null with <paramref name="SagM"/>.</param>
public sealed record FemCableResponse(string MemberName, double AxialForceKN, bool Active,
    double TargetForceKN = 0, double AppliedPretensionKN = 0, double? PretensionStageForceKN = null,
    IReadOnlyList<double>? FitHistoryKN = null, double? SagM = null, double? TangentStiffnessRatio = null)
{
    /// <summary>P_result / P_preset after each pretension-fitting run (run 1 applies the target itself).</summary>
    public IReadOnlyList<double> FitRatios => TargetForceKN > 0 && FitHistoryKN is { } history
        ? history.Select(force => force / TargetForceKN).ToArray() : [];
}
public sealed record FemAnalysisResult(
    IReadOnlyList<FemJointResponse> Joints,
    IReadOnlyList<FemSupportReaction> SupportReactions,
    IReadOnlyList<FemFrameResponse> Frames,
    IReadOnlyList<FemCableResponse> Cables,
    int NonlinearIterations,
    int TargetFitIterations = 0,
    string? TargetFitFailure = null)
{
    /// <summary>False when the pretension stage could not reach every target within tolerance; the result then uses
    /// the closest pretension found so the deviation can still be reviewed.</summary>
    public bool TargetFitConverged => TargetFitFailure is null;
    public double MaximumDownwardDeflectionMm => Joints.Count == 0 ? 0 : Joints.Max(joint => -joint.UZM * 1000);
    public double MaximumAbsoluteAxialKN => Frames.SelectMany(frame => new[] { frame.AxialStartKN, frame.AxialEndKN })
        .Concat(Cables.Select(cable => cable.AxialForceKN)).Select(Math.Abs).DefaultIfEmpty().Max();
    public double MaximumAbsoluteMomentKNm => Frames.SelectMany(frame => new[] { frame.MomentStartKNm, frame.MomentEndKNm })
        .Select(Math.Abs).DefaultIfEmpty().Max();
    public double MaximumSupportReactionKN => SupportReactions.Count == 0 ? 0 :
        SupportReactions.Max(reaction => Math.Sqrt(reaction.ReactionXKN * reaction.ReactionXKN + reaction.ReactionZKN * reaction.ReactionZKN));
}
