using Mcotis.Analysis;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Native = Mcotis.Core;

namespace Mcotis.Desktop.Application;

/// <summary>
/// The application's analysis. It hands the model the application holds to the general solver of the standalone
/// system — <see cref="StaticSolver"/> in <c>Mcotis.Analysis</c>, the same engine the native workspace runs — and
/// gives the results back in the application's result types. No numerical method lives here.
/// <para>The application's model is planar (joints in one XZ plane, loads in X and Z, pin supports), so the engine
/// runs it with the XZ degrees of freedom. What it solves, from which sources and how it was checked is recorded in
/// <c>docs/analysis/cable-static-verification.md</c>; it replaced the planar solver on 2026-10-12, after reproducing
/// that solver's results on the same input (<c>--general-solver</c>).</para>
/// <para>The model is passed on as the application always analysed it: a frame is split at every joint within
/// <see cref="FrameJoints.OnFrameToleranceM"/> of it; frame self-weight and member loads are line loads; a cable's
/// self-weight goes to its two ends and, with cable sag on, makes it sag; the case that applies the cable target
/// forces fits them, and the cases continuing from it keep that pretension. Target forces that cannot be met give the
/// results of the closest pretension found, with the reason in <see cref="FemAnalysisResult.TargetFitFailure"/>.</para>
/// </summary>
public sealed class FemSolver
{
    public const int DefaultTargetFitIterations = 100;
    public const int DefaultFrameMeshSegments = 10;
    public const int MaximumFrameMeshSegments = 100;
    private const double PlanarityToleranceM = 1e-7;

    /// <summary>Single loading (joint loads only) with the pretension applied — the one-case entry point.</summary>
    public FemAnalysisResult Solve(
        GeometryModel geometry,
        IReadOnlyDictionary<string, FemSectionProperties> frameSections,
        IReadOnlyDictionary<string, FemSectionProperties> cableSections,
        IReadOnlyDictionary<string, double> cablePretensionsKN,
        IReadOnlyList<FemAppliedLoad> loads,
        IReadOnlyList<string> pinSupportPoints,
        CancellationToken cancellationToken = default,
        double? targetFitPercent = null,
        int maximumTargetFitIterations = DefaultTargetFitIterations,
        int frameMeshSegments = 1,
        bool cableSag = false)
    {
        var caseLoads = new FemLoadSet(loads.Select(load => new FemJointLoad(load.PointName, load.FxKN, load.FzKN)).ToArray(), [], []);
        return SolveCases(geometry, frameSections, cableSections, cablePretensionsKN, FemLoadSet.Empty,
            [new FemCaseLoads("Default", caseLoads, 1)], pinSupportPoints, cancellationToken, targetFitPercent,
            maximumTargetFitIterations, frameMeshSegments, cableSag)["Default"];
    }

