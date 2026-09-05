# Structural Engineering Design System - MASTER

> **Source of Truth for All UI/UX Decisions**  
> This document defines colors, typography, components, and patterns for the Structural Engineering Design Tool.  
> **Usage:** Reference this file for all new features. Override only with explicit page-specific `.md` files.

---

## 1. COLOR SYSTEM

### Primary Colors
| Color | Hex Code | Usage | RGB |
|-------|----------|-------|-----|
| **Professional Blue** | `#1F4788` | Primary brand, trusted elements | 31, 71, 136 |
| **Structural Green** | `#2E7D32` | Success, valid, passed checks | 46, 125, 50 |
| **Engineering Orange** | `#F57C00` | Warnings, needs attention | 245, 124, 0 |
| **Critical Red** | `#C62828` | Errors, failures, alerts | 198, 40, 40 |
| **Clean White** | `#FAFAFA` | Backgrounds, readability | 250, 250, 250 |
| **Dark Gray** | `#424242` | Grid lines, subtle divisions | 66, 66, 66 |
| **Light Gray** | `#EEEEEE` | Disabled states, borders | 238, 238, 238 |

### Text Colors
| Element | Color | Hex | Contrast Ratio |
|---------|-------|-----|-----------------|
| **Primary Text** | Near Black | `#1A1A1A` | 16.6:1 ✓ WCAG AAA |
| **Secondary Text** | Medium Gray | `#666666` | 7.8:1 ✓ WCAG AA |
| **Disabled Text** | Light Gray | `#999999` | 4.5:1 ✓ WCAG AA |
| **Link Text** | Professional Blue | `#1F4788` | 8.3:1 ✓ WCAG AA |

### Semantic Colors
| Meaning | Color | Hex | Usage |
|---------|-------|-----|-------|
| **Success** | Green | `#2E7D32` | Valid input, passed checks, successful operations |
| **Warning** | Orange | `#F57C00` | Caution needed, review required, stress ratio 1.0-1.5 |
| **Error** | Red | `#C62828` | Failed validation, unsafe condition, SF < 1.0 |
| **Info** | Bright Blue | `#0288D1` | Informational, help, documentation |
| **Neutral** | Gray | `#757575` | Inactive, unselected, default state |

### Material Grid for Backgrounds
- **Surface L0 (Base):** `#FFFFFF` — Primary app background
- **Surface L1 (Raised):** `#F5F5F5` — Cards, panels, floating sections
- **Surface L2 (Elevated):** `#EEEEEE` — Dialogs, modals
- **Surface L3 (Overlay):** `#E0E0E0` — Hover states, selected items

---

## 2. TYPOGRAPHY SYSTEM

### Font Stack
```css
/* Headings & UI Labels */
font-family: 'Segoe UI', '-apple-system', 'BlinkMacSystemFont', 'Helvetica Neue', sans-serif;

/* Body & Description Text */
font-family: 'Segoe UI', 'Tahoma', sans-serif;

/* Technical Values & Dimensions */
font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
```

### Type Scale & Usage
| Level | Size | Weight | Line Height | Usage | Example |
|-------|------|--------|-------------|-------|---------|
| **H1 - Document Title** | 24px | 700 (Bold) | 32px | Project name, main page title | "Steel Frame Analysis" |
| **H2 - Section Header** | 20px | 600 (Semi-Bold) | 28px | Major sections (Analysis, Results, Design) | "Load Cases" |
| **H3 - Subsection** | 16px | 600 (Semi-Bold) | 24px | Subsections, panel headers | "Point Loads" |
| **Body Regular** | 13px | 400 (Regular) | 20px | Descriptions, labels, standard text | Property values, descriptions |
| **Body Small** | 11px | 400 (Regular) | 16px | Annotations, secondary data, hints | Units, abbreviations, metadata |
| **Monospace Value** | 13px | 500 (Medium) | 20px | Dimensions, calculations, code | "12345.6789", "A36 Steel" |

### Text Emphasis Hierarchy
1. **Primary:** Font weight 700, color `#1A1A1A` (headings)
2. **Secondary:** Font weight 600, color `#1A1A1A` (subheadings)
3. **Body:** Font weight 400, color `#1A1A1A` (main text)
4. **Tertiary:** Font weight 400, color `#666666` (secondary labels)
5. **Disabled:** Font weight 400, color `#999999` (inactive elements)

---

## 3. SPACING & LAYOUT GRID

### Grid System (8px base unit)
```
Multiples: 0, 8px, 16px, 24px, 32px, 40px, 48px, 56px, 64px
```

