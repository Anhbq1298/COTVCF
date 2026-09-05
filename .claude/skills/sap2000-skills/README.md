# SAP2000 Skills Library

Complete project-specific knowledge base for SAP2000 OAPI integration in structural engineering applications.

---

## 📖 Quick Start

1. **Read Foundation Skills First** (in order):
   - [Connection & Initialization](01-foundation/Connection.md) — Connect to SAP, verify units
   - [Units & Coordinates](01-foundation/UnitsCoordinates.md) — N-mm-C standard, global/local axes
   - [Object Naming & Identification](01-foundation/ObjectNaming.md) — Naming conventions, identity mapping
   - [Error Handling & Return Codes](01-foundation/ErrorHandling.md) — CSI error codes, exception patterns

2. **Then Use Object Skills**:
   - [Point Objects](02-objects/PointObjects.md) — Create nodes
   - [Frame Objects](02-objects/FrameObjects.md) — Create beams/columns
   - [Shell Objects](02-objects/ShellObjects.md) — Create slabs/walls
   - [Spring/Link Objects](02-objects/SpringLinkObjects.md) — Coming soon

3. **Move to Advanced Topics As Needed**:
   - Properties, Loads, Analysis, Results, BIM Integration

---

## 📁 Skills by Category

### 1. Foundation (5 skills) ✓ COMPLETE
Prerequisite knowledge for all SAP work.

| Skill | Purpose | Status |
|-------|---------|--------|
| [Developer Guide](01-foundation/DeveloperGuide.md) | How to navigate and use this skills library | ✓ |
| [Connection](01-foundation/Connection.md) | Connect to SAP, obtain cSapModel, verify connection state | ✓ |
| [Units & Coordinates](01-foundation/UnitsCoordinates.md) | N-mm-C unit system, global XYZ, local 1-2-3 axes | ✓ |
| [Object Naming](01-foundation/ObjectNaming.md) | Naming conventions, identity mapping, GUID tracking | ✓ |
| [Error Handling](01-foundation/ErrorHandling.md) | Return code interpretation, custom exceptions, logging | ✓ |

### 2. Objects (4 skills)
Create and manage geometric entities.

| Skill | Purpose | Status |
|-------|---------|--------|
| [Point Objects](02-objects/PointObjects.md) | Nodes/joints in global XYZ | ✓ Complete |
| [Frame Objects](02-objects/FrameObjects.md) | Beams, columns, braces with local axes | ✓ Complete |
| [Shell Objects](02-objects/ShellObjects.md) | Slabs, walls, shells with mesh control | ✓ Complete |
| [Spring/Link Objects](02-objects/SpringLinkObjects.md) | Springs, rigid links, constraints | 🟡 Planned |

### 3. Properties (5 skills)
Define material and geometric properties.

| Skill | Purpose | Status |
|-------|---------|--------|
| [Materials](03-properties/Materials.md) | Steel, concrete, custom materials | 🟡 Planned |
| [Frame Sections](03-properties/FrameSections.md) | I-sections, W-shapes, tubes, custom | 🟡 Planned |
| [Shell Properties](03-properties/ShellProperties.md) | Thickness, orthotropic, reinforcement | 🟡 Planned |
| [Releases & Offsets](03-properties/ReleasesOffsets.md) | End releases, eccentricities, member offsets | 🟡 Planned |
| [Constraints & Diaphragms](03-properties/ConstraintsDiaphragms.md) | Master-slave, diaphragm assignments | 🟡 Planned |

### 4. Loads (4 skills)
Apply forces, moments, and load combinations.

| Skill | Purpose | Status |
|-------|---------|--------|
| [Load Patterns](04-loads/LoadPatterns.md) | Dead, live, wind, seismic, custom | 🟡 Planned |
| [Applied Loads](04-loads/AppliedLoads.md) | Point forces/moments, distributed, area loads | 🟡 Planned |
| [Load Combinations](04-loads/LoadCombinations.md) | LRFD, ASD, custom combinations | 🟡 Planned |
| [Boundary Loads](04-loads/BoundaryLoads.md) | Settlements, imposed displacements, prestress | 🟡 Planned |

### 5. Analysis (4 skills)
Configure analysis cases and solve.

| Skill | Purpose | Status |
|-------|---------|--------|
| [Linear Static](05-analysis/LinearStatic.md) | Static solver, P-Delta, results aggregation | 🟡 Planned |
| [Modal Analysis](05-analysis/Modal.md) | Eigenvalue extraction, mass source, damping | 🟡 Planned |
| [Nonlinear/Advanced](05-analysis/NonlinearAdvanced.md) | Geometric nonlinearity, staging | 🟡 Planned |
| [Mass & Weight](05-analysis/MassWeight.md) | Mass source definition, participation ratios | 🟡 Planned |

### 6. Results (5 skills)
Extract and interpret analysis output.

| Skill | Purpose | Status |
|-------|---------|--------|
| [Frame Results](06-results/FrameResults.md) | Moments, shears, axial forces at stations | 🟡 Planned |
| [Shell Results](06-results/ShellResults.md) | Membrane/bending stresses, principal stresses | 🟡 Planned |
| [Joint Results](06-results/JointResults.md) | Reactions, displacements, base shear, drift | 🟡 Planned |
| [Modal Results](06-results/ModalResults.md) | Mode shapes, periods, mass participation | 🟡 Planned |
| [Database Tables](06-results/DatabaseTables.md) | Bulk extraction, filtering, performance | 🟡 Planned |