    /// <summary>
    /// Solves several load cases on one geometry. With a tolerance, the cable pretension is fitted once under
    /// <paramref name="fitLoads"/> — the other loads of the case that holds the cable target-force pattern — and
    /// every case with a <see cref="FemCaseLoads.PretensionFactor"/> carries it; without one, the target is applied as
    /// the pretension itself. <paramref name="frameMeshSegments"/> is the number of intervals each frame piece is read
    /// at for diagrams and the deflected line; no result at a joint depends on it.
    /// </summary>
    public IReadOnlyDictionary<string, FemAnalysisResult> SolveCases(
        GeometryModel geometry,
        IReadOnlyDictionary<string, FemSectionProperties> frameSections,
        IReadOnlyDictionary<string, FemSectionProperties> cableSections,
        IReadOnlyDictionary<string, double> cablePretensionsKN,
        FemLoadSet fitLoads,
        IReadOnlyList<FemCaseLoads> cases,
        IReadOnlyList<string> pinSupportPoints,
        CancellationToken cancellationToken = default,
        double? targetFitPercent = null,
        int maximumTargetFitIterations = DefaultTargetFitIterations,
        int frameMeshSegments = 1,
        bool cableSag = false)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (cases.Count == 0) throw new InvalidOperationException("At least one load case is required.");
        var frameMembers = geometry.Members.Where(member => member.Kind == MemberKind.Frame).ToArray();
        if (frameMembers.Length == 0) throw new InvalidOperationException("The connected model has no frame members to analyze.");
        var cableMembers = geometry.Members.Where(member => member.Kind == MemberKind.Cable && cableSections.ContainsKey(member.Name)).ToArray();
        var usedPoints = frameMembers.Concat(cableMembers).SelectMany(member => new[] { member.Start, member.End })
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var points = usedPoints.ToDictionary(name => name, name => geometry.Points.TryGetValue(name, out var point) ? point.Position
            : throw new InvalidOperationException($"Member endpoint '{name}' is missing from geometry."), StringComparer.Ordinal);
        if (points.Values.Max(point => point.Y) - points.Values.Min(point => point.Y) > PlanarityToleranceM)
            throw new InvalidOperationException("The internal FEM solver requires all analyzed frame and cable joints to lie in one XZ plane.");
        if (frameMeshSegments is < 1 or > MaximumFrameMeshSegments)
            throw new InvalidOperationException($"Frame mesh must be between 1 and {MaximumFrameMeshSegments} segments per frame.");
        var supports = pinSupportPoints.Distinct(StringComparer.Ordinal).ToArray();
        if (supports.Length < 2 || supports.Any(name => !points.ContainsKey(name)))
            throw new InvalidOperationException("At least two valid pin-support joints connected to analyzed members are required.");
        if (targetFitPercent is double fit && (!double.IsFinite(fit) || fit <= 0 || fit >= 100))
            throw new InvalidOperationException("Target-force fitting tolerance must be greater than 0% and less than 100%.");
        if (maximumTargetFitIterations < 1) throw new InvalidOperationException("Target-force fitting needs at least one iteration.");

        try { return Analyse(); }
        catch (Native.DocumentException ex) { throw new InvalidOperationException(ex.Message, ex); }

