using Mcotis.Desktop.Application;
using Mcotis.Desktop.Application.Etabs;
using Mcotis.Desktop.Configuration;

namespace Mcotis.Desktop.Presentation;

// ---- The generator flag (task of 2026-10-14, §2): what makes the open model optimizable ----
// A model a parametric generator made (Load VCF / Edit → VCF System, Load TAS / Edit → Tied Arch System) carries the
// generator's flag: its id, version, parameters and the generation key of every object it made. The Optimization menu
// reads the flag and the generator's validation of the document against it — never the model's name or file. Edit →
// Detach from Generator removes the flag, Edit → Re-apply Generator rebuilds the system from it; both are steps of the
// model's history.
public sealed partial class MainViewModel
{
    /// <summary>The flag of the open model, as its configuration holds it; null for a model no generator made.</summary>
    private FemGeneratorDto? generatorFlag;

    private (Mcotis.Core.ModelDocument? Document, GeneratorValidationReport? Report) generatorCheck;

    /// <summary>The generator's validation of the main window's document against its flag; null without a flag.</summary>
    internal GeneratorValidationReport? GeneratorReport
    {
        get
        {
            var document = NativeDocumentSession.Document;
            if (!ReferenceEquals(generatorCheck.Document, document)) generatorCheck = (document, ParametricGenerators.Validate(document));
            return generatorCheck.Report;
        }
    }

    /// <summary>The id of the generator that made the open model (VCF, TAS), or null.</summary>
    internal string? GeneratorId => generatorFlag?.Id;

    /// <summary>The open model is a VCF system the VCF generator made, and the edits so far keep it one — what Optimization
    /// VCF varies.</summary>
    public bool CanOptimizeVcf => GeneratorReport is { GeneratorId: VcfGenerator.GeneratorId, IsValid: true };

    /// <summary>The open model is a tied arch system the TAS generator made, and the edits so far keep it one — what
    /// Optimization TAS varies.</summary>
    public bool CanOptimizeTas => GeneratorReport is { GeneratorId: TasGenerator.GeneratorId, IsValid: true };

    private const string VcfOptimizationToolTip = "Set the VCF search space — min / max / step for bays, bay width, truss depth (middle chord kept at 0.5 × depth), clear height, back-span and back-stay height, and candidate sections per chord, mega column and cable; Run Optimization analyses every combination and lists the max deflection of each case";
    private const string TasOptimizationToolTip = "Set the tied arch system's search space — min / max / step for H1, H2, bays and spacing and for the storeys above (SW, SDL, LL, floors, B), and candidate sections per role; Run Optimization analyses every combination and lists the max deflection, check and cost of each case";

    public string OptimizationVcfToolTip => OptimizationToolTip(VcfGenerator.GeneratorId, "a VCF system (File → Load VCF… or Edit → VCF System…)", VcfOptimizationToolTip);
    public string OptimizationTasToolTip => OptimizationToolTip(TasGenerator.GeneratorId, "the tied arch system (File → Load TAS… or Edit → Tied Arch System…)", TasOptimizationToolTip);

    private string OptimizationToolTip(string id, string system, string what)
    {
        var report = GeneratorReport;
        var why = report switch
        {
            null when NativeDocumentProblem is { } problem => $"the model has no native document ({problem})",
            null => $"the open model was not made by a parametric generator — an imported, TemplateBuilding or detached model, or a file " +
                    $"without a generator flag. Only {system} made by the {id} generator can be optimized",
            { GeneratorId: var other } when other != id => $"the open model was made by the {other} generator",
            { IsValid: false } => report.Text(),
            _ => null,
        };
        return why is null ? $"For {system}. {what}" : $"For {system}. {what}\n\nNot available: {why}";
    }

    /// <summary>The parameters of the open tied arch system (from its flag), or null for another model.</summary>
    internal TiedArchParameters? TiedArchParameters
    {
        get
        {
            if (generatorFlag is not { Id: TasGenerator.GeneratorId } flag) return null;
            try { return TasGenerator.Read(flag.Parameters); }
            catch (System.IO.InvalidDataException) { return null; }
        }
    }

