using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Presentation;

/// <summary>What File → Import From SAP2000 brought in, and whether Run Analysis can start.</summary>
public sealed record SapImportSummary(bool ReadyToRun, IReadOnlyList<string> Lines);

// ---- File → Import From SAP2000: the active SAP2000 model, complete, as the working model ----
// One step instead of Connect SAP + Read FEM Properties + Get From SAP2000 (materials, sections) + Tabs 01–03: geometry,
// member properties, materials and section libraries, load patterns with their loads, load cases and combinations, the
// Tab 02 chord groups (baseline captured) and Tab 03 cables with their SAP target forces. Tab 01 gets one load at 0 kPa on
// no points, so the run uses the loads exactly as SAP2000 assigns them. Read-only: SAP2000 is never written.
public sealed partial class MainViewModel
{
    public bool CanImportFromSap => !IsBusy;

    internal async Task<SapImportSummary> ImportFromSapAsync()
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running; try again when it has finished.");
        var lines = new List<string>();
        IsBusy = true;
        try
        {
            // a fresh session on the active SAP2000 model: what Mcotis held before is set aside
            Message = "Connecting to SAP2000…";
            State.Model = null;
            State.SessionBaselineModel = null;
            State.SessionBaseline = GeometryModel.Empty;
            ClearDefaultFemModel();
            State.Connection = "Connecting";
            try
            {
                await workflow.ConnectAsync();
                await workflow.RefreshAsync();
            }
            catch
            {
                State.Connection = "Error";
                throw;
            }
            offlineModelPath = null;
            if (State.SessionBaselineModel is { Path.Length: > 0 } baseline &&
                System.IO.Path.GetDirectoryName(baseline.Path) is { Length: > 0 } directory)
                State.Study.OutputDirectory = System.IO.Path.Combine(directory, "Study");
            var frames = State.Geometry.Members.Count(member => member.Kind == MemberKind.Frame);
            var cableMembers = State.Geometry.Members.Where(member => member.Kind == MemberKind.Cable).Select(member => member.Name).ToArray();
            lines.Add($"Model: {State.Model?.Name} · {State.Geometry.Points.Count} joints, {frames} frames, {cableMembers.Length} cables");
            if (State.Geometry.Members.Count == 0) throw new InvalidOperationException("The active SAP2000 model has no frames or cables to import.");

            Message = "Reading member properties, loads, load cases and combinations…";
            await ReadAndDisplayFemPropertiesAsync(resetLoadRows: true);

            Message = "Reading the material and section libraries…";
            var materials = (await workflow.ReadMaterialsAsync()).Select(MaterialCosts.WithDefaultCost).ToArray();
            var library = await workflow.ReadSectionsAsync();
            // the libraries as SAP2000 defines them; the member properties keep the values SAP2000 reported for each member
            Materials.Clear();
            foreach (var material in materials) Materials.Add(material);
            FrameSections.Clear();
            foreach (var section in library.Frames) FrameSections.Add(section);
            CableSections.Clear();
            foreach (var section in library.Cables) CableSections.Add(section);
            TendonSections.Clear();
            foreach (var section in library.Tendons) TendonSections.Add(section);
            lines.Add($"Libraries: {materials.Length} materials, {library.Frames.Count} frame / {library.Cables.Count} cable / {library.Tendons.Count} tendon sections");
            lines.Add($"Loading: {femSapLoading.Patterns.Count} load patterns ({femSapLoading.JointLoads.Count} joint, " +
                      $"{femSapLoading.MemberLoads.Count + femSapLoading.MemberPointLoads.Count} member loads), " +
                      $"{femSapLoading.Cases.Count} load cases, {femSapLoading.Combinations.Count} combinations");

            applyingProject = true;
            try
            {
                // Tab 03: every cable SAP2000 pretensions, with its target force, load pattern and section
                var targets = cableMembers.Length > 0 ? await workflow.ReadCableTargetForcesAsync(cableMembers) : new Dictionary<string, CableTargetForce>();
                var sections = targets.Count > 0 ? await workflow.ReadCableSectionsAsync(targets.Keys) : new Dictionary<string, CableSection>();
                State.Cables.Clear();
                foreach (var name in cableMembers.Where(targets.ContainsKey))
                {
                    var target = targets[name];
                    var cable = new CablePretensionConfig { CableName = name, LoadPattern = target.LoadPattern, TargetForce = target.TargetForce };
                    if (sections.TryGetValue(name, out var section) && section.AreaM2 > 0)
                    {
                        cable.SectionName = section.PropertyName;
                        cable.SectionAreaM2 = section.AreaM2;
                        if (cable.ResolveCapacity(State.CableStrengthMpa, State.CableStrengthReductionFactor) is { } capacity) cable.DesignCapacityKN = capacity;
                    }
                    State.Cables.Add(cable);
                }
                var untargeted = cableMembers.Length - State.Cables.Count;
                lines.Add(State.Cables.Count > 0
                    ? $"Tab 03: {State.Cables.Count} cables with their SAP2000 target forces" + (untargeted > 0 ? $" ({untargeted} without a target force left out)" : "")
                    : "Tab 03: no cable has a target force in SAP2000 — add the cable targets on 03 Pretension to run");

                // Tab 02: the chord groups, then the baseline for the truss-height study
                var chords = VcfChordDetection.Detect(State.Geometry);
                if (chords is not null)
                {
                    State.Truss.TopPoints = chords.Top;
                    State.Truss.MiddlePoints = chords.Middle;
                    State.Truss.BottomPoints = chords.Bottom;
                    try
                    {
                        workflow.CaptureBaseline();
                        lines.Add($"Tab 02: chords from {chords.Source} — {chords.Top.Count} top, {chords.Middle.Count} middle, {chords.Bottom.Count} bottom joints; " +
                                  $"truss height {State.Truss.BaselineHeight:0.###} m captured");
                    }
                    catch (InvalidOperationException ex) { lines.Add($"Tab 02: chord groups found but the baseline was not captured — {ex.Message}"); }
                }
                else lines.Add("Tab 02: no top / middle / bottom chord lines found — select them on 02 Truss Height for the truss-height study (Run FEM Analysis does not need them)");

                lines.Add(ImportTab01(chords?.Top ?? []));
            }
            finally { applyingProject = false; }

            ResetFemLoadRows();
            ClearFemResults();
            RegenerateStudyPreview();
            SelectedTab = TabFem;
            string? blocker = null;
            try { femStudy.ComposeModel(State); }
            catch (InvalidOperationException ex) { blocker = ex.Message; }
            var ready = blocker is null && HasWorkingModel;
            lines.Add(ready ? "Ready: Run → Run FEM Analysis solves the imported model." : $"Not ready to run yet: {blocker ?? "no working model"}");
            Message = ready ? "Imported the active SAP2000 model — ready to run (read-only; SAP2000 unchanged)."
                            : "Imported the active SAP2000 model — see the import summary for what is still needed.";
            return new SapImportSummary(ready, lines);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            State.Connection = "Connection Lost";
            throw;
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

    /// <summary>
    /// Tab 01 from the SAP2000 joint loads, so the run applies exactly the loads SAP2000 holds. Tab 01 places
    /// Fz = −qz × tributary length × B × storeys on each loaded point and replaces that pattern's SAP joint loads there, so a
    /// pattern whose vertical joint loads are uniform per tributary length becomes a Tab 01 load with that qz, on those points.
    /// Patterns that are not (or carry horizontal joint loads) stay SAP loads. Without one, Tab 01 holds 0 kPa on the top
    /// chord in a pattern with no joint loads there, which changes nothing. Returns the summary line.
    /// </summary>
    private string ImportTab01(IReadOnlyList<string> topChord, string program = "SAP2000")
    {
        State.AppliedPoints.Clear();
        State.LoadDefinitions.Clear();
        if (!double.IsFinite(State.TributaryWidthB) || State.TributaryWidthB <= 0) State.TributaryWidthB = 1;
        if (!double.IsFinite(State.NumberOfStories) || State.NumberOfStories < 1) State.NumberOfStories = 1;
        var width = State.TributaryWidthB;
        var stories = Math.Truncate(State.NumberOfStories);
        var pretension = State.Cables.Select(cable => cable.LoadPattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var patterns = femSapLoading.Patterns.Where(pattern => !pretension.Contains(pattern.Name)).ToArray();

        // qz of each pattern whose joint loads are vertical and uniform per tributary length, on its loaded joints
        var fitted = new List<(SapLoadPattern Pattern, string[] Points, double Qz)>();
        foreach (var pattern in patterns)
        {
            var loads = femSapLoading.JointLoads.Where(item => item.Pattern.Equals(pattern.Name, StringComparison.OrdinalIgnoreCase))
                .GroupBy(item => item.Load.PointName, StringComparer.Ordinal)
                .Select(group => (Point: group.Key, Fx: group.Sum(item => item.Load.FxKN), Fz: group.Sum(item => item.Load.FzKN)))
                .Where(load => State.Geometry.Points.ContainsKey(load.Point)).ToArray();
            if (loads.Length < 2 || loads.Any(load => Math.Abs(load.Fx) > 1e-9 || Math.Abs(load.Fz) < 1e-9)) continue;
            var ordered = loads.OrderBy(load => State.Geometry.Points[load.Point].Position.X).ThenBy(load => load.Point, StringComparer.Ordinal).ToArray();
            var xs = ordered.Select(load => State.Geometry.Points[load.Point].Position.X).ToArray();
            var qz = new double[ordered.Length];
            for (var i = 0; i < ordered.Length; i++)
            {
                var tributary = i == 0 ? (xs[1] - xs[0]) / 2 : i == xs.Length - 1 ? (xs[^1] - xs[^2]) / 2 : (xs[i + 1] - xs[i - 1]) / 2;
                qz[i] = tributary > 0 ? -ordered[i].Fz / (tributary * width * stories) : double.NaN;
            }
            if (qz.Any(value => !double.IsFinite(value))) continue;
            var mean = qz.Average();
            if (qz.All(value => Math.Abs(value - mean) <= 0.005 * Math.Abs(mean)))
                fitted.Add((pattern, ordered.Select(load => load.Point).ToArray(), mean));
        }

        if (fitted.Count > 0)
        {
            // the loaded joints of the widest fit; patterns fitted on exactly those joints join it
            var basis = fitted.OrderByDescending(item => item.Points.Length)
                .ThenByDescending(item => item.Pattern.Type.Contains("Live", StringComparison.OrdinalIgnoreCase)).First();
            var set = basis.Points.ToHashSet(StringComparer.Ordinal);
            var chosen = fitted.Where(item => item.Points.Length == set.Count && item.Points.All(set.Contains)).ToArray();
            workflow.AddAppliedPoints(basis.Points);
            foreach (var (pattern, _, qz) in chosen)
                State.LoadDefinitions.Add(new LoadDefinition { LoadName = pattern.Name, Qz = Math.Round(qz, 6) });
            var others = patterns.Length - chosen.Length;
            return $"Tab 01: {string.Join(", ", chosen.Select(item => $"{item.Pattern.Name} {item.Qz:0.###} kPa"))} on {basis.Points.Length} loaded joints " +
                   $"(B = {width:0.###} m, {stories:0} storey(s)) — reproduces the {program} joint loads" +
                   (others > 0 ? $"; {others} other pattern(s) keep their {program} loads" : "");
        }

        // nothing fits: 0 kPa on the top chord, in a pattern without joint loads there, so nothing is replaced
        var top = topChord.Where(State.Geometry.Points.ContainsKey).ToArray();
        var loaded = femSapLoading.JointLoads.Where(item => top.Contains(item.Load.PointName)).Select(item => item.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var placeholder = patterns.FirstOrDefault(pattern => !loaded.Contains(pattern.Name) && pattern.Type.Contains("Live", StringComparison.OrdinalIgnoreCase))
                          ?? patterns.FirstOrDefault(pattern => !loaded.Contains(pattern.Name));
        if (top.Length >= 2 && placeholder is not null)
        {
            workflow.AddAppliedPoints(top);
            State.LoadDefinitions.Add(new LoadDefinition { LoadName = placeholder.Name, Qz = 0 });
            return $"Tab 01: {placeholder.Name} at 0 kPa on the {top.Length} top-chord joints — the {program} loads run as assigned (set qz on 01 Loading for a parametric study)";
        }
        return $"Tab 01: the loading could not be set from {program} — choose the loaded points and qz on 01 Loading to run";
    }
}
