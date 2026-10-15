using Mcotis.Analysis;
using Mcotis.Core;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Presentation;

// The rows of the modal tables and the joint-mass form (moved here from the native workspace window, archived with it in
// M8 of the main-window migration: the main window's Modal Results and Assign → Joint → Masses… use them).
public sealed record NativeModeRow(int Mode, double Eigenvalue, double? Frequency, double? Period, string Status, double Residual);
public sealed record NativeParticipationRow(int Mode, string Direction, double? Factor, double? EffectiveMass, double? Ratio, double? Cumulative);
public sealed record NativeModeShapeRow(string Point, double UX, double UY, double UZ, double RX, double RY, double RZ);
public sealed record NativeModalMassRow(string Item, double Value, string Unit, string Scope);
public sealed record NativeJointMassDefinition(Mcotis.Core.ObjectId PointId, double UX, double UY, double UZ, double RX, double RY, double RZ);

/// <summary>The rows of a modal result — modes, participation, a mode's shape, the mass inventory — and the drawing of a
/// mode's shape, for every window that shows one: the native workspace and the main window. One definition, so the two
/// cannot say different things of the same result. <paramref name="name"/> gives a point's name.</summary>
public static class ModalResultRows
{
    public static IEnumerable<NativeModeRow> Modes(ModalAnalysisResult? result) => result?.Modes.Select(mode => new NativeModeRow(mode.ModeNumber,
        mode.EigenvaluePerSecondSquared, mode.FrequencyHz, mode.PeriodSeconds, mode.Status.ToString(), mode.ScaledResidual)) ?? [];

    public static IEnumerable<NativeParticipationRow> Participation(ModalAnalysisResult? result) => result?.Modes.SelectMany(mode => mode.Participation.Select(value =>
        new NativeParticipationRow(mode.ModeNumber, value.Direction.ToString(), value.Factor, value.EffectiveMass, value.RatioPercent, value.CumulativePercent))) ?? [];

    public static IEnumerable<NativeModeShapeRow> Shape(ModalAnalysisResult? result, int modeNumber, Func<ObjectId, string> name) =>
        result?.Modes.FirstOrDefault(mode => mode.ModeNumber == modeNumber) is { } mode
            ? mode.Shape.Select(item => new NativeModeShapeRow(name(item.PointId), item.UX, item.UY, item.UZ, item.RX, item.RY, item.RZ))
                .OrderBy(row => row.Point, StringComparer.Ordinal).ToArray()
            : [];

    public static IEnumerable<NativeModalMassRow> Mass(ModalAnalysisResult? result)
    {
        if (result is null) return [];
        var rows = new List<NativeModalMassRow> { new("Member self mass", result.MassSummary.ElementMass, "t", "Physical inventory") };
        string[] components = ["UX", "UY", "UZ", "RX", "RY", "RZ"];
        for (var i = 0; i < components.Length; i++)
        {
            string unit = i < 3 ? "t" : "t·m²";
            rows.Add(new($"Assigned {components[i]}", result.MassSummary.AdditionalByDof[i], unit, "All joints"));
            rows.Add(new($"Free {components[i]}", result.MassSummary.FreeMassByDof[i], unit, "Active, unrestrained DOFs"));
        }
        if (result.MassSummary.CenterOfMass is { } center)
        {
            rows.Add(new("Centre of mass X", center.X, "m", "Physical member + assigned translational mass"));
            rows.Add(new("Centre of mass Y", center.Y, "m", "Physical member + assigned translational mass"));
            rows.Add(new("Centre of mass Z", center.Z, "m", "Physical member + assigned translational mass"));
        }
        return rows;
    }

    /// <summary>Drawing adapter only: a mode's shape for the viewer, which magnifies these relative amplitudes and says so
    /// (<c>DeflectionIsModeShape</c>). They are not a displacement caused by a load.</summary>
    public static FemAnalysisResult? Display(ModalAnalysisResult? result, int modeNumber, Func<ObjectId, string> name)
    {
        if (result?.Modes.FirstOrDefault(mode => mode.ModeNumber == modeNumber) is not { } mode) return null;
        var joints = mode.Shape.Select(item => new FemJointResponse(name(item.PointId), item.UX, item.UZ, -item.RY)
            { UYM = item.UY, RXRad = item.RX, RZRad = item.RZ }).ToArray();
        return new(joints, [], [], [], 0);
    }
}
