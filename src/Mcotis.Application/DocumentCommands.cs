using Mcotis.Core;

namespace Mcotis.Application;

/// <summary>The commands of the model. A caller that creates something passes the id it will have, so it knows the new
/// object without asking the command for a result. <see cref="Edit"/> wraps a larger edit — a generated geometry, an
/// import — into one undoable step.</summary>
public static class DocumentCommands
{
    public static IDocumentCommand Edit(string name, Action<DocumentEditor> edit) => new DocumentCommand(name, edit);

    // ---- Objects ----
    public static IDocumentCommand AddPoint(ObjectId id, Xyz position, string? name = null) =>
        new DocumentCommand("Add Point", editor => editor.AddPoint(position, name, id));

    public static IDocumentCommand AddFrame(ObjectId id, ObjectId startPointId, ObjectId endPointId, string? name = null) =>
        new DocumentCommand("Add Frame", editor => editor.AddFrame(startPointId, endPointId, name, id));

    public static IDocumentCommand AddCable(ObjectId id, ObjectId startPointId, ObjectId endPointId, string? name = null) =>
        new DocumentCommand("Add Cable", editor => editor.AddCable(startPointId, endPointId, name, id));

    public static IDocumentCommand AddShell(ObjectId id, IReadOnlyList<ObjectId> pointIds, string? name = null) =>
        new DocumentCommand("Add Shell", editor => editor.AddShell(pointIds, name, id));

    public static IDocumentCommand Remove(IReadOnlyCollection<ObjectId> ids) => new DocumentCommand("Delete", editor => editor.Remove(ids));

    public static IDocumentCommand Rename(ObjectId id, string name) => new DocumentCommand("Rename", editor => editor.Rename(id, name));

    public static IDocumentCommand Rename(IReadOnlyDictionary<ObjectId, string> names) => new DocumentCommand("Rename Objects", editor => editor.Rename(names));

    public static IDocumentCommand MovePoints(IReadOnlyDictionary<ObjectId, Xyz> positions) => new DocumentCommand("Move Points", editor =>
    {
        foreach (var (id, position) in positions) editor.MovePoint(id, position);
    });

    public static IDocumentCommand MovePoints(IReadOnlyCollection<ObjectId> pointIds, Xyz offset) => new DocumentCommand("Move Points", editor =>
    {
        foreach (var id in pointIds) editor.MovePoint(id, editor.Points.TryGetValue(id, out var point) ? point.Position + offset : offset);
    });

    public static IDocumentCommand Duplicate(IReadOnlyCollection<ObjectId> ids, Xyz offset) => new DocumentCommand("Duplicate", editor => editor.Duplicate(ids, offset));

    // ---- Assignments ----
    public static IDocumentCommand AssignRestraint(IReadOnlyCollection<ObjectId> pointIds, Restraint restraint) =>
        new DocumentCommand("Assign Restraints", editor => editor.SetRestraint(pointIds, restraint));

    public static IDocumentCommand AssignFrameSection(IReadOnlyCollection<ObjectId> frameIds, ObjectId? sectionId) =>
        new DocumentCommand("Assign Frame Section", editor => editor.SetFrameSection(frameIds, sectionId));

    public static IDocumentCommand AssignFrameReleases(IReadOnlyCollection<ObjectId> frameIds, EndReleases releases) =>
        new DocumentCommand("Assign Frame Releases", editor => editor.SetFrameReleases(frameIds, releases));

    public static IDocumentCommand AssignFrameLocalAxis(IReadOnlyCollection<ObjectId> frameIds, double angle) =>
        new DocumentCommand("Assign Frame Local Axes", editor => editor.SetFrameLocalAxis(frameIds, angle));

    public static IDocumentCommand AssignFrameModifiers(IReadOnlyCollection<ObjectId> frameIds, FrameModifiers modifiers) =>
        new DocumentCommand("Assign Frame Modifiers", editor => editor.SetFrameModifiers(frameIds, modifiers));

    public static IDocumentCommand AssignCableSection(IReadOnlyCollection<ObjectId> cableIds, ObjectId? sectionId) =>
        new DocumentCommand("Assign Cable Section", editor => editor.SetCableSection(cableIds, sectionId));

    public static IDocumentCommand AssignCableModifiers(IReadOnlyCollection<ObjectId> cableIds, CableModifiers modifiers) =>
        new DocumentCommand("Assign Cable Modifiers", editor => editor.SetCableModifiers(cableIds, modifiers));

    public static IDocumentCommand AssignJointMass(JointMassAssignment assignment) =>
        new DocumentCommand("Assign Joint Mass", editor => editor.SetJointMass(assignment));

    public static IDocumentCommand AssignShellSection(IReadOnlyCollection<ObjectId> shellIds, ObjectId? sectionId) =>
        new DocumentCommand("Assign Shell Section", editor => editor.SetShellSection(shellIds, sectionId));

    public static IDocumentCommand AssignShellLocalAxis(IReadOnlyCollection<ObjectId> shellIds, double angle) =>
        new DocumentCommand("Assign Shell Local Axes", editor => editor.SetShellLocalAxis(shellIds, angle));

    public static IDocumentCommand PutLoad(LoadAssignment load) => new DocumentCommand("Assign Load", editor => editor.PutLoad(load));

    /// <summary>Assign → Frame → Frame Load: one definition on several frames, added to, replacing or deleting the
    /// distributed loads of its pattern (<see cref="FrameLoads.Assign"/>). One step of the history.</summary>
    public static IDocumentCommand AssignFrameDistributedLoad(IReadOnlyCollection<ObjectId> frameIds,
        FrameDistributedLoadDefinition definition, LoadAssignMode mode) =>
        new DocumentCommand("Assign Frame Distributed Loads", editor => FrameLoads.Assign(editor, frameIds, definition, mode));

    // ---- Definitions: add, or replace the one with the same id ----
    public static IDocumentCommand PutMaterial(MaterialDefinition material) => new DocumentCommand("Define Material", editor => editor.PutMaterial(material));
    public static IDocumentCommand PutFrameSection(FrameSectionDefinition section) => new DocumentCommand("Define Frame Section", editor => editor.PutFrameSection(section));
    public static IDocumentCommand PutCableSection(CableSectionDefinition section) => new DocumentCommand("Define Cable Section", editor => editor.PutCableSection(section));
    public static IDocumentCommand PutShellSection(ShellSectionDefinition section) => new DocumentCommand("Define Shell Section", editor => editor.PutShellSection(section));
    public static IDocumentCommand PutLoadPattern(LoadPattern pattern) => new DocumentCommand("Define Load Pattern", editor => editor.PutLoadPattern(pattern));
    public static IDocumentCommand PutLoadCase(LoadCase loadCase) => new DocumentCommand("Define Load Case", editor => editor.PutLoadCase(loadCase));
    public static IDocumentCommand PutLoadCombination(LoadCombination combination) => new DocumentCommand("Define Load Combination", editor => editor.PutLoadCombination(combination));
    public static IDocumentCommand SetSettings(AnalysisSettings settings) => new DocumentCommand("Analysis Settings", editor => editor.SetSettings(settings));
}
