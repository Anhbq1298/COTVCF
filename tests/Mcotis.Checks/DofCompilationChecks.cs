using System.Text.Json.Nodes;
using Mcotis.Analysis;
using Mcotis.Application;
using Mcotis.Core;
using Mcotis.Persistence;
using static Check;

internal static class DofCompilationChecks
{
    private static readonly ObjectId A = ObjectId.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly ObjectId B = ObjectId.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly ObjectId Member = ObjectId.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly ObjectId Material = ObjectId.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly ObjectId Section = ObjectId.Parse("00000000-0000-0000-0000-000000000005");
    private static readonly ObjectId Pattern = ObjectId.Parse("00000000-0000-0000-0000-000000000006");

    public static void Run()
    {
        PresetsAndMapping();
        CommandsAndSnapshot();
        Compatibility();
        Files();
    }

    private static DocumentSession Frame(DofMask mask = DofMask.Full3D, Xyz? end = null, bool reverseOrder = false, bool cable = false)
    {
        var editor = ModelDocument.Create().Edit();
        editor.PutMaterial(new(Material, "Steel", MaterialType.Steel, 2e8, 0.3, 77, 7.85));
        if (reverseOrder) { editor.AddPoint(end ?? new Xyz(4, 0, 0), "B", B); editor.AddPoint(default, "A", A); }
        else { editor.AddPoint(default, "A", A); editor.AddPoint(end ?? new Xyz(4, 0, 0), "B", B); }
        if (cable)
        {
            editor.PutCableSection(new(Section, "Strand", Material, 0.01));
            editor.AddCable(A, B, "Cable", Member);
            editor.SetCableSection([Member], Section);
        }
        else
        {
            editor.PutFrameSection(new(Section, "Beam", Material, FrameSectionShape.General, new(0.01, 0.0008, 0.0004, 0.00001)));
            editor.AddFrame(A, B, "Beam", Member);
            editor.SetFrameSection([Member], Section);
        }
        editor.SetRestraint([A], Restraint.Fixed);
        editor.PutLoadPattern(new(Pattern, "LOAD", LoadPatternType.Other));
        editor.SetSettings(AnalysisSettings.Default with { ActiveDofs = mask });
        return new(editor.Commit().Document);
    }

    private static CompiledModel Compile(DocumentSession s)
    {
        var result = ModelCompiler.Compile(s.Document, s.Revision);
        Assert(result.Success, "Compatible topology compiles: " + string.Join("; ", result.Issues));
        return result.Model!;
    }

    private static void PresetsAndMapping()
    {
        Assert(ModelDocument.Create().Settings.ActiveDofs == DofMask.Full3D, "New native models default to Full 3D");
        Assert(DofMask.XZ == (DofMask.UX | DofMask.UZ | DofMask.RY) &&
               DofMask.YZ == (DofMask.UY | DofMask.UZ | DofMask.RX) &&
               DofMask.XY == (DofMask.UX | DofMask.UY | DofMask.RZ), "Plane presets use the correct translations and normal rotation");
        foreach (var (mask, end) in new[] { (DofMask.Full3D, new Xyz(4, 3, 2)), (DofMask.XZ, new Xyz(4, 0, 2)),
                     (DofMask.YZ, new Xyz(0, 4, 2)), (DofMask.XY, new Xyz(4, 2, 0)) })
        {
            var s = Frame(mask, end);
            var model = Compile(s);
            var active = DegreesOfFreedom.Components.Count(component => mask.Contains(component));
            Assert(model.Dofs.Length == 12 && model.FreeDofCount == active &&
                model.Dofs.Count(dof => dof.State == DofState.Restrained) == active, "Two joints always retain 12 full slots; active support/free slots follow the preset");
            Assert(model.Dofs.Where(dof => dof.State == DofState.Free).Select(dof => dof.ReducedIndex!.Value).SequenceEqual(Enumerable.Range(0, active)),
                "Free equation indices are contiguous, with no gaps left by inactive or restrained DOFs");
            Assert(model.Points[1].Position == end && model.InputHash == s.Document.AnalysisInputHash && model.DocumentId == s.Document.DocumentId,
                "XYZ geometry, document identity and analysis hash survive compilation");
            Assert(model.Dofs.Take(6).Select(dof => dof.Key.Component).SequenceEqual(DegreesOfFreedom.Components), "Joint order is UX UY UZ RX RY RZ");
            var reordered = Compile(Frame(mask, end, reverseOrder: true));
            Assert(model.Dofs.SequenceEqual(reordered.Dofs) && model.Members.SequenceEqual(reordered.Members), "Point insertion order does not change equation or member mapping");
        }
        Assert(Compile(Frame(DofMask.UX)).FreeDofCount == 1, "A custom axial-only mask is compatible with a collinear frame and suppressed rotations");
        Throws<ArgumentOutOfRangeException>(() => ((DofComponent)99).Mask(), "Unknown DOF components cannot alias a real bit");
    }

