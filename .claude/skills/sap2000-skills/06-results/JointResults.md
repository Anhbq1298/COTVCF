# SAP2000 Joint Results

**Category:** Results  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Extract joint/point results (reactions, displacements, base shear, story drift).

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `Results.JointReact(name, caseType, caseNames[], fx, fy, fz, mx, my, mz)` | int | Get support reactions |
| `Results.JointDispl(name, caseType, caseNames[], dx, dy, dz, rx, ry, rz)` | int | Get displacements |

---

## Workflow

### Extract Support Reaction

```csharp
public void GetSupportReaction(
    cSapModel model,
    string supportPointName,
    string loadCase)
{
    double fx = 0, fy = 0, fz = 0, mx = 0, my = 0, mz = 0;

    int ret = model.Results.JointReact(
        supportPointName,
        eItemTypeElm.Point,
        new[] { loadCase },
        ref fx, ref fy, ref fz, ref mx, ref my, ref mz);

    if (ret != 0)
        throw new SAPException(...);

    Debug.WriteLine($"Support {supportPointName}: Fz = {fz} N");
}
```

### Extract Displacement

```csharp
public void GetDisplacement(
    cSapModel model,
    string pointName,
    string loadCase)
{
    double dx = 0, dy = 0, dz = 0, rx = 0, ry = 0, rz = 0;

    int ret = model.Results.JointDispl(
        pointName,
        eItemTypeElm.Point,
        new[] { loadCase },
        ref dx, ref dy, ref dz, ref rx, ref ry, ref rz);

    if (ret != 0)
        throw new SAPException(...);

    Debug.WriteLine($"Point {pointName}: Dz = {dz} mm");
}
```

---

## Validation Checklist

✓ Load case exists  
✓ Analysis complete  
✓ Reactions at supports  
✓ Displacements in mm  

---

## Related Skills

- [Point Objects](../02-objects/PointObjects.md) — Query points
- [Linear Static Analysis](../05-analysis/LinearStatic.md) — Generate results
