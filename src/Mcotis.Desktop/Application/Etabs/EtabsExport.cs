using COT_VCF.Geometry;

namespace COT_VCF.Application.Etabs;

/// <summary>
/// Everything File → Export to ETABS writes into the model open in ETABS, in kN, m. <see cref="Base"/> is what Export to
/// SAP2000 writes (geometry, member assignments, libraries, supports, patterns, cases, combinations). Unlike a SAP2000
/// target — usually the model the loads came from — an ETABS target may be empty, so the export also carries every load
/// Mcotis holds: <see cref="JointLoads"/> are the joint loads as Run Analysis applies them (the Tab 01 loads in place of
/// their patterns' loads at the loaded joints), and the member loads. <see cref="SectionWeights"/> holds the weight and
/// mass per metre of each section, so a section ETABS gets as properties (CFT, encased, Section Designer) keeps its true
/// self-weight through its weight / mass modifiers.
/// </summary>
public sealed record EtabsExportModel(
    SapExportModel Base,
    IReadOnlyList<SapPatternJointLoad> JointLoads,
    IReadOnlyList<SapPatternMemberLoad> MemberLoads,
    IReadOnlyList<SapPatternMemberPointLoad> MemberPointLoads,
    int GravityLoadsLeftOut,
    IReadOnlyDictionary<string, (double WeightPerLength, double MassPerLength)> SectionWeights);

/// <summary>The ETABS model an export was previewed against: its file (empty: unsaved), version and object counts, which
/// must be unchanged when the export is written.</summary>
public sealed record EtabsExportTarget(string Path, string Version, int Points, int Frames, bool IsLocked)
{
    public string Name => Path.Length > 0 ? System.IO.Path.GetFileName(Path) : "ETABS (Untitled)";
}

/// <summary>What the export changes (preview) or changed in ETABS, one line per step.</summary>
public sealed record EtabsExportReport(EtabsExportTarget Target, IReadOnlyList<string> Lines);
