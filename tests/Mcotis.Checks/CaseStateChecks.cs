using System.Text.Json.Nodes;
using Mcotis.Analysis;
using Mcotis.Analysis.Cases;
using Mcotis.Core;
using Mcotis.Persistence;
using static Check;

/// <summary>Load cases as docs/analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md defines them (its PR-01 and the native part
/// of PR-02): the four kinds and what each may carry, the two references between cases kept apart, the order a run
/// solves them in and what a failed case blocks, the file that holds them — and that a case whose method is not
/// verified yet is refused by the solver, never solved as something else.</summary>
internal static class CaseStateChecks
{
    public static void Run()
    {
        WorkedModel();
        RunGraph();
        KindRules();
        ReferencesAndHash();
        NotSolvedYet();
        FileSchema4();
        Schema3Upgrade();
    }

    private static ObjectId Id(int number) => ObjectId.Parse($"00000000-0000-0000-0050-{number:D12}");

    /// <summary>The model of the plan's worked example, on a mast with a stay: patterns SW, DEAD, LIVE, WINDX and
    /// PRETENSION; NL_PRET fits the stay; PDELTA_GRAVITY continues from it; two linear cases and a modal case use its
    /// end stiffness; two nonlinear cases continue from it as independent branches; NL_ALTERNATE branches off NL_PRET.</summary>
    private sealed class Worked
    {
        public readonly ObjectId Sw = Id(1), Dead = Id(2), Live = Id(3), WindX = Id(4), Pretension = Id(5), Ms1 = Id(6),
            NlPret = Id(11), PDelta = Id(12), LinWind = Id(13), LinLive = Id(14), Modes = Id(15), NlSls = Id(16), NlUls = Id(17), NlAlternate = Id(18),
            CombWind = Id(21), Foot = Id(31), Head = Id(32), Anchor = Id(33), Mast = Id(34), Stay = Id(35);
        public readonly ModelDocument Document;

        public Worked()
        {
            var e = ModelDocument.Create(Id(99)).Edit();
            ObjectId steel = Id(41), strand = Id(42), tube = Id(43), rope = Id(44);
            e.PutMaterial(new(steel, "Steel", MaterialType.Steel, 2e8, .3, 77, 7.85));
            e.PutMaterial(new(strand, "Strand", MaterialType.Tendon, 1.6e8, .3, 0, 0));
            e.PutFrameSection(new(tube, "Tube", steel, FrameSectionShape.General, new(.02, .0008, .0008, .0012)));
            e.PutCableSection(new(rope, "Rope", strand, .002));
            e.AddPoint(new(0, 0, 0), "Foot", Foot);
            e.AddPoint(new(0, 0, 8), "Head", Head);
            e.AddPoint(new(6, 0, 0), "Anchor", Anchor);
            e.SetRestraint([Foot], Restraint.Fixed);
            e.SetRestraint([Anchor], Restraint.Pinned);
            e.AddFrame(Foot, Head, "Mast", Mast);
            e.SetFrameSection([Mast], tube);
            e.AddCable(Head, Anchor, "Stay", Stay);
            e.SetCableSection([Stay], rope);
            e.PutLoadPattern(new(Sw, "SW", LoadPatternType.Dead, 1));
            e.PutLoadPattern(new(Dead, "DEAD", LoadPatternType.Dead));
            e.PutLoadPattern(new(Live, "LIVE", LoadPatternType.Live));
            e.PutLoadPattern(new(WindX, "WINDX", LoadPatternType.Other));
            e.PutLoadPattern(new(Pretension, "PRETENSION", LoadPatternType.Prestress));
            e.PutLoad(new PointLoad(Id(51), Dead, Head, new(0, 0, -100)));
            e.PutLoad(new PointLoad(Id(52), Live, Head, new(0, 0, -40)));
            e.PutLoad(new PointLoad(Id(53), WindX, Head, new(-5, 0, 0)));
            e.PutLoad(new CableTargetForce(Id(54), Pretension, Stay, 60));
            e.PutMassSource(new(Ms1, "MS1"));

            e.PutLoadCase(new(NlPret, "NL_PRET", CaseKind.NonlinearStatic, [new(Sw, 1), new(Pretension, 1)]) { FitCableTargetForces = true, Run = false });
            e.PutLoadCase(new(PDelta, "PDELTA_GRAVITY", CaseKind.InitialPDelta, [new(Dead, 1), new(Live, .25)]) { InitialCaseId = NlPret });
            e.PutLoadCase(new(LinWind, "LIN_WINDX", CaseKind.LinearStatic, [new(WindX, 1)]) { StiffnessSourceCaseId = PDelta });
            e.PutLoadCase(new(LinLive, "LIN_LIVE", CaseKind.LinearStatic, [new(Live, 1)]) { StiffnessSourceCaseId = PDelta, Run = false });
            e.PutLoadCase(new(Modes, "MODES_PD", CaseKind.ModalEigen, []) { StiffnessSourceCaseId = PDelta, ModalOptions = new(Ms1, 12) });
            e.PutLoadCase(new(NlSls, "NL_SLS", CaseKind.NonlinearStatic, [new(Live, .75)]) { InitialCaseId = PDelta });
            e.PutLoadCase(new(NlUls, "NL_ULS", CaseKind.NonlinearStatic, [new(Dead, .35), new(Live, 1.25)]) { InitialCaseId = PDelta, GeometricMode = GeometricNonlinearity.PDelta });
            e.PutLoadCase(new(NlAlternate, "NL_ALTERNATE", CaseKind.NonlinearStatic, [new(WindX, 1)]) { InitialCaseId = NlPret });
            e.PutLoadCombination(new(CombWind, "COMB_WIND", CombinationType.LinearAdd, [new(LinWind, 1.5), new(LinLive, 1.05)]));
            Document = e.Commit().Document;
        }

