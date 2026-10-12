using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Globalization;
using COT_VCF.Domain;
using Mcotis.Application;
using Mcotis.Core;

namespace COT_VCF.Presentation;

public sealed class BuildingBayRow(int index, string width)
{
    public int Bay { get; } = index;
    public string Width { get; set; } = width;
}
public sealed class BuildingStoreyRow(int index)
{
    public int Storey { get; } = index;
    public string Height { get; set; } = "3";
    public string LoadX { get; set; } = "10";
    public string LoadY { get; set; } = "10";
}
public sealed class BuildingSectionRow(string name)
{
    public string Name { get; } = name;
    public string Area { get; set; } = "0.02";
    public string I33 { get; set; } = "0.0004";
    public string I22 { get; set; } = "0.0002";
    public string J { get; set; } = "0.00001";
}

public sealed class TemplateBuildingViewModel : Observable
{
    public string BaysX { get; set; } = "2";
    public string BaysY { get; set; } = "2";
    public string StoreyCount { get; set; } = "3";
    public string ElasticModulus { get; set; } = "200000000";
    public string PoissonRatio { get; set; } = "0.3";
    public string WeightDensity { get; set; } = "78.5";
    public bool IncludeSelfWeight { get; set; }
    public bool FixedBases { get; set; } = true;
    public ObservableCollection<BuildingBayRow> XWidths { get; } = [];
    public ObservableCollection<BuildingBayRow> YWidths { get; } = [];
    public ObservableCollection<BuildingStoreyRow> Storeys { get; } = [];
    public ObservableCollection<BuildingSectionRow> Sections { get; } = [new("Columns"), new("Beams")];
    private string message = "";
    public string Message { get => message; private set => Set(ref message, value); }

    public TemplateBuildingViewModel() => UpdateTables();
    private (int X, int Y, int S) Counts()
    {
        if (!int.TryParse(BaysX, out var x) || x < 0 || !int.TryParse(BaysY, out var y) || y < 0 ||
            !int.TryParse(StoreyCount, out var s) || s < 1 || x > 100 || y > 100 || s > 100 || x + y == 0)
            throw new ArgumentException("Bay counts must be 0–100 (at least one direction nonzero); storeys must be 1–100.");
        if ((long)(x + 1) * (y + 1) * (s + 1) > TemplateBuilding.MaximumPoints)
            throw new ArgumentException($"Reduce the counts: the template limit is {TemplateBuilding.MaximumPoints} points.");
        return (x, y, s);
    }
    public bool UpdateTables()
    {
        try
        {
            var (x, y, s) = Counts();
            Resize(XWidths, x, i => new(i + 1, "5"));
            Resize(YWidths, y, i => new(i + 1, "5"));
            Resize(Storeys, s, i => new(i + 1));
            Message = $"{(x + 1) * (y + 1) * (s + 1)} points. Set Y bays to 0 for an XZ frame, or X bays to 0 for a YZ frame.";
            return true;
        }
        catch (ArgumentException ex) { Message = ex.Message; return false; }
    }
    private static void Resize<T>(ObservableCollection<T> rows, int count, Func<int, T> create)
    {
        while (rows.Count > count) rows.RemoveAt(rows.Count - 1);
        while (rows.Count < count) rows.Add(create(rows.Count));
    }
    private static double Number(string text, string label, bool allowZero = false)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value) ||
            (allowZero ? value < 0 : value <= 0)) throw new ArgumentException($"{label}: enter a finite {(allowZero ? "nonnegative" : "positive")} value.");
        return value;
    }
    public TemplateBuildingInput ReadInput()
    {
        var (x, y, s) = Counts();
        if (XWidths.Count != x || YWidths.Count != y || Storeys.Count != s)
            throw new ArgumentException("Click Update Tables after changing the bay or storey counts, then review the dimensions and loads.");
        double nu = Number(PoissonRatio, "Poisson ratio", true);
        if (nu >= 0.5) throw new ArgumentException("Poisson ratio must be between 0 and 0.5 (exclusive upper limit).");
        var material = new MaterialDefinition(ObjectId.New(), "TemplateMaterial", MaterialType.Other,
            Number(ElasticModulus, "E (kN/m²)"), nu, Number(WeightDensity, "Weight density (kN/m³)", true), 0);
        FrameSectionDefinition Section(BuildingSectionRow row) => new(ObjectId.New(), row.Name, material.Id, FrameSectionShape.General,
            new(Number(row.Area, row.Name + " A"), Number(row.I33, row.Name + " I33"), Number(row.I22, row.Name + " I22"), Number(row.J, row.Name + " J")));
        return new(XWidths.Select(r => Number(r.Width, $"X bay {r.Bay} width (m)")).ToImmutableArray(),
            YWidths.Select(r => Number(r.Width, $"Y bay {r.Bay} width (m)")).ToImmutableArray(),
            Storeys.Select(r => new BuildingStorey(Number(r.Height, $"Storey {r.Storey} height (m)"),
                Number(r.LoadX, $"Storey {r.Storey} X-beam load (kN/m)", true), Number(r.LoadY, $"Storey {r.Storey} Y-beam load (kN/m)", true))).ToImmutableArray(),
            material, Section(Sections[0]), Section(Sections[1]), FixedBases ? Restraint.Fixed : Restraint.Pinned, IncludeSelfWeight);
    }
    internal void ReportError(string value) => Message = value;
}
