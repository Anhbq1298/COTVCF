# SAP2000 Units & Coordinates

**Category:** Foundation  
**Priority:** CRITICAL  
**Last Updated:** 2026-09-05

---

## Purpose

Establish consistent unit system and coordinate convention throughout application.

**Golden Rule:** 
> Never rely on hidden unit or coordinate assumptions. Always be explicit.

---

## Unit System

### Project Standard: N-mm-C
**Force:** Newtons (N)  
**Length:** Millimeters (mm)  
**Temperature:** Celsius (C)

**Rationale:**
- mm for precision (structural detail)
- N for SI consistency
- Avoids large/small numbers that cause floating-point errors
- Standard in CSI ecosystem

### SAP2000 Unit Enum
```csharp
enum eUnits
{
    lb_in_F = 1,    // Pounds, inches, Fahrenheit
    lb_ft_F = 2,    // Pounds, feet, Fahrenheit
    kip_in_F = 3,   // Kips, inches
    kip_ft_F = 4,   // Kips, feet
    kN_mm_C = 5,    // Kilonewtons, mm
    kN_m_C = 6,     // Kilonewtons, meters
    kgf_mm_C = 7,   // Kilogram-force, mm
    kgf_m_C = 8,    // Kilogram-force, meters
    N_mm_C = 9,     // Newtons, mm ← PROJECT STANDARD
    N_m_C = 10,     // Newtons, meters
    Tonf_mm_C = 11, // Metric tons, mm
    Tonf_m_C = 12   // Metric tons, meters
}
```

### At Connection Time
```csharp
// After obtaining cSapModel
int ret = model.SetPresentUnits((int)eUnits.N_mm_C);
if (ret != 0)
{
    throw new Exception($"Failed to set units (code: {ret})");
}

// Verify what SAP is using
int actualUnits = 0;
model.GetPresentUnits(ref actualUnits);
// actualUnits should now == (int)eUnits.N_mm_C
```

### Unit Conversion Reference
| From | To N-mm-C | Formula |
|------|-----------|---------|
| kN-m-C | × 1,000,000 | multiply by 10^6 |
| N-m-C | × 1,000 | multiply by 10^3 |
| kip-in-F | × 4.448 | 1 kip = 4.448 kN |
| kip-ft-F | × 67.33 | 1 kip-ft = 67.33 kN-m |
| Tonf-m-C | × 9.807 | 1 metric ton = 9.807 kN |

---

## Coordinate Systems

### Global XYZ (SAP2000 World)
- **X:** Horizontal (typically left-right)
- **Y:** Horizontal (typically front-back)
- **Z:** Vertical (up = positive)

**Convention:** Right-hand rule

```
      Z (UP)
      ↑
      |
      |
------+-----→ X
     /
    /
   Y
```

### Local Axes (Frame Elements)
Each frame element has local 1-2-3 axes.

