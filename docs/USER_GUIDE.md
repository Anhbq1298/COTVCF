Welcome to the **Mcotis User Guide** — the reference for every function of Mcotis (Meinhardt Cities of Tomorrow Integrated Structures). Each article explains one function: where to find it, what it does, how to use it and what to watch out for. Use the contents on the left, or type in the search box; blue links lead to other articles.

New to Mcotis? Start with [[Introduction to Mcotis]], then [[The main window]] and [[Typical workflow]].

The guide follows the menu bar: [[Configurations|File]] (models, import and export), Define (materials, sections, loads), Edit (the parametric systems), Assign, Run (analysis), Result and Display, Design (columns), and Optimization. The Reference articles cover [[Units and sign conventions]], [[Design codes and references]], the [[Glossary]] and the [[Limitations and known issues]].

The guide is docs/USER_GUIDE.md in the repository; it is built into the app, so it always matches the version you run. Whoever adds or changes a function updates its article (see [[Help and development plans]]).

**Export to Word…** saves any article with the same format: A4, Arial 11 pt throughout, line spacing 1.15; margins top/left 1.88 cm, bottom 1.28 cm and right 1.58 cm. Screenshots fit the width between the margins.

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

## Configurations of earlier versions
A configuration saved by an earlier version of Mcotis opens as it was: every load case with its name, its load patterns and factors, the case it continues from and its Run setting, and the results stored with it stay with it. The file on disk is not changed until you save. Once saved by this version, the file is in the newer format and an earlier version of Mcotis no longer opens it — it reports that the file is newer than it supports. Update Mcotis on every computer before exchanging configurations.

A configuration can hold load cases this version does not analyse yet — a case with P-Delta, a modal case, a linear static case. They are kept, can be edited and are saved back (see [[Load cases and combinations]]). [[Run FEM Analysis]] with such a case ticked stops and says which case and why; it does not analyse the case in a simpler way instead. Untick the case to run the others.

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
The **Default Setup** is Pretension → SLS / ULS: Pretension fits the cables to their target forces with self-weight, and the service and ultimate cases continue from it. All three are Nonlinear Static.

**+ Add** and **Modify** open Load Case Data: the name of the case, its **Type**, and what belongs to the type.

- **Nonlinear Static** — tension-only cables with their sag. **Continue from** names the nonlinear case it starts from: the loads of this case are added to what is already on the structure at the end of that case. (zero) starts unstressed. **Purpose** Cable Target Forces makes it the case in which the cable pretension is fitted.
- **Initial P-Delta** — a nonlinear case whose geometric nonlinearity is P-Delta, meant to give its end stiffness to other cases.
- **Linear Static** — **Stiffness from** names the nonlinear case whose end stiffness it uses. Only the stiffness is taken; the loads of that case are not applied again. (unstressed) is the stiffness of the unloaded structure.
- **Modal (Eigenvectors)** — a **Mass source** and a number of **Modes** in place of loads, on a stiffness chosen as for a linear case. A model without a mass source is offered MS1, the self mass of its members.

A nonlinear case can also be given P-Delta as its **Geometric nonlinearity**, and its own load steps, iterations and tolerances (**Own iteration controls**). The lists under Continue from and Stiffness from hold nonlinear cases only. A case that other cases start from cannot be made linear or modal.

## What is not analysed yet
This version analyses Nonlinear Static cases without P-Delta and without iteration controls of their own. P-Delta, Initial P-Delta, Linear Static, Modal and a case's own iteration controls can be defined and saved, so that a model can be prepared and exchanged, but their analysis has not passed its verification. Load Case Data says so, and the **Status** column of the table reads "Unavailable — verification pending". A new case of such a type is added without its Run tick. [[Run FEM Analysis]] with such a case ticked stops and says which case and why; it never analyses the case in a simpler way instead.

## Combinations
Linear additive combinations of cases (and other combinations).

## Running
Before each run, **Set Load Cases to Run** lists the cases and combinations with their action, Run or Do Not Run, and the plan those actions make. **Role** is Requested for a case set to Run, Prerequisite for a case that is not set to Run but is needed first — a case of the run continues from it, or a combination of the run adds it up — and Result for a combination. **Depends On** names what a case needs: the case it continues, the case whose stiffness it uses, its mass source. **Availability** says whether the case is analysed yet.

A prerequisite is solved with the run and listed among its results, though it is not set to Run; its log in the run monitor says which case needs it. The cases are solved and listed in the order of the Load Cases table.

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
- 3D frames connected at every point lying on them. The degrees of freedom are set in [[Analysis options]]: a model starts in Full 3D, and the XZ plane preset analyses a model of the main window in its plane.
- A movement without stiffness that the loads do not act on is held and reported, and the case is solved; one the loads do act on has no result.
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

The degrees of freedom of the analysis, and the settings of the cable target-force fit. OK and Apply use the form for the next run and clear the results of the last one; a locked model shows the form read-only.

## Degrees of Freedom
The six degrees of freedom of the analysis — UX, UY, UZ, RX, RY, RZ — with the presets Full 3D, XZ plane, YZ plane, XY plane and Custom, and those of cable joints. A degree of freedom that is switched off is held at every joint, as by a support. It is not a release — a frame still bends between its joints — and a load on it is refused, never dropped.

**A model starts in Full 3D.** A model of the main window lies in its XZ plane and stands on pin supports at its lowest joints, all on one line. In Full 3D nothing holds it out of that plane: it is free to turn about the line of its supports. Its loads act in the plane and do no work on that movement, so the analysis goes on: the solver holds the movement, solves, and reports it. [[Run FEM Analysis]] reads Completed with Warnings, every load case reads "Converged with Warnings · Instabilities: … · 1 unloaded movement held", and the [[Analysis monitor]] lists the movement joint by joint — RX of every joint, UY of every joint off the line of the supports. The results are those of the XZ plane. Choose the **XZ plane** preset to analyse the model in its plane without those warnings. A configuration saved before this option existed opens in the XZ plane it was analysed in.

What is held is only a movement the loads do not act on. A mechanism under its own load — a frame that sways under a sideways force with nothing to resist it — has no equilibrium and no result: the load case reads Numerical Instability. So does a degree of freedom that nothing holds at all, such as a cable joint with no stiffness sideways.

