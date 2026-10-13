Welcome to the **Mcotis User Guide** — the reference for every function of Mcotis (Meinhardt Cities of Tomorrow Integrated Structures). Each article explains one function: where to find it, what it does, how to use it and what to watch out for. Use the contents on the left, or type in the search box; blue links lead to other articles.

New to Mcotis? Start with [[Introduction to Mcotis]], then [[The main window]] and [[Typical workflow]].

The guide follows the menu bar: [[Configurations|File]] (models, import and export), Define (materials, sections, loads), Edit (the parametric systems), Assign, Run (analysis), Result and Display, Design (columns), Optimization and MB (the SAP2000 candidate study). The Reference articles cover [[Units and sign conventions]], [[Design codes and references]], the [[Glossary]] and the [[Limitations and known issues]].

The guide is docs/USER_GUIDE.md in the repository; it is built into the app, so it always matches the version you run. Whoever adds or changes a function updates its article (see [[Help and development plans]]).

# Introduction to Mcotis
Category: Getting started
Where: The whole application
See also: The main window; Typical workflow; Glossary

Mcotis is a desktop application for designing long-span structures for the Cities of Tomorrow projects: it builds a parametric structural system, analyses it with its own solver, checks and designs its members, and searches the design space for the cheapest system that meets the criteria. It works on its own, and it exchanges models with SAP2000 and ETABS.

## What Mcotis does
- **Parametric systems.** The Vierendeel / cable frame (VCF) and the tied arch system (TAS) are built from a few parameters — bays, spans, heights, storeys and loads — see [[VCF System]] and [[Tied Arch System]].
- **Analysis.** [[Run FEM Analysis]] solves the model with the Mcotis solver: frames, tension-only cables with sag, cable target forces and staged nonlinear load cases.
- **Results.** Deformed shape, member forces and reactions on the model view, tables and HTML reports — see [[Showing results]].
- **Design.** Columns to Eurocode with the Singapore National Annexes and BCA BC4 — see [[Column design]] — with a cost [[Column design optimization]].
- **Optimization.** Thousands of system variants analysed and compared by deflection and cost — see [[Optimization VCF]] and [[Optimization TAS]].
- **SAP2000 and ETABS.** Import a model to run it in Mcotis, export the Mcotis model back — see [[Import From SAP2000]] and [[Import From ETABS]].

## The models
Mcotis opens with the tied arch system (see [[Built-in models]]). Your own work is saved as a configuration — one .cotfem.db file with the model and every setting (see [[Configurations]]).

# The main window
Category: Getting started
Where: The main window
See also: Model view and selection; Keyboard shortcuts; Display options

The main window has the menu bar at the top, a toolbar below it, the model view in the middle, a pane on the left for the page chosen from a menu, and the status bar at the bottom.

## Menu bar
File · Define · Edit · Assign · Run · Result · Post-Processing · Display · Design · Help · Optimization · MB. Menus that need a model stay disabled until one is open. Optimization is set apart and highlighted; MB holds the pages of the SAP2000 candidate study ([[MB study workflow]]).

## Toolbar
Buttons for the common commands: new / open / save configuration, run analysis, analysis options and monitor, zoom extents, zoom in and out, the 3-d, xy, xz and yz views, the Select tool, view options and the result pickers. Every button repeats a menu command and says which in its tooltip.

## Model view
The model drawn in 2D (XZ, XY, YZ) or 3D. Results are drawn on it. See [[Model view and selection]] and [[Display options]].

## Left pane
Pages chosen from Define, Assign, Run, Result or MB open here; the ✕ in the pane header hides it and gives the model view the full width.

## Status bar
Messages from the last command, and the count of selected frames, cables and points.

# Typical workflow
Category: Getting started
Where: The whole application
See also: Introduction to Mcotis; Run FEM Analysis; Column design

A typical session goes from a model to a checked, costed design.

1. **Open a model.** Mcotis opens with the tied arch system. Or use File → Load VCF…, File → Load Config…, [[Import From SAP2000]] or [[Import From ETABS]], or start blank with File → New Config ([[Configurations]]).
2. **Define.** Check the [[Materials]], [[Frame sections]] and [[Cable and tendon sections]]; the [[Load patterns]] and [[Load cases and combinations]].
3. **Shape the system.** Edit → [[VCF System]] or [[Tied Arch System]]; adjust members with Assign ([[Assign frame sections]], [[Frame local axes]]) or [[Delete objects|Delete]].
4. **Analyse.** Run → [[Run FEM Analysis]]; watch the [[Analysis monitor]].
5. **Review results.** [[Showing results]], [[Results table]], [[FEM live results]].
6. **Design.** Design → a column design ([[Column design]]); find the cheapest section with [[Column design optimization]].
7. **Explore the design space.** [[Optimization VCF]] or [[Optimization TAS]], then [[Optimization results and charts]].
8. **Save and share.** File → Save Config; [[Export to SAP2000]] or [[Export to ETABS]] for the design team.

# Model view and selection
Category: Getting started
Where: The model view in the main window; Select tool (V)
See also: Object information; Delete objects; Keyboard shortcuts

The model view draws the model and its results, and is where you select objects for Assign, Delete and Design.

