using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using System.IO;

namespace Mcotis.Desktop.Presentation;

// ---- Edit → VCF System: rebuild the working model from VCF parameters and member sections ----
// Apply changes the model the app works with (viewer, Tabs 01–03 coordinates, the next FEM run). SAP2000 is never written:
// a connected session switches to an offline model, and Connect SAP / Refresh brings the SAP model back.
public sealed partial class MainViewModel
{
    /// <summary>Section names a frame / a cable can take, from the member properties read from SAP or a loaded model.</summary>
    public IReadOnlyList<string> VcfSectionNames(bool cable) => FemSectionProperties
        .Where(property => property.IsCable == cable && !string.IsNullOrWhiteSpace(property.SectionName))
        .Select(property => property.SectionName!)
        .Concat(cable ? CableSections.Select(section => section.Name) : FrameSections.Select(section => section.Name))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public string? MemberSection(string member) =>
        FemSectionProperties.FirstOrDefault(property => property.MemberName.Equals(member, StringComparison.Ordinal))?.SectionName;

    /// <summary>Whether Edit → VCF System can edit this VCF, replace a different working model with the VCF template,
    /// or create the first VCF geometry of a blank model whose sections have been defined.</summary>
    public bool CanEditVcfGeometry => !IsBusy &&
        (HasModelGeometry
            ? (NeedsVcfModelReplacement ? HasWorkingModel : !IsFemModelLocked)
            : !IsFemModelLocked && IsOfflineModel && FrameSections.Count > 0 && CableSections.Count > 0);

    public bool NeedsVcfModelReplacement => HasModelGeometry && !VcfGeometry.CanAnalyze(State.Geometry, State.Truss);

    public string VcfGeometryToolTip => NeedsVcfModelReplacement
        ? "Replace the current working model with the built-in VCF model, then edit the VCF system. You will be asked before anything is replaced."
        : "Create or edit the parametric VCF geometry after defining materials and frame/cable sections.";

    /// <summary>Replaces a non-VCF working model through the same complete, SAP-free path as File → Load VCF.</summary>
    internal void ReplaceCurrentWithVcfModel() => LoadDefaultFemModel(replaceCurrent: true);

    /// <summary>The current geometry, or the built-in VCF topology used only as a starting canvas for an untitled model.</summary>
    internal VcfGeometryAnalysis AnalyzeVcfGeometryForEdit()
    {
        if (HasModelGeometry) return VcfGeometry.Analyze(State.Geometry, State.Truss);
        if (FrameSections.Count == 0 || CableSections.Count == 0)
            throw new InvalidOperationException("Define at least one frame section and one cable section before creating the VCF system.");
        var template = FemInputDatabase.ReadDefault().Snapshot.ToLegacy();
        var setup = template.Project?.Tab03TrussHeight
            ?? throw new InvalidDataException("The built-in VCF topology has no chord definition.");
        var truss = new TrussHeightConfiguration
        {
            TopPoints = setup.TopPoints,
            MiddlePoints = setup.MiddlePoints,
            BottomPoints = setup.BottomPoints,
            Reference = setup.ReferenceElevation,
        };
        return VcfGeometry.Analyze(template.Geometry!, truss);
    }

    /// <summary>Writes the VCF geometry and the section of each member listed in <paramref name="sections"/> to the
    /// working model. Every point and member keeps its name — unless <paramref name="topology"/> changes the number of bays,
    /// then new members take their template's section and Tabs 01–04 and the SAP loading follow the new names.</summary>
    internal async Task ApplyVcfGeometryAsync(GeometryModel geometry, IReadOnlyDictionary<string, string> sections, VcfTopology? topology = null)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (State.IsSapConnected && (femInputsOutdated || FemSectionProperties.Count == 0 || FemPatternRows.Count == 0))
            {
                Message = "Reading sections and loading from SAP2000 before applying the VCF system (read-only)…";
                await ReadAndDisplayFemPropertiesAsync(resetLoadRows: true);
            }
            if (FemSectionProperties.Count == 0) SeedBlankVcfModel(sections);

            if (topology is not null) ApplyBayChange(geometry, topology);

            // Sections: copy the section data of a member that already has the chosen section; the member keeps its own
            // name, local axis and end releases.
            var byMember = FemSectionProperties.ToDictionary(property => property.MemberName, StringComparer.Ordinal);
            var swap = SectionSwapper();
            foreach (var (member, section) in sections)
            {
                if (!byMember.TryGetValue(member, out var current) || current.SectionName == section) continue;
                byMember[member] = swap(current, section);
            }
            FemSectionProperties.Clear();
            foreach (var property in byMember.Values.OrderBy(value => value.MemberName, StringComparer.Ordinal)) FemSectionProperties.Add(property);

