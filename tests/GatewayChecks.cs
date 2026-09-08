using CSiAPIv1;
using COT_VCF.Domain;
using COT_VCF.SAP2000;
using System.Reflection;
using System.IO;

internal static class GatewayChecks
{
    public static void Run()
    {
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
            // Returning to the baseline happens with the just-analysed child still locked, which is
            // exactly the state SAP2000 rejects File.OpenFile in.
            fake.Locked = true;
            var unlocksBeforeReopen = fake.UnlockCount;
            var rebaselined = gateway.OpenModelAsync(baselinePath).GetAwaiter().GetResult();
            Check(rebaselined.Path == baselinePath && !fake.Locked && fake.UnlockCount == unlocksBeforeReopen + 1,
                "OpenModel clears a locked model before File.OpenFile so worker 1 can return to the baseline");
        }
        finally { Directory.Delete(lifecycleRoot, true); }
    }
    private static void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected write rejection."); }
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private sealed class FakeApi
    {
        public bool Locked;
        public bool FailAnalysis;
        public bool NoRunCases;
        public eUnits Units = eUnits.kN_m_C;
        public string? FailLoadName, FailMoveName;
        public int WriteCount;
        public int UnlockCount, SaveCalls, AnalysisRuns;
        public int SetCoordSystemCalls;
        public string PresentCoordSystem = "Global";
        public string CurrentPath = "test.sdb";
        public Dictionary<string, Position> Points { get; } = new() { ["p1"] = new(0, 0, 0), ["p2"] = new(10, 0, 0) };
        public Dictionary<string, double[]> Loads { get; } = new() { ["p1"] = [10, 20, 30, 40, 50, 60], ["p2"] = [11, 21, 31, 41, 51, 61] };
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
                        string key = (string)a[0]!; a[1] = 1; a[2] = new[] { key }; a[3] = new[] { "LIVE" }; a[4] = new[] { 0 }; a[5] = new[] { "Global" };
                        for (int i = 0; i < 6; i++) a[6 + i] = new[] { Loads[key][i] }; return 0;
                    case "SetLoadForce":
                        WriteCount++; string p = (string)a[0]!; Loads[p] = ((double[])a[2]!).ToArray();
                        if (FailLoadName == p) { FailLoadName = null; return 9; } return 0;
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
                    case "OpenFile": CurrentPath = (string)a[0]!; return 0;
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
