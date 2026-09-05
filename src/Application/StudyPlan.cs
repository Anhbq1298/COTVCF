using System.Numerics;
using COT_VCF.Domain;

namespace COT_VCF.Application;

/// <summary>One planned iteration: which loading case, truss-height candidate and pretension combination it uses.</summary>
public readonly record struct StudyRun(long Index, int LoadIndex, int HeightIndex, long PretensionIndex);

/// <summary>
/// Tab 05 study space maths. Three modes isolate design variables before the full interaction:
/// Fix Pretension → N_L·N_H, Fix Height → N_L·N_P, Combined → N_L·N_H·N_P. Counting only — nothing
/// runs here; the lazy <see cref="Enumerate"/> is the seam for a future iteration engine.
/// </summary>
public static class StudyPlan
{
    public static BigInteger TotalRuns(IterationStudyMode mode, int loadCount, int heightCount, BigInteger pretensionCount)
    {
        if (loadCount <= 0 || heightCount <= 0 || pretensionCount <= 0) return BigInteger.Zero;
        return mode switch
        {
            IterationStudyMode.FixPretension => (BigInteger)loadCount * heightCount,
            IterationStudyMode.FixHeight => (BigInteger)loadCount * pretensionCount,
            _ => (BigInteger)loadCount * heightCount * pretensionCount,
        };
    }

    public static IEnumerable<StudyRun> Enumerate(IterationStudyMode mode, int loadCount, int heightCount, long pretensionCount, int fixedHeightIndex, long fixedPretensionIndex)
    {
        if (loadCount <= 0 || heightCount <= 0 || pretensionCount <= 0) yield break;
        var fixedH = Math.Clamp(fixedHeightIndex, 0, heightCount - 1);
        var fixedP = Math.Clamp(fixedPretensionIndex, 0, pretensionCount - 1);
        long index = 0;
        for (int l = 0; l < loadCount; l++)
        {
            if (mode == IterationStudyMode.FixHeight)
            {
                for (long p = 0; p < pretensionCount; p++) yield return new(index++, l, fixedH, p);
            }
            else
            {
                for (int h = 0; h < heightCount; h++)
                {
                    if (mode == IterationStudyMode.FixPretension) yield return new(index++, l, h, fixedP);
                    else for (long p = 0; p < pretensionCount; p++) yield return new(index++, l, h, p);
                }
            }
        }
    }
}
