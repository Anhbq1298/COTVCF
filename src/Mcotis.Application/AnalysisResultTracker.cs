using Mcotis.Core;

namespace Mcotis.Application;

public enum ResultState { NotRun, Running, Current, Stale, Failed }

/// <summary>The input of one analysis run: the document as it was when the run started. Edits made while it runs
/// produce other documents and do not reach it.</summary>
public sealed record AnalysisRun(ModelDocument Input, long Revision)
{
    public ObjectId DocumentId => Input.DocumentId;
    public string InputHash => Input.AnalysisInputHash;
}

/// <summary>Whether the results on hand still belong to the model. Results are tied to the hash of the analysis input
/// they were computed from: a rename leaves them current, a change of geometry, loads, stiffness or restraints makes
/// them stale, and undoing that change makes them current again.</summary>
public sealed class AnalysisResultTracker
{
    private readonly DocumentSession session;
    private AnalysisRun? running;
    private AnalysisRun? last;
    private bool lastFailed;
    private ResultState reported;

    public AnalysisResultTracker(DocumentSession session)
    {
        this.session = session;
        session.Changed += (_, change) =>
        {
            // Results of another document say nothing about this one.
            if (change.Reason == DocumentChangeReason.Replaced && change.Before.DocumentId != change.After.DocumentId) last = null;
            Report();
        };
    }

    public event EventHandler? StateChanged;

    public ResultState State =>
        running is not null ? ResultState.Running
        : last is null ? ResultState.NotRun
        : last.DocumentId == session.Document.DocumentId && last.InputHash == session.Document.AnalysisInputHash
            ? lastFailed ? ResultState.Failed : ResultState.Current
            : lastFailed ? ResultState.NotRun : ResultState.Stale;

    /// <summary>The run the results on hand came from: its input, its hash and the document revision it started at.</summary>
    public AnalysisRun? ResultSource => lastFailed ? null : last;

    public AnalysisRun Begin()
    {
        if (running is not null) throw new InvalidOperationException("An analysis is already running.");
        running = new(session.Document, session.Revision);
        Report();
        return running;
    }

    public void Complete(AnalysisRun run) => End(run, failed: false);

    public void Fail(AnalysisRun run) => End(run, failed: true);

    /// <summary>The run was cancelled: whatever results were there before stay as they were.</summary>
    public void Cancel(AnalysisRun run)
    {
        if (!ReferenceEquals(run, running)) throw new InvalidOperationException("This is not the analysis that is running.");
        running = null;
        Report();
    }

    private void End(AnalysisRun run, bool failed)
    {
        if (!ReferenceEquals(run, running)) throw new InvalidOperationException("This is not the analysis that is running.");
        running = null;
        last = run;
        lastFailed = failed;
        Report();
    }

    private void Report()
    {
        var state = State;
        if (state == reported) return;
        reported = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