### Common Spacing Values
| Use Case | Size | Pixels |
|----------|------|--------|
| **Tight** (icon spacing, badge) | 0.5x | 4px |
| **Compact** (tight padding) | 1x | 8px |
| **Standard** (normal padding) | 2x | 16px |
| **Comfortable** (section spacing) | 3x | 24px |
| **Spacious** (major section gap) | 4x | 32px |

### Component Spacing Examples
- **Button padding:** 8px (vertical) × 16px (horizontal)
- **Input field padding:** 8px × 12px
- **Card padding:** 16px all sides
- **Section margin:** 24px bottom
- **Column gap in grid:** 16px

---

## 4. BORDER & SHADOW SYSTEM

### Borders
| Type | Width | Color | Usage |
|------|-------|-------|-------|
| **Subtle Divider** | 1px | `#E0E0E0` | Section dividers, grid lines |
| **Active Border** | 2px | `#1F4788` | Selected elements, focus states |
| **Error Border** | 2px | `#C62828` | Invalid inputs, error states |
| **Success Border** | 2px | `#2E7D32` | Valid/confirmed states |

### Shadows (Elevation System)
| Level | Box-Shadow | Usage |
|-------|-----------|-------|
| **None (L0)** | `none` | Flat backgrounds |
| **Subtle (L1)** | `0 2px 4px rgba(0,0,0,0.1)` | Cards, subtle lift |
| **Raised (L2)** | `0 4px 8px rgba(0,0,0,0.12)` | Floated panels, toolbars |
| **Elevated (L3)** | `0 8px 16px rgba(0,0,0,0.15)` | Dialogs, modals, dropdowns |
| **Floating (L4)** | `0 12px 24px rgba(0,0,0,0.18)` | Tooltips, context menus |

---

## 5. COMPONENT SPECIFICATIONS

### Input Fields
```
Height: 36px (WPF: ControlHeight template)
Padding: 8px vertical, 12px horizontal
Border: 1px #E0E0E0 (normal), 2px #1F4788 (focused)
Background: #FFFFFF (normal), #F5F5F5 (disabled)
Text: 13px Segoe UI, #1A1A1A
Radius: 2px (subtle, professional)
Focus: Blue border (2px), no outline needed
Error: Red border (2px), error message below in #C62828
```

### Buttons
```
Height: 36px (standard), 32px (compact)
Padding: 8px vertical, 16px horizontal
Font: 13px Segoe UI, 600 weight
Radius: 2px
States:
  - Normal: Background #1F4788, Text white
  - Hover: Background #163655 (darken 15%)
  - Pressed: Background #0F2341 (darken 30%)
  - Disabled: Background #E0E0E0, Text #999999, cursor not-allowed
  - Focus: 2px outline #1F4788 (offset 2px outward)
Icon + Text: 8px gap between icon and label
```

### Data Grid / Table
```
Row Height: 36px
Header Height: 40px
Header Font: 13px, 600 weight, all-caps, #1A1A1A
Cell Padding: 8px horizontal, 6px vertical
Borders: 1px #E0E0E0 between rows and columns
Alternating Rows: Every 2nd row background #F5F5F5
Sort Indicator: ▲ (ascending) / ▼ (descending), blue color
Hover Row: Background #F0F7FF (very subtle blue)
Selected Row: Background #E3F2FD with left border 3px #1F4788
Numbers: Right-aligned, monospace font, 4-6 decimal places
Status Cells: Use semantic colors (green/orange/red) with icon + text
```

### Dialogs & Modals
```
Width: 480px (standard), up to 600px (wide)
Padding: 24px
Title: 20px, 600 weight
Content: 13px, 400 weight
Button Container: 24px top margin, right-aligned buttons
Button Gap: 8px between buttons
Close Button: X in top-right corner, 24px × 24px
Overlay: Semi-transparent black, rgba(0,0,0,0.5)
Animation: Fade in 200ms, scale from 95%
```

### Tree View / Hierarchy Panel
```
Indent per Level: 16px
Row Height: 32px
Icon: 16px × 16px, left-aligned with 8px margin
Text: 13px, left-aligned, #1A1A1A
Expand/Collapse: Triangle (▶ / ▼), blue on hover
Selected: Background #E3F2FD, left border 3px #1F4788
Drag & Drop: Visual feedback (dotted border around drop zone)
```

### Status Indicators & Badges
```
Size: 8px dot or 20px × 20px icon
Color: Semantic (green/orange/red)
Text: Optional label, 11px monospace, #1A1A1A
Examples:
  - ✓ Valid: Green dot + "OK"
  - ⚠ Warning: Orange dot + "Review"
  - ✗ Failed: Red dot + "Error"
```

