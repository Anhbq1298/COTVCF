using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using COT_VCF.Application.Workers;
using COT_VCF.Infrastructure;

namespace COT_VCF.Application.Quantities;

/// <summary>Reads the database tables of a saved SAP2000 model without touching that model or any SAP2000 the user
/// is working in.</summary>
public interface ISapModelTableReader
{
    Task<SapTableSet> ReadAsync(string modelPath, CancellationToken token);
}

public enum QuantitySource { TextExport, IsolatedSapRead, ConnectedSap }

/// <summary>The take-off of one candidate and where its data came from.</summary>
public sealed record QuantityLoadResult(QuantityCandidate Candidate, QuantityTakeoff Takeoff, QuantitySource Source, string SourceText);

/// <summary>Outcome of looking at a candidate's text export: the take-off, or why the export cannot be used.</summary>
public sealed record QuantityExportAttempt(QuantityLoadResult? Result, string? Rejected);

/// <summary>
/// Loads the quantity take-off of a study candidate from that candidate's own model data.
/// <para>First choice is SAP2000's text copy of the model (<c>.$2k</c>), which SAP2000 writes beside the
/// <c>.sdb</c> whenever it saves: it is used when it is this candidate's, complete, and no older than the model
/// except by the study run itself (the study saves the child — writing the export — and SAP2000 then writes the
/// model once more when the analysis finishes). Otherwise the model is read through an isolated SAP2000 instance.</para>
/// </summary>
public sealed class QuantityTakeoffService(ISapModelTableReader? sapReader = null)
{
    /// <summary>Without a run record, how much later than its export a model may have been written by the analysis.</summary>
    public static readonly TimeSpan UnrecordedAnalysisAllowance = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan WriteSlack = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RunSlack = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, QuantityLoadResult> sapReads = new(StringComparer.OrdinalIgnoreCase);

    public bool CanReadWithSap => sapReader is not null;

    /// <summary>Tries the candidate's text export. Never starts SAP2000.</summary>
    public Task<QuantityExportAttempt> TryTextExportAsync(QuantityCandidate candidate, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var export = candidate.TextExportPath;
        if (!File.Exists(export)) return new QuantityExportAttempt(null, $"there is no SAP2000 text export ({Path.GetFileName(export)}) beside the model");
        try
        {
            if (ExportIsOutdated(candidate) is { } outdated) return new(null, outdated);
            var tables = SapTableSet.ReadText(export);
            token.ThrowIfCancellationRequested();
            if (tables.RecordedFileName is { } recorded && !recorded.Equals(Path.GetFileName(export), StringComparison.OrdinalIgnoreCase))
                return new(null, $"the text export was written for another model ({recorded})");
            if (QuantityTakeoffCalculator.MissingInputs(tables) is { Count: > 0 } missing)
                return new(null, $"the text export does not hold {string.Join(", ", missing)}");
            var written = File.GetLastWriteTime(export).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return new(new(candidate, QuantityTakeoffCalculator.Calculate(tables), QuantitySource.TextExport,
                $"SAP2000 text export {Path.GetFileName(export)} · written {written} · {tables.Units}"), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return new(null, $"the text export cannot be used: {ex.Message}");
        }
    }, token);

    /// <summary>
    /// Takes off the model that is open in the connected SAP2000 as it is at this moment — saved or not — from its
    /// database tables, read through <paramref name="readTables"/> (the connected gateway). Nothing is kept: every
    /// call reads SAP2000 again. Read-only; the model, its units and its lock state are not touched.
    /// </summary>
    public async Task<QuantityLoadResult> ReadConnectedAsync(QuantityCandidate connected, Func<IReadOnlyList<string>, Task<SapTableSet>> readTables,
        CancellationToken token)
    {
        var tables = await readTables(IsolatedSapTableReader.TablePrefixes);
        token.ThrowIfCancellationRequested();
        if (QuantityTakeoffCalculator.MissingInputs(tables) is { Count: > 0 } missing)
            throw new InvalidDataException($"SAP2000 did not return {string.Join(", ", missing)} for the connected model.");
        var takeoff = await Task.Run(() => QuantityTakeoffCalculator.Calculate(tables), token);
        var name = string.IsNullOrWhiteSpace(connected.ModelPath) ? "unsaved model" : Path.GetFileName(connected.ModelPath);
        return new(connected, takeoff, QuantitySource.ConnectedSap,
            $"{name} · read live from the connected SAP2000 at {DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} · {tables.Units}");
    }