- **1-axis:** Along element (from start to end node)
- **2-axis:** Perpendicular to 1 (in element's vertical plane if horizontal)
- **3-axis:** Perpendicular to 1-2 (right-hand rule)

**Critical:** Local axes determine:
- Moment signs (M2, M3)
- Shear directions (V2, V3)
- Section orientation (I-axis alignment)
- Release directions

### Coordinate Transformation Workflow

```
BIM Coordinates (e.g., Revit)
        ↓
[Transform to SAP Global XYZ]
        ↓
SAP2000 Point/Frame Objects
        ↓
[Extract results in local 1-2-3]
        ↓
[Report in application units]
```

### Example: Point Creation

```csharp
public void CreatePointInGlobalCoordinates(
    cSapModel model,
    string pointName,
    double xGlobal_mm,
    double yGlobal_mm,
    double zGlobal_mm)
{
    int ret = model.PointObj.AddPoint(
        xGlobal_mm,
        yGlobal_mm,
        zGlobal_mm,
        pointName,      // Name
        ""              // Group
    );

    if (ret != 0)
    {
        throw new Exception(
            $"Failed to create point {pointName} at ({xGlobal_mm}, {yGlobal_mm}, {zGlobal_mm}) " +
            $"in global N-mm-C coordinates (code: {ret})");
    }
}
```

---

## Local Axis Orientation (Critical!)

### Frame Element Local Axes

**How SAP assigns local axes:**

1. **Start and End Nodes:** Point-in + Point-out (or Point-1 and Point-j)
2. **1-axis:** From start to end node (element longitudinal)
3. **2-3 axes:** Perpendicular to 1

**For a horizontal beam:**
```
Start Node (i)    →    End Node (j)
    ●────────────────────●
    
1-axis: Along beam (i to j)
2-axis: In vertical plane (typically up)
3-axis: Perpendicular to vertical plane (right-hand rule)
```

**For a vertical column:**
```
    Top (j)
      ●
      |
      |
      ●
    Base (i)

1-axis: Vertical (i to j)
2-axis: Horizontal (assigned by SAP based on global Z)
3-axis: Horizontal (perpendicular to 2)
```

### Modifying Local Axes

SAP allows rotating local axes via **angle offset**.

```csharp
public void RotateLocalAxes(
    cSapModel model,
    string frameName,
    double angleDegreesAbout1Axis)
{
    int ret = model.FrameObj.SetLocalAxes(
        frameName,
        angleDegreesAbout1Axis  // Rotation about local 1-axis
    );

    if (ret != 0)
    {
        throw new Exception(
            $"Failed to rotate local axes for frame {frameName} " +
            $"by {angleDegreesAbout1Axis}° (code: {ret})");
    }
}
```

---

## Project Conventions

### Coordinate Storage
**Application-level model:** Always use N-mm-C  
**Database:** Store in N-mm-C unless explicitly documented  
**API calls to SAP:** Use N-mm-C (always)  
**Results from SAP:** Interpret as N-mm-C (verify via GetPresentUnits)  

### Point Names
Format: `PT_<Index>` or `PT_<Level>_<X>_<Y>`

Example:
```
PT_001          (Generic point)
PT_L1_0_0_0     (Level L1, at origin)
PT_L2_5000_2500 (Level L2, X=5000, Y=2500)
```

### Frame Names
Format: `FR_<Type>_<Index>` or `FR_<Type>_<Level>_<ID>`

Example:
```
FR_BEAM_001
FR_COL_L1_C101
FR_BRACE_001
```

### Local Axis Documentation
**Rule:** Whenever creating a frame, document intended local 2-3 orientation.

```csharp
public class FrameCreationContext
{
    public string FrameName { get; set; }
    public (double x1, double y1, double z1) StartNode { get; set; }
    public (double x2, double y2, double z2) EndNode { get; set; }
    public string LocalAxisDesign { get; set; }  // "Horizontal beam, 2-axis up"
    
    // If not standard, rotation applied:
    public double LocalAxisRotationDegrees { get; set; } = 0.0;
}
```

---

## Validation Checklist

✓ Units set to N-mm-C at connection  
✓ All coordinates interpreted as mm (not m or in)  
✓ All forces interpreted as N (not kN or kip)  
✓ All moments interpreted as N-mm (not N-m or kip-in)  
✓ Local axis orientations documented at creation  
✓ No hidden coordinate assumptions in code  
✓ Coordinate transformations explicit and testable  
✓ When extracting results: verify axis direction (global vs local)  
✓ Moment/shear signs match local axis convention  
✓ Point data always in global XYZ  
✓ Frame results always in local 1-2-3 (unless explicitly transformed)  

---

## Common Pitfalls

### ❌ Assuming Units After Model Changes
```csharp
// WRONG: Units might have changed
double x = 500; // Is this 500 mm or 500 m?
model.PointObj.AddPoint(x, 0, 0, "PT_1", "");
```

**Fix:** Always set units after opening/creating model.

### ❌ Mixing Coordinate Systems
```csharp
// WRONG: Mixing BIM (meters) and SAP (mm)
double bimX_m = 5.0;
model.PointObj.AddPoint(bimX_m, 0, 0, "PT_1", ""); // Treated as 5 mm, not 5000 mm!
```

**Fix:** Transform explicitly.

```csharp
const double metersToMM = 1000.0;
double sapX_mm = bimX_m * metersToMM;
model.PointObj.AddPoint(sapX_mm, 0, 0, "PT_1", "");
```

### ❌ Ignoring Local Axes
```csharp
// WRONG: Assumes moment is M2
var m2 = resultData["M2"]; // But local axis might be rotated!
```

**Fix:** Document and verify local axis orientation at creation.

### ❌ Floating-Point Precision
```csharp
// RISKY: Large numbers in N_m_C
double moment_Nm = 1000000000; // 1 GN-m in N-m (too large, loses precision)
```

**Fix:** Use N-mm or kN-m (avoid huge numbers).

```csharp
double moment_Nmm = 1000000000 * 1000; // Better: smaller mantissa relative to exponent
```

---

## External References

- **SAP2000 Help:** Search "Units", "Local axes", "Coordinate system"
- **CSI Documentation:** https://wiki.csiamerica.com/display/etabs/Units
- **Right-Hand Rule:** Standard in FEM (verify orientation in SAP2000 GUI)

---

## Related Skills

- [Connection & Initialization](Connection.md) — Set units here
- [Error Handling & Return Codes](ErrorHandling.md) — Handle unit-setting errors
- [Point Objects & Nodes](../02-objects/PointObjects.md) — Create points in global XYZ
- [Frame Objects](../02-objects/FrameObjects.md) — Create frames with local axes
