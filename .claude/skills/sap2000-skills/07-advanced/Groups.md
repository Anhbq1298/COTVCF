# SAP2000 Groups & Batch Operations

**Category:** Advanced  
**Priority:** MEDIUM  
**Last Updated:** 2026-09-05

---

## Purpose

Create groups for selection and batch operations on multiple objects.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `GroupDef.SetGroup(name, selections)` | int | Create group |
| `GroupDef.GetGroup(name, ref selections)` | int | Read group members |
| `Modify.GroupassignOnly(name, selections)` | int | Assign to group |

---

## Workflow

### Create Group of All Columns

```csharp
public void CreateColumnGroup(cSapModel model, List<string> frameNames)
{
    foreach (var frame in frameNames)
    {
        model.Modify.GroupassignOnly(frame, "GRP_COLUMNS");
    }

    Debug.WriteLine($"Created GRP_COLUMNS with {frameNames.Count} members");
}
```

---

## Validation Checklist

✓ Group name unique  
✓ Members properly added  
✓ Can select by group name  

---

## Related Skills

- [Frame Objects](../02-objects/FrameObjects.md) — Objects to group