    /// <summary>Reads the candidate's model through the isolated SAP2000 reader. The result is kept for as long as
    /// the model file stays as it is.</summary>
    public async Task<QuantityLoadResult> ReadWithSapAsync(QuantityCandidate candidate, CancellationToken token, bool reload = false)
    {
        if (sapReader is null) throw new InvalidOperationException("No isolated SAP2000 reader is available in this session.");
        if (!File.Exists(candidate.ModelPath)) throw new FileNotFoundException($"The model of {candidate.Id} is not on disk.", candidate.ModelPath);
        var model = new FileInfo(candidate.ModelPath);
        var key = $"{model.FullName}|{model.Length}|{model.LastWriteTimeUtc.Ticks}";
        if (!reload && sapReads.TryGetValue(key, out var kept)) return kept;
        var tables = await sapReader.ReadAsync(candidate.ModelPath, token);
        token.ThrowIfCancellationRequested();
        if (QuantityTakeoffCalculator.MissingInputs(tables) is { Count: > 0 } missing)
            throw new InvalidDataException($"SAP2000 did not return {string.Join(", ", missing)} for {model.Name}.");
        var result = new QuantityLoadResult(candidate, await Task.Run(() => QuantityTakeoffCalculator.Calculate(tables), token), QuantitySource.IsolatedSapRead,
            $"Read from a private copy of {model.Name} in a separate SAP2000 instance · {tables.Units}");
        sapReads[key] = result;
        return result;
    }

    // SAP2000 writes the export when the model is saved. A model written later than its export has changed since —
    // unless a study run did it: the run saves the child, then the analysis writes the model again.
    private static string? ExportIsOutdated(QuantityCandidate candidate)
    {
        if (!File.Exists(candidate.ModelPath)) return null;                 // nothing newer to compare with
        var export = new DateTimeOffset(File.GetLastWriteTimeUtc(candidate.TextExportPath), TimeSpan.Zero);
        var model = new DateTimeOffset(File.GetLastWriteTimeUtc(candidate.ModelPath), TimeSpan.Zero);
        if (model <= export + WriteSlack) return null;
        var explained = candidate.RunWindows.Count == 0
            ? model - export <= UnrecordedAnalysisAllowance
            : candidate.RunWindows.Any(run => export >= run.StartedAt - WriteSlack && (run.CompletedAt is not { } completed || model <= completed + RunSlack));
        return explained ? null
            : $"the model was written {Since(model - export)} after its text export and no study run accounts for that, so the export may not show the model as it is now";
    }

    private static string Since(TimeSpan gap) => gap.TotalHours >= 48 ? $"{gap.TotalDays:0} days" : gap.TotalMinutes >= 120 ? $"{gap.TotalHours:0} h" : $"{Math.Max(1, gap.TotalMinutes):0} min";
}

/// <summary>
/// The isolated read-only SAP2000 workflow. The candidate's <c>.sdb</c> is copied into a private folder; a SAP2000
/// instance of its own opens that copy, its database tables are read, and the instance is closed without saving.
/// The candidate's files are never opened by SAP2000, so they cannot be changed, and the SAP2000 the user is
/// connected to — its model, units and lock state — is never involved. One read at a time.
/// </summary>
public sealed class IsolatedSapTableReader(ISapInstanceHostFactory hosts, string? workRoot = null) : ISapModelTableReader
{
    /// <summary>Every table whose name starts with one of these is read; nothing else.</summary>
    public static readonly IReadOnlyList<string> TablePrefixes =
    [
        "JOINT COORDINATES", "CONNECTIVITY - FRAME", "CONNECTIVITY - CABLE", "FRAME SECTION ASSIGNMENTS", "FRAME SECTION PROPERTIES",
        "CABLE SECTION ASSIGNMENTS", "CABLE SECTION DEFINITIONS", "MATERIAL PROPERTIES 01 - GENERAL",
        "MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", "SECTION DESIGNER PROPERTIES",
    ];
    private const int InstanceId = 90;                                      // study workers are 1–3
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private readonly string workRoot = workRoot ?? Path.Combine(AppDataPaths.RootDirectory, "QuantityRead");

    public async Task<SapTableSet> ReadAsync(string modelPath, CancellationToken token)
    {
        var source = Path.GetFullPath(modelPath);
        await OneAtATime.WaitAsync(token);
        var folder = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        try
        {
            RemoveLeftovers();
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Path.GetFileName(source));
            await Task.Run(() => File.Copy(source, copy), token);
            // The copy is private to this read; clearing a read-only flag it may have inherited touches nothing else.
            File.SetAttributes(copy, FileAttributes.Normal);
            token.ThrowIfCancellationRequested();
            await using var host = hosts.Create(InstanceId);
            var opened = await host.StartAsync(copy, token);
            token.ThrowIfCancellationRequested();
            return await host.Gateway.ReadDatabaseTablesAsync(TablePrefixes, opened);
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* removed by a later read */ }
            OneAtATime.Release();
        }
    }

    // A SAP2000 that held on to its files can leave a private copy behind. A copy a day old belongs to no read.
    private void RemoveLeftovers()
    {
        try
        {
            if (!Directory.Exists(workRoot)) return;
            foreach (var old in Directory.GetDirectories(workRoot).Where(old => Directory.GetCreationTimeUtc(old) < DateTime.UtcNow.AddDays(-1)))
                try { Directory.Delete(old, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