        public string Name(ObjectId id) => Document.TryGetNamed(id, out _, out var name) ? name : id.ToString();
    }

    private static void WorkedModel()
    {
        var w = new Worked();
        var d = w.Document;
        Assert(DocumentValidator.Validate(d).Count == 0 && d.LoadCases.Count == 8 && d.MassSources.Count == 1,
            "The plan's worked model — a fitted pretension case, an initial P-Delta case continuing from it, two linear cases and a modal case on its end stiffness, two nonlinear branches from it and one from the pretension case — is a valid document, without a warning");
        Assert(d.LoadCases[w.PDelta] is { Kind: CaseKind.InitialPDelta, Type: LoadCaseType.NonlinearStatic, GeometricMode: GeometricNonlinearity.PDelta, ProducesState: true } &&
               d.LoadCases[w.Modes] is { Type: LoadCaseType.ModalEigen, ProducesState: false } && d.LoadCases[w.LinWind] is { Type: LoadCaseType.LinearStatic, ProducesState: false } &&
               d.LoadCases[w.NlSls] is { Type: LoadCaseType.NonlinearStatic, GeometricMode: GeometricNonlinearity.None } && d.LoadCases[w.NlUls].GeometricMode == GeometricNonlinearity.PDelta,
            "Four kinds, three methods: an initial P-Delta case is a nonlinear static case whose geometric nonlinearity is P-Delta without being told; a nonlinear case has none unless it is set; a linear static and a modal case end in no state");
        Assert(d.LoadCases[w.NlSls].InitialState == new InitialState(InitialStateKind.EndOfNonlinearCase, w.PDelta) && d.LoadCases[w.NlPret].InitialState == new InitialState(InitialStateKind.Zero, null) &&
               d.LoadCases[w.LinWind].StiffnessSource == new StiffnessSource(StiffnessSourceKind.EndOfNonlinearCase, w.PDelta) &&
               d.LoadCases[w.NlSls].Dependencies.SequenceEqual([new(CaseDependencyKind.ContinueState, w.PDelta)]) &&
               d.LoadCases[w.LinWind].Dependencies.SequenceEqual([new(CaseDependencyKind.FrozenStiffness, w.PDelta)]) &&
               d.LoadCases[w.Modes].Dependencies.SequenceEqual([new(CaseDependencyKind.FrozenStiffness, w.PDelta), new(CaseDependencyKind.MassSource, w.Ms1)]),
            "The two references are two things: NL_SLS continues the state of PDELTA_GRAVITY; LIN_WINDX and MODES_PD only read its stiffness, and MODES_PD its mass source");
    }

