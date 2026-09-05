using COT_VCF.Application;
using COT_VCF.Domain;
using COT_VCF.Infrastructure;
using CSiAPIv1;
using System.Runtime.InteropServices;

namespace COT_VCF.SAP2000;

public sealed class SapGateway : ISapGateway
{
    private readonly StaWorker worker = new();
    private cOAPI? api;
    private cSapModel? model;
    public SapGateway() { }
    internal SapGateway(cSapModel model) => this.model = model;
    private cSapModel Model => model ?? throw new InvalidOperationException("Connect to a running SAP2000 model first.");
    private static void Check(int result, string operation, string name = "")
    {
        if (result != 0) throw new InvalidOperationException($"SAP2000 returned code {result} during {operation} {name}. Review the model and refresh before retrying.");
    }
    public Task<ModelInfo> ConnectAsync() => worker.Run(() =>
    {
        // GetObject attaches only. Never call CreateObject or ApplicationStart.
        cHelper helper = new Helper();
        api = helper.GetObject("CSI.SAP2000.API.SapObject");
        if (api is null) throw new InvalidOperationException("No running SAP2000 instance is available. Open SAP2000 and a model, then connect again.");
        model = api.SapModel;
        return ReadInfo();
    });
    private ModelInfo ReadInfo()
    {
        string version = ""; double number = 0;
        Check(Model.GetVersion(ref version, ref number), "GetVersion");
        // SAP2000 26 returns -100 (not implemented) for cross-product GetPresentUnits_2.
        var units = Model.GetPresentUnits();
        var components = units.ToString().Split('_');
        if (components.Length != 3) throw new InvalidOperationException($"Unsupported SAP unit system: {units}.");
        return new(Model.GetModelFilename(true), version, (int)units, components[0], components[1], Model.GetModelIsLocked());
    }
    private void Verify(ModelInfo expected, bool write = false)
    {
        var actual = ReadInfo();
        if (!string.Equals(actual.Path, expected.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The active SAP model has changed. Reconnect and review your configuration.");
        if (actual.UnitsCode != expected.UnitsCode) throw new InvalidOperationException($"SAP units changed from {expected.Units} to {actual.Units}. Restore the previous units before continuing, or start a new configuration.");
        if (write && string.IsNullOrWhiteSpace(actual.Path)) throw new InvalidOperationException("Save the SAP2000 model before applying changes so its identity can be checked.");
        if (write && actual.IsLocked) throw new InvalidOperationException("The SAP2000 model is locked. Unlock it in SAP2000 (which clears analysis results), then retry Apply.");
    }
    public Task<ModelSnapshot> ReadAsync() => worker.Run(() =>
    {
        var info = ReadInfo();
        var points = new Dictionary<string, StructuralPoint>(StringComparer.Ordinal);
        int n = 0; string[] names = [];
        Check(Model.PointObj.GetNameList(ref n, ref names), "PointObj.GetNameList");
        foreach (var name in names.Take(n)) points.Add(name, new(name, ReadPosition(name)));
        var members = new List<StructuralMember>();
        foreach (var kind in Enum.GetValues<MemberKind>())
        {
            n = 0; names = [];
            int rc = kind switch { MemberKind.Frame => Model.FrameObj.GetNameList(ref n, ref names), MemberKind.Cable => Model.CableObj.GetNameList(ref n, ref names), _ => Model.TendonObj.GetNameList(ref n, ref names) };
            Check(rc, $"{kind}.GetNameList");
            foreach (var name in names.Take(n))
            {
                string start = "", end = "";
                rc = kind switch { MemberKind.Frame => Model.FrameObj.GetPoints(name, ref start, ref end), MemberKind.Cable => Model.CableObj.GetPoints(name, ref start, ref end), _ => Model.TendonObj.GetPoints(name, ref start, ref end) };
                Check(rc, $"{kind}.GetPoints", name);
                if (!points.ContainsKey(start) || !points.ContainsKey(end)) throw new InvalidOperationException($"{kind} {name} has missing connectivity. Refresh the model.");
                members.Add(new(name, start, end, kind));
            }
        }
        n = 0; names = [];
        Check(Model.LoadPatterns.GetNameList(ref n, ref names), "LoadPatterns.GetNameList");
        Verify(info);
        return new ModelSnapshot(info, new(points, members), names.Take(n).ToArray());
    });
    private Position ReadPosition(string name)
    {
        double x = 0, y = 0, z = 0;
        Check(Model.PointObj.GetCoordCartesian(name, ref x, ref y, ref z, "Global"), "PointObj.GetCoordCartesian", name);
        var p = new Position(x, y, z);
        if (!p.IsFinite) throw new InvalidOperationException($"Point {name} has non-finite coordinates.");
        return p;
    }
    public Task<IReadOnlyList<ForceSource>> ReadForcesAsync(IReadOnlyList<StructuralMember> members, ModelInfo expected) => worker.Run<IReadOnlyList<ForceSource>>(() =>
    {
        Verify(expected);
        var sources = new List<ForceSource>();
        foreach (var member in members)
        {
            if (member.Kind == MemberKind.Cable)
            {
                int type = 0, segments = 0; double weight = 0, load = 0; bool deformed = false, frames = false; double[] parameters = [];
                Check(Model.CableObj.GetCableData(member.Name, ref type, ref segments, ref weight, ref load, ref deformed, ref frames, ref parameters), "CableObj.GetCableData", member.Name);
                int index = type is 2 or 4 ? 1 : type == 5 ? 2 : 0;
                string source = type is 2 or 4 ? "Shape tension at J-end" : type == 5 ? "Shape horizontal tension" : "Shape tension at I-end";
                sources.Add(new(member.Name, member.Kind, source, parameters.Length > index ? parameters[index] : null, parameters.Length > index ? null : "Cable shape force is unavailable."));
            }
            else if (member.Kind == MemberKind.Tendon)
            {
                int count = 0; string[] names = [], patterns = []; int[] jack = [], types = [];
                double[] values = [], curvature = [], wobble = [], anchorage = [], shortening = [], creep = [], shrinkage = [], relaxation = [];
                Check(Model.TendonObj.GetLoadForceStress(member.Name, ref count, ref names, ref patterns, ref jack, ref types, ref values, ref curvature, ref wobble, ref anchorage, ref shortening, ref creep, ref shrinkage, ref relaxation), "TendonObj.GetLoadForceStress", member.Name);
                double area = 0;
                if (types.Take(count).Contains(1))
                {
                    string prop = "", material = "", notes = "", guid = ""; int option = 0, color = 0;
                    Check(Model.TendonObj.GetProperty(member.Name, ref prop), "TendonObj.GetProperty", member.Name);
                    Check(Model.PropTendon.GetProp(prop, ref material, ref option, ref area, ref color, ref notes, ref guid), "PropTendon.GetProp", prop);
                }
                for (int i = 0; i < count; i++)
                {
                    double? force = types[i] == 0 ? values[i] : types[i] == 1 && area > 0 ? values[i] * area : null;
                    string source = $"{patterns[i]} / jack {jack[i]} / assignment {i + 1}" + (types[i] == 1 ? " (stress × area)" : " (force)");
                    sources.Add(new(member.Name, member.Kind, source, force, force is null ? "Force unavailable: check tendon area and load type." : null));
                }
                if (count == 0) sources.Add(new(member.Name, member.Kind, "No prestress assignment", null, "Assign a tendon force in SAP2000, then refresh."));
            }
        }
        Verify(expected); return sources;
    });
    public Task ApplyLoadsAsync(IReadOnlyList<PointLoad> loads, ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected, true);
        if (loads.GroupBy(l => (l.PointName, l.LoadPattern)).Any(g => g.Count() > 1)) throw new InvalidOperationException("Duplicate point and pattern assignments.");
        int count = 0; string[] patterns = [];
        Check(Model.LoadPatterns.GetNameList(ref count, ref patterns), "LoadPatterns.GetNameList");
        var assignments = new List<(PointLoad Load, double[] Original, bool Existed)>();
        foreach (var load in loads)
        {
            if (!double.IsFinite(load.Fx) || !double.IsFinite(load.Fy) || !double.IsFinite(load.Fz)) throw new InvalidOperationException($"{load.PointName}: force values must be finite.");
            ReadPosition(load.PointName);
            if (!patterns.Contains(load.LoadPattern)) throw new InvalidOperationException($"Load pattern {load.LoadPattern} no longer exists.");
            int n = 0; string[] pts = [], pats = [], systems = []; int[] steps = [];
            double[] f1 = [], f2 = [], f3 = [], m1 = [], m2 = [], m3 = [];
            Check(Model.PointObj.GetLoadForce(load.PointName, ref n, ref pts, ref pats, ref steps, ref systems, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3), "PointObj.GetLoadForce", load.PointName);
            var matches = Enumerable.Range(0, n).Where(i => pats[i] == load.LoadPattern).ToArray();
            if (matches.Length > 1 || matches.Any(i => !systems[i].Equals("Global", StringComparison.OrdinalIgnoreCase) || steps[i] != 0)) throw new InvalidOperationException($"{load.PointName} / {load.LoadPattern}: multiple, local-axis or stepped loads exist. Resolve them in SAP2000 before replacing this assignment.");
            int index = matches.FirstOrDefault(-1);
            assignments.Add((load, index < 0 ? new double[6] : [f1[index], f2[index], f3[index], m1[index], m2[index], m3[index]], index >= 0));
        }
        var journal = RecoveryJournal.Save("point-loads", new { Model = expected, Assignments = assignments.Select(a => new { Proposed = a.Load, Original = a.Original, Existed = a.Existed }).ToArray() });
        var attempted = new List<(PointLoad Load, double[] Original, bool Existed)>();
        try
        {
            foreach (var item in assignments)
            {
                Verify(expected, true); attempted.Add(item);
                double[] values = [item.Load.Fx, item.Load.Fy, item.Load.Fz, item.Original[3], item.Original[4], item.Original[5]];
                Check(Model.PointObj.SetLoadForce(item.Load.PointName, item.Load.LoadPattern, ref values, true, "Global"), "PointObj.SetLoadForce", item.Load.PointName);
            }
        }
        catch (Exception original)
        {
            var failures = new List<string>();
            foreach (var item in attempted.AsEnumerable().Reverse())
            {
                try { Verify(expected, true); var values = item.Original; Check(item.Existed ? Model.PointObj.SetLoadForce(item.Load.PointName, item.Load.LoadPattern, ref values, true, "Global") : Model.PointObj.DeleteLoadForce(item.Load.PointName, item.Load.LoadPattern), "Restore point load", item.Load.PointName); }
                catch (Exception ex) { failures.Add(ex.Message); }
            }
            throw new InvalidOperationException(original.Message + (failures.Count == 0 ? " Attempted load changes were restored." : " Some changes could not be restored: " + string.Join("; ", failures)) + $" Recovery data: {journal}", original);
        }
        return true;
    });
    public Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected, true);
        if (Model.GetPresentCoordSystem() != "Global") throw new InvalidOperationException("Set SAP2000's present coordinate system to Global before applying geometry.");
        if (!expectedGeometry.Points.Keys.Order().SequenceEqual(proposed.Points.Keys.Order())) throw new InvalidOperationException("Geometry updates must preserve all point identities.");
        int pointCount = 0; string[] pointNames = [];
        Check(Model.PointObj.GetNameList(ref pointCount, ref pointNames), "PointObj.GetNameList");
        if (!pointNames.Take(pointCount).Order().SequenceEqual(expectedGeometry.Points.Keys.Order())) throw new InvalidOperationException("SAP point names changed. Refresh before applying geometry.");
        foreach (var kind in Enum.GetValues<MemberKind>())
        {
            int count = 0; string[] names = [];
            Check(kind switch { MemberKind.Frame => Model.FrameObj.GetNameList(ref count, ref names), MemberKind.Cable => Model.CableObj.GetNameList(ref count, ref names), _ => Model.TendonObj.GetNameList(ref count, ref names) }, $"{kind}.GetNameList");
            var expectedMembers = expectedGeometry.Members.Where(m => m.Kind == kind).ToDictionary(m => m.Name);
            if (!names.Take(count).Order().SequenceEqual(expectedMembers.Keys.Order())) throw new InvalidOperationException($"SAP {kind} objects changed. Refresh before applying geometry.");
            foreach (var name in names.Take(count))
            {
                string start = "", end = "";
                Check(kind switch { MemberKind.Frame => Model.FrameObj.GetPoints(name, ref start, ref end), MemberKind.Cable => Model.CableObj.GetPoints(name, ref start, ref end), _ => Model.TendonObj.GetPoints(name, ref start, ref end) }, $"{kind}.GetPoints", name);
                if (start != expectedMembers[name].Start || end != expectedMembers[name].End) throw new InvalidOperationException($"Connectivity changed for {kind} {name}. Refresh before applying geometry.");
            }
        }
        var updates = proposed.Points.Values.Where(p => expectedGeometry.Points.TryGetValue(p.Name, out var old) && old.Position != p.Position).ToArray();
        foreach (var p in expectedGeometry.Points.Values)
            if (!Close(ReadPosition(p.Name), p.Position)) throw new InvalidOperationException($"Point {p.Name} changed in SAP2000. Refresh and recapture the baseline before applying.");
        if (updates.Any(p => !p.Position.IsFinite)) throw new InvalidOperationException("Proposed coordinates must be finite.");
        double mergeTolerance = 0;
        Check(Model.GetMergeTol(ref mergeTolerance), "GetMergeTol");
        if (!double.IsFinite(mergeTolerance) || mergeTolerance < 0) throw new InvalidOperationException("SAP returned an invalid point merge tolerance.");
        bool Near(Position a, Position b) => Math.Abs(a.X - b.X) <= mergeTolerance && Math.Abs(a.Y - b.Y) <= mergeTolerance && Math.Abs(a.Z - b.Z) <= mergeTolerance;
        foreach (var p in updates)
        {
            // Check both final positions and temporary positions during sequential API writes.
            if (proposed.Points.Values.Any(q => q.Name != p.Name && Near(q.Position, p.Position)) || expectedGeometry.Points.Values.Any(q => q.Name != p.Name && Near(q.Position, p.Position)))
                throw new InvalidOperationException($"Moving {p.Name} would overlap another point within the SAP merge tolerance. Review the proposed height.");
        }
        var journal = RecoveryJournal.Save("geometry", new { Model = expected, Before = expectedGeometry, Proposed = proposed });
        var attempted = new List<StructuralPoint>();
        try
        {
            foreach (var p in updates)
            {
                Verify(expected, true);
                if (Model.GetPresentCoordSystem() != "Global") throw new InvalidOperationException("SAP coordinate system changed during the update.");
                attempted.Add(expectedGeometry.Points[p.Name]);
                Check(Model.EditPoint.ChangeCoordinates_1(p.Name, p.Position.X, p.Position.Y, p.Position.Z, true), "EditPoint.ChangeCoordinates_1", p.Name);
                if (!Close(ReadPosition(p.Name), p.Position)) throw new InvalidOperationException($"Point {p.Name} did not reach the requested coordinates.");
            }
        }
        catch (Exception original)
        {
            var failures = new List<string>();
            foreach (var p in attempted.AsEnumerable().Reverse())
            {
                try { Verify(expected, true); if (Model.GetPresentCoordSystem() != "Global") throw new InvalidOperationException("Restore requires SAP's Global coordinate system."); Check(Model.EditPoint.ChangeCoordinates_1(p.Name, p.Position.X, p.Position.Y, p.Position.Z, true), "Restore coordinates", p.Name); }
                catch (Exception ex) { failures.Add(ex.Message); }
            }
            throw new InvalidOperationException(original.Message + (failures.Count == 0 ? " Attempted coordinate changes were restored." : " Some coordinates could not be restored: " + string.Join("; ", failures)) + $" Recovery data: {journal}", original);
        }
        Check(Model.View.RefreshView(0, false), "View.RefreshView");
        return true;
    });
    private static bool Close(Position a, Position b) => Math.Abs(a.X - b.X) <= 1e-7 && Math.Abs(a.Y - b.Y) <= 1e-7 && Math.Abs(a.Z - b.Z) <= 1e-7;
    public void Dispose() { worker.Dispose(); /* SAP2000 remains running; no ApplicationExit. */ }
}
