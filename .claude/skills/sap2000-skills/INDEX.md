# SAP2000 Skills Library - Master Index

**Project:** Structural Engineering Application (SAP2000 Integration)  
**Version:** 1.0  
**Last Updated:** 2026-09-05

---

## 📚 Skills Organization

This library contains project-specific SAP2000 OAPI knowledge, conventions, and validated workflows.

### ✓ When to Use This Library
- Before implementing any SAP2000 integration feature
- When debugging SAP2000 connectivity issues
- When extracting or modifying model data
- When implementing analysis workflows
- When integrating BIM-to-SAP synchronization
- When handling units, coordinates, or local axes

### ⚠️ Mandatory Reading
Before working with SAP2000, read:
1. [01-foundation/Connection.md](01-foundation/Connection.md)
2. Relevant skill for your task (objects, properties, loads, analysis, results, or advanced)

---

## Category Structure

### **01 - Foundation** (Start here) - ✓ COMPLETE
Core SAP2000 concepts and connection patterns.

| Skill | Purpose | Audience | Status |
|-------|---------|----------|--------|
| [Developer Guide](01-foundation/DeveloperGuide.md) | How to navigate and use this skills library | All developers | ✓ |
| [Connection & Initialization](01-foundation/Connection.md) | Attach to SAP2000, cOAPI, cSapModel | All developers | ✓ |
| [Units & Coordinates](01-foundation/UnitsCoordinates.md) | Unit systems, global XYZ, local axes | All developers | ✓ |
| [Object Naming & Identification](01-foundation/ObjectNaming.md) | Unique names, GUIDs, ID mapping | Integration developers | ✓ |
| [Error Handling & Return Codes](01-foundation/ErrorHandling.md) | CSI return codes, exceptions | All developers | ✓ |

### **02 - Objects** (Structural Elements) - ✓ COMPLETE
Creating and querying structural model objects.

| Skill | Purpose | Audience | Status |
|-------|---------|----------|--------|
| [Point Objects & Nodes](02-objects/PointObjects.md) | Create/read/modify nodes, coordinates | Modelers | ✓ |
| [Frame Objects](02-objects/FrameObjects.md) | Beams, columns, braces, cables | Modelers | ✓ |
| [Shell/Area Objects](02-objects/ShellObjects.md) | Walls, slabs, shells, membrane behavior | Modelers | ✓ |
| [Spring/Link Objects](02-objects/SpringLinkObjects.md) | Springs, rigid links, constraints | Advanced modelers | ✓ |

### **03 - Properties** (Material, Geometric, Constraints) - ✓ COMPLETE
Assigning material and geometric properties to structural elements.

| Skill | Purpose | Audience | Status |
|-------|---------|----------|--------|
| [Materials](03-properties/Materials.md) | Define/assign steel, concrete, custom materials | Modelers | ✓ |
| [Frame Sections](03-properties/FrameSections.md) | I-sections, W-shapes, tubes, custom | Modelers | ✓ |
| [Shell Properties](03-properties/ShellProperties.md) | Thickness, orthotropic, reinforcement | Advanced modelers | ✓ |
| [Releases & Offsets](03-properties/ReleasesOffsets.md) | End releases, eccentricities, offsets | Modelers | ✓ |
| [Constraints & Diaphragms](03-properties/ConstraintsDiaphragms.md) | Master-slave, diaphragm, constraints | Modelers | ✓ |

### **04 - Loads** (Loading System) - ✓ COMPLETE
Define load cases, load patterns, and combinations.

| Skill | Purpose | Audience | Status |
|-------|---------|----------|--------|
| [Load Patterns & Cases](04-loads/LoadPatterns.md) | DL, LL, WL, custom patterns, cases | Load engineers | ✓ |
| [Applied Loads](04-loads/AppliedLoads.md) | Point forces, distributed, area loads | Modelers | ✓ |
| [Load Combinations](04-loads/LoadCombinations.md) | LRFD, ASD, custom combinations | Load engineers | ✓ |
| [Boundary Loads](04-loads/BoundaryLoads.md) | Support settlements, imposed displacements | Advanced engineers | ✓ |

### **05 - Analysis** (Solver & Setup) - ✓ COMPLETE
Configure and run structural analysis.

| Skill | Purpose | Audience | Status |
|-------|---------|----------|--------|
| [Linear Static Analysis](05-analysis/LinearStatic.md) | Set solver, P-Delta, run analysis | Engineers | ✓ |
| [Modal Analysis](05-analysis/Modal.md) | Eigenvalue, mass source, modes | Engineers | ✓ |
| [Nonlinear & Advanced](05-analysis/NonlinearAdvanced.md) | Geometric nonlinearity, P-Delta, staging | Advanced engineers | ✓ |
| [Mass & Weight](05-analysis/MassWeight.md) | Mass source, self-weight, participation | Engineers | ✓ |