    /// <summary>CaseDAG: prerequisites, two branches, what a failed parent blocks.</summary>
    private static void RunGraph()
    {
        var w = new Worked();
        var plan = CaseDependencyPlanner.Plan(w.Document);
        string Order(CaseRunPlan p) => string.Join(" ", p.Cases.Select(item => $"{w.Name(item.CaseId)}{(item.Role == CaseRunRole.Prerequisite ? "*" : "")}"));
        Assert(plan.IsValid && Order(plan) == "NL_PRET* PDELTA_GRAVITY LIN_WINDX LIN_LIVE* MODES_PD NL_SLS NL_ULS NL_ALTERNATE" && plan.Combinations.SequenceEqual([w.CombWind]),
            "A run of what is set to run solves each case once, after what it needs, and by id among cases that are ready: " + Order(plan) +
            " — NL_PRET and LIN_LIVE are not set to run and are solved as prerequisites (*), of the P-Delta case and of the combination; the combination is evaluated after the cases");
        var modes = CaseDependencyPlanner.Plan(w.Document, [w.Modes]);
        Assert(Order(modes) == "NL_PRET* PDELTA_GRAVITY* MODES_PD" && modes.Combinations.IsEmpty,
            "Asked for the modal case alone, the run solves the pretension case and the P-Delta case first, as prerequisites: " + Order(modes));
        Assert(Order(CaseDependencyPlanner.Plan(w.Document, [w.NlSls, w.NlUls])) == "NL_PRET* PDELTA_GRAVITY* NL_SLS NL_ULS" &&
               !plan.BlockedBy([w.NlSls]).Contains(w.NlUls) && plan.BlockedBy([w.NlSls]).IsEmpty,
            "NL_SLS and NL_ULS are independent branches of one parent: each needs PDELTA_GRAVITY, neither needs the other, and one failing blocks nothing");
        string Blocked(params ObjectId[] failed) => string.Join(" ", plan.BlockedBy(failed).Select(w.Name));
        Assert(Blocked(w.PDelta) == "LIN_WINDX LIN_LIVE MODES_PD NL_SLS NL_ULS COMB_WIND",
            "A P-Delta case without a result blocks every case that continues from it or uses its stiffness, and the combination of them — not NL_ALTERNATE, which branches off earlier: " + Blocked(w.PDelta));
        Assert(Blocked(w.NlPret) == "PDELTA_GRAVITY LIN_WINDX LIN_LIVE MODES_PD NL_SLS NL_ULS NL_ALTERNATE COMB_WIND" && Blocked(w.LinLive) == "COMB_WIND" && Blocked(w.Modes) == "",
            "… the pretension case blocks everything after it, a linear case only the combination that adds it, the modal case nothing");
        Assert(string.Join(" ", CaseDependencyPlanner.Dependents(w.Document, w.PDelta).Select(w.Name)) == "LIN_WINDX LIN_LIVE MODES_PD NL_SLS NL_ULS COMB_WIND" &&
               CaseDependencyPlanner.Dependents(w.Document, w.NlAlternate).IsEmpty,
            "When PDELTA_GRAVITY changes, the results of everything that rests on it are stale with it — the same six; nothing rests on NL_ALTERNATE");
        var missing = CaseDependencyPlanner.Plan(w.Document, [w.LinWind, Id(77)]);
        Assert(!missing.IsValid && missing.Issues.Single() is { Code: "plan.missing", Severity: ValidationSeverity.Error },
            "Asked for a case that does not exist, the plan says so and is not valid");
    }

