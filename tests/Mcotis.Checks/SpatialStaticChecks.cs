using System.Text.Json;
using Mcotis.Analysis;
using Mcotis.Core;
using static Check;

internal static class SpatialStaticChecks
{
    private static ObjectId Id(int group, int number = 1) => ObjectId.Parse($"00000000-0000-0000-{group:D4}-{number:D12}");
    private static ObjectId Point(int number) => Id(1, number);
    private static ObjectId Frame(int number) => Id(2, number);
    private static readonly ObjectId Material = Id(3), Section = Id(4), Pattern = Id(5), Case = Id(6);
    private const double E = 2e8, A = .01, Iy = .0004, Iz = .0008, J = .00001, L = 4;
    private static JsonElement spec;

    /// <summary>Fast gate: closed-form 3D solutions, equilibrium and safe rejection of unstable systems.
    /// The exhaustive element matrix and OpenSees comparisons stay in Run() for full verification.</summary>
    public static void RunSmoke()
    {
        using var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spatial-static-spec.json")));
        spec = input.RootElement;
        CantileversAndLoads();
        GlobalEquilibrium();
        FailureCases();
    }

    public static void Run()
    {
        using var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spatial-static-spec.json")));
        spec = input.RootElement;
        ElementAndFactorization();
        CantileversAndLoads();
        GlobalEquilibrium();
        TransformationsAndScaling();
        FailureCases();
        CrossCheckOpenSees();
    }

    /// <summary>Every benchmark model as a free body: its applied loads, its support reactions and the constraint forces
    /// of the DOFs a plane preset suppresses sum to zero force and to zero moment about the origin. The applied
    /// resultants are worked out here from the stored loads, not taken from the solver's load vector.</summary>
    private static void GlobalEquilibrium()
    {
        foreach (var item in spec.GetProperty("cases").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            var document = Build(item);
            var result = Solve(document);
            Xyz force = default, moment = default;
            void Add(Xyz at, Xyz applied, Xyz couple = default)
            {
                force += applied;
                moment += at.Cross(applied) + couple;
            }
            foreach (var joint in result.Joints)
            {
                var (r, s) = (joint.Reactions, joint.SuppressedDofForces);
                Add(document.Points[joint.PointId].Position, new(r[0] + s[0], r[1] + s[1], r[2] + s[2]), new(r[3] + s[3], r[4] + s[4], r[5] + s[5]));
            }
            foreach (var load in document.Loads.Values)
            {
                if (load is PointLoad nodal)
                {
                    Add(document.Points[nodal.TargetId].Position, nodal.Force, nodal.Moment);
                    continue;
                }
                var frame = document.Frames[load.TargetId];
                var axes = result.Frames.Single(frameResult => frameResult.ParentObjectId == frame.Id);
                var start = document.Points[frame.StartPointId].Position;
                var along = document.Points[frame.EndPointId].Position - start;
                Xyz Unit(LoadDirection direction) => direction switch
                {
                    LoadDirection.GlobalX => new(1, 0, 0), LoadDirection.GlobalY => new(0, 1, 0), LoadDirection.GlobalZ => new(0, 0, 1),
                    LoadDirection.Gravity => new(0, 0, -1), LoadDirection.Local1 => axes.Axis1, LoadDirection.Local2 => axes.Axis2, _ => axes.Axis3,
                };
                if (load is FramePointLoad point) Add(start + along * point.RelativeDistance, Unit(point.Direction) * point.Value);
                else if (load is FrameDistributedLoad line)
                {
                    // a linearly varying line load: its resultant acts at the centroid of the trapezoid
                    double span = line.RelativeEnd - line.RelativeStart, total = (line.ValueStart + line.ValueEnd) / 2 * span * along.Length;
                    double centroid = line.ValueStart + line.ValueEnd == 0 ? 0.5 : (line.ValueStart + 2 * line.ValueEnd) / (3 * (line.ValueStart + line.ValueEnd));
                    Add(start + along * (line.RelativeStart + span * centroid), Unit(line.Direction) * total);
                }
            }
            Near(force.Length, 0, name + ": applied loads and reactions are in force equilibrium", 1e-8);
            Near(moment.Length, 0, name + ": applied loads and reactions are in moment equilibrium about the origin", 1e-7);
        }
    }

