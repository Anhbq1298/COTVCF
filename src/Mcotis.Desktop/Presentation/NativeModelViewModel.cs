using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Analysis;
using Mcotis.Application;
using Mcotis.Core;
using Mcotis.Persistence;

namespace Mcotis.Desktop.Presentation;

public sealed record NativeJointRow(string Point, double UX, double UY, double UZ, double RX, double RY, double RZ,
    double FX, double FY, double FZ, double MX, double MY, double MZ);
public sealed record NativeFrameRow(string Frame, string End, double N, double V2, double V3, double T, double M2, double M3);
public sealed record NativeCableRow(string Cable, double Tension, string State, double? Target, double Pretension, double? FitStage, double? Sag, double? StiffnessRatio);
public sealed record NativeLoadRow(string Frame, string Pattern, string Direction, double Start, double End, double QI, double QJ);
public sealed record NativeModeRow(int Mode, double Eigenvalue, double? Frequency, double? Period, string Status, double Residual);
public sealed record NativeParticipationRow(int Mode, string Direction, double? Factor, double? EffectiveMass, double? Ratio, double? Cumulative);
public sealed record NativeModeShapeRow(string Point, double UX, double UY, double UZ, double RX, double RY, double RZ);
public sealed record NativeModalMassRow(string Item, double Value, string Unit, string Scope);
public sealed record NativeModalDefinition(string CaseName, string MassSourceName, bool IncludeSelfMass,
    bool IncludeAdditionalMass, int RequestedModes);
public sealed record NativeJointMassDefinition(ObjectId PointId, double UX, double UY, double UZ, double RX, double RY, double RZ);

