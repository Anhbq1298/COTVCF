---
name: structural-engineering-ui
description: Design, implement, or review structural-engineering desktop UI in COTVCF using WPF, MVVM, engineering units, validation, accessibility, data grids, and model viewports. Use for presentation or UX work; do not use for backend-only changes.
---

# Structural Engineering UI

Read the shared source skill at
[` .claude/skills/structural-engineering-ui/SKILL.md`](../../../.claude/skills/structural-engineering-ui/SKILL.md)
completely before UI work. Then read only the sections relevant to the task in:

- [`.instructions.md`](../../../.instructions.md) for repository-level WPF expectations and checks.
- [`design-system/structural-engineering/MASTER.md`](../../../design-system/structural-engineering/MASTER.md) for design tokens and component rules.
- [`src/Mcotis.Desktop/UI/Templates/`](../../../src/UI/Templates/) when an implementation template is useful.

For COTVCF itself, also read
[`skills/cotvcf-ui-handoff/SKILL.md`](../../../skills/cotvcf-ui-handoff/SKILL.md).
The current COTVCF handoff overrides older generic examples where they conflict, especially density, navigation, viewport styling, and workflow labels.

Keep technical values precise and unit-labelled, preserve keyboard and accessibility behavior, and centralize reusable styling in `src/Mcotis.Desktop/Presentation/Theme.xaml`.
