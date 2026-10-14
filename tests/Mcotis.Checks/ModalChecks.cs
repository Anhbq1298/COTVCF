using Mcotis.Analysis;
using Mcotis.Core;
using Mcotis.Persistence;
using System.Text.Json;
using static Check;

internal static class ModalChecks
{
    private static ObjectId Id(int value) => ObjectId.Parse($"00000000-0000-0000-0000-{value:D12}");
    private const double E = 200_000_000, A = .02, I = .0005, L = 4;

    public static void RunSmoke()
    {
        SdofAndParticipation();
        MasslessRotation();
        FailurePaths();
    }

    public static void Run()
    {
        RunSmoke();
        CoupledTwoDof();
        SplitMassAndPersistence();
        NumericalEdgeCases();
        CrossCheckOpenSees();
    }

    private static (ModelDocument Document, ObjectId Modal) Axial(params (double X, double Mass)[] joints)
    {
        ObjectId material = Id(100), section = Id(101), massSource = Id(102), modal = Id(103);
        var editor = ModelDocument.Create(Id(999)).Edit();
        editor.PutMaterial(new(material, "Steel", MaterialType.Steel, E, .3, 0, 0));
        editor.PutFrameSection(new(section, "A", material, FrameSectionShape.General, new(A, I, I, I / 10)));
        ObjectId previous = default;
        for (int i = 0; i < joints.Length; i++)
        {
            var point = Id(i + 1);
            editor.AddPoint(new(joints[i].X, 0, 0), "P" + i, point);
            if (i == 0) editor.SetRestraint([point], Restraint.Fixed);
            else editor.SetJointMass(new(point, joints[i].Mass, 0, 0));
            if (i > 0)
            {
                var frame = Id(10 + i);
                editor.AddFrame(previous, point, "F" + i, frame);
                editor.SetFrameSection([frame], section);
            }
            previous = point;
        }
        editor.PutMassSource(new(massSource, "MS1", IncludeSelfMass: false, IncludeAdditionalMass: true));
        editor.PutLoadCase(new(modal, "MODAL", CaseKind.ModalEigen, [])
        { ModalOptions = new(massSource, Math.Max(1, joints.Length - 1)) });
        editor.SetSettings(AnalysisSettings.Default with { ActiveDofs = DofMask.UX });
        return (editor.Commit().Document, modal);
    }

    private static ModalAnalysisResult Solve(ModelDocument document, ObjectId modal)
    {
        var compiled = ModelCompiler.Compile(document);
        Assert(compiled.Success, "modal benchmark compiles");
        var outcome = ModalSolver.Solve(compiled.Model!, modal);
        Assert(outcome.Success, "modal benchmark solves: " + string.Join(" | ", outcome.Issues.Select(issue => issue.Message)));
        return outcome.Result!;
    }

    private static void SdofAndParticipation()
    {
        const double mass = 3;
        var (document, modal) = Axial((0, 0), (L, mass));
        var result = Solve(document, modal);
        double expected = E * A / L / mass;
        Near(result.Modes[0].EigenvaluePerSecondSquared, expected, 1e-9, "SDOF eigenvalue k/m");
        Near(result.Modes[0].FrequencyHz, Math.Sqrt(expected) / (2 * Math.PI), 1e-9, "SDOF frequency");
        Near(result.Modes[0].Participation[0].RatioPercent, 100, 1e-9, "SDOF UX effective mass is 100%");
        double amplitude = result.Modes[0].Shape.Single(item => item.PointId == Id(2)).UX;
        Near(amplitude * amplitude * mass, 1, 1e-9, "SDOF mode is mass normalized");
    }

    private static void CoupledTwoDof()
    {
        const double mass = 2;
        var (document, modal) = Axial((0, 0), (L, mass), (2 * L, mass));
        var result = Solve(document, modal);
        double k = E * A / L;
        double root = Math.Sqrt(5);
        Near(result.Modes[0].EigenvaluePerSecondSquared, (3 - root) / 2 * k / mass, 1e-8, "two-DOF lower eigenvalue");
        Near(result.Modes[1].EigenvaluePerSecondSquared, (3 + root) / 2 * k / mass, 1e-8, "two-DOF upper eigenvalue");
        Near(result.Modes[1].Participation[0].CumulativePercent, 100, 1e-8, "complete two-mode UX participation sums to 100%");
    }

