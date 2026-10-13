# Mcotis Desktop legacy Tabs 01–05

Archived on 2026-10-13 by user decision.

This folder preserves the former MB pages:

- 01 Loading
- 02 Truss Height
- 03 Pretension
- 04 Output Settings
- 05 Iteration

`MainWindow.Tabs01To05.xaml` is the exact XAML fragment removed from the active main window.
`MainWindow.Tabs01To05.xaml.cs` preserves the code-behind that existed only to serve those pages.
Neither file is compiled.

The active `WorkflowTabs` control retains five empty slots so the established indices of Post-Processing,
Optimization, FEM and Quantities do not change. The legacy domain/configuration types and study services remain
in the desktop project only where current VCF/TAS/optimization workflows or older configuration files still need
them. New UI work must not navigate to or restore slots 0–4.
