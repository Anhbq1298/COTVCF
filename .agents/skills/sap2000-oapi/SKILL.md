---
name: sap2000-oapi
description: Implement, debug, or review COTVCF integration with SAP2000 OAPI, including connection, objects, properties, loads, analysis, results, coordinates, units, and guarded model writes. Use whenever code calls or depends on CSiAPIv1; do not use for UI-only work.
---

# SAP2000 OAPI

The shared SAP2000 knowledge base lives under
[` .claude/skills/sap2000-skills/`](../../../.claude/skills/sap2000-skills/).
Do not bulk-load every topic. Use this routing workflow:

1. Read [`INDEX.md`](../../../.claude/skills/sap2000-skills/INDEX.md).
2. Read [`01-foundation/Connection.md`](../../../.claude/skills/sap2000-skills/01-foundation/Connection.md) for every SAP2000 integration task.
3. Read the task-specific object, property, load, analysis, result, or advanced topic selected from the index.
4. For units or coordinates, also read [`01-foundation/UnitsCoordinates.md`](../../../.claude/skills/sap2000-skills/01-foundation/UnitsCoordinates.md).
5. For calls, failures, batches, or model writes, also read [`01-foundation/ErrorHandling.md`](../../../.claude/skills/sap2000-skills/01-foundation/ErrorHandling.md).

Treat the library as project guidance, not proof of an installed API signature. Check the current gateway, the installed SAP2000 26 `CSiAPIv1.dll`, or official CSI documentation before adding or changing a call. Check every CSI return code, preserve present units and model identity, keep CSI calls in `src/SAP2000` on the STA worker, and retain the existing write guards and recovery behavior.

Read [`skills/cotvcf-ui-handoff/SKILL.md`](../../../skills/cotvcf-ui-handoff/SKILL.md) when the task affects the current COTVCF workflow or its SAP-facing boundaries.