    /// <summary>What each kind of case may carry: every wrong pairing is refused by the document, with its own code.</summary>
    private static void KindRules()
    {
        var w = new Worked();
        void Refused(string code, string description, Action<DocumentEditor> edit)
        {
            var editor = w.Document.Edit();
            edit(editor);
            var exception = Throws<DocumentException>(() => editor.Commit(), description);
            Assert(exception.Error == DocumentError.Invalid && exception.Issues.Any(issue => issue.Code == code),
                $"… with {code}: {exception.Issues.FirstOrDefault(issue => issue.Code == code)?.Message ?? string.Join(" | ", exception.Issues.Select(issue => issue.Code))}");
        }
        var d = w.Document;
        var linear = d.LoadCases[w.LinWind];
        Refused("case.initial.kind", "A linear static case cannot continue from a case", e => e.PutLoadCase(linear with { InitialCaseId = w.NlPret }));
        Refused("case.fit", "A linear static case cannot fit cable target forces", e => e.PutLoadCase(linear with { FitCableTargetForces = true }));
        Refused("case.geometric", "A linear static case has no geometric nonlinearity", e => e.PutLoadCase(linear with { GeometricMode = GeometricNonlinearity.PDelta }));
        Refused("case.controls", "A linear static case has no iteration to control", e => e.PutLoadCase(linear with { NonlinearControls = new(1, 50, 1e-8, 1e-8) }));
        Refused("case.stiffness.producer", "A case cannot use the stiffness of a linear static case", e => e.PutLoadCase(linear with { StiffnessSourceCaseId = w.LinLive }));
        Refused("case.stiffness.producer", "… nor that of a modal case", e => e.PutLoadCase(linear with { StiffnessSourceCaseId = w.Modes }));
        Refused("case.stiffness", "… nor that of a case that does not exist", e => e.PutLoadCase(linear with { StiffnessSourceCaseId = Id(78) }));
        Refused("case.stiffness.kind", "A nonlinear static case forms its own stiffness: it has no stiffness source", e => e.PutLoadCase(d.LoadCases[w.NlSls] with { StiffnessSourceCaseId = w.NlPret }));
        Refused("case.initial.producer", "A nonlinear case cannot continue from a linear static case", e => e.PutLoadCase(d.LoadCases[w.NlSls] with { InitialCaseId = w.LinWind }));
        Refused("case.initial.producer", "… nor from a modal case", e => e.PutLoadCase(d.LoadCases[w.NlSls] with { InitialCaseId = w.Modes }));
        Refused("case.controls.value", "Iteration controls need steps, iterations and tolerances above zero", e => e.PutLoadCase(d.LoadCases[w.NlSls] with { NonlinearControls = new(0, 50, 1e-8, 1e-8) }));
        Refused("case.geometric", "An initial P-Delta case is P-Delta: it cannot be set to none", e => e.PutLoadCase(d.LoadCases[w.PDelta] with { GeometricMode = GeometricNonlinearity.None }));
        Refused("case.fit", "An initial P-Delta case does not fit cable target forces", e => e.PutLoadCase(d.LoadCases[w.PDelta] with { FitCableTargetForces = true }));
        Refused("case.modal.loads", "A modal case has no loads", e => e.PutLoadCase(d.LoadCases[w.Modes] with { Components = [new(w.Dead, 1)] }));
        Refused("case.modal", "A modal case needs its mass source and number of modes", e => e.PutLoadCase(d.LoadCases[w.Modes] with { ModalOptions = null }));
        Refused("case.modal.massSource", "… a mass source that exists", e => e.PutLoadCase(d.LoadCases[w.Modes] with { ModalOptions = new(Id(79), 12) }));
        Refused("case.modal.value", "… and at least one mode", e => e.PutLoadCase(d.LoadCases[w.Modes] with { ModalOptions = new(w.Ms1, 0) }));
        Refused("case.modal", "Modal options belong to a modal case only", e => e.PutLoadCase(d.LoadCases[w.NlSls] with { ModalOptions = new(w.Ms1, 3) }));
        Refused("case.initial.kind", "A modal case does not continue from a case", e => e.PutLoadCase(d.LoadCases[w.Modes] with { InitialCaseId = w.PDelta }));
        Refused("case.cycle", "No case depends on itself through the cases it continues from", e =>
        {
            e.PutLoadCase(d.LoadCases[w.NlPret] with { InitialCaseId = w.NlSls, FitCableTargetForces = false });
        });
        Refused("combination.modal", "A combination cannot add a modal case: modes are not load-case results",
            e => e.PutLoadCombination(new(Id(22), "COMB_MODES", CombinationType.LinearAdd, [new(w.LinWind, 1), new(w.Modes, 1)])));

        // linear results about two different stiffnesses do not superpose: said, as a warning
        var mixed = w.Document.Edit();
        mixed.PutLoadCase(new(Id(19), "LIN_UNSTRESSED", CaseKind.LinearStatic, [new(w.WindX, 1)]));
        mixed.PutLoadCombination(new(Id(23), "COMB_MIXED", CombinationType.LinearAdd, [new(w.LinWind, 1), new(Id(19), 1)]));
        var warned = DocumentValidator.Validate(mixed.Commit().Document);
        Assert(warned.Single() is { Severity: ValidationSeverity.Warning, Code: "combination.stiffness.mixed" } warning && warning.Message.Contains("unstressed") && warning.Message.Contains("PDELTA_GRAVITY"),
            "A combination that adds linear cases linearized about different stiffnesses is kept, with a warning that their results do not superpose: " + warned.Single().Message);
    }