    private static void MasslessRotation()
    {
        ObjectId material = Id(200), section = Id(201), source = Id(202), modal = Id(203), p1 = Id(204), p2 = Id(205), frame = Id(206);
        const double density = 5;
        var editor = ModelDocument.Create().Edit();
        editor.PutMaterial(new(material, "M", MaterialType.Other, E, .3, 0, density));
        editor.PutFrameSection(new(section, "S", material, FrameSectionShape.General, new(A, I, I, I / 10)));
        editor.AddPoint(new(0, 0, 0), "P1", p1); editor.AddPoint(new(L, 0, 0), "P2", p2);
        editor.AddFrame(p1, p2, "F1", frame); editor.SetFrameSection([frame], section);
        editor.SetRestraint([p1], Restraint.Fixed);
        editor.PutMassSource(new(source, "SELF"));
        editor.PutLoadCase(new(modal, "MODAL", CaseKind.ModalEigen, []) { ModalOptions = new(source, 1) });
        editor.SetSettings(AnalysisSettings.Default with { ActiveDofs = DofMask.XZ });
        var result = Solve(editor.Commit().Document, modal);
        double expected = 6 * E * I / (density * A * Math.Pow(L, 4));
        Near(result.Modes[0].EigenvaluePerSecondSquared, expected, 1e-8, "massless tip rotation is statically condensed, not restrained");
        Assert(result.Solver.CondensedMasslessDofs == 1, "one massless free rotation was condensed");
    }

    private static void SplitMassAndPersistence()
    {
        ObjectId material = Id(300), section = Id(301), source = Id(302), modal = Id(303), p1 = Id(304), pm = Id(305), p2 = Id(306), frame = Id(307);
        var editor = ModelDocument.Create().Edit();
        editor.PutMaterial(new(material, "M", MaterialType.Other, E, .3, 0, 7.5));
        editor.PutFrameSection(new(section, "S", material, FrameSectionShape.General, new(A, I, I, I / 10)));
        editor.AddPoint(new(0, 0, 0), "P1", p1); editor.AddPoint(new(L / 2, 0, 0), "PM", pm); editor.AddPoint(new(L, 0, 0), "P2", p2);
        editor.AddFrame(p1, p2, "F1", frame); editor.SetFrameSection([frame], section); editor.SetRestraint([p1], Restraint.Fixed);
        editor.SetJointMass(new(pm, 1, 2, 3, 4, 5, 6));
        editor.PutMassSource(new(source, "MS", true, true));
        editor.PutLoadCase(new(modal, "MODAL", CaseKind.ModalEigen, []) { ModalOptions = new(source, 1) });
        editor.SetSettings(AnalysisSettings.Default with { ActiveDofs = DofMask.UX });
        var document = editor.Commit().Document;
        var result = Solve(document, modal);
        Near(result.MassSummary.ElementMass, 7.5 * A * L, 1e-12, "frame split at an interior point conserves self mass");
        var reopened = NativeDocumentFormat.Deserialize(NativeDocumentFormat.Serialize(document));
        Assert(Check.SameContent(document, reopened), "schema-5 native file preserves modal mass inputs");
        Assert(reopened.JointMassAssignments[pm].RZ == 6 && reopened.MassSources[source].IncludeAdditionalMass,
            "joint inertia and mass-source components reopen exactly");
    }

    private static void FailurePaths()
    {
        var (document, modal) = Axial((0, 0), (L, 0));
        var compiled = ModelCompiler.Compile(document).Model!;
        var noMass = ModalSolver.Solve(compiled, modal);
        Assert(!noMass.Success && noMass.Issues.Any(issue => issue.Code == "modal.mass.none"), "zero physical mass is refused without fictitious mass");

        var source = document.MassSources.Values.Single();
        var editor = document.Edit();
        var p3 = Id(50); editor.AddPoint(new(L / 2, 1, 0), "P3", p3);
        ObjectId cableMaterial = Id(51), cableSection = Id(52), cable = Id(53);
        editor.PutMaterial(new(cableMaterial, "Cable material", MaterialType.Tendon, E, .3, 0, 1));
        editor.PutCableSection(new(cableSection, "Cable section", cableMaterial, A));
        editor.AddCable(Id(1), p3, "C1", cable); editor.SetCableSection([cable], cableSection);
        editor.SetSettings(AnalysisSettings.Default with { ActiveDofs = DofMask.Full3D });
        var withCable = editor.Commit().Document;
        var cableOutcome = ModalSolver.Solve(ModelCompiler.Compile(withCable).Model!, modal);
        Assert(!cableOutcome.Success && cableOutcome.Issues.Any(issue => issue.Code == "modal.cable.state"),
            "unstressed cable modal analysis is refused until a verified tangent state exists");
        Assert(source.IncludeAdditionalMass, "mass source remains independent of cable refusal");
    }

