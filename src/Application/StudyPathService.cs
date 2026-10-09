using System.IO;
using System.Text.RegularExpressions;

namespace COT_VCF.Application;

/// <summary>Computes deterministic study paths without touching the file system.</summary>
public sealed partial class StudyPathService
{
    public string GetStudyRoot(string baselinePath)
    {
        var baseline = NormalizeBaselinePath(baselinePath);
        var parent = Path.GetDirectoryName(baseline)
            ?? throw new InvalidOperationException("The baseline model must have a parent directory.");
        var modelFolder = SanitizeFileName(Path.GetFileNameWithoutExtension(baseline));
        return Path.Combine(parent, "Study", modelFolder);
    }

    public string GetIterationDirectory(string baselinePath, string iterationId)
    {
        ValidateIterationId(iterationId);
        return Path.Combine(GetStudyRoot(baselinePath), iterationId);
    }

    public string GetIterationModelPath(string baselinePath, string iterationId)
    {
        var baseline = NormalizeBaselinePath(baselinePath);
        ValidateIterationId(iterationId);
        var stem = SanitizeFileName(Path.GetFileNameWithoutExtension(baseline));
        return Path.Combine(GetIterationDirectory(baseline, iterationId), $"{stem}_{iterationId}.sdb");
    }

    /// <summary>True for a study iteration ID of the form <c>Lxx_Hxx_Pxx</c>.</summary>
    public static bool IsIterationId(string? value) => !string.IsNullOrWhiteSpace(value) && IterationIdPattern().IsMatch(value);

    private static string NormalizeBaselinePath(string baselinePath)
    {
        if (string.IsNullOrWhiteSpace(baselinePath))
            throw new InvalidOperationException("Save the baseline SAP2000 model before composing a study.");
        var fullPath = Path.GetFullPath(baselinePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".sdb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The baseline model must be a saved SAP2000 .sdb file.");
        return fullPath;
    }

    private static void ValidateIterationId(string iterationId)
    {
        if (string.IsNullOrWhiteSpace(iterationId) || !IterationIdPattern().IsMatch(iterationId))
            throw new InvalidOperationException($"Invalid study iteration ID: {iterationId}.");
    }

    private static string SanitizeFileName(string stem)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(stem.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "COT_VCF" : clean;
    }

    [GeneratedRegex("^L[0-9]+_H[0-9]+_P[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IterationIdPattern();
}
