using System.Collections.Immutable;
using System.Reflection;
using Mcotis.Application;
using Mcotis.Core;
using Mcotis.Persistence;

internal static class Program
{
    public static int Main()
    {
        (string Name, Action Run)[] groups =
        [
            ("Naming", NamingChecks.Run),
            ("References", ReferenceChecks.Run),
            ("Validation", ValidationChecks.Run),
            ("Commands, undo/redo and revisions", SessionChecks.Run),
            ("Analysis input and result state", ResultStateChecks.Run),
            ("Native file", PersistenceChecks.Run),
            ("3D topology and DOF compilation", DofCompilationChecks.Run),
            ("Spatial linear static engineering benchmarks", SpatialStaticChecks.Run),
            ("General static solver: split frames, cables, target forces, stages and sag", GeneralStaticChecks.Run),
            ("TemplateBuilding generation, loading and analysis", TemplateBuildingChecks.Run),
            ("Independence", IndependenceChecks.Run),
        ];
        foreach (var (name, run) in groups)
        {
            var before = Check.Count;
            try { run(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL: {name} — {ex.Message}");
                Console.Error.WriteLine(ex);
                return 1;
            }
            Console.WriteLine($"PASS: {name} ({Check.Count - before} checks)");
        }
        Console.WriteLine($"PASS: {Check.Count} Mcotis checks; no SAP2000, no WPF, no database.");
        return 0;
    }
}

internal sealed class CheckFailedException(string message) : Exception(message);

internal static class Check
{
    public static int Count { get; private set; }

    public static void Assert(bool condition, string description)
    {
        if (!condition) throw new CheckFailedException(description);
        Count++;
    }

    /// <summary>The action must throw this exception; any other exception fails the run as it is.</summary>
    public static TException Throws<TException>(Action action, string description) where TException : Exception
    {
        try { action(); }
        catch (TException ex)
        {
            Count++;
            return ex;
        }
        throw new CheckFailedException(description + " — nothing was thrown");
    }

    /// <summary>A command the session must refuse, leaving its document as it was.</summary>
    public static DocumentException Refused(DocumentSession session, IDocumentCommand command, DocumentError error, string description)
    {
        var (document, revision, canUndo) = (session.Document, session.Revision, session.CanUndo);
        var refusal = Throws<DocumentException>(() => session.Execute(command), description);
        Assert(refusal.Error == error && ReferenceEquals(session.Document, document) && session.Revision == revision && session.CanUndo == canUndo,
            $"{description} — refused as {error}, document, revision and history untouched (was {refusal.Error}: {refusal.Message})");
        return refusal;
    }

    public static bool SameContent(ModelDocument a, ModelDocument b)
    {
        static bool Same<T>(ImmutableDictionary<ObjectId, T> x, ImmutableDictionary<ObjectId, T> y) where T : notnull =>
            x.Count == y.Count && x.All(pair => y.TryGetValue(pair.Key, out var other) && pair.Value.Equals(other));
        return a.DocumentId == b.DocumentId && a.Settings == b.Settings &&
            NamingService.Kinds.All(kind => a.Names.LastNumber(kind) == b.Names.LastNumber(kind)) &&
            Same(a.Points, b.Points) && Same(a.Frames, b.Frames) && Same(a.Cables, b.Cables) && Same(a.Shells, b.Shells) &&
            Same(a.Materials, b.Materials) && Same(a.FrameSections, b.FrameSections) && Same(a.CableSections, b.CableSections) &&
            Same(a.ShellSections, b.ShellSections) && Same(a.Restraints, b.Restraints) && Same(a.FrameAssignments, b.FrameAssignments) &&
            Same(a.CableAssignments, b.CableAssignments) && Same(a.ShellAssignments, b.ShellAssignments) &&
            Same(a.LoadPatterns, b.LoadPatterns) && Same(a.Loads, b.Loads) && Same(a.LoadCases, b.LoadCases) && Same(a.LoadCombinations, b.LoadCombinations);
    }
}

/// <summary>A small model with one of everything: a portal with a cable stay and a roof slab.</summary>
internal sealed class Sample
{
    public readonly ObjectId Steel = ObjectId.New(), Strand = ObjectId.New(), Concrete = ObjectId.New(),
        Beam = ObjectId.New(), CableSection = ObjectId.New(), Slab = ObjectId.New(),
        P1 = ObjectId.New(), P2 = ObjectId.New(), P3 = ObjectId.New(), P4 = ObjectId.New(), P5 = ObjectId.New(), P6 = ObjectId.New(),
        F1 = ObjectId.New(), F2 = ObjectId.New(), F3 = ObjectId.New(), C1 = ObjectId.New(), S1 = ObjectId.New(),
        Dead = ObjectId.New(), Live = ObjectId.New(), Prestress = ObjectId.New(),
        PointLoad = ObjectId.New(), LineLoad = ObjectId.New(), Target = ObjectId.New(), AreaLoad = ObjectId.New(),
        Pretension = ObjectId.New(), Sls = ObjectId.New(), Envelope = ObjectId.New();

