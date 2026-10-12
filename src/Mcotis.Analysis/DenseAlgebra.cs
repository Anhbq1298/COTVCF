namespace Mcotis.Analysis;

/// <summary>Small, dense verification solver. No stabilization, pseudo-inverse or diagonal regularization.</summary>
internal sealed class ScaledCholesky
{
    private readonly double[,] lower;
    private readonly double[] scale;
    public double MinimumPivot { get; private set; } = 1;

    public ScaledCholesky(double[,] matrix, double pivotTolerance, CancellationToken cancellationToken = default)
    {
        int n = matrix.GetLength(0);
        lower = new double[n, n];
        scale = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (!(matrix[i, i] > 0) || !double.IsFinite(matrix[i, i])) throw new FactorizationFailure(i, matrix[i, i]);
            scale[i] = Math.Sqrt(matrix[i, i]);
        }
        for (int i = 0; i < n; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int j = 0; j <= i; j++)
            {
                double value = matrix[i, j] / scale[i] / scale[j];
                for (int k = 0; k < j; k++) value -= lower[i, k] * lower[j, k];
                if (i == j)
                {
                    if (!double.IsFinite(value) || value <= pivotTolerance) throw new FactorizationFailure(i, value);
                    MinimumPivot = Math.Min(MinimumPivot, value);
                    lower[i, i] = Math.Sqrt(value);
                }
                else lower[i, j] = value / lower[j, j];
            }
        }
    }

    public double[] Solve(double[] rhs)
    {
        int n = scale.Length;
        var x = new double[n];
        for (int i = 0; i < n; i++)
        {
            double value = rhs[i] / scale[i];
            for (int j = 0; j < i; j++) value -= lower[i, j] * x[j];
            x[i] = value / lower[i, i];
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double value = x[i];
            for (int j = i + 1; j < n; j++) value -= lower[j, i] * x[j];
            x[i] = value / lower[i, i];
        }
        for (int i = 0; i < n; i++) x[i] /= scale[i];
        return x;
    }
}

internal sealed class FactorizationFailure(int index, double pivot) : Exception("Nonpositive or too small stiffness pivot")
{
    public int Index { get; } = index;
    public double Pivot { get; } = pivot;
}

internal static class DenseAlgebra
{
    public static double[] Multiply(double[,] a, double[] x)
    {
        var result = new double[a.GetLength(0)];
        for (int i = 0; i < result.Length; i++)
        for (int j = 0; j < x.Length; j++) result[i] += a[i, j] * x[j];
        return result;
    }

    /// <summary>Infinity-norm backward error in the diagonally equilibrated system; not a condition estimate.</summary>
    public static double ScaledResidual(double[,] a, double[] x, double[] b)
    {
        double matrixNorm = 0, solutionNorm = 0, loadNorm = 0, residualNorm = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double rowNorm = 0, residual = -b[i];
            double si = Math.Sqrt(a[i, i]);
            for (int j = 0; j < x.Length; j++)
            {
                rowNorm += Math.Abs(a[i, j] / si / Math.Sqrt(a[j, j]));
                residual += a[i, j] * x[j];
            }
            matrixNorm = Math.Max(matrixNorm, rowNorm);
            solutionNorm = Math.Max(solutionNorm, Math.Abs(x[i] * si));
            loadNorm = Math.Max(loadNorm, Math.Abs(b[i] / si));
            residualNorm = Math.Max(residualNorm, Math.Abs(residual / si));
        }
        double denominator = matrixNorm * solutionNorm + loadNorm;
        return denominator == 0 ? residualNorm : residualNorm / denominator;
    }
}
