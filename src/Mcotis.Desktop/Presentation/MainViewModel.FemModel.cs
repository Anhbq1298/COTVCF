using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Infrastructure;
using Microsoft.Win32;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;

namespace Mcotis.Desktop.Presentation;

// ---- 07 FEM → Inputs: Save Model / Load Model ----
// Save Model writes everything the FEM run reads from SAP2000 and Tabs 01–05 into one file. Load Model restores it
// as an offline model: the FEM tab works (Run Analysis, Results, View) without connecting SAP2000 or loading a config.
// Commands that need the live SAP2000 model stay disabled until Connect SAP succeeds.
public sealed partial class MainViewModel
{
    private LegacyNativeDocumentMigration.Source nativeDocumentSource =
        LegacyNativeDocumentMigration.Source.Imported("Mcotis legacy config");
    /// <summary>The model open in the workspace has been given its source — a template, an import, a file.</summary>
    private bool nativeDocumentSourceKnown;
    private string? offlineModelPath;
    /// <summary>The offline model is the built-in default (not a file the user chose).</summary>
    private bool isDefaultModel;
    private IReadOnlyList<string> offlineLoadCaseNames = [];
    private Command? saveFemModel, saveFemModelAs, loadFemModel, loadDefaultModel, continueWithBlankModel;
    private bool blankModelChoiceDismissed;
    private FemInputSnapshot? femInputSnapshot;
    private string? femInputDatabasePath;
    private string? femWorkingDatabasePath;
    private readonly List<FemOutputRegistration> femOutputRegistrations = [];
    private DispatcherTimer? femWorkingSaveTimer;
    private bool applyingFemDatabase;
    private bool femWorkingDirty;

    public bool IsFemModelLocked => femInputSnapshot?.IsLocked == true;
    public bool CanDefineFemModel => !IsBusy && !IsFemModelLocked;
    public bool HasModelGeometry => State.Geometry.Points.Count > 0 && State.Geometry.Members.Count > 0;
    public bool HasUsableModelGeometry => HasWorkingModel && HasModelGeometry;
    public bool ShowBlankModelChoice => IsOfflineModel && !HasModelGeometry && !blankModelChoiceDismissed;
    public bool CanEditFemModel => !IsBusy && HasUsableModelGeometry && !IsFemModelLocked;
    public bool CanUnlockFemModel => !IsBusy && IsFemModelLocked;
    public bool CanToggleFemModelLock => !IsBusy && HasUsableModelGeometry;
    public string FemLockGlyph => IsFemModelLocked ? "\uE72E" : "\uE785";
    public string FemLockActionText => IsFemModelLocked
        ? "Unlock model — delete this revision's output databases and enable editing"
        : "Lock model — freeze the current FEM inputs before analysis";
    public string FemModelEditState => isFemRunning ? "RUNNING" : IsFemModelLocked
        ? $"LOCKED · REV {femInputSnapshot!.LockedRevision ?? femInputSnapshot.Revision}"
        : femWorkingDirty ? "UNLOCKED · MODIFIED" : "UNLOCKED";
    public string FemDatabaseSummary => femInputSnapshot is null ? "No MCOTiS configuration session"
        : $"Rev {femInputSnapshot.Revision} · {Path.GetFileName(femInputDatabasePath ?? femWorkingDatabasePath ?? "working configuration")}";

    /// <summary>A saved FEM model is loaded and SAP2000 is not connected.</summary>
    public bool IsOfflineModel => offlineModelPath is not null && State.Model is not null && !State.IsSapConnected;
    /// <summary>The workflow pages have a model to work on: the live SAP2000 model or a loaded FEM model.</summary>
    public bool HasWorkingModel => HasSapConnection || IsOfflineModel;
    public string OfflineModelName => offlineModelPath is null ? "" : Path.GetFileName(offlineModelPath);

    public const string ProductTitle = "Mcotis — Meinhardt Cities of Tomorrow Integrated Structures";
    /// <summary>The main window's title: the product, then the file the configuration is in (<see cref="CurrentFileName"/>).</summary>
    public string WindowTitle => $"{ProductTitle} — {CurrentFileName}{(IsModelDirty ? " *" : "")}";
    /// <summary>The file name of the active configuration — the one Save overwrites. While it has no file: "Default model"
    /// for the built-in model, the name of a model opened from an older file or from SAP2000, else "Untitled".</summary>
    public string CurrentFileName =>
        !string.IsNullOrWhiteSpace(femInputDatabasePath) ? Path.GetFileName(femInputDatabasePath)
        : IsDefaultModel ? "Default model"
        : IsOfflineModel ? OfflineModelName is { Length: > 0 } offline ? offline : "Untitled"
        : State.Model?.Name is { Length: > 0 } model ? model : "Untitled";
    public bool IsDefaultModel => IsOfflineModel && isDefaultModel;
    public string OfflineModelSummary => IsOfflineModel
        ? !HasModelGeometry
            ? "Blank model · Define materials and frame/cable sections, then Edit → VCF System."
            : $"{(isDefaultModel ? "Default model" : "Offline model")} {OfflineModelName} · from {State.Model?.Name} — " +
              "Run Analysis solves this model as defined; SAP2000 is not used."
        : "";

