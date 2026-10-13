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

The rest of the former MB workspace (06, 06A, 08 and its entry points) is archived beside this folder under
`archive/Mcotis.Desktop/LegacyMb/`. The active `WorkflowTabs` control retains inert slots 0–6 and 8 so FEM keeps
its established index 7. New UI work must not navigate to or restore any archived slot.
