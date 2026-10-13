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

    /// <summary>The least-squares solution of a·x = b of smallest length, by a one-sided Jacobi singular value
    /// decomposition (Hestenes). For small dense systems that may be rank deficient: a singular value below
    /// max(m, n)·ε·σmax counts as zero, so a direction the system does not determine gets no correction.
    /// <paramref name="relativeCutoff"/> raises that threshold to a fraction of σmax, for a matrix whose entries are
    /// themselves only known to that accuracy (finite differences).</summary>
    public static double[] MinimumNormSolve(double[,] a, double[] b, double relativeCutoff = 0)
    {
        int m = a.GetLength(0), n = a.GetLength(1);
        var u = (double[,])a.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 60; sweep++)
        {
            double largest = 0;
            for (int p = 0; p < n - 1; p++)
            for (int q = p + 1; q < n; q++)
            {
                double alpha = 0, beta = 0, gamma = 0;
                for (int i = 0; i < m; i++) { alpha += u[i, p] * u[i, p]; beta += u[i, q] * u[i, q]; gamma += u[i, p] * u[i, q]; }
                if (gamma == 0) continue;
                largest = Math.Max(largest, Math.Abs(gamma) / Math.Sqrt(alpha * beta));
                double zeta = (beta - alpha) / (2 * gamma);
                double t = zeta == 0 ? 1 : Math.Sign(zeta) / (Math.Abs(zeta) + Math.Sqrt(1 + zeta * zeta));
                double c = 1 / Math.Sqrt(1 + t * t), s = c * t;
                for (int i = 0; i < m; i++) { double up = u[i, p], uq = u[i, q]; u[i, p] = c * up - s * uq; u[i, q] = s * up + c * uq; }
                for (int i = 0; i < n; i++) { double vp = v[i, p], vq = v[i, q]; v[i, p] = c * vp - s * vq; v[i, q] = s * vp + c * vq; }
            }
            if (largest <= 1e-15) break;
        }
        // a·v = u, whose columns are now orthogonal: their lengths are the singular values.
        var squared = new double[n];
        double maximum = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < m; i++) squared[j] += u[i, j] * u[i, j];
            maximum = Math.Max(maximum, Math.Sqrt(squared[j]));
        }
        double cutoff = maximum * Math.Max(Math.Max(m, n) * 2.220446049250313e-16, relativeCutoff);
        var x = new double[n];
        for (int j = 0; j < n; j++)
        {
            if (!(Math.Sqrt(squared[j]) > cutoff)) continue;
            double projection = 0;
            for (int i = 0; i < m; i++) projection += u[i, j] * b[i];
            projection /= squared[j];
            for (int i = 0; i < n; i++) x[i] += v[i, j] * projection;
        }
        return x;
    }

    /// <summary>Infinity-norm backward error in the diagonally equilibrated system; not a condition estimate.</summary>
    public static double ScaledResidual(double[,] a, double[] x, double[] b)
    {
        double worst = 0;
        foreach (double value in ScaledResiduals(a, x, b)) worst = Math.Max(worst, value);
        return worst;
    }

    /// <summary>The backward error of <see cref="ScaledResidual"/> equation by equation; that one is the largest of these.</summary>
    public static double[] ScaledResiduals(double[,] a, double[] x, double[] b)
    {
        double matrixNorm = 0, solutionNorm = 0, loadNorm = 0;
        var residuals = new double[x.Length];
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
            residuals[i] = Math.Abs(residual / si);
        }
        double denominator = matrixNorm * solutionNorm + loadNorm;
        if (denominator != 0)
            for (int i = 0; i < residuals.Length; i++) residuals[i] /= denominator;
        return residuals;
    }
}
