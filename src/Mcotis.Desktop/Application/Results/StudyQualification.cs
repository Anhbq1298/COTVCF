using System.Globalization;
using COT_VCF.Domain;
using COT_VCF.Domain.Results;

namespace COT_VCF.Application.Results;

// Tab 06A: which candidates of a study are "qualified". Pure functions over values that were already extracted —
// no SAP, no I/O. A candidate qualifies when it passes every active criterion; a result a criterion needs but
// cannot read makes the candidate "not assessed", never qualified and never a zero.

/// <summary>The acceptance criteria. Post-processing only: never part of a run or of its fingerprint.</summary>
/// <param name="DeflectionBand">Window on the peak downward deflection δ [mm] — the Tab 04 limit band.</param>
/// <param name="MaxCableUtilization">Largest allowed final force / design capacity of any cable (1.00 = 100 %).</param>
public sealed record QualificationCriteria(
    bool DeflectionEnabled,
    DeflectionLimitBand DeflectionBand,
    bool CableEnabled,
    double MaxCableUtilization)
{
    public static readonly QualificationCriteria Default = new(true, new DeflectionLimitBand(null, null), true, 1.0);

    /// <summary>The criteria as currently set on Tab 06A. The deflection range is the Tab 04 limit band itself.</summary>
    public static QualificationCriteria From(OutputSettingsConfiguration output) =>
        new(output.DeflectionCriterionEnabled, output.DeflectionLimitBand, output.CableCriterionEnabled, output.MaxCableUtilization);

    /// <summary>The deflection criterion applies only once a bound is entered.</summary>
    public bool DeflectionActive => DeflectionEnabled && DeflectionBand.IsActive;
    public bool CableActive => CableEnabled;
    public bool AnyActive => DeflectionActive || CableActive;

    /// <summary>First problem with the inputs, or null when they are usable.</summary>
    public string? Problem() =>
        DeflectionBand.Problem() is { } band ? band :
        CableEnabled && (!double.IsFinite(MaxCableUtilization) || MaxCableUtilization <= 0)
            ? "Cable utilisation limit must be a finite value greater than 0." : null;

    public string DeflectionLabel => DeflectionActive ? $"Deflection δ {DeflectionBand.Label}" : "Deflection: no limit";
    public string CableLabel => CableActive ? $"Cable utilisation ≤ {Ratio(MaxCableUtilization)}" : "Cable force: no limit";
    public string Label => $"{DeflectionLabel} · {CableLabel}";

    public static string Ratio(double value) => value.ToString("0.00##", CultureInfo.InvariantCulture);
}

/// <summary>Final analysed force of one cable in one candidate; null = result unavailable.</summary>
public sealed record CandidateCableForce(string CableName, double? ForceKN);

/// <summary>What the criteria read from one candidate — derived once from the raw results and kept, so a
/// criterion can change without the raw data being read again.</summary>
/// <param name="ExtractionProblem">Set when the analysis or extraction of the candidate did not complete.</param>
/// <param name="PeakDownMm">δ = −min(Uz) over every monitored point [mm, + = sag]; null when it cannot be
/// stated (see <paramref name="DeflectionProblem"/>).</param>
public sealed record CandidateMetrics(
    string IterationKey,
    long RunIndex,
    string LoadId,
    string HeightId,
    string PretensionId,
    string LoadingLabel,
    double TrussHeightM,
    string PretensionLabel,
    string? ExtractionProblem,
    double? PeakDownMm,
    string? DeflectionProblem,
    IReadOnlyList<CandidateCableForce> Cables)
{
    public static CandidateMetrics From(CandidateResultDto candidate)
    {
        var p = candidate.Parameters;
        var extraction = candidate.ExtractionStatus is CandidateExtractionStatus.AnalysisFailed
            or CandidateExtractionStatus.ExtractionFailed or CandidateExtractionStatus.NotStarted
            ? $"Extraction status is {candidate.ExtractionStatus}." : null;

        // One invalid monitored point leaves the peak unknown — it may be the governing one.
        var points = candidate.Groups.SelectMany(group => group.Points).ToArray();
        var invalid = points.FirstOrDefault(point => point.Status != ResultValueStatus.Valid || point.UzMm is not { } uz || !double.IsFinite(uz));
        var deflectionProblem = points.Length == 0 ? "No monitored Uz results."
            : invalid is not null ? $"Uz unavailable at {invalid.PointName} ({invalid.Status})." : null;
        var peak = deflectionProblem is null ? DeflectionLimitBand.PeakDownMm(points.Select(point => point.UzMm!.Value)) : null;

        var cables = (candidate.CableForces ?? [])
            .Select(row => new CandidateCableForce(row.CableName,
                row.Status == ResultValueStatus.Valid && row.ForceKN is { } force && double.IsFinite(force) ? force : null))
            .ToArray();
        return new(candidate.IterationKey, p.RunIndex, p.LoadingCandidateId, p.HeightCandidateId, p.PretensionCandidateId,
            p.LoadingLabel, p.TrussHeightM, p.PretensionLabel, extraction, peak, deflectionProblem, cables);
    }
}

