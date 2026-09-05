# SAP2000 Database Tables

**Category:** Results  
**Priority:** MEDIUM  
**Last Updated:** 2026-09-05

---

## Purpose

Bulk extraction of analysis results via database tables for performance and filtering.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `DatabaseTables.GetTableForDisplay(tableName, ref csvString)` | int | Export table as CSV |
| `DatabaseTables.SetTableForImport(tableName, csvString)` | int | Import table data |

---

## Workflow

### Extract Frame Forces Table

```csharp
public string ExportFrameForcesTable(cSapModel model)
{
    string csvData = "";

    int ret = model.DatabaseTables.GetTableForDisplay(
        "Frame Forces - Summary",  // Table name
        ref csvData);

    if (ret != 0)
        throw new SAPException(...);

    return csvData;
}
```

---

## Validation Checklist

✓ Table name correct  
✓ CSV parsing works  
✓ Data complete  

---

## Related Skills

- [Frame Results](FrameResults.md) — Alternative to single-element queries
