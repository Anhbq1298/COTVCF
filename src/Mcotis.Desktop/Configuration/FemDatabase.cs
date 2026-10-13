using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcotis.Desktop.Configuration;

/// <summary>The FEM input persisted in SQLite. It intentionally contains no MB study/candidate configuration.</summary>
public sealed class FemInputSnapshot
{
    public const string FormatName = "Mcotis.FemInput";
    public const int CurrentSchemaVersion = 1;

    public string Format { get; set; } = FormatName;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid DatabaseId { get; set; } = Guid.NewGuid();
    public long Revision { get; set; } = 1;
    public bool IsLocked { get; set; }
    public long? LockedRevision { get; set; }
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public ModelInfo? Model { get; set; }
    public GeometryModel? SessionBaseline { get; set; }
    public GeometryModel? Geometry { get; set; }
    public GeometryModel? Baseline { get; set; }
    public List<string> LoadPatterns { get; set; } = [];
    public List<string> LoadCaseNames { get; set; } = [];
    public UnitsDto Units { get; set; } = new();
    public ModelSnapshotDto CompatibilityModel { get; set; } = new();
    public LoadingConfigDto? LoadingSetup { get; set; }
    public TrussHeightConfigDto? GeometrySetup { get; set; }
    public CableForceConfigDto? CableSetup { get; set; }
    public OutputSettingsConfigDto? OutputSettings { get; set; }
    public List<FemSectionProperties> SectionProperties { get; set; } = [];
    public List<MaterialProperty> Materials { get; set; } = [];
    public List<FrameSectionProperty> FrameSections { get; set; } = [];
    public List<StrandSectionProperty> CableSections { get; set; } = [];
    public List<StrandSectionProperty> TendonSections { get; set; } = [];
    public SapFemLoading? Loading { get; set; }
    public List<FemPatternRowDto> PatternRows { get; set; } = [];
    public List<FemDefinitionRowDto> CaseRows { get; set; } = [];
    public List<FemDefinitionRowDto> CombinationRows { get; set; } = [];
    public FemSettingsDto Settings { get; set; } = new();

    public static FemInputSnapshot FromLegacy(FemModelFileDto file, FemInputSnapshot? previous = null)
    {
        var project = file.Project ?? throw new InvalidDataException("The FEM model has no resolved setup.");
        return new FemInputSnapshot
        {
            DatabaseId = previous?.DatabaseId ?? Guid.NewGuid(),
            Revision = previous is null ? 1 : previous.Revision + 1,
            IsLocked = previous?.IsLocked ?? false,
            LockedRevision = previous?.LockedRevision,
            SavedUtc = DateTime.UtcNow,
            Model = file.Model,
            SessionBaseline = file.SessionBaseline,
            Geometry = file.Geometry,
            Baseline = file.Baseline,
            LoadPatterns = file.LoadPatterns.ToList(),
            LoadCaseNames = file.LoadCaseNames.ToList(),
            Units = project.Units,
            CompatibilityModel = project.Model,
            LoadingSetup = project.Tab02Loading,
            GeometrySetup = project.Tab03TrussHeight,
            CableSetup = project.Tab04CableForce,
            OutputSettings = project.Tab05OutputSettings,
            SectionProperties = file.SectionProperties.ToList(),
            Materials = (file.Materials ?? []).ToList(),
            FrameSections = (file.FrameSections ?? []).ToList(),
            CableSections = (file.CableSections ?? []).ToList(),
            TendonSections = (file.TendonSections ?? []).ToList(),
            Loading = file.Loading,
            PatternRows = file.PatternRows.ToList(),
            CaseRows = file.CaseRows.ToList(),
            CombinationRows = file.CombinationRows.ToList(),
            Settings = file.Settings,
        };
    }

