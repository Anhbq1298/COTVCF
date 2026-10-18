using System.Globalization;
using Mcotis.Desktop.Application.Quantities;
using Mcotis.Desktop.Geometry;

namespace Mcotis.Desktop.Application;

/// <summary>A cell of a report table: its text, or a small image (a section drawing) with the text as its description.</summary>
public sealed record ReportCell(string Text, byte[]? Image = null);

/// <summary>A column of a report table: its header, its share of the page width and whether its text sits left.</summary>
public sealed record ReportColumn(string Header, double Width, bool Left = false);

/// <summary>A table of the results report. <see cref="Landscape"/> tables come last, on landscape pages; the last
/// <see cref="TotalRows"/> rows are totals, set in bold.</summary>
public sealed record ReportTable(string Title, string? Note, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<ReportCell>> Rows,
    bool Landscape = false, int TotalRows = 0);

/// <summary>The member forces of one member for one case: peak absolute bending and shear, the governing (signed) axial force.</summary>
public sealed record ResultMemberPeak(string Member, double? MomentKNm, double? ShearKN, double? AxialKN);

/// <summary>
/// The tables of the results report. Every force is in MN (bending in MN·m) with one decimal; a UR column is shown only when the
/// design utilization was asked for, and then in every table.
/// </summary>
public static class ResultReportTables
{
    /// <summary>Cable UR of an assigned section: tension ÷ (area × breaking stress × reduction factor), the Section Check defaults.</summary>
    public const double CableBreakingStressMpa = 2160, CableReductionFactor = 0.5;

    public const string SummaryNote = "Peak absolute forces over every member and station of each group; the columns may govern at different places. " +
        "Vertical Chord includes the middle chords; end frames are not listed. Main Cable (External) = the cables outside the mega columns on both sides, not connected to the bottom chord; Internal Cable = every other cable. — = not applicable.";

    /// <summary>The limit state a UR belongs to: a member's (frame) UR is a strength check, read only in ULS cases; a cable's is a
    /// service check, read only in SLS cases. A model whose case names carry no ULS / SLS reads every case.</summary>
    public static bool IsUlsCase(string name, IEnumerable<string> allCases) =>
        !allCases.Any(item => item.Contains("ULS", StringComparison.OrdinalIgnoreCase)) || name.Contains("ULS", StringComparison.OrdinalIgnoreCase);

    public static bool IsSlsCase(string name, IEnumerable<string> allCases) =>
        !allCases.Any(item => item.Contains("SLS", StringComparison.OrdinalIgnoreCase)) || name.Contains("SLS", StringComparison.OrdinalIgnoreCase);

    public static string Mn(double? kN) => kN is { } value && double.IsFinite(value) ? (value / 1000).ToString("#,0.0", CultureInfo.InvariantCulture) : "—";
    public static string Ur(double? ur) => ur is { } value && double.IsFinite(value) ? value.ToString("0.00", CultureInfo.InvariantCulture) : "—";
    private static string Length(double lengthM) => lengthM > 0 && double.IsFinite(lengthM) ? lengthM.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    /// <summary>The peak forces of each frame of <paramref name="result"/>, over both ends and every station.</summary>
    public static IReadOnlyDictionary<string, ResultMemberPeak> FramePeaks(FemAnalysisResult result) =>
        result.Frames.GroupBy(frame => frame.MemberName, StringComparer.Ordinal).ToDictionary(group => group.Key, group =>
        {
            double? Peak(IEnumerable<double> values) { var data = values.Where(double.IsFinite).ToArray(); return data.Length == 0 ? null : data.Max(Math.Abs); }
            var moments = group.SelectMany(f => new[] { f.MomentStartKNm, f.MomentEndKNm }.Concat(f.Stations?.Select(s => s.MomentKNm) ?? []));
            var shears = group.SelectMany(f => new[] { f.ShearStartKN, f.ShearEndKN }.Concat(f.Stations?.Select(s => s.ShearKN) ?? []));
            var axials = group.SelectMany(f => new[] { f.AxialStartKN, f.AxialEndKN }.Concat(f.Stations?.Select(s => s.AxialKN) ?? [])).Where(double.IsFinite).ToArray();
            return new ResultMemberPeak(group.Key, Peak(moments), Peak(shears), axials.Length == 0 ? null : axials.MaxBy(Math.Abs));
        }, StringComparer.Ordinal);

