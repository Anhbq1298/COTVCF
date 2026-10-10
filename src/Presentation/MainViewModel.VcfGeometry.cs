using COT_VCF.Application;
using COT_VCF.Domain;
using COT_VCF.Geometry;

namespace COT_VCF.Presentation;

// ---- Edit → VCF Geometry: rebuild the working model from VCF parameters and member sections ----
// Apply changes the model the app works with (viewer, Tabs 01–03 coordinates, the next FEM run). SAP2000 is never written:
// a connected session switches to an offline model, and Connect SAP / Refresh brings the SAP model back.
public sealed partial class MainViewModel
{
    /// <summary>Section names a frame / a cable can take, from the member properties read from SAP or a loaded model.</summary>
    public IReadOnlyList<string> VcfSectionNames(bool cable) => FemSectionProperties
        .Where(property => property.IsCable == cable && !string.IsNullOrWhiteSpace(property.SectionName))
        .Select(property => property.SectionName!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public string? MemberSection(string member) =>
        FemSectionProperties.FirstOrDefault(property => property.MemberName.Equals(member, StringComparison.Ordinal))?.SectionName;

    public bool CanEditVcfGeometry => !IsBusy && HasWorkingModel && State.Geometry.Points.Count > 0;

    /// <summary>Writes the VCF geometry and the section of each member listed in <paramref name="sections"/> to the
    /// working model. Every point and member keeps its name.</summary>
    internal async Task ApplyVcfGeometryAsync(GeometryModel geometry, IReadOnlyDictionary<string, string> sections)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (State.IsSapConnected && (FemSectionProperties.Count == 0 || FemPatternRows.Count == 0))
            {
                Message = "Reading sections and loading from SAP2000 before applying the VCF geometry (read-only)…";
                await ReadAndDisplayFemPropertiesAsync(resetLoadRows: true);
            }
            if (FemSectionProperties.Count == 0)
                throw new InvalidOperationException("No member properties yet — use File → Read FEM Properties from SAP, or load a model.");

            // Sections: copy the section data of a member that already has the chosen section; the member keeps its own
            // name, local axis and end releases.
            var byMember = FemSectionProperties.ToDictionary(property => property.MemberName, StringComparer.Ordinal);
            foreach (var (member, section) in sections)
            {
                if (!byMember.TryGetValue(member, out var current) || current.SectionName == section) continue;
                var template = FemSectionProperties.FirstOrDefault(property => property.SectionName == section && property.IsCable == current.IsCable)
                    ?? throw new InvalidOperationException($"Section '{section}' is not assigned to any {(current.IsCable ? "cable" : "frame")} of the model.");
                byMember[member] = current with
                {
                    AreaM2 = template.AreaM2, ElasticModulusKNPerM2 = template.ElasticModulusKNPerM2, InPlaneInertiaM4 = template.InPlaneInertiaM4,
                    SectionName = template.SectionName, MaterialName = template.MaterialName, UnitWeightKNPerM3 = template.UnitWeightKNPerM3,
                    UnitMassTPerM3 = template.UnitMassTPerM3, GrossAreaM2 = template.GrossAreaM2, Modifiers = template.Modifiers, Shape = template.Shape,
                };
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
            const string edited = " · VCF geometry";
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
            FemProgressText = $"VCF geometry applied · {FemCandidateSummary}. Run Analysis uses the edited model; SAP2000 is unchanged.";
            Message = "VCF geometry applied to the working model (SAP2000 unchanged; Connect SAP restores the SAP model).";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            Warn("VCF Geometry", ex.Message);
            if (ex is System.Runtime.InteropServices.COMException) State.Connection = "Connection Lost";
        }
        finally
        {
            IsBusy = false;
            Notify(nameof(SharedViewerMemberSizes));
            Notify(nameof(SharedViewerSections));
            Update();
        }
    }
}