            // Leave the live SAP model: the edited model is an offline model from here on.
            if (State.IsSapConnected)
            {
                offlineLoadCaseNames = femSapLoading.Cases.Select(item => item.Name).ToArray();
                isDefaultModel = false;
                State.Connection = "Offline model";
            }
            const string edited = " · VCF system";
            var name = offlineModelPath is null ? State.Model?.Name ?? "Model" : System.IO.Path.GetFileName(offlineModelPath);
            offlineModelPath = name.EndsWith(edited, StringComparison.Ordinal) ? name : name + edited;

            // Geometry: the new shape becomes the model and its baseline; Tab 02 keeps its ΔH range around the new height.
            var truss = State.Truss;
            var (deltaMin, deltaMax, step, deltaCurrent) = (truss.MinimumHeight - truss.BaselineHeight, truss.MaximumHeight - truss.BaselineHeight,
                truss.Increment, truss.CurrentHeight - truss.BaselineHeight);
            var captured = State.BaselineCaptured;
            State.Geometry = geometry;
            State.Baseline = geometry;
            State.SessionBaseline = geometry;
            State.Preview = geometry;
            applyingProject = true;
            try
            {
                if (captured)
                {
                    workflow.CaptureBaseline();
                    truss.MinimumHeight = truss.BaselineHeight + deltaMin;
                    truss.MaximumHeight = truss.BaselineHeight + deltaMax;
                    truss.Increment = step;
                    truss.CurrentHeight = truss.BaselineHeight + deltaCurrent;
                    State.CommittedHeight = truss.BaselineHeight;
                    workflow.UpdatePreview();
                }
                foreach (var point in State.AppliedPoints)
                    if (geometry.Points.TryGetValue(point.PointName, out var moved))
                    { point.X = moved.Position.X; point.Y = moved.Position.Y; point.Z = moved.Position.Z; }
                foreach (var cable in State.Cables)
                    if (byMember.TryGetValue(cable.CableName, out var property) && property.SectionName != cable.SectionName)
                    {
                        cable.SectionName = property.SectionName;
                        cable.SectionAreaM2 = property.GrossAreaM2 > 0 ? property.GrossAreaM2 : property.AreaM2;
                        cable.DesignCapacityKN = cable.ResolveCapacity(State.CableStrengthMpa, State.CableStrengthReductionFactor);
                    }
            }
            finally { applyingProject = false; }

