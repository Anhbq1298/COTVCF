using System.Collections.Immutable;

namespace Mcotis.Core;

internal sealed record DocumentData(
    ImmutableDictionary<ObjectId, PointObject> Points,
    ImmutableDictionary<ObjectId, FrameObject> Frames,
    ImmutableDictionary<ObjectId, CableObject> Cables,
    ImmutableDictionary<ObjectId, ShellObject> Shells,
    ImmutableDictionary<ObjectId, MaterialDefinition> Materials,
    ImmutableDictionary<ObjectId, FrameSectionDefinition> FrameSections,
    ImmutableDictionary<ObjectId, CableSectionDefinition> CableSections,
    ImmutableDictionary<ObjectId, ShellSectionDefinition> ShellSections,
    ImmutableDictionary<ObjectId, LoadPattern> LoadPatterns,
    ImmutableDictionary<ObjectId, LoadCase> LoadCases,
    ImmutableDictionary<ObjectId, LoadCombination> LoadCombinations,
    ImmutableDictionary<ObjectId, Restraint> Restraints,
    ImmutableDictionary<ObjectId, FrameAssignment> FrameAssignments,
    ImmutableDictionary<ObjectId, CableAssignment> CableAssignments,
    ImmutableDictionary<ObjectId, ShellAssignment> ShellAssignments,
    ImmutableDictionary<ObjectId, LoadAssignment> Loads,
    AnalysisSettings Settings,
    ImmutableDictionary<ObjectId, MassSource> MassSources,
    ImmutableDictionary<ObjectId, JointMassAssignment> JointMassAssignments,
    ImmutableSortedDictionary<string, string> ApplicationData,
    GeneratorOrigin? Generator)
{
    public static DocumentData Empty { get; } = new(
        ImmutableDictionary<ObjectId, PointObject>.Empty, ImmutableDictionary<ObjectId, FrameObject>.Empty,
        ImmutableDictionary<ObjectId, CableObject>.Empty, ImmutableDictionary<ObjectId, ShellObject>.Empty,
        ImmutableDictionary<ObjectId, MaterialDefinition>.Empty, ImmutableDictionary<ObjectId, FrameSectionDefinition>.Empty,
        ImmutableDictionary<ObjectId, CableSectionDefinition>.Empty, ImmutableDictionary<ObjectId, ShellSectionDefinition>.Empty,
        ImmutableDictionary<ObjectId, LoadPattern>.Empty, ImmutableDictionary<ObjectId, LoadCase>.Empty,
        ImmutableDictionary<ObjectId, LoadCombination>.Empty, ImmutableDictionary<ObjectId, Restraint>.Empty,
        ImmutableDictionary<ObjectId, FrameAssignment>.Empty, ImmutableDictionary<ObjectId, CableAssignment>.Empty,
        ImmutableDictionary<ObjectId, ShellAssignment>.Empty, ImmutableDictionary<ObjectId, LoadAssignment>.Empty,
        AnalysisSettings.Default, ImmutableDictionary<ObjectId, MassSource>.Empty,
        ImmutableDictionary<ObjectId, JointMassAssignment>.Empty, ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal), null);
}

/// <summary>The engineering model: every object, definition, assignment and load of one structure, and nothing of
/// the user interface. A document never changes — an edit (<see cref="Edit"/>) produces a new one — so a document
/// handed to an analysis, a save or an undo step stays exactly what it was. Every document that exists has passed
/// <see cref="DocumentValidator"/>.</summary>
public sealed class ModelDocument
{
    private readonly Lazy<string> analysisInputHash;

    internal ModelDocument(ObjectId documentId, DocumentData data, NameIndex names)
    {
        DocumentId = documentId;
        Data = data;
        Names = names;
        analysisInputHash = new(() => AnalysisInputHasher.Compute(this));
    }

    /// <summary>A new, empty document.</summary>
    public static ModelDocument Create(ObjectId? documentId = null)
    {
        var id = documentId ?? ObjectId.New();
        if (id.IsEmpty) throw new ArgumentException("A document id cannot be empty.", nameof(documentId));
        return new(id, DocumentData.Empty, NameIndex.Empty);
    }

    internal DocumentData Data { get; }

