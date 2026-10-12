using COT_VCF.Domain;
using Mcotis.Analysis;
using Mcotis.Application;
using Mcotis.Core;
using Mcotis.Persistence;

namespace COT_VCF.Presentation;

public sealed record NativeJointRow(string Point, double UX, double UY, double UZ, double RX, double RY, double RZ,
    double FX, double FY, double FZ, double MX, double MY, double MZ);
public sealed record NativeFrameRow(string Frame, string End, double N, double V2, double V3, double T, double M2, double M3);
public sealed record NativeLoadRow(string Frame, string Pattern, string Direction, double Start, double End, double QI, double QJ);

/// <summary>The desktop adapter owns a native session. The libraries never read legacy geometry or presentation types.</summary>
public sealed class NativeModelViewModel : Observable
{
    public DocumentSession Session { get; }
    public DocumentLifecycle Files { get; }
    public AnalysisResultTracker Results { get; }
    private LinearStaticResult? result;
    private ModelDocument? resultInput;
    private bool busy;
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) Notify(nameof(Editable)); } }
    public bool Editable => !Busy;
    private string message = "Ready. Linear static frames; 600 free DOFs / 2,400 total DOFs maximum.";
    public string Message { get => message; set => Set(ref message, value); }
    public string Title => $"Mcotis · Native model · {(Session.FilePath is null ? "TemplateBuilding" : System.IO.Path.GetFileName(Session.FilePath))}{(Session.IsDirty ? " *" : "")}";
    public string Summary => $"{Session.Document.Points.Count} points · {Session.Document.Frames.Count} frames · DOFs: {Session.Document.Settings.ActiveDofs} · Results: {Results.State}" +
        (result is not null && resultInput is not null ? $" ({resultInput.LoadCases[result.LoadCaseId].Name}, revision {result.Revision})" : "");
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
    public IEnumerable<NativeLoadRow> Loads => Session.Document.Loads.Values.OfType<FrameDistributedLoad>().OrderBy(l => Session.Document.Frames[l.TargetId].Name, StringComparer.Ordinal).Select(l => new NativeLoadRow(
        Session.Document.Frames[l.TargetId].Name, Session.Document.LoadPatterns[l.PatternId].Name, l.Direction.ToString(), l.RelativeStart, l.RelativeEnd, l.ValueStart, l.ValueEnd));

    public NativeModelViewModel(DocumentSession session)
    {
        Session = session; Files = new(session, new NativeDocumentStore()); Results = new(session);
        session.Changed += (_, e) =>
        {
            if (e.Reason == DocumentChangeReason.Replaced) { result = null; resultInput = null; }
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
        foreach (var name in new[] { nameof(Session), nameof(Title), nameof(Summary), nameof(Geometry), nameof(Supports), nameof(Cases), nameof(Joints), nameof(Frames), nameof(Loads) }) Notify(name);
    }
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Busy) return;
        if (SelectedCase is not { } loadCase) { Message = "Select an existing load case."; return; }
        Busy = true;
        var run = Results.Begin();
        Message = "Compiling model and solving linear static frames…";
        try
        {
            var outcome = await Task.Run(() =>
            {
                var compiled = ModelCompiler.Compile(run.Input, run.Revision);
                return compiled.Success ? LinearStaticSolver.Solve(compiled.Model!, loadCase.Id, cancellationToken: cancellationToken)
                    : new LinearStaticOutcome(null, compiled.Issues);
            }, cancellationToken);
            if (outcome.Success)
            {
                result = outcome.Result; resultInput = run.Input; Results.Complete(run);
                Message = $"Case {loadCase.Name}: {result!.Diagnostics.SolvedDofCount} DOFs solved; scaled residual {result.Diagnostics.ScaledResidual:G4}. Translations m, rotations rad, reactions kN / kN·m.";
            }
            else
            {
                result = null; resultInput = null; Results.Fail(run);
                Message = string.Join(Environment.NewLine, outcome.Issues.Select(i => $"{i.Code}: {i.Message}"));
            }
        }
        catch (OperationCanceledException) { Results.Cancel(run); Message = "Analysis cancelled."; }
        catch (Exception ex) { Results.Fail(run); result = null; resultInput = null; Message = $"Analysis failed: {ex.Message}"; }
        finally { Busy = false; Refresh(); }
    }
}