- **XZ plane** — UX, UZ and RY. Any of them can be switched off too: with RY off no joint turns; with UX off no joint moves in X, which suits only a model whose joints all share one X — a column on one line, not a truss that spans X.
- **Degrees of freedom of cable joints.** A joint that only cables reach has the ones that are ticked, among those that are on. A cable gives stiffness along its own line only, so a movement left on that nothing else holds makes the model unstable, and the monitor names the joint. Switching it off holds it at those joints: with UX off, a vertical cable modelled in two segments through a joint nothing else reaches is analysed as the one cable it is.

The form keeps the set you choose; it does not judge whether this model can be analysed with it, and nothing corrects it. The run does: a set the model cannot be analysed with stops the run, and the monitor says why — which degree of freedom, which joints, and where to change it. Where an option holds a cable joint against the pull of a cable, the cases still run, read Converged with Warnings, and their log names the cable and the joint.

In 3D a frame is analysed with the out-of-plane inertia and the torsion constant of its section. The results shown are the in-plane ones; the displacement along a movement that was held is given as zero.

The monitor counts the degrees of freedom of each run (active | solved | restrained) and shows "-" for one that is off.

## Cable Force Fitting
The tolerance of the cable target-force fit (%), its maximum number of fitting iterations, the frame mesh — the number of stations each frame is read at, which changes no joint result — and cable sag.

This tab is shown for a model that has cables. For a model without cables — the tied arch system without its elevated cable, for instance — the tab is **Frame Mesh** and holds the mesh alone; the cable settings keep their values for when the model has cables again. The same goes for the results of the fit: Result → Cable target fit, its choice in Show Results, its tab of the Results Table and the Cable Target-Force Convergence tab of the [[Analysis monitor]] are shown only for a model with cables.

# Analysis monitor
Category: Run
Where: Run → Analysis Monitor
See also: Run FEM Analysis

The run monitor follows one run of [[Run FEM Analysis]]: the run on top, then every load case with its convergence and its numerical stability. It opens with each run, and Run → Analysis Monitor opens it again.

## The run
The status of the run — Running, Completed, Completed with Warnings, Failed or Cancelled — a bar that counts the load cases ended, the degrees of freedom as the equations were assembled (active | solved | restrained), the start time and the elapsed time. A run that failed shows the reason under them, in red; a run refused before it was solved — a translation switched off for a model that spans it, for instance ([[Analysis options]]) — has no equations to count, and its load cases read Cancelled · Not run with the reason in their log. A model analysed in Full 3D reads Completed with Warnings, every load case with the movement out of its plane held and listed: see [[Analysis options]].

## Load Cases
- **Numerical stability — information.** A note on how numerical stability is reported, with the thresholds in force. Click its heading to close it.
- **Model information.** The file the model is in and its counts: joints, restrained joints, frames, cables, shells, load patterns, joint and member load assignments, load cases and load combinations, and the supports by name.
- **Load cases.** One entry per solve of the run: the fit of the cable target forces of a pretension stage first, then its cases. A load combination is a superposition of case results, not a solve, so it has no entry. Each entry shows its number, name, status, what was found of it, and its time (hours:minutes:seconds).

A load case reads Pending, Running, Converged, Converged with Warnings, Not Converged, Numerical Instability, Failed or Cancelled, and after that what the solver found:

- **Converged · Instabilities: 0** — a result, and no severe instability detected. The 0 counts instabilities; it is not a residual and not the digits lost.
- **Converged with Warnings · Instabilities: 0 · Accuracy warnings: 2** — a result, with degrees of freedom that lost accuracy. Other warnings read "Cable target forces not met", "1 rotation left out" or "2 degree-of-freedom warnings".
- **Converged with Warnings · Instabilities: 78 · 1 unloaded movement held** — a result: the model has a movement without stiffness that the loads do not act on, which was held. The count is the degrees of freedom that move in it; they are listed, with the remark "possible mechanism, not loaded: held".
- **Numerical Instability · 6 affected DOFs** — no result: the number of degrees of freedom the solver reported, which is not the number of joints.
- **Not Converged · 100 iterations · Instabilities: N/A** — the equilibrium iteration did not converge. Its numerical stability was not assessed, so there is no count: N/A, not 0.
- **Failed · Cable target-force fit failed**, **Cancelled · Not run**, **Cancelled · Stopped** — why there is no result.

Click the header to open a case; several can stay open, and a case that failed opens itself. Open, it shows:

- **Convergence**, **Numerical Stability** and **Instabilities** — the number of degrees of freedom reported as a severe instability or a singularity. 0 means none was detected; N/A that the stability was not assessed.
- The details: analysis type, equations solved, iterations, whether the convergence tolerance was satisfied, joints in equilibrium, fitting runs and whether the cable target forces were met, and the accuracy warnings.
- **The affected joints**, when a degree of freedom is reported — for a warning as for a failure: the joint, its global coordinates X, Y, Z in m, and the six columns UX UY UZ RX RY RZ with the digits of accuracy lost — amber from 6, red above 11 — and the remarks naming the degrees of freedom.
- **Technical diagnostics** — closed until you click it: the numbers the solver measured — scaled backward error, out-of-balance against the accepted value, smallest scaled pivot, resultant of internal forces, largest loss of accuracy.
- **Analysis log** — what the solver reported of the case, in order, with the time of each line. The text can be selected and copied.

## Reading the table
- A number is the estimated digits of accuracy lost at that degree of freedom during the matrix solution.
- **∞** — a singularity was identified there: no stiffness and no restraint, a joint held by slack cables only, or a movement the stiffness does not resist ("possible mechanism").
- **-** — the degree of freedom is not active: one that is switched off — UY, RX and RZ in the XZ plane — and the rotation of a joint only cables reach.
- **R** — restrained by a support. **N/A** — active, but without an estimate.

Numerical Instability and Not Converged are different outcomes: the first is a stiffness the solver cannot rely on, the second an equilibrium iteration that did not converge. For an instability, review the joints listed for missing restraints, disconnected members, mechanisms or very different stiffnesses meeting at a joint.

