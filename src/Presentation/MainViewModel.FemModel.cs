using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using COT_VCF.Geometry;
using COT_VCF.Infrastructure;
using Microsoft.Win32;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;

namespace COT_VCF.Presentation;

// ---- 07 FEM → Inputs: Save Model / Load Model ----
// Save Model writes everything the FEM run reads from SAP2000 and Tabs 01–05 into one file. Load Model restores it
// as an offline model: the FEM tab works (Run Analysis, Results, View) without connecting SAP2000 or loading a config.
// Commands that need the live SAP2000 model stay disabled until Connect SAP succeeds.
public sealed partial class MainViewModel
{
    private string? offlineModelPath;
    /// <summary>The offline model is the built-in default (not a file the user chose).</summary>
    private bool isDefaultModel;
    private IReadOnlyList<string> offlineLoadCaseNames = [];
    private Command? saveFemModel, loadFemModel, loadDefaultModel;
    private FemInputSnapshot? femInputSnapshot;
    private string? femInputDatabasePath;
    private string? femWorkingDatabasePath;
    private readonly List<FemOutputRegistration> femOutputRegistrations = [];
    private DispatcherTimer? femWorkingSaveTimer;
    private bool applyingFemDatabase;
    private bool femWorkingDirty;

    public bool IsFemModelLocked => femInputSnapshot?.IsLocked == true;
    public bool CanEditFemModel => !IsBusy && !IsFemModelLocked;
    public bool CanUnlockFemModel => !IsBusy && IsFemModelLocked;
    public string FemModelEditState => isFemRunning ? "RUNNING" : IsFemModelLocked
        ? $"LOCKED · REV {femInputSnapshot!.LockedRevision ?? femInputSnapshot.Revision}"
        : femWorkingDirty ? "UNLOCKED · MODIFIED" : "UNLOCKED";
    public string FemDatabaseSummary => femInputSnapshot is null ? "No FEM database session"
        : $"Rev {femInputSnapshot.Revision} · {Path.GetFileName(femInputDatabasePath ?? femWorkingDatabasePath ?? "working database")}";

    /// <summary>A saved FEM model is loaded and SAP2000 is not connected.</summary>
    public bool IsOfflineModel => offlineModelPath is not null && State.Model is not null && !State.IsSapConnected;
    /// <summary>The workflow pages have a model to work on: the live SAP2000 model or a loaded FEM model.</summary>
    public bool HasWorkingModel => HasSapConnection || IsOfflineModel;
    public string OfflineModelName => offlineModelPath is null ? "" : Path.GetFileName(offlineModelPath);
    public bool IsDefaultModel => IsOfflineModel && isDefaultModel;
    public string OfflineModelSummary => IsOfflineModel
        ? $"{(isDefaultModel ? "Default model" : "Offline model")} {OfflineModelName} · from {State.Model?.Name} — " +
          "Run Analysis solves this model as defined; SAP2000 is not used."
        : "";

    public bool CanSaveFemModel => !IsBusy && HasWorkingModel && State.BaselineCaptured &&
        State.Geometry.Members.Any(member => member.Kind is MemberKind.Frame or MemberKind.Cable);
    public bool CanLoadFemModel => !IsBusy;

    public Command SaveFemModel => saveFemModel ??= new(_ => SaveFemModelToFile(), () => CanSaveFemModel);
    public Command LoadFemModel => loadFemModel ??= new(_ => LoadFemModelFromFile(), () => CanLoadFemModel);
    /// <summary>File → Default Model: drop the current model (live or loaded) and open the built-in default model.</summary>
    public Command LoadDefaultModel => loadDefaultModel ??= new(_ => LoadDefaultFemModel(replaceCurrent: true), () => CanLoadFemModel);

