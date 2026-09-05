# SAP2000 Releases & Offsets

**Category:** Properties  
**Priority:** MEDIUM  
**Last Updated:** 2026-09-05

---

## Purpose

Define end releases (hinges, semi-rigid connections) and member offsets for frame elements.

---

## API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `FrameObj.SetReleases(name, startReleases[], endReleases[])` | int | Set end releases |
| `FrameObj.GetReleases(name, ref startReleases[], ref endReleases[])` | int | Read releases |
| `FrameObj.SetInsertionPoint(name, relDist, cardinalPt)` | int | Offset member location |
| `FrameObj.SetEndOffsets(name, i1, i2, i3, j1, j2, j3)` | int | Set rigid offsets |

### Release Flags (per axis)
- 0 = Fixed (fully restrained)
- 1 = Free (hinged)
- 2 = Partial (spring support)

---

## Project Convention

**Naming:** `REL_<Location>_<Type>` (if needed)

---

## Workflow

### Create Pinned Connection (Hinge at End)

```csharp
public void SetPinnedEndRelease(
    cSapModel model,
    string frameName,
    bool atStartNode = false)
{
    // Get current releases
    bool[] startRel = new bool[6];
    bool[] endRel = new bool[6];

    int ret = model.FrameObj.GetReleases(frameName, ref startRel, ref endRel);

    if (ret != 0)
        throw new SAPException(...);

    // Set hinge (release moment M3)
    if (atStartNode)
    {
        startRel[2] = true;  // Release M3 at start
    }
    else
    {
        endRel[2] = true;    // Release M3 at end
    }

    ret = model.FrameObj.SetReleases(frameName, startRel, endRel);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "FrameObj.SetReleases",
            returnCode: ret,
            context: $"Frame '{frameName}'");
    }
}
```

### Set Rigid Offset

```csharp
public void SetRigidOffset(
    cSapModel model,
    string frameName,
    double offsetStart1_mm,
    double offsetStart2_mm,
    double offsetStart3_mm,
    double offsetEnd1_mm,
    double offsetEnd2_mm,
    double offsetEnd3_mm)
{
    int ret = model.FrameObj.SetEndOffsets(
        frameName,
        offsetStart1_mm, offsetStart2_mm, offsetStart3_mm,
        offsetEnd1_mm, offsetEnd2_mm, offsetEnd3_mm);

    if (ret != 0)
        throw new SAPException(...);
}
```

---

## Validation Checklist

✓ Releases consistent with connection design  
✓ Offsets in mm  
✓ Offsets don't create geometric issues  
✓ All return codes checked  

---

## Related Skills

- [Frame Objects](../02-objects/FrameObjects.md) — Apply to frames