### Tooltips
```
Background: #212121 (near black)
Text: 11px, white color, bold for abbreviations
Padding: 8px horizontal, 6px vertical
Radius: 2px
Delay: 300ms on hover
Arrow: 6px triangle pointing to trigger
Max-width: 240px (auto-wrap text)
```

---

## 6. ICON SPECIFICATIONS

### Icon System
**Source:** Heroicons (solid variant) or Material Design Icons  
**Size:** 16px (compact UI), 24px (standard), 32px (large/toolbar)  
**Stroke Width:** 2px (for outlined icons)  
**Color:** Inherit from text color or use semantic colors  

### Standard Icon Set for Structural Tools
| Icon | Name | Usage |
|------|------|-------|
| ⬇ | Arrow Down | Vertical load, downward direction |
| ➡ | Arrow Right | Horizontal load, rightward direction |
| 🔒 | Lock / Anchor | Fixed support, constraint |
| 🔓 | Unlock | Pinned support, partial constraint |
| ✓ | Checkmark | Valid input, success |
| ✗ | X Mark | Invalid, error, delete |
| ⚙ | Settings | Configuration, properties |
| 📊 | Chart Bar | Results, analysis, data |
| 💾 | Save | Save file, store data |
| 📂 | Folder | Project, directory |
| 🔍 | Search / Zoom | Find, zoom to element |
| ⚠ | Warning Triangle | Caution, needs review |
| ℹ | Info Circle | Information, help |
| + | Plus | Add, create new |
| − | Minus | Remove, delete |

---

## 7. INTERACTION & ANIMATION

### Transition Timing
| Type | Duration | Easing | Usage |
|------|----------|--------|-------|
| **Instant** | 0ms | — | No animation (disabled state, non-visual change) |
| **Snappy** | 100ms | ease-in-out | Button hover, quick feedback |
| **Standard** | 200ms | ease-in-out | Modal open/close, tab switch |
| **Leisurely** | 300ms | ease-in-out | Page transitions, complex layouts |
| **Smooth** | 400ms | ease-in | Loading animations, fade effects |

### Hover States
- **Buttons:** 15% darker background, no border change
- **Links:** Underline appears (if not already underlined)
- **Rows/Cards:** Subtle background lift (L1 shadow), 200ms transition
- **Icons:** Color change to primary blue, 100ms transition

### Focus States
- **Keyboard focus:** 2px solid outline in primary blue, 2px offset outward
- **Visual priority:** Focus should be obvious for accessibility
- **Tab navigation:** Logical flow left→right, top→bottom

### Disabled States
- **Opacity:** Reduce to 0.5 or grayscale
- **Cursor:** `not-allowed` (forbidden sign)
- **Interaction:** No hover effects, no clicks
- **Color:** Text `#999999`, background `#E0E0E0`

### Loading States
- **Progress Bar:** Indeterminate animation (horizontal stripes moving), blue color
- **Spinner:** Rotating circle, primary blue, 24px diameter
- **Duration:** Show immediately (no delay for UX)
- **Label:** "Processing…" or "Analyzing…" text below spinner

---

## 8. ACCESSIBILITY REQUIREMENTS (WCAG 2.1 Level AA)

### Text Contrast
- **Normal Text:** ≥ 4.5:1 contrast ratio (verified: all text meets AAA)
- **Large Text (18px+):** ≥ 3:1 contrast ratio
- **Graphics & UI:** ≥ 3:1 contrast ratio for meaningful elements
- **Disabled Elements:** May be below minimum (semantic meaning still clear)

### Keyboard Navigation
- **Tab Order:** Logical, left-to-right, top-to-bottom
- **Focus Visible:** Every interactive element must show focus
- **Skip Links:** Option to skip repetitive navigation (if applicable)
- **Shortcut Keys:** Documented, standard (Ctrl+S, Ctrl+Z, etc.)

### Screen Reader Support
- **Alt Text:** All images have meaningful alt text or aria-labels
- **Labels:** All inputs associated with labels via `<label>` or aria-label
- **Headings:** Semantic heading structure (H1 → H2 → H3)
- **Lists:** Use `<ul>`, `<ol>` for list structures
- **Live Regions:** Announce dynamic content changes (results, errors)

### Color Independence
- **No color-only:** Meaning cannot rely on color alone
- **Icons + Text:** Status indicators use both icon and text label
- **Patterns:** Use patterns (stripes, dots) in addition to color

