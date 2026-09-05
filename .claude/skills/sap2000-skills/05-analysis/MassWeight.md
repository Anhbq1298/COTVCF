# SAP2000 Mass & Weight

**Category:** Analysis  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Define mass source for dynamic analysis (self-weight, additional mass, mass participation).

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `LoadCases.SetMassSource(name, massPattern, massMultiplier)` | int | Set mass source |
| `LoadPatterns.SetSelfWtMultiplier(pattern, factor)` | int | Self-weight factor |

---

## Workflow

### Set Mass Source for Modal Analysis

```csharp
public void SetMassSourceForModal(
    cSapModel model,
    string modalCaseName,
    string deadLoadPattern = "DEAD")
{
    int ret = model.LoadCases.SetMassSource(
        modalCaseName,
        deadLoadPattern,
        1.0);  // 100% of dead load

    if (ret != 0)
        throw new SAPException(...);
}
```

---

## Validation Checklist

✓ Mass source defined  
✓ Mass multiplier appropriate  
✓ Self-weight properly assigned  

---

## Related Skills

- [Modal Analysis](Modal.md) — Uses mass definition
- [Load Patterns](../04-loads/LoadPatterns.md) — Define patterns