    /// <summary>
    /// The tables of one load case / combination: the summary of the member groups (with each group's section descriptions and
    /// drawings), the forces of the members of each frame group, and the cable forces.
    /// </summary>
    /// <param name="summary">The Results Table rows of this case, in the order of <paramref name="groups"/>.</param>
    /// <param name="memberUr">The UR of each designed member in this case; null leaves every UR column out.</param>
    /// <param name="cableAreaM2">The steel area of a cable's assigned section [m²], or null.</param>
    public static IReadOnlyList<ReportTable> ForCase(string caseName, FemAnalysisResult result, IReadOnlyList<ResultMemberGroup> groups,
        IReadOnlyList<FemMemberSummaryRow> summary, IReadOnlyDictionary<string, double>? memberUr, Func<string, string> sectionOf,
        Func<string, double> lengthM, Func<string, double?> cableAreaM2, Func<QuantitySectionFigure, byte[]?> image,
        bool memberUrApplies = true, bool cableUrApplies = true)
    {
        bool withUr = memberUr is not null;
        var peaks = FramePeaks(result);
        var cables = result.Cables.GroupBy(cable => cable.MemberName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        double? CableCapacityKN(string cable) => cableAreaM2(cable) is > 0 and var area ? area * 1e6 * CableBreakingStressMpa * CableReductionFactor / 1000 : null;
        // a cable's UR is read in SLS cases only, a member's in ULS cases only: elsewhere the column reads —
        double? CableUr(string cable) => cableUrApplies && cables.TryGetValue(cable, out var response) && CableCapacityKN(cable) is { } capacity
            ? Math.Max(0, response.AxialForceKN) / capacity : null;
        double? UrOf(string member) => cables.ContainsKey(member) ? CableUr(member)
            : memberUrApplies && memberUr is not null && memberUr.TryGetValue(member, out var value) ? value : null;
        IReadOnlyList<ReportColumn> WithUr(params ReportColumn[] columns) => withUr ? [.. columns, new ReportColumn("UR", 7)] : columns;

        var tables = new List<ReportTable>();
        var summaryRows = summary.Select((row, index) =>
        {
            var urs = index < groups.Count ? groups[index].Members.Select(UrOf).OfType<double>().ToArray() : [];
            var drawing = row.Sections.Select(section => section.Figure).OfType<QuantitySectionFigure>().FirstOrDefault();
            IReadOnlyList<ReportCell> cells =
            [
                new(row.Member), new(Mn(row.Forces.BendingMNm * 1000)), new(Mn(row.Forces.ShearMN * 1000)), new(Mn(row.Forces.AxialMN * 1000)),
                new(row.MemberSize), new(row.SectionNames, drawing is null ? null : image(drawing)),
            ];
            return withUr ? [.. cells, new ReportCell(Ur(urs.Length == 0 ? null : urs.Max()))] : cells;
        }).ToArray();
        const string urNote = " UR: members (strength) in ULS cases only, cables (service) in SLS cases only; — elsewhere.";
        tables.Add(new($"Summary of member forces — {caseName}", SummaryNote + (withUr ? " UR = the largest UR of the group's members in this case." + urNote : ""),
            WithUr(new("Member", 15, true), new("Bending (MN·m)", 10), new("Shear (MN)", 9), new("Axial (MN)", 9), new("Member size", 30, true), new("Section", 13)),
            summaryRows));

        // The frame groups of a VCF system each get their table; another model lists all its frames in one.
        var frameGroups = groups.Where(group => group.Members.Any(peaks.ContainsKey)).ToArray();
        var vcf = groups.Any(group => group.Title == "Top Chord");
        IEnumerable<(string Title, IReadOnlyList<string> Members)> detail = vcf
            ? frameGroups.Select(group => (group.Title, (IReadOnlyList<string>)group.Members.Where(peaks.ContainsKey).ToArray()))
            : [("Frame", peaks.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray())];
        foreach (var (title, members) in detail)
        {
            if (members.Count == 0) continue;
            var rows = members.Select(member =>
            {
                var peak = peaks[member];
                var type = peak.AxialKN is > 1e-6 ? "Tension" : peak.AxialKN is < -1e-6 ? "Compression" : "—";
                IReadOnlyList<ReportCell> cells = [new(member), new(sectionOf(member)), new(Length(lengthM(member))), new(Mn(peak.MomentKNm)), new(Mn(peak.ShearKN)),
                    new(Mn(peak.AxialKN)), new(type)];
                return withUr ? [.. cells, new ReportCell(Ur(UrOf(member)))] : cells;
            }).ToArray();
            tables.Add(new($"{title} forces — {caseName}", title == "Vertical Chord" ? "Includes the middle chords. N: + tension, − compression." : "N: + tension, − compression.",
                WithUr(new("Member", 14, true), new("Section", 30, true), new("L (m)", 8), new("M max (MN·m)", 11), new("V max (MN)", 10), new("N (MN)", 10), new("N type", 12, true)),
                rows));
        }

        if (cables.Count > 0)
        {
            var groupOf = groups.SelectMany(group => group.Members.Select(member => (member, group.Title))).GroupBy(item => item.member, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.First().Title, StringComparer.Ordinal);
            var rows = cables.Values.OrderBy(cable => cable.MemberName, StringComparer.Ordinal).Select(cable =>
            {
                var group = groupOf.GetValueOrDefault(cable.MemberName) ?? "—";
                IReadOnlyList<ReportCell> cells = [new(cable.MemberName), new(group.Replace(" Cable", "", StringComparison.Ordinal)), new(sectionOf(cable.MemberName)),
                    new(Length(lengthM(cable.MemberName))), new(cable.TargetForceKN > 0 ? Mn(cable.TargetForceKN) : "—"), new(Mn(cable.AxialForceKN))];
                return withUr ? [.. cells, new ReportCell(Mn(CableCapacityKN(cable.MemberName))), new ReportCell(Ur(CableUr(cable.MemberName)))] : cells;
            }).ToArray();
            IReadOnlyList<ReportColumn> columns = [new("Cable", 13, true), new("Group", 15, true), new("Section", 14, true), new("L (m)", 8), new("Target pretension (MN)", 13), new("Tension (MN)", 11)];
            if (withUr) columns = [.. columns, new ReportColumn("Capacity (MN)", 11), new ReportColumn("UR", 7)];
            tables.Add(new($"Cable forces — {caseName}",
                withUr ? $"Capacity = section area × {CableBreakingStressMpa:0} MPa × {CableReductionFactor:0%}; UR = tension ÷ capacity, in SLS cases only." : null, columns, rows));
        }
        return tables;
    }

    /// <summary>Show Quantities: per section its drawing and description, the members, length, concrete, steel and estimated cost, with totals.</summary>
    public static ReportTable Quantities(NativeQuantityTakeoff takeoff, Func<QuantitySectionFigure, byte[]?> image)
    {
        var rows = takeoff.Rows.Select(row => (IReadOnlyList<ReportCell>)
        [
            new(row.Section), new(row.SectionType), new(row.Section, row.Figure is null ? null : image(row.Figure)), new(row.FigureDescription),
            new(row.MemberCount.ToString("N0", CultureInfo.InvariantCulture)), new(row.LengthText), new(row.ConcreteText), new(row.SteelText), new(row.EstimatedCostText),
        ]).ToList();
        var total = takeoff.Total;
        rows.Add([new("Total"), new(""), new(""), new(""), new(total.MemberCount.ToString("N0", CultureInfo.InvariantCulture)), new(total.LengthText),
            new(total.ConcreteText), new(total.SteelText), new(total.EstimatedCostText)]);
        var excluded = takeoff.Notes.Where(note => note.StartsWith("Total ", StringComparison.Ordinal)).ToArray();
        return new("Quantities", "Member length from the model geometry; concrete volume and steel mass from the assigned sections and stored material densities; " +
                                 "estimated cost from the unit costs under Define → Materials." + (excluded.Length == 0 ? "" : " " + string.Join(" ", excluded)),
            [new("Section", 15, true), new("Member type", 11, true), new("Section drawing", 9), new("Description", 22, true), new("Members", 7),
             new("Total length", 9), new("Concrete", 9), new("Steel", 8), new("Est. cost (S$)", 10)],
            rows, TotalRows: 1);
    }

    /// <summary>Show Quantities → Design Utilization: each section's UR envelope over its members and the governing combination.</summary>
    public static ReportTable Utilization(IReadOnlyList<NativeQuantityUtilizationRow> rows) =>
        new("Design utilization", "UR envelope of each section over its members and the load cases of their design; the governing combination is where it occurs.",
            [new("Section", 24, true), new("Procedure", 20, true), new("Members", 8), new("UR", 7), new("Governing combination", 25, true), new("Status", 9, true)],
            rows.Select(row => (IReadOnlyList<ReportCell>)[new(row.Section), new(row.Procedure), new(row.MemberCount.ToString(CultureInfo.InvariantCulture)),
                new(row.UtilizationText), new(row.GoverningCombinations), new(row.Status)]).ToArray(),
            Landscape: false);
}