    public FemModelFileDto ToLegacy()
    {
        if (Format != FormatName || SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException("The selected FEM database was written by an unsupported version of Mcotis.");
        return new FemModelFileDto
        {
            SavedUtc = SavedUtc,
            Model = Model,
            SessionBaseline = SessionBaseline,
            Geometry = Geometry,
            Baseline = Baseline,
            LoadPatterns = LoadPatterns.ToList(),
            LoadCaseNames = LoadCaseNames.ToList(),
            Project = new McotisConfigDto
            {
                Units = Units,
                Model = CompatibilityModel,
                Tab02Loading = LoadingSetup,
                Tab03TrussHeight = GeometrySetup,
                Tab04CableForce = CableSetup,
                Tab05OutputSettings = OutputSettings,
                Tab05IterationStudy = null,
                PrintSetup = null,
            },
            SectionProperties = SectionProperties.ToList(),
            Materials = Materials.ToList(),
            FrameSections = FrameSections.ToList(),
            CableSections = CableSections.ToList(),
            TendonSections = TendonSections.ToList(),
            Loading = Loading,
            PatternRows = PatternRows.ToList(),
            CaseRows = CaseRows.ToList(),
            CombinationRows = CombinationRows.ToList(),
            Settings = Settings,
        };
    }
}

public sealed record FemOutputRegistration(string Path, string RunId, Guid DatabaseId, long Revision, string RevisionHash,
    DateTime CreatedUtc, bool Optimization = false, string? CandidateId = null);

public sealed record FemInputDatabaseRead(FemInputSnapshot Snapshot, IReadOnlyList<FemOutputRegistration> Outputs);

/// <summary>Portable input database. The JSON snapshot is the migration seam; normalized tables provide stable object IDs and queryable definitions.</summary>
public static class FemInputDatabase
{
    public const string Extension = ".cotfem.db";
    public const string Filter = "MCOTiS Config (*.cotfem.db)|*.cotfem.db";
    public const string OpenFilter = Filter + "|Legacy FEM Config (*.cotfem.json)|*.cotfem.json";
    public const string DefaultModelName = "Default.cotfem.db";
    private const string DefaultResource = "Mcotis.Default.cotfem.db";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Write(string path, FemInputSnapshot snapshot, IReadOnlyList<FemOutputRegistration>? outputs = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);
        var oldIds = File.Exists(path) ? TryReadObjectIds(path) : new Dictionary<(string Type, string Name), string>();
        AtomicFile.Write(path, temporary => WriteDatabase(temporary, snapshot, outputs ?? [], oldIds));
    }

