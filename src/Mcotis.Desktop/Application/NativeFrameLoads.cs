using System.Collections.Immutable;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application;

/// <summary>
/// The frame distributed loads assigned with Assign → Frame → Frame Load belong to the native document
/// (docs/loads/FRAME_DISTRIBUTED_LOAD_PLAN.md, FL-03). The main window still rebuilds its document from the legacy
/// configuration after a legacy-first edit (<see cref="NativeToLegacyProjection.Write"/>), and that configuration cannot
/// hold them — no Global Y, Local 3, moments or projection — so the rebuild would lose them. The ids of the loads the
/// document owns are kept in its application data under <see cref="Key"/>, and <see cref="Carry"/> takes those loads
/// from the document before a rebuild into the one after it.
/// <para>Temporary, like the bridge itself: it goes when the last legacy-first edit has moved to the document.</para>
/// </summary>
internal static class NativeFrameLoads
{
    public const string Key = "mcotis.desktop.nativeFrameLoads";

    /// <summary>The ids of the frame loads the document owns (assigned natively), as its application data lists them.</summary>
    public static ImmutableHashSet<Native.ObjectId> Ids(IReadOnlyDictionary<string, string> applicationData) =>
        applicationData.TryGetValue(Key, out var text)
            ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => Guid.TryParse(item, out var guid) ? new Native.ObjectId(guid) : default)
                .Where(id => !id.IsEmpty).ToImmutableHashSet()
            : [];

    /// <summary>Lists in the editor's application data the native frame loads it holds: the ones it listed before that
    /// are still there, and <paramref name="added"/>.</summary>
    public static void Remember(Native.DocumentEditor editor, IEnumerable<Native.ObjectId> added)
    {
        var ids = Ids(editor.ApplicationData).Union(added).Where(id => editor.Loads.ContainsKey(id)).OrderBy(id => id).ToArray();
        editor.SetApplicationData(Key, ids.Length == 0 ? null : string.Join(",", ids));
    }

    /// <summary><paramref name="rebuilt"/> with the native frame loads of <paramref name="before"/> carried over: each one
    /// whose frame and load pattern are still in the rebuilt document. A load whose frame went (deleted, or renamed by a
    /// legacy edit) goes with it, as any load of a deleted object does.</summary>
    public static Native.ModelDocument Carry(Native.ModelDocument before, Native.ModelDocument rebuilt)
    {
        var ids = Ids(before.ApplicationData);
        if (ids.Count == 0) return rebuilt;
        var editor = rebuilt.Edit();
        var kept = new List<Native.ObjectId>();
        foreach (var id in ids.OrderBy(id => id))
        {
            if (!before.Loads.TryGetValue(id, out var load) || load is not (Native.FrameDistributedLoad or Native.FramePointLoad)) continue;
            if (!editor.Frames.ContainsKey(load.TargetId) || !editor.LoadPatterns.ContainsKey(load.PatternId)) continue;
            if (!editor.Loads.ContainsKey(id)) editor.PutLoad(load);
            kept.Add(id);
        }
        editor.SetApplicationData(Key, kept.Count == 0 ? null : string.Join(",", kept));
        return editor.Commit().Document;
    }
}
