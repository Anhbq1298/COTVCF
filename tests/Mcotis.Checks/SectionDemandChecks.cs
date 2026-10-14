using Mcotis.Analysis;
using Mcotis.Core;
using static Check;

/// <summary>
/// The capability check of frame sections (<see cref="FrameStiffnessDemand"/>, 2026-10-14): a section property is
/// needed only where its stiffness term moves a degree of freedom that is on. A frame of the XZ plane without I22 and
/// J is analysed in the XZ plane exactly as one that has them — those terms act on UY, RX and RZ only — and refused in
/// full 3D, by name; a turned local axis makes I22 the in-plane inertia, which is then required; in the XY plane the
/// in-plane term of a horizontal frame is I22, and I33 is the one left out. Static and modal alike.
/// </summary>
internal static class SectionDemandChecks
{
    private static ObjectId Id(int value) => ObjectId.Parse($"00000000-0000-0000-0042-{value:D12}");
    private static readonly ObjectId Material = Id(1), Full = Id(2), Point1 = Id(3), Point2 = Id(4), Frame = Id(5), Pattern = Id(6), Case = Id(7), Mass = Id(8), Modal = Id(9);
    private const double E = 2e8, A = .01, I33 = .0008, I22 = .0004, J = .00002, L = 6;

    public static void Run()
    {
        // a cantilever along X in the XZ plane, a tip force in Z and a tip moment about Y
        var complete = Cantilever(new(A, I33, I22, J), DofMask.XZ, new(0, 0, -50), new(0, 30, 0));
        var inPlaneOnly = Cantilever(new(A, I33, 0, 0), DofMask.XZ, new(0, 0, -50), new(0, 30, 0));
        var withAll = Solve(complete);
        var without = StaticSolver.Solve(Compile(inPlaneOnly), Case);
        Assert(without.Success, "XZ plane: a frame without I22 and J is analysed — " + string.Join(" | ", without.Issues.Select(issue => issue.Message)));
        double scale = withAll.Joints.SelectMany(joint => joint.Displacements.Concat(joint.Reactions)).Max(Math.Abs);
        double worst = withAll.Joints.Zip(without.Result!.Joints).SelectMany(pair =>
            pair.First.Displacements.Zip(pair.Second.Displacements).Concat(pair.First.Reactions.Zip(pair.Second.Reactions))).Max(pair => Math.Abs(pair.First - pair.Second));
        // closed form of the tip: δ = P L³ / 3EI − M L² / 2EI (sign by the axes), the same with or without I22 and J
        double tip = without.Result.Joints.Single(joint => joint.PointId == Point2).Displacements[2];
        double expected = -50 * L * L * L / (3 * E * I33) - 30 * L * L / (2 * E * I33);
        Assert(worst <= 1e-12 * scale && Math.Abs(tip - expected) <= 1e-9 * Math.Abs(expected),
            $"XZ plane: without I22 and J the cantilever gives the results of the complete section (largest difference {worst:G3} of {scale:G3}); tip {tip * 1000:0.000} mm = P L³/3EI + M L²/2EI");

        var refused = StaticSolver.Solve(Compile(Cantilever(new(A, I33, 0, 0), DofMask.Full3D, new(0, 0, -50), default)), Case);
        var issue = refused.Issues.SingleOrDefault(item => item.Code == "static.section.stiffness");
        Assert(!refused.Success && issue is { ObjectId: var id } && id == Frame && issue.Message.Contains("'F1'", StringComparison.Ordinal) &&
               issue.Message.Contains("I22", StringComparison.Ordinal) && issue.Message.Contains("J (torsion)", StringComparison.Ordinal) && !issue.Message.Contains("I33", StringComparison.Ordinal),
            "Full 3D: the same frame is refused by name — I22 and J act there and its section has none; nothing is assumed (" + issue?.Message + ")");

        var turned = Cantilever(new(A, I33, 0, 0), DofMask.XZ, new(0, 0, -50), default, localAxis: 90);
        var turnedOutcome = StaticSolver.Solve(Compile(turned), Case);
        var turnedIssue = turnedOutcome.Issues.SingleOrDefault(item => item.Code == "static.section.stiffness");
        Assert(!turnedOutcome.Success && turnedIssue is not null && turnedIssue.Message.Contains("I22", StringComparison.Ordinal) && !turnedIssue.Message.Contains("J (torsion)", StringComparison.Ordinal),
            "XZ plane, local axes turned 90°: I22 now bends the frame in the plane and is required; J still acts on nothing that is on");

        // the XY plane: a horizontal frame along X bends in the plane about local 2 (local 2 is global Z)
        var xyWithout = StaticSolver.Solve(Compile(Cantilever(new(A, 0, I22, 0), DofMask.XY, new(0, -40, 0), default)), Case);
        var xyComplete = Solve(Cantilever(new(A, I33, I22, J), DofMask.XY, new(0, -40, 0), default));
        var xyRefused = StaticSolver.Solve(Compile(Cantilever(new(A, I33, 0, J), DofMask.XY, new(0, -40, 0), default)), Case);
        Assert(xyWithout.Success && Math.Abs(xyWithout.Result!.Joints.Single(joint => joint.PointId == Point2).Displacements[1] -
                                             xyComplete.Joints.Single(joint => joint.PointId == Point2).Displacements[1]) <= 1e-15 &&
               xyRefused.Issues.Any(item => item.Code == "static.section.stiffness" && item.Message.Contains("I22", StringComparison.Ordinal)),
            "XY plane: the same rule by the frame's own axes — I33 and J are left out, I22 is required");

        Assert(FrameStiffnessDemand.Active(complete, complete.Frames[Frame], DofMask.XZ) == (FrameStiffnessDemand.Terms.Axial | FrameStiffnessDemand.Terms.Bending33) &&
               FrameStiffnessDemand.Active(complete, complete.Frames[Frame], DofMask.Full3D) ==
               (FrameStiffnessDemand.Terms.Axial | FrameStiffnessDemand.Terms.Bending22 | FrameStiffnessDemand.Terms.Bending33 | FrameStiffnessDemand.Terms.Torsion) &&
               FrameStiffnessDemand.Active(complete, complete.Frames[Frame], DofMask.UX | DofMask.UZ) == (FrameStiffnessDemand.Terms.Axial | FrameStiffnessDemand.Terms.Bending33),
            "the terms a frame of the XZ plane calls on: EA and EI33 in the plane (rotation on or off), all four in full 3D");

        // modal: the same frame in the XZ plane, its own mass only — the same modes without I22 and J; refused in 3D
        var modes = ModalSolver.Solve(Compile(WithModal(inPlaneOnly)), Modal);
        var modesComplete = ModalSolver.Solve(Compile(WithModal(complete)), Modal);
        var modalRefused = ModalSolver.Solve(Compile(WithModal(Cantilever(new(A, I33, 0, 0), DofMask.Full3D, default, default))), Modal);
        Assert(modes.Success && modesComplete.Success && modes.Result!.Modes.Length == modesComplete.Result!.Modes.Length &&
               modes.Result.Modes.Zip(modesComplete.Result.Modes).All(pair => Math.Abs(pair.First.EigenvaluePerSecondSquared - pair.Second.EigenvaluePerSecondSquared) <= 1e-9 * pair.Second.EigenvaluePerSecondSquared) &&
               modes.Result.Modes.All(mode => mode.EigenvaluePerSecondSquared > 0) &&
               modalRefused.Issues.Any(item => item.Code == "modal.section.stiffness" && item.ObjectId == Frame),
            $"modal: in the XZ plane the frame without I22 and J has the modes of the complete section ({modes.Result?.Modes.Length} positive); in full 3D it is refused by name");
    }