public enum QualificationStatus { Qualified, NotQualified, NotAssessed }

public enum CriterionVerdict { Inactive, Pass, Fail, NotAssessed }

/// <summary>One candidate against the criteria. The governing cable utilisation (force / capacity, over the
/// cables that have both) is reported whether or not the cable criterion is active.</summary>
public sealed record CandidateQualification(
    CandidateMetrics Metrics,
    QualificationStatus Status,
    CriterionVerdict Deflection,
    CriterionVerdict Cable,
    double? GoverningUtilisation,
    string? GoverningCable,
    double? GoverningForceKN,
    double? GoverningCapacityKN,
    string Reason)
{
    public string StatusText => Status switch
    {
        QualificationStatus.Qualified => "Qualified",
        QualificationStatus.NotQualified => "Not qualified",
        _ => "Not assessed",
    };
}

/// <summary>Counts over the study, and the range the study itself spans in each criterion's quantity.</summary>
public sealed record QualificationSummary(
    int Total,
    int Qualified,
    int FailedDeflectionOnly,
    int FailedCableOnly,
    int FailedBoth,
    int NotAssessed,
    double? MinPeakDownMm,
    double? MaxPeakDownMm,
    double? MinUtilisation,
    double? MaxUtilisation,
    IReadOnlyList<string> CablesWithCapacity,
    IReadOnlyList<string> CablesWithoutCapacity)
{
    public int NotQualified => FailedDeflectionOnly + FailedCableOnly + FailedBoth;

    public string Headline => $"Qualified {Qualified:N0} / {Total:N0}";

    public string Breakdown =>
        $"failed deflection only {FailedDeflectionOnly:N0} · failed cable only {FailedCableOnly:N0} · failed both {FailedBoth:N0} · not assessed {NotAssessed:N0}";
}

public sealed record StudyQualification(
    QualificationCriteria Criteria,
    IReadOnlyList<CandidateQualification> Candidates,
    QualificationSummary Summary)
{
    public IEnumerable<CandidateQualification> Qualified => Candidates.Where(candidate => candidate.Status == QualificationStatus.Qualified);
}

