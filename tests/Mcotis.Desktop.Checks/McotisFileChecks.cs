using System.IO;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Infrastructure;
using Mcotis.Desktop.Presentation;
using Native = Mcotis.Core;

/// <summary>
/// The main window's file is a native <c>.mcotis</c> document since 2026-10-14 (M6 of the main-window migration):
/// Save writes the session's document, Open reads one, an older <c>.cotfem.db</c> / <c>.cotfem.json</c> opens as an
/// import whose file is never written, and a file that cannot be read leaves the model as it is. <c>--mcotis-file</c>,
/// and in the quick run.
/// </summary>
internal static class McotisFileChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcotis-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { Run(folder); }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Run(string folder)
    {
        // ---- Save: the document of the session, in one step, clean afterwards ----
        var (vm, _, _, warnings) = FemModelFileChecks.DefaultModelApp();
        var bases = vm.FemPinSupportPoints;
        vm.AssignJointRestraints([bases[0]], Native.Restraint.Fixed);
        vm.FlushFemWorkingSession();
        var path = Path.Combine(folder, "edited" + McotisModelFile.Extension);
        vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
        var saved = vm.NativeDocumentSession.Document;
        var (written, state) = McotisModelFile.Read(path);
        Check(File.Exists(path) && !vm.IsModelDirty && vm.ActiveModelFile == path && vm.CurrentFileName == "edited" + McotisModelFile.Extension &&
              vm.CanUndoModel && Directory.GetFiles(folder).Length == 1,
            "Save writes the model as a .mcotis file in one step (no file left beside it): the model is clean, the file is its file, its history stays");
        Check(written.DocumentId == saved.DocumentId && written.AnalysisInputHash == saved.AnalysisInputHash &&
              written.Points.Count == saved.Points.Count && written.Frames.Count == saved.Frames.Count && written.Cables.Count == saved.Cables.Count &&
              written.Generator == saved.Generator && !written.ApplicationData.ContainsKey(McotisModelFile.RunStateKey) &&
              NativeToLegacyProjection.Read(written) is not null && state is { IsLocked: false, Outputs.Count: 0 },
            $"the file is the session's document — {written.Points.Count} joints, {written.Frames.Count} frames, {written.Cables.Count} cables, " +
            "the same identity, analysis input and generator flag — with the run state of the file beside it (not locked, no results)");
        Check(new FemWorkingSessionStore().TryReadManifest()?.InputDatabasePath == path,
            "the working-session journal records the model's .mcotis file");

        // ---- Open: the document as it was saved, a history of its own, nothing dirty ----
        var (reopened, _, _, reopenWarnings) = FemModelFileChecks.DefaultModelApp();
        reopened.OpenFemModelFile(path);
        var opened = reopened.NativeDocumentSession.Document;
        Check(reopenWarnings.Count == 0 && opened.DocumentId == saved.DocumentId && opened.AnalysisInputHash == saved.AnalysisInputHash &&
              !reopened.IsModelDirty && !reopened.CanUndoModel && reopened.ActiveModelFile == path &&
              reopened.FemJointRestraints[bases[0]] == Native.Restraint.Fixed && reopened.GeneratorId == "VCF" && !reopened.CanOptimizeVcf &&
              reopened.GeneratorReport?.Breaking.Single().Object == $"Joint {bases[0]}",
            $"Open gives the model as it was saved: the same document, a history of its own, clean, {bases[0]} fixed — and the generator's verdict " +
            "with it: still the VCF generator's model, whose fixed base breaks the system for Optimization VCF" +
            (reopenWarnings.Count > 0 ? ": " + string.Join(" | ", reopenWarnings) : ""));
        var before = FemModelFileChecks.Analyze(vm).Single().ResultFor(null);
        var after = FemModelFileChecks.Analyze(reopened).Single().ResultFor(null);
        double worst = before.Joints.Zip(after.Joints).Max(pair => Math.Max(Math.Abs(pair.First.UXM - pair.Second.UXM), Math.Abs(pair.First.UZM - pair.Second.UZM)));
        Check(before.Joints.Select(joint => joint.PointName).SequenceEqual(after.Joints.Select(joint => joint.PointName)) && worst == 0,
            $"the model opened from its file analyses as the model that was saved: {before.Joints.Count} joints, largest difference {worst:0.###E+0} m");

        // ---- a locked model: its results come back with the file ----
        var lockedPath = Path.Combine(folder, "locked" + McotisModelFile.Extension);
        reopened.SaveFemModelFileAsync(lockedPath).GetAwaiter().GetResult();
        var (_, lockedState) = McotisModelFile.Read(lockedPath);
        var (results, _, _, _) = FemModelFileChecks.DefaultModelApp();
        results.OpenFemModelFile(lockedPath);
        Check(lockedState is { IsLocked: true, Outputs.Count: 1 } && results.IsFemModelLocked && results.HasFemResults &&
              results.SelectedFemRun?.ResultFor(null).Joints.Zip(after.Joints).All(pair => pair.First.UZM == pair.Second.UZM) == true,
            "a locked model is saved with its run state — the revision and its output database — and opens locked with its results");

        // ---- Save right after Open: nothing new in the history, the same model in the file ----
        var (again, _, _, _) = FemModelFileChecks.DefaultModelApp();
        again.OpenFemModelFile(path);
        again.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
        Check(!again.CanUndoModel && !again.IsModelDirty && McotisModelFile.Read(path).Document.AnalysisInputHash == saved.AnalysisInputHash,
            "saving a model just opened adds no step to its history and writes the same model");

        // ---- a detached model stays detached ----
        Check(again.DetachGenerator() && again.GeneratorId is null, "Detach from Generator removes the flag");
        var detachedPath = Path.Combine(folder, "detached" + McotisModelFile.Extension);
        again.SaveFemModelFileAsync(detachedPath).GetAwaiter().GetResult();
        var (detached, _, _, _) = FemModelFileChecks.DefaultModelApp();
        detached.OpenFemModelFile(detachedPath);
        Check(detached.GeneratorId is null && !detached.CanOptimizeVcf && detached.NativeDocumentSession.Document.Generator is null,
            "a model saved detached opens detached: Open never flags a model its file says no generator made");

        // ---- an older file: an import, never written ----
        var oldPath = Path.Combine(folder, "older" + FemInputDatabase.Extension);
        var (older, _, _, _) = FemModelFileChecks.DefaultModelApp();
        older.SaveFemModelFileAsync(oldPath).GetAwaiter().GetResult();   // the writer the checks keep to make older files
        var oldBytes = File.ReadAllBytes(oldPath);
        var oldTime = File.GetLastWriteTimeUtc(oldPath);
        var (imported, _, _, importWarnings) = FemModelFileChecks.DefaultModelApp();
        imported.OpenFemModelFile(oldPath);
        var asked = new List<(string? Folder, string Name)>();
        var answer = Path.Combine(folder, "older");
        imported.AskFemSavePath = (start, name) => { asked.Add((start, name)); return answer; };
        imported.SaveFemModel.Execute(null);
        var importedPath = answer + McotisModelFile.Extension;
        Check(importWarnings.Count == 0 && asked.Count == 1 && asked[0] == (folder, "older" + McotisModelFile.Extension) &&
              File.Exists(importedPath) && imported.ActiveModelFile == importedPath &&
              File.ReadAllBytes(oldPath).SequenceEqual(oldBytes) && File.GetLastWriteTimeUtc(oldPath) == oldTime,
            $"an older {FemInputDatabase.Extension} opens as an import: Save asks for a .mcotis file (offered as older{McotisModelFile.Extension} beside it) and the older file is never written");
        var (fromImport, _, _, _) = FemModelFileChecks.DefaultModelApp();
        fromImport.OpenFemModelFile(importedPath);
        Check(FemInputDatabase.RevisionHash(NativeToLegacyProjection.Read(fromImport.NativeDocumentSession.Document)!) ==
              FemInputDatabase.RevisionHash(NativeToLegacyProjection.Read(imported.NativeDocumentSession.Document)!),
            "the .mcotis file written from the import holds the configuration of the older file");

        // ---- files that cannot be opened leave the model as it is ----
        var badPath = Path.Combine(folder, "damaged" + McotisModelFile.Extension);
        File.WriteAllText(badPath, "{ \"format\": \"Mcotis.Document\", \"schemaVersion\": 6, \"documentId\": ");
        // a native model this window cannot hold yet (M8 takes native models in): a joint load along Y
        var nativePath = Path.Combine(folder, "workspace" + McotisModelFile.Extension);
        var workspace = Native.ModelDocument.Create().Edit();
        var material = new Native.MaterialDefinition(Native.ObjectId.New(), "Steel", Native.MaterialType.Steel, 2e8, .3, 78.5, 8);
        var section = new Native.FrameSectionDefinition(Native.ObjectId.New(), "Beam", material.Id, Native.FrameSectionShape.General, new(.01, .0008, .0004, .00001));
        workspace.PutMaterial(material);
        workspace.PutFrameSection(section);
        var a = workspace.AddPoint(new(0, 0, 0), "A").Id;
        var b = workspace.AddPoint(new(4, 0, 0), "B").Id;
        workspace.SetRestraint([a], Native.Restraint.Fixed);
        workspace.SetFrameSection([workspace.AddFrame(a, b, "Arm").Id], section.Id);
        var pattern = new Native.LoadPattern(Native.ObjectId.New(), "SIDE", Native.LoadPatternType.Other);
        workspace.PutLoadPattern(pattern);
        workspace.PutLoad(new Native.PointLoad(Native.ObjectId.New(), pattern.Id, b, new(0, 5, 0)));
        new Mcotis.Persistence.NativeDocumentStore().Save(nativePath, workspace.Commit().Document);
        var (refusing, _, _, refusals) = FemModelFileChecks.DefaultModelApp();
        var kept = refusing.NativeDocumentSession.Document;
        refusing.OpenFemModelFile(badPath);
        refusing.OpenFemModelFile(nativePath);
        Check(refusals.Count == 2 && ReferenceEquals(refusing.NativeDocumentSession.Document, kept) && refusing.IsDefaultModel &&
              refusals[1].Contains("Joint B: a load along Y", StringComparison.Ordinal),
            "a damaged file, and a native model with what this window cannot hold yet (a joint load along Y), are refused with the reason; the open model stays: " +
            string.Join(" | ", refusals.Select(text => text.Length > 110 ? text[..110] + "…" : text)));

        RunOptimizationStudy(folder);
        Check(warnings.Count == 0, "no warning on the way" + (warnings.Count > 0 ? ": " + string.Join(" | ", warnings) : ""));
    }

    /// <summary>M7: the optimization study shown is saved beside the model (its results folder) and shown again when the
    /// model is opened — said to be of the model before later edits when it is; a model without one opens without one.</summary>
    private static void RunOptimizationStudy(string folder)
    {
        var (vm, state, _, warnings) = FemModelFileChecks.DefaultModelApp();
        var analysis = Mcotis.Desktop.Application.VcfGeometry.Analyze(state.Geometry, state.Truss);
        var current = VcfOptimization.Current(analysis);
        var live = state.LoadDefinitions.Single(load => load.ApplyIncrementStep);
        var dead = state.LoadDefinitions.Single(load => !load.ApplyIncrementStep);
        var settings = current with
        {
            Geometry = new Dictionary<string, VcfRange>(current.Geometry) { ["Bay"] = new VcfRange(10, 12, 2) },
            CableForces = VcfOptimization.ModelCableForces(state.Cables.ToArray()),
            StoreyLoads = new Dictionary<string, VcfRange>(StringComparer.Ordinal)
            {
                [dead.LoadName] = VcfOptimization.LoadRange(dead)!, [live.LoadName] = VcfRange.Fixed(live.MinQz!.Value, 1),
                [VcfOptimization.StoriesKey] = VcfRange.Fixed(Math.Truncate(state.NumberOfStories), 1),
                [VcfOptimization.TributaryKey] = VcfRange.Fixed(state.TributaryWidthB, 0.5),
            },
        };
        var rows = FemModelFileChecks.Optimize(vm, settings);
        var report = vm.OptimizationReport;
        var path = Path.Combine(folder, "studied" + McotisModelFile.Extension);
        vm.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
        var beside = Path.Combine(McotisModelFile.ResultsFolder(path), SavedOptimizationStudy.FileName);

        var (reopened, _, _, _) = FemModelFileChecks.DefaultModelApp();
        reopened.OpenFemModelFile(path);
        Check(rows.Length == 2 && rows.All(row => row.MaxDeflectionMm > 0) && File.Exists(beside) && reopened.VcfOptimizationRows.SequenceEqual(rows, RowComparer.Instance) &&
              reopened.VcfOptimizationColumns.Select(axis => axis.Label).SequenceEqual(vm.VcfOptimizationColumns.Select(axis => axis.Label)) &&
              reopened.OptimizationTitle == "Optimization VCF" && reopened.OptimizationReport.Summary.SequenceEqual(report.Summary) &&
              reopened.OptimizationReport.Charts.Count == report.Charts.Count &&
              reopened.VcfOptimizationProgress.StartsWith("Saved with the model", StringComparison.Ordinal) && !reopened.VcfOptimizationProgress.Contains("before later edits", StringComparison.Ordinal),
            $"the optimization study ({rows.Length} cases) is saved beside the model, in {Path.GetFileName(McotisModelFile.ResultsFolder(path))}, and comes back with it: " +
            "every row, the columns, the Chart page and its summary" +
            $" (file exists: {File.Exists(beside)}; reopened rows: {reopened.VcfOptimizationRows.Count}; rows match: {reopened.VcfOptimizationRows.SequenceEqual(rows, RowComparer.Instance)}; progress: {reopened.VcfOptimizationProgress}; warnings: {string.Join("; ", warnings)})");

        // an edit after the study: the study saved with the model is said to be of the model before it
        var bases = reopened.FemPinSupportPoints;
        reopened.AssignJointRestraints([bases[0]], Native.Restraint.Fixed);
        reopened.FlushFemWorkingSession();
        reopened.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
        var (edited, _, _, _) = FemModelFileChecks.DefaultModelApp();
        edited.OpenFemModelFile(path);
        Check(edited.VcfOptimizationRows.Count == rows.Length && edited.VcfOptimizationProgress.Contains("Of the model before later edits", StringComparison.Ordinal),
            "a study run before the model was edited comes back said to be of the model before the edits");

        // a model without a study: the window is empty, not left with another model's study
        var other = Path.Combine(folder, "plain" + McotisModelFile.Extension);
        var (plain, _, _, _) = FemModelFileChecks.DefaultModelApp();
        plain.SaveFemModelFileAsync(other).GetAwaiter().GetResult();
        edited.OpenFemModelFile(other);
        Check(edited.VcfOptimizationRows.Count == 0 && edited.OptimizationReport.Charts.Count == 0 && warnings.Count == 0,
            "opening a model without a study empties the results: no model shows another model's study");
    }

    private sealed class RowComparer : IEqualityComparer<VcfOptimizationRow>
    {
        public static RowComparer Instance { get; } = new();
        public bool Equals(VcfOptimizationRow? a, VcfOptimizationRow? b) =>
            a is not null && b is not null && a with { Values = [], Members = null } == b with { Values = [], Members = null } &&
            a.Values.SequenceEqual(b.Values) && (a.Members is null ? b.Members is null : b.Members is not null && a.Members.SequenceEqual(b.Members));
        public int GetHashCode(VcfOptimizationRow row) => row.Case.GetHashCode();
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