    private static void Near(double actual, double expected, string description, double absolute = 1e-10, double relative = 1e-9) =>
        Assert(double.IsFinite(actual) && Math.Abs(actual - expected) <= absolute + relative * Math.Abs(expected),
            $"{description}: {actual:G17} vs {expected:G17}");

    private static Xyz Vector(JsonElement value) => new(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());

    private static ModelDocument Build(JsonElement item)
    {
        var editor = ModelDocument.Create().Edit();
        editor.PutMaterial(new(Material, "Steel", MaterialType.Steel, E, .3, 77, 7.85));
        editor.PutFrameSection(new(Section, "Beam", Material, FrameSectionShape.General, new(A, Iz, Iy, J)));
        var nodes = item.GetProperty("nodes");
        var fix = item.GetProperty("fix");
        for (int i = 0; i < nodes.GetArrayLength(); i++)
        {
            editor.AddPoint(Vector(nodes[i]), "P" + (i + 1), Point(i + 1));
            bool Fixed(int component) => fix[i][component].GetInt32() != 0;
            editor.SetRestraint([Point(i + 1)], new(Fixed(0), Fixed(1), Fixed(2), Fixed(3), Fixed(4), Fixed(5)));
        }
        editor.PutLoadPattern(new(Pattern, "LOAD", LoadPatternType.Other));
        int loadId = 0, frameId = 0;
        foreach (var frame in item.GetProperty("frames").EnumerateArray())
        {
            var id = Frame(++frameId);
            editor.AddFrame(Point(frame.GetProperty("i").GetInt32()), Point(frame.GetProperty("j").GetInt32()), "F" + frameId, id);
            editor.SetFrameSection([id], Section);
            if (frame.TryGetProperty("roll", out var roll)) editor.SetFrameLocalAxis([id], roll.GetDouble());
            int releaseY = frame.TryGetProperty("releasey", out var ry) ? ry.GetInt32() : 0;
            int releaseZ = frame.TryGetProperty("releasez", out var rz) ? rz.GetInt32() : 0;
            EndRelease Released(int end) => new(false, false, false, false, (releaseY & end) != 0, (releaseZ & end) != 0);
            editor.SetFrameReleases([id], new(Released(1), Released(2)));
            foreach (var (key, local) in new[] { ("udlGlobal", false), ("udlLocal", true) })
                if (frame.TryGetProperty(key, out var line))
                    for (int direction = 0; direction < 3; direction++)
                        if (line[direction].GetDouble() != 0)
                            editor.PutLoad(new FrameDistributedLoad(Id(7, ++loadId), Pattern, id,
                                (LoadDirection)(direction + (local ? 3 : 0)), 0, 1, line[direction].GetDouble(), line[direction].GetDouble()));
            if (frame.TryGetProperty("pointGlobal", out var point))
                for (int direction = 0; direction < 3; direction++)
                    if (point[direction].GetDouble() != 0)
                        editor.PutLoad(new FramePointLoad(Id(7, ++loadId), Pattern, id, (LoadDirection)direction,
                            frame.GetProperty("pointAt").GetDouble(), point[direction].GetDouble()));
        }
        if (item.TryGetProperty("nodal", out var nodal))
            foreach (var load in nodal.EnumerateArray())
            {
                var values = load.GetProperty("values");
                editor.PutLoad(new PointLoad(Id(7, ++loadId), Pattern, Point(load.GetProperty("node").GetInt32()),
                    new(values[0].GetDouble(), values[1].GetDouble(), values[2].GetDouble()),
                    new(values[3].GetDouble(), values[4].GetDouble(), values[5].GetDouble())));
            }
        editor.PutLoadCase(new(Case, item.GetProperty("name").GetString()!, LoadCaseType.LinearStatic, [new(Pattern, 1)]));
        editor.SetSettings(AnalysisSettings.Default with { ActiveDofs = Enum.Parse<DofMask>(item.GetProperty("mask").GetString()!), FrameMeshSegments = 1, CableSag = false });
        return editor.Commit().Document;
    }

