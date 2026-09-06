using COT_VCF.Domain;
using System.IO;

namespace COT_VCF.Application.Workers;

public sealed record IterationVerificationResult(
    int GeometryPointsVerified,
    int PointLoadsVerified,
    int CableTargetForcesVerified);

/// <summary>Reads a saved child back through ISapGateway and proves its resolved L/H/P state before Ready.</summary>
public sealed class IterationCandidateVerifier
{
    public async Task<IterationVerificationResult> VerifyAsync(
        ISapGateway gateway,
        StudyExecutionSnapshot snapshot,
        StudyIterationExecutionDto job,
        GeometryModel expectedGeometry,
        ModelInfo child,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        VerifyChildIdentity(snapshot, job, child);

        var modifiedPoints = expectedGeometry.Points.Values
            .Where(point => snapshot.BaselineGeometry.Points.TryGetValue(point.Name, out var baseline) &&
                            !Close(point.Position, baseline.Position))
            .OrderBy(point => point.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (var point in modifiedPoints)
        {
            token.ThrowIfCancellationRequested();
            var actual = await gateway.GetPointPositionAsync(point.Name, child);
            if (!Close(actual, point.Position))
                throw new InvalidOperationException(
                    $"Geometry verification failed for {point.Name}. Expected ({point.Position.X:0.########}, {point.Position.Y:0.########}, {point.Position.Z:0.########}); " +
                    $"actual ({actual.X:0.########}, {actual.Y:0.########}, {actual.Z:0.########}) {child.LengthUnit}.");
        }

        foreach (var pointGroup in job.Loading.Loads.GroupBy(load => load.PointName, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var actual = await gateway.GetPointLoadsAsync(pointGroup.Key, child);
            foreach (var expected in pointGroup)
            {
                var matches = actual.Where(load =>
                    load.LoadPattern.Equals(expected.LoadPattern, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || !Close(matches[0].Fx, expected.Fx) ||
                    !Close(matches[0].Fy, expected.Fy) || !Close(matches[0].Fz, expected.Fz))
                    throw new InvalidOperationException(
                        $"Loading verification failed for {expected.PointName} / {expected.LoadPattern}. " +
                        $"Expected F=({expected.Fx:0.########}, {expected.Fy:0.########}, {expected.Fz:0.########}) {child.ForceUnit}; " +
                        $"found {matches.Length} matching assignment(s)" +
                        (matches.Length == 1
                            ? $" with F=({matches[0].Fx:0.########}, {matches[0].Fy:0.########}, {matches[0].Fz:0.########})."
                            : "."));
            }
        }

        foreach (var cableGroup in job.Pretension.Assignments.GroupBy(assignment => assignment.CableName, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var actual = await gateway.GetCableTargetForcesAsync(cableGroup.Key, child);
            foreach (var expected in cableGroup)
            {
                var matches = actual.Where(assignment =>
                    assignment.CableName.Equals(expected.CableName, StringComparison.OrdinalIgnoreCase) &&
                    assignment.LoadPattern.Equals(expected.LoadPattern, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || !Close(matches[0].TargetForce, expected.TargetForce))
                    throw new InvalidOperationException(
                        $"Pretension verification failed for {expected.CableName} / {expected.LoadPattern}. " +
                        $"Expected target force {expected.TargetForce:0.########} {child.ForceUnit}; found {matches.Length} matching assignment(s)" +
                        (matches.Length == 1 ? $" with target force {matches[0].TargetForce:0.########}." : "."));
            }
        }

        token.ThrowIfCancellationRequested();
        var active = await gateway.GetCurrentModelInfoAsync();
        VerifyChildIdentity(snapshot, job, active);
        return new(modifiedPoints.Length, job.Loading.Loads.Count, job.Pretension.Assignments.Count);
    }

    private static void VerifyChildIdentity(
        StudyExecutionSnapshot snapshot,
        StudyIterationExecutionDto job,
        ModelInfo actual)
    {
        if (!SamePath(actual.Path, job.ModelFullPath))
            throw new InvalidOperationException($"Child verification opened the wrong model. Expected {job.ModelFullPath}; active {actual.Path}.");
        if (actual.UnitsCode != snapshot.BaselineModel.UnitsCode)
            throw new InvalidOperationException(
                $"Child verification found different units than the baseline ({actual.Units} vs {snapshot.BaselineModel.Units}).");
        if (!File.Exists(job.ModelFullPath))
            throw new InvalidOperationException($"The saved child model was not found: {job.ModelFullPath}");
        if (new FileInfo(job.ModelFullPath).Length <= 0)
            throw new InvalidOperationException($"The saved child model is empty: {job.ModelFullPath}");
    }

    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool Close(Position left, Position right) =>
        Close(left.X, right.X) && Close(left.Y, right.Y) && Close(left.Z, right.Z);

    private static bool Close(double left, double right) =>
        Math.Abs(left - right) <= 1e-7 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
}
