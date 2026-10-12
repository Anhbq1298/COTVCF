using CSiAPIv1;
using COT_VCF.Domain;
using COT_VCF.SAP2000;
using System.Reflection;
using System.IO;

internal static class GatewayChecks
{
    public static void Run()
    {
        Check(SapGateway.ResolveEffectiveMaterial("A992", "None", "Frame1", "W14X90") == "A992" &&
              SapGateway.ResolveEffectiveMaterial("A992", "", "Frame1", "W14X90") == "A992" &&
              SapGateway.ResolveEffectiveMaterial("A992", "UserSteel", "Frame1", "W14X90") == "UserSteel",
            "FEM material resolution ignores CSI None overrides and respects explicit member overrides");
        Throws(() => SapGateway.ResolveEffectiveMaterial("None", "None", "Frame1", "W14X90"));

        var fake = new FakeApi();
        using var gateway = new SapGateway(fake.Model);
        var expected = new ModelInfo("test.sdb", "26", 6, "kN", "m", false);
        var baselineLoads = gateway.ReadPointLoadsAsync(expected).GetAwaiter().GetResult();
        Check(baselineLoads.Count == 2 && baselineLoads.Single(load => load.PointName == "p1").Fz == 30,
            "SAP baseline point loads are captured read-only");
        gateway.ApplyLoadsAsync([new("p1", "LIVE", 1, 2, 3)], expected).GetAwaiter().GetResult();
        Check(fake.Loads["p1"].SequenceEqual(new double[] { 1, 2, 3, 40, 50, 60 }), "SAP load write preserves existing moments");
        gateway.SetCableTargetForceAsync("Cable-A", "PRETENSION", 1300, expected).GetAwaiter().GetResult();
        Check(fake.TargetForces[("Cable-A", "PRETENSION")] is [{ Force: 1300, Distance: 0.5 }],
            "SAP cable target force replaces the same-pattern assignment and verifies the written value");
        var pointPosition = gateway.GetPointPositionAsync("p1", expected).GetAwaiter().GetResult();
        var pointLoads = gateway.GetPointLoadsAsync("p1", expected).GetAwaiter().GetResult();
        var cableTargets = gateway.GetCableTargetForcesAsync("Cable-A", expected).GetAwaiter().GetResult();
        Check(pointPosition == fake.Points["p1"] && pointLoads is [{ Fx: 1, Fy: 2, Fz: 3 }] &&
              cableTargets is [{ CableName: "Cable-A", LoadPattern: "PRETENSION", TargetForce: 1300, RelativeDistance: 0.5 }],
            "Focused gateway read-back returns global point position, point loads, and cable target force");
        var before = fake.Loads.ToDictionary(p => p.Key, p => p.Value.ToArray());
        fake.FailLoadName = "p2";
        Throws(() => gateway.ApplyLoadsAsync([new("p1", "LIVE", 100, 0, 0), new("p2", "LIVE", 200, 0, 0)], expected).GetAwaiter().GetResult());
        Check(fake.Loads.All(p => p.Value.SequenceEqual(before[p.Key])), "Failed load batch restores all attempted assignments");
        fake.Locked = true; int calls = fake.WriteCount;
        Throws(() => gateway.ApplyLoadsAsync([new("p1", "LIVE", 1, 0, 0)], expected).GetAwaiter().GetResult());
        Check(fake.WriteCount == calls, "Locked SAP model rejects writes without unlocking");
        fake.Locked = false; fake.Units = eUnits.N_mm_C;
        Throws(() => gateway.ApplyLoadsAsync([new("p1", "LIVE", 1, 0, 0)], expected).GetAwaiter().GetResult());
        Check(fake.WriteCount == calls, "Changed SAP units reject writes"); fake.Units = eUnits.kN_m_C;
        fake.Loads["p1"] = [0, 0, -12000, 0, 0, 0];
        gateway.ApplyLoadsAsync([new("p1", "LIVE", 0, 0, -0.0)], expected).GetAwaiter().GetResult();
        Check(!fake.Loads.ContainsKey("p1") && gateway.GetPointLoadsAsync("p1", expected).GetAwaiter().GetResult().Count == 0,
            "A zero target joint load removes the assignment instead of the all-zero SetLoadForce SAP2000 rejects");
        calls = fake.WriteCount;
        gateway.ApplyLoadsAsync([new("p1", "LIVE", 0, 0, 0)], expected).GetAwaiter().GetResult();
        Check(fake.WriteCount == calls && !fake.Loads.ContainsKey("p1"), "A zero target joint load with no existing assignment writes nothing");
        fake.Loads["p1"] = [1, 2, 3, 40, 50, 60];
        var geometry = new GeometryModel(fake.Points.ToDictionary(p => p.Key, p => new StructuralPoint(p.Key, p.Value)),
            [new StructuralMember("Cable-A", "p1", "p2", MemberKind.Cable)]);
        fake.PresentCoordSystem = "Local-CSys";
        gateway.MovePointsAsync(geometry, geometry, expected).GetAwaiter().GetResult();
        Check(fake.PresentCoordSystem == "Global" && fake.SetCoordSystemCalls == 1,
            "Geometry write explicitly establishes the SAP Global coordinate system");
        var proposed = new GeometryModel(geometry.Points.ToDictionary(p => p.Key, p => p.Value with { Position = p.Value.Position with { Z = 5 } }), geometry.Members);
        fake.FailMoveName = "p2";
        Throws(() => gateway.MovePointsAsync(geometry, proposed, expected).GetAwaiter().GetResult());
        Check(fake.Points.All(p => p.Value == geometry.Points[p.Key].Position), "Failed coordinate batch restores all attempted points");
        fake.Points["p1"] = new(0, 0, 1); calls = fake.WriteCount;
        Throws(() => gateway.MovePointsAsync(geometry, proposed, expected).GetAwaiter().GetResult());
        Check(fake.WriteCount == calls, "Externally edited coordinates reject stale preview");

        CableLengthsFollowTheJoints(expected);

        var lifecycleRoot = Path.Combine(Path.GetTempPath(), "COTVCF-gateway-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lifecycleRoot);
        try
        {
            var baselinePath = Path.Combine(lifecycleRoot, "baseline.sdb");
            File.WriteAllText(baselinePath, "baseline");
            var opened = gateway.OpenModelAsync(baselinePath).GetAwaiter().GetResult();
            fake.Locked = true;
            var childPath = Path.Combine(lifecycleRoot, "child.sdb");
            var child = gateway.SaveAsAsync(childPath, opened).GetAwaiter().GetResult();
            gateway.SaveAsync(child).GetAwaiter().GetResult();
            fake.FailAnalysis = true;
            Throws(() => gateway.RunAnalysisAsync(child).GetAwaiter().GetResult());
            gateway.RunAnalysisAsync(child).GetAwaiter().GetResult();
            gateway.SaveAsync(child).GetAwaiter().GetResult();
            Check(child.Path == childPath && !child.IsLocked && fake.UnlockCount == 1 &&
                  fake.AnalysisRuns == 2 && fake.Locked && fake.SaveCalls == 3,
                "SAP analysis checks failure codes, then saves the successfully analyzed locked model");
            fake.Locked = false; fake.NoRunCases = true;
            Throws(() => gateway.RunAnalysisAsync(child).GetAwaiter().GetResult());
            Check(fake.AnalysisRuns == 3 && !fake.Locked,
                "RunAnalysis that executes no load cases leaves the model unlocked and is rejected");
            // SAP2000 26 returns a nonzero code from File.OpenFile whenever it replaces an
            // already-open model, even though the open succeeds. The active model decides.
            fake.OpenFileCode = 1;
            var rebaselined = gateway.OpenModelAsync(baselinePath).GetAwaiter().GetResult();
            Check(rebaselined.Path == baselinePath,
                "OpenModel accepts SAP2000's nonzero File.OpenFile code when the requested model did become active");
            var otherPath = Path.Combine(lifecycleRoot, "other.sdb");
            File.WriteAllText(otherPath, "other");
            fake.OpenFileIgnoresRequest = true;
            Throws(() => gateway.OpenModelAsync(otherPath).GetAwaiter().GetResult());
            Check(fake.CurrentPath == baselinePath,
                "OpenModel still fails when SAP2000 does not actually activate the requested model");
            fake.OpenFileCode = 0; fake.OpenFileIgnoresRequest = false;
        }
        finally { Directory.Delete(lifecycleRoot, true); }
    }
    /// <summary>A cable object defined by its undeformed length keeps that number when its joints move; the geometry
    /// write has to carry it over to the new chord, or SAP2000 analyses a cable of the old length.</summary>
    private static void CableLengthsFollowTheJoints(ModelInfo expected)
    {
        var fake = new FakeApi();
        using var gateway = new SapGateway(fake.Model);
        GeometryModel Geometry() => new(fake.Points.ToDictionary(p => p.Key, p => new StructuralPoint(p.Key, p.Value)),
            [new StructuralMember("Cable-A", "p1", "p2", MemberKind.Cable)]);
        GeometryModel Moved(GeometryModel from, string point, Position to) =>
            new(from.Points.ToDictionary(p => p.Key, p => p.Key == point ? p.Value with { Position = to } : p.Value), from.Members);

        // 10 m chord, cable 2 % shorter than it, with its own segments, added weight and projected load
        fake.CableData["Cable-A"] = (8, 3, 0.5, 0.25, 9.8);
        var flat = Geometry();
        var taller = Moved(flat, "p2", new(10, 0, 7.5));              // chord 10 → 12.5 m
        gateway.MovePointsAsync(flat, taller, expected).GetAwaiter().GetResult();
        var carried = fake.CableData["Cable-A"];
        Check(fake.Points["p2"] == new Position(10, 0, 7.5) && fake.CableWrites == 1 && Math.Abs(carried.Length - 12.25) < 1e-12 &&
              (carried.Type, carried.Segments, carried.Weight, carried.Load) == (8, 3, 0.5, 0.25),
            "Moving a joint gives a cable defined by its undeformed length the same relative length on the new chord (9.8 of 10 m → 12.25 of 12.5 m), and nothing else of its definition changes");

        var shifted = new GeometryModel(taller.Points.ToDictionary(p => p.Key, p => p.Value with { Position = p.Value.Position with { X = p.Value.Position.X + 2 } }), taller.Members);
        gateway.MovePointsAsync(taller, shifted, expected).GetAwaiter().GetResult();
        Check(fake.CableWrites == 1 && fake.CableData["Cable-A"].Length == carried.Length, "A move that leaves the chord as long as it was does not rewrite the cable");

        fake.CableData["Cable-A"] = (3, 1, 0, 0, 12.25);              // defined by its tension at the I-end
        var lower = Moved(shifted, "p2", new(12, 0, 0));
        gateway.MovePointsAsync(shifted, lower, expected).GetAwaiter().GetResult();
        Check(fake.CableWrites == 1 && fake.CableData["Cable-A"].Length == 12.25,
            "A cable defined by a tension, a sag or a relative length is left to SAP2000: only an absolute undeformed length is carried over");

        fake.CableData["Cable-A"] = (8, 1, 0, 0, 10);
        fake.FailCableName = "Cable-A";
        var raised = Moved(lower, "p2", new(12, 0, 7.5));
        Throws(() => gateway.MovePointsAsync(lower, raised, expected).GetAwaiter().GetResult());
        Check(fake.Points["p2"] == new Position(12, 0, 0) && fake.CableData["Cable-A"].Length == 10,
            "When a cable does not take its new length, the joints and the cable are put back as they were");

        fake.CableParameterCount = 5;
        var writes = fake.WriteCount;
        Throws(() => gateway.MovePointsAsync(lower, raised, expected).GetAwaiter().GetResult());
        Check(fake.WriteCount == writes && fake.Points["p2"] == new Position(12, 0, 0),
            "Cable shape data SAP2000 returns in an unexpected layout stops the geometry write before anything is changed");
    }

    private static void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected write rejection."); }
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private sealed class FakeApi
    {
        public bool Locked;
        public bool FailAnalysis;
        public bool NoRunCases;
        public int OpenFileCode;
        public bool OpenFileIgnoresRequest;
        public eUnits Units = eUnits.kN_m_C;
        public string? FailLoadName, FailMoveName;
        public int WriteCount;
        public int UnlockCount, SaveCalls, AnalysisRuns;
        public int SetCoordSystemCalls;
        public string PresentCoordSystem = "Global";
        public string CurrentPath = "test.sdb";
        public Dictionary<string, Position> Points { get; } = new() { ["p1"] = new(0, 0, 0), ["p2"] = new(10, 0, 0) };
        public Dictionary<string, double[]> Loads { get; } = new() { ["p1"] = [10, 20, 30, 40, 50, 60], ["p2"] = [11, 21, 31, 41, 51, 61] };
        // cable shape definition: SAP2000 type code (8 = by undeformed length), segments, added weight, projected load, undeformed length
        public Dictionary<string, (int Type, int Segments, double Weight, double Load, double Length)> CableData { get; } = new() { ["Cable-A"] = (8, 1, 0, 0, 10) };
        public int CableWrites;
        public int CableParameterCount = 11;
        public string? FailCableName;
        public Dictionary<(string Cable, string Pattern), List<(double Force, double Distance)>> TargetForces { get; } = new()
        {
            [("Cable-A", "PRETENSION")] = [(900, 0.25), (1000, 0.75)]
        };
        public cSapModel Model { get; }
        public FakeApi()
        {
            var point = ApiDouble.Create<cPointObj>((name, a) =>
            {
                switch (name)
                {
                    case "GetNameList": a[0] = Points.Count; a[1] = Points.Keys.ToArray(); return 0;
                    case "GetCoordCartesian": var pos = Points[(string)a[0]!]; a[1] = pos.X; a[2] = pos.Y; a[3] = pos.Z; return 0;
                    case "GetLoadForce":
                        string key = (string)a[0]!;
                        if (!Loads.ContainsKey(key))
                        {
                            a[1] = 0; a[2] = Array.Empty<string>(); a[3] = Array.Empty<string>(); a[4] = Array.Empty<int>(); a[5] = Array.Empty<string>();
                            for (int i = 0; i < 6; i++) a[6 + i] = Array.Empty<double>(); return 0;
                        }
                        a[1] = 1; a[2] = new[] { key }; a[3] = new[] { "LIVE" }; a[4] = new[] { 0 }; a[5] = new[] { "Global" };
                        for (int i = 0; i < 6; i++) a[6 + i] = new[] { Loads[key][i] }; return 0;
                    case "SetLoadForce":
                        WriteCount++; string p = (string)a[0]!;
                        if (((double[])a[2]!).All(value => value == 0)) return 1; // SAP2000 rejects all-zero joint loads.
                        Loads[p] = ((double[])a[2]!).ToArray();
                        if (FailLoadName == p) { FailLoadName = null; return 9; } return 0;
                    case "DeleteLoadForce":
                        WriteCount++; Loads.Remove((string)a[0]!); return 0;
                    default: throw new NotSupportedException(name);
                }
            });
            var edit = ApiDouble.Create<cEditPoint>((name, a) =>
            {
                if (name != "ChangeCoordinates_1") throw new NotSupportedException(name);
                WriteCount++; string p = (string)a[0]!; Points[p] = new((double)a[1]!, (double)a[2]!, (double)a[3]!);
                if (FailMoveName == p) { FailMoveName = null; return 9; } return 0;
            });
            object? NoMembers(string name, object?[] a) { if (name == "GetNameList") { a[0] = 0; a[1] = Array.Empty<string>(); return 0; } throw new NotSupportedException(name); }
            var frame = ApiDouble.Create<cFrameObj>(NoMembers);
            var cable = ApiDouble.Create<cCableObj>((name, a) =>
            {
                switch (name)
                {
                    case "GetNameList": a[0] = 1; a[1] = new[] { "Cable-A" }; return 0;
                    case "GetPoints": a[1] = "p1"; a[2] = "p2"; return 0;
                    case "GetCableData":
                    {
                        var data = CableData[(string)a[0]!];
                        var (i, j) = (Points["p1"], Points["p2"]);
                        var chord = Math.Sqrt(Math.Pow(j.X - i.X, 2) + Math.Pow(j.Y - i.Y, 2) + Math.Pow(j.Z - i.Z, 2));
                        var parameters = new double[CableParameterCount];
                        if (CableParameterCount > 10) { parameters[9] = data.Length; parameters[10] = data.Length / chord; }
                        a[1] = data.Type; a[2] = data.Segments; a[3] = data.Weight; a[4] = data.Load; a[5] = false; a[6] = false; a[7] = parameters;
                        return 0;
                    }
                    case "SetCableData":
                    {
                        WriteCount++; CableWrites++;
                        var cableKey = (string)a[0]!;
                        CableData[cableKey] = ((int)a[1]!, (int)a[2]!, (double)a[3]!, (double)a[4]!, (double)a[5]!);
                        if (FailCableName == cableKey) { FailCableName = null; return 9; }
                        return 0;
                    }
                    case "GetLoadTargetForce":
                        var cableName = (string)a[0]!;
                        var rows = TargetForces
                            .Where(pair => pair.Key.Cable == cableName)
                            .SelectMany(pair => pair.Value.Select(value => (pair.Key.Pattern, value.Force, value.Distance)))
                            .ToArray();
                        a[1] = rows.Length;
                        a[2] = Enumerable.Repeat(cableName, rows.Length).ToArray();
                        a[3] = rows.Select(row => row.Pattern).ToArray();
                        a[4] = rows.Select(row => row.Force).ToArray();
                        a[5] = rows.Select(row => row.Distance).ToArray();
                        return 0;
                    case "DeleteLoadTargetForce":
                        TargetForces.Remove(((string)a[0]!, (string)a[1]!));
                        return 0;
                    case "SetLoadTargetForce":
                        var key = ((string)a[0]!, (string)a[1]!);
                        if (!TargetForces.TryGetValue(key, out var assignments)) TargetForces[key] = assignments = [];
                        assignments.Add(((double)a[2]!, (double)a[3]!));
                        return 0;
                    default: throw new NotSupportedException(name);
                }
            });
            var tendon = ApiDouble.Create<cTendonObj>(NoMembers);
            var patterns = ApiDouble.Create<cLoadPatterns>((name, a) =>
            {
                if (name == "GetNameList") { a[0] = 2; a[1] = new[] { "LIVE", "PRETENSION" }; return 0; }
                throw new NotSupportedException(name);
            });
            var view = ApiDouble.Create<cView>((name, a) => 0);
            var analyze = ApiDouble.Create<cAnalyze>((name, a) =>
            {
                if (name != "RunAnalysis") throw new NotSupportedException(name);
                AnalysisRuns++;
                if (FailAnalysis) { FailAnalysis = false; return 9; }
                if (!NoRunCases) Locked = true;
                return 0;
            });
            var file = ApiDouble.Create<cFile>((name, a) =>
            {
                switch (name)
                {
                    case "OpenFile": if (!OpenFileIgnoresRequest) CurrentPath = (string)a[0]!; return OpenFileCode;
                    case "Save": SaveCalls++; CurrentPath = (string)a[0]!; return 0;
                    default: throw new NotSupportedException(name);
                }
            });
            Model = ApiDouble.Create<cSapModel>((name, a) =>
            {
                switch (name)
                {
                    case "GetVersion": a[0] = "26"; a[1] = 26d; return 0;
                    case "GetModelFilename": return CurrentPath;
                    case "GetPresentUnits": return Units;
                    case "GetModelIsLocked": return Locked;
                    case "SetModelIsLocked": UnlockCount++; Locked = (bool)a[0]!; return 0;
                    case "GetPresentCoordSystem": return PresentCoordSystem;
                    case "SetPresentCoordSystem": SetCoordSystemCalls++; PresentCoordSystem = (string)a[0]!; return 0;
                    case "GetMergeTol": a[0] = .001; return 0;
                    case "get_PointObj": return point;
                    case "get_EditPoint": return edit;
                    case "get_FrameObj": return frame;
                    case "get_CableObj": return cable;
                    case "get_TendonObj": return tendon;
                    case "get_LoadPatterns": return patterns;
                    case "get_View": return view;
                    case "get_Analyze": return analyze;
                    case "get_File": return file;
                    default: throw new NotSupportedException(name);
                }
            });
        }
    }
}
public class ApiDouble : DispatchProxy
{
    private Func<string, object?[], object?> handler = null!;
    public static T Create<T>(Func<string, object?[], object?> handler) where T : class
    {
        var proxy = Create<T, ApiDouble>(); ((ApiDouble)(object)proxy).handler = handler; return proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => handler(targetMethod!.Name, args!);
}