    private static void CommandsAndSnapshot()
    {
        var s = Frame(DofMask.XZ);
        var saved = s.Document;
        s.MarkSaved(saved, Path.Combine(Path.GetTempPath(), "dofs.mcotis"));
        var tracker = new AnalysisResultTracker(s);
        tracker.Complete(tracker.Begin());
        var compiled = Compile(s);
        var changes = s.Execute(DocumentCommands.SetSettings(s.Document.Settings with { ActiveDofs = DofMask.Full3D }));
        Assert(s.IsDirty && tracker.State == ResultState.Stale && changes.Affects(ObjectKind.Settings), "Changing DOFs is undoable, dirty, reported in the change set and makes results stale");
        Assert(compiled.ActiveDofs == DofMask.XZ && ReferenceEquals(compiled.Input, saved), "Edits cannot alter an already compiled snapshot");
        s.Undo();
        Assert(!s.IsDirty && tracker.State == ResultState.Current && ReferenceEquals(saved, s.Document), "Undo restores saved assumptions and current results");
        s.Redo();
        Assert(s.Document.Settings.ActiveDofs == DofMask.Full3D && tracker.State == ResultState.Stale, "Redo restores DOFs and invalidation");
        var beforeRename = Compile(s);
        s.Execute(DocumentCommands.Rename(B, "Tip"));
        var afterRename = Compile(s);
        Assert(beforeRename.InputHash == afterRename.InputHash && beforeRename.Dofs.SequenceEqual(afterRename.Dofs), "Rename leaves compiled connectivity and numerical identity unchanged");
        foreach (var mask in new[] { DofMask.None, (DofMask)64, (DofMask)(-1) })
            Refused(s, DocumentCommands.SetSettings(s.Document.Settings with { ActiveDofs = mask }), DocumentError.Invalid, "Invalid DOF masks leave the session unchanged");
    }