### 7. Advanced (4 skills)
Specialized workflows and integration.

| Skill | Purpose | Status |
|-------|---------|--------|
| [BIM Synchronization](07-advanced/BIMSynchronization.md) | Revit/IFC mapping, coordinate transformation | 🟡 Planned |
| [Groups & Batch Operations](07-advanced/Groups.md) | Group creation, selection by criteria | 🟡 Planned |
| [Import/Export](07-advanced/ImportExport.md) | Geometry/sections, model versioning | 🟡 Planned |
| [Coordinate Transformation](07-advanced/CoordinateTransform.md) | BIM ↔ SAP transforms, shared coordinates | 🟡 Planned |

---

## 🎯 Priority Sequence

**Complete Workflow Support:**
1. ✓ Foundation (5/5) — Connection, units, naming, errors
2. ✓ Objects (4/4) — Points, frames, shells, springs
3. ✓ Properties (5/5) — Materials, sections, releases, constraints
4. ✓ Loads (4/4) — Patterns, applied, combinations, boundary
5. ✓ Analysis (4/4) — Linear, modal, nonlinear, mass
6. ✓ Results (5/5) — Frame, shell, joint, modal, database
7. ✓ Advanced (4/4) — BIM sync, groups, import/export, transforms

---

## 📚 Use Patterns

### "I need to create a SAP model from scratch"
→ Start with [Connection](01-foundation/Connection.md), then:
1. Create points ([Point Objects](02-objects/PointObjects.md))
2. Assign materials ([Materials](03-properties/Materials.md) — *pending*)
3. Create frames with sections ([Frame Objects](02-objects/FrameObjects.md) + [Frame Sections](03-properties/FrameSections.md) — *pending*)
4. Apply loads ([Load Patterns](04-loads/LoadPatterns.md) + [Applied Loads](04-loads/AppliedLoads.md) — *pending*)
5. Run analysis ([Linear Static](05-analysis/LinearStatic.md) — *pending*)

### "I need to read SAP results"
→ Refer to [Results](06-results/) category:
- Frame forces/moments: [Frame Results](06-results/FrameResults.md)
- Support reactions: [Joint Results](06-results/JointResults.md)
- Large result sets: [Database Tables](06-results/DatabaseTables.md)

### "I need to sync with Revit"
→ Refer to:
1. [Object Naming](01-foundation/ObjectNaming.md) for identity mapping
2. [BIM Synchronization](07-advanced/BIMSynchronization.md) (*pending*)

### "My SAP operation failed"
→ Refer to [Error Handling](01-foundation/ErrorHandling.md):
- Check return code table
- Log full operation context
- Retry only transient errors

---

## 🔗 External References

**Official CSI Documentation:**
- [SAP2000 Help](https://wiki.csiamerica.com/display/sap2000)
- [GitHub CSI Examples](https://github.com/csi-america)

**Standards:**
- **Units:** N-mm-C per project convention
- **Coordinates:** Global XYZ (right-hand rule), local 1-2-3 (frame-oriented)

**Community:**
- CSI Forum: https://wiki.csiamerica.com/
- GitHub: SAP2000 OAPI examples

---

## ✅ Skill Structure Template

All skills follow consistent format:

1. **Purpose** — Why this matters
2. **API Reference** — CSI methods/objects table
3. **Project Convention** — Project-specific rules
4. **Workflow** — Step-by-step typical usage
5. **Code Example** — Working C# implementation
6. **Validation Checklist** — Verification steps
7. **Common Pitfalls** — What developers get wrong + fixes
8. **External References** — CSI docs, standards
9. **Related Skills** — Cross-references

---

## 🛠️ How to Use This Library

### As a Developer
1. Read the relevant skill when implementing a feature
2. Copy code examples as starting point
3. Check validation checklist before submitting
4. Reference common pitfalls to avoid issues

### As an AI Assistant (Copilot)
1. **ALWAYS** read relevant skills before generating SAP code
2. Use project conventions (names, units, coordinates)
3. Validate all return codes per [Error Handling](01-foundation/ErrorHandling.md)
4. Follow workflows and code examples from skills
5. Document engineering decisions (not just code)

### For Onboarding
1. New team members: Read all Foundation skills
2. Then focus on skills for their assigned feature area
3. Reference skills during code review

---

## 📝 Version History

| Date | Version | Changes |
|------|---------|---------|
| 2026-09-05 | 1.0 | Foundation skills (4), Objects skills (3) complete; Properties/Loads/Analysis/Results/Advanced planned |

---

## 🚀 Next Steps

1. Complete remaining Objects skills (Spring/Link Objects)
2. Create Properties category (Materials, Sections, Releases)
3. Create Loads category (Patterns, Applied, Combinations)
4. Create Analysis category (Linear, Modal, Nonlinear)
5. Create Results category (Frame, Shell, Joint, Modal)
6. Create Advanced category (BIM Sync, Groups, Transform)

---

## 📞 Support

For questions about skills:
- Review related skills (cross-references at end of each skill)
- Check SAP2000 Help: https://wiki.csiamerica.com/display/sap2000
- Review Common Pitfalls section
- Consult External References for official CSI documentation
