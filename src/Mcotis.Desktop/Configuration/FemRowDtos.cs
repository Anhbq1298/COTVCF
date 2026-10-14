using Mcotis.Desktop.Application;

namespace Mcotis.Desktop.Configuration;

/// <summary>The load pattern, load case and combination rows of the application as they are saved — one definition for
/// the configuration file and for the document an optimization candidate is analysed as.</summary>
internal static class FemRowDtos
{
    public static FemPatternRowDto Pattern(FemPatternRow row) => new(row.Name, row.Type, row.SelfWeightMultiplier, row.Role, row.Loads);

    /// <summary>A case row with its identity; the case it continues from, or takes its stiffness from, is named here and
    /// resolved to that case's identity where the rows are completed (<see cref="FemCaseSchema"/>). An option that is
    /// the default is left out: the row then says what a row has always said.</summary>
    public static FemDefinitionRowDto Case(FemCaseRow row, IReadOnlyList<FemMassSourceDto> massSources) => new(row.Name,
        // the type as text: a case of the application says what it is; one read from another program keeps
        // that program's word for it, unless Load Case Data gave it another type
        row.Source == "App" || row.Options.Kind != Mcotis.Core.CaseKind.NonlinearStatic ? row.CaseType : row.Type,
        row.Definition, row.InitialCase, row.Run, row.Supported, row.Source, row.Note, row.CableTargetForces)
    {
        Id = row.Id.ToString("D"),
        CaseKind = row.Options.Kind == Mcotis.Core.CaseKind.NonlinearStatic ? null : row.Options.Kind.ToString(),
        GeometricMode = row.Options.GeometricMode == Mcotis.Core.GeometricNonlinearity.None ? null : row.Options.GeometricMode.ToString(),
        StiffnessSourceCase = row.Options.StiffnessSourceCase,
        NonlinearControls = row.Options.Controls is { } controls
            ? new(controls.MaxLoadSteps, controls.MaxIterationsPerStep, controls.RelativeResidualTolerance, controls.RelativeDisplacementTolerance) : null,
        Modal = row.Options.MassSource is { } mass
            ? new(mass, massSources.FirstOrDefault(source => source.Name.Equals(mass, StringComparison.OrdinalIgnoreCase))?.Id, row.Options.RequestedModes ?? 0) : null,
    };

    public static FemDefinitionRowDto Combination(FemCombinationRow row) =>
        new(row.Name, row.Type, row.Definition, null, row.Run, row.Supported, row.Source, row.Note) { Id = row.Id.ToString("D") };
}