    private static ModelDocument Named(string name) => Build(spec.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name));

    private static CompiledModel Compile(ModelDocument document)
    {
        var compiled = ModelCompiler.Compile(document, 17);
        Assert(compiled.Success, "Benchmark input compiles: " + string.Join("; ", compiled.Issues));
        return compiled.Model!;
    }

    private static StaticResult Solve(ModelDocument document)
    {
        var outcome = StaticSolver.Solve(Compile(document), Case);
        Assert(outcome.Success, "Static solution available: " + string.Join("; ", outcome.Issues));
        Assert(outcome.Result!.Diagnostics.ScaledResidual < 1e-10, "Scaled backward error is within the configured acceptance tolerance");
        Assert(outcome.Result.InputHash == document.AnalysisInputHash && outcome.Result.Revision == 17, "Results retain snapshot identity and revision");
        return outcome.Result;
    }

    private static ModelDocument Unloaded()
    {
        var document = Named("combined-cantilever");
        var editor = document.Edit();
        editor.Remove(document.Loads.Keys);
        return editor.Commit().Document;
    }

    private static void ElementAndFactorization()
    {
        var k = SpatialFrameElement.LocalStiffness(L, E * A, E * Iy, E * Iz, E / 2.6 * J);
        // Independent audit of every entry, structural zeros and signs included: EA and GJ from the constant axial and
        // twist strains, EI from 2-point Gauss integration of the products of the Hermite curvatures (exact — the
        // integrand is quadratic). It does not use the element's own matrix or the printed eq. 4.33.
        var integrated = new double[12, 12];
        for (int i = 0; i < 12; i++)
        for (int j = 0; j < 12; j++)
        {
            double axI = i == 0 ? -1 / L : i == 6 ? 1 / L : 0, axJ = j == 0 ? -1 / L : j == 6 ? 1 / L : 0;
            double torI = i == 3 ? -1 / L : i == 9 ? 1 / L : 0, torJ = j == 3 ? -1 / L : j == 9 ? 1 / L : 0;
            integrated[i, j] = L * (E * A * axI * axJ + E / 2.6 * J * torI * torJ);
        }
        foreach (double t in new[] { (1 - 1 / Math.Sqrt(3)) / 2, (1 + 1 / Math.Sqrt(3)) / 2 })
        {
            double[] bY = new double[12], bZ = new double[12];
            bY[1] = (-6 + 12 * t) / (L * L); bY[5] = (-4 + 6 * t) / L;
            bY[7] = (6 - 12 * t) / (L * L); bY[11] = (-2 + 6 * t) / L;
            bZ[2] = bY[1]; bZ[4] = -bY[5]; bZ[8] = bY[7]; bZ[10] = -bY[11];
            for (int i = 0; i < 12; i++)
            for (int j = 0; j < 12; j++) integrated[i, j] += L / 2 * E * (Iz * bY[i] * bY[j] + Iy * bZ[i] * bZ[j]);
        }
        for (int i = 0; i < 12; i++)
        for (int j = 0; j < 12; j++) Near(k[i, j], integrated[i, j], $"12x12 stiffness entry [{i},{j}]", 1e-8, 1e-12);
        for (int mode = 0; mode < 6; mode++)
        {
            var rigid = new double[12]; rigid[mode] = rigid[mode + 6] = 1;
            if (mode == 4) rigid[8] = -L;
            if (mode == 5) rigid[7] = L;
            foreach (var value in DenseAlgebra.Multiply(k, rigid)) Near(value, 0, "Each of the six element rigid-body motions has zero force", 1e-8);
        }
        double[,] system = { { 4, 1, 1 }, { 1, 3, 0 }, { 1, 0, 2 } };
        var x = new ScaledCholesky(system, 1e-12).Solve([5, -5, 7]);
        for (int i = 0; i < 3; i++) Near(x[i], new[] { 1.0, -2, 3 }[i], "Independent hand-solvable SPD system");
        double[] scaling = [1e-9, 1, 1e9];
        var scaled = new double[3, 3];
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++) scaled[i, j] = scaling[i] * system[i, j] * scaling[j];
        var scaledX = new ScaledCholesky(scaled, 1e-12).Solve([5e-9, -5, 7e9]);
        for (int i = 0; i < 3; i++) Near(scaledX[i] * scaling[i], x[i], "Diagonal equilibration preserves the solution across 18 orders of scaling");
        Throws<FactorizationFailure>(() => new ScaledCholesky(new double[,] { { 1, 1 }, { 1, 1 } }, 1e-12), "Singular matrices are refused even with no load");
        Throws<FactorizationFailure>(() => new ScaledCholesky(new double[,] { { 1, 1 - 1e-14 }, { 1 - 1e-14, 1 } }, 1e-12), "Near-singularity is refused at the stated pivot tolerance");
    }

    private static void CantileversAndLoads()
    {
        for (int component = 0; component < 6; component++)
        {
            var editor = Unloaded().Edit();
            var force = new double[6]; force[component] = 7;
            editor.PutLoad(new PointLoad(Id(7), Pattern, Point(2), new(force[0], force[1], force[2]), new(force[3], force[4], force[5])));
            var result = Solve(editor.Commit().Document);
            var expected = new double[6];
            switch (component)
            {
                case 0: expected[0] = 7 * L / (E * A); break;
                case 1: expected[1] = 7 * Math.Pow(L, 3) / (3 * E * Iy); expected[5] = 7 * L * L / (2 * E * Iy); break;
                case 2: expected[2] = 7 * Math.Pow(L, 3) / (3 * E * Iz); expected[4] = -7 * L * L / (2 * E * Iz); break;
                case 3: expected[3] = 7 * L / (E / 2.6 * J); break;
                case 4: expected[2] = -7 * L * L / (2 * E * Iz); expected[4] = 7 * L / (E * Iz); break;
                case 5: expected[1] = 7 * L * L / (2 * E * Iy); expected[5] = 7 * L / (E * Iy); break;
            }
            var tip = result.Joints.Single(joint => joint.PointId == Point(2));
            for (int c = 0; c < 6; c++) Near(tip.Displacements[c], expected[c], "Cantilever closed-form displacement/rotation");
            var externalMoment = new Xyz(L, 0, 0).Cross(new(force[0], force[1], force[2])) + new Xyz(force[3], force[4], force[5]);
            double[] reactions = [-force[0], -force[1], -force[2], -externalMoment.X, -externalMoment.Y, -externalMoment.Z];
            var support = result.Joints.Single(joint => joint.PointId == Point(1));
            for (int c = 0; c < 6; c++) Near(support.Reactions[c], reactions[c], "Cantilever full force/moment equilibrium", 1e-8);
        }
        var uniform = Solve(Named("uniform-cantilever"));
        Near(uniform.Joints.Single(j => j.PointId == Point(2)).Displacements[2], -2 * Math.Pow(L, 4) / (8 * E * Iz), "Uniform cantilever tip deflection");
        Near(uniform.Joints.Single(j => j.PointId == Point(1)).Reactions[4], -2 * L * L / 2, "Uniform cantilever root moment", 1e-8);
        var propped = Solve(Named("released-propped-beam"));
        Near(propped.Joints.Single(j => j.PointId == Point(1)).Reactions[2], 5 * 2 * L / 8, "Propped cantilever fixed-end vertical reaction", 1e-8);
        Near(propped.Joints.Single(j => j.PointId == Point(2)).Reactions[2], 3 * 2 * L / 8, "Propped cantilever pin vertical reaction", 1e-8);
        Near(propped.Frames[0].LocalEndActions[11], 0, "Released end has zero moment under member load", 1e-8);
        Assert(Math.Abs(propped.Frames[0].LocalDisplacements[11]) > 0 && propped.Diagnostics.UnconnectedRotations.Length == 1,
            "Member-end rotation is recovered; disconnected joint rotation is explicitly undetermined");
        var legacy = Solve(Named("legacy-xz-two-element"));
        Near(legacy.Joints.Single(j => j.PointId == Point(2)).Displacements[2], -100 * Math.Pow(10, 3) / (48 * E * Iz), "Legacy XZ analytical beam result");
        Near(legacy.Joints.Single(j => j.PointId == Point(1)).Reactions[2], 50, "Legacy XZ beam reaction", 1e-8);

        var partial = Unloaded().Edit();
        partial.PutLoad(new FrameDistributedLoad(Id(7), Pattern, Frame(1), LoadDirection.GlobalZ, .2, .9, -2, -7));
        var triangular = Solve(partial.Commit().Document);
        double start = .2 * L, end = .9 * L, slope = (-7 + 2) / (end - start), intercept = -2 - slope * start;
        double DeflectionPrimitive(double x) => (intercept * (L * Math.Pow(x, 3) - Math.Pow(x, 4) / 4) +
            slope * (.75 * L * Math.Pow(x, 4) - Math.Pow(x, 5) / 5)) / (6 * E * Iz);
        double ForcePrimitive(double x) => intercept * x + slope * x * x / 2;
        double MomentPrimitive(double x) => intercept * x * x / 2 + slope * Math.Pow(x, 3) / 3;
        Near(triangular.Joints.Single(j => j.PointId == Point(2)).Displacements[2], DeflectionPrimitive(end) - DeflectionPrimitive(start), "Partial triangular load versus virtual-work antiderivative");
        Near(triangular.Joints.Single(j => j.PointId == Point(1)).Reactions[2], ForcePrimitive(start) - ForcePrimitive(end), "Partial triangular force equilibrium", 1e-8);
        Near(triangular.Joints.Single(j => j.PointId == Point(1)).Reactions[4], MomentPrimitive(end) - MomentPrimitive(start), "Partial triangular moment equilibrium", 1e-8);
        var selfWeight = Unloaded().Edit();
        selfWeight.PutLoadPattern(new(Pattern, "LOAD", LoadPatternType.Dead, 1));
        selfWeight.SetFrameModifiers([Frame(1)], FrameModifiers.Identity with { Weight = 2, Area = 3 });
        var weighted = Solve(selfWeight.Commit().Document);
        Near(weighted.Joints.Single(j => j.PointId == Point(1)).Reactions[2], 77 * A * 2 * L, "Self-weight uses physical area and weight modifier without duplicating stiffness area modifier", 1e-8);
        var supportLoad = Unloaded().Edit();
        supportLoad.PutLoad(new PointLoad(Id(7), Pattern, Point(1), new(3, -4, 5), new(6, -7, 8)));
        var loadedSupport = Solve(supportLoad.Commit().Document).Joints.Single(j => j.PointId == Point(1));
        for (int i = 0; i < 6; i++) Near(loadedSupport.Reactions[i], -new[] { 3.0, -4, 5, 6, -7, 8 }[i], "Applied support forces/moments are retained in reaction recovery");
    }

    private static void TransformationsAndScaling()
    {
        foreach (var name in new[] { "combined-cantilever", "vertical-cantilever", "inclined-cantilever", "rolled-cantilever" })
        {
            var doc = Named(name);
            var element = new SpatialFrameElement(doc, doc.Frames[Frame(1)]);
            Near(element.Axis1.Dot(element.Axis2), 0, "Local axes orthogonal");
            Near(element.Axis1.Dot(element.Axis3), 0, "Local axes orthogonal");
            Near(element.Axis2.Dot(element.Axis3), 0, "Local axes orthogonal");
            Near(element.Axis1.Cross(element.Axis2).Dot(element.Axis3), 1, "Frame orientation has determinant +1");
            Xyz vector = new(1.2, -3.4, 5.6);
            Near(element.ToGlobal(element.ToLocal(vector)).DistanceTo(vector), 0, "Global/local transformation round-trip");
        }
        // Isotropic bending section lets arbitrary rigid rotation change the default transverse basis without changing physics.
        var original = Named("combined-cantilever");
        var isotropic = original.Edit();
        isotropic.PutFrameSection(original.FrameSections[Section] with { Properties = new(A, Iz, Iz, J) });
        var baseDocument = isotropic.Commit().Document;
        var baseResult = Solve(baseDocument);
        Xyz axis = new Xyz(1, 2, 3) * (1 / Math.Sqrt(14)); double angle = .73;
        Xyz Rotate(Xyz v) => v * Math.Cos(angle) + axis.Cross(v) * Math.Sin(angle) + axis * (axis.Dot(v) * (1 - Math.Cos(angle)));
        var rotated = baseDocument.Edit();
        rotated.MovePoint(Point(2), Rotate(new(L, 0, 0)));
        foreach (var load in baseDocument.Loads.Values.OfType<PointLoad>()) rotated.PutLoad(load with { Force = Rotate(load.Force), Moment = Rotate(load.Moment) });
        var rotatedResult = Solve(rotated.Commit().Document);
        for (int group = 0; group < 2; group++)
        {
            var tip = baseResult.Joints.Single(j => j.PointId == Point(2)).Displacements;
            var expected = Rotate(new(tip[group * 3], tip[group * 3 + 1], tip[group * 3 + 2]));
            var actual = rotatedResult.Joints.Single(j => j.PointId == Point(2)).Displacements;
            Near(new Xyz(actual[group * 3], actual[group * 3 + 1], actual[group * 3 + 2]).DistanceTo(expected), 0, "Rigid rotation preserves vector displacement/rotation", 1e-10);
        }
        var scaled = baseDocument.Edit();
        scaled.PutMaterial(baseDocument.Materials[Material] with { ElasticModulus = E * 3 });
        var stiffResult = Solve(scaled.Commit().Document);
        for (int i = 0; i < 6; i++)
        {
            Near(stiffResult.Joints.Single(j => j.PointId == Point(2)).Displacements[i] * 3,
                baseResult.Joints.Single(j => j.PointId == Point(2)).Displacements[i], "Stiffness scaling scales displacement inversely");
            Near(stiffResult.Joints.Single(j => j.PointId == Point(1)).Reactions[i],
                baseResult.Joints.Single(j => j.PointId == Point(1)).Reactions[i], "Stiffness scaling preserves equilibrium reactions", 1e-8);
        }
        var custom = Unloaded().Edit();
        custom.SetSettings(AnalysisSettings.Default with { ActiveDofs = DofMask.UX | DofMask.UZ });
        custom.PutLoad(new PointLoad(Id(7), Pattern, Point(2), new(0, 0, 7)));
        var guided = Solve(custom.Commit().Document);
        Near(guided.Joints.Single(j => j.PointId == Point(2)).Displacements[2], 7 * Math.Pow(L, 3) / (12 * E * Iz), "Custom mask suppressing all rotations behaves as a guided end, not a free cantilever");
        Assert(guided.Joints.Single(j => j.PointId == Point(2)).SuppressedDofForces.Any(value => Math.Abs(value) > 0), "Suppressed DOFs retain constraint forces separately from physical support reactions");
    }

    private static void FailureCases()
    {
        void Refuse(ModelDocument document, string code, StaticOptions? options = null)
        {
            var hash = document.AnalysisInputHash;
            var outcome = StaticSolver.Solve(Compile(document), Case, options);
            Assert(outcome.Result is null && outcome.Issues.Any(issue => issue.Code == code), "Refused safely with diagnostic " + code);
            Assert(document.AnalysisInputHash == hash, "Solving/refusing never edits its native snapshot");
        }
        // A case that no method can solve as it is defined is refused where it is defined: by the document, before any solver.
        void Invalid(DocumentEditor edit, string code)
        {
            string? refused = null;
            try { edit.Commit(); }
            catch (DocumentException exception) { refused = string.Join(", ", exception.Issues.Select(issue => issue.Code)); }
            Assert(refused is not null && refused.Split(", ").Contains(code), $"Refused by the document with {code} ({refused ?? "it was accepted"})");
        }
        var unsupported = Unloaded().Edit();
        var earlier = ObjectId.New();
        unsupported.PutLoadCase(new(earlier, "Earlier", LoadCaseType.LinearStatic, [new(Pattern, 1)]));
        unsupported.PutLoadCase(new(Case, "Continued", LoadCaseType.LinearStatic, [new(Pattern, 1)]) { InitialCaseId = earlier });
        Invalid(unsupported, "case.initial.kind");
        var fitting = Unloaded().Edit();
        fitting.PutLoadCase(new(Case, "Fitting", LoadCaseType.LinearStatic, [new(Pattern, 1)]) { FitCableTargetForces = true });
        Invalid(fitting, "case.fit");
        var afterLinear = Unloaded().Edit();
        afterLinear.PutLoadCase(new(earlier, "Earlier", LoadCaseType.LinearStatic, [new(Pattern, 1)]));
        afterLinear.PutLoadCase(new(Case, "Continued", LoadCaseType.NonlinearStatic, [new(Pattern, 1)]) { InitialCaseId = earlier });
        Invalid(afterLinear, "case.initial.producer");
        var mechanism = Unloaded().Edit();
        mechanism.SetRestraint([Point(1)], Restraint.Free);
        Refuse(mechanism.Commit().Document, "static.matrix.unstable");
        var shearRelease = Unloaded().Edit();
        shearRelease.SetFrameReleases([Frame(1)], new(new(false, true, false, false, false, false), EndRelease.None));
        Refuse(shearRelease.Commit().Document, "static.release.unsupported");
        Refuse(Unloaded(), "static.size", new(MaximumFullDofs: 6));
        var zeroTorsion = Unloaded().Edit();
        zeroTorsion.PutFrameSection(Unloaded().FrameSections[Section] with { Properties = new(A, Iz, Iy, 0) });
        Refuse(zeroTorsion.Commit().Document, "static.section.stiffness");
        var loadedRelease = Named("released-propped-beam").Edit();
        loadedRelease.PutLoad(new PointLoad(ObjectId.New(), Pattern, Point(2), default, new(0, 5, 0)));
        Refuse(loadedRelease.Commit().Document, "static.matrix.unstable");
        var overflow = Unloaded().Edit();
        overflow.PutLoad(new PointLoad(Id(7), Pattern, Point(2), new(1e308, 0, 0)));
        overflow.PutLoadCase(new(Case, "Overflow", LoadCaseType.LinearStatic, [new(Pattern, 1e308)]));
        Refuse(overflow.Commit().Document, "static.assembly.nonfinite");
        Throws<OperationCanceledException>(() => StaticSolver.Solve(Compile(Unloaded()), Case, cancellationToken: new(true)), "Cancellation never returns a completed result");
        var compiled = Compile(Unloaded());
        Assert(!StaticSolver.Solve(compiled, ObjectId.New()).Success, "A missing case fails explicitly");
        var restrained = Unloaded().Edit(); restrained.SetRestraint([Point(2)], Restraint.Fixed);
        Assert(Solve(restrained.Commit().Document).Diagnostics.SolvedDofCount == 0, "A fully restrained model recovers reactions without inventing free equations");
    }

    private static void CrossCheckOpenSees()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spatial-static-opensees.json")));
        double maximumDisplacementDifference = 0, maximumActionDifference = 0;
        int compared = 0;
        foreach (var reference in file.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = reference.GetProperty("name").GetString()!;
            var result = Solve(Named(name));
            foreach (var node in reference.GetProperty("nodes").EnumerateArray())
            {
                var joint = result.Joints.Single(joint => joint.PointId == Point(node.GetProperty("node").GetInt32()));
                for (int i = 0; i < 6; i++)
                {
                    double displacement = node.GetProperty("displacement")[i].GetDouble(), reaction = node.GetProperty("reaction")[i].GetDouble();
                    maximumDisplacementDifference = Math.Max(maximumDisplacementDifference, Math.Abs(joint.Displacements[i] - displacement));
                    maximumActionDifference = Math.Max(maximumActionDifference, Math.Abs(joint.Reactions[i] - reaction));
                    Near(joint.Displacements[i], displacement, name + " OpenSees nodal displacement/rotation", 1e-11, 1e-8);
                    Near(joint.Reactions[i], reaction, name + " OpenSees support reaction", 1e-7, 1e-8);
                    compared += 2;
                }
            }
            foreach (var frame in reference.GetProperty("frames").EnumerateArray())
            {
                var actual = result.Frames.Single(frameResult => frameResult.ParentObjectId == Frame(frame.GetProperty("frame").GetInt32()));
                for (int i = 0; i < 12; i++)
                {
                    double expected = frame.GetProperty("localEndActions")[i].GetDouble();
                    maximumActionDifference = Math.Max(maximumActionDifference, Math.Abs(actual.LocalEndActions[i] - expected));
                    Near(actual.LocalEndActions[i], expected, name + " OpenSees local end action", 1e-7, 1e-8);
                    compared++;
                }
            }
        }
        Console.WriteLine($"OpenSees reference: 12 cases, {compared} scalar comparisons; max |delta u/rotation| {maximumDisplacementDifference:G6}, max |delta force/moment| {maximumActionDifference:G6} (mixed units; see verification report).");
    }
}
