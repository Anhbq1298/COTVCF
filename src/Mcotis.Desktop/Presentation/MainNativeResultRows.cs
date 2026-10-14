using Mcotis.Analysis;

namespace Mcotis.Desktop.Presentation;

/// <summary>Six-component static results kept apart from the legacy XZ table. Values use the solver's global joint
/// axes and each frame's local 1/2/3 axes; no 3D component is projected into a planar column.</summary>
public sealed record NativeJointResultRow(string Joint,
    double UX, double UY, double UZ, double RX, double RY, double RZ,
    double FX, double FY, double FZ, double MX, double MY, double MZ);

public sealed record NativeFrameStationResultRow(string Frame, double Station,
    double U1, double U2, double U3, double R1, double R2, double R3,
    double F1, double F2, double F3, double M1, double M2, double M3);

public sealed partial class MainViewModel
{
    private StaticResult? SelectedNativeStatic => SelectedFemRun?.Native is { } native && SelectedFemResultName is { } name &&
        native.Static.TryGetValue(name, out var result) ? result : null;

    public bool HasNativeStaticResults => SelectedNativeStatic is not null;

    public IReadOnlyList<NativeJointResultRow> NativeJointResultRows => SelectedNativeStatic is not { } result || SelectedFemRun?.Native is not { } native ? [] :
        result.Joints.Select(item => new NativeJointResultRow(
                native.Names.TryGetName(item.PointId, out var name) ? name : item.PointId.ToString(),
                item.Displacements[0], item.Displacements[1], item.Displacements[2], item.Displacements[3], item.Displacements[4], item.Displacements[5],
                item.Reactions[0], item.Reactions[1], item.Reactions[2], item.Reactions[3], item.Reactions[4], item.Reactions[5]))
            .OrderBy(item => item.Joint, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<NativeFrameStationResultRow> NativeFrameStationResultRows => SelectedNativeStatic is not { } result || SelectedFemRun?.Native is not { } native ? [] :
        result.Frames.SelectMany(frame => frame.Segments.SelectMany(segment => segment.Stations.Select(station =>
                new NativeFrameStationResultRow(
                    native.Names.TryGetName(frame.ParentObjectId, out var name) ? name : frame.ParentObjectId.ToString(), station.Relative,
                    station.LocalDisplacements[0], station.LocalDisplacements[1], station.LocalDisplacements[2],
                    station.LocalDisplacements[3], station.LocalDisplacements[4], station.LocalDisplacements[5],
                    station.Actions[0], station.Actions[1], station.Actions[2], station.Actions[3], station.Actions[4], station.Actions[5]))))
            .OrderBy(item => item.Frame, StringComparer.Ordinal).ThenBy(item => item.Station).ToArray();
}
