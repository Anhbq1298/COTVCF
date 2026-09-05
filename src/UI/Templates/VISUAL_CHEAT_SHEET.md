# Structural Engineering UI - Visual Cheat Sheet

Quick visual reference for colors, spacing, and component styles.

---

## 1. COLOR PALETTE REFERENCE

```
┌─────────────────────────────────────────────────────────────────┐
│  PRIMARY COLORS (Main UI Elements)                              │
├─────────────────────────────────────────────────────────────────┤
│  🟦 Professional Blue     #1F4788   RGB(31, 71, 136)            │
│     Usage: Primary buttons, links, focus states, trusted UI     │
│                                                                  │
│  🟩 Structural Green      #2E7D32   RGB(46, 125, 50)            │
│     Usage: Success, valid input, safe results (SF ≥ 1.5)       │
│                                                                  │
│  🟧 Engineering Orange    #F57C00   RGB(245, 124, 0)            │
│     Usage: Warnings, caution, review needed (1.0 ≤ SF < 1.5)  │
│                                                                  │
│  🟥 Critical Red          #C62828   RGB(198, 40, 40)            │
│     Usage: Errors, failures, unsafe (SF < 1.0)                │
│                                                                  │
│  ⬜ Clean White           #FAFAFA   RGB(250, 250, 250)          │
│     Usage: App background                                       │
│                                                                  │
│  ⬛ Dark Gray             #424242   RGB(66, 66, 66)             │
│     Usage: Grid lines, dividers, subtle elements               │
│                                                                  │
│  ⬜ Light Gray            #EEEEEE   RGB(238, 238, 238)          │
│     Usage: Disabled states, alternate row backgrounds           │
└─────────────────────────────────────────────────────────────────┘
```

### Text Colors
```
┌─────────────────────────────────────────┐
│  Primary Text:    #1A1A1A              │ Main text (4.5:1 on white)
│  Secondary Text:  #666666              │ Labels, metadata
│  Disabled Text:   #999999              │ Inactive elements
│  Link Color:      #1F4788              │ Hyperlinks (same as primary)
└─────────────────────────────────────────┘
```

### Usage Rules
```
✓ Always use palette colors (no arbitrary hex values)
✓ Blue for interactive elements (buttons, links, focus)
✓ Green for success states (✓ icons, passed validation)
✓ Orange for warnings (⚠ icons, caution states)
✓ Red for errors/failures (✗ icons, unsafe conditions)
✗ No gradients or blend modes
✗ No transparency for text (always opaque)
✗ No neon or bright colors outside palette
```

---

## 2. TYPOGRAPHY SCALE

```
┌──────────────────────────────────────────────────────────────────┐
│  HEADING 1 (H1)           24px | Weight: 700 (Bold)             │
│  Project title, main page title                                  │
│  Example: "Steel Frame Analysis - Project 2024"                 │
│                                                                  │
│  HEADING 2 (H2)           20px | Weight: 600 (Semi-Bold)        │
│  Section headers (Analysis, Results, Design)                    │
│  Example: "Load Cases"                                          │
│                                                                  │
│  HEADING 3 (H3)           16px | Weight: 600 (Semi-Bold)        │
│  Subsection headers, panel titles                               │
│  Example: "Point Loads"                                         │
│                                                                  │
│  BODY TEXT                13px | Weight: 400 (Regular)          │
│  Main description, property values                              │
│  Example: "Material: A36 Steel | Section: W12x26"               │
│                                                                  │
│  SMALL TEXT               11px | Weight: 400 (Regular)          │
│  Annotations, hints, secondary data                             │
│  Example: "Units: mm | Status: Valid ✓"                         │
│                                                                  │
│  MONOSPACE VALUE          13px | Weight: 500 (Medium)           │
│  Dimensions, calculations, technical codes                      │
│  Example: "12345.6789" "A36" "W12x26"                          │
└──────────────────────────────────────────────────────────────────┘

FONT FAMILY STACK:
  Headlines & UI:    Segoe UI, -apple-system, BlinkMacSystemFont, sans-serif
  Body & Labels:     Segoe UI, Tahoma, sans-serif
  Technical Values:  Consolas, Monaco, Courier New, monospace
```

