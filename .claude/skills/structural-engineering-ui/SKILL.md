# Structural Engineering UI/UX Pro Max Skill

## Overview
Specialized AI skill for building professional, precision-focused UI/UX for structural engineering design tools and analysis applications. Tailored for WPF (.NET Framework) desktop applications with emphasis on accuracy, data visualization, and professional workflow optimization.

**Industry:** Structural Engineering & CAD Design Tools  
**Primary Framework:** WPF (Windows Presentation Foundation)  
**Secondary Frameworks:** WinUI 3, Avalonia  
**Focus:** Design Tools, Analysis Dashboards, Project Management  

---

## Design Philosophy

### Core Principles for Structural Engineering UI
1. **Precision & Accuracy** - Every measurement, calculation, and visual must be exact
2. **Professional Rigor** - Enterprise-grade reliability and verification
3. **Efficient Workflow** - Minimize clicks, maximize productivity
4. **Data Visualization** - Complex structures rendered with clarity
5. **Accessibility** - WCAG compliance for technical professionals
6. **Regulatory Compliance** - Support for engineering codes and standards

### Design System for Structural Analysis Tools

#### Visual Style
- **Pattern:** Data-Centric Grid + Canvas
- **Style Family:** Modern Industrial / Professional Minimalism
- **Design Approach:** Clarity-first, function-focused, zero ornamental elements

#### Color Palette (Structural Engineering)
- **Primary:** `#1F4788` (Deep Engineering Blue) - Trust, professionalism, structural integrity
- **Secondary:** `#2E7D32` (Engineering Green) - Validation, safety, passed checks
- **Accent - Warning:** `#F57C00` (Structural Orange) - Attention needed, warnings
- **Accent - Critical:** `#C62828` (Structural Red) - Failures, errors, critical alerts
- **Background:** `#FAFAFA` (Clean White) - High contrast for reading specifications
- **Grid/Neutral:** `#424242` (Dark Gray) - Material lines, grid, reference elements
- **Text Primary:** `#1A1A1A` (Near Black) - All body text, specs, dimensions
- **Text Secondary:** `#666666` (Medium Gray) - Secondary labels, metadata
- **Success:** `#2E7D32` (Engineering Green) - Passed checks, valid input
- **Info:** `#0288D1` (Bright Blue) - Informational messages, data displays

#### Typography (Structural Engineering)
- **Heading Font:** `Inter` or `Segoe UI` (Sans-serif, geometric, precision)
- **Body Font:** `Segoe UI` (System font on Windows, readable at all sizes)
- **Monospace Font:** `Consolas` or `Courier New` (For dimensions, codes, technical values)
- **Sizing Hierarchy:**
  - H1: 24px (Project/Document title)
  - H2: 20px (Section headers)
  - H3: 16px (Subsection headers)
  - Body: 13px (Standard text, specifications)
  - Small: 11px (Annotations, secondary data)
  - Monospace Values: 13px (Dimensions, calculations)

#### Key Components for Structural Tools

##### 1. Property Grid
- Multi-column data entry (Name, Value, Unit, Status)
- Real-time validation with inline errors
- Combo boxes for standard materials, sections, codes
- Unit conversion support
- Input masking for dimensions

##### 2. 3D Canvas Area
- OpenGL or Direct3D rendering viewport
- Coordinate axes and grid display
- Selection highlighting with glow effect
- Measurement tools overlay
- Layer/visibility panel on side

##### 3. Results Viewer
- Tabular results (stresses, deflections, reactions)
- Color-coded results (green = safe, orange = warning, red = unsafe)
- Export to PDF, Excel, HTML
- Printable reports with professional formatting

##### 4. Analysis Output
- Load cases displayed in tree structure
- Result sets with filtering and sorting
- Graphical display of stress distribution
- Animation playback of deformations
- Section cuts and slicing tools

##### 5. Drawing/Sketch Tools
- Member creation: columns, beams, plates
- Node definition and connectivity
- Load application interface
- Constraint (support) definition
- Quick dimension and annotation

#### Icon System
- **Source:** Heroicons or Material Design Icons (structural engineering variants)
- **Style:** Outlined, 2px strokes, 24x24px default
- **Structural Engineering Icons:**
  - Column/Vertical Member: ⬆️ → Formal icon
  - Beam/Horizontal Member: ➡️ → Formal icon
  - Support/Fixed: 🔒 (locked) or anchor icon
  - Load: Arrow pointing (direction-sensitive)
  - Stress/Results: Heatmap or gradient icon
  - Validation/Check: Checkmark (green)
  - Error/Failure: X mark (red)