    private static void Compatibility()
    {
        void RefuseCompile(DocumentSession s, string code)
        {
            var (document, revision) = (s.Document, s.Revision);
            var result = ModelCompiler.Compile(document, revision);
            Assert(!result.Success && result.Model is null && result.Issues.Any(issue => issue.Code == code && issue.Severity == ValidationSeverity.Error),
                "Compilation refuses incompatible input with diagnostic " + code);
            Assert(ReferenceEquals(document, s.Document) && revision == s.Revision, "Failed compilation leaves the document and revision untouched");
        }
        RefuseCompile(Frame(DofMask.XZ, new(4, 0.01, 0)), "dof.geometry");
        RefuseCompile(Frame(DofMask.RY), "dof.translations");
        var s = Frame(DofMask.XZ);
        s.Execute(DocumentCommands.PutLoad(new PointLoad(ObjectId.New(), Pattern, B, new(0, 2, 0))));
        RefuseCompile(s, "dof.load.inactive");
        s.Undo();
        s.Execute(DocumentCommands.PutLoad(new FrameDistributedLoad(ObjectId.New(), Pattern, Member, LoadDirection.Local2, 0, 1, -2, -2)));
        RefuseCompile(s, "dof.load.local.unverified");
        s.Undo();
        s.Execute(DocumentCommands.PutLoad(new FramePointLoad(ObjectId.New(), Pattern, Member, LoadDirection.GlobalY, 0.5, 1)));
        RefuseCompile(s, "dof.load.inactive");
        s.Undo();
        s.Execute(DocumentCommands.AssignFrameSection([Member], null));
        RefuseCompile(s, "analysis.section");
        s.Undo();
        s.Execute(DocumentCommands.AddPoint(ObjectId.New(), new(2, 0, 1)));
        RefuseCompile(s, "analysis.disconnected.point");
        s.Undo();
        // A point on a frame's axis is connected to it: the frame is one member per piece.
        var onFrame = ObjectId.New();
        s.Execute(DocumentCommands.AddPoint(onFrame, new(1, 0, 0)));
        var split = Compile(s);
        Assert(split.Members.Length == 2 && split.Members.All(member => member.ParentObjectId == Member && member.Kind == ObjectKind.Frame) &&
               split.Members[0] is { RelativeStart: 0, RelativeEnd: 0.25 } first && first.StartPointId == A && first.EndPointId == onFrame &&
               split.Members[1] is { RelativeStart: 0.25, RelativeEnd: 1 } second && second.StartPointId == onFrame && second.EndPointId == B,
            "A frame is compiled as one member per piece between the points lying on it, in order from its start");
        Assert(split.Points.Single(point => point.Id == onFrame).ElementDofs == DofMask.Full3D && split.FreeDofCount == 6,
            "A point lying on a frame has the frame's degrees of freedom there");
        s.Execute(DocumentCommands.AddPoint(ObjectId.New(), new(1, 0, 0)));
        RefuseCompile(s, "analysis.frame.coincidentPoint");
        s.Undo();
        s.Execute(DocumentCommands.AddPoint(ObjectId.New(), new(4, 0, 0)));
        RefuseCompile(s, "analysis.frame.coincidentPoint");
        s.Undo();
        s.Undo();
        s.Execute(DocumentCommands.AddPoint(ObjectId.New(), new(2, 0, 0.0005)));
        var near = ModelCompiler.Compile(s.Document);
        Assert(!near.Success && near.Issues.Any(issue => issue.Code == "analysis.frame.nearPoint" && issue.Severity == ValidationSeverity.Warning) &&
               near.Issues.Any(issue => issue.Code == "analysis.disconnected.point"),
            "A point half a millimetre off a frame's axis is not connected to it, and that is said");
        s.Undo();
        Assert(Compile(Frame()).Members is [{ RelativeStart: 0, RelativeEnd: 1 }], "A frame with no point on it is one member");
        var result = ModelCompiler.Compile(s.Document);
        Assert(result.Success && result.Issues.Any(issue => issue.Code == "dof.restraint.inactive"), "Inactive support components are retained and explicitly reported");
        s.Execute(DocumentCommands.PutLoad(new PointLoad(ObjectId.New(), Pattern, A, new(-10, 0, -20), new(0, 30, 0))));
        s.Execute(DocumentCommands.PutLoad(new PointLoad(ObjectId.New(), Pattern, B, new(40, 0, 50))));
        var model = Compile(s);
        Assert(model.PointLoadsByPattern[Pattern].SequenceEqual(new double[] { -10, 0, -20, 0, 30, 0, 40, 0, 50, 0, 0, 0 }),
            "Full nodal load vector preserves signed support forces and moments for future reaction recovery");
        var cable = Frame(cable: true);
        Assert(Compile(cable).FreeDofCount == 3 && Compile(cable).DofIndex[new(B, DofComponent.RY)].InactiveReason == InactiveDofReason.ElementUnused,
            "Cable-only joints have translations, without artificial free rotations");
        cable.Execute(DocumentCommands.PutLoad(new PointLoad(ObjectId.New(), Pattern, B, default, new(0, 1, 0))));
        RefuseCompile(cable, "dof.load.inactive");
        var xy = Frame(DofMask.XY);
        xy.Execute(DocumentCommands.PutLoadPattern(new(Pattern, "LOAD", LoadPatternType.Dead, 1)));
        RefuseCompile(xy, "dof.selfweight.inactive");
        RefuseCompile(new Sample().Session, "analysis.shell.unsupported");
        RefuseCompile(new DocumentSession(), "analysis.empty");
        var overflow = Frame();
        for (var i = 0; i < 2; i++) overflow.Execute(DocumentCommands.PutLoad(new PointLoad(ObjectId.New(), Pattern, B, new(1e308, 0, 0))));
        RefuseCompile(overflow, "analysis.load.overflow");
    }

