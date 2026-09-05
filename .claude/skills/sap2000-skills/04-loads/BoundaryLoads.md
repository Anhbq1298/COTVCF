# SAP2000 Boundary Loads

**Category:** Loads  
**Priority:** MEDIUM  
**Last Updated:** 2026-09-05

---

## Purpose

Apply support settlements, imposed displacements, and temperature loads.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `PointObj.SetLoadDisplacement(name, patternName, dx, dy, dz)` | int | Imposed displacement |
| `FrameObj.SetLoadTemperature(name, patternName, tempType, val1)` | int | Temperature load |
| `PointObj.SetConstraint(name, cxtype, ...)` | int | Support restraint |

---

## Workflow

### Apply Settlement

```csharp
public void ApplySettlement(
    cSapModel model,
    string supportPoint,
    string loadPattern,
    double settlementZ_mm)
{
    int ret = model.PointObj.SetLoadDisplacement(
        supportPoint,
        loadPattern,
        0, 0, settlementZ_mm);  // Only Z displacement

    if (ret != 0)
        throw new SAPException(...);
}
```

---

## Validation Checklist

✓ Load pattern exists  
✓ Boundary loads in mm (displacement)  
✓ All return codes checked  

---

## Related Skills

- [Load Patterns & Cases](LoadPatterns.md) — Define patterns
