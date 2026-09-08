using System.Globalization;
using System.IO;
using COT_VCF.Application;
using COT_VCF.Domain;
using COT_VCF.Presentation;
using COT_VCF.Infrastructure;
using COT_VCF.Configuration;

internal static class ReliabilityChecks
{
    public static void Run()
    {
        InvalidNumericTextIsRejected();
        LoadingCandidateCountDoesNotMaterializeExtremeRanges();
        AtomicWritePreservesTheDestinationOnFailure();
        RecoveryUsesPerUserStorage();
        ConfigCompatibilityRejectsBrokenCableSync();
    }

    private static void AtomicWritePreservesTheDestinationOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cotvcf-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "project.json");
        File.WriteAllText(path, "original");
        try
        {
            var threw = false;
            try
            {
                AtomicFile.Write(path, temp =>
                {
                    File.WriteAllText(temp, "partial");
                    throw new IOException("simulated write failure");
                });
            }
            catch (IOException) { threw = true; }
            Check(threw && File.ReadAllText(path) == "original", "atomic write failure preserves the previous destination");
            AtomicFile.WriteAllText(path, "replacement");
            Check(File.ReadAllText(path) == "replacement", "atomic write replaces a completed destination");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void RecoveryUsesPerUserStorage()
    {
        Check(Path.GetFullPath(AppDataPaths.RecoveryDirectory).StartsWith(
                Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                StringComparison.OrdinalIgnoreCase),
            "recovery journals use writable per-user application storage");
    }

    private static void ConfigCompatibilityRejectsBrokenCableSync()
    {
        var state = StudyComposerChecks.CreateState();
        var extractor = new ConfigurationExtractor();
        var orphan = extractor.Extract(state, ["LIVE"], ConfigTabs.Loading | ConfigTabs.Truss | ConfigTabs.Cable);
        orphan.Tab04CableForce!.Cables[0].SyncMode = CableSyncMode.SameValues;
        orphan.Tab04CableForce.Cables[0].ReferenceCableName = "Missing-Cable";
        var orphanCheck = new ConfigCompatibilityValidator().Validate(orphan, state, ["LIVE"]);
        Check(!orphanCheck.Cable.Ok && orphanCheck.Cable.Problems.Any(p => p.Contains("does not exist", StringComparison.OrdinalIgnoreCase)),
            "config compatibility rejects an orphaned cable Sync reference");

        var geometry = new GeometryModel(state.Geometry.Points,
            state.Geometry.Members.Concat([new StructuralMember("Cable-B", "top", "bot", MemberKind.Cable)]).ToArray());
        state.Geometry = geometry;
        state.Baseline = geometry;
        state.SessionBaseline = geometry;
        state.Cables.Add(new CablePretensionConfig
        {
            CableName = "Cable-B", LoadPattern = CablePretensionConfig.PretensionLoadCase,
            ApplyRange = true, MinForce = 800, MaxForce = 1200, StepForce = 100
        });
        var cyclic = extractor.Extract(state, ["LIVE"], ConfigTabs.Loading | ConfigTabs.Truss | ConfigTabs.Cable);
        cyclic.Tab04CableForce!.Cables[0].SyncMode = CableSyncMode.SameValues;
        cyclic.Tab04CableForce.Cables[0].ReferenceCableName = "Cable-B";
        cyclic.Tab04CableForce.Cables[1].SyncMode = CableSyncMode.SameRatio;
        cyclic.Tab04CableForce.Cables[1].ReferenceCableName = "Cable-A";
        cyclic.Tab04CableForce.Cables[1].SyncFactor = 1.1;
        var cycleCheck = new ConfigCompatibilityValidator().Validate(cyclic, state, ["LIVE"]);
        Check(!cycleCheck.Cable.Ok && cycleCheck.Cable.Problems.Any(p => p.Contains("circular", StringComparison.OrdinalIgnoreCase)),
            "config compatibility rejects a circular cable Sync graph");

        cyclic.Tab04CableForce.Cables[1].ReferenceCableName = null;
        cyclic.Tab04CableForce.Cables[1].SyncFactor = double.PositiveInfinity;
        var factorCheck = new ConfigCompatibilityValidator().Validate(cyclic, state, ["LIVE"]);
        Check(!factorCheck.Cable.Ok && factorCheck.Cable.Problems.Any(p => p.Contains("ratio", StringComparison.OrdinalIgnoreCase)),
            "config compatibility rejects invalid Sync factors");
    }

    private static void InvalidNumericTextIsRejected()
    {
        var converter = new NullableDoubleConverter();
        var threw = false;
        try { converter.ConvertBack("not-a-number", typeof(double?), null, CultureInfo.InvariantCulture); }
        catch (FormatException) { threw = true; }
        Check(threw, "invalid nullable numeric text raises a binding validation error");
        Check(Equals(converter.ConvertBack("12.5", typeof(double?), null, CultureInfo.InvariantCulture), 12.5d),
            "valid nullable numeric text still commits normally");
    }

    private static void LoadingCandidateCountDoesNotMaterializeExtremeRanges()
    {
        var definition = new LoadDefinition
        {
            LoadName = "LIVE",
            ApplyIncrementStep = true,
            MinQz = 0,
            MaxQz = 1_000_000_000_000,
            IncrementStep = 0.000001
        };
        var count = LoadingCandidates.TotalCount([definition]);
        Check(count > LoadDefinition.MaterializeCeiling,
            "extreme loading range is counted without materializing its values");
        var threw = false;
        try { _ = definition.CandidateValues; }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "extreme loading range is blocked before allocation");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
