using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Application;

/// <summary>The rise of an arch: from its springing (<see cref="Support"/>) to its crown, the highest joint of the arch.</summary>
public sealed record ArchCrown(string Support, string Crown, double SupportZ, double CrownZ)
{
    public double Rise => CrownZ - SupportZ;
}

/// <summary>
/// Finds the arch of a model in the XZ plane, for the viewer's arch height dimension (File → Load TAS…). The arch is the
/// frames of one section that leave two springing points inclined (not plumb) and join them in one chain; a springing
/// point is a pinned support, or a joint a plumb column carries up from it (an arch raised on columns). Its crown is the
/// highest joint of that chain. A model without such a chain (a truss, a frame on columns) has no arch.
/// </summary>
public static class ArchRise
{
    public static ArchCrown? Find(GeometryModel geometry, IEnumerable<string>? supports, Func<string, string?> sectionOf)
    {
        var pins = (supports ?? []).Where(geometry.Points.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
        if (pins.Length < 2) return null;
        var frames = geometry.Members.Where(member => member.Kind == MemberKind.Frame &&
            geometry.Points.ContainsKey(member.Start) && geometry.Points.ContainsKey(member.End)).ToArray();
        Position At(string joint) => geometry.Points[joint].Position;
        bool Plumb(StructuralMember member) => Math.Abs(At(member.Start).X - At(member.End).X) <= 1e-3;
        bool Inclined(StructuralMember member) => !Plumb(member) && Math.Abs(At(member.Start).Z - At(member.End).Z) > 1e-3;
        string Other(StructuralMember member, string joint) => member.Start == joint ? member.End : member.Start;

        // the springing points of each pin: the pin and the joints plumb columns carry up from it
        List<string> Springings(string pin)
        {
            var found = new List<string> { pin };
            for (var index = 0; index < found.Count; index++)
                foreach (var member in frames.Where(member => (member.Start == found[index] || member.End == found[index]) && Plumb(member)))
                {
                    var next = Other(member, found[index]);
                    if (At(next).Z > At(found[index]).Z && !found.Contains(next)) found.Add(next);
                }
            return found;
        }
        // the sections leaving each springing point inclined
        var springing = pins.ToDictionary(pin => pin, pin => Springings(pin).Select(joint => (Joint: joint,
            Sections: frames.Where(member => (member.Start == joint || member.End == joint) && Inclined(member))
                .Select(member => sectionOf(member.Name)).OfType<string>().ToHashSet(StringComparer.Ordinal))).ToArray(), StringComparer.Ordinal);
        foreach (var section in springing.Values.SelectMany(points => points.SelectMany(point => point.Sections)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // per pin, the lowest springing point the section leaves inclined
            var ends = pins.Select(pin => springing[pin].Where(point => point.Sections.Contains(section)).Select(point => point.Joint).FirstOrDefault())
                .OfType<string>().ToArray();
            if (ends.Length < 2) continue;
            var arch = frames.Where(member => sectionOf(member.Name) == section && !Plumb(member)).ToArray();
            var links = arch.SelectMany(member => new[] { (member.Start, member.End), (member.End, member.Start) })
                .ToLookup(link => link.Item1, link => link.Item2, StringComparer.Ordinal);
            // the chain reached from the first springing point must reach another one
            var reached = new HashSet<string>(StringComparer.Ordinal) { ends[0] };
            var queue = new Queue<string>([ends[0]]);
            while (queue.Count > 0)
                foreach (var next in links[queue.Dequeue()])
                    if (reached.Add(next)) queue.Enqueue(next);
            if (!ends.Skip(1).Any(reached.Contains)) continue;
            var support = ends.Where(reached.Contains).OrderBy(joint => At(joint).Z).ThenBy(joint => At(joint).X).First();
            var crown = reached.OrderByDescending(joint => At(joint).Z).ThenBy(joint => At(joint).X).First();
            var (supportZ, crownZ) = (At(support).Z, At(crown).Z);
            if (crownZ - supportZ > 1e-3) return new ArchCrown(support, crown, supportZ, crownZ);
        }
        return null;
    }
}
