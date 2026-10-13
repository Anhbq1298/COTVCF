using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application.Quantities;

/// <summary>The force and length units a set of SAP2000 database tables is written in, and what brings its
/// values to metres, square metres and tonnes per cubic metre.</summary>
public readonly record struct SapTableUnits(string Force, string Length)
{
    private static readonly Dictionary<string, double> Newtons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lb"] = 4.4482216152605, ["kip"] = 4448.2216152605, ["kN"] = 1000, ["kgf"] = 9.80665, ["N"] = 1,
        ["tonf"] = 9806.65, ["ton"] = 9806.65,
    };
    private static readonly Dictionary<string, double> Metres = new(StringComparer.OrdinalIgnoreCase)
    {
        ["in"] = 0.0254, ["ft"] = 0.3048, ["mm"] = 0.001, ["cm"] = 0.01, ["m"] = 1,
    };

    /// <summary>Reads SAP2000's own statement of the units, e.g. <c>KN, m, C</c>.</summary>
    public static SapTableUnits Parse(string currentUnits)
    {
        var parts = (currentUnits ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new NotSupportedException($"SAP2000 units '{currentUnits}' are not understood.");
        return Checked(parts[0], parts[1], currentUnits!);
    }

    public static SapTableUnits From(ModelInfo model) => Checked(model.ForceUnit, model.LengthUnit, model.Units);

    private static SapTableUnits Checked(string force, string length, string stated) =>
        Newtons.ContainsKey(force) && Metres.ContainsKey(length)
            ? new(force, length)
            : throw new NotSupportedException($"SAP2000 units '{stated}' are not supported by the quantity take-off.");

    public double MetresPerLength => Metres[Length];
    public double SquareMetresPerArea => MetresPerLength * MetresPerLength;
    /// <summary>A mass density is force·s²/length⁴ in SAP2000's consistent units; 1 N·s²/m⁴ is 1 kg/m³.</summary>
    public double TonnesPerCubicMetrePerDensity => Newtons[Force] / Math.Pow(MetresPerLength, 4) / 1000;
    public override string ToString() => $"{Force}, {Length}";
}

/// <summary>One record of a SAP2000 database table: field key → value as SAP2000 wrote it.</summary>
public sealed class SapTableRow
{
    private readonly Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
    public SapTableRow(IEnumerable<KeyValuePair<string, string>> fields)
    {
        foreach (var (key, value) in fields) this.fields[key] = value;
    }

    /// <summary>The field's text, or null when SAP2000 gave none.</summary>
    public string? Text(string field) => fields.TryGetValue(field, out var value) && value.Length > 0 ? value : null;
    /// <summary>The field as a number, or null when it is absent or not a finite number — never a default.</summary>
    public double? Number(string field) =>
        Text(field) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    public bool Yes(string field) => string.Equals(Text(field), "Yes", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// SAP2000 database tables of one model, as SAP2000 states them: either its own text copy of the model (the
/// <c>.$2k</c> / <c>.s2k</c> file written beside the <c>.sdb</c> at every save) or the same tables read through the
/// database-tables API. Field keys are the same in both, so one calculation serves both sources.
/// </summary>
public sealed class SapTableSet
{
    private static readonly Regex Pair = new("""([A-Za-z0-9_]+)=("[^"]*"|\S+)""", RegexOptions.Compiled);
    private static readonly Regex Header = new(@"^\s*File\s+(.+?)\s+was saved on\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly Dictionary<string, List<SapTableRow>> tables = new(StringComparer.OrdinalIgnoreCase);

    private SapTableSet(SapTableUnits units, string origin, string? recordedFileName)
    {
        Units = units;
        Origin = origin;
        RecordedFileName = recordedFileName;
    }

    public SapTableUnits Units { get; }
    /// <summary>Where the tables came from, for the user.</summary>
    public string Origin { get; }
    /// <summary>The model file name a text export states in its first line; null when it states none.</summary>
    public string? RecordedFileName { get; }
    public IReadOnlyCollection<string> TableNames => tables.Keys;
    public IReadOnlyList<SapTableRow> Rows(string table) => tables.TryGetValue(table, out var rows) ? rows : [];
    public bool Has(string table) => Rows(table).Count > 0;

    public static SapTableSet FromTables(IEnumerable<KeyValuePair<string, IReadOnlyList<SapTableRow>>> source, SapTableUnits units, string origin)
    {
        var set = new SapTableSet(units, origin, null);
        foreach (var (name, rows) in source)
        {
            if (!set.tables.TryGetValue(name, out var list)) set.tables[name] = list = [];
            list.AddRange(rows);
        }
        return set;
    }

    /// <summary>Reads SAP2000's text copy of a model. A file that does not state its units, or that SAP2000 did not
    /// finish writing, is refused.</summary>
    public static SapTableSet ReadText(string path)
    {
        var read = new Dictionary<string, List<SapTableRow>>(StringComparer.OrdinalIgnoreCase);
        List<SapTableRow>? rows = null;
        string pending = "", last = "";
        string? recordedFileName = null;
        var first = true;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (first)
            {
                first = false;
                if (Header.Match(line) is { Success: true } header) recordedFileName = FileNameOf(header.Groups[1].Value);
            }
            if (line.Trim().Length > 0) last = line.Trim();
            if (line.StartsWith("TABLE:", StringComparison.Ordinal))
            {
                var name = line[6..].Trim().Trim('"');
                if (!read.TryGetValue(name, out rows)) read[name] = rows = [];
                pending = "";
                continue;
            }
            if (rows is null || line.Trim().Length == 0) continue;
            if (line.EndsWith(" _", StringComparison.Ordinal)) { pending += line[..^2] + " "; continue; }      // the record continues
            var text = pending + line;
            pending = "";
            var fields = Pair.Matches(text).Select(match => KeyValuePair.Create(match.Groups[1].Value, match.Groups[2].Value.Trim('"'))).ToArray();
            if (fields.Length > 0) rows.Add(new(fields));
        }
        if (!last.Equals("END TABLE DATA", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("the text export is incomplete — it does not end with END TABLE DATA");
        var stated = read.TryGetValue("PROGRAM CONTROL", out var control) ? control.Select(row => row.Text("CurrUnits")).FirstOrDefault(value => value is not null) : null;
        if (stated is null) throw new InvalidDataException("the text export does not state its units (PROGRAM CONTROL · CurrUnits)");
        var set = new SapTableSet(SapTableUnits.Parse(stated), path, recordedFileName);
        foreach (var (name, list) in read) set.tables[name] = list;
        return set;
    }

    // The path in the header may come from another machine; only its last segment is compared.
    private static string? FileNameOf(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        var name = (cut >= 0 ? path[(cut + 1)..] : path).Trim();
        return name.Length > 0 ? name : null;
    }
}
