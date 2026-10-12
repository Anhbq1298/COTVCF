using COT_VCF.Domain;

namespace COT_VCF.Geometry.Handlers;

public sealed class CableGeometryHandler
{
    public IReadOnlyList<string> Validate(CableObject cable, IReadOnlyDictionary<JointId, JointObject> joints) =>
        FrameGeometryHandler.ValidateMember(cable.Name, cable.StartJointId, cable.EndJointId, joints);

    public StructuralMember ToSolver(CableObject cable, IReadOnlyDictionary<JointId, JointObject> joints) =>
        new(cable.Name, joints[cable.StartJointId].Name, joints[cable.EndJointId].Name, MemberKind.Cable);
}
