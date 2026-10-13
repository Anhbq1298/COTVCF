using System.Globalization;
using Mcotis.Analysis;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application;

/// <summary>How one of the six degrees of freedom of a joint stands in the equations of a case.</summary>
public enum FemDofState
{
    /// <summary>Not a degree of freedom of this analysis: the plane of the model switches it off, or no member uses it there.</summary>
    Inactive,
    /// <summary>Held by a support: not an equation.</summary>
    Restrained,
    /// <summary>An equation, with an estimate of the digits of accuracy lost.</summary>
    Estimated,
    /// <summary>An equation at which a singularity was identified.</summary>
    Infinite,
    /// <summary>On, but without an estimate: left out of the equations, or the stiffness could not be factorized.</summary>
    Unavailable,
}

/// <summary>One degree of freedom of one joint in the numerical stability report of a case: the joint by its own name
/// and global coordinates [m], the movement, what the solver found there and — only where it has one — its estimate.
/// The case and the step it belongs to are those of the report that holds it.</summary>
public sealed record FemDofDiagnostic(string Joint, double X, double Y, double Z, Native.DofComponent Dof, FemDofState State,
    StabilityCondition? Condition, StabilitySeverity Severity, double? DigitsLost);

/// <param name="Dofs">UX, UY, UZ, RX, RY, RZ — always the six, whatever the model solves.</param>
public sealed record FemJointDiagnostic(string Joint, double X, double Y, double Z, IReadOnlyList<FemDofDiagnostic> Dofs)
{
    public IEnumerable<FemDofDiagnostic> Reported => Dofs.Where(dof => dof.Severity != StabilitySeverity.None);
}

/// <summary>The numerical stability of one load case as the solver assessed it (<see cref="StaticStability"/>), with
/// the joints by name. Nothing here is computed in the application.</summary>
/// <param name="Joints">The joints that have a degree of freedom reported, each with its six.</param>
public sealed record FemStabilityReport(StabilityState State, StabilityStage Stage, int Iteration, int Equations, int ZeroStiffnessModes,
    double? LargestDigitsLost, string? LargestJoint, Native.DofComponent? LargestDof, int Warnings, int Instabilities,
    IReadOnlyList<FemJointDiagnostic> Joints, double WarningDigits, double SevereDigits)
{
    public bool Passed => Warnings == 0 && Instabilities == 0;

    public static FemStabilityReport From(StaticStability stability, CompiledModel model)
    {
        var document = model.Input;
        var assessed = stability.Dofs.ToDictionary(dof => dof.Dof);
        var joints = stability.Dofs.Select(dof => dof.Dof.PointId).Distinct().Select(pointId =>
        {
            var point = document.Points[pointId];
            FemDofDiagnostic Dof(Native.DofComponent component, FemDofState state, StaticDofStability? found = null) =>
                new(point.Name, point.Position.X, point.Position.Y, point.Position.Z, component, state, found?.Condition, found?.Severity ?? StabilitySeverity.None, found?.DigitsLost);
            var dofs = Native.DegreesOfFreedom.Components.Select(component =>
            {
                if (assessed.TryGetValue(new(pointId, component), out var found))
                    return Dof(component, found.IsInfinite ? FemDofState.Infinite : found.DigitsLost is null ? FemDofState.Unavailable : FemDofState.Estimated, found);
                return Dof(component, model.DofIndex[new(pointId, component)].State switch
                {
                    DofState.Inactive => FemDofState.Inactive,
                    DofState.Restrained => FemDofState.Restrained,
                    _ => FemDofState.Unavailable,   // on and free, but left out of the equations
                });
            }).ToArray();
            return new FemJointDiagnostic(point.Name, point.Position.X, point.Position.Y, point.Position.Z, dofs);
        }).ToArray();
        return new(stability.State, stability.Stage, stability.Iteration, stability.Equations, stability.ZeroStiffnessModes,
            stability.LargestDigitsLost, stability.LargestAt is { } at ? document.Points[at.PointId].Name : null, stability.LargestAt?.Component,
            stability.Warnings, stability.Instabilities, joints, stability.Thresholds.WarningDigits, stability.Thresholds.SevereDigits);
    }
}

