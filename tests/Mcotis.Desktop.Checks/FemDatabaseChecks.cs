using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Geometry.Handlers;
using Mcotis.Desktop.Infrastructure;
using Microsoft.Data.Sqlite;
using System.IO;

internal static class FemDatabaseChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcotis-db-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = FemInputDatabase.ReadDefault().Snapshot.ToLegacy();
            var sourceGeometry = source.Geometry ?? throw new InvalidOperationException("The default FEM model has no geometry.");
            var snapshot = FemInputSnapshot.FromLegacy(source);
            var inputPath = Path.Combine(root, "model" + FemInputDatabase.Extension);
            FemInputDatabase.Write(inputPath, snapshot);

            Check(FemInputDatabase.Filter.Contains("MCOTiS Config", StringComparison.Ordinal) &&
                  !FemInputDatabase.Filter.Contains(".json", StringComparison.OrdinalIgnoreCase) &&
                  FemInputDatabase.OpenFilter.Contains("Legacy FEM Config", StringComparison.Ordinal),
                "Save creates one MCOTiS configuration database; Open still accepts the legacy FEM JSON for migration");

            var firstIds = ObjectIds(inputPath);
            var reopened = FemInputDatabase.Read(inputPath);
            Check(reopened.Snapshot.DatabaseId == snapshot.DatabaseId && reopened.Snapshot.Geometry!.Points.Count == sourceGeometry.Points.Count,
                "FEM input database round-trips the model identity and geometry");
            Check(reopened.Snapshot.PatternRows.Count == source.PatternRows.Count && reopened.Snapshot.CaseRows.Count == source.CaseRows.Count &&
                  reopened.Snapshot.OutputSettings is not null && reopened.Snapshot.ToLegacy().Project?.Tab05IterationStudy is null,
                "FEM input database stores load definitions and output settings without the MB iteration study");
            using (var connection = Open(inputPath))
                Check(Scalar(connection, "SELECT COUNT(*) FROM member_properties") == snapshot.SectionProperties.Count &&
                      Scalar(connection, "SELECT COUNT(*) FROM joint_load_assignments") == snapshot.Loading!.JointLoads.Count &&
                      Scalar(connection, "SELECT COUNT(*) FROM cable_target_assignments") == snapshot.CableSetup!.Cables.Count &&
                      Scalar(connection, "SELECT COUNT(*) FROM analysis_settings") == 1,
                    "FEM input database normalizes member assignments, applied loads, cable dependencies and analysis settings");

            snapshot.Revision++;
            snapshot.SavedUtc = DateTime.UtcNow;
            FemInputDatabase.Write(inputPath, snapshot);
            Check(firstIds.SequenceEqual(ObjectIds(inputPath)), "FEM database keeps stable IDs when an object keeps its type and name");

            var result = new FemAnalysisResult(
                [new("J1", 0.001, -0.002, 0.003)],
                [new("J1", 4, 5)],
                [new("F1", 1, 2, 3, 4, 5, 6, [new(0, 1, 3, 5, 0.001, -0.002)])],
                [new("C1", 120, true, 100, 110, 100, [80, 100], 0.02, 0.9)], 7, 2);
            var run = new FemStudyRun("Model", "L", "H", "P", result, [], new Dictionary<string, FemAnalysisResult> { ["SLS"] = result });
            var hash = FemInputDatabase.RevisionHash(snapshot);
            var outputPath = Path.Combine(root, "run" + FemOutputDatabase.Extension);
            var info = new FemOutputDatabaseInfo("run-1", snapshot.DatabaseId, snapshot.Revision, hash, DateTime.UtcNow, "Complete", inputPath);
            FemOutputDatabase.Write(outputPath, info, run);
            var output = FemOutputDatabase.Read(outputPath);
            Check(output.Info.InputDatabaseId == snapshot.DatabaseId && output.Info.InputRevisionHash == hash &&
                  output.Run.Results!["SLS"].Cables.Single().FitHistoryKN!.SequenceEqual([80d, 100d]),
                "FEM output database round-trips run provenance and engineering results");
            using (var connection = Open(outputPath))
                Check(Scalar(connection, "SELECT COUNT(*) FROM frame_station_results") == 1 &&
                      Scalar(connection, "SELECT COUNT(*) FROM cable_fit_iterations") == 2,
                    "FEM output database exposes normalized frame and cable iteration rows");

            var registration = new FemOutputRegistration(outputPath, info.RunId, snapshot.DatabaseId, snapshot.Revision, hash, info.CreatedUtc);
            var sessions = new FemWorkingSessionStore(Path.Combine(root, "sessions"));
            var workingPath = sessions.Save(snapshot, [registration], inputPath);
            var active = sessions.TryReadActive();
            Check(File.Exists(workingPath) && active?.Snapshot.DatabaseId == snapshot.DatabaseId && active?.Outputs.Single().Path == outputPath,
                "working-session database reopens the current model and registered outputs");

            var registeredOutputs = new List<FemOutputRegistration> { registration };
            FemOutputLifecycle.DeleteVerified(snapshot, registeredOutputs);
            Check(!File.Exists(outputPath) && registeredOutputs.Count == 0,
                "unlock cleanup deletes every verified output database and clears its registry");

            var objects = GeometryModelAdapter.ToObjects(sourceGeometry, snapshot.DatabaseId);
            var solver = GeometryModelAdapter.ToSolver(objects);
            Check(solver.Points.Keys.Order().SequenceEqual(sourceGeometry.Points.Keys.Order()) &&
                  solver.Members.Select(member => member.Name).Order().SequenceEqual(sourceGeometry.Members.Select(member => member.Name).Order()),
                "joint/frame/cable object handlers preserve solver geometry");
            var naming = new GeometryNamingService();
            Check(naming.SymmetricCableName("Cable1a-2", 7, 'A') == "Cable7a-2" &&
                  naming.SymmetricCableName("Cable1a-2", 7, 'B') == "Cable7b-2" &&
                  naming.NextSequentialName("Frame001", ["Frame001", "Frame002"]) == "Frame003",
                "parametric naming generates deterministic symmetric cable pairs and sequences");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<string> ObjectIds(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT object_type || ':' || name || '=' || object_id FROM geometry_objects ORDER BY object_type,name";
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
