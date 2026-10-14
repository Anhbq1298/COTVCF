using System.IO;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Presentation;
using Native = Mcotis.Core;

/// <summary>
/// The main window's Undo and Redo are the document session's (task of 2026-10-14, M2): every edit of the model is one
/// step of its native document's history — the configuration written into the document whole
/// (<see cref="NativeToLegacyProjection"/>) — and Undo / Redo restore the configuration the document holds. Dirty state
/// is the session's: Save makes the model clean, an edit dirty, Undo back to the saved state clean again. A locked model
/// (a run on hand) has no Undo: its results would not be of the model. <c>--model-history</c>, and in <c>--full</c>.
/// </summary>
internal static class ModelHistoryChecks
{
    public static void Run()
    {
        var (vm, _, _, warnings) = FemModelFileChecks.DefaultModelApp();
        Check(!vm.CanUndoModel && !vm.CanRedoModel && !vm.IsModelDirty && !vm.WindowTitle.EndsWith(" *", StringComparison.Ordinal) &&
              NativeToLegacyProjection.Read(vm.NativeDocumentSession.Document) is not null,
            "a model that is opened starts a history of its own: nothing to undo, clean, and its document holds the whole configuration");

        var nativePoint = vm.NativeDocumentSession.Document.Points.Values.First();
        var applicationName = LegacyNames.Of(vm.NativeDocumentSession.Document)[nativePoint.Id];
        var displayedPosition = vm.SharedViewerModel.Points[applicationName].Position;
        Check(displayedPosition.X == nativePoint.Position.X && displayedPosition.Y == nativePoint.Position.Y && displayedPosition.Z == nativePoint.Position.Z &&
              vm.SharedViewerModel.Points.Count == vm.NativeDocumentSession.Document.Points.Count &&
              vm.SharedViewerModel.Members.Count == vm.NativeDocumentSession.Document.Frames.Count + vm.NativeDocumentSession.Document.Cables.Count,
            "M5: the main viewport reads the native document, not the compatibility geometry");

        // two edits: a fixed base, then another section on a frame
        var bases = vm.FemPinSupportPoints;
        var frame = vm.FemSectionProperties.First(property => !property.IsCable);
        var other = vm.FrameSections.Select(section => section.Name).First(name => name != frame.SectionName);
        Check(vm.CanEditFemModel,
            $"the opened model is editable before M5 restraint assignment (busy {vm.IsBusy}, working {vm.HasWorkingModel}, geometry {vm.HasModelGeometry}, locked {vm.IsFemModelLocked}; {string.Join(" | ", warnings)}; {vm.FemProgressText})");
        vm.AssignJointRestraints([bases[0]], Native.Restraint.Fixed);
        var editedPoint = LegacyNames.Of(vm.NativeDocumentSession.Document).Point(bases[0]);
        var embedded = NativeToLegacyProjection.Read(vm.NativeDocumentSession.Document);
        Check(editedPoint is { } pointId && vm.NativeDocumentSession.Document.Restraints[pointId] == Native.Restraint.Fixed &&
              embedded?.JointRestraints.Single(item => item.JointName == bases[0]).ToNative() == Native.Restraint.Fixed,
            "M5: Assign Restraints commits the native assignment and compatibility payload atomically");
        vm.FlushFemWorkingSession();
        vm.AssignSection([frame.MemberName], other, cable: false);
        vm.FlushFemWorkingSession();
        Check(vm.CanUndoModel && vm.IsModelDirty && vm.WindowTitle.EndsWith(" *", StringComparison.Ordinal) &&
              vm.FemSectionProperties.Single(property => property.MemberName == frame.MemberName).SectionName == other,
            $"each edit is a step of the document's history — {bases[0]} fixed, then {frame.MemberName} on {other}; the title marks the model changed");

        bool undone = vm.UndoModel();
        var after = vm.FemSectionProperties.Single(property => property.MemberName == frame.MemberName);
        Check(undone && Same(after, frame) && vm.FemJointRestraints.GetValueOrDefault(bases[0]) == Native.Restraint.Fixed && vm.CanRedoModel,
            $"Undo takes the last edit back: {frame.MemberName} has its section again, {bases[0]} is still fixed" +
            (undone && Same(after, frame) ? "" : $" — undone {undone}, {after} against {frame}, restraint {vm.FemJointRestraints.GetValueOrDefault(bases[0])}, redo {vm.CanRedoModel}, {vm.Message}"));
        vm.FlushFemWorkingSession();
        Check(vm.CanRedoModel, "the model restored by Undo is written again as it is: no new step, Redo is still there");
        Check(vm.UndoModel() && vm.FemJointRestraints[bases[0]] == Native.Restraint.Pinned && !vm.CanUndoModel && !vm.IsModelDirty &&
              !vm.WindowTitle.EndsWith(" *", StringComparison.Ordinal),
            "Undo again: the base is pinned as it was opened, nothing more to undo, and the model is clean again");
        Check(vm.RedoModel() && vm.RedoModel() && vm.FemJointRestraints[bases[0]] == Native.Restraint.Fixed &&
              vm.FemSectionProperties.Single(property => property.MemberName == frame.MemberName).SectionName == other && !vm.CanRedoModel,
            "Redo twice makes both edits again");

        // the run reads the document that holds the edits; a run on hand locks the model, and a locked model has no Undo
        var run = FemModelFileChecks.Analyze(vm).SingleOrDefault();
        Check(run is not null && vm.IsFemModelLocked && !vm.CanUndoModel && !vm.UndoModel() &&
              run.ResultFor(null).SupportReactions.Single(reaction => reaction.PointName == bases[0]).MomentYKNm != 0,
            "the run solves the model as edited (the fixed base carries a moment); with its results on hand the model is locked and Undo waits for Unlock");

        // Save: the file holds the document, which is clean until the next edit
        var folder = Path.Combine(Path.GetTempPath(), "mcotis-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            vm.UnlockFemModel.Execute(null);
            var path = Path.Combine(folder, "edited" + McotisModelFile.Extension);
            vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            Check(!vm.IsModelDirty && !vm.WindowTitle.EndsWith(" *", StringComparison.Ordinal) && vm.NativeDocumentSession.FilePath == path,
                "Save makes the model clean: the document is what the file holds");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Check(warnings.Count == 0, "no warning on the way" + (warnings.Count > 0 ? ": " + string.Join(" | ", warnings) : ""));
    }

    /// <summary>The same section on the member, with the same stiffness and weight. (A model the application writes carries
    /// the out-of-plane inertia it fills in for a member that had none: a model written and read back holds it.)</summary>
    private static bool Same(Mcotis.Desktop.Geometry.FemSectionProperties a, Mcotis.Desktop.Geometry.FemSectionProperties b) =>
        a.SectionName == b.SectionName && a.MaterialName == b.MaterialName && a.AreaM2 == b.AreaM2 && a.InPlaneInertiaM4 == b.InPlaneInertiaM4 &&
        a.ElasticModulusKNPerM2 == b.ElasticModulusKNPerM2 && a.WeightPerLengthKNPerM == b.WeightPerLengthKNPerM &&
        a.ReleaseStartRotation == b.ReleaseStartRotation && a.ReleaseEndRotation == b.ReleaseEndRotation && a.LocalAngleDeg == b.LocalAngleDeg &&
        (a.Modifiers ?? []).SequenceEqual(b.Modifiers ?? []);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