## Cable Target-Force Convergence
Each cable's result / target per fitting run, for the stage that fits the cable target forces. The tab is shown for a model that has cables.

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
- **Export Result…** in Show Results opens the Word / PDF export dialog. Select solved load cases or combinations and all results or individual result types, then click OK and choose the file name and folder. The report opens after saving. Both formats use portrait A4, Arial 11, 1.15 line spacing and margins of 1.88 cm top/left, 1.28 cm bottom and 1.58 cm right. Reports contain high-resolution captures of the model result diagrams, with Figure numbers and load case/output captions, for axial/cable forces, bending, shear, reactions and deflection. The full model is fitted into each figure using the screen renderer, with result labels. Cable fit warnings are retained. Cable target fit remains in Results Table and is not a graphical export option. PDF uses the installed Edge or Chrome browser.
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
See also: Column design; Materials; Design results; Run FEM Analysis

Finds the cheapest candidate cross-section of the chosen column type that passes every ticked member and load case, ranked by **cost per metre of column**. It supports RC, concrete filled tube (CFT), steel encased and mega composite columns. Open the appropriate design dialog and use its Optimization tab; this is separate from the Optimization VCF / TAS system studies.

The search designs sections against the selected, existing FEM forces. It does not rerun the structural FEM model for every candidate, and Use Selected puts a candidate into the design dialog rather than automatically assigning it to the model.

## Step 1 — Prepare results and choose the column type
1. Define the working model's materials, sections, loads and combinations, then run **Run → Run FEM Analysis**.
2. Identify the members to size. You may select them in the model viewer before opening the design dialog, then verify the ticked list on Members & Loads.
3. Open the matching **Design** command: **RC Column Design…**, **CFT (Concrete Filled Tube) Design…**, **Steel Encased Composite Column Design…** or **Mega Composite Column Design…**.
4. Review the existing Section inputs, or use **From Member Section** to start from a ticked member's section. The optimization ranges start around the section present when the dialog is opened; inspect them before running.

The screenshots below use real dialogs. The complete CFT walkthrough uses the design checks' **illustrative forces**, not a particular open FEM model: C1 and C2 are 6 m members, with effective lengths of 6 m about both axes. Their ULS1 station forces are C1 N = −12,000 kN, V = 300 kN, M = 900 / −450 kN·m; C2 N = −9,000 kN, V = 500 kN, M = 1,400 / −700 kN·m. Negative N denotes compression in these example inputs. Use your own analysed member forces in normal work.

## Step 2 — Confirm Design Basis and effective lengths
On **Design Basis**, confirm the code, method, partial factors and material limits for the selected column type. Enter positive **l0,y** and **l0,z [m]**, the effective lengths for buckling about the major and minor axes. They are not automatically the same as a member's geometric length; enter the values appropriate to your design basis.

The Optimization tab has the same two length editors, synchronized with Design Basis. Changing either location changes the lengths used by the search. These values apply to the selected design scope. Do not compare two runs with different effective lengths as though only their cross-sections had changed.

For the illustrated CFT example, enter **6 m** for both axes. See [[Column design]] for the design basis and method descriptions.

![Step 2 — CFT Design Basis: confirm the method, material limits and 6 m effective lengths before sizing the section](images/column-optimization-basis.png)

## Step 3 — Select members and load cases
On **Members & Loads**, tick every member that the common section must satisfy and the load cases/combinations to design. One section is sized for **all** those members, not separately for each member. In the Optimization tab, the right-hand panel repeats the selected member scope and offers the load-case checkboxes, with All / None controls.

The example checks **C1 and C2 under ULS1**. In your model, include the required design combinations; checking only a lightly loaded member or omitting a governing case can produce a different optimum. The governing member/case may change as candidate sizes and grades change.

If there are no selected forces, the search reports that it needs FEM results. Selecting a name alone does not create forces; run analysis and choose results available for the selected members.

![Step 3 — Members & Loads: the illustrated common CFT section must pass C1 and C2 under ULS1](images/column-optimization-members.png)

## Step 4 — Enter size ranges for the chosen type
Open **Optimization**. Dimensions are in **mm**. Each continuous dimension uses **Min / Max / Step**: Min = Max fixes it; otherwise the search enumerates stepped values up to Max. An off-step maximum is not added. Keep positive steps and start with a small range.

The input groups change with the column type. Disabled width editors under a square option are not another independent range. Every additional dimension, grade or bar alternative multiplies the search space; more than **250,000 combinations** is refused.

### CFT — tube shape, size and thickness
Choose a **rectangular** or **circular** tube. A rectangular tube uses **H, B and wall thickness t**; a circular tube uses **diameter D and t**. The square option links the rectangular width to its depth, so B is not varied independently.

For the worked example, use a square rectangular tube with **H 400–900 mm at Step 100** and **t 10–30 mm at Step 5**. These give six outer sizes and five thicknesses before grades are combined. Try unreinforced tubes first by leaving the reinforced-options checkbox clear.

![Step 4 — CFT ranges: square tube H 400–900 mm, wall thickness 10–30 mm; effective lengths and case scope stay visible](images/column-optimization-cft-ranges.png)

### RC — concrete outline and longitudinal bars
Set **concrete h and b**, or use a square outline to keep them equal. RC has no structural steel tube/profile ranges. Choose concrete grades and longitudinal-bar alternatives. Cover is measured **to the links**, and the Section tab supplies the link inputs used when pricing the bars and links.

Bar counts on the b faces include corners. On the h faces, use the same-spacing option or specify how many bars fit **between corners**. Those conventions matter when reading the resulting total bar count.

![RC controls — concrete outline, concrete-grade range and longitudinal reinforcement; structural steel ranges are absent](images/column-optimization-rc-ranges.png)

### Steel encased — outline, embedded I section and bars
Set the concrete outline and the embedded steel I-section dimensions: **h, b, flange thickness tf and web thickness tw**. The outline and profile dimensions are different groups; changing one does not mean the other is automatically scaled to match.

Choose steel and concrete grades plus longitudinal bars and cover to links. Candidates whose steel profile or reinforcement cannot fit within the concrete outline are excluded. Narrowing the profile ranges can reduce the search substantially.

![Steel encased controls — concrete outline and I-section h, b, tf and tw are separate search ranges](images/column-optimization-encased-ranges.png)

### Mega composite — multiple profiles and placement
Set the concrete outline and dimensions of one steel I profile, then tick **4 and/or 6 profiles**. Ticking both tries both arrangements. Enter the **edge distance from the concrete face to the profile [mm]**; it controls placement, rather than being a centre-to-centre spacing.

