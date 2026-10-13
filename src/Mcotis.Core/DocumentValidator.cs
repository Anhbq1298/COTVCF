namespace Mcotis.Core;

/// <summary>Integrity of a document on its own terms: finite geometry, references that resolve, unique names, no
/// dependency cycle. Whether a given analysis engine can solve the model — a frame without a section, a shell in a
/// frame solver — is a different question, answered by that engine's capability check and never here.</summary>
public static class DocumentValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(ModelDocument document)
    {
        var checker = new Checker(document);
        checker.Names();
        checker.Geometry();
        checker.Definitions();
        checker.Assignments();
        checker.Loads();
        checker.Cases();
        return checker.Issues;
    }

    private sealed class Checker(ModelDocument d)
    {
        public List<ValidationIssue> Issues { get; } = [];

        private void Error(string code, ObjectKind kind, ObjectId id, string message) => Issues.Add(new(ValidationSeverity.Error, code, kind, id, message));
        private void Warning(string code, ObjectKind kind, ObjectId id, string message) => Issues.Add(new(ValidationSeverity.Warning, code, kind, id, message));

        private string NameOf(ObjectId id) => d.TryGetNamed(id, out _, out var name) ? name : id.ToString();

        public void Names()
        {
            var counts = new Dictionary<NameScope, int>();
            void Scope<T>(ObjectKind kind, IEnumerable<T> items) where T : INamedObject
            {
                var scope = NamingService.ScopeOf(kind);
                foreach (var item in items)
                {
                    counts[scope] = counts.GetValueOrDefault(scope) + 1;
                    if (!NamingService.TryNormalize(item.Name, out var normalized) || !string.Equals(normalized, item.Name, StringComparison.Ordinal))
                        Error("name.invalid", kind, item.Id, $"'{item.Name}' is not a valid name.");
                    else if (!d.Names.TryGet(scope, item.Name, out var holder) || holder != item.Id)
                        Error("name.duplicate", kind, item.Id, $"The name '{item.Name}' is used more than once.");
                    else if (NamingService.Kinds.Any(series => NamingService.ScopeOf(series) == scope &&
                                 NamingService.SequenceNumber(item.Name, series) > d.Names.LastNumber(series)))
                        Error("name.sequence", kind, item.Id, $"The name '{item.Name}' is ahead of the name sequence it belongs to.");
                }
            }
            Scope(ObjectKind.Point, d.Points.Values);
            Scope(ObjectKind.Frame, d.Frames.Values);
            Scope(ObjectKind.Cable, d.Cables.Values);
            Scope(ObjectKind.Shell, d.Shells.Values);
            Scope(ObjectKind.Material, d.Materials.Values);
            Scope(ObjectKind.FrameSection, d.FrameSections.Values);
            Scope(ObjectKind.CableSection, d.CableSections.Values);
            Scope(ObjectKind.ShellSection, d.ShellSections.Values);
            Scope(ObjectKind.LoadPattern, d.LoadPatterns.Values);
            Scope(ObjectKind.LoadCase, d.LoadCases.Values);
            Scope(ObjectKind.LoadCombination, d.LoadCombinations.Values);
            Scope(ObjectKind.MassSource, d.MassSources.Values);
            foreach (var scope in Enum.GetValues<NameScope>())
                if (d.Names.Count(scope) != counts.GetValueOrDefault(scope))
                    Error("name.index", ObjectKind.Settings, default, $"The name index of {scope} does not match its objects.");
        }

        public void Geometry()
        {
            foreach (var point in d.Points.Values)
                if (!point.Position.IsFinite)
                    Error("point.position", ObjectKind.Point, point.Id, $"Point '{point.Name}' has a coordinate that is not a finite number.");

            foreach (var frame in d.Frames.Values) Line(ObjectKind.Frame, "frame", "Frame", frame.Id, frame.Name, frame.StartPointId, frame.EndPointId);
            foreach (var cable in d.Cables.Values) Line(ObjectKind.Cable, "cable", "Cable", cable.Id, cable.Name, cable.StartPointId, cable.EndPointId);
            foreach (var shell in d.Shells.Values) Shell(shell);
        }

        /// <summary>A frame or a cable: two points that exist, apart from each other.</summary>
        private void Line(ObjectKind kind, string code, string what, ObjectId id, string name, ObjectId startPointId, ObjectId endPointId)
        {
            if (!d.Points.TryGetValue(startPointId, out var start) || !d.Points.TryGetValue(endPointId, out var end))
                Error(code + ".point", kind, id, $"{what} '{name}' ends on a point that does not exist.");
            else if (startPointId == endPointId || start.Position.DistanceTo(end.Position) < Tolerances.Length)
                Error(code + ".length", kind, id, $"{what} '{name}' has no length: both ends are at the same position.");
        }

        private void Shell(ShellObject shell)
        {
            if (shell.PointIds.Length < 3)
            {
                Error("shell.points", ObjectKind.Shell, shell.Id, $"Shell '{shell.Name}' needs at least three points.");
                return;
            }
            if (shell.PointIds.Distinct().Count() != shell.PointIds.Length)
            {
                Error("shell.repeated", ObjectKind.Shell, shell.Id, $"Shell '{shell.Name}' lists a point more than once.");
                return;
            }
            var positions = new List<Xyz>(shell.PointIds.Length);
            foreach (var pointId in shell.PointIds)
            {
                if (!d.Points.TryGetValue(pointId, out var point))
                {
                    Error("shell.point", ObjectKind.Shell, shell.Id, $"Shell '{shell.Name}' has a corner on a point that does not exist.");
                    return;
                }
                positions.Add(point.Position);
            }
            if (positions.Any(position => !position.IsFinite)) return;   // reported on the point

            var shape = ShellGeometry.Analyze(positions);
            if (shape.HasCoincidentPoints || shape.Area < Tolerances.Area)
                Error("shell.degenerate", ObjectKind.Shell, shell.Id, $"Shell '{shell.Name}' has no area: its points coincide or lie on one line.");
            else if (shape.SelfIntersects)
                Error("shell.boundary", ObjectKind.Shell, shell.Id, $"The boundary of shell '{shell.Name}' crosses itself; list its points in order around the boundary.");
            else if (shape.PlanarityDeviation > Tolerances.Planarity)
                Warning("shell.planarity", ObjectKind.Shell, shell.Id,
                    $"Shell '{shell.Name}' is warped: a point is {shape.PlanarityDeviation * 1000:0.###} mm off its mean plane.");
        }

        public void Definitions()
        {
            foreach (var material in d.Materials.Values)
                if (!(double.IsFinite(material.ElasticModulus) && material.ElasticModulus > 0) ||
                    !(double.IsFinite(material.PoissonRatio) && material.PoissonRatio > -1 && material.PoissonRatio <= 0.5) ||
                    !(double.IsFinite(material.WeightDensity) && material.WeightDensity >= 0) ||
                    !(double.IsFinite(material.MassDensity) && material.MassDensity >= 0))
                    Error("material.value", ObjectKind.Material, material.Id,
                        $"Material '{material.Name}' needs an elastic modulus above zero, a Poisson ratio within (-1, 0.5] and densities of zero or more.");

            foreach (var section in d.FrameSections.Values)
            {
                if (!d.Materials.ContainsKey(section.MaterialId))
                    Error("section.material", ObjectKind.FrameSection, section.Id, $"Frame section '{section.Name}' uses a material that does not exist.");
                var p = section.Properties;
                if (!(double.IsFinite(p.Area) && p.Area > 0) ||
                    new[] { p.Inertia33, p.Inertia22, p.TorsionConstant, p.ShearArea2, p.ShearArea3 }.Any(value => !double.IsFinite(value) || value < 0) ||
                    section.Dimensions.Values.Any(value => !double.IsFinite(value) || value < 0))
                    Error("section.value", ObjectKind.FrameSection, section.Id,
                        $"Frame section '{section.Name}' needs an area above zero; its other properties and dimensions cannot be negative.");
            }

            foreach (var section in d.CableSections.Values)
            {
                if (!d.Materials.ContainsKey(section.MaterialId))
                    Error("section.material", ObjectKind.CableSection, section.Id, $"Cable section '{section.Name}' uses a material that does not exist.");
                if (!(double.IsFinite(section.Area) && section.Area > 0))
                    Error("section.value", ObjectKind.CableSection, section.Id, $"Cable section '{section.Name}' needs an area above zero.");
            }

            foreach (var section in d.ShellSections.Values)
            {
                if (!d.Materials.ContainsKey(section.MaterialId))
                    Error("section.material", ObjectKind.ShellSection, section.Id, $"Shell section '{section.Name}' uses a material that does not exist.");
                if (!(double.IsFinite(section.Thickness) && section.Thickness > 0))
                    Error("section.value", ObjectKind.ShellSection, section.Id, $"Shell section '{section.Name}' needs a thickness above zero.");
            }

            foreach (var pattern in d.LoadPatterns.Values)
                if (!double.IsFinite(pattern.SelfWeightMultiplier))
                    Error("pattern.value", ObjectKind.LoadPattern, pattern.Id, $"Load pattern '{pattern.Name}' has a self-weight multiplier that is not a finite number.");

            var settings = d.Settings;
            if (!DegreesOfFreedom.IsValid(settings.ActiveDofs))
                Error("settings.dofs", ObjectKind.Settings, default, "Select at least one of UX, UY, UZ, RX, RY, RZ; unknown DOF bits are invalid.");
            if (!DegreesOfFreedom.IsValid(settings.CableDofs) || (settings.CableDofs & DofMask.Translations) == 0)
                Error("settings.cableDofs", ObjectKind.Settings, default, "The degrees of freedom of cable joints need at least one of UX, UY, UZ; unknown DOF bits are invalid.");
            if (!(double.IsFinite(settings.CableFitTolerancePercent) && settings.CableFitTolerancePercent > 0) ||
                settings.CableFitMaxIterations < 1 || settings.FrameMeshSegments < 1)
                Error("settings.value", ObjectKind.Settings, default,
                    "The analysis settings need a cable fit tolerance above zero, at least one fit iteration and at least one mesh segment per frame.");
        }

        public void Assignments()
        {
            foreach (var pointId in d.Restraints.Keys)
                if (!d.Points.ContainsKey(pointId))
                    Error("restraint.point", ObjectKind.Point, pointId, "A restraint is kept for a point that does not exist.");

            foreach (var (frameId, assignment) in d.FrameAssignments)
            {
                if (!d.Frames.TryGetValue(frameId, out var frame))
                {
                    Error("assignment.frame", ObjectKind.Frame, frameId, "An assignment is kept for a frame that does not exist.");
                    continue;
                }
                if (assignment.SectionId is { } sectionId && !d.FrameSections.ContainsKey(sectionId))
                    Error("assignment.section", ObjectKind.Frame, frameId, $"Frame '{frame.Name}' is assigned a section that does not exist.");
                var m = assignment.Modifiers;
                if (!double.IsFinite(assignment.LocalAxisAngle) ||
                    new[] { m.Area, m.Inertia33, m.Inertia22, m.Torsion, m.Mass, m.Weight }.Any(value => !double.IsFinite(value) || value < 0))
                    Error("assignment.value", ObjectKind.Frame, frameId, $"Frame '{frame.Name}' needs a finite local-axis angle and modifiers of zero or more.");
            }

            foreach (var (cableId, assignment) in d.CableAssignments)
            {
                if (!d.Cables.TryGetValue(cableId, out var cable))
                {
                    Error("assignment.cable", ObjectKind.Cable, cableId, "An assignment is kept for a cable that does not exist.");
                    continue;
                }
                if (assignment.SectionId is { } sectionId && !d.CableSections.ContainsKey(sectionId))
                    Error("assignment.section", ObjectKind.Cable, cableId, $"Cable '{cable.Name}' is assigned a section that does not exist.");
                var m = assignment.Modifiers;
                if (new[] { m.Area, m.Mass, m.Weight }.Any(value => !double.IsFinite(value) || value < 0))
                    Error("assignment.value", ObjectKind.Cable, cableId, $"Cable '{cable.Name}' needs modifiers of zero or more.");
            }

            foreach (var (shellId, assignment) in d.ShellAssignments)
            {
                if (!d.Shells.TryGetValue(shellId, out var shell))
                {
                    Error("assignment.shell", ObjectKind.Shell, shellId, "An assignment is kept for a shell that does not exist.");
                    continue;
                }
                if (assignment.SectionId is { } sectionId && !d.ShellSections.ContainsKey(sectionId))
                    Error("assignment.section", ObjectKind.Shell, shellId, $"Shell '{shell.Name}' is assigned a section that does not exist.");
                if (!double.IsFinite(assignment.LocalAxisAngle))
                    Error("assignment.value", ObjectKind.Shell, shellId, $"Shell '{shell.Name}' needs a finite local-axis angle.");
            }
        }

        public void Loads()
        {
            static bool Finite(params double[] values) => values.All(double.IsFinite);
            static bool Relative(double value) => double.IsFinite(value) && value >= 0 && value <= 1;

            foreach (var load in d.Loads.Values)
            {
                if (!d.LoadPatterns.ContainsKey(load.PatternId))
                    Error("load.pattern", ObjectKind.Load, load.Id, $"A load on '{NameOf(load.TargetId)}' is in a load pattern that does not exist.");

                var (onRightObject, valuesOk, what) = load switch
                {
                    PointLoad point => (d.Points.ContainsKey(load.TargetId), point.Force.IsFinite && point.Moment.IsFinite, "A point load"),
                    FrameDistributedLoad line => (d.Frames.ContainsKey(load.TargetId),
                        Finite(line.ValueStart, line.ValueEnd) && Relative(line.RelativeStart) && Relative(line.RelativeEnd) && line.RelativeStart < line.RelativeEnd,
                        "A distributed frame load"),
                    FramePointLoad point => (d.Frames.ContainsKey(load.TargetId), Finite(point.Value) && Relative(point.RelativeDistance), "A frame point load"),
                    CableTargetForce target => (d.Cables.ContainsKey(load.TargetId),
                        double.IsFinite(target.Force) && target.Force > 0 && Relative(target.RelativeDistance), "A cable target force"),
                    ShellUniformLoad area => (d.Shells.ContainsKey(load.TargetId), Finite(area.Value), "A shell load"),
                    _ => (false, false, "A load of an unknown type"),
                };
                if (!onRightObject)
                    Error("load.target", ObjectKind.Load, load.Id, $"{what} is on an object that does not exist or cannot carry it.");
                else if (!valuesOk)
                    Error("load.value", ObjectKind.Load, load.Id,
                        $"{what} on '{NameOf(load.TargetId)}' has a value that is not valid: forces must be finite, relative distances within 0 … 1, a target force above zero.");
            }
        }

        public void Cases()
        {
            foreach (var loadCase in d.LoadCases.Values)
            {
                string label = $"Load case '{loadCase.Name}'";
                foreach (var component in loadCase.Components)
                {
                    if (!d.LoadPatterns.ContainsKey(component.PatternId))
                        Error("case.pattern", ObjectKind.LoadCase, loadCase.Id, $"{label} uses a load pattern that does not exist.");
                    if (!double.IsFinite(component.Factor))
                        Error("case.value", ObjectKind.LoadCase, loadCase.Id, $"{label} has a factor that is not a finite number.");
                }
                // what a case of its kind may carry and refer to: one set of rules, shared with the desktop's configuration
                foreach (var (code, message) in LoadCaseRules.Check(loadCase, id => d.LoadCases.GetValueOrDefault(id), d.MassSources.ContainsKey))
                    Error(code, ObjectKind.LoadCase, loadCase.Id, message);
            }

            foreach (var id in LoadCaseRules.Circular(d.LoadCases.Values))
                Error("case.cycle", ObjectKind.LoadCase, id, $"Load case '{d.LoadCases[id].Name}' depends, through the cases it continues from or takes its stiffness from, on itself.");

            var open = new HashSet<ObjectId>();
            var known = new Dictionary<ObjectId, bool>();
            bool Cyclic(ObjectId id)
            {
                if (!d.LoadCombinations.TryGetValue(id, out var combination)) return false;   // a load case ends the chain
                if (known.TryGetValue(id, out var result)) return result;
                if (!open.Add(id)) return true;
                foreach (var term in combination.Terms) result |= Cyclic(term.SourceId);
                open.Remove(id);
                return known[id] = result;
            }
            foreach (var combination in d.LoadCombinations.Values)
            {
                foreach (var term in combination.Terms)
                {
                    if (!d.LoadCases.ContainsKey(term.SourceId) && !d.LoadCombinations.ContainsKey(term.SourceId))
                        Error("combination.source", ObjectKind.LoadCombination, combination.Id, $"Load combination '{combination.Name}' uses a load case or combination that does not exist.");
                    if (!double.IsFinite(term.Factor))
                        Error("combination.value", ObjectKind.LoadCombination, combination.Id, $"Load combination '{combination.Name}' has a factor that is not a finite number.");
                }
                if (Cyclic(combination.Id))
                {
                    Error("combination.cycle", ObjectKind.LoadCombination, combination.Id, $"Load combination '{combination.Name}' contains itself, directly or through another combination.");
                    continue;
                }
                // What a combination adds or envelopes are results of static cases. Modes are not such results.
                var statics = CasesOf(combination).ToArray();
                foreach (var modal in statics.Where(item => item.Kind == CaseKind.ModalEigen))
                    Error("combination.modal", ObjectKind.LoadCombination, combination.Id,
                        $"Load combination '{combination.Name}' includes modal case '{modal.Name}': mode shapes and frequencies are not load-case results and cannot be combined.");
                // Linear results add up only about one stiffness: cases linearized about different states do not superpose.
                var stiffnesses = statics.Where(item => item.Kind == CaseKind.LinearStatic).Select(item => item.StiffnessSourceCaseId).Distinct().ToArray();
                if (combination.Type == CombinationType.LinearAdd && stiffnesses.Length > 1)
                    Warning("combination.stiffness.mixed", ObjectKind.LoadCombination, combination.Id,
                        $"Load combination '{combination.Name}' adds linear static cases that use different stiffnesses ({string.Join(", ", stiffnesses.Select(id => id is { } source ? $"the end of '{NameOf(source)}'" : "unstressed"))}): their results do not superpose as one linear scenario.");
            }

            IEnumerable<LoadCase> CasesOf(LoadCombination combination)
            {
                foreach (var term in combination.Terms)
                {
                    if (d.LoadCases.TryGetValue(term.SourceId, out var loadCase)) yield return loadCase;
                    else if (d.LoadCombinations.TryGetValue(term.SourceId, out var inner))
                        foreach (var nested in CasesOf(inner)) yield return nested;
                }
            }
        }
    }
}

