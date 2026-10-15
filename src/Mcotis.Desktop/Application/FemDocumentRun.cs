using System.Collections.Immutable;
using Mcotis.Analysis;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application;

/// <summary>What a run of a document gives: per load case the result in the application's terms (its names, its plane),
/// the solver's own result with all six components, the modes of a modal case, and what the run said about the model.</summary>
public sealed record FemDocumentResults(
    IReadOnlyDictionary<string, FemAnalysisResult> Cases,
    IReadOnlyDictionary<string, StaticResult> Static,
    IReadOnlyDictionary<string, ModalAnalysisResult> Modal,
    IReadOnlyList<string> Notes,
    LegacyNames Names)
{
    /// <summary>The declared type of every solved case, persisted with the output so a result is never reclassified
    /// from its numerical iteration count.</summary>
    public IReadOnlyDictionary<string, Native.CaseKind> CaseKinds { get; init; } = new Dictionary<string, Native.CaseKind>();
}

/// <summary>
/// The one analysis path of the application (task of 2026-10-14, M1): a native <see cref="Native.ModelDocument"/> and the
/// load cases asked for go to the solvers of the standalone system as they are — <see cref="StaticSolver"/> for the
/// static cases, <see cref="ModalSolver"/> for the modal ones. Run FEM Analysis hands it the document of the model, an
/// optimization the document of each candidate. Nothing numerical happens here: the document is compiled and solved,
/// what the solver reports is passed on in the terms of the run monitor, and the results are named by
/// <see cref="LegacyNames"/> for the tables, the viewer and the reports that still read the application's model.
/// <para>As the run always did, a joint that no frame and no cable reaches is not part of the analysis: it is left out
/// and the run says so. One with a load or a mass on it is not left out — the run is refused with the reason.</para>
/// <para>The engine is asked to hold a movement the loads of a case do no work on (<see cref="StaticOptions.HoldUnloadedMechanisms"/>;
/// user, 2026-10-13: "ETABS can run regardless") and to keep the closest pretension when targets cannot be met; both are
/// reported, never silent.</para>
/// </summary>
public static class FemDocumentRun
{
    /// <summary>Where the degrees of freedom are set, as a refusal that turns on them tells the user.</summary>
    internal const string DofOptionPlace = "under Run → Analysis Options → Degrees of Freedom";

