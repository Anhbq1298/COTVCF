# SAP2000 Shell Properties

**Category:** Properties  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Define shell/area element properties (thickness, orthotropic behavior, reinforcement).

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `ShellProp.SetShellType(name, type)` | int | Set membrane or plate |
| `ShellProp.SetThickness(name, thickness)` | int | Set uniform thickness |
| `ShellProp.SetMaterial(name, material)` | int | Assign material |
| `ShellProp.GetNameList(ref count, ref names)` | int | Get all shell properties |

### Shell Types

```csharp
enum eShellType
{
    ShellThin = 1,      // Thin shell (Kirchhoff)
    ShellThick = 2,     // Thick shell (Mindlin)
    PlaneStress = 3,    // Plane stress (2D)
    PlaneStrain = 4,    // Plane strain (2D)
    Axisymmetric = 5    // Axisymmetric
}
```

---

## Project Convention

**Naming:** `PROP_SH_<Type>_<Thickness>`

Examples:
- `PROP_SH_SLAB_200`
- `PROP_SH_WALL_250`
- `PROP_SH_CORE_300`

**Thickness:** Always mm in N-mm-C

---

## Workflow

### Create Slab Property

```csharp
public ShellProperty CreateSlabProperty(
    cSapModel model,
    string propertyName,
    string materialName,
    double thickness_mm)
{
    int ret = model.ShellProp.SetShellType(
        propertyName,
        (int)eShellType.ShellThin);

    if (ret != 0)
        throw new SAPException(...);

    ret = model.ShellProp.SetThickness(propertyName, thickness_mm);

    if (ret != 0)
        throw new SAPException(...);

    ret = model.ShellProp.SetMaterial(propertyName, materialName);

    if (ret != 0)
        throw new SAPException(...);

    return new ShellProperty
    {
        ApplicationId = Guid.NewGuid(),
        SAPObjectName = propertyName,
        MaterialName = materialName,
        Thickness_mm = thickness_mm,
        ShellType = "Thin"
    };
}
```

---

## Code Example: Standard Shell Properties

```csharp
public void InitializeStandardShellProperties(cSapModel model)
{
    // Slabs
    CreateSlabProperty(model, "PROP_SH_SLAB_150", "MAT_CONCRETE_25", 150);
    CreateSlabProperty(model, "PROP_SH_SLAB_200", "MAT_CONCRETE_25", 200);

    // Walls
    CreateSlabProperty(model, "PROP_SH_WALL_250", "MAT_CONCRETE_25", 250);
    CreateSlabProperty(model, "PROP_SH_WALL_300", "MAT_CONCRETE_25", 300);
}
```

---

## Validation Checklist

✓ Property name follows convention  
✓ Thickness in mm  
✓ Material assigned  
✓ Shell type appropriate (thin vs thick)  
✓ All return codes checked  

---

## Related Skills

- [Materials](Materials.md) — Material assignment
- [Shell Objects](../02-objects/ShellObjects.md) — Use shell properties
