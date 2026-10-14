# Mcotis Desktop native workspace window

Archived on 2026-10-15, M8 of the main-window migration (task of 2026-10-14: "one window, one model").

This folder preserves the separate native model workspace that Edit → TemplateBuilding… and File → Open Native Model…
used to open:

- `NativeModelWindow.xaml` / `.xaml.cs` — the window: File (Open, Save, Save As), Edit (TemplateBuilding, Undo, Redo),
  Define → Modal Eigen Case, Assign → Joint Mass, Run (Analysis Options, Run Analysis) and its result tabs.
- `NativeModelViewModel.cs` — its view model, which solved one selected case of its own session.
- `NativeModalSetupWindow.cs` — its modal case and mass source form.

These files are outside `src/Mcotis.Desktop`, are not included by the WPF project and are not compiled.

Every function the window had is in the main window, on its one native document:

| Native workspace | Main window |
|---|---|
| Edit → TemplateBuilding… (a second window) | Edit → TemplateBuilding… — the building becomes the main window's model, in 3D |
| File → Open Native Model… / Save / Save As | File → Open… / Save / Save As… (`.mcotis`; a native-workspace file opens in the main window) |
| Edit → Undo / Redo | Edit → Undo / Redo |
| Define → Modal Eigen Case… | Define → Load Cases → Load Case Data, kind Modal; mass source MS1 (self mass and joint masses) |
| Assign → Joint Mass… | Assign → Joint → Masses… (the same form, on the joints selected on the model view) |
| Run → Analysis Options… / Run Analysis | Run → Analysis Options… / Run FEM Analysis (linear static cases on the unstressed stiffness are solved since the same step) |
| Joint / frame / cable / modal result tabs | Result → Results Table… (Joint 6-DOF, Frame 6-component, cables) and Result → Modal Results… |

The forms the main window still uses — `TemplateBuildingWindow`, `NativeJointMassWindow`, `AnalysisOptionsWindow` —
remain in the desktop project, as do the row records of the modal tables (`ModalResultRows.cs`).
