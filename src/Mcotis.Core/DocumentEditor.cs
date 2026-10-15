using System.Collections.Immutable;

namespace Mcotis.Core;

/// <summary>The result of an edit: the new document and what changed against the one the edit started from.</summary>
public sealed record DocumentCommit(ModelDocument Document, DocumentChangeSet Changes);

/// <summary>The only way to change a document. An editor is a private working copy: nothing reaches a document until
/// <see cref="Commit"/> has validated the whole result, and an editor that refused an operation is discarded — the
/// document it started from is never touched, so an edit either happens completely or not at all. Names are checked
/// and issued here, through <see cref="NamingService"/>.</summary>
public sealed class DocumentEditor
{
    private readonly ModelDocument source;
    private readonly ImmutableDictionary<ObjectId, PointObject>.Builder points;
    private readonly ImmutableDictionary<ObjectId, FrameObject>.Builder frames;
    private readonly ImmutableDictionary<ObjectId, CableObject>.Builder cables;
    private readonly ImmutableDictionary<ObjectId, ShellObject>.Builder shells;
    private readonly ImmutableDictionary<ObjectId, MaterialDefinition>.Builder materials;
    private readonly ImmutableDictionary<ObjectId, FrameSectionDefinition>.Builder frameSections;
    private readonly ImmutableDictionary<ObjectId, CableSectionDefinition>.Builder cableSections;
    private readonly ImmutableDictionary<ObjectId, ShellSectionDefinition>.Builder shellSections;
    private readonly ImmutableDictionary<ObjectId, LoadPattern>.Builder loadPatterns;
    private readonly ImmutableDictionary<ObjectId, LoadCase>.Builder loadCases;
    private readonly ImmutableDictionary<ObjectId, LoadCombination>.Builder loadCombinations;
    private readonly ImmutableDictionary<ObjectId, MassSource>.Builder massSources;
    private readonly ImmutableDictionary<ObjectId, JointMassAssignment>.Builder jointMassAssignments;
    private readonly ImmutableDictionary<ObjectId, Restraint>.Builder restraints;
    private readonly ImmutableDictionary<ObjectId, FrameAssignment>.Builder frameAssignments;
    private readonly ImmutableDictionary<ObjectId, CableAssignment>.Builder cableAssignments;
    private readonly ImmutableDictionary<ObjectId, ShellAssignment>.Builder shellAssignments;
    private readonly ImmutableDictionary<ObjectId, LoadAssignment>.Builder loads;
    private readonly Dictionary<NameScope, ImmutableDictionary<string, ObjectId>.Builder> names = [];
    private readonly ImmutableDictionary<ObjectKind, int>.Builder sequence;
    private readonly ChangeRecorder changes = new();
    private AnalysisSettings settings;
    private ImmutableSortedDictionary<string, string>.Builder applicationData;
    private GeneratorOrigin? generator;
    private ReferenceGeometry referenceGeometry;
    private bool closed, faulted, sequenceRestored;

    internal DocumentEditor(ModelDocument source)
    {
        this.source = source;
        var data = source.Data;
        points = data.Points.ToBuilder();
        frames = data.Frames.ToBuilder();
        cables = data.Cables.ToBuilder();
        shells = data.Shells.ToBuilder();
        materials = data.Materials.ToBuilder();
        frameSections = data.FrameSections.ToBuilder();
        cableSections = data.CableSections.ToBuilder();
        shellSections = data.ShellSections.ToBuilder();
        loadPatterns = data.LoadPatterns.ToBuilder();
        loadCases = data.LoadCases.ToBuilder();
        loadCombinations = data.LoadCombinations.ToBuilder();
        massSources = data.MassSources.ToBuilder();
        jointMassAssignments = data.JointMassAssignments.ToBuilder();
        restraints = data.Restraints.ToBuilder();
        frameAssignments = data.FrameAssignments.ToBuilder();
        cableAssignments = data.CableAssignments.ToBuilder();
        shellAssignments = data.ShellAssignments.ToBuilder();
        loads = data.Loads.ToBuilder();
        sequence = source.Names.Sequence.ToBuilder();
        settings = data.Settings;
        applicationData = data.ApplicationData.ToBuilder();
        generator = data.Generator;
        referenceGeometry = data.ReferenceGeometry;
    }

    // ---- The working state, for an edit that reads what it has built so far ----
    public IReadOnlyDictionary<ObjectId, PointObject> Points => points;
    public IReadOnlyDictionary<ObjectId, FrameObject> Frames => frames;
    public IReadOnlyDictionary<ObjectId, CableObject> Cables => cables;
    public IReadOnlyDictionary<ObjectId, ShellObject> Shells => shells;
    public IReadOnlyDictionary<ObjectId, MaterialDefinition> Materials => materials;
    public IReadOnlyDictionary<ObjectId, FrameSectionDefinition> FrameSections => frameSections;
    public IReadOnlyDictionary<ObjectId, CableSectionDefinition> CableSections => cableSections;
    public IReadOnlyDictionary<ObjectId, ShellSectionDefinition> ShellSections => shellSections;
    public IReadOnlyDictionary<ObjectId, LoadPattern> LoadPatterns => loadPatterns;
    public IReadOnlyDictionary<ObjectId, LoadCase> LoadCases => loadCases;
    public IReadOnlyDictionary<ObjectId, LoadCombination> LoadCombinations => loadCombinations;
    public IReadOnlyDictionary<ObjectId, MassSource> MassSources => massSources;
    public IReadOnlyDictionary<ObjectId, JointMassAssignment> JointMassAssignments => jointMassAssignments;
    public IReadOnlyDictionary<ObjectId, LoadAssignment> Loads => loads;
    public AnalysisSettings Settings => settings;
    public IReadOnlyDictionary<string, string> ApplicationData => applicationData;
    public GeneratorOrigin? Generator => generator;
    public ReferenceGeometry ReferenceGeometry => referenceGeometry;

