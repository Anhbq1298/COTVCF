# SAP2000 Nonlinear & Advanced Analysis

**Category:** Analysis  
**Priority:** MEDIUM  
**Last Updated:** 2026-09-05

---

## Purpose

Configure geometric nonlinearity (P-Delta effects) and staged/nonlinear analysis.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `LoadCases.SetTypeLinearStatic(name, pdelta)` | int | Enable P-Delta |
| `LoadCases.SetTypeNonlinear(name)` | int | Define nonlinear case |

---

## Workflow

### Enable P-Delta Analysis

```csharp
public void EnablePDeltaAnalysis(
    cSapModel model,
    string staticCaseName)
{
    int ret = model.LoadCases.SetTypeLinearStatic(staticCaseName, 1);  // 1 = P-Delta

    if (ret != 0)
        throw new SAPException(...);
}
```

---

## Validation Checklist

✓ P-Delta case appropriate for model  
✓ Analysis converges  
✓ Results reasonable  

---

## Related Skills

- [Linear Static](LinearStatic.md) — Basic analysis
