namespace Mcotis.Desktop.Geometry;

/// <summary>
/// Direction of a member load, using SAP2000's load-direction codes resolved to the 2D XZ model. Local directions
/// follow SAP's default local axes for a frame in the XZ plane (local 2 is the in-plane normal with +Z component,
/// or +X for a vertical member); projected loads are per unit projected length; gravity is −Z.
/// </summary>
public enum FemLoadDirection { Local1, Local2, GlobalX, GlobalZ, ProjectedX, ProjectedZ, Gravity, ProjectedGravity }

/// <summary>Joint load in the global XZ plane. <see cref="MyKNm"/> is SAP's M2 (moment about global Y).</summary>
public sealed record FemJointLoad(string PointName, double FxKN, double FzKN, double MyKNm = 0);

/// <summary>Distributed member load between relative distances <see cref="RelStart"/> and <see cref="RelEnd"/>,
/// varying linearly from <see cref="ValueStart"/> to <see cref="ValueEnd"/> [kN/m].</summary>
public sealed record FemMemberLoad(string MemberName, FemLoadDirection Direction, double RelStart, double RelEnd, double ValueStart, double ValueEnd);

/// <summary>Concentrated member load at relative distance <see cref="Rel"/> [kN].</summary>
public sealed record FemMemberPointLoad(string MemberName, FemLoadDirection Direction, double Rel, double ValueKN);

/// <summary>Already-factored loads of one analysis case.</summary>
public sealed record FemLoadSet(IReadOnlyList<FemJointLoad> Joints, IReadOnlyList<FemMemberLoad> Members, IReadOnlyList<FemMemberPointLoad> MemberPoints)
{
    public static FemLoadSet Empty { get; } = new([], [], []);
    public bool IsEmpty => Joints.Count == 0 && Members.Count == 0 && MemberPoints.Count == 0;

    public FemLoadSet Scaled(double factor) => factor == 1 ? this : new(
        Joints.Select(load => load with { FxKN = load.FxKN * factor, FzKN = load.FzKN * factor, MyKNm = load.MyKNm * factor }).ToArray(),
        Members.Select(load => load with { ValueStart = load.ValueStart * factor, ValueEnd = load.ValueEnd * factor }).ToArray(),
        MemberPoints.Select(load => load with { ValueKN = load.ValueKN * factor }).ToArray());

    public static FemLoadSet Sum(IEnumerable<FemLoadSet> sets)
    {
        var all = sets.ToArray();
        return new(all.SelectMany(set => set.Joints).ToArray(), all.SelectMany(set => set.Members).ToArray(),
            all.SelectMany(set => set.MemberPoints).ToArray());
    }
}

/// <summary>One load case for the solver: its factored loads and the factor on the fitted cable pretension
/// (0 when the case does not include a cable target-force pattern).</summary>
public sealed record FemCaseLoads(string Name, FemLoadSet Loads, double PretensionFactor);

/// <summary>Linear-additive combination of case (or other combination) results, as SAP2000 combines results.</summary>
public static class FemResultCombiner
{
    public static FemAnalysisResult Combine(IReadOnlyList<(FemAnalysisResult Result, double Factor)> items)
    {
        if (items.Count == 0) throw new InvalidOperationException("A load combination needs at least one case.");
        var first = items[0].Result;
        var joints = first.Joints.Select((joint, index) => new FemJointResponse(joint.PointName,
            items.Sum(item => item.Factor * item.Result.Joints[index].UXM),
            items.Sum(item => item.Factor * item.Result.Joints[index].UZM),
            items.Sum(item => item.Factor * item.Result.Joints[index].RotationRad))).ToArray();
        var reactions = first.SupportReactions.Select((reaction, index) => new FemSupportReaction(reaction.PointName,
            items.Sum(item => item.Factor * item.Result.SupportReactions[index].ReactionXKN),
            items.Sum(item => item.Factor * item.Result.SupportReactions[index].ReactionZKN),
            items.Sum(item => item.Factor * item.Result.SupportReactions[index].MomentYKNm))).ToArray();
        var frames = first.Frames.Select((frame, index) =>
        {
            double Sum(Func<FemFrameResponse, double> value) => items.Sum(item => item.Factor * value(item.Result.Frames[index]));
            var stations = frame.Stations?.Select((station, stationIndex) =>
            {
                double StationSum(Func<FemFrameStation, double> value) => items.Sum(item => item.Factor * value(item.Result.Frames[index].Stations![stationIndex]));
                return new FemFrameStation(station.Fraction, StationSum(s => s.AxialKN), StationSum(s => s.ShearKN), StationSum(s => s.MomentKNm),
                    StationSum(s => s.UXM), StationSum(s => s.UZM));
            }).ToArray();
            return new FemFrameResponse(frame.MemberName, Sum(f => f.AxialStartKN), Sum(f => f.AxialEndKN), Sum(f => f.ShearStartKN),
                Sum(f => f.ShearEndKN), Sum(f => f.MomentStartKNm), Sum(f => f.MomentEndKNm), stations);
        }).ToArray();
        var cables = first.Cables.Select((cable, index) =>
        {
            var force = items.Sum(item => item.Factor * item.Result.Cables[index].AxialForceKN);
            // sag and stiffness belong to one equilibrium state; a linear sum of cases has neither
            return cable with { AxialForceKN = force, Active = force > 1e-8, SagM = null, TangentStiffnessRatio = null };
        }).ToArray();
        return new FemAnalysisResult(joints, reactions, frames, cables, items.Max(item => item.Result.NonlinearIterations),
            first.TargetFitIterations, first.TargetFitFailure);
    }
}