/// <summary>The words the run monitor uses for a stability report. They follow the solver's own classification; a
/// degree of freedom is called a possible mechanism, never a mechanism, because the diagnostic is numerical.</summary>
public static class FemStabilityText
{
    public const string Infinity = "∞", Inactive = "-", Restrained = "R", Unavailable = "N/A";
    private static readonly CultureInfo Plain = CultureInfo.InvariantCulture;

    /// <summary>Digits lost with one decimal — more where one decimal would read as the other side of a threshold.</summary>
    public static string Digits(double value, double warningDigits, double severeDigits)
    {
        int Side(double digits) => digits > severeDigits ? 2 : digits >= warningDigits ? 1 : 0;
        for (int decimals = 1; decimals < 9; decimals++)
        {
            string text = value.ToString("F" + decimals, Plain);
            if (Side(double.Parse(text, Plain)) == Side(value)) return text;
        }
        return value.ToString("R", Plain);
    }

    public static string Cell(FemDofDiagnostic dof, FemStabilityReport report) => dof.State switch
    {
        FemDofState.Inactive => Inactive,
        FemDofState.Restrained => Restrained,
        FemDofState.Infinite => Infinity,
        FemDofState.Estimated => Digits(dof.DigitsLost!.Value, report.WarningDigits, report.SevereDigits),
        _ => Unavailable,
    };

    private static string Kind(FemDofDiagnostic dof, FemStabilityReport report) => dof.Condition switch
    {
        StabilityCondition.NoStiffness => $"Singular ({Infinity}) — no stiffness and no restraint",
        StabilityCondition.SlackCablesOnly => $"Singular ({Infinity}) — held by slack cables only",
        StabilityCondition.ZeroStiffnessMode => $"Singular ({Infinity}) — possible mechanism",
        StabilityCondition.NegativePivot => "Not positive definite (negative pivot)",
        _ when dof.Severity == StabilitySeverity.Severe => report.State == StabilityState.NearlySingular ? "Nearly singular" : "Ill-conditioned",
        _ => "Numerical accuracy warning",
    };

    /// <summary>What is reported of a joint, movement by movement: <c>UX, RY: Singular (∞) — possible mechanism; UZ: Numerical accuracy warning (7.0 digits)</c>.</summary>
    public static string Remarks(FemJointDiagnostic joint, FemStabilityReport report) => string.Join("; ", joint.Reported
        .GroupBy(dof => Kind(dof, report)).Select(group =>
        {
            var digits = group.Where(dof => dof.DigitsLost is not null).Select(dof => Digits(dof.DigitsLost!.Value, report.WarningDigits, report.SevereDigits)).ToArray();
            return $"{string.Join(", ", group.Select(dof => dof.Dof))}: {group.Key}" + (digits.Length > 0 ? $" ({string.Join(", ", digits)} digits)" : "");
        }));

    /// <summary>One reported degree of freedom in a sentence, for a log.</summary>
    public static string Message(FemDofDiagnostic dof, FemStabilityReport report) =>
        $"{dof.Joint} {dof.Dof}: {Kind(dof, report)}" + (dof.DigitsLost is { } digits ? $" ({Digits(digits, report.WarningDigits, report.SevereDigits)} digits lost)" : "") + ".";

    /// <summary>The state of the stiffness in a few words; "Passed" when nothing is reported.</summary>
    public static string Summary(FemStabilityReport report)
    {
        if (report.Passed) return "Passed";
        string modes = report.ZeroStiffnessModes == 1 ? "1 movement without stiffness" : $"{report.ZeroStiffnessModes} movements without stiffness";
        return report.State switch
        {
            StabilityState.Singular => $"Singular stiffness — {modes}",
            StabilityState.NearlySingular => "Nearly singular stiffness",
            StabilityState.NotPositiveDefinite => "Stiffness not positive definite",
            _ when report.Instabilities > 0 => "Severe ill-conditioning",
            _ => "Numerical accuracy warning",
        };
    }

    public static string? Largest(FemStabilityReport report) => report.LargestDigitsLost is { } digits && report.LargestJoint is { } joint
        ? $"{Digits(digits, report.WarningDigits, report.SevereDigits)} digits at {joint} {report.LargestDof}" : null;
}
