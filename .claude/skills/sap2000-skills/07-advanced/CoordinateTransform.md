# SAP2000 Coordinate Transformations

**Category:** Advanced  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Transform coordinates between BIM (Revit, IFC), survey systems, and SAP2000 global coordinates.

---

## Project Convention

**Transformation Pipeline:**
```
BIM Coordinates (Project Coordinates)
        ↓
[Apply Shared Coordinate Transform]
        ↓
Survey System (Easting, Northing, Elevation)
        ↓
[Apply Survey → SAP Transform]
        ↓
SAP2000 Global XYZ (N-mm-C)
```

---

## Code Example: Revit to SAP Transform

```csharp
public class CoordinateTransformService
{
    public (double x, double y, double z) TransformRevitToSAP(
        double revitX_m, double revitY_m, double revitZ_m,
        Matrix4x4 sharedCoordinateTransform,
        double rotationZ_degrees = 0.0)
    {
        // 1. Revit coordinates (meters) → MM
        var revitMM = (revitX_m * 1000, revitY_m * 1000, revitZ_m * 1000);

        // 2. Apply shared coordinate transform
        var (x, y, z) = ApplyMatrix(revitMM, sharedCoordinateTransform);

        // 3. Apply rotation about Z
        var rotated = RotateAboutZ(x, y, z, rotationZ_degrees);

        return rotated;
    }

    private (double, double, double) ApplyMatrix(
        (double x, double y, double z) point,
        Matrix4x4 transform)
    {
        var vector = new System.Numerics.Vector4((float)point.x, (float)point.y, (float)point.z, 1.0f);
        var result = System.Numerics.Vector4.Transform(vector, transform);
        return (result.X, result.Y, result.Z);
    }

    private (double, double, double) RotateAboutZ(
        double x, double y, double z,
        double angleDegrees)
    {
        double angle = angleDegrees * Math.PI / 180.0;
        double cos_a = Math.Cos(angle);
        double sin_a = Math.Sin(angle);

        double x_rot = x * cos_a - y * sin_a;
        double y_rot = x * sin_a + y * cos_a;

        return (x_rot, y_rot, z);
    }
}
```

---

## Validation Checklist

✓ Transform matrix validated  
✓ Test points verified after transform  
✓ Rotation angle appropriate  
✓ Scale factors correct (mm conversion)  

---

## Related Skills

- [Units & Coordinates](../01-foundation/UnitsCoordinates.md) — Coordinate systems
- [BIM Synchronization](BIMSynchronization.md) — Apply transforms during sync