    public Restraint RestraintOf(ObjectId pointId) => restraints.TryGetValue(pointId, out var restraint) ? restraint : Restraint.Free;
    public FrameAssignment AssignmentOfFrame(ObjectId frameId) => frameAssignments.TryGetValue(frameId, out var assignment) ? assignment : FrameAssignment.Default;
    public CableAssignment AssignmentOfCable(ObjectId cableId) => cableAssignments.TryGetValue(cableId, out var assignment) ? assignment : CableAssignment.Default;
    public ShellAssignment AssignmentOfShell(ObjectId shellId) => shellAssignments.TryGetValue(shellId, out var assignment) ? assignment : ShellAssignment.Default;

    /// <summary>The holder of a name in the working state.</summary>
    public bool TryFind(ObjectKind kind, string? name, out ObjectId id)
    {
        id = default;
        return NamingService.TryNormalize(name, out var normalized) && Scope(kind).TryGetValue(normalized, out id);
    }

    /// <summary>The automatic name the next new object of this kind gets: the one after the highest its sequence has
    /// reached (P1, P2 … F, C, S …). The sequence only moves forward — deleting an object neither renames another nor
    /// frees its number, so the next name never fills a gap a deleted object left.</summary>
    public string NextName(ObjectKind kind)
    {
        var next = sequence.GetValueOrDefault(kind) + 1;
        return NamingService.NextFree(NamingService.Prefix(kind), Scope(kind).ContainsKey, ref next);
    }

    /// <summary>States that the names of this kind have reached at least this number — what a file hands back when it
    /// is opened, so the sequence continues where it stopped. It never lowers the sequence.</summary>
    public void RaiseNameSequence(ObjectKind kind, int number)
    {
        Open();
        if (!NamingService.Kinds.Contains(kind) || number < 0) throw Fail(DocumentError.InvalidName, $"{kind} {number} is not a place in a name sequence.");
        if (number <= sequence.GetValueOrDefault(kind)) return;
        sequence[kind] = number;
        sequenceRestored = true;
    }

    // ---- Objects ----

    public PointObject AddPoint(Xyz position, string? name = null, ObjectId? id = null, ObjectOrigin? origin = null)
    {
        Open();
        var newId = NewId(id);
        var point = new PointObject(newId, ClaimNew(ObjectKind.Point, name, newId), position) { Origin = origin };
        points[newId] = point;
        changes.Record(newId, ObjectKind.Point, ChangeAspects.Added);
        return point;
    }

    public FrameObject AddFrame(ObjectId startPointId, ObjectId endPointId, string? name = null, ObjectId? id = null, ObjectOrigin? origin = null)
    {
        Open();
        RequirePoint(startPointId);
        RequirePoint(endPointId);
        var newId = NewId(id);
        var frame = new FrameObject(newId, ClaimNew(ObjectKind.Frame, name, newId), startPointId, endPointId) { Origin = origin };
        frames[newId] = frame;
        changes.Record(newId, ObjectKind.Frame, ChangeAspects.Added);
        return frame;
    }

    public CableObject AddCable(ObjectId startPointId, ObjectId endPointId, string? name = null, ObjectId? id = null, ObjectOrigin? origin = null)
    {
        Open();
        RequirePoint(startPointId);
        RequirePoint(endPointId);
        var newId = NewId(id);
        var cable = new CableObject(newId, ClaimNew(ObjectKind.Cable, name, newId), startPointId, endPointId) { Origin = origin };
        cables[newId] = cable;
        changes.Record(newId, ObjectKind.Cable, ChangeAspects.Added);
        return cable;
    }

    public ShellObject AddShell(IEnumerable<ObjectId> pointIds, string? name = null, ObjectId? id = null, ObjectOrigin? origin = null)
    {
        Open();
        var boundary = pointIds.ToImmutableArray();
        foreach (var pointId in boundary) RequirePoint(pointId);
        var newId = NewId(id);
        var shell = new ShellObject(newId, ClaimNew(ObjectKind.Shell, name, newId), boundary) { Origin = origin };
        shells[newId] = shell;
        changes.Record(newId, ObjectKind.Shell, ChangeAspects.Added);
        return shell;
    }

    public void MovePoint(ObjectId pointId, Xyz position)
    {
        Open();
        var point = RequirePoint(pointId);
        if (point.Position == position) return;
        points[pointId] = point with { Position = position };
        changes.Record(pointId, ObjectKind.Point, ChangeAspects.Geometry);
    }

