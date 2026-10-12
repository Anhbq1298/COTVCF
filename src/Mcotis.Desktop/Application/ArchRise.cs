using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application;

/// <summary>The rise of an arch: from its supports to its crown, the highest joint of the arch.</summary>
public sealed record ArchCrown(string Support, string Crown, double SupportZ, double CrownZ)
{
    public double Rise => CrownZ - SupportZ;
}

/// <summary>
/// Finds the arch of a model in the XZ plane, for the viewer's arch height dimension (File → Load TAS…). The arch is
/// the frames of one section that leave two pinned supports inclined (not plumb) and join them in one chain; its crown
/// is the highest joint of that chain. A model without such a chain (a truss, a frame on columns) has no arch.
/// </summary>
public static class ArchRise
{
    public static ArchCrown? Find(GeometryModel geometry, IEnumerable<string>? supports, Func<string, string?> sectionOf)
    {
        var pins = (supports ?? []).Where(geometry.Points.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
        if (pins.Length < 2) return null;
        var frames = geometry.Members.Where(member => member.Kind == MemberKind.Frame &&
            geometry.Points.ContainsKey(member.Start) && geometry.Points.ContainsKey(member.End)).ToArray();
        bool Inclined(StructuralMember member)
        {
            var (a, b) = (geometry.Points[member.Start].Position, geometry.Points[member.End].Position);
            return Math.Abs(a.X - b.X) > 1e-3 && Math.Abs(a.Z - b.Z) > 1e-3;
        }
        // sections leaving each support inclined; the arch's section leaves at least two of them
        var springing = pins.ToDictionary(pin => pin, pin => frames.Where(member => (member.Start == pin || member.End == pin) && Inclined(member))
            .Select(member => sectionOf(member.Name)).OfType<string>().ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var section in springing.Values.SelectMany(sections => sections).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var ends = pins.Where(pin => springing[pin].Contains(section)).ToArray();
            if (ends.Length < 2) continue;
            var arch = frames.Where(member => sectionOf(member.Name) == section).ToArray();
            var links = arch.SelectMany(member => new[] { (member.Start, member.End), (member.End, member.Start) })
                .ToLookup(link => link.Item1, link => link.Item2, StringComparer.Ordinal);
            // the chain reached from the first support must reach another one
            var reached = new HashSet<string>(StringComparer.Ordinal) { ends[0] };
            var queue = new Queue<string>([ends[0]]);
            while (queue.Count > 0)
                foreach (var next in links[queue.Dequeue()])
                    if (reached.Add(next)) queue.Enqueue(next);
            if (!ends.Skip(1).Any(reached.Contains)) continue;
            var support = ends.Where(reached.Contains).OrderBy(pin => geometry.Points[pin].Position.Z).ThenBy(pin => geometry.Points[pin].Position.X).First();
            var crown = reached.OrderByDescending(name => geometry.Points[name].Position.Z).ThenBy(name => geometry.Points[name].Position.X).First();
            var (supportZ, crownZ) = (geometry.Points[support].Position.Z, geometry.Points[crown].Position.Z);
            if (crownZ - supportZ > 1e-3) return new ArchCrown(support, crown, supportZ, crownZ);
        }
        return null;
    }
}