---

## 3. SPACING GRID (8px base)

```
┌────────────────────────────────────────────┐
│  Spacing Multiples (8px base unit)        │
├────────────────────────────────────────────┤
│  4px   (0.5x) ╔════╗  Tight icon spacing  │
│  8px   (1x)   ╔════════╗  Compact padding │
│  16px  (2x)   ╔════════════════╗  Normal  │
│  24px  (3x)   ╔════════════════════════╗  │
│  32px  (4x)   ╔════════════════════════════╗│
│  40px  (5x)                              │
│  48px  (6x)                              │
│  56px  (7x)                              │
│  64px  (8x)                              │
└────────────────────────────────────────────┘

COMMON USAGE:
  Padding (buttons):       8px vertical × 16px horizontal
  Padding (inputs):        8px vertical × 12px horizontal
  Padding (cards):         16px all sides
  Margin (sections):       24px bottom
  Gap (grid columns):      16px
  Gap (toolbar buttons):   8px
  Section separator:       32px top and bottom
```

---

## 4. COMPONENT HEIGHTS

```
┌────────────────────────────────────┐
│  Standard Heights (pixels)         │
├────────────────────────────────────┤
│  Button (standard):      36px      │
│  Button (compact):       32px      │
│  Input field:            36px      │
│  Combo box:              36px      │
│  DataGrid row:           36px      │
│  DataGrid header:        40px      │
│  Toolbar:                40-48px   │
│  StatusBar:              28px      │
│  ListBox item:           32-36px   │
│  TreeView item:          32px      │
│  Tab height:             36px      │
└────────────────────────────────────┘
```

---

## 5. BUTTON STATES

```
┌─────────────────────────────────────────────────────┐
│  BUTTON: "Run Analysis"                             │
├─────────────────────────────────────────────────────┤
│                                                     │
│  ☐ NORMAL                                           │
│    Background: #1F4788 (Professional Blue)         │
│    Text: White                                      │
│    Cursor: Hand pointer                            │
│                                                     │
│  ☐ HOVER                                            │
│    Background: #163655 (15% darker)                │
│    Text: White                                      │
│    Shadow: Subtle lift                             │
│                                                     │
│  ☐ PRESSED                                          │
│    Background: #0F2341 (30% darker)                │
│    Text: White                                      │
│    Feeling: Sunken                                 │
│                                                     │
│  ☐ DISABLED                                         │
│    Background: #EEEEEE (Light Gray)                │
│    Text: #999999 (Disabled Gray)                   │
│    Cursor: Not-allowed                             │
│    Opacity: 0.5                                    │
│                                                     │
│  ☐ FOCUSED (Keyboard)                              │
│    Border: 2px solid #1F4788                       │
│    Offset: 2px outward                             │
│                                                     │
└─────────────────────────────────────────────────────┘

BUTTON SIZE GUIDE:
  Width:      Auto (min 60px for labels)
  Height:     36px (standard) or 32px (compact)
  Padding:    8px vertical × 16px horizontal
  Border:     0px (no border)
  Radius:     2px (very subtle)
  Font:       13px Segoe UI, weight 600
```

---

## 6. INPUT FIELD STATES