Choose grades, bars and cover as for the mega section. More profiles do not automatically produce the cheapest result: their added capacity and material cost are both considered. The screenshot illustrates the mega-specific controls, not a completed mega optimization run.

![Mega composite controls — concrete outline, repeated I profile, four/six-profile alternatives and face-to-profile edge distance](images/column-optimization-mega-ranges.png)

## Step 5 — Set grade and reinforcement alternatives
Grades are stepped through the app's grade list between the selected lower and upper grades; they are not a numeric MPa step. For example **S355 to S460** includes the intervening S420 grade. The yield strength used follows plate thickness, so the nominal grade name alone does not determine fy. The results show the grade and fy actually used.

For the CFT example, choose **S355 to S460** and **C50/60 to C70/85**. The listed concrete classes in that interval are C50/60, C55/67, C60/75 and C70/85. Together with six sizes and five thicknesses this gives 360 raw combinations before buildability filtering, with no reinforcement alternatives.

For bars, enter a diameter list such as **20, 25, 32 mm** and the per-face count limits. RC, steel encased and mega candidates use longitudinal reinforcement. For CFT, **Also try reinforced options** adds bar alternatives while the unreinforced tube is still tried. CFT cover is measured **to the tube**, unlike cover to links in the other types. Scroll within the input panel to see all groups.

## Step 6 — Review and edit Unit costs
The Unit costs table is populated for the grades selected above. Edit the Rate cells to use the rates you want this search to compare:

- Structural steel and rebar: **SGD/t**.
- Concrete: **SGD/m³**.
- Candidate result: **SGD/m of column**, not total project cost.

Pricing uses steel and rebar mass plus **net** concrete volume per metre. RC, steel encased and mega pricing includes links with reinforcement. **Formwork is not priced**. The quantities are material quantities, not fabrication, erection or complete installed costs.

Check the units before entering a rate. A zero rate prices that material at zero. Dialog rate edits are inputs to this search; confirm the material library separately if you intend to change the model's rates.

For a simple pricing illustration, a 400 × 400 × 10 mm unreinforced CFT has about **122.46 kg steel/m** and **0.1444 m³ concrete/m**. At illustrative rates of 4,000 SGD/t and 150 SGD/m³, the material cost is **122.46 / 1,000 × 4,000 + 0.1444 × 150 = 511.5 SGD/m**. This arithmetic illustrates units; it does not mean that section passes the worked example's forces.

![Step 6 — Scroll to Unit costs: review the rates for the selected steel/concrete grades and rebar before running](images/column-optimization-costs.png)

## Step 7 — Run and read the ranked results
Click **Run**. The app prices buildable candidates, sorts them from cheapest to most expensive, and checks them against all selected members/cases until it finds up to **10 cheapest passing sections**, or exhausts the candidates. The progress text reports how many candidates were designed and how many passing options were found. **Stop** cancels the search.

A completed search does not have to design every listed candidate: once the retained cheapest passing options have been found, more expensive candidates need not be checked. The cheapest result is the cheapest **within your ranges, grade list, reinforcement choices, rates, member/case scope and basis**; it is not a claim about every possible section.

The results columns show:

- **Rank / Section**: the cost rank and candidate dimensions.
- **Steel / Concrete / Bars**: chosen grades, the steel fy used and reinforcement arrangement.
- **Steel kg/m / Conc. m³/m**: the material quantities shown for the candidate.
- **SGD/m**: the candidate's material cost per metre.
- **Util. / Governs**: maximum design utilisation and governing member/case. Another member or case may govern a different candidate.

Select a row and read the dimensions and material grades together. A thicker tube can use a lower thickness-dependent fy than a thinner tube of the same nominal grade. A low cost with high utilisation is a different choice from a more expensive section with greater reserve.

If no section passes, review the load scope and effective lengths, then consider wider size/thickness/grade ranges. If the range is too large, fix less influential dimensions or reduce grade/bar alternatives rather than starting a huge search.

In the illustrated CFT run, 228 buildable candidates remain after filtering. The search reports 35 candidates designed and retains ten passing options. Rank 1 is **RHS 500 × 500 × 15, S355 / C60/75, no bars**, at about **877 SGD/m**, with utilisation **0.97**, governed by **C2 · ULS1**. Those results use the stated example forces, effective lengths and captured rates; they are not a recommended section for another model.

![Step 7 — Actual CFT optimization results for the labelled C1/C2 example: passing candidates are ranked by material cost per metre](images/column-optimization-results.png)

## Step 8 — Use Selected and review the full design
Choose a result and click **Use Selected**, or double-click the row. The candidate appears on **Section**, with its dimensions, strengths and reinforcement. The dialog raises its material limit fields when needed to accommodate the selected grade.

Then click **Run Design** to obtain the detailed member-by-member calculation and governing checks under the selected basis and force scope. Inspect the report through [[Design results]] rather than deciding from a rounded utilisation value alone.

Use Selected does **not** assign the section to the structural model or rerun FEM. If you want the optimized section in the model, update/assign the corresponding section separately and analyse the revised model as needed before the final design review. Section stiffness and self-weight changes can change the member forces used in design.

![Step 8 — Use Selected transfers the ranked CFT candidate to Section; Run Design provides its full calculation](images/column-optimization-selected-section.png)

## Troubleshooting and practical sequence
- **No FEM forces:** run analysis, check the selected members and cases, and reopen the design scope if the model changed.
- **No passing section:** confirm the basis, lengths and loads; widen a relevant size, thickness, grade or reinforcement range. Do not weaken the design basis just to obtain a passing result.
- **Too many combinations:** fix dimensions, reduce alternative grades or bar counts, and use coarser steps before refining around promising candidates.
- **Fewer candidates than expected:** impossible fits, reinforcement layouts and unsupported/slender tube candidates can be excluded before detailed design. Inspect which dimensions and options are actually enabled.
- **Unexpected cost ranking:** check SGD/t versus SGD/m³, the chosen rates and material quantities. This is a cost per metre comparison, not a total-cost structural optimization.
- **Comparing runs:** keep member/case scope, effective lengths and cost basis consistent; change one group of choices at a time.
- Export this article with **Export to Word…** in the User Guide to share the procedure and screenshots.

# Optimization VCF
Category: Optimization
Where: Optimization → Optimization VCF…
See also: Optimization results and charts; VCF System; Materials; Load cases and combinations; Analysis options; 03 Pretension

