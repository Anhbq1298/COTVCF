# SAP2000 Spring & Link Objects

**Category:** Objects  
**Priority:** MEDIUM  
**Last Updated:** 2026-09-05

---

## Purpose

Create and manage spring and link objects for boundary conditions, constraints, and special connections.

---

## Spring Objects

Springs simulate flexible supports or foundation behavior.

### API Reference

| Method | Returns | Purpose |
|--------|---------|---------|
| `SpringObj.AddSpring(x, y, z, name, propName)` | int | Create spring at point |
| `SpringObj.SetSpringAssignment(pointName, springName)` | int | Assign spring to point |
| `SpringObj.GetNameList(ref count, ref names)` | int | Get all spring names |

### Create Spring Support

```csharp
public Spring CreateSpring(
    cSapModel model,
    double x_mm, double y_mm, double z_mm,
    string springName,
    string propertyName = "")
{
    int ret = model.SpringObj.AddSpring(x_mm, y_mm, z_mm, springName, propertyName ?? "");

    if (ret != 0)
    {
        throw new SAPException(
            operation: "SpringObj.AddSpring",
            returnCode: ret,
            context: $"Spring '{springName}' at ({x_mm}, {y_mm}, {z_mm}) mm",
            userMessage: "Failed to create spring. Verify property name and coordinates.");
    }

    return new Spring
    {
        ApplicationId = Guid.NewGuid(),
        SAPObjectName = springName,
        PropertyName = propertyName ?? "",
        X_mm = x_mm,
        Y_mm = y_mm,
        Z_mm = z_mm
    };
}
```

---

## Link Objects

Links provide rigid or semi-rigid constraints between elements.

### Types of Links

1. **Rigid Link** — No relative displacement between nodes
2. **Constraint Link** — Couples degrees of freedom
3. **Connector Element** — Spring with nonlinear behavior

### API Reference

| Method | Returns | Purpose |
|--------|---------|--------|
| `LinkObj.AddGeneralizedLink(p1, p2, name, propName)` | int | Create link element |
| `LinkObj.AddRigidLink(masterPoint, slavePoints[], name)` | int | Create rigid constraint |
| `LinkObj.GetNameList(ref count, ref names)` | int | Get all link names |

### Create Rigid Link

```csharp
public Link CreateRigidLink(
    cSapModel model,
    string masterPointName,
    List<string> slavePointNames,
    string linkName)
{
    if (slavePointNames.Count == 0)
        throw new ArgumentException("At least one slave point required");

    string[] slavePoints = slavePointNames.ToArray();

    int ret = model.LinkObj.AddRigidLink(
        masterPointName,
        slavePoints,
        linkName);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "LinkObj.AddRigidLink",
            returnCode: ret,
            context: $"Link '{linkName}': master={masterPointName}, slaves={slavePointNames.Count}",
            userMessage: "Failed to create rigid link. Verify master and slave points exist.");
    }

    return new Link
    {
        ApplicationId = Guid.NewGuid(),
        SAPObjectName = linkName,
        MasterPoint = masterPointName,
        SlavePoints = slavePointNames,
        LinkType = "RigidLink"
    };
}
```

### Create Generalized Link (Spring-Damper)

```csharp
public Link CreateGeneralizedLink(
    cSapModel model,
    string point1Name,
    string point2Name,
    string linkName,
    string propertyName = "")
{
    int ret = model.LinkObj.AddGeneralizedLink(
        point1Name,
        point2Name,
        linkName,
        propertyName ?? "");

    if (ret != 0)
    {
        throw new SAPException(
            operation: "LinkObj.AddGeneralizedLink",
            returnCode: ret,
            context: $"Link '{linkName}': {point1Name} → {point2Name}",
            userMessage: "Failed to create generalized link. Verify points and property.");
    }

    return new Link
    {
        ApplicationId = Guid.NewGuid(),
        SAPObjectName = linkName,
        PropertyName = propertyName ?? "",
        LinkType = "GeneralizedLink",
        Point1 = point1Name,
        Point2 = point2Name
    };
}
```

---

## Constraints (Diaphragm-like)

Couple multiple points for floor/foundation behavior.

### Create Constraint

```csharp
public Constraint CreateDiaphragmConstraint(
    cSapModel model,
    List<string> constrainedPointNames,
    string constraintName,
    int dofX = 1, int dofY = 1, int dofZ = 1,
    int dofRX = 0, int dofRY = 0, int dofRZ = 1)
{
    string[] points = constrainedPointNames.ToArray();

    int ret = model.ConstraintObj.AddConstraint(
        constraintName,
        points.Length,
        points,
        dofX, dofY, dofZ, dofRX, dofRY, dofRZ);

    if (ret != 0)
    {
        throw new SAPException(
            operation: "ConstraintObj.AddConstraint",
            returnCode: ret,
            context: $"Constraint '{constraintName}', {points.Length} points",
            userMessage: "Failed to create constraint. Verify point names and DOF settings.");
    }

    return new Constraint
    {
        ApplicationId = Guid.NewGuid(),
        SAPObjectName = constraintName,
        ConstrainedPoints = constrainedPointNames,
        DegreesOfFreedomX = dofX == 1,
        DegreesOfFreedomY = dofY == 1,
        DegreesOfFreedomZ = dofZ == 1,
        RotationZ = dofRZ == 1
    };
}
```

---

## Code Example: Create Diaphragm Floor

```csharp
public void CreateDiaphragmFloor(
    cSapModel model,
    string storyLabel,
    List<string> floorPointNames)
{
    // Constrain all points on floor to move together
    var constraint = CreateDiaphragmConstraint(
        model,
        floorPointNames,
        $"GRP_DIAPHRAGM_{storyLabel}",
        dofX: 1,      // Couple X translation
        dofY: 1,      // Couple Y translation
        dofZ: 0,      // Allow vertical independence
        dofRX: 0,     // Allow independent rotation about X
        dofRY: 0,     // Allow independent rotation about Y
        dofRZ: 1      // Couple rotation about Z
    );

    Debug.WriteLine($"Created diaphragm constraint for {floorPointNames.Count} points");
}
```

---

## Validation Checklist

✓ Spring properties defined before creation  
✓ Link element properties assigned  
✓ Rigid link master/slave relationship correct  
✓ Constraint degree-of-freedom settings match design intent  
✓ No circular dependencies (master → slave → master)  
✓ All return codes checked  

---

## Common Pitfalls

### ❌ Circular Dependencies
```csharp
// WRONG: Master linked to slave, slave linked to another master
model.LinkObj.AddRigidLink("PT_1", new[]{ "PT_2" }, "LINK_1");
model.LinkObj.AddRigidLink("PT_2", new[]{ "PT_3" }, "LINK_2");
// Ambiguous hierarchy!
```

**Fix:** Define clear master/slave hierarchy.

### ❌ Over-Constraining
```csharp
// WRONG: Point both pinned (support) AND in rigid link (constraint)
model.PointObj.AddConstraint("PT_1", pinned: 1, ...);
model.LinkObj.AddRigidLink("PT_1", slavePoints, ...);
// Redundant constraints cause solver issues!
```

**Fix:** Choose either support OR constraint, not both.

### ❌ Missing Property Assignment
```csharp
// WRONG: Creating spring without property
model.SpringObj.AddSpring(x, y, z, "SPRING_1", "");
// SAP assigns default stiffness!
```

**Fix:** Always assign spring property.

---

## Related Skills

- [Point Objects & Nodes](PointObjects.md) — Master/slave points
- [Constraints & Diaphragms](../03-properties/ConstraintsDiaphragms.md) — Property definition