    /// <summary>Copies points, frames, cables and shells with their assignments (not their loads) at an offset. Every
    /// copy has a new id and a new automatic name; a copied frame, cable or shell gets copies of its points. Returns
    /// original → copy.</summary>
    public IReadOnlyDictionary<ObjectId, ObjectId> Duplicate(IEnumerable<ObjectId> objectIds, Xyz offset)
    {
        Open();
        var requested = objectIds.Distinct().ToArray();
        var sourcePoints = new List<ObjectId>();
        foreach (var id in requested)
        {
            if (points.ContainsKey(id)) sourcePoints.Add(id);
            else if (frames.TryGetValue(id, out var frame)) sourcePoints.AddRange([frame.StartPointId, frame.EndPointId]);
            else if (cables.TryGetValue(id, out var cable)) sourcePoints.AddRange([cable.StartPointId, cable.EndPointId]);
            else if (shells.TryGetValue(id, out var shell)) sourcePoints.AddRange(shell.PointIds);
            else throw Fail(DocumentError.NotFound, $"There is no point, frame, cable or shell with id {id} to duplicate.");
        }

        var copies = new Dictionary<ObjectId, ObjectId>();
        foreach (var id in sourcePoints.Distinct())
        {
            var copy = AddPoint(points[id].Position + offset);
            copies[id] = copy.Id;
            if (restraints.TryGetValue(id, out var restraint)) SetRestraint([copy.Id], restraint);
        }
        foreach (var id in requested)
        {
            if (frames.TryGetValue(id, out var frame))
            {
                var copy = AddFrame(copies[frame.StartPointId], copies[frame.EndPointId]);
                copies[id] = copy.Id;
                if (frameAssignments.TryGetValue(id, out var assignment)) frameAssignments[copy.Id] = assignment;
            }
            else if (cables.TryGetValue(id, out var cable))
            {
                var copy = AddCable(copies[cable.StartPointId], copies[cable.EndPointId]);
                copies[id] = copy.Id;
                if (cableAssignments.TryGetValue(id, out var assignment)) cableAssignments[copy.Id] = assignment;
            }
            else if (shells.TryGetValue(id, out var shell))
            {
                var copy = AddShell(shell.PointIds.Select(pointId => copies[pointId]));
                copies[id] = copy.Id;
                if (shellAssignments.TryGetValue(id, out var assignment)) shellAssignments[copy.Id] = assignment;
            }
        }
        return copies;
    }

    // ---- Names ----

    public void Rename(ObjectId id, string name) => Rename(new Dictionary<ObjectId, string> { [id] = name });

    /// <summary>Renames several named objects (of any kind) as one step. The set is judged by its outcome, so two
    /// objects may exchange names; a collision in the outcome refuses the whole set.</summary>
    public void Rename(IReadOnlyDictionary<ObjectId, string> newNames)
    {
        Open();
        var planned = new List<(ObjectId Id, ObjectKind Kind, string Old, string New)>();
        foreach (var (id, requested) in newNames)
        {
            if (!TryLocate(id, out var kind, out var old)) throw Fail(DocumentError.NotFound, $"There is no named object with id {id} to rename.");
            var name = Normalized(requested);
            if (!string.Equals(old, name, StringComparison.Ordinal)) planned.Add((id, kind, old, name));
        }
        foreach (var item in planned) ReleaseName(item.Kind, item.Old);
        foreach (var item in planned) Claim(item.Kind, item.New, item.Id);
        foreach (var item in planned)
        {
            SetName(item.Id, item.Kind, item.New);
            changes.Record(item.Id, item.Kind, ChangeAspects.Name);
        }
    }

    // ---- Removal ----

    /// <summary>Removes objects, definitions and loads. A removed object takes its assignments and loads with it.
    /// Anything still referred to by what remains — a point under a frame, a section on a frame, a pattern in a
    /// load case — refuses the removal.</summary>
    public void Remove(IEnumerable<ObjectId> ids)
    {
        Open();
        var removed = new Dictionary<ObjectId, string>();
        foreach (var id in ids.Distinct())
        {
            if (TryLocate(id, out var kind, out var name))
            {
                removed[id] = $"{Label(kind)} '{name}'";
                RemoveNamed(id, kind, name);
            }
            else if (loads.ContainsKey(id)) RemoveLoad(id);
            else throw Fail(DocumentError.NotFound, $"There is nothing with id {id} to remove.");
        }
        foreach (var load in loads.Values.Where(load => removed.ContainsKey(load.TargetId)).ToArray()) RemoveLoad(load.Id);
        EnsureNotReferenced(removed);
    }

    // ---- Definitions: add, or replace the one with the same id ----

    public void PutMaterial(MaterialDefinition material) => PutNamed(materials, ObjectKind.Material, material, (item, name) => item with { Name = name });
    public void PutFrameSection(FrameSectionDefinition section) => PutNamed(frameSections, ObjectKind.FrameSection, section, (item, name) => item with { Name = name });
    public void PutCableSection(CableSectionDefinition section) => PutNamed(cableSections, ObjectKind.CableSection, section, (item, name) => item with { Name = name });
    public void PutShellSection(ShellSectionDefinition section) => PutNamed(shellSections, ObjectKind.ShellSection, section, (item, name) => item with { Name = name });
    public void PutLoadPattern(LoadPattern pattern) => PutNamed(loadPatterns, ObjectKind.LoadPattern, pattern, (item, name) => item with { Name = name });
    public void PutLoadCase(LoadCase loadCase) => PutNamed(loadCases, ObjectKind.LoadCase, loadCase, (item, name) => item with { Name = name });
    public void PutLoadCombination(LoadCombination combination) => PutNamed(loadCombinations, ObjectKind.LoadCombination, combination, (item, name) => item with { Name = name });
    public void PutMassSource(MassSource massSource) => PutNamed(massSources, ObjectKind.MassSource, massSource, (item, name) => item with { Name = name });