    private static void Files()
    {
        foreach (var mask in new[] { DofMask.Full3D, DofMask.XZ, DofMask.XY, DofMask.YZ, DofMask.UX | DofMask.RY })
        {
            var document = Frame(mask).Document;
            var restored = NativeDocumentFormat.Deserialize(NativeDocumentFormat.Serialize(document));
            Assert(SameContent(document, restored) && document.AnalysisInputHash == restored.AnalysisInputHash, "Save/Open preserves preset and custom masks: " + mask);
        }
        var file = JsonNode.Parse(NativeDocumentFormat.Serialize(Frame().Document))!.AsObject();
        file["settings"]!.AsObject().Remove("activeDofs");
        Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize(file.ToJsonString()), "Schema 2 cannot silently default a missing DOF mask");
        file["settings"]!["activeDofs"] = "None";
        Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize(file.ToJsonString()), "Schema 2 rejects an empty DOF mask");
        file["settings"]!["activeDofs"] = "Unknown";
        Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize(file.ToJsonString()), "Schema 2 rejects unknown DOF names");

        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema1-xz.mcotis");
        var original = File.ReadAllBytes(fixture);
        var stamp = File.GetLastWriteTimeUtc(fixture);
        var migrated = new NativeDocumentStore().Load(fixture);
        Assert(migrated.Settings.ActiveDofs == DofMask.XZ, "The committed schema-1 fixture migrates to XZ, not the new Full 3D default");
        Assert(migrated.Settings.CableDofs == DofMask.Translations, "… and its cable joints get the translations, as they had before there was a setting");
        // the degrees of freedom of cable joints: saved, required, validated, part of what the results depend on
        var cabled = Frame(cable: true);
        var before = cabled.Document.AnalysisInputHash;
        cabled.Execute(DocumentCommands.SetSettings(cabled.Document.Settings with { CableDofs = DofMask.UX | DofMask.UZ | DofMask.RY }));
        Assert(cabled.Document.AnalysisInputHash != before, "Changing the degrees of freedom of cable joints changes the analysis input");
        Assert(NativeDocumentFormat.Deserialize(NativeDocumentFormat.Serialize(cabled.Document)).Settings.CableDofs == (DofMask.UX | DofMask.UZ | DofMask.RY),
            "The degrees of freedom of cable joints are saved with the model");
        var compiledCable = Compile(cabled);
        Assert(compiledCable.Members.Single().SupportedDofs == (DofMask.UX | DofMask.UZ | DofMask.RY) &&
               compiledCable.DofIndex[new(B, DofComponent.RY)].State == DofState.Free && compiledCable.DofIndex[new(B, DofComponent.UY)] is { State: DofState.Inactive, InactiveReason: InactiveDofReason.ElementUnused } &&
               compiledCable.FreeDofCount == 3,
            "A joint only cables reach has exactly the degrees of freedom the model gives cable joints");
        Refused(cabled, DocumentCommands.SetSettings(cabled.Document.Settings with { CableDofs = DofMask.RX }), DocumentError.Invalid, "Cable joints without a translation are refused");
        cabled.Execute(DocumentCommands.SetSettings(cabled.Document.Settings with { ActiveDofs = DofMask.XZ, CableDofs = DofMask.UY }));
        Assert(ModelCompiler.Compile(cabled.Document) is { Success: false } blocked && blocked.Issues.Any(issue => issue.Code == "dof.cable.translations"),
            "Cable joints with no translation among the active ones do not compile");
        var older = (JsonObject)JsonNode.Parse(NativeDocumentFormat.Serialize(Frame(cable: true).Document))!;
        older["settings"]!.AsObject().Remove("cableDofs");
        Throws<DocumentFileException>(() => NativeDocumentFormat.Deserialize(older.ToJsonString()), "The current schema cannot silently default missing cable-joint degrees of freedom");
        older["schemaVersion"] = 2;
        LoadCasesAsSchema3(older);
        Assert(NativeDocumentFormat.Deserialize(older.ToJsonString()).Settings.CableDofs == DofMask.Translations, "A schema-2 file opens with the translations for its cable joints");
        var compiled = ModelCompiler.Compile(migrated).Model!;
        Assert(compiled.FreeDofCount == 3 && compiled.Dofs.Length == 12 && compiled.Input.Points[B].Position == new Xyz(4, 0, 0),
            "Legacy XZ fixture preserves geometry and the three free tip equations");
        Assert(File.ReadAllBytes(fixture).SequenceEqual(original) && File.GetLastWriteTimeUtc(fixture) == stamp, "Migration leaves the original fixture unchanged");
    }
}
