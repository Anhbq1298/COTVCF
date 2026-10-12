using Mcotis.Core;

namespace Mcotis.Analysis;

/// <summary>Prismatic, small-displacement Euler–Bernoulli frame. ALG-01/02/03/05/LOAD-01:
/// O'Hara & Ramming (2015) eqs 4.33–4.38, §5.3; consistent loads use Hermite virtual work.
/// Exact conventions and inspected source passages are recorded in docs/analysis/spatial-static-verification.md.</summary>
internal sealed class SpatialFrameElement
{
    public FrameObject Frame { get; }
    public Xyz Axis1 { get; }
    public Xyz Axis2 { get; }
    public Xyz Axis3 { get; }
    public double Length { get; }
    public double[,] OriginalStiffness { get; }
    public double[,] Stiffness { get; }
    public double[] OriginalLoad { get; } = new double[12];
    public double[] Load { get; private set; } = new double[12];
    private readonly int[] released;
    private readonly int[] retained;
    private readonly ScaledCholesky? releaseFactor;

    public SpatialFrameElement(ModelDocument document, FrameObject frame)
    {
        Frame = frame;
        var assignment = document.AssignmentOfFrame(frame.Id);
        var section = document.FrameSections[assignment.SectionId!.Value];
        var material = document.Materials[section.MaterialId];
        var delta = document.Points[frame.EndPointId].Position - document.Points[frame.StartPointId].Position;
        Length = delta.Length;
        Axis1 = delta * (1 / Length);
        // Native axis convention: project global Z as local 2; use global X for near-vertical members.
        Xyz reference = Math.Abs(Axis1.Z) < 0.99 ? new(0, 0, 1) : new(1, 0, 0);
        var y = reference - Axis1 * reference.Dot(Axis1);
        y *= 1 / y.Length;
        var z = Axis1.Cross(y);
        double angle = assignment.LocalAxisAngle * Math.PI / 180;
        Axis2 = y * Math.Cos(angle) + z * Math.Sin(angle);
        Axis3 = z * Math.Cos(angle) - y * Math.Sin(angle);
        var p = section.Properties;
        var m = assignment.Modifiers;
        OriginalStiffness = LocalStiffness(Length, material.ElasticModulus * p.Area * m.Area,
            material.ElasticModulus * p.Inertia22 * m.Inertia22, material.ElasticModulus * p.Inertia33 * m.Inertia33,
            material.ElasticModulus / (2 * (1 + material.PoissonRatio)) * p.TorsionConstant * m.Torsion);
        released = new[] { (4, assignment.Releases.Start.Moment2), (5, assignment.Releases.Start.Moment3),
            (10, assignment.Releases.End.Moment2), (11, assignment.Releases.End.Moment3) }
            .Where(item => item.Item2).Select(item => item.Item1).ToArray();
        retained = Enumerable.Range(0, 12).Except(released).ToArray();
        Stiffness = (double[,])OriginalStiffness.Clone();
        if (released.Length == 0) return;
        var rr = new double[released.Length, released.Length];
        for (int i = 0; i < released.Length; i++)
        for (int j = 0; j < released.Length; j++) rr[i, j] = OriginalStiffness[released[i], released[j]];
        releaseFactor = new(rr, 1e-14);
        foreach (var col in retained)
        {
            var rhs = released.Select(row => OriginalStiffness[row, col]).ToArray();
            var response = releaseFactor.Solve(rhs);
            foreach (var row in retained)
                for (int r = 0; r < released.Length; r++) Stiffness[row, col] -= OriginalStiffness[row, released[r]] * response[r];
        }
        foreach (var r in released)
            for (int i = 0; i < 12; i++) Stiffness[r, i] = Stiffness[i, r] = 0;
    }