    /// <summary>A configuration saved before the flag existed (and the built-in VCF model) is flagged when it is opened —
    /// only if it is a VCF system its generator validates. A tied arch system of such a file cannot be flagged: its
    /// parameters were not saved. <paramref name="note"/> says why a VCF-looking model was not flagged.</summary>
    private FemGeneratorDto? MigratedGeneratorFlag(out string? note)
    {
        note = null;
        if (!VcfGeometry.CanAnalyze(State.Geometry, State.Truss)) return null;
        try
        {
            var flag = VcfGenerator.Instance.Flag(State.Geometry, State.Truss.TopPoints, State.Truss.MiddlePoints, State.Truss.BottomPoints);
            var report = ParametricGenerators.Validate(flag, State.Geometry, State.JointRestraints);
            if (report.IsValid) return flag;
            note = $"Not flagged as a VCF system (Optimization VCF needs the flag): {report.Text(3)}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.InvalidDataException)
        {
            note = $"Not flagged as a VCF system: {ex.Message}";
        }
        return null;
    }

    /// <summary>After Edit → VCF System on a model the VCF generator made: the flag follows the new parameters.</summary>
    private void FlagVcfSystem()
    {
        try { generatorFlag = VcfGenerator.Instance.Flag(State.Geometry, State.Truss.TopPoints, State.Truss.MiddlePoints, State.Truss.BottomPoints); }
        catch (InvalidOperationException ex) { Message += $" The VCF flag keeps its parameters: {ex.Message}"; }
    }

    private void NotifyGenerator()
    {
        foreach (var name in new[] { nameof(CanOptimizeVcf), nameof(CanOptimizeTas), nameof(OptimizationVcfToolTip), nameof(OptimizationTasToolTip),
                     nameof(CanDetachGenerator), nameof(CanReapplyGenerator), nameof(GeneratorSummary) })
            Notify(name);
    }

    /// <summary>The generator of the open model and its verdict, for the status of Edit → Detach / Re-apply.</summary>
    public string GeneratorSummary => GeneratorReport is { } report
        ? $"Made by the {report.GeneratorId} generator. {report.Text()}"
        : "No generator made this model: it cannot be optimized.";

    // ---- Edit → Detach from Generator ----
    public bool CanDetachGenerator => generatorFlag is not null && CanEditFemModel;

    /// <summary>Removes the flag: the model becomes an ordinary model that cannot be optimized. A step of the model's
    /// history — Undo gives the flag back. Nothing else removes it.</summary>
    internal bool DetachGenerator()
    {
        if (!CanDetachGenerator) return false;
        var id = generatorFlag!.Id;
        generatorFlag = null;
        femWorkingDirty = true;
        nextStepName = "Detach from generator";
        PersistFemWorkingSnapshot(advanceRevision: true);
        Message = $"Detached from the {id} generator: the model is an ordinary model now and cannot be optimized (Edit → Undo gives the flag back).";
        NotifyGenerator();
        return true;
    }

    // ---- Edit → Re-apply Generator ----
    public bool CanReapplyGenerator => generatorFlag is { Id: VcfGenerator.GeneratorId or TasGenerator.GeneratorId } && CanEditFemModel && femInputSnapshot is not null;

    /// <summary>What Re-apply overwrites, for the confirmation: the edits that break the system, and that the whole model
    /// becomes the generator's.</summary>
    internal string ReapplyGeneratorText()
    {
        var report = GeneratorReport;
        var breaking = report is { Breaking.Count: > 0 }
            ? "It undoes these edits:\n" + string.Join("\n", report.Breaking.Take(12).Select(finding => "• " + finding)) +
              (report.Breaking.Count > 12 ? $"\n• … and {report.Breaking.Count - 12} more" : "") + "\n\n"
            : "No edit breaks the system now.\n\n";
        var what = generatorFlag?.Id == TasGenerator.GeneratorId
            ? "The model becomes the tied arch system of its parameters, as Edit → Tied Arch System → Apply makes it: geometry, supports, sections, materials, load patterns, deck loads, cases and combinations."
            : "The model becomes the VCF system of its parameters, as File → Load VCF… and Edit → VCF System make it: geometry, supports, sections, materials, loads, cases and combinations.";
        return breaking + what + " Every other change made since is overwritten.\n\nEdit → Undo takes it back.";
    }

    /// <summary>Rebuilds the system from the flag's parameters, as one step of the model's history.</summary>
    internal async Task<bool> ReapplyGeneratorAsync()
    {
        if (!CanReapplyGenerator || IsBusy) return false;
        FlushFemWorkingSession();
        var flag = generatorFlag!;
        var identity = femInputSnapshot!.DatabaseId;
        var revision = femInputSnapshot.Revision;
        var (path, isDefault, file) = (offlineModelPath, isDefaultModel, femInputDatabasePath);
        try
        {
            if (flag.Id == TasGenerator.GeneratorId)
            {
                var parameters = TasGenerator.Read(flag.Parameters);
                regeneratingIdentity = identity;
                nextStepName = "Re-apply generator";
                try { ApplyTiedArchSystem(parameters); }
                finally { regeneratingIdentity = null; }
                // the model keeps its file: Re-apply is an edit of it
                offlineModelPath = path;
                femInputDatabasePath = file;
                femWorkingDirty = true;
                PersistFemWorkingSnapshot(advanceRevision: true);
            }
            else
            {
                var (bays, parameters) = VcfGenerator.Read(flag.Parameters);
                var snapshot = FemInputDatabase.ReadDefault().Snapshot;
                snapshot.DatabaseId = identity;
                snapshot.Revision = revision + 1;
                snapshot.IsLocked = false;
                snapshot.LockedRevision = null;
                snapshot.Model = snapshot.Model! with { IsLocked = false };
                snapshot.Generator = null;
                // the built-in model in the window, the document unchanged until the system is complete: one step
                restoringFromDocument = true;
                try { ApplyFemModel(snapshot.ToLegacy(), path ?? FemInputDatabase.DefaultModelName, isDefault, snapshot, keepDocument: true); }
                finally { restoringFromDocument = false; }
                var analysis = VcfGeometry.Analyze(State.Geometry, State.Truss);
                var topology = bays == analysis.Bays ? null : VcfGeometry.ChangeBays(analysis, bays);
                var system = topology is null ? analysis : VcfGeometry.Analyze(topology);
                await ApplyVcfGeometryAsync(VcfGeometry.Build(system, parameters), new Dictionary<string, string>(StringComparer.Ordinal), topology);
                FlagVcfSystem();
                offlineModelPath = path;
                femInputDatabasePath = file;
                femWorkingDirty = true;
                nextStepName = "Re-apply generator";
                PersistFemWorkingSnapshot(advanceRevision: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.InvalidDataException)
        {
            Warn("Re-apply Generator", ex.Message);
            return false;
        }
        Message = $"Re-applied the {flag.Id} generator: the model is the system of its parameters again (Edit → Undo takes it back).";
        NotifyGenerator();
        return true;
    }

    /// <summary>While Re-apply rebuilds the model: the identity the rebuilt model keeps, so that it is a step of the same
    /// document's history and not a model of its own.</summary>
    private Guid? regeneratingIdentity;
}
