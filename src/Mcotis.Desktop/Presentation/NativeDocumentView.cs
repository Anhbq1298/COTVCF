using Mcotis.Desktop.Application;
using Mcotis.Desktop.Domain;
using Mcotis.Desktop.Geometry;
using Mcotis.Core;

namespace Mcotis.Desktop.Presentation;

/// <summary>
/// Presentation-only view of a native document for controls that still draw <see cref="GeometryModel"/>.
/// Object names are the main-window names recorded by the migration; native-only objects use their own names.
/// This adapter owns no model state and must not be used for analysis or editing.
/// </summary>
internal static class NativeDocumentView
{
    public static GeometryModel Geometry(ModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var names = LegacyNames.Of(document);
        var points = document.Points.Values.ToDictionary(
            point => names[point.Id],
            point => new StructuralPoint(names[point.Id], new(point.Position.X, point.Position.Y, point.Position.Z)),
            StringComparer.Ordinal);
        var members = document.Frames.Values.Select(frame => new StructuralMember(
                names[frame.Id], names[frame.StartPointId], names[frame.EndPointId], MemberKind.Frame))
            .Concat(document.Cables.Values.Select(cable => new StructuralMember(
                names[cable.Id], names[cable.StartPointId], names[cable.EndPointId], MemberKind.Cable)))
            .ToArray();
        return new(points, members);
    }

    public static IReadOnlyDictionary<string, Restraint> Restraints(ModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var names = LegacyNames.Of(document);
        return document.Restraints
            .Where(item => !item.Value.IsFree && document.Points.ContainsKey(item.Key))
            .ToDictionary(item => names[item.Key], item => item.Value, StringComparer.Ordinal);
    }
}
