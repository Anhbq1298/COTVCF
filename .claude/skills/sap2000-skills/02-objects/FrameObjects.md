# SAP2000 Frame Objects

**Category:** Objects  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Create and manage frame objects (beams, columns, braces) that carry axial and bending forces.

---

## API Reference

### Key Methods

| Method | Returns | Purpose |
|--------|---------|---------|
| `FrameObj.AddFrame(x1, y1, z1, x2, y2, z2, name, propName)` | int | Create frame by coordinates |
| `FrameObj.AddFrameByPoint(p1, p2, name, propName)` | int | Create frame by point names |
| `FrameObj.GetLocalAxes(name, ref a, ref b, ref c)` | int | Get local axes orientation |
| `FrameObj.SetLocalAxes(name, angle)` | int | Rotate local axes |
| `FrameObj.GetNameList(ref count, ref names)` | int | Get all frame names |
| `FrameObj.SetName(oldName, newName)` | int | Rename frame |

---

## Project Convention

**Naming:** `FR_<Type>_<Index>` (FR_BEAM_001, FR_COL_L1_C101)

**Local Axes:** Document intended 2-axis orientation at creation

**Starts/End:** Always specify direction (matters for local axes and releases)

---

## Workflow

### Create Frame by Points

```csharp
public Frame CreateFrameByPoints(
    cSapModel model,
    string startPointName,
    string endPointName,
    string frameName,
    string sectionName = "")
{
    // Get start and end coordinates for local axis calculation
    double x1 = 0, y1 = 0, z1 = 0;
    double x2 = 0, y2 = 0, z2 = 0;

    int ret = model.PointObj.GetCoordinates(startPointName, ref x1, ref y1, ref z1);
    if (ret != 0) throw new SAPException(...);

    ret = model.PointObj.GetCoordinates(endPointName, ref x2, ref y2, ref z2);
    if (ret != 0) throw new SAPException(...);

    // Create frame
    ret = model.FrameObj.AddFrameByPoint(
        startPointName,
        endPointName,
        frameName,
        sectionName ?? "");

    if (ret != 0)
    {
        throw new SAPException(
            operation: "FrameObj.AddFrameByPoint",
            returnCode: ret,
            context: $"Frame '{frameName}': {startPointName} → {endPointName}",
            userMessage: "Failed to create frame. Verify point names exist and are not coincident.");
    }

    // Calculate local 1-axis direction
    var local1 = Normalize((x2 - x1, y2 - y1, z2 - z1));

    return new Frame
    {
        ApplicationId = Guid.NewGuid(),
        SAPObjectName = frameName,
        StartPointName = startPointName,
        EndPointName = endPointName,
        SectionName = sectionName ?? "",
        Local1Axis = local1,
        Local2AxisDesign = DeterminedFromGeometry(x1, y1, z1, x2, y2, z2)
    };
}
```

### Get/Set Local Axes

```csharp
public void RotateLocalAxes(
    cSapModel model,
    string frameName,
    double rotationDegrees)
{
    int ret = model.FrameObj.SetLocalAxes(frameName, rotationDegrees);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "FrameObj.SetLocalAxes",
            returnCode: ret,
            context: $"Frame '{frameName}', rotation={rotationDegrees}°",
            userMessage: "Failed to rotate local axes. Verify frame exists.");
    }
}

public (double angle1, double angle2, double angle3) GetLocalAxes(
    cSapModel model,
    string frameName)
{
    double a = 0, b = 0, c = 0;

    int ret = model.FrameObj.GetLocalAxes(frameName, ref a, ref b, ref c);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "FrameObj.GetLocalAxes",
            returnCode: ret,
            context: $"Frame: {frameName}");
    }

    return (a, b, c);
}
```

### Read All Frames

