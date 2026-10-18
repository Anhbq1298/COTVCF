using System.Collections.Immutable;
using Mcotis.Desktop.Application;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

// ---- Assign → Frame → Frame Load (docs/loads/FRAME_DISTRIBUTED_LOAD_PLAN.md, FL-04) ----
public sealed partial class MainViewModel
{
    /// <summary>Frame distributed loads assigned in the native document, with application names for the viewer.</summary>
    public IReadOnlyList<FrameLoadDisplay> SharedViewerFrameLoads
    {
        get
        {
            var document = NativeDocumentSession.Document;
            var names = LegacyNames.Of(document);
            return document.Loads.Values.OfType<Native.FrameDistributedLoad>()
                .Where(load => names.TryGetName(load.TargetId, out _))
                .Select(load => new FrameLoadDisplay(names[load.TargetId],
                    document.LoadPatterns.TryGetValue(load.PatternId, out var pattern) ? pattern.Name : "Load",
                    load.Type, load.Direction, load.Projected, load.RelativeStart, load.RelativeEnd, load.ValueStart, load.ValueEnd))
                .ToArray();
        }
    }

    /// <summary>The load patterns of the model, by name: what the Frame Load form offers.</summary>
    internal IReadOnlyList<string> FrameLoadPatternNames =>
        NativeDocumentSession.Document.LoadPatterns.Values.Select(pattern => pattern.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The exports still write the legacy loading (FL-06 moves them to the document): a frame load assigned with
    /// Assign → Frame → Frame Load is not in it, so the export says so instead of leaving it out silently.</summary>
    private void WarnNativeFrameLoadsNotExported(string title)
    {
        var document = NativeDocumentSession.Document;
        var count = NativeFrameLoads.Ids(document.ApplicationData).Count(id => document.Loads.ContainsKey(id));
        if (count > 0)
            Warn(title, $"{count} frame distributed load segment{(count == 1 ? "" : "s")} assigned with Assign → Frame → Frame Load " +
                $"{(count == 1 ? "is" : "are")} not exported yet: assign {(count == 1 ? "it" : "them")} in the target model, or wait for the export of " +
                "frame loads from the model (planned, docs/loads/FRAME_DISTRIBUTED_LOAD_PLAN.md FL-06). Everything else is exported as before.", warning: true);
    }

    /// <summary>Assign → Frame → Frame Load: SAP2000's frame distributed load on every one of <paramref name="frames"/> —
    /// added to, replacing or deleting the distributed loads of the pattern (<see cref="Native.FrameLoads.Assign"/>). One
    /// step of the model's history; the results of the model go; SAP2000 is never written. Returns how many frames changed.
    /// <para>The loads are the document's own (<see cref="NativeFrameLoads"/>). Replace and Delete also take the pattern's
    /// loads of these frames out of the legacy loading the window still keeps, so that no rebuild brings them back.</para></summary>
    internal int AssignFrameDistributedLoad(IReadOnlyCollection<string> frames, string patternName, Native.FrameLoadType type,
        Native.LoadDirection direction, bool projected, Native.LoadDistanceType distances, IReadOnlyList<Native.FrameLoadPoint> points,
        double uniform, Native.LoadAssignMode mode)
    {
        if (!CanEditFemModel) throw new InvalidOperationException("Unlock the model before assigning frame loads.");
        var document = NativeDocumentSession.Document;
        var names = LegacyNames.Of(document);
        var chosen = frames.Select(name => (Name: name, Id: names.Frame(name))).Where(item => item.Id is not null)
            .Select(item => (item.Name, Id: item.Id!.Value)).DistinctBy(item => item.Id).ToArray();
        if (chosen.Length == 0) throw new ArgumentException("Select one or more frames of the model first.", nameof(frames));
        var pattern = document.LoadPatterns.Values.FirstOrDefault(item => string.Equals(item.Name, patternName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"The model has no load pattern '{patternName}'.", nameof(patternName));
        var definition = new Native.FrameDistributedLoadDefinition(pattern.Id, type, direction, projected, distances, points.ToImmutableArray(), uniform);
        var frameIds = chosen.Select(item => item.Id).ToArray();
        var frameNames = chosen.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);

        var priorLoading = femSapLoading;
        if (mode != Native.LoadAssignMode.Add)
            femSapLoading = femSapLoading with
            {
                MemberLoads = femSapLoading.MemberLoads.Where(load => !(frameNames.Contains(load.Load.MemberName) &&
                    string.Equals(load.Pattern, pattern.Name, StringComparison.OrdinalIgnoreCase))).ToArray(),
            };
        int changed = 0;
        try
        {
            var snapshot = CaptureFemInputSnapshot(advanceRevision: false);
            NativeDocumentSession.Execute(Mcotis.Application.DocumentCommands.Edit("Assign frame distributed loads", editor =>
            {
                var before = editor.Loads.Keys.ToHashSet();
                changed = Native.FrameLoads.Assign(editor, frameIds, definition, mode);
                if (changed == 0) return;
                NativeFrameLoads.Remember(editor, editor.Loads.Keys.Where(id => !before.Contains(id)));
                NativeToLegacyProjection.WriteConfiguration(editor, snapshot);
            }));
        }
        catch
        {
            femSapLoading = priorLoading;
            throw;
        }
        if (changed == 0) femSapLoading = priorLoading;
        else
        {
            ClearFemResults();
            ScheduleFemWorkingSave();
            NotifySharedViewer();
            NotifyModelHistory();
        }
        string what = mode switch
        {
            Native.LoadAssignMode.Delete => "deleted from",
            Native.LoadAssignMode.Add => "added to",
            _ => "assigned to",
        };
        Message = changed == 0
            ? $"The selected frames already have these distributed loads in {pattern.Name}."
            : $"Distributed {(type == Native.FrameLoadType.Moment ? "moments" : "loads")} in {pattern.Name} {what} {changed} frame{(changed == 1 ? "" : "s")} in the Mcotis model. FEM results cleared.";
        Update();
        return changed;
    }
}
