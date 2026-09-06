using CSiAPIv1;
using COT_VCF.Domain;
using COT_VCF.SAP2000;
using System.Reflection;

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
        var geometry = new GeometryModel(fake.Points.ToDictionary(p => p.Key, p => new StructuralPoint(p.Key, p.Value)), []);
        var proposed = new GeometryModel(geometry.Points.ToDictionary(p => p.Key, p => p.Value with { Position = p.Value.Position with { Z = 5 } }), []);
        fake.FailMoveName = "p2";
        Throws(() => gateway.MovePointsAsync(geometry, proposed, expected).GetAwaiter().GetResult());
        Check(fake.Points.All(p => p.Value == geometry.Points[p.Key].Position), "Failed coordinate batch restores all attempted points");
        fake.Points["p1"] = new(0, 0, 1); calls = fake.WriteCount;
        Throws(() => gateway.MovePointsAsync(geometry, proposed, expected).GetAwaiter().GetResult());
        Check(fake.WriteCount == calls, "Externally edited coordinates reject stale preview");
    }
    private static void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected write rejection."); }
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private sealed class FakeApi
    {
        public bool Locked;
        public eUnits Units = eUnits.kN_m_C;
        public string? FailLoadName, FailMoveName;
        public int WriteCount;
        public Dictionary<string, Position> Points { get; } = new() { ["p1"] = new(0, 0, 0), ["p2"] = new(10, 0, 0) };
        public Dictionary<string, double[]> Loads { get; } = new() { ["p1"] = [10, 20, 30, 40, 50, 60], ["p2"] = [11, 21, 31, 41, 51, 61] };
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
            var frame = ApiDouble.Create<cFrameObj>(NoMembers); var cable = ApiDouble.Create<cCableObj>(NoMembers); var tendon = ApiDouble.Create<cTendonObj>(NoMembers);
            var patterns = ApiDouble.Create<cLoadPatterns>((name, a) => { a[0] = 1; a[1] = new[] { "LIVE" }; return 0; });
            var view = ApiDouble.Create<cView>((name, a) => 0);
            Model = ApiDouble.Create<cSapModel>((name, a) =>
            {
                switch (name)
                {
                    case "GetVersion": a[0] = "26"; a[1] = 26d; return 0;
                    case "GetModelFilename": return "test.sdb";
                    case "GetPresentUnits": return Units;
                    case "GetModelIsLocked": return Locked;
                    case "GetPresentCoordSystem": return "Global";
                    case "GetMergeTol": a[0] = .001; return 0;
                    case "get_PointObj": return point;
                    case "get_EditPoint": return edit;
                    case "get_FrameObj": return frame;
                    case "get_CableObj": return cable;
                    case "get_TendonObj": return tendon;
                    case "get_LoadPatterns": return patterns;
                    case "get_View": return view;
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