### **06 - Results** (Extract & Interpret) - ✓ COMPLETE
Retrieve and process analysis results.

| Skill | Purpose | Audience | Status |
|-------|---------|----------|--------|
| [Frame Results](06-results/FrameResults.md) | Moment, shear, axial at stations | Engineers | ✓ |
| [Shell Results](06-results/ShellResults.md) | Stresses, forces per unit width, strains | Engineers | ✓ |
| [Joint Results](06-results/JointResults.md) | Reactions, displacements, base shear | Engineers | ✓ |
| [Modal Results](06-results/ModalResults.md) | Mode shapes, periods, mass participation | Engineers | ✓ |
| [Database Tables](06-results/DatabaseTables.md) | Bulk extraction, table queries | Integration/Analysis | ✓ |

### **07 - Advanced** (Specialized Workflows) - ✓ COMPLETE
Complex integration and specialized workflows.

| Skill | Purpose | Audience | Status |
|-------|---------|----------|--------|
| [BIM-to-SAP Synchronization](07-advanced/BIMSynchronization.md) | Revit/IFC mapping, coordinate transform | BIM integrators | ✓ |
| [Groups & Batch Operations](07-advanced/Groups.md) | Create groups, select by criteria, batch ops | Automation engineers | ✓ |
| [Import/Export](07-advanced/ImportExport.md) | Geometry import, model versioning, handoff | Technical leads | ✓ |
| [Coordinate Transformations](07-advanced/CoordinateTransform.md) | BIM ↔ SAP, shared coordinates | Integration engineers | ✓ |

---

## 🔗 External References

### Official CSI Documentation
- **SAP2000 OAPI Help:** Installed with SAP2000 (`<Installation>\Help\CSI_OAPI.chm`)
- **CSI GitHub:** https://github.com/Csi-Ers
- **SAP2000 Documentation Portal:** https://wiki.csiamerica.com/

### Community & Examples
- **CSI Example Programs:** https://github.com/csi-ers/SAP2000-csharp-examples
- **OAPI Sample Code:** Included in SAP2000 installation
- **Third-party Wrappers:** (reviewed & validated for project use)

### Structural Analysis References
- **AISC Steel Design:** https://www.aisc.org/
- **ACI Concrete Design:** https://www.concrete.org/
- **ASCE/SEI Load Standards:** https://www.asce.org/

---

## 📋 Quick Start Workflow

### For Adding a New Feature

1. **Understand requirement** → Define what needs to happen
2. **Find relevant skill** → Read from this index
3. **Check conventions** → Follow project naming/unit rules
4. **Implement** → Use skill as reference during coding
5. **Validate** → Test against SAP2000 GUI and database tables
6. **Extend skill** → Document any new conventions discovered

### Example: "Add member force extraction"
```
1. Read: 06-results/FrameResults.md (station-based forces)
2. Read: 06-results/DatabaseTables.md (bulk extraction method)
3. Implement: Choose method based on performance needs
4. Validate: Compare with SAP2000 GUI results
5. If new pattern found: Update skill documentation
```

---

## 🛠️ Skill Content Structure

Each skill file follows this structure:

```markdown
# Skill Title

## Purpose
What this skill covers and why it matters.

## API Reference
Relevant CSI object/method names.

## Project Conventions
Project-specific naming, units, assumptions.

## Workflow
Step-by-step typical usage patterns.

## Code Example
Minimal working example in C#.

## Common Pitfalls
What developers frequently get wrong.

## Validation Checklist
How to verify correct implementation.

## External References
Links to CSI docs, standards, examples.
```

---

## 📊 Skill Dependency Graph

```
Foundation (Connection, Units, Naming)
    ↓
    ├→ Objects (Points, Frames, Shells)
    ├→ Properties (Materials, Sections, Constraints)
    └→ Loads (Patterns, Cases, Combinations)
         ↓
      Analysis (Linear, Modal, Nonlinear)
         ↓
      Results (Frame, Shell, Joint, Modal, Tables)
         ↓
      Advanced (BIM Sync, Groups, Import/Export, Coordinates)
```

---

## 🚀 Maintenance & Updates

Skills are living documents. When:

- **New SAP2000 version released:** Review for API changes
- **Bug fixed:** Document in "Common Pitfalls"
- **New workflow discovered:** Add to "Workflow" section
- **Project convention established:** Add to "Project Conventions"

Update frequency: As needed (not scheduled).

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2026-09-05 | Initial SAP2000 Skills Library |

---

## Questions?

If a skill doesn't answer your question:
1. Search all skills for related content
2. Check external references (CSI docs)
3. Consult SAP2000 GUI documentation
4. Propose a new skill or skill extension

**Never guess about SAP2000 behavior.** If documentation doesn't exist, document your discovery and add it to the library.
