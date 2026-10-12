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
    /// <summary>Where this element sits on its frame: 0 and 1 for a whole frame, a part of that for one piece of a
    /// frame split at the points lying on it.</summary>
    public double RelativeStart { get; }
    public double RelativeEnd { get; }
    public double[,] OriginalStiffness { get; }
    public double[,] Stiffness { get; }
    public double[] OriginalLoad { get; } = new double[12];
    public double[] Load { get; private set; } = new double[12];
    private readonly int[] released;
    private readonly int[] retained;
    private readonly ScaledCholesky? releaseFactor;
    private readonly double axialRigidity, bendingRigidity2, bendingRigidity3, torsionalRigidity;
    // The loads as they were given, in local axes: the nodal load vector alone does not say where they act.
    private readonly List<(double At, Xyz Force)> pointLoads = [];
    private readonly List<(double Start, double End, Xyz StartIntensity, Xyz EndIntensity)> lineLoads = [];

    public SpatialFrameElement(ModelDocument document, FrameObject frame)
        : this(document, frame, document.Points[frame.StartPointId].Position, document.Points[frame.EndPointId].Position, 0, 1) { }

    /// <summary>One piece of a frame, between two points on its axis. The piece has the frame's section, modifiers and
    /// local axes; a release of the frame applies only at the frame's own end, never where it was split.</summary>
    public SpatialFrameElement(ModelDocument document, FrameObject frame, Xyz start, Xyz end, double relativeStart, double relativeEnd)
    {
        Frame = frame;
        RelativeStart = relativeStart;
        RelativeEnd = relativeEnd;
        var assignment = document.AssignmentOfFrame(frame.Id);
        var section = document.FrameSections[assignment.SectionId!.Value];
        var material = document.Materials[section.MaterialId];
        var delta = end - start;
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
        axialRigidity = material.ElasticModulus * p.Area * m.Area;
        bendingRigidity2 = material.ElasticModulus * p.Inertia22 * m.Inertia22;
        bendingRigidity3 = material.ElasticModulus * p.Inertia33 * m.Inertia33;
        torsionalRigidity = material.ElasticModulus / (2 * (1 + material.PoissonRatio)) * p.TorsionConstant * m.Torsion;
        OriginalStiffness = LocalStiffness(Length, axialRigidity, bendingRigidity2, bendingRigidity3, torsionalRigidity);
        bool first = relativeStart == 0, last = relativeEnd == 1;
        released = new[] { (4, first && assignment.Releases.Start.Moment2), (5, first && assignment.Releases.Start.Moment3),
            (10, last && assignment.Releases.End.Moment2), (11, last && assignment.Releases.End.Moment3) }
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
        pointLoads.Add((relative, localForce));
        Accumulate(relative, localForce);
    }

    private void Accumulate(double relative, Xyz localForce)
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
        lineLoads.Add((start, end, LoadVector(direction, startValue), LoadVector(direction, endValue)));
        double g = Math.Sqrt(3.0 / 5), midpoint = (start + end) / 2, half = (end - start) / 2;
        foreach (var (point, weight) in new[] { (-g, 5.0 / 9), (0.0, 8.0 / 9), (g, 5.0 / 9) })
        {
            double relative = midpoint + half * point;
            double intensity = startValue + (endValue - startValue) * (relative - start) / (end - start);
            Accumulate(relative, LoadVector(direction, intensity * Length * half * weight));
        }
    }

    private static readonly (double Point, double Weight)[] Gauss4 =
    [
        (-0.8611363115940526, 0.3478548451374538), (-0.3399810435848563, 0.6521451548625461),
        (0.3399810435848563, 0.6521451548625461), (0.8611363115940526, 0.3478548451374538),
    ];

    /// <summary>The actions [F1,F2,F3,M1,M2,M3] at a section, a distance <paramref name="x"/> from the start: what the part
    /// of the element beyond the section does to the part before it, from the equilibrium of that part under the
    /// I-end actions and the loads on it. A point load exactly at the section is beyond it, except at the far end of the
    /// element, where the section is the end itself.</summary>
    private double[] ActionsAt(double x, double[] endActions, bool atEnd)
    {
        Xyz force = new(-endActions[0], -endActions[1], -endActions[2]);
        Xyz moment = new(-endActions[3], -endActions[4] - x * endActions[2], -endActions[5] + x * endActions[1]);
        foreach (var (at, load) in pointLoads)
        {
            double a = at * Length;
            if (!(a < x || (atEnd && a <= x))) continue;
            force -= load;
            moment += new Xyz(0, -load.Z, load.Y) * (x - a);
        }
        foreach (var (start, end, startIntensity, endIntensity) in lineLoads)
        {
            double a = start * Length, b = end * Length, reach = Math.Min(x, b) - a;
            if (reach <= 0) continue;
            var slope = (endIntensity - startIntensity) * (1 / (b - a));
            var total = startIntensity * reach + slope * (reach * reach / 2);                                    // ∫ q ds
            var lever = total * (x - a) - (startIntensity * (reach * reach / 2) + slope * (reach * reach * reach / 3));   // ∫ (x − s) q ds
            force -= total;
            moment += new Xyz(0, -lever.Z, lever.Y);
        }
        return [force.X, force.Y, force.Z, moment.X, moment.Y, moment.Z];
    }

    /// <summary>The element at a relative distance along it: its local displacements [u1,u2,u3,θ1,θ2,θ3] and the actions
    /// [F1,F2,F3,M1,M2,M3] of the part beyond that section on the part before it — at the far end the J-end actions,
    /// at the start the opposite of the I-end actions; F1 is the axial force, tension positive.
    /// <para>The actions are statics. The displacements integrate u1′ = F1/EA, θ′ = (M1/GJ, M2/EI2, M3/EI3),
    /// u2′ = θ3, u3′ = −θ2 from the I end — the same Euler–Bernoulli beam as the stiffness matrix — with 4-point Gauss
    /// between the load discontinuities, which is exact for point and linearly varying loads.</para></summary>
    public (double[] Displacements, double[] Actions) Evaluate(double relative, double[] localDisplacements, double[] endActions)
    {
        double x = relative * Length;
        var stops = new SortedSet<double> { 0, x };
        foreach (var (at, _) in pointLoads) if (at * Length > 0 && at * Length < x) stops.Add(at * Length);
        foreach (var (start, end, _, _) in lineLoads)
        {
            if (start * Length > 0 && start * Length < x) stops.Add(start * Length);
            if (end * Length > 0 && end * Length < x) stops.Add(end * Length);
        }
        var displacement = localDisplacements.Take(6).ToArray();
        displacement[1] += x * localDisplacements[5];
        displacement[2] -= x * localDisplacements[4];
        static double Ratio(double action, double rigidity) => rigidity > 0 ? action / rigidity : 0;
        double previous = 0;
        foreach (double stop in stops.Skip(1))
        {
            double half = (stop - previous) / 2, middle = (stop + previous) / 2;
            foreach (var (point, weight) in Gauss4)
            {
                double s = middle + half * point, w = half * weight;
                var actions = ActionsAt(s, endActions, atEnd: false);
                double curvature2 = Ratio(actions[4], bendingRigidity2), curvature3 = Ratio(actions[5], bendingRigidity3);
                displacement[0] += w * Ratio(actions[0], axialRigidity);
                displacement[1] += w * (x - s) * curvature3;
                displacement[2] -= w * (x - s) * curvature2;
                displacement[3] += w * Ratio(actions[3], torsionalRigidity);
                displacement[4] += w * curvature2;
                displacement[5] += w * curvature3;
            }
            previous = stop;
        }
        return (displacement, ActionsAt(x, endActions, atEnd: relative >= 1));
    }

    /// <summary>The part of a load of the frame that falls on this piece. Positions are relative distances along the
    /// whole frame; a point load at a split point goes to the piece that ends there.</summary>
    public void AddFrameLoad(LoadAssignment load, double factor)
    {
        double span = RelativeEnd - RelativeStart;
        if (load is FramePointLoad point)
        {
            bool mine = RelativeStart == 0 ? point.RelativeDistance <= RelativeEnd
                : point.RelativeDistance > RelativeStart && point.RelativeDistance <= RelativeEnd;
            if (mine) AddPointLoad((point.RelativeDistance - RelativeStart) / span, LoadVector(point.Direction, point.Value * factor));
        }
        else if (load is FrameDistributedLoad line)
        {
            double from = Math.Max(line.RelativeStart, RelativeStart), to = Math.Min(line.RelativeEnd, RelativeEnd);
            if (to <= from) return;
            double Intensity(double relative) => line.ValueStart +
                (line.ValueEnd - line.ValueStart) * (relative - line.RelativeStart) / (line.RelativeEnd - line.RelativeStart);
            AddDistributedLoad((from - RelativeStart) / span, (to - RelativeStart) / span, line.Direction, Intensity(from) * factor, Intensity(to) * factor);
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