    /// <summary>Sets a point's additional inertia. Six zeros remove the assignment.</summary>
    public void SetJointMass(JointMassAssignment assignment)
    {
        Open();
        RequirePoint(assignment.PointId);
        if (assignment.Values.All(value => value == 0)) jointMassAssignments.Remove(assignment.PointId);
        else jointMassAssignments[assignment.PointId] = assignment;
        changes.Record(assignment.PointId, ObjectKind.Point, ChangeAspects.Assignment);
    }

    public void SetSettings(AnalysisSettings value)
    {
        Open();
        if (settings == value) return;
        settings = value;
        changes.Record(default, ObjectKind.Settings, ChangeAspects.Definition);
    }

    /// <summary>Sets the application data under <paramref name="key"/>; null removes it (<see cref="ModelDocument.ApplicationData"/>).</summary>
    public void SetApplicationData(string key, string? value)
    {
        Open();
        if (string.IsNullOrWhiteSpace(key)) throw Fail(DocumentError.Invalid, "Application data needs a key.");
        if (value is null ? !applicationData.ContainsKey(key) : applicationData.TryGetValue(key, out var held) && held == value) return;
        if (value is null) applicationData.Remove(key); else applicationData[key] = value;
        changes.Record(default, ObjectKind.Settings, ChangeAspects.Definition);
    }

    /// <summary>Sets the generator the model was built by; null detaches it (<see cref="ModelDocument.Generator"/>).</summary>
    public void SetGenerator(GeneratorOrigin? origin)
    {
        Open();
        if (Equals(generator, origin)) return;
        if (origin is not null && (string.IsNullOrWhiteSpace(origin.GeneratorId) || origin.Version < 1))
            throw Fail(DocumentError.Invalid, "A generator needs an id and a version of at least 1.");
        generator = origin;
        changes.Record(default, ObjectKind.Settings, ChangeAspects.Definition);
    }

    /// <summary>Sets the grid systems and levels of the model (<see cref="ModelDocument.ReferenceGeometry"/>) in their
    /// stored form (<see cref="ReferenceGeometry.Normalized"/>). Definitions that are not valid refuse the edit with every
    /// problem; nothing else of the document changes — no point is made or moved by a grid.</summary>
    public void SetReferenceGeometry(ReferenceGeometry value)
    {
        Open();
        ArgumentNullException.ThrowIfNull(value);
        ReferenceGeometry normalized;
        try { normalized = value.Normalized(); }
        catch (DocumentException ex) { throw Fail(ex.Error, ex.Message); }
        if (normalized.Equals(referenceGeometry)) return;
        referenceGeometry = normalized;
        changes.Record(ReferenceGeometry.ChangeId, ObjectKind.ReferenceGeometry, ChangeAspects.Definition);
    }

    /// <summary>
    /// Makes the content of the edit that of <paramref name="target"/> — a document of the same model (the same
    /// <see cref="ModelDocument.DocumentId"/>) — as one edit: every object, definition, assignment, load, setting, name and
    /// the application data, generator and reference geometry. What differs is recorded object by object, so Undo returns to the content before
    /// and a panel refreshes what changed. An application that still keeps a model of its own writes it into its document
    /// this way while it moves onto the document.
    /// </summary>
    public void ReplaceContent(ModelDocument target)
    {
        Open();
        ArgumentNullException.ThrowIfNull(target);
        if (target.DocumentId != source.DocumentId)
            throw Fail(DocumentError.Invalid, "The content of another model cannot replace this one's: the documents differ in identity.");
        void Replace<T>(ImmutableDictionary<ObjectId, T>.Builder builder, ImmutableDictionary<ObjectId, T> next, ObjectKind kind, ChangeAspects changed)
        {
            foreach (var id in builder.Keys.Where(id => !next.ContainsKey(id)).ToArray())
            {
                builder.Remove(id);
                changes.Record(id, kind, ChangeAspects.Removed);
            }
            foreach (var (id, value) in next)
            {
                if (!builder.TryGetValue(id, out var current)) changes.Record(id, kind, ChangeAspects.Added);
                else if (!EqualityComparer<T>.Default.Equals(current, value)) changes.Record(id, kind, changed);
                else continue;
                builder[id] = value;
            }
        }
        var data = target.Data;
        Replace(points, data.Points, ObjectKind.Point, ChangeAspects.Geometry | ChangeAspects.Name);
        Replace(frames, data.Frames, ObjectKind.Frame, ChangeAspects.Geometry | ChangeAspects.Name);
        Replace(cables, data.Cables, ObjectKind.Cable, ChangeAspects.Geometry | ChangeAspects.Name);
        Replace(shells, data.Shells, ObjectKind.Shell, ChangeAspects.Geometry | ChangeAspects.Name);
        Replace(materials, data.Materials, ObjectKind.Material, ChangeAspects.Definition);
        Replace(frameSections, data.FrameSections, ObjectKind.FrameSection, ChangeAspects.Definition);
        Replace(cableSections, data.CableSections, ObjectKind.CableSection, ChangeAspects.Definition);
        Replace(shellSections, data.ShellSections, ObjectKind.ShellSection, ChangeAspects.Definition);
        Replace(loadPatterns, data.LoadPatterns, ObjectKind.LoadPattern, ChangeAspects.Definition);
        Replace(loadCases, data.LoadCases, ObjectKind.LoadCase, ChangeAspects.Definition);
        Replace(loadCombinations, data.LoadCombinations, ObjectKind.LoadCombination, ChangeAspects.Definition);
        Replace(massSources, data.MassSources, ObjectKind.MassSource, ChangeAspects.Definition);
        Replace(loads, data.Loads, ObjectKind.Load, ChangeAspects.Definition);
        Replace(jointMassAssignments, data.JointMassAssignments, ObjectKind.Point, ChangeAspects.Assignment);
        Replace(restraints, data.Restraints, ObjectKind.Point, ChangeAspects.Assignment);
        Replace(frameAssignments, data.FrameAssignments, ObjectKind.Frame, ChangeAspects.Assignment);
        Replace(cableAssignments, data.CableAssignments, ObjectKind.Cable, ChangeAspects.Assignment);
        Replace(shellAssignments, data.ShellAssignments, ObjectKind.Shell, ChangeAspects.Assignment);
        SetSettings(data.Settings);
        foreach (var key in applicationData.Keys.Where(key => !data.ApplicationData.ContainsKey(key)).ToArray()) SetApplicationData(key, null);
        foreach (var (key, value) in data.ApplicationData) SetApplicationData(key, value);
        SetGenerator(data.Generator);
        SetReferenceGeometry(data.ReferenceGeometry);
        // names and the numbering of their series are those of the target
        foreach (var scope in target.Names.Scopes.Keys) names[scope] = target.Names.Scope(scope).ToBuilder();
        foreach (var scope in source.Names.Scopes.Keys.Where(scope => !target.Names.Scopes.ContainsKey(scope))) names[scope] = NameIndex.EmptyScope.ToBuilder();
        sequence.Clear();
        foreach (var (kind, number) in target.Names.Sequence) sequence[kind] = number;
        if (!target.Names.Sequence.SequenceEqual(source.Names.Sequence)) sequenceRestored = true;
    }