    public static FemInputDatabaseRead Read(string path)
    {
        using var connection = Open(path, readOnly: true);
        var schema = Scalar(connection, "SELECT schema_version FROM database_info LIMIT 1");
        if (schema > FemInputSnapshot.CurrentSchemaVersion)
            throw new InvalidDataException($"The FEM database schema {schema} is newer than this app supports.");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM model_snapshot WHERE singleton = 1";
        var payload = command.ExecuteScalar() as string ?? throw new InvalidDataException("The FEM database has no input snapshot.");
        var snapshot = JsonSerializer.Deserialize<FemInputSnapshot>(payload, Json)
            ?? throw new InvalidDataException("The FEM database input snapshot is invalid.");
        Validate(snapshot);
        var outputs = new List<FemOutputRegistration>();
        using var output = connection.CreateCommand();
        output.CommandText = "SELECT path, run_id, database_id, revision, revision_hash, created_utc, is_optimization, candidate_id FROM output_registry ORDER BY created_utc";
        using var reader = output.ExecuteReader();
        while (reader.Read())
            outputs.Add(new(reader.GetString(0), reader.GetString(1), Guid.Parse(reader.GetString(2)), reader.GetInt64(3), reader.GetString(4),
                DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.GetInt64(6) != 0,
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        return new(snapshot, outputs);
    }

    /// <summary>Reads the SQLite configuration bundled with the app. SQLite needs a file path, so the embedded bytes
    /// are copied to a unique temporary file for this read only; callers never save back to the bundled template.</summary>
    public static FemInputDatabaseRead ReadDefault()
    {
        using var resource = typeof(FemInputDatabase).Assembly.GetManifestResourceStream(DefaultResource)
            ?? throw new InvalidDataException("The built-in default MCOTiS database is missing from the application.");
        var path = Path.Combine(Path.GetTempPath(), $"mcotis-default-{Guid.NewGuid():N}{Extension}");
        try
        {
            using (var file = File.Create(path)) resource.CopyTo(file);
            return Read(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    public static string RevisionHash(FemInputSnapshot snapshot)
    {
        var model = snapshot.ToLegacy();
        model.SavedUtc = default;
        var canonical = FemModelFile.Serialize(model);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void WriteDatabase(string path, FemInputSnapshot snapshot, IReadOnlyList<FemOutputRegistration> outputs,
        IReadOnlyDictionary<(string Type, string Name), string> oldIds)
    {
        if (File.Exists(path)) File.Delete(path);
        using var connection = Open(path, readOnly: false);
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, SchemaSql);
        Execute(connection, transaction,
            "INSERT INTO database_info(format, schema_version, database_id, saved_utc) VALUES ($format,$schema,$id,$saved)",
            ("$format", FemInputSnapshot.FormatName), ("$schema", snapshot.SchemaVersion), ("$id", snapshot.DatabaseId.ToString("D")),
            ("$saved", snapshot.SavedUtc.ToString("O")));
        Execute(connection, transaction,
            "INSERT INTO model_state(singleton, revision, is_locked, locked_revision, revision_hash) VALUES (1,$revision,$locked,$lockedRevision,$hash)",
            ("$revision", snapshot.Revision), ("$locked", snapshot.IsLocked ? 1 : 0),
            ("$lockedRevision", snapshot.LockedRevision), ("$hash", RevisionHash(snapshot)));
        Execute(connection, transaction, "INSERT INTO model_snapshot(singleton, payload_json) VALUES (1,$payload)",
            ("$payload", JsonSerializer.Serialize(snapshot, Json)));

        var jointIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var point in snapshot.Geometry!.Points.Values.OrderBy(point => point.Name, StringComparer.Ordinal))
        {
            var id = StableId(snapshot.DatabaseId, "Joint", point.Name, oldIds);
            jointIds[point.Name] = id;
            Execute(connection, transaction, "INSERT INTO geometry_objects(object_id,object_type,name,origin,generation_key) VALUES($id,'Joint',$name,'Imported',$key)",
                ("$id", id), ("$name", point.Name), ("$key", "Joint:" + point.Name));
            Execute(connection, transaction, "INSERT INTO joints(object_id,x,y,z) VALUES($id,$x,$y,$z)",
                ("$id", id), ("$x", point.Position.X), ("$y", point.Position.Y), ("$z", point.Position.Z));
        }
        var memberIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in snapshot.Geometry.Members.OrderBy(member => member.Name, StringComparer.Ordinal))
        {
            if (!jointIds.TryGetValue(member.Start, out var start) || !jointIds.TryGetValue(member.End, out var end))
                throw new InvalidDataException($"Member '{member.Name}' references a missing joint.");
            var type = member.Kind.ToString();
            var id = StableId(snapshot.DatabaseId, type, member.Name, oldIds);
            memberIds[member.Name] = id;
            Execute(connection, transaction, "INSERT INTO geometry_objects(object_id,object_type,name,origin,generation_key) VALUES($id,$type,$name,'Imported',$key)",
                ("$id", id), ("$type", type), ("$name", member.Name), ("$key", type + ":" + member.Name));
            Execute(connection, transaction, "INSERT INTO members(object_id,member_type,start_joint_id,end_joint_id) VALUES($id,$type,$start,$end)",
                ("$id", id), ("$type", type), ("$start", start), ("$end", end));
        }

        foreach (var property in snapshot.SectionProperties.OrderBy(item => item.MemberName, StringComparer.Ordinal))
        {
            if (!memberIds.TryGetValue(property.MemberName, out var memberId)) continue;
            Execute(connection, transaction, "INSERT INTO member_properties(object_id,section_name,material_name,member_category,payload_json) VALUES($id,$section,$material,$category,$payload)",
                ("$id", memberId), ("$section", property.SectionName), ("$material", property.MaterialName),
                ("$category", property.IsCable ? "Cable" : "Frame"), ("$payload", JsonSerializer.Serialize(property, Json)));
        }

        foreach (var material in snapshot.Materials.OrderBy(item => item.Name, StringComparer.Ordinal))
            Execute(connection, transaction, "INSERT INTO materials(material_id,name,material_type,payload_json) VALUES($id,$name,$type,$payload)",
                ("$id", StableId(snapshot.DatabaseId, "Material", material.Name, oldIds)), ("$name", material.Name),
                ("$type", material.Kind.ToString()), ("$payload", JsonSerializer.Serialize(material, Json)));
        foreach (var section in snapshot.FrameSections.OrderBy(item => item.Name, StringComparer.Ordinal))
            InsertSection(connection, transaction, snapshot.DatabaseId, oldIds, section.Name, "Frame", section.Shape.ToString(), section);
        foreach (var section in snapshot.CableSections.OrderBy(item => item.Name, StringComparer.Ordinal))
            InsertSection(connection, transaction, snapshot.DatabaseId, oldIds, section.Name, "Cable", "Strand", section);
        foreach (var section in snapshot.TendonSections.OrderBy(item => item.Name, StringComparer.Ordinal))
            InsertSection(connection, transaction, snapshot.DatabaseId, oldIds, section.Name, "Tendon", "Strand", section);

        foreach (var row in snapshot.PatternRows.Select((row, index) => (row, index)))
            Execute(connection, transaction, "INSERT INTO load_patterns(pattern_id,name,engineering_type,self_weight_multiplier,sequence_no,payload_json) VALUES($id,$name,$type,$sw,$seq,$payload)",
                ("$id", StableId(snapshot.DatabaseId, "LoadPattern", row.row.Name, oldIds)), ("$name", row.row.Name), ("$type", row.row.Type),
                ("$sw", row.row.SelfWeightMultiplier), ("$seq", row.index), ("$payload", JsonSerializer.Serialize(row.row, Json)));
        foreach (var row in snapshot.CaseRows.Select((row, index) => (row, index)))
        {
            var caseId = StableId(snapshot.DatabaseId, "LoadCase", row.row.Name, oldIds);
            Execute(connection, transaction, "INSERT INTO load_cases(case_id,name,analysis_type,purpose,initial_case_name,run,sequence_no,payload_json) VALUES($id,$name,'NonlinearStatic',$purpose,$initial,$run,$seq,$payload)",
                ("$id", caseId), ("$name", row.row.Name), ("$purpose", row.row.CableTargetForces == true ? "CableTargetForces" : "Normal"),
                ("$initial", row.row.InitialCase), ("$run", row.row.Run ? 1 : 0), ("$seq", row.index), ("$payload", JsonSerializer.Serialize(row.row, Json)));
            try
            {
                var terms = FemLoadDefinition.Parse(row.row.Definition);
                foreach (var term in terms.Select((term, index) => (term, index)))
                    Execute(connection, transaction, "INSERT INTO load_case_components(case_id,pattern_name,factor,sequence_no) VALUES($case,$pattern,$factor,$seq)",
                        ("$case", caseId), ("$pattern", term.term.Name), ("$factor", term.term.Factor), ("$seq", term.index));
            }
            catch (ArgumentException) { /* the snapshot remains authoritative and UI validation reports the row */ }
        }
        foreach (var row in snapshot.CombinationRows.Select((row, index) => (row, index)))
        {
            var combinationId = StableId(snapshot.DatabaseId, "LoadCombination", row.row.Name, oldIds);
            Execute(connection, transaction, "INSERT INTO load_combinations(combination_id,name,combination_type,run,sequence_no,payload_json) VALUES($id,$name,$type,$run,$seq,$payload)",
                ("$id", combinationId), ("$name", row.row.Name),
                ("$type", row.row.Type), ("$run", row.row.Run ? 1 : 0), ("$seq", row.index), ("$payload", JsonSerializer.Serialize(row.row, Json)));
            try
            {
                foreach (var term in FemLoadDefinition.Parse(row.row.Definition).Select((term, index) => (term, index)))
                    Execute(connection, transaction, "INSERT INTO load_combination_terms(combination_id,item_name,factor,sequence_no) VALUES($id,$name,$factor,$seq)",
                        ("$id", combinationId), ("$name", term.term.Name), ("$factor", term.term.Factor), ("$seq", term.index));
            }
            catch (ArgumentException) { /* the snapshot remains authoritative and UI validation reports the row */ }
        }

        if (snapshot.Loading is { } loading)
        {
            foreach (var row in loading.JointLoads.Select((row, index) => (row, index)))
                Execute(connection, transaction, "INSERT INTO joint_load_assignments(sequence_no,pattern_name,joint_name,fx_kn,fz_kn,my_knm) VALUES($seq,$pattern,$joint,$fx,$fz,$my)",
                    ("$seq", row.index), ("$pattern", row.row.Pattern), ("$joint", row.row.Load.PointName),
                    ("$fx", row.row.Load.FxKN), ("$fz", row.row.Load.FzKN), ("$my", row.row.Load.MyKNm));
            foreach (var row in loading.MemberLoads.Select((row, index) => (row, index)))
                Execute(connection, transaction, "INSERT INTO member_distributed_load_assignments(sequence_no,pattern_name,member_name,direction,rel_start,rel_end,value_start_kn_per_m,value_end_kn_per_m) VALUES($seq,$pattern,$member,$direction,$start,$end,$valueStart,$valueEnd)",
                    ("$seq", row.index), ("$pattern", row.row.Pattern), ("$member", row.row.Load.MemberName), ("$direction", row.row.Load.Direction.ToString()),
                    ("$start", row.row.Load.RelStart), ("$end", row.row.Load.RelEnd), ("$valueStart", row.row.Load.ValueStart), ("$valueEnd", row.row.Load.ValueEnd));
            foreach (var row in loading.MemberPointLoads.Select((row, index) => (row, index)))
                Execute(connection, transaction, "INSERT INTO member_point_load_assignments(sequence_no,pattern_name,member_name,direction,relative_distance,value_kn) VALUES($seq,$pattern,$member,$direction,$distance,$value)",
                    ("$seq", row.index), ("$pattern", row.row.Pattern), ("$member", row.row.Load.MemberName), ("$direction", row.row.Load.Direction.ToString()),
                    ("$distance", row.row.Load.Rel), ("$value", row.row.Load.ValueKN));
            foreach (var row in loading.GravityLoads.Select((row, index) => (row, index)))
                Execute(connection, transaction, "INSERT INTO member_gravity_load_assignments(sequence_no,pattern_name,member_name,multiplier_x,multiplier_z) VALUES($seq,$pattern,$member,$x,$z)",
                    ("$seq", row.index), ("$pattern", row.row.Pattern), ("$member", row.row.MemberName),
                    ("$x", row.row.MultiplierX), ("$z", row.row.MultiplierZ));
        }

        if (snapshot.CableSetup is { } cableSetup)
            foreach (var row in cableSetup.Cables.Select((row, index) => (row, index)))
                Execute(connection, transaction, "INSERT INTO cable_target_assignments(sequence_no,cable_name,pattern_name,mode,fixed_force_kn,min_force_kn,max_force_kn,step_force_kn,sync_mode,reference_cable_name,sync_factor) VALUES($seq,$cable,$pattern,$mode,$fixed,$min,$max,$step,$sync,$reference,$factor)",
                    ("$seq", row.index), ("$cable", row.row.CableName), ("$pattern", row.row.PretensionLoadCase),
                    ("$mode", row.row.Mode.ToString()), ("$fixed", row.row.FixedForce), ("$min", row.row.MinForce),
                    ("$max", row.row.MaxForce), ("$step", row.row.StepForce), ("$sync", row.row.SyncMode.ToString()),
                    ("$reference", row.row.ReferenceCableName), ("$factor", row.row.SyncFactor));

        Execute(connection, transaction, "INSERT INTO analysis_settings(singleton,target_fit_percent,target_fit_iterations,frame_mesh_segments,cable_sag) VALUES(1,$tolerance,$iterations,$mesh,$sag)",
            ("$tolerance", snapshot.Settings.TargetFitPercent), ("$iterations", snapshot.Settings.TargetFitIterations),
            ("$mesh", snapshot.Settings.FrameMeshSegments), ("$sag", snapshot.Settings.CableSag ? 1 : 0));

        if (snapshot.OutputSettings is { } settings)
            Execute(connection, transaction, "INSERT INTO output_settings(singleton,payload_json) VALUES(1,$payload)",
                ("$payload", JsonSerializer.Serialize(settings, Json)));
        foreach (var output in outputs)
            Execute(connection, transaction, "INSERT INTO output_registry(path,run_id,database_id,revision,revision_hash,created_utc,is_optimization,candidate_id) VALUES($path,$run,$id,$revision,$hash,$created,$optimization,$candidate)",
                ("$path", output.Path), ("$run", output.RunId), ("$id", output.DatabaseId.ToString("D")), ("$revision", output.Revision),
                ("$hash", output.RevisionHash), ("$created", output.CreatedUtc.ToString("O")), ("$optimization", output.Optimization ? 1 : 0),
                ("$candidate", output.CandidateId));
        transaction.Commit();
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        checkpoint.ExecuteNonQuery();
    }

    private static void InsertSection(SqliteConnection connection, SqliteTransaction transaction, Guid databaseId,
        IReadOnlyDictionary<(string Type, string Name), string> oldIds, string name, string category, string type, object payload) =>
        Execute(connection, transaction, "INSERT INTO sections(section_id,name,section_category,section_type,payload_json) VALUES($id,$name,$category,$type,$payload)",
            ("$id", StableId(databaseId, "Section:" + category, name, oldIds)), ("$name", name), ("$category", category),
            ("$type", type), ("$payload", JsonSerializer.Serialize(payload, Json)));

    private static Dictionary<(string Type, string Name), string> TryReadObjectIds(string path)
    {
        try
        {
            using var connection = Open(path, readOnly: true);
            var result = new Dictionary<(string, string), string>();
            foreach (var (sql, typeColumn) in new[]
            {
                ("SELECT object_type,name,object_id FROM geometry_objects", 0),
                ("SELECT 'Material',name,material_id FROM materials", 0),
                ("SELECT 'Section:' || section_category,name,section_id FROM sections", 0),
                ("SELECT 'LoadPattern',name,pattern_id FROM load_patterns", 0),
                ("SELECT 'LoadCase',name,case_id FROM load_cases", 0),
                ("SELECT 'LoadCombination',name,combination_id FROM load_combinations", 0),
            })
            {
                using var command = connection.CreateCommand(); command.CommandText = sql;
                using var reader = command.ExecuteReader();
                while (reader.Read()) result[(reader.GetString(typeColumn), reader.GetString(1))] = reader.GetString(2);
            }
            return result;
        }
        catch { return []; }
    }

    private static string StableId(Guid databaseId, string type, string name, IReadOnlyDictionary<(string Type, string Name), string> oldIds)
    {
        if (oldIds.TryGetValue((type, name), out var existing)) return existing;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{databaseId:D}|{type}|{name}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D");
    }

    private static void Validate(FemInputSnapshot snapshot)
    {
        if (snapshot.Format != FemInputSnapshot.FormatName) throw new InvalidDataException("The selected file is not a Mcotis FEM input database.");
        if (snapshot.SchemaVersion > FemInputSnapshot.CurrentSchemaVersion) throw new InvalidDataException("The FEM database was saved by a newer app version.");
        if (snapshot.Model is null || snapshot.Geometry is not { Points.Count: > 0 } || snapshot.Baseline is not { Points.Count: > 0 })
            throw new InvalidDataException("The FEM database has no model or geometry.");
        if (snapshot.LoadingSetup is null || snapshot.GeometrySetup is null || snapshot.CableSetup is null || snapshot.Loading is null)
            throw new InvalidDataException("The FEM database is missing loading, geometry, cable, or load-assignment setup.");
        if (snapshot.SectionProperties.Count == 0) throw new InvalidDataException("The FEM database has no member section properties.");
    }

    internal static SqliteConnection Open(string path, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys=ON;";
        foreignKeys.ExecuteNonQuery();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private const string SchemaSql = """
        CREATE TABLE database_info(format TEXT NOT NULL, schema_version INTEGER NOT NULL, database_id TEXT NOT NULL, saved_utc TEXT NOT NULL);
        CREATE TABLE model_state(singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision INTEGER NOT NULL, is_locked INTEGER NOT NULL, locked_revision INTEGER, revision_hash TEXT NOT NULL);
        CREATE TABLE model_snapshot(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload_json TEXT NOT NULL);
        CREATE TABLE geometry_objects(object_id TEXT PRIMARY KEY, object_type TEXT NOT NULL, name TEXT NOT NULL COLLATE NOCASE, origin TEXT NOT NULL, generation_key TEXT, UNIQUE(object_type,name));
        CREATE TABLE joints(object_id TEXT PRIMARY KEY REFERENCES geometry_objects(object_id) ON DELETE CASCADE, x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL);
        CREATE TABLE members(object_id TEXT PRIMARY KEY REFERENCES geometry_objects(object_id) ON DELETE CASCADE, member_type TEXT NOT NULL, start_joint_id TEXT NOT NULL REFERENCES joints(object_id), end_joint_id TEXT NOT NULL REFERENCES joints(object_id));
        CREATE TABLE member_properties(object_id TEXT PRIMARY KEY REFERENCES members(object_id) ON DELETE CASCADE, section_name TEXT, material_name TEXT, member_category TEXT NOT NULL, payload_json TEXT NOT NULL);
        CREATE TABLE materials(material_id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, material_type TEXT NOT NULL, payload_json TEXT NOT NULL);
        CREATE TABLE sections(section_id TEXT PRIMARY KEY, name TEXT NOT NULL, section_category TEXT NOT NULL, section_type TEXT NOT NULL, payload_json TEXT NOT NULL, UNIQUE(section_category,name));
        CREATE TABLE load_patterns(pattern_id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, engineering_type TEXT NOT NULL, self_weight_multiplier REAL NOT NULL, sequence_no INTEGER NOT NULL, payload_json TEXT NOT NULL);
        CREATE TABLE load_cases(case_id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, analysis_type TEXT NOT NULL, purpose TEXT NOT NULL, initial_case_name TEXT, run INTEGER NOT NULL, sequence_no INTEGER NOT NULL, payload_json TEXT NOT NULL);
        CREATE TABLE load_case_components(case_id TEXT NOT NULL REFERENCES load_cases(case_id) ON DELETE CASCADE, pattern_name TEXT NOT NULL, factor REAL NOT NULL, sequence_no INTEGER NOT NULL, PRIMARY KEY(case_id,sequence_no));
        CREATE TABLE load_combinations(combination_id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, combination_type TEXT NOT NULL, run INTEGER NOT NULL, sequence_no INTEGER NOT NULL, payload_json TEXT NOT NULL);
        CREATE TABLE load_combination_terms(combination_id TEXT NOT NULL REFERENCES load_combinations(combination_id) ON DELETE CASCADE, item_name TEXT NOT NULL, factor REAL NOT NULL, sequence_no INTEGER NOT NULL, PRIMARY KEY(combination_id,sequence_no));
        CREATE TABLE joint_load_assignments(sequence_no INTEGER PRIMARY KEY, pattern_name TEXT NOT NULL, joint_name TEXT NOT NULL, fx_kn REAL NOT NULL, fz_kn REAL NOT NULL, my_knm REAL NOT NULL);
        CREATE TABLE member_distributed_load_assignments(sequence_no INTEGER PRIMARY KEY, pattern_name TEXT NOT NULL, member_name TEXT NOT NULL, direction TEXT NOT NULL, rel_start REAL NOT NULL, rel_end REAL NOT NULL, value_start_kn_per_m REAL NOT NULL, value_end_kn_per_m REAL NOT NULL);
        CREATE TABLE member_point_load_assignments(sequence_no INTEGER PRIMARY KEY, pattern_name TEXT NOT NULL, member_name TEXT NOT NULL, direction TEXT NOT NULL, relative_distance REAL NOT NULL, value_kn REAL NOT NULL);
        CREATE TABLE member_gravity_load_assignments(sequence_no INTEGER PRIMARY KEY, pattern_name TEXT NOT NULL, member_name TEXT NOT NULL, multiplier_x REAL NOT NULL, multiplier_z REAL NOT NULL);
        CREATE TABLE cable_target_assignments(sequence_no INTEGER PRIMARY KEY, cable_name TEXT NOT NULL, pattern_name TEXT NOT NULL, mode TEXT NOT NULL, fixed_force_kn REAL, min_force_kn REAL, max_force_kn REAL, step_force_kn REAL, sync_mode TEXT NOT NULL, reference_cable_name TEXT, sync_factor REAL);
        CREATE TABLE analysis_settings(singleton INTEGER PRIMARY KEY CHECK(singleton=1), target_fit_percent REAL NOT NULL, target_fit_iterations INTEGER NOT NULL, frame_mesh_segments INTEGER NOT NULL, cable_sag INTEGER NOT NULL);
        CREATE TABLE output_settings(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload_json TEXT NOT NULL);
        CREATE TABLE output_registry(path TEXT PRIMARY KEY, run_id TEXT NOT NULL, database_id TEXT NOT NULL, revision INTEGER NOT NULL, revision_hash TEXT NOT NULL, created_utc TEXT NOT NULL, is_optimization INTEGER NOT NULL, candidate_id TEXT);
        CREATE INDEX ix_geometry_name ON geometry_objects(name);
        CREATE INDEX ix_members_start_end ON members(start_joint_id,end_joint_id);
        CREATE INDEX ix_case_components_case ON load_case_components(case_id,sequence_no);
        """;
}

public sealed record FemOutputDatabaseInfo(string RunId, Guid InputDatabaseId, long InputRevision, string InputRevisionHash,
    DateTime CreatedUtc, string State, string SourceInputPath);

public static class FemOutputDatabase
{
    public const string Extension = ".cotfem.out.db";

    public static void Write(string path, FemOutputDatabaseInfo info, FemStudyRun run)
    {
        AtomicFile.Write(path, temporary =>
        {
            using var connection = FemInputDatabase.Open(temporary, readOnly: false);
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, """
                CREATE TABLE run_info(run_id TEXT PRIMARY KEY,input_database_id TEXT NOT NULL,input_revision INTEGER NOT NULL,input_revision_hash TEXT NOT NULL,created_utc TEXT NOT NULL,state TEXT NOT NULL,source_input_path TEXT NOT NULL,run_payload_json TEXT NOT NULL);
                CREATE TABLE run_cases(case_id INTEGER PRIMARY KEY AUTOINCREMENT,name TEXT NOT NULL UNIQUE,nonlinear_iterations INTEGER NOT NULL,target_fit_iterations INTEGER NOT NULL,target_fit_failure TEXT);
                CREATE TABLE joint_displacements(case_id INTEGER NOT NULL REFERENCES run_cases(case_id),object_name TEXT NOT NULL,ux_m REAL NOT NULL,uz_m REAL NOT NULL,rotation_rad REAL NOT NULL,PRIMARY KEY(case_id,object_name));
                CREATE TABLE support_reactions(case_id INTEGER NOT NULL REFERENCES run_cases(case_id),object_name TEXT NOT NULL,rx_kn REAL NOT NULL,rz_kn REAL NOT NULL,PRIMARY KEY(case_id,object_name));
                CREATE TABLE frame_station_results(case_id INTEGER NOT NULL REFERENCES run_cases(case_id),object_name TEXT NOT NULL,station_index INTEGER NOT NULL,station REAL NOT NULL,axial_kn REAL NOT NULL,shear_kn REAL NOT NULL,moment_knm REAL NOT NULL,ux_m REAL NOT NULL,uz_m REAL NOT NULL,PRIMARY KEY(case_id,object_name,station_index));
                CREATE TABLE cable_results(case_id INTEGER NOT NULL REFERENCES run_cases(case_id),object_name TEXT NOT NULL,axial_kn REAL NOT NULL,active INTEGER NOT NULL,target_kn REAL NOT NULL,applied_pretension_kn REAL NOT NULL,pretension_stage_kn REAL,sag_m REAL,tangent_stiffness_ratio REAL,PRIMARY KEY(case_id,object_name));
                CREATE TABLE cable_fit_iterations(case_id INTEGER NOT NULL REFERENCES run_cases(case_id),object_name TEXT NOT NULL,iteration_no INTEGER NOT NULL,result_force_kn REAL NOT NULL,PRIMARY KEY(case_id,object_name,iteration_no));
                CREATE INDEX ix_frame_results ON frame_station_results(case_id,object_name,station);
                """);
            Execute(connection, transaction, "INSERT INTO run_info VALUES($run,$db,$revision,$hash,$created,$state,$source,$payload)",
                ("$run", info.RunId), ("$db", info.InputDatabaseId.ToString("D")), ("$revision", info.InputRevision),
                ("$hash", info.InputRevisionHash), ("$created", info.CreatedUtc.ToString("O")), ("$state", info.State),
                ("$source", info.SourceInputPath), ("$payload", JsonSerializer.Serialize(run, FemInputDatabase.Json)));
            var results = run.Results is { Count: > 0 } ? run.Results : new Dictionary<string, FemAnalysisResult> { ["Result"] = run.Result };
            foreach (var (name, result) in results)
            {
                Execute(connection, transaction, "INSERT INTO run_cases(name,nonlinear_iterations,target_fit_iterations,target_fit_failure) VALUES($name,$nonlinear,$fit,$failure)",
                    ("$name", name), ("$nonlinear", result.NonlinearIterations), ("$fit", result.TargetFitIterations), ("$failure", result.TargetFitFailure));
                using var idCommand = connection.CreateCommand();
                idCommand.Transaction = transaction;
                idCommand.CommandText = "SELECT last_insert_rowid();";
                var caseId = Convert.ToInt64(idCommand.ExecuteScalar());
                foreach (var joint in result.Joints)
                    Execute(connection, transaction, "INSERT INTO joint_displacements VALUES($case,$name,$ux,$uz,$rotation)",
                        ("$case", caseId), ("$name", joint.PointName), ("$ux", joint.UXM), ("$uz", joint.UZM), ("$rotation", joint.RotationRad));
                foreach (var reaction in result.SupportReactions)
                    Execute(connection, transaction, "INSERT INTO support_reactions VALUES($case,$name,$rx,$rz)",
                        ("$case", caseId), ("$name", reaction.PointName), ("$rx", reaction.ReactionXKN), ("$rz", reaction.ReactionZKN));
                foreach (var frame in result.Frames)
                {
                    var stations = frame.Stations is { Count: > 0 } ? frame.Stations :
                        [new FemFrameStation(0, frame.AxialStartKN, frame.ShearStartKN, frame.MomentStartKNm, 0, 0),
                         new FemFrameStation(1, frame.AxialEndKN, frame.ShearEndKN, frame.MomentEndKNm, 0, 0)];
                    foreach (var stationRow in stations.Select((station, index) => (station, index)))
                        Execute(connection, transaction, "INSERT INTO frame_station_results VALUES($case,$name,$index,$station,$axial,$shear,$moment,$ux,$uz)",
                            ("$case", caseId), ("$name", frame.MemberName), ("$index", stationRow.index), ("$station", stationRow.station.Fraction), ("$axial", stationRow.station.AxialKN),
                            ("$shear", stationRow.station.ShearKN), ("$moment", stationRow.station.MomentKNm), ("$ux", stationRow.station.UXM), ("$uz", stationRow.station.UZM));
                }
                foreach (var cable in result.Cables)
                {
                    Execute(connection, transaction, "INSERT INTO cable_results VALUES($case,$name,$axial,$active,$target,$applied,$stage,$sag,$stiffness)",
                        ("$case", caseId), ("$name", cable.MemberName), ("$axial", cable.AxialForceKN), ("$active", cable.Active ? 1 : 0),
                        ("$target", cable.TargetForceKN), ("$applied", cable.AppliedPretensionKN), ("$stage", cable.PretensionStageForceKN),
                        ("$sag", cable.SagM), ("$stiffness", cable.TangentStiffnessRatio));
                    foreach (var fit in (cable.FitHistoryKN ?? []).Select((force, index) => (force, index)))
                        Execute(connection, transaction, "INSERT INTO cable_fit_iterations VALUES($case,$name,$iteration,$force)",
                            ("$case", caseId), ("$name", cable.MemberName), ("$iteration", fit.index + 1), ("$force", fit.force));
                }
            }
            transaction.Commit();
        });
    }

    public static (FemOutputDatabaseInfo Info, FemStudyRun Run) Read(string path)
    {
        using var connection = FemInputDatabase.Open(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,input_database_id,input_revision,input_revision_hash,created_utc,state,source_input_path,run_payload_json FROM run_info LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("The output database has no run metadata.");
        var info = new FemOutputDatabaseInfo(reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetInt64(2), reader.GetString(3),
            DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.GetString(5), reader.GetString(6));
        var run = JsonSerializer.Deserialize<FemStudyRun>(reader.GetString(7), FemInputDatabase.Json)
            ?? throw new InvalidDataException("The output database run payload is invalid.");
        return (info, run);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}

/// <summary>Deletes only output databases whose embedded provenance matches the registered input revision.</summary>
public static class FemOutputLifecycle
{
    public static void DeleteVerified(FemInputSnapshot input, ICollection<FemOutputRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(registrations);
        var existing = registrations.Where(registration => File.Exists(registration.Path)).ToArray();

        // Verify the complete set first. Unlock must never leave a half-deleted result set.
        foreach (var registration in existing)
        {
            var (info, _) = FemOutputDatabase.Read(registration.Path);
            if (registration.DatabaseId != input.DatabaseId || info.InputDatabaseId != input.DatabaseId ||
                info.InputRevision != registration.Revision ||
                !string.Equals(info.InputRevisionHash, registration.RevisionHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Refusing to delete unverified output '{registration.Path}'.");
        }

        foreach (var registration in existing)
        {
            File.Delete(registration.Path);
            var directory = Path.GetDirectoryName(registration.Path);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        registrations.Clear();
    }
}