```
┌─────────────────────────────────────────────────────┐
│  TEXTBOX: Beam Depth                                │
├─────────────────────────────────────────────────────┤
│                                                     │
│  ☐ EMPTY / NORMAL                                   │
│    ┌──────────────────────────┐                    │
│    │ [empty white background] │                    │
│    └──────────────────────────┘                    │
│    Border: 1px #E0E0E0, Cursor inside              │
│                                                     │
│  ☐ FILLED                                           │
│    ┌──────────────────────────┐                    │
│    │ 350.50                   │                    │
│    └──────────────────────────┘                    │
│    Text: #1A1A1A, 13px Consolas                    │
│    Font: Monospace for alignment                   │
│                                                     │
│  ☐ FOCUSED                                          │
│    ╔══════════════════════════╗                    │
│    ║ 350.50                   ║                    │
│    ╚══════════════════════════╝                    │
│    Border: 2px #1F4788 (blue), shadow glow        │
│                                                     │
│  ☐ ERROR STATE                                      │
│    ┌──────────────────────────┐                    │
│    │ 0 [invalid]              │                    │
│    └──────────────────────────┘                    │
│    Border: 2px #C62828 (red)                       │
│    Message below: "Depth must be > 0 mm" (red)   │
│                                                     │
│  ☐ DISABLED                                         │
│    ┌──────────────────────────┐                    │
│    │ [greyed out value]       │                    │
│    └──────────────────────────┘                    │
│    Background: #EEEEEE, Text: #999999             │
│    Cursor: Not-allowed                             │
│                                                     │
│  ☐ WITH VALIDATION STATUS                          │
│    ┌──────────────────────────┐ ✓                  │
│    │ 350.50                   │(Green)             │
│    └──────────────────────────┘                    │
│    Checkmark appears when valid                    │
│                                                     │
└─────────────────────────────────────────────────────┘

DIMENSIONS:
  Height:      36px
  Padding:     8px vertical, 12px horizontal
  Border:      1px (normal) → 2px (focused/error)
  Radius:      2px (subtle, professional)
  Min width:   120px (standard field)
```

---

## 7. DATA GRID / TABLE STYLING

```
┌────────────────────────────────────────────────────────────────┐
│  RESULTS TABLE (Color-Coded Safety Factors)                   │
├────────────────────────────────────────────────────────────────┤
│                                                               │
│  ┏━━━━━━━━━━━┳━━━━━━━━━━━━┳━━━━━━━━━━━━┳━━━━━━━━━━┓        │
│  ┃ Element   ┃ Stress     ┃ SF (Color) ┃ Status   ┃ Header  │
│  ┃ (16px)    ┃ (16px)     ┃ (16px)     ┃ (16px)   ┃ 40px    │
│  ┡━━━━━━━━━━━╇━━━━━━━━━━━━╇━━━━━━━━━━━━╇━━━━━━━━━━┩        │
│  │ Beam B1   │ 125.50 MPa │ ┌──────┐  │ ✓ OK    │         │
│  │ (36px)    │            │ │2.15  │  │        │ Row 36px│
│  │           │            │ └──────┘  │ GREEN  │         │
│  │(Alt: gray)│            │  SF≥1.5   │        │         │
│  │           │            │           │        │         │
│  ├───────────┼────────────┼───────────┼────────┤         │
│  │ Beam B2   │ 168.75 MPa │ ┌──────┐  │ ⚠ CHCK │         │
│  │           │            │ │1.32  │  │        │         │
│  │           │            │ └──────┘  │ORANGE  │         │
│  │           │            │ 1.0≤SF<1.5│        │         │
│  │           │            │           │        │         │
│  ├───────────┼────────────┼───────────┼────────┤         │
│  │ Beam B3   │ 215.00 MPa │ ┌──────┐  │ ✗ FAIL │         │
│  │           │            │ │0.85  │  │        │         │
│  │           │            │ └──────┘  │ RED    │         │
│  │           │            │  SF<1.0   │        │         │
│  └───────────┴────────────┴───────────┴────────┘         │
│                                                               │
│  Border between cells: 1px #E0E0E0                            │
│  Alternate row:        #F5F5F5 (subtle gray stripe)           │
│  Hover row:            #F0F7FF (very subtle blue tint)       │
│  Selected row:         Blue left border 3px + light background│
│  Header font:          13px, weight 600, ALL CAPS            │
│  Cell padding:         8px horizontal, 6px vertical          │
│                                                               │
└────────────────────────────────────────────────────────────────┘
```

