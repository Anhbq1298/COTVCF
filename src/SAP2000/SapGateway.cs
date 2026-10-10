using COT_VCF.Application;
using COT_VCF.Application.Quantities;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;
using COT_VCF.Geometry;
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
    public Task<IReadOnlyList<FrameForceRead>> GetFrameForcesAsync(
        IReadOnlyList<string> frameNames, string outputCase, bool isCombo, ModelInfo expected) =>
        worker.Run<IReadOnlyList<FrameForceRead>>(() =>
    {
        Verify(expected);
        var results = new List<FrameForceRead>(frameNames.Count);
        FrameForceRead Failed(string name, ResultValueStatus status, string warning) =>
            new(name, [], status, warning);

        if (!ReadInfo().IsLocked)
        {
            foreach (var name in frameNames)
                results.Add(Failed(name, ResultValueStatus.AnalysisNotRun, "The model holds no analysis results."));
            return results;
        }
        if (!Model.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Frame-force extraction expects kN, m units; the model is in {ReadInfo().Units}.");

        Check(Model.Results.Setup.DeselectAllCasesAndCombosForOutput(), "Results.Setup.DeselectAllCasesAndCombosForOutput");
        var select = isCombo
            ? Model.Results.Setup.SetComboSelectedForOutput(outputCase)
            : Model.Results.Setup.SetCaseSelectedForOutput(outputCase);
        if (select != 0)
        {
            foreach (var name in frameNames)
                results.Add(Failed(name, ResultValueStatus.CaseNotFound,
                    $"Output {(isCombo ? "combo" : "case")} '{outputCase}' is not in the model."));
            return results;
        }

        foreach (var name in frameNames)
        {
            string pointI = "", pointJ = "";
            if (Model.FrameObj.GetPoints(name, ref pointI, ref pointJ) != 0)
            {
                results.Add(Failed(name, ResultValueStatus.FrameNotFound, $"Frame '{name}' is not in the model."));
                continue;
            }

            int n = 0;
            string[] obj = [], elm = [], caseNames = [], stepType = [];
            double[] objSt = [], elmSt = [], stepNum = [], p = [], v2 = [], v3 = [], tq = [], m2 = [], m3 = [];
            var rc = Model.Results.FrameForce(name, eItemTypeElm.ObjectElm, ref n, ref obj, ref objSt,
                ref elm, ref elmSt, ref caseNames, ref stepType, ref stepNum, ref p, ref v2, ref v3, ref tq, ref m2, ref m3);
            if (rc != 0)
            {
                results.Add(Failed(name, ResultValueStatus.ApiError, $"Results.FrameForce returned {rc} for frame '{name}'."));
                continue;
            }
            if (n <= 0)
            {
                results.Add(Failed(name, ResultValueStatus.NoResult, $"No frame-force rows for '{name}' under '{outputCase}'."));
                continue;
            }

            var limit = new[] { n, obj.Length, objSt.Length, elm.Length, elmSt.Length, caseNames.Length,
                stepType.Length, stepNum.Length, p.Length, v2.Length, v3.Length, tq.Length, m2.Length, m3.Length }.Min();
            var stations = Enumerable.Range(0, Math.Max(0, limit))
                .Where(i => double.IsFinite(objSt[i]) && double.IsFinite(elmSt[i]) &&
                    double.IsFinite(stepNum[i]) && double.IsFinite(p[i]) && double.IsFinite(v2[i]) &&
                    double.IsFinite(v3[i]) && double.IsFinite(tq[i]) && double.IsFinite(m2[i]) && double.IsFinite(m3[i]))
                .Select(i => new FrameForceStationRead(
                    string.IsNullOrWhiteSpace(obj[i]) ? name : obj[i], elm[i], objSt[i], elmSt[i],
                    string.IsNullOrWhiteSpace(caseNames[i]) ? outputCase : caseNames[i], stepType[i], stepNum[i],
                    p[i], v2[i], v3[i], tq[i], m2[i], m3[i]))
                .OrderBy(row => row.ObjectStationM)
                .ThenBy(row => row.ElementName, StringComparer.Ordinal)
                .ThenBy(row => row.ElementStationM)
                .ThenBy(row => row.StepType, StringComparer.Ordinal)
                .ThenBy(row => row.StepNumber)
                .ToArray();
            results.Add(stations.Length == 0
                ? Failed(name, ResultValueStatus.NoResult, $"No finite frame-force rows for '{name}' under '{outputCase}'.")
                : new FrameForceRead(name, stations, ResultValueStatus.Valid));
        }

        Verify(expected);
        return results;
    });
    public Task<IReadOnlyList<JointReactionRead>> GetJointReactionsAsync(
        IReadOnlyList<string> pointNames, string outputCase, bool isCombo, ModelInfo expected) =>
        worker.Run<IReadOnlyList<JointReactionRead>>(() =>
    {
        Verify(expected);
        var results = new List<JointReactionRead>(pointNames.Count);
        JointReactionRead Failed(string name, double x, double y, double z, ResultValueStatus status, string warning) =>
            new(name, x, y, z, [], status, warning);

        if (!ReadInfo().IsLocked)
        {
            foreach (var name in pointNames)
                results.Add(Failed(name, 0, 0, 0, ResultValueStatus.AnalysisNotRun, "The model holds no analysis results."));
            return results;
        }
        if (!Model.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Support-reaction extraction expects kN, m units; the model is in {ReadInfo().Units}.");

        Check(Model.Results.Setup.DeselectAllCasesAndCombosForOutput(), "Results.Setup.DeselectAllCasesAndCombosForOutput");
        var select = isCombo
            ? Model.Results.Setup.SetComboSelectedForOutput(outputCase)
            : Model.Results.Setup.SetCaseSelectedForOutput(outputCase);
        if (select != 0)
        {
            foreach (var name in pointNames)
                results.Add(Failed(name, 0, 0, 0, ResultValueStatus.CaseNotFound,
                    $"Output {(isCombo ? "combo" : "case")} '{outputCase}' is not in the model."));
            return results;
        }

        foreach (var name in pointNames)
        {
            double x = 0, y = 0, z = 0;
            if (Model.PointObj.GetCoordCartesian(name, ref x, ref y, ref z, "Global") != 0)
            {
                results.Add(Failed(name, 0, 0, 0, ResultValueStatus.PointNotFound, $"Point '{name}' is not in the model."));
                continue;
            }

            int n = 0;
            string[] obj = [], elm = [], caseNames = [], stepType = [];
            double[] stepNum = [], f1 = [], f2 = [], f3 = [], m1 = [], m2 = [], m3 = [];
            var rc = Model.Results.JointReact(name, eItemTypeElm.ObjectElm, ref n, ref obj, ref elm,
                ref caseNames, ref stepType, ref stepNum, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3);
            if (rc != 0)
            {
                results.Add(Failed(name, x, y, z, ResultValueStatus.ApiError, $"Results.JointReact returned {rc} for point '{name}'."));
                continue;
            }
            if (n <= 0)
            {
                results.Add(Failed(name, x, y, z, ResultValueStatus.NoResult, $"No support-reaction rows for '{name}' under '{outputCase}'."));
                continue;
            }

            var limit = new[] { n, caseNames.Length, stepType.Length, stepNum.Length,
                f1.Length, f2.Length, f3.Length, m1.Length, m2.Length, m3.Length }.Min();
            var rows = Enumerable.Range(0, Math.Max(0, limit))
                .Where(i => double.IsFinite(stepNum[i]) && double.IsFinite(f1[i]) && double.IsFinite(f2[i]) &&
                    double.IsFinite(f3[i]) && double.IsFinite(m1[i]) && double.IsFinite(m2[i]) && double.IsFinite(m3[i]))
                .Select(i => new JointReactionRowRead(
                    string.IsNullOrWhiteSpace(caseNames[i]) ? outputCase : caseNames[i], stepType[i], stepNum[i],
                    f1[i], f2[i], f3[i], m1[i], m2[i], m3[i]))
                .OrderBy(row => row.StepType, StringComparer.Ordinal)
                .ThenBy(row => row.StepNumber)
                .ToArray();
            results.Add(rows.Length == 0
                ? Failed(name, x, y, z, ResultValueStatus.NoResult, $"No finite support-reaction rows for '{name}' under '{outputCase}'.")
                : new JointReactionRead(name, x, y, z, rows, ResultValueStatus.Valid));
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
    public Task<IReadOnlyDictionary<string, FemSectionProperties>> GetFemSectionPropertiesAsync(
        IReadOnlyList<StructuralMember> members, ModelInfo expected) => worker.Run<IReadOnlyDictionary<string, FemSectionProperties>>(() =>
    {
        Verify(expected);
        if (!Model.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Internal FEM property reads require kN/m units; the model is in {ReadInfo().Units}. Set SAP2000 to kN, m, C and refresh.");

        var properties = new Dictionary<string, FemSectionProperties>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member.Kind == MemberKind.Frame)
            {
                string section = "", autoSection = "";
                Check(Model.FrameObj.GetSection(member.Name, ref section, ref autoSection), "FrameObj.GetSection", member.Name);
                if (string.IsNullOrWhiteSpace(section)) throw new InvalidOperationException($"Frame '{member.Name}' has no assigned SAP section.");

                double area = 0, as2 = 0, as3 = 0, torsion = 0, i22 = 0, i33 = 0;
                double s22 = 0, s33 = 0, z22 = 0, z33 = 0, r22 = 0, r33 = 0;
                Check(Model.PropFrame.GetSectProps(section, ref area, ref as2, ref as3, ref torsion,
                    ref i22, ref i33, ref s22, ref s33, ref z22, ref z33, ref r22, ref r33), "PropFrame.GetSectProps", section);

                string material = "";
                Check(Model.PropFrame.GetMaterial(section, ref material), "PropFrame.GetMaterial", section);
                string materialOverride = "";
                Check(Model.FrameObj.GetMaterialOverwrite(member.Name, ref materialOverride), "FrameObj.GetMaterialOverwrite", member.Name);
                material = ResolveEffectiveMaterial(material, materialOverride, member.Name, section);
                var modulus = ReadElasticModulus(material, member.Name);

                double[] modifiers = new double[8];
                Check(Model.FrameObj.GetModifiers(member.Name, ref modifiers), "FrameObj.GetModifiers", member.Name);
                if (modifiers.Length < 6) throw new InvalidOperationException($"SAP returned incomplete stiffness modifiers for frame '{member.Name}'.");
                var grossArea = area;
                var shape = ReadSectionShape(section, area, i33, i22);
                area *= modifiers[0];
                i22 *= modifiers[4];
                i33 *= modifiers[5];

                double localAngle = 0; bool advancedAxes = false;
                Check(Model.FrameObj.GetLocalAxes(member.Name, ref localAngle, ref advancedAxes), "FrameObj.GetLocalAxes", member.Name);
                if (advancedAxes) throw new InvalidOperationException($"Frame '{member.Name}' uses advanced local axes, which the planar FEM solver does not support.");
                var radians = localAngle * Math.PI / 180;
                var cosine = Math.Cos(radians);
                var sine = Math.Sin(radians);
                var inPlaneInertia = i33 * cosine * cosine + i22 * sine * sine;

                bool[] releasedI = new bool[6], releasedJ = new bool[6];
                double[] partialI = new double[6], partialJ = new double[6];
                Check(Model.FrameObj.GetReleases(member.Name, ref releasedI, ref releasedJ, ref partialI, ref partialJ), "FrameObj.GetReleases", member.Name);
                if (releasedI.Length < 6 || releasedJ.Length < 6)
                    throw new InvalidOperationException($"SAP returned incomplete end-release data for frame '{member.Name}'.");
                var axesAligned = Math.Abs(cosine * sine) < 1e-7;
                if (!axesAligned && (releasedI[1] || releasedI[2] || releasedI[4] || releasedI[5] ||
                                     releasedJ[1] || releasedJ[2] || releasedJ[4] || releasedJ[5]))
                    throw new InvalidOperationException($"Frame '{member.Name}' has a rotated local axis and an in-plane shear/moment release; this is not supported by the 2D FEM solver.");
                if (releasedI[0] || releasedJ[0])
                    throw new InvalidOperationException($"Frame '{member.Name}' has an axial end release, which the 2D FEM solver does not support.");
                var inPlaneMomentIndex = Math.Abs(cosine) >= Math.Abs(sine) ? 5 : 4;
                var inPlaneShearIndex = Math.Abs(cosine) >= Math.Abs(sine) ? 1 : 2;
                if (releasedI[inPlaneShearIndex] || releasedJ[inPlaneShearIndex])
                    throw new InvalidOperationException($"Frame '{member.Name}' has an in-plane shear release, which the Euler-Bernoulli FEM solver does not support.");

                var (unitWeight, unitMass) = ReadWeightAndMass(material);
                properties[member.Name] = new FemSectionProperties(member.Name, area, modulus, inPlaneInertia,
                    releasedI[inPlaneMomentIndex], releasedJ[inPlaneMomentIndex], section, material,
                    localAngle, unitWeight, unitMass, grossArea, modifiers.ToArray(), shape);
            }
            else if (member.Kind == MemberKind.Cable)
            {
                string section = "";
                Check(Model.CableObj.GetProperty(member.Name, ref section), "CableObj.GetProperty", member.Name);
                if (string.IsNullOrWhiteSpace(section)) throw new InvalidOperationException($"Cable '{member.Name}' has no assigned SAP section.");
                string material = "", notes = "", guid = ""; double area = 0; int color = 0;
                Check(Model.PropCable.GetProp(section, ref material, ref area, ref color, ref notes, ref guid), "PropCable.GetProp", section);
                string materialOverride = "";
                Check(Model.CableObj.GetMaterialOverwrite(member.Name, ref materialOverride), "CableObj.GetMaterialOverwrite", member.Name);
                material = ResolveEffectiveMaterial(material, materialOverride, member.Name, section);
                // Cable modifiers (SAP order): area, mass, weight.
                double[] cableModifiers = new double[3];
                Check(Model.CableObj.GetModifiers(member.Name, ref cableModifiers), "CableObj.GetModifiers", member.Name);
                if (cableModifiers.Length < 3) cableModifiers = [1, 1, 1];
                var (unitWeight, unitMass) = ReadWeightAndMass(material);
                properties[member.Name] = new FemSectionProperties(member.Name, area * cableModifiers[0],
                    ReadElasticModulus(material, member.Name), null, SectionName: section, MaterialName: material,
                    UnitWeightKNPerM3: unitWeight, UnitMassTPerM3: unitMass, GrossAreaM2: area, Modifiers: cableModifiers.ToArray());
            }
        }
        Verify(expected);
        return properties;
    });

    public Task<SapFemLoading> GetFemLoadingAsync(IReadOnlyList<StructuralMember> members, IReadOnlyDictionary<string, double> localAnglesDeg,
        ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected);
        if (!Model.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Internal FEM load reads require kN/m units; the model is in {ReadInfo().Units}. Set SAP2000 to kN, m, C and refresh.");
        var warnings = new List<string>();
        void Warn(string message) { if (warnings.Count < 200) warnings.Add(message); }

        // Load patterns.
        int count = 0; string[] names = [];
        Check(Model.LoadPatterns.GetNameList(ref count, ref names), "LoadPatterns.GetNameList");
        var patterns = names.Take(count).Select(name =>
        {
            double selfWeight = 0; var type = eLoadPatternType.Other;
            Check(Model.LoadPatterns.GetSelfWTMultiplier(name, ref selfWeight), "LoadPatterns.GetSelfWTMultiplier", name);
            Check(Model.LoadPatterns.GetLoadType(name, ref type), "LoadPatterns.GetLoadType", name);
            return new SapLoadPattern(name, type.ToString(), selfWeight);
        }).ToArray();

        static FemLoadDirection? Direction(int dir) => dir switch
        {
            1 => FemLoadDirection.Local1, 2 => FemLoadDirection.Local2, 4 => FemLoadDirection.GlobalX, 6 => FemLoadDirection.GlobalZ,
            7 => FemLoadDirection.ProjectedX, 9 => FemLoadDirection.ProjectedZ, 10 => FemLoadDirection.Gravity, 11 => FemLoadDirection.ProjectedGravity,
            _ => null // local 3 and global/projected Y act out of the XZ plane
        };
        bool UsableCsys(string csys, int dir) => dir <= 3 ? csys.Equals("Local", StringComparison.OrdinalIgnoreCase)
            : csys.Equals("Global", StringComparison.OrdinalIgnoreCase);

        // Joint loads on every analysed joint.
        var jointLoads = new List<SapPatternJointLoad>();
        foreach (var point in members.SelectMany(member => new[] { member.Start, member.End }).Distinct(StringComparer.Ordinal))
        {
            int n = 0; string[] pts = [], pats = [], csys = []; int[] steps = [];
            double[] f1 = [], f2 = [], f3 = [], m1 = [], m2 = [], m3 = [];
            if (Model.PointObj.GetLoadForce(point, ref n, ref pts, ref pats, ref steps, ref csys, ref f1, ref f2, ref f3, ref m1, ref m2, ref m3, eItemType.Objects) != 0) continue;
            for (var i = 0; i < n; i++)
            {
                if (!csys[i].Equals("Global", StringComparison.OrdinalIgnoreCase)) { Warn($"Joint load on {point} ({pats[i]}) uses coordinate system '{csys[i]}' — ignored."); continue; }
                if (Math.Abs(f2[i]) > 1e-9 || Math.Abs(m1[i]) > 1e-9 || Math.Abs(m3[i]) > 1e-9)
                    Warn($"Joint load on {point} ({pats[i]}) has out-of-plane components (F2/M1/M3) — ignored; F1, F3 and M2 used.");
                jointLoads.Add(new(pats[i], new FemJointLoad(point, f1[i], f3[i], m2[i])));
            }
        }

        // Member loads on analysed frames and cables.
        var memberLoads = new List<SapPatternMemberLoad>();
        var pointLoads = new List<SapPatternMemberPointLoad>();
        var gravityLoads = new List<SapPatternGravityLoad>();
        foreach (var member in members)
        {
            var angle = localAnglesDeg.GetValueOrDefault(member.Name);
            if (member.Kind == MemberKind.Frame)
            {
                int n = 0; string[] frames = [], pats = [], csys = []; int[] types = [], dirs = [];
                double[] rd1 = [], rd2 = [], d1 = [], d2 = [], v1 = [], v2 = [];
                if (Model.FrameObj.GetLoadDistributed(member.Name, ref n, ref frames, ref pats, ref types, ref csys, ref dirs, ref rd1, ref rd2, ref d1, ref d2, ref v1, ref v2, eItemType.Objects) == 0)
                    for (var i = 0; i < n; i++)
                    {
                        var direction = Direction(dirs[i]);
                        if (types[i] != 1 || direction is null || !UsableCsys(csys[i], dirs[i]))
                        { Warn($"Distributed load on {member.Name} ({pats[i]}, dir {dirs[i]}, {csys[i]}) is a moment or acts out of the XZ plane — ignored."); continue; }
                        if (direction == FemLoadDirection.Local2 && Math.Abs(Math.Sin(angle * Math.PI / 180)) > 1e-6)
                            Warn($"Local-2 load on {member.Name} ({pats[i]}) with local axis angle {angle:0.#}° — only its in-plane part is used.");
                        memberLoads.Add(new(pats[i], new FemMemberLoad(member.Name, direction.Value, rd1[i], rd2[i], v1[i], v2[i])));
                    }
                n = 0; frames = []; pats = []; csys = []; types = []; dirs = [];
                double[] rel = [], dist = [], val = [];
                if (Model.FrameObj.GetLoadPoint(member.Name, ref n, ref frames, ref pats, ref types, ref csys, ref dirs, ref rel, ref dist, ref val, eItemType.Objects) == 0)
                    for (var i = 0; i < n; i++)
                    {
                        var direction = Direction(dirs[i]);
                        if (types[i] != 1 || direction is null || !UsableCsys(csys[i], dirs[i]))
                        { Warn($"Point load on {member.Name} ({pats[i]}, dir {dirs[i]}, {csys[i]}) is a moment or acts out of the XZ plane — ignored."); continue; }
                        pointLoads.Add(new(pats[i], new FemMemberPointLoad(member.Name, direction.Value, rel[i], val[i])));
                    }
                n = 0; frames = []; pats = []; csys = [];
                double[] gx = [], gy = [], gz = [];
                if (Model.FrameObj.GetLoadGravity(member.Name, ref n, ref frames, ref pats, ref csys, ref gx, ref gy, ref gz, eItemType.Objects) == 0)
                    for (var i = 0; i < n; i++)
                    {
                        if (!csys[i].Equals("Global", StringComparison.OrdinalIgnoreCase)) { Warn($"Gravity load on {member.Name} ({pats[i]}) in '{csys[i]}' — ignored."); continue; }
                        gravityLoads.Add(new(pats[i], member.Name, gx[i], gz[i]));
                    }
            }
            else if (member.Kind == MemberKind.Cable)
            {
                int n = 0; string[] cables = [], pats = [], csys = []; int[] types = [], dirs = []; double[] values = [];
                if (Model.CableObj.GetLoadDistributed(member.Name, ref n, ref cables, ref pats, ref types, ref csys, ref dirs, ref values, eItemType.Objects) == 0)
                    for (var i = 0; i < n; i++)
                    {
                        var direction = Direction(dirs[i]);
                        if (types[i] != 1 || direction is null || !UsableCsys(csys[i], dirs[i]))
                        { Warn($"Distributed load on cable {member.Name} ({pats[i]}, dir {dirs[i]}) is a moment or acts out of the XZ plane — ignored."); continue; }
                        memberLoads.Add(new(pats[i], new FemMemberLoad(member.Name, direction.Value, 0, 1, values[i], values[i])));
                    }
                n = 0; cables = []; pats = []; csys = [];
                double[] gx = [], gy = [], gz = [];
                if (Model.CableObj.GetLoadGravity(member.Name, ref n, ref cables, ref pats, ref csys, ref gx, ref gy, ref gz, eItemType.Objects) == 0)
                    for (var i = 0; i < n; i++)
                        if (csys[i].Equals("Global", StringComparison.OrdinalIgnoreCase)) gravityLoads.Add(new(pats[i], member.Name, gx[i], gz[i]));
                        else Warn($"Gravity load on cable {member.Name} ({pats[i]}) in '{csys[i]}' — ignored.");
            }
        }

        // Static load cases.
        count = 0; names = [];
        Check(Model.LoadCases.GetNameList(ref count, ref names), "LoadCases.GetNameList");
        var cases = new List<SapLoadCase>();
        foreach (var name in names.Take(count))
        {
            var type = eLoadCaseType.LinearStatic; int subType = 0;
            Check(Model.LoadCases.GetTypeOAPI(name, ref type, ref subType), "LoadCases.GetTypeOAPI", name);
            int n = 0; string[] loadTypes = [], loadNames = []; double[] factors = [];
            string initial = "";
            if (type == eLoadCaseType.LinearStatic)
            {
                Check(Model.LoadCases.StaticLinear.GetLoads(name, ref n, ref loadTypes, ref loadNames, ref factors), "StaticLinear.GetLoads", name);
                Model.LoadCases.StaticLinear.GetInitialCase(name, ref initial);
            }
            else if (type == eLoadCaseType.NonlinearStatic)
            {
                Check(Model.LoadCases.StaticNonlinear.GetLoads(name, ref n, ref loadTypes, ref loadNames, ref factors), "StaticNonlinear.GetLoads", name);
                Model.LoadCases.StaticNonlinear.GetInitialCase(name, ref initial);
            }
            else
            {
                cases.Add(new(name, type.ToString(), false, [], null, "Not a static case — not used by the internal FEM."));
                continue;
            }
            var loads = new List<(string, double)>();
            var note = "";
            for (var i = 0; i < n; i++)
                if (loadTypes[i].Equals("Load", StringComparison.OrdinalIgnoreCase)) loads.Add((loadNames[i], factors[i]));
                else note = "Acceleration loads are not used.";
            var initialCase = FemLoadPlan.IsNoInitialCase(initial) ? null : initial;
            if (type == eLoadCaseType.LinearStatic && initialCase is not null)
                note = (note + " Continues from the stiffness of '" + initialCase + "' in SAP; here its loads are included.").Trim();
            cases.Add(new(name, type == eLoadCaseType.LinearStatic ? "Linear Static" : "Nonlinear Static", true, loads, initialCase, note.Length == 0 ? null : note));
        }

        // Response combinations.
        count = 0; names = [];
        Check(Model.RespCombo.GetNameList(ref count, ref names), "RespCombo.GetNameList");
        var combinations = new List<SapLoadCombination>();
        foreach (var name in names.Take(count))
        {
            int comboType = 0;
            Check(Model.RespCombo.GetTypeOAPI(name, ref comboType), "RespCombo.GetTypeOAPI", name);
            int n = 0; eCNameType[] itemTypes = []; string[] itemNames = []; double[] factors = [];
            Check(Model.RespCombo.GetCaseList(name, ref n, ref itemTypes, ref itemNames, ref factors), "RespCombo.GetCaseList", name);
            var typeName = comboType switch { 0 => "Linear Additive", 1 => "Envelope", 2 => "Absolute Additive", 3 => "SRSS", 4 => "Range Additive", _ => $"Type {comboType}" };
            combinations.Add(new(name, typeName, comboType == 0,
                Enumerable.Range(0, n).Select(i => (itemNames[i], itemTypes[i] == eCNameType.LoadCombo, factors[i])).ToArray(),
                comboType == 0 ? null : "Only linear additive combinations are evaluated."));
        }

        Verify(expected);
        return new SapFemLoading(patterns, jointLoads, memberLoads, pointLoads, gravityLoads, cases, combinations, warnings);
    });

    /// <summary>
    /// Section outline for the 3D view, from the type-specific SAP getters (dimensions in m). Types without a
    /// dimension getter (Section Designer, filled/encased, built-up, variable…) or a failed read fall back to the
    /// solid rectangle with the same A, I33, I22. Read-only.
    /// </summary>
    private FemSectionShape? ReadSectionShape(string section, double area, double i33, double i22)
    {
        var fallback = FemSectionShape.EquivalentRectangle(area, i33, i22);
        var type = eFramePropType.General;
        if (Model.PropFrame.GetTypeOAPI(section, ref type) != 0) return fallback;
        string file = "", mat = "", notes = "", guid = ""; int color = 0;
        double t3 = 0, t2 = 0, tf = 0, tw = 0, t2b = 0, tfb = 0;
        int rc;
        FemSectionShape? shape = null;
        switch (type)
        {
            case eFramePropType.Rectangular:
                rc = Model.PropFrame.GetRectangle(section, ref file, ref mat, ref t3, ref t2, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.Rectangle, t3, t2);
                break;
            case eFramePropType.Box:
                rc = Model.PropFrame.GetTube(section, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.Box, t3, t2, tf, tw);
                break;
            case eFramePropType.ConcreteBox:
                rc = Model.PropFrame.GetConcreteBox(section, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.Box, t3, t2, tf, tw);
                break;
            case eFramePropType.I:
                rc = Model.PropFrame.GetISection(section, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref t2b, ref tfb, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.I, t3, t2, tf, tw, t2b, tfb);
                break;
            case eFramePropType.Channel:
                rc = Model.PropFrame.GetChannel(section, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.Channel, t3, t2, tf, tw);
                break;
            case eFramePropType.T:
                rc = Model.PropFrame.GetTee(section, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.Tee, t3, t2, tf, tw);
                break;
            case eFramePropType.Angle:
                rc = Model.PropFrame.GetAngle(section, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.Angle, t3, t2, tf, tw);
                break;
            case eFramePropType.Circle:
                rc = Model.PropFrame.GetCircle(section, ref file, ref mat, ref t3, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.Circle, t3, t3);
                break;
            case eFramePropType.Pipe:
                rc = Model.PropFrame.GetPipe(section, ref file, ref mat, ref t3, ref tw, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.Pipe, t3, t3, 0, tw);
                break;
            case eFramePropType.ConcretePipe:
                rc = Model.PropFrame.GetConcretePipe(section, ref file, ref mat, ref t3, ref tw, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.Pipe, t3, t3, 0, tw);
                break;
            case eFramePropType.General:
            {
                double a = 0, as2 = 0, as3 = 0, j = 0, gi22 = 0, gi33 = 0, s22 = 0, s33 = 0, z22 = 0, z33 = 0, r22 = 0, r33 = 0;
                rc = Model.PropFrame.GetGeneral(section, ref file, ref mat, ref t3, ref t2, ref a, ref as2, ref as3, ref j,
                    ref gi22, ref gi33, ref s22, ref s33, ref z22, ref z33, ref r22, ref r33, ref color, ref notes, ref guid);
                if (rc == 0) shape = new(SectionShapeKind.Rectangle, t3, t2);
                break;
            }
        }
        return shape is { Depth: > 0, Width: > 0 } && double.IsFinite(shape.Depth) && double.IsFinite(shape.Width) ? shape : fallback;
    }

    /// <summary>
    /// Define → Materials → Get From SAP2000. Signatures checked against CSiAPIv1 of SAP2000 27: PropMaterial.GetNameList,
    /// GetMaterial (type, colour, notes), GetWeightAndMass, GetMPIsotropic / GetMPUniaxial (tendon, rebar), and per type
    /// GetOConcrete_2 (f'c, expected f'c, lightweight, shear reduction), GetOSteel_1 / GetORebar_1 (Fy, Fu, expected),
    /// GetOTendon_1 / GetOColdFormed (Fy, Fu). There is no getter for the grade; it comes from the "Material Properties
    /// 01 - General" display table. Read-only, kN-m units required (like the FEM reads).
    /// </summary>
    public Task<IReadOnlyList<MaterialProperty>> GetMaterialsAsync(ModelInfo expected) => worker.Run<IReadOnlyList<MaterialProperty>>(() =>
    {
        Verify(expected);
        if (!Model.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Material reads require kN/m units; the model is in {ReadInfo().Units}. Set SAP2000 to kN, m, C and refresh.");
        int count = 0; string[] names = [];
        Check(Model.PropMaterial.GetNameList(ref count, ref names), "PropMaterial.GetNameList");

        var grades = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] requested = [], fields = [], data = []; int version = 0, records = 0;
        if (Model.DatabaseTables.GetTableForDisplayArray("Material Properties 01 - General", ref requested, "All", ref version, ref fields, ref records, ref data) == 0)
        {
            var name = Array.FindIndex(fields, field => field.Equals("Material", StringComparison.OrdinalIgnoreCase));
            var grade = Array.FindIndex(fields, field => field.Equals("Grade", StringComparison.OrdinalIgnoreCase));
            if (name >= 0 && grade >= 0)
                for (var record = 0; record < records && (record + 1) * fields.Length <= data.Length; record++)
                    grades[data[record * fields.Length + name]] = data[record * fields.Length + grade] ?? "";
        }

        var materials = new List<MaterialProperty>();
        foreach (var material in names.Take(count))
        {
            var type = eMatType.Steel; int color = 0; string notes = "", guid = "";
            Check(Model.PropMaterial.GetMaterial(material, ref type, ref color, ref notes, ref guid), "PropMaterial.GetMaterial", material);
            var (weight, _) = ReadWeightAndMass(material);
            var kind = type switch
            {
                eMatType.Steel => MaterialKind.Steel, eMatType.Concrete => MaterialKind.Concrete, eMatType.NoDesign => MaterialKind.NoDesign,
                eMatType.Aluminum => MaterialKind.Aluminum, eMatType.ColdFormed => MaterialKind.ColdFormed, eMatType.Rebar => MaterialKind.Rebar,
                eMatType.Tendon => MaterialKind.Tendon, eMatType.Masonry => MaterialKind.Masonry, _ => MaterialKind.Other,
            };
            double e = 0, u = 0, a = 0, g = 0;
            if (kind is MaterialKind.Tendon or MaterialKind.Rebar) Model.PropMaterial.GetMPUniaxial(material, ref e, ref a, 0);
            else Model.PropMaterial.GetMPIsotropic(material, ref e, ref u, ref a, ref g, 0);
            var result = new MaterialProperty
            {
                Name = material, Kind = kind, Notes = notes ?? "", Grade = grades.GetValueOrDefault(material, ""),
                Color = ((color & 0xFF) << 16) | (color & 0xFF00) | ((color >> 16) & 0xFF),   // SAP COLORREF 0x00BBGGRR → 0xRRGGBB
                WeightPerVolume = weight, ElasticModulus = e, PoissonRatio = u, ThermalCoefficient = a, Source = "SAP2000",
            };
            int ssType = 0, ssHys = 0; double s1 = 0, s2 = 0, s3 = 0, slope = 0;
            switch (kind)
            {
                case MaterialKind.Concrete:
                {
                    double fc = 0, efc = 0, fcs = 0, friction = 0, dilatation = 0; bool light = false;
                    if (Model.PropMaterial.GetOConcrete_2(material, ref fc, ref efc, ref light, ref fcs, ref ssType, ref ssHys, ref s1, ref s2, ref slope, ref friction, ref dilatation, 0) == 0)
                        result = result with { Fc = fc, ExpectedFc = efc, IsLightweight = light, ShearStrengthReductionFactor = light ? fcs : null };
                    else if (Model.PropMaterial.GetOConcrete_1(material, ref fc, ref light, ref fcs, ref ssType, ref ssHys, ref s1, ref s2, ref slope, ref friction, ref dilatation, 0) == 0)
                        result = result with { Fc = fc, IsLightweight = light, ShearStrengthReductionFactor = light ? fcs : null };
                    break;
                }
                case MaterialKind.Steel:
                {
                    double fy = 0, fu = 0, efy = 0, efu = 0;
                    if (Model.PropMaterial.GetOSteel_1(material, ref fy, ref fu, ref efy, ref efu, ref ssType, ref ssHys, ref s1, ref s2, ref s3, ref slope, 0) == 0)
                        result = result with { Fy = fy, Fu = fu, ExpectedFy = efy, ExpectedFu = efu };
                    break;
                }
                case MaterialKind.Rebar:
                {
                    double fy = 0, fu = 0, efy = 0, efu = 0; bool caltrans = false;
                    if (Model.PropMaterial.GetORebar_1(material, ref fy, ref fu, ref efy, ref efu, ref ssType, ref ssHys, ref s1, ref s2, ref slope, ref caltrans, 0) == 0)
                        result = result with { Fy = fy, Fu = fu, ExpectedFy = efy, ExpectedFu = efu };
                    break;
                }
                case MaterialKind.Tendon:
                {
                    double fy = 0, fu = 0;
                    if (Model.PropMaterial.GetOTendon_1(material, ref fy, ref fu, ref ssType, ref ssHys, ref slope, 0) == 0)
                        result = result with { Fy = fy, Fu = fu };
                    break;
                }
                case MaterialKind.ColdFormed:
                {
                    double fy = 0, fu = 0;
                    if (Model.PropMaterial.GetOColdFormed(material, ref fy, ref fu, ref ssHys, 0) == 0) result = result with { Fy = fy, Fu = fu };
                    break;
                }
            }
            materials.Add(result);
        }
        Verify(expected);
        return materials;
    });

    /// <summary>
    /// Define → Section Properties → Get From SAP2000. Signatures checked against CSiAPIv1 of SAP2000 27: PropFrame.GetNameList
    /// (type 0 = all), GetTypeOAPI, GetRectangle / GetTube_1 / GetISection_1 / GetPipe / GetCircle / GetChannel_2 / GetTee_1 /
    /// GetAngle_1 for the dimensions, GetSectProps (any shape), GetMaterial, GetModifiers; PropCable.GetNameList / GetProp /
    /// GetModifiers; PropTendon.GetNameList / GetProp. Shapes other than the eight drawn ones (Section Designer, filled tube,
    /// concrete box, …) are returned as General with the properties SAP2000 reports. Read-only, kN-m units required.
    /// </summary>
    public Task<SectionLibrary> GetSectionsAsync(ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected);
        if (!Model.GetPresentUnits().ToString().StartsWith("kN_m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Section reads require kN/m units; the model is in {ReadInfo().Units}. Set SAP2000 to kN, m, C and refresh.");
        static int Rgb(int bgr) => ((bgr & 0xFF) << 16) | (bgr & 0xFF00) | ((bgr >> 16) & 0xFF);

        var frames = new List<FrameSectionProperty>();
        int count = 0; string[] names = [];
        Check(Model.PropFrame.GetNameList(ref count, ref names, 0), "PropFrame.GetNameList");
        foreach (var name in names.Take(count))
        {
            var type = eFramePropType.I;
            Check(Model.PropFrame.GetTypeOAPI(name, ref type), "PropFrame.GetTypeOAPI", name);
            string file = "", mat = "", notes = "", guid = ""; int color = 0;
            double t3 = 0, t2 = 0, tf = 0, tw = 0, t2b = 0, tfb = 0, radius = 0; bool mirror = false;
            var shape = type switch
            {
                eFramePropType.Rectangular when Model.PropFrame.GetRectangle(name, ref file, ref mat, ref t3, ref t2, ref color, ref notes, ref guid) == 0 => FrameShape.Rectangle,
                eFramePropType.Box when Model.PropFrame.GetTube_1(name, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref radius, ref color, ref notes, ref guid) == 0 => FrameShape.Box,
                eFramePropType.I when Model.PropFrame.GetISection_1(name, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref t2b, ref tfb, ref radius, ref color, ref notes, ref guid) == 0 => FrameShape.I,
                eFramePropType.Pipe when Model.PropFrame.GetPipe(name, ref file, ref mat, ref t3, ref tw, ref color, ref notes, ref guid) == 0 => FrameShape.Pipe,
                eFramePropType.Circle when Model.PropFrame.GetCircle(name, ref file, ref mat, ref t3, ref color, ref notes, ref guid) == 0 => FrameShape.Circle,
                eFramePropType.Channel when Model.PropFrame.GetChannel_2(name, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref radius, ref mirror, ref color, ref notes, ref guid) == 0 => FrameShape.Channel,
                eFramePropType.T when Model.PropFrame.GetTee_1(name, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref radius, ref mirror, ref color, ref notes, ref guid) == 0 => FrameShape.Tee,
                eFramePropType.Angle when Model.PropFrame.GetAngle_1(name, ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref radius, ref color, ref notes, ref guid) == 0 => FrameShape.Angle,
                _ => FrameShape.General,
            };
            if (mat.Length == 0) Model.PropFrame.GetMaterial(name, ref mat);
            double area = 0, as2 = 0, as3 = 0, torsion = 0, i22 = 0, i33 = 0, s22 = 0, s33 = 0, z22 = 0, z33 = 0, r22 = 0, r33 = 0;
            Model.PropFrame.GetSectProps(name, ref area, ref as2, ref as3, ref torsion, ref i22, ref i33, ref s22, ref s33, ref z22, ref z33, ref r22, ref r33);
            double[] modifiers = new double[8];
            if (Model.PropFrame.GetModifiers(name, ref modifiers) != 0 || modifiers.Length < 8) modifiers = [1, 1, 1, 1, 1, 1, 1, 1];
            frames.Add(new FrameSectionProperty
            {
                Name = name, Shape = shape, SapType = type.ToString(), Material = mat, Notes = notes ?? "", Color = Rgb(color),
                Depth = t3, Width = t2, FlangeThickness = tf, WebThickness = tw,
                BottomWidth = shape == FrameShape.I ? t2b : 0, BottomFlangeThickness = shape == FrameShape.I ? tfb : 0, CornerRadius = shape == FrameShape.Box ? radius : 0,
                Modifiers = modifiers.Take(8).ToArray(),
                Given = new SectionValues(area, i33, i22, torsion, as2, as3), Source = "SAP2000",
            });
        }

        var cables = new List<StrandSectionProperty>();
        count = 0; names = [];
        if (Model.PropCable.GetNameList(ref count, ref names) == 0)
            foreach (var name in names.Take(count))
            {
                string mat = "", notes = "", guid = ""; double area = 0; int color = 0;
                Check(Model.PropCable.GetProp(name, ref mat, ref area, ref color, ref notes, ref guid), "PropCable.GetProp", name);
                double[] modifiers = new double[3];
                if (Model.PropCable.GetModifiers(name, ref modifiers) != 0 || modifiers.Length < 3) modifiers = [1, 1, 1];
                cables.Add(new StrandSectionProperty { Name = name, Material = mat, Area = area, Color = Rgb(color), Notes = notes ?? "", Modifiers = modifiers.Take(3).ToArray(), Source = "SAP2000" });
            }

        var tendons = new List<StrandSectionProperty>();
        count = 0; names = [];
        if (Model.PropTendon.GetNameList(ref count, ref names) == 0)
            foreach (var name in names.Take(count))
            {
                string mat = "", notes = "", guid = ""; double area = 0; int color = 0, option = 0;
                Check(Model.PropTendon.GetProp(name, ref mat, ref option, ref area, ref color, ref notes, ref guid), "PropTendon.GetProp", name);
                tendons.Add(new StrandSectionProperty { Name = name, IsTendon = true, Material = mat, Area = area, Color = Rgb(color), Notes = notes ?? "", Modifiers = [], Source = "SAP2000" });
            }
        Verify(expected);
        return new SectionLibrary(frames, cables, tendons);
    });

    /// <summary>Material weight per volume [kN/m³] and mass per volume [kN·s²/m⁴ = t/m³] in the kN-m model units.</summary>
    private (double Weight, double Mass) ReadWeightAndMass(string material)
    {
        double weight = 0, mass = 0;
        Check(Model.PropMaterial.GetWeightAndMass(material, ref weight, ref mass), "PropMaterial.GetWeightAndMass", material);
        return (double.IsFinite(weight) ? weight : 0, double.IsFinite(mass) ? mass : 0);
    }

    private double ReadElasticModulus(string material, string memberName)
    {
        if (string.IsNullOrWhiteSpace(material) || material.Equals("None", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"No usable SAP material is assigned to '{memberName}'. Check its frame/cable section assignment and material override.");
        var type = eMatType.Steel; int symmetry = 0; string notes = "", guid = "";
        Check(Model.PropMaterial.GetMaterial(material, ref type, ref symmetry, ref notes, ref guid),
            "PropMaterial.GetMaterial", material);
        double e = 0;
        if (type == eMatType.Tendon)
        {
            double yieldStress = 0;
            Check(Model.PropMaterial.GetMPUniaxial(material, ref e, ref yieldStress, 0),
                "PropMaterial.GetMPUniaxial", material);
        }
        else
        {
            double poisson = 0, thermal = 0, shear = 0;
            Check(Model.PropMaterial.GetMPIsotropic(material, ref e, ref poisson, ref thermal, ref shear, 0),
                "PropMaterial.GetMPIsotropic", material);
        }
        if (!double.IsFinite(e) || e <= 0) throw new InvalidOperationException($"SAP returned invalid elastic modulus for '{memberName}' ({material}).");
        return e;
    }

    internal static string ResolveEffectiveMaterial(string sectionMaterial, string materialOverride, string memberName, string sectionName)
    {
        var section = sectionMaterial?.Trim() ?? "";
        var material = materialOverride?.Trim() ?? "";
        if (material.Length == 0 || material.Equals("None", StringComparison.OrdinalIgnoreCase)) material = section;
        if (material.Length == 0 || material.Equals("None", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"No material is assigned to {memberName} (section '{sectionName}'); FEM requires a valid elastic material.");
        return material;
    }
    /// <summary>The model's own database tables, as SAP2000 shows them (Display → Show Tables), for the quantity
    /// take-off. Signatures checked against CSiAPIv1 2.15 (SAP2000 27): GetAvailableTables lists the tables that hold
    /// data; GetTableForDisplayArray returns one table as text, row by row, in the present units. Neither writes.
    /// Run against SAP2000 27.1: table keys come in title case, field keys are those of the text export, and numbers
    /// are rounded for display (a density as 7.849, an area as 0.028802) — see <see cref="Refine"/>.</summary>
    public Task<SapTableSet> ReadDatabaseTablesAsync(IReadOnlyList<string> tableNamePrefixes, ModelInfo expected) => worker.Run(() =>
    {
        Verify(expected);
        int tableCount = 0; string[] keys = [], names = []; int[] importTypes = [];
        Check(Model.DatabaseTables.GetAvailableTables(ref tableCount, ref keys, ref names, ref importTypes), "DatabaseTables.GetAvailableTables");
        var read = new List<KeyValuePair<string, IReadOnlyList<SapTableRow>>>();
        foreach (var key in keys.Take(tableCount).Where(key => tableNamePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            // An empty field list asks for every field; "All" is the group every object of the model belongs to.
            // If SAP2000 refuses the group by name, the blank group name stands for the same thing.
            string[] requested = [], fields = [], data = []; int version = 0, records = 0;
            var code = Model.DatabaseTables.GetTableForDisplayArray(key, ref requested, "All", ref version, ref fields, ref records, ref data);
            if (code != 0)
            {
                requested = []; fields = []; data = []; version = 0; records = 0;
                Check(Model.DatabaseTables.GetTableForDisplayArray(key, ref requested, "", ref version, ref fields, ref records, ref data),
                    "DatabaseTables.GetTableForDisplayArray", key);
            }
            if (records == 0 || fields.Length == 0) continue;
            if (data.Length < records * fields.Length) throw new InvalidOperationException($"SAP2000 returned an incomplete '{key}' table.");
            var rows = new List<SapTableRow>(records);
            for (var record = 0; record < records; record++)
            {
                var values = new string[fields.Length];
                for (var column = 0; column < fields.Length; column++) values[column] = data[record * fields.Length + column] ?? "";
                Refine(key, fields, values);
                rows.Add(new(fields.Select((field, column) => KeyValuePair.Create(field, values[column]))));
            }
            read.Add(new(key, rows));
        }
        var info = ReadInfo();
        Verify(expected);
        return SapTableSet.FromTables(read, SapTableUnits.From(info), $"SAP2000 database tables of {info.Name}");
    });
    /// <summary>
    /// The display tables round their numbers to about five figures. The three a quantity is multiplied by — a
    /// material's mass density, a frame section's area and a cable section's area — are therefore taken from their own
    /// read-only getters, at full precision, in the same present units. The getter's value replaces the displayed one
    /// only where the two agree to 0.1 %, i.e. where it is the same number with more digits; where SAP2000 does not
    /// answer, or answers something else, the displayed value stays.
    /// </summary>
    private void Refine(string table, string[] fields, string[] values)
    {
        int Column(string field) => Array.FindIndex(fields, candidate => candidate.Equals(field, StringComparison.OrdinalIgnoreCase));
        string? Shown(string field) => Column(field) is >= 0 and var column && values[column].Length > 0 ? values[column] : null;
        void Exact(string field, double exact)
        {
            if (!double.IsFinite(exact) || Shown(field) is not { } text ||
                !double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var shown)) return;
            if (Math.Abs(exact - shown) <= 1e-3 * Math.Abs(shown)) values[Column(field)] = exact.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (table.Equals("Material Properties 02 - Basic Mechanical Properties", StringComparison.OrdinalIgnoreCase))
        {
            double weight = 0, mass = 0;
            if (Shown("Material") is { } material && Shown("UnitMass") is not null && Model.PropMaterial.GetWeightAndMass(material, ref weight, ref mass) == 0)
                Exact("UnitMass", mass);
        }
        else if (table.Equals("Frame Section Properties 01 - General", StringComparison.OrdinalIgnoreCase))
        {
            double area = 0, as2 = 0, as3 = 0, torsion = 0, i22 = 0, i33 = 0, s22 = 0, s33 = 0, z22 = 0, z33 = 0, r22 = 0, r33 = 0;
            if (Shown("SectionName") is { } section && Shown("Area") is not null &&
                Model.PropFrame.GetSectProps(section, ref area, ref as2, ref as3, ref torsion, ref i22, ref i33, ref s22, ref s33, ref z22, ref z33, ref r22, ref r33) == 0)
                Exact("Area", area);
        }
        else if (table.Equals("Cable Section Definitions", StringComparison.OrdinalIgnoreCase))
        {
            string material = "", notes = "", guid = ""; double area = 0; int color = 0;
            if (Shown("CableSect") is { } section && Shown("Area") is not null &&
                Model.PropCable.GetProp(section, ref material, ref area, ref color, ref notes, ref guid) == 0)
                Exact("Area", area);
        }
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
                Verify(expected, true);
                double[] values = [item.Load.Fx, item.Load.Fy, item.Load.Fz, item.Original[3], item.Original[4], item.Original[5]];
                // SAP2000 rejects an all-zero SetLoadForce (return code 1), so a zero target removes the
                // pattern's assignment instead; with no prior assignment there is nothing to write.
                var clear = values.All(value => Close(value, 0));
                if (clear && !item.Existed) continue;
                attempted.Add(item);
                if (clear)
                    Check(Model.PointObj.DeleteLoadForce(item.Load.PointName, item.Load.LoadPattern, eItemType.Objects),
                        "PointObj.DeleteLoadForce", $"{item.Load.PointName} / {item.Load.LoadPattern}");
                else
                    Check(Model.PointObj.SetLoadForce(item.Load.PointName, item.Load.LoadPattern, ref values, true, "Global", eItemType.Objects),
                        "PointObj.SetLoadForce", $"{item.Load.PointName} / {item.Load.LoadPattern} / Fz={item.Load.Fz:0.###}");
                var written = ReadPointForceAssignments(item.Load.PointName)
                    .Where(candidate => candidate.LoadPattern.Equals(item.Load.LoadPattern, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (clear)
                {
                    if (written.Length != 0)
                        throw new InvalidOperationException($"SAP2000 did not remove the zero joint load: {item.Load.PointName} / {item.Load.LoadPattern}.");
                    continue;
                }
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
        // A cable object defined by its undeformed length keeps that number when its end joints move: SAP2000 would then
        // analyse a cable of the old length stretched across the new chord — too short and too stiff. Read every such
        // cable before the joints move, so it can be given the same length relative to its chord afterwards.
        var movedPoints = updates.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var cableShapes = expectedGeometry.Members
            .Where(m => m.Kind == MemberKind.Cable && (movedPoints.Contains(m.Start) || movedPoints.Contains(m.End)))
            .Select(m => ReadCableShape(m.Name, ChordLength(expectedGeometry, m), ChordLength(proposed, m)))
            .Where(shape => shape is not null && Math.Abs(shape.NewChord - shape.OldChord) > 1e-9)
            .Select(shape => shape!).ToArray();
        var journal = RecoveryJournal.Save("geometry", new { Model = expected, Before = expectedGeometry, Proposed = proposed, Cables = cableShapes });
        var attempted = new List<StructuralPoint>();
        var attemptedCables = new List<CableShape>();
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
            foreach (var shape in cableShapes)
            {
                Verify(expected, true);
                attemptedCables.Add(shape);
                WriteCableShape(shape, shape.NewUndeformedLength);
                // read back against the new chord: SAP2000 must now report the length it was given, and the same relative length as before
                var written = ReadCableShape(shape.Name, shape.NewChord, shape.NewChord);
                if (written is null || Math.Abs(written.UndeformedLength - shape.NewUndeformedLength) > 1e-6 * Math.Max(1, shape.NewUndeformedLength))
                    throw new InvalidOperationException($"Cable {shape.Name} did not take its undeformed length for the new geometry.");
            }
        }
        catch (Exception original)
        {
            var failures = new List<string>();
            foreach (var shape in attemptedCables.AsEnumerable().Reverse())
            {
                try { WriteCableShape(shape, shape.UndeformedLength); }
                catch (Exception ex) { failures.Add(ex.Message); }
            }
            foreach (var p in attempted.AsEnumerable().Reverse())
            {
                try { Verify(expected, true); if (!Model.GetPresentCoordSystem().Equals("Global", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Restore requires SAP's Global coordinate system."); Check(Model.EditPoint.ChangeCoordinates_1(p.Name, p.Position.X, p.Position.Y, p.Position.Z, true), "Restore coordinates", p.Name); }
                catch (Exception ex) { failures.Add(ex.Message); }
            }
            throw new InvalidOperationException(original.Message + (failures.Count == 0 ? " Attempted coordinate and cable-length changes were restored." : " Some changes could not be restored: " + string.Join("; ", failures)) + $" Recovery data: {journal}", original);
        }
        Check(Model.View.RefreshView(0, false), "View.RefreshView");
        return true;
    });
    private static bool Close(Position a, Position b) => Math.Abs(a.X - b.X) <= 1e-7 && Math.Abs(a.Y - b.Y) <= 1e-7 && Math.Abs(a.Z - b.Z) <= 1e-7;

    // CableObj.GetCableData / SetCableData: cable type 8 is "defined by its undeformed length"; in the parameter array
    // GetCableData returns, [9] is that length and [10] the same length relative to the chord between the end joints.
    private const int CableByUndeformedLength = 8;
    private const int UndeformedLengthParameter = 9, RelativeLengthParameter = 10;

    /// <summary>A cable object defined by its undeformed length, as it stands before its end joints move.</summary>
    private sealed record CableShape(string Name, int Type, int Segments, double Weight, double ProjectedLoad, bool UseDeformedGeometry,
        bool ModelUsingFrames, double UndeformedLength, double OldChord, double NewChord)
    {
        /// <summary>The same length relative to the chord, on the new chord.</summary>
        public double NewUndeformedLength => UndeformedLength * NewChord / OldChord;
    }

    private static double ChordLength(GeometryModel geometry, StructuralMember member)
    {
        var (a, b) = (geometry.Points[member.Start].Position, geometry.Points[member.End].Position);
        return Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
    }

    /// <summary>The cable's shape definition when it is defined by its undeformed length; null for every other
    /// definition (tension, sag, relative length), which SAP2000 re-evaluates on the geometry by itself. Refuses a
    /// reply it cannot make sense of — the reported relative length has to be the reported length over the chord.</summary>
    private CableShape? ReadCableShape(string name, double oldChord, double newChord)
    {
        int type = 0, segments = 0; double weight = 0, load = 0; bool deformed = false, frames = false; double[] parameters = [];
        Check(Model.CableObj.GetCableData(name, ref type, ref segments, ref weight, ref load, ref deformed, ref frames, ref parameters), "CableObj.GetCableData", name);
        if (type != CableByUndeformedLength) return null;
        if (parameters.Length <= RelativeLengthParameter || !(oldChord > 0) || !(parameters[UndeformedLengthParameter] > 0) ||
            Math.Abs(parameters[UndeformedLengthParameter] / oldChord - parameters[RelativeLengthParameter]) > 1e-4)
            throw new InvalidOperationException($"SAP2000 returned the shape data of cable {name} in an unexpected layout; its length cannot be carried over to the new geometry.");
        return new(name, type, segments, weight, load, deformed, frames, parameters[UndeformedLengthParameter], oldChord, newChord);
    }

    private void WriteCableShape(CableShape shape, double undeformedLength) =>
        Check(Model.CableObj.SetCableData(shape.Name, shape.Type, shape.Segments, shape.Weight, shape.ProjectedLoad, undeformedLength,
            shape.UseDeformedGeometry, shape.ModelUsingFrames), "CableObj.SetCableData", shape.Name);
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
        int caseCount = 0; string[] cases = [];
        Check(Model.LoadCases.GetNameList(ref caseCount, ref cases), "LoadCases.GetNameList");
        int comboCount = 0; string[] combos = [];
        Check(Model.RespCombo.GetNameList(ref comboCount, ref combos), "RespCombo.GetNameList");
        return cases.Take(caseCount).Concat(combos.Take(comboCount))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
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
