using Mcotis.Desktop.Application;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Geometry;
using Mcotis.Desktop.Geometry.Handlers;
using Mcotis.Desktop.Infrastructure;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Collections.Immutable;
using Mcotis.Analysis;
using Native = Mcotis.Core;

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
            var native = LegacyNativeDocumentMigration.Convert(snapshot, LegacyNativeDocumentMigration.Source.Generated("VCF"));
            var nativeAgain = LegacyNativeDocumentMigration.Convert(snapshot, LegacyNativeDocumentMigration.Source.Generated("VCF"));
            Check(native.DocumentId.Value == snapshot.DatabaseId && native.Points.Count == sourceGeometry.Points.Count &&
                  native.Frames.Count == sourceGeometry.Members.Count(member => member.Kind == Mcotis.Desktop.Domain.MemberKind.Frame) &&
                  native.Cables.Count == sourceGeometry.Members.Count(member => member.Kind != Mcotis.Desktop.Domain.MemberKind.Frame) &&
                  native.LoadPatterns.Count == snapshot.PatternRows.Count && native.LoadCases.Count == snapshot.CaseRows.Count &&
                  native.LoadCombinations.Count == snapshot.CombinationRows.Count,
                "legacy VCF input becomes one complete native 3D document with geometry, loading and case definitions");
            Check(native.Settings.ActiveDofs == Mcotis.Core.DofMask.Full3D && native.Restraints.Count >= 2 &&
                  native.Points.Values.All(point => point.Origin is { Kind: Mcotis.Core.OriginKind.Generated, GenerationKey: not null }) &&
                  native.Frames.Keys.Order().SequenceEqual(nativeAgain.Frames.Keys.Order()) &&
                  native.AnalysisInputHash == nativeAgain.AnalysisInputHash,
                "VCF is a deterministic native generator snapshot with explicit supports, stable IDs and full-3D defaults");
            var nativeRoundTrip = Mcotis.Persistence.NativeDocumentFormat.Deserialize(Mcotis.Persistence.NativeDocumentFormat.Serialize(native));
            Check(nativeRoundTrip.DocumentId == native.DocumentId && nativeRoundTrip.AnalysisInputHash == native.AnalysisInputHash &&
                  nativeRoundTrip.Loads.Count == native.Loads.Count,
                "the migrated VCF model saves and reopens as a native .mcotis document without losing analysis input");
            var inputPath = Path.Combine(root, "model" + FemInputDatabase.Extension);
            var rollerName = sourceGeometry.Points.Keys.OrderBy(name => name, StringComparer.Ordinal).First();
            var roller = new Mcotis.Core.Restraint(true, false, true, false, false, false);
            snapshot.JointRestraints = [FemJointRestraintDto.FromNative(rollerName, roller)];
            FemInputDatabase.Write(inputPath, snapshot);

            Check(FemInputDatabase.Filter.Contains("MCOTiS Config", StringComparison.Ordinal) &&
                  !FemInputDatabase.Filter.Contains(".json", StringComparison.OrdinalIgnoreCase) &&
                  FemInputDatabase.OpenFilter.Contains("Legacy FEM Config", StringComparison.Ordinal),
                "Save creates one MCOTiS configuration database; Open still accepts the legacy FEM JSON for migration");

            var firstIds = ObjectIds(inputPath);
            var reopened = FemInputDatabase.Read(inputPath);
            var reopenedNative = LegacyNativeDocumentMigration.Convert(reopened.Snapshot, LegacyNativeDocumentMigration.Source.Imported("test"));
            Check(reopened.Snapshot.DatabaseId == snapshot.DatabaseId && reopened.Snapshot.Geometry!.Points.Count == sourceGeometry.Points.Count &&
                  reopened.Snapshot.JointRestraints.SequenceEqual(snapshot.JointRestraints) &&
                  reopenedNative.RestraintOf(reopenedNative.Points.Values.Single(point => point.Name == rollerName).Id) == roller,
                "FEM input database round-trips the model identity, geometry and exact six-DOF joint restraints");
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
            var documentId = new Native.ObjectId(Guid.NewGuid());
            var caseId = new Native.ObjectId(Guid.NewGuid());
            var modalCaseId = new Native.ObjectId(Guid.NewGuid());
            var massSourceId = new Native.ObjectId(Guid.NewGuid());
            var pointId = new Native.ObjectId(Guid.NewGuid());
            var frameId = new Native.ObjectId(Guid.NewGuid());
            var names = LegacyNames.FromEntries([(pointId, "J1", "Point"), (frameId, "F1", "Frame")]);
            var nativeStatic = new StaticResult(documentId, "static-hash", 4, caseId,
                [new(pointId, [0.001, 0.004, -0.002, 0.005, -0.003, 0.006], [4, 7, 5, 8, -6, 9], [0, 0, 0, 0, 0, 0])],
                [new(frameId, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1),
                    ImmutableArray.CreateRange(Enumerable.Repeat(0d, 12)), ImmutableArray.CreateRange(Enumerable.Repeat(0d, 12)),
                    [new(pointId, pointId, 0, 1, ImmutableArray.CreateRange(Enumerable.Repeat(0d, 12)), ImmutableArray.CreateRange(Enumerable.Repeat(0d, 12)),
                        [new(0, [0.001, 0.004, -0.002, 0.005, -0.003, 0.006], [1, 2, 3, 4, 5, 6])])])],
                [], new(6, 1e-12, 0.5, [], 0, 1e-13, null));
            var nativeModal = new ModalAnalysisResult(documentId, modalCaseId, "modal-hash", 4, massSourceId, null, "elastic-1",
                new("test", "1", 3, 0, 1e-10, 1e-8),
                [new(1, 100, 10 / (2 * Math.PI), 2 * Math.PI / 10, ModalEigenStatus.Positive,
                    [new(pointId, 0.1, 0.2, 0.3, 0.01, 0.02, 0.03)],
                    [new(Native.DofComponent.UX, 1.2, 2.3, 45.6, 45.6)], 2e-10)],
                new(12, [1, 2, 3, 4, 5, 6], [6, 5, 4, 3, 2, 1], new(1, 2, 3)), []);
            var nativeResults = new FemDocumentResults(new Dictionary<string, FemAnalysisResult> { ["SLS"] = result },
                new Dictionary<string, StaticResult> { ["SLS"] = nativeStatic },
                new Dictionary<string, ModalAnalysisResult> { ["MODES"] = nativeModal }, ["native note"], names)
            {
                CaseKinds = new Dictionary<string, Native.CaseKind> { ["SLS"] = Native.CaseKind.LinearStatic, ["MODES"] = Native.CaseKind.ModalEigen },
            };
            var run = new FemStudyRun("Model", "L", "H", "P", result, [], new Dictionary<string, FemAnalysisResult> { ["SLS"] = result }) { Native = nativeResults };
            var hash = FemInputDatabase.RevisionHash(snapshot);
            var outputPath = Path.Combine(root, "run" + FemOutputDatabase.Extension);
            var info = new FemOutputDatabaseInfo("run-1", snapshot.DatabaseId, snapshot.Revision, hash, DateTime.UtcNow, "Complete", inputPath);
            FemOutputDatabase.Write(outputPath, info, run);
            var output = FemOutputDatabase.Read(outputPath);
            Check(output.Info.InputDatabaseId == snapshot.DatabaseId && output.Info.InputRevisionHash == hash &&
                  output.Run.Results!["SLS"].Cables.Single().FitHistoryKN!.SequenceEqual([80d, 100d]) &&
                  output.Run.Native?.Static["SLS"].Joints.Single().Displacements[1] == 0.004 &&
                  output.Run.Native.Modal["MODES"].Modes.Single().Shape.Single().RZ == 0.03 &&
                  output.Run.Native.CaseKinds["SLS"] == Native.CaseKind.LinearStatic &&
                  output.Run.Native.Notes.SequenceEqual(["native note"]),
                "FEM output database round-trips run provenance, six-component static results and modal results");
            using (var connection = Open(outputPath))
                Check(Scalar(connection, "PRAGMA user_version") == FemOutputDatabase.CurrentSchemaVersion &&
                      Scalar(connection, "SELECT COUNT(*) FROM frame_station_results") == 1 &&
                      Scalar(connection, "SELECT COUNT(*) FROM cable_fit_iterations") == 2 &&
                      Scalar(connection, "SELECT COUNT(*) FROM joint_dof_results") == 6 &&
                      Scalar(connection, "SELECT COUNT(*) FROM frame_station_components") == 6 &&
                      Scalar(connection, "SELECT COUNT(*) FROM modal_modes") == 1 &&
                      Scalar(connection, "SELECT COUNT(*) FROM modal_vectors") == 6 &&
                      Scalar(connection, "SELECT COUNT(*) FROM modal_mass_participation") == 1,
                    "FEM output schema v2 exposes normalized planar, six-component frame/joint and modal rows");

            var legacyOutputPath = Path.Combine(root, "legacy-output" + FemOutputDatabase.Extension);
            File.Copy(outputPath, legacyOutputPath);
            using (var connection = OpenWritable(legacyOutputPath))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE native_result_payloads; PRAGMA user_version = 1;";
                command.ExecuteNonQuery();
            }
            var legacyOutput = FemOutputDatabase.Read(legacyOutputPath);
            Check(legacyOutput.Run.Native is null && legacyOutput.Run.Results!["SLS"].Joints.Single().UZM == -0.002,
                "the schema-v2 reader keeps opening a schema-v1 planar output without inventing native or modal results");

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

    private static SqliteConnection OpenWritable(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
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