    private async void SaveFemModelToFile()
    {
        if (IsBusy || CommitEditors?.Invoke() == false) return;
        var dialog = new SaveFileDialog
        {
            Filter = FemInputDatabase.Filter,
            FileName = SuggestFemDatabaseFileName(),
            AddExtension = false,
        };
        if (offlineModelPath is not null && !isDefaultModel) dialog.InitialDirectory = Path.GetDirectoryName(offlineModelPath);
        else if (State.Model is { Path.Length: > 0 } model && Directory.Exists(Path.GetDirectoryName(model.Path))) dialog.InitialDirectory = Path.GetDirectoryName(model.Path);
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FilterIndex == 2 || dialog.FileName.EndsWith(FemModelFile.Extension, StringComparison.OrdinalIgnoreCase)
            ? dialog.FileName
            : dialog.FileName.EndsWith(FemInputDatabase.Extension, StringComparison.OrdinalIgnoreCase)
                ? dialog.FileName : dialog.FileName.TrimEnd('.') + FemInputDatabase.Extension;
        await SaveFemModelFileAsync(path);
    }

    /// <summary>Writes the FEM model to <paramref name="path"/> — what Save Model does once the file is chosen.</summary>
    internal async Task SaveFemModelFileAsync(string path)
    {
        if (IsBusy || !CanSaveFemModel) return;
        IsBusy = true;
        try
        {
            IReadOnlyDictionary<string, FemSectionProperties> properties;
            IReadOnlyList<string> loadCases;
            if (IsOfflineModel)
            {
                properties = HeldFemProperties();
                loadCases = offlineLoadCaseNames;
            }
            else
            {
                FemProgressText = "Reading sections, materials, loading, cases and combinations from SAP2000 for Save Model…";
                properties = await ReadAndDisplayFemPropertiesAsync();
                loadCases = await workflow.GetLoadCaseNamesAsync();
            }
            var file = BuildFemModelFile(properties, loadCases);
            if (path.EndsWith(FemModelFile.Extension, StringComparison.OrdinalIgnoreCase))
            {
                FemModelFile.Write(path, file);
            }
            else
            {
                var snapshot = FemInputSnapshot.FromLegacy(file, femInputSnapshot);
                if (IsFemModelLocked && femInputSnapshot is not null)
                {
                    snapshot.IsLocked = true;
                    snapshot.LockedRevision = femInputSnapshot.LockedRevision ?? femInputSnapshot.Revision;
                    snapshot.Revision = femInputSnapshot.Revision;
                }
                FemInputDatabase.Write(path, snapshot, femOutputRegistrations);
                femInputSnapshot = snapshot;
                femInputDatabasePath = path;
                femWorkingDirty = false;
                femWorkingDatabasePath = femSessionStore.Save(snapshot, femOutputRegistrations, path);
            }
            FemProgressText = $"Saved FEM model {Path.GetFileName(path)} · {file.SectionProperties.Count:N0} members, {file.CaseRows.Count} load cases, " +
                $"{file.CombinationRows.Count} combinations. Load Model reopens it without SAP2000.";
            Message = FemProgressText;
        }
        catch (Exception ex)
        {
            FemProgressText = $"Save Model failed: {ex.Message}";
            Message = FemProgressText;
            Warn("Save Model", ex.Message);
            if (ex is System.Runtime.InteropServices.COMException) State.Connection = "Connection Lost";
        }
        finally { IsBusy = false; Update(); }
    }

    private string SuggestFemModelFileName()
    {
        var stem = State.Model is { Path.Length: > 0 } model ? Path.GetFileNameWithoutExtension(model.Path) : "Mcotis";
        return stem + "_FEM" + FemModelFile.Extension;
    }

    private string SuggestFemDatabaseFileName()
    {
        var stem = State.Model is { Path.Length: > 0 } model ? Path.GetFileNameWithoutExtension(model.Path) : "Mcotis";
        return stem + "_FEM" + FemInputDatabase.Extension;
    }

