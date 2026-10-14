using Mcotis.Core;

namespace Mcotis.Persistence;

// The shape of a .mcotis file, schema 2. Every member a file must carry is `required`: a missing value is an error
// when the file is read, never a silent zero. A change of this shape is a new schema version with its migration.

internal sealed class FileDto
{
    public required string Format { get; init; }
    public required int SchemaVersion { get; init; }
    public required string DocumentId { get; init; }
    public required DateTime SavedUtc { get; init; }
    public required UnitsDto Units { get; init; }
    public required SettingsDto Settings { get; init; }
    /// <summary>Per kind of object ("Point", "Frame", "Cable" …), the highest number its automatic names have reached.
    /// It is saved because deleted objects leave no trace of it: with it, the sequence continues after Open.</summary>
    public required Dictionary<string, int> NameSequence { get; init; }
    public required List<MaterialDto> Materials { get; init; }
    public required List<FrameSectionDto> FrameSections { get; init; }
    public required List<CableSectionDto> CableSections { get; init; }
    public required List<ShellSectionDto> ShellSections { get; init; }
    public required List<PointDto> Points { get; init; }
    public required List<FrameDto> Frames { get; init; }
    public required List<CableDto> Cables { get; init; }
    public required List<ShellDto> Shells { get; init; }
    public required List<RestraintDto> Restraints { get; init; }
    public required List<FrameAssignmentDto> FrameAssignments { get; init; }
    public required List<CableAssignmentDto> CableAssignments { get; init; }
    public required List<ShellAssignmentDto> ShellAssignments { get; init; }
    public required List<LoadPatternDto> LoadPatterns { get; init; }
    public required List<LoadDto> Loads { get; init; }
    public required List<LoadCaseDto> LoadCases { get; init; }
    public required List<LoadCombinationDto> LoadCombinations { get; init; }
    /// <summary>Schema 4. The masses modal cases read.</summary>
    public required List<MassSourceDto> MassSources { get; init; }
    /// <summary>Schema 5. Additional inertia assigned to joints.</summary>
    public required List<JointMassDto> JointMassAssignments { get; init; }
}

internal sealed class UnitsDto
{
    public required string Force { get; init; }
    public required string Length { get; init; }
    public required string Mass { get; init; }
}

internal sealed class SettingsDto
{
    public required DofMask ActiveDofs { get; init; }
    public required DofMask CableDofs { get; init; }
    public required double CableFitTolerancePercent { get; init; }
    public required int CableFitMaxIterations { get; init; }
    public required int FrameMeshSegments { get; init; }
    public required bool CableSag { get; init; }
}

internal sealed class OriginDto
{
    public required OriginKind Kind { get; init; }
    public string? GenerationKey { get; init; }
    public string? SourceProduct { get; init; }
    public string? SourceType { get; init; }
    public string? SourceName { get; init; }
}

internal sealed class VectorDto
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Z { get; init; }
}

internal sealed class PointDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Z { get; init; }
    public OriginDto? Origin { get; init; }
}

internal sealed class FrameDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string StartPointId { get; init; }
    public required string EndPointId { get; init; }
    public OriginDto? Origin { get; init; }
}

internal sealed class CableDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string StartPointId { get; init; }
    public required string EndPointId { get; init; }
    public OriginDto? Origin { get; init; }
}

internal sealed class ShellDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required List<string> PointIds { get; init; }
    public OriginDto? Origin { get; init; }
}

internal sealed class MaterialDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required MaterialType Type { get; init; }
    public required double ElasticModulus { get; init; }
    public required double PoissonRatio { get; init; }
    public required double WeightDensity { get; init; }
    public required double MassDensity { get; init; }
}

internal sealed class FrameSectionDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string MaterialId { get; init; }
    public required FrameSectionShape Shape { get; init; }
    public required double Area { get; init; }
    public required double Inertia33 { get; init; }
    public required double Inertia22 { get; init; }
    public required double TorsionConstant { get; init; }
    public required double ShearArea2 { get; init; }
    public required double ShearArea3 { get; init; }
    public required Dictionary<string, double> Dimensions { get; init; }
}

internal sealed class CableSectionDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string MaterialId { get; init; }
    public required double Area { get; init; }
}

internal sealed class ShellSectionDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string MaterialId { get; init; }
    public required ShellSectionType Type { get; init; }
    public required double Thickness { get; init; }
}

internal sealed class RestraintDto
{
    public required string PointId { get; init; }
    public required bool Ux { get; init; }
    public required bool Uy { get; init; }
    public required bool Uz { get; init; }
    public required bool Rx { get; init; }
    public required bool Ry { get; init; }
    public required bool Rz { get; init; }
}