---

## 8. VALIDATION & ERROR STATES

```
┌────────────────────────────────────────────────┐
│  INPUT VALIDATION FEEDBACK                    │
├────────────────────────────────────────────────┤
│                                                │
│  ✓ VALID (Success)                            │
│    Color:  #2E7D32 (Green)                    │
│    Icon:   ✓ or ✔                             │
│    Border: 1px green (optional)                │
│    Msg:    "OK" or omitted                     │
│                                                │
│  ⚠ WARNING (Caution)                          │
│    Color:  #F57C00 (Orange)                   │
│    Icon:   ⚠ or !                             │
│    Border: 2px orange                         │
│    Msg:    "Review this value"                │
│                                                │
│  ✗ ERROR (Invalid)                            │
│    Color:  #C62828 (Red)                      │
│    Icon:   ✗ or X                             │
│    Border: 2px red, background #FFEBEE        │
│    Msg:    "Field depth must be > 0 mm"      │
│                                                │
│  ◯ LOADING (In Progress)                      │
│    Color:  #1F4788 (Blue)                     │
│    Icon:   ⟳ (spinning)                       │
│    Border: None                                │
│    Msg:    "Analyzing structure..."           │
│                                                │
└────────────────────────────────────────────────┘
```

---

## 9. SHADOWS & ELEVATION

```
┌─────────────────────────────────────────────────┐
│  SHADOW SYSTEM (Depth Effect)                  │
├─────────────────────────────────────────────────┤
│                                                 │
│  L0 - FLAT                                      │
│      No shadow                                  │
│      Usage: Flat backgrounds, content areas   │
│                                                │
│  L1 - SUBTLE                                    │
│      0 2px 4px rgba(0,0,0,0.1)                 │
│      Usage: Cards, panels, slight lift        │
│                                                │
│  L2 - RAISED                                    │
│      0 4px 8px rgba(0,0,0,0.12)                │
│      Usage: Toolbars, floating panels         │
│                                                │
│  L3 - ELEVATED                                  │
│      0 8px 16px rgba(0,0,0,0.15)               │
│      Usage: Dialogs, modals, dropdowns        │
│                                                │
│  L4 - FLOATING                                  │
│      0 12px 24px rgba(0,0,0,0.18)              │
│      Usage: Tooltips, context menus           │
│                                                │
└─────────────────────────────────────────────────┘
```

---

## 10. KEYBOARD SHORTCUTS

```
┌──────────────────────────────────────────────┐
│  STANDARD ENGINEERING TOOL SHORTCUTS         │
├──────────────────────────────────────────────┤
│  Ctrl+N          │ New Project               │
│  Ctrl+O          │ Open Project              │
│  Ctrl+S          │ Save Project              │
│  Ctrl+Shift+S    │ Save As                   │
│  Ctrl+Z          │ Undo                      │
│  Ctrl+Y or Ctrl+Shift+Z  │ Redo             │
│  Ctrl+X          │ Cut                       │
│  Ctrl+C          │ Copy                      │
│  Ctrl+V          │ Paste                     │
│  Delete          │ Delete Selected Element   │
│  F1              │ Help / Documentation      │
│  F5              │ Run Analysis              │
│  F6              │ Post-Process / Results    │
│  Space (+ drag)  │ Pan 3D Viewport           │
│  Shift           │ Multi-select Elements     │
│  Escape          │ Cancel Operation / Close  │
│                  │ Context Menu              │
└──────────────────────────────────────────────┘
```

---

## 11. ICON SET