    // ---- Assignments ----

    public void SetRestraint(IEnumerable<ObjectId> pointIds, Restraint restraint)
    {
        Open();
        foreach (var id in pointIds)
        {
            RequirePoint(id);
            if (RestraintOf(id) == restraint) continue;
            if (restraint.IsFree) restraints.Remove(id); else restraints[id] = restraint;
            changes.Record(id, ObjectKind.Point, ChangeAspects.Assignment);
        }
    }

    /// <summary>Assigns a frame section, or none with null.</summary>
    public void SetFrameSection(IEnumerable<ObjectId> frameIds, ObjectId? sectionId)
    {
        Open();
        if (sectionId is { } id && !frameSections.ContainsKey(id)) throw Fail(DocumentError.NotFound, $"There is no frame section with id {id} to assign.");
        UpdateFrames(frameIds, assignment => assignment with { SectionId = sectionId });
    }

    public void SetFrameReleases(IEnumerable<ObjectId> frameIds, EndReleases releases) => UpdateFrames(frameIds, assignment => assignment with { Releases = releases });
    public void SetFrameLocalAxis(IEnumerable<ObjectId> frameIds, double angle) => UpdateFrames(frameIds, assignment => assignment with { LocalAxisAngle = angle });
    public void SetFrameModifiers(IEnumerable<ObjectId> frameIds, FrameModifiers modifiers) => UpdateFrames(frameIds, assignment => assignment with { Modifiers = modifiers });

    /// <summary>Assigns a cable section, or none with null.</summary>
    public void SetCableSection(IEnumerable<ObjectId> cableIds, ObjectId? sectionId)
    {
        Open();
        if (sectionId is { } id && !cableSections.ContainsKey(id)) throw Fail(DocumentError.NotFound, $"There is no cable section with id {id} to assign.");
        UpdateCables(cableIds, assignment => assignment with { SectionId = sectionId });
    }

    public void SetCableModifiers(IEnumerable<ObjectId> cableIds, CableModifiers modifiers) => UpdateCables(cableIds, assignment => assignment with { Modifiers = modifiers });

    /// <summary>Assigns a shell section, or none with null.</summary>
    public void SetShellSection(IEnumerable<ObjectId> shellIds, ObjectId? sectionId)
    {
        Open();
        if (sectionId is { } id && !shellSections.ContainsKey(id)) throw Fail(DocumentError.NotFound, $"There is no shell section with id {id} to assign.");
        UpdateShells(shellIds, assignment => assignment with { SectionId = sectionId });
    }

    public void SetShellLocalAxis(IEnumerable<ObjectId> shellIds, double angle) => UpdateShells(shellIds, assignment => assignment with { LocalAxisAngle = angle });

    // ---- Loads ----

    /// <summary>Adds a load, or replaces the load with the same id.</summary>
    public void PutLoad(LoadAssignment load)
    {
        Open();
        if (load.Id.IsEmpty) throw Fail(DocumentError.NotFound, "A load needs an id.");
        if (loads.TryGetValue(load.Id, out var existing))
        {
            if (existing.Equals(load)) return;
            loads[load.Id] = load;
            changes.Record(load.Id, ObjectKind.Load, ChangeAspects.Definition);
            MarkLoaded(existing.TargetId);
        }
        else
        {
            if (Exists(load.Id)) throw Fail(DocumentError.DuplicateId, $"The id {load.Id} is already in use.");
            loads[load.Id] = load;
            changes.Record(load.Id, ObjectKind.Load, ChangeAspects.Added);
        }
        MarkLoaded(load.TargetId);
    }

    // ---- Commit ----

