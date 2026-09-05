# SAP2000 Shell/Area Objects

**Category:** Objects  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Create and manage shell/area objects (slabs, walls, shells) that carry in-plane and bending forces.

---

## API Reference

### Key Methods

| Method | Returns | Purpose |
|--------|---------|---------|
| `AreaObj.AddByCoord(numPoints, x[], y[], z[], name, propName)` | int | Create area by coordinates |
| `AreaObj.AddByPoint(numPoints, pointNames[], name, propName)` | int | Create area by point names |
| `AreaObj.GetNameList(ref count, ref names)` | int | Get all area names |
| `AreaObj.SetMeshSize(name, meshSize, automatic)` | int | Set mesh refinement |
| `AreaObj.GetMeshSize(name, ref meshSize)` | int | Get mesh size |

---

## Project Convention

**Naming:** `SH_<Type>_<Index>` (SH_SLAB_L1_001, SH_WALL_001)

**Meshing:** Control mesh size via SetMeshSize (avoid SAP defaults)

**Degrees of Freedom:** Define membrane vs plate behavior via property

---

## Workflow

### Create Shell by Points

```csharp
public Shell CreateShellByPoints(
    cSapModel model,
    List<string> pointNames,
    string shellName,
    string propertyName = "")
{
    if (pointNames.Count < 3)
        throw new ArgumentException("Shell requires at least 3 points");

    // Convert to array
    string[] points = pointNames.ToArray();

    int ret = model.AreaObj.AddByPoint(
        pointNames.Count,
        points,
        shellName,
        propertyName ?? "");

    if (ret != 0)
    {
        throw new SAPException(
            operation: "AreaObj.AddByPoint",
            returnCode: ret,
            context: $"Shell '{shellName}', {pointNames.Count} vertices",
            userMessage: "Failed to create shell. Verify points form a valid quadrilateral or triangle.");
    }

    return new Shell
    {
        ApplicationId = Guid.NewGuid(),
        SAPObjectName = shellName,
        PropertyName = propertyName ?? "",
        PointNames = pointNames
    };
}
```

### Set Mesh Size

```csharp
public void SetMeshRefinement(
    cSapModel model,
    string shellName,
    double maxMeshSize_mm,
    bool autoMesh = true)
{
    int ret = model.AreaObj.SetMeshSize(shellName, maxMeshSize_mm, autoMesh ? 1 : 0);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "AreaObj.SetMeshSize",
            returnCode: ret,
            context: $"Shell '{shellName}', mesh size={maxMeshSize_mm} mm",
            userMessage: "Failed to set mesh size. Verify shell exists and mesh size is valid.");
    }
}

public double GetMeshSize(cSapModel model, string shellName)
{
    double meshSize = 0;

    int ret = model.AreaObj.GetMeshSize(shellName, ref meshSize);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "AreaObj.GetMeshSize",
            returnCode: ret,
            context: $"Shell: {shellName}");
    }

    return meshSize;
}
```

### Read All Shells

```csharp
public List<Shell> ReadAllShells(cSapModel model)
{
    var shells = new List<Shell>();

    int count = 0;
    string[] names = null;

    int ret = model.AreaObj.GetNameList(ref count, ref names);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "AreaObj.GetNameList",
            returnCode: ret);
    }

    for (int i = 0; i < count; i++)
    {
        try
        {
            var shell = ReadShellDetails(model, names[i]);
            shells.Add(shell);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: Failed to read shell {names[i]}: {ex.Message}");
        }
    }

    return shells;
}

private Shell ReadShellDetails(cSapModel model, string shellName)
{
    // Get vertices
    int numVertices = 0;
    string[] vertexNames = null;

    int ret = model.AreaObj.GetPoints(shellName, ref numVertices, ref vertexNames);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "AreaObj.GetPoints",
            returnCode: ret,
            context: $"Shell: {shellName}");
    }

    // Get property
    string propName = "";
    ret = model.AreaObj.GetProperty(shellName, ref propName);

    return new Shell
    {
        SAPObjectName = shellName,
        PointNames = vertexNames.ToList(),
        PropertyName = propName
    };
}
```

---

## Code Example: Create Rectangular Slab

