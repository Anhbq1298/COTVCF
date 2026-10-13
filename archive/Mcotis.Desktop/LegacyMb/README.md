# Mcotis Desktop legacy MB workspace

Archived on 2026-10-13 by user decision.

This folder preserves the remaining legacy MB user interface:

- 06 Post-Processing
- 06A candidate qualification and optimized study reports
- 08 Quantity Take-off
- the top-level MB menu, Study Reports entry, report toolbar entry and MB navigation handler

`MainWindow.MbPages.xaml` contains the exact page fragments removed from the active main window.
`MainWindow.MbEntryPoints.xaml` and `MainWindow.MbNavigation.xaml.cs` preserve their former entry points.
These files are outside `src/Mcotis.Desktop`, are not included by the WPF project and are not compiled.

The active `WorkflowTabs` control keeps inert slots 0–6 and 8 only so the FEM workspace remains at its
established index 7. The standalone Optimization menu (VCF/TAS) remains active and is not part of this archive.
Legacy state, persistence and report services remain in the desktop project where old files, checks or active
Optimization VCF/TAS code still depend on them. No active menu, toolbar button or startup flow may navigate to
the archived MB slots.