    /// <summary>Validates the result as a whole and returns it as a new document with what changed. Throws
    /// <see cref="DocumentException"/> (<see cref="DocumentError.Invalid"/>) when the result is not a valid document.</summary>
    public DocumentCommit Commit()
    {
        Open();
        closed = true;
        if (changes.IsEmpty && !sequenceRestored) return new(source, DocumentChangeSet.Empty);

        var scopes = source.Names.Scopes;
        foreach (var (scope, builder) in names) scopes = scopes.SetItem(scope, builder.ToImmutable());
        var document = new ModelDocument(source.DocumentId, new DocumentData(points.ToImmutable(), frames.ToImmutable(),
            cables.ToImmutable(), shells.ToImmutable(), materials.ToImmutable(), frameSections.ToImmutable(),
            cableSections.ToImmutable(), shellSections.ToImmutable(), loadPatterns.ToImmutable(), loadCases.ToImmutable(),
            loadCombinations.ToImmutable(), restraints.ToImmutable(), frameAssignments.ToImmutable(),
            cableAssignments.ToImmutable(), shellAssignments.ToImmutable(), loads.ToImmutable(), settings, massSources.ToImmutable(),
            jointMassAssignments.ToImmutable(), applicationData.ToImmutable(), generator, referenceGeometry),
            new NameIndex(scopes, sequence.ToImmutable()));

        var errors = DocumentValidator.Validate(document).Where(issue => issue.Severity == ValidationSeverity.Error).ToArray();
        if (errors.Length > 0)
            throw new DocumentException(DocumentError.Invalid,
                string.Join(" ", errors.Take(5).Select(issue => issue.Message)) + (errors.Length > 5 ? $" (+{errors.Length - 5} more)" : ""), errors);
        return new(document, changes.ToChangeSet());
    }

    // ---- Internals ----

    private void Open()
    {
        if (closed) throw new InvalidOperationException("This editor has been committed; start a new edit from the document.");
        if (faulted) throw new InvalidOperationException("This editor refused an earlier operation and must be discarded.");
    }

    /// <summary>Every refusal goes through here: the working copy may be half changed, so it cannot be committed.</summary>
    private DocumentException Fail(DocumentError error, string message)
    {
        faulted = true;
        return new DocumentException(error, message);
    }

    private PointObject RequirePoint(ObjectId id) =>
        points.TryGetValue(id, out var point) ? point : throw Fail(DocumentError.NotFound, $"There is no point with id {id}.");

    private bool Exists(ObjectId id) => TryLocate(id, out _, out _) || loads.ContainsKey(id);

    private ObjectId NewId(ObjectId? requested)
    {
        var id = requested ?? ObjectId.New();
        if (id.IsEmpty) throw Fail(DocumentError.NotFound, "An object needs an id.");
        if (Exists(id)) throw Fail(DocumentError.DuplicateId, $"The id {id} is already in use.");
        return id;
    }

    private string Normalized(string? name) => NamingService.TryNormalize(name, out var normalized)
        ? normalized
        : throw Fail(DocumentError.InvalidName, "A name cannot be empty or blank, and cannot hold control characters.");

    private ImmutableDictionary<string, ObjectId>.Builder Scope(ObjectKind kind)
    {
        var scope = NamingService.ScopeOf(kind);
        if (!names.TryGetValue(scope, out var builder)) names[scope] = builder = source.Names.Scope(scope).ToBuilder();
        return builder;
    }

    private void Claim(ObjectKind kind, string normalized, ObjectId id)
    {
        var scope = Scope(kind);
        if (scope.TryGetValue(normalized, out var holder) && holder != id)
            throw Fail(DocumentError.DuplicateName, $"The name '{normalized}' is already used by {Describe(holder)}.");
        scope[normalized] = id;

        // A name that sits in a sequence — issued here or typed by the user — moves that sequence up to it.
        foreach (var series in NamingService.Kinds)
            if (NamingService.ScopeOf(series) == NamingService.ScopeOf(kind) && NamingService.SequenceNumber(normalized, series) is { } number &&
                number > sequence.GetValueOrDefault(series))
                sequence[series] = number;
    }

    /// <summary>The name is free for the user to give again. Its place in the sequence stays taken: automatic names do not go back.</summary>
    private void ReleaseName(ObjectKind kind, string name) => Scope(kind).Remove(name);

    private string ClaimNew(ObjectKind kind, string? name, ObjectId id)
    {
        var normalized = name is null ? NextName(kind) : Normalized(name);
        Claim(kind, normalized, id);
        return normalized;
    }

    private bool TryLocate(ObjectId id, out ObjectKind kind, out string name)
    {
        (kind, name) = (default, "");
        if (points.TryGetValue(id, out var point)) (kind, name) = (ObjectKind.Point, point.Name);
        else if (frames.TryGetValue(id, out var frame)) (kind, name) = (ObjectKind.Frame, frame.Name);
        else if (cables.TryGetValue(id, out var cable)) (kind, name) = (ObjectKind.Cable, cable.Name);
        else if (shells.TryGetValue(id, out var shell)) (kind, name) = (ObjectKind.Shell, shell.Name);
        else if (materials.TryGetValue(id, out var material)) (kind, name) = (ObjectKind.Material, material.Name);
        else if (frameSections.TryGetValue(id, out var frameSection)) (kind, name) = (ObjectKind.FrameSection, frameSection.Name);
        else if (cableSections.TryGetValue(id, out var cableSection)) (kind, name) = (ObjectKind.CableSection, cableSection.Name);
        else if (shellSections.TryGetValue(id, out var shellSection)) (kind, name) = (ObjectKind.ShellSection, shellSection.Name);
        else if (loadPatterns.TryGetValue(id, out var pattern)) (kind, name) = (ObjectKind.LoadPattern, pattern.Name);
        else if (loadCases.TryGetValue(id, out var loadCase)) (kind, name) = (ObjectKind.LoadCase, loadCase.Name);
        else if (loadCombinations.TryGetValue(id, out var combination)) (kind, name) = (ObjectKind.LoadCombination, combination.Name);
        else if (massSources.TryGetValue(id, out var massSource)) (kind, name) = (ObjectKind.MassSource, massSource.Name);
        else return false;
        return true;
    }

