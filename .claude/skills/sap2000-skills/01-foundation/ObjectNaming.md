# SAP2000 Object Naming & Identification

**Category:** Foundation  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Establish robust object identity across SAP2000, BIM, and application layers.

**Critical Principle:**
> Do not rely solely on visible SAP names for object identity. Maintain explicit ID mapping.

---

## Naming Convention

### Points (Nodes)
Format: `PT_<Index>` or `PT_<Story>_<GridX>_<GridY>`

```
PT_001              (Generic point 1)
PT_L1_0_0           (Level L1, at origin)
PT_L2_A_B_5000      (Level L2, Grid A-B intersection, Z=5000)
```

### Frame Objects (Beams, Columns, Braces)
Format: `FR_<Type>_<Index>` or `FR_<Type>_<Story>_<ID>`

```
FR_BEAM_001         (Beam 1)
FR_COL_L1_C101      (Column Level L1, column C-101)
FR_BRACE_001        (Brace 1)
FR_CHORD_UPPER_001  (Upper chord of truss)
```

### Shell Objects (Walls, Slabs)
Format: `SH_<Type>_<Index>` or `SH_<Type>_<Story>_<ID>`

```
SH_SLAB_L1_001      (Slab Level L1, instance 001)
SH_WALL_001         (Wall 1)
SH_CORE_L2_001      (Core wall Level L2)
```

### Groups
Format: `GRP_<Category>` or `GRP_<Story>_<Category>`

```
GRP_COLUMNS         (All columns)
GRP_L1_BEAMS        (All beams on Level 1)
GRP_DIAPHRAGM_L2    (Diaphragm constraint Level 2)
```

### Load Cases
Format: `LC_<Description>`

```
LC_SELFWEIGHT       (Self-weight analysis)
LC_DEAD             (Dead load)
LC_LIVE             (Live load)
LC_WINDX            (Wind in X direction)
LC_SEISMIC_X        (Seismic in X direction)
```

### Load Combinations
Format: `COMB_<Standard>_<Description>`

```
COMB_LRFD_1_2DL_1_6_LL     (LRFD: 1.2 DL + 1.6 LL)
COMB_ASD_1_DL_1_LL         (ASD: 1 DL + 1 LL)
```

---

## Identity Mapping (Critical for BIM Integration)

### Application-Level Model

Each structural object should carry:

```csharp
public abstract class StructuralObject
{
    /// <summary>
    /// Unique internal application ID (GUID).
    /// Stable across SAP synchronization.
    /// </summary>
    public Guid ApplicationId { get; set; }

    /// <summary>
    /// SAP2000 object name (e.g., "FR_BEAM_001").
    /// May change if renamed in SAP.
    /// </summary>
    public string SAPObjectName { get; set; }

    /// <summary>
    /// BIM element ID (e.g., Revit element ID).
    /// For tracking source/destination in BIM-SAP mapping.
    /// </summary>
    public long? BIMElementId { get; set; }

    /// <summary>
    /// IFC GUID (for IFC interoperability).
    /// </summary>
    public Guid? IFCGuid { get; set; }

    /// <summary>
    /// Story/level assignment (e.g., "L1", "L2").
    /// Important for vertical coordination.
    /// </summary>
    public string Story { get; set; }

    /// <summary>
    /// Engineering description/comment.
    /// </summary>
    public string Description { get; set; }
}
```

### Point Object Example

```csharp
public class Point : StructuralObject
{
    /// <summary>
    /// Global X coordinate (mm, N-mm-C).
    /// </summary>
    public double X_mm { get; set; }

    /// <summary>
    /// Global Y coordinate (mm).
    /// </summary>
    public double Y_mm { get; set; }

    /// <summary>
    /// Global Z coordinate (mm).
    /// </summary>
    public double Z_mm { get; set; }

    /// <summary>
    /// Unique hash of coordinates (for duplicate detection).
    /// Used to validate BIM-to-SAP mapping.
    /// </summary>
    public string CoordinateHash { get; set; }

    public override string ToString()
    {
        return $"{SAPObjectName} @ ({X_mm}, {Y_mm}, {Z_mm}) mm";
    }
}
```

### Frame Object Example

```csharp
public class Frame : StructuralObject
{
    public string StartPointName { get; set; }
    public string EndPointName { get; set; }
    public string MaterialName { get; set; }
    public string SectionName { get; set; }

    /// <summary>
    /// Local axis 1 direction (unit vector from start to end).
    /// </summary>
    public (double x, double y, double z) Local1Axis { get; set; }

    /// <summary>
    /// Intended local axis 2 orientation (documented at creation).
    /// E.g., "Up" for horizontal beams, "Out" for columns.
    /// </summary>
    public string Local2AxisDesign { get; set; }

    /// <summary>
    /// Rotation angle applied to local axes (degrees).
    /// </summary>
    public double LocalAxisRotationDegrees { get; set; } = 0.0;

    public override string ToString()
    {
        return $"{SAPObjectName}: {StartPointName} → {EndPointName}, " +
               $"Material={MaterialName}, Section={SectionName}";
    }
}
```

---

## Mapping Workflow

