using System.IO;
using System.Text.Json.Nodes;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Presentation;
using Microsoft.Data.Sqlite;

/// <summary>The desktop configuration at schema 2 (docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md §5.2, §5.3; its
/// "SchemaRoundTrip" for the input database and the JSON model file): a configuration of version 1 — the files in
/// tests/fem-schema, written by the code before the change — opens with every load case meaning what it meant, the
/// same identities, patterns and factors, and the same revision; the four kinds of case and their options are saved
/// and read back; and what contradicts itself is refused.</summary>
internal static class FemSchemaChecks
{
    private const string V1Database = "tests/fem-schema/v1-default.cotfem.db", V1Json = "tests/fem-schema/v1-default.cotfem.json";
    /// <summary>The revision of the version-1 fixture, as the code that wrote version 1 computed it.</summary>
    private const string V1Revision = "5b793864bc032b292b14a6952ccf851b82fa2fd08703cea47a4213fb24391aa8";

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mcotis-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var upgraded = DatabaseUpgrade(folder);
            var kinds = KindsRoundTrip(folder, upgraded);
            Refusals(folder, upgraded, kinds);
            JsonUpgrade(folder);
            BundledDefault(upgraded);
            NotSolvedInTheWorkspace(folder, kinds);
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    private static string Refused(Action action, string description)
    {
        try { action(); }
        catch (InvalidDataException refused)
        {
            Console.WriteLine($"PASS: {description} — {refused.Message}");
            return refused.Message;
        }
        throw new Exception("FAIL: " + description + " — it was accepted");
    }

