# SAP2000 Import/Export

**Category:** Advanced  
**Priority:** MEDIUM  
**Last Updated:** 2026-09-05

---

## Purpose

Import geometry from external sources and export models for versioning/handoff.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `File.ImportIFC(fileName)` | int | Import IFC file |
| `File.Save()` | int | Save model |
| `File.SaveAs(fileName)` | int | Save with new name |

---

## Workflow

### Save Model Version

```csharp
public void SaveModelVersion(
    cSapModel model,
    string versionLabel)
{
    string fileName = $"Model_{versionLabel}_{DateTime.Now:yyyy_MM_dd_HHmm}.sdb";

    int ret = model.File.SaveAs(fileName);

    if (ret != 0)
        throw new SAPException(...);

    Debug.WriteLine($"Model saved to {fileName}");
}
```

---

## Validation Checklist

✓ File path valid  
✓ Version naming consistent  
✓ File saved successfully  

---

## Related Skills

- [Connection & Initialization](../01-foundation/Connection.md) — Open saved models