### Import from SAP2000

```csharp
public List<Point> ReadPointsFromSAP(cSapModel model)
{
    var points = new List<Point>();

    // Get all point objects
    int count = 0;
    string[] names = null;
    int ret = model.PointObj.GetNameList(ref count, ref names);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "PointObj.GetNameList",
            returnCode: ret,
            userMessage: "Failed to retrieve point list from SAP2000");
    }

    for (int i = 0; i < count; i++)
    {
        string pointName = names[i];
        double x = 0, y = 0, z = 0;

        ret = model.PointObj.GetCoordinates(pointName, ref x, ref y, ref z);
        if (ret != 0)
        {
            throw new SAPException(
                operation: "PointObj.GetCoordinates",
                returnCode: ret,
                context: $"Point: {pointName}",
                userMessage: $"Failed to read coordinates for point '{pointName}'");
        }

        var point = new Point
        {
            ApplicationId = Guid.NewGuid(), // Or retrieve from database if re-importing
            SAPObjectName = pointName,
            X_mm = x,
            Y_mm = y,
            Z_mm = z,
            CoordinateHash = GetCoordinateHash(x, y, z),
            Story = ExtractStoryFromName(pointName) // "L1", "L2", etc.
        };

        points.Add(point);
    }

    return points;
}

private string GetCoordinateHash(double x, double y, double z)
{
    // Simple hash for duplicate detection (use tolerance-based matching in production)
    return $"{x:F2}_{y:F2}_{z:F2}";
}

private string ExtractStoryFromName(string pointName)
{
    // Parse "PT_L1_..." → "L1"
    var parts = pointName.Split('_');
    if (parts.Length >= 2 && parts[1].StartsWith("L"))
    {
        return parts[1];
    }
    return "Unknown";
}
```

---

## Validation Checklist

✓ All SAP objects follow naming convention  
✓ Application objects carry ApplicationId (stable GUID)  
✓ BIM mapping includes source ID (Revit, IFC)  
✓ Story/level assignment documented  
✓ SAP name extraction correctly handles format  
✓ No reliance on display names for identity  
✓ Unique object names verified at creation  
✓ Name changes tracked (if applicable)  
✓ Coordinate hashes support duplicate detection  
✓ Mapping reversible (SAP → App and App → SAP)  

---

## Common Pitfalls

### ❌ Relying on Display Name
```csharp
// WRONG: If user renames in SAP, mapping breaks
var frame = frames.FirstOrDefault(f => f.SAPObjectName == "Beam-1");
// User renames to "Beam-2" in SAP GUI → frame not found!
```

**Fix:** Use ApplicationId (internal GUID).

```csharp
var frame = frames.FirstOrDefault(f => f.ApplicationId == expectedGuid);
```

### ❌ Duplicate Names
```csharp
// WRONG: Two beams named "FR_BEAM_001"
model.FrameObj.AddFrame(p1, p2, "FR_BEAM_001", "");
model.FrameObj.AddFrame(p3, p4, "FR_BEAM_001", ""); // SAP may reject or replace first!
```

**Fix:** Enforce unique names in application layer.

```csharp
public void CreateFrameWithUniqueNameCheck(
    cSapModel model,
    string intendedName,
    Point start, Point end)
{
    // Verify name is unique
    if (FrameExists(model, intendedName))
    {
        throw new InvalidOperationException(
            $"Frame name '{intendedName}' already exists in SAP2000. " +
            $"Use unique names or check for duplicates before creation.");
    }

    int ret = model.FrameObj.AddFrame(
        start.X_mm, start.Y_mm, start.Z_mm,
        end.X_mm, end.Y_mm, end.Z_mm,
        intendedName, "");

    if (ret != 0)
    {
        throw new SAPException(
            operation: "FrameObj.AddFrame",
            returnCode: ret,
            context: $"Frame name: '{intendedName}'");
    }
}
```

### ❌ Losing BIM Identity
```csharp
// WRONG: No record of Revit element → SAP frame mapping
CreateFrameFromRevitBeam(revitBeam, "FR_BEAM_001");
// Later: Which Revit beam is FR_BEAM_001?
```

**Fix:** Store BIMElementId in application object.

```csharp
var frame = new Frame
{
    ApplicationId = Guid.NewGuid(),
    SAPObjectName = "FR_BEAM_001",
    BIMElementId = revitBeam.Id.IntegerValue, // Store Revit ID
    Description = $"Created from Revit element {revitBeam.Name}"
};
```

---

## External References

- **SAP2000 Object Naming:** SAP2000 Help → Objects → Naming conventions
- **BIM Identity Standards:** IFC GUID, Revit Element IDs
- **Coordinate Hashing:** See Units & Coordinates skill for tolerance-based matching

---

## Related Skills

- [Units & Coordinates](UnitsCoordinates.md) — Coordinates for identity hashing
- [Point Objects & Nodes](../02-objects/PointObjects.md) — Create with unique names
- [Frame Objects](../02-objects/FrameObjects.md) — Frame naming
- [BIM-to-SAP Synchronization](../07-advanced/BIMSynchronization.md) — Maintain identity during sync