public static class StudyQualifier
{
    /// <param name="capacities">Design capacity [kN] per cable name — the resolved set the Cable Forces report
    /// uses (current Tab 03, then stored). A cable without one cannot be assessed.</param>
    public static StudyQualification Evaluate(
        IReadOnlyList<CandidateMetrics> candidates,
        QualificationCriteria criteria,
        IReadOnlyDictionary<string, double>? capacities)
    {
        if (criteria.Problem() is { } problem) throw new InvalidOperationException(problem);
        capacities ??= new Dictionary<string, double>();
        var rows = candidates.Select(candidate => EvaluateOne(candidate, criteria, capacities)).ToArray();

        var peaks = candidates.Where(c => c.PeakDownMm.HasValue).Select(c => c.PeakDownMm!.Value).ToArray();
        var utilisations = rows.Where(r => r.GoverningUtilisation.HasValue).Select(r => r.GoverningUtilisation!.Value).ToArray();
        var cables = candidates.SelectMany(c => c.Cables).Select(c => c.CableName).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, NaturalNameComparer.Instance).ToArray();
        bool Failed(CandidateQualification row, bool deflection, bool cable) =>
            (row.Deflection == CriterionVerdict.Fail) == deflection && (row.Cable == CriterionVerdict.Fail) == cable;
        var summary = new QualificationSummary(rows.Length,
            rows.Count(r => r.Status == QualificationStatus.Qualified),
            rows.Count(r => Failed(r, true, false)), rows.Count(r => Failed(r, false, true)), rows.Count(r => Failed(r, true, true)),
            rows.Count(r => r.Status == QualificationStatus.NotAssessed),
            peaks.Length > 0 ? peaks.Min() : null, peaks.Length > 0 ? peaks.Max() : null,
            utilisations.Length > 0 ? utilisations.Min() : null, utilisations.Length > 0 ? utilisations.Max() : null,
            cables.Where(name => HasCapacity(capacities, name)).ToArray(),
            cables.Where(name => !HasCapacity(capacities, name)).ToArray());
        return new(criteria, rows, summary);
    }

    private static bool HasCapacity(IReadOnlyDictionary<string, double> capacities, string cable) =>
        capacities.TryGetValue(cable, out var capacity) && CableCapacityResolver.IsCapacity(capacity);

    private static CandidateQualification EvaluateOne(CandidateMetrics m, QualificationCriteria criteria,
        IReadOnlyDictionary<string, double> capacities)
    {
        var reasons = new List<string>();

        // ---- deflection: Min ≤ δ ≤ Max, bounds inclusive, a blank bound open
        var deflection = CriterionVerdict.Inactive;
        if (criteria.DeflectionActive)
        {
            if (m.PeakDownMm is not { } peak)
            {
                deflection = CriterionVerdict.NotAssessed;
                reasons.Add(m.DeflectionProblem ?? "Deflection unavailable.");
            }
            else if (criteria.DeflectionBand.Contains(peak)) deflection = CriterionVerdict.Pass;
            else
            {
                deflection = CriterionVerdict.Fail;
                reasons.Add(criteria.DeflectionBand.MaxMm is { } hi && peak > hi
                    ? $"δ {Mm(peak)} mm exceeds Max {Mm(hi)} mm"
                    : $"δ {Mm(peak)} mm is below Min {Mm(criteria.DeflectionBand.MinMm ?? 0)} mm");
            }
        }

        // ---- cable: every cable's force / capacity ≤ limit. The governing value is found either way.
        double? governing = null, governingForce = null, governingCapacity = null;
        string? governingCable = null, unassessed = null;
        foreach (var cable in m.Cables)
        {
            if (cable.ForceKN is not { } force) { unassessed ??= $"Cable force unavailable for {cable.CableName}."; continue; }
            if (!HasCapacity(capacities, cable.CableName)) { unassessed ??= $"No design capacity for {cable.CableName}."; continue; }
            var capacity = capacities[cable.CableName];
            var utilisation = force / capacity;
            if (governing is null || utilisation > governing)
                (governing, governingCable, governingForce, governingCapacity) = (utilisation, cable.CableName, force, capacity);
        }
        var cableVerdict = CriterionVerdict.Inactive;
        if (criteria.CableActive)
        {
            if (governing is { } worst && worst > criteria.MaxCableUtilization)
            {
                // a cable above the limit fails the candidate whatever else is missing
                cableVerdict = CriterionVerdict.Fail;
                reasons.Add($"{governingCable} utilisation {Percent(worst)} exceeds {Percent(criteria.MaxCableUtilization)}");
            }
            else if (m.Cables.Count == 0)
            {
                cableVerdict = CriterionVerdict.NotAssessed;
                reasons.Add("No cable-force results.");
            }
            else if (unassessed is not null)
            {
                cableVerdict = CriterionVerdict.NotAssessed;
                reasons.Add(unassessed);
            }
            else cableVerdict = CriterionVerdict.Pass;
        }

        QualificationStatus status;
        if (deflection == CriterionVerdict.Fail || cableVerdict == CriterionVerdict.Fail) status = QualificationStatus.NotQualified;
        else if (m.ExtractionProblem is not null)
        {
            status = QualificationStatus.NotAssessed;
            reasons.Insert(0, m.ExtractionProblem);
        }
        else if (deflection == CriterionVerdict.NotAssessed || cableVerdict == CriterionVerdict.NotAssessed) status = QualificationStatus.NotAssessed;
        else status = QualificationStatus.Qualified;

        var reason = reasons.Count > 0 ? string.Join(" · ", reasons)
            : criteria.AnyActive ? "Passes every active criterion" : "No criterion active";
        return new(m, status, deflection, cableVerdict, governing, governingCable, governingForce, governingCapacity, reason);
    }

    public static string Mm(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    public static string Percent(double ratio) => (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";
}