```csharp
public List<Frame> ReadAllFrames(cSapModel model)
{
    var frames = new List<Frame>();

    int count = 0;
    string[] names = null;

    int ret = model.FrameObj.GetNameList(ref count, ref names);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "FrameObj.GetNameList",
            returnCode: ret);
    }

    for (int i = 0; i < count; i++)
    {
        try
        {
            var frame = ReadFrameDetails(model, names[i]);
            frames.Add(frame);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Warning: Failed to read frame {names[i]}: {ex.Message}");
        }
    }

    return frames;
}

private Frame ReadFrameDetails(cSapModel model, string frameName)
{
    string startPoint = "", endPoint = "";
    int ret = model.FrameObj.GetPoints(frameName, ref startPoint, ref endPoint);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "FrameObj.GetPoints",
            returnCode: ret,
            context: $"Frame: {frameName}");
    }

    // Get section
    string sectionName = "";
    ret = model.FrameObj.GetSection(frameName, ref sectionName);

    return new Frame
    {
        SAPObjectName = frameName,
        StartPointName = startPoint,
        EndPointName = endPoint,
        SectionName = sectionName
    };
}
```

---

## Code Example: Create Portal Frame

```csharp
public void CreateSimplePortalFrame(
    cSapModel model,
    string storyLabel,
    double spanLength_mm,
    double height_mm)
{
    // Create four corner points
    var (p1, p2, p3, p4) = CreatePortalPoints(
        model, storyLabel, spanLength_mm, height_mm);

    // Create columns
    CreateFrameByPoints(model, p1.SAPObjectName, p3.SAPObjectName, 
                       $"FR_COL_{storyLabel}_LEFT", "SECTION_COLUMN");

    CreateFrameByPoints(model, p2.SAPObjectName, p4.SAPObjectName,
                       $"FR_COL_{storyLabel}_RIGHT", "SECTION_COLUMN");

    // Create beam
    CreateFrameByPoints(model, p3.SAPObjectName, p4.SAPObjectName,
                       $"FR_BEAM_{storyLabel}", "SECTION_BEAM");
}

private (Point, Point, Point, Point) CreatePortalPoints(
    cSapModel model,
    string story,
    double span_mm,
    double height_mm)
{
    var p1 = CreatePoint(model, 0, 0, 0, $"PT_{story}_1", story);
    var p2 = CreatePoint(model, span_mm, 0, 0, $"PT_{story}_2", story);
    var p3 = CreatePoint(model, 0, 0, height_mm, $"PT_{story}_3", story);
    var p4 = CreatePoint(model, span_mm, 0, height_mm, $"PT_{story}_4", story);

    return (p1, p2, p3, p4);
}
```

---

## Validation Checklist

✓ Frame names follow convention (FR_TYPE_INDEX)  
✓ Start and end points exist  
✓ Local axes documented (2-axis design intent)  
✓ Section assigned before analysis  
✓ Releases defined if applicable  
✓ Offsets applied if needed  
✓ Axis angle defaults to 0° (standard orientation)  
✓ All return codes checked  

---

## Common Pitfalls

### ❌ Coincident Start/End Points
```csharp
// WRONG: Creates frame with zero length
model.FrameObj.AddFrameByPoint("PT_1", "PT_1", "FR_1", "");
```

**Fix:** Verify points are distinct.

```csharp
if (startPoint == endPoint)
    throw new InvalidOperationException("Start and end points must be different");
```

### ❌ Ignoring Local Axes
```csharp
// WRONG: Assumes default 2-axis orientation
var moment_M2 = results.M2; // But local axis may be rotated!
```

**Fix:** Document and verify local axis orientation.

### ❌ Section Not Assigned
```csharp
// WRONG: Creates frame without section
model.FrameObj.AddFrameByPoint(p1, p2, "FR_1", "");
// SAP may assign default section!
```

**Fix:** Always specify section name.

```csharp
if (string.IsNullOrEmpty(sectionName))
    throw new ArgumentException("Section name required");
```

---

## Related Skills

- [Point Objects & Nodes](PointObjects.md) — Start/end points
- [Frame Sections](../03-properties/FrameSections.md) — Section properties
- [Releases & Offsets](../03-properties/ReleasesOffsets.md) — End releases
- [Frame Results](../06-results/FrameResults.md) — Extract forces/moments