#### Interaction Patterns

##### Input Validation
- Real-time validation with green checkmark when valid
- Inline error messages (below field) in red
- Unit validation (ensure compatible units)
- Range checking (e.g., section depth must be > 0)
- Cross-field validation (e.g., mesh size < member dimension)

##### Toolbar Organization
- **Main Toolbar:** File, Edit, View, Tools, Solve, Post-Process, Help
- **Drawing Toolbar:** Create (vertical or horizontal), floating palette
- **Results Toolbar:** Load Cases, Result Sets, Display Options
- **View Toolbar:** Zoom, Pan, Fit to View, Wireframe/Shaded

##### Keyboard Shortcuts (Professional Standards)
- `Ctrl+N` - New Project
- `Ctrl+O` - Open
- `Ctrl+S` - Save
- `Ctrl+Z` - Undo
- `Delete` - Delete selected element
- `F1` - Help / Documentation
- `F5` - Run Analysis
- `F6` - Post-Process / Results
- `Space` - Pan viewport (press + drag)
- `Shift` - Multi-select

##### Right-Click Context Menus
- Element-specific options (Edit, Properties, Delete, Copy)
- Viewport: Zoom options, visibility toggles
- Tree nodes: Expand/collapse, organize, rename

#### Data Grid Best Practices
- Sortable columns with visual indicators
- Filterable rows with search bar
- Alternating row colors (subtle stripe for readability)
- Frozen header row and first column (for large grids)
- Inline editing with Tab to next cell
- Decimal places standardized (typically 4-6 for SI units)
- Results color-coded by safety factor or stress ratio

#### Performance & Rendering
- Large model support: 100k+ nodes/elements
- GPU acceleration for 3D rendering
- Asynchronous analysis solver (non-blocking UI)
- Progress bar for long-running operations
- Memory-efficient data structures
- Lazy-loading for large result sets

---

## UI Anti-Patterns (Never Use)
- ❌ Bright neon colors or gradients (unprofessional)
- ❌ Decorative animations or transition effects
- ❌ Emoji as icons (use proper Heroicons or Material icons)
- ❌ Inconsistent units or missing unit labels
- ❌ Unexplained technical jargon without tooltips
- ❌ Non-standard keyboard shortcuts
- ❌ Missing undo/redo functionality
- ❌ Results without safety factor or stress ratio indicators
- ❌ Modal dialogs without close button (X) or Cancel option
- ❌ Overflow text without truncation or ellipsis (…)

---

## WPF Implementation Guidelines

### Recommended NuGet Packages
- `MahApps.Metro` - Modern WPF UI framework
- `HandyControl` - Rich WPF control library
- `Extended.Wpf.Toolkit` - Additional WPF controls
- `OxyPlot.Wpf` - Scientific plotting and charting
- `HelixToolkit.Wpf` - 3D visualization (OpenGL)
- `Prism.Wpf` - MVVM architecture framework
- `MaterialDesignInXamlToolkit` - Material Design for WPF
- `CommunityToolkit.Mvvm` - MVVM helpers

### WPF Styling Approach
- Use **merged dictionaries** for consistent theming
- Define all colors, brushes, and styles in ResourceDictionary
- Apply consistent `DataTemplate` for tree views (analysis tree)
- Use `Converters` for value formatting (e.g., 3-decimal places)
- Style `DataGrid` with alternating row brushes and frozen columns

### MVVM Architecture (Recommended)
```
ProjectRoot/
├── Models/
│   ├── Structure.cs (domain model)
│   ├── Material.cs
│   └── LoadCase.cs
├── ViewModels/
│   ├── MainViewModel.cs
│   ├── AnalysisViewModel.cs
│   └── ResultsViewModel.cs
├── Views/
│   ├── MainWindow.xaml
│   ├── AnalysisView.xaml
│   └── ResultsView.xaml
├── Services/
│   ├── AnalysisSolver.cs
│   ├── FileService.cs
│   └── ExportService.cs
└── Resources/
    ├── Themes/
    │   └── Colors.xaml (central color definitions)
    └── Icons/
        └── *.xaml (vector icon resources)
```

### Accessibility (WCAG 2.1 Level AA)
- ✓ All buttons must have text labels (or aria-labels for icons)
- ✓ Keyboard navigation: Tab, Shift+Tab, Arrow keys work
- ✓ Color contrast: Text ≥ 4.5:1 on background
- ✓ Focus indicators: Visible when tabbing through controls
- ✓ Tooltips: Explain all technical buttons and abbreviations
- ✓ Error messages: Clear, actionable, not color-only

