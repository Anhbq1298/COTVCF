using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Quantities;
using Mcotis.Desktop.Application.Workers;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation;
using Mcotis.Desktop.SAP2000;
using CSiAPIv1;

/// <summary>
/// Tab 08 Quantity Take-off, offline. The model data is SAP2000's own text copy of the benchmark model
/// (<c>tests/fem-sap-benchmark/model.s2k</c>) and candidates derived from it the way a study derives them — joints
/// moved, a section reassigned. SAP2000 itself is replaced by doubles; nothing here starts or attaches to it.
/// <para>The expected numbers were worked out separately from the code under test: areas by hand from the stored
/// Section Designer dimensions (CFT chord: 0.9 × 1.7 concrete inside a 1.2 × 2.0 × 0.15 tube; mega columns: a
/// concrete rectangle with three embedded 0.07 m² I-shapes), lengths from the joint coordinates.</para>
/// </summary>
internal static class QuantityTakeoffChecks
{
    private const string Cft = "CFT_SHS[1200x2000x150_S460]_C70/85", MegaA = "MegaCol_1.5x3_C70/85-1", MegaB = "MegaCol_2.5x3_C70/85",
        Shs = "SHS_1200x1200x120_S460", Strand = "192T15.7";
    private const double SteelDensity = 7.849047379959917;                 // t/m³, as the model states it for S355 / S460

    public static void Run()
    {
        var fixture = ExportText.Parse(File.ReadAllText(Path.Combine(FemSapBenchmark.DefaultFolder, FemSapBenchmark.ModelFile)));
        ReaderAndUnits(fixture);
        Geometry();
        Calculation(fixture);
        MissingAndUnsupportedData(fixture);
        UnitConversion();
        using var disk = new StudyOnDisk();
        Catalog(disk, fixture);
        SourceSelection(fixture);
        IsolatedRead(fixture);
        ViewModel(fixture);
        FemFailedRow();
    }

    /// <summary>Opt-in, offline: prints the take-off of every candidate of a study on disk from the candidates' own
    /// text exports. Reads only.</summary>
    public static void PrintStudy(string baselineModelPath)
    {
        var service = new QuantityTakeoffService();
        var candidates = new QuantityCandidateCatalog().Discover(baselineModelPath);
        Console.WriteLine($"{candidates.Count} candidate model(s) for {baselineModelPath}");
        foreach (var candidate in candidates)
        {
            var attempt = service.TryTextExportAsync(candidate, CancellationToken.None).GetAwaiter().GetResult();
            if (attempt.Result is not { } result) { Console.WriteLine($"{candidate.Id}: text export not usable — {attempt.Rejected}"); continue; }
            var total = result.Takeoff.Total;
            Console.WriteLine($"{candidate.Id}: {total.MemberCount} members · {Text(total.TotalLengthM)} m · {Text(total.ConcreteVolumeM3)} m³ concrete · {Text(total.SteelMassT)} t steel" +
                              (result.Takeoff.Notes.Count > 0 ? $" · {result.Takeoff.Notes.Count} note(s): {result.Takeoff.Notes[0]}" : ""));
            foreach (var row in result.Takeoff.Sections)
                Console.WriteLine($"    {row.Section,-40} {row.MemberCount,3}  {Text(row.TotalLengthM),12} m  {Text(row.ConcreteVolumeM3),12} m³  {Text(row.SteelMassT),12} t");
        }
        static string Text(double? value) => value?.ToString("N3", CultureInfo.InvariantCulture) ?? "n/a";
    }