    private FemModelFileDto BuildFemModelFile(IReadOnlyDictionary<string, FemSectionProperties> properties, IReadOnlyList<string> loadCases)
    {
        var project = new ConfigurationExtractor().Extract(State, loadCases.ToArray(), ConfigTabs.All);
        return new FemModelFileDto
        {
            SavedUtc = DateTime.UtcNow,
            Model = State.Model,
            SessionBaseline = State.SessionBaseline.Points.Count > 0 ? State.SessionBaseline : null,
            Geometry = State.Geometry,
            Baseline = State.Baseline,
            LoadPatterns = State.LoadPatterns.ToList(),
            LoadCaseNames = loadCases.ToList(),
            Project = project,
            SectionProperties = properties.Values.OrderBy(property => property.MemberName, StringComparer.Ordinal).ToList(),
            Materials = Materials.Count > 0 ? Materials.ToList() : null,
            FrameSections = FrameSections.Count > 0 ? FrameSections.ToList() : null,
            CableSections = CableSections.Count > 0 ? CableSections.ToList() : null,
            TendonSections = TendonSections.Count > 0 ? TendonSections.ToList() : null,
            Loading = femSapLoading,
            PatternRows = FemPatternRows.Select(row => new FemPatternRowDto(row.Name, row.Type, row.SelfWeightMultiplier, row.Role, row.Loads)).ToList(),
            CaseRows = FemCaseRows.Select(row => new FemDefinitionRowDto(row.Name, row.Type, row.Definition, row.InitialCase, row.Run, row.Supported, row.Source, row.Note, row.CableTargetForces)).ToList(),
            CombinationRows = FemCombinationRows.Select(row => new FemDefinitionRowDto(row.Name, row.Type, row.Definition, null, row.Run, row.Supported, row.Source, row.Note)).ToList(),
            Settings = new FemSettingsDto
            {
                TargetFitPercent = FemTargetFitPercent,
                TargetFitIterations = FemTargetFitIterations,
                FrameMeshSegments = FemFrameMeshSegments,
                CableSag = FemCableSag,
            },
        };
    }

    private void LoadFemModelFromFile()
    {
        if (IsBusy || CommitEditors?.Invoke() == false) return;
        var dialog = new OpenFileDialog { Filter = FemInputDatabase.Filter, CheckFileExists = true };
        if (offlineModelPath is not null && !isDefaultModel) dialog.InitialDirectory = Path.GetDirectoryName(offlineModelPath);
        else if (State.Model is { Path.Length: > 0 } model && Directory.Exists(Path.GetDirectoryName(model.Path))) dialog.InitialDirectory = Path.GetDirectoryName(model.Path);
        if (dialog.ShowDialog() == true) OpenFemModelFile(dialog.FileName);
    }

    /// <summary>Opens the built-in default FEM model: 07 FEM ready to run. At startup it only opens while SAP2000 is not
    /// connected; with <paramref name="replaceCurrent"/> (File → Default Model) it replaces whatever model is open.</summary>
    internal void LoadDefaultFemModel(bool replaceCurrent = false)
    {
        if (IsBusy || (State.IsSapConnected && !replaceCurrent)) return;
        if (CommitEditors?.Invoke() == false) return;
        FemModelFileDto file;
        try { file = FemModelFile.ReadDefault(); }
        catch (Exception ex)
        {
            Message = $"Default FEM model unavailable: {ex.Message}";
            return;
        }
        ApplyFemModel(file, FemModelFile.DefaultModelName, isDefault: true);
    }

    /// <summary>Restores a saved FEM model as an offline model — what Load Model does once the file is chosen.</summary>
    internal void OpenFemModelFile(string path)
    {
        if (IsBusy) return;
        FemModelFileDto file;
        FemInputDatabaseRead? database = null;
        try
        {
            if (path.EndsWith(FemInputDatabase.Extension, StringComparison.OrdinalIgnoreCase))
            {
                database = FemInputDatabase.Read(path);
                file = database.Snapshot.ToLegacy();
            }
            else file = FemModelFile.Read(path);
        }
        catch (Exception ex)
        {
            Warn("Load Model", ex.Message);
            Message = "Load Model cancelled — the current model is unchanged.";
            return;
        }
        ApplyFemModel(file, path, isDefault: false, database?.Snapshot, database?.Outputs);
    }