    internal static double[,] LocalStiffness(double length, double ea, double ei2, double ei3, double gj)
    {
        double[] values = [length, ea, ei2, ei3, gj];
        if (values.Any(value => !double.IsFinite(value)) || length <= Tolerances.Length || ea <= 0 || ei2 < 0 || ei3 < 0 || gj < 0)
            throw new ArgumentException("Element length and stiffness must be finite with positive length/EA and nonnegative EI/GJ.");
        var k = new double[12, 12];
        void Pair(int i, int j, double value) { k[i, i] = k[j, j] = value; k[i, j] = k[j, i] = -value; }
        Pair(0, 6, ea / length);
        Pair(3, 9, gj / length);
        void Bend(int[] indices, double ei, double rotationSign)
        {
            double a = 12 * ei / Math.Pow(length, 3), b = 6 * ei / (length * length), c = 4 * ei / length, d = 2 * ei / length;
            double[,] block = { { a, b, -a, b }, { b, c, -b, d }, { -a, -b, a, -b }, { b, d, -b, c } };
            double[] signs = [1, rotationSign, 1, rotationSign];
            for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) k[indices[i], indices[j]] = block[i, j] * signs[i] * signs[j];
        }
        Bend([1, 5, 7, 11], ei3, 1);
        Bend([2, 4, 8, 10], ei2, -1);
        if (k.Cast<double>().Any(value => !double.IsFinite(value))) throw new ArgumentException("Element stiffness overflowed.");
        return k;
    }

    public Xyz ToLocal(Xyz global) => new(Axis1.Dot(global), Axis2.Dot(global), Axis3.Dot(global));
    public Xyz ToGlobal(Xyz local) => Axis1 * local.X + Axis2 * local.Y + Axis3 * local.Z;

    public double[] ToLocal(double[] global)
    {
        var local = new double[12];
        for (int i = 0; i < 12; i += 3)
        {
            var v = ToLocal(new Xyz(global[i], global[i + 1], global[i + 2]));
            local[i] = v.X; local[i + 1] = v.Y; local[i + 2] = v.Z;
        }
        return local;
    }

    public double[] ToGlobal(double[] local)
    {
        var global = new double[12];
        for (int i = 0; i < 12; i += 3)
        {
            var v = ToGlobal(new Xyz(local[i], local[i + 1], local[i + 2]));
            global[i] = v.X; global[i + 1] = v.Y; global[i + 2] = v.Z;
        }
        return global;
    }

    public double[,] GlobalStiffness()
    {
        var result = new double[12, 12];
        for (int col = 0; col < 12; col++)
        {
            var unit = new double[12]; unit[col] = 1;
            var forces = ToGlobal(DenseAlgebra.Multiply(Stiffness, ToLocal(unit)));
            for (int row = 0; row < 12; row++) result[row, col] = forces[row];
        }
        return result;
    }

    public Xyz LoadVector(LoadDirection direction, double value) => direction switch
    {
        LoadDirection.GlobalX => ToLocal(new Xyz(value, 0, 0)), LoadDirection.GlobalY => ToLocal(new Xyz(0, value, 0)),
        LoadDirection.GlobalZ => ToLocal(new Xyz(0, 0, value)), LoadDirection.Gravity => ToLocal(new Xyz(0, 0, -value)),
        LoadDirection.Local1 => new(value, 0, 0), LoadDirection.Local2 => new(0, value, 0), LoadDirection.Local3 => new(0, 0, value),
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    public void AddPointLoad(double relative, Xyz localForce)
    {
        double t = relative, n1 = 1 - 3 * t * t + 2 * t * t * t, n2 = 3 * t * t - 2 * t * t * t;
        double h1 = Length * (t - 2 * t * t + t * t * t), h2 = Length * (-t * t + t * t * t);
        OriginalLoad[0] += (1 - t) * localForce.X; OriginalLoad[6] += t * localForce.X;
        OriginalLoad[1] += n1 * localForce.Y; OriginalLoad[5] += h1 * localForce.Y;
        OriginalLoad[7] += n2 * localForce.Y; OriginalLoad[11] += h2 * localForce.Y;
        OriginalLoad[2] += n1 * localForce.Z; OriginalLoad[4] -= h1 * localForce.Z;
        OriginalLoad[8] += n2 * localForce.Z; OriginalLoad[10] -= h2 * localForce.Z;
    }

    /// <summary>3-point Gauss integrates Hermite cubic × linear intensity exactly on the loaded interval.</summary>
    public void AddDistributedLoad(double start, double end, LoadDirection direction, double startValue, double endValue)
    {
        double g = Math.Sqrt(3.0 / 5), midpoint = (start + end) / 2, half = (end - start) / 2;
        foreach (var (point, weight) in new[] { (-g, 5.0 / 9), (0.0, 8.0 / 9), (g, 5.0 / 9) })
        {
            double relative = midpoint + half * point;
            double intensity = startValue + (endValue - startValue) * (relative - start) / (end - start);
            AddPointLoad(relative, LoadVector(direction, intensity * Length * half * weight));
        }
    }

    public void FinalizeLoad()
    {
        Load = (double[])OriginalLoad.Clone();
        if (releaseFactor is null) return;
        var eliminated = releaseFactor.Solve(released.Select(i => OriginalLoad[i]).ToArray());
        foreach (var row in retained)
            for (int r = 0; r < released.Length; r++) Load[row] -= OriginalStiffness[row, released[r]] * eliminated[r];
        foreach (var r in released) Load[r] = 0;
    }

    public (double[] Displacements, double[] Forces) Recover(double[] globalDisplacement)
    {
        var local = ToLocal(globalDisplacement);
        if (releaseFactor is not null)
        {
            var rhs = released.Select(row => OriginalLoad[row] - retained.Sum(col => OriginalStiffness[row, col] * local[col])).ToArray();
            var rotations = releaseFactor.Solve(rhs);
            for (int r = 0; r < released.Length; r++) local[released[r]] = rotations[r];
        }
        var forces = DenseAlgebra.Multiply(OriginalStiffness, local);
        for (int i = 0; i < 12; i++) forces[i] -= OriginalLoad[i];
        return (local, forces);
    }
}
