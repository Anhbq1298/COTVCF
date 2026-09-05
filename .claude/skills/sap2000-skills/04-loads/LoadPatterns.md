# SAP2000 Load Patterns & Cases

**Category:** Loads  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Create load patterns (dead, live, wind, seismic) and analysis cases that apply loads.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `LoadCases.Add(name, caseType)` | int | Create load case |
| `LoadCases.SetTypeLinearStatic(name)` | int | Set static analysis |
| `LoadPatterns.Add(name, patternType)` | int | Create load pattern |
| `LoadPatterns.SetSelfWtMultiplier(name, multiplier)` | int | Set self-weight factor |

---

## Project Convention

**Load Case Names:** `LC_<Description>` (LC_DEAD, LC_LIVE, LC_WIND_X)

**Load Pattern Names:** `LP_<Type>` (LP_DEAD, LP_LIVE, LP_SNOW)

---

## Workflow

### Create Load Case

```csharp
public LoadCase CreateStaticLoadCase(
    cSapModel model,
    string caseNameLike_LC_DEAD,
    string loadPattern)
{
    int ret = model.LoadCases.Add(
        caseNameLike_LC_DEAD,
        (int)eCaseType.LinearStatic);

    if (ret != 0)
        throw new SAPException(...);

    ret = model.LoadCases.SetTypeLinearStatic(caseNameLike_LC_DEAD);

    if (ret != 0)
        throw new SAPException(...);

    return new LoadCase
    {
        SAPObjectName = caseNameLike_LC_DEAD,
        CaseType = "LinearStatic"
    };
}
```

---

## Validation Checklist

✓ Load case name follows convention  
✓ Load pattern assigned  
✓ Case type correct  
✓ All return codes checked  

---

## Related Skills

- [Applied Loads](AppliedLoads.md) — Apply to patterns
- [Load Combinations](LoadCombinations.md) — Combine cases