    private static void ReferencesAndHash()
    {
        var w = new Worked();
        var d = w.Document;
        var renamed = d.Edit();
        renamed.Rename(w.PDelta, "PD_G");
        var after = renamed.Commit().Document;
        Assert(after.LoadCases[w.PDelta].Name == "PD_G" && after.LoadCases[w.LinWind].StiffnessSourceCaseId == w.PDelta && after.LoadCases[w.NlSls].InitialCaseId == w.PDelta &&
               after.AnalysisInputHash == d.AnalysisInputHash,
            "Renaming a case keeps its id, every reference to it, and the analysis input");

        DocumentException InUse(ObjectId id, string description)
        {
            var editor = d.Edit();
            var exception = Throws<DocumentException>(() => editor.Remove([id]), description);
            Assert(exception.Error == DocumentError.InUse, "… as in use: " + exception.Message);
            return exception;
        }
        Assert(InUse(w.PDelta, "A case another case continues from, or takes its stiffness from, cannot be removed").Message.Contains("PDELTA_GRAVITY"), "… naming it");
        InUse(w.Ms1, "A mass source a modal case uses cannot be removed");
        var free = d.Edit();
        free.Remove([w.NlAlternate, w.CombWind, w.LinLive]);
        Assert(free.Commit().Document.LoadCases.Count == 6, "A case nothing refers to is removed, and a linear case once the combination that added it is gone");

        string Hash(Action<DocumentEditor> edit)
        {
            var editor = d.Edit();
            edit(editor);
            return editor.Commit().Document.AnalysisInputHash;
        }
        string[] changed =
        [
            Hash(e => e.PutLoadCase(d.LoadCases[w.NlSls] with { GeometricMode = GeometricNonlinearity.PDelta })),
            Hash(e => e.PutLoadCase(d.LoadCases[w.LinWind] with { StiffnessSourceCaseId = null })),
            Hash(e => e.PutLoadCase(d.LoadCases[w.Modes] with { ModalOptions = new(w.Ms1, 6) })),
            Hash(e => e.PutLoadCase(d.LoadCases[w.NlSls] with { NonlinearControls = new(4, 60, 1e-8, 1e-7) })),
            Hash(e => e.PutMassSource(new(w.Ms1, "MS1", IncludeSelfMass: false))),
            Hash(e => e.PutLoadCase(d.LoadCases[w.PDelta] with { InitialCaseId = null })),
        ];
        Assert(changed.Append(d.AnalysisInputHash).Distinct().Count() == 7,
            "The geometric nonlinearity, the stiffness source, the number of modes, the iteration controls, the mass source and the case continued from are each part of the analysis input: changing one changes its hash");
    }

    /// <summary>No silent fallback: what is defined but not verified is refused, by name, and nothing is computed for it.</summary>
    private static void NotSolvedYet()
    {
        var w = new Worked();
        var compiled = ModelCompiler.Compile(w.Document);
        Assert(compiled.Success, "The worked model compiles: " + string.Join("; ", compiled.Issues));
        var outcomes = StaticSolver.Solve(compiled.Model!, [.. w.Document.LoadCases.Keys.OrderBy(id => id)]);
        bool Refused(ObjectId id, string code) => outcomes[id] is { Success: false, Result: null } outcome && outcome.Issues.Single().Code == code;
        string Said(ObjectId id) => outcomes[id].Issues.Single().Message;
        Assert(outcomes[w.NlPret].Success && outcomes[w.NlAlternate].Success &&
               Math.Abs(outcomes[w.NlPret].Result!.Cables.Single().Tension - 60) <= 60 * w.Document.Settings.CableFitTolerancePercent / 100,
            $"The first-order nonlinear cases are solved as before: NL_PRET fits the stay to its target ({outcomes[w.NlPret].Result!.Cables.Single().Tension:0.######} kN) and NL_ALTERNATE continues from it");
        Assert(Refused(w.PDelta, "static.case.pdelta") && Said(w.PDelta).Contains("not replaced by a first-order analysis"),
            "The initial P-Delta case is not solved, and not as a first-order case: " + Said(w.PDelta));
        Assert(Refused(w.NlUls, "static.case.pdelta") && Refused(w.NlSls, "static.case.pdelta") && Said(w.NlSls).Contains("continues from 'PDELTA_GRAVITY'"),
            "… nor a nonlinear case with P-Delta, nor a first-order case that continues from a P-Delta case — its start does not exist: " + Said(w.NlSls));
        Assert(Refused(w.LinWind, "static.case.stiffnessSource") && Said(w.LinWind).Contains("not solved with the unstressed stiffness instead"),
            "A linear case on the end stiffness of another case is not solved with the unstressed stiffness in its place: " + Said(w.LinWind));
        Assert(Refused(w.Modes, "static.case.modal"), "A modal case is not solved: " + Said(w.Modes));

        var controlled = w.Document.Edit();
        controlled.PutLoadCase(w.Document.LoadCases[w.NlAlternate] with { NonlinearControls = new(5, 40, 1e-8, 1e-8) });
        var withControls = StaticSolver.Solve(ModelCompiler.Compile(controlled.Commit().Document).Model!, w.NlAlternate);
        Assert(withControls is { Success: false, Result: null } && withControls.Issues.Single().Code == "static.case.controls",
            "A case with its own load steps and tolerances is not solved in one step with the solver's own instead: " + withControls.Issues.Single().Message);
    }

