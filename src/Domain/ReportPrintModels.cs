namespace COT_VCF.Domain;

public enum ReportPaperSize { A4, A3, Letter }

public enum ReportPageOrientation { Landscape, Portrait }

/// <summary>What the printed page carries in its header and footer, and the default sheet. A presentation
/// input only: it never touches a result, SAP2000 or the study files, and is applied to a report the next
/// time that report is generated.</summary>
public sealed class ReportPrintSettings : Observable
{
    private string companyName = "", logoPath = "", projectName = "", jobNumber = "";
    private string preparedBy = "", checkedBy = "", revision = "", footerNote = "";
    private ReportPaperSize paperSize = ReportPaperSize.A4;
    private ReportPageOrientation orientation = ReportPageOrientation.Landscape;
    private bool showPageNumbers = true, showDate = true, includeSourceData;

    /// <summary>Footer, left. Also stands in for the logo in the header when no logo file is set.</summary>
    public string CompanyName { get => companyName; set => Set(ref companyName, value ?? ""); }
    /// <summary>Image file printed top-left on every page (PNG / JPG / SVG …). Embedded into the report.</summary>
    public string LogoPath { get => logoPath; set => Set(ref logoPath, value ?? ""); }
    /// <summary>Header, centre, first line.</summary>
    public string ProjectName { get => projectName; set => Set(ref projectName, value ?? ""); }
    /// <summary>Header, right.</summary>
    public string JobNumber { get => jobNumber; set => Set(ref jobNumber, value ?? ""); }
    public string PreparedBy { get => preparedBy; set => Set(ref preparedBy, value ?? ""); }
    public string CheckedBy { get => checkedBy; set => Set(ref checkedBy, value ?? ""); }
    public string Revision { get => revision; set => Set(ref revision, value ?? ""); }
    /// <summary>Footer, centre — e.g. a confidentiality or status line.</summary>
    public string FooterNote { get => footerNote; set => Set(ref footerNote, value ?? ""); }
    public ReportPaperSize PaperSize { get => paperSize; set => Set(ref paperSize, value); }
    public ReportPageOrientation Orientation { get => orientation; set => Set(ref orientation, value); }
    public bool ShowPageNumbers { get => showPageNumbers; set => Set(ref showPageNumbers, value); }
    /// <summary>Print the date the report was generated in the header.</summary>
    public bool ShowDate { get => showDate; set => Set(ref showDate, value); }
    /// <summary>Default for the report's "Include Source Data table" print option.</summary>
    public bool IncludeSourceData { get => includeSourceData; set => Set(ref includeSourceData, value); }

    /// <summary>A detached copy — what a background report generation reads while the form stays editable.</summary>
    public ReportPrintSettings Clone() => new()
    {
        CompanyName = CompanyName, LogoPath = LogoPath, ProjectName = ProjectName, JobNumber = JobNumber,
        PreparedBy = PreparedBy, CheckedBy = CheckedBy, Revision = Revision, FooterNote = FooterNote,
        PaperSize = PaperSize, Orientation = Orientation, ShowPageNumbers = ShowPageNumbers, ShowDate = ShowDate,
        IncludeSourceData = IncludeSourceData,
    };
}