    private string Describe(ObjectId id) => TryLocate(id, out var kind, out var name) ? $"{Label(kind)} '{name}'" : $"object {id}";

    private static string Label(ObjectKind kind) => kind switch
    {
        ObjectKind.Point => "point",
        ObjectKind.Frame => "frame",
        ObjectKind.Cable => "cable",
        ObjectKind.Shell => "shell",
        ObjectKind.Material => "material",
        ObjectKind.FrameSection => "frame section",
        ObjectKind.CableSection => "cable section",
        ObjectKind.ShellSection => "shell section",
        ObjectKind.LoadPattern => "load pattern",
        ObjectKind.LoadCase => "load case",
        ObjectKind.LoadCombination => "load combination",
        ObjectKind.MassSource => "mass source",
        _ => "object",
    };

    private void SetName(ObjectId id, ObjectKind kind, string name)
    {
        switch (kind)
        {
            case ObjectKind.Point: points[id] = points[id] with { Name = name }; break;
            case ObjectKind.Frame: frames[id] = frames[id] with { Name = name }; break;
            case ObjectKind.Cable: cables[id] = cables[id] with { Name = name }; break;
            case ObjectKind.Shell: shells[id] = shells[id] with { Name = name }; break;
            case ObjectKind.Material: materials[id] = materials[id] with { Name = name }; break;
            case ObjectKind.FrameSection: frameSections[id] = frameSections[id] with { Name = name }; break;
            case ObjectKind.CableSection: cableSections[id] = cableSections[id] with { Name = name }; break;
            case ObjectKind.ShellSection: shellSections[id] = shellSections[id] with { Name = name }; break;
            case ObjectKind.LoadPattern: loadPatterns[id] = loadPatterns[id] with { Name = name }; break;
            case ObjectKind.LoadCase: loadCases[id] = loadCases[id] with { Name = name }; break;
            case ObjectKind.LoadCombination: loadCombinations[id] = loadCombinations[id] with { Name = name }; break;
            case ObjectKind.MassSource: massSources[id] = massSources[id] with { Name = name }; break;
        }
    }

    private void RemoveNamed(ObjectId id, ObjectKind kind, string name)
    {
        switch (kind)
        {
            case ObjectKind.Point: points.Remove(id); restraints.Remove(id); jointMassAssignments.Remove(id); break;
            case ObjectKind.Frame: frames.Remove(id); frameAssignments.Remove(id); break;
            case ObjectKind.Cable: cables.Remove(id); cableAssignments.Remove(id); break;
            case ObjectKind.Shell: shells.Remove(id); shellAssignments.Remove(id); break;
            case ObjectKind.Material: materials.Remove(id); break;
            case ObjectKind.FrameSection: frameSections.Remove(id); break;
            case ObjectKind.CableSection: cableSections.Remove(id); break;
            case ObjectKind.ShellSection: shellSections.Remove(id); break;
            case ObjectKind.LoadPattern: loadPatterns.Remove(id); break;
            case ObjectKind.LoadCase: loadCases.Remove(id); break;
            case ObjectKind.LoadCombination: loadCombinations.Remove(id); break;
            case ObjectKind.MassSource: massSources.Remove(id); break;
        }
        ReleaseName(kind, name);
        changes.Record(id, kind, ChangeAspects.Removed);
    }

    private void RemoveLoad(ObjectId id)
    {
        var load = loads[id];
        loads.Remove(id);
        changes.Record(id, ObjectKind.Load, ChangeAspects.Removed);
        MarkLoaded(load.TargetId);
    }

    /// <summary>The loads on an object changed (when the object is still there).</summary>
    private void MarkLoaded(ObjectId targetId)
    {
        if (TryLocate(targetId, out var kind, out _)) changes.Record(targetId, kind, ChangeAspects.Loads);
    }

