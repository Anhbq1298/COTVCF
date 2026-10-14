using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

// ---- Edit → Delete: remove the objects selected on the model view from the working model ----
// As SAP2000: a deleted joint takes the frames and cables connected to it; a deleted member leaves its end joints. Everything
// that names a removed object follows — member properties, SAP loading, Tab 01 loaded points and point loads, Tab 02 chord
// groups, Tab 03 cables, Tab 04 output lists. The edited model is an offline model; SAP2000 is never written.
public sealed partial class MainViewModel
{
    /// <summary>The members <see cref="DeleteObjectsAsync"/> removes for this selection: the selected members and every
    /// member connected to a selected joint.</summary>
    internal IReadOnlyList<string> MembersDeletedWith(IReadOnlyCollection<string> members, IReadOnlyCollection<string> joints)
    {
        var points = joints.ToHashSet(StringComparer.Ordinal);
        var chosen = members.ToHashSet(StringComparer.Ordinal);
        return State.Geometry.Members.Where(member => chosen.Contains(member.Name) || points.Contains(member.Start) || points.Contains(member.End))
            .Select(member => member.Name).ToArray();
    }

    /// <summary>Deletes <paramref name="members"/>, <paramref name="joints"/> and the members connected to those joints.
    /// FEM results are cleared. Returns how many members and joints were removed.</summary>
    internal async Task<(int Members, int Joints)> DeleteObjectsAsync(IReadOnlyCollection<string> members, IReadOnlyCollection<string> joints)
    {
        if (!CanEditFemModel) throw new InvalidOperationException("The model cannot be edited now (busy, locked or without geometry).");
        var geometry = State.Geometry;
        var removedPoints = joints.Where(geometry.Points.ContainsKey).ToHashSet(StringComparer.Ordinal);
        var removedMembers = MembersDeletedWith(members, removedPoints).ToHashSet(StringComparer.Ordinal);
        if (removedMembers.Count == 0 && removedPoints.Count == 0) throw new ArgumentException("Select one or more joints, frames or cables first.");

        IsBusy = true;
        try
        {
            // the member properties and loading must be the model's before objects go (connected: read them once, read-only)
            if (State.IsSapConnected && (femInputsOutdated || FemSectionProperties.Count == 0 || FemPatternRows.Count == 0))
            {
                Message = "Reading sections and loading from SAP2000 before deleting (read-only)…";
                await ReadAndDisplayFemPropertiesAsync(resetLoadRows: true);
            }

            var edited = new GeometryModel(
                geometry.Points.Where(point => !removedPoints.Contains(point.Key)).ToDictionary(point => point.Key, point => point.Value, StringComparer.Ordinal),
                geometry.Members.Where(member => !removedMembers.Contains(member.Name)).ToArray());
            var points = edited.Points.Keys.ToHashSet(StringComparer.Ordinal);
            var kept = edited.Members.Select(member => member.Name).ToHashSet(StringComparer.Ordinal);

            var properties = FemSectionProperties.Where(property => kept.Contains(property.MemberName)).ToArray();
            FemSectionProperties.Clear();
            foreach (var property in properties) FemSectionProperties.Add(property);
            femSapLoading = femSapLoading with
            {
                JointLoads = femSapLoading.JointLoads.Where(load => points.Contains(load.Load.PointName)).ToArray(),
                MemberLoads = femSapLoading.MemberLoads.Where(load => kept.Contains(load.Load.MemberName)).ToArray(),
                MemberPointLoads = femSapLoading.MemberPointLoads.Where(load => kept.Contains(load.Load.MemberName)).ToArray(),
                GravityLoads = femSapLoading.GravityLoads.Where(load => kept.Contains(load.MemberName)).ToArray(),
            };

            // leave the live SAP model: the edited model is an offline model from here on
            if (State.IsSapConnected)
            {
                offlineLoadCaseNames = femSapLoading.Cases.Select(item => item.Name).ToArray();
                isDefaultModel = false;
                State.Connection = "Offline model";
            }
            const string suffix = " · edited";
            var name = offlineModelPath is null ? State.Model?.Name ?? "Model" : System.IO.Path.GetFileName(offlineModelPath);
            offlineModelPath = name.EndsWith(suffix, StringComparison.Ordinal) ? name : name + suffix;

            string? note = null;
            var truss = State.Truss;
            var (deltaMin, deltaMax, step, deltaCurrent) = (truss.MinimumHeight - truss.BaselineHeight, truss.MaximumHeight - truss.BaselineHeight,
                truss.Increment, truss.CurrentHeight - truss.BaselineHeight);
            var captured = State.BaselineCaptured;
            // a restraint goes with its joint
            State.JointRestraints = State.JointRestraints.Where(item => points.Contains(item.Key))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            State.Geometry = edited;
            State.Baseline = edited;
            State.SessionBaseline = edited;
            State.Preview = edited;
            applyingProject = true;
            try
            {
                static void Keep(System.Collections.ObjectModel.ObservableCollection<string> list, HashSet<string> valid)
                {
                    foreach (var item in list.Where(item => !valid.Contains(item)).ToArray()) list.Remove(item);
                }
                truss.TopPoints = truss.TopPoints.Where(points.Contains).ToArray();
                truss.MiddlePoints = truss.MiddlePoints.Where(points.Contains).ToArray();
                truss.BottomPoints = truss.BottomPoints.Where(points.Contains).ToArray();
                if (captured)
                {
                    try
                    {
                        workflow.CaptureBaseline();
                        truss.MinimumHeight = truss.BaselineHeight + deltaMin;
                        truss.MaximumHeight = truss.BaselineHeight + deltaMax;
                        truss.Increment = step;
                        truss.CurrentHeight = truss.BaselineHeight + deltaCurrent;
                        State.CommittedHeight = truss.BaselineHeight;
                        workflow.UpdatePreview();
                    }
                    catch (InvalidOperationException ex) { note = $" Tab 02 needs its chord groups reviewed: {ex.Message}"; }
                }

                foreach (var point in State.AppliedPoints.Where(point => !points.Contains(point.PointName)).ToArray()) State.AppliedPoints.Remove(point);
                foreach (var load in State.PointLoads.Where(load => !points.Contains(load.PointName)).ToArray()) State.PointLoads.Remove(load);

                // a cable that follows a deleted cable is set free first, with the force it had from it as its own target —
                // every change of the cables recomposes the study, which cannot read a follower whose reference is gone
                var cables = State.Cables.ToArray();
                foreach (var cable in cables.Where(cable => kept.Contains(cable.CableName)))
                {
                    if (cable.ReferenceCableName is not { } reference || kept.Contains(reference)) continue;
                    var forces = cable.IsFollower ? CableSyncGraph.ResolvedForces(cable, cables) : [];
                    cable.SyncMode = CableSyncMode.Independent;
                    cable.ReferenceCableName = null;
                    if (forces.Count > 1)
                    {
                        cable.MinForce = forces[0];
                        cable.MaxForce = forces[^1];
                        cable.StepForce = (forces[^1] - forces[0]) / (forces.Count - 1);
                        cable.ApplyRange = true;
                    }
                    else if (forces.Count == 1) { cable.ApplyRange = false; cable.TargetForce = forces[0]; }
                    else if (!cable.ApplyRange) cable.TargetForce ??= 0;
                }
                foreach (var cable in cables.Where(cable => !kept.Contains(cable.CableName))) State.Cables.Remove(cable);

                var output = State.OutputSettings;
                Keep(output.DeflectionPoints, points);
                Keep(output.FoundationLeftPoints, points);
                Keep(output.FoundationRightPoints, points);
                Keep(output.CableNames, kept);
                foreach (var group in output.MemberForceGroups) Keep(group.Members, kept);
            }
            finally { applyingProject = false; }

            ClearFemResults();
            RegenerateStudyPreview();
            var what = string.Join(" and ", new[]
            {
                removedMembers.Count > 0 ? $"{removedMembers.Count} member{(removedMembers.Count == 1 ? "" : "s")}" : null,
                removedPoints.Count > 0 ? $"{removedPoints.Count} joint{(removedPoints.Count == 1 ? "" : "s")}" : null,
            }.Where(part => part is not null));
            FemProgressText = $"Deleted {what}. Run Analysis solves the edited model; SAP2000 is unchanged.";
            Message = $"Deleted {what} from the working model (FEM results cleared; SAP2000 unchanged).{note}";
            return (removedMembers.Count, removedPoints.Count);
        }
        finally
        {
            IsBusy = false;
            Notify(nameof(SharedViewerSpanPoints));
            Notify(nameof(SharedViewerMemberSizes));
            Notify(nameof(SharedViewerSections));
            Notify(nameof(SharedViewerPinSupportPoints));
            Update();
        }
    }
}
