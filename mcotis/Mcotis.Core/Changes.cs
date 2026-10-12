namespace Mcotis.Core;

[Flags]
public enum ChangeAspects
{
    None = 0,
    Added = 1,
    Removed = 2,
    Name = 4,
    /// <summary>Position of a point; through it, the geometry of the frames and shells on it.</summary>
    Geometry = 8,
    /// <summary>Section, restraint, releases, local axis or modifiers of an object.</summary>
    Assignment = 16,
    /// <summary>The loads on an object.</summary>
    Loads = 32,
    /// <summary>The content of a definition (material, section, pattern, case, combination), a load or the settings.</summary>
    Definition = 64,
}

public readonly record struct ObjectChange(ObjectId Id, ObjectKind Kind, ChangeAspects Aspects);

/// <summary>What one edit changed: which objects and which kind of data. A panel refreshes only what concerns it.</summary>
public sealed class DocumentChangeSet
{
    public static DocumentChangeSet Empty { get; } = new([], false);
    /// <summary>The whole document was replaced (New, Open): everything is to be read again.</summary>
    public static DocumentChangeSet Reset { get; } = new([], true);

    internal DocumentChangeSet(IReadOnlyList<ObjectChange> changes, bool isReset)
    {
        Changes = changes;
        IsReset = isReset;
        Aspects = changes.Aggregate(ChangeAspects.None, (all, change) => all | change.Aspects);
    }

    public IReadOnlyList<ObjectChange> Changes { get; }
    public bool IsReset { get; }
    public bool IsEmpty => !IsReset && Changes.Count == 0;
    /// <summary>Every aspect any object of this set changed in.</summary>
    public ChangeAspects Aspects { get; }

    public bool Affects(ObjectKind kind) => IsReset || Changes.Any(change => change.Kind == kind);
    public bool Affects(ObjectId id) => IsReset || Changes.Any(change => change.Id == id);

    public IEnumerable<ObjectId> Ids(ObjectKind kind, ChangeAspects aspects) =>
        Changes.Where(change => change.Kind == kind && (change.Aspects & aspects) != 0).Select(change => change.Id);

    /// <summary>The same changes seen from the other side: what Undo of this edit changes.</summary>
    public DocumentChangeSet Inverse() => IsReset ? this : new(Changes.Select(change => change with
    {
        Aspects = (change.Aspects & ~(ChangeAspects.Added | ChangeAspects.Removed)) |
                  (change.Aspects.HasFlag(ChangeAspects.Added) ? ChangeAspects.Removed : 0) |
                  (change.Aspects.HasFlag(ChangeAspects.Removed) ? ChangeAspects.Added : 0),
    }).ToArray(), false);
}

/// <summary>Collects the net effect of an edit: an object added and removed within one edit did not change.</summary>
internal sealed class ChangeRecorder
{
    private readonly Dictionary<ObjectId, ObjectChange> items = [];
    private readonly List<ObjectId> order = [];

    public bool IsEmpty => items.Count == 0;

    public void Record(ObjectId id, ObjectKind kind, ChangeAspects aspects)
    {
        if (!items.TryGetValue(id, out var current))
        {
            items[id] = new(id, kind, aspects);
            order.Add(id);
            return;
        }
        if (current.Aspects.HasFlag(ChangeAspects.Added) && aspects.HasFlag(ChangeAspects.Removed))
        {
            items.Remove(id);
            order.Remove(id);
            return;
        }
        // Added stays Added whatever follows; a removed object that comes back under its id was replaced.
        var merged = current.Aspects | aspects;
        if (merged.HasFlag(ChangeAspects.Added) && merged.HasFlag(ChangeAspects.Removed))
            merged = (merged & ~(ChangeAspects.Added | ChangeAspects.Removed)) | ChangeAspects.Definition;
        items[id] = new(id, kind, current.Aspects.HasFlag(ChangeAspects.Added) ? ChangeAspects.Added : merged);
    }

    public DocumentChangeSet ToChangeSet() => new(order.Select(id => items[id]).ToArray(), false);
}