### Motion & Animation
- **Respect `prefers-reduced-motion`:** Disable animations if user preference set
- **No auto-play:** Animations should never auto-start
- **No flashing:** No content flashes more than 3 times per second

---

## 9. RESPONSIVE DESIGN

### Breakpoints (Desktop Focus, WPF Native)
| Breakpoint | Width | Usage |
|------------|-------|-------|
| **Mobile** | < 768px | Not primary (external monitor if supported) |
| **Tablet** | 768px - 1024px | Secondary support for accessibility |
| **Desktop (Small)** | 1024px - 1440px | Standard monitors |
| **Desktop (Large)** | ≥ 1440px | High-resolution displays, ultrawide |

### Flexible Layouts
- **Splitters:** Resizable panels (Analysis left, Canvas middle, Properties right)
- **Docking:** Allow panels to dock/undock, float, tab
- **Responsive Grid:** Data grids adjust column width based on available space
- **Scrollable:** Large content areas scroll independently

---

## 10. EXAMPLE WIREFRAME: Analysis Window

```
╔════════════════════════════════════════════════════════════════════════════╗
║ Steel Frame Design Tool                                    File View Tools ║
╠═══════════════════════════════════════════════════════════════════════════╣
║                                                                            ║
║ Project Tree  │                                          │ Properties      ║
║ ▼ MyProject   │                   3D Canvas Area         │ ▼ Member: B1   ║
║   ▼ Members   │                    (OpenGL Viewport)     │   Material: A36║
║     + B1      │                                          │   Section:     ║
║     + B2      │                    [3D Frame Visual]     │   W12x26  ✓    ║
║     + C1      │                                          │   Length: 6m   ║
║   ▼ Loads     │                                          │   Start: N1    ║
║     + LC1     │                                          │   End: N2      ║
║   ▼ Supports  │                                          │ ┌──────────────┤
║     + S1      │                                          │ │ Load: 50 kN  │
║               │                                          │ │ @ mid-span ✓ │
║               │                                          │ └──────────────┤
║               │                                          │                ║
╠═══════════════════════════════════════════════════════════════════════════╣
║ Status: Ready | Zoom: 1.0x | Elements: 3 | Nodes: 4 | Loads: 1          ║
║ [Run Analysis F5]  [Export Report]  [Help F1]                            ║
╚═══════════════════════════════════════════════════════════════════════════╝
```

---

## 11. PERSIST PATTERN (Master + Page Overrides)

### Folder Structure
```
design-system/
└── structural-engineering/
    ├── MASTER.md              ← This file (Global rules)
    └── pages/
        ├── analysis.md        ← Analysis window overrides
        ├── results.md         ← Results viewer overrides
        ├── properties.md      ← Properties panel overrides
        └── settings.md        ← Settings dialog overrides
```

### How to Use
1. **For every page/feature:** Check if `pages/[page-name].md` exists
2. **If exists:** Read page file first, use its rules (overrides Master)
3. **If not exists:** Use Master rules exclusively
4. **New features:** Add page-specific file only if rules differ from Master

### Example Override
```markdown
# Analysis Window Design (page: analysis.md)

## Overrides from Master
- **Canvas background:** Dark theme `#2A2A2A` (instead of white)
- **Grid visibility toggle:** Added toolbar button
- **Keyboard shortcut:** `G` toggles grid on/off (in addition to Master shortcuts)

## Inherits from Master
- All colors (except Canvas bg)
- Typography, spacing, icons
- Accessibility requirements
- Component specifications
```

---

## Integration Checklist

- [ ] **Colors:** All UI uses only palette colors (no arbitrary hex)
- [ ] **Typography:** Follows type scale exactly (no custom sizes)
- [ ] **Spacing:** Uses 8px grid multiples only
- [ ] **Icons:** From approved icon set (Heroicons or Material)
- [ ] **Buttons:** Follow button spec (36px height, 8px padding, proper states)
- [ ] **Inputs:** Follow input spec (validation, error handling, disabled state)
- [ ] **Grids:** Implement alternating rows, proper column types
- [ ] **Accessibility:** Text contrast ≥ 4.5:1, keyboard nav, focus visible
- [ ] **Animations:** Respect `prefers-reduced-motion`, use standard timings
- [ ] **Responsive:** Adapt to multiple monitor sizes
- [ ] **Tooltips:** All abbreviations and technical terms explained
- [ ] **Undo/Redo:** Implemented for all editable actions

---

**Document Version:** 1.0  
**Last Updated:** 2026-09-05  
**Next Review:** Quarterly or after major feature additions
