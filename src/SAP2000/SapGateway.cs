using COT_VCF.Application;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Infrastructure;
using CSiAPIv1;
using System.IO;
using System.Runtime.InteropServices;

namespace COT_VCF.SAP2000;

public sealed class SapGateway : ISapGateway
{
    private readonly StaWorker worker;
    private readonly bool ownsWorker;
    private cOAPI? api;
    private cSapModel? model;
    public SapGateway()
    {
        worker = new();
        ownsWorker = true;
    }
    internal SapGateway(cSapModel model)
    {
        worker = new();
        ownsWorker = true;
        this.model = model;
    }
    internal SapGateway(StaWorker worker, cOAPI api, cSapModel model)
    {
        this.worker = worker;
        ownsWorker = false;
        this.api = api;
        this.model = model;
    }
    private cSapModel Model => model ?? throw new InvalidOperationException("Connect to a running SAP2000 model first.");
    private static void Check(int result, string operation, string name = "")
    {
        if (result != 0) throw new InvalidOperationException($"SAP2000 returned code {result} during {operation} {name}. Review the model and refresh before retrying.");
    }
    public Task<ModelInfo> ConnectAsync() => worker.Run(() =>
    {
        // GetObject attaches only. Never call CreateObject or ApplicationStart.
        cHelper? helper = null;
        cOAPI? nextApi = null;
        cSapModel? nextModel = null;
        var previousApi = api;
        var previousModel = model;
        try
        {
            helper = new Helper();
            nextApi = helper.GetObject("CSI.SAP2000.API.SapObject");
            if (nextApi is null) throw new InvalidOperationException("No running SAP2000 instance is available. Open SAP2000 and a model, then connect again.");
            nextModel = nextApi.SapModel ?? throw new InvalidOperationException("The running SAP2000 instance did not expose an active model.");
            api = nextApi;
            model = nextModel;
            var info = ReadInfo();
            ReleaseIfReplaced(previousModel, nextModel);
            ReleaseIfReplaced(previousApi, nextApi);
            return info;
        }
        catch
        {
            api = previousApi;
            model = previousModel;
            ReleaseIfReplaced(nextModel, previousModel);
            ReleaseIfReplaced(nextApi, previousApi);
            throw;
        }
        finally
        {
            ReleaseComReference(helper, final: true);
        }
    });
    public Task<ModelInfo> OpenModelAsync(string modelPath) => worker.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(modelPath)) throw new InvalidOperationException("A baseline model path is required.");
        var fullPath = Path.GetFullPath(modelPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The baseline SAP2000 model no longer exists.", fullPath);
        // SAP2000 26 returns a nonzero code from File.OpenFile whenever it replaces a model that is
        // already open — the open still succeeds. Only a fresh instance with no model loaded
        // returns 0. The active model read back below is the authoritative result, so the return
        // code is kept for diagnostics instead of being treated as a failure.
        var openCode = Model.File.OpenFile(fullPath);
        var info = ReadInfo();
        if (!SamePath(info.Path, fullPath))
            throw new InvalidOperationException(
                $"SAP2000 did not open the requested model (File.OpenFile returned {openCode}). Expected {fullPath}; active {info.Path}.");
        return info;
    });
    public Task<ModelInfo> SaveAsAsync(string targetPath, ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected);
        if (string.IsNullOrWhiteSpace(targetPath)) throw new InvalidOperationException("A child model path is required.");
        var fullPath = Path.GetFullPath(targetPath);
        if (File.Exists(fullPath)) throw new InvalidOperationException($"Existing output will not be overwritten: {fullPath}");
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"The candidate directory does not exist: {parent}");
        Check(Model.File.Save(fullPath), "File.Save (SaveAs)", fullPath);
        var child = ReadInfo();
        if (!SamePath(child.Path, fullPath))
            throw new InvalidOperationException($"SAP2000 did not activate the requested child model. Expected {fullPath}; active {child.Path}.");
        if (child.UnitsCode != expected.UnitsCode)
            throw new InvalidOperationException($"SAP units changed while creating the child model ({expected.Units} to {child.Units}).");
        if (child.IsLocked)
        {
            Check(Model.SetModelIsLocked(false), "SetModelIsLocked", fullPath);
            child = ReadInfo();
        }
        return child;
    });
    public Task SaveAsync(ModelInfo expected) => worker.Run(() =>
    {
        // Saving analysis results is valid while SAP2000 has the model locked.
        Verify(expected);
        Check(Model.File.Save(expected.Path), "File.Save", expected.Path);
        Verify(expected);
        return true;
    });
    public Task RunAnalysisAsync(ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected, true);
        Check(Model.Analyze.RunAnalysis(), "Analyze.RunAnalysis", expected.Path);
        Verify(expected);
        // A real analysis run locks the model. If it comes back unlocked, RunAnalysis executed no
        // load cases (none are set to run) and produced no results — fail instead of saving an empty child.
        if (!ReadInfo().IsLocked)
            throw new InvalidOperationException(
                $"Analyze.RunAnalysis executed no load cases for {expected.Path} and produced no results. " +
                "Set at least one load case to run in the baseline model (Analyze > Set Load Cases to Run), then retry.");
        return true;
    });
    public Task<ModelInfo> GetCurrentModelInfoAsync() => worker.Run(ReadInfo);
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
        if (!SamePath(actual.Path, expected.Path)) throw new InvalidOperationException("The active SAP model has changed. Reconnect and review your configuration.");
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
    public Task<IReadOnlyList<PointLoad>> ReadPointLoadsAsync(ModelInfo expected) => worker.Run<IReadOnlyList<PointLoad>>(() =>
    {
        Verify(expected);
        int pointCount = 0; string[] pointNames = [];
        Check(Model.PointObj.GetNameList(ref pointCount, ref pointNames), "PointObj.GetNameList");
        var loads = new List<PointLoad>();
        foreach (var pointName in pointNames.Take(pointCount))
        {
            int count = 0; string[] points = [], patterns = [], systems = []; int[] steps = [];
            double[] f1 = [], f2 = [], f3 = [], m1 = [], m2 = [], m3 = [];
            Check(Model.PointObj.GetLoadForce(pointName, ref count, ref points, ref patterns, ref steps, ref systems,
                ref f1, ref f2, ref f3, ref m1, ref m2, ref m3), "PointObj.GetLoadForce", pointName);
            for (int i = 0; i < count; i++)
                if (steps[i] == 0 && systems[i].Equals("Global", StringComparison.OrdinalIgnoreCase))
                    loads.Add(new PointLoad(points[i], patterns[i], f1[i], f2[i], f3[i]));
        }
        Verify(expected);
        return loads;
    });
    public Task<Position> GetPointPositionAsync(string pointName, ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected);
        var position = ReadPosition(pointName);
        Verify(expected);
        return position;
    });

    public Task<IReadOnlyList<JointDisplacement>> GetJointDisplacementsAsync(
        IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected) =>
        worker.Run<IReadOnlyList<JointDisplacement>>(() =>
    {
        Verify(expected);
        var results = new List<JointDisplacement>(pointNames.Count);

        if (!ReadInfo().IsLocked)
        {
            foreach (var name in pointNames)
                results.Add(new(name, 0, 0, 0, null, ResultValueStatus.AnalysisNotRun, "The model holds no analysis results."));
            return results;
        }

        // SAP joint displacements are in the present database length unit. The study session runs in kN/m/C,
        // so U3 is metres → millimetres. Assert it rather than assume.
        if (!Model.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Deflection extraction expects kN, m units; the model is in {ReadInfo().Units}.");

        Check(Model.Results.Setup.DeselectAllCasesAndCombosForOutput(), "Results.Setup.DeselectAllCasesAndCombosForOutput");
        var select = isCombo
            ? Model.Results.Setup.SetComboSelectedForOutput(outputCase)
            : Model.Results.Setup.SetCaseSelectedForOutput(outputCase);
        if (select != 0)
        {
            foreach (var name in pointNames)
                results.Add(new(name, 0, 0, 0, null, ResultValueStatus.CaseNotFound,
                    $"Output {(isCombo ? "combo" : "case")} '{outputCase}' is not in the model."));
            return results;
        }

        foreach (var name in pointNames)
        {
            double x = 0, y = 0, z = 0;
            var coordRc = Model.PointObj.GetCoordCartesian(name, ref x, ref y, ref z, "Global");
            if (coordRc != 0)
            {
                results.Add(new(name, 0, 0, 0, null, ResultValueStatus.PointNotFound, $"Point '{name}' is not in the model."));
                continue;
            }

            int n = 0;
            string[] obj = [], elm = [], loadCase = [], stepType = [];
            double[] stepNum = [], u1 = [], u2 = [], u3 = [], r1 = [], r2 = [], r3 = [];
            var rc = Model.Results.JointDispl(name, eItemTypeElm.ObjectElm, ref n, ref obj, ref elm,
                ref loadCase, ref stepType, ref stepNum, ref u1, ref u2, ref u3, ref r1, ref r2, ref r3);
            if (rc != 0)
                results.Add(new(name, x, y, z, null, ResultValueStatus.ApiError, $"Results.JointDispl returned {rc} for '{name}'."));
            else if (n <= 0)
                results.Add(new(name, x, y, z, null, ResultValueStatus.NoResult, $"No displacement row for '{name}' under '{outputCase}'."));
            else
            {
                // one case selected → take the governing (max |U3|) row so an enveloping combo is handled too
                var idx = 0;
                for (int i = 1; i < n; i++) if (Math.Abs(u3[i]) > Math.Abs(u3[idx])) idx = i;
                var uz = u3[idx] * 1000.0;
                results.Add(double.IsFinite(uz)
                    ? new(name, x, y, z, uz, ResultValueStatus.Valid)
                    : new(name, x, y, z, null, ResultValueStatus.ApiError, $"Non-finite Uz for '{name}'."));
            }
        }

        Verify(expected);
        return results;
    });
    public Task<IReadOnlyList<CableForceRead>> GetCableForcesAsync(
        IReadOnlyList<string> cableNames, string outputCase, bool isCombo, ModelInfo expected) =>
        worker.Run<IReadOnlyList<CableForceRead>>(() =>
    {
        Verify(expected);
        var results = new List<CableForceRead>(cableNames.Count);

        if (!ReadInfo().IsLocked)
        {
            foreach (var name in cableNames)
                results.Add(new(name, null, ResultValueStatus.AnalysisNotRun, "The model holds no analysis results."));
            return results;
        }
        if (!Model.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cable force extraction expects kN, m units; the model is in {ReadInfo().Units}.");

        Check(Model.Results.Setup.DeselectAllCasesAndCombosForOutput(), "Results.Setup.DeselectAllCasesAndCombosForOutput");
        var select = isCombo
            ? Model.Results.Setup.SetComboSelectedForOutput(outputCase)
            : Model.Results.Setup.SetCaseSelectedForOutput(outputCase);
        if (select != 0)
        {
            foreach (var name in cableNames)
                results.Add(new(name, null, ResultValueStatus.CaseNotFound,
                    $"Output {(isCombo ? "combo" : "case")} '{outputCase}' is not in the model."));
            return results;
        }

        foreach (var name in cableNames)
        {
            // A cable object resolves to one or more analysis (frame) elements. Read FrameForce for each and
            // take the governing axial force (max |P|) — mirrors the Cable Axial Forces VBA module.
            int elmCount = 0; string[] elmNames = []; double[] relI = [], relJ = [];
            var elmRc = Model.CableObj.GetElm(name, ref elmCount, ref elmNames, ref relI, ref relJ);
            if (elmRc != 0)
            {
                results.Add(new(name, null, ResultValueStatus.PointNotFound, $"CableObj.GetElm returned {elmRc} for cable '{name}'."));
                continue;
            }
            if (elmCount <= 0 || elmNames.Length == 0)
            {
                results.Add(new(name, null, ResultValueStatus.NoResult, $"No analysis elements for cable '{name}'."));
                continue;
            }

            double? governing = null;
            var apiFailed = false;
            for (int e = 0; e < elmCount && e < elmNames.Length; e++)
            {
                if (string.IsNullOrWhiteSpace(elmNames[e])) continue;
                int n = 0;
                string[] obj = [], elm = [], loadCase = [], stepType = [];
                double[] objSt = [], elmSt = [], stepNum = [], p = [], v2 = [], v3 = [], tq = [], m2 = [], m3 = [];
                var rc = Model.Results.FrameForce(elmNames[e], eItemTypeElm.Element, ref n, ref obj, ref objSt,
                    ref elm, ref elmSt, ref loadCase, ref stepType, ref stepNum, ref p, ref v2, ref v3, ref tq, ref m2, ref m3);
                if (rc != 0) { apiFailed = true; continue; }
                for (int i = 0; i < n && i < p.Length; i++)
                {
                    var mag = Math.Abs(p[i]);
                    if (double.IsFinite(mag) && (governing is null || mag > governing)) governing = mag;
                }
            }

            results.Add(governing is { } g
                ? new(name, g, ResultValueStatus.Valid)
                : new(name, null, apiFailed ? ResultValueStatus.ApiError : ResultValueStatus.NoResult,
                    apiFailed ? $"Results.FrameForce failed for every element of cable '{name}'."
                              : $"No axial-force row for cable '{name}' under '{outputCase}'."));
        }

        Verify(expected);
        return results;
    });
    public Task<IReadOnlyList<PointLoad>> GetPointLoadsAsync(string pointName, ModelInfo expected) => worker.Run<IReadOnlyList<PointLoad>>(() =>
    {
        Verify(expected);
        ReadPosition(pointName); // Gives a focused, contextual CSI error when the point no longer exists.
        var loads = ReadPointForceAssignments(pointName)
            .Where(item => item.Step == 0 && item.CoordinateSystem.Equals("Global", StringComparison.OrdinalIgnoreCase))
            .Select(item => new PointLoad(pointName, item.LoadPattern, item.Values[0], item.Values[1], item.Values[2]))
            .ToArray();
        Verify(expected);
        return loads;
    });
    public Task<IReadOnlyList<CableTargetForce>> GetCableTargetForcesAsync(string cableName, ModelInfo expected) => worker.Run<IReadOnlyList<CableTargetForce>>(() =>
    {
        Verify(expected);
        int count = 0; string[] cableNames = [];
        Check(Model.CableObj.GetNameList(ref count, ref cableNames), "CableObj.GetNameList");
        var actualCable = cableNames.Take(count).FirstOrDefault(name => name.Equals(cableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Cable {cableName} no longer exists in the model.");
        var assignments = ReadCableTargetForces(actualCable)
            .Select(item => new CableTargetForce(actualCable, item.LoadPattern, item.TargetForce, item.RelativeDistance))
            .ToArray();
        Verify(expected);
        return assignments;
    });
    public Task<IReadOnlyList<CableSection>> GetCableSectionsAsync(IReadOnlyList<string> cableNames, ModelInfo expected) =>
        worker.Run<IReadOnlyList<CableSection>>(() =>
    {
        Verify(expected);
        var results = new List<CableSection>(cableNames.Count);
        // Cable section area is length²; the study session runs in kN/m/C, so the value is m². Assert it.
        if (!Model.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cable section retrieval expects kN, m units; the model is in {ReadInfo().Units}.");

        int total = 0; string[] all = [];
        Check(Model.CableObj.GetNameList(ref total, ref all), "CableObj.GetNameList");
        var known = all.Take(total).ToArray();

        foreach (var name in cableNames)
        {
            var actual = known.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (actual is null) { results.Add(new(name, 0, Problem: $"Cable '{name}' is not in the model.")); continue; }

            string prop = "";
            var propRc = Model.CableObj.GetProperty(actual, ref prop);
            if (propRc != 0 || string.IsNullOrWhiteSpace(prop))
            { results.Add(new(actual, 0, Problem: $"CableObj.GetProperty returned {propRc} for '{actual}'.")); continue; }

            string mat = "", notes = "", guid = ""; double area = 0; int color = 0;
            var areaRc = Model.PropCable.GetProp(prop, ref mat, ref area, ref color, ref notes, ref guid);
            results.Add(areaRc == 0 && double.IsFinite(area) && area > 0
                ? new(actual, area, prop)
                : new(actual, 0, prop, $"PropCable.GetProp returned {areaRc} (area {area}) for section '{prop}'."));
        }
        Verify(expected);
        return results;
    });
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
        if (loads.GroupBy(l => (l.PointName, l.LoadPattern), new PointPatternComparer()).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Duplicate point and load-pattern assignments.");
        int count = 0; string[] patterns = [];
        Check(Model.LoadPatterns.GetNameList(ref count, ref patterns), "LoadPatterns.GetNameList");
        var assignments = new List<(PointLoad Load, double[] Original, bool Existed)>();
        foreach (var load in loads)
        {
            if (!double.IsFinite(load.Fx) || !double.IsFinite(load.Fy) || !double.IsFinite(load.Fz)) throw new InvalidOperationException($"{load.PointName}: force values must be finite.");
            ReadPosition(load.PointName);
            var actualPattern = patterns.Take(count).FirstOrDefault(pattern => pattern.Equals(load.LoadPattern, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Load pattern {load.LoadPattern} no longer exists.");
            var normalized = load with { LoadPattern = actualPattern };
            var matches = ReadPointForceAssignments(normalized.PointName)
                .Where(item => item.LoadPattern.Equals(actualPattern, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 1 || matches.Any(item => !item.CoordinateSystem.Equals("Global", StringComparison.OrdinalIgnoreCase) || item.Step != 0))
                throw new InvalidOperationException($"{normalized.PointName} / {actualPattern}: multiple, local-axis or stepped loads exist. Resolve them in SAP2000 before replacing this assignment.");
            var match = matches.SingleOrDefault();
            assignments.Add((normalized, match?.Values ?? new double[6], match is not null));
        }
        var journal = RecoveryJournal.Save("point-loads", new { Model = expected, Assignments = assignments.Select(a => new { Proposed = a.Load, Original = a.Original, Existed = a.Existed }).ToArray() });
        var attempted = new List<(PointLoad Load, double[] Original, bool Existed)>();
        try
        {
            foreach (var item in assignments)
            {
                Verify(expected, true); attempted.Add(item);
                double[] values = [item.Load.Fx, item.Load.Fy, item.Load.Fz, item.Original[3], item.Original[4], item.Original[5]];
                Check(Model.PointObj.SetLoadForce(item.Load.PointName, item.Load.LoadPattern, ref values, true, "Global", eItemType.Objects),
                    "PointObj.SetLoadForce", $"{item.Load.PointName} / {item.Load.LoadPattern} / Fz={item.Load.Fz:0.###}");
                var written = ReadPointForceAssignments(item.Load.PointName)
                    .Where(candidate => candidate.LoadPattern.Equals(item.Load.LoadPattern, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (written.Length != 1 || written[0].Step != 0 ||
                    !written[0].CoordinateSystem.Equals("Global", StringComparison.OrdinalIgnoreCase) ||
                    !SameValues(written[0].Values, values))
                    throw new InvalidOperationException($"SAP2000 did not retain the requested joint load: {item.Load.PointName} / {item.Load.LoadPattern} / Fz={item.Load.Fz:0.###} {expected.ForceUnit}.");
            }
        }
        catch (Exception original)
        {
            var failures = new List<string>();
            foreach (var item in attempted.AsEnumerable().Reverse())
            {
                try { Verify(expected, true); var values = item.Original; Check(item.Existed ? Model.PointObj.SetLoadForce(item.Load.PointName, item.Load.LoadPattern, ref values, true, "Global", eItemType.Objects) : Model.PointObj.DeleteLoadForce(item.Load.PointName, item.Load.LoadPattern, eItemType.Objects), "Restore point load", item.Load.PointName); }
                catch (Exception ex) { failures.Add(ex.Message); }
            }
            throw new InvalidOperationException(original.Message + (failures.Count == 0 ? " Attempted load changes were restored." : " Some changes could not be restored: " + string.Join("; ", failures)) + $" Recovery data: {journal}", original);
        }
        return true;
    });
    public Task MovePointsAsync(GeometryModel expectedGeometry, GeometryModel proposed, ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected, true);
        if (!Model.GetPresentCoordSystem().Equals("Global", StringComparison.OrdinalIgnoreCase))
        {
            Check(Model.SetPresentCoordSystem("Global"), "SetPresentCoordSystem", "Global");
            if (!Model.GetPresentCoordSystem().Equals("Global", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SAP2000 did not retain Global as the present coordinate system required for geometry updates.");
        }
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
                if (!Model.GetPresentCoordSystem().Equals("Global", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SAP coordinate system changed during the update.");
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
                try { Verify(expected, true); if (!Model.GetPresentCoordSystem().Equals("Global", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Restore requires SAP's Global coordinate system."); Check(Model.EditPoint.ChangeCoordinates_1(p.Name, p.Position.X, p.Position.Y, p.Position.Z, true), "Restore coordinates", p.Name); }
                catch (Exception ex) { failures.Add(ex.Message); }
            }
            throw new InvalidOperationException(original.Message + (failures.Count == 0 ? " Attempted coordinate changes were restored." : " Some coordinates could not be restored: " + string.Join("; ", failures)) + $" Recovery data: {journal}", original);
        }
        Check(Model.View.RefreshView(0, false), "View.RefreshView");
        return true;
    });
    private static bool Close(Position a, Position b) => Math.Abs(a.X - b.X) <= 1e-7 && Math.Abs(a.Y - b.Y) <= 1e-7 && Math.Abs(a.Z - b.Z) <= 1e-7;
    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private const double PretensionRelDist = 0.5; // Relative distance along the cable for the target-force assignment.

    public Task SetCableTargetForceAsync(string cableName, string loadPattern, double targetForce, ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected, true);
        if (string.IsNullOrWhiteSpace(cableName)) throw new InvalidOperationException("A cable object name is required.");
        if (string.IsNullOrWhiteSpace(loadPattern)) throw new InvalidOperationException($"{cableName}: a target-force load pattern is required.");
        if (!double.IsFinite(targetForce)) throw new InvalidOperationException("Target force must be a finite number.");
        int count = 0; string[] patterns = [];
        Check(Model.LoadPatterns.GetNameList(ref count, ref patterns), "LoadPatterns.GetNameList");
        var actualPattern = patterns.Take(count).FirstOrDefault(pattern => pattern.Equals(loadPattern, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Load pattern {loadPattern} does not exist. Cable target force assignments require a load pattern, not an analysis load case.");
        int cableTotal = 0; string[] cableNames = [];
        Check(Model.CableObj.GetNameList(ref cableTotal, ref cableNames), "CableObj.GetNameList");
        var actualCable = cableNames.Take(cableTotal).FirstOrDefault(name => name.Equals(cableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Cable {cableName} no longer exists in the model.");
        var original = ReadCableTargetForces(actualCable)
            .Where(item => item.LoadPattern.Equals(actualPattern, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var journal = RecoveryJournal.Save("cable-target-force", new
        {
            Model = expected,
            Cable = actualCable,
            LoadPattern = actualPattern,
            ProposedTargetForce = targetForce,
            RelativeDistance = PretensionRelDist,
            Original = original
        });
        try
        {
            if (original.Length > 0)
                Check(Model.CableObj.DeleteLoadTargetForce(actualCable, actualPattern, eItemType.Objects),
                    "CableObj.DeleteLoadTargetForce", $"{actualCable} / {actualPattern}");
            double p = targetForce, rd = PretensionRelDist;
            Check(Model.CableObj.SetLoadTargetForce(actualCable, actualPattern, ref p, ref rd, eItemType.Objects),
                "CableObj.SetLoadTargetForce", $"{actualCable} / {actualPattern} / P={targetForce:0.###}");
            var written = ReadCableTargetForces(actualCable)
                .Where(item => item.LoadPattern.Equals(actualPattern, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (written.Length != 1 || !Close(written[0].TargetForce, targetForce) || !Close(written[0].RelativeDistance, PretensionRelDist))
                throw new InvalidOperationException($"SAP2000 did not retain the requested cable target force: {actualCable} / {actualPattern} / P={targetForce:0.###} {expected.ForceUnit} at RD={PretensionRelDist:0.###}.");
        }
        catch (Exception originalError)
        {
            var failures = new List<string>();
            try
            {
                var current = ReadCableTargetForces(actualCable)
                    .Any(item => item.LoadPattern.Equals(actualPattern, StringComparison.OrdinalIgnoreCase));
                if (current)
                    Check(Model.CableObj.DeleteLoadTargetForce(actualCable, actualPattern, eItemType.Objects),
                        "Restore cable target force", $"{actualCable} / {actualPattern}");
                foreach (var prior in original)
                {
                    double p = prior.TargetForce, rd = prior.RelativeDistance;
                    Check(Model.CableObj.SetLoadTargetForce(actualCable, actualPattern, ref p, ref rd, eItemType.Objects),
                        "Restore cable target force", $"{actualCable} / {actualPattern}");
                }
            }
            catch (Exception ex) { failures.Add(ex.Message); }
            throw new InvalidOperationException(originalError.Message +
                (failures.Count == 0 ? " The previous target-force assignment was restored." : " The previous target-force assignment could not be fully restored: " + string.Join("; ", failures)) +
                $" Recovery data: {journal}", originalError);
        }
        return true;
    });

    private sealed record PointForceAssignment(string LoadPattern, int Step, string CoordinateSystem, double[] Values);
    private sealed record CableTargetForceAssignment(string LoadPattern, double TargetForce, double RelativeDistance);

    private IReadOnlyList<PointForceAssignment> ReadPointForceAssignments(string pointName)
    {
        int count = 0; string[] points = [], patterns = [], systems = []; int[] steps = [];
        double[] f1 = [], f2 = [], f3 = [], m1 = [], m2 = [], m3 = [];
        Check(Model.PointObj.GetLoadForce(pointName, ref count, ref points, ref patterns, ref steps, ref systems,
            ref f1, ref f2, ref f3, ref m1, ref m2, ref m3, eItemType.Objects), "PointObj.GetLoadForce", pointName);
        return Enumerable.Range(0, count)
            .Select(index => new PointForceAssignment(patterns[index], steps[index], systems[index],
                [f1[index], f2[index], f3[index], m1[index], m2[index], m3[index]]))
            .ToArray();
    }

    private IReadOnlyList<CableTargetForceAssignment> ReadCableTargetForces(string cableName)
    {
        int count = 0; string[] cables = [], patterns = []; double[] forces = [], distances = [];
        Check(Model.CableObj.GetLoadTargetForce(cableName, ref count, ref cables, ref patterns, ref forces, ref distances, eItemType.Objects),
            "CableObj.GetLoadTargetForce", cableName);
        return Enumerable.Range(0, count)
            .Select(index => new CableTargetForceAssignment(patterns[index], forces[index], distances[index]))
            .ToArray();
    }

    private static bool SameValues(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
        left.Count == right.Count && Enumerable.Range(0, left.Count).All(index => Close(left[index], right[index]));

    private static bool Close(double left, double right) =>
        Math.Abs(left - right) <= 1e-8 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

    private sealed class PointPatternComparer : IEqualityComparer<(string PointName, string LoadPattern)>
    {
        public bool Equals((string PointName, string LoadPattern) left, (string PointName, string LoadPattern) right) =>
            left.PointName.Equals(right.PointName, StringComparison.OrdinalIgnoreCase) &&
            left.LoadPattern.Equals(right.LoadPattern, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string PointName, string LoadPattern) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.PointName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.LoadPattern));
    }

    public Task<IReadOnlyList<string>> GetLoadCaseNamesAsync(ModelInfo expected) => worker.Run<IReadOnlyList<string>>(() =>
    {
        Verify(expected);
        int n = 0; string[] names = [];
        Check(Model.LoadCases.GetNameList(ref n, ref names), "LoadCases.GetNameList");
        return names.Take(n).ToArray();
    });

    public Task<IReadOnlyList<SapGroupSnapshot>> ReadOutputGroupsAsync(IReadOnlyList<string> groupNames, ModelInfo expected) =>
        worker.Run<IReadOnlyList<SapGroupSnapshot>>(() =>
        {
            Verify(expected);
            var requested = groupNames.Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var snapshots = ReadOutputGroups(requested);
            Verify(expected);
            return snapshots;
        });

    public Task ReplaceOutputGroupsAsync(IReadOnlyList<OutputSapGroupDefinition> groups, ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected, true);
        var requested = groups?.ToArray() ?? [];
        if (requested.Length == 0) throw new InvalidOperationException("At least one output group is required.");
        if (requested.Any(group => string.IsNullOrWhiteSpace(group.Name) ||
            !group.Name.StartsWith(OutputGroupNaming.Prefix, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Every managed output group must use the {OutputGroupNaming.Prefix} prefix.");
        if (requested.GroupBy(group => group.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Duplicate output group names are not allowed.");

        int n = 0; string[] names = [];
        Check(Model.PointObj.GetNameList(ref n, ref names), "PointObj.GetNameList");
        var points = names.Take(n).ToHashSet(StringComparer.Ordinal);
        n = 0; names = [];
        Check(Model.FrameObj.GetNameList(ref n, ref names), "FrameObj.GetNameList");
        var frames = names.Take(n).ToHashSet(StringComparer.Ordinal);
        n = 0; names = [];
        Check(Model.CableObj.GetNameList(ref n, ref names), "CableObj.GetNameList");
        var cables = names.Take(n).ToHashSet(StringComparer.Ordinal);

        foreach (var group in requested)
        {
            var missing = group.ObjectNames.Distinct(StringComparer.Ordinal).Where(name => group.ObjectKind switch
            {
                OutputGroupObjectKind.Point => !points.Contains(name),
                OutputGroupObjectKind.Frame => !frames.Contains(name),
                OutputGroupObjectKind.Cable => !cables.Contains(name),
                _ => true
            }).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"{group.Name}: missing {group.ObjectKind} object(s): {string.Join(", ", missing)}.");
        }

        var prior = ReadOutputGroups(requested.Select(group => group.Name).ToArray());
        try
        {
            foreach (var group in requested)
            {
                Verify(expected, true);
                DeleteGroupIfExists(group.Name);
                Check(Model.GroupDef.SetGroup(group.Name), "GroupDef.SetGroup", group.Name);
                foreach (var name in group.ObjectNames.Distinct(StringComparer.Ordinal))
                {
                    var rc = group.ObjectKind switch
                    {
                        OutputGroupObjectKind.Point => Model.PointObj.SetGroupAssign(name, group.Name, false, eItemType.Objects),
                        OutputGroupObjectKind.Frame => Model.FrameObj.SetGroupAssign(name, group.Name, false, eItemType.Objects),
                        OutputGroupObjectKind.Cable => Model.CableObj.SetGroupAssign(name, group.Name, false, eItemType.Objects),
                        _ => -1
                    };
                    Check(rc, $"{group.ObjectKind}.SetGroupAssign", $"{name} -> {group.Name}");
                }
            }

            var written = ReadOutputGroups(requested.Select(group => group.Name).ToArray());
            foreach (var group in requested)
            {
                var snapshot = written.First(item => item.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase));
                var expectedNames = group.ObjectNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                var actualNames = snapshot.Names(group.ObjectKind).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                if (!snapshot.Exists || !expectedNames.SequenceEqual(actualNames, StringComparer.Ordinal))
                    throw new InvalidOperationException($"{group.Name}: SAP2000 group membership did not match the requested output definition.");
                var total = snapshot.PointNames.Count + snapshot.FrameNames.Count + snapshot.CableNames.Count;
                if (total != actualNames.Length)
                    throw new InvalidOperationException($"{group.Name}: SAP2000 retained an unexpected object type in the output group.");
            }
        }
        catch (Exception original)
        {
            var restoreFailures = new List<string>();
            foreach (var snapshot in prior)
            {
                try
                {
                    Verify(expected, true);
                    DeleteGroupIfExists(snapshot.Name);
                    if (!snapshot.Exists) continue;
                    Check(Model.GroupDef.SetGroup(snapshot.Name), "Restore GroupDef.SetGroup", snapshot.Name);
                    foreach (var name in snapshot.PointNames)
                        Check(Model.PointObj.SetGroupAssign(name, snapshot.Name, false, eItemType.Objects), "Restore PointObj.SetGroupAssign", name);
                    foreach (var name in snapshot.FrameNames)
                        Check(Model.FrameObj.SetGroupAssign(name, snapshot.Name, false, eItemType.Objects), "Restore FrameObj.SetGroupAssign", name);
                    foreach (var name in snapshot.CableNames)
                        Check(Model.CableObj.SetGroupAssign(name, snapshot.Name, false, eItemType.Objects), "Restore CableObj.SetGroupAssign", name);
                }
                catch (Exception ex) { restoreFailures.Add($"{snapshot.Name}: {ex.Message}"); }
            }
            throw new InvalidOperationException(original.Message +
                (restoreFailures.Count == 0 ? " Previous output-group state was restored." :
                 " Some output groups could not be restored: " + string.Join("; ", restoreFailures)), original);
        }
        return true;
    });

    private IReadOnlyList<SapGroupSnapshot> ReadOutputGroups(IReadOnlyList<string> groupNames)
    {
        int count = 0; string[] existingNames = [];
        Check(Model.GroupDef.GetNameList(ref count, ref existingNames), "GroupDef.GetNameList");
        var existing = existingNames.Take(count).ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);
        var result = new List<SapGroupSnapshot>(groupNames.Count);
        foreach (var requestedName in groupNames)
        {
            if (!existing.TryGetValue(requestedName, out var actualName))
            {
                result.Add(new(requestedName, false, [], [], []));
                continue;
            }
            int n = 0; int[] types = []; string[] names = [];
            Check(Model.GroupDef.GetAssignments(actualName, ref n, ref types, ref names), "GroupDef.GetAssignments", actualName);
            var points = new List<string>();
            var frames = new List<string>();
            var cables = new List<string>();
            for (int i = 0; i < n; i++)
            {
                switch (types[i])
                {
                    case 1: points.Add(names[i]); break;
                    case 2: frames.Add(names[i]); break;
                    case 3: cables.Add(names[i]); break;
                    default:
                        throw new InvalidOperationException($"{actualName}: unsupported SAP object type {types[i]} is assigned to this COTVCF output group.");
                }
            }
            result.Add(new(actualName, true, points, frames, cables));
        }
        return result;
    }

    private void DeleteGroupIfExists(string groupName)
    {
        int count = 0; string[] names = [];
        Check(Model.GroupDef.GetNameList(ref count, ref names), "GroupDef.GetNameList");
        var actual = names.Take(count).FirstOrDefault(name => name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
        if (actual is not null) Check(Model.GroupDef.Delete(actual), "GroupDef.Delete", actual);
    }

    internal Task ReleaseComReferencesAsync() => worker.Run(() =>
    {
        var currentModel = model;
        var currentApi = api;
        model = null;
        api = null;
        ReleaseComReference(currentModel, final: true);
        ReleaseComReference(currentApi, final: true);
        return true;
    });

    private static void ReleaseIfReplaced(object? oldReference, object? replacement)
    {
        if (oldReference is null || ReferenceEquals(oldReference, replacement)) return;
        ReleaseComReference(oldReference, final: false);
    }

    private static void ReleaseComReference(object? reference, bool final)
    {
        if (reference is null || !Marshal.IsComObject(reference)) return;
        if (final) Marshal.FinalReleaseComObject(reference);
        else Marshal.ReleaseComObject(reference);
    }

    public void Dispose()
    {
        if (!ownsWorker) { model = null; api = null; return; }
        try { ReleaseComReferencesAsync().GetAwaiter().GetResult(); }
        finally { worker.Dispose(); } // Controller SAP2000 remains running; no ApplicationExit.
    }
}