    private static List<object?[]> Query(string path, string sql)
    {
        using var connection = FemInputDatabase.Open(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int column = 0; column < row.Length; column++) row[column] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(row);
        }
        return rows;
    }

    private static string Text(IEnumerable<object?[]> rows) => string.Join(" | ", rows.Select(row => string.Join(",", row.Select(value => value?.ToString() ?? "null"))));

    private static FemInputSnapshot DatabaseUpgrade(string folder)
    {
        var bytes = File.ReadAllBytes(V1Database);
        var oldCases = Query(V1Database, "SELECT name, case_id, initial_case_name, run, purpose FROM load_cases ORDER BY sequence_no");
        var oldComponents = Text(Query(V1Database, "SELECT c.name, k.pattern_name, k.factor, k.sequence_no FROM load_case_components k JOIN load_cases c ON c.case_id = k.case_id ORDER BY c.sequence_no, k.sequence_no"));
        var oldState = Query(V1Database, "SELECT (SELECT schema_version FROM database_info), (SELECT database_id FROM database_info), revision, is_locked, locked_revision FROM model_state").Single();
        var read = FemInputDatabase.Read(V1Database);
        var snapshot = read.Snapshot;
        var idOf = oldCases.ToDictionary(row => (string)row[0]!, row => (string)row[1]!);
        Check((long)oldState[0]! == 1 && snapshot.SchemaVersion == 2 && FemInputSnapshot.CurrentSchemaVersion == 2 && snapshot.CaseRows.Count == oldCases.Count && oldCases.Count == 3 &&
              snapshot.DatabaseId.ToString("D") == (string)oldState[1]! && snapshot.Revision == (long)oldState[2]! && snapshot.IsLocked == ((long)oldState[3]! != 0) && snapshot.LockedRevision == (long?)oldState[4] &&
              read.Outputs.Count == 1 && File.ReadAllBytes(V1Database).SequenceEqual(bytes),
            "a configuration database of schema 1 opens as schema 2: its three load cases, its identity, revision, lock and output registration as they were — and the file on disk untouched");
        Check(snapshot.CaseRows.Zip(oldCases).All(pair => pair.First.Name == (string)pair.Second[0]! && pair.First.Id == (string)pair.Second[1]! && pair.First.Run == ((long)pair.Second[3]! != 0) &&
                  (pair.First.CableTargetForces == true) == ((string)pair.Second[4]! == "CableTargetForces") &&
                  (pair.First.InitialCase ?? "") == ((string?)pair.Second[2] ?? "") &&
                  pair.First.InitialCaseId == (string.IsNullOrEmpty((string?)pair.Second[2]) ? null : idOf[(string)pair.Second[2]!])) &&
              snapshot.CaseRows.All(row => row is { CaseKind: null, GeometricMode: null, StiffnessSourceCase: null, StiffnessSourceCaseId: null, NonlinearControls: null, Modal: null }) &&
              snapshot.CaseRows.Count(row => row.InitialCaseId == idOf["Pretension"]) == 2 && snapshot.MassSources.Count == 0,
            "… every case keeps the identity the database gave it, its name, its run and fit flags; the case it named as the one it continues from is now referred to by that case's identity — " +
            "two independent cases continue from Pretension; none is given a kind, a P-Delta or a stiffness source it did not have, and no mass source is made up");
        Check(FemInputDatabase.RevisionHash(snapshot) == V1Revision,
            "… and it is the same revision as before the upgrade: results that belonged to it still do");

        string path = Path.Combine(folder, "upgraded.cotfem.db");
        FemInputDatabase.Write(path, snapshot, read.Outputs);
        var columns = Query(path, "SELECT name FROM pragma_table_info('load_cases')").Select(row => (string)row[0]!).ToArray();
        var options = Query(path, "SELECT c.name, o.case_kind, o.geometric_mode, o.initial_case_id, o.stiffness_source_case_id, o.stiffness_source_kind, o.mass_source_id, o.requested_modes, o.max_load_steps " +
                                  "FROM case_analysis_options o JOIN load_cases c ON c.case_id = o.case_id ORDER BY c.sequence_no");
        Check((long)Query(path, "SELECT schema_version FROM database_info").Single()[0]! == 2 && !columns.Contains("initial_case_name") &&
              Text(Query(path, "SELECT name, case_id, analysis_type FROM load_cases ORDER BY sequence_no")) == string.Join(" | ", oldCases.Select(row => $"{row[0]},{row[1]},NonlinearStatic")) &&
              options.Count == 3 && options.All(row => (string)row[1]! == "NonlinearStatic" && (string)row[2]! == "None" && row[4] is null && (string)row[5]! == "NotApplicable" && row[6] is null && row[7] is null && row[8] is null) &&
              options[0][3] is null && (string)options[1][3]! == idOf["Pretension"] && (string)options[2][3]! == idOf["Pretension"] &&
              Query(path, "PRAGMA foreign_key_check").Count == 0 && Query(path, "SELECT * FROM mass_sources").Count == 0,
            "saved, it is a schema-2 database: the load cases under the same identities, their options in a table of their own, the case continued from by identity and no longer by name, every reference resolving");
        Check(Text(Query(path, "SELECT c.name, k.pattern_name, k.factor, k.sequence_no FROM load_case_components k JOIN load_cases c ON c.case_id = k.case_id ORDER BY c.sequence_no, k.sequence_no")) == oldComponents &&
              oldComponents.Length > 0,
            "… with the load patterns and factors of every case exactly as schema 1 held them: " + oldComponents);
        var again = FemInputDatabase.Read(path).Snapshot;
        Check(again.SchemaVersion == 2 && again.CaseRows.SequenceEqual(snapshot.CaseRows) && again.CombinationRows.SequenceEqual(snapshot.CombinationRows) &&
              FemInputDatabase.RevisionHash(again) == V1Revision && (string)Query(path, "SELECT revision_hash FROM model_state").Single()[0]! == V1Revision,
            "… and read again it is the same configuration, of the same revision");

        // a rename: the name changes, the identity and what refers to it do not
        var renamed = FemInputDatabase.Read(path).Snapshot;
        renamed.CaseRows = renamed.CaseRows.Select(row => row.Name == "Pretension" ? row with { Name = "PT" } : row.InitialCase == "Pretension" ? row with { InitialCase = "PT" } : row).ToList();
        string renamedPath = Path.Combine(folder, "renamed.cotfem.db");
        File.Copy(path, renamedPath);
        FemInputDatabase.Write(renamedPath, renamed, read.Outputs);
        var afterRename = FemInputDatabase.Read(renamedPath).Snapshot.CaseRows;
        Check(afterRename.Single(row => row.Name == "PT").Id == idOf["Pretension"] && afterRename.Count(row => row.InitialCaseId == idOf["Pretension"] && row.InitialCase == "PT") == 2 &&
              afterRename.Zip(snapshot.CaseRows).All(pair => pair.First.Id == pair.Second.Id),
            "renaming a case changes its name and nothing else: it keeps its identity, and the cases that continue from it still refer to it");
        return snapshot;
    }

    /// <summary>The upgraded configuration with a case of every kind added to it, as a file of schema 2 can hold them.</summary>
    private static FemInputSnapshot KindsRoundTrip(string folder, FemInputSnapshot upgraded)
    {
        string loads = upgraded.CaseRows.First(row => row.Name.StartsWith("SLS", StringComparison.Ordinal)).Definition;
        string massId = Guid.NewGuid().ToString("D");
        var added = new List<FemDefinitionRowDto>
        {
            new("PDELTA_GRAVITY", "Initial P-Delta", loads, "Pretension", false, true, "App", "") { CaseKind = "InitialPDelta" },
            new("LIN_WIND", "Linear Static", loads, null, false, true, "App", "") { CaseKind = "LinearStatic", StiffnessSourceCase = "PDELTA_GRAVITY" },
            new("LIN_FREE", "Linear Static", loads, null, false, true, "App", "") { CaseKind = "LinearStatic" },
            new("MODES_PD", "Modal", "", null, false, true, "App", "") { CaseKind = "ModalEigen", StiffnessSourceCase = "PDELTA_GRAVITY", Modal = new("MS1", massId, 12) },
            new("NL_STEPS", "Nonlinear Static", loads, "PDELTA_GRAVITY", false, true, "App", "") { GeometricMode = "PDelta", NonlinearControls = new(5, 40, 1e-8, 1e-7) },
        };
        var legacy = upgraded.ToLegacy();
        legacy.CaseRows = [.. upgraded.CaseRows, .. added];
        legacy.MassSources = [new(massId, "MS1")];
        var snapshot = FemInputSnapshot.FromLegacy(legacy, upgraded);
        snapshot.IsLocked = false;
        snapshot.LockedRevision = null;
        string path = Path.Combine(folder, "kinds.cotfem.db");
        FemInputDatabase.Write(path, snapshot);
        var rows = snapshot.CaseRows.ToDictionary(row => row.Name);
        Check(snapshot.CaseRows.All(row => Guid.TryParse(row.Id, out _)) && snapshot.CaseRows.Select(row => row.Id).Distinct().Count() == 8 &&
              rows["PDELTA_GRAVITY"].InitialCaseId == rows["Pretension"].Id && rows["LIN_WIND"].StiffnessSourceCaseId == rows["PDELTA_GRAVITY"].Id &&
              rows["MODES_PD"].StiffnessSourceCaseId == rows["PDELTA_GRAVITY"].Id && rows["NL_STEPS"].InitialCaseId == rows["PDELTA_GRAVITY"].Id &&
              upgraded.CaseRows.All(row => rows[row.Name].Id == row.Id),
            "cases of every kind added to a configuration get identities of their own, the references they name are resolved to identities, and the cases that were there keep theirs");

        var types = Query(path, "SELECT name, analysis_type FROM load_cases ORDER BY sequence_no").ToDictionary(row => (string)row[0]!, row => (string)row[1]!);
        var options = Query(path, "SELECT c.name, o.case_kind, o.geometric_mode, o.initial_case_id, o.stiffness_source_case_id, o.stiffness_source_kind, o.mass_source_id, o.requested_modes, " +
                                  "o.max_load_steps, o.max_iterations_per_step, o.residual_tolerance, o.correction_tolerance FROM case_analysis_options o JOIN load_cases c ON c.case_id = o.case_id")
            .ToDictionary(row => (string)row[0]!);
        Check(types["PDELTA_GRAVITY"] == "NonlinearStatic" && types["LIN_WIND"] == "LinearStatic" && types["MODES_PD"] == "ModalEigen" && types["NL_STEPS"] == "NonlinearStatic" && types["Pretension"] == "NonlinearStatic" &&
              (string)options["PDELTA_GRAVITY"][1]! == "InitialPDelta" && (string)options["PDELTA_GRAVITY"][2]! == "PDelta" && (string)options["PDELTA_GRAVITY"][3]! == rows["Pretension"].Id &&
              (string)options["LIN_WIND"][4]! == rows["PDELTA_GRAVITY"].Id && (string)options["LIN_WIND"][5]! == "EndOfNonlinearCase" && (string)options["LIN_FREE"][5]! == "Unstressed" && options["LIN_FREE"][4] is null &&
              (string)options["MODES_PD"][1]! == "ModalEigen" && (string)options["MODES_PD"][6]! == massId && (long)options["MODES_PD"][7]! == 12 &&
              (string)options["NL_STEPS"][2]! == "PDelta" && (long)options["NL_STEPS"][8]! == 5 && (long)options["NL_STEPS"][9]! == 40 && (double)options["NL_STEPS"][10]! == 1e-8 && (double)options["NL_STEPS"][11]! == 1e-7,
            "in the database each case is the kind it is — not a fixed one — with its method beside it (an initial P-Delta case is solved as a nonlinear static case), its P-Delta, " +
            "the case it continues from or takes its stiffness from by identity, its mass source and modes, and its own iteration controls");
        Check(Query(path, $"SELECT COUNT(*) FROM load_case_components WHERE case_id = '{rows["MODES_PD"].Id}'").Single()[0] is 0L &&
              Text(Query(path, "SELECT mass_source_id, name FROM mass_sources")) == $"{massId},MS1" && Query(path, "PRAGMA foreign_key_check").Count == 0,
            "… a modal case has no load patterns in it, not a made-up one, and its mass source is in the table of mass sources");
        var read = FemInputDatabase.Read(path).Snapshot;
        Check(read.CaseRows.SequenceEqual(snapshot.CaseRows) && read.MassSources.SequenceEqual(snapshot.MassSources) &&
              FemInputDatabase.RevisionHash(read) == FemInputDatabase.RevisionHash(snapshot) && FemInputDatabase.RevisionHash(read) != V1Revision,
            "read back, every case has its kind and options as they were saved — and the configuration is another revision than the one without them");

        string json = Path.Combine(folder, "kinds.cotfem.json");
        FemModelFile.Write(json, snapshot.ToLegacy());
        var parsed = FemModelFile.Read(json);
        string text = File.ReadAllText(json);
        Check(parsed.Version == 2 && parsed.CaseRows.SequenceEqual(snapshot.CaseRows) && parsed.MassSources!.SequenceEqual(snapshot.MassSources) &&
              text.Contains("\"version\": 2") && text.Contains("\"caseKind\": \"InitialPDelta\"") && text.Contains("\"initialCaseId\"") && text.Contains("\"stiffnessSourceCaseId\""),
            "the JSON model file of version 2 holds the same: kinds by name, references by identity, mass sources");
        return snapshot;
    }

    private static void Refusals(string folder, FemInputSnapshot upgraded, FemInputSnapshot kinds)
    {
        string scratch = Path.Combine(folder, "refused.cotfem.db");
        FemInputSnapshot With(Func<FemDefinitionRowDto, FemDefinitionRowDto> change, string name)
        {
            var legacy = kinds.ToLegacy();
            legacy.CaseRows = legacy.CaseRows.Select(row => row.Name == name ? change(row) : row).ToList();
            var snapshot = FemInputSnapshot.FromLegacy(legacy, kinds);
            return snapshot;
        }
        var ids = kinds.CaseRows.ToDictionary(row => row.Name, row => row.Id!);
        Refused(() => FemInputDatabase.Write(scratch, With(row => row with { InitialCase = "Pretension", InitialCaseId = null }, "LIN_WIND")),
            "a linear static case that continues from a case is not written");
        Refused(() => FemInputDatabase.Write(scratch, With(row => row with { StiffnessSourceCase = "LIN_FREE", StiffnessSourceCaseId = null }, "LIN_WIND")),
            "… nor a case on the stiffness of a linear static case");
        Refused(() => FemInputDatabase.Write(scratch, With(row => row with { Definition = "1 DEAD" }, "MODES_PD")), "… nor a modal case with loads");
        Refused(() => FemInputDatabase.Write(scratch, With(row => row with { GeometricMode = "None" }, "PDELTA_GRAVITY")), "… nor an initial P-Delta case without P-Delta");
        Refused(() => FemInputDatabase.Write(scratch, With(row => row with { CaseKind = "Buckling" }, "LIN_FREE")), "… nor a case of a kind this version does not know");
        Refused(() => FemInputDatabase.Write(scratch, With(row => row with { InitialCaseId = ids["LIN_FREE"] }, "NL_STEPS")),
            "… nor a case that continues from one case by name and from another by identity");
        Refused(() => FemInputDatabase.Write(scratch, With(row => row with { Id = ids["Pretension"] }, "LIN_FREE")), "… nor two cases with one identity");
        Refused(() => FemInputDatabase.Write(scratch, With(row => row with { InitialCase = "Nowhere", InitialCaseId = null }, "NL_STEPS")),
            "… nor a case that continues from a case the configuration does not hold");
        Check(!File.Exists(scratch), "… and nothing was written by any of them");

        string good = Path.Combine(folder, "kinds.cotfem.db");
        string Tampered(string name, string sql)
        {
            string path = Path.Combine(folder, name);
            File.Copy(good, path, overwrite: true);
            using (var connection = FemInputDatabase.Open(path, readOnly: false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            return path;
        }
        string message = Refused(() => FemInputDatabase.Read(Tampered("tampered.cotfem.db", "UPDATE case_analysis_options SET initial_case_id = NULL WHERE case_kind = 'InitialPDelta'")),
            "a database whose table says a case starts from zero while its snapshot says it continues from another is refused, not read in favour of either");
        Check(message.Contains("PDELTA_GRAVITY", StringComparison.Ordinal) && message.Contains("inconsistent", StringComparison.Ordinal), "… naming the case");
        Refused(() => FemInputDatabase.Read(Tampered("kind.cotfem.db", "UPDATE case_analysis_options SET case_kind = 'LinearStatic' WHERE case_kind = 'ModalEigen'")),
            "… so is one whose table gives a case another kind than its snapshot");
        Refused(() => FemInputDatabase.Read(Tampered("newer.cotfem.db", "UPDATE database_info SET schema_version = 3")), "a database of a newer schema is refused");
        string claimed = Path.Combine(folder, "claimed.cotfem.db");
        File.Copy(V1Database, claimed);
        using (var connection = FemInputDatabase.Open(claimed, readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE database_info SET schema_version = 2";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        Refused(() => FemInputDatabase.Read(claimed), "a database that states schema 2 over a snapshot of schema 1 is refused");
        _ = upgraded;
    }

    private static void JsonUpgrade(string folder)
    {
        var bytes = File.ReadAllBytes(V1Json);
        string text = File.ReadAllText(V1Json);
        var first = FemModelFile.Read(V1Json);
        var second = FemModelFile.Read(V1Json);
        var pretension = first.CaseRows.Single(row => row.Name == "Pretension");
        Check(text.Contains("\"version\": 1") && !text.Contains("\"initialCaseId\"") && !text.Contains("\"caseKind\"") && first.Version == 2 && first.CaseRows.Count == 3 &&
              first.CaseRows.All(row => Guid.TryParse(row.Id, out _) && row is { CaseKind: null, GeometricMode: null, StiffnessSourceCaseId: null, Modal: null, NonlinearControls: null }) &&
              first.CaseRows.Count(row => row.InitialCase == "Pretension" && row.InitialCaseId == pretension.Id) == 2 && pretension.InitialCaseId is null &&
              first.CaseRows.SequenceEqual(second.CaseRows) && File.ReadAllBytes(V1Json).SequenceEqual(bytes),
            "a JSON model file of version 1 is read as version 2: every case with an identity — the same one each time it is read — the case it continues from by that identity, " +
            "nothing else added, and the file on disk untouched");
        string saved = Path.Combine(folder, "upgraded.cotfem.json");
        FemModelFile.Write(saved, first);
        Check(FemModelFile.Read(saved).CaseRows.SequenceEqual(first.CaseRows) && File.ReadAllText(saved).Contains("\"version\": 2"), "… and saved, it is a file of version 2 that reads the same");

        // the file with one member of its load cases changed
        static string Changed(string json, Action<JsonArray> change)
        {
            var file = JsonNode.Parse(json)!;
            change(file["caseRows"]!.AsArray());
            return file.ToJsonString();
        }
        Refused(() => FemModelFile.Parse(Changed(text, rows => rows[1]!["initialCase"] = "Missing")),
            "a version-1 file whose case continues from a case it does not hold is refused, not opened with the reference dropped");
        Refused(() => FemModelFile.Parse(Changed(text, rows => rows[2]!["name"] = "Pretension")),
            "… and so is one with two cases of one name: the case that is continued from would be ambiguous");
        Refused(() => FemModelFile.Parse(Changed(File.ReadAllText(saved), rows => rows[1]!.AsObject().Remove("initialCaseId"))),
            "a file of version 2 whose case names the case it continues from without its identity is refused: only a file of version 1 refers by name");
    }

    private static void BundledDefault(FemInputSnapshot upgraded)
    {
        var bundled = FemInputDatabase.ReadDefault().Snapshot;
        string resource = Path.Combine("src", "Mcotis.Desktop", "Resources", FemInputDatabase.DefaultModelName);
        Check(bundled.SchemaVersion == 2 && bundled.CaseRows.SequenceEqual(upgraded.CaseRows) && bundled.DatabaseId == upgraded.DatabaseId && bundled.Revision == upgraded.Revision &&
              bundled.IsLocked == upgraded.IsLocked && FemInputDatabase.RevisionHash(bundled) == V1Revision &&
              (long)Query(resource, "SELECT schema_version FROM database_info").Single()[0]! == 2 && Query(resource, "PRAGMA foreign_key_check").Count == 0 &&
              Query(resource, "SELECT COUNT(*) FROM case_analysis_options").Single()[0] is 3L,
            "the default model bundled with the application is that version-1 database taken through the upgrade and saved as schema 2: same identity, revision, lock, cases and identities");
    }

    /// <summary>A case this workspace cannot solve yet is kept as it is, and is not solved as something else.</summary>
    private static void NotSolvedInTheWorkspace(string folder, FemInputSnapshot kinds)
    {
        var legacy = kinds.ToLegacy();
        legacy.CaseRows = legacy.CaseRows.Select(row => row.Name == "PDELTA_GRAVITY" ? row with { Run = true } : row).ToList();
        string path = Path.Combine(folder, "workspace.cotfem.db");
        FemInputDatabase.Write(path, FemInputSnapshot.FromLegacy(legacy, kinds));
        var (vm, _, _, warnings) = FemModelFileChecks.DefaultModelApp();
        vm.OpenFemModelFile(path);
        var rows = vm.FemCaseRows.ToDictionary(row => row.Name);
        Check(warnings.Count == 0 && rows.Count == 8 && rows["PDELTA_GRAVITY"] is { Options: { Kind: Mcotis.Core.CaseKind.InitialPDelta, GeometricMode: Mcotis.Core.GeometricNonlinearity.PDelta, IsDefault: false } } &&
              rows["LIN_WIND"].Options is { Kind: Mcotis.Core.CaseKind.LinearStatic, StiffnessSourceCase: "PDELTA_GRAVITY" } && rows["MODES_PD"].Options is { Kind: Mcotis.Core.CaseKind.ModalEigen, MassSource: "MS1", RequestedModes: 12 } &&
              rows["NL_STEPS"].Options.Controls is { MaxLoadSteps: 5, MaxIterationsPerStep: 40 } && rows["Pretension"].Options.IsDefault &&
              kinds.CaseRows.All(row => rows[row.Name].Id.ToString("D") == row.Id),
            "the application opens a configuration with cases of every kind and keeps each as it is — kind, P-Delta, stiffness source, modes, controls and identity — ready for the type-dependent editor");

        // the case is renamed in the application: what continues from it and what uses its stiffness follow, and it cannot be removed from under them
        var pdelta = rows["PDELTA_GRAVITY"];
        pdelta.Name = "PD_G";
        bool removed = vm.RemoveFemLoadCase(pdelta);
        Check(rows["NL_STEPS"].InitialCase == "PD_G" && rows["LIN_WIND"].Options.StiffnessSourceCase == "PD_G" && rows["MODES_PD"].Options.StiffnessSourceCase == "PD_G" &&
              rows["LIN_FREE"].Options.StiffnessSourceCase is null && !removed && vm.FemCaseRows.Contains(pdelta) && warnings.Count == 1 &&
              warnings[0].Contains("'NL_STEPS'", StringComparison.Ordinal) && warnings[0].Contains("'LIN_WIND'", StringComparison.Ordinal) && warnings[0].Contains("'MODES_PD'", StringComparison.Ordinal),
            "renamed in the application, the case is followed by the case that continues from it and by the two that use its stiffness; removing it is refused while they are there — " + warnings.FirstOrDefault());
        warnings.Clear();

        var runs = FemModelFileChecks.Analyze(vm);
        string said = vm.FemMonitor.Message + " " + string.Join(" ", warnings);
        Check(runs.Count == 0 && vm.FemMonitor.Status == FemRunStatus.Failed && said.Contains("'PD_G' is analysed with P-Delta", StringComparison.Ordinal) &&
              said.Contains("not replaced by a first-order analysis", StringComparison.Ordinal),
            "Run Analysis with the P-Delta case ticked does not solve it as a first-order case: the run stops with the reason — " + vm.FemMonitor.Message);

        string saved = Path.Combine(folder, "workspace-saved" + FemInputDatabase.Extension);
        vm.SaveFemModelFileAsync(saved).GetAwaiter().GetResult();
        var back = FemInputDatabase.Read(saved).Snapshot;
        var expected = legacy.CaseRows.ToDictionary(row => row.Id!);
        Check(back.CaseRows.Count == 8 && back.CaseRows.All(row => expected[row.Id!] is var before && row.Name == (before.Name == "PDELTA_GRAVITY" ? "PD_G" : before.Name) &&
                  row.CaseKind == before.CaseKind && row.InitialCaseId == before.InitialCaseId && row.StiffnessSourceCaseId == before.StiffnessSourceCaseId &&
                  row.NonlinearControls == before.NonlinearControls && row.Modal == before.Modal && FemCaseSchema.GeometricOf(row) == FemCaseSchema.GeometricOf(before)) &&
              back.CaseRows.Count(row => row.StiffnessSourceCase == "PD_G") == 2 && back.MassSources.SequenceEqual(kinds.MassSources),
            "saved from the application, the configuration holds every case as it was opened, under the same identity — kinds, references and options, its mass source — and the renamed case under its new name");
    }
}
