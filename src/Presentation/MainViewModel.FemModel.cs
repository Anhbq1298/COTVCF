using COT_VCF.Application;
using COT_VCF.Configuration;
using COT_VCF.Domain;
using COT_VCF.Geometry;
using Microsoft.Win32;
using System.IO;

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
            Filter = FemModelFile.Filter,
            FileName = SuggestFemModelFileName(),
            AddExtension = false,
        };
        if (offlineModelPath is not null && !isDefaultModel) dialog.InitialDirectory = Path.GetDirectoryName(offlineModelPath);
        else if (State.Model is { Path.Length: > 0 } model && Directory.Exists(Path.GetDirectoryName(model.Path))) dialog.InitialDirectory = Path.GetDirectoryName(model.Path);
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FileName.EndsWith(FemModelFile.Extension, StringComparison.OrdinalIgnoreCase) || dialog.FilterIndex != 1
            ? dialog.FileName : Path.ChangeExtension(dialog.FileName, null).TrimEnd('.') + FemModelFile.Extension;
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
            FemModelFile.Write(path, file);
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
            CaseRows = FemCaseRows.Select(row => new FemDefinitionRowDto(row.Name, row.Type, row.Definition, row.InitialCase, row.Run, row.Supported, row.Source, row.Note)).ToList(),
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
        var dialog = new OpenFileDialog { Filter = FemModelFile.Filter, CheckFileExists = true };
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
        try { file = FemModelFile.Read(path); }
        catch (Exception ex)
        {
            Warn("Load Model", ex.Message);
            Message = "Load Model cancelled — the current model is unchanged.";
            return;
        }
        ApplyFemModel(file, path, isDefault: false);
    }

    private void ApplyFemModel(FemModelFileDto file, string path, bool isDefault)
    {
        IsBusy = true;
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
        }
    }

    private void RestoreFemLoadRows(FemModelFileDto file)
    {
        SetFemLoadRows(
            file.PatternRows.Select(row => new FemPatternRow(row.Name, row.Type, row.SelfWeightMultiplier, row.Role, row.Loads)).ToArray(),
            file.CaseRows.Select(row => new FemCaseRow(row.Name, row.Type, row.Definition, row.InitialCase, row.Run, row.Supported, row.Source, row.Note)).ToArray(),
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
}
