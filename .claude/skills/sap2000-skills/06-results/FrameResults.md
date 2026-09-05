# SAP2000 Frame Results

**Category:** Results  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Extract frame element results (moments, shears, axial forces) at design stations.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `Results.FrameForce(name, caseType, caseNames[], step[], p, pDist, px, py, pz, v2, v3, t, m2, m3)` | int | Get frame forces/moments |
| `Results.Setup.SetCaseSelectedForOutput(caseName, selected)` | int | Include case in results |

---

## Workflow

### Extract Moment at Mid-Span

```csharp
public void GetFrameMoment(
    cSapModel model,
    string frameName,
    string loadCase)
{
    double p = 0, pDist = 0.5;  // 50% span (mid-point)
    double v2 = 0, v3 = 0, t = 0, m2 = 0, m3 = 0;
    int step = 0;

    int ret = model.Results.FrameForce(
        frameName,
        eItemTypeElm.Frame,
        new[] { loadCase },
        new[] { step },
        pDist,
        ref p, ref v2, ref v3, ref t, ref m2, ref m3);

    if (ret != 0)
        throw new SAPException(...);

    Debug.WriteLine($"Frame {frameName}: M3 = {m3} N-mm");
}
```

---

## Validation Checklist

✓ Load case exists  
✓ Analysis has run  
✓ Results available  
✓ Moment in local axes (M2, M3)  

---

## Related Skills

- [Linear Static Analysis](../05-analysis/LinearStatic.md) — Generate results
- [Joint Results](JointResults.md) — Support reactions