```
┌──────────────────────────────────────────────────────┐
│  RECOMMENDED ICONS (Heroicons / Material Design)    │
├──────────────────────────────────────────────────────┤
│  Geometry & Structure:                              │
│    ⬇  Arrow Down        → Downward load             │
│    ➡  Arrow Right       → Horizontal/rightward load│
│    ⬅  Arrow Left        → Leftward load             │
│    ⬆  Arrow Up          → Upward load               │
│    🔒 Lock/Anchor       → Fixed support             │
│    🔓 Unlock/Pin        → Pinned support            │
│                                                      │
│  Validation & Status:                              │
│    ✓  Checkmark         → Valid, success            │
│    ✗  X Mark           → Invalid, error             │
│    ⚠  Warning Triangle  → Caution, review needed   │
│    ℹ  Info Circle      → Information               │
│                                                      │
│  Actions:                                           │
│    ⚙  Settings         → Configuration             │
│    💾 Save              → Save file                 │
│    📂 Folder           → Project/directory         │
│    📊 Chart Bar        → Results/analytics         │
│    🔍 Search/Zoom      → Find, zoom to element    │
│    +  Plus             → Add/create new            │
│    −  Minus            → Remove/delete             │
│                                                      │
│  SIZE: 16px (compact), 24px (standard), 32px (large)│
│  STROKE: 2px (outlined), solid for filled          │
│  COLOR: Inherit text color or use semantic color   │
│                                                      │
└──────────────────────────────────────────────────────┘
```

---

## 12. ACCESSIBILITY CHECKLIST

```
┌─────────────────────────────────────────────────┐
│  WCAG 2.1 LEVEL AA COMPLIANCE                  │
├─────────────────────────────────────────────────┤
│  ✓ Text Contrast: ≥ 4.5:1                      │
│    Verified: All text in palette meets AAA     │
│                                                 │
│  ✓ Keyboard Navigation                         │
│    Tab order logical (left→right, top→bottom)  │
│    All interactive elements keyboard-accessible│
│                                                 │
│  ✓ Focus Indicators                            │
│    Visible 2px outline when tabbing             │
│    2px offset for clear visibility             │
│                                                 │
│  ✓ Tooltips & Labels                           │
│    Abbreviations explained (FBD, SF, etc)      │
│    All buttons have text labels                │
│                                                 │
│  ✓ Color Independence                          │
│    Status shown by icon + text (not color only)│
│    Results use green ✓ + text for success     │
│                                                 │
│  ✓ Animation Respect                           │
│    Respect prefers-reduced-motion setting      │
│    No auto-play animations                     │
│    No flashing > 3 times per second            │
│                                                 │
└─────────────────────────────────────────────────┘
```

---

## Quick Copy-Paste Color Hex Values

```
Professional Blue:     #1F4788
Structural Green:      #2E7D32
Engineering Orange:    #F57C00
Critical Red:          #C62828
Clean White:           #FAFAFA
Dark Gray:             #424242
Light Gray:            #EEEEEE
Bright Blue:           #0288D1

Primary Text:          #1A1A1A
Secondary Text:        #666666
Disabled Text:         #999999

Surface L0:            #FFFFFF
Surface L1:            #F5F5F5
Surface L2:            #EEEEEE
Surface L3:            #E0E0E0
```

---

## WPF Resource Dictionary Snippet

```xml
<!-- Quick paste into Colors.xaml -->
<Color x:Key="PrimaryBlue">#1F4788</Color>
<Color x:Key="StructuralGreen">#2E7D32</Color>
<Color x:Key="EngineeringOrange">#F57C00</Color>
<Color x:Key="CriticalRed">#C62828</Color>
<Color x:Key="TextPrimary">#1A1A1A</Color>
<Color x:Key="TextSecondary">#666666</Color>
<Color x:Key="SurfaceL0">#FFFFFF</Color>
<Color x:Key="SurfaceL1">#F5F5F5</Color>

<SolidColorBrush x:Key="PrimaryBlueBrush" Color="{StaticResource PrimaryBlue}" />
<SolidColorBrush x:Key="StructuralGreenBrush" Color="{StaticResource StructuralGreen}" />
<!-- etc... -->
```

---

**Visual Cheat Sheet Version:** 1.0  
**Last Updated:** 2026-09-05  
**Print-Friendly:** Yes (save as PDF for on-desk reference)

