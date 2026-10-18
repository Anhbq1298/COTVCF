using System.Globalization;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application;

public sealed record ResultMemberGroup(string Title, IReadOnlyList<string> Members, string Sections);
public sealed record ResultMemberRow(string Member, double? BendingMNm, double? ShearMN, double? AxialMN, string MemberSize)
{
    public string[] Cells => [Member, Number(BendingMNm), Number(ShearMN), Number(AxialMN), MemberSize];
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
}
public sealed record ResultMemberTable(string CaseName, IReadOnlyList<ResultMemberRow> Rows, string? Warning);

/// <summary>Presentation of already solved in-plane forces, in MN and MNm; no analysis or force interpolation.</summary>
public static class ResultMemberSummary
{
    public static readonly string[] Headers = ["Member", "Bending (MNm)", "Shear (MN)", "Axial (MN)", "Member size"];
    public const string Note = "Peak absolute in-plane forces over all members and frame stations in each row; columns may govern at different locations and are not concurrent. Member size lists assigned section names. — = absent, unavailable, not applicable, or not selected.";

    public static IReadOnlyList<ResultMemberGroup> Groups(GeometryModel geometry, TrussHeightConfiguration truss,
        IReadOnlyList<FemSectionProperties> properties)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        VcfGeometryAnalysis? vcf = null;
        try { vcf = VcfGeometry.Analyze(geometry, truss); }
        catch (InvalidOperationException) { /* Other models retain their actual member names. */ }
        if (vcf is not null)
            foreach (var title in new[] { "Mega Column", "Top Chord", "Bottom Chord", "Vertical Chord", "Main Cable (External)", "Internal Cable" }) groups[title] = [];
        var fans = vcf is null ? new HashSet<string>() : VcfGeometry.FanCables(vcf).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var member in geometry.Members)
        {
            var title = member.Name;
            if (vcf is not null && vcf.Roles.TryGetValue(member.Name, out var role))
            {
                title = role switch
                {
                    VcfMemberRole.MegaColumn => "Mega Column",
                    VcfMemberRole.TopChord => "Top Chord",
                    VcfMemberRole.BottomChord => "Bottom Chord",
                    VcfMemberRole.VerticalChord => "Vertical Chord",
                    VcfMemberRole.MiddleChord => "Middle Chord",
                    VcfMemberRole.EndFrame => "End Frames",
                    _ => fans.Contains(member.Name) ? "Internal Cable" : External(member) ? "Main Cable (External)" : "Other Cables"
                };
            }
            if (!groups.TryGetValue(title, out var names)) groups[title] = names = [];
            names.Add(member.Name);
        }
        return groups.Select(g => new ResultMemberGroup(g.Key, g.Value.ToArray(), string.Join("; ", g.Value.Select(name =>
            properties.FirstOrDefault(p => p.MemberName == name)?.SectionName is { Length: > 0 } section ? section : "(section unavailable)")
            .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)))).ToArray();

        bool External(StructuralMember member) => new[] { member.Start, member.End }.Any(name =>
            geometry.Points[name].Position.X < vcf!.TrussLines[0] - 1e-3 || geometry.Points[name].Position.X > vcf.TrussLines[^1] + 1e-3);
    }

    public static ResultMemberTable Create(string caseName, FemAnalysisResult result, IReadOnlyList<ResultMemberGroup> groups,
        IReadOnlyList<ResultReportOutput> outputs)
    {
        double? Peak(IEnumerable<double> values)
        {
            var data = values.ToArray();
            return data.Length == 0 || data.Any(v => !double.IsFinite(v)) ? null : data.Max(v => Math.Abs(v)) / 1000;
        }
        var rows = groups.Select(group =>
        {
            var names = group.Members.ToHashSet(StringComparer.Ordinal);
            var frames = result.Frames.Where(f => names.Contains(f.MemberName)).ToArray();
            var cables = result.Cables.Where(c => names.Contains(c.MemberName)).ToArray();
            // A partially missing group must not look like a complete envelope.
            bool complete = group.Members.All(n => frames.Any(f => f.MemberName == n) || cables.Any(c => c.MemberName == n));
            var bending = frames.SelectMany(f => new[] { f.MomentStartKNm, f.MomentEndKNm }.Concat(f.Stations?.Select(s => s.MomentKNm) ?? []));
            var shear = frames.SelectMany(f => new[] { f.ShearStartKN, f.ShearEndKN }.Concat(f.Stations?.Select(s => s.ShearKN) ?? []));
            var axial = frames.SelectMany(f => new[] { f.AxialStartKN, f.AxialEndKN }.Concat(f.Stations?.Select(s => s.AxialKN) ?? [])).Concat(cables.Select(c => c.AxialForceKN));
            return new ResultMemberRow(group.Title,
                complete && outputs.Contains(ResultReportOutput.BendingMoment) ? Peak(bending) : null,
                complete && outputs.Contains(ResultReportOutput.Shear) ? Peak(shear) : null,
                complete && outputs.Contains(ResultReportOutput.AxialForce) ? Peak(axial) : null, group.Sections);
        }).ToArray();
        return new(caseName, rows, result.TargetFitFailure);
    }
}
