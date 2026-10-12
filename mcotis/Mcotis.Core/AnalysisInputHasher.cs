using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mcotis.Core;

/// <summary>Hashes what an analysis reads of a document: everything except names and origins, in an order that does
/// not depend on how the document was built. Two documents with the same hash give the same results.</summary>
internal static class AnalysisInputHasher
{
    public static string Compute(ModelDocument d)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var w = new Writer(hash);

        w.Section("points", d.Points.Count);
        foreach (var point in d.Points.Values.OrderBy(item => item.Id)) { w.Id(point.Id); w.Xyz(point.Position); }

        w.Section("frames", d.Frames.Count);
        foreach (var frame in d.Frames.Values.OrderBy(item => item.Id)) { w.Id(frame.Id); w.Id(frame.StartPointId); w.Id(frame.EndPointId); }

        w.Section("cables", d.Cables.Count);
        foreach (var cable in d.Cables.Values.OrderBy(item => item.Id)) { w.Id(cable.Id); w.Id(cable.StartPointId); w.Id(cable.EndPointId); }

        w.Section("shells", d.Shells.Count);
        foreach (var shell in d.Shells.Values.OrderBy(item => item.Id))
        {
            w.Id(shell.Id);
            w.Int(shell.PointIds.Length);
            foreach (var pointId in shell.PointIds) w.Id(pointId);
        }

        w.Section("materials", d.Materials.Count);
        foreach (var material in d.Materials.Values.OrderBy(item => item.Id))
        {
            w.Id(material.Id); w.Int((int)material.Type);
            w.Doubles(material.ElasticModulus, material.PoissonRatio, material.WeightDensity, material.MassDensity);
        }

        w.Section("frame sections", d.FrameSections.Count);
        foreach (var section in d.FrameSections.Values.OrderBy(item => item.Id))
        {
            var p = section.Properties;
            w.Id(section.Id); w.Id(section.MaterialId); w.Int((int)section.Shape);
            w.Doubles(p.Area, p.Inertia33, p.Inertia22, p.TorsionConstant, p.ShearArea2, p.ShearArea3);
            w.Int(section.Dimensions.Count);
            foreach (var (key, value) in section.Dimensions) { w.Text(key); w.Doubles(value); }
        }

        w.Section("cable sections", d.CableSections.Count);
        foreach (var section in d.CableSections.Values.OrderBy(item => item.Id)) { w.Id(section.Id); w.Id(section.MaterialId); w.Doubles(section.Area); }

        w.Section("shell sections", d.ShellSections.Count);
        foreach (var section in d.ShellSections.Values.OrderBy(item => item.Id))
        {
            w.Id(section.Id); w.Id(section.MaterialId); w.Int((int)section.Type); w.Doubles(section.Thickness);
        }

        w.Section("restraints", d.Restraints.Count);
        foreach (var (pointId, r) in d.Restraints.OrderBy(item => item.Key)) { w.Id(pointId); w.Bools(r.Ux, r.Uy, r.Uz, r.Rx, r.Ry, r.Rz); }

        w.Section("frame assignments", d.FrameAssignments.Count);
        foreach (var (frameId, a) in d.FrameAssignments.OrderBy(item => item.Key))
        {
            w.Id(frameId); w.OptionalId(a.SectionId);
            foreach (var end in new[] { a.Releases.Start, a.Releases.End }) w.Bools(end.Axial, end.Shear2, end.Shear3, end.Torsion, end.Moment2, end.Moment3);
            w.Doubles(a.LocalAxisAngle, a.Modifiers.Area, a.Modifiers.Inertia33, a.Modifiers.Inertia22, a.Modifiers.Torsion, a.Modifiers.Mass, a.Modifiers.Weight);
        }

        w.Section("cable assignments", d.CableAssignments.Count);
        foreach (var (cableId, a) in d.CableAssignments.OrderBy(item => item.Key))
        {
            w.Id(cableId); w.OptionalId(a.SectionId); w.Doubles(a.Modifiers.Area, a.Modifiers.Mass, a.Modifiers.Weight);
        }