---

## Pre-Delivery Checklist

### Design & UX
- [ ] All dimensions, loads, and results include units
- [ ] Color scheme limited to palette above (no random colors)
- [ ] Icons from Heroicons or Material Design (no emoji, no custom art)
- [ ] Keyboard shortcuts documented in Help menu
- [ ] Undo/Redo functionality implemented for all edits
- [ ] Context menus (right-click) available where appropriate
- [ ] Hover tooltips explain abbreviated abbreviations (e.g., "FBD" = Free Body Diagram)

### WPF Implementation
- [ ] XAML code is clean, no code-behind logic (MVVM pattern)
- [ ] Resource dictionary centralizes all colors and brushes
- [ ] Data binding uses INotifyPropertyChanged or ObservableCollection
- [ ] Converters format numbers (e.g., "{Binding Value, StringFormat='F4'}")
- [ ] No hardcoded values—use configuration files for tolerances
- [ ] Window layout saves/restores on close/open

### Data & Analysis
- [ ] Input validation prevents invalid units or negative sections
- [ ] Error messages are user-friendly (e.g., "Beam depth must be > 50mm")
- [ ] Results export includes all assumptions and input parameters
- [ ] Calculation logs available for verification
- [ ] PDF/Excel export uses professional report template

### Accessibility & Standards
- [ ] Text contrast: 4.5:1 minimum (WCAG AA)
- [ ] All buttons keyboard-accessible
- [ ] Focus indicators visible when tabbing
- [ ] Screen reader friendly (alt text, aria labels)
- [ ] `prefers-reduced-motion` respected (no gratuitous animations)

### Performance & Reliability
- [ ] Large models (100k+ elements) load without freezing
- [ ] Analysis runs asynchronously (progress indicator shown)
- [ ] Memory usage monitored; no leaks after repeated operations
- [ ] Undo/Redo stack limited to prevent runaway memory

### Testing
- [ ] Unit tests for solver logic
- [ ] Integration tests for file I/O
- [ ] UI tests for critical workflows
- [ ] End-to-end test with realistic large model

---

## Example Workflow: Steel Beam Design

```
1. User launches application
   ↓ Shows blank canvas with toolbar and empty properties grid
   
2. User creates two columns (vertical members)
   ↓ Columns appear in 3D viewport, listed in tree panel
   
3. User creates a beam connecting the columns
   ↓ Beam appears, properties auto-populate with defaults
   
4. User sets beam properties: material (A36 Steel), section (W12x26)
   ↓ Validation shows ✓ (green) when valid section selected
   
5. User applies downward load: 50 kN @ mid-span
   ↓ Load arrow renders at correct position/scale
   
6. User defines support conditions: pin supports at columns
   ↓ Lock icons appear at column bases
   
7. User clicks "Run Analysis" (F5)
   ↓ Solver runs asynchronously, progress bar shown
   ↓ Results tab auto-opens when complete
   
8. User reviews results: stresses, deflections, reactions
   ↓ Color legend: Green (safe, SF > 1.5), Orange (warning, SF 1.0-1.5), Red (unsafe, SF < 1.0)
   
9. User exports report to PDF
   ↓ Professional report generated with all input/output, assumptions
```

---

## Integration with Claude / Copilot

When generating code or designs, the assistant will:
1. **Automatically generate** a complete design system (colors, typography, components)
2. **Provide WPF XAML** and C# code following MVVM pattern
3. **Include validation logic** for all inputs
4. **Generate keyboard shortcuts** and accessibility features
5. **Create a checklist** tailored to this specific feature
6. **Suggest NuGet packages** for dependencies
7. **Propose testing strategies** for structural analysis features

---

## Resources
- [WPF Documentation](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/)
- [Material Design in XAML Toolkit](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit)
- [Prism Framework](https://prismlibrary.com/)
- [HelixToolkit for 3D](https://www.helix3d.org/)
- [Structural Analysis Solver Reference](https://en.wikipedia.org/wiki/Finite_element_method)
- [Engineering Code Standards](https://www.aisc.org/) (AISC for Steel)

---

## Version & Updates
**Version:** 1.0 (Specialized Structural Engineering Edition)  
**Last Updated:** 2026-09-05  
**Maintained By:** Structural Engineering Design Team  
**License:** Project-specific (based on UI UX Pro Max open-source license)