    private void ApplyFemModel(FemModelFileDto file, string path, bool isDefault, FemInputSnapshot? databaseSnapshot = null,
        IReadOnlyList<FemOutputRegistration>? outputs = null)
    {
        IsBusy = true;
        applyingFemDatabase = true;
        try
        {
            ClearFemResults();
            State.Connection = "Offline model";
            State.Model = file.Model;
            State.SessionBaselineModel = file.SessionBaseline is null ? null : file.Model;
            State.SessionBaseline = file.SessionBaseline ?? GeometryModel.Empty;
            State.SessionBaselinePointLoads = [];
            State.SessionBaselinePretensionForces = new Dictionary<string, double>(StringComparer.Ordinal);
            State.Geometry = file.Geometry!;
            State.Baseline = file.Baseline!;
            State.Preview = file.Geometry!;
            State.LoadPatterns.Clear();
            foreach (var pattern in file.LoadPatterns) State.LoadPatterns.Add(pattern);
            offlineLoadCaseNames = file.LoadCaseNames;

            var check = new ConfigCompatibilityValidator().Validate(file.Project!, State, file.LoadCaseNames);
            if (!check.FileValid || !check.AllPresentOk)
                throw new InvalidDataException("The saved Tabs 01–05 project does not match the saved geometry:\n\n" +
                    string.Join("\n", check.FileErrors.Concat(new[] { check.Loading, check.Truss, check.Cable, check.Output, check.Study }
                        .Where(tab => tab.Present && !tab.Ok).SelectMany(tab => tab.Problems.Select(problem => problem.ToString())))));
            applyingProject = true;
            try
            {
                new ConfigurationApplicator(State, workflow).Reset();
                config.Apply(file.Project!, check);
            }
            finally { applyingProject = false; }
            // The model file is not a project file: the next Save All/Save Config (after Connect SAP) chooses a file.
            config.Session.Close();
            config.Session.ClearDirty();

            FemSectionProperties.Clear();
            foreach (var property in file.SectionProperties) FemSectionProperties.Add(property);
            Materials.Clear();
            foreach (var material in file.Materials ?? []) Materials.Add(material);
            ResetMaterialLibrary();
            EnsureMaterials();
            FrameSections.Clear(); CableSections.Clear(); TendonSections.Clear();
            foreach (var section in file.FrameSections ?? []) FrameSections.Add(section);
            foreach (var section in file.CableSections ?? []) CableSections.Add(section);
            foreach (var section in file.TendonSections ?? []) TendonSections.Add(section);
            EnsureSections();
            femSapLoading = file.Loading!;
            RestoreFemLoadRows(file);
            // The built-in model starts on the app's default load cases; SAP Cases shows the ones saved with it.
            if (isDefault) UseDefaultFemLoadCases();
            FemTargetFitPercent = file.Settings.TargetFitPercent;
            FemTargetFitIterations = file.Settings.TargetFitIterations;
            FemFrameMeshSegments = file.Settings.FrameMeshSegments;
            FemCableSag = file.Settings.CableSag;

            offlineModelPath = path;
            isDefaultModel = isDefault;
            femInputSnapshot = databaseSnapshot ?? FemInputSnapshot.FromLegacy(file);
            femInputDatabasePath = databaseSnapshot is null ? null : path;
            femOutputRegistrations.Clear();
            femOutputRegistrations.AddRange(outputs ?? []);
            femWorkingDirty = false;
            femWorkingDatabasePath = femSessionStore.Save(femInputSnapshot, femOutputRegistrations, femInputDatabasePath);
            RestoreFemOutputs();
            SelectedAppliedPoint = null;
            SelectedCable = null;
            RegenerateStudyPreview();
            SelectedTab = TabFem;
            SelectedFemPage = FemInputsPage;
            FemProgressText = $"Loaded {(isDefault ? "default " : "")}FEM model {Path.GetFileName(path)} (saved {file.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}). " +
                "Run Analysis solves this model as defined, without SAP2000; Connect SAP for the SAP2000 study tabs.";
            Message = $"{(isDefault ? "Default FEM model" : "FEM model")} {Path.GetFileName(path)} loaded offline from {file.Model!.Name} — SAP2000 not connected.";
        }
        catch (Exception ex)
        {
            offlineModelPath = null;
            State.Connection = "Disconnected";
            FemProgressText = $"Load Model failed: {ex.Message}";
            Message = "Load Model failed.";
            Warn("Load Model", ex.Message);
        }
        finally
        {
            IsBusy = false;
            Notify(nameof(SharedViewerMemberSizes));
            Notify(nameof(SharedViewerSections));
            Notify(nameof(FemMemberSizeHint));
            Update();
            applyingFemDatabase = false;
        }
    }

