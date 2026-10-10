using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Geometry;
namespace COT_VCF.Application;

public interface ISapGateway : IDisposable
{
    Task<ModelInfo> ConnectAsync();
    /// <summary>Opens a saved model in this gateway's SAP2000 instance and returns its active identity.</summary>
    Task<ModelInfo> OpenModelAsync(string modelPath);
    /// <summary>Saves the verified active model to a new path and returns the new child-model identity.</summary>
    Task<ModelInfo> SaveAsAsync(string targetPath, ModelInfo expected);
    /// <summary>Saves the verified active model without changing its identity.</summary>
    Task SaveAsync(ModelInfo expected);
    /// <summary>Runs every analysis case currently marked to run in the verified active child model.</summary>
    Task RunAnalysisAsync(ModelInfo expected);
    Task<ModelInfo> GetCurrentModelInfoAsync();
    Task<ModelSnapshot> ReadAsync();
    Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected);
    /// <summary>Reads one point's current global coordinates from the verified active model.</summary>
    Task<Position> GetPointPositionAsync(string pointName, ModelInfo expected);
    /// <summary>Reads the ordinary, global-axis force assignments for one point from the verified active model.</summary>
    Task<IReadOnlyList<PointLoad>> GetPointLoadsAsync(string pointName, ModelInfo expected);
    /// <summary>Reads all target-force assignments for one cable from the verified active model.</summary>
    Task<IReadOnlyList<CableTargetForce>> GetCableTargetForcesAsync(string cableName, ModelInfo expected);
    /// <summary>Reads each cable's assigned section property and cross-section area [m²] from the verified
    /// active model. One row per requested cable, in order; a cable whose section can't be read comes back
    /// with <c>AreaM2 == 0</c> and a Problem message rather than throwing.</summary>
    Task<IReadOnlyList<CableSection>> GetCableSectionsAsync(IReadOnlyList<string> cableNames, ModelInfo expected);
    Task<IReadOnlyDictionary<string, FemSectionProperties>> GetFemSectionPropertiesAsync(
        IReadOnlyList<StructuralMember> members, ModelInfo expected) =>
        throw new NotSupportedException("FEM section-property reads are not implemented by this SAP gateway.");
    /// <summary>Read-only snapshot of load patterns (with self-weight multipliers), the loads assigned to the given
    /// joints/frames/cables, static load cases and response combinations, for the internal FEM.</summary>
    Task<SapFemLoading> GetFemLoadingAsync(IReadOnlyList<StructuralMember> members, IReadOnlyDictionary<string, double> localAnglesDeg,
        ModelInfo expected) =>
        throw new NotSupportedException("FEM load reads are not implemented by this SAP gateway.");
    /// <summary>Reads, for display, every database table of the verified active model that holds data and whose
    /// name starts with one of the given prefixes, in the model's present units. Read-only: nothing is opened for
    /// editing and units, lock state and model are left as they are.</summary>
    Task<Quantities.SapTableSet> ReadDatabaseTablesAsync(IReadOnlyList<string> tableNamePrefixes, ModelInfo expected) =>
        throw new NotSupportedException("Database-table reads are not implemented by this SAP gateway.");
    /// <summary>Every material of the verified active model (Define → Materials → Get From SAP2000), in kN, m, C.
    /// Read-only.</summary>
    Task<IReadOnlyList<MaterialProperty>> GetMaterialsAsync(ModelInfo expected) =>
        throw new NotSupportedException("Material reads are not implemented by this SAP gateway.");
    /// <summary>Every frame, cable and tendon section of the verified active model (Define → Section Properties → Get From
    /// SAP2000), in kN, m. Read-only.</summary>
    Task<SectionLibrary> GetSectionsAsync(ModelInfo expected) =>
        throw new NotSupportedException("Section reads are not implemented by this SAP gateway.");
    Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected);
    Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected);
    /// <summary>Moves joints to <paramref name="proposed"/>. A cable object defined by its undeformed length is given
    /// the same length relative to its chord on the new geometry — SAP2000 would otherwise keep the old length.</summary>
    Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected);
    /// <summary>Assigns a Cable Loads / Target Force value on an existing load pattern.
    /// Reserved for execution and never called while editing Tab 3.</summary>
    Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected);
    /// <summary>Every load case and response-combination name in the model. Read-only; used by
    /// result-output selection and config compatibility validation.</summary>
    Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected);
    /// <summary>Reads only the requested SAP groups and their point/frame/cable assignments.</summary>
    Task<IReadOnlyList<SapGroupSnapshot>> ReadOutputGroupsAsync(IReadOnlyList<string> groupNames, ModelInfo expected);
    /// <summary>Replaces the requested COTVCF-managed [O]- groups in the active baseline model. Does not save the model.</summary>
    Task ReplaceOutputGroupsAsync(IReadOnlyList<OutputSapGroupDefinition> groups, ModelInfo expected);

    /// <summary>Reads global joint displacements (U3 → Uz, mm) for the given points from the analyzed active model
    /// under exactly one output case or combo. Selects only that case for output first. One row per requested
    /// point, in the same order; each row carries an explicit <see cref="ResultValueStatus"/> — a missing result
    /// is never reported as 0.</summary>
    Task<IReadOnlyList<JointDisplacement>> GetJointDisplacementsAsync(
        IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected);

    /// <summary>Reads the governing axial force (max |P|, kN) for each cable from the analyzed active model
    /// under exactly one output case or combo. Selects only that case for output first. One row per
    /// requested cable, in the same order; each row carries an explicit <see cref="ResultValueStatus"/> —
    /// a missing result is never reported as 0.</summary>
    Task<IReadOnlyList<CableForceRead>> GetCableForcesAsync(
        IReadOnlyList<string> cableNames, string outputCase, bool isCombo, ModelInfo expected);

    /// <summary>Reads signed governing local-axis P/V2/V3/T/M2/M3 values for each requested frame.
    /// Forces are kN, moments kNm, stations metres. Missing results are never reported as zero.</summary>
    Task<IReadOnlyList<FrameForceRead>> GetFrameForcesAsync(
        IReadOnlyList<string> frameNames, string outputCase, bool isCombo, ModelInfo expected) =>
        throw new NotSupportedException("Frame-force extraction is not implemented by this SAP gateway.");

    /// <summary>Reads every global-axis Fx/Fy/Fz/Mx/My/Mz support-reaction row for the requested points.
    /// The selected case/combo and SAP step identity are retained; missing results are never reported as zero.</summary>
    Task<IReadOnlyList<JointReactionRead>> GetJointReactionsAsync(
        IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected) =>
        throw new NotSupportedException("Support-reaction extraction is not implemented by this SAP gateway.");
}