/// <summary>The shape of a shell boundary: area, how far it is from flat, whether it crosses itself.</summary>
internal static class ShellGeometry
{
    public readonly record struct Shape(bool HasCoincidentPoints, double Area, double PlanarityDeviation, bool SelfIntersects);

    public static Shape Analyze(IReadOnlyList<Xyz> points)
    {
        var count = points.Count;
        var coincident = false;
        Xyz normal = default, centroid = default;
        for (var i = 0; i < count; i++)
        {
            var next = points[(i + 1) % count];
            coincident |= points[i].DistanceTo(next) < Tolerances.Length;
            normal += points[i].Cross(next);   // Newell: the sum is 2 × area × unit normal
            centroid += points[i];
        }
        centroid *= 1.0 / count;
        var area = normal.Length / 2;
        if (coincident || area < Tolerances.Area) return new(coincident, area, 0, false);

        var unit = normal * (1 / normal.Length);
        var deviation = points.Max(point => Math.Abs((point - centroid).Dot(unit)));

        // Two axes in the mean plane, then the boundary as a flat polygon.
        var (ax, ay, az) = (Math.Abs(unit.X), Math.Abs(unit.Y), Math.Abs(unit.Z));
        var least = ax <= ay && ax <= az ? new Xyz(1, 0, 0) : ay <= az ? new Xyz(0, 1, 0) : new Xyz(0, 0, 1);
        var u = least.Cross(unit);
        u *= 1 / u.Length;
        var v = unit.Cross(u);
        var flat = points.Select(point => (X: (point - centroid).Dot(u), Y: (point - centroid).Dot(v))).ToArray();

        var crosses = false;
        for (var i = 0; i < count && !crosses; i++)
            for (var j = i + 2; j < count && !crosses; j++)
            {
                if (i == 0 && j == count - 1) continue;   // the closing edge is a neighbour of the first
                crosses = Cross(flat[i], flat[(i + 1) % count], flat[j], flat[(j + 1) % count]);
            }
        return new(false, area, deviation, crosses);
    }

    private static bool Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        static double Side((double X, double Y) p, (double X, double Y) q, (double X, double Y) r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        const double eps = Tolerances.Length * Tolerances.Length;
        double d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
        return ((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps));
    }
}