    private void RestoreFemLoadRows(FemModelFileDto file)
    {
        SetFemLoadRows(
            file.PatternRows.Select(row => new FemPatternRow(row.Name, row.Type, row.SelfWeightMultiplier, row.Role, row.Loads)).ToArray(),
            file.CaseRows.Select(row =>
            {
                var restored = new FemCaseRow(row.Name, row.Type, row.Definition, row.InitialCase, row.Run, row.Supported, row.Source, row.Note);
                restored.CableTargetForces = row.CableTargetForces ?? Terms(restored).Any(term => PretensionPatterns.Contains(term.Name, StringComparer.OrdinalIgnoreCase));
                return restored;
            }).ToArray(),
            file.CombinationRows.Select(row => new FemCombinationRow(row.Name, row.Type, row.Definition, row.Run, row.Supported, row.Source, row.Note)).ToArray());
        FemLoadWarnings.Clear();
        foreach (var warning in femSapLoading.Warnings) FemLoadWarnings.Add(warning);
        Notify(nameof(FemLoadSummary));
        var names = FemLoadSelectionNames;
        SelectedFemResultName = FemCombinationRows.FirstOrDefault(row => row.Run)?.Name ?? names.LastOrDefault();
    }

    /// <summary>Connect SAP replaces the built-in default model: its geometry, Tabs 01–05 and FEM inputs are cleared
    /// so the connected model starts clean (a model the user loaded keeps its inputs instead).</summary>
    private void ClearDefaultFemModel()
    {
        if (IsFemModelLocked) DeleteRegisteredFemOutputs();
        State.Geometry = GeometryModel.Empty;
        State.Baseline = GeometryModel.Empty;
        State.Preview = GeometryModel.Empty;
        State.LoadPatterns.Clear();
        applyingProject = true;
        try { new ConfigurationApplicator(State, workflow).Reset(); }
        finally { applyingProject = false; }
        ClearFemResults();
        FemSectionProperties.Clear();
        femSapLoading = SapFemLoading.Empty;
        SetFemLoadRows([], [], []);
        FemLoadWarnings.Clear();
        offlineModelPath = null;
        isDefaultModel = false;
        femInputSnapshot = null;
        femInputDatabasePath = null;
        femWorkingDatabasePath = null;
        femOutputRegistrations.Clear();
        femWorkingDirty = false;
    }

    private void ClearFemResults()
    {
        SelectedFemRun = null;
        FemRuns.Clear();
        FemCableFitRows.Clear();
        FemLog.Clear();
        FemProgressText = "";
        NotifyFemMonitorChart();
    }

    private FemInputSnapshot CaptureFemInputSnapshot(bool advanceRevision)
    {
        var loadCases = offlineLoadCaseNames.Count > 0 ? offlineLoadCaseNames : FemLoadSelectionNames;
        var file = BuildFemModelFile(HeldFemProperties(), loadCases);
        var snapshot = FemInputSnapshot.FromLegacy(file, femInputSnapshot);
        if (!advanceRevision && femInputSnapshot is not null) snapshot.Revision = femInputSnapshot.Revision;
        snapshot.IsLocked = IsFemModelLocked;
        snapshot.LockedRevision = femInputSnapshot?.LockedRevision;
        return snapshot;
    }

