# SAP2000 Shell Results

**Category:** Results  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Extract shell element results (membrane stresses, bending stresses, principal stresses).

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `Results.AreaStress(name, caseType, caseNames[], s11, s22, s12, smax, smin)` | int | Get shell stresses |

---

## Workflow

### Extract Shell Stress

```csharp
public void GetShellStress(
    cSapModel model,
    string shellName,
    string loadCase)
{
    double s11 = 0, s22 = 0, s12 = 0, smax = 0, smin = 0;

    int ret = model.Results.AreaStress(
        shellName,
        eItemTypeElm.Area,
        new[] { loadCase },
        ref s11, ref s22, ref s12, ref smax, ref smin);

    if (ret != 0)
        throw new SAPException(...);

    Debug.WriteLine($"Shell {shellName}: Smax = {smax} MPa");
}
```

---

## Validation Checklist

✓ Load case exists  
✓ Analysis has run  
✓ Stresses in N/mm² (MPa)  

---

## Related Skills

- [Shell Objects](../02-objects/ShellObjects.md) — Query shell
- [Linear Static Analysis](../05-analysis/LinearStatic.md) — Generate results
