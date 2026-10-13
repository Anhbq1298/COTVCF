namespace Mcotis.Desktop.Application.Etabs;

/// <summary>
/// Joins the frames ETABS splits at every story it crosses back into one frame per straight member (File → Load TAS…).
/// A joint between two frames goes when nothing else needs it: exactly those two frames meet there, end to start, along
/// one straight line, with the same kind, section, angle and modifiers, no end release at that joint, and the joint has
/// no load and no restraint. The joined frame keeps the first frame's label and story, the first frame's start releases
/// and the last frame's end releases. Frames that carry frame loads are left as they are.
/// </summary>
public static class EtabsFrameChains
{
    public static EtabsModel Join(EtabsModel model)
    {
        var joints = model.Joints.ToDictionary(joint => joint.Name, StringComparer.Ordinal);
        var pinned = model.Restraints.Select(item => item.Joint).Concat(model.JointLoads.Select(load => load.Joint)).ToHashSet(StringComparer.Ordinal);
        var loaded = model.FrameLoads.Select(load => load.Frame).ToHashSet(StringComparer.Ordinal);
        var frames = model.Frames.ToList();
        var joined = true;
        while (joined)
        {
            joined = false;
            var atJoint = frames.SelectMany(frame => new[] { (Joint: frame.StartJoint, Frame: frame), (Joint: frame.EndJoint, Frame: frame) })
                .ToLookup(item => item.Joint, item => item.Frame, StringComparer.Ordinal);
            foreach (var group in atJoint)
            {
                if (pinned.Contains(group.Key) || group.Count() != 2) continue;
                var first = group.FirstOrDefault(frame => frame.EndJoint == group.Key);
                var second = group.FirstOrDefault(frame => frame.StartJoint == group.Key);
                if (first is null || second is null || ReferenceEquals(first, second) || !CanJoin(first, second, joints, loaded)) continue;
                var releases = first.Releases.Where(release => release.EndsWith('I'))
                    .Concat(second.Releases.Where(release => release.EndsWith('J'))).ToHashSet(StringComparer.Ordinal);
                var index = frames.IndexOf(first);
                frames[index] = first with { EndJoint = second.EndJoint, Releases = releases };
                frames.Remove(second);
                joined = true;
                break;
            }
        }
        var used = frames.SelectMany(frame => new[] { frame.StartJoint, frame.EndJoint }).Concat(pinned).ToHashSet(StringComparer.Ordinal);
        return model with { Frames = frames, Joints = model.Joints.Where(joint => used.Contains(joint.Name)).ToArray() };
    }

    private static bool CanJoin(EtabsFrame first, EtabsFrame second, IReadOnlyDictionary<string, EtabsJoint> joints, IReadOnlySet<string> loaded)
    {
        if (first.Kind != second.Kind || first.Section != second.Section || first.AngleDeg != second.AngleDeg ||
            !first.Modifiers.SequenceEqual(second.Modifiers) || loaded.Contains(first.Key) || loaded.Contains(second.Key) ||
            first.Releases.Any(release => release.EndsWith('J')) || second.Releases.Any(release => release.EndsWith('I')))
            return false;
        if (!joints.TryGetValue(first.StartJoint, out var a) || !joints.TryGetValue(first.EndJoint, out var b) ||
            !joints.TryGetValue(second.EndJoint, out var c)) return false;
        var (ux, uy, uz) = Unit(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var (vx, vy, vz) = Unit(c.X - b.X, c.Y - b.Y, c.Z - b.Z);
        // one straight line, same sense: the unit vectors agree to 1e-6 (about 0.2 mm off line over 120 m)
        return Math.Abs(ux - vx) < 1e-6 && Math.Abs(uy - vy) < 1e-6 && Math.Abs(uz - vz) < 1e-6;
    }

    private static (double, double, double) Unit(double x, double y, double z)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);
        return length > 0 ? (x / length, y / length, z / length) : (double.NaN, double.NaN, double.NaN);
    }
}
