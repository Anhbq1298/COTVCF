# SAP2000 Constraints & Diaphragms

**Category:** Properties  
**Priority:** MEDIUM  
**Last Updated:** 2026-09-05

---

## Purpose

Create constraint properties that couple multiple points (diaphragm, rigid, semi-rigid).

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `ConstraintObj.AddConstraint(name, numPts, points[], dof[])` | int | Create constraint |
| `ConstraintObj.SetDiaphragm(name, diaphragmType)` | int | Set diaphragm behavior |
| `ConstraintObj.GetNameList(ref count, ref names)` | int | Get all constraints |

---

## Project Convention

**Naming:** `CONSTR_<Type>_<Story>` or `GRP_DIAPHRAGM_<Story>`

---

## Workflow

### Create Diaphragm Constraint

```csharp
public Constraint CreateDiaphragmConstraint(
    cSapModel model,
    List<string> constrainedPointNames,
    string constraintName)
{
    string[] points = constrainedPointNames.ToArray();

    // DOF: 1=couple, 0=free
    // Order: DX, DY, DZ, RX, RY, RZ
    int ret = model.ConstraintObj.AddConstraint(
        constraintName,
        points.Length,
        points,
        1, 1, 0, 0, 0, 1);  // Couple X, Y, RZ only

    if (ret != 0)
        throw new SAPException(...);

    return new Constraint
    {
        SAPObjectName = constraintName,
        ConstrainedPoints = constrainedPointNames
    };
}
```

---

## Validation Checklist

✓ Constraint name follows convention  
✓ All points valid  
✓ DOF settings match design intent  
✓ No circular dependencies  
✓ All return codes checked  

---

## Related Skills

- [Spring/Link Objects](../02-objects/SpringLinkObjects.md) — Alternative to constraints