    private void EnsureNotReferenced(Dictionary<ObjectId, string> removed)
    {
        if (removed.Count == 0) return;
        string? gone;
        DocumentException InUse(string what, string user) => Fail(DocumentError.InUse, $"{char.ToUpperInvariant(what[0])}{what[1..]} is used by {user} and cannot be removed.");

        foreach (var frame in frames.Values)
            if (removed.TryGetValue(frame.StartPointId, out gone) || removed.TryGetValue(frame.EndPointId, out gone))
                throw InUse(gone, $"frame '{frame.Name}'");
        foreach (var cable in cables.Values)
            if (removed.TryGetValue(cable.StartPointId, out gone) || removed.TryGetValue(cable.EndPointId, out gone))
                throw InUse(gone, $"cable '{cable.Name}'");
        foreach (var shell in shells.Values)
            foreach (var pointId in shell.PointIds)
                if (removed.TryGetValue(pointId, out gone)) throw InUse(gone, $"shell '{shell.Name}'");
        foreach (var section in frameSections.Values)
            if (removed.TryGetValue(section.MaterialId, out gone)) throw InUse(gone, $"frame section '{section.Name}'");
        foreach (var section in cableSections.Values)
            if (removed.TryGetValue(section.MaterialId, out gone)) throw InUse(gone, $"cable section '{section.Name}'");
        foreach (var section in shellSections.Values)
            if (removed.TryGetValue(section.MaterialId, out gone)) throw InUse(gone, $"shell section '{section.Name}'");
        foreach (var (frameId, assignment) in frameAssignments)
            if (assignment.SectionId is { } sectionId && removed.TryGetValue(sectionId, out gone)) throw InUse(gone, Describe(frameId));
        foreach (var (cableId, assignment) in cableAssignments)
            if (assignment.SectionId is { } sectionId && removed.TryGetValue(sectionId, out gone)) throw InUse(gone, Describe(cableId));
        foreach (var (shellId, assignment) in shellAssignments)
            if (assignment.SectionId is { } sectionId && removed.TryGetValue(sectionId, out gone)) throw InUse(gone, Describe(shellId));
        foreach (var load in loads.Values)
            if (removed.TryGetValue(load.PatternId, out gone)) throw InUse(gone, $"a load on {Describe(load.TargetId)}");
        foreach (var loadCase in loadCases.Values)
        {
            if (loadCase.InitialCaseId is { } initial && removed.TryGetValue(initial, out gone)) throw InUse(gone, $"load case '{loadCase.Name}', which continues from it");
            if (loadCase.StiffnessSourceCaseId is { } stiffness && removed.TryGetValue(stiffness, out gone)) throw InUse(gone, $"load case '{loadCase.Name}', which uses its stiffness");
            if (loadCase.ModalOptions is { } modal && removed.TryGetValue(modal.MassSourceId, out gone)) throw InUse(gone, $"load case '{loadCase.Name}'");
            foreach (var component in loadCase.Components)
                if (removed.TryGetValue(component.PatternId, out gone)) throw InUse(gone, $"load case '{loadCase.Name}'");
        }
        foreach (var combination in loadCombinations.Values)
            foreach (var term in combination.Terms)
                if (removed.TryGetValue(term.SourceId, out gone)) throw InUse(gone, $"load combination '{combination.Name}'");
    }

    private void PutNamed<T>(ImmutableDictionary<ObjectId, T>.Builder map, ObjectKind kind, T item, Func<T, string, T> withName)
        where T : class, INamedObject
    {
        Open();
        if (item.Id.IsEmpty) throw Fail(DocumentError.NotFound, $"A {Label(kind)} needs an id.");
        var name = Normalized(item.Name);
        var stored = withName(item, name);
        if (map.TryGetValue(item.Id, out var existing))
        {
            if (stored.Equals(existing)) return;
            if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                ReleaseName(kind, existing.Name);
                Claim(kind, name, item.Id);
                changes.Record(item.Id, kind, ChangeAspects.Name);
            }
            if (!stored.Equals(withName(existing, name))) changes.Record(item.Id, kind, ChangeAspects.Definition);
            map[item.Id] = stored;
            return;
        }
        if (Exists(item.Id)) throw Fail(DocumentError.DuplicateId, $"The id {item.Id} is already in use.");
        Claim(kind, name, item.Id);
        map[item.Id] = stored;
        changes.Record(item.Id, kind, ChangeAspects.Added);
    }

    private void UpdateFrames(IEnumerable<ObjectId> frameIds, Func<FrameAssignment, FrameAssignment> update)
    {
        Open();
        foreach (var id in frameIds)
        {
            if (!frames.ContainsKey(id)) throw Fail(DocumentError.NotFound, $"There is no frame with id {id}.");
            var current = AssignmentOfFrame(id);
            var next = update(current);
            if (next == current) continue;
            if (next == FrameAssignment.Default) frameAssignments.Remove(id); else frameAssignments[id] = next;
            changes.Record(id, ObjectKind.Frame, ChangeAspects.Assignment);
        }
    }

    private void UpdateCables(IEnumerable<ObjectId> cableIds, Func<CableAssignment, CableAssignment> update)
    {
        Open();
        foreach (var id in cableIds)
        {
            if (!cables.ContainsKey(id)) throw Fail(DocumentError.NotFound, $"There is no cable with id {id}.");
            var current = AssignmentOfCable(id);
            var next = update(current);
            if (next == current) continue;
            if (next == CableAssignment.Default) cableAssignments.Remove(id); else cableAssignments[id] = next;
            changes.Record(id, ObjectKind.Cable, ChangeAspects.Assignment);
        }
    }

    private void UpdateShells(IEnumerable<ObjectId> shellIds, Func<ShellAssignment, ShellAssignment> update)
    {
        Open();
        foreach (var id in shellIds)
        {
            if (!shells.ContainsKey(id)) throw Fail(DocumentError.NotFound, $"There is no shell with id {id}.");
            var current = AssignmentOfShell(id);
            var next = update(current);
            if (next == current) continue;
            if (next == ShellAssignment.Default) shellAssignments.Remove(id); else shellAssignments[id] = next;
            changes.Record(id, ObjectKind.Shell, ChangeAspects.Assignment);
        }
    }
}