The parametric study of the VCF system: Mcotis builds every combination of the geometry, cable force, storey force and candidate section inputs, analyses it with the internal solver, checks bottom-chord deflection and uplift, and prices its members. The working model is not changed. A single Run FEM Analysis analyses the model as defined; Optimization VCF compares many variants without applying them to that model.

The command is available while the open model is a VCF system (File → Load VCF… or Edit → [[VCF System]]). With another system open it is greyed; the tied arch system has [[Optimization TAS]].

## Step 1 — Prepare the working model
1. Open the VCF model through **File → Load VCF…**, a saved configuration, or an existing imported model.
2. Check **Edit → VCF System…**: geometry, loaded points, storey forces, cable target forces and assigned sections should describe the system you want to vary. The optimization starts from these inputs; it does not fetch a fresh SAP2000 model during the run.
3. Review **Define → Load Cases / Load Combinations**. Choose which cases run and check any staged pretension definitions. A load pattern such as LL_ABOVE is an assigned loading source; the result checked later is a solved case or combination such as SLS: 1.0DL+1.0LL.
4. Review **Run → Analysis Options**, including cable target-force fitting, iteration limit, mesh and sag. These apply to the optimization cases as well.
5. Check section assignments and material unit costs through **Define → Materials / Section Properties**. Missing rates can make the cost unavailable; unavailable is not zero.
6. Open **Optimization → Optimization VCF…**.

For a first study, vary only one or two inputs. The illustrated nine-case example uses **truss depth 18, 20, 22 m** and **LL_ABOVE qz 5, 7.5, 10 kPa**, with one cable-force step and no section alternatives. Other geometry stays fixed. The six screenshots illustrate the relevant controls; some were captured while only the loading range was active, before the depth range was added.

## Step 2 — Set Geometry ranges and inspect the preview
The form has four sheets on the left and a model preview on the right. On Geometry, each row has **Min / Max / Step** and **n**, the number of values:

- **Min = Max** keeps an input fixed.
- Otherwise values are Min, Min + Step, Min + 2 × Step, and so on up to Max. An off-step Max is not appended: 18–23 at Step 2 gives 18, 20 and 22.
- A positive step is required even for a fixed range. Whole-number inputs such as bays must use whole-number values.
- Independent ranges multiply. Three depths and three bay widths already make nine geometries, before loading or section alternatives are added.

The geometry inputs describe **number of bays, bay width, truss depth, clear height, back-span and back-stay node height**. Truss depth is bottom-to-top chord height; clear height is ground-to-bottom chord. Back-span runs from the mega column to the outer column. The middle chord is not an independent range: it remains at **0.5 × truss depth**. Invalid geometries are listed as skipped cases with a reason.

For the example, enter **truss depth Min 18 / Max 22 / Step 2** and leave the other geometry inputs fixed. Check **n = 3**. The truss span between mega columns is **bays × bay width**; changing either also changes the deflection limit, member lengths and quantities being priced.

Use **Preview at** to inspect the minimum, current-model or maximum corner of the ranges. A corner takes that value for every varying input; it is not a selected result or a best-case recommendation. The preview changes neither the working model nor SAP2000. When the bay range changes the fan, the choices identify fewer/current/more cables and the caption identifies new or omitted cables. Check the fan layout and force labels at both ends of the range before running.

![Step 2 — Geometry: truss depth 18–22 m at Step 2 gives three geometries; with three loading values the study contains nine combinations](images/optimization-vcf-geometry.png)

## Step 3 — Define Cable Force and the shared steps
Cable Force starts from the model's pretension setup. Values here are **target pretension [kN]**, not the cable's final force under the chosen SLS/ULS result. Each case fits the applicable targets in its pretension stage and then solves its defined loading cases.

**Steps** is the number of shared force steps. All independently varied cable spans move together from Min to Max; they are not a separate Cartesian axis for every cable. For example, Min 5,000 / Max 7,000 with Steps 3 gives 5,000, 6,000 and 7,000 kN. With Steps 1, the run uses Min even if Max differs; equal Min/Max remains fixed. A follower keeps its configured synchronization relationship rather than receiving an unrelated independent force.

### Fan cables — Force by length
The fan runs from a top corner to bottom-chord joints. **Force by length** offers:

- **+ kN per m**: T = T shortest + increase × (L − L shortest). Enter the shortest cable's Min/Max and the increase in kN/m. Longer cables receive a greater force when the increase is positive.
- **× length**: T = T shortest × L / L shortest.
- **Manual**: enter a separate Min/Max for each independently controlled fan cable.

A length rule uses **the cable lengths of each geometry case**, including the other side's lengths. Derived forces are rounded to **100 kN**; typed forces are not rounded by this rule. More bays can add fan cables, whose targets follow the selected length rule; in Manual, a new cable uses its template cable's force scaled by the length ratio.

The screenshot uses **+ kN per m**, shortest Cable2a-2 at **5,000 kN**, increase **318 kN/m** and **Steps 1**. Other fan rows are derived. The **Model** column is a reference to the model's force; it need not equal the proposed force.

### Other cables and synchronization
Other cables, such as back-stays, have their own spans. Their rows follow the previewed geometry. A cable that takes another cable's value is not repeated as an independent editable row. Check Min, Max and Steps together: in the screenshot Cable1a-2 has Max 105,000 kN, but with one step the run still uses its Min 100,000 kN.

For the nine-case walkthrough, keep **Steps 1**. If you introduce three effective cable steps, the same three depths and three loading values become **27 cases**.

![Step 3 — Cable Force: shortest fan cable 5,000 kN plus 318 kN/m; Steps 1 uses Min for every independent cable span](images/optimization-vcf-cable-force.png)

## Step 4 — Set Storey Force ranges
Storey Force starts from the model's loading setup and varies **qz per load definition [kPa]**, **number of storeys** and **tributary width B [m]**. qz is a nonnegative downward loading magnitude. Each loaded point receives:

**Fz = −qz × tributary length × B × number of storeys [kN]**

The tributary length follows that case's geometry, so changing bay widths can change the joint loads even when qz stays fixed. B is the out-of-plane width; it is not the truss span. End points use their own tributary lengths rather than automatically receiving the same force as interior points. The sheet summarizes the load at minimum and maximum corners.

