# SAP2000 Applied Loads

**Category:** Loads  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Apply point forces, moments, and distributed loads to frame and shell elements.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `PointObj.SetLoadForce(name, patternName, fx, fy, fz)` | int | Apply point force |
| `PointObj.SetLoadMass(name, patternName, mx, my, mz)` | int | Apply point mass |
| `FrameObj.SetLoadDistributed(name, patternName, loadType, val1, val2)` | int | Distributed load |
| `AreaObj.SetLoadUniform(name, patternName, pressure)` | int | Uniform pressure |

---

## Project Convention

**Load unit:** Always N (or N/mm for distributed)

---

## Workflow

### Apply Point Force

```csharp
public void ApplyPointForce(
    cSapModel model,
    string pointName,
    string loadPattern,
    double fx_N, double fy_N, double fz_N)
{
    int ret = model.PointObj.SetLoadForce(
        pointName,
        loadPattern,
        fx_N, fy_N, fz_N);

    if (ret != 0)
        throw new SAPException(...);
}
```

### Apply Distributed Load on Beam

```csharp
public void ApplyDistributedLoadOnBeam(
    cSapModel model,
    string frameName,
    string loadPattern,
    double loadPerLength_N_per_mm)  // N/mm
{
    int ret = model.FrameObj.SetLoadDistributed(
        frameName,
        loadPattern,
        0,  // Load type: uniform
        loadPerLength_N_per_mm,
        loadPerLength_N_per_mm);

    if (ret != 0)
        throw new SAPException(...);
}
```

---

## Validation Checklist

✓ Loads in correct units (N, N/mm, Pa)  
✓ Load pattern exists  
✓ Element exists  
✓ All return codes checked  

---

## Related Skills

- [Load Patterns & Cases](LoadPatterns.md) — Define patterns first
- [Load Combinations](LoadCombinations.md) — Combine applied loads
