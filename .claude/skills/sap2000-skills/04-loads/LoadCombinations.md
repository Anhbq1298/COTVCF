# SAP2000 Load Combinations

**Category:** Loads  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Create load combinations (LRFD, ASD, envelope) that combine multiple load cases.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `RespCombo.Add(name, comboType)` | int | Create combination |
| `RespCombo.SetCaseList(name, numCases, caseNames[], factors[])` | int | Add cases with factors |
| `RespCombo.GetNameList(ref count, ref names)` | int | Get all combinations |

---

## Project Convention

**Naming:** `COMB_<Standard>_<Description>`

Examples:
- `COMB_LRFD_1_2DL_1_6LL`
- `COMB_ASD_1DL_1LL`
- `COMB_ENV_MAX`

---

## Workflow

### Create LRFD Combination

```csharp
public LoadCombination CreateLRFDCombination(
    cSapModel model,
    string combinationName,
    string deadLoadCase,
    string liveLoadCase)
{
    int ret = model.RespCombo.Add(
        combinationName,
        (int)eCombinationType.LinearAdditive);

    if (ret != 0)
        throw new SAPException(...);

    // 1.2 DL + 1.6 LL
    string[] caseNames = new[] { deadLoadCase, liveLoadCase };
    double[] factors = new[] { 1.2, 1.6 };

    ret = model.RespCombo.SetCaseList(
        combinationName,
        2,
        caseNames,
        factors);

    if (ret != 0)
        throw new SAPException(...);

    return new LoadCombination
    {
        SAPObjectName = combinationName,
        Standard = "LRFD"
    };
}
```

---

## Validation Checklist

✓ Combination name follows convention  
✓ All referenced cases exist  
✓ Load factors appropriate for standard  
✓ All return codes checked  

---

## Related Skills

- [Load Patterns & Cases](LoadPatterns.md) — Define cases
- [Applied Loads](AppliedLoads.md) — Apply loads to cases