        w.Section("shell assignments", d.ShellAssignments.Count);
        foreach (var (shellId, a) in d.ShellAssignments.OrderBy(item => item.Key)) { w.Id(shellId); w.OptionalId(a.SectionId); w.Doubles(a.LocalAxisAngle); }

        w.Section("load patterns", d.LoadPatterns.Count);
        foreach (var pattern in d.LoadPatterns.Values.OrderBy(item => item.Id)) { w.Id(pattern.Id); w.Int((int)pattern.Type); w.Doubles(pattern.SelfWeightMultiplier); }

        w.Section("loads", d.Loads.Count);
        foreach (var load in d.Loads.Values.OrderBy(item => item.Id))
        {
            w.Id(load.Id); w.Id(load.PatternId); w.Id(load.TargetId);
            switch (load)
            {
                case PointLoad point: w.Int(1); w.Xyz(point.Force); w.Xyz(point.Moment); break;
                case FrameDistributedLoad line: w.Int(2); w.Int((int)line.Direction); w.Doubles(line.RelativeStart, line.RelativeEnd, line.ValueStart, line.ValueEnd); break;
                case FramePointLoad point: w.Int(3); w.Int((int)point.Direction); w.Doubles(point.RelativeDistance, point.Value); break;
                case CableTargetForce target: w.Int(4); w.Doubles(target.Force, target.RelativeDistance); break;
                case ShellUniformLoad area: w.Int(5); w.Int((int)area.Direction); w.Doubles(area.Value); break;
                default: throw new NotSupportedException($"The load type {load.GetType().Name} is not part of the analysis input hash yet.");
            }
        }

        w.Section("load cases", d.LoadCases.Count);
        foreach (var loadCase in d.LoadCases.Values.OrderBy(item => item.Id))
        {
            w.Id(loadCase.Id); w.Int((int)loadCase.Type); w.OptionalId(loadCase.InitialCaseId); w.Bools(loadCase.Run, loadCase.FitCableTargetForces);
            w.Int(loadCase.Components.Length);
            foreach (var component in loadCase.Components) { w.Id(component.PatternId); w.Doubles(component.Factor); }
        }

        w.Section("load combinations", d.LoadCombinations.Count);
        foreach (var combination in d.LoadCombinations.Values.OrderBy(item => item.Id))
        {
            w.Id(combination.Id); w.Int((int)combination.Type); w.Bools(combination.Run);
            w.Int(combination.Terms.Length);
            foreach (var term in combination.Terms) { w.Id(term.SourceId); w.Doubles(term.Factor); }
        }

        var s = d.Settings;
        w.Section("settings", 1);
        w.Doubles(s.CableFitTolerancePercent); w.Int(s.CableFitMaxIterations); w.Int(s.FrameMeshSegments); w.Bools(s.CableSag);

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class Writer(IncrementalHash hash)
    {
        private readonly byte[] buffer = new byte[16];

        public void Section(string name, int count) { Text(name); Int(count); }

        public void Text(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Int(bytes.Length);
            hash.AppendData(bytes);
        }

        public void Id(ObjectId id)
        {
            id.Value.TryWriteBytes(buffer);
            hash.AppendData(buffer, 0, 16);
        }

        public void OptionalId(ObjectId? id) => Id(id ?? default);

        public void Int(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            hash.AppendData(buffer, 0, 4);
        }

        public void Doubles(params double[] values)
        {
            foreach (var value in values)
            {
                // -0.0 and 0.0 are the same input
                BinaryPrimitives.WriteInt64LittleEndian(buffer, BitConverter.DoubleToInt64Bits(value == 0 ? 0.0 : value));
                hash.AppendData(buffer, 0, 8);
            }
        }

        public void Xyz(Xyz value) => Doubles(value.X, value.Y, value.Z);

        public void Bools(params bool[] values)
        {
            foreach (var value in values)
            {
                buffer[0] = value ? (byte)1 : (byte)0;
                hash.AppendData(buffer, 0, 1);
            }
        }
    }
}
