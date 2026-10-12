using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Geometry.Handlers;

public sealed class JointGeometryHandler
{
    public IReadOnlyList<string> Validate(JointObject joint) =>
        string.IsNullOrWhiteSpace(joint.Name) ? ["Joint name is required."] :
        !joint.Position.IsFinite ? [$"Joint '{joint.Name}' has a non-finite coordinate."] : [];

    public StructuralPoint ToSolver(JointObject joint) => new(joint.Name, joint.Position);
}