    /// <summary>Schema 5 of the native file: every kind round-trips, and a case that carries the wrong reference is refused.</summary>
    private static void FileSchema4()
    {
        var w = new Worked();
        var controlled = w.Document.Edit();
        controlled.PutLoadCase(w.Document.LoadCases[w.NlSls] with { NonlinearControls = new(5, 40, 1e-8, 1e-7) });
        var document = controlled.Commit().Document;
        string json = NativeDocumentFormat.Serialize(document);
        var read = NativeDocumentFormat.Deserialize(json);
        Assert(SameContent(read, document) && read.AnalysisInputHash == document.AnalysisInputHash && read.MassSources[w.Ms1] == document.MassSources[w.Ms1],
            "A document with all four kinds of case, both references, iteration controls and a mass source is saved and opened whole: same content, same analysis input");

        var file = (JsonObject)JsonNode.Parse(json)!;
        JsonObject Case(JsonObject root, ObjectId id) => root["loadCases"]!.AsArray().Select(node => node!.AsObject()).Single(item => (string?)item["id"] == id.ToString());
        Assert((int)file["schemaVersion"]! == 8 && NativeDocumentFormat.CurrentSchemaVersion == 8 && file["massSources"]!.AsArray().Count == 1 &&
               (string?)Case(file, w.PDelta)["kind"] == "InitialPDelta" && (string?)Case(file, w.PDelta)["geometricMode"] == "PDelta" &&
               (string?)Case(file, w.PDelta)["initialState"]!["caseId"] == w.NlPret.ToString() && !Case(file, w.PDelta).ContainsKey("stiffnessSource") &&
               (string?)Case(file, w.NlPret)["initialState"]!["kind"] == "Zero" && !Case(file, w.NlPret)["initialState"]!.AsObject().ContainsKey("caseId") &&
               (string?)Case(file, w.LinWind)["stiffnessSource"]!["kind"] == "EndOfNonlinearCase" && !Case(file, w.LinWind).ContainsKey("initialState") &&
               (string?)Case(file, w.Modes)["modal"]!["massSourceId"] == w.Ms1.ToString() && (int)Case(file, w.Modes)["modal"]!["requestedModes"]! == 12 &&
               !Case(file, w.Modes).ContainsKey("type") && !Case(file, w.NlSls).ContainsKey("initialCaseId"),
            "The file is schema 7 (the load cases as schema 5 wrote them): a case states its kind by name and the one reference of its kind in full, by id — no type, no bare initial case id");

        void Refuse(string description, Action<JsonObject> damage)
        {
            var damaged = (JsonObject)JsonNode.Parse(json)!;
            damage(damaged);
            Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize(damaged.ToJsonString()), description);
        }
        Refuse("A nonlinear case that also states a stiffness source is refused", f => Case(f, w.NlSls)["stiffnessSource"] = new JsonObject { ["kind"] = "Unstressed" });
        Refuse("A linear case that states an initial state is refused", f => Case(f, w.LinWind)["initialState"] = new JsonObject { ["kind"] = "Zero" });
        Refuse("A nonlinear case without its initial state is refused — never read as starting from zero", f => Case(f, w.NlSls).Remove("initialState"));
        Refuse("A linear case without its stiffness source is refused — never read as unstressed", f => Case(f, w.LinWind).Remove("stiffnessSource"));
        Refuse("A case that continues from the end of a case it does not name is refused", f => Case(f, w.NlSls)["initialState"]!.AsObject().Remove("caseId"));
        Refuse("A case that starts from zero and names a case is refused", f => Case(f, w.NlPret)["initialState"]!["caseId"] = w.NlAlternate.ToString());
        Refuse("A case of a kind this version does not know is refused", f => Case(f, w.NlSls)["kind"] = "Buckling");
        Refuse("… also a kind written as a number", f => Case(f, w.NlSls)["kind"] = 1);
        Refuse("A geometric nonlinearity this version does not know is refused", f => Case(f, w.NlUls)["geometricMode"] = "LargeDisplacement");
        Refuse("A file without its mass sources is refused", f => f.Remove("massSources"));
        Refuse("A modal case on a mass source the file does not hold is refused", f => f["massSources"]!.AsArray().Clear());
        Refuse("An initial P-Delta case set to no geometric nonlinearity is refused", f => Case(f, w.PDelta)["geometricMode"] = "None");
    }

    /// <summary>SchemaRoundTrip, native part: a file as schema 3 wrote it (Fixtures/schema3-cases.mcotis) opens in
    /// schema 5 with every case meaning what it meant.</summary>
    private static void Schema3Upgrade()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema3-cases.mcotis");
        var bytes = File.ReadAllBytes(path);
        string text = File.ReadAllText(path);
        var old = (JsonObject)JsonNode.Parse(text)!;
        var upgraded = new NativeDocumentStore().Load(path);
        var oldCases = old["loadCases"]!.AsArray().Select(node => node!.AsObject()).ToArray();
        Assert((int)old["schemaVersion"]! == 3 && oldCases.Length == 4 && upgraded.LoadCases.Count == 4 && upgraded.MassSources.IsEmpty &&
               File.ReadAllBytes(path).SequenceEqual(bytes) && DocumentValidator.Validate(upgraded).All(issue => issue.Severity != ValidationSeverity.Error),
            "A schema-3 file opens in schema 5: its four load cases, no mass source made up, the file on disk untouched");
        foreach (var item in oldCases)
        {
            var id = ObjectId.Parse((string)item["id"]!);
            var now = upgraded.LoadCases[id];
            string type = (string)item["type"]!;
            ObjectId? initial = item["initialCaseId"] is { } parent ? ObjectId.Parse((string)parent!) : null;
            var components = item["components"]!.AsArray().Select(node => (Pattern: ObjectId.Parse((string)node!["patternId"]!), Factor: (double)node["factor"]!)).ToArray();
            Assert(now.Name == (string)item["name"]! && now.Kind.ToString() == type && now.Type.ToString() == type && now.Run == (bool)item["run"]! &&
                   now.FitCableTargetForces == (bool)item["fitCableTargetForces"]! && now.GeometricMode == GeometricNonlinearity.None &&
                   now.InitialCaseId == initial && now.StiffnessSourceCaseId is null && now.NonlinearControls is null && now.ModalOptions is null &&
                   now.Components.Select(component => (component.PatternId, component.Factor)).SequenceEqual(components),
                $"… '{now.Name}' ({type}{(initial is null ? "" : ", continuing from '" + upgraded.LoadCases[initial.Value].Name + "'")}{(now.Run ? "" : ", not set to run")}) keeps its id, " +
                $"its {components.Length} patterns and factors ({string.Join(", ", components.Select(component => component.Factor))}), its run and fit flags; no P-Delta is switched on");
        }
        var reopened = NativeDocumentFormat.Deserialize(NativeDocumentFormat.Serialize(upgraded));
        Assert(SameContent(reopened, upgraded) && reopened.AnalysisInputHash == upgraded.AnalysisInputHash, "… and saved as schema 5 it opens the same again");

        // a linear case that named an initial case had no meaning to keep
        var linear = oldCases.Single(item => (string)item["type"]! == "LinearStatic");
        linear["initialCaseId"] = (string)oldCases.First(item => (string)item["type"]! == "NonlinearStatic")["id"]!;
        var refused = Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize(old.ToJsonString()),
            "A schema-3 linear static case that names an initial case is refused, not guessed at");
        Assert(refused.Message.Contains("'WIND'") && refused.Message.Contains("linear static"), "… by its name: " + refused.Message);
    }
}
