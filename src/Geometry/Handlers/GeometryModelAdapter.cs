using System.Security.Cryptography;
using System.Text;
using COT_VCF.Domain;

namespace COT_VCF.Geometry.Handlers;

public static class GeometryModelAdapter
{
    public static GeometryObjectModel ToObjects(GeometryModel geometry, Guid namespaceId)
    {
        var jointsByName = geometry.Points.Values.ToDictionary(point => point.Name,
            point => new JointObject(new JointId(Stable(namespaceId, "Joint", point.Name)), point.Name, point.Position), StringComparer.Ordinal);
        var joints = jointsByName.Values.ToDictionary(joint => joint.Id);
        var members = geometry.Members.Select(member => member.Kind switch
        {
            MemberKind.Frame => (MemberObject)new FrameObject(new MemberId(Stable(namespaceId, "Frame", member.Name)), member.Name,
                jointsByName[member.Start].Id, jointsByName[member.End].Id),
            MemberKind.Cable => new CableObject(new MemberId(Stable(namespaceId, "Cable", member.Name)), member.Name,
                jointsByName[member.Start].Id, jointsByName[member.End].Id),
            _ => new TendonObject(new MemberId(Stable(namespaceId, "Tendon", member.Name)), member.Name,
                jointsByName[member.Start].Id, jointsByName[member.End].Id),
        }).ToArray();
        return new GeometryObjectModel(joints, members);
    }

    public static GeometryModel ToSolver(GeometryObjectModel model)
    {
        var points = model.Joints.Values.ToDictionary(joint => joint.Name, joint => new StructuralPoint(joint.Name, joint.Position), StringComparer.Ordinal);
        var members = model.Members.Select(member => new StructuralMember(member.Name,
            model.Joints[member.StartJointId].Name, model.Joints[member.EndJointId].Name,
            member switch { FrameObject => MemberKind.Frame, CableObject => MemberKind.Cable, _ => MemberKind.Tendon })).ToArray();
        return new GeometryModel(points, members);
    }

    private static Guid Stable(Guid namespaceId, string type, string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{namespaceId:D}|{type}|{name}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
