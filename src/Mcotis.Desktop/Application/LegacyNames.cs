using System.IO;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application;

/// <summary>
/// The one map between the identities of the native document and the names the application's own model, its tables,
/// its viewer and its reports still use (task of 2026-10-14, M1: "nothing else may compare names"). An object made
/// from the application's model keeps that name in its origin (<see cref="Native.ObjectOrigin.SourceName"/>); its
/// native name can differ where two objects of the old model shared a name. An object made in the document itself has
/// no other name than its own. Temporary: it goes when the last consumer reads the document (M7).
/// </summary>
public sealed class LegacyNames
{
    private readonly Dictionary<Native.ObjectId, string> names = [];
    private readonly Dictionary<string, Native.ObjectId> points = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Native.ObjectId> frames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Native.ObjectId> cables = new(StringComparer.Ordinal);

    private LegacyNames() { }

    /// <summary>Names persisted with an analysis output. Output databases are immutable snapshots, so rebuilding this
    /// map must not require the input document still to be open.</summary>
    internal static LegacyNames FromEntries(IEnumerable<(Native.ObjectId Id, string Name, string Kind)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var map = new LegacyNames();
        foreach (var (id, name, kind) in entries)
        {
            var index = kind switch
            {
                "Point" => map.points,
                "Frame" => map.frames,
                "Cable" => map.cables,
                _ => throw new InvalidDataException($"Unknown result object kind '{kind}'."),
            };
            map.Add(id, name, index);
        }
        return map;
    }

    internal IEnumerable<(Native.ObjectId Id, string Name, string Kind)> Entries =>
        points.Select(item => (item.Value, item.Key, "Point"))
            .Concat(frames.Select(item => (item.Value, item.Key, "Frame")))
            .Concat(cables.Select(item => (item.Value, item.Key, "Cable")));

    public static LegacyNames Of(Native.ModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var map = new LegacyNames();
        static string Name(string own, Native.ObjectOrigin? origin) => origin?.SourceName is { Length: > 0 } source ? source : own;
        foreach (var point in document.Points.Values) map.Add(point.Id, Name(point.Name, point.Origin), map.points);
        foreach (var frame in document.Frames.Values) map.Add(frame.Id, Name(frame.Name, frame.Origin), map.frames);
        foreach (var cable in document.Cables.Values) map.Add(cable.Id, Name(cable.Name, cable.Origin), map.cables);
        return map;
    }

    private void Add(Native.ObjectId id, string name, Dictionary<string, Native.ObjectId> byName)
    {
        names[id] = name;
        byName.TryAdd(name, id);
    }

    /// <summary>The application's name of a point, frame or cable of the document.</summary>
    public string this[Native.ObjectId id] => names[id];

    public bool TryGetName(Native.ObjectId id, out string name) => names.TryGetValue(id, out name!);

    /// <summary>The document's point, frame or cable the application knows by <paramref name="name"/>, if any.</summary>
    public Native.ObjectId? Point(string name) => points.TryGetValue(name, out var id) ? id : null;
    public Native.ObjectId? Frame(string name) => frames.TryGetValue(name, out var id) ? id : null;
    public Native.ObjectId? Cable(string name) => cables.TryGetValue(name, out var id) ? id : null;
}