    internal void ScheduleFemWorkingSave()
    {
        if (applyingFemDatabase || IsFemModelLocked || !HasWorkingModel || State.Baseline.Points.Count == 0 || FemSectionProperties.Count == 0) return;
        femWorkingDirty = true;
        femWorkingSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        femWorkingSaveTimer.Stop();
        femWorkingSaveTimer.Tick -= SaveWorkingTimerTick;
        femWorkingSaveTimer.Tick += SaveWorkingTimerTick;
        femWorkingSaveTimer.Start();
        Notify(nameof(FemModelEditState));
    }

    private void SaveWorkingTimerTick(object? sender, EventArgs e)
    {
        femWorkingSaveTimer?.Stop();
        if (IsBusy || applyingFemDatabase || IsFemModelLocked) return;
        try { PersistFemWorkingSnapshot(advanceRevision: true); }
        catch (Exception ex) { Message = $"Working database autosave failed: {ex.Message}"; }
    }

    private void PersistFemWorkingSnapshot(bool advanceRevision)
    {
        var snapshot = CaptureFemInputSnapshot(advanceRevision);
        femInputSnapshot = snapshot;
        femWorkingDatabasePath = femSessionStore.Save(snapshot, femOutputRegistrations, femInputDatabasePath);
        femWorkingDirty = false;
        Notify(nameof(IsFemModelLocked));
        Notify(nameof(CanEditFemModel));
        Notify(nameof(CanUnlockFemModel));
        Notify(nameof(FemModelEditState));
        Notify(nameof(FemDatabaseSummary));
    }

    internal void FlushFemWorkingSession()
    {
        femWorkingSaveTimer?.Stop();
        if (!femWorkingDirty || applyingFemDatabase || IsBusy || IsFemModelLocked || !HasWorkingModel) return;
        try { PersistFemWorkingSnapshot(advanceRevision: true); }
        catch { /* shutdown remains best effort; the prior working database is still valid */ }
    }

    internal bool TryRestoreFemWorkingSession()
    {
        var manifest = femSessionStore.TryReadManifest();
        var database = femSessionStore.TryReadActive();
        if (manifest is null || database is null) return false;
        try
        {
            var displayPath = manifest.InputDatabasePath ?? manifest.WorkingDatabasePath;
            ApplyFemModel(database.Snapshot.ToLegacy(), displayPath, isDefault: false, database.Snapshot, database.Outputs);
            femInputDatabasePath = manifest.InputDatabasePath;
            femWorkingDatabasePath = manifest.WorkingDatabasePath;
            Message = $"Recovered FEM working revision {database.Snapshot.Revision} from the previous session.";
            return true;
        }
        catch { return false; }
    }

    private void LockFemModelForRun()
    {
        if (IsFemModelLocked) return;
        var snapshot = CaptureFemInputSnapshot(advanceRevision: true);
        snapshot.IsLocked = true;
        snapshot.LockedRevision = snapshot.Revision;
        femInputSnapshot = snapshot;
        femWorkingDirty = false;
        femWorkingDatabasePath = femSessionStore.Save(snapshot, femOutputRegistrations, femInputDatabasePath);
        NotifyFemDatabaseState();
    }

    private void UnlockFemModelForEditing()
    {
        if (!CanUnlockFemModel || femInputSnapshot is null) return;
        try
        {
            DeleteRegisteredFemOutputs();
            ClearFemResults();
            femInputSnapshot.IsLocked = false;
            femInputSnapshot.LockedRevision = null;
            femInputSnapshot.Revision++;
            femInputSnapshot.SavedUtc = DateTime.UtcNow;
            femWorkingDatabasePath = femSessionStore.Save(femInputSnapshot, femOutputRegistrations, femInputDatabasePath);
            femWorkingDirty = false;
            FemProgressText = "Model unlocked. Previous output databases were deleted; Run Analysis is required to create new results.";
            Message = FemProgressText;
            NotifyFemDatabaseState();
            Update();
        }
        catch (Exception ex)
        {
            Warn("Unlock FEM Model", $"The model remains locked because its output cleanup did not complete.\n\n{ex.Message}");
        }
    }