        Dictionary<string, FemAnalysisResult> Analyse()
        {
        var editor = Native.ModelDocument.Create().Edit();
        double plane = points[usedPoints[0]].Y;
        // Identities in the order things are made, joints first by name: the engine orders its equations by identity,
        // so the same model always gives the same numbers to the last digit.
        int made = 0;
        Native.ObjectId NewId() => new(new Guid(++made, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        var pointIds = usedPoints.ToDictionary(name => name, _ => NewId(), StringComparer.Ordinal);
        // joints keep their own names, so that what the engine says about one reads as the user knows it
        foreach (var name in usedPoints) editor.AddPoint(new(points[name].X, plane, points[name].Z), name, pointIds[name]);
        editor.SetRestraint(supports.Select(name => pointIds[name]), Native.Restraint.Pinned);

        (double C, double S, double Length) Direction(StructuralMember member)
        {
            double dx = points[member.End].X - points[member.Start].X, dz = points[member.End].Z - points[member.Start].Z;
            double length = Math.Sqrt(dx * dx + dz * dz);
            if (!double.IsFinite(length) || length <= PlanarityToleranceM)
                throw new InvalidOperationException($"Member '{member.Name}' has zero or invalid XZ length.");
            return (dx / length, dz / length, length);
        }
        static void Require(string name, FemSectionProperties properties, bool inertia)
        {
            if (!double.IsFinite(properties.AreaM2) || properties.AreaM2 <= 0 ||
                !double.IsFinite(properties.ElasticModulusKNPerM2) || properties.ElasticModulusKNPerM2 <= 0 ||
                (inertia && (properties.InPlaneInertiaM4 is not double value || !double.IsFinite(value) || value <= 0)))
                throw new InvalidOperationException($"SAP section properties for '{name}' must have positive finite E, A" + (inertia ? ", and in-plane I." : "."));
        }

        // Frames: one native frame per piece between the joints lying on the member, as the application has always
        // connected them. A frame's weight reaches it as a line load, like its other member loads.
        var framePieces = new Dictionary<string, (FrameJoints.Piece Piece, Native.ObjectId Id, double C, double S, double Length)[]>(StringComparer.Ordinal);
        foreach (var member in frameMembers)
        {
            var properties = frameSections.TryGetValue(member.Name, out var found) ? found
                : throw new InvalidOperationException($"SAP section and material properties are missing for frame '{member.Name}'.");
            Require(member.Name, properties, inertia: true);
            var (material, section) = (NewId(), NewId());
            double inertia = properties.InPlaneInertiaM4!.Value;
            editor.PutMaterial(new(material, "MF:" + member.Name, Native.MaterialType.Other, properties.ElasticModulusKNPerM2, .3, 0, 0));
            editor.PutFrameSection(new(section, "SF:" + member.Name, material, Native.FrameSectionShape.General, new(properties.AreaM2, inertia, inertia, 1e-6)));
            var pieces = FrameJoints.Pieces(member, points);
            framePieces[member.Name] = pieces.Select((piece, index) =>
            {
                var id = editor.AddFrame(pointIds[piece.Member.Start], pointIds[piece.Member.End], $"F:{member.Name}#{index + 1}", NewId()).Id;
                editor.SetFrameSection([id], section);
                bool start = index == 0 && properties.ReleaseStartRotation, end = index == pieces.Count - 1 && properties.ReleaseEndRotation;
                if (start || end) editor.SetFrameReleases([id], new(new(false, false, false, false, false, start), new(false, false, false, false, false, end)));
                var (c, s, length) = Direction(piece.Member);
                return (piece, id, c, s, length);
            }).ToArray();
        }
        // The pretension stage: the case that fits the cable target forces, and the cases that carry its pretension.
        var carried = cases.Where(item => item.PretensionFactor != 0).Select(item => item.PretensionFactor).Distinct().ToArray();
        if (carried.Length > 1)
            throw new InvalidOperationException("The cases of one pretension stage scale the cable target forces by different factors; use one factor.");
        foreach (var member in cableMembers)
            if (cablePretensionsKN.GetValueOrDefault(member.Name) is var target && (!double.IsFinite(target) || target < 0))
                throw new InvalidOperationException($"Cable pretension for '{member.Name}' must be finite and nonnegative.");
        bool pretensioned = carried.Length == 1 && cableMembers.Any(member => cablePretensionsKN.GetValueOrDefault(member.Name) > 0);
        bool fitted = pretensioned && targetFitPercent is not null;
        var loadSets = (pretensioned ? new[] { fitLoads } : []).Concat(cases.Select(item => item.Loads)).ToArray();

        // Cables. A load along a cable is taken as the application has always taken it: its resultant goes to the cable's
        // two ends by statics, and — with cable sag on — the part of that resultant normal to the chord, spread over the
        // length, is the load the cable sags under. The engine's cable sags under its own weight times the load
        // pattern's self-weight factor; so each cable is given the weight, and each pattern the factor, that make those
        // the same load. For a cable loaded by its self-weight that is its self-weight and the pattern's own factor.
        var cableGeometry = cableMembers.ToDictionary(member => member.Name, Direction, StringComparer.Ordinal);
        Dictionary<string, (double StartX, double StartZ, double EndX, double EndZ, double Normal)> AlongCables(FemLoadSet set)
        {
            var sums = new Dictionary<string, (double StartX, double StartZ, double EndX, double EndZ, double Normal)>(StringComparer.Ordinal);
            foreach (var load in set.Members)
            {
                if (!cableGeometry.TryGetValue(load.MemberName, out var cable)) continue;
                if (!double.IsFinite(load.ValueStart) || !double.IsFinite(load.ValueEnd)) throw new InvalidOperationException($"Member load on '{load.MemberName}' is not finite.");
                double from = Math.Clamp(Math.Min(load.RelStart, load.RelEnd), 0, 1), to = Math.Clamp(Math.Max(load.RelStart, load.RelEnd), 0, 1);
                if (to - from <= 1e-12) continue;
                double Intensity(double relative) => load.ValueStart + (load.ValueEnd - load.ValueStart) * (relative - load.RelStart) / (load.RelEnd - load.RelStart);
                var (x, z) = LoadVector(load.Direction, cable.C, cable.S, 0);
                double start = Intensity(from), slope = (Intensity(to) - start) / (to - from);
                double total = (start + Intensity(to)) / 2 * (to - from) * cable.Length;
                // ∫ q(r)·r dr over the loaded part: the share of the far end, by moments about the start
                double far = cable.Length * (start * (to * to - from * from) / 2 +
                    slope * ((to * to * to - from * from * from) / 3 - from * (to * to - from * from) / 2)), near = total - far;
                var sum = sums.GetValueOrDefault(load.MemberName);
                sums[load.MemberName] = (sum.StartX + x * near, sum.StartZ + z * near, sum.EndX + x * far, sum.EndZ + z * far,
                    sum.Normal + (-cable.S * x + cable.C * z) * total);
            }
            return sums.ToDictionary(pair => pair.Key, pair => pair.Value with { Normal = Math.Abs(pair.Value.Normal) / cableGeometry[pair.Key].Length }, StringComparer.Ordinal);
        }
        var alongCables = loadSets.Select(AlongCables).ToArray();
        var sagWeight = cableMembers.ToDictionary(member => member.Name, _ => 0.0, StringComparer.Ordinal);
        var sagFactor = new double[loadSets.Length];
        int reference = cableSag ? Array.FindIndex(alongCables, loads => loads.Values.Any(load => load.Normal > 0)) : -1;
        if (reference >= 0)
        {
            const string proportion = "The loads along the cables are not in the same proportion to each other in every load case. With cable sag on, the solver needs them to be, as self-weight is; switch cable sag off, or make them so.";
            foreach (var (name, load) in alongCables[reference].Where(pair => pair.Value.Normal > 0))
            {
                double across = Math.Abs(cableGeometry[name].C);
                if (across < 1e-9)
                    throw new InvalidOperationException($"Cable '{name}' is vertical and loaded across its length. With cable sag on, the solver's cable sags under a vertical load only; switch cable sag off, or remove that load.");
                sagWeight[name] = load.Normal / across;
            }
            for (int index = 0; index < loadSets.Length; index++)
            {
                double? factor = null;
                foreach (var member in cableMembers)
                {
                    double normal = alongCables[index].GetValueOrDefault(member.Name).Normal, basis = alongCables[reference].GetValueOrDefault(member.Name).Normal;
                    if (basis > 0)
                    {
                        if (factor is { } known && Math.Abs(normal / basis - known) > 1e-9 * Math.Max(1, known)) throw new InvalidOperationException(proportion);
                        factor ??= normal / basis;
                    }
                    else if (normal > 0) throw new InvalidOperationException(proportion);
                }
                sagFactor[index] = factor ?? 0;
            }
        }
        var cables = new Dictionary<string, (Native.ObjectId Id, double Length, double Target)>(StringComparer.Ordinal);
        foreach (var member in cableMembers)
        {
            var properties = cableSections[member.Name];
            Require(member.Name, properties, inertia: false);
            var (material, section) = (NewId(), NewId());
            double gross = properties.GrossAreaM2 > 0 ? properties.GrossAreaM2 : properties.AreaM2;
            editor.PutMaterial(new(material, "MC:" + member.Name, Native.MaterialType.Tendon, properties.ElasticModulusKNPerM2, .3, sagWeight[member.Name] / gross, 0));
            editor.PutCableSection(new(section, "SC:" + member.Name, material, gross));
            var id = editor.AddCable(pointIds[member.Start], pointIds[member.End], "C:" + member.Name, NewId()).Id;
            editor.SetCableSection([id], section);
            editor.SetCableModifiers([id], new(properties.AreaM2 / gross, 1, 1));
            cables[member.Name] = (id, cableGeometry[member.Name].Length, cablePretensionsKN.GetValueOrDefault(member.Name));
        }

        Native.ObjectId AddPattern(string name, FemLoadSet set, int index)
        {
            var id = NewId();
            var joints = new Dictionary<string, (double X, double Z, double M)>(StringComparer.Ordinal);
            void AddJoint(string point, double x, double z, double m = 0)
            {
                var sum = joints.GetValueOrDefault(point);
                joints[point] = (sum.X + x, sum.Z + z, sum.M + m);
            }
            foreach (var load in set.Joints)
            {
                if (!pointIds.ContainsKey(load.PointName))
                    throw new InvalidOperationException($"Load point '{load.PointName}' is not connected to an analyzed member.");
                if (!double.IsFinite(load.FxKN) || !double.IsFinite(load.FzKN) || !double.IsFinite(load.MyKNm))
                    throw new InvalidOperationException($"Load at '{load.PointName}' is not finite.");
                AddJoint(load.PointName, load.FxKN, load.FzKN, load.MyKNm);
            }

            // The engine puts half a cable's weight × the pattern's factor on each of its ends; the application's own
            // end forces go in beside it, less that half.
            editor.PutLoadPattern(new(id, name, Native.LoadPatternType.Other, sagFactor[index]));
            foreach (var member in cableMembers)
            {
                var along = alongCables[index].GetValueOrDefault(member.Name);
                double carriedByWeight = sagWeight[member.Name] * sagFactor[index] * cables[member.Name].Length / 2;
                double size = Math.Abs(along.StartX) + Math.Abs(along.StartZ) + Math.Abs(along.EndX) + Math.Abs(along.EndZ) + carriedByWeight;
                bool Matters(double x, double z) => Math.Abs(x) + Math.Abs(z) > 1e-12 * size;
                if (Matters(along.StartX, along.StartZ + carriedByWeight)) AddJoint(member.Start, along.StartX, along.StartZ + carriedByWeight);
                if (Matters(along.EndX, along.EndZ + carriedByWeight)) AddJoint(member.End, along.EndX, along.EndZ + carriedByWeight);
            }

            foreach (var load in set.Members)
            {
                if (!double.IsFinite(load.ValueStart) || !double.IsFinite(load.ValueEnd)) throw new InvalidOperationException($"Member load on '{load.MemberName}' is not finite.");
                double from = Math.Clamp(Math.Min(load.RelStart, load.RelEnd), 0, 1), to = Math.Clamp(Math.Max(load.RelStart, load.RelEnd), 0, 1);
                if (to - from <= 1e-12) continue;
                double Intensity(double relative) => load.ValueStart + (load.ValueEnd - load.ValueStart) * (relative - load.RelStart) / (load.RelEnd - load.RelStart);
                if (framePieces.TryGetValue(load.MemberName, out var pieces))
                {
                    double memberLength = pieces.Sum(piece => piece.Length);
                    var (x, z) = LoadVector(load.Direction, pieces[0].C, pieces[0].S, frameSections[load.MemberName].LocalAngleDeg);
                    foreach (var piece in pieces)
                    {
                        double a = piece.Piece.FractionStart, b = piece.Piece.FractionEnd, start = Math.Max(a, from), end = Math.Min(b, to);
                        if (end - start <= 1e-12) continue;
                        // the load is per length of the member; a piece is measured along itself
                        double scale = memberLength * (b - a) / piece.Length, startValue = Intensity(start) * scale, endValue = Intensity(end) * scale;
                        double startAt = (start - a) / (b - a), endAt = (end - a) / (b - a);
                        if (x != 0) editor.PutLoad(new Native.FrameDistributedLoad(NewId(), id, piece.Id, Native.LoadDirection.GlobalX, startAt, endAt, x * startValue, x * endValue));
                        if (z != 0) editor.PutLoad(new Native.FrameDistributedLoad(NewId(), id, piece.Id, Native.LoadDirection.GlobalZ, startAt, endAt, z * startValue, z * endValue));
                    }
                }
                else if (cables.ContainsKey(load.MemberName)) continue;   // taken above, with the other loads along that cable
                else throw new InvalidOperationException($"Member load on '{load.MemberName}', which is not an analyzed frame or cable.");
            }
            foreach (var load in set.MemberPoints)
            {
                if (!double.IsFinite(load.ValueKN)) throw new InvalidOperationException($"Point load on '{load.MemberName}' is not finite.");
                if (!framePieces.TryGetValue(load.MemberName, out var pieces))
                    throw new InvalidOperationException($"Point load on '{load.MemberName}', which is not an analyzed frame.");
                var direction = load.Direction switch
                {
                    FemLoadDirection.ProjectedX => FemLoadDirection.GlobalX, FemLoadDirection.ProjectedZ => FemLoadDirection.GlobalZ,
                    FemLoadDirection.ProjectedGravity => FemLoadDirection.Gravity, var other => other,
                };
                var (x, z) = LoadVector(direction, pieces[0].C, pieces[0].S, frameSections[load.MemberName].LocalAngleDeg);
                double at = Math.Clamp(load.Rel, 0, 1);
                var piece = pieces.First(item => at <= item.Piece.FractionEnd + 1e-12);
                double onPiece = Math.Clamp((at - piece.Piece.FractionStart) / (piece.Piece.FractionEnd - piece.Piece.FractionStart), 0, 1);
                if (x != 0) editor.PutLoad(new Native.FramePointLoad(NewId(), id, piece.Id, Native.LoadDirection.GlobalX, onPiece, x * load.ValueKN));
                if (z != 0) editor.PutLoad(new Native.FramePointLoad(NewId(), id, piece.Id, Native.LoadDirection.GlobalZ, onPiece, z * load.ValueKN));
            }
            foreach (var (point, load) in joints)
                editor.PutLoad(new Native.PointLoad(NewId(), id, pointIds[point], new(load.X, 0, load.Z), new(0, load.M, 0)));
            return id;
        }

        // A case that carries the pretension continues from the fit case; the application gives each case its whole
        // loading, so the fit loads are taken off again.
        Native.ObjectId fitCase = default, fitPattern = default;
        if (pretensioned)
        {
            var targets = NewId();
            editor.PutLoadPattern(new(targets, "PT", Native.LoadPatternType.Prestress));
            foreach (var cable in cables.Values.Where(cable => cable.Target > 0))
                editor.PutLoad(new Native.CableTargetForce(NewId(), targets, cable.Id, cable.Target));
            fitPattern = AddPattern("fit loads", fitLoads, 0);
            fitCase = NewId();
            editor.PutLoadCase(new(fitCase, "fit stage", Native.LoadCaseType.NonlinearStatic, [new(targets, carried[0]), new(fitPattern, 1)]) { FitCableTargetForces = true });
        }
        var caseIds = new List<(FemCaseLoads Case, Native.ObjectId Id)>();
        foreach (var (item, number) in cases.Select((item, number) => (item, number)))
        {
            var pattern = AddPattern("loads of " + item.Name, item.Loads, (pretensioned ? 1 : 0) + number);
            var id = NewId();
            editor.PutLoadCase(pretensioned && item.PretensionFactor != 0
                ? new(id, "case " + item.Name, Native.LoadCaseType.NonlinearStatic, [new(pattern, 1), new(fitPattern, -1)]) { InitialCaseId = fitCase }
                : new(id, "case " + item.Name, Native.LoadCaseType.NonlinearStatic, [new(pattern, 1)]));
            caseIds.Add((item, id));
        }
        // Without a tolerance the target is applied as the pretension itself: that is the first fitting run, taken as it is.
        editor.SetSettings(new Native.AnalysisSettings(targetFitPercent ?? 50, fitted ? maximumTargetFitIterations : 1, frameMeshSegments, cableSag)
            { ActiveDofs = Native.DofMask.XZ });

        var document = editor.Commit().Document;
        static InvalidOperationException Refused(IEnumerable<Native.ValidationIssue> issues)
        {
            var errors = issues.Where(issue => issue.Severity == Native.ValidationSeverity.Error).ToArray();
            // In this window the degrees of freedom of cable joints are those of its plane, UX and UZ; what can be changed is the model.
            string hint = errors.Any(issue => issue.Code == "static.matrix.unstable" && issue.Message.Contains("cable", StringComparison.OrdinalIgnoreCase))
                ? " In this window a joint that only cables reach moves in UX and UZ: model a cable that runs through such a joint as one cable, and hold every cable end with a frame or a support."
                : "";
            return new(string.Join(" ", errors.Select(issue => issue.Message).Distinct().Take(3)) + hint);
        }
        var compiled = ModelCompiler.Compile(document);
        if (compiled.Model is not { } model) throw Refused(compiled.Issues);
        var wanted = caseIds.Select(item => item.Id).ToList();
        if (pretensioned) wanted.Insert(0, fitCase);
        // The engine is dense. These limits are where its memory, not its method, ends: about 1,000 joints.
        var outcomes = StaticSolver.Solve(model, wanted, new StaticOptions(MaximumFullDofs: 6000, MaximumFreeDofs: 3000, AcceptClosestFit: true), cancellationToken);
        StaticResult Result(Native.ObjectId id) => outcomes[id].Result ?? throw Refused(outcomes[id].Issues);

        var stage = pretensioned ? Result(fitCase) : null;
        var results = new Dictionary<string, FemAnalysisResult>(StringComparer.Ordinal);
        foreach (var (item, id) in caseIds)
        {
            var solved = Result(id);
            var jointResults = solved.Joints.ToDictionary(joint => joint.PointId);
            var joints = usedPoints.Select(name => jointResults[pointIds[name]].Displacements)
                // the application's slope is positive toward +Z for +X: the opposite of the rotation about global Y
                .Select((moved, index) => new FemJointResponse(usedPoints[index], moved[0], moved[2], -moved[4])).ToArray();
            var reactions = supports.Select(name => new FemSupportReaction(name, jointResults[pointIds[name]].Reactions[0], jointResults[pointIds[name]].Reactions[2])).ToArray();
            var frameResults = solved.Frames.ToDictionary(frame => frame.ParentObjectId);
            var frames = frameMembers.Select(member =>
            {
                var stations = new List<FemFrameStation>();
                foreach (var piece in framePieces[member.Name])
                {
                    var frame = frameResults[piece.Id];
                    Native.Xyz Global(IReadOnlyList<double> local, int at) => frame.Axis1 * local[at] + frame.Axis2 * local[at + 1] + frame.Axis3 * local[at + 2];
                    Native.Xyz normal = new(-piece.S, 0, piece.C);
                    foreach (var station in frame.Segments.SelectMany(segment => segment.Stations))
                    {
                        var moved = Global(station.LocalDisplacements, 0);
                        // One sign convention along the frame: axial force in tension, shear and moment as the application draws them.
                        stations.Add(new(piece.Piece.FractionStart + (piece.Piece.FractionEnd - piece.Piece.FractionStart) * station.Relative,
                            station.Actions[0], -normal.Dot(Global(station.Actions, 0)), -Global(station.Actions, 3).Y, moved.X, moved.Z));
                    }
                }
                var (first, last) = (stations[0], stations[^1]);
                return new FemFrameResponse(member.Name, first.AxialKN, last.AxialKN, first.ShearKN, last.ShearKN, first.MomentKNm, last.MomentKNm, stations);
            }).ToArray();
            var cableResults = solved.Cables.ToDictionary(cable => cable.ParentObjectId);
            var stageCables = stage?.Cables.ToDictionary(cable => cable.ParentObjectId);
            var cableResponses = cableMembers.Select(member =>
            {
                var cable = cables[member.Name];
                var now = cableResults[cable.Id];
                var atStage = stageCables?[cable.Id];
                double target = pretensioned ? cable.Target * carried[0] : cable.Target;
                return new FemCableResponse(member.Name, now.Tension, now.Tension > 1e-8, target, atStage?.AppliedPretension ?? target,
                    fitted ? atStage!.FitStageTension : null, fitted ? atStage!.FitHistory.ToArray() : null, now.Sag, now.TangentStiffnessRatio);
            }).ToArray();
            var fit = fitted ? stage!.Diagnostics.CableFit : null;
            results[item.Name] = new(joints, reactions, frames, cableResponses, Math.Max(1, solved.Diagnostics.NonlinearIterations),
                fit?.Iterations ?? 0, fit?.Failure);
        }
        return results;
        }
    }

    /// <summary>Global (X, Z) components per unit load value for a member whose I→J direction is (c, s), as the
    /// application reads SAP2000's load directions in its XZ model.</summary>
    private static (double X, double Z) LoadVector(FemLoadDirection direction, double c, double s, double localAngleDeg)
    {
        switch (direction)
        {
            case FemLoadDirection.GlobalX: return (1, 0);
            case FemLoadDirection.GlobalZ: return (0, 1);
            case FemLoadDirection.Gravity: return (0, -1);
            case FemLoadDirection.ProjectedX: return (Math.Abs(s), 0);
            case FemLoadDirection.ProjectedZ: return (0, Math.Abs(c));
            case FemLoadDirection.ProjectedGravity: return (0, -Math.Abs(c));
            case FemLoadDirection.Local1: return (c, s);
            case FemLoadDirection.Local2:
            {
                // SAP default local 2: +X for a vertical member, otherwise the in-plane normal with a +Z component.
                // A local-axis angle rotates it out of the plane; only its in-plane part (cos angle) acts here.
                var (nx, nz) = Math.Abs(c) < 1e-6 ? (1.0, 0.0) : c > 0 ? (-s, c) : (s, -c);
                double inPlane = Math.Cos(localAngleDeg * Math.PI / 180);
                return (nx * inPlane, nz * inPlane);
            }
            default: throw new InvalidOperationException($"Unsupported load direction {direction}.");
        }
    }
}
