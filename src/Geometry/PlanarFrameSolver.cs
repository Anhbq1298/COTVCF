using COT_VCF.Domain;
using MathNet.Numerics.LinearAlgebra.Double;

namespace COT_VCF.Geometry;

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
    FemSectionShape? Shape = null)
{
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
public sealed record FemJointResponse(string PointName, double UXM, double UZM, double RotationRad);
public sealed record FemSupportReaction(string PointName, double ReactionXKN, double ReactionZKN);
/// <summary>Result at one frame mesh node: <see cref="Fraction"/> runs 0 (I-end) to 1 (J-end) along the frame.</summary>
public sealed record FemFrameStation(double Fraction, double AxialKN, double ShearKN, double MomentKNm, double UXM, double UZM);
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

/// <summary>Splits a frame at joints lying along it, as SAP connects frames at intermediate joints.</summary>
public static class FrameJoints
{
    /// <summary>Distance from the frame axis within which a joint is treated as lying on the frame [m].</summary>
    public const double OnFrameToleranceM = 0.001;

    public sealed record Piece(StructuralMember Member, double FractionStart, double FractionEnd);

    /// <summary>
    /// The frame's pieces between consecutive joints from <paramref name="joints"/> that lie on it (in XZ), ordered from
    /// I to J. A frame with no intermediate joint returns itself as one piece.
    /// </summary>
    public static IReadOnlyList<Piece> Pieces(StructuralMember member, IReadOnlyDictionary<string, Position> joints)
    {
        if (!joints.TryGetValue(member.Start, out var start) || !joints.TryGetValue(member.End, out var end)) return [new(member, 0, 1)];
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        if (length <= OnFrameToleranceM) return [new(member, 0, 1)];
        var minimumFraction = OnFrameToleranceM / length;
        var interior = joints
            .Where(joint => joint.Key != member.Start && joint.Key != member.End)
            .Select(joint =>
            {
                var px = joint.Value.X - start.X;
                var pz = joint.Value.Z - start.Z;
                var along = (px * dx + pz * dz) / length;
                var offset = Math.Abs(px * dz - pz * dx) / length;
                return (joint.Key, Fraction: along / length, Offset: offset, OutOfPlane: Math.Abs(joint.Value.Y - start.Y));
            })
            .Where(item => item.Offset <= OnFrameToleranceM && item.OutOfPlane <= OnFrameToleranceM &&
                           item.Fraction > minimumFraction && item.Fraction < 1 - minimumFraction)
            .OrderBy(item => item.Fraction).ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();
        // Coincident joints (within tolerance) would create a zero-length piece; keep the first.
        var stops = new List<(string Name, double Fraction)> { (member.Start, 0) };
        foreach (var item in interior)
            if ((item.Fraction - stops[^1].Fraction) > minimumFraction) stops.Add((item.Key, item.Fraction));
        if (1 - stops[^1].Fraction <= minimumFraction) stops.RemoveAt(stops.Count - 1);
        stops.Add((member.End, 1));
        if (stops.Count == 2) return [new(member, 0, 1)];
        return stops.Zip(stops.Skip(1), (from, to) => new Piece(member with { Start = from.Name, End = to.Name }, from.Fraction, to.Fraction)).ToArray();
    }
}

public sealed class PlanarFrameSolver
{
    private const double PlanarityToleranceM = 1e-7;
    private const double CableForceToleranceKN = 1e-8;
    private const int MaximumNonlinearIterations = 100;
    public const int DefaultTargetFitIterations = 100;
    public const int DefaultFrameMeshSegments = 10;
    public const int MaximumFrameMeshSegments = 100;

    /// <summary>Single loading (joint loads only) with the pretension applied — the original one-case entry point.</summary>
    public FemAnalysisResult Solve(
        GeometryModel geometry,
        IReadOnlyDictionary<string, FemSectionProperties> frameSections,
        IReadOnlyDictionary<string, FemSectionProperties> cableSections,
        IReadOnlyDictionary<string, double> cablePretensionsKN,
        IReadOnlyList<FemAppliedLoad> loads,
        IReadOnlyList<string> pinSupportPoints,
        CancellationToken cancellationToken = default,
        double? targetFitPercent = null,
        int maximumTargetFitIterations = DefaultTargetFitIterations,
        int frameMeshSegments = 1,
        bool cableSag = false)
    {
        var caseLoads = new FemLoadSet(loads.Select(load => new FemJointLoad(load.PointName, load.FxKN, load.FzKN)).ToArray(), [], []);
        return SolveCases(geometry, frameSections, cableSections, cablePretensionsKN, FemLoadSet.Empty,
            [new FemCaseLoads("Default", caseLoads, 1)], pinSupportPoints, cancellationToken, targetFitPercent,
            maximumTargetFitIterations, frameMeshSegments, cableSag)["Default"];
    }

    /// <summary>
    /// Solves several load cases on one candidate geometry. The cable pretension is fitted once (when a tolerance is
    /// given) under <paramref name="fitLoads"/> — the other loads of the case that holds the cable target-force
    /// pattern, as SAP meets a Target Force at the end of that case — then every case is solved as its own
    /// tension-only equilibrium with its loads and <see cref="FemCaseLoads.PretensionFactor"/> × the fitted pretension.
    /// <para>With <paramref name="cableSag"/> a cable that a case loads along its length (its self-weight, typically)
    /// is no longer a straight bar: it hangs in a shallow parabola, and its tension follows the sag — see
    /// <see cref="SaggingTension"/>. This is the geometric nonlinearity of the cables; joint displacements stay
    /// small-displacement, and frames stay linear.</para>
    /// </summary>
    public IReadOnlyDictionary<string, FemAnalysisResult> SolveCases(
        GeometryModel geometry,
        IReadOnlyDictionary<string, FemSectionProperties> frameSections,
        IReadOnlyDictionary<string, FemSectionProperties> cableSections,
        IReadOnlyDictionary<string, double> cablePretensionsKN,
        FemLoadSet fitLoads,
        IReadOnlyList<FemCaseLoads> cases,
        IReadOnlyList<string> pinSupportPoints,
        CancellationToken cancellationToken = default,
        double? targetFitPercent = null,
        int maximumTargetFitIterations = DefaultTargetFitIterations,
        int frameMeshSegments = 1,
        bool cableSag = false)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (cases.Count == 0) throw new InvalidOperationException("At least one load case is required.");
        var frameMembers = geometry.Members.Where(member => member.Kind == MemberKind.Frame).ToArray();
        if (frameMembers.Length == 0) throw new InvalidOperationException("The connected model has no frame members to analyze.");

        var cableMembers = geometry.Members.Where(member => member.Kind == MemberKind.Cable && cableSections.ContainsKey(member.Name)).ToArray();
        var usedPoints = frameMembers.SelectMany(member => new[] { member.Start, member.End })
            .Concat(cableMembers.SelectMany(member => new[] { member.Start, member.End }))
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var points = usedPoints.ToDictionary(name => name, name => RequirePoint(geometry, name), StringComparer.Ordinal);
        if (points.Values.Max(point => point.Y) - points.Values.Min(point => point.Y) > PlanarityToleranceM)
            throw new InvalidOperationException("The internal FEM solver requires all analyzed frame and cable joints to lie in one XZ plane.");

        var nodeIndex = usedPoints.Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        if (frameMeshSegments is < 1 or > MaximumFrameMeshSegments)
            throw new InvalidOperationException($"Frame mesh must be between 1 and {MaximumFrameMeshSegments} segments per frame.");
        // Like SAP's automatic frame meshing at intermediate joints: a frame is split at every analyzed joint lying on
        // it (e.g. a full-length chord crossed by verticals and cable anchors), so those members connect to it. Each
        // piece is then meshed into frameMeshSegments sub-elements. Cables are never split or meshed.
        var frameElements = frameMembers.SelectMany(member =>
        {
            var properties = frameSections.TryGetValue(member.Name, out var found) ? found :
                throw new InvalidOperationException($"SAP section and material properties are missing for frame '{member.Name}'.");
            var pieces = FrameJoints.Pieces(member, points);
            return pieces.Select((piece, index) => CreateFrame(piece.Member, points, nodeIndex,
                properties with
                {
                    ReleaseStartRotation = index == 0 && properties.ReleaseStartRotation,
                    ReleaseEndRotation = index == pieces.Count - 1 && properties.ReleaseEndRotation
                },
                frameMeshSegments, piece.FractionStart, piece.FractionEnd));
        }).ToArray();
        var cableElements = cableMembers.Select(member => CreateCable(member, points, nodeIndex,
            cableSections[member.Name], cablePretensionsKN.GetValueOrDefault(member.Name))).ToArray();

        var supports = pinSupportPoints.Distinct(StringComparer.Ordinal).ToArray();
        if (supports.Length < 2 || supports.Any(name => !nodeIndex.ContainsKey(name)))
            throw new InvalidOperationException("At least two valid pin-support joints connected to analyzed members are required.");

        var dofCount = usedPoints.Length * 3;
        var restrained = new HashSet<int>();
        foreach (var support in supports)
        {
            var start = nodeIndex[support] * 3;
            restrained.Add(start);
            restrained.Add(start + 1);
        }
        var loadContext = new LoadContext(dofCount, nodeIndex,
            frameElements.GroupBy(frame => frame.Member.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal),
            cableElements.ToDictionary(cable => cable.Member.Name, StringComparer.Ordinal),
            frameSections.ToDictionary(pair => pair.Key, pair => pair.Value.LocalAngleDeg, StringComparer.Ordinal));

        if (targetFitPercent is double fit && (!double.IsFinite(fit) || fit <= 0 || fit >= 100))
            throw new InvalidOperationException("Target-force fitting tolerance must be greater than 0% and less than 100%.");
        if (maximumTargetFitIterations < 1)
            throw new InvalidOperationException("Target-force fitting needs at least one iteration.");

        // Without fitting, the target is applied once as the initial pretension. With fitting, the applied pretension is
        // adjusted until the targets are met under the fit-stage loads. The tension-only energy is convex — with
        // cable sag too — so a case's equilibrium does not depend on load path and one solve with pretension and
        // loads gives the staged result.
        var targets = cableElements.Select(cable => cable.InitialPretensionKN).ToArray();
        var applied = targets.ToArray();
        var pretensionStageForces = new double[targets.Length];
        var targetFitIterations = 0;
        var fitHistory = new List<double[]>();
        string? targetFitFailure = null;
        if (targetFitPercent is double tolerancePercent && targets.Any(target => target > 0))
        {
            var (fitExternal, _) = BuildLoads(loadContext, fitLoads);
            (applied, pretensionStageForces, targetFitIterations, targetFitFailure) = FitPretension(dofCount, frameElements,
                WithSag(cableElements, fitLoads, cableSag),
                restrained, targets, tolerancePercent, maximumTargetFitIterations, fitHistory, fitExternal, cancellationToken);
        }

        var results = new Dictionary<string, FemAnalysisResult>(StringComparer.Ordinal);
        foreach (var loadCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (external, pieceLoads) = BuildLoads(loadContext, loadCase.Loads);
            var caseCables = WithSag(WithPretension(cableElements, applied.Select(value => value * loadCase.PretensionFactor).ToArray()),
                loadCase.Loads, cableSag);
            var (displacement, nonlinearIterations) = SolveEquilibrium(dofCount, frameElements, caseCables, external, restrained, cancellationToken);

            var activeCables = caseCables.Where(cable => CableForce(cable, displacement) > CableForceToleranceKN)
                .Select(cable => cable.Member.Name).ToHashSet(StringComparer.Ordinal);
            var (stiffness, rightHandSide) = Assemble(dofCount, frameElements, caseCables, activeCables, external, displacement);
            var finalResidual = Residual(stiffness, rightHandSide, displacement);

            var jointResults = usedPoints.Select((name, index) => new FemJointResponse(name,
                displacement[index * 3], displacement[index * 3 + 1], displacement[index * 3 + 2])).ToArray();
            var reactions = supports.Select(name =>
            {
                var start = nodeIndex[name] * 3;
                return new FemSupportReaction(name, finalResidual[start], finalResidual[start + 1]);
            }).ToArray();
            var frameResults = frameElements.GroupBy(frame => frame.Member.Name, StringComparer.Ordinal)
                .Select(pieces => FrameResponse(pieces.ToArray(), displacement, pieceLoads)).ToArray();
            var cableResults = caseCables.Select((cable, index) =>
            {
                var force = Math.Max(0, CableForce(cable, displacement));
                var sagging = cable.SagParameter > 0 && force > CableForceToleranceKN;
                return new FemCableResponse(cable.Member.Name, force, force > CableForceToleranceKN, targets[index], applied[index],
                    targetFitIterations > 0 ? pretensionStageForces[index] : null,
                    fitHistory.Count > 0 ? fitHistory.Select(forces => forces[index]).ToArray() : null,
                    // parabola: f = w_n L² / 8T
                    sagging ? cable.NormalLoadKNPerM * cable.Length * cable.Length / (8 * force) : null,
                    sagging ? SaggingTangent(cable, force) / cable.AxialStiffness : null);
            }).ToArray();
            results[loadCase.Name] = new(jointResults, reactions, frameResults, cableResults, nonlinearIterations, targetFitIterations, targetFitFailure);
        }
        return results;
    }

    private sealed record LoadContext(int DofCount, IReadOnlyDictionary<string, int> NodeIndex,
        IReadOnlyDictionary<string, FrameElement[]> FramePieces, IReadOnlyDictionary<string, CableElement> Cables,
        IReadOnlyDictionary<string, double> LocalAnglesDeg);

    private static readonly (double Point, double Weight)[] Gauss3 =
        [(0.5 - Math.Sqrt(0.15), 5.0 / 18), (0.5, 8.0 / 18), (0.5 + Math.Sqrt(0.15), 5.0 / 18)];

    /// <summary>
    /// Global load vector of a load set. Joint loads go straight to the joints (SAP M2 about +Y acts as −θ in the XZ
    /// model). Frame member loads become consistent (Hermite) nodal loads on every mesh node, integrated exactly by
    /// 3-point Gauss over each sub-element; interior node loads are condensed onto the end joints with the same
    /// static condensation as the stiffness (and through any end release). Cable member loads are lumped to the
    /// cable's end joints by statics, since a cable carries load only axially.
    /// </summary>
    private static (double[] External, Dictionary<FrameElement, PieceLoad> PieceLoads) BuildLoads(LoadContext context, FemLoadSet loads)
    {
        var external = new double[context.DofCount];
        var nodeLoads = new Dictionary<FrameElement, (double[] Nodes, double[][] Sub)>(ReferenceEqualityComparer.Instance);
        foreach (var load in loads.Joints)
        {
            if (!context.NodeIndex.TryGetValue(load.PointName, out var node))
                throw new InvalidOperationException($"Load point '{load.PointName}' is not connected to an analyzed member.");
            if (!double.IsFinite(load.FxKN) || !double.IsFinite(load.FzKN) || !double.IsFinite(load.MyKNm))
                throw new InvalidOperationException($"Load at '{load.PointName}' is not finite.");
            external[node * 3] += load.FxKN;
            external[node * 3 + 1] += load.FzKN;
            external[node * 3 + 2] -= load.MyKNm;
        }

        (double[] Nodes, double[][] Sub) PieceVectors(FrameElement piece)
        {
            if (!nodeLoads.TryGetValue(piece, out var vectors))
                nodeLoads[piece] = vectors = (new double[3 * (piece.Segments + 1)], Enumerable.Range(0, piece.Segments).Select(_ => new double[6]).ToArray());
            return vectors;
        }
        void AddSubElementLoad(FrameElement piece, int segment, double xi, double axial, double transverse)
        {
            var h = piece.Length / piece.Segments;
            double[] shape = [(1 - xi) * axial, (1 - 3 * xi * xi + 2 * xi * xi * xi) * transverse, h * (xi - 2 * xi * xi + xi * xi * xi) * transverse,
                xi * axial, (3 * xi * xi - 2 * xi * xi * xi) * transverse, h * (-xi * xi + xi * xi * xi) * transverse];
            var (nodes, sub) = PieceVectors(piece);
            for (var index = 0; index < 6; index++)
            {
                sub[segment][index] += shape[index];
                nodes[3 * segment + index] += shape[index];
            }
        }

        foreach (var load in loads.Members)
        {
            if (!double.IsFinite(load.ValueStart) || !double.IsFinite(load.ValueEnd)) throw new InvalidOperationException($"Member load on '{load.MemberName}' is not finite.");
            var r1 = Math.Clamp(Math.Min(load.RelStart, load.RelEnd), 0, 1);
            var r2 = Math.Clamp(Math.Max(load.RelStart, load.RelEnd), 0, 1);
            if (r2 - r1 <= 1e-12) continue;
            double Intensity(double rel) => load.ValueStart + (load.ValueEnd - load.ValueStart) * (rel - load.RelStart) / (load.RelEnd - load.RelStart);
            if (context.FramePieces.TryGetValue(load.MemberName, out var pieces))
            {
                var memberLength = pieces.Sum(piece => piece.Length);
                var (gx, gz) = LoadVector(load.Direction, pieces[0].C, pieces[0].S, context.LocalAnglesDeg.GetValueOrDefault(load.MemberName));
                foreach (var piece in pieces)
                for (var segment = 0; segment < piece.Segments; segment++)
                {
                    var a = piece.FractionStart + (piece.FractionEnd - piece.FractionStart) * segment / piece.Segments;
                    var b = piece.FractionStart + (piece.FractionEnd - piece.FractionStart) * (segment + 1) / piece.Segments;
                    var from = Math.Max(a, r1);
                    var to = Math.Min(b, r2);
                    if (to - from <= 1e-12) continue;
                    foreach (var (point, weight) in Gauss3)
                    {
                        var rel = from + (to - from) * point;
                        var q = Intensity(rel) * weight * (to - from) * memberLength;
                        var (qx, qz) = (gx * q, gz * q);
                        AddSubElementLoad(piece, segment, (rel - a) / (b - a), qx * piece.C + qz * piece.S, -qx * piece.S + qz * piece.C);
                    }
                }
            }
            else if (context.Cables.TryGetValue(load.MemberName, out var cable))
            {
                var length = cable.Length;
                var (gx, gz) = LoadVector(load.Direction, cable.C, cable.S, 0);
                double forceI = 0, forceJ = 0;
                foreach (var (point, weight) in Gauss3)
                {
                    var rel = r1 + (r2 - r1) * point;
                    var q = Intensity(rel) * weight * (r2 - r1) * length;
                    forceI += q * (1 - rel);
                    forceJ += q * rel;
                }
                external[cable.Dofs[0]] += gx * forceI;
                external[cable.Dofs[1]] += gz * forceI;
                external[cable.Dofs[2]] += gx * forceJ;
                external[cable.Dofs[3]] += gz * forceJ;
            }
            else throw new InvalidOperationException($"Member load on '{load.MemberName}', which is not an analyzed frame or cable.");
        }

        foreach (var load in loads.MemberPoints)
        {
            if (!double.IsFinite(load.ValueKN)) throw new InvalidOperationException($"Point load on '{load.MemberName}' is not finite.");
            var rel = Math.Clamp(load.Rel, 0, 1);
            if (context.FramePieces.TryGetValue(load.MemberName, out var pieces))
            {
                var direction = load.Direction switch
                {
                    FemLoadDirection.ProjectedX => FemLoadDirection.GlobalX,
                    FemLoadDirection.ProjectedZ => FemLoadDirection.GlobalZ,
                    FemLoadDirection.ProjectedGravity => FemLoadDirection.Gravity,
                    var other => other
                };
                var (gx, gz) = LoadVector(direction, pieces[0].C, pieces[0].S, context.LocalAnglesDeg.GetValueOrDefault(load.MemberName));
                var (px, pz) = (gx * load.ValueKN, gz * load.ValueKN);
                var piece = pieces.First(item => rel <= item.FractionEnd + 1e-12);
                var local = (rel - piece.FractionStart) / (piece.FractionEnd - piece.FractionStart) * piece.Segments;
                var segment = Math.Clamp((int)Math.Floor(local), 0, piece.Segments - 1);
                AddSubElementLoad(piece, segment, Math.Clamp(local - segment, 0, 1), px * piece.C + pz * piece.S, -px * piece.S + pz * piece.C);
            }
            else throw new InvalidOperationException($"Point load on '{load.MemberName}', which is not an analyzed frame.");
        }

        var pieceLoads = new Dictionary<FrameElement, PieceLoad>(ReferenceEqualityComparer.Instance);
        foreach (var (piece, (nodes, sub)) in nodeLoads)
        {
            // Condensed end load g = f_e + Rᵀ f_i (R = −K_ii⁻¹K_ie), then through the releases like the stiffness.
            var interiorCount = piece.InteriorRecovery.GetLength(0);
            var condensed = new double[6];
            for (var index = 0; index < 3; index++)
            {
                condensed[index] = nodes[index];
                condensed[3 + index] = nodes[nodes.Length - 3 + index];
            }
            for (var col = 0; col < 6; col++)
            for (var row = 0; row < interiorCount; row++) condensed[col] += piece.InteriorRecovery[row, col] * nodes[3 + row];

            var released = (double[])condensed.Clone();
            var matrix = (double[,])piece.UnreleasedStiffness.Clone();
            foreach (var dof in new[] { piece.ReleaseStart ? 2 : -1, piece.ReleaseEnd ? 5 : -1 }.Where(dof => dof >= 0))
            {
                for (var other = 0; other < 6; other++)
                    if (other != dof) released[other] -= matrix[other, dof] / matrix[dof, dof] * released[dof];
                CondenseReleasedDof(matrix, dof);
                released[dof] = 0;
            }
            for (var row = 0; row < 6; row++)
            {
                var value = 0.0;
                for (var k = 0; k < 6; k++) value += piece.Transform[k, row] * released[k];
                external[piece.Dofs[row]] += value;
            }
            pieceLoads[piece] = new PieceLoad(nodes, sub, condensed);
        }
        return (external, pieceLoads);
    }

    /// <summary>Global (X, Z) components per unit load value for a member whose I→J direction is (c, s).</summary>
    private static (double X, double Z) LoadVector(FemLoadDirection direction, double c, double s, double localAngleDeg)
    {
        switch (direction)
        {
            case FemLoadDirection.GlobalX: return (1, 0);
            case FemLoadDirection.GlobalZ: return (0, 1);
            case FemLoadDirection.Gravity: return (0, -1);
            case FemLoadDirection.ProjectedX: return (Math.Abs(s), 0);
            case FemLoadDirection.ProjectedZ: return (0, Math.Abs(c));
            case FemLoadDirection.ProjectedGravity: return (0, -Math.Abs(c));
            case FemLoadDirection.Local1: return (c, s);
            case FemLoadDirection.Local2:
            {
                // SAP default local 2: +X for a vertical member, otherwise the in-plane normal with a +Z component.
                // A local-axis angle rotates it out of the plane; only its in-plane part (cos angle) acts here.
                var (nx, nz) = Math.Abs(c) < 1e-6 ? (1.0, 0.0) : c > 0 ? (-s, c) : (s, -c);
                var inPlane = Math.Cos(localAngleDeg * Math.PI / 180);
                return (nx * inPlane, nz * inPlane);
            }
            default: throw new InvalidOperationException($"Unsupported load direction {direction}.");
        }
    }

    /// <summary>
    /// Newton iteration on the cable pretensions so that, under the fit-stage loads, every targeted cable's tension is
    /// within <paramref name="tolerancePercent"/> of its target. The Jacobian is built by finite differences over all
    /// targeted cables, so the interaction between cables (tensioning one changes the others) is accounted for.
    /// </summary>
    private static (double[] Applied, double[] StageForces, int Iterations, string? Failure) FitPretension(int dofCount,
        IReadOnlyList<FrameElement> frameElements, CableElement[] cableElements, IReadOnlySet<int> restrained,
        double[] targets, double tolerancePercent, int maximumIterations, List<double[]> history, double[] fitExternal,
        CancellationToken cancellationToken)
    {
        var noLoad = fitExternal;
        var targeted = Enumerable.Range(0, targets.Length).Where(index => targets[index] > 0).ToArray();
        var applied = targets.ToArray();
        double[] StageForces(double[] pretension)
        {
            var cables = WithPretension(cableElements, pretension);
            var (displacement, _) = SolveEquilibrium(dofCount, frameElements, cables, noLoad, restrained, cancellationToken);
            return cables.Select(cable => Math.Max(0, CableForce(cable, displacement))).ToArray();
        }

        // When the targets cannot all be met, the closest iterate (smallest worst deviation) is returned with a reason,
        // so the case still produces results and its deviation from target can be reviewed.
        (double[] Applied, double[] Forces, double WorstDeviation) best = (applied, [], double.PositiveInfinity);
        var needsCompression = new SortedSet<string>(StringComparer.Ordinal);
        string Unfitted(string reason)
        {
            var worst = targeted.MaxBy(index => Math.Abs(best.Forces[index] - targets[index]) / targets[index]);
            return $"Not fitted within {tolerancePercent:0.###}%: {reason}; closest has '{cableElements[worst].Member.Name}' at {best.Forces[worst]:0.##} kN against its {targets[worst]:0.##} kN target ({best.WorstDeviation:0.##}%)." +
                (needsCompression.Count > 0
                    ? $" Meeting every target would need negative pretension in {string.Join(", ", needsCompression.Select(name => $"'{name}'"))}, so this Tab 03 target combination is not achievable."
                    : "");
        }

        for (var iteration = 1; ; iteration++)
        {
            var forces = StageForces(applied);
            history.Add(forces);
            var worst = targeted.MaxBy(index => Math.Abs(forces[index] - targets[index]) / targets[index]);
            var worstDeviation = Math.Abs(forces[worst] - targets[worst]) / targets[worst] * 100;
            if (worstDeviation < best.WorstDeviation) best = (applied.ToArray(), forces, worstDeviation);
            if (worstDeviation <= tolerancePercent) return (applied, forces, iteration, null);
            if (iteration >= maximumIterations)
                return (best.Applied, best.Forces, iteration, Unfitted($"{maximumIterations} pretension iterations used"));

            var jacobian = new double[targeted.Length, targeted.Length];
            for (var column = 0; column < targeted.Length; column++)
            {
                var cable = targeted[column];
                var step = Math.Max(1, 1e-3 * targets[cable]);
                var perturbed = applied.ToArray();
                perturbed[cable] += step;
                var perturbedForces = StageForces(perturbed);
                for (var row = 0; row < targeted.Length; row++)
                    jacobian[row, column] = (perturbedForces[targeted[row]] - forces[targeted[row]]) / step;
            }
            var deficit = new double[targeted.Length];
            for (var row = 0; row < targeted.Length; row++)
            {
                deficit[row] = targets[targeted[row]] - forces[targeted[row]];
                // A slack cable does not respond until it becomes taut; push its own pretension directly.
                if (Math.Abs(jacobian[row, row]) < 1e-6)
                {
                    for (var column = 0; column < targeted.Length; column++) jacobian[row, column] = 0;
                    jacobian[row, row] = 1;
                }
            }

            double[] correction;
            // Minimum-norm solve: cable segments in series share one tension, so only their combined pretension is
            // determined; the pseudo-inverse splits the correction evenly instead of failing or picking an extreme.
            try { correction = (DenseMatrix.OfArray(jacobian).PseudoInverse() * DenseVector.OfArray(deficit)).ToArray(); }
            catch (Exception) { correction = []; }
            if (correction.Length != targeted.Length || correction.Any(value => !double.IsFinite(value)))
                return (best.Applied, best.Forces, iteration, Unfitted("the cable pretension responses are not independent"));
            needsCompression.Clear();
            for (var row = 0; row < targeted.Length; row++)
            {
                var next = applied[targeted[row]] + correction[row];
                if (next < 0) needsCompression.Add(cableElements[targeted[row]].Member.Name);
                applied[targeted[row]] = Math.Max(0, next);
            }
        }
    }

    private static CableElement[] WithPretension(IReadOnlyList<CableElement> cables, IReadOnlyList<double> pretension) =>
        cables.Select((cable, index) => cable with { InitialPretensionKN = pretension[index] }).ToArray();

    /// <summary>
    /// Gives every cable the load that makes it sag in this load set: the distributed load on the cable, as its
    /// component normal to the chord per unit chord length (uniform-equivalent — self-weight w gives w·cos θ). Sag
    /// switched off, or a load set that puts nothing on a cable, leaves that cable the straight tension-only bar.
    /// The same loads still reach the joints through <see cref="BuildLoads"/>; this only adds what they do to the
    /// cable's own shape.
    /// </summary>
    private static CableElement[] WithSag(IReadOnlyList<CableElement> cables, FemLoadSet loads, bool enabled)
    {
        var normal = new Dictionary<string, double>(StringComparer.Ordinal);
        if (enabled)
        {
            var byName = cables.ToDictionary(cable => cable.Member.Name, StringComparer.Ordinal);
            foreach (var load in loads.Members)
            {
                if (!byName.TryGetValue(load.MemberName, out var cable)) continue;
                var r1 = Math.Clamp(Math.Min(load.RelStart, load.RelEnd), 0, 1);
                var r2 = Math.Clamp(Math.Max(load.RelStart, load.RelEnd), 0, 1);
                if (r2 - r1 <= 1e-12) continue;
                double Intensity(double rel) => load.ValueStart + (load.ValueEnd - load.ValueStart) * (rel - load.RelStart) / (load.RelEnd - load.RelStart);
                var total = (Intensity(r1) + Intensity(r2)) / 2 * (r2 - r1) * cable.Length;     // linear intensity: exact
                var (gx, gz) = LoadVector(load.Direction, cable.C, cable.S, 0);
                normal[load.MemberName] = normal.GetValueOrDefault(load.MemberName) + (-cable.S * gx + cable.C * gz) * total;
            }
        }
        return cables.Select(cable => cable with { NormalLoadKNPerM = Math.Abs(normal.GetValueOrDefault(cable.Member.Name)) / cable.Length }).ToArray();
    }

    private static (double[] Displacement, int Iterations) SolveEquilibrium(int dofCount,
        IReadOnlyList<FrameElement> frameElements, IReadOnlyList<CableElement> cableElements,
        double[] external, IReadOnlySet<int> restrained, CancellationToken cancellationToken)
    {
        var activeCables = new HashSet<string>(StringComparer.Ordinal);
        double[,] stiffness;
        double[] rightHandSide;
        var displacement = new double[dofCount];
        var forceTolerance = 1e-7 * Math.Max(1, Math.Max(external.Select(Math.Abs).DefaultIfEmpty().Max(),
            cableElements.Select(cable => cable.InitialPretensionKN).DefaultIfEmpty().Max()));
        var residualNorm = double.PositiveInfinity;
        var nonlinearIterations = 0;
        for (var iteration = 1; iteration <= MaximumNonlinearIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activeCables = cableElements.Where(cable => CableForce(cable, displacement) > CableForceToleranceKN)
                .Select(cable => cable.Member.Name).ToHashSet(StringComparer.Ordinal);
            (stiffness, rightHandSide) = Assemble(dofCount, frameElements, cableElements, activeCables, external, displacement);
            var residual = Residual(stiffness, rightHandSide, displacement);
            var freeDofs = Enumerable.Range(0, dofCount)
                .Where(index => !restrained.Contains(index) && stiffness[index, index] > 0).ToArray();
            residualNorm = freeDofs.Select(index => Math.Abs(residual[index])).DefaultIfEmpty().Max();
            if (residualNorm <= forceTolerance)
            {
                nonlinearIterations = iteration;
                break;
            }

            // The search direction uses a regularized tangent: slack cables keep a tiny stiffness and the free diagonal
            // gets a Levenberg shift that grows if the step fails. Joints held only by slack (or collinear) cables
            // otherwise make the tangent singular and the Newton direction meaningless. The residual and energy are
            // unregularized, so the converged equilibrium is unchanged.
            var currentEnergy = PotentialEnergy(frameElements, cableElements, external, displacement);
            var maxDiagonal = freeDofs.Select(index => stiffness[index, index]).DefaultIfEmpty(1).Max();
            var candidate = new double[dofCount];
            var accepted = false;
            foreach (var shift in LevenbergShifts)
            {
                var tangent = RegularizedTangent(stiffness, cableElements, activeCables, freeDofs, shift * maxDiagonal);
                double[] increment;
                try { increment = SolveSystem(tangent, residual.Select(value => -value).ToArray(), restrained); }
                catch (InvalidOperationException) { continue; }
                var slope = freeDofs.Sum(index => residual[index] * increment[index]);
                if (!double.IsFinite(slope) || slope >= 0) continue;

                var step = 1.0;
                while (step >= 1.0 / 1_048_576)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var index = 0; index < dofCount; index++) candidate[index] = displacement[index] + step * increment[index];
                    var candidateEnergy = PotentialEnergy(frameElements, cableElements, external, candidate);
                    if (double.IsFinite(candidateEnergy) && candidateEnergy <= currentEnergy + 1e-4 * step * slope)
                    {
                        accepted = true;
                        break;
                    }
                    step *= 0.5;
                }
                if (accepted) break;
            }
            if (!accepted)
                throw new InvalidOperationException($"Cable tension-only FEM line search stalled with residual {residualNorm:0.###} kN and {activeCables.Count} active cables.");
            displacement = candidate;
        }
        if (nonlinearIterations == 0)
            throw new InvalidOperationException($"Cable tension-only FEM equilibrium did not converge after {MaximumNonlinearIterations} damped Newton iterations (free-DOF residual {residualNorm:0.###} kN; {activeCables.Count} active cables).");
        return (displacement, nonlinearIterations);
    }

    // 0 first: a well-posed tangent takes the exact Newton step; larger shifts only when that step fails.
    private static readonly double[] LevenbergShifts = [0, 1e-12, 1e-8, 1e-5, 1e-3, 1e-1, 1];
    private const double SlackCableStiffnessRatio = 1e-6;

    private static double[,] RegularizedTangent(double[,] stiffness, IReadOnlyList<CableElement> cables,
        IReadOnlySet<string> activeCables, IReadOnlyList<int> freeDofs, double diagonalShift)
    {
        var tangent = (double[,])stiffness.Clone();
        foreach (var cable in cables)
        {
            if (activeCables.Contains(cable.Member.Name)) continue;
            var b = new[] { -cable.C, -cable.S, cable.C, cable.S };
            var k = SlackCableStiffnessRatio * cable.AxialStiffness;
            for (var row = 0; row < 4; row++)
            for (var col = 0; col < 4; col++) tangent[cable.Dofs[row], cable.Dofs[col]] += k * b[row] * b[col];
        }
        foreach (var index in freeDofs) tangent[index, index] += diagonalShift;
        return tangent;
    }

    private static double[,] BeamStiffness(double axialRigidity, double flexuralRigidity, double l)
    {
        var axial = axialRigidity / l;
        var b = flexuralRigidity / (l * l * l);
        var local = new double[6, 6];
        local[0, 0] = local[3, 3] = axial;
        local[0, 3] = local[3, 0] = -axial;
        local[1, 1] = local[4, 4] = 12 * b;
        local[1, 2] = local[2, 1] = 6 * l * b;
        local[1, 4] = local[4, 1] = -12 * b;
        local[1, 5] = local[5, 1] = 6 * l * b;
        local[2, 2] = local[5, 5] = 4 * l * l * b;
        local[2, 4] = local[4, 2] = -6 * l * b;
        local[2, 5] = local[5, 2] = 2 * l * l * b;
        local[4, 5] = local[5, 4] = -6 * l * b;
        return local;
    }

    /// <summary>
    /// A frame meshed into <paramref name="segments"/> equal Euler-Bernoulli sub-elements. The interior mesh nodes are
    /// unloaded and connect only to this frame, so they are statically condensed onto the end joints (exact, and it
    /// keeps the global system the size of the model); their displacements are recovered after the solve for
    /// station forces and the deflected shape. End-rotation releases are applied to the condensed end stiffness.
    /// </summary>
    private static FrameElement CreateFrame(StructuralMember member, IReadOnlyDictionary<string, Position> points,
        IReadOnlyDictionary<string, int> nodeIndex, FemSectionProperties properties, int segments,
        double fractionStart = 0, double fractionEnd = 1)
    {
        ValidateProperties(member.Name, properties, requireInertia: true);
        var (c, s, length) = Direction(member, points);
        var ea = properties.ElasticModulusKNPerM2 * properties.AreaM2;
        var ei = properties.ElasticModulusKNPerM2 * properties.InPlaneInertiaM4!.Value;
        var subStiffness = BeamStiffness(ea, ei, length / segments);
        var nodeDofs = 3 * (segments + 1);
        var assembled = new double[nodeDofs, nodeDofs];
        for (var segment = 0; segment < segments; segment++)
        for (var row = 0; row < 6; row++)
        for (var col = 0; col < 6; col++) assembled[3 * segment + row, 3 * segment + col] += subStiffness[row, col];
        int[] endDofs = [0, 1, 2, nodeDofs - 3, nodeDofs - 2, nodeDofs - 1];
        var interiorDofs = Enumerable.Range(3, nodeDofs - 6).ToArray();
        var local = new double[6, 6];
        var recovery = new double[interiorDofs.Length, 6];
        MathNet.Numerics.LinearAlgebra.Matrix<double>? interiorFlexibility = null;
        if (interiorDofs.Length == 0) local = (double[,])subStiffness.Clone();
        else
        {
            var kii = DenseMatrix.Create(interiorDofs.Length, interiorDofs.Length, (row, col) => assembled[interiorDofs[row], interiorDofs[col]]);
            var kie = DenseMatrix.Create(interiorDofs.Length, 6, (row, col) => assembled[interiorDofs[row], endDofs[col]]);
            interiorFlexibility = kii.Inverse();
            var r = -kii.Solve(kie);
            for (var row = 0; row < 6; row++)
            for (var col = 0; col < 6; col++)
            {
                var value = assembled[endDofs[row], endDofs[col]];
                for (var k = 0; k < interiorDofs.Length; k++) value += assembled[endDofs[row], interiorDofs[k]] * r[k, col];
                local[row, col] = value;
            }
            for (var row = 0; row < interiorDofs.Length; row++)
            for (var col = 0; col < 6; col++) recovery[row, col] = r[row, col];
        }
        var unreleased = (double[,])local.Clone();
        if (properties.ReleaseStartRotation) CondenseReleasedDof(local, 2);
        if (properties.ReleaseEndRotation) CondenseReleasedDof(local, 5);
        var transform = new double[6, 6];
        for (var offset = 0; offset <= 3; offset += 3)
        {
            transform[offset, offset] = c;
            transform[offset, offset + 1] = s;
            transform[offset + 1, offset] = -s;
            transform[offset + 1, offset + 1] = c;
            transform[offset + 2, offset + 2] = 1;
        }
        var temp = Multiply(local, transform);
        var global = Multiply(Transpose(transform), temp);
        var dofs = new[] { nodeIndex[member.Start] * 3, nodeIndex[member.Start] * 3 + 1,
            nodeIndex[member.Start] * 3 + 2, nodeIndex[member.End] * 3,
            nodeIndex[member.End] * 3 + 1, nodeIndex[member.End] * 3 + 2 };
        return new(member, dofs, local, transform, global, segments, c, s, subStiffness, unreleased, recovery,
            properties.ReleaseStartRotation, properties.ReleaseEndRotation, fractionStart, fractionEnd, length, interiorFlexibility);
    }

    /// <summary>Member loads of one frame piece for one case, in the piece's local axes: consistent nodal loads on
    /// every mesh node, the fixed-end load vector of each sub-element, and the condensed end-joint load (before
    /// releases) used to recover a released end rotation.</summary>
    private sealed record PieceLoad(double[] Nodes, double[][] SubElements, double[] CondensedEnds);

    /// <summary>Local displacements (u, w, θ) of every mesh node of a frame, from the end-joint solution.</summary>
    private static double[] MeshNodeDisplacements(FrameElement frame, IReadOnlyList<double> displacement, PieceLoad? load)
    {
        var ends = ToLocalDisplacement(frame.Transform, frame.Dofs, displacement);
        // A released end rotation is the member's own rotation, not the joint's: recover it from the unreleased
        // condensed stiffness with zero end moment at the released end(s), including any member load on the piece.
        var released = new List<int>();
        if (frame.ReleaseStart) released.Add(2);
        if (frame.ReleaseEnd) released.Add(5);
        if (released.Count > 0)
        {
            var others = Enumerable.Range(0, 6).Except(released).ToArray();
            var krr = DenseMatrix.Create(released.Count, released.Count, (row, col) => frame.UnreleasedStiffness[released[row], released[col]]);
            var rhs = DenseVector.Create(released.Count, row => (load?.CondensedEnds[released[row]] ?? 0) -
                others.Sum(col => frame.UnreleasedStiffness[released[row], col] * ends[col]));
            var rotations = krr.Solve(rhs);
            for (var index = 0; index < released.Count; index++) ends[released[index]] = rotations[index];
        }
        var nodes = new double[3 * (frame.Segments + 1)];
        for (var index = 0; index < 3; index++)
        {
            nodes[index] = ends[index];
            nodes[nodes.Length - 3 + index] = ends[3 + index];
        }
        // Interior nodes: u_i = K_ii⁻¹ f_i − K_ii⁻¹ K_ie u_e.
        var interiorLoad = load is not null && frame.InteriorFlexibility is { } flexibility
            ? flexibility * DenseVector.OfArray(load.Nodes.Skip(3).Take(nodes.Length - 6).ToArray())
            : null;
        for (var row = 0; row < frame.InteriorRecovery.GetLength(0); row++)
        {
            var value = interiorLoad?[row] ?? 0;
            for (var col = 0; col < 6; col++) value += frame.InteriorRecovery[row, col] * ends[col];
            nodes[3 + row] = value;
        }
        return nodes;
    }

    /// <summary>One result per SAP frame, with stations from every piece (split at intermediate joints) and mesh node.
    /// Stations repeat at a piece boundary so a jump in shear or axial force where a member connects stays visible.</summary>
    private static FemFrameResponse FrameResponse(IReadOnlyList<FrameElement> pieces, IReadOnlyList<double> displacement,
        IReadOnlyDictionary<FrameElement, PieceLoad> loads)
    {
        var stations = new List<FemFrameStation>();
        double[] firstForces = [];
        double[] lastForces = [];
        foreach (var frame in pieces)
        {
            var load = loads.GetValueOrDefault(frame);
            var nodes = MeshNodeDisplacements(frame, displacement, load);
            var pieceStations = new FemFrameStation[frame.Segments + 1];
            double[] pieceFirst = [];
            for (var segment = 0; segment < frame.Segments; segment++)
            {
                // Sub-element end forces = k·d − fixed-end load vector.
                var forces = Multiply(frame.SubStiffness, nodes.Skip(3 * segment).Take(6).ToArray());
                if (load is not null)
                    for (var index = 0; index < 6; index++) forces[index] -= load.SubElements[segment][index];
                if (segment == 0) pieceFirst = forces;
                lastForces = forces;
                pieceStations[segment + 1] = Station(frame, nodes, segment + 1, forces[3], -forces[4], forces[5]);
            }
            if (firstForces.Length == 0) firstForces = pieceFirst;
            // One sign convention along the frame: the J-end values (f3, -f4, f5) are the internal axial, shear and
            // moment; at the I-end the same internal actions are (-f0, f1, -f2).
            pieceStations[0] = Station(frame, nodes, 0, -pieceFirst[0], pieceFirst[1], -pieceFirst[2]);
            stations.AddRange(pieceStations);
        }
        return new FemFrameResponse(pieces[0].Member.Name, -firstForces[0], lastForces[3],
            firstForces[1], -lastForces[4], -firstForces[2], lastForces[5], stations);
    }

    private static FemFrameStation Station(FrameElement frame, double[] nodes, int node, double axial, double shear, double moment)
    {
        var u = nodes[3 * node];
        var w = nodes[3 * node + 1];
        var fraction = frame.FractionStart + (frame.FractionEnd - frame.FractionStart) * node / frame.Segments;
        return new FemFrameStation(fraction, axial, shear, moment,
            frame.C * u - frame.S * w, frame.S * u + frame.C * w);
    }

    private static CableElement CreateCable(StructuralMember member, IReadOnlyDictionary<string, Position> points,
        IReadOnlyDictionary<string, int> nodeIndex, FemSectionProperties properties, double pretensionKN)
    {
        ValidateProperties(member.Name, properties, requireInertia: false);
        if (!double.IsFinite(pretensionKN) || pretensionKN < 0)
            throw new InvalidOperationException($"Cable pretension for '{member.Name}' must be finite and nonnegative.");
        var (c, s, length) = Direction(member, points);
        var dofs = new[] { nodeIndex[member.Start] * 3, nodeIndex[member.Start] * 3 + 1,
            nodeIndex[member.End] * 3, nodeIndex[member.End] * 3 + 1 };
        var axial = properties.ElasticModulusKNPerM2 * properties.AreaM2 / length;
        return new(member, dofs, c, s, axial, pretensionKN, length);
    }

    private static (double C, double S, double Length) Direction(StructuralMember member, IReadOnlyDictionary<string, Position> points)
    {
        var start = points[member.Start];
        var end = points[member.End];
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        if (!double.IsFinite(length) || length <= PlanarityToleranceM)
            throw new InvalidOperationException($"Member '{member.Name}' has zero or invalid XZ length.");
        return (dx / length, dz / length, length);
    }

    private static void ValidateProperties(string name, FemSectionProperties properties, bool requireInertia)
    {
        if (!double.IsFinite(properties.AreaM2) || properties.AreaM2 <= 0 ||
            !double.IsFinite(properties.ElasticModulusKNPerM2) || properties.ElasticModulusKNPerM2 <= 0 ||
            (requireInertia && (properties.InPlaneInertiaM4 is not double inertia || !double.IsFinite(inertia) || inertia <= 0)))
            throw new InvalidOperationException($"SAP section properties for '{name}' must have positive finite E, A" +
                (requireInertia ? ", and in-plane I." : "."));
    }

    /// <summary>Stiffness and load vector at <paramref name="displacement"/>. A straight cable is linear, so they do
    /// not depend on it; a sagging cable enters with its tangent stiffness there and the constant that makes
    /// K·u − rhs its exact tension at that point — so the residual is exact and the matrix is the Newton tangent.</summary>
    private static (double[,] Stiffness, double[] RightHandSide) Assemble(int dofCount,
        IReadOnlyList<FrameElement> frames, IReadOnlyList<CableElement> cables,
        IReadOnlySet<string> activeCables, IReadOnlyList<double> external, IReadOnlyList<double> displacement)
    {
        var stiffness = new double[dofCount, dofCount];
        var rhs = external.ToArray();
        foreach (var frame in frames) AddMatrix(stiffness, frame.Dofs, frame.GlobalStiffness);
        foreach (var cable in cables)
        {
            if (!activeCables.Contains(cable.Member.Name)) continue;
            var (axial, pretension) = (cable.AxialStiffness, cable.InitialPretensionKN);
            if (cable.SagParameter > 0)
            {
                var tension = CableForce(cable, displacement);
                axial = SaggingTangent(cable, tension);
                pretension = tension - axial * CableExtension(cable, displacement);
            }
            var b = new[] { -cable.C, -cable.S, cable.C, cable.S };
            var local = new double[4, 4];
            for (var row = 0; row < 4; row++)
            for (var col = 0; col < 4; col++) local[row, col] = axial * b[row] * b[col];
            AddMatrix(stiffness, cable.Dofs, local);
            for (var i = 0; i < 4; i++) rhs[cable.Dofs[i]] -= b[i] * pretension;
        }
        return (stiffness, rhs);
    }

    private static double[] SolveSystem(double[,] stiffness, double[] rhs, IReadOnlySet<int> restrained)
    {
        var free = Enumerable.Range(0, rhs.Length)
            .Where(index => !restrained.Contains(index) && stiffness[index, index] > 0).ToArray();
        var zeroDofWithLoad = Enumerable.Range(0, rhs.Length)
            .FirstOrDefault(index => !restrained.Contains(index) && stiffness[index, index] == 0 && Math.Abs(rhs[index]) > 1e-9, -1);
        if (zeroDofWithLoad >= 0) throw new InvalidOperationException("A load acts on an unconstrained degree of freedom with no stiffness.");
        var solution = new double[rhs.Length];
        if (free.Length == 0) return solution;
        var reduced = new double[free.Length, free.Length];
        var reducedRhs = new double[free.Length];
        for (var row = 0; row < free.Length; row++)
        {
            reducedRhs[row] = rhs[free[row]];
            for (var col = 0; col < free.Length; col++) reduced[row, col] = stiffness[free[row], free[col]];
        }
        try
        {
            var factorization = DenseMatrix.OfArray(reduced).LU();
            var values = factorization.Solve(DenseVector.OfArray(reducedRhs)).ToArray();
            for (var index = 0; index < free.Length; index++)
            {
                if (!double.IsFinite(values[index])) throw new InvalidOperationException("The FEM solution contains a non-finite displacement.");
                solution[free[index]] = values[index];
            }
            return solution;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException("The FEM stiffness matrix could not be solved; check supports and connectivity.", ex); }
    }

    /// <summary>Change of the chord length between the cable's end joints [m].</summary>
    private static double CableExtension(CableElement cable, IReadOnlyList<double> displacement) =>
        -cable.C * displacement[cable.Dofs[0]] - cable.S * displacement[cable.Dofs[1]] +
        cable.C * displacement[cable.Dofs[2]] + cable.S * displacement[cable.Dofs[3]];

    /// <summary>Cable tension at the given joint displacements. A straight cable: pretension + EA/L × extension, which
    /// may come out negative (the caller treats that as slack). A sagging cable: always positive.</summary>
    private static double CableForce(CableElement cable, IReadOnlyList<double> displacement)
    {
        var straight = cable.InitialPretensionKN + cable.AxialStiffness * CableExtension(cable, displacement);
        return cable.SagParameter > 0 ? SaggingTension(cable.AxialStiffness, cable.SagParameter, straight) : straight;
    }

    /// <summary>
    /// Tension T of an elastic cable hanging in a shallow parabola under a load w_n normal to its chord. Its length
    /// along the curve exceeds the chord by w_n²L³ / 24T² (= a / T²), so less of the joint movement stretches it:
    /// <c>T / k − a / T² = s / k</c>, where k = EA / L and s is the tension the same cable would have as a straight
    /// bar (pretension + k × chord extension; negative once a straight cable would be slack). The root is unique
    /// and positive — a cable with weight never goes fully slack, it sags further — and its slope dT/de is Ernst's
    /// equivalent stiffness k / (1 + w_n²L²·EA / 12T³). With a = 0 this is the straight tension-only cable.
    /// </summary>
    private static double SaggingTension(double axialStiffness, double sagParameter, double straight)
    {
        // T³ − s·T² − k·a = 0. Right of its root the cubic is increasing and convex, so Newton from the upper bound
        // max(s, 0) + ∛(k·a) comes down onto the root monotonically.
        var q = axialStiffness * sagParameter;
        var tension = Math.Max(straight, 0) + Math.Cbrt(q);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var value = tension * tension * (tension - straight) - q;
            var slope = tension * (3 * tension - 2 * straight);
            if (!(slope > 0)) break;
            var next = tension - value / slope;
            if (!(next > 0) || !double.IsFinite(next)) break;
            var change = Math.Abs(next - tension);
            tension = next;
            if (change <= 1e-13 * Math.Max(1, tension)) break;
        }
        return tension;
    }

    /// <summary>dT/de of a sagging cable at tension <paramref name="tension"/>: k / (1 + 2·k·a / T³).</summary>
    private static double SaggingTangent(CableElement cable, double tension) =>
        cable.AxialStiffness / (1 + 2 * cable.AxialStiffness * cable.SagParameter / (tension * tension * tension));

    private static void CondenseReleasedDof(double[,] stiffness, int released)
    {
        var pivot = stiffness[released, released];
        if (!double.IsFinite(pivot) || pivot <= 0) throw new InvalidOperationException("A released frame-end rotation has no finite stiffness to condense.");
        for (var row = 0; row < stiffness.GetLength(0); row++)
        {
            if (row == released) continue;
            for (var col = 0; col < stiffness.GetLength(1); col++)
            {
                if (col == released) continue;
                stiffness[row, col] -= stiffness[row, released] * stiffness[released, col] / pivot;
            }
        }
        for (var index = 0; index < stiffness.GetLength(0); index++)
            stiffness[released, index] = stiffness[index, released] = 0;
    }

    private static double[] ToLocalDisplacement(double[,] transform, IReadOnlyList<int> dofs, IReadOnlyList<double> displacement)
    {
        var local = new double[6];
        for (var row = 0; row < 6; row++)
        for (var col = 0; col < 6; col++) local[row] += transform[row, col] * displacement[dofs[col]];
        return local;
    }

    private static Position RequirePoint(GeometryModel geometry, string name) =>
        geometry.Points.TryGetValue(name, out var point)
            ? point.Position
            : throw new InvalidOperationException($"Member endpoint '{name}' is missing from geometry.");

    private static void AddMatrix(double[,] target, IReadOnlyList<int> dofs, double[,] source)
    {
        for (var row = 0; row < dofs.Count; row++)
        for (var col = 0; col < dofs.Count; col++) target[dofs[row], dofs[col]] += source[row, col];
    }

    private static double[,] Transpose(double[,] matrix)
    {
        var result = new double[matrix.GetLength(1), matrix.GetLength(0)];
        for (var row = 0; row < result.GetLength(0); row++)
        for (var col = 0; col < result.GetLength(1); col++) result[row, col] = matrix[col, row];
        return result;
    }

    private static double[,] Multiply(double[,] left, double[,] right)
    {
        var result = new double[left.GetLength(0), right.GetLength(1)];
        for (var row = 0; row < left.GetLength(0); row++)
        for (var col = 0; col < right.GetLength(1); col++)
        for (var k = 0; k < left.GetLength(1); k++) result[row, col] += left[row, k] * right[k, col];
        return result;
    }

    private static double[] Multiply(double[,] matrix, IReadOnlyList<double> vector)
    {
        var result = new double[matrix.GetLength(0)];
        for (var row = 0; row < matrix.GetLength(0); row++)
        for (var col = 0; col < matrix.GetLength(1); col++) result[row] += matrix[row, col] * vector[col];
        return result;
    }

    private static double[] Residual(double[,] stiffness, IReadOnlyList<double> rightHandSide, IReadOnlyList<double> displacement)
    {
        var residual = Multiply(stiffness, displacement);
        for (var index = 0; index < residual.Length; index++) residual[index] -= rightHandSide[index];
        return residual;
    }

    private static double PotentialEnergy(IReadOnlyList<FrameElement> frames, IReadOnlyList<CableElement> cables,
        IReadOnlyList<double> external, IReadOnlyList<double> displacement)
    {
        var energy = 0.0;
        foreach (var frame in frames)
        {
            for (var row = 0; row < frame.Dofs.Length; row++)
            for (var col = 0; col < frame.Dofs.Length; col++)
                energy += 0.5 * displacement[frame.Dofs[row]] * frame.GlobalStiffness[row, col] * displacement[frame.Dofs[col]];
        }
        foreach (var cable in cables)
        {
            var force = CableForce(cable, displacement);
            // ∫T de. Sagging: de = (1/k + 2a/T³) dT gives T²/2k − 2a/T, convex in the extension like the straight law.
            if (cable.SagParameter > 0) energy += force * force / (2 * cable.AxialStiffness) - 2 * cable.SagParameter / force;
            else if (force > 0) energy += force * force / (2 * cable.AxialStiffness);
        }
        for (var index = 0; index < displacement.Count; index++) energy -= external[index] * displacement[index];
        return energy;
    }

    private sealed record FrameElement(StructuralMember Member, int[] Dofs, double[,] LocalStiffness,
        double[,] Transform, double[,] GlobalStiffness, int Segments, double C, double S, double[,] SubStiffness,
        double[,] UnreleasedStiffness, double[,] InteriorRecovery, bool ReleaseStart, bool ReleaseEnd,
        double FractionStart, double FractionEnd, double Length,
        MathNet.Numerics.LinearAlgebra.Matrix<double>? InteriorFlexibility);
    /// <param name="InitialPretensionKN">Tension the cable would have at the model geometry as a straight bar — its
    /// lack of fit × EA / L. With sag the tension there is a little higher: the curve is longer than the chord.</param>
    /// <param name="NormalLoadKNPerM">Distributed load normal to the chord in the load set being solved; 0 = straight.</param>
    private sealed record CableElement(StructuralMember Member, int[] Dofs, double C, double S,
        double AxialStiffness, double InitialPretensionKN, double Length, double NormalLoadKNPerM = 0)
    {
        /// <summary>a = w_n²·L³ / 24 [kN²·m]: the cable's curve is a / T² longer than its chord.</summary>
        public double SagParameter => NormalLoadKNPerM * NormalLoadKNPerM * Length * Length * Length / 24;
    }
}