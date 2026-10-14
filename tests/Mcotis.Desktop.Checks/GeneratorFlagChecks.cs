using System.IO;
using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Configuration;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Presentation;
using Native = Mcotis.Core;

/// <summary>
/// The generator flag (task of 2026-10-14, §2, M5): a model a parametric generator made carries the generator's flag, and
/// the Optimization menu offers the generator's optimization only while the generator validates the model against it —
/// never by the model's name or file. Compatible edits keep it; each kind of breaking edit disables it with the object
/// and the reason; Undo gives it back; Save / Open keep it; an imported tied arch .e2k has none; Detach removes it and Undo
/// gives it back; Re-apply rebuilds the system as one step of the history. <c>--generator-flag</c>, and in <c>--full</c>.
/// </summary>
internal static class GeneratorFlagChecks
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mcotis-generator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            RunTas(folder);
            RunVcf(folder);
            RunDocument();
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void RunTas(string folder)
    {
        var tas = EtabsImportChecks.NewViewModel(out _);
        var warnings = new List<string>();
        tas.WarningSink = (title, body) => warnings.Add($"{title}: {body}");
        tas.LoadTasModel();
        var document = tas.NativeDocumentSession.Document;
        Check(tas.GeneratorId == TasGenerator.GeneratorId && tas.GeneratorReport is { IsValid: true } && tas.CanOptimizeTas && !tas.CanOptimizeVcf &&
              tas.TiedArchParameters == TiedArchParameters.Default && document.Generator is { GeneratorId: "TAS", Version: 1 } &&
              document.Points.Values.All(point => point.Origin?.GenerationKey?.StartsWith("TAS:", StringComparison.Ordinal) == true) &&
              document.Frames.Values.All(frame => frame.Origin?.GenerationKey?.StartsWith("TAS:F:", StringComparison.Ordinal) == true),
            $"File → Load TAS: the model carries the TAS flag (version 1, the default parameters), every joint and frame its generation key, " +
            $"and the generator validates it — Optimization TAS on, VCF off ({tas.GeneratorReport?.Text()})");

        // a compatible edit keeps it
        var frame = tas.FemSectionProperties.First(property => !property.IsCable);
        var other = tas.FrameSections.Select(section => section.Name).First(name => name != frame.SectionName);
        tas.AssignSection([frame.MemberName], other, cable: false);
        tas.FlushFemWorkingSession();
        Check(tas.CanOptimizeTas, $"another section on {frame.MemberName} is a compatible edit: still optimizable");

        // a restraint changed on a generated support breaks it; Undo gives it back
        var support = tas.FemPinSupportPoints[0];
        tas.AssignJointRestraints([support], Native.Restraint.Fixed);
        tas.FlushFemWorkingSession();
        Check(!tas.CanOptimizeTas && tas.GeneratorReport is { Breaking: [{ Object: var joint, Reason: var reason }] } &&
              joint == $"Joint {support}" && reason.StartsWith("restraint fixed", StringComparison.Ordinal) &&
              tas.OptimizationTasToolTip.Contains("Not available", StringComparison.Ordinal) && tas.OptimizationTasToolTip.Contains(support, StringComparison.Ordinal),
            $"a fixed support breaks the system — Optimization TAS off, the tooltip names the joint: {tas.GeneratorReport?.Text()}");
        Check(tas.UndoModel() && tas.CanOptimizeTas, "Undo of the restraint makes the model optimizable again");

        // a generated member deleted breaks it; Re-apply rebuilds the system in one step, Undo takes that back
        var arch = tas.State.Geometry.Members.First(member => member.Name.StartsWith("ARCH", StringComparison.Ordinal)).Name;
        tas.DeleteObjectsAsync([arch], []).GetAwaiter().GetResult();
        tas.FlushFemWorkingSession();
        Check(!tas.CanOptimizeTas && tas.GeneratorReport!.Breaking.Any(finding => finding.Object == $"Frame {arch}" && finding.Reason == "deleted"),
            $"{arch} deleted breaks the system: {tas.GeneratorReport?.Text()}");
        Check(tas.ReapplyGeneratorText().Contains($"Frame {arch}: deleted", StringComparison.Ordinal),
            "Re-apply says first what it overwrites, the deleted member included");
        Check(tas.ReapplyGeneratorAsync().GetAwaiter().GetResult() && tas.CanOptimizeTas &&
              tas.State.Geometry.Members.Any(member => member.Name == arch) && tas.NativeDocumentSession.UndoName == "Re-apply generator",
            $"Re-apply Generator rebuilds the system from its parameters ({arch} is back), as one step of the history");
        Check(tas.UndoModel() && !tas.CanOptimizeTas && tas.State.Geometry.Members.All(member => member.Name != arch) && tas.RedoModel() && tas.CanOptimizeTas,
            "Undo takes Re-apply back (the member is gone again), Redo makes it again");

        // Detach removes the flag; Undo gives it back
        Check(tas.DetachGenerator() && tas.GeneratorReport is null && !tas.CanOptimizeTas && tas.TiedArchParameters is null &&
              tas.OptimizationTasToolTip.Contains("not made by a parametric generator", StringComparison.Ordinal) &&
              tas.NativeDocumentSession.UndoName == "Detach from generator",
            "Edit → Detach from Generator: an ordinary model, Optimization TAS off and the tooltip says why");
        Check(tas.UndoModel() && tas.GeneratorId == TasGenerator.GeneratorId && tas.CanOptimizeTas, "Undo of Detach gives the flag back");

        // other parameters, the elevated cable included
        var p = new TiedArchParameters(25, 30, 10, 12, 10, 10, 5, 7.5, 6);
        tas.ApplyTiedArchSystem(p);
        Check(tas.TiedArchParameters == p && tas.CanOptimizeTas, "Edit → Tied Arch System with H1 25, H2 30, 10 bays of 12 m: flagged with those parameters, valid");
        var elevated = TiedArchParameters.Default with { ElevatedCable = true };
        tas.ApplyTiedArchSystem(elevated);
        Check(tas.TiedArchParameters == elevated && tas.CanOptimizeTas &&
              tas.NativeDocumentSession.Document.Cables.Values.Single().Origin?.GenerationKey?.StartsWith("TAS:C:", StringComparison.Ordinal) == true,
            "… and with the elevated cable: the tie is a generated cable, the model valid");

        // Save / Open keep the flag, in both file forms
        foreach (var extension in new[] { FemInputDatabase.Extension, FemModelFile.Extension })
        {
            var path = Path.Combine(folder, "tas" + extension);
            tas.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
            var reopened = EtabsImportChecks.NewViewModel(out _);
            reopened.OpenFemModelFile(path);
            Check(reopened.GeneratorId == TasGenerator.GeneratorId && reopened.TiedArchParameters == elevated && reopened.CanOptimizeTas,
                $"Save and Open ({extension}) keep the flag and its parameters: the reopened model is optimizable");
        }

        // a tied arch imported from its .e2k has no flag
        var imported = EtabsImportChecks.NewViewModel(out _);
        imported.ImportFromE2kFile(Path.Combine(EtabsImportChecks.Folder, "CoT120.e2k"));
        Check(imported.GeneratorReport is null && !imported.CanOptimizeTas && !imported.CanOptimizeVcf &&
              imported.OptimizationTasToolTip.Contains("not made by a parametric generator", StringComparison.Ordinal),
            "the same tied arch imported from its .e2k (File → Import) is not flagged: no optimization, and the tooltip says why");
        Check(warnings.Count == 0, "no warning on the way" + (warnings.Count > 0 ? ": " + string.Join(" | ", warnings) : ""));
    }

    private static void RunVcf(string folder)
    {
        var (vcf, state, _, warnings) = FemModelFileChecks.DefaultModelApp();
        Check(vcf.GeneratorId == VcfGenerator.GeneratorId && vcf.CanOptimizeVcf && !vcf.CanOptimizeTas &&
              vcf.NativeDocumentSession.Document.Generator?.Parameters["Bays"] == "10",
            $"File → Load VCF: the built-in model (saved before flags existed) is flagged when it opens, as its generator validates it — {vcf.GeneratorReport?.Text()}");

        // Edit → VCF System, 10 → 12 bays of 12.5 m, then back to 10: names depend on the way, keys do not
        var analysis = VcfGeometry.Analyze(state.Geometry, state.Truss);
        var topology = VcfGeometry.ChangeBays(analysis, 12);
        var working = VcfGeometry.Analyze(topology);
        vcf.ApplyVcfGeometryAsync(VcfGeometry.Build(working, working.Parameters with { BayWidth = 12.5 }), new Dictionary<string, string>(StringComparer.Ordinal), topology)
            .GetAwaiter().GetResult();
        vcf.FlushFemWorkingSession();
        Check(vcf.CanOptimizeVcf && vcf.NativeDocumentSession.Document.Generator is { } twelve && twelve.Parameters["Bays"] == "12" && twelve.Parameters["BayWidth"] == "12.5",
            $"Edit → VCF System with 12 bays of 12.5 m: the flag follows (Bays 12, BayWidth 12.5) and the generator validates the model — {vcf.GeneratorReport?.Text()}");
        analysis = VcfGeometry.Analyze(state.Geometry, state.Truss);
        topology = VcfGeometry.ChangeBays(analysis, 10);
        working = VcfGeometry.Analyze(topology);
        vcf.ApplyVcfGeometryAsync(VcfGeometry.Build(working, working.Parameters with { BayWidth = 11 }), new Dictionary<string, string>(StringComparer.Ordinal), topology)
            .GetAwaiter().GetResult();
        vcf.FlushFemWorkingSession();
        Check(vcf.CanOptimizeVcf, $"… and back to 10 bays of 11 m: still the VCF system of its parameters — {vcf.GeneratorReport?.Text()}");

        // a chord joint deleted takes its members: each listed
        var chord = state.Truss.BottomPoints[3];
        var connected = vcf.MembersDeletedWith([], [chord]);
        vcf.DeleteObjectsAsync([], [chord]).GetAwaiter().GetResult();
        vcf.FlushFemWorkingSession();
        var findings = vcf.GeneratorReport!.Breaking;
        Check(!vcf.CanOptimizeVcf && findings.Any(finding => finding.Object == $"Joint {chord}" && finding.Reason == "deleted") &&
              connected.All(member => findings.Any(finding => finding.Object.EndsWith(" " + member, StringComparison.Ordinal) && finding.Reason == "deleted")),
            $"a chord joint deleted with its {connected.Count} members: Optimization VCF off, each listed — {vcf.GeneratorReport.Text(3)}");
        Check(vcf.UndoModel() && vcf.CanOptimizeVcf, "Undo restores eligibility");

        // Re-apply rebuilds the VCF system of the flag (10 bays of 11 m) in one step
        vcf.AssignJointRestraints([vcf.FemPinSupportPoints[0]], Native.Restraint.Fixed);
        vcf.FlushFemWorkingSession();
        Check(!vcf.CanOptimizeVcf && vcf.ReapplyGeneratorAsync().GetAwaiter().GetResult() && vcf.CanOptimizeVcf &&
              vcf.NativeDocumentSession.Document.Generator?.Parameters["BayWidth"] == "11" && vcf.NativeDocumentSession.UndoName == "Re-apply generator" &&
              vcf.UndoModel() && !vcf.CanOptimizeVcf,
            $"Re-apply Generator on a broken VCF system rebuilds the 10 bays of 11 m as one step; Undo takes it back — {vcf.GeneratorReport?.Text()}");

        // Save / Open keep the flag
        var path = Path.Combine(folder, "vcf" + FemInputDatabase.Extension);
        vcf.RedoModel();
        vcf.SaveFemModelFileAsync(path).GetAwaiter().GetResult();
        var reopened = EtabsImportChecks.NewViewModel(out _);
        reopened.OpenFemModelFile(path);
        Check(reopened.GeneratorId == VcfGenerator.GeneratorId && reopened.CanOptimizeVcf &&
              reopened.NativeDocumentSession.Document.Generator?.Parameters["BayWidth"] == "11",
            "Save and Open keep the VCF flag and its parameters");
        Check(warnings.Count == 0, "no warning on the way" + (warnings.Count > 0 ? ": " + string.Join(" | ", warnings) : ""));
    }

    // what the main window cannot do yet but a document can hold: a joint moved, a member reconnected or added, a rename
    private static void RunDocument()
    {
        var vm = EtabsImportChecks.NewViewModel(out _);
        vm.LoadTasModel();
        var document = vm.NativeDocumentSession.Document;
        var generator = TasGenerator.Instance;
        var points = document.Points.Values.OrderBy(point => point.Name, StringComparer.Ordinal).ToArray();
        var frame = document.Frames.Values.OrderBy(frame => frame.Name, StringComparer.Ordinal).First();

        var renamed = document.Edit();
        renamed.Rename(frame.Id, "Renamed arch member");
        Check(generator.Validate(renamed.Commit().Document).IsValid, "a generated member renamed keeps its key: still valid");

        var moved = document.Edit();
        var point = points[3];
        moved.MovePoint(point.Id, point.Position with { Z = point.Position.Z + 0.5 });
        var movedFindings = generator.Validate(moved.Commit().Document).Breaking;
        Check(movedFindings is [{ Reason: var movedReason } finding] && finding.Object == $"Joint {point.Name}" &&
              movedReason.StartsWith("moved 0.5 m", StringComparison.Ordinal),
            $"a generated joint moved 0.5 m: listed — {string.Join("; ", movedFindings)}");

        var reconnected = document.Edit();
        var free = points.First(candidate => candidate.Id != frame.StartPointId && candidate.Id != frame.EndPointId);
        reconnected.Remove([frame.Id]);
        reconnected.AddFrame(frame.StartPointId, free.Id, frame.Name, origin: frame.Origin);
        Check(generator.Validate(reconnected.Commit().Document).Breaking.Any(item => item.Object == $"Frame {frame.Name}" &&
                item.Reason.StartsWith("reconnected", StringComparison.Ordinal)),
            $"a generated member reconnected to {free.Name}: listed");

        var added = document.Edit();
        added.AddFrame(points[0].Id, points[^1].Id, "Hand-added brace");
        Check(generator.Validate(added.Commit().Document).Breaking is [{ Object: "Frame Hand-added brace", Reason: var addedReason }] &&
              addedReason.StartsWith("added", StringComparison.Ordinal),
            "a member added by hand inside the system: listed");

        Check(VcfGenerator.Instance.Validate(document).Refusal is { } refusal && refusal.Contains("not made by the VCF generator", StringComparison.Ordinal),
            "the VCF generator refuses the tied arch system: it did not make it");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