    /// <summary>Solves the cases named <paramref name="caseNames"/> of <paramref name="document"/>, in that order.
    /// <paramref name="order"/>, when given, is the application's model: its results list frames and cables in its
    /// order, as they always were. A case without a result refuses the run (<see cref="InvalidOperationException"/>).</summary>
    public static FemDocumentResults Solve(Native.ModelDocument document, IReadOnlyList<string> caseNames, GeometryModel? order = null,
        StabilityThresholds? thresholds = null, int maximumEquilibriumIterations = 100,
        IProgress<FemEquilibriumProgress>? equilibrium = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (caseNames.Count == 0) throw new InvalidOperationException("At least one load case is required.");
        var notes = new List<string>();
        var analysed = WithoutUnconnectedJoints(document, notes);
        var settings = analysed.Settings;
        if ((settings.ActiveDofs & Native.DofMask.Translations) == 0)
            throw new InvalidOperationException($"No translation is switched on: no joint of the model can move. Switch one on {DofOptionPlace}.");
        if (analysed.Frames.IsEmpty) throw new InvalidOperationException("The connected model has no frame members to analyze.");

        var cases = caseNames.Select(name => analysed.LoadCases.Values.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Load case '{name}' is not in the model.")).ToArray();
        var compiled = ModelCompiler.Compile(analysed);
        if (compiled.Model is not { } model) throw Refused(analysed, compiled.Issues);
        // a joint only cables reach that a switched-off degree of freedom holds against the pull of a cable: said with every case
        var held = compiled.Issues.Where(issue => issue is { Severity: Native.ValidationSeverity.Warning, Code: "dof.cable.held" })
            .Select(issue => issue.Message).Distinct().ToArray();
        var names = LegacyNames.Of(analysed);

        var statics = cases.Where(item => item.Kind != Native.CaseKind.ModalEigen).ToArray();
        var modals = cases.Where(item => item.Kind == Native.CaseKind.ModalEigen).ToArray();
        // the stage that fits the cable target forces a case carries, by the case that fits them
        Native.LoadCase? FitOf(Native.LoadCase item)
        {
            for (Native.LoadCase? current = item; current is not null;
                 current = current.InitialCaseId is { } parent ? analysed.LoadCases[parent] : null)
                if (current.FitCableTargetForces) return current;
            return null;
        }
        var fitOf = statics.ToDictionary(item => item.Id, FitOf);
        int entries = fitOf.Values.Where(fit => fit is not null).Select(fit => fit!.Id).Distinct().Count() + statics.Length + modals.Length;
        int ended = 0;
        var stagesEnded = new HashSet<Native.ObjectId>();

        IProgress<StaticProgress>? follow = equilibrium is null ? null : new DirectProgress<StaticProgress>(report =>
        {
            if (!fitOf.TryGetValue(report.CaseId, out var fit)) return;
            var own = analysed.LoadCases[report.CaseId];
            bool fitting = fit is not null && report.FitRun > 0 && report.Kind is StaticProgressKind.Started or StaticProgressKind.Equilibrium;
            FemEquilibriumProgress Progress(bool stage, FemEquilibriumState state, bool counts) => new(
                stage ? fit!.Name : own.Name, stage, state, report.ActiveDofs, report.RestrainedDofs, report.SolvedDofs, report.FitRun,
                report.Balance?.Iteration ?? 0, report.Balance?.Linear ?? false, report.Balance?.OutOfBalance, report.Balance?.Tolerance,
                report.Balance?.Joints ?? 0, report.Balance?.JointsInBalance ?? 0,
                report.Balance?.Worst is { } worst ? analysed.Points[worst.PointId].Name : null, report.Balance?.Worst?.Component.ToString(),
                (double)(ended + (counts ? 1 : 0)) / entries, report.Issue is { } issue ? issue.Message : null)
            {
                Scale = report.Balance?.Scale ?? 1,
                Pretensioned = fit is not null,
                Outcome = report.Diagnostics is { } done
                    ? new(done.MinimumScaledPivot, done.EquilibriumError, done.CableFit?.WorstDeviationPercent, done.CableFit?.Converged ?? true, done.CableFit?.Failure)
                    : null,
                FailureCode = report.Kind == StaticProgressKind.Failed ? report.Issue?.Code : null,
                Stability = report.Stability is { } assessed ? FemStabilityReport.From(assessed, model) : null,
                ModelWarnings = held,
            };
            // The fit of the cable target forces is an entry of its own in the monitor: the stage, by the case that fits.
            // The solver fits them while it solves the first case that needs them, and that case's equilibrium follows;
            // the stage is running until that case ends, and ends with it — failed if the case failed while fitting.
            bool pending = fit is not null && !stagesEnded.Contains(fit.Id);
            if (pending && (fitting || report.Kind == StaticProgressKind.Started))
            {
                equilibrium.Report(Progress(stage: true, FemEquilibriumState.Running, counts: false));
                return;
            }
            if (pending && report.Kind is StaticProgressKind.Finished or StaticProgressKind.Failed)
            {
                stagesEnded.Add(fit!.Id);
                ended++;
                bool failedInFit = report.Kind == StaticProgressKind.Failed && report.FitRun > 0;
                equilibrium.Report(Progress(stage: true, failedInFit ? State(report) : FemEquilibriumState.Converged, counts: false));
            }
            if (report.Kind is StaticProgressKind.Finished or StaticProgressKind.Failed) ended++;
            var state = report.Kind switch
            {
                StaticProgressKind.Finished => FemEquilibriumState.Converged,
                StaticProgressKind.Failed => State(report),
                _ => FemEquilibriumState.Running,
            };
            equilibrium.Report(Progress(stage: false, state, counts: false) with
            {
                // a case that failed while its pretension was fitted failed in that stage, not in its own equilibrium
                PrerequisiteFailed = report.Kind == StaticProgressKind.Failed && report.FitRun > 0,
            });
        });
        static FemEquilibriumState State(StaticProgress report) => report.Issue?.Code switch
        {
            "static.matrix.unstable" => FemEquilibriumState.Unstable,
            "static.nonlinear.noConvergence" or "static.nonlinear.stalled" => FemEquilibriumState.NotConverged,
            _ => FemEquilibriumState.Failed,
        };

        // The engine is dense. These limits are where its memory, not its method, ends: about 1,000 joints.
        var outcomes = statics.Length == 0 ? ImmutableDictionary<Native.ObjectId, StaticOutcome>.Empty
            : StaticSolver.Solve(model, statics.Select(item => item.Id),
                new StaticOptions(MaximumFullDofs: 6000, MaximumFreeDofs: 3000, MaximumNonlinearIterations: maximumEquilibriumIterations,
                    AcceptClosestFit: true, Stability: thresholds ?? StabilityThresholds.Default, HoldUnloadedMechanisms: true),
                cancellationToken, follow);
        var legacy = new Dictionary<string, FemAnalysisResult>(StringComparer.OrdinalIgnoreCase);
        var native = new Dictionary<string, StaticResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in statics)
        {
            var solved = outcomes[item.Id].Result ?? throw Refused(analysed, outcomes[item.Id].Issues);
            native[item.Name] = solved;
            legacy[item.Name] = ToApplication(analysed, names, solved, order);
        }
        var modal = new Dictionary<string, ModalAnalysisResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in modals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stiffnessState = item.StiffnessSourceCaseId is { } sourceId && analysed.LoadCases.TryGetValue(sourceId, out var sourceCase)
                ? native.GetValueOrDefault(sourceCase.Name) : null;
            var outcome = ModalSolver.Solve(model, item.Id, cancellationToken: cancellationToken, stiffnessState: stiffnessState);
            ended++;
            equilibrium?.Report(new(item.Name, false, outcome.Success ? FemEquilibriumState.Converged : FemEquilibriumState.Failed,
                model.Dofs.Count(dof => dof.State != DofState.Inactive), model.Dofs.Count(dof => dof.State == DofState.Restrained), model.FreeDofCount,
                0, 0, true, null, null, 0, 0, null, null, (double)ended / entries,
                outcome.Success ? null : outcome.Issues.FirstOrDefault(issue => issue.Severity == Native.ValidationSeverity.Error)?.Message)
            { FailureCode = outcome.Success ? null : outcome.Issues.FirstOrDefault(issue => issue.Severity == Native.ValidationSeverity.Error)?.Code, ModelWarnings = held });
            modal[item.Name] = outcome.Result is { } result && outcome.Success ? result : throw Refused(analysed, outcome.Issues);
        }
        return new(legacy, native, modal, notes, names)
        {
            CaseKinds = cases.ToDictionary(item => item.Name, item => item.Kind, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>The document without the joints that no frame and no cable reaches — the run has never analysed them —
    /// each named in <paramref name="notes"/>. A joint with a load or a mass on it stays, and the compiler refuses it.</summary>
    private static Native.ModelDocument WithoutUnconnectedJoints(Native.ModelDocument document, List<string> notes)
    {
        var compiled = ModelCompiler.Compile(document);
        var loose = compiled.Issues.Where(issue => issue.Code == "analysis.disconnected.point").Select(issue => issue.ObjectId).Distinct()
            .Where(id => document.Loads.Values.All(load => load.TargetId != id) && !document.JointMassAssignments.ContainsKey(id)).ToArray();
        if (loose.Length == 0) return document;
        var editor = document.Edit();
        editor.Remove(loose);
        foreach (var id in loose.OrderBy(id => document.Points[id].Name, StringComparer.Ordinal))
            notes.Add($"Joint '{document.Points[id].Name}' is connected to no frame and no cable: it is not part of the analysis.");
        return editor.Commit().Document;
    }

    /// <summary>A case's result in the application's terms: its XZ plane, its names, its sign of the joint slope and of
    /// the frame diagrams. The six components stay in the solver's own result.</summary>
    internal static FemAnalysisResult ToApplication(Native.ModelDocument document, LegacyNames names, StaticResult solved, GeometryModel? order)
    {
        var jointResults = solved.Joints.ToDictionary(joint => joint.PointId);
        var joints = jointResults.Values.Select(joint => (Name: names[joint.PointId], joint.Displacements))
            .OrderBy(joint => joint.Name, StringComparer.Ordinal)
            // the application's slope is positive toward +Z for +X: the opposite of the rotation about global Y
            .Select(joint => new FemJointResponse(joint.Name, joint.Displacements[0], joint.Displacements[2], -joint.Displacements[4])
                { UYM = joint.Displacements[1], RXRad = joint.Displacements[3], RZRad = joint.Displacements[5] }).ToArray();
        // the supports of the model as it is drawn: the joints held in the XZ plane, left to right
        var supports = jointResults.Keys.Where(id => FemSolver.HoldsInPlane(document.RestraintOf(id)))
            .OrderBy(id => document.Points[id].Position.X).ThenBy(id => names[id], StringComparer.Ordinal)
            .Select(id => new FemSupportReaction(names[id], jointResults[id].Reactions[0], jointResults[id].Reactions[2], -jointResults[id].Reactions[4])).ToArray();

        int Rank(string name) => order is null ? 0 : order.Members.Select((member, index) => (member, index)).FirstOrDefault(entry => entry.member.Name == name) is var found && found.member is not null ? found.index : int.MaxValue;
        var frames = solved.Frames.Select(frame =>
        {
            Native.Xyz Global(IReadOnlyList<double> local, int at) => frame.Axis1 * local[at] + frame.Axis2 * local[at + 1] + frame.Axis3 * local[at + 2];
            Native.Xyz normal = new(-frame.Axis1.Z, 0, frame.Axis1.X);
            var stations = frame.Segments.SelectMany(segment => segment.Stations).Select(station =>
            {
                var moved = Global(station.LocalDisplacements, 0);
                // one sign convention along the frame: axial force in tension, shear and moment as the application draws them
                return new FemFrameStation(station.Relative, station.Actions[0], -normal.Dot(Global(station.Actions, 0)), -Global(station.Actions, 3).Y, moved.X, moved.Z)
                    { UYM = moved.Y };
            }).ToArray();
            var (first, last) = (stations[0], stations[^1]);
            return new FemFrameResponse(names[frame.ParentObjectId], first.AxialKN, last.AxialKN, first.ShearKN, last.ShearKN, first.MomentKNm, last.MomentKNm, stations);
        }).OrderBy(frame => Rank(frame.MemberName)).ThenBy(frame => frame.MemberName, StringComparer.Ordinal).ToArray();
        var cables = solved.Cables.Select(cable => new FemCableResponse(names[cable.ParentObjectId], cable.Tension, cable.Tension > 1e-8,
                cable.TargetForce ?? 0, cable.AppliedPretension, cable.FitStageTension,
                cable.FitStageTension is null ? null : cable.FitHistory.ToArray(), cable.Sag, cable.TangentStiffnessRatio))
            .OrderBy(cable => Rank(cable.MemberName)).ThenBy(cable => cable.MemberName, StringComparer.Ordinal).ToArray();
        var fit = solved.Diagnostics.CableFit;
        return new(joints, supports, frames, cables, Math.Max(1, solved.Diagnostics.NonlinearIterations), fit?.Iterations ?? 0, fit?.Failure);
    }

    /// <summary>A refusal in the user's words, with where in the application it can be changed.</summary>
    private static InvalidOperationException Refused(Native.ModelDocument document, IEnumerable<Native.ValidationIssue> issues)
    {
        var errors = issues.Where(issue => issue.Severity == Native.ValidationSeverity.Error).ToArray();
        var active = document.Settings.ActiveDofs;
        // A translation of the plane is off and the joints are not on one line across it: the engine names every
        // joint; here they are said once, with the degree of freedom that was switched off.
        var offLine = errors.Where(issue => issue.Code == "dof.geometry" && document.Points.ContainsKey(issue.ObjectId))
            .Select(issue => document.Points[issue.ObjectId].Name).Distinct(StringComparer.Ordinal).ToArray();
        var off = new[] { Native.DofComponent.UX, Native.DofComponent.UZ }.Where(component => !Native.DegreesOfFreedom.Contains(active, component)).ToArray();
        if (offLine.Length > 0 && off.Length == 1)
        {
            string axis = off[0] == Native.DofComponent.UX ? "X" : "Z";
            string joints = string.Join(", ", offLine.Take(4).Select(name => $"'{name}'")) + (offLine.Length > 4 ? $" and {offLine.Length - 4} more" : "");
            return new($"{off[0]} is switched off, but the joints of the model are not all at one {axis}: {joints} {(offLine.Length == 1 ? "is" : "are")} elsewhere. " +
                $"With {off[0]} off no joint moves in {axis}, which only a model whose joints all share one {axis} can be analysed with. " +
                $"Switch {off[0]} on {DofOptionPlace}.");
        }
        var said = errors.Select(issue => issue.Message).Distinct().ToArray();
        var outOfPlane = Native.DegreesOfFreedom.Components.Where(component => Native.DegreesOfFreedom.Contains(active & ~Native.DofMask.XZ, component)).ToArray();
        bool spatial = outOfPlane.Length > 0;
        bool unstable = errors.Any(issue => issue.Code == "static.matrix.unstable");
        // In 3D nothing holds a model of the XZ plane out of its plane. The engine holds that movement while the loads
        // leave it alone; a case that still has no result in 3D is told where its plane is chosen.
        string hint = unstable && spatial
            ? $" With {string.Join(", ", outOfPlane)} on the model is analysed in 3D, where nothing holds it out of its XZ plane. To analyse it in its plane, choose the XZ plane {DofOptionPlace}."
            : errors.Any(issue => issue.Code is "static.section.stiffness" or "modal.section.stiffness") && spatial
            ? $" Out of its plane a frame bends about its other axis and twists. Give it a section that has both (Define → Section Properties), or analyse the model in its plane: the XZ plane {DofOptionPlace}."
            : errors.Any(issue => issue.Code == "static.matrix.unstable" && issue.Message.Contains("cable", StringComparison.OrdinalIgnoreCase))
            ? " In the model: model a cable that runs through such a joint as one cable, and hold every cable end with a frame or a support."
            : errors.Any(issue => issue.Code.StartsWith("dof.", StringComparison.Ordinal)) ? $" The degrees of freedom are set {DofOptionPlace}." : "";
        return new(string.Join(" ", said.Take(3)) + (said.Length > 3 ? $" … and {said.Length - 3} more." : "") + hint);
    }
}
