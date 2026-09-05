# SAP2000 Modal Results

**Category:** Results  
**Priority:** MEDIUM  
**Last Updated:** 2026-09-05

---

## Purpose

Extract natural mode shapes, periods, and mass participation ratios.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `Results.ModeShape(name, caseType, stepType, stepNum, displ[])` | int | Get mode shape displacements |
| `Results.ModalPeriod(caseType, stepType, stepNum, ref period)` | int | Get natural period |
| `Results.ModalMassPartic(name, caseType, stepNum, ref ux, ref uy, ref uz)` | int | Get mass participation |

---

## Workflow

### Extract Modal Period

```csharp
public double GetNaturalPeriod(
    cSapModel model,
    string modalCase,
    int modeNumber)
{
    double period = 0;

    int ret = model.Results.ModalPeriod(
        eAnalysisResultType.ModeShape,
        eAnalysisStepType.Mode,
        modeNumber,
        ref period);

    if (ret != 0)
        throw new SAPException(...);

    return period;
}
```

---

## Validation Checklist

✓ Modal analysis complete  
✓ Mode numbers valid  
✓ Periods reasonable (>0)  

---

## Related Skills

- [Modal Analysis](../05-analysis/Modal.md) — Generate modal results
