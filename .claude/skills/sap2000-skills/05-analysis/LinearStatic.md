# SAP2000 Linear Static Analysis

**Category:** Analysis  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Configure and run linear static structural analysis.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `Analyze.RunAnalysis()` | int | Run active analysis |
| `Analyze.SetSolverOption(solver)` | int | Select solver (standard, multi-threaded) |
| `LoadCases.SetTypeLinearStatic(name)` | int | Define static case |

---

## Workflow

### Run Analysis

```csharp
public void RunLinearStaticAnalysis(cSapModel model)
{
    int ret = model.Analyze.RunAnalysis();

    if (ret != 0)
        throw new SAPException(
            operation: "Analyze.RunAnalysis",
            returnCode: ret,
            userMessage: "Analysis failed. Check model for errors.");
}
```

---

## Validation Checklist

✓ All load cases defined  
✓ All supports defined  
✓ Geometry is stable  
✓ No unconnected elements  

---

## Related Skills

- [Load Cases](../04-loads/LoadPatterns.md) — Define cases
- [Frame Results](../06-results/FrameResults.md) — Extract results
