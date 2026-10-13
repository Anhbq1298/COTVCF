namespace Mcotis.Core;

/// <summary>What a load case of each kind may carry, and what it may refer to. One place decides it: the document
/// validator uses it, and so does anything that holds load cases in another form — the desktop's configuration file —
/// so that both refuse the same things for the same reasons (docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md §2).
/// <code>
///                  continues   uses stiffness   P-Delta    fits cable   iteration   modal     loads
///                  from a case of a case                   targets      controls    options
/// Linear static       no          may            no          no           no          no       yes
/// Nonlinear static    may         no             may         may          may         no       yes
/// Initial P-Delta     may         no             always      no           may         no       yes
/// Modal               no          may            no          no           no          needs    none
/// </code>
/// A case that is continued from, or whose stiffness is used, is a nonlinear static or an initial P-Delta case: only
/// those end in a converged state.</summary>
public static class LoadCaseRules
{
    /// <param name="caseOf">The case with this id, or null when there is none.</param>
    /// <returns>Every rule the case breaks, by its stable code, with a sentence for the user that names the case.</returns>
    public static IEnumerable<(string Code, string Message)> Check(LoadCase loadCase, Func<ObjectId, LoadCase?> caseOf, Func<ObjectId, bool> massSourceExists)
    {
        string label = $"Load case '{loadCase.Name}'";
        if (!Enum.IsDefined(loadCase.Kind) || !Enum.IsDefined(loadCase.GeometricMode))
        {
            yield return ("case.kind", $"{label} is of a kind, or has a geometric nonlinearity, that this version does not know.");
            yield break;
        }
        bool nonlinear = loadCase.ProducesState, modal = loadCase.Kind == CaseKind.ModalEigen;
        string kind = loadCase.Kind switch
        {
            CaseKind.LinearStatic => "linear static", CaseKind.NonlinearStatic => "nonlinear static",
            CaseKind.InitialPDelta => "an initial P-Delta case", _ => "a modal case",
        };

        // Continuing from a case and using the stiffness of a case are two references of two kinds. Either one points
        // to the converged end of a nonlinear case; a linear static or a modal case ends in none.
        if (loadCase.InitialCaseId is { } initial)
        {
            if (!nonlinear)
                yield return ("case.initial.kind", $"{label} is {kind}: it does not continue from another case. To use the stiffness at the end of a nonlinear case, set that case as its stiffness source.");
            if (caseOf(initial) is not { } parent)
                yield return ("case.initial", $"{label} continues from a load case that does not exist.");
            else if (!parent.ProducesState)
                yield return ("case.initial.producer", $"{label} continues from '{parent.Name}', which is not a nonlinear static or initial P-Delta case: only those end in a state that can be continued.");
        }
        if (loadCase.StiffnessSourceCaseId is { } stiffness)
        {
            if (nonlinear)
                yield return ("case.stiffness.kind", $"{label} is {kind}: it forms its own stiffness. To start from the end of another case, continue from it.");
            if (caseOf(stiffness) is not { } source)
                yield return ("case.stiffness", $"{label} uses the stiffness of a load case that does not exist.");
            else if (!source.ProducesState)
                yield return ("case.stiffness.producer", $"{label} uses the stiffness of '{source.Name}', which is not a nonlinear static or initial P-Delta case: only those end in a stiffness that can be used.");
        }

        if (loadCase.GeometricMode != GeometricNonlinearity.None && !nonlinear)
            yield return ("case.geometric", $"{label} is {kind}: geometric nonlinearity belongs to a nonlinear static or an initial P-Delta case.");
        if (loadCase.Kind == CaseKind.InitialPDelta && loadCase.GeometricMode != GeometricNonlinearity.PDelta)
            yield return ("case.geometric", $"{label} is an initial P-Delta case: its geometric nonlinearity is P-Delta.");
        if (loadCase.FitCableTargetForces && loadCase.Kind != CaseKind.NonlinearStatic)
            yield return ("case.fit", $"{label} is {kind}: cable target forces are fitted in a nonlinear static case.");
        if (loadCase.NonlinearControls is { } controls)
        {
            if (!nonlinear)
                yield return ("case.controls", $"{label} is {kind}: it has no iteration to control.");
            if (controls.MaxLoadSteps < 1 || controls.MaxIterationsPerStep < 1 ||
                !(double.IsFinite(controls.RelativeResidualTolerance) && controls.RelativeResidualTolerance > 0) ||
                !(double.IsFinite(controls.RelativeDisplacementTolerance) && controls.RelativeDisplacementTolerance > 0))
                yield return ("case.controls.value", $"{label} needs at least one load step, at least one iteration per step and tolerances above zero.");
        }
        if (modal)
        {
            if (loadCase.Components.Length > 0)
                yield return ("case.modal.loads", $"{label} is a modal case: it has no loads. Its stiffness and its mass decide its modes.");
            if (loadCase.ModalOptions is not { } options)
                yield return ("case.modal", $"{label} is a modal case: it needs a mass source and a number of modes.");
            else
            {
                if (!massSourceExists(options.MassSourceId))
                    yield return ("case.modal.massSource", $"{label} uses a mass source that does not exist.");
                if (options.RequestedModes < 1 || !Enum.IsDefined(options.MassMatrixKind))
                    yield return ("case.modal.value", $"{label} needs at least one mode and a known kind of mass matrix.");
            }
        }
        else if (loadCase.ModalOptions is not null)
            yield return ("case.modal", $"{label} is {kind}: modal options belong to a modal case.");
    }

    /// <summary>The cases that depend on themselves through the cases they continue from or take their stiffness from.</summary>
    public static IEnumerable<ObjectId> Circular(IEnumerable<LoadCase> cases)
    {
        var byId = cases.ToDictionary(item => item.Id);
        var visiting = new HashSet<ObjectId>();
        var settled = new Dictionary<ObjectId, bool>();
        bool Visit(ObjectId id)
        {
            if (!byId.TryGetValue(id, out var loadCase)) return false;
            if (settled.TryGetValue(id, out var result)) return result;
            if (!visiting.Add(id)) return true;
            foreach (var dependency in loadCase.Dependencies.Where(item => item.Kind != CaseDependencyKind.MassSource)) result |= Visit(dependency.SourceId);
            visiting.Remove(id);
            return settled[id] = result;
        }
        return byId.Keys.Where(Visit).OrderBy(id => id).ToArray();
    }
}