    private static void NumericalEdgeCases()
    {
        var backend = new DenseSymmetricEigenSolver();
        var (values, vectors) = backend.Solve(new double[,] { { 10, 0 }, { 0, 10 } }, [2, 2]);
        Near(values[0], 5, 1e-12, "repeated eigenvalue 1"); Near(values[1], 5, 1e-12, "repeated eigenvalue 2");
        Near(2 * (vectors[0, 0] * vectors[0, 1] + vectors[1, 0] * vectors[1, 1]), 0, 1e-12,
            "repeated-mode basis remains mass orthogonal");
        Throws<OperationCanceledException>(() => backend.Solve(new double[,] { { 2, -1 }, { -1, 2 } }, [1, 1], new(true)),
            "modal eigen extraction observes cancellation");

        var (limitedDocument, limitedCase) = Axial((0, 0), (L, 2), (2 * L, 2));
        var limited = ModalSolver.Solve(ModelCompiler.Compile(limitedDocument).Model!, limitedCase,
            new ModalOptions(MaximumInertialDofs: 1));
        Assert(!limited.Success && limited.Issues.Any(issue => issue.Code == "modal.limit.inertial"),
            "dense modal resource cap is an explicit refusal");

        var free = limitedDocument.Edit();
        free.SetRestraint([Id(1)], Restraint.Free);
        free.SetJointMass(new(Id(1), 2, 0, 0));
        var freeDocument = free.Commit().Document;
        var rigidMode = ModalSolver.Solve(ModelCompiler.Compile(freeDocument).Model!, limitedCase);
        Assert(!rigidMode.Success && rigidMode.Result is { Modes.Length: > 0 } &&
               rigidMode.Result.Modes[0] is { Status: ModalEigenStatus.NearZero, FrequencyHz: null, PeriodSeconds: null } &&
               rigidMode.Issues.Any(issue => issue.Code == "modal.stiffness.unstable"),
            "a rigid-body eigenvalue is diagnosed without inventing frequency or period");
    }

    private static void CrossCheckOpenSees()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "modal-eigen-opensees.json")));
        int compared = 0;
        double maximumDifference = 0;
        foreach (var reference in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var masses = reference.GetProperty("masses").EnumerateArray().Select((mass, index) => ((index + 1) * L, mass.GetDouble())).Prepend((0, 0d)).ToArray();
            var (document, modal) = Axial(masses);
            var result = Solve(document, modal);
            var eigenvalues = reference.GetProperty("eigenvalues").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            var frequencies = reference.GetProperty("frequenciesHz").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            var ratios = reference.GetProperty("participationRatioPercentUX").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            var cumulative = reference.GetProperty("cumulativeRatioPercentUX").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            for (var i = 0; i < result.Modes.Length; i++)
            {
                var participation = result.Modes[i].Participation.Single(value => value.Direction == DofComponent.UX);
                maximumDifference = Math.Max(maximumDifference, Math.Abs(result.Modes[i].EigenvaluePerSecondSquared - eigenvalues[i]));
                Near(result.Modes[i].EigenvaluePerSecondSquared, eigenvalues[i], 2e-12, "OpenSees eigenvalue");
                Near(result.Modes[i].FrequencyHz, frequencies[i], 2e-12, "OpenSees frequency");
                Near(participation.RatioPercent, ratios[i], 2e-10, "OpenSees participation ratio");
                Near(participation.CumulativePercent, cumulative[i], 2e-10, "OpenSees cumulative participation");
                compared += 4;
            }
        }
        Console.WriteLine($"OpenSees modal reference: 2 independently specified axial models, {compared} scalar comparisons; max |delta eigenvalue| {maximumDifference:G6} s^-2.");
    }

    private static void Near(double? actual, double expected, double relative, string description) =>
        Assert(actual is { } value && double.IsFinite(value) && Math.Abs(value - expected) <= 1e-10 + relative * Math.Abs(expected),
            $"{description}: {actual:G17} vs {expected:G17}");
}