    public bool CanSaveFemModel => !IsBusy && HasWorkingModel && State.Baseline.Points.Count > 0 &&
        State.Geometry.Members.Any(member => member.Kind is MemberKind.Frame or MemberKind.Cable);
    public bool CanLoadFemModel => !IsBusy;

    /// <summary>File → Save Config (Ctrl+S): overwrites the active configuration; a configuration without a file asks for one.</summary>
    public Command SaveFemModel => saveFemModel ??= new(_ => SaveFemModelToFile(saveAs: false), () => CanSaveFemModel);
    /// <summary>File → Save Config As… (Ctrl+Shift+S): always asks for the file; the saved file becomes the active configuration.</summary>
    public Command SaveFemModelAs => saveFemModelAs ??= new(_ => SaveFemModelToFile(saveAs: true), () => CanSaveFemModel);
    private Command? undoModel, redoModel;
    /// <summary>Edit → Undo (Ctrl+Z) / Redo (Ctrl+Y): one step of the document's history (<see cref="UndoModel"/>).</summary>
    public Command UndoModelCommand => undoModel ??= new(_ => UndoModel(), () => CanUndoModel);
    public Command RedoModelCommand => redoModel ??= new(_ => RedoModel(), () => CanRedoModel);

    /// <summary>Asks where to save the configuration, starting from a folder (null: the dialog's own) and a file name; null
    /// cancels. The Windows Save dialog — a check replaces it.</summary>
    internal Func<string?, string, string?> AskFemSavePath { get; set; } = (folder, fileName) =>
    {
        var dialog = new SaveFileDialog { Filter = McotisModelFile.Filter, FileName = fileName, AddExtension = false };
        if (folder is not null && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };
    public Command LoadFemModel => loadFemModel ??= new(_ => LoadFemModelFromFile(), () => CanLoadFemModel);

    /// <summary>Asks whether the model's changes not saved yet may go — Save them, drop them, or stay (the window's
    /// "Save changes?" prompt, M8). Null — the checks — never asks.</summary>
    internal Func<bool>? ConfirmDiscardChanges { get; set; }

    /// <summary>Whether another model may take the place of the open one: it has no changes that are not saved, or the
    /// user has saved or dropped them.</summary>
    internal bool MayReplaceModel() => !IsModelDirty || ConfirmDiscardChanges?.Invoke() != false;

    /// <summary>The "Save changes?" prompt's Yes: Save — to the model's file, or to a file the user chooses. False when
    /// the user cancels the file dialog or the save fails: the open model then stays.</summary>
    internal async Task<bool> SaveBeforeReplaceAsync()
    {
        if (ActiveModelFile is { } active) await SaveFemModelFileAsync(active);
        else
        {
            var folder = offlineModelPath is not null && !isDefaultModel ? Path.GetDirectoryName(offlineModelPath) : null;
            if (AskFemSavePath(folder, SuggestModelFileName()) is not { } chosen) return false;
            await SaveFemModelFileAsync(McotisModelFile.WithExtension(chosen));
        }
        return !IsModelDirty;
    }
    /// <summary>File → Load VCF…: drop the current model (live or loaded) and open the built-in default model.</summary>
    public Command LoadDefaultModel => loadDefaultModel ??= new(_ => { if (MayReplaceModel()) LoadDefaultFemModel(replaceCurrent: true); }, () => CanLoadFemModel);
    public Command ContinueWithBlankModel => continueWithBlankModel ??= new(_ =>
    {
        blankModelChoiceDismissed = true;
        Notify(nameof(ShowBlankModelChoice));
        Message = "Blank model ready. Define materials and frame/cable sections, then Edit → VCF System.";
    }, () => ShowBlankModelChoice && !IsBusy);

    /// <summary>Fresh application / File → New Config: an unsaved, offline model with no geometry or definitions.</summary>
    internal void InitializeBlankMcotisConfiguration()
    {
        if (IsBusy || IsFemModelLocked) return;
        femWorkingSaveTimer?.Stop();
        femSessionStore.ClearActive();
        applyingFemDatabase = true;
        applyingProject = true;
        try
        {
            ClearFemResults();
            State.Connection = "Offline model";
            State.Model = new ModelInfo("", "MCOTiS", 6, "kN", "m", false);
            State.SessionBaselineModel = null;
            State.SessionBaseline = GeometryModel.Empty;
            State.SessionBaselinePointLoads = [];
            State.SessionBaselinePretensionForces = new Dictionary<string, double>(StringComparer.Ordinal);
            State.JointRestraints = new Dictionary<string, Mcotis.Core.Restraint>(StringComparer.Ordinal);
            State.JointMasses = new Dictionary<string, FemJointMassDto>(StringComparer.Ordinal);
            State.Geometry = GeometryModel.Empty;
            State.Baseline = GeometryModel.Empty;
            State.Preview = GeometryModel.Empty;
            State.LoadPatterns.Clear();
            new ConfigurationApplicator(State, workflow).Reset();
            config.Session.Close();
            config.Session.ClearDirty();

            FemSectionProperties.Clear();
            Materials.Clear();
            FrameSections.Clear();
            CableSections.Clear();
            TendonSections.Clear();
            ResetMaterialLibrary();
            ResetFemDofs();
            femSapLoading = SapFemLoading.Empty;
            SetFemLoadRows([], [], []);
            offlineLoadCaseNames = [];
            offlineModelPath = "Untitled";
            isDefaultModel = false;
            blankModelChoiceDismissed = false;
            femInputSnapshot = null;
            femInputDatabasePath = null;
            femWorkingDatabasePath = null;
            femOutputRegistrations.Clear();
            femWorkingDirty = false;
            generatorFlag = null;
            ClearNativeDocument();
            SelectedAppliedPoint = null;
            SelectedCable = null;
            SelectedTab = TabFem;
            SelectedFemPage = FemInputsPage;
            FemProgressText = "Blank MCOTiS model · Define materials and frame/cable sections, then Edit → VCF System.";
            Message = FemProgressText;
        }
        finally
        {
            applyingProject = false;
            applyingFemDatabase = false;
            NotifyFemDatabaseState();
            Update();
        }
    }

    /// <summary>The file Save overwrites: the model's own <c>.mcotis</c> (M6). A model opened from an older configuration
    /// file, generated or imported has none until it is saved as one — the older file is never written.</summary>
    internal string? ActiveModelFile => McotisModelFile.IsModelFile(femInputDatabasePath) ? femInputDatabasePath : null;

    private async void SaveFemModelToFile(bool saveAs)
    {
        if (IsBusy || CommitEditors?.Invoke() == false) return;
        var active = ActiveModelFile;
        if (!saveAs && active is not null)
        {
            await SaveFemModelFileAsync(active);
            return;
        }
        // Save As starts from the active file's own folder and name; a first save from the model's
        var folder = active is not null ? Path.GetDirectoryName(active)
            : offlineModelPath is not null && !isDefaultModel ? Path.GetDirectoryName(offlineModelPath)
            : State.Model is { Path.Length: > 0 } model ? Path.GetDirectoryName(model.Path) : null;
        if (AskFemSavePath(folder, active is not null ? Path.GetFileName(active) : SuggestModelFileName()) is not { } chosen) return;
        await SaveFemModelFileAsync(McotisModelFile.WithExtension(chosen));
    }

    /// <summary>Writes the model to <paramref name="path"/> — what Save does once the file is chosen. A <c>.mcotis</c>
    /// path is the main window's file: the document of the session, with the run state beside it (M6). The older
    /// <c>.cotfem.db</c> / <c>.cotfem.json</c> writers remain for the checks, which make older files with them; the
    /// window never offers them.</summary>
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
                FemProgressText = "Reading sections, materials, loading, cases and combinations from SAP2000 for Save MCOTiS Config…";
                properties = await ReadAndDisplayFemPropertiesAsync();
                loadCases = await workflow.GetLoadCaseNamesAsync();
            }
            var file = BuildFemModelFile(properties, loadCases);
            if (McotisModelFile.IsModelFile(path))
            {
                var snapshot = LockedAsNow(FemInputSnapshot.FromLegacy(file, femInputSnapshot));
                // the file is the document: it holds this configuration before it is written — an edit not yet in the
                // document becomes its step of the history first, as an autosave makes it
                if (!NativeToLegacyProjection.HoldsConfiguration(NativeDocumentSession.Document, snapshot)) SyncNativeDocument(snapshot, edit: true);
                if (NativeDocumentProblem is { } problem)
                    throw new InvalidOperationException($"The model cannot be written as a Mcotis document: {problem}");
                var document = NativeDocumentSession.Document;
                McotisModelFile.Write(path, document, new(snapshot.Revision, snapshot.IsLocked, snapshot.LockedRevision, femOutputRegistrations.ToArray()));
                femInputSnapshot = snapshot;
                femInputDatabasePath = path;
                femWorkingDirty = false;
                femWorkingDatabasePath = femSessionStore.Save(snapshot, femOutputRegistrations, path);
                NativeDocumentSession.MarkSaved(document, path);
                // the optimization study shown goes beside the model, with its FEM outputs (M7)
                if (OptimizationStudyToSave() is { } study)
                {
                    try { McotisModelFile.WriteBeside(path, SavedOptimizationStudy.FileName, study); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Warn("Save", $"The model was saved; its optimization study could not be written beside it: {ex.Message}");
                    }
                }
            }
            else
            {
                if (path.EndsWith(FemModelFile.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    FemModelFile.Write(path, file);
                }
                else
                {
                    var snapshot = LockedAsNow(FemInputSnapshot.FromLegacy(file, femInputSnapshot));
                    FemInputDatabase.Write(path, snapshot, femOutputRegistrations);
                    femInputSnapshot = snapshot;
                    femInputDatabasePath = path;
                    femWorkingDirty = false;
                    femWorkingDatabasePath = femSessionStore.Save(snapshot, femOutputRegistrations, path);
                }
                // the document is what the file now holds: clean until the next edit
                SyncNativeDocument(femInputSnapshot is not null && !path.EndsWith(FemModelFile.Extension, StringComparison.OrdinalIgnoreCase)
                    ? femInputSnapshot : FemInputSnapshot.FromLegacy(file, femInputSnapshot), edit: true);
                NativeDocumentSession.MarkSaved(NativeDocumentSession.Document, path);
            }
            NotifyModelHistory();
            FemProgressText = $"Saved {Path.GetFileName(path)} · {file.SectionProperties.Count:N0} members, {file.CaseRows.Count} load cases, " +
                $"{file.CombinationRows.Count} combinations. File → Open… reopens this complete setup without SAP2000.";
            Message = FemProgressText;
        }
        catch (Exception ex)
        {
            FemProgressText = $"Save failed: {ex.Message}";
            Message = FemProgressText;
            Warn("Save", ex.Message);
            if (ex is System.Runtime.InteropServices.COMException) State.Connection = "Connection Lost";
        }
        finally { IsBusy = false; Update(); }
    }

    /// <summary>A snapshot about to be written keeps the lock of the model and its revision: the results of a locked
    /// revision stay that revision's.</summary>
    private FemInputSnapshot LockedAsNow(FemInputSnapshot snapshot)
    {
        if (IsFemModelLocked && femInputSnapshot is not null)
        {
            snapshot.IsLocked = true;
            snapshot.LockedRevision = femInputSnapshot.LockedRevision ?? femInputSnapshot.Revision;
            snapshot.Revision = femInputSnapshot.Revision;
        }
        return snapshot;
    }

    /// <summary>The file name the first Save offers: the name of the older file the model was opened from, else the name
    /// of the model it came from, as a <c>.mcotis</c> file.</summary>
    private string SuggestModelFileName()
    {
        if (offlineModelPath is not null && !isDefaultModel && McotisModelFile.IsOlderFile(offlineModelPath))
            return McotisModelFile.WithExtension(Path.GetFileName(offlineModelPath));
        var stem = State.Model is { Path.Length: > 0 } model ? Path.GetFileNameWithoutExtension(model.Path) : "Mcotis";
        return stem + McotisModelFile.Extension;
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
            PatternRows = FemPatternRows.Select(FemRowDtos.Pattern).ToList(),
            CaseRows = FemCaseRows.Select(row => FemRowDtos.Case(row, femMassSources)).ToList(),
            CombinationRows = FemCombinationRows.Select(FemRowDtos.Combination).ToList(),
            MassSources = femMassSources.Count > 0 ? femMassSources.ToList() : null,
            Generator = generatorFlag,
            // the six-component restraints the model holds; a model that holds none is written without the field
            JointRestraints = State.JointRestraints.Any(item => !item.Value.IsFree)
                ? State.JointRestraints.Where(item => !item.Value.IsFree).OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => FemJointRestraintDto.FromNative(item.Key, item.Value)).ToList()
                : null,
            JointMasses = State.JointMasses.Values.Any(item => !item.IsZero)
                ? State.JointMasses.Values.Where(item => !item.IsZero).OrderBy(item => item.JointName, StringComparer.Ordinal).ToList()
                : null,
            Settings = new FemSettingsDto
            {
                TargetFitPercent = FemTargetFitPercent,
                TargetFitIterations = FemTargetFitIterations,
                FrameMeshSegments = FemFrameMeshSegments,
                CableSag = FemCableSag,
                ActiveDofs = DofNames(FemActiveDofs),
                CableJointDofs = DofNames(FemCableDofs),
            },
        };
    }

    private void LoadFemModelFromFile()
    {
        if (IsBusy || CommitEditors?.Invoke() == false || !MayReplaceModel()) return;
        var dialog = new OpenFileDialog { Filter = McotisModelFile.OpenFilter, CheckFileExists = true };
        if (offlineModelPath is not null && !isDefaultModel) dialog.InitialDirectory = Path.GetDirectoryName(offlineModelPath);
        else if (State.Model is { Path.Length: > 0 } model && Directory.Exists(Path.GetDirectoryName(model.Path))) dialog.InitialDirectory = Path.GetDirectoryName(model.Path);
        if (dialog.ShowDialog() == true) OpenFemModelFile(dialog.FileName);
    }

    /// <summary>Opens the bundled MCOTiS database as a fresh editable model. At startup it only opens while SAP2000 is not
    /// connected; with <paramref name="replaceCurrent"/> (File → Load VCF…) it replaces whatever model is open.</summary>
    internal void LoadDefaultFemModel(bool replaceCurrent = false)
    {
        if (IsBusy || (State.IsSapConnected && !replaceCurrent)) return;
        if (CommitEditors?.Invoke() == false) return;
        FemInputSnapshot snapshot;
        try
        {
            snapshot = FemInputDatabase.ReadDefault().Snapshot;
            // The bundled database is a template, not the working file. Its saved lock and output registrations
            // belong to the authoring session; a fresh default model starts editable and requires a new analysis.
            snapshot.DatabaseId = Guid.NewGuid();
            snapshot.Revision = 1;
            snapshot.IsLocked = false;
            snapshot.LockedRevision = null;
            snapshot.Model = snapshot.Model! with { IsLocked = false };
        }
        catch (Exception ex)
        {
            Message = $"Default FEM model unavailable: {ex.Message}";
            return;
        }
        ApplyFemModel(snapshot.ToLegacy(), FemInputDatabase.DefaultModelName, isDefault: true, databaseSnapshot: snapshot);
        AddPredefinedStaySections();   // the VCF system starts with the VSL schedule of the Technical Options Study as cable sections
    }

    /// <summary>Restores a saved FEM model as an offline model — what Load Model does once the file is chosen.</summary>
    /// <summary>File → Open…: a <c>.mcotis</c> file is the model it holds — its document, its history starting there,
    /// its run state (M6). An older <c>.cotfem.db</c> / <c>.cotfem.json</c> is opened as an import: the model it holds
    /// becomes the window's document, Save asks for a <c>.mcotis</c> file, and the older file is never written. A file
    /// that cannot be read leaves the open model as it is.</summary>
    internal void OpenFemModelFile(string path)
    {
        if (IsBusy) return;
        FemModelFileDto file;
        FemInputDatabaseRead? database = null;
        FemInputSnapshot? snapshot = null;
        Mcotis.Core.ModelDocument? document = null;
        IReadOnlyList<FemOutputRegistration>? outputs = null;
        try
        {
            if (McotisModelFile.IsModelFile(path))
            {
                (document, var state) = McotisModelFile.Read(path);
                if (NativeToLegacyProjection.Read(document) is not { } configuration)
                {
                    // a model of the native workspace (TemplateBuilding, an earlier version's native window): taken in (M8)
                    OpenNativeDocument(document, Path.GetFileName(path), path, LegacyNativeDocumentMigration.Source.Imported("Mcotis native model"));
                    return;
                }
                snapshot = configuration;
                snapshot.Revision = state?.Revision ?? 1;
                snapshot.IsLocked = state?.IsLocked ?? false;
                snapshot.LockedRevision = state?.LockedRevision;
                snapshot.SavedUtc = File.GetLastWriteTimeUtc(path);
                outputs = state?.Outputs;
                file = snapshot.ToLegacy();
            }
            else if (path.EndsWith(FemInputDatabase.Extension, StringComparison.OrdinalIgnoreCase))
            {
                database = FemInputDatabase.Read(path);
                (snapshot, outputs) = (database.Snapshot, database.Outputs);
                file = database.Snapshot.ToLegacy();
            }
            else file = FemModelFile.Read(path);
        }
        catch (Exception ex)
        {
            Warn("Open", ex.Message);
            Message = "Open cancelled — the current model is unchanged.";
            return;
        }
        ApplyFemModel(file, path, isDefault: false, snapshot, outputs, openedDocument: document);
        if (document is not null && offlineModelPath == path)
            RestoreOptimizationStudy(McotisModelFile.ReadBeside<SavedOptimizationStudy>(path, SavedOptimizationStudy.FileName), NativeDocumentSession.Document,
                FemInputDatabase.RevisionHash(femInputSnapshot!));
        if (document is null && offlineModelPath == path && McotisModelFile.IsOlderFile(path))
        {
            FemProgressText += $" Opened from an older file: Save writes the model as a {McotisModelFile.Extension} file; {Path.GetFileName(path)} is never written.";
            Message = FemProgressText;
        }
    }

    /// <param name="openedDocument">The document of a <c>.mcotis</c> file: the session takes it as it is, instead of
    /// making one of the configuration.</param>
    private void ApplyFemModel(FemModelFileDto file, string path, bool isDefault, FemInputSnapshot? databaseSnapshot = null,
        IReadOnlyList<FemOutputRegistration>? outputs = null, bool keepDocument = false, Mcotis.Core.ModelDocument? openedDocument = null)
    {
        IsBusy = true;
        applyingFemDatabase = true;
        try
        {
            var restoredSnapshot = databaseSnapshot ?? FemInputSnapshot.FromLegacy(file);
            var restoredSource = isDefault ? LegacyNativeDocumentMigration.Source.Generated("VCF")
                : LegacyNativeDocumentMigration.Infer(restoredSnapshot);
            ClearFemResults();
            State.Connection = "Offline model";
            State.Model = file.Model;
            State.SessionBaselineModel = file.SessionBaseline is null ? null : file.Model;
            State.SessionBaseline = file.SessionBaseline ?? GeometryModel.Empty;
            State.SessionBaselinePointLoads = [];
            State.SessionBaselinePretensionForces = new Dictionary<string, double>(StringComparer.Ordinal);
            State.JointRestraints = restoredSnapshot.JointRestraints
                .ToDictionary(item => item.JointName, item => item.ToNative(), StringComparer.Ordinal);
            State.JointMasses = restoredSnapshot.JointMasses.ToDictionary(item => item.JointName, StringComparer.Ordinal);
            State.Geometry = file.Geometry!;
            State.Baseline = file.Baseline!;
            State.Preview = file.Geometry!;
            // a configuration that holds no restraint is migrated once: its column-base pins become assignments
            MakeRestraintsExplicit();
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
            // the generator flag: the configuration's own; for a configuration saved before flags existed (and the built-in
            // VCF model) the VCF flag, if its generator validates the model. Undo and Redo give back the flag of the step.
            // A .mcotis file states its flag: a model saved detached stays detached.
            string? generatorNote = null;
            generatorFlag = openedDocument is not null ? (openedDocument.Generator is null ? null : restoredSnapshot.Generator)
                : restoredSnapshot.Generator ?? (keepDocument ? null : MigratedGeneratorFlag(out generatorNote));
            restoredSnapshot.Generator = generatorFlag;

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
            // Keep the load patterns and case definitions saved in the bundled database.
            FemTargetFitPercent = file.Settings.TargetFitPercent;
            FemTargetFitIterations = file.Settings.TargetFitIterations;
            FemFrameMeshSegments = file.Settings.FrameMeshSegments;
            FemCableSag = file.Settings.CableSag;
            RestoreFemDofs(file.Settings.ActiveDofs, file.Settings.CableJointDofs, starting: isDefault);

            offlineModelPath = path;
            isDefaultModel = isDefault;
            femInputSnapshot = restoredSnapshot;
            if (!keepDocument)
            {
                nativeDocumentSource = openedDocument is not null ? LegacyNativeDocumentMigration.SourceOf(openedDocument) : restoredSource;
                nativeDocumentSourceKnown = true;
            }
            // a model opened from its .mcotis file is that document, its history starting there; a configuration
            // restored from the document (Undo, Redo) keeps the document and its history
            if (openedDocument is not null)
            {
                NativeDocumentProblem = null;
                NativeDocumentSession.Replace(openedDocument, path);
                nextStepName = null;
                NotifyModelHistory();
            }
            else if (!keepDocument) SyncNativeDocument(restoredSnapshot);
            // the file Save overwrites is a .mcotis; an older file the model came from is not one (M6)
            femInputDatabasePath = keepDocument ? femInputDatabasePath : isDefault || !McotisModelFile.IsModelFile(path) ? null : path;
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
            FemProgressText = $"Loaded {(isDefault ? "default " : "")}MCOTiS config {Path.GetFileName(path)} (saved {file.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}). " +
                "Run Analysis solves this model as defined, without SAP2000; Connect SAP for the SAP2000 study tabs.";
            Message = $"{(isDefault ? "Default MCOTiS config" : "MCOTiS config")} {Path.GetFileName(path)} loaded offline from {file.Model!.Name} — SAP2000 not connected.";
            if (generatorNote is not null) FemProgressText += " " + generatorNote;
        }
        catch (Exception ex)
        {
            offlineModelPath = null;
            State.Connection = "Disconnected";
            FemProgressText = $"Load MCOTiS Config failed: {ex.Message}";
            Message = "Load MCOTiS Config failed.";
            Warn("Load MCOTiS Config", ex.Message);
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

    /// <summary>The mass sources of the configuration, as it holds them. There is no editor for them yet: they are
    /// kept and saved again.</summary>
    private List<FemMassSourceDto> femMassSources = [];

    private void RestoreFemLoadRows(FemModelFileDto file)
    {
        SetFemLoadRows(
            file.PatternRows.Select(row => new FemPatternRow(row.Name, row.Type, row.SelfWeightMultiplier, row.Role, row.Loads)).ToArray(),
            file.CaseRows.Select(row =>
            {
                // the row as it was saved: its identity, and its kind and what belongs to it — kept even where this
                // workspace cannot solve every kind yet; the load-case form can edit the stored options
                var restored = new FemCaseRow(row.Name, row.Type, row.Definition, row.InitialCase, row.Run, row.Supported, row.Source, row.Note)
                {
                    Id = Guid.TryParse(row.Id, out var id) ? id : Guid.NewGuid(),
                    Options = new(FemCaseSchema.KindOf(row), FemCaseSchema.GeometricOf(row), row.StiffnessSourceCase,
                        row.NonlinearControls is { } controls
                            ? new(controls.MaxLoadSteps, controls.MaxIterationsPerStep, controls.RelativeResidualTolerance, controls.RelativeDisplacementTolerance) : null,
                        row.Modal?.MassSource, row.Modal?.RequestedModes),
                };
                restored.CableTargetForces = row.CableTargetForces ?? Terms(restored).Any(term => PretensionPatterns.Contains(term.Name, StringComparer.OrdinalIgnoreCase));
                return restored;
            }).ToArray(),
            file.CombinationRows.Select(row => new FemCombinationRow(row.Name, row.Type, row.Definition, row.Run, row.Supported, row.Source, row.Note)
                { Id = Guid.TryParse(row.Id, out var id) ? id : Guid.NewGuid() }).ToArray());
        femMassSources = (file.MassSources ?? []).ToList();
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
        State.JointRestraints = new Dictionary<string, Mcotis.Core.Restraint>(StringComparer.Ordinal);
        State.JointMasses = new Dictionary<string, FemJointMassDto>(StringComparer.Ordinal);
        generatorFlag = null;
        ClearNativeDocument();
    }

    /// <summary>No model: an empty native document, whose source the next model names.</summary>
    private void ClearNativeDocument()
    {
        NativeDocumentSession.Replace(Mcotis.Core.ModelDocument.Create(), null);
        nativeDocumentSourceKnown = false;
        NativeDocumentProblem = null;
    }

    private void ClearFemResults()
    {
        SelectedFemRun = null;
        FemRuns.Clear();
        FemCableFitRows.Clear();
        FemMonitor.Reset();
        FemProgressText = "";
        NotifyFemMonitorChart();
    }

    /// <summary>Starts a new user-facing MCOTiS configuration while keeping the connected model as its source.</summary>
    /// <param name="generator">The flag of the generator that made the model (Load TAS, Edit → Tied Arch System); null for
    /// a model no generator made.</param>
    /// <param name="migrateRestraints">False for a native document taken in (M8): its restraints are its own, and no pin is
    /// added at a column base of a model that holds none — its run reports what is not held.</param>
    private void StartNewMcotisConfigurationFile(LegacyNativeDocumentMigration.Source? source = null, FemGeneratorDto? generator = null,
        bool migrateRestraints = true)
    {
        generatorFlag = generator;
        femWorkingSaveTimer?.Stop();
        femInputSnapshot = null;
        femInputDatabasePath = null;
        femWorkingDatabasePath = null;
        femOutputRegistrations.Clear();
        femWorkingDirty = true;
        // a model imported or generated without restraints gets the column-base pins as real assignments, once
        if (migrateRestraints) MakeRestraintsExplicit();
        // Give a newly generated/imported model its native identity immediately. Revision zero is the unsaved base;
        // the first working-database checkpoint advances it to revision one without changing the document id. The
        // snapshot carries the restraints the model holds (State.JointRestraints), as every snapshot does.
        if (HasWorkingModel && State.Baseline.Points.Count > 0 && FemSectionProperties.Count > 0)
        {
            var snapshot = CaptureFemInputSnapshot(advanceRevision: false);
            snapshot.Revision = 0;
            // Re-apply Generator: the rebuilt model is a step of the same document's history
            if (regeneratingIdentity is { } identity) snapshot.DatabaseId = identity;
            // a native document taken in keeps its identity (M8)
            else if (importedIdentity is { } native) snapshot.DatabaseId = native;
            femInputSnapshot = snapshot;
            nativeDocumentSource = source ?? LegacyNativeDocumentMigration.Infer(snapshot, startingModel: true);
            nativeDocumentSourceKnown = true;
            SyncNativeDocument(snapshot, edit: regeneratingIdentity is not null);
        }
        NotifyFemDatabaseState();
        ScheduleFemWorkingSave();
    }

    /// <summary>Why the model open in the workspace has no native document: null when it has one.</summary>
    internal string? NativeDocumentProblem { get; private set; }

    /// <summary>Brings the native document to the model a snapshot holds. A model the workspace holds and the standalone
    /// document cannot express is still the user's model: it stays open, runs and is saved as before; the session is
    /// then left with an empty document, not with the document of an earlier state, and the reason is kept.</summary>
    private void SyncNativeDocument(FemInputSnapshot snapshot, bool edit = false)
    {
        // A model that was connected, and never loaded or imported, is named here by where it is from.
        if (!nativeDocumentSourceKnown) nativeDocumentSource = LegacyNativeDocumentMigration.Infer(snapshot);
        nativeDocumentSourceKnown = true;
        Mcotis.Core.ModelDocument document;
        try
        {
            // the whole configuration, the parts the native model has no place for yet included (NativeToLegacyProjection)
            document = NativeToLegacyProjection.Write(snapshot, nativeDocumentSource);
            NativeDocumentProblem = null;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or FormatException or Mcotis.Core.DocumentException)
        {
            document = Mcotis.Core.ModelDocument.Create();
            NativeDocumentProblem = ex.Message;
        }
        // An edit of the open model is one step of the document's history (2026-10-14, M2): Edit → Undo goes back to the
        // document before it. A model that is opened, imported or generated starts a history of its own.
        var current = NativeDocumentSession.Document;
        // the grids and levels are the document's own (the configuration has no place for them): an edit keeps them, and
        // a native document opened into the window hands its own over (geometry editing plan, 2026-10-15)
        var reference = carriedReferenceGeometry ?? (current.DocumentId == document.DocumentId ? current.ReferenceGeometry : null);
        carriedReferenceGeometry = null;
        if (reference is { IsEmpty: false } && NativeDocumentProblem is null) document = WithReferenceGeometry(document, reference);
        // frame loads assigned with Assign → Frame → Frame Load are the document's own: the configuration cannot hold
        // them, so a rebuild of the same model carries them over (NativeFrameLoads, FL-03)
        if (NativeDocumentProblem is null && current.DocumentId == document.DocumentId) document = NativeFrameLoads.Carry(current, document);
        if (edit && NativeDocumentProblem is null && current.DocumentId == document.DocumentId && !current.Points.IsEmpty)
        {
            if (!restoringFromDocument)
                NativeDocumentSession.Execute(Mcotis.Application.DocumentCommands.Edit(nextStepName ?? "Edit model", editor => editor.ReplaceContent(document)));
        }
        else NativeDocumentSession.Replace(document, null);
        nextStepName = null;
        NotifyModelHistory();
    }

    /// <summary>The name the next step of the history is given (Edit → Undo names it); "Edit model" when none is set.</summary>
    private string? nextStepName;

    /// <summary>True while the configuration is being restored from a document (Undo, Redo): it is not a new edit.</summary>
    private bool restoringFromDocument;

    public bool CanUndoModel => CanEditFemModel && NativeDocumentSession.CanUndo;
    public bool CanRedoModel => CanEditFemModel && NativeDocumentSession.CanRedo;
    /// <summary>The model differs from what its file holds (the document session's own rule: Undo back to it is clean).</summary>
    public bool IsModelDirty => NativeDocumentSession.IsDirty;

    private void NotifyModelHistory()
    {
        Notify(nameof(CanUndoModel));
        Notify(nameof(CanRedoModel));
        Notify(nameof(IsModelDirty));
        Notify(nameof(WindowTitle));
        NotifyGenerator();
    }

    /// <summary>Edit → Undo / Redo: the document session goes back (or forward) one step, and the configuration that
    /// document holds becomes the model of the window (<see cref="NativeToLegacyProjection.Read"/>). The edit not yet
    /// written is written first, so that it is the step undone. Results go, as with any change of the model.</summary>
    internal bool UndoModel() => MoveInHistory(undo: true);
    internal bool RedoModel() => MoveInHistory(undo: false);

    private bool MoveInHistory(bool undo)
    {
        if (!CanEditFemModel || IsBusy) return false;
        if (femWorkingDirty && HasWorkingModel && State.Baseline.Points.Count > 0 && FemSectionProperties.Count > 0)
            PersistFemWorkingSnapshot(advanceRevision: true);
        var name = undo ? NativeDocumentSession.UndoName : NativeDocumentSession.RedoName;
        var before = NativeDocumentSession.Document;
        if (!(undo ? NativeDocumentSession.Undo() : NativeDocumentSession.Redo())) return false;
        // a step of the grids and levels only: the model and its results stay as they are
        if (OnlyReferenceDiffers(before, NativeDocumentSession.Document))
        {
            Message = $"{(undo ? "Undo" : "Redo")}: {name ?? "edit"} — the grids and levels as they were {(undo ? "before" : "after")} it; no point moved.";
            NotifySharedViewer();
            NotifyModelHistory();
            return true;
        }
        if (NativeToLegacyProjection.Read(NativeDocumentSession.Document) is not { } configuration)
        {
            Warn(undo ? "Undo" : "Redo", "The document holds no configuration of the main window to go back to.");
            if (undo) NativeDocumentSession.Redo(); else NativeDocumentSession.Undo();
            return false;
        }
        configuration.DatabaseId = femInputSnapshot?.DatabaseId ?? configuration.DatabaseId;
        configuration.Revision = (femInputSnapshot?.Revision ?? 0) + 1;
        configuration.SavedUtc = DateTime.UtcNow;
        restoringFromDocument = true;
        try
        {
            var path = offlineModelPath ?? FemInputDatabase.DefaultModelName;
            ApplyFemModel(configuration.ToLegacy(), path, isDefaultModel, configuration, keepDocument: true);
        }
        finally { restoringFromDocument = false; }
        Message = $"{(undo ? "Undo" : "Redo")}: {name ?? "edit"} — the model as it was {(undo ? "before" : "after")} it (FEM results cleared).";
        NotifyModelHistory();
        return true;
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
        // A native-first command (currently Assign Restraints, M5) committed the engineering edit and this payload
        // atomically already. Rebuilding it here would turn its autosave into a second Undo step. A legacy-first edit
        // has changed the captured payload, so it still goes through the bridge until that control is migrated.
        if (NativeToLegacyProjection.HoldsConfiguration(NativeDocumentSession.Document, snapshot))
        {
            nextStepName = null;
            NotifyModelHistory();
        }
        else SyncNativeDocument(snapshot, edit: true);
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
        // the native document is the model that is locked and run, not the model of the last autosave
        SyncNativeDocument(snapshot, edit: true);
        NotifyFemDatabaseState();
    }

    private void ToggleFemModelLockFromUi()
    {
        if (!CanToggleFemModelLock) return;
        if (IsFemModelLocked) { UnlockFemModelForEditing(); return; }
        try
        {
            LockFemModelForRun();
            Message = "FEM model locked. Run Analysis to generate output databases.";
        }
        catch (Exception ex) { Warn("Lock FEM Model", ex.Message); }
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
        FemOutputLifecycle.DeleteVerified(femInputSnapshot, femOutputRegistrations);
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
        Notify(nameof(CanToggleFemModelLock));
        Notify(nameof(FemLockGlyph));
        Notify(nameof(FemLockActionText));
        Notify(nameof(FemModelEditState));
        Notify(nameof(FemDatabaseSummary));
        Notify(nameof(FemStatus));
        Notify(nameof(WindowTitle));
        Notify(nameof(CanRunFemAnalysis));
        CommandManager.InvalidateRequerySuggested();
    }
}
