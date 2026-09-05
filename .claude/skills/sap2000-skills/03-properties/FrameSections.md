# SAP2000 Frame Sections

**Category:** Properties  
**Priority:** CRITICAL  
**Last Updated:** 2026-09-05

---

## Purpose

Define and assign section properties (I-beams, W-shapes, tubes, custom) to frame elements.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `FrameProp.SetAutoSection(name, shape)` | int | Create standard section |
| `FrameProp.SetSectionDimensions(name, dims[])` | int | Set dimensions for shape |
| `FrameProp.GetSectionDimensions(name, ref dims[])` | int | Read dimensions |
| `FrameProp.SetMaterial(name, material)` | int | Assign material to section |
| `FrameProp.GetNameList(ref count, ref names)` | int | Get all sections |

### Section Shapes

```csharp
enum eSectionShape
{
    ISection = 1,      // I-beam
    Channel = 2,       // C-section
    Angle = 3,         // L-section
    Tee = 4,          // T-section
    Rectangular = 5,   // Rectangular solid
    Circle = 6,        // Circular solid
    Pipe = 7,         // Circular pipe
    Box = 8           // Rectangular tube
}
```

---

## Project Convention

**Naming:** `SEC_<Type>_<Size>`

Examples:
- `SEC_IBEAM_W36X300`
- `SEC_COLUMN_HSS10X10X0.5`
- `SEC_TUBE_RHS100X100X4`

**Dimensions:** Always mm in N-mm-C

---

## Workflow

### Create I-Beam Section

```csharp
public FrameSection CreateIBeamSection(
    cSapModel model,
    string sectionName,
    string materialName,
    double depthFlange_mm,
    double widthFlange_mm,
    double thickWeb_mm,
    double thickFlange_mm)
{
    // Create auto section (template)
    int ret = model.FrameProp.SetAutoSection(
        sectionName,
        (int)eSectionShape.ISection);

    if (ret != 0)
        throw new SAPException(...);

    // Set dimensions
    double[] dims = new double[12];
    dims[0] = depthFlange_mm;
    dims[1] = widthFlange_mm;
    dims[2] = thickWeb_mm;
    dims[3] = thickFlange_mm;

    ret = model.FrameProp.SetSectionDimensions(sectionName, dims);

    if (ret != 0)
        throw new SAPException(...);

    // Assign material
    ret = model.FrameProp.SetMaterial(sectionName, materialName);

    if (ret != 0)
        throw new SAPException(...);

    return new FrameSection
    {
        ApplicationId = Guid.NewGuid(),
        SAPObjectName = sectionName,
        MaterialName = materialName,
        SectionType = "ISection",
        Dimensions = new { Depth = depthFlange_mm, Width = widthFlange_mm }
    };
}
```

### Create Rectangular Tube

```csharp
public FrameSection CreateRectangularTube(
    cSapModel model,
    string sectionName,
    string materialName,
    double height_mm,
    double width_mm,
    double thickness_mm)
{
    int ret = model.FrameProp.SetAutoSection(
        sectionName,
        (int)eSectionShape.Box);

    if (ret != 0)
        throw new SAPException(...);

    double[] dims = new double[12];
    dims[0] = height_mm;
    dims[1] = width_mm;
    dims[2] = thickness_mm;
    dims[3] = thickness_mm;

    ret = model.FrameProp.SetSectionDimensions(sectionName, dims);

    if (ret != 0)
        throw new SAPException(...);

    ret = model.FrameProp.SetMaterial(sectionName, materialName);

    if (ret != 0)
        throw new SAPException(...);

    return new FrameSection
    {
        SAPObjectName = sectionName,
        MaterialName = materialName,
        SectionType = "Box"
    };
}
```

---

## Code Example: Standard Sections Library

```csharp
public class StandardSections
{
    public static void InitializeStandardSections(cSapModel model)
    {
        // Steel beams
        CreateIBeamSection(model, "SEC_IBEAM_W24X68", "MAT_STEEL_A992",
            610, 230, 8.0, 12.0);
        CreateIBeamSection(model, "SEC_IBEAM_W36X300", "MAT_STEEL_A992",
            910, 300, 10.0, 16.0);

        // Steel columns
        CreateRectangularTube(model, "SEC_HSS10X10X0.5", "MAT_STEEL_A992",
            254, 254, 12.7);

        // Concrete sections (placeholder)
        CreateRectangularSection(model, "SEC_CONC_400X800", "MAT_CONCRETE_25",
            400, 800);
    }
}
```

---

## Validation Checklist

✓ Section name follows convention (SEC_TYPE_SIZE)  
✓ Material assigned before use  
✓ All dimensions in mm  
✓ Dimensions match physical reality  
✓ Section shape matches dimension count  
✓ All return codes checked  
✓ Section used in at least one frame  

---

## Common Pitfalls

### ❌ Dimensions in Wrong Units
```csharp
// WRONG: Dimensions in meters
CreateIBeamSection(model, "SEC_1", "MAT_1",
    0.610,  // 610 mm as 0.61 m
    0.230, ...);
// SAP interprets as 0.61 mm (tiny!)
```

**Fix:** Always use mm.

### ❌ Missing Material Assignment
```csharp
// WRONG: Section created without material
model.FrameProp.SetAutoSection(name, shape);
// No SetMaterial call!
```

**Fix:** Always assign material.

### ❌ No Dimension Validation
```csharp
// WRONG: Unrealistic dimensions
CreateIBeamSection(model, "SEC_1", "MAT_1",
    10, 5, 0.1, 0.05);  // Tiny web/flange
// Creates invalid section
```

**Fix:** Validate dimensions before creating.

---

## External References

- **SAP2000 Help:** Frame sections, auto sections
- **AISC Manual:** Section properties, W-shapes, HSS

---

## Related Skills

- [Materials](Materials.md) — Assign to sections
- [Frame Objects](../02-objects/FrameObjects.md) — Use sections