    private static ModelDocument Cantilever(FrameSectionProperties properties, DofMask mask, Xyz force, Xyz moment, double localAxis = 0)
    {
        var editor = ModelDocument.Create(Id(999)).Edit();
        editor.PutMaterial(new(Material, "Steel", MaterialType.Steel, E, .3, 77, 7.85));
        editor.PutFrameSection(new(Full, "Section", Material, FrameSectionShape.General, properties));
        editor.AddPoint(new(0, 0, 0), "P1", Point1);
        editor.AddPoint(new(L, 0, 0), "P2", Point2);
        editor.SetRestraint([Point1], Restraint.Fixed);
        editor.AddFrame(Point1, Point2, "F1", Frame);
        editor.SetFrameSection([Frame], Full);
        if (localAxis != 0) editor.SetFrameLocalAxis([Frame], localAxis);
        editor.PutLoadPattern(new(Pattern, "LOAD", LoadPatternType.Other));
        if (force != default || moment != default) editor.PutLoad(new PointLoad(Id(10), Pattern, Point2, force, moment));
        editor.PutLoadCase(new(Case, "STATIC", LoadCaseType.LinearStatic, [new(Pattern, 1)]));
        editor.SetSettings(AnalysisSettings.Default with { ActiveDofs = mask, FrameMeshSegments = 1, CableSag = false });
        return editor.Commit().Document;
    }

    private static ModelDocument WithModal(ModelDocument document)
    {
        var editor = document.Edit();
        editor.PutMassSource(new(Mass, "MS1", IncludeSelfMass: true, IncludeAdditionalMass: false));
        editor.PutLoadCase(new(Modal, "MODAL", CaseKind.ModalEigen, []) { ModalOptions = new(Mass, 2) });
        return editor.Commit().Document;
    }

    private static CompiledModel Compile(ModelDocument document)
    {
        var compiled = ModelCompiler.Compile(document);
        Assert(compiled.Success, "the cantilever compiles: " + string.Join("; ", compiled.Issues.Select(issue => issue.Message)));
        return compiled.Model!;
    }

    private static StaticResult Solve(ModelDocument document)
    {
        var outcome = StaticSolver.Solve(Compile(document), Case);
        Assert(outcome.Success, "the cantilever solves: " + string.Join("; ", outcome.Issues.Select(issue => issue.Message)));
        return outcome.Result!;
    }
}