For the example, keep **DEAD_ABOVE qz = 10 kPa**, **10 storeys** and **B = 12 m** fixed; set **LL_ABOVE Min 5 / Max 10 / Step 2.5 kPa**. At an interior point with 10 m tributary length, the live force is −6,000, −9,000 or −12,000 kN. Each of those loading variants is combined with every depth. The load definitions feed patterns; the defined cases/combinations supply the result factors.

The counter's **force** factor combines the storey-load ranges and the effective shared cable steps. With one cable step and three LL values, it is three. Adding a two-value storey-count range would double the nine-case study to 18 cases.

![Step 4 — Storey Force: DEAD_ABOVE 10 kPa; LL_ABOVE 5–10 kPa at Step 2.5; 10 storeys and B = 12 m](images/optimization-vcf-storey-force.png)

## Step 5 — Choose candidate Sections
On Sections, open the candidate picker for each frame group — **top, middle, bottom and vertical chords, mega columns and end frames** — or each cable. Tick the sections you want to try. **(keep as assigned)** retains the model's assignments when no alternatives are selected.

Each set of alternatives is another independent choice. Two top-chord sections and three mega-column sections make **six section variants** for every geometry/force combination. The nine-case example would therefore become 54 cases. Frame and cable choices come from their appropriate section libraries; a frame section is not a cable section.

Start with the existing assignments for the first study. Add section alternatives after identifying the influential geometry and loading inputs. Confirm materials, properties and unit costs before comparing section variants: the cost is the sum of member cost per metre × each case's member length.