    private void DeleteRegisteredFemOutputs()
    {
        if (femInputSnapshot is null) return;
        foreach (var registration in femOutputRegistrations.ToArray())
        {
            if (!File.Exists(registration.Path)) { femOutputRegistrations.Remove(registration); continue; }
            var (info, _) = FemOutputDatabase.Read(registration.Path);
            if (info.InputDatabaseId != femInputSnapshot.DatabaseId || info.InputRevision != registration.Revision ||
                !string.Equals(info.InputRevisionHash, registration.RevisionHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Refusing to delete unverified output '{registration.Path}'.");
            File.Delete(registration.Path);
            femOutputRegistrations.Remove(registration);
            var directory = Path.GetDirectoryName(registration.Path);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    private void RestoreFemOutputs()
    {
        if (femInputSnapshot is null) return;
        if (!femInputSnapshot.IsLocked)
        {
            if (femOutputRegistrations.Count > 0) DeleteRegisteredFemOutputs();
            return;
        }
        var hash = FemInputDatabase.RevisionHash(femInputSnapshot);
        foreach (var registration in femOutputRegistrations.OrderBy(item => item.CreatedUtc))
        {
            if (!File.Exists(registration.Path) || registration.DatabaseId != femInputSnapshot.DatabaseId ||
                registration.Revision != femInputSnapshot.LockedRevision || !string.Equals(registration.RevisionHash, hash, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var (info, run) = FemOutputDatabase.Read(registration.Path);
                if (info.State != "Completed" || info.InputDatabaseId != femInputSnapshot.DatabaseId || info.InputRevisionHash != hash) continue;
                FemRuns.Add(run);
                SelectedFemRun = run;
                femResultOrder = run.Results?.Keys.ToArray() ?? [];
                SelectedFemResultName = femResultOrder.FirstOrDefault();
            }
            catch (Exception) { /* leave a damaged output registered so Unlock can report it instead of deleting blindly */ }
        }
    }

    private string WriteFemOutput(FemStudyRun run)
    {
        if (femInputSnapshot is not { IsLocked: true, LockedRevision: long revision })
            throw new InvalidOperationException("FEM output requires a locked input revision.");
        var source = femInputDatabasePath ?? femWorkingDatabasePath ?? "working database";
        var sourceDirectory = Path.GetDirectoryName(source) ?? AppDataPaths.SessionsDirectory;
        var stem = Path.GetFileName(source);
        if (stem.EndsWith(FemInputDatabase.Extension, StringComparison.OrdinalIgnoreCase)) stem = stem[..^FemInputDatabase.Extension.Length];
        else stem = Path.GetFileNameWithoutExtension(stem);
        var directory = Path.Combine(sourceDirectory, stem + ".results", "output");
        Directory.CreateDirectory(directory);
        var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{SafeFileName(run.Id)}";
        var path = Path.Combine(directory, runId + FemOutputDatabase.Extension);
        var hash = FemInputDatabase.RevisionHash(femInputSnapshot);
        var info = new FemOutputDatabaseInfo(runId, femInputSnapshot.DatabaseId, revision, hash, DateTime.UtcNow, "Completed", source);
        FemOutputDatabase.Write(path, info, run);
        femOutputRegistrations.Add(new(path, runId, femInputSnapshot.DatabaseId, revision, hash, info.CreatedUtc));
        femWorkingDatabasePath = femSessionStore.Save(femInputSnapshot, femOutputRegistrations, femInputDatabasePath);
        Notify(nameof(FemDatabaseSummary));
        return path;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return result.Length == 0 ? "run" : result;
    }

    private void NotifyFemDatabaseState()
    {
        Notify(nameof(IsFemModelLocked));
        Notify(nameof(CanEditFemModel));
        Notify(nameof(CanUnlockFemModel));
        Notify(nameof(FemModelEditState));
        Notify(nameof(FemDatabaseSummary));
        Notify(nameof(FemStatus));
        Notify(nameof(CanRunFemAnalysis));
        CommandManager.InvalidateRequerySuggested();
    }
}