/// <summary>The desktop adapter owns a native session. The libraries never read legacy geometry or presentation types.</summary>
public sealed class NativeModelViewModel : Observable
{
    public DocumentSession Session { get; }
    public DocumentLifecycle Files { get; }
    public AnalysisResultTracker Results { get; }
    private StaticResult? result;
    private ModalAnalysisResult? modalResult;
    private ModelDocument? resultInput;
    private bool busy;
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) Notify(nameof(Editable)); } }
    public bool Editable => !Busy;
    private string message = "Ready. Static analysis of frames and cables; 600 free DOFs / 2,400 total DOFs maximum.";
    public string Message { get => message; set => Set(ref message, value); }
    public string Title => $"Mcotis · Native model · {(Session.FilePath is null ? "TemplateBuilding" : System.IO.Path.GetFileName(Session.FilePath))}{(Session.IsDirty ? " *" : "")}";
    public string Summary => $"{Session.Document.Points.Count} points · {Session.Document.Frames.Count} frames · {Session.Document.Cables.Count} cables · DOFs: {Session.Document.Settings.ActiveDofs} · Results: {Results.State}" +
        (result is not null && resultInput is not null ? $" ({resultInput.LoadCases[result.LoadCaseId].Name}, revision {result.Revision})"
        : modalResult is not null && resultInput is not null ? $" ({resultInput.LoadCases[modalResult.ModalCaseId].Name}, {modalResult.Modes.Length} modes, revision {modalResult.Revision})" : "");
    public GeometryModel Geometry => new(Session.Document.Points.Values.ToDictionary(p => p.Name, p => new StructuralPoint(p.Name, new(p.Position.X, p.Position.Y, p.Position.Z))),
        Session.Document.Frames.Values.Select(f => new StructuralMember(f.Name, Session.Document.Points[f.StartPointId].Name, Session.Document.Points[f.EndPointId].Name, MemberKind.Frame))
            .Concat(Session.Document.Cables.Values.Select(f => new StructuralMember(f.Name, Session.Document.Points[f.StartPointId].Name, Session.Document.Points[f.EndPointId].Name, MemberKind.Cable))).ToArray());
    public IEnumerable<string> Supports => Session.Document.Restraints.Where(p => !p.Value.IsFree).Select(p => Session.Document.Points[p.Key].Name);
    public IEnumerable<LoadCase> Cases => Session.Document.LoadCases.Values.OrderBy(c => c.Name);
    private LoadCase? selectedCase;
    public LoadCase? SelectedCase { get => selectedCase; set => Set(ref selectedCase, value); }
    public IEnumerable<NativeJointRow> Joints => result is null || resultInput is null ? [] : result.Joints.OrderBy(j => resultInput.Points[j.PointId].Name, StringComparer.Ordinal).Select(j => new NativeJointRow(
        resultInput.Points[j.PointId].Name, j.Displacements[0], j.Displacements[1], j.Displacements[2], j.Displacements[3], j.Displacements[4], j.Displacements[5],
        j.Reactions[0], j.Reactions[1], j.Reactions[2], j.Reactions[3], j.Reactions[4], j.Reactions[5]));
    public IEnumerable<NativeFrameRow> Frames => result is null || resultInput is null ? [] : result.Frames.OrderBy(f => resultInput.Frames[f.ParentObjectId].Name, StringComparer.Ordinal).SelectMany(f => Enumerable.Range(0, 2).Select(end => new NativeFrameRow(
        resultInput.Frames[f.ParentObjectId].Name, end == 0 ? "I" : "J", f.LocalEndActions[6 * end], f.LocalEndActions[6 * end + 1], f.LocalEndActions[6 * end + 2],
        f.LocalEndActions[6 * end + 3], f.LocalEndActions[6 * end + 4], f.LocalEndActions[6 * end + 5])));
    public IEnumerable<NativeCableRow> Cables => result is null || resultInput is null ? [] : result.Cables.OrderBy(c => resultInput.Cables[c.ParentObjectId].Name, StringComparer.Ordinal).Select(c => new NativeCableRow(
        resultInput.Cables[c.ParentObjectId].Name, c.Tension, c.Active ? "Taut" : "Slack", c.TargetForce, c.AppliedPretension, c.FitStageTension, c.Sag, c.TangentStiffnessRatio));
    public IEnumerable<NativeLoadRow> Loads => Session.Document.Loads.Values.OfType<FrameDistributedLoad>().OrderBy(l => Session.Document.Frames[l.TargetId].Name, StringComparer.Ordinal).Select(l => new NativeLoadRow(
        Session.Document.Frames[l.TargetId].Name, Session.Document.LoadPatterns[l.PatternId].Name, l.Direction.ToString(), l.RelativeStart, l.RelativeEnd, l.ValueStart, l.ValueEnd));
    public IEnumerable<NativeModeRow> Modes => modalResult?.Modes.Select(mode => new NativeModeRow(mode.ModeNumber,
        mode.EigenvaluePerSecondSquared, mode.FrequencyHz, mode.PeriodSeconds, mode.Status.ToString(), mode.ScaledResidual)) ?? [];
    public IEnumerable<NativeParticipationRow> Participation => modalResult?.Modes.SelectMany(mode => mode.Participation.Select(value =>
        new NativeParticipationRow(mode.ModeNumber, value.Direction.ToString(), value.Factor, value.EffectiveMass, value.RatioPercent, value.CumulativePercent))) ?? [];
    private int selectedMode = 1;
    public int SelectedMode
    {
        get => selectedMode;
        set { if (Set(ref selectedMode, value)) { Notify(nameof(ModeShape)); Notify(nameof(ModalDisplayResult)); } }
    }
    public IEnumerable<int> ModeNumbers => modalResult?.Modes.Select(mode => mode.ModeNumber) ?? [];
    public IEnumerable<NativeModeShapeRow> ModeShape => modalResult is null || resultInput is null ? []
        : modalResult.Modes.FirstOrDefault(mode => mode.ModeNumber == SelectedMode)?.Shape
            .OrderBy(item => resultInput.Points[item.PointId].Name, StringComparer.Ordinal)
            .Select(item => new NativeModeShapeRow(resultInput.Points[item.PointId].Name, item.UX, item.UY, item.UZ, item.RX, item.RY, item.RZ)) ?? [];
    public IEnumerable<NativeModalMassRow> ModalMass
    {
        get
        {
            if (modalResult is null) return [];
            var rows = new List<NativeModalMassRow>
            {
                new("Frame self mass", modalResult.MassSummary.ElementMass, "t", "Physical inventory")
            };
            string[] components = ["UX", "UY", "UZ", "RX", "RY", "RZ"];
            for (var i = 0; i < components.Length; i++)
            {
                string unit = i < 3 ? "t" : "t·m²";
                rows.Add(new($"Assigned {components[i]}", modalResult.MassSummary.AdditionalByDof[i], unit, "All joints"));
                rows.Add(new($"Free {components[i]}", modalResult.MassSummary.FreeMassByDof[i], unit, "Active, unrestrained DOFs"));
            }
            if (modalResult.MassSummary.CenterOfMass is { } center)
            {
                rows.Add(new("Centre of mass X", center.X, "m", "Physical frame + assigned translational mass"));
                rows.Add(new("Centre of mass Y", center.Y, "m", "Physical frame + assigned translational mass"));
                rows.Add(new("Centre of mass Z", center.Z, "m", "Physical frame + assigned translational mass"));
            }
            return rows;
        }
    }
    /// <summary>Drawing adapter only. The viewer magnifies these relative amplitudes; they are not a load-caused displacement.</summary>
    public FemAnalysisResult? ModalDisplayResult
    {
        get
        {
            if (modalResult is null || resultInput is null || modalResult.Modes.FirstOrDefault(mode => mode.ModeNumber == SelectedMode) is not { } mode) return null;
            var joints = mode.Shape.Select(item => new FemJointResponse(resultInput.Points[item.PointId].Name, item.UX, item.UZ, -item.RY)).ToArray();
            return new(joints, [], [], [], 0);
        }
    }

    public NativeModelViewModel(DocumentSession session)
    {
        Session = session; Files = new(session, new NativeDocumentStore()); Results = new(session);
        session.Changed += (_, e) =>
        {
            if (e.Reason == DocumentChangeReason.Replaced) { result = null; modalResult = null; resultInput = null; }
            Refresh();
        };
        session.SavedStateChanged += (_, _) => Notify(nameof(Title));
        Results.StateChanged += (_, _) => Notify(nameof(Summary));
        SelectedCase = Cases.FirstOrDefault();
    }
    private void Refresh()
    {
        var id = SelectedCase?.Id;
        SelectedCase = Cases.FirstOrDefault(c => c.Id == id) ?? Cases.FirstOrDefault();
        foreach (var name in new[] { nameof(Session), nameof(Title), nameof(Summary), nameof(Geometry), nameof(Supports), nameof(Cases), nameof(Joints), nameof(Frames), nameof(Cables), nameof(Loads),
            nameof(Modes), nameof(Participation), nameof(ModeNumbers), nameof(ModeShape), nameof(ModalMass), nameof(ModalDisplayResult) }) Notify(name);
    }

    public NativeModalDefinition ModalDefinition()
    {
        var modal = SelectedCase?.Kind == CaseKind.ModalEigen ? SelectedCase : null;
        var source = modal?.ModalOptions is { } options ? Session.Document.MassSources.GetValueOrDefault(options.MassSourceId) : null;
        return new(modal?.Name ?? "MODAL", source?.Name ?? "MS1", source?.IncludeSelfMass ?? true,
            source?.IncludeAdditionalMass ?? true, modal?.ModalOptions?.RequestedModes ?? 12);
    }

    public void DefineModal(NativeModalDefinition definition)
    {
        var existingCase = SelectedCase?.Kind == CaseKind.ModalEigen ? SelectedCase : null;
        var existingSource = existingCase?.ModalOptions is { } options
            ? Session.Document.MassSources.GetValueOrDefault(options.MassSourceId)
            : Session.Document.MassSources.Values.FirstOrDefault(item => item.Name.Equals(definition.MassSourceName, StringComparison.OrdinalIgnoreCase));
        ObjectId caseId = existingCase?.Id ?? ObjectId.New(), sourceId = existingSource?.Id ?? ObjectId.New();
        Session.Execute(DocumentCommands.Edit(existingCase is null ? "Define Modal Case" : "Modify Modal Case", editor =>
        {
            editor.PutMassSource(new(sourceId, definition.MassSourceName, definition.IncludeSelfMass, definition.IncludeAdditionalMass));
            editor.PutLoadCase(new(caseId, definition.CaseName, CaseKind.ModalEigen, [])
            { ModalOptions = new(sourceId, definition.RequestedModes), Run = true });
        }));
        SelectedCase = Session.Document.LoadCases[caseId];
        Message = $"Modal case '{definition.CaseName}' uses mass source '{definition.MassSourceName}'. Run Analysis to extract {definition.RequestedModes} modes.";
        Refresh();
    }

    public void AssignJointMass(NativeJointMassDefinition definition)
    {
        Session.Execute(DocumentCommands.AssignJointMass(new(definition.PointId, definition.UX, definition.UY,
            definition.UZ, definition.RX, definition.RY, definition.RZ)));
        var point = Session.Document.Points[definition.PointId];
        Message = definition is { UX: 0, UY: 0, UZ: 0, RX: 0, RY: 0, RZ: 0 }
            ? $"Additional mass removed from joint '{point.Name}'."
            : $"Additional mass assigned to joint '{point.Name}'. Translational mass t; rotational inertia t·m².";
        Refresh();
    }
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Busy) return;
        if (SelectedCase is not { } loadCase) { Message = "Select an existing load case."; return; }
        Busy = true;
        var run = Results.Begin();
        Message = "Compiling the model and solving…";
        try
        {
            if (loadCase.Kind == CaseKind.ModalEigen)
            {
                var outcome = await Task.Run(() =>
                {
                    var compiled = ModelCompiler.Compile(run.Input, run.Revision);
                    return compiled.Success ? ModalSolver.Solve(compiled.Model!, loadCase.Id, cancellationToken: cancellationToken)
                        : new ModalOutcome(null, compiled.Issues);
                }, cancellationToken);
                result = null; modalResult = outcome.Result; resultInput = modalResult is null ? null : run.Input;
                if (outcome.Success)
                {
                    Results.Complete(run); SelectedMode = modalResult!.Modes.FirstOrDefault()?.ModeNumber ?? 1;
                    Message = $"Case {loadCase.Name}: {modalResult.Modes.Length} mass-normalized eigenmodes; " +
                        $"{modalResult.Solver.InertialDofs} inertial DOFs, {modalResult.Solver.CondensedMasslessDofs} massless DOFs condensed. " +
                        "Frequencies Hz; periods s; mode amplitudes are arbitrary-scaled free-vibration shapes, not static displacements.";
                }
                else
                {
                    Results.Fail(run);
                    Message = string.Join(Environment.NewLine, outcome.Issues.Select(i => $"{i.Code}: {i.Message}"));
                }
            }
            else
            {
                var outcome = await Task.Run(() =>
                {
                    var compiled = ModelCompiler.Compile(run.Input, run.Revision);
                    return compiled.Success ? StaticSolver.Solve(compiled.Model!, loadCase.Id, cancellationToken: cancellationToken)
                        : new StaticOutcome(null, compiled.Issues);
                }, cancellationToken);
                modalResult = null;
                if (outcome.Success)
                {
                    result = outcome.Result; resultInput = run.Input; Results.Complete(run);
                    var d = result!.Diagnostics;
                    Message = $"Case {loadCase.Name}: {d.SolvedDofCount} DOFs solved; scaled residual {d.ScaledResidual:G4}" +
                        (d.NonlinearIterations > 0 ? $"; cable equilibrium in {d.NonlinearIterations} iterations" : "") +
                        (d.CableFit is { } fit ? $"; cable target forces met within {fit.WorstDeviationPercent:G3}% in {fit.Iterations} fitting runs" : "") +
                        ". Translations m, rotations rad, forces kN, moments kN·m." +
                        string.Concat(outcome.Issues.Where(i => i.Severity == Mcotis.Core.ValidationSeverity.Warning).Select(i => $"{Environment.NewLine}Note — {i.Message}"));
                }
                else
                {
                    result = null; resultInput = null; Results.Fail(run);
                    Message = string.Join(Environment.NewLine, outcome.Issues.Select(i => $"{i.Code}: {i.Message}"));
                }
            }
        }
        catch (OperationCanceledException) { Results.Cancel(run); Message = "Analysis cancelled."; }
        catch (Exception ex) { Results.Fail(run); result = null; modalResult = null; resultInput = null; Message = $"Analysis failed: {ex.Message}"; }
        finally { Busy = false; Refresh(); }
    }
}