            ClearFemResults();
            RegenerateStudyPreview();
            FemProgressText = "VCF system applied. Run Analysis solves the edited model; SAP2000 is unchanged.";
            Message = "VCF system applied to the working model (SAP2000 unchanged; Connect SAP restores the SAP model).";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            Warn("VCF System", ex.Message);
            if (ex is System.Runtime.InteropServices.COMException) State.Connection = "Connection Lost";
        }
        finally
        {
            IsBusy = false;
            Notify(nameof(SharedViewerSpanPoints));
            Notify(nameof(SharedViewerMemberSizes));
            Notify(nameof(SharedViewerSections));
            Update();
        }
    }

    /// <summary>Creates member objects only when the user applies the first VCF edit to a blank model. Defined library
    /// sections provide every engineering property; the embedded model contributes topology, local axes and releases.</summary>
    private void SeedBlankVcfModel(IReadOnlyDictionary<string, string> sections)
    {
        if (HasModelGeometry) return;
        var template = FemInputDatabase.ReadDefault().Snapshot.ToLegacy();
        var geometry = template.Geometry ?? throw new InvalidDataException("The built-in VCF topology is missing.");
        var setup = template.Project?.Tab03TrussHeight
            ?? throw new InvalidDataException("The built-in VCF topology has no chord definition.");
        var templates = template.SectionProperties.ToDictionary(property => property.MemberName, StringComparer.Ordinal);
        var properties = new List<FemSectionProperties>(geometry.Members.Count);
        foreach (var member in geometry.Members)
        {
            if (!templates.TryGetValue(member.Name, out var basis))
                throw new InvalidDataException($"The VCF topology has no property basis for '{member.Name}'.");
            if (!sections.TryGetValue(member.Name, out var section))
                throw new InvalidOperationException($"Choose a section for '{member.Name}' before applying the blank model.");
            if (member.Kind == MemberKind.Frame)
            {
                var defined = FrameSections.FirstOrDefault(item => item.Name == section)
                    ?? throw new InvalidOperationException($"Frame section '{section}' is not defined.");
                properties.Add(PropertiesFor(basis, defined));
            }
            else
            {
                var defined = CableSections.FirstOrDefault(item => item.Name == section)
                    ?? throw new InvalidOperationException($"Cable section '{section}' is not defined.");
                properties.Add(PropertiesFor(basis, defined));
            }
        }

        State.Geometry = geometry;
        State.Baseline = geometry;
        State.Preview = geometry;
        // the model is the VCF system from here on: the native document records it as generated from that template
        nativeDocumentSource = LegacyNativeDocumentMigration.Source.Generated("VCF");
        nativeDocumentSourceKnown = true;
        State.Truss.TopPoints = setup.TopPoints;
        State.Truss.MiddlePoints = setup.MiddlePoints;
        State.Truss.BottomPoints = setup.BottomPoints;
        State.Truss.Reference = setup.ReferenceElevation;
        workflow.CaptureBaseline();
        FemSectionProperties.Clear();
        foreach (var property in properties.OrderBy(item => item.MemberName, StringComparer.Ordinal)) FemSectionProperties.Add(property);
    }

    /// <summary>A member's properties with another section — copied from a member that has it, or (for a section from
    /// Define → Section Properties that no member has yet) from the section library; the member keeps its name, local axis,
    /// end releases and modifiers. Works on a snapshot, so the function may be used off the UI thread.</summary>
    internal Func<FemSectionProperties, string, FemSectionProperties> SectionSwapper()
    {
        var assigned = FemSectionProperties.ToArray();
        var frames = FrameSections.ToArray();
        var strands = CableSections.ToArray();
        return (current, section) =>
        {
            if (current.SectionName == section) return current;
            if (!current.IsCable && frames.FirstOrDefault(item => item.Name == section) is { } frame && assigned.All(property => property.SectionName != section))
                return PropertiesFor(current, frame);
            if (current.IsCable && strands.FirstOrDefault(item => item.Name == section) is { } strand && assigned.All(property => property.SectionName != section))
                return PropertiesFor(current, strand);
            var template = assigned.FirstOrDefault(property => property.SectionName == section && property.IsCable == current.IsCable)
                ?? throw new InvalidOperationException($"Section '{section}' is not assigned to any {(current.IsCable ? "cable" : "frame")} of the model.");
            return current with
            {
                AreaM2 = template.AreaM2, ElasticModulusKNPerM2 = template.ElasticModulusKNPerM2, InPlaneInertiaM4 = template.InPlaneInertiaM4,
                OutOfPlaneInertiaM4 = template.OutOfPlaneInertiaM4, TorsionConstantM4 = template.TorsionConstantM4,
                SectionName = template.SectionName, MaterialName = template.MaterialName, UnitWeightKNPerM3 = template.UnitWeightKNPerM3,
                UnitMassTPerM3 = template.UnitMassTPerM3, GrossAreaM2 = template.GrossAreaM2, Modifiers = template.Modifiers, Shape = template.Shape,
            };
        };
    }

    /// <summary>A new number of bays: member properties for the regenerated members (from their templates), the SAP loading,
    /// Tab 01 loading points, Tab 02 chord groups, Tab 03 cables and Tab 04 output objects follow the new names.</summary>
    private void ApplyBayChange(GeometryModel geometry, VcfTopology topology)
    {
        string Map(string point) => topology.RenamedPoints.TryGetValue(point, out var renamed) ? renamed : point;
        var points = geometry.Points.Keys.ToHashSet(StringComparer.Ordinal);
        var members = geometry.Members.Select(member => member.Name).ToHashSet(StringComparer.Ordinal);
        var oldTop = State.Truss.TopPoints.ToArray();
        var oldBottom = State.Truss.BottomPoints.ToArray();
        var oldCables = State.Geometry.Members.Where(member => member.Kind != MemberKind.Frame).Select(member => member.Name).ToArray();
        var newCables = geometry.Members.Where(member => member.Kind != MemberKind.Frame).Select(member => member.Name).ToArray();

        // member properties: a regenerated member takes its template's section
        var properties = FemSectionProperties.ToDictionary(property => property.MemberName, StringComparer.Ordinal);
        var next = new List<FemSectionProperties>();
        foreach (var member in geometry.Members)
            if (topology.Templates.TryGetValue(member.Name, out var template) && template != member.Name && properties.TryGetValue(template, out var copy))
                next.Add(copy with { MemberName = member.Name });
            else if (properties.TryGetValue(member.Name, out var own)) next.Add(own);
        FemSectionProperties.Clear();
        foreach (var property in next.OrderBy(value => value.MemberName, StringComparer.Ordinal)) FemSectionProperties.Add(property);

        // SAP loading: loads move with renamed end points; loads on removed objects are dropped
        femSapLoading = femSapLoading with
        {
            JointLoads = femSapLoading.JointLoads.Select(load => load with { Load = load.Load with { PointName = Map(load.Load.PointName) } })
                .Where(load => points.Contains(load.Load.PointName)).ToArray(),
            MemberLoads = femSapLoading.MemberLoads.Where(load => members.Contains(load.Load.MemberName)).ToArray(),
            MemberPointLoads = femSapLoading.MemberPointLoads.Where(load => members.Contains(load.Load.MemberName)).ToArray(),
            GravityLoads = femSapLoading.GravityLoads.Where(load => members.Contains(load.MemberName)).ToArray(),
        };

        // joint restraints the model holds: they move with renamed joints and go with removed ones, as the loads do
        State.JointRestraints = State.JointRestraints.Select(item => (Joint: Map(item.Key), item.Value))
            .Where(item => points.Contains(item.Joint)).GroupBy(item => item.Joint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

        // Tab 01: every top chord point stays loaded when every one was; otherwise the loaded points that still exist
        var appliedNames = State.AppliedPoints.Select(point => point.PointName).ToArray();
        var allTopLoaded = oldTop.Length > 0 && appliedNames.Length == oldTop.Length && oldTop.All(appliedNames.Contains);
        var applied = (allTopLoaded ? topology.TopPoints : appliedNames.Select(Map).Where(points.Contains)).Distinct(StringComparer.Ordinal).ToArray();
        State.AppliedPoints.Clear();
        foreach (var name in applied)
        {
            var position = geometry.Points[name].Position;
            State.AppliedPoints.Add(new AppliedPoint { PointName = name, X = position.X, Y = position.Y, Z = position.Z });
        }
        workflow.SortAppliedPoints();

        // Tab 02: the chord groups are the new chord lines
        State.Truss.TopPoints = topology.TopPoints;
        State.Truss.MiddlePoints = topology.MiddlePoints;
        State.Truss.BottomPoints = topology.BottomPoints;

        // Tab 03: removed fan cables go; a new one copies the settings of its template cable
        var removedCables = State.Cables.Where(cable => !members.Contains(cable.CableName)).ToArray();
        foreach (var cable in removedCables) State.Cables.Remove(cable);
        foreach (var (name, template) in topology.AddedCables)
            if (State.Cables.FirstOrDefault(cable => cable.CableName == template) is { } source && State.Cables.All(cable => cable.CableName != name))
                State.Cables.Add(new CablePretensionConfig
                {
                    CableName = name, LoadPattern = source.LoadPattern, ApplyRange = source.ApplyRange, TargetForce = source.TargetForce,
                    MinForce = source.MinForce, MaxForce = source.MaxForce, StepForce = source.StepForce, SyncMode = source.SyncMode,
                    ReferenceCableName = source.ReferenceCableName, SyncFactor = source.SyncFactor, SectionName = source.SectionName,
                    SectionAreaM2 = source.SectionAreaM2, DesignCapacityKN = source.DesignCapacityKN,
                });
        // A cable that followed a removed one becomes independent and takes over the removed cable's range where its own
        // is blank (e.g. Same Step: the step came from the reference).
        foreach (var cable in State.Cables)
            if (cable.ReferenceCableName is { } reference && !members.Contains(reference))
            {
                var source = removedCables.FirstOrDefault(removed => removed.CableName == reference);
                cable.SyncMode = CableSyncMode.Independent;
                if (source is null) continue;
                if (cable.ApplyRange)
                {
                    cable.MinForce ??= source.MinForce;
                    cable.MaxForce ??= source.MaxForce;
                    cable.StepForce ??= source.StepForce;
                }
                else cable.TargetForce ??= source.TargetForce;
            }

        // Tab 04: lists that held every bottom point / cable keep doing so; other names follow or are dropped
        void Remap(System.Collections.ObjectModel.ObservableCollection<string> list, IReadOnlyList<string> fullOld, IReadOnlyList<string> fullNew, HashSet<string> valid)
        {
            var all = fullOld.Count > 0 && list.Count == fullOld.Count && fullOld.All(list.Contains);
            var mapped = (all ? fullNew : list.Select(Map).Where(valid.Contains)).Distinct(StringComparer.Ordinal).ToArray();
            list.Clear();
            foreach (var name in mapped) list.Add(name);
        }
        var output = State.OutputSettings;
        Remap(output.DeflectionPoints, oldBottom, topology.BottomPoints, points);
        Remap(output.FoundationLeftPoints, [], [], points);
        Remap(output.FoundationRightPoints, [], [], points);
        Remap(output.CableNames, oldCables, newCables, members);
        foreach (var group in output.MemberForceGroups)
        {
            var listed = group.Members.ToHashSet(StringComparer.Ordinal);
            var updated = group.Members.Where(members.Contains)
                .Concat(topology.Templates.Where(pair => listed.Contains(pair.Value)).Select(pair => pair.Key))
                .Distinct(StringComparer.Ordinal).ToArray();
            group.Members.Clear();
            foreach (var member in updated) group.Members.Add(member);
        }
    }
}
