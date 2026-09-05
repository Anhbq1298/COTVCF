# How to Use the SAP2000 Skills Library

**Category:** Foundation  
**Audience:** All Developers  
**Priority:** HIGH  
**Last Updated:** 2026-09-05

---

## Purpose

Learn how to navigate, read, and apply the SAP2000 skills library effectively.

This guide helps developers:
- Find the right skill for their task
- Understand skill structure and conventions
- Apply code examples correctly
- Follow project best practices
- Contribute back to the library

---

## Before You Start

### Prerequisites
- Read in this order (one time only):
  1. This guide (you're reading it now)
  2. [Connection & Initialization](Connection.md) — Mandatory, explains SAP2000 connection
  3. [Units & Coordinates](UnitsCoordinates.md) — Mandatory, explains N-mm-C and axes
  4. [Object Naming & Identification](ObjectNaming.md) — Recommended, explains naming conventions
  5. [Error Handling & Return Codes](ErrorHandling.md) — Recommended, explains error patterns

### Time Investment
- First-time setup: 2-3 hours (foundation skills)
- Per feature: 30-60 minutes (read relevant skill + implement)

---

## Skill Structure

Every skill follows this format:

### 1. **Purpose**
Why this skill matters. Start here to know if this is the right skill.

### 2. **API Reference**
SAP2000 methods with return types and descriptions.

| Method | Returns | Purpose |
|--------|---------|---------|
| `SomeObj.Method(params)` | int | What it does |

**How to use:** Use this table to find CSI method names and signatures.

### 3. **Project Convention**
Project-specific rules (naming, units, coordinate systems, defaults).

**How to use:** Follow these rules in all implementations. Non-negotiable.

### 4. **Workflow**
Step-by-step typical usage pattern.

**How to use:** Follows this sequence when implementing features.

### 5. **Code Example**
Working C# implementation (copy-paste ready).

**How to use:**
- Start here if you understand the concept
- Use as template/starting point
- Adapt to your specific use case
- DO NOT copy blindly—understand what it does

### 6. **Validation Checklist**
Verification steps before submitting code.

**How to use:**
- ✓ Check ALL items before code review
- Include checklist items in PR description
- Use during self-review

### 7. **Common Pitfalls**
What developers get wrong + fixes.

**How to use:**
- Read before coding
- Check if you're making these mistakes
- Reference in code reviews

### 8. **External References**
Official CSI docs, standards, community resources.

**How to use:** Dive deeper if you need detailed understanding.

### 9. **Related Skills**
Cross-references to other skills.

**How to use:** Read these next if implementing related features.

---

## Finding the Right Skill

### By Task

**"I need to..."**

| Task | Start Here |
|------|-----------|
| Connect to SAP2000 | [Connection](Connection.md) |
| Create a point/node | [Point Objects](../02-objects/PointObjects.md) |
| Create a beam or column | [Frame Objects](../02-objects/FrameObjects.md) |
| Create a slab or wall | [Shell Objects](../02-objects/ShellObjects.md) |
| Add constraints/links | [Spring/Link Objects](../02-objects/SpringLinkObjects.md) |
| Define materials | [Materials](../03-properties/Materials.md) *(pending)* |
| Define sections/shapes | [Frame Sections](../03-properties/FrameSections.md) *(pending)* |
| Apply loads | [Applied Loads](../04-loads/AppliedLoads.md) *(pending)* |
| Run analysis | [Linear Static](../05-analysis/LinearStatic.md) *(pending)* |
| Extract results | [Frame Results](../06-results/FrameResults.md) *(pending)* |
| Handle errors | [Error Handling](ErrorHandling.md) |

### By Developer Role

**Structural Modeler**
1. [Connection](Connection.md) — Connect to SAP
2. [Units & Coordinates](UnitsCoordinates.md) — Understand coordinate system
3. [Point Objects](../02-objects/PointObjects.md) — Create nodes
4. [Frame Objects](../02-objects/FrameObjects.md) — Create beams/columns
5. [Shell Objects](../02-objects/ShellObjects.md) — Create slabs/walls
6. Then: Properties, Loads, Analysis

**Integration Developer (BIM-to-SAP)**
1. [Connection](Connection.md)
2. [Units & Coordinates](UnitsCoordinates.md)
3. [Object Naming & Identification](ObjectNaming.md) — Critical for mapping
4. [Spring/Link Objects](../02-objects/SpringLinkObjects.md) — Constraints
5. Then: BIM Synchronization *(pending)*, Coordinate Transforms *(pending)*

**Analysis Engineer**
1. [Connection](Connection.md)
2. [Units & Coordinates](UnitsCoordinates.md)
3. [Error Handling](ErrorHandling.md)
4. [Linear Static Analysis](../05-analysis/LinearStatic.md) *(pending)* — Run analysis
5. [Frame Results](../06-results/FrameResults.md) *(pending)* — Extract forces/moments
6. [Joint Results](../06-results/JointResults.md) *(pending)* — Get reactions

**Technical Lead (Architecture)**
1. All foundation skills
2. [BIM Synchronization](../07-advanced/BIMSynchronization.md) *(pending)*
3. [Groups & Batch Operations](../07-advanced/Groups.md) *(pending)*
4. [Import/Export](../07-advanced/ImportExport.md) *(pending)*

---

## How to Read a Skill

### Quick Start (5 minutes)
1. Read **Purpose** — Is this the right skill?
2. Read **Workflow** — Understand the pattern
3. Copy **Code Example** — Get working code
4. Run and test

### Deep Dive (20 minutes)
1. Read **Purpose** + **API Reference**
2. Read **Project Convention** — Understand rules
3. Study **Code Example** — Learn the details
4. Read **Common Pitfalls** — Avoid mistakes
5. Check **Validation Checklist** — Verify correctness
6. Read **Related Skills** — Understand dependencies

### Expert Level (1 hour)
1. Everything above
2. Read **External References** — Dive into CSI docs
3. Review **API Reference** in detail
4. Understand error codes and edge cases
5. Review **Common Pitfalls** and why they happen

---

## Code Example Patterns

### Pattern 1: Create and Verify

```csharp
// 1. Call CSI method
int ret = model.PointObj.AddPoint(x, y, z, pointName, "");

// 2. Check return code (ALWAYS)
if (ret != 0)
{
    throw new SAPException(
        operation: "PointObj.AddPoint",
        returnCode: ret,
        context: $"Point '{pointName}'",
        userMessage: "Describe what went wrong for user");
}

// 3. Return application object
return new Point
{
    SAPObjectName = pointName,
    X_mm = x,
    Y_mm = y,
    Z_mm = z
};
```

**Key points:**
- ✓ Check return code (ret != 0)
- ✓ Throw SAPException with context
- ✓ Return application object (not SAP object)
- ✓ Include userMessage

### Pattern 2: Read List

```csharp
// 1. Get list
int count = 0;
string[] names = null;
int ret = model.PointObj.GetNameList(ref count, ref names);

if (ret != 0) throw new SAPException(...);

// 2. Iterate safely
var points = new List<Point>();
for (int i = 0; i < count; i++)
{
    try
    {
        var point = ReadPoint(model, names[i]);
        points.Add(point);
    }
    catch (Exception ex)
    {
        // Log but continue
        Debug.WriteLine($"Warning: {names[i]} failed: {ex.Message}");
    }
}

return points;
```

**Key points:**
- ✓ Check return code after GetNameList
- ✓ Iterate from 0 to count (not foreach if using array)
- ✓ Catch and log per-item errors (don't fail entire list)
- ✓ Return successful items even if some fail

### Pattern 3: Batch Operation

```csharp
// 1. Validate input
if (points.Count < 3)
    throw new ArgumentException("At least 3 points required");

// 2. Call CSI method
string[] pointNames = points.Select(p => p.SAPObjectName).ToArray();
int ret = model.AreaObj.AddByPoint(
    pointNames.Length,
    pointNames,
    shellName,
    propertyName ?? "");

// 3. Handle error
if (ret != 0)
    throw new SAPException(...);

// 4. Create and return application object
return new Shell
{
    SAPObjectName = shellName,
    PointNames = points.Select(p => p.SAPObjectName).ToList()
};
```

**Key points:**
- ✓ Validate before calling SAP
- ✓ Convert application objects to SAP names
- ✓ Handle error with context
- ✓ Return application object

---

## Project Conventions (Critical!)

### Units
- **ALWAYS use N-mm-C** (Newtons, millimeters, Celsius)
- Convert external units (meters, kN, etc.) before calling SAP
- Store in application as N-mm-C
- Example:
  ```csharp
  double revitX_m = 5.0;           // From Revit
  double sapX_mm = revitX_m * 1000.0;  // Convert to mm
  model.PointObj.AddPoint(sapX_mm, 0, 0, "PT_1", "");
  ```

### Naming
- **Points:** `PT_<Story>_<X>_<Y>` or `PT_<Index>`
- **Frames:** `FR_<Type>_<Level>_<ID>` (e.g., FR_BEAM_001, FR_COL_L1_C101)
- **Shells:** `SH_<Type>_<Story>_<Index>` (e.g., SH_SLAB_L1_001)
- **Groups:** `GRP_<Category>` (e.g., GRP_COLUMNS, GRP_DIAPHRAGM_L2)
- **Loads:** `LC_<Description>` (e.g., LC_DEAD, LC_LIVE)

### Coordinates
- **Global XYZ:** Store point coordinates as global
- **Local 1-2-3:** Frame results are in local axes
- **Never assume:** Always be explicit (see [Units & Coordinates](UnitsCoordinates.md))

### Return Code Checking
- **MANDATORY:** Check every SAP method return code
- `ret == 0` → Success
- `ret != 0` → Error (see [Error Handling](ErrorHandling.md) for codes)
- Example:
  ```csharp
  int ret = model.SomeObj.SomeMethod(...);
  if (ret != 0)
      throw new SAPException(...);
  ```

---

## Workflow Patterns

### Simple: Create Single Point
```
1. Read [Point Objects](../02-objects/PointObjects.md)
2. Copy CreatePoint() code example
3. Adjust parameter names
4. Test with known coordinates
```

### Moderate: Create Frame with Section
```
1. Read [Point Objects](../02-objects/PointObjects.md) - create start/end points
2. Read [Frame Objects](../02-objects/FrameObjects.md) - connect frames
3. Read [Frame Sections](../03-properties/FrameSections.md) - assign section
4. Combine in sequence: CreatePoints → CreateFrame → SetSection
```

### Complex: Import Model from Revit
```
1. Read [Object Naming & Identification](ObjectNaming.md) - understand mapping
2. Read [Point Objects](../02-objects/PointObjects.md)
3. Read [Frame Objects](../02-objects/FrameObjects.md)
4. Read [BIM Synchronization](../07-advanced/BIMSynchronization.md)
5. Combine: Read Revit → Transform coordinates → Create SAP objects → Track BIM IDs
```

---

## Code Review Checklist

When reviewing SAP2000 code, use this checklist:

### Mandatory
- ✓ All CSI return codes checked (`ret != 0`)
- ✓ Errors raise SAPException with context
- ✓ All coordinates in N-mm-C
- ✓ All object names follow naming convention
- ✓ No hardcoded units or assumptions
- ✓ No silent failures

### Important
- ✓ Application objects returned (not SAP objects)
- ✓ Local axes documented (for frame elements)
- ✓ Batch operations handle partial failures gracefully
- ✓ Error messages are user-actionable
- ✓ Related skills followed

### Nice to Have
- ✓ Validation checklist items included in PR description
- ✓ Code follows existing skill examples
- ✓ Comments reference relevant skill(s)
- ✓ Tests cover success and error cases

---

## Common Mistakes & Fixes

### ❌ Ignoring Return Codes
```csharp
// WRONG
model.PointObj.AddPoint(x, y, z, "PT_1", "");
// What if it failed?
```

**Fix:** Check return code.
```csharp
int ret = model.PointObj.AddPoint(x, y, z, "PT_1", "");
if (ret != 0) throw new SAPException(...);
```

### ❌ Assuming Units
```csharp
// WRONG: Is this meters or millimeters?
double x = 5.0;
model.PointObj.AddPoint(x, 0, 0, "PT_1", "");
```

**Fix:** Convert to N-mm-C explicitly.
```csharp
// From Revit (meters)
double revitX_m = 5.0;
// To SAP (mm)
double sapX_mm = revitX_m * 1000.0;
model.PointObj.AddPoint(sapX_mm, 0, 0, "PT_1", "");
```

### ❌ Wrong Naming Convention
```csharp
// WRONG
model.PointObj.AddPoint(x, y, z, "MyPoint", "");
model.FrameObj.AddFrameByPoint(p1, p2, "MyBeam", "");
```

**Fix:** Follow naming conventions.
```csharp
model.PointObj.AddPoint(x, y, z, "PT_L1_0_0", "");
model.FrameObj.AddFrameByPoint(p1, p2, "FR_BEAM_001", "");
```

### ❌ Forgetting Local Axes
```csharp
// WRONG: Assumes standard orientation
var moment = results.M2;
// But local axis might be rotated!
```

**Fix:** Document and verify local axes.
```csharp
// From skill example
var frame = CreateFrameByPoints(model, p1, p2, "FR_BEAM_001", "SECTION");
// frame.Local2AxisDesign documented at creation
// Use that to interpret results
```

---

## Tips for Success

### 1. Read Skills Before Coding
- Spend 20 minutes reading right skill
- Save hours debugging
- Avoid common pitfalls

### 2. Start with Code Examples
- Copy example from skill
- Adapt to your use case
- Understand before running

### 3. Test Early
- Verify connection works first
- Create single point, verify
- Build up incrementally

### 4. Reference Skills in Comments
```csharp
// See [Point Objects](../../skills/sap2000/02-objects/PointObjects.md)
var point = CreatePoint(model, x, y, z, pointName);
```

### 5. Use Validation Checklist
- Before submitting code
- In code review comments
- During debugging

### 6. Log Everything
- Log operation name, inputs, return code
- Makes debugging faster
- Helps team understand failures

---

## Asking for Help

**Problem:** "SAP method failed with code 5"

→ See [Error Handling](ErrorHandling.md) → Code 5 = Duplicate object → Fix name uniqueness

**Problem:** "My beam results look wrong"

→ Check [Common Pitfalls](ErrorHandling.md) → "Assuming standard local axes" → Verify local axis orientation at creation

**Problem:** "BIM points don't map to SAP"

→ See [Object Naming & Identification](ObjectNaming.md) → Coordinate hash matching section → Debug coordinate conversion

---

## Contributing to Skills Library

If you find:
- ❌ Missing information
- ❌ Incorrect code example
- ❌ New common pitfall
- ❌ Unclear explanation

→ Document it and propose update to skill

Skills are living documents—they improve with team feedback.

---

## Related Skills

- [Connection & Initialization](Connection.md) — How to start
- [Units & Coordinates](UnitsCoordinates.md) — Critical concepts
- [Error Handling & Return Codes](ErrorHandling.md) — Debugging