internal sealed class ReleaseDto
{
    public required bool Axial { get; init; }
    public required bool Shear2 { get; init; }
    public required bool Shear3 { get; init; }
    public required bool Torsion { get; init; }
    public required bool Moment2 { get; init; }
    public required bool Moment3 { get; init; }
}

internal sealed class ModifiersDto
{
    public required double Area { get; init; }
    public required double Inertia33 { get; init; }
    public required double Inertia22 { get; init; }
    public required double Torsion { get; init; }
    public required double Mass { get; init; }
    public required double Weight { get; init; }
}

internal sealed class FrameAssignmentDto
{
    public required string FrameId { get; init; }
    public string? SectionId { get; init; }
    public required ReleaseDto StartRelease { get; init; }
    public required ReleaseDto EndRelease { get; init; }
    public required double LocalAxisAngle { get; init; }
    public required ModifiersDto Modifiers { get; init; }
}

internal sealed class CableAssignmentDto
{
    public required string CableId { get; init; }
    public string? SectionId { get; init; }
    public required double AreaModifier { get; init; }
    public required double MassModifier { get; init; }
    public required double WeightModifier { get; init; }
}

internal sealed class ShellAssignmentDto
{
    public required string ShellId { get; init; }
    public string? SectionId { get; init; }
    public required double LocalAxisAngle { get; init; }
}

internal sealed class LoadPatternDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required LoadPatternType Type { get; init; }
    public required double SelfWeightMultiplier { get; init; }
}

/// <summary>One load. <see cref="Type"/> says which of the optional members it carries.</summary>
internal sealed class LoadDto
{
    public const string Point = "point", FrameDistributed = "frameDistributed", FramePoint = "framePoint",
        CableTargetForce = "cableTargetForce", ShellUniform = "shellUniform";

    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string PatternId { get; init; }
    public required string TargetId { get; init; }
    public VectorDto? Force { get; init; }
    public VectorDto? Moment { get; init; }
    public LoadDirection? Direction { get; init; }
    public double? RelativeStart { get; init; }
    public double? RelativeEnd { get; init; }
    public double? ValueStart { get; init; }
    public double? ValueEnd { get; init; }
    public double? RelativeDistance { get; init; }
    public double? Value { get; init; }
}

internal sealed class ComponentDto
{
    public required string PatternId { get; init; }
    public required double Factor { get; init; }
}

/// <summary>Schema 4. A case states its kind and, by that kind, exactly one of two references to another case: a
/// nonlinear static or initial P-Delta case its <see cref="InitialState"/>, a linear static or modal case its
/// <see cref="StiffnessSource"/>. The one that does not apply is absent, and the reader refuses a case that carries it.</summary>
internal sealed class LoadCaseDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required CaseKind Kind { get; init; }
    public required List<ComponentDto> Components { get; init; }
    public required bool Run { get; init; }
    public required bool FitCableTargetForces { get; init; }
    public required GeometricNonlinearity GeometricMode { get; init; }
    public InitialStateDto? InitialState { get; init; }
    public StiffnessSourceDto? StiffnessSource { get; init; }
    public NonlinearControlsDto? NonlinearControls { get; init; }
    public ModalDto? Modal { get; init; }
}

internal sealed class InitialStateDto
{
    public required InitialStateKind Kind { get; init; }
    /// <summary>With <see cref="InitialStateKind.EndOfNonlinearCase"/> only: the case that is continued, by its id.</summary>
    public string? CaseId { get; init; }
}

internal sealed class StiffnessSourceDto
{
    public required StiffnessSourceKind Kind { get; init; }
    /// <summary>With <see cref="StiffnessSourceKind.EndOfNonlinearCase"/> only: the case whose end stiffness is used, by its id.</summary>
    public string? CaseId { get; init; }
}

internal sealed class NonlinearControlsDto
{
    public required int MaxLoadSteps { get; init; }
    public required int MaxIterationsPerStep { get; init; }
    public required double RelativeResidualTolerance { get; init; }
    public required double RelativeDisplacementTolerance { get; init; }
}

internal sealed class ModalDto
{
    public required string MassSourceId { get; init; }
    public required int RequestedModes { get; init; }
    public required ModalMassMatrixKind MassMatrixKind { get; init; }
}

internal sealed class MassSourceDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool IncludeSelfMass { get; init; }
    public required bool IncludeAdditionalMass { get; init; }
}

internal sealed class JointMassDto
{
    public required string PointId { get; init; }
    public required double UX { get; init; }
    public required double UY { get; init; }
    public required double UZ { get; init; }
    public required double RX { get; init; }
    public required double RY { get; init; }
    public required double RZ { get; init; }
}

internal sealed class TermDto
{
    public required string SourceId { get; init; }
    public required double Factor { get; init; }
}

internal sealed class LoadCombinationDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required CombinationType Type { get; init; }
    public required List<TermDto> Terms { get; init; }
    public required bool Run { get; init; }
}