```csharp
public Shell CreateRectangularSlab(
    cSapModel model,
    string storyLabel,
    double length_mm,
    double width_mm,
    double z_mm,
    double meshSize_mm = 1000.0)
{
    // Create four corners
    var (p1, p2, p3, p4) = CreateRectanglePoints(
        model, storyLabel, length_mm, width_mm, z_mm);

    // Create shell with quadrilateral
    var shell = CreateShellByPoints(
        model,
        new List<string> 
        {
            p1.SAPObjectName,
            p2.SAPObjectName,
            p4.SAPObjectName,
            p3.SAPObjectName
        },
        $"SH_SLAB_{storyLabel}_001",
        "SECTION_SLAB");

    // Set mesh size
    SetMeshRefinement(model, shell.SAPObjectName, meshSize_mm, autoMesh: true);

    return shell;
}

private (Point, Point, Point, Point) CreateRectanglePoints(
    cSapModel model,
    string story,
    double length_mm,
    double width_mm,
    double z_mm)
{
    var p1 = CreatePoint(model, 0, 0, z_mm, $"PT_{story}_SLAB_1", story);
    var p2 = CreatePoint(model, length_mm, 0, z_mm, $"PT_{story}_SLAB_2", story);
    var p3 = CreatePoint(model, length_mm, width_mm, z_mm, $"PT_{story}_SLAB_3", story);
    var p4 = CreatePoint(model, 0, width_mm, z_mm, $"PT_{story}_SLAB_4", story);

    return (p1, p2, p3, p4);
}
```

---

## Mesh Strategy

### General Guidelines

- **Slabs:** 1000-2000 mm mesh for typical buildings
- **Walls:** 500-1500 mm mesh
- **Complex geometry:** Finer mesh (500 mm or less)
- **Automatic meshing:** Enable for simplicity

### Check Mesh Quality

```csharp
public void VerifyMeshQuality(cSapModel model, string shellName)
{
    double meshSize = GetMeshSize(model, shellName);

    // Check aspect ratio, element distortion (via Database Tables if needed)
    // Log warnings if mesh is too coarse
    if (meshSize > 2500.0)
    {
        Debug.WriteLine($"Warning: Shell {shellName} mesh size ({meshSize} mm) may be too coarse");
    }
}
```

---

## Validation Checklist

✓ Shell names follow convention (SH_TYPE_INDEX)  
✓ At least 3 points (triangle) or 4 points (quad) defined  
✓ Points form valid non-self-intersecting polygon  
✓ Mesh size set explicitly (not SAP default)  
✓ Property assigned (membrane vs plate behavior)  
✓ Element local axes consistent with design intent  
✓ All return codes checked  

---

## Common Pitfalls

### ❌ Coincident Points
```csharp
// WRONG: Two vertices are the same
var vertices = new List<string> { "PT_1", "PT_2", "PT_2", "PT_4" };
model.AreaObj.AddByPoint(..., vertices.ToArray(), ...);
// Invalid shell!
```

**Fix:** Verify points are distinct.

### ❌ Non-Planar Shell
```csharp
// WRONG: Four points not in same plane (for quad shells)
var p1 = (0, 0, 0);
var p2 = (1000, 0, 0);
var p3 = (1000, 1000, 500);  // Out of plane!
var p4 = (0, 1000, 0);
```

**Fix:** Ensure points are coplanar.

### ❌ Self-Intersecting Geometry
```csharp
// WRONG: "Bowtie" shell (vertices not in correct order)
// Creates double-counted area
```

**Fix:** Specify vertices in consistent order (CCW or CW, not mixed).

### ❌ Missing Mesh Specification
```csharp
// WRONG: Using SAP's default mesh
model.AreaObj.AddByPoint(...); // No SetMeshSize called
// SAP may create very coarse or very fine mesh unpredictably
```

**Fix:** Always call SetMeshSize explicitly.

---

## Related Skills

- [Point Objects & Nodes](PointObjects.md) — Create vertices
- [Shell Properties](../03-properties/ShellProperties.md) — Material/thickness
- [Shell Results](../06-results/ShellResults.md) — Extract stress/strain