    public DocumentSession Session { get; } = new();
    public ModelDocument Document => Session.Document;

    public Sample()
    {
        var s = Session;
        s.Execute(DocumentCommands.PutMaterial(new(Steel, "S355", MaterialType.Steel, 2.1e8, 0.3, 76.97, 7.849)));
        s.Execute(DocumentCommands.PutMaterial(new(Strand, "Y1860", MaterialType.Tendon, 1.95e8, 0.3, 77.0, 7.85)));
        s.Execute(DocumentCommands.PutMaterial(new(Concrete, "C40/50", MaterialType.Concrete, 3.5e7, 0.2, 25, 2.5)));
        s.Execute(DocumentCommands.PutFrameSection(new(Beam, "HB400", Steel, FrameSectionShape.ISection, new(0.0219, 6.66e-4, 2.24e-4, 2.7e-6, 0.0054, 0.014))
        {
            Dimensions = FrameSectionDefinition.NoDimensions.AddRange(new Dictionary<string, double> { ["t3"] = 0.4, ["t2"] = 0.4, ["tf"] = 0.021, ["tw"] = 0.013 }),
        }));
        s.Execute(DocumentCommands.PutCableSection(new(CableSection, "19T15.7", Strand, 0.00285)));
        s.Execute(DocumentCommands.PutShellSection(new(Slab, "SLAB200", Concrete, ShellSectionType.Shell, 0.2)));

        s.Execute(DocumentCommands.AddPoint(P1, new(0, 0, 0)));
        s.Execute(DocumentCommands.AddPoint(P2, new(0, 0, 5)));
        s.Execute(DocumentCommands.AddPoint(P3, new(6, 0, 5)));
        s.Execute(DocumentCommands.AddPoint(P4, new(6, 0, 0)));
        s.Execute(DocumentCommands.Edit("Add Generated Point", editor => editor.AddPoint(new(6, 4, 5), id: P5, origin: new(OriginKind.Generated, "slab:corner:3"))));
        s.Execute(DocumentCommands.AddPoint(P6, new(0, 4, 5)));
        s.Execute(DocumentCommands.AddFrame(F1, P1, P2));
        s.Execute(DocumentCommands.AddFrame(F2, P2, P3));
        s.Execute(DocumentCommands.AddFrame(F3, P4, P3));
        s.Execute(DocumentCommands.AddCable(C1, P1, P3));
        s.Execute(DocumentCommands.AddShell(S1, [P2, P3, P5, P6]));

        s.Execute(DocumentCommands.AssignRestraint([P1, P4], Restraint.Pinned));
        s.Execute(DocumentCommands.AssignFrameSection([F1, F2, F3], Beam));
        s.Execute(DocumentCommands.AssignCableSection([C1], CableSection));
        s.Execute(DocumentCommands.AssignCableModifiers([C1], CableModifiers.Identity with { Weight = 1.05 }));
        s.Execute(DocumentCommands.AssignFrameReleases([F2], new(new(false, false, false, false, false, true), new(false, false, false, false, false, true))));
        s.Execute(DocumentCommands.AssignFrameLocalAxis([F1], 90));
        s.Execute(DocumentCommands.AssignFrameModifiers([F3], FrameModifiers.Identity with { Inertia33 = 0.7 }));
        s.Execute(DocumentCommands.AssignShellSection([S1], Slab));

        s.Execute(DocumentCommands.PutLoadPattern(new(Dead, "DEAD", LoadPatternType.Dead, 1)));
        s.Execute(DocumentCommands.PutLoadPattern(new(Live, "LIVE", LoadPatternType.Live)));
        s.Execute(DocumentCommands.PutLoadPattern(new(Prestress, "PT", LoadPatternType.Prestress)));
        s.Execute(DocumentCommands.PutLoad(new PointLoad(PointLoad, Live, P2, new(0, 0, -100))));
        s.Execute(DocumentCommands.PutLoad(new FrameDistributedLoad(LineLoad, Dead, F2, LoadDirection.GlobalZ, 0, 1, -12, -12)));
        s.Execute(DocumentCommands.PutLoad(new CableTargetForce(Target, Prestress, C1, 500)));
        s.Execute(DocumentCommands.PutLoad(new ShellUniformLoad(AreaLoad, Live, S1, LoadDirection.GlobalZ, -5)));

        s.Execute(DocumentCommands.PutLoadCase(new(Pretension, "Pretension", LoadCaseType.NonlinearStatic, [new(Prestress, 1), new(Dead, 1)]) { FitCableTargetForces = true }));
        s.Execute(DocumentCommands.PutLoadCase(new(Sls, "SLS: 1.0DL+1.0LL", LoadCaseType.NonlinearStatic, [new(Dead, 1), new(Live, 1)]) { InitialCaseId = Pretension }));
        s.Execute(DocumentCommands.PutLoadCombination(new(Envelope, "ENV", CombinationType.Envelope, [new(Sls, 1), new(Pretension, 1)])));
    }
}

internal static class IndependenceChecks
{
    public static void Run()
    {
        static string[] References(Assembly assembly) => assembly.GetReferencedAssemblies().Select(name => name.Name ?? "").ToArray();
        static bool Runtime(string name) => name is "netstandard" or "mscorlib" or "System" ||
            (name.StartsWith("System.", StringComparison.Ordinal) && !name.StartsWith("System.Windows", StringComparison.Ordinal) &&
             !name.StartsWith("System.Data", StringComparison.Ordinal) && name != "System.Xaml");

        var core = References(typeof(ModelDocument).Assembly);
        var application = References(typeof(DocumentSession).Assembly);
        var persistence = References(typeof(NativeDocumentStore).Assembly);
        var analysis = References(typeof(Mcotis.Analysis.ModelCompiler).Assembly);
        Check.Assert(analysis.All(name => Runtime(name) || name == "Mcotis.Core"),
            "Mcotis.Analysis refers to Mcotis.Core and the runtime only: " + string.Join(", ", analysis));
        Check.Assert(core.All(Runtime), "Mcotis.Core refers to the .NET runtime only: " + string.Join(", ", core));
        Check.Assert(application.All(name => Runtime(name) || name == "Mcotis.Core"), "Mcotis.Application refers to Mcotis.Core and the runtime only: " + string.Join(", ", application));
        Check.Assert(persistence.All(name => Runtime(name) || name is "Mcotis.Core" or "Mcotis.Application"),
            "Mcotis.Persistence refers to Mcotis.Core, Mcotis.Application and the runtime only: " + string.Join(", ", persistence));

        // By now this process has created, edited, saved, reopened and tracked a model.
        string[] foreign = ["CSiAPIv1", "PresentationFramework", "PresentationCore", "WindowsBase", "Microsoft.Data.Sqlite", "Mcotis"];
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name ?? "").ToArray();
        Check.Assert(!loaded.Intersect(foreign, StringComparer.OrdinalIgnoreCase).Any(),
            "Creating, saving and reopening a model loads no SAP2000 API, WPF, database or legacy application assembly");
    }
}
