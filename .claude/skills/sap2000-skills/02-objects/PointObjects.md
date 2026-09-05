# SAP2000 Point Objects & Nodes

**Category:** Objects  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Create and manage point objects (joints/nodes) that form the geometric foundation of the structural model.

---

## API Reference

### Key Methods

| Method | Returns | Purpose |
|--------|---------|---------|
| `PointObj.AddPoint(x, y, z, name, group)` | int | Create point at global coordinates |
| `PointObj.GetCoordinates(name, ref x, ref y, ref z)` | int | Read point coordinates |
| `PointObj.SetCoordinates(name, x, y, z)` | int | Modify point coordinates |
| `PointObj.GetNameList(ref count, ref names)` | int | Get all point names |
| `PointObj.DeletePoint(name)` | int | Delete point |
| `PointObj.SetName(oldName, newName)` | int | Rename point |

---

## Project Convention

**Naming:** `PT_<Index>` or `PT_<Story>_<X>_<Y>`

**Units:** Always mm in N-mm-C system

**Coordinates:** Global XYZ (not local)

---

## Workflow

### Create Point

```csharp
public Point CreatePoint(
    cSapModel model,
    double x_mm, double y_mm, double z_mm,
    string pointName,
    string story = "")
{
    // Validate
    if (string.IsNullOrWhiteSpace(pointName))
        throw new ArgumentException("Point name required");

    // Create in SAP
    int ret = model.PointObj.AddPoint(x_mm, y_mm, z_mm, pointName, "");

    if (ret != 0)
    {
        throw new SAPException(
            operation: "PointObj.AddPoint",
            returnCode: ret,
            context: $"Point '{pointName}' at ({x_mm}, {y_mm}, {z_mm}) mm",
            userMessage: "Failed to create point. Verify name is unique and coordinates are within bounds.");
    }

    return new Point
    {
        ApplicationId = Guid.NewGuid(),
        SAPObjectName = pointName,
        X_mm = x_mm,
        Y_mm = y_mm,
        Z_mm = z_mm,
        Story = story,
        CoordinateHash = CoordinateHash(x_mm, y_mm, z_mm)
    };
}
```

### Read Point

```csharp
public Point ReadPoint(cSapModel model, string pointName)
{
    double x = 0, y = 0, z = 0;

    int ret = model.PointObj.GetCoordinates(pointName, ref x, ref y, ref z);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "PointObj.GetCoordinates",
            returnCode: ret,
            context: $"Point: {pointName}",
            userMessage: $"Failed to read coordinates for point '{pointName}'");
    }

    return new Point
    {
        SAPObjectName = pointName,
        X_mm = x,
        Y_mm = y,
        Z_mm = z,
        CoordinateHash = CoordinateHash(x, y, z)
    };
}
```

### Read All Points

```csharp
public List<Point> ReadAllPoints(cSapModel model)
{
    var points = new List<Point>();

    int count = 0;
    string[] names = null;

    int ret = model.PointObj.GetNameList(ref count, ref names);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "PointObj.GetNameList",
            returnCode: ret,
            userMessage: "Failed to retrieve point list");
    }

    for (int i = 0; i < count; i++)
    {
        try
        {
            var point = ReadPoint(model, names[i]);
            points.Add(point);
        }
        catch (Exception ex)
        {
            // Log but continue reading other points
            Debug.WriteLine($"Warning: Failed to read point {names[i]}: {ex.Message}");
        }
    }

    return points;
}
```

### Modify Point Coordinates

```csharp
public void MovePoint(
    cSapModel model,
    string pointName,
    double newX_mm, double newY_mm, double newZ_mm)
{
    int ret = model.PointObj.SetCoordinates(pointName, newX_mm, newY_mm, newZ_mm);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "PointObj.SetCoordinates",
            returnCode: ret,
            context: $"Point '{pointName}' to ({newX_mm}, {newY_mm}, {newZ_mm}) mm",
            userMessage: "Failed to move point. Verify point exists and coordinates are valid.");
    }
}
```

---

## Code Example: Batch Creation

```csharp
public List<Point> CreateGridOfPoints(
    cSapModel model,
    double xStart_mm, double xEnd_mm, double xSpacing_mm,
    double yStart_mm, double yEnd_mm, double ySpacing_mm,
    double z_mm,
    string storyLabel = "L1")
{
    var points = new List<Point>();
    int pointIndex = 1;

    for (double x = xStart_mm; x <= xEnd_mm + 0.1; x += xSpacing_mm)
    {
        for (double y = yStart_mm; y <= yEnd_mm + 0.1; y += ySpacing_mm)
        {
            string pointName = $"PT_{storyLabel}_{(int)x}_{(int)y}";

            var point = CreatePoint(model, x, y, z_mm, pointName, storyLabel);
            points.Add(point);
            pointIndex++;
        }
    }

    return points;
}
```

---

## Validation Checklist

✓ Points created in N-mm-C coordinates  
✓ Global XYZ (not local) coordinates  
✓ Unique point names verified  
✓ Coordinate hashes computed for duplicate detection  
✓ All return codes checked  
✓ Story/level assignment documented  
✓ Point existence verified before reading  

---

## Common Pitfalls

### ❌ Forgetting Units
```csharp
// WRONG: Assuming meters
double bimX_m = 5.0;
model.PointObj.AddPoint(bimX_m, 0, 0, "PT_1", ""); // Treated as 5 mm!
```

**Fix:** Convert to mm.

```csharp
double sapX_mm = bimX_m * 1000.0;
model.PointObj.AddPoint(sapX_mm, 0, 0, "PT_1", "");
```

### ❌ Duplicate Points
```csharp
// WRONG: Creates points with same coordinates
for (int i = 0; i < 3; i++)
{
    model.PointObj.AddPoint(0, 0, 0, $"PT_{i}", "");
    // Three points at origin!
}
```

**Fix:** Use coordinate hash to detect duplicates before creating.

---

## Related Skills

- [Units & Coordinates](../01-foundation/UnitsCoordinates.md) — Global XYZ
- [Frame Objects](FrameObjects.md) — Connect points with frames
- [Object Naming & Identification](../01-foundation/ObjectNaming.md) — Naming convention
