using Mcotis.Desktop.Domain;

namespace Mcotis.Desktop.Geometry.Handlers;

public sealed class FrameGeometryHandler
{
    public IReadOnlyList<string> Validate(FrameObject frame, IReadOnlyDictionary<JointId, JointObject> joints) =>
        ValidateMember(frame.Name, frame.StartJointId, frame.EndJointId, joints);

    public StructuralMember ToSolver(FrameObject frame, IReadOnlyDictionary<JointId, JointObject> joints) =>
        new(frame.Name, joints[frame.StartJointId].Name, joints[frame.EndJointId].Name, MemberKind.Frame);

    internal static IReadOnlyList<string> ValidateMember(string name, JointId start, JointId end,
        IReadOnlyDictionary<JointId, JointObject> joints)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) problems.Add("Member name is required.");
        if (!joints.ContainsKey(start)) problems.Add($"Member '{name}' has no start joint.");
        if (!joints.ContainsKey(end)) problems.Add($"Member '{name}' has no end joint.");
        if (start == end) problems.Add($"Member '{name}' starts and ends at the same joint.");
        return problems;
    }
}
