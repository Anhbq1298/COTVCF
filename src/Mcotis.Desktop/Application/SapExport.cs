using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application;

/// <summary>A load pattern to exist in SAP2000: its name, SAP pattern type (Dead, Live, Other …) and self-weight multiplier.</summary>
public sealed record SapExportPattern(string Name, string Type, double SelfWeightMultiplier);

/// <summary>A static load case: "Linear Static" or "Nonlinear Static", its pattern loads, the case it continues from
/// (empty: none) and whether it is set to run.</summary>
public sealed record SapExportCase(string Name, string Type, IReadOnlyList<(string Pattern, double Factor)> Loads, string InitialCase, bool Run);

/// <summary>A linear additive response combination: its load cases / combinations with their factors.</summary>
public sealed record SapExportCombination(string Name, IReadOnlyList<(string Name, double Factor)> Items);

/// <summary>A vertical joint force of a Tab 01 load pattern [kN, global Z].</summary>
public sealed record SapExportJointLoad(string PointName, string Pattern, double FzKN);

/// <summary>A cable's target force [kN] in its load pattern (Tab 03).</summary>
public sealed record SapExportCableTarget(string CableName, string Pattern, double ForceKN);

/// <summary>
/// Everything File → Export to SAP2000 writes into the open SAP2000 model, in kN, m: the geometry with each member's
/// section, local axis, releases and modifiers, the material and section libraries, the supports and the active
/// analysis DOFs, the load patterns, the
/// Tab 01 joint loads (replacing those patterns' joint loads at <see cref="Tab01Points"/>), the Tab 03 cable target forces,
/// the static load cases and the linear additive combinations.
/// <para><see cref="JointRestraints"/> are the restraints the model is analysed with. With
/// <see cref="RestraintsAssigned"/> they are assignments the model holds — read from SAP2000 or ETABS — and are written
/// as they are, every component, a joint the model leaves free being freed. Without it the model holds none and they
/// are the pins of its column bases: such a joint becomes a pin only where the target restrains nothing, and what
/// the target restrains is kept. <see cref="PinSupports"/> are the joints held in the plane of the model.</para>
/// </summary>
public sealed record SapExportModel(
    GeometryModel Geometry,
    IReadOnlyList<FemSectionProperties> Members,
    IReadOnlyList<MaterialProperty> Materials,
    IReadOnlyList<FrameSectionProperty> FrameSections,
    IReadOnlyList<StrandSectionProperty> CableSections,
    IReadOnlyList<string> PinSupports,
    IReadOnlyDictionary<string, Mcotis.Core.Restraint> JointRestraints,
    bool RestraintsAssigned,
    Mcotis.Core.DofMask ActiveDofs,
    IReadOnlyList<SapExportPattern> Patterns,
    IReadOnlyList<string> Tab01Points,
    IReadOnlyList<SapExportJointLoad> JointLoads,
    IReadOnlyList<SapExportCableTarget> CableTargets,
    IReadOnlyList<SapExportCase> Cases,
    IReadOnlyList<SapExportCombination> Combinations);

/// <summary>What the export changed in SAP2000, one line per step.</summary>
public sealed record SapExportReport(string ModelPath, IReadOnlyList<string> Lines);
