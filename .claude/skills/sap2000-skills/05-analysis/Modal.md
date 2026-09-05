# SAP2000 Modal Analysis

**Category:** Analysis  
**Priority:** MEDIUM  
**Last Updated:** 2026-09-05

---

## Purpose

Extract eigenvalues and natural modes for vibration analysis.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `LoadCases.SetTypeModal(name, maxModes)` | int | Define modal case |
| `LoadCases.SetInitialCondition(name, massType)` | int | Set mass source |

---

## Workflow

### Create Modal Analysis Case

```csharp
public void CreateModalAnalysisCase(
    cSapModel model,
    string caseNameLike_LC_MODAL,
    int numberofModes = 12)
{
    int ret = model.LoadCases.Add(
        caseNameLike_LC_MODAL,
        (int)eCaseType.Modal);

    if (ret != 0)
        throw new SAPException(...);

    ret = model.LoadCases.SetTypeModal(
        caseNameLike_LC_MODAL,
        numberofModes);

    if (ret != 0)
        throw new SAPException(...);
}
```

---

## Validation Checklist

✓ Modal case defined  
✓ Number of modes appropriate  
✓ Mass source defined  
✓ Analysis can run  

---

## Related Skills

- [Mass & Weight](MassWeight.md) — Mass definition
- [Modal Results](../06-results/ModalResults.md) — Extract modes