    /// <summary>Opt-in, live and read-only: attaches to the running SAP2000 and takes off the model that is open in
    /// it through the database-tables API — what Tab 08 does for "Connected model". Opens nothing, writes nothing,
    /// changes no units; the model's identity, units and lock state are compared before and after.</summary>
    public static async Task RunConnectedAsync()
    {
        using var sap = new SapGateway();
        var before = await sap.ConnectAsync();
        Console.WriteLine($"Attached to {before.Name} · SAP2000 {before.Version} · {before.Units} · locked = {before.IsLocked}");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tables = await sap.ReadDatabaseTablesAsync(IsolatedSapTableReader.TablePrefixes, before);
        Console.WriteLine($"{tables.TableNames.Count} tables in {watch.Elapsed.TotalSeconds:0.0} s, units {tables.Units}:");
        foreach (var name in tables.TableNames.Order(StringComparer.OrdinalIgnoreCase)) Console.WriteLine($"    {name}  ({tables.Rows(name).Count} rows)");
        string Sample(string table, params string[] fields) => tables.Rows(table).FirstOrDefault() is { } row
            ? string.Join("  ", fields.Select(field => $"{field}={row.Text(field) ?? "(none)"}")) : "(no rows)";
        Console.WriteLine("as SAP2000 hands the values over:");
        Console.WriteLine("    joint     " + Sample("JOINT COORDINATES", "Joint", "CoordSys", "CoordType", "XorR", "Y", "Z"));
        Console.WriteLine("    frame     " + Sample("CONNECTIVITY - FRAME", "Frame", "JointI", "JointJ", "IsCurved"));
        Console.WriteLine("    assigned  " + Sample("FRAME SECTION ASSIGNMENTS", "Frame", "AnalSect", "MatProp"));
        Console.WriteLine("    section   " + Sample("FRAME SECTION PROPERTIES 01 - GENERAL", "SectionName", "Material", "Shape", "t3", "t2", "tf", "tw", "Area"));
        Console.WriteLine("    material  " + Sample("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", "Material", "UnitMass", "E1"));
        Console.WriteLine("    cable     " + Sample("CABLE SECTION DEFINITIONS", "CableSect", "Material", "Diameter", "Area"));
        Check(QuantityTakeoffCalculator.MissingInputs(tables) is var missing && missing.Count == 0,
            "the connected SAP2000 returns every table the take-off needs" + (missing.Count > 0 ? " — missing: " + string.Join(", ", missing) : ""));
        var takeoff = QuantityTakeoffCalculator.Calculate(tables);
        static string Text(double? value) => value?.ToString("N3", CultureInfo.InvariantCulture) ?? "n/a";
        foreach (var row in takeoff.Sections)
            Console.WriteLine($"    {row.Section,-40} {row.MemberCount,3}  {Text(row.TotalLengthM),12} m  {Text(row.ConcreteVolumeM3),12} m³  {Text(row.SteelMassT),12} t  figure: {(row.Figure is null ? "none" : $"{row.Figure.Width * 1000:0.#} × {row.Figure.Height * 1000:0.#} mm")}");
        Console.WriteLine($"    {"Total",-40} {takeoff.Total.MemberCount,3}  {Text(takeoff.Total.TotalLengthM),12} m  {Text(takeoff.Total.ConcreteVolumeM3),12} m³  {Text(takeoff.Total.SteelMassT),12} t");
        foreach (var note in takeoff.Notes) Console.WriteLine("    note: " + note);
        var after = await sap.GetCurrentModelInfoAsync();
        Check(after == before, $"the connected model is as it was: {after.Name} · {after.Units} · locked = {after.IsLocked}");
        Check(takeoff.Complete, "every section of the connected model is taken off in full");

        // SAP2000's own text copy of the same model, when the model has not been changed since it was saved, must agree
        var export = Path.ChangeExtension(before.Path, QuantityCandidateCatalog.TextExportExtension);
        if (!File.Exists(export)) { Console.WriteLine("No text export beside the model to compare with."); return; }
        var saved = QuantityTakeoffCalculator.Calculate(SapTableSet.ReadText(export));
        var same = saved.Sections.Count == takeoff.Sections.Count && saved.Sections.Zip(takeoff.Sections).All(pair =>
            pair.First.Section == pair.Second.Section && pair.First.MemberCount == pair.Second.MemberCount && Near(pair.First.TotalLengthM, pair.Second.TotalLengthM, 1e-4) &&
            Near(pair.First.ConcreteVolumeM3, pair.Second.ConcreteVolumeM3, 1e-4) && Near(pair.First.SteelMassT, pair.Second.SteelMassT, 1e-4));
        Console.WriteLine(same
            ? $"SAME as the model's saved text export {Path.GetFileName(export)} (to 0.01 %)."
            : $"DIFFERENT from the saved text export {Path.GetFileName(export)}: total {Text(saved.Total.TotalLengthM)} m, {Text(saved.Total.ConcreteVolumeM3)} m³, {Text(saved.Total.SteelMassT)} t there — " +
              "expected only if the model was changed in SAP2000 since it was saved.");
    }

    /// <summary>Opt-in, live: reads one candidate model through the isolated SAP2000 workflow — a SAP2000 instance of
    /// its own on a private copy — and compares the result with the candidate's text export. Never attaches to a
    /// running SAP2000 and never opens the candidate's own files in SAP2000.</summary>
    public static async Task RunLiveAsync(string candidateModelPath)
    {
        var model = Path.GetFullPath(candidateModelPath);
        var folder = Path.GetDirectoryName(model)!;
        var before = FolderState(folder);
        var candidate = new QuantityCandidate(Path.GetFileName(folder), model, Path.ChangeExtension(model, QuantityCandidateCatalog.TextExportExtension), null, null, []);
        var service = new QuantityTakeoffService(new IsolatedSapTableReader(new SapInstanceHostFactory()));
        var live = await service.ReadWithSapAsync(candidate, CancellationToken.None);
        Check(FolderState(folder) == before, "the candidate folder is exactly as it was: no file added, removed or rewritten");
        Console.WriteLine(live.SourceText);
        foreach (var row in live.Takeoff.Sections)
            Console.WriteLine($"    {row.Section,-40} {row.MemberCount,3}  {row.TotalLengthM,12:N3} m  {row.ConcreteVolumeM3,12:N3} m³  {row.SteelMassT,12:N3} t");
        foreach (var note in live.Takeoff.Notes) Console.WriteLine("    note: " + note);
        var export = (await service.TryTextExportAsync(candidate, CancellationToken.None)).Result;
        if (export is null) { Console.WriteLine("No usable text export to compare with."); return; }
        Check(live.Takeoff.Sections.Count == export.Takeoff.Sections.Count && live.Takeoff.Sections.Zip(export.Takeoff.Sections).All(pair =>
                  pair.First.Section == pair.Second.Section && pair.First.MemberCount == pair.Second.MemberCount &&
                  Near(pair.First.TotalLengthM, pair.Second.TotalLengthM, 1e-4) && Near(pair.First.ConcreteVolumeM3, pair.Second.ConcreteVolumeM3, 1e-4) &&
                  Near(pair.First.SteelMassT, pair.Second.SteelMassT, 1e-4)),
            "the isolated SAP2000 read and the candidate's text export give the same take-off");
    }

    /// <summary>For the UI check: two candidate folders beside a baseline, each with SAP2000's text copy of its model —
    /// a complete one, and a taller one with another section on two members and a joint without coordinates.</summary>
    public static void WriteCandidates(string baselineModelPath)
    {
        var fixture = ExportText.Parse(File.ReadAllText(Path.Combine(FemSapBenchmark.DefaultFolder, FemSapBenchmark.ModelFile)));
        var paths = new StudyPathService();
        void Write(string id, ExportText export)
        {
            var model = paths.GetIterationModelPath(baselineModelPath, id);
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            File.WriteAllText(Path.ChangeExtension(model, QuantityCandidateCatalog.TextExportExtension), export.Named(Path.GetFileNameWithoutExtension(model) + ".$2k").Text);
            File.WriteAllText(model, "binary model of " + id);
        }
        Write("L01_H01_P01", fixture);
        Write("L01_H02_P01", Reassigned(Taller(fixture)).Edit("JOINT COORDINATES", line => Field(line, "Joint") == "MTrPt005" ? null : line));
    }

    /// <summary>The fixture model as the connected SAP2000 would hand it over through the database-tables API.</summary>
    public static SapTableSet FixtureTables(ModelInfo model) =>
        ApiTables(ExportText.Parse(File.ReadAllText(Path.Combine(FemSapBenchmark.DefaultFolder, FemSapBenchmark.ModelFile))), model);

    private static SapTableSet ApiTables(ExportText export, ModelInfo model) => SapTableSet.FromTables(
        export.ApiTables().Select(table => KeyValuePair.Create(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(table.Table.ToLowerInvariant()),
            (IReadOnlyList<SapTableRow>)Enumerable.Range(0, table.Records).Select(record => new SapTableRow(
                table.Fields.Select((field, column) => KeyValuePair.Create(field, table.Data[record * table.Fields.Length + column])))).ToArray())),
        SapTableUnits.From(model), $"SAP2000 database tables of {model.Name}");

    // ---------- reader and units ----------

    private static void ReaderAndUnits(ExportText fixture)
    {
        using var folder = new TempFolder();
        var path = folder.Write("model.$2k", fixture.Text);
        var tables = SapTableSet.ReadText(path);
        Check(tables.Units == new SapTableUnits("KN", "m") && tables.Units.MetresPerLength == 1 && tables.Units.TonnesPerCubicMetrePerDensity == 1 &&
              tables.RecordedFileName == "COT Framing_V1.2.$2k" && tables.Rows("CONNECTIVITY - FRAME").Count == 38 && tables.Rows("connectivity - cable").Count == 16 &&
              tables.Rows("SECTION DESIGNER PROPERTIES 16 - SHAPE POLYGON").Count == 4 &&
              tables.Rows("CABLE SECTION DEFINITIONS").Single(row => row.Text("CableSect") == Strand).Text("Material") == "HDPE Galvanised Strand T15.7 – Y1860S7" &&
              QuantityTakeoffCalculator.MissingInputs(tables).Count == 0,
            "SAP2000's text copy of a model is read table by table: units, the file it was written for, continued records, quoted names");
        Throws<InvalidDataException>(() => SapTableSet.ReadText(folder.Write("cut.$2k", fixture.Text.Replace("END TABLE DATA", ""))),
            "a text export SAP2000 did not finish writing is refused");
        Throws<InvalidDataException>(() => SapTableSet.ReadText(folder.Write("units.$2k", fixture.Drop("PROGRAM CONTROL").Text)),
            "a text export that does not state its units is refused");
        Throws<NotSupportedException>(() => SapTableUnits.Parse("Furlong, ell, C"), "unknown units are refused, not guessed");
        var imperial = SapTableUnits.Parse("Kip, in, F");
        Check(Near(imperial.MetresPerLength, 0.0254) && Near(imperial.SquareMetresPerArea, 0.00064516) &&
              Near(7.3455e-7 * imperial.TonnesPerCubicMetrePerDensity, 7.8501, 1e-4) &&                     // steel, kip·s²/in⁴
              Near(7.849e-9 * SapTableUnits.Parse("N, mm, C").TonnesPerCubicMetrePerDensity, 7.849) &&       // steel, N·s²/mm⁴ = t/mm³
              Near(SapTableUnits.From(new ModelInfo("m.sdb", "27", 12, "Ton", "m", false)).TonnesPerCubicMetrePerDensity, 9.80665),
            "length, area and mass density convert to m, m² and t/m³ from SAP2000's consistent units");
    }

    // ---------- Section Designer geometry ----------

    private static void Geometry()
    {
        SectionDesignerShape Shape(string name, double z, params PlanePoint[][] pieces) => new(name, "M", z, pieces);
        var square = SectionDesignerGeometry.Rectangle(0, 0, 2, 2);
        var diamond = SectionDesignerGeometry.Rotate(SectionDesignerGeometry.Rectangle(1, 0, 1, 1), 1, 0, 45);
        var areas = SectionDesignerGeometry.VisibleAreas([Shape("back", 1, square), Shape("front", 2, diamond)], out var problem);
        Check(problem is null && Near(areas["front"], 1) && Near(areas["back"], 3.5),
            "a shape in front replaces what it covers: a rotated square half over the edge of another takes exactly its overlap");
        areas = SectionDesignerGeometry.VisibleAreas([Shape("back", 2, square), Shape("front", 1, diamond)], out problem);
        Check(problem is null && Near(areas["back"], 4) && Near(areas["front"], 0.5), "the drawing order decides, not the order of the records");
        SectionDesignerGeometry.VisibleAreas([Shape("a", 1, square), Shape("b", 1, diamond)], out problem);
        Check(problem is not null && problem.Contains("same Z-order"), "shapes that overlap at one Z-order have no defined owner and are reported");
        SectionDesignerGeometry.VisibleAreas([Shape("a", 1, square), Shape("b", 1, SectionDesignerGeometry.Rectangle(5, 0, 1, 1))], out problem);
        Check(problem is null, "shapes at one Z-order that do not touch are fine");
        var three = SectionDesignerGeometry.VisibleAreas(
            [Shape("slab", 1, SectionDesignerGeometry.Rectangle(0, 0, 4, 4)), Shape("plate", 2, SectionDesignerGeometry.Rectangle(0, 0, 2, 2)),
             Shape("bar", 3, SectionDesignerGeometry.Rectangle(0, 0, 1, 1))], out problem);
        Check(problem is null && Near(three["slab"], 12) && Near(three["plate"], 3) && Near(three["bar"], 1), "three shapes stacked: each area is counted once");

        var angle = SectionDesignerGeometry.ConvexPieces([new(0, 0), new(2, 0), new(2, 1), new(1, 1), new(1, 2), new(0, 2)]);
        var clockwise = SectionDesignerGeometry.ConvexPieces([new(0, 2), new(1, 2), new(1, 1), new(2, 1), new(2, 0), new(0, 0)]);
        Check(angle is not null && Near(SectionDesignerGeometry.Area(angle), 3) && clockwise is not null && Near(SectionDesignerGeometry.Area(clockwise), 3),
            "a re-entrant polygon is cut into convex pieces of the same total area, whichever way it was drawn");
        Check(SectionDesignerGeometry.ConvexPieces([new(0, 0), new(2, 2), new(2, 0), new(0, 2)]) is null &&
              SectionDesignerGeometry.ConvexPieces([new(0, 0), new(1, 1), new(2, 2)]) is null,
            "an outline that crosses itself, or has no area, is refused");
    }

    // ---------- the calculation on stored model data ----------

    private static QuantityTakeoff Calculate(ExportText text)
    {
        using var folder = new TempFolder();
        return QuantityTakeoffCalculator.Calculate(SapTableSet.ReadText(folder.Write("model.$2k", text.Text)));
    }

    private static QuantitySectionRow Row(QuantityTakeoff takeoff, string section) => takeoff.Sections.Single(row => row.Section == section);

    /// <summary>The candidate a study derives for a taller truss: top joints up 5 m, middle joints up 2.5 m.</summary>
    private static ExportText Taller(ExportText text) => text.Edit("JOINT COORDINATES", line =>
        Field(line, "Joint") is { } joint && (joint.StartsWith("TTrPt") || joint.StartsWith("MTrPt"))
            ? Set(line, "Z", (double.Parse(Field(line, "Z")!, CultureInfo.InvariantCulture) + (joint[0] == 'T' ? 5 : 2.5)).ToString(CultureInfo.InvariantCulture))
            : line);

    private static ExportText Reassigned(ExportText text) => text.Edit("FRAME SECTION ASSIGNMENTS", line =>
        Field(line, "Frame") is "VRM001" or "VRM002" ? Set(line, "AnalSect", "FSEC1") : line);

    private static void Calculation(ExportText fixture)
    {
        var baseline = Calculate(fixture);
        Check(baseline.Sections.Select(row => row.Section).SequenceEqual([Cft, MegaA, MegaB, Shs, Strand]) &&
              baseline.Sections.Select(row => row.Kind).SequenceEqual([QuantityMemberKind.Frame, QuantityMemberKind.Frame, QuantityMemberKind.Frame, QuantityMemberKind.Frame, QuantityMemberKind.Cable]) &&
              baseline.Sections.Select(row => row.MemberCount).SequenceEqual([2, 4, 2, 30, 16]) && baseline.Complete && baseline.Notes.Count == 0,
            "members are grouped by the section assigned to them in the model — frames, then cables, each section once");
        Check(Near(Row(baseline, Cft).TotalLengthM, 200) && Near(Row(baseline, MegaA).TotalLengthM, 80) && Near(Row(baseline, MegaB).TotalLengthM, 20) &&
              Near(Row(baseline, Shs).TotalLengthM, 300) && Near(Row(baseline, Strand).TotalLengthM, 490.3610384951724),
            "a member's length is the distance between its end joints as the model holds them");

        var chord = Row(baseline, Cft);
        Check(Near(chord.ConcreteVolumeM3, 0.9 * 1.7 * 200) && Near(chord.SteelMassT, (2.0 * 1.2 - 0.9 * 1.7) * 200 * SteelDensity) &&
              chord.Components.Count == 2 &&
              chord.Components.Single(part => part.Class == QuantityMaterialClass.Concrete) is { Material: "C70/85" } infill && Near(infill.AreaM2, 1.53) &&
              chord.Components.Single(part => part.Class == QuantityMaterialClass.Steel) is { Material: "S460" } tube && Near(tube.AreaM2, 0.87) && Near(tube.DensityTPerM3, SteelDensity),
            "a composite section is separated into its materials: the concrete infill by volume, the steel tube by its own density");
        Check(!Near(chord.ConcreteVolumeM3, 5.98609756097561 * 200, 1e-2) && Near(chord.ConcreteVolumeM3, 306) && Near(chord.SteelMassT, 1365.7342441130254),
            "the transformed area SAP2000 reports for the section (5.986 m²) is not what is taken off — 306.0 m³ and 1,365.7 t, not 1,197 m³");
        Check(Near(Row(baseline, MegaA).ConcreteVolumeM3, (3 * 1.5 - 3 * 0.07) * 80) && Near(Row(baseline, MegaA).SteelMassT, 131.86399598332662) &&
              Near(Row(baseline, MegaB).ConcreteVolumeM3, (2.5 * 3 - 3 * 0.07) * 20) && Near(Row(baseline, MegaB).SteelMassT, 32.965998995831654),
            "embedded steel shapes displace the concrete they sit in: 4.29 m² of concrete in a 1.5 × 3.0 column, not 4.50");
        Check(Row(baseline, Shs).ConcreteVolumeM3 == 0 && Near(Row(baseline, Shs).SteelMassT, 1220.6838485313665) &&
              Row(baseline, Strand).ConcreteVolumeM3 == 0 && Near(Row(baseline, Strand).SteelMassT, 110.85629119867916) &&
              Row(baseline, Strand).Components.Single() is { MaterialType: "Tendon", Class: QuantityMaterialClass.Steel },
            "a steel tube and the cable strand have no concrete (a true zero) and their steel mass");
        Check(baseline.Total is { MemberCount: 54 } total && Near(total.TotalLengthM, 1090.3610384951724) && Near(total.ConcreteVolumeM3, 795.0) &&
              Near(total.SteelMassT, 2862.104378822229) && Near(total.SteelMassT, baseline.Sections.Sum(row => row.SteelMassT!.Value)) &&
              Near(total.ConcreteVolumeM3, baseline.Sections.Sum(row => row.ConcreteVolumeM3!.Value)) && Near(total.AvailableSteelMassT, total.SteelMassT),
            "the total is the sum of the sections: 54 members, 1,090.361 m, 795.000 m³, 2,862.104 t");

        var candidate = Calculate(Reassigned(Taller(fixture)));
        Check(candidate.Sections.Select(row => row.Section).SequenceEqual([Cft, "FSEC1", MegaA, MegaB, Shs, Strand]) &&
              Row(candidate, "FSEC1").MemberCount == 2 && Near(Row(candidate, "FSEC1").TotalLengthM, 25) && Near(Row(candidate, "FSEC1").SteelMassT, 0.8368080551149787) &&
              Row(candidate, Shs).MemberCount == 28 && Near(Row(candidate, Shs).TotalLengthM, 320) && Near(Row(candidate, Shs).SteelMassT, 1302.0627717667905),
            "another candidate is taken off with its own section assignments: two verticals in another section form their own row");
        Check(Near(Row(candidate, MegaA).TotalLengthM, 90) && Near(Row(candidate, MegaA).ConcreteVolumeM3, 386.1) && Near(Row(candidate, MegaA).SteelMassT, 148.34699548124246) &&
              Near(Row(candidate, Strand).TotalLengthM, 531.4982387880247) && Near(Row(candidate, Strand).SteelMassT, 120.15620921165504) &&
              Near(Row(candidate, Cft).TotalLengthM, 200) && Near(Row(candidate, MegaB).TotalLengthM, 20) &&
              Near(candidate.Total.TotalLengthM, 1186.4982387880245) && Near(candidate.Total.ConcreteVolumeM3, 837.9) && Near(candidate.Total.SteelMassT, 2970.10302762366),
            "…and with its own member lengths: columns and cables are longer in the taller candidate, the chords are not");

        var twice = Calculate(fixture
            .Add("CONNECTIVITY - FRAME", "   Frame=VRM001   JointI=BTrPt004   JointJ=MTrPt004   IsCurved=No")
            .Add("FRAME SECTION ASSIGNMENTS", $"   Frame=VRM001   AutoSelect=N.A.   AnalSect={Shs}   MatProp=Default"));
        Check(Row(twice, Shs).MemberCount == 30 && Near(Row(twice, Shs).TotalLengthM, 300) && Near(twice.Total.SteelMassT, 2862.104378822229) &&
              twice.Total.MemberCount == 54 && twice.Notes.Count(note => note.Contains("listed twice") && note.Contains("counted once")) == 2,
            "a member the tables list twice is counted once, and the note says so");
        var twins = Calculate(fixture
            .Add("CONNECTIVITY - FRAME", "   Frame=VRM001-copy   JointI=MTrPt004   JointJ=BTrPt004   IsCurved=No")
            .Add("FRAME SECTION ASSIGNMENTS", $"   Frame=VRM001-copy   AutoSelect=N.A.   AnalSect={Shs}   MatProp=Default"));
        Check(Row(twins, Shs).MemberCount == 31 && twins.Complete &&
              twins.Notes.Any(note => note.Contains("VRM001") && note.Contains("VRM001-copy") && note.Contains("same two joints")),
            "two objects between the same joints with one section are both in the model, so both are counted — and pointed out");

        SectionFigures(fixture, baseline, candidate);

        var overwritten = Calculate(fixture.Edit("FRAME SECTION ASSIGNMENTS", line => Field(line, "Frame") == "VRM003" ? Set(line, "MatProp", "S355") : line));
        Check(Near(Row(overwritten, Shs).SteelMassT, 1220.6838485313665) && Row(overwritten, Shs).Components.Select(part => part.Material).Order().SequenceEqual(["S355", "S460"]),
            "a member's material overwrite replaces the material of its (one-material) section");
    }

    // ---------- the figure of a section: its stored shapes, to scale ----------

    private static (double Width, double Height, double CentreX, double CentreY) Box(IReadOnlyList<QuantityFigurePoint> outline) =>
        (outline.Max(point => point.X) - outline.Min(point => point.X), outline.Max(point => point.Y) - outline.Min(point => point.Y),
         (outline.Max(point => point.X) + outline.Min(point => point.X)) / 2, (outline.Max(point => point.Y) + outline.Min(point => point.Y)) / 2);

    private static bool Is(IReadOnlyList<QuantityFigurePoint> outline, double width, double height, double centreX = 0, double centreY = 0) =>
        Box(outline) is var box && Near(box.Width, width, 1e-9) && Near(box.Height, height, 1e-9) && Math.Abs(box.CentreX - centreX) < 1e-9 && Math.Abs(box.CentreY - centreY) < 1e-9;

    private static void SectionFigures(ExportText fixture, QuantityTakeoff baseline, QuantityTakeoff candidate)
    {
        var chord = Row(baseline, Cft).Figure!;
        Check(chord.Shapes.Select(shape => (shape.Name, shape.Material, shape.Class, shape.Outlines.Count))
                  .SequenceEqual([("Box1", "S460", QuantityMaterialClass.Steel, 2), ("Polygon1", "C70/85", QuantityMaterialClass.Concrete, 1)]) &&
              Near(chord.Width, 1.2) && Near(chord.Height, 2.0) && Near(chord.MinX, -0.6) && Near(chord.MaxY, 1.0) &&
              Is(chord.Shapes[0].Outlines[0], 1.2, 2.0) && Is(chord.Shapes[0].Outlines[1], 0.9, 1.7) && Is(chord.Shapes[1].Outlines[0], 0.9, 1.7) &&
              chord.Shapes[1].Outlines[0].Count == 4,
            "the figure of a composite section is its stored shapes in metres, back to front: the steel tube with its opening, then the concrete that fills it");
        var column = Row(baseline, MegaA).Figure!;
        Check(column.Shapes.Select(shape => shape.Name).SequenceEqual(["Rectangle1", "ISection1", "ISection2", "ISection3"]) &&
              column.Shapes.Select(shape => shape.Class).SequenceEqual([QuantityMaterialClass.Concrete, QuantityMaterialClass.Steel, QuantityMaterialClass.Steel, QuantityMaterialClass.Steel]) &&
              Near(column.Width, 1.5) && Near(column.Height, 3.0) && Is(column.Shapes[0].Outlines[0], 1.5, 3.0) &&
              column.Shapes.Skip(1).All(shape => shape.Outlines.Count == 1 && shape.Outlines[0].Count == 12) &&
              Is(column.Shapes[1].Outlines[0], 0.5, 0.5) && Is(column.Shapes[2].Outlines[0], 0.5, 0.5, 0, 1.0) && Is(column.Shapes[3].Outlines[0], 0.5, 0.5, 0, -1.0) &&
              Row(baseline, MegaB).Figure is { Width: var wide, Height: var deep } && Near(wide, 3.0) && Near(deep, 2.5),
            "embedded I-shapes are drawn where they are stored — at y = −1, 0 and +1 m in the 1.5 × 3.0 column — in front of the concrete");
        Check(Row(baseline, Shs).Figure is { Shapes: [{ Class: QuantityMaterialClass.Steel, Material: "S460", Outlines: [var outer, var opening] }] } &&
              Is(outer, 1.2, 1.2) && Is(opening, 0.96, 0.96) &&
              Row(baseline, Strand).Figure is { Shapes: [{ Class: QuantityMaterialClass.Steel, Outlines: [var strand] }] } round && Near(round.Width, 0.1915, 1e-9) && Near(round.Height, 0.1915, 1e-9) &&
              strand.Count >= 48 &&
              Row(candidate, "FSEC1").Figure is { Shapes: [{ Outlines: [var rolled] }] } && Is(rolled, 0.127, 0.3048) && rolled.Count == 12,
            "a standard section is drawn from its own stored dimensions — a 1.2 m tube with 0.12 m walls, a 127 × 305 I-section — and a cable as a circle of its stored diameter");
        var noShape = Calculate(fixture.Add("SECTION DESIGNER PROPERTIES 13 - SHAPE SOLID CIRCLE", $"   SectionName={Cft}   ShapeName=Circle1   ShapeMat=C70/85   ZOrder=3   XCenter=0   YCenter=0   Diameter=0.2"));
        var hidden = Calculate(fixture.Edit("SECTION DESIGNER PROPERTIES 04 - SHAPE I/WIDE FLANGE", line => Field(line, "SectionName") == MegaA ? Set(line, "ZOrder", "0") : line));
        var loose = Calculate(fixture.Edit("FRAME SECTION ASSIGNMENTS", line => Field(line, "Frame") == "VRM005" ? null : line));
        Check(Row(noShape, Cft).Figure is null && Row(noShape, MegaA).Figure is not null &&
              Row(loose, QuantityTakeoffCalculator.UnassignedSection).Figure is null &&
              Row(hidden, MegaA) is { ConcreteVolumeM3: null, Figure: { } stored } && stored.Shapes.Select(shape => shape.Name).SequenceEqual(["ISection1", "ISection2", "ISection3", "Rectangle1"]),
            "a section with a shape that is not read has no figure rather than a partial one; a section whose quantities are withheld is still drawn as it is stored");
    }

    // ---------- what cannot be established stays unavailable ----------

    private static void MissingAndUnsupportedData(ExportText fixture)
    {
        var noJoint = Calculate(fixture.Edit("JOINT COORDINATES", line => Field(line, "Joint") == "MTrPt004" ? null : line));
        var gap = Row(noJoint, Shs);
        Check(gap.MemberCount == 30 && gap.TotalLengthM is null && gap.ConcreteVolumeM3 == 0 && gap.SteelMassT is null && !gap.Complete &&
              gap.Problems.Count == 4 && gap.Problems.All(problem => problem.Contains("joint 'MTrPt004' has no coordinates")) &&
              Row(noJoint, Cft).Complete && Row(noJoint, Strand).Complete,
            "the four members that meet at a joint without coordinates have no length: their section's length and steel are unavailable, the other sections are not affected");
        Check(noJoint.Total is { TotalLengthM: null, SteelMassT: null, MemberCount: 54 } rest && Near(rest.ConcreteVolumeM3, 795.0) &&
              Near(rest.AvailableSteelMassT, 2862.104378822229 - 1220.6838485313665) && Near(rest.AvailableLengthM, 1090.3610384951724 - 300) && !noJoint.Complete &&
              noJoint.Notes.Any(note => note.StartsWith(Shs + ":")),
            "the total is then unavailable too — not zero, and not the sum of what is left presented as a total");

        var noDensity = Calculate(fixture.Edit("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", line => Field(line, "Material") == "S460" ? Drop(line, "UnitMass") : line));
        Check(Row(noDensity, Shs).SteelMassT is null && Row(noDensity, Cft).SteelMassT is null && Near(Row(noDensity, Cft).ConcreteVolumeM3, 306) &&
              Near(Row(noDensity, MegaA).SteelMassT, 131.86399598332662) && noDensity.Total.SteelMassT is null && Near(noDensity.Total.ConcreteVolumeM3, 795) &&
              Row(noDensity, Shs).Problems.Single().Contains("'S460' has no mass density"),
            "a steel without a mass density has no mass; the concrete of the same section is still taken off");

        string Why(QuantityTakeoff takeoff, string section) => string.Join(" | ", Row(takeoff, section).Problems);
        bool Unavailable(QuantityTakeoff takeoff, string section) =>
            Row(takeoff, section) is { ConcreteVolumeM3: null, SteelMassT: null, TotalLengthM: not null } && takeoff.Total is { ConcreteVolumeM3: null, SteelMassT: null };

        var circle = Calculate(fixture.Add("SECTION DESIGNER PROPERTIES 13 - SHAPE SOLID CIRCLE", $"   SectionName={Cft}   ShapeName=Circle1   ShapeMat=C70/85   ZOrder=3   XCenter=0   YCenter=0   Diameter=0.2"));
        Check(Unavailable(circle, Cft) && Why(circle, Cft).Contains("SECTION DESIGNER PROPERTIES 13 - SHAPE SOLID CIRCLE") && Row(circle, MegaA).Complete,
            "a Section Designer shape the take-off does not read makes that section unavailable and names the table");
        var bars = Calculate(fixture.Edit("SECTION DESIGNER PROPERTIES 12 - SHAPE SOLID RECTANGLE", line => Field(line, "SectionName") == MegaA ? Set(line, "Reinforcing", "Yes") : line));
        Check(Unavailable(bars, MegaA) && Why(bars, MegaA).Contains("'Rectangle1' carries reinforcement") && Row(bars, MegaB).Complete,
            "a shape that carries reinforcement is not taken off without it");
        var rounded = Calculate(fixture.Edit("SECTION DESIGNER PROPERTIES 16 - SHAPE POLYGON", line => line.Contains("Y=-0.85") && line.Contains("X=-0.44") ? Set(line, "Radius", "0.05") : line));
        Check(Unavailable(rounded, Cft) && Why(rounded, Cft).Contains("rounded corners"), "a polygon with rounded corners is not read as if its corners were sharp");
        var behind = Calculate(fixture.Edit("SECTION DESIGNER PROPERTIES 04 - SHAPE I/WIDE FLANGE", line => Field(line, "SectionName") == MegaA ? Set(line, "ZOrder", "0") : line));
        Check(Unavailable(behind, MegaA) && Why(behind, MegaA).Contains("but SAP2000 reports 5.3656 m²"),
            "shape areas that do not reproduce the area SAP2000 reports are withheld: here the steel was put behind the concrete");
        var tied = Calculate(fixture.Edit("SECTION DESIGNER PROPERTIES 04 - SHAPE I/WIDE FLANGE", line => Field(line, "SectionName") == MegaB ? Set(line, "ZOrder", "1") : line));
        Check(Unavailable(tied, MegaB) && Why(tied, MegaB).Contains("same Z-order"), "an embedded shape at the Z-order of the shape around it is not guessed");
        var library = Calculate(fixture.Edit("SECTION DESIGNER PROPERTIES 09 - SHAPE BOX/TUBE", line => Set(line, "ShapeType", "\"HSS20X12X5/8\"")));
        Check(Unavailable(library, Cft) && Why(library, Cft).Contains("only user-defined dimensions are read"), "a shape taken from a section library is not read from fields that may not describe it");

        var aluminium = Calculate(fixture.Edit("MATERIAL PROPERTIES 01 - GENERAL", line => Field(line, "Material") == "S355" ? Set(line, "Type", "Aluminum") : line));
        Check(Unavailable(aluminium, MegaA) && Why(aluminium, MegaA).Contains("type 'Aluminum'") && Row(aluminium, Shs).Complete,
            "a material that is neither concrete nor steel is not counted as either");
        var undefined = Calculate(fixture.Edit("FRAME SECTION PROPERTIES 01 - GENERAL", line => Field(line, "SectionName") == Shs ? null : line));
        Check(Unavailable(undefined, Shs) && Why(undefined, Shs).Contains($"frame section '{Shs}' is not defined"), "a section the model does not define gives no quantity");
        var reinforced = Calculate(Reassigned(fixture)
            .Edit("FRAME SECTION PROPERTIES 01 - GENERAL", line => Field(line, "SectionName") == "FSEC1" ? Set(Set(line, "Material", "C30/37"), "Shape", "Rectangular") : line)
            .Add("FRAME SECTION PROPERTIES 02 - CONCRETE COLUMN", "   SectionName=FSEC1   RebarMatL=Rebar   ReinfConfig=Rectangular   Cover=0.04   NumBars3Dir=3   NumBars2Dir=3   BarSizeL=#9"));
        Check(Row(reinforced, "FSEC1") is { SteelMassT: null } column && Near(column.ConcreteVolumeM3, 0.004264507599999999 * 20) &&
              column.Problems.Single().Contains("reinforcement is not taken off") && reinforced.Total.SteelMassT is null,
            "a concrete section with reinforcement data gives its concrete and no steel — the bars are not taken off");
        var tapered = Calculate(fixture.Edit("FRAME SECTION PROPERTIES 01 - GENERAL", line => Field(line, "SectionName") == Shs ? Set(line, "Shape", "Nonprismatic") : line));
        Check(Unavailable(tapered, Shs) && Why(tapered, Shs).Contains("Nonprismatic"), "a non-prismatic section has no one area and is not taken off");

        var unassigned = Calculate(fixture.Edit("FRAME SECTION ASSIGNMENTS", line => Field(line, "Frame") == "VRM005" ? null : line));
        Check(Row(unassigned, QuantityTakeoffCalculator.UnassignedSection) is { MemberCount: 1, ConcreteVolumeM3: null, SteelMassT: null } loose && Near(loose.TotalLengthM, 10) &&
              Row(unassigned, Shs).MemberCount == 29 && unassigned.Total.MemberCount == 54 && unassigned.Total.SteelMassT is null && Near(unassigned.Total.TotalLengthM, 1090.3610384951724),
            "a member without a section assignment is listed on a line of its own, with its length and no quantities");
        var conflict = Calculate(fixture.Add("FRAME SECTION ASSIGNMENTS", "   Frame=VRM005   AutoSelect=N.A.   AnalSect=FSEC1   MatProp=Default"));
        Check(Row(conflict, QuantityTakeoffCalculator.UnassignedSection) is { MemberCount: 1 } disputed && disputed.Problems.Single().Contains("more than one section") &&
              Row(conflict, Shs).MemberCount == 29 && conflict.Sections.All(row => row.Section != "FSEC1") && conflict.Total.MemberCount == 54,
            "a member assigned to two different sections is counted with neither");
        var curved = Calculate(fixture.Edit("CONNECTIVITY - FRAME", line => Field(line, "Frame") == "TopChord" ? Set(line, "IsCurved", "Yes") : line));
        Check(Row(curved, Cft) is { TotalLengthM: null, ConcreteVolumeM3: null, SteelMassT: null } arc && arc.Problems.Single().Contains("curved"),
            "a curved member is not measured along its chord");
        var onSection = Calculate(fixture.Edit("FRAME SECTION ASSIGNMENTS", line => Field(line, "Frame") == "TopChord" ? Set(line, "MatProp", "S355") : line));
        Check(Row(onSection, Cft) is { ConcreteVolumeM3: null, SteelMassT: null } chord && chord.Problems.Single().Contains("overwrites the material of a Section Designer section"),
            "a material overwrite on a composite section is not applied to one shape or the other by guesswork");

        using var folder = new TempFolder();
        SapTableSet Tables(ExportText text) => SapTableSet.ReadText(folder.Write(Guid.NewGuid().ToString("N") + ".$2k", text.Text));
        Check(QuantityTakeoffCalculator.MissingInputs(Tables(fixture.Drop("FRAME SECTION ASSIGNMENTS"))).SequenceEqual(["FRAME SECTION ASSIGNMENTS"]) &&
              QuantityTakeoffCalculator.MissingInputs(Tables(fixture.Drop("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES"))).Count == 1 &&
              QuantityTakeoffCalculator.MissingInputs(Tables(fixture.Drop("SECTION DESIGNER PROPERTIES 09 - SHAPE BOX/TUBE").Drop("SECTION DESIGNER PROPERTIES 16 - SHAPE POLYGON")))
                  .SequenceEqual([$"Section Designer shapes of '{Cft}'"]) &&
              QuantityTakeoffCalculator.MissingInputs(Tables(fixture.Drop("CONNECTIVITY - CABLE").Drop("CABLE SECTION ASSIGNMENTS").Drop("CABLE SECTION DEFINITIONS"))).Count == 0,
            "a table set states which of the tables a take-off needs it does not hold — a model without cables needs no cable tables");
    }

    // ---------- units ----------

    /// <summary>A small model written in the given units: a concrete column with an embedded steel tube, a steel
    /// beam and a strand. In metres: column 6, beam 8, cable 10.</summary>
    private static string SmallModel(string units, double tubeCentreX = 0, double tubeRotation = 0, double sectionAreaM2 = 0.7696 + 0.0304 * 200 / 30)
    {
        var (metres, newtons) = units switch
        {
            "KN, m, C" => (1.0, 1000.0), "N, mm, C" => (0.001, 1.0), "Kip, in, F" => (0.0254, 4448.2216152605), "Kip, ft, F" => (0.3048, 4448.2216152605),
            "Lb, in, F" => (0.0254, 4.4482216152605), "KN, cm, C" => (0.01, 1000.0), "Kgf, m, C" => (1.0, 9.80665), "Tonf, mm, C" => (0.001, 9806.65),
            _ => throw new ArgumentException(units)
        };
        string L(double value) => (value / metres).ToString("R", CultureInfo.InvariantCulture);
        string A(double value) => (value / (metres * metres)).ToString("R", CultureInfo.InvariantCulture);
        string E(double kiloNewtonsPerM2) => (kiloNewtonsPerM2 * 1000 / newtons * metres * metres).ToString("R", CultureInfo.InvariantCulture);
        string Rho(double tonnesPerM3) => (tonnesPerM3 * 1000 * Math.Pow(metres, 4) / newtons).ToString("R", CultureInfo.InvariantCulture);
        return string.Join(Environment.NewLine,
            "File C:\\Models\\small.$2k was saved on m/d/yy at h:mm:ss", "",
            "TABLE:  \"PROGRAM CONTROL\"", $"   ProgramName=SAP2000   Version=27.1.0   CurrUnits=\"{units}\"", "",
            "TABLE:  \"JOINT COORDINATES\"",
            $"   Joint=A   CoordSys=GLOBAL   CoordType=Cartesian   XorR=0   Y=0   Z=0",
            $"   Joint=B   CoordSys=GLOBAL   CoordType=Cartesian   XorR=0   Y=0   Z={L(6)}",
            $"   Joint=C   CoordSys=GLOBAL   CoordType=Cartesian   XorR={L(8)}   Y=0   Z={L(6)}", "",
            "TABLE:  \"CONNECTIVITY - FRAME\"", "   Frame=F1   JointI=A   JointJ=B   IsCurved=No", "   Frame=F2   JointI=B   JointJ=C   IsCurved=No", "",
            "TABLE:  \"CONNECTIVITY - CABLE\"", "   Cable=K1   JointI=A   JointJ=C", "",
            "TABLE:  \"FRAME SECTION ASSIGNMENTS\"", "   Frame=F1   AutoSelect=N.A.   AnalSect=COL   MatProp=Default", "   Frame=F2   AutoSelect=N.A.   AnalSect=BEAM   MatProp=Default", "",
            "TABLE:  \"CABLE SECTION ASSIGNMENTS\"", "   Cable=K1   CableSect=STRAND   MatProp=Default", "",
            "TABLE:  \"CABLE SECTION DEFINITIONS\"", $"   CableSect=STRAND   Material=STR   Specify=Area   Area={A(0.005)}", "",
            "TABLE:  \"FRAME SECTION PROPERTIES 01 - GENERAL\"",
            $"   SectionName=COL   Material=CONC   Shape=\"SD Section\"   Area={A(sectionAreaM2)}",
            $"   SectionName=BEAM   Material=STL   Shape=Box/Tube   Area={A(0.02)}", "",
            "TABLE:  \"MATERIAL PROPERTIES 01 - GENERAL\"", "   Material=CONC   Type=Concrete", "   Material=STL   Type=Steel", "   Material=STR   Type=Tendon", "",
            "TABLE:  \"MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES\"",
            $"   Material=CONC   UnitMass={Rho(2.5)}   E1={E(30e6)}", $"   Material=STL   UnitMass={Rho(7.85)}   E1={E(200e6)}", $"   Material=STR   UnitMass={Rho(7.85)}   E1={E(195e6)}", "",
            "TABLE:  \"SECTION DESIGNER PROPERTIES 01 - GENERAL\"", "   SectionName=COL   DesignType=\"Concrete Column\"", "",
            "TABLE:  \"SECTION DESIGNER PROPERTIES 09 - SHAPE BOX/TUBE\"",
            $"   SectionName=COL   ShapeName=Tube1   ShapeType=\"User Defined\"   ShapeMat=STL   ZOrder=2   XCenter={L(tubeCentreX)}   YCenter=0   Height={L(0.4)}   Width={L(0.4)}   FlngThick={L(0.02)}   WebThick={L(0.02)}   Rotation={tubeRotation.ToString(CultureInfo.InvariantCulture)}", "",
            "TABLE:  \"SECTION DESIGNER PROPERTIES 12 - SHAPE SOLID RECTANGLE\"",
            $"   SectionName=COL   ShapeName=Core1   ShapeMat=CONC   ZOrder=1   XCenter=0   YCenter=0   Height={L(0.8)}   Width={L(1)}   Rotation=0   Reinforcing=No", "",
            "END TABLE DATA", "");
    }

    private static void UnitConversion()
    {
        using var folder = new TempFolder();
        QuantityTakeoff Take(string text) => QuantityTakeoffCalculator.Calculate(SapTableSet.ReadText(folder.Write(Guid.NewGuid().ToString("N") + ".$2k", text)));
        bool Expected(QuantityTakeoff takeoff, double concreteArea = 0.7696) =>
            takeoff.Complete && takeoff.Sections.Select(row => row.Section).SequenceEqual(["BEAM", "COL", "STRAND"]) &&
            Near(Row(takeoff, "COL").TotalLengthM, 6, 1e-9) && Near(Row(takeoff, "COL").ConcreteVolumeM3, concreteArea * 6, 1e-9) && Near(Row(takeoff, "COL").SteelMassT, 0.0304 * 6 * 7.85, 1e-9) &&
            Near(Row(takeoff, "BEAM").TotalLengthM, 8, 1e-9) && Row(takeoff, "BEAM").ConcreteVolumeM3 == 0 && Near(Row(takeoff, "BEAM").SteelMassT, 0.02 * 8 * 7.85, 1e-9) &&
            Near(Row(takeoff, "STRAND").TotalLengthM, 10, 1e-9) && Near(Row(takeoff, "STRAND").SteelMassT, 0.005 * 10 * 7.85, 1e-9) &&
            Near(takeoff.Total.ConcreteVolumeM3, concreteArea * 6, 1e-9) && Near(takeoff.Total.SteelMassT, (0.0304 * 6 + 0.02 * 8 + 0.005 * 10) * 7.85, 1e-9);
        Check(Expected(Take(SmallModel("KN, m, C"))), "a concrete column with an embedded steel tube: the tube's walls displace concrete, its inside stays concrete (0.7696 m²)");
        Check(new[] { "N, mm, C", "Kip, in, F", "Kip, ft, F", "Lb, in, F", "KN, cm, C", "Kgf, m, C", "Tonf, mm, C" }.All(units => Expected(Take(SmallModel(units)))),
            "the same model saved in N-mm, kip-in, kip-ft, lb-in, kN-cm, kgf-m or tonf-mm gives the same metres, cubic metres and tonnes");
        Check(new[] { "KN, m, C", "N, mm, C", "Kip, in, F" }.All(units => Take(SmallModel(units)) is var model &&
                  Row(model, "COL").Figure is { Shapes.Count: 2 } figure && Near(figure.Width, 1.0, 1e-9) && Near(figure.Height, 0.8, 1e-9) &&
                  Row(model, "BEAM").Figure is null && Row(model, "STRAND").Figure is null),
            "a section figure is in metres whatever units the model was saved in; a section stored without its dimensions has no figure");
        Check(Expected(Take(SmallModel("N, mm, C", tubeRotation: 30))), "a rotated embedded shape displaces the same area");
        // the tube half outside the core: only the wall area inside the core (one web and half of each flange) displaces concrete
        Check(Expected(Take(SmallModel("KN, m, C", tubeCentreX: 0.5, sectionAreaM2: 0.7848 + 0.0304 * 200 / 30)), concreteArea: 0.8 - (0.02 * 0.36 + 2 * 0.2 * 0.02)),
            "a shape that lies partly outside the shape behind it displaces only what it covers");
        var wrong = Take(SmallModel("KN, m, C", tubeCentreX: 0.5));
        Check(Row(wrong, "COL") is { ConcreteVolumeM3: null, SteelMassT: null } && Row(wrong, "COL").Problems.Single().Contains("SAP2000 reports"),
            "…and when the section's own area says otherwise, nothing is reported for it");
    }

    // ---------- candidates through the study records ----------

    private static void Catalog(StudyOnDisk disk, ExportText fixture)
    {
        var catalog = new QuantityCandidateCatalog();
        Check(catalog.Discover(disk.Baseline).Count == 0 && catalog.Discover("").Count == 0 && catalog.Discover(null).Count == 0 && catalog.Discover(@"C:\nowhere\model.txt").Count == 0,
            "no study folder, no saved baseline: no candidates, no error");
        var start = new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
        disk.Candidate("L01_H10_P01", fixture);
        disk.Candidate("L01_H02_P01", Taller(fixture));
        disk.Candidate("L01_H01_P01", fixture);
        disk.Candidate("L02_H01_P01", null);                                    // model only: no text export
        disk.Candidate("L03_H01_P01", fixture, model: false);                   // text export only
        Directory.CreateDirectory(Path.Combine(disk.StudyRoot, "L04_H01_P01")); // folder without a model
        Directory.CreateDirectory(Path.Combine(disk.StudyRoot, "RawData"));
        Directory.CreateDirectory(Path.Combine(disk.StudyRoot, "Optimized"));
        disk.Manifest("run2", start.AddHours(2),
            ("L01_H01_P01", StudyIterationStatus.Reused, start.AddHours(2), start.AddHours(2).AddMinutes(1)),
            ("L01_H02_P01", StudyIterationStatus.Failed, start.AddHours(2), start.AddHours(2).AddMinutes(1)),
            ("L01_H10_P01", StudyIterationStatus.Skipped, null, null),
            ("L09_H09_P09", StudyIterationStatus.Ready, start.AddHours(2), start.AddHours(2).AddMinutes(1)));
        disk.Manifest("run1", start, history: true,
            ("L01_H01_P01", StudyIterationStatus.Ready, start, start.AddMinutes(1)),
            ("L01_H10_P01", StudyIterationStatus.Ready, start.AddMinutes(1), start.AddMinutes(2)));
        var found = catalog.Discover(disk.Baseline);
        Check(found.Select(candidate => candidate.Id).SequenceEqual(["L01_H01_P01", "L01_H02_P01", "L01_H10_P01", "L02_H01_P01", "L03_H01_P01"]),
            "the candidates of the study are listed by their Lxx_Hxx_Pxx identifier in natural order — only those with a model or its text export on disk");
        var first = found[0];
        Check(first.ModelPath == disk.ModelPath("L01_H01_P01") && first.TextExportPath == Path.ChangeExtension(first.ModelPath, ".$2k") &&
              first.RecordStatus == "Reused" && first.RecordWarning is null && first.RunWindows.Count == 2 && first.RunWindows.Any(run => run.StartedAt == start),
            "a candidate is resolved through the study records: its model path, its status and every run that worked on it");
        Check(found[1].RecordStatus == "Failed" && found[1].RecordWarning!.Contains("L01_H02_P01 as Failed") &&
              found[2].RecordStatus == "Ready" && found[2].RecordWarning is null && found[3].RecordStatus is null && found[3].RunWindows.Count == 0,
            "a candidate a run failed on is listed with a warning; one a later run merely left out keeps the record of the run that built it");
        Check(catalog.Discover(disk.Baseline).SequenceEqual(found) && found[0].Equals(catalog.Discover(disk.Baseline)[0]),
            "looking again finds equal candidates, so an open dropdown keeps its selection");

        // the baseline was renamed after the study: its children keep their names and are still found
        var renamed = Path.Combine(disk.Root, "Renamed.sdb");
        File.WriteAllText(renamed, "baseline");
        var again = catalog.Discover(renamed);
        Check(again.Select(candidate => candidate.Id).SequenceEqual(found.Select(candidate => candidate.Id)) && again[0].ModelPath == first.ModelPath,
            "a study whose baseline was renamed afterwards still resolves its child models");
    }

    // ---------- text export first, isolated SAP2000 read otherwise ----------

    private static void SourceSelection(ExportText fixture)
    {
        using var disk = new StudyOnDisk();
        var reader = new CountingReader(fixture);
        var service = new QuantityTakeoffService(reader);
        QuantityCandidate Candidate(string id, IReadOnlyList<QuantityRunWindow>? runs = null) =>
            new(id, disk.ModelPath(id), Path.ChangeExtension(disk.ModelPath(id), ".$2k"), null, null, runs ?? []);
        QuantityExportAttempt Try(QuantityCandidate candidate) => service.TryTextExportAsync(candidate, CancellationToken.None).GetAwaiter().GetResult();

        disk.Candidate("L01_H01_P01", fixture);
        var used = Try(Candidate("L01_H01_P01"));
        Check(used.Rejected is null && used.Result is { Source: QuantitySource.TextExport } result && Near(result.Takeoff.Total.SteelMassT, 2862.104378822229) &&
              result.SourceText.Contains("SAP2000 text export COT_L01_H01_P01.$2k") && result.SourceText.Contains("KN, m") && reader.Reads == 0,
            "a candidate's own text export is used when it holds everything — SAP2000 is not started");

        disk.Candidate("L01_H02_P01", null);
        Check(Try(Candidate("L01_H02_P01")) is { Result: null, Rejected: var none } && none!.Contains("no SAP2000 text export"), "no text export: said so");
        disk.Candidate("L01_H03_P01", fixture.Drop("FRAME SECTION ASSIGNMENTS").Drop("JOINT COORDINATES"));
        Check(Try(Candidate("L01_H03_P01")) is { Result: null, Rejected: var lacking } && lacking!.Contains("does not hold JOINT COORDINATES, FRAME SECTION ASSIGNMENTS"),
            "a text export without the tables a take-off needs is not used, and the missing tables are named");
        disk.Candidate("L01_H04_P01", fixture, exportNamedFor: "Other model.$2k");
        Check(Try(Candidate("L01_H04_P01")) is { Result: null, Rejected: var foreign } && foreign!.Contains("written for another model (Other model.$2k)"),
            "a text export written for another model is not taken for this candidate's");
        disk.Candidate("L01_H05_P01", fixture);
        File.WriteAllText(Path.ChangeExtension(disk.ModelPath("L01_H05_P01"), ".$2k"), fixture.Named("COT_L01_H05_P01.$2k").Text.Replace("END TABLE DATA", ""));
        Check(Try(Candidate("L01_H05_P01")) is { Result: null, Rejected: var cut } && cut!.Contains("incomplete"), "a text export that was cut short is not used");

        // freshness: the study saves the child (writing the export), then the analysis writes the model once more
        var saved = new DateTime(2026, 10, 6, 8, 59, 2, DateTimeKind.Utc);
        void Stamp(string id, TimeSpan modelAfterExport)
        {
            File.SetLastWriteTimeUtc(Path.ChangeExtension(disk.ModelPath(id), ".$2k"), saved);
            File.SetLastWriteTimeUtc(disk.ModelPath(id), saved + modelAfterExport);
        }
        var run = new QuantityRunWindow(new DateTimeOffset(saved.AddSeconds(-16), TimeSpan.Zero), new DateTimeOffset(saved.AddSeconds(36), TimeSpan.Zero));
        disk.Candidate("L01_H06_P01", fixture);
        Stamp("L01_H06_P01", TimeSpan.FromSeconds(28));
        Check(Try(Candidate("L01_H06_P01", [run])).Result is not null && Try(Candidate("L01_H06_P01")).Result is not null,
            "a model the analysis wrote 28 s after the export is current — with the run record, and without one inside the allowance");
        Stamp("L01_H06_P01", TimeSpan.FromHours(40));
        Check(Try(Candidate("L01_H06_P01", [run])) is { Result: null, Rejected: var later } && later!.Contains("40 h after its text export") && later.Contains("no study run accounts for that") &&
              Try(Candidate("L01_H06_P01")).Result is null,
            "a model written long after its export, outside any study run, has changed since: the export is not used");
        Stamp("L01_H06_P01", TimeSpan.FromMinutes(40));
        var longRun = new QuantityRunWindow(run.StartedAt, new DateTimeOffset(saved.AddMinutes(41), TimeSpan.Zero));
        Check(Try(Candidate("L01_H06_P01", [run, longRun])).Result is not null && Try(Candidate("L01_H06_P01", [run])).Result is null && Try(Candidate("L01_H06_P01")).Result is null,
            "a 40-minute analysis is accounted for by its run record, and only by it");
        Stamp("L01_H06_P01", TimeSpan.FromSeconds(-3));
        Check(Try(Candidate("L01_H06_P01")).Result is not null, "an export written after the model is current");

        var read = service.ReadWithSapAsync(Candidate("L01_H02_P01"), CancellationToken.None).GetAwaiter().GetResult();
        var kept = service.ReadWithSapAsync(Candidate("L01_H02_P01"), CancellationToken.None).GetAwaiter().GetResult();
        Check(read.Source == QuantitySource.IsolatedSapRead && Near(read.Takeoff.Total.ConcreteVolumeM3, 795) && ReferenceEquals(read, kept) && reader.Reads == 1 &&
              reader.Paths.Single() == disk.ModelPath("L01_H02_P01") && read.SourceText.Contains("separate SAP2000 instance"),
            "a candidate without a usable export is read through the isolated reader — once for as long as its model file is unchanged");
        service.ReadWithSapAsync(Candidate("L01_H02_P01"), CancellationToken.None, reload: true).GetAwaiter().GetResult();
        File.SetLastWriteTimeUtc(disk.ModelPath("L01_H02_P01"), saved.AddDays(1));
        service.ReadWithSapAsync(Candidate("L01_H02_P01"), CancellationToken.None).GetAwaiter().GetResult();
        Check(reader.Reads == 3, "Reload, or a changed model file, reads it again");
        Throws<InvalidOperationException>(() => new QuantityTakeoffService().ReadWithSapAsync(Candidate("L01_H02_P01"), CancellationToken.None).GetAwaiter().GetResult(),
            "without an isolated reader the model itself cannot be read — the service says so instead of reaching for the connected SAP2000");
        Throws<InvalidDataException>(() => new QuantityTakeoffService(new CountingReader(fixture.Drop("JOINT COORDINATES")))
                .ReadWithSapAsync(Candidate("L01_H02_P01"), CancellationToken.None).GetAwaiter().GetResult(),
            "tables SAP2000 did not return are an error, not an empty take-off");
    }

    // ---------- the isolated read-only SAP2000 workflow, against API doubles ----------

    private static void IsolatedRead(ExportText fixture)
    {
        using var disk = new StudyOnDisk();
        disk.Candidate("L01_H01_P01", fixture);
        var model = disk.ModelPath("L01_H01_P01");
        File.SetAttributes(model, FileAttributes.ReadOnly);
        var folder = Path.GetDirectoryName(model)!;
        var before = FolderState(folder);
        var work = Path.Combine(disk.Root, "work");
        var api = new TablesApi(fixture);
        var hosts = new TablesHostFactory(api);
        var reader = new IsolatedSapTableReader(hosts, work);
        var (abandoned, inUse) = (Path.Combine(work, "abandoned"), Path.Combine(work, "in-use"));
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(abandoned).FullName, "old.sdb"), "left behind");
        Directory.SetCreationTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));
        Directory.CreateDirectory(inUse);

        var tables = reader.ReadAsync(model, CancellationToken.None).GetAwaiter().GetResult();
        var takeoff = QuantityTakeoffCalculator.Calculate(tables);
        var fromText = Calculate(fixture);
        Check(QuantityTakeoffCalculator.MissingInputs(tables).Count == 0 && takeoff.Complete && takeoff.Sections.Count == fromText.Sections.Count &&
              takeoff.Sections.Zip(fromText.Sections).All(pair => pair.First.Section == pair.Second.Section && pair.First.MemberCount == pair.Second.MemberCount &&
                  Near(pair.First.TotalLengthM, pair.Second.TotalLengthM) && Near(pair.First.ConcreteVolumeM3, pair.Second.ConcreteVolumeM3) && Near(pair.First.SteelMassT, pair.Second.SteelMassT)) &&
              tables.Units == new SapTableUnits("kN", "m"),
            "the database tables read through the SAP2000 API give the same take-off as the text export — one calculation for both sources");
        Check(hosts.Opened.Count == 1 && !string.Equals(hosts.Opened[0], model, StringComparison.OrdinalIgnoreCase) &&
              hosts.Opened[0].StartsWith(work, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(hosts.Opened[0]) == Path.GetFileName(model) && hosts.CopyExistedWhenOpened,
            "the SAP2000 instance of the isolated read opens a private copy of the model, never the candidate's own file");
        Check(FolderState(folder) == before && File.GetAttributes(model).HasFlag(FileAttributes.ReadOnly),
            "the candidate folder is exactly as it was: no file added, removed, rewritten or re-stamped");
        Check(hosts.Created == 1 && hosts.Stopped == 1 && Directory.GetFileSystemEntries(work).SequenceEqual([inUse]),
            "the instance is closed and its private copy removed when the read is over — with a copy an earlier read left behind days ago, not a recent one");
        Directory.Delete(inUse);
        Check(api.Calls.Distinct().Order().SequenceEqual(new[] { "DatabaseTables.GetAvailableTables", "DatabaseTables.GetTableForDisplayArray", "File.OpenFile",
                  "GetModelFilename", "GetModelIsLocked", "GetPresentUnits", "GetVersion", "PropCable.GetProp", "PropFrame.GetSectProps", "PropMaterial.GetWeightAndMass",
                  "get_DatabaseTables", "get_File", "get_PropCable", "get_PropFrame", "get_PropMaterial" }.Order()) &&
              api.Tables.All(table => IsolatedSapTableReader.TablePrefixes.Any(prefix => table.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) &&
              api.Tables.Count == 17 && api.Groups.All(group => group == "All"),
            "the read asks SAP2000 only for the model's identity and the tables it needs: no unit change, no unlock, no edit, no save, no analysis");

        api.FailTable = "Joint Coordinates";
        Throws<InvalidOperationException>(() => reader.ReadAsync(model, CancellationToken.None).GetAwaiter().GetResult(), "a table SAP2000 refuses stops the read");
        Check(hosts.Created == 2 && hosts.Stopped == 2 && Directory.GetFileSystemEntries(work).Length == 0 && FolderState(folder) == before,
            "…and the instance is still closed, the copy removed and the candidate untouched");
        api.FailTable = null;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => reader.ReadAsync(model, cancelled.Token).GetAwaiter().GetResult(), "a cancelled read starts no SAP2000");
        Check(hosts.Created == 2 && FolderState(folder) == before, "…nothing was started for it");
        // SAP2000 rounds what it displays; the density and the areas a quantity is multiplied by are taken at full precision
        api.RoundForDisplay = true;
        var exact = QuantityTakeoffCalculator.Calculate(reader.ReadAsync(model, CancellationToken.None).GetAwaiter().GetResult());
        api.GettersAnswer = false;
        var rounded = QuantityTakeoffCalculator.Calculate(reader.ReadAsync(model, CancellationToken.None).GetAwaiter().GetResult());
        api.GettersAnswer = true;
        api.RoundForDisplay = false;
        Check(exact.Complete && Near(exact.Total.SteelMassT, 2862.104378822229) && Near(Row(exact, Strand).SteelMassT, 110.85629119867916) && Near(exact.Total.ConcreteVolumeM3, 795),
            "numbers SAP2000 rounds for display — a density shown as 7.849, a cable area as 0.028802 — are read at full precision, so the live take-off equals the saved one");
        Check(rounded.Complete && !Near(rounded.Total.SteelMassT, 2862.104378822229, 1e-8) && Near(rounded.Total.SteelMassT, 2862.104378822229, 1e-4),
            "…and where SAP2000 does not answer for the full number, the displayed one is used rather than nothing");
        api.RefuseGroup = "All";
        api.Groups.Clear();
        Check(QuantityTakeoffCalculator.Calculate(reader.ReadAsync(model, CancellationToken.None).GetAwaiter().GetResult()).Complete &&
              api.Groups.Count == 34 && api.Groups.Count(group => group == "") == 17,
            "a SAP2000 that does not take the group 'All' by name is asked with the blank group name, which stands for the same");
        api.RefuseGroup = null;
        File.SetAttributes(model, FileAttributes.Normal);
    }

    // ---------- Tab 08 view model: the live connected model, gating, switching, stale requests ----------

    private static void ViewModel(ExportText fixture)
    {
        using var disk = new StudyOnDisk();
        disk.Candidate("L01_H01_P01", fixture);
        disk.Candidate("L01_H02_P01", Reassigned(Taller(fixture)));
        disk.Candidate("L01_H03_P01", fixture.Edit("JOINT COORDINATES", line => Field(line, "Joint") == "MTrPt004" ? null : line));
        disk.Candidate("L01_H04_P01", null);                                    // no text export: needs the isolated read

        var state = StudyComposerChecks.CreateState();
        state.SessionBaselineModel = state.Model! with { Path = disk.Baseline };
        state.Connection = "Disconnected";
        var sap = new LiveSap { Open = Taller(fixture) };                       // what is open in SAP2000 right now: no saved candidate is like it
        var workflow = new WorkflowService(sap, state);
        var reader = new GatedReader();
        var vm = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap, state, workflow), quantityTakeoff: new QuantityTakeoffService(reader));
        void Pick(string id) => vm.SelectedQuantityCandidate = vm.QuantityCandidates.Single(candidate => candidate.Id == id);
        const string Live = QuantityCandidate.ConnectedModelId;

        vm.SelectedTab = 8;
        Check(!vm.HasSapConnection && vm.QuantityCandidates.Count == 0 && vm.QuantityTabStatus == "Connect SAP" && vm.QuantityStatus.StartsWith("Connect to SAP2000") &&
              !vm.ReloadQuantities.CanExecute(null) && !vm.CancelQuantityRead.CanExecute(null) && vm.QuantityRows.Count == 0,
            "before a SAP connection Tab 08 lists no model and its commands are unavailable");
        vm.SelectedQuantityCandidate = new QuantityCandidateCatalog().Discover(disk.Baseline)[0];
        vm.ReloadQuantities.Execute(null);
        vm.SelectedQuantityCandidate = QuantityCandidate.Connected(state.Model!);
        Check(vm.QuantityState == QuantityViewState.NoCandidate && !vm.QuantityLoading && vm.QuantityRows.Count == 0 && reader.Started == 0 && sap.Reads == 0,
            "a selection made without a connection reads nothing — neither a candidate nor SAP2000");
        vm.SelectedQuantityCandidate = null;

        // the running model, live
        state.Connection = "Connected";
        Wait(() => vm.QuantityState == QuantityViewState.Ready && !vm.IsBusy, "with a SAP connection the tab opens on the connected model and reads it from SAP2000");
        Check(vm.QuantityCandidates.Select(candidate => candidate.Id).SequenceEqual([Live, "L01_H01_P01", "L01_H02_P01", "L01_H03_P01", "L01_H04_P01"]) &&
              vm.QuantityCandidates[0].IsConnectedModel && vm.QuantityCandidates.Skip(1).All(candidate => !candidate.IsConnectedModel) &&
              vm.SelectedQuantityCandidate is { IsConnectedModel: true } && vm.ReloadQuantities.CanExecute(null),
            "the connected model is listed first, then the candidate models of the connected baseline's study");
        Check(vm.QuantityRows.Select(row => row.Section).SequenceEqual([Cft, MegaA, MegaB, Shs, Strand, "Total"]) && vm.QuantityRows[^1] is { IsTotal: true, MemberCount: 54 } &&
              Near(vm.QuantityRows[^1].TotalLengthM, 1186.4982387880245) && Near(vm.QuantityRows[^1].ConcreteVolumeM3, 837.9) && Near(vm.QuantityRows[^1].SteelMassT, 3070.9898736128254) &&
              vm.QuantitySource.StartsWith($"{Live} · Mcotis.sdb · read live from the connected SAP2000 at ") && vm.QuantitySource.EndsWith(" · kN, m") &&
              vm.QuantityTabStatus == "Live model" && vm.QuantityStatus == "5 sections · 54 members" && vm.QuantityNotes.Count == 0 &&
              vm.Message.StartsWith("Quantity take-off of the connected model read live from SAP2000") &&
              sap.Reads == 1 && sap.Expected.Single() == state.Model && sap.Prefixes!.SequenceEqual(IsolatedSapTableReader.TablePrefixes) && reader.Started == 0,
            "the take-off of the running model comes from SAP2000 itself — its own tables, tied to the connected model, no file and no second SAP2000");

        sap.Open = Reassigned(Taller(fixture));                                 // the model is changed in SAP2000 …
        vm.ReloadQuantities.Execute(null);                                      // … and Refresh is pressed
        Wait(() => vm.QuantityState == QuantityViewState.Ready && sap.Reads == 2 && vm.QuantityRows.Any(row => row.Section == "FSEC1") && !vm.IsBusy,
            "Refresh reads the connected model from SAP2000 again");
        Check(vm.QuantityRows.Select(row => row.Section).SequenceEqual([Cft, "FSEC1", MegaA, MegaB, Shs, Strand, "Total"]) &&
              vm.QuantityRows.Single(row => row.Section == Shs).MemberCount == 28 && Near(vm.QuantityRows[^1].SteelMassT, 2970.10302762366),
            "a section changed in the running model shows after Refresh — the tab follows SAP2000, not a file saved earlier");
        sap.Open = fixture;
        var list = vm.QuantityCandidates;
        vm.SelectedTab = 0; vm.SelectedTab = 8;
        Wait(() => vm.QuantityState == QuantityViewState.Ready && sap.Reads == 3 && vm.QuantityRows.Count == 6 && !vm.IsBusy, "returning to the tab reads the connected model again");
        Check(ReferenceEquals(list, vm.QuantityCandidates) && Near(vm.QuantityRows[^1].SteelMassT, 2862.104378822229) && vm.SelectedQuantityCandidate is { IsConnectedModel: true },
            "…with the list and the selection as they were");

        // a saved candidate: from its own file, without SAP2000
        var shown = new List<(QuantityViewState State, int Rows, string Status, string Tab)>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.QuantityState)) lock (shown) shown.Add((vm.QuantityState, vm.QuantityRows.Count, vm.QuantityStatus, vm.QuantityTabStatus));
        };
        Pick("L01_H01_P01");
        Wait(() => vm.QuantityState == QuantityViewState.Ready, "the take-off of L01_H01_P01 loads in the background");
        lock (shown)
            Check(shown.Count >= 2 && shown[0] == (QuantityViewState.Loading, 0, "Reading L01_H01_P01…", "Reading…") && shown[^1].State == QuantityViewState.Ready && shown[^1].Rows == 6,
                "choosing a candidate shows the loading state first, with no rows, and then its take-off");
        Check(vm.QuantityRows.Select(row => row.Section).SequenceEqual([Cft, MegaA, MegaB, Shs, Strand, "Total"]) && vm.QuantityRows[^1].IsTotal &&
              vm.QuantityRows.Take(5).All(row => !row.IsTotal) && vm.QuantityRows[^1].MemberCount == 54 && Near(vm.QuantityRows[^1].TotalLengthM, 1090.3610384951724) &&
              Near(vm.QuantityRows[^1].ConcreteVolumeM3, 795) && Near(vm.QuantityRows[^1].SteelMassT, 2862.104378822229) && vm.QuantityRows[^1].Problem is null &&
              vm.QuantityRows[0].Detail.Contains("Section Designer section") && vm.QuantityNotes.Count == 0 && !vm.HasQuantityProblem &&
              vm.QuantityRows.Take(5).All(row => row.Figure is not null) && vm.QuantityRows[0].Figure!.Shapes.Count == 2 && vm.QuantityRows[^1].Figure is null &&
              vm.QuantityRows.Select(row => row.FigureSize).SequenceEqual(["1200 × 2000 mm", "1500 × 3000 mm", "3000 × 2500 mm", "1200 × 1200 mm", "191.5 × 191.5 mm", ""]) &&
              vm.QuantitySource.StartsWith("L01_H01_P01 · SAP2000 text export COT_L01_H01_P01.$2k") && vm.QuantityTabStatus == "L01_H01_P01" &&
              vm.QuantityStatus == "5 sections · 54 members" && !vm.IsBusy && reader.Started == 0 && sap.Reads == 3,
            "a candidate's section breakdown is shown with a total row and its data source — its own saved export, SAP2000 not involved");

        Pick("L01_H02_P01");
        Wait(() => vm.QuantityState == QuantityViewState.Ready, "switching to L01_H02_P01 loads its take-off");
        Check(vm.QuantityRows.Select(row => row.Section).SequenceEqual([Cft, "FSEC1", MegaA, MegaB, Shs, Strand, "Total"]) &&
              Near(vm.QuantityRows[^1].TotalLengthM, 1186.4982387880245) && Near(vm.QuantityRows[^1].SteelMassT, 2970.10302762366) && vm.QuantityTabStatus == "L01_H02_P01",
            "switching the candidate shows that candidate's own sections and lengths — this is the comparison between models");

        Pick("L01_H03_P01");
        Wait(() => vm.QuantityState == QuantityViewState.Ready, "a candidate with a gap in its data still loads");
        var total = vm.QuantityRows[^1];
        Check(vm.QuantityRows.Single(row => row.Section == Shs) is { TotalLengthM: null, SteelMassT: null, ConcreteVolumeM3: 0 } gap && gap.Problem!.Contains("MTrPt004") &&
              total is { TotalLengthM: null, SteelMassT: null } && Near(total.ConcreteVolumeM3, 795) && total.Problem!.StartsWith("Not a complete total") &&
              total.Problem.Contains(Shs) && total.Problem.Contains("1,641.421 t steel") && vm.QuantityNotes.Count == 4 && vm.QuantityNotes.All(note => note.StartsWith(Shs + ":")) &&
              vm.QuantityTabStatus == "L01_H03_P01 · incomplete" && vm.QuantityStatus.Contains("some quantities are unavailable"),
            "a quantity that cannot be established is unavailable in its row and in the total — never zero, never a total of what is left");

        // stale requests: a slow isolated read must not overwrite a newer selection
        Pick("L01_H04_P01");
        Wait(() => vm.QuantitySapReading && reader.Started == 1, "a candidate without a text export starts the isolated read");
        Check(vm.IsBusy && vm.QuantityLoading && vm.QuantityRows.Count == 0 && vm.QuantityStatus == "Reading L01_H04_P01 in a separate SAP2000 instance…" &&
              vm.CancelQuantityRead.CanExecute(null) && !vm.ReloadQuantities.CanExecute(null) && vm.Message.Contains("The connected model is not touched") &&
              reader.Paths.Single() == disk.ModelPath("L01_H04_P01"),
            "while a separate SAP2000 instance reads the model the application is busy, and the read can be cancelled");
        Pick("L01_H01_P01");
        Wait(() => vm.QuantityState == QuantityViewState.Ready && !vm.IsBusy, "choosing another candidate meanwhile shows that candidate");
        Check(vm.QuantityRows.Count == 6 && vm.QuantityTabStatus == "L01_H01_P01" && reader.Cancelled == 1 && !vm.QuantitySapReading && vm.SelectedQuantityCandidate!.Id == "L01_H01_P01",
            "the earlier request was cancelled and left nothing on the tab");

        reader.IgnoreCancellation = true;
        Pick("L01_H04_P01");
        Wait(() => vm.QuantitySapReading && reader.Started == 2, "the isolated read starts again");
        Pick("L01_H02_P01");
        Wait(() => vm.QuantityState == QuantityViewState.Ready, "the newer selection is shown while the old read is still running");
        Check(vm.IsBusy && vm.QuantityTabStatus == "L01_H02_P01" && vm.QuantityRows.Count == 7, "…the old read still holds its SAP2000 instance");
        reader.Complete(fixture);                                               // the old read finishes late, with its own candidate's data
        Wait(() => !vm.IsBusy, "the old read ends");
        Thread.Sleep(50);
        Check(vm.QuantityState == QuantityViewState.Ready && vm.QuantityTabStatus == "L01_H02_P01" && vm.QuantityRows.Count == 7 &&
              vm.QuantitySource.StartsWith("L01_H02_P01 ·") && Near(vm.QuantityRows[^1].SteelMassT, 2970.10302762366),
            "a read that finishes after a newer selection does not replace it");

        // the live read never runs in the middle of another SAP2000 operation: it waits for it
        Pick("L01_H04_P01");
        Wait(() => vm.QuantitySapReading && reader.Started == 3, "another operation holds the application");
        Pick(Live);
        Wait(() => vm.QuantityStatus.StartsWith("Waiting for the running operation"), "the connected model is not read while another operation is running");
        Thread.Sleep(400);
        Check(sap.Reads == 3 && vm.IsBusy && vm.QuantityLoading && vm.QuantityRows.Count == 0, "…SAP2000 is left alone until that operation has finished");
        reader.Complete(fixture);
        Wait(() => vm.QuantityState == QuantityViewState.Ready && sap.Reads == 4 && !vm.IsBusy, "…and is read as soon as it has");
        Check(vm.QuantityTabStatus == "Live model" && vm.QuantityRows.Count == 6 && vm.QuantitySource.StartsWith(Live + " ·"), "the tab then shows the connected model");

        // the isolated read itself: result, cancel, failure
        reader.IgnoreCancellation = false;
        Pick("L01_H04_P01");
        Wait(() => vm.QuantitySapReading && reader.Started == 4, "the isolated read starts");
        reader.Complete(fixture);
        Wait(() => vm.QuantityState == QuantityViewState.Ready && !vm.IsBusy, "the isolated read delivers");
        Check(vm.QuantityRows.Count == 6 && vm.QuantitySource.Contains("separate SAP2000 instance") &&
              vm.QuantityNotes.Single().StartsWith("The text export was not used: there is no SAP2000 text export"),
            "a take-off read through SAP2000 says so, and why the text export was not used");
        vm.ReloadQuantities.Execute(null);
        Wait(() => vm.QuantitySapReading && reader.Started == 5, "Reload reads the model again");
        vm.CancelQuantityRead.Execute(null);
        Wait(() => vm.QuantityState == QuantityViewState.Unavailable && !vm.IsBusy, "Cancel stops the read");
        Check(vm.QuantityProblem.Contains("was cancelled") && vm.HasQuantityProblem && !vm.QuantityProblemIsError && vm.QuantityRows.Count == 0 && vm.QuantityTabStatus == "▲ Unavailable",
            "a cancelled read leaves the tab without numbers and says what happened");
        vm.ReloadQuantities.Execute(null);
        Wait(() => vm.QuantitySapReading && reader.Started == 6, "Reload after a cancel reads again");
        reader.Fail(new InvalidOperationException("SAP2000 worker 90 returned code 1 during ApplicationStart."));
        Wait(() => vm.QuantityState == QuantityViewState.Error && !vm.IsBusy, "a failed read ends");
        Check(vm.QuantityProblem == "The quantities of L01_H04_P01 could not be read: SAP2000 worker 90 returned code 1 during ApplicationStart." &&
              vm.QuantityProblemIsError && vm.QuantityRows.Count == 0 && vm.QuantityTabStatus == "× Read failed" && !vm.QuantitySapReading,
            "a failed read is an error state with SAP2000's message, not an empty table");

        // the live read fails: said so, nothing shown; a lost SAP2000 is a lost connection
        sap.Failure = new InvalidOperationException("The active SAP model has changed. Reconnect and review your configuration.");
        Pick(Live);
        Wait(() => vm.QuantityState == QuantityViewState.Error && !vm.IsBusy, "a live read SAP2000 refuses ends");
        Check(vm.QuantityProblem == "The quantities of the connected model could not be read: The active SAP model has changed. Reconnect and review your configuration." &&
              vm.QuantityProblemIsError && vm.QuantityRows.Count == 0 && sap.Reads == 5 && state.IsSapConnected,
            "when the model open in SAP2000 is no longer the connected one, the live read says so instead of taking off another model");
        sap.Failure = new System.Runtime.InteropServices.COMException("The RPC server is unavailable.");
        vm.ReloadQuantities.Execute(null);
        Wait(() => state.Connection == "Connection Lost" && !vm.IsBusy && vm.QuantityCandidates.Count == 0, "a SAP2000 that has gone away is a lost connection");
        Check(vm.SelectedQuantityCandidate is null && vm.QuantityRows.Count == 0 && vm.QuantityTabStatus == "Connect SAP" && !vm.ReloadQuantities.CanExecute(null),
            "…and the tab is empty and disabled until SAP2000 is connected again");
        sap.Failure = null;
        state.Connection = "Connected";
        Wait(() => vm.QuantityState == QuantityViewState.Ready && !vm.IsBusy && vm.SelectedQuantityCandidate is { IsConnectedModel: true } && vm.QuantityRows.Count == 6,
            "reconnecting reads the running model again");

        // connection loss empties the tab and stops a read in flight
        Pick("L01_H04_P01");
        Wait(() => vm.QuantityState == QuantityViewState.Ready && !vm.IsBusy && reader.Started == 6,
            "a candidate that was read through SAP2000 is not read again while its model file is unchanged");
        vm.ReloadQuantities.Execute(null);
        Wait(() => vm.QuantitySapReading && reader.Started == 7, "a read is in flight");
        state.Connection = "Connection Lost";
        Wait(() => !vm.IsBusy, "losing the connection stops the read");
        Check(vm.QuantityCandidates.Count == 0 && vm.SelectedQuantityCandidate is null && vm.QuantityRows.Count == 0 && vm.QuantityState == QuantityViewState.NoCandidate &&
              vm.QuantityTabStatus == "Connect SAP" && !vm.ReloadQuantities.CanExecute(null) && reader.Cancelled == 3 && !vm.HasQuantityProblem,
            "losing the SAP connection empties Tab 08 and disables it again");
        state.Connection = "Connected";
        Wait(() => vm.QuantityState == QuantityViewState.Ready && !vm.IsBusy, "reconnecting lists the models again");
        Check(vm.QuantityCandidates.Count == 5 && vm.SelectedQuantityCandidate is { IsConnectedModel: true }, "…and opens on the connected model");

        // without an isolated reader a candidate that needs one is unavailable, not empty
        var plain = new MainViewModel(state, workflow, new NoSelection(), new ConfigService(sap, state, workflow)) { SelectedTab = 8 };
        Wait(() => plain.QuantityState == QuantityViewState.Ready && !plain.IsBusy, "a second view of the same session reads the connected model too");
        plain.SelectedQuantityCandidate = plain.QuantityCandidates.Single(candidate => candidate.Id == "L01_H04_P01");
        Wait(() => plain.QuantityState == QuantityViewState.Unavailable && !plain.IsBusy, "a candidate that cannot be read ends as unavailable");
        Check(plain.QuantityProblem.StartsWith("Quantities of L01_H04_P01 are unavailable: there is no SAP2000 text export") && plain.QuantityRows.Count == 0 &&
              !plain.QuantityProblemIsError,
            "unavailable data is stated with its reason; the table stays empty rather than showing zeros");

        // another study: its own candidates; the take-off of a candidate that is no longer listed is dropped
        using var other = new StudyOnDisk();
        other.Candidate("L05_H05_P05", fixture);
        Pick("L01_H02_P01");
        Wait(() => vm.QuantityState == QuantityViewState.Ready && vm.QuantityRows.Count == 7, "a candidate is shown");
        state.SessionBaselineModel = state.Model! with { Path = other.Baseline };
        Wait(() => vm.QuantityState == QuantityViewState.Ready && !vm.IsBusy && vm.SelectedQuantityCandidate is { IsConnectedModel: true }, "a different baseline starts the tab again");
        Check(vm.QuantityCandidates.Select(candidate => candidate.Id).SequenceEqual([Live, "L05_H05_P05"]) && vm.QuantityRows.Count == 6 && vm.QuantitySource.StartsWith(Live + " ·"),
            "a different baseline lists its own study's candidates; the previous candidate's take-off is gone and the connected model is shown");
    }

    private static void FemFailedRow()
    {
        var failed = FemCableFitRow.FailedCase("L01_H02_P03", "no equilibrium: residual 12.5 kN with 3 active cables");
        Check(failed is { RunId: "L01_H02_P03", CableName: "", Status: "Failed", Failed: true, FitFailure: "no equilibrium: residual 12.5 kN with 3 active cables" } &&
              failed.TargetForceKN is null && failed.ResultForceKN is null,
            "a failed FEM candidate row keeps its candidate ID, Failed status and reason, with a blank Cable cell — no '(all cables)' placeholder");
    }

    // ---------- helpers ----------

    private static bool Near(double? actual, double? expected, double relative = 1e-10) =>
        actual is { } a && expected is { } e && Math.Abs(a - e) <= relative * Math.Max(1, Math.Abs(e));

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Console.WriteLine("PASS: " + name); return; }
        throw new Exception("FAIL: " + name);
    }

    private static void Wait(Func<bool> done, string name)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < until) Thread.Sleep(5);
        Check(done(), name);
    }

    private static string? Field(string line, string name) =>
        Regex.Match(line, $"""(?<![A-Za-z0-9_]){Regex.Escape(name)}=("[^"]*"|\S+)""") is { Success: true } match ? match.Groups[1].Value.Trim('"') : null;

    private static string Set(string line, string name, string value)
    {
        var pattern = $"""(?<![A-Za-z0-9_]){Regex.Escape(name)}=("[^"]*"|\S+)""";
        var written = value.Contains(' ') && !value.StartsWith('"') ? $"\"{value}\"" : value;
        return Regex.IsMatch(line, pattern) ? Regex.Replace(line, pattern, _ => $"{name}={written}") : $"{line}   {name}={written}";
    }

    private static string Drop(string line, string name) => Regex.Replace(line, $"""\s*(?<![A-Za-z0-9_]){Regex.Escape(name)}=("[^"]*"|\S+)""", "");

    /// <summary>Everything about a folder that a write would change: names, sizes, time stamps and contents.</summary>
    private static string FolderState(string folder) => string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => $"{Path.GetRelativePath(folder, path)}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}"));

    /// <summary>A SAP2000 text export as a list of records, so a candidate can be derived from another the way SAP2000
    /// would have written it.</summary>
    private sealed class ExportText
    {
        private readonly List<(string Table, List<string> Records)> tables = [];
        private string header = "";

        public static ExportText Parse(string text)
        {
            var export = new ExportText();
            List<string>? records = null;
            var pending = "";
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("File ", StringComparison.Ordinal) && export.tables.Count == 0) { export.header = line; continue; }
                if (line.StartsWith("TABLE:", StringComparison.Ordinal)) { export.tables.Add((line[6..].Trim().Trim('"'), records = [])); pending = ""; continue; }
                if (records is null || line.Trim().Length == 0 || line.Trim() == "END TABLE DATA") continue;
                if (line.EndsWith(" _", StringComparison.Ordinal)) { pending += line[..^2] + " "; continue; }
                records.Add(pending + line);
                pending = "";
            }
            return export;
        }

        private ExportText Copy(Action<ExportText> change)
        {
            var copy = new ExportText { header = header };
            copy.tables.AddRange(tables.Select(table => (table.Table, table.Records.ToList())));
            change(copy);
            return copy;
        }

        /// <summary>Rewrites the records of one table; a null drops the record.</summary>
        public ExportText Edit(string table, Func<string, string?> change) => Copy(copy =>
        {
            var index = copy.tables.FindIndex(item => item.Table == table);
            if (index < 0) throw new InvalidOperationException($"The fixture has no table '{table}'.");
            copy.tables[index] = (table, copy.tables[index].Records.Select(change).OfType<string>().ToList());
        });
        public ExportText Add(string table, params string[] records) => Copy(copy =>
        {
            var index = copy.tables.FindIndex(item => item.Table == table);
            if (index < 0) copy.tables.Add((table, records.ToList())); else copy.tables[index].Records.AddRange(records);
        });
        public ExportText Drop(string table) => Copy(copy => copy.tables.RemoveAll(item => item.Table == table));
        /// <summary>The export as SAP2000 would have written it for a model of this file name.</summary>
        public ExportText Named(string fileName) => Copy(copy => copy.header = $@"File C:\Models\Study\{fileName} was saved on m/d/yy at h:mm:ss");

        public string Text => string.Join("\r\n", new[] { header, " " }
            .Concat(tables.SelectMany(table => new[] { $"TABLE:  \"{table.Table}\"" }.Concat(table.Records).Append(" ")))
            .Append("END TABLE DATA").Append(""));

        /// <summary>The tables as the database-tables API hands them over: field keys once, then the values row by row.</summary>
        public IEnumerable<(string Table, string[] Fields, string[] Data, int Records)> ApiTables()
        {
            foreach (var (table, records) in tables)
            {
                var rows = records.Select(record => Regex.Matches(record, """([A-Za-z0-9_]+)=("[^"]*"|\S+)""")
                    .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value.Trim('"'))).ToArray();
                var fields = rows.SelectMany(row => row.Keys).Distinct().ToArray();
                yield return (table, fields, rows.SelectMany(row => fields.Select(field => row.GetValueOrDefault(field, ""))).ToArray(), rows.Length);
            }
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mcotis_qty_" + Guid.NewGuid().ToString("N"))).FullName;
        public string Write(string name, string text)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, text);
            return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>A baseline with a Study folder beside it, laid out the way the study writes it.</summary>
    private sealed class StudyOnDisk : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() }
        };
        private readonly TempFolder folder = new();
        public string Root => folder.Root;
        public string Baseline { get; }
        public string StudyRoot => Path.Combine(Root, "Study");
        public StudyOnDisk() => Baseline = folder.Write("COT.sdb", "baseline");
        public string ModelPath(string id) => Path.Combine(StudyRoot, id, $"COT_{id}.sdb");

        public void Candidate(string id, ExportText? export, bool model = true, string? exportNamedFor = null)
        {
            Directory.CreateDirectory(Path.Combine(StudyRoot, id));
            var written = DateTime.UtcNow.AddMinutes(-5);
            if (export is not null)
            {
                var path = Path.ChangeExtension(ModelPath(id), ".$2k");
                File.WriteAllText(path, export.Named(exportNamedFor ?? Path.GetFileName(path)).Text);
                File.SetLastWriteTimeUtc(path, written);
            }
            if (!model) return;
            File.WriteAllText(ModelPath(id), "binary model of " + id);
            File.SetLastWriteTimeUtc(ModelPath(id), written.AddSeconds(20));    // as after an analysis: a little later than the export
        }

        public void Manifest(string runId, DateTimeOffset started, params (string Id, StudyIterationStatus Status, DateTimeOffset? Start, DateTimeOffset? End)[] candidates) =>
            Manifest(runId, started, false, candidates);

        public void Manifest(string runId, DateTimeOffset started, bool history,
            params (string Id, StudyIterationStatus Status, DateTimeOffset? Start, DateTimeOffset? End)[] candidates)
        {
            var manifest = new StudyRunManifest
            {
                RunId = runId, StartedAt = started, CompletedAt = started.AddMinutes(10), State = StudyRunState.Completed, BaselineModelPath = Baseline,
                Candidates = candidates.Select((candidate, index) => new StudyRunCandidateManifest
                {
                    RunIndex = index + 1, IterationId = candidate.Id, ModelPath = ModelPath(candidate.Id), Selected = candidate.Status != StudyIterationStatus.Skipped,
                    Status = candidate.Status, Stage = candidate.Status == StudyIterationStatus.Failed ? StudyWorkerStage.RunningAnalysis : StudyWorkerStage.Completed,
                    StartedAt = candidate.Start, CompletedAt = candidate.End
                }).ToList()
            };
            var path = history
                ? Path.Combine(Directory.CreateDirectory(Path.Combine(StudyRoot, StudyRunJournal.HistoryDirectoryName)).FullName, $"{started:yyyyMMddTHHmmssfffZ}_{runId}.json")
                : Path.Combine(Directory.CreateDirectory(StudyRoot).FullName, StudyRunJournal.FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, Json));
        }

        public void Dispose() => folder.Dispose();
    }

    /// <summary>Stands in for the isolated SAP2000 reader: hands back fixed tables and counts the reads.</summary>
    private sealed class CountingReader(ExportText tables) : ISapModelTableReader
    {
        public int Reads;
        public List<string> Paths { get; } = [];
        public Task<SapTableSet> ReadAsync(string modelPath, CancellationToken token)
        {
            Reads++;
            Paths.Add(modelPath);
            using var folder = new TempFolder();
            return Task.FromResult(SapTableSet.ReadText(folder.Write("read.$2k", tables.Text)));
        }
    }

    /// <summary>An isolated reader whose read finishes only when the check says so.</summary>
    private sealed class GatedReader : ISapModelTableReader
    {
        private TaskCompletionSource<SapTableSet> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int started, cancelled;
        public int Started => Volatile.Read(ref started);
        public int Cancelled => Volatile.Read(ref cancelled);
        public bool IgnoreCancellation;
        public List<string> Paths { get; } = [];

        public async Task<SapTableSet> ReadAsync(string modelPath, CancellationToken token)
        {
            var current = gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Paths) Paths.Add(modelPath);
            Interlocked.Increment(ref started);
            if (IgnoreCancellation) return await current.Task;
            await using var registration = token.Register(() => { Interlocked.Increment(ref cancelled); current.TrySetCanceled(token); });
            return await current.Task;
        }

        public void Complete(ExportText tables)
        {
            using var folder = new TempFolder();
            gate.TrySetResult(SapTableSet.ReadText(folder.Write("read.$2k", tables.Text)));
        }
        public void Fail(Exception error) => gate.TrySetException(error);
    }

    /// <summary>SAP2000's API as the isolated read sees it: a model that can be opened and asked for its tables.
    /// Every other call throws, so a write, an unlock or a change of units would fail the check.</summary>
    private sealed class TablesApi
    {
        private readonly Dictionary<string, (string[] Fields, string[] Data, int Records)> tables;
        public List<string> Calls { get; } = [];
        public List<string> Tables { get; } = [];
        public List<string> Groups { get; } = [];
        public string CurrentPath = "";
        public string? FailTable, RefuseGroup;
        /// <summary>SAP2000 shows a density or an area with about five figures; its getters give the full number.</summary>
        public bool RoundForDisplay, GettersAnswer = true;
        public cSapModel Model { get; }

        public TablesApi(ExportText export)
        {
            // the API names tables in title case; the text export in capitals
            tables = export.ApiTables().ToDictionary(table => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(table.Table.ToLowerInvariant()),
                table => (table.Fields, table.Data, table.Records), StringComparer.Ordinal);
            var database = ApiDouble.Create<cDatabaseTables>((name, a) =>
            {
                Calls.Add("DatabaseTables." + name);
                switch (name)
                {
                    case "GetAvailableTables":
                        a[0] = tables.Count; a[1] = tables.Keys.ToArray(); a[2] = tables.Keys.ToArray(); a[3] = new int[tables.Count];
                        return 0;
                    case "GetTableForDisplayArray":
                        var key = (string)a[0]!;
                        Tables.Add(key);
                        Groups.Add((string)a[2]!);
                        if (key == FailTable || (string)a[2]! == RefuseGroup) return 1;
                        var table = tables[key];
                        a[3] = 1; a[4] = table.Fields; a[5] = table.Records; a[6] = RoundForDisplay ? Displayed(table.Fields, table.Data) : table.Data;
                        return 0;
                    default: throw new NotSupportedException(name);
                }
            });
            string Stored(string table, string nameField, object? name, string field)
            {
                var (fields, data, records) = tables[table];
                int key = Array.IndexOf(fields, nameField), value = Array.IndexOf(fields, field);
                var record = Enumerable.Range(0, records).First(index => data[index * fields.Length + key] == (string)name!);
                return data[record * fields.Length + value];
            }
            double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            var materials = ApiDouble.Create<cPropMaterial>((name, a) =>
            {
                Calls.Add("PropMaterial." + name);
                if (name != "GetWeightAndMass") throw new NotSupportedException(name);
                if (!GettersAnswer) return 1;
                a[2] = Number(Stored("Material Properties 02 - Basic Mechanical Properties", "Material", a[0], "UnitMass"));
                return 0;
            });
            var frameSections = ApiDouble.Create<cPropFrame>((name, a) =>
            {
                Calls.Add("PropFrame." + name);
                if (name != "GetSectProps") throw new NotSupportedException(name);
                if (!GettersAnswer) return 1;
                a[1] = Number(Stored("Frame Section Properties 01 - General", "SectionName", a[0], "Area"));
                return 0;
            });
            var cableSections = ApiDouble.Create<cPropCable>((name, a) =>
            {
                Calls.Add("PropCable." + name);
                if (name != "GetProp") throw new NotSupportedException(name);
                if (!GettersAnswer) return 1;
                a[2] = Number(Stored("Cable Section Definitions", "CableSect", a[0], "Area"));
                return 0;
            });
            var file = ApiDouble.Create<cFile>((name, a) =>
            {
                Calls.Add("File." + name);
                if (name != "OpenFile") throw new NotSupportedException(name);
                CurrentPath = (string)a[0]!;
                return 0;
            });
            Model = ApiDouble.Create<cSapModel>((name, a) =>
            {
                Calls.Add(name);
                switch (name)
                {
                    case "GetVersion": a[0] = "27.1.0"; a[1] = 27.1; return 0;
                    case "GetModelFilename": return CurrentPath;
                    case "GetPresentUnits": return eUnits.kN_m_C;
                    case "GetModelIsLocked": return true;                       // an analysed model, as copied
                    case "get_File": return file;
                    case "get_DatabaseTables": return database;
                    case "get_PropMaterial": return materials;
                    case "get_PropFrame": return frameSections;
                    case "get_PropCable": return cableSections;
                    default: throw new NotSupportedException(name);
                }
            });
        }
    }

    /// <summary>A table's values as SAP2000 displays them: densities and areas cut to five significant figures.</summary>
    private static string[] Displayed(string[] fields, string[] data)
    {
        var shown = data.ToArray();
        for (var index = 0; index < shown.Length; index++)
            if (fields[index % fields.Length] is "UnitMass" or "Area" && double.TryParse(shown[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                shown[index] = value.ToString("G5", CultureInfo.InvariantCulture);
        return shown;
    }

    private sealed class TablesHostFactory(TablesApi api) : ISapInstanceHostFactory
    {
        public int Created, Stopped;
        public List<string> Opened { get; } = [];
        public bool CopyExistedWhenOpened = true;
        public ISapInstanceHost Create(int instanceId) { Created++; return new Host(this, api, instanceId); }

        private sealed class Host(TablesHostFactory owner, TablesApi api, int instanceId) : ISapInstanceHost
        {
            private readonly SapGateway gateway = new(api.Model);
            private bool stopped;
            public int InstanceId => instanceId;
            public ISapGateway Gateway => gateway;
            public Task<ModelInfo> StartAsync(string modelPath, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                owner.Opened.Add(modelPath);
                owner.CopyExistedWhenOpened &= File.Exists(modelPath);
                return gateway.OpenModelAsync(modelPath);
            }
            public Task StopAsync()
            {
                if (stopped) return Task.CompletedTask;
                stopped = true;
                owner.Stopped++;
                gateway.Dispose();
                return Task.CompletedTask;
            }
            public async ValueTask DisposeAsync() => await StopAsync();
        }
    }

    private sealed class NoSelection : ISelectionService
    {
        public IReadOnlyList<string>? Select(string title, IReadOnlyList<string> available, IReadOnlyList<string> selected) => null;
    }

    /// <summary>The connected SAP2000 as Tab 08 sees it: the model that is open in it can be asked for its database
    /// tables. Everything that would open, change, save or analyse a model throws.</summary>
    private sealed class LiveSap : ISapGateway
    {
        private int reads;
        /// <summary>The model open in SAP2000 at this moment.</summary>
        public ExportText? Open;
        public Exception? Failure;
        public int Reads => Volatile.Read(ref reads);
        public List<ModelInfo> Expected { get; } = [];
        public IReadOnlyList<string>? Prefixes;
        public Task<SapTableSet> ReadDatabaseTablesAsync(IReadOnlyList<string> tableNamePrefixes, ModelInfo expected)
        {
            lock (Expected) Expected.Add(expected);
            Prefixes = tableNamePrefixes;
            Interlocked.Increment(ref reads);
            if (Failure is { } failure) return Task.FromException<SapTableSet>(failure);
            var open = Open ?? throw new NotSupportedException();
            return Task.Run(() => ApiTables(open, expected));                   // off the calling thread, as the SAP2000 lane is
        }

        public Task<ModelInfo> ConnectAsync() => throw new NotSupportedException();
        public Task<ModelInfo> OpenModelAsync(string modelPath) => throw new NotSupportedException();
        public Task<ModelInfo> SaveAsAsync(string targetPath, ModelInfo expected) => throw new NotSupportedException();
        public Task SaveAsync(ModelInfo expected) => throw new NotSupportedException();
        public Task RunAnalysisAsync(ModelInfo expected) => throw new NotSupportedException();
        public Task<ModelInfo> GetCurrentModelInfoAsync() => throw new NotSupportedException();
        public Task<ModelSnapshot> ReadAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public Task<Position> GetPointPositionAsync(string pointName, ModelInfo expected) => Task.FromResult(new Position());
        public Task<IReadOnlyList<PointLoad>> GetPointLoadsAsync(string pointName, ModelInfo expected) => Task.FromResult<IReadOnlyList<PointLoad>>([]);
        public Task<IReadOnlyList<CableTargetForce>> GetCableTargetForcesAsync(string cableName, ModelInfo expected) => Task.FromResult<IReadOnlyList<CableTargetForce>>([]);
        public Task<IReadOnlyList<CableSection>> GetCableSectionsAsync(IReadOnlyList<string> cableNames, ModelInfo expected) => Task.FromResult<IReadOnlyList<CableSection>>([]);
        public Task<IReadOnlyList<Mcotis.Desktop.Domain.Results.JointDisplacement>> GetJointDisplacementsAsync(IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected) => throw new NotSupportedException();
        public Task<IReadOnlyList<Mcotis.Desktop.Domain.Results.CableForceRead>> GetCableForcesAsync(IReadOnlyList<string> cableNames, string outputCase, bool isCombo, ModelInfo expected) => throw new NotSupportedException();
        public Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected) => Task.FromResult<IReadOnlyList<ForceSource>>([]);
        public Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected) => throw new NotSupportedException();
        public Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected) => throw new NotSupportedException();
        public Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<SapGroupSnapshot>> ReadOutputGroupsAsync(IReadOnlyList<string> groupNames, ModelInfo expected) => Task.FromResult<IReadOnlyList<SapGroupSnapshot>>([]);
        public Task ReplaceOutputGroupsAsync(IReadOnlyList<OutputSapGroupDefinition> groups, ModelInfo expected) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
