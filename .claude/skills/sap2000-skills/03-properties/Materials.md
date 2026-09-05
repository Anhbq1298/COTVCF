# SAP2000 Materials

**Category:** Properties  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Define and assign material properties (steel, concrete, custom) to structural elements.

---

## API Reference

### Key Methods

| Method | Returns | Purpose |
|--------|---------|---------|
| `MatProp.SetMaterial(name, matType)` | int | Create/modify material |
| `MatProp.GetMaterial(name, ref matType)` | int | Read material type |
| `MatProp.SetMaterialMech(name, e, nu, a, density)` | int | Set mechanical properties |
| `MatProp.GetMaterialMech(name, ref e, ref nu, ref a, ref density)` | int | Read mechanical properties |
| `MatProp.GetNameList(ref count, ref names)` | int | Get all material names |

### Material Types

```csharp
enum eMatType
{
    Steel = 1,     // Structural steel
    Concrete = 2,  // Reinforced concrete
    Aluminum = 3,  // Aluminum
    ColdFormed = 4, // Cold-formed steel
    Rebar = 5,     // Rebar steel
    Tendon = 6,    // Prestressing tendon
    Other = 99     // Custom material
}
```

---

## Project Convention

**Naming:** `MAT_<Type>_<Grade>`

Examples:
- `MAT_STEEL_A992`
- `MAT_CONCRETE_3000PSI`
- `MAT_ALUMINUM_6061T6`

**Units:** Always N-mm-C
- E (modulus): N/mm² (MPa)
- Density: N/mm³ (or kg/mm³ / 1000000)
- Coefficient of thermal expansion: 1/°C

---

## Workflow

### Create Steel Material

```csharp
public Material CreateSteelMaterial(
    cSapModel model,
    string materialName,
    double yieldStrength_MPa = 250.0)  // A992 grade
{
    // Material mechanical properties for steel
    double e_MPa = 200000;              // Young's modulus
    double poisson = 0.3;               // Poisson's ratio
    double alpha = 0.000012;            // Thermal expansion (1/°C)
    double density_kgm3 = 7850;         // Steel density
    double density_Nmm3 = density_kgm3 / 1000000.0;

    int ret = model.MatProp.SetMaterial(
        materialName,
        (int)eMatType.Steel);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "MatProp.SetMaterial",
            returnCode: ret,
            context: $"Material '{materialName}', type=Steel");
    }

    ret = model.MatProp.SetMaterialMech(
        materialName,
        e_MPa,
        poisson,
        alpha,
        density_Nmm3);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "MatProp.SetMaterialMech",
            returnCode: ret,
            context: $"Material '{materialName}'");
    }

    return new Material
    {
        ApplicationId = Guid.NewGuid(),
        SAPObjectName = materialName,
        MaterialType = "Steel",
        YieldStrength_MPa = yieldStrength_MPa,
        Modulus_MPa = e_MPa,
        Density_kgm3 = density_kgm3
    };
}
```

### Create Concrete Material

```csharp
public Material CreateConcreteMaterial(
    cSapModel model,
    string materialName,
    double compressiveStrength_MPa = 25.0)  // f'c = 25 MPa
{
    // Concrete mechanical properties
    double e_MPa = 4700 * Math.Sqrt(compressiveStrength_MPa); // Approximate from ACI
    double poisson = 0.2;
    double alpha = 0.00001;
    double density_kgm3 = 2400;
    double density_Nmm3 = density_kgm3 / 1000000.0;

    int ret = model.MatProp.SetMaterial(
        materialName,
        (int)eMatType.Concrete);

    if (ret != 0)
        throw new SAPException(...);

    ret = model.MatProp.SetMaterialMech(
        materialName,
        e_MPa,
        poisson,
        alpha,
        density_Nmm3);

    if (ret != 0)
        throw new SAPException(...);

    return new Material
    {
        SAPObjectName = materialName,
        MaterialType = "Concrete",
        CompressiveStrength_MPa = compressiveStrength_MPa,
        Modulus_MPa = e_MPa,
        Density_kgm3 = density_kgm3
    };
}
```

### Assign Material to Section

```csharp
public void AssignMaterialToSection(
    cSapModel model,
    string sectionName,
    string materialName)
{
    int ret = model.FrameProp.SetMaterial(sectionName, materialName);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "FrameProp.SetMaterial",
            returnCode: ret,
            context: $"Section '{sectionName}', Material '{materialName}'");
    }
}
```

---

## Code Example: Standard Material Library

```csharp
public class StandardMaterials
{
    public static void InitializeStandardMaterials(cSapModel model)
    {
        // Steel materials
        CreateSteelMaterial(model, "MAT_STEEL_A992", 250.0);
        CreateSteelMaterial(model, "MAT_STEEL_A36", 235.0);

        // Concrete materials
        CreateConcreteMaterial(model, "MAT_CONCRETE_25", 25.0);   // 25 MPa
        CreateConcreteMaterial(model, "MAT_CONCRETE_30", 30.0);   // 30 MPa
    }
}
```

---

## Validation Checklist

✓ Material name follows convention (MAT_TYPE_GRADE)  
✓ Material type correctly specified (Steel, Concrete, etc.)  
✓ Modulus in N/mm² (MPa)  
✓ Density in correct units (kg/m³ or N/mm³)  
✓ Poisson's ratio in valid range (0.0-0.5)  
✓ Thermal expansion coefficient appropriate  
✓ All return codes checked  
✓ Material assigned to section before analysis  

---

## Common Pitfalls

### ❌ Wrong Units for Modulus
```csharp
// WRONG: Using Pa instead of MPa
double e = 200_000_000_000;  // 200 GPa in Pa
model.MatProp.SetMaterialMech(name, e, ...);
// SAP interprets as 200 billion N/mm² (wrong!)
```

**Fix:** Use MPa for N-mm-C system.
```csharp
double e = 200_000;  // 200,000 MPa = 200 GPa
```

### ❌ Density Units Confusion
```csharp
// WRONG: Density in kg/m³ (not converted)
double density = 7850;  // kg/m³
model.MatProp.SetMaterialMech(name, e, nu, alpha, density);
// Too large!
```

**Fix:** Convert to N/mm³.
```csharp
double density_kgm3 = 7850;
double density_Nmm3 = density_kgm3 / 1_000_000;
model.MatProp.SetMaterialMech(name, e, nu, alpha, density_Nmm3);
```

### ❌ Using Default SAP Materials
```csharp
// WRONG: No custom material defined
model.FrameProp.SetMaterial(section, "");
// SAP assigns default (unpredictable)
```

**Fix:** Always assign specific material.

---

## External References

- **SAP2000 Help:** Materials, material properties
- **ACI 318:** Concrete strength/modulus relationships
- **AISC:** Steel material properties, grades

---

## Related Skills

- [Frame Sections](FrameSections.md) — Assign materials to sections
- [Shell Properties](ShellProperties.md) — Material for shells