![Step 5 — Sections: choose alternatives per frame role and cable, or keep the model's assigned sections](images/optimization-vcf-sections.png)

## Step 6 — Set the Deflection Check and start the run
The bottom strip applies to every case:

1. In **max δ in**, choose the load case or combination used for the check. The form normally starts with an SLS-named result when one exists; verify the actual name and its loading rather than relying on the letters SLS alone.
2. Set **≤ span / = 250** for this example. With 10 bays × 10 m, span = 100 m and the deflection limit is **400.0 mm**. Each varying-span case uses its own span.
3. Set **uplift ≤ = 0 mm** for the illustrated setup, or enter your permitted upward movement.
4. Check the space counter: **3 geometry × 3 force × 1 section variant = 9 combinations**. Resolve any invalid-input messages and check both preview corners.
5. Click **Run Optimization**.

Both limits are read on **bottom-chord points**, not every joint in the model. A strong fan can reduce downward movement while lifting the middle of the chord; passing the downward limit alone is insufficient. **OK** requires both downward deflection within span/ratio and uplift within its allowance. Optimization TAS instead checks its top-chord deck points.

One run accepts at most **10,000 cases**. If the count is too large, fix less important inputs, reduce candidate sections or use larger steps. A single three-value axis triples the study. Do not assume a fixed cable span adds Steps variants: the counter reflects whether any cable force actually varies.

**OK** keeps the form settings for the current app session without running or applying them. **Cancel** discards form edits. **Reset Form to Current Model** rebuilds the starting settings from the current model. The optimization does not automatically apply a winning case to the working model.

## Step 7 — Read the Run results
The Run sheet fills as cases finish. Each row shows the varying inputs, **max downward deflection [mm]**, its governing point, span, limit, **δ / limit**, uplift, **OK / NG**, total cost, result name and status. Values that do not vary need not appear as individual columns.

- **δ / limit ≤ 1** meets the downward-displacement limit; check uplift and status as well.
- **NG** indicates a failed check. A skipped or failed analysis has a reason in Status and may have no numerical result; do not interpret a blank as zero.
- A **cable not fitted** status means the requested target was not met within tolerance. Read that status before accepting the case as the intended pretension setup.
- Sort by cost to compare economical options, then confirm that the selected row passes both displacement checks and has the intended result definition.

The summary identifies the cheapest passing case and the least-deflection case; they need not be the same. The pictured completed run has **nine passing cases**, with cheapest case 1 at depth 18 m and LL 5 kPa, priced at **SGD 7,248,380** using the model and rates when captured. Your current model, analysis settings and rates determine your own results.

**Cancel Run**, or **Optimization → Cancel Optimization**, stops the study and keeps completed cases. **Optimization → Optimization Results…** reopens the last run.

![Step 7 — Run results: the illustrated nine-case study passes both displacement checks; the summary identifies the cheapest passing case](images/optimization-vcf-results-run.png)

## Step 8 — Use Chart to refine the study
Open Chart for numbered figures of responses against the inputs that actually varied, followed by a summary and conclusions. See [[Optimization results and charts]] for the shared controls and available response families.

The main-effect line summarizes the response at each input value over the other inputs; a shaded band shows their minimum-to-maximum spread. It is not a deflection acceptance band. A wide band means another variable matters too; use the interaction plots to examine combinations instead of treating one line as a single case.

For the pictured study, increasing live load increases deflection and increasing truss depth reduces it. This is an observation of those cases, not a guaranteed rule for every model or pretension setting. Read utilisation against its limit, compare cost versus utilisation, and use the sensitivity ranking to decide which input deserves finer steps. Cost can remain unchanged across loading-only cases while changing with geometry or sections.

Use **Copy Summary** to copy the report text. A fixed or small study may produce fewer figures because there are fewer varied inputs or available responses. Refine one range at a time, rerun, and compare using the same load result and acceptance limits.

![Step 8 — Chart: in the illustrated study, deflection increases with LL and decreases with truss depth; bands show spread over the other inputs](images/optimization-vcf-results-chart.png)

## Troubleshooting and good practice
- **Run disabled:** inspect invalid ranges, missing geometry or force inputs, the case-count limit and whether another run is active. Use positive steps and whole-number values where required.
- **Unexpected number of cases:** multiply the counts of every geometry range, storey-load range, effective shared cable step and section choice. Off-step maxima are omitted.
- **Cable changes with geometry despite one force step:** a length rule recalculates targets using each geometry's lengths; one step fixes the shortest cable's starting target, not every derived cable force.
- **Large uplift with small downward deflection:** review fan pretension and the separate uplift allowance. Do not decide from δ / limit alone.
- **Unavailable cost:** inspect the section/material assignments and material rates. Compare total costs only on a consistent cost basis.
- **Using a selected variant:** enter its geometry, forces and sections through [[VCF System]], apply them, and run the working model to review its results. The optimization table is a comparison, not an automatic model edit.
- Export this article with **Export to Word…** in the User Guide to share its steps and six screenshots.

# Optimization TAS
Category: Optimization
Where: Optimization → Optimization TAS…
See also: Optimization results and charts; Tied Arch System; Load cases and combinations; Materials; Analysis options

The parametric study of the tied arch system: Mcotis builds every combination of the geometry, storey loading, optional cable pretension and candidate sections, analyses it with the internal solver, checks the deck deflection and uplift, and calculates its member cost. Like [[Optimization VCF]], it runs in memory and leaves the open model unchanged. SAP2000 and ETABS are not needed.

The command is available while the tied arch system is open (File → Load TAS… or Edit → [[Tied Arch System]]). With another system open it is greyed; a VCF system has [[Optimization VCF]].

## Step 1 — Open and check the TAS model
1. Choose **File → Load TAS…** for the built-in 120 m system, or use **Edit → Tied Arch System…** to create your own system first.
2. Check **Define → Load Cases / Load Combinations**. Only the cases set to run are available to the study; the example below checks **SLS1 G+Q full**.
3. Check **Run → Analysis Options**. These options also apply to each optimization case.
4. Check material unit costs under **Define → Materials** before comparing costs. A missing rate can leave the cost unavailable; it does not mean the member is free.
5. Open **Optimization → Optimization TAS…**. The menu command is enabled only while the tied arch system is open.

The walkthrough uses the built-in system: H1 = 30 m, 12 bays at 10 m, B = 12 m and 10 floors, without an elevated cable. It varies only **h = 2, 3, 4 m** and **LL = 5, 7.5, 10 kPa**, giving nine cases. The Cable Force screenshot is a separate optional example.

## Step 2 — Set the Geometry ranges
The form has four sheets on the left and a preview on the right. On **Geometry**, enter **Min / Max / Step** for each input. Min = Max fixes an input. Otherwise values are Min, Min + Step, and so on up to Max; an off-step Max is not appended. The **n** column counts the values.

- **H1 — support → arch crown [m]**: the arch rise from its springing to the crown at mid-span. The arch is parabolic.
- **h — arch crown → top chord [m]**: the gap above the crown. **H2 = H1 + h**, so H2 is derived rather than entered as another independent range. h must stay greater than zero.
- **Number of bays (even)**: a whole, even number from 6 to 40. Use Step = 2 when varying it. If an interior value is odd, that case is skipped and its reason appears in the results.
- **Spacing of vertical lines [m]**: the bay spacing. **Span L = bays × spacing**, so changing either input also changes the deflection limit and the length being priced.
- **H0 — ground → cable and springing [m]** appears only with an elevated cable. In that system, the crown elevation is H0 + H1 and the deck elevation is H0 + H1 + h.

For the nine-case example, fix H1 at 30, bays at 12 and spacing at 10; set **h Min 2 / Max 4 / Step 1**. This gives three geometries, with H2 = 32, 33 and 34 m.

**Preview at** switches between Minimum, Current and Maximum. Minimum/Maximum uses that corner of every range; Current shows the open model. It is a preview, not a selected optimization case and not an edit to the open model. In the screenshots below Maximum shows h = 4 m and LL = 10 kPa.

![Step 2 — Geometry: H1 fixed at 30 m, h 2–4 m in steps of 1, 12 bays at 10 m; Maximum previews the deck at H2 = 34 m](images/optimization-tas-geometry.png)

## Step 3 — Set Storey Force ranges
On **Storey Force**, set the loads carried by the deck:

- **SW [kPa]** is slab self-weight from the floors above; structural member self-weight follows the model's load cases.
- **SDL [kPa]** is superimposed dead load.
- **LL [kPa]** is live load.
- **Number of floors n** is a whole-number range.
- **Tributary width B [m]** is the width carried by the system.

For an inner top-chord joint, the form shows the following downward load magnitudes:

**SLS = (SW + SDL + LL) × spacing × B × n**

**ULS = (1.35 SW + 1.35 SDL + 1.5 LL) × spacing × B × n**

The two end joints take half the inner-joint load. SW + SDL goes into DEAD_above; LL goes into LL_above, with LL_left / LL_right for the half-span loading cases. The defined cases and combinations apply the factors.

For this example, fix SW at **7 kPa**, SDL at **3 kPa**, n at **10** and B at **12 m**. Set **LL Min 5 / Max 10 / Step 2.5**. The three LL values combine with all three h values: **3 geometry × 3 load × 1 section = 9 combinations**. The load summary reads **SLS 18,000–24,000 kN** and **ULS 25,200–34,200 kN** per inner joint; the preview arrows show SLS at the chosen corner.

![Step 3 — Storey Force: SW 7 kPa, SDL 3 kPa, LL 5–10 kPa at 2.5, 10 floors and B = 12 m; the study now contains nine combinations](images/optimization-tas-storey-force.png)

## Step 4 — Set Cable Force when an elevated cable is present
For a system without an elevated cable, **Cable Force** is disabled. Keep it disabled for the nine-case example above.

To study a prestressed tie, first open **Edit → Tied Arch System…**, tick **With elevated cable**, set its H0 and cable force, and apply the system. Then reopen Optimization TAS. H0 appears on Geometry and Cable Force becomes available.

The tie is **TIE** at H0. Enter its pretension **Min / Max [kN]** and **Steps**. The first step is Min and the last is Max, with equally spaced forces in between; one step uses Min, and equal Min/Max gives one force variant. For example, **10,000–30,000 kN with 3 steps** tries **10,000, 20,000 and 30,000 kN**. With the same h and LL ranges this would make **27 combinations**, not nine.

Each case fits the tie to its target in the Pretension case; the subsequent staged cases carry that pretension. Review any cable-not-fitted status in the results rather than treating it as a successfully fitted target. The screenshot below illustrates this optional setup with H0 fixed at 10 m; it is separate from the nine-case results shown later.

![Optional Step 4 — Elevated cable: TIE at H0 = 10 m, pretension 10,000–30,000 kN in three steps; this optional study contains 27 combinations](images/optimization-tas-cable-force.png)

## Step 5 — Choose candidate Sections
On **Sections**, open the candidate picker for each role and tick the sections to try: **arch rib, top chord (deck), mega column, end strut, strut and brace**, plus the tie when there is an elevated cable. Frame choices come from **Define → Section Properties → Frame Sections**; the tie uses cable sections.

No sections ticked keeps the TAS section for that role. Tick several to try each one. Section choices multiply: two arch sections and three deck sections make six section variants for every geometry/load combination. The sheet also shows the TAS section name beside each role.

Leave every picker at its existing TAS section for the nine-case walkthrough. Try section alternatives in a second, smaller study once you understand the geometry and load effects. See [[Materials]] for the cost basis.

![Step 5 — Sections: candidates are chosen per member role; no alternatives are selected in the nine-case walkthrough](images/optimization-tas-sections.png)

## Step 6 — Set the Deflection Check and run
The bottom strip is common to all four sheets:

1. In **max δ in**, select **SLS1 G+Q full** for this example. A blank result selection checks the largest deflection over every solved result, which may be governed by a different case.
2. Set **≤ L / = 250**. For 12 bays × 10 m, L = 120 m and the limit is **480.0 mm**. If the span varies, each case uses its own L.
3. Set **uplift ≤ = 0 mm** for this example, or enter the allowed upward movement for your study.
4. Check the combination count and the preview at Minimum and Maximum. Red problem text must be resolved before running. One run accepts at most **10,000 combinations**.
5. Click **Run Optimization**. **OK** keeps the settings for the current app session without running or applying them. **Cancel** discards the form edits. **Reset Form to Current Model** restores the TAS starting form, including its default h and load ranges; it does not make every range fixed.

Both displacement limits are checked on **top chord (deck) joints**, where the floors sit. This differs from Optimization VCF, which checks bottom chord points. A case passes only when its downward deflection is within L / ratio and its uplift is within the uplift limit.

The starting form has wider ranges than this walkthrough: h 2–5 m, SW 7–15 kPa, SDL 3–10 kPa, LL 5–10 kPa and n 5–10, giving **5,184 combinations** for the built-in system with no section alternatives. Start with the nine-case example before expanding the study.

## Step 7 — Read the Run results
The **Run** sheet fills as cases finish. Each row lists the varying inputs, maximum deck deflection and its point, span, limit, δ / limit, uplift, **OK / NG**, total cost and status. The Result column identifies the load case or combination checked.

Sort by deflection, utilisation or cost to compare cases. Read the status of skipped or failed cases: unavailable results are not zero. The summary names the cheapest passing case and the case with the least deflection. **Cancel Run**, or **Optimization → Cancel Optimization**, stops the run; completed cases remain available. Reopen the last run through **Optimization → Optimization Results…**.

In the illustrated run, **all nine cases pass**. Deflection ranges from **74.6 to 100.0 mm** against the 480.0 mm limit, and uplift is 0.0 mm to the displayed precision. The cheapest passing and least-deflection case is **case 1: h = 2 m, LL = 5 kPa**, at **SGD 2,558,537** using the model rates when the screenshots were captured. These are the example results, not prescribed inputs or a cost estimate for another model.

![Step 7 — Run results from the actual nine-case TAS study: each h value is paired with LL 5, 7.5 and 10 kPa; deck deflection is checked against L/250](images/optimization-tas-results-run.png)

## Step 8 — Compare the Chart results
Open **Chart** to see the completed study as numbered figures, followed by its summary and conclusions. For this walkthrough, compare how the responses change with **h** and **LL**. Deflection, utilisation, cost and other available responses come from the cases actually solved. A plotted band shows the spread over the other varying inputs; it is not another acceptance limit.

In this example, the mean deflection rises from about **75.4 to 99.0 mm** as LL rises from 5 to 10 kPa. Changing h from 2 to 4 m has a smaller effect: the mean rises from about **86.2 to 88.1 mm**. Increasing h does not reduce deflection in this particular study; read the plotted response rather than assuming the direction of an effect.

Use the sensitivity figures to decide which range to refine. Compare cost against utilisation to find economical passing cases, and read the interaction plots when more than one input varies. A small or fixed study may produce fewer figures. **Copy Summary** copies the report text. See [[Optimization results and charts]] for the shared Run/Chart controls.

![Step 8 — Chart results from the same nine-case TAS run: numbered response figures compare the varied geometry and loading](images/optimization-tas-results-chart.png)

## Tips and troubleshooting
- If Run Optimization is disabled, check the red problem text, the case count, and that a tied arch system is open. Use positive h and spacing, whole-number floors, and even bay values.
- The preview is a corner of the input ranges, not the cheapest or best result. Optimization does not automatically replace the open model with a result row; enter your chosen parameters through [[Tied Arch System]] if you want to use them.
- If a cost is unavailable, check section/material assignments and unit costs before ranking the cases.
- Use a named service load result consistently when comparing studies; changing the result or the uplift allowance changes which cases pass.
- Export this article with **Export to Word…** in the User Guide to share the steps and screenshots with the team.

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
Where: Archived on 2026-10-13 — no longer in the application
See also: Run study; 01 Loading; 05 Iteration

**The complete legacy MB workspace was archived on 2026-10-13 and is no longer in the application.** This includes pages 01–06, 06A and 08, the MB menu, Run Study / Cancel Study, Study Reports and the old report toolbar button. These articles remain only as a record of the former SAP2000 study workflow. The separate [[Optimization VCF]] and [[Optimization TAS]] tools remain active and use the internal solver.

The MB pages ran the original candidate study on SAP2000: candidates of loading (01), truss height (02) and cable pretension (03) were combined, each candidate was built as a child SAP2000 model, analysed by SAP2000 and reported (06), qualified (06A) and quantified (08).

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
Where: Archived on 2026-10-13 — no longer in the application (was MB → 06 Post-Processing)
See also: 06A Optimization; Run study

The raw data of the study turned into reports: cable forces with design capacities, deflections by candidate, member forces and support reactions, as interactive HTML. Print Setup and PDF export print the reports.

# 06A Optimization
Category: MB study (SAP2000)
Where: Archived on 2026-10-13 — no longer in the application (was MB → 06A Optimization)
See also: 06 Post-Processing

Qualifies the stored candidates against acceptance criteria — available once 06 has raw data.

# 08 Quantities
Category: MB study (SAP2000)
Where: Archived on 2026-10-13 — no longer in the application (was MB → 08 Quantities)
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