    /// <summary>Identity of the model across files and revisions; results refer to it.</summary>
    public ObjectId DocumentId { get; }
    public NameIndex Names { get; }
    public ImmutableDictionary<ObjectId, PointObject> Points => Data.Points;
    public ImmutableDictionary<ObjectId, FrameObject> Frames => Data.Frames;
    public ImmutableDictionary<ObjectId, CableObject> Cables => Data.Cables;
    public ImmutableDictionary<ObjectId, ShellObject> Shells => Data.Shells;
    public ImmutableDictionary<ObjectId, MaterialDefinition> Materials => Data.Materials;
    public ImmutableDictionary<ObjectId, FrameSectionDefinition> FrameSections => Data.FrameSections;
    public ImmutableDictionary<ObjectId, CableSectionDefinition> CableSections => Data.CableSections;
    public ImmutableDictionary<ObjectId, ShellSectionDefinition> ShellSections => Data.ShellSections;
    public ImmutableDictionary<ObjectId, LoadPattern> LoadPatterns => Data.LoadPatterns;
    public ImmutableDictionary<ObjectId, LoadCase> LoadCases => Data.LoadCases;
    public ImmutableDictionary<ObjectId, LoadCombination> LoadCombinations => Data.LoadCombinations;
    /// <summary>The masses modal cases read (<see cref="ModalEigenOptions.MassSourceId"/>).</summary>
    public ImmutableDictionary<ObjectId, MassSource> MassSources => Data.MassSources;
    /// <summary>Additional translational masses and rotational inertias, keyed by point identity.</summary>
    public ImmutableDictionary<ObjectId, JointMassAssignment> JointMassAssignments => Data.JointMassAssignments;
    /// <summary>Points with a restraint; a point without an entry is free.</summary>
    public ImmutableDictionary<ObjectId, Restraint> Restraints => Data.Restraints;
    /// <summary>Frames with an assignment other than <see cref="FrameAssignment.Default"/>.</summary>
    public ImmutableDictionary<ObjectId, FrameAssignment> FrameAssignments => Data.FrameAssignments;
    public ImmutableDictionary<ObjectId, CableAssignment> CableAssignments => Data.CableAssignments;
    public ImmutableDictionary<ObjectId, ShellAssignment> ShellAssignments => Data.ShellAssignments;
    public ImmutableDictionary<ObjectId, LoadAssignment> Loads => Data.Loads;
    public AnalysisSettings Settings => Data.Settings;

    /// <summary>Data of an application kept with the model, by key, as text: what the application holds of a model that the
    /// model itself has no place for yet (the main window's configuration while it moves onto the document). Saved and
    /// undone with the document; no part of the analysis input, and never read by an analysis.</summary>
    public ImmutableSortedDictionary<string, string> ApplicationData => Data.ApplicationData;

    /// <summary>The parametric generator the model was built by, or null (<see cref="GeneratorOrigin"/>).</summary>
    public GeneratorOrigin? Generator => Data.Generator;

    /// <summary>Identifies the data an analysis reads: everything but names and origins. Renaming an object leaves it
    /// as it is; moving a point, a load, a stiffness or a restraint changes it. Results are current while it matches.</summary>
    public string AnalysisInputHash => analysisInputHash.Value;

    /// <summary>A working copy to change. The document itself stays as it is; <see cref="DocumentEditor.Commit"/> returns the new one.</summary>
    public DocumentEditor Edit() => new(this);

    public Restraint RestraintOf(ObjectId pointId) => Data.Restraints.TryGetValue(pointId, out var restraint) ? restraint : Restraint.Free;
    public FrameAssignment AssignmentOfFrame(ObjectId frameId) => Data.FrameAssignments.TryGetValue(frameId, out var assignment) ? assignment : FrameAssignment.Default;
    public CableAssignment AssignmentOfCable(ObjectId cableId) => Data.CableAssignments.TryGetValue(cableId, out var assignment) ? assignment : CableAssignment.Default;
    public ShellAssignment AssignmentOfShell(ObjectId shellId) => Data.ShellAssignments.TryGetValue(shellId, out var assignment) ? assignment : ShellAssignment.Default;

    /// <summary>The kind and name of whatever named object has this id.</summary>
    public bool TryGetNamed(ObjectId id, out ObjectKind kind, out string name)
    {
        INamedObject? found =
            Data.Points.TryGetValue(id, out var point) ? point
            : Data.Frames.TryGetValue(id, out var frame) ? frame
            : Data.Cables.TryGetValue(id, out var cable) ? cable
            : Data.Shells.TryGetValue(id, out var shell) ? shell
            : Data.Materials.TryGetValue(id, out var material) ? material
            : Data.FrameSections.TryGetValue(id, out var frameSection) ? frameSection
            : Data.CableSections.TryGetValue(id, out var cableSection) ? cableSection
            : Data.ShellSections.TryGetValue(id, out var shellSection) ? shellSection
            : Data.LoadPatterns.TryGetValue(id, out var pattern) ? pattern
            : Data.LoadCases.TryGetValue(id, out var loadCase) ? loadCase
            : Data.LoadCombinations.TryGetValue(id, out var combination) ? combination
            : Data.MassSources.TryGetValue(id, out var massSource) ? massSource
            : null;
        kind = found switch
        {
            PointObject => ObjectKind.Point, FrameObject => ObjectKind.Frame, CableObject => ObjectKind.Cable, ShellObject => ObjectKind.Shell,
            MaterialDefinition => ObjectKind.Material, FrameSectionDefinition => ObjectKind.FrameSection,
            CableSectionDefinition => ObjectKind.CableSection, ShellSectionDefinition => ObjectKind.ShellSection,
            LoadPattern => ObjectKind.LoadPattern, LoadCase => ObjectKind.LoadCase, MassSource => ObjectKind.MassSource,
            _ => ObjectKind.LoadCombination,
        };
        name = found?.Name ?? "";
        return found is not null;
    }

    /// <summary>The object (point, frame, cable or shell) with this name, compared as names are: trimmed, any case.</summary>
    public bool TryFindObject(string? name, out ObjectId id) => Names.TryGet(NameScope.Object, name, out id);
}
