# SAP2000 BIM-to-SAP Synchronization

**Category:** Advanced  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Synchronize structural models between BIM (Revit, IFC) and SAP2000 with identity preservation.

---

## Project Convention

**Mapping:** BIM Element ID ↔ SAP Object Name ↔ Application ID (GUID)

**Workflow:** BIM Export → Transform → SAP Create → Verify → Track IDs

---

## Workflow

### Import Revit Structural Model

```csharp
public void ImportFromRevit(
    cSapModel model,
    List<RevitColumn> columns,
    List<RevitBeam> beams)
{
    foreach (var col in columns)
    {
        // Transform Revit coordinates to SAP global XYZ
        var sapCoords = TransformRevitToSAP(col.BasePoint, col.TopPoint);

        // Create point objects
        var p1 = CreatePoint(model, sapCoords.Base.X, sapCoords.Base.Y, sapCoords.Base.Z,
                            $"PT_COL_{col.RevitElementId}", col.Story);
        var p2 = CreatePoint(model, sapCoords.Top.X, sapCoords.Top.Y, sapCoords.Top.Z,
                            $"PT_COL_{col.RevitElementId}_TOP", col.Story);

        // Create frame
        var frame = CreateFrameByPoints(model, p1.SAPObjectName, p2.SAPObjectName,
                                       $"FR_COL_{col.RevitElementId}", col.SectionName);

        // Preserve BIM identity
        frame.BIMElementId = col.RevitElementId;

        // Store mapping for two-way sync
        _bidirectionalMapping.Add(col.RevitElementId, frame.ApplicationId);
    }
}
```

---

## Validation Checklist

✓ Revit → SAP coordinate transform verified  
✓ BIM IDs tracked for all objects  
✓ No ID collisions  
✓ Two-way mapping functional  

---

## Related Skills

- [Object Naming & Identification](../01-foundation/ObjectNaming.md) — Identity mapping
- [Coordinate Transformation](CoordinateTransform.md) — Transform BIM coordinates