## Views
The 3-d, xy, xz and yz buttons (or the drop-down in the view's corner) choose the view. Zoom with the wheel or the toolbar, fit with Zoom extents (Home). In 3D a drag rotates the model.

## Selecting
- **Click** a point, frame or cable to select it; click again to add more.
- **Drag a window** in a 2D view to select everything inside it.
- **V** is the Select tool; **Esc** clears the selection.
- The status bar counts the selected frames, cables and points.

## Using the selection
- Assign → Frame / Cable sections and Local Axes act on the selected members ([[Assign frame sections]], [[Frame local axes]]).
- **Del** or Edit → Delete removes the selection ([[Delete objects]]).
- A column design ticks the selected frames as its members ([[Column design]]).

## Information
Right-click an object for its information; double-click a member to change its assignments — see [[Object information]].

# Keyboard shortcuts
Category: Getting started
Where: The main window
See also: Model view and selection; Showing results

The keys of the main window, for the commands used most often.

- **F1** — open this User Guide.
- **F4** — show the undeformed shape (clear the result display).
- **F5** — Show Deformed Shape… (choose the load case).
- **F6** — Show Member Forces… (load case and N, V or M).
- **F7** — Show Joint Reactions….
- **V** — Select tool.
- **Esc** — clear the selection.
- **Del** — delete the selected objects ([[Delete objects]]).
- **Home** — zoom extents.
- **Ctrl+Shift+S** — Save Config As….

Del, Esc and the F-keys are ignored while you type in a field or a table.

# Configurations
Category: Files and models
Where: File → New Config, Load Config…, Save Config, Save Config As…
See also: Built-in models; Native model workspace; Run FEM Analysis

A configuration is one Mcotis file (.cotfem.db) that holds the complete analysis model and its setup: geometry, objects and assignments, materials, sections, loads, load cases, analysis and output settings, and the lock state.

## Commands
- **New Config** starts a blank model. Define the materials and frame / cable sections first, then create the system under Edit → [[VCF System]], which gives the new members the sections chosen there. The start screen also offers the built-in template.
- **Load Config…** opens a configuration.
- **Save Config** saves to the active configuration; the first save asks for a file name.
- **Save Config As…** (Ctrl+Shift+S) saves under a new name; that file becomes the active configuration and the original is left as it was.

## Locked models
A run of [[Run FEM Analysis]] locks the model and stores its results with the configuration; Run → Unlock FEM Model deletes the results and allows editing again (see [[Unlock FEM Model]]).

# Built-in models
Category: Files and models
Where: File → Load VCF…, File → Load TAS…; Mcotis's start
See also: Configurations; Tied Arch System; VCF System

Mcotis carries two complete models that open and run without SAP2000 or ETABS.

## Load TAS (tied arch system)
The CoT 120 m composite arch deck, read from its ETABS file exactly as [[Import From ETABS]] would: C90/105, S690 and B500 materials; CFT, steel encased and Section Designer sections; end releases, pinned bases, 6 load patterns, 9 load cases and 8 combinations. Straight members that ETABS splits at every storey are joined back (30 joints, 59 frames, the same results). Mcotis opens with this model; its title is "TAS - Tied Arch System".

## Load VCF
The default VCF model, COT Framing_V1.2_100m, on 07 FEM, ready to run.

Either command sets the current model aside after a confirmation.

# Native model workspace
Category: Files and models
Where: File → Open Native Model…; Edit → TemplateBuilding…
See also: TemplateBuilding; Run FEM Analysis

The native workspace is a separate window for 3D building frames in the standalone .mcotis format, with its own Save / Open, Undo / Redo, geometry view, degrees-of-freedom settings and Run Analysis (linear static frame results, and cable results for models with cables).

Limits: no slabs or diaphragms; frames up to about 600 free degrees of freedom. Create a model with [[TemplateBuilding]] or open one with File → Open Native Model….

# Import From SAP2000
Category: Files and models
Where: File → Import From SAP2000
See also: Export to SAP2000; Read FEM properties from SAP; Configurations

Brings the model open in SAP2000 into Mcotis in one read-only step, ready to run. SAP2000 is never written.

## What is imported
- Geometry: joints, frames and cables.
- Every member's properties: section, material, modifiers, end releases, local axis.
- The material and section libraries as SAP2000 defines them.
- Load patterns with their loads, static load cases and combinations.
- **Tab 02 chord groups**: the TTrPt / MTrPt / BTrPt joints, or else the three highest levels of horizontal frames; the baseline is captured.
- **Tab 03 cables**: every cable with a target force in SAP2000 (pattern, force, section).
- **Tab 01 loading**: fitted to the SAP2000 joint loads — a pattern whose vertical joint loads are uniform per tributary length becomes a qz on those joints, so the run applies exactly SAP's loads.

## Before you use it
- Open the model in SAP2000 first; it must be in kN, m units.
- Importing replaces the current model and its tabs (you are asked first).
- The fitted Tab 01 uses B = 1 m and 1 storey; set the real tributary width and storeys before a parametric study.

A summary lists each step and says whether Run FEM Analysis is ready, or what is missing.

# Export to SAP2000
Category: Files and models
Where: File → Export to SAP2000…
See also: Import From SAP2000; Export to ETABS

Writes the Mcotis model into the model open in SAP2000, so it runs there.

## How it works
1. **Preview.** Mcotis reads the SAP2000 model and lists what the export would change — members removed and added, joints moved and added, materials, sections, patterns, cases and combinations added. Nothing is written yet; a model that cannot be exported (missing section, coinciding joints, unknown names, units other than kN-m) is refused here.
2. **Confirm.** SAP2000 members and joints that are not in Mcotis will be removed. A locked SAP2000 model must be unlocked, which deletes its analysis results.
3. **Export.** Materials, sections (SAP's own Section Designer sections are kept), geometry, each member's section / local axis / releases / modifiers, pins on unrestrained supports, load patterns, the Tab 01 joint loads, cable target forces, load cases (new nonlinear ones with P-Δ and large displacements), combinations and run flags.

## Not saved
The SAP2000 model is not saved: review it, then save — or close it without saving to discard the export.

The SAP2000 calls are checked against SAP2000 27's API but have not yet been run against a live SAP2000: try it on a copy of a model first (see [[Limitations and known issues]]).

# Import From ETABS
Category: Files and models
Where: File → Import From ETABS (without a running ETABS: an .e2k file)
See also: Export to ETABS; Built-in models

Reads the model open in ETABS (read-only, through its database tables), or an .e2k file when ETABS is not running, into an offline Mcotis model ready to run.

Imported: joints (named label@story), frames, materials, sections (I, box, pipe, CFT, steel encased, Section Designer with ETABS's own area, inertia and true self-weight), end releases, loads, load cases and combinations; Tab 01 is fitted to the ETABS joint loads. Frames stay split as ETABS splits them (File → Load TAS… joins them back for the built-in model).

# Export to ETABS
Category: Files and models
Where: File → Export to ETABS…
See also: Import From ETABS; Export to SAP2000

Writes the Mcotis model into the model open in ETABS after a read-only preview and your OK: frames matched to ETABS frames by their end coordinates, sections, materials, releases, supports, every joint and frame load, load cases and combinations, plane-frame analysis. CFT, encased and Section Designer sections go as general sections with their true self-weight. The ETABS model is not saved.

Cables go as tension-only frames **without their pretension** — ETABS has no cable target-force load (see [[Limitations and known issues]]).

# Read FEM properties from SAP
Category: Files and models
Where: File → Read FEM Properties from SAP
See also: Import From SAP2000

With a connected SAP2000 model, reads its sections, materials (unit weight and mass), modifiers, end releases, load patterns with their loads, static load cases and combinations — read-only — and resets the Loads tables to SAP2000. [[Import From SAP2000]] does this and more in one step.

# Materials
Category: Define
Where: Define → Materials…
See also: Frame sections; Units and sign conventions

The material library of the model: steel, concrete, rebar, tendon and others, with E, Poisson's ratio, unit weight and mass, strengths and the **unit cost**.

## Using it
- **Get From SAP2000** reads every material of the open SAP2000 model.
- **Add New / Add Copy / Modify/Show / Delete** manage materials; a change applies to the FEM members that use the material (E, unit weight, unit mass).
- A standard library is always offered: S355, S460, S690; C70/85 to C185/200.

## Unit costs
Each material carries a unit cost (SGD per tonne; concrete also per m³) with its basis. Defaults: concrete from the BCA 2025 average (Grade 40 ≈ 121 SGD/m³) and project rates by strength; steel from the S460M (4,860 SGD/t) and S690QL (7,500 SGD/t) project rates by yield strength; rebar 690 SGD/t; cable strand 12,000 SGD/t. Costs feed [[Optimization VCF]], [[Optimization TAS]] and [[Column design optimization]]; enter your own rates where you have them.

# Frame sections
Category: Define
Where: Define → Section Properties → Frame Sections…
See also: Materials; Assign frame sections; Member sections table

The frame section library: rectangle, box/tube, I, pipe, circle, channel, tee, angle, general (Section Designer and other shapes with SAP2000's properties) and the composite sections:

- **Concrete filled tube (CFT)** — a steel tube filled with concrete, optionally with bars inside.
- **Steel encased** — an I section encased in reinforced concrete.
- **Mega composite** — several encased I profiles in one large column.

Composite sections get transformed properties (as SAP2000 transforms them to the concrete) and a cost per metre. Add, copy, modify or delete sections; a change applies to the members that use the section.

# Cable and tendon sections
Category: Define
Where: Define → Section Properties → Cable Sections… / Tendon Sections…
See also: Frame sections; Assign cable sections

Cable sections are defined by material and area. **Strand Bundle** works a cable out from its strands — number × area of one strand — and names it by the convention, e.g. 192T15.7 (192 strands of 15.7 mm). Tendon sections are kept for SAP2000 models; the Mcotis solver analyses frames and cables, not tendons.

# Member sections table
Category: Define
Where: Define → Section Properties → Member Sections Table…
See also: Frame sections; Object information

A table of every member the analysis uses with its section, material and properties (area, inertia, E, unit weight, modifiers, releases, local axis).

# Load patterns
Category: Define
Where: Define → Load Patterns
See also: Load cases and combinations; VCF System; Tied Arch System

The load patterns of the model with their type and self-weight multiplier. The loads of a parametric system are set in the system editor: Edit → VCF System → Storey Force and Cable Force, or Edit → Tied Arch System.

# Load cases and combinations
Category: Define
Where: Define → Load Cases / Load Combinations
See also: Load patterns; Run FEM Analysis

The load cases the analysis solves and the combinations of their results.

## Load cases
Linear or nonlinear static cases, each a factored list of load patterns (for example 1.35 DEAD + 1.5 LL). A nonlinear case can **continue from** another (staged: its loads act on the deformed, prestressed structure). The **Default Setup** is Pretension → SLS / ULS: the Pretension case fits the cables to their target forces with the self-weight, and the service and ultimate cases continue from it. Cases saved from SAP2000 or ETABS are kept, and you can add your own.

## Combinations
Linear additive combinations of cases (and other combinations).

## Running
Before each run you choose which cases to run (Set Load Cases to Run).

# VCF System
Category: Edit
Where: Edit → VCF System…
See also: Optimization VCF; Load patterns; Built-in models

The parametric editor of the Vierendeel / cable frame: bay count and width, truss depth (middle chord at half depth), clear height, back-span and back-stay height, and the section of each chord, mega column and cable — with a live preview. Apply writes the system to the model (SAP2000 is not changed).

## Storey Force
The tributary width, number of storeys and the qz of each load pattern, and the loaded points (picked from a list or on the preview). Each loaded point gets −qz × tributary length × width × storeys.

## Cable Force
The cable target forces of the Pretension pattern.

In a blank configuration the editor starts from the built-in VCF layout and creates the members with the sections you choose.

# Tied Arch System
Category: Edit
Where: Edit → Tied Arch System…
See also: Optimization TAS; Built-in models

Builds the tied arch system (TAS) from a few parameters, with a live preview.

## Geometry
- **H1** — support to arch crown; **H2** — support to top chord (deck), above the crown.
- **Bays** (even, 6–40, default 12) and their **spacing** (default 10 m); span L = bays × spacing.
- **With elevated cable**: the arch springs at **H0** (default 10 m) from two columns of the arch section, pinned on the ground, and a cable TIE (192T15.7) ties the springing points; H1 and H2 are then measured from the springing.

The arch is the parabola through the supports and the crown, with a joint at every vertical line; the layout, sections, releases, patterns, cases and combinations are those of the TAS model.

## Storey Force
SW, SDL, LL (kPa), floors n and tributary width B. The form shows the point load on each top chord joint: SLS = (SW + SDL + LL) × s × B × n and ULS = (1.35 SW + 1.35 SDL + 1.5 LL) × s × B × n (half at the end joints).

## Cable Force
With an elevated cable, the tie's target force (default 20,000 kN) in the Pretension pattern; the service and ultimate cases continue from the prestressed state.

With the defaults, Apply builds exactly the model of File → Load TAS….

# TemplateBuilding
Category: Edit
Where: Edit → TemplateBuilding…
See also: Native model workspace

Creates a building frame in a new [[Native model workspace]]: per-bay X and Y widths, per-storey heights, downward beam line loads, general section properties, fixed or pinned bases and optional self-weight.

# Delete objects
Category: Edit
Where: Edit → Delete (Del key)
See also: Model view and selection; Configurations

Removes the joints, frames and cables selected on the model view, after an OK / Cancel confirmation. There is no undo — save first if you may want the model back.

- As in SAP2000, deleting a joint also deletes the members connected to it; deleting a member leaves its end joints.
- Everything that names a removed object follows: member properties, loads on removed objects, Tab 01 loaded points and point loads, the Tab 02 chord groups (the baseline is captured again; if a group becomes invalid the message says so), Tab 03 cables (a cable that followed a removed one becomes independent) and the output lists.
- FEM results are cleared and supports are worked out again from the new geometry.
- The model becomes an offline "· edited" model; SAP2000 is never changed.

The Del key does nothing while you type in a field or a table.

# Joint restraints
Category: Assign
Where: Assign → Joint → Restraints (Pin Supports)…
See also: Run FEM Analysis

The supports of the model. In the 2D analysis the lowest joints are pinned automatically; the page shows which joints are restrained.

# Assign frame sections
Category: Assign
Where: Assign → Frame → Frame Sections…
See also: Frame sections; Model view and selection

Assigns a library section to the frames selected on the model view — select them first, as in SAP2000. Area, inertia, material and outline follow the section; each frame keeps its local axis and modifiers. FEM results are cleared.

# Assign cable sections
Category: Assign
Where: Assign → Cable → Cable Sections…
See also: Cable and tendon sections

Assigns a cable section to the selected cables; Tab 03's section, area and capacity follow.

# Frame local axes
Category: Assign
Where: Assign → Frame → Local Axes…; Display → Frame local axes
See also: Assign frame sections; Display options

Rotates the local 2 / 3 axes of the selected frames by an angle; the in-plane stiffness of the 2D analysis rotates with it (I33 cos² + I22 sin²). Display → Frame local axes draws the axes; select frames to show only theirs.

# Object information
Category: Assign
Where: Model view — right-click an object; double-click a frame or cable
See also: Model view and selection; Assign frame sections

- **Point Information** (right-click a point): coordinates, the members meeting at it, its support and its loads.
- **Frame / Cable Information** (right-click a member): geometry, assignments and loads; the **Assignments** tab changes the section, local-axis angle and end moment releases of a frame, or the section, target pattern and target force of a cable. OK / Cancel / Apply; only what you change is written. A double-click opens the form on Assignments.

A locked model shows the form read-only.

# Run FEM Analysis
Category: Run
Where: Run → Run FEM Analysis
See also: Analysis options; Analysis monitor; Load cases and combinations; Unlock FEM Model

Solves the model as it is defined with the Mcotis solver. SAP2000 and ETABS are not read or written.

## The solver
- 3D frames connected at every point lying on them; the main window's model is solved in its XZ plane.
- Tension-only cables, with sag when the option is on.
- Cable target forces fitted in the case set to fit them (the Pretension case), within the fitting tolerance.
- Staged nonlinear cases (a case continuing from another) and internal forces along every frame.

## Running
Choose the load cases to run, then Run; Run → Cancel FEM Analysis stops a run. The [[Analysis monitor]] shows the run, every load case with its convergence and numerical stability, and the cable convergence. A finished run locks the model and stores its results ([[Unlock FEM Model]]). Results: [[Showing results]].

## Limits
Small displacements, no P-Δ, no shells or modal analysis, about 1,000 joints (see [[Limitations and known issues]]).

# Analysis options
Category: Run
Where: Run → Analysis Options…
See also: Run FEM Analysis

The active degrees of freedom, the cable fitting tolerance and iteration limit, the frame mesh (segments per frame) and cable sag.

# Analysis monitor
Category: Run
Where: Run → Analysis Monitor
See also: Run FEM Analysis

The run monitor follows one run of [[Run FEM Analysis]]: the run on top, then every load case with its convergence and its numerical stability. It opens with each run, and Run → Analysis Monitor opens it again.

## The run
The status of the run — Running, Completed, Completed with Warnings, Failed or Cancelled — a bar that counts the load cases ended, the degrees of freedom as the equations were assembled (active | solved | restrained), the start time and the elapsed time. A run that failed shows the reason under them.

## Load Cases
- **Numerical stability — information.** A note on how numerical stability is reported, with the thresholds in force. Click its heading to close it.
- **Model information.** The file the model is in and its counts: joints, restrained joints, frames, cables, shells, load patterns, joint and member load assignments, load cases and load combinations, and the supports by name.
- **Load cases.** One entry per solve of the run: the fit of the cable target forces of a pretension stage first, then its cases. A load combination is a superposition of case results, not a solve, so it has no entry. Each entry shows its number, name, status and time (hours:minutes:seconds).

A load case reads Pending, Running, Converged, Converged with Warnings, Not Converged, Numerical Instability, Failed or Cancelled. Click its header to open it; several can stay open, and a case that failed opens itself. Open, it shows:

- **Convergence**, **Numerical Stability** and **Instabilities** — the number of degrees of freedom reported as a severe instability or a singularity. 0 means none was detected; the out-of-balance and the residuals are shown beside it as measured.
- The details: analysis type, equations solved, iterations, whether the convergence tolerance was satisfied, the out-of-balance against the accepted value, joints in equilibrium, fitting runs and whether the cable target forces were met, and the largest loss of accuracy.
- **The affected joints**, when a degree of freedom is reported: the joint, its global coordinates in m, and the six columns UX UY UZ RX RY RZ with the digits of accuracy lost — amber from 6, red above 11 — and the remarks naming the degrees of freedom.
- **Analysis log** — what the solver reported of the case, in order, with the time of each line. The text can be selected and copied.

## Reading the table
- A number is the estimated digits of accuracy lost at that degree of freedom during the matrix solution.
- **∞** — a singularity was identified there: no stiffness and no restraint, a joint held by slack cables only, or a movement the stiffness does not resist ("possible mechanism").
- **-** — the degree of freedom is not active: UY, RX and RZ in the XZ plane, and the rotation of a joint only cables reach.
- **R** — restrained by a support. **N/A** — active, but without an estimate.

Numerical Instability and Not Converged are different outcomes: the first is a stiffness the solver cannot rely on, the second an equilibrium iteration that did not converge. For an instability, review the joints listed for missing restraints, disconnected members, mechanisms or very different stiffnesses meeting at a joint.

## Cable Target-Force Convergence
Each cable's result / target per fitting run, for the stage that fits the cable target forces.

# Unlock FEM Model
Category: Run
Where: Run → Unlock FEM Model
See also: Run FEM Analysis; Configurations

Deletes this revision's FEM output databases, clears Post-Processing and allows the model to be edited again.

# Showing results
Category: Results
Where: Result → Show Deformed Shape… (F5), Show Member Forces… (F6), Show Joint Reactions… (F7), Show Results…
See also: Results table; Display options; FEM live results

Draws the results of the selected load case on the model view.

- **Deformed shape** (F5) — choose the load case.
- **Member forces** (F6) — choose the load case and N, V or M.
- **Joint reactions** (F7).
- **Show Results…** — choose the case / combination and the output.
- Quick items: axial forces / cables, bending moments, shear forces, support reactions, deflection, cable target fit.
- **F4** shows the undeformed model again.

Values are written in compact units (MN, MN·m) with a fixed number of decimals; the stored results stay in kN and kN·m.

# Results table
Category: Results
Where: Result → Results Table…
See also: Showing results

The results of the selected load case and output as a table, browsed by load case.

# FEM live results
Category: Results
Where: Post-Processing → FEM Live Results (HTML)…
See also: Output settings; Showing results

An interactive HTML report of the run: switch load cases and result families, inspect values, zoom and pan.

# Output settings
Category: Results
Where: Post-Processing → Output Settings
See also: FEM live results; 04 Output Settings

The result cases, monitored objects (deflection points, foundation points, cables, member-force groups) and limits used by the results and reports.

# Display options
Category: Results
Where: Display → Show Undeformed Shape, Frame mesh, Member size, Frame local axes, Dimensions, Loading, View Options…
See also: Showing results; Model view and selection

- **Show Undeformed Shape** (F4) — clear the result display.
- **Frame mesh** — frames drawn in their analysis mesh segments.
- **Member size** — each member labelled with its section.
- **Frame local axes** — the local 1 / 2 / 3 axes.
- **Dimensions** — bay spans, overall span, level heights and elevations; for an arch, the arch height H.
- **Loading** — the Tab 01 load arrows, labelled with Fz after a run.
- **View Options…** — the full display form.

# Column design
Category: Design
Where: Design → RC Column / CFT (Concrete Filled Tube) / Steel Encased Composite Column / Mega Composite Column Design…
See also: Column design optimization; Design results; Design codes and references

Designs columns with the forces of the FEM run: pick a design from the Design menu, confirm the design basis, section and members, and Run Design.

## The Design Reference dialog
- **Design Basis** — the design code and its references, the method, the Singapore National Annex, the effective lengths l0,y (major, in-plane) and l0,z (minor) in m, the partial factors (γC, γS, γM0) and the grade limits.
- **Section** — the section and materials (mm, MPa), filled from the member's FEM section; From Member Section fills them again.
- **Members & Loads** — the members to design (frames selected on the model view are ticked; Suggested / All / None) and the load cases.
- **Optimization** — the cheapest section ([[Column design optimization]]).

## Assumptions
The 2D model's moment bends the major axis (depth h in the model plane); the minor axis carries imperfection moments only. One section is checked for every ticked member, each with its own forces.

Run Design opens the [[Design results]]. The designs: [[RC column design]], [[CFT column design]], [[Steel encased column design]], [[Mega column design]].

# RC column design
Category: Design
Where: Design → RC Column Design…
See also: Column design; Column design optimization

Reinforced concrete columns to SS EN 1992-1-1 (EC2) with the Singapore National Annex.

- N–M resistance by strain compatibility (rectangular stress block, high-strength factors of Table 3.1).
- Second order by nominal curvature (5.8.8) or nominal stiffness (5.8.7); imperfection l0 / 400; minimum eccentricity.
- Biaxial check (5.8.9, Eq. 5.39), shear (6.2) and detailing (9.5: minimum and maximum steel, bar size, spacing, links).

Section inputs: b × h, cover, bars per face, bar and link size and spacing, fck, fyk, αcc, effective creep.

# CFT column design
Category: Design
Where: Design → CFT (Concrete Filled Tube) Design…
See also: Column design; Column design optimization; Frame sections

Concrete filled steel tubes (rectangular or circular) to SS EN 1994-1-1 6.7.3 with the Singapore National Annex, extended to high-strength materials by BCA BC4:2025.

- Plastic N–M interaction; confinement of circular tubes; effective stiffness and buckling curve a.
- Second order with the member imperfection; αM; biaxial interaction; shear of the steel tube.
- Local buckling (d/t, h/t), steel contribution ratio, slenderness and grade scope.
- Optional **bars inside the tube** (Section tab, Bars inside the tube): bars per face (or on the circle), size and cover to the tube's inside face.

# Steel encased column design
Category: Design
Where: Design → Steel Encased Composite Column Design…
See also: Column design; Column design optimization

An I section encased in reinforced concrete, to SS EN 1994-1-1 6.7.3 extended by BCA BC4:2025: plastic N–M with 0.85 fcd, buckling curves b (y-y) and c (z-z), second order, biaxial interaction, shear of the web, cover to the steel section, and minimum / maximum longitudinal steel.

# Mega column design
Category: Design
Where: Design → Mega Composite Column Design…
See also: Column design; Column design optimization

Columns with several concrete-encased steel profiles (4, or 6 with two on the bending axis), by the CTBUH / ArcelorMittal (2016) simplified method on SS EN 1994-1-1 6.7.3, with high-strength materials per BCA BC4:2025. The report shows the method and a fibre cross-check; detailing covers web bars and profile cover. The default section is 2200 × 2200, C100, 4 × I 500 × 430 × 60 / 37 S460 with Ø40 bars.

# Design results
Category: Design
Where: Design → a column design → Run Design
See also: Column design

A summary per member — governing case, NEd, MEd,y, MEd,z, VEd, the axial, major, minor / biaxial and shear utilisations and the status — above the step-by-step calculation of the selected member with clause references.

- **OK** — every utilisation ≤ 1.00 and the detailing passes.
- **CHECK** — utilisations pass but a detailing rule does not.
- **FAIL** — a utilisation above 1.00. **ERROR** — no forces for the member.

Copy Report / Save Report… write the summary and every calculation as text.

# Column design optimization
Category: Design
Where: Design → any column design → Optimization tab
See also: Column design; Materials; Design results

Finds the cheapest section of the column type that passes every ticked member and load case, and gives its cost per metre of column.

## Before you start
Run the FEM analysis, open the column design and tick the members on Members & Loads — one section is sized for all of them. The effective lengths are those of Design Basis (also editable on the tab).

## What you can vary
- **Concrete outline** h × b (RC, steel encased, mega; square or not).
- **Steel tube** H × B or D and wall thickness t (CFT).
- **Encased I section** h, b, tf, tw (steel encased, mega); for mega columns also 4 and / or 6 profiles placed from an edge distance (face of the concrete to the profile).
- **Grades**, stepped grade by grade: steel S235 – S690 (not for RC) and concrete C30/37 – C185/200. Steel fy follows the plate thickness (tube wall or flange) by the EN 10025 bands.
- **Bars**: sizes, bars per b face, the h faces at the same spacing or a range, and cover (optional inside a CFT tube).

Each range is min / max / step. The starting ranges come from the section on the Section tab.

## Costs
Unit costs come from the material library (steel SGD/t per grade, concrete SGD/m³ per grade, rebar SGD/t) and can be edited on the tab. Cost per metre = steel and bars by mass + concrete by net volume, with the links of RC, encased and mega columns. Formwork is not priced.

## Run
Run prices every combination, sorts them cheapest first and designs them in that order (as Run Design) until the 10 cheapest passing sections are found — so number 1 is the cheapest section that passes. It runs in the background; Stop ends it. Sections that cannot be built (a wall too slender for its steel, a profile not fitting, bars without room) are left out first. More than 250,000 combinations are refused: narrow a range or use larger steps.

## Results
Rank, section, steel (with the fy used), concrete, bars, kg/m, m³/m, SGD/m, utilisation and the governing member and case. **Use Selected** (or a double-click) puts the section on the Section tab; Run Design then gives its full report.

# Optimization VCF
Category: Optimization
Where: Optimization → Optimization VCF…
See also: Optimization results and charts; VCF System; Materials

The parametric study of the VCF system: every combination of geometry, cable force, storey force and section variants is built in memory and analysed with the Mcotis solver.

- **Ranges** (min / max / step): bays, bay width, truss depth (middle chord at half depth), clear height, back-span, back-stay height; cable forces; storey loads.
- **Sections**: candidate sections per chord, mega column and cable.
- **Check**: max deflection of a bottom chord point in the chosen result ≤ span / ratio, and the uplift limit.
- **Cost**: Σ member cost per metre × length.

Run Optimization lists each case as it completes; the working model and SAP2000 are not changed. Results: [[Optimization results and charts]]. Optimization → Cancel Optimization stops a run.

# Optimization TAS
Category: Optimization
Where: Optimization → Optimization TAS…
See also: Optimization results and charts; Tied Arch System

The parametric study of the tied arch system, run like [[Optimization VCF]].

- **Geometry**: H1, h (H2 = H1 + h, so the deck stays above the crown), bays (an odd number is skipped) and spacing; H0 with an elevated cable.
- **Storey Force**: SW, SDL, LL, floors n and tributary width B.
- **Cable Force** (elevated cable): the tie's pretension in steps.
- **Sections**: candidates per role — arch rib, top chord, mega column, end strut, strut, brace, tie.
- **Check**: max deflection of a top chord joint ≤ L / ratio (480 mm at L / 250 for the 120 m TAS), and the uplift limit.

Needs the tied arch system open (File → Load TAS… or Edit → Tied Arch System…); the open model is not changed.

# Optimization results and charts
Category: Optimization
Where: Optimization → Optimization Results…; after a run of Optimization VCF / TAS
See also: Optimization VCF; Optimization TAS

The results window of the last optimization run, with two sheets.

## Run
One row per case: the values of the inputs that vary, the max deflection, span, limit, δ / limit, uplift, check (OK / NG), total cost and status. Sort by any column.

## Chart
The parametric study written like the results section of a paper: up to 20 numbered figures — for every response (deflection, utilisation, base forces, cost, cost per metre, mass, cable tension, frame forces, uplift) its main effect against each varying parameter, a sensitivity ranking, cost against utilisation for every case, and an interaction plot — then a summary and numbered conclusions. Copy Summary puts the text on the clipboard.

# MB study workflow
Category: MB study (SAP2000)
Where: MB menu → 06A Optimization, 08 Quantities; Post-Processing → Study Reports…
See also: Run study; 01 Loading; 05 Iteration

**The pages 01 Loading, 02 Truss Height, 03 Pretension, 04 Output Settings and 05 Iteration, and Run → Run Study / Cancel Study, were archived on 2026-10-13 and are no longer in the application.** Their articles are kept as a record of the study; what remains in the menus is the reporting (06), the qualification (06A) and the quantities (08) of a study already made.

The MB pages ran the original candidate study on SAP2000: candidates of loading (01), truss height (02) and cable pretension (03) are combined, each candidate is built as a child SAP2000 model, analysed by SAP2000 and reported (06), qualified (06A) and quantified (08). It needs SAP2000 and a connected model; the parametric studies inside Mcotis ([[Optimization VCF]], [[Optimization TAS]]) need neither.

Each MB item shows its page's status next to its name.

# 01 Loading
Category: MB study (SAP2000)
Where: Archived on 2026-10-13 — no longer in the application (was MB → 01 Loading)
See also: MB study workflow; 02 Truss Height

The loading candidates: tributary width, number of storeys, the loaded points, and per load pattern a qz value or a min / max / step range. Each loaded point gets −qz × tributary length × width × storeys.

# 02 Truss Height
Category: MB study (SAP2000)
Where: Archived on 2026-10-13 — no longer in the application (was MB → 02 Truss Height)
See also: MB study workflow; 03 Pretension

The top, middle and bottom chord groups, the captured baseline, and the truss-height candidates (min / max / increment) by which the chords are moved.

# 03 Pretension
Category: MB study (SAP2000)
Where: Archived on 2026-10-13 — no longer in the application (was MB → 03 Pretension)
See also: MB study workflow

The cables of the study with their target-force load pattern, a single target or a min / max / step range, their section and design capacity. Sync modes let a cable follow another: Same Values, Ratio, Offset or Same Step.

# 04 Output Settings
Category: MB study (SAP2000)
Where: Archived on 2026-10-13 — no longer in the application (was MB → 04 Output Settings)
See also: MB study workflow; Output settings

The output groups prepared on the parent model, the result cases and the monitored deflection, foundation, cable and member-force objects of the study reports.

# 05 Iteration
Category: MB study (SAP2000)
Where: Archived on 2026-10-13 — no longer in the application (was MB → 05 Iteration)
See also: Run study; MB study workflow

The candidate study: the study mode, fixed candidates, the output folder, the candidate filters and the worker monitor.

# Run study
Category: MB study (SAP2000)
Where: Archived on 2026-10-13 — no longer in the application (was Run → Run Study; Run → Cancel Study)
See also: 05 Iteration; 06 Post-Processing

Validates the study and runs it from anywhere — it jumps to 05 Iteration for the worker monitor and any validation issues. One to three SAP2000 workers build and analyse a child model per candidate; built child models are reused.

# 06 Post-Processing
Category: MB study (SAP2000)
Where: MB → 06 Post-Processing; toolbar Print All Reports
See also: 06A Optimization; Run study

The raw data of the study turned into reports: cable forces with design capacities, deflections by candidate, member forces and support reactions, as interactive HTML. Print Setup and PDF export print the reports.

# 06A Optimization
Category: MB study (SAP2000)
Where: MB → 06A Optimization
See also: 06 Post-Processing

Qualifies the stored candidates against acceptance criteria — available once 06 has raw data.

# 08 Quantities
Category: MB study (SAP2000)
Where: MB → 08 Quantities
See also: MB study workflow; Materials

Concrete volume and steel mass by assigned section, read live from the connected SAP2000 model, with section figures.

# Units and sign conventions
Category: Reference
Where: The whole application
See also: Glossary; Showing results

- Model units: kN, m (SAP2000 and ETABS models must be in kN, m for import and export).
- Design inputs: mm and MPa; results in kN, kN·m.
- Display on the model view: MN and MN·m, fixed decimals; stored results stay kN and kN·m.
- Axial force: tension positive in the analysis results (as SAP2000); in column design NEd is shown compression positive.
- Costs: SGD — per tonne for steel, rebar and strand, per m³ for concrete, per metre of column in the design optimization.

# Design codes and references
Category: Reference
Where: Design menu; Design Reference → Design Basis
See also: Column design

- **SS EN 1990 / 1991-1-1 + NA** — basis of design, actions.
- **SS EN 1992-1-1 + NA** — concrete structures (RC columns).
- **SS EN 1993-1-1 + NA** — steel structures.
- **SS EN 1994-1-1 + NA** — composite structures (CFT, steel encased, mega columns).
- **BCA BC4:2025** — Design Guide for Steel-Concrete Composite Columns with High Strength Materials (extends EC4 to high-strength concrete and steel).
- **BCA BC1** — use of alternative structural steel.
- **SS EN 206, SS 560** — concrete; steel for reinforcement.
- **CTBUH / ArcelorMittal (2016)** — mega column method.
- **EN 10025-2 / -4 / -6** — steel grades and yield strength by thickness.

The default partial factors are the Eurocode recommended values (γC 1.5, γS 1.15, γM0 1.0); check them, and the grade limits, against the current documents.

# Glossary
Category: Reference
Where: The whole application
See also: Introduction to Mcotis

- **VCF** — Vierendeel / cable frame, the long-span truss with mega columns and cables.
- **TAS** — tied arch system.
- **CFT** — concrete filled (steel) tube. **SRC** — steel reinforced concrete (steel encased).
- **H0, H1, H2, h** — TAS heights: ground to springing, support to arch crown, support to deck, crown to deck.
- **L** — span. **l0,y / l0,z** — effective (buckling) lengths about the major / minor axis.
- **Target force** — the tension a cable is fitted to in the Pretension case.
- **Tab 01 / 02 / 03** — loading, truss height (chord groups) and pretension of the model (the former MB pages 01–03, archived on 2026-10-13).
- **Configuration** — one .cotfem.db file with the model and its setup.
- **Candidate** — one combination of the study's inputs.

# Limitations and known issues
Category: Reference
Where: The whole application
See also: Help and development plans

- The Mcotis solver is first-order: no P-Δ (planned), no shells, no modal analysis; about 1,000 joints in the main window.
- Export to ETABS writes cables without their pretension (planned).
- Export to SAP2000 has not yet been run against a live SAP2000 — try it on a copy first.
- Column design: the model is 2D, so the minor axis carries imperfection moments only.
- Column design optimization does not price formwork.
- Import fits Tab 01 with B = 1 m and 1 storey; set the real values before a parametric study.
- Post-Processing → Post-Processing (Optimization) — multi-candidate optimization reports — is reserved for a later release; use [[Optimization results and charts]] meanwhile.

Planned work is listed under Help → Development Plans.

# Help and development plans
Category: Reference
Where: Help → User Guide (F1), Workflow Help, FEM Procedure, Development Plans
See also: Limitations and known issues; Introduction to Mcotis

- **User Guide** (F1) — this guide.
- **Workflow Help** — the workflow overview.
- **FEM Procedure** — how the FEM analysis works.
- **Development Plans** — every function built in Mcotis, who built it, where to find it and how it is checked, and what is planned (docs/DEVELOPMENT_LOG.md).

Whoever adds or changes a function records it in the development log and updates its article in this guide (docs/USER_GUIDE.md).
