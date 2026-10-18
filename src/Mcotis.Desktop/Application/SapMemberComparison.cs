using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Domain.Results;

namespace Mcotis.Desktop.Application;

/// <summary>Peak absolute forces SAP2000 reports for one member, in kN and kNm (null = not read).</summary>
public sealed record SapMemberPeak(string Member, double? BendingKNm, double? ShearKN, double? AxialKN, string? Warning);

public sealed record SapCompareRow(string Member, double? McotisBending, double? SapBending, double? McotisShear, double? SapShear,
    double? McotisAxial, double? SapAxial, string MemberSize)
{
    public string[] Cells => [Member, N(McotisBending), N(SapBending), N(McotisShear), N(SapShear), N(McotisAxial), N(SapAxial), MemberSize];
    private static string N(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
}
public sealed record SapCompareTable(string CaseName, string SapModel, IReadOnlyList<SapCompareRow> Rows, IReadOnlyList<string> Warnings);

/// <summary>The member force table of Export Results with a SAP2000 column beside every Mcotis column. SAP2000 is only
/// read: its members carry the names Export to SAP2000 gave them (the Mcotis member names). Bending is M3 and shear V2, the
/// in-plane pair of a frame whose local axis 2 lies in the plane of the model, as SAP2000 sets it by default.</summary>
public static class SapMemberComparison
{
    public const string Note = "Cables have no bending or shear (0). Peak absolute forces over all members and stations of each row, SAP2000 from the model open in SAP2000 (analysed), Mcotis from this run, same load case name. Bending is M3 and shear V2 of SAP2000 (in-plane, default local axes). — = not available.";

    public static async Task<IReadOnlyList<SapMemberPeak>> ReadAsync(ISapGateway sap, GeometryModel geometry, string loadCase)
    {
        var info = await sap.ConnectAsync();
        if (!info.IsLocked) throw new InvalidOperationException($"The SAP2000 model '{info.Name}' holds no analysis results: run the analysis in SAP2000 first.");
        var frames = geometry.Members.Where(m => m.Kind == MemberKind.Frame).Select(m => m.Name).ToArray();
        var cables = geometry.Members.Where(m => m.Kind != MemberKind.Frame).Select(m => m.Name).ToArray();
        var frameReads = frames.Length == 0 ? [] : await sap.GetFrameForcesAsync(frames, loadCase, false, info);
        if (frameReads.Count > 0 && frameReads.All(r => r.Status == ResultValueStatus.CaseNotFound))
            frameReads = await sap.GetFrameForcesAsync(frames, loadCase, true, info);
        var cableReads = cables.Length == 0 ? [] : await sap.GetCableForcesAsync(cables, loadCase, false, info);
        if (cableReads.Count > 0 && cableReads.All(r => r.Status == ResultValueStatus.CaseNotFound))
            cableReads = await sap.GetCableForcesAsync(cables, loadCase, true, info);
        var peaks = new List<SapMemberPeak>();
        foreach (var read in frameReads)
            peaks.Add(read.Status != ResultValueStatus.Valid || read.Stations.Count == 0
                ? new(read.FrameName, null, null, null, $"{read.Status}{(read.Warning is null ? "" : " — " + read.Warning)}")
                : new(read.FrameName, read.Stations.Max(s => Math.Abs(s.M3)), read.Stations.Max(s => Math.Abs(s.V2)), read.Stations.Max(s => Math.Abs(s.P)), null));
        foreach (var read in cableReads)
            peaks.Add(read.Status == ResultValueStatus.Valid
                ? new(read.CableName, null, null, read.ForceKN, null)
                : new(read.CableName, null, null, null, $"{read.Status}{(read.Warning is null ? "" : " — " + read.Warning)}"));
        return peaks;
    }

    public static SapCompareTable Create(string caseName, string sapModel, ResultMemberTable mcotis, IReadOnlyList<ResultMemberGroup> groups,
        IReadOnlyList<SapMemberPeak> sap, ISet<string> cables)
    {
        var byName = sap.ToDictionary(p => p.Member, StringComparer.Ordinal);
        var warnings = sap.Where(p => p.Warning is not null).Select(p => $"{p.Member}: {p.Warning}").ToList();
        double? Peak(IEnumerable<double?> values, int expected)
        {
            var data = values.ToArray();
            return data.Length < expected || data.Any(v => v is null) ? null : data.Max()! / 1000;
        }
        var rows = mcotis.Rows.Select(row =>
        {
            var names = groups.First(g => g.Title == row.Member).Members;
            var members = names.Select(n => byName.GetValueOrDefault(n)).Where(p => p is not null).Select(p => p!).ToArray();
            var axial = Peak(members.Select(p => p.AxialKN), names.Count);
            // a cable has no bending or shear: a group of cables reports 0 for both, in both programs
            if (names.Count > 0 && names.All(cables.Contains)) return new SapCompareRow(row.Member, 0, 0, 0, 0, row.AxialMN, axial, row.MemberSize);
            return new SapCompareRow(row.Member,
                row.BendingMNm, Peak(members.Select(p => p.BendingKNm), names.Count),
                row.ShearMN, Peak(members.Select(p => p.ShearKN), names.Count),
                row.AxialMN, axial, row.MemberSize);
        }).ToArray();
        return new(caseName, sapModel, rows, warnings);
    }

    /// <summary>The table as a Word document (A4 landscape, Arial 11 pt, the margins of Export Results), every force column split in two.</summary>
    public static void WriteWord(SapCompareTable data, string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart(); var body = new Body(); main.Document = new Document(body);
        main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Arial", HighAnsi = "Arial", EastAsia = "Arial", ComplexScript = "Arial" }, new FontSize { Val = "22" }, new FontSizeComplexScript { Val = "22" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { Line = "276", LineRule = LineSpacingRuleValues.Auto, After = "120" }))));
        const int pageW = ResultReport.PageHeight, pageH = ResultReport.PageWidth;
        int total = pageW - ResultReport.Left - ResultReport.Right;
        int force = total * 9 / 100, name = total * 15 / 100, size = total - name - 6 * force;
        int[] widths = [name, force, force, force, force, force, force, size];
        body.Append(new Paragraph(new Run(new Text("Member force comparison — Mcotis and SAP2000"))),
            new Paragraph(new Run(new Text($"Load case / combination: {data.CaseName} · SAP2000 model: {data.SapModel}"))),
            new Paragraph(new Run(new Text(Note))));
        static T Line<T>() where T : BorderType, new() => new() { Val = BorderValues.Single, Size = 4 };
        var table = new Table(new TableProperties(new TableWidth { Width = total.ToString(), Type = TableWidthUnitValues.Dxa },
            new TableBorders(Line<TopBorder>(), Line<LeftBorder>(), Line<BottomBorder>(), Line<RightBorder>(), Line<InsideHorizontalBorder>(), Line<InsideVerticalBorder>()),
            new TableLayout { Type = TableLayoutValues.Fixed }),
            new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString() })));
        TableCell Cell(string text, int column, bool head, int span = 1, bool? restart = null)
        {
            var properties = new TableCellProperties(new TableCellWidth { Width = widths.Skip(column).Take(span).Sum().ToString(), Type = TableWidthUnitValues.Dxa });
            if (span > 1) properties.Append(new GridSpan { Val = span });
            if (restart is { } r) properties.Append(r ? new VerticalMerge { Val = MergedCellValues.Restart } : new VerticalMerge());
            if (head) properties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "8FB3DF" });
            properties.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
            return new TableCell(properties, new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "60", After = "60" },
                new Justification { Val = head ? JustificationValues.Center : column is >= 1 and <= 6 ? JustificationValues.Right : JustificationValues.Left }),
                new Run(new Text(text))));
        }
        TableRow Row(bool header, IEnumerable<TableCell> cells)
        {
            var row = new TableRow(new TableRowProperties(new CantSplit()));
            if (header) row.GetFirstChild<TableRowProperties>()!.Append(new TableHeader());
            row.Append(cells);
            return row;
        }
        string[] titles = ["Bending (MNm)", "Shear (MN)", "Axial (MN)"];
        table.Append(Row(true, new[] { Cell("Member", 0, true, restart: true) }
            .Concat(titles.Select((t, i) => Cell(t, 1 + 2 * i, true, 2))).Append(Cell("Member size", 7, true, restart: true))));
        table.Append(Row(true, new[] { Cell("", 0, true, restart: false) }
            .Concat(Enumerable.Range(0, 3).SelectMany(i => new[] { Cell("Mcotis", 1 + 2 * i, true), Cell("SAP2000", 2 + 2 * i, true) })).Append(Cell("", 7, true, restart: false))));
        foreach (var row in data.Rows) table.Append(Row(false, row.Cells.Select((c, i) => Cell(c, i, false))));
        body.Append(table);
        body.Append(new SectionProperties(new PageSize { Width = pageW, Height = pageH, Orient = PageOrientationValues.Landscape },
            new PageMargin { Top = ResultReport.Top, Bottom = ResultReport.Bottom, Left = ResultReport.Left, Right = ResultReport.Right, Header = 0, Footer = 0, Gutter = 0 }));
        main.Document.Save();
    }
}
