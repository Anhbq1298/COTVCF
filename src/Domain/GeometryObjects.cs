namespace COT_VCF.Domain;

public readonly record struct JointId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct MemberId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum ModelObjectOrigin { Imported, Generated, User }

public sealed record JointObject(JointId Id, string Name, Position Position,
    ModelObjectOrigin Origin = ModelObjectOrigin.Imported, string? GenerationKey = null);

public abstract record MemberObject(MemberId Id, string Name, JointId StartJointId, JointId EndJointId,
    ModelObjectOrigin Origin, string? GenerationKey);

public sealed record FrameObject(MemberId Id, string Name, JointId StartJointId, JointId EndJointId,
    ModelObjectOrigin Origin = ModelObjectOrigin.Imported, string? GenerationKey = null)
    : MemberObject(Id, Name, StartJointId, EndJointId, Origin, GenerationKey);

public sealed record CableObject(MemberId Id, string Name, JointId StartJointId, JointId EndJointId,
    ModelObjectOrigin Origin = ModelObjectOrigin.Imported, string? GenerationKey = null)
    : MemberObject(Id, Name, StartJointId, EndJointId, Origin, GenerationKey);

public sealed record TendonObject(MemberId Id, string Name, JointId StartJointId, JointId EndJointId,
    ModelObjectOrigin Origin = ModelObjectOrigin.Imported, string? GenerationKey = null)
    : MemberObject(Id, Name, StartJointId, EndJointId, Origin, GenerationKey);

public sealed record GeometryObjectModel(IReadOnlyDictionary<JointId, JointObject> Joints, IReadOnlyList<MemberObject> Members);
