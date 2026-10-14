# Mcotis — kế hoạch hệ thống độc lập

Bản chỉnh ngày 2026-10-12. Thay cho bản plan 10 mục trước đó, và thay cho các quyết định về Lock/Unlock, SQLite và
"stable ID theo tên" trong [fem-database-plan.md](fem-database-plan.md) đối với hệ mới.

**Cập nhật UI legacy (2026-10-13):** toàn bộ workspace MB đã rời khỏi ứng dụng. Các trang 01–05 được giữ đông
băng tại `archive/Mcotis.Desktop/LegacyTabs01To05/`; các trang 06, 06A, 08 cùng menu và entry point của chúng ở
`archive/Mcotis.Desktop/LegacyMb/`. `WorkflowTabs` chỉ còn FEM hoạt động tại index 7; các slot 0–6 và 8 là placeholder
rỗng để không làm đổi index. Không được khôi phục navigation vào các slot đó. Optimization VCF/TAS độc lập vẫn là
menu hoạt động cuối cùng. Kiểu dữ liệu/config cũ chỉ còn để mở file cũ hoặc phục vụ code hiện hành còn phụ thuộc.

**Cập nhật 3D (2026-10-12):** theo yêu cầu mới, tầng analysis sẽ dùng chung model XYZ và DOF presets cho 3D/XZ/XY/YZ,
thay cho việc port nguyên solver planar. Foundation đã có: `Mcotis.Analysis.ModelCompiler`, DOF settings lưu ở schema 2,
migration schema 1 → XZ, mapping sáu DOF và diagnostics. Đã có thêm một engine tuyến tính 3D cho frame (dense, dùng để
kiểm chứng công thức): `LinearStaticSolver`, đối chiếu với lời giải giải tích, OpenSeesPy và solver planar hiện tại —
phạm vi, nguồn công thức và bằng chứng ở [analysis/spatial-static-verification.md](analysis/spatial-static-verification.md).
**Cập nhật solver tổng quát (2026-10-12, người dùng: "put in archive for planar frame solver and do the general
solver now, confirm you need to validate before putting in terms of result", sau đó "use general solver every
where"):** mọi phép phân tích của Mcotis nay chạy một solver: `Mcotis.Analysis.StaticSolver`. Nó xử lý frame tự nối
tại các điểm nằm trên nó, cable chỉ chịu kéo, target force của cable, các case nonlinear nối tiếp nhau, cable sag, và
nội lực / đường đàn hồi tại các station dọc frame. Cửa sổ chính (ứng dụng standalone) gọi nó qua
`src/Mcotis.Desktop/Application/FemSolver.cs` với DOF XZ vì model của cửa sổ chính là phẳng; native workspace gọi trực
tiếp. Solver planar đã đưa vào archive: không còn nằm trong app, chỉ giữ ở
`tests/Mcotis.Desktop.Checks/Archive/PlanarFrameSolver.cs` (đóng băng) làm mốc đối chiếu. Kết quả đã được đối chiếu
với lời giải giải tích, solver planar (default model như app chạy: 11.361 giá trị) và OpenSees **trước khi** hiển thị:
[analysis/cable-static-verification.md](analysis/cable-static-verification.md). Modal Eigen tuyến tính cho frame nay đã
có trong native workspace, đã đối chiếu closed form và OpenSeesPy; xem
[analysis/modal-eigen-verification.md](analysis/modal-eigen-verification.md). Chưa có shell, P-Delta, modal từ tangent
phi tuyến/cable, solver sparse. WPF đầu tiên đã nối vào engine qua
**Edit → TemplateBuilding…**: model workspace riêng, native Save/Open, Undo/Redo, DOF options, **Run Analysis**
và bảng kết quả (kể cả bảng cable). Chưa phải desktop migration đầy đủ; xem [phạm vi UI](native-building-ui.md). Chi tiết và source gates:
[analysis/foundation-status.md](analysis/foundation-status.md), [implementation plan](analysis/IMPLEMENTATION_PLAN.md),
[literature register](analysis/literature.md). P-Delta và modal vẫn là các giai đoạn sau.

**Cập nhật load case (2026-10-13, người dùng: "implement this plan" / "continue this plan", phạm vi PR-01 đến PR-03 của
[analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md](analysis/LOAD_CASE_STATE_REFACTOR_PLAN.md)):** load case nay có bốn loại
(Linear Static, Nonlinear Static, Initial P-Delta, Modal Eigen) và hai tham chiếu tách biệt — case mà nó *tiếp tục từ*
(`InitialCaseId`) và case mà nó *lấy stiffness* (`StiffnessSourceCaseId`). Có thêm `MassSource`, `LoadCaseRules` (một bộ
quy tắc dùng chung cho cả hai cửa sổ) và `Mcotis.Analysis.Cases.CaseDependencyPlanner` (thứ tự chạy, prerequisite, case
bị chặn khi case trước hỏng). File: `.mcotis` schema 5, `.cotfem.db` schema 2, `.cotfem.json` version 2; file cũ được
nâng cấp trong bộ nhớ khi mở, giữ nguyên ý nghĩa, ID, pattern và hệ số. **Chưa có phép tính mới nào**: P-Delta, modal,
linear static trên stiffness của case khác và iteration controls riêng của case đều bị từ chối khi Run kèm lý do —
không có fallback ngầm về phân tích bậc nhất. Ở cửa sổ chính, form Load Case Data nay định nghĩa đủ bốn loại và đổi
trường theo loại: nonlinear / Initial P-Delta dùng **Continue from**, linear / modal dùng **Stiffness from**; modal có
mass source (model chưa có thì được đề xuất `MS1` = khối lượng bản thân) và số mode, nonlinear có geometric
nonlinearity và controls riêng. Trường không thuộc loại đang chọn không tham gia vào case. Loại chưa được giải ghi rõ
"Unavailable — verification pending"; case mới thuộc loại đó được thêm với Run tắt. Set Load Cases to Run hiển thị run
plan (`CaseExecutionCoordinator` → `CaseDependencyPlanner`): Requested / Prerequisite, phụ thuộc, khả dụng.
`FemLoadPlan.Build` chạy theo plan đó, **theo thứ tự của bảng Load Cases**; case không tick nhưng được case khác tiếp tục
từ nay được giải và liệt kê như một prerequisite (trước đây chỉ cộng load), kết quả các case còn lại không đổi.
`PDELTA_GRAVITY → LIN_WIND` không cộng load của PDELTA vào WIND. Solver vẫn nhận mỗi case nối tiếp dưới dạng load cộng
dồn (CASE-01); state transfer là PR-04, bước tiếp theo, còn bị chặn bởi literature gate (ALG-07, ALG-08). Native
workspace chưa có form load case.

**Cập nhật di chuyển cửa sổ chính (2026-10-14, người dùng giao nhiệm vụ M0–M8: "do all"):** các quyết định sau thay cho
mọi chỗ cũ nói khác.

1. **`ModelDocument` là nguồn dữ liệu duy nhất của cửa sổ chính.** Model cũ (`GeometryModel`, `FemConfiguration`,
   `FemSectionProperties`, `.cotfem.db`) trở thành bản chiếu chỉ đọc sinh ra từ document, chỉ tồn tại cho các tính năng
   chưa chuyển, và bị xóa khi tính năng cuối cùng đã chuyển.
2. **Một cửa sổ.** Khi chuyển xong, `NativeModelWindow` được gỡ; Edit → TemplateBuilding… sinh model vào cửa sổ chính.
3. **Model mặc định khi khởi động: hệ vòm có dây căng (TAS), dạng native document, view 3D, phân tích Full 3D.** TAS
   vẫn là khung phẳng; ứng xử ngoài mặt phẳng phải được giữ bằng dữ liệu thật (tiết diện, gối), không bằng mẹo của
   solver. Hệ vòm không gian không thuộc nhiệm vụ này.
4. Modal, 2D theo DOF mask và mọi khả năng tĩnh chạy trong cửa sổ chính vì cửa sổ chính chạy cùng đường
   `ModelDocument` → `Mcotis.Analysis` như cửa sổ native.
5. **Bỏ hẳn workflow MB** khỏi nhiệm vụ: không port, không kiểm, không xóa; code dùng chung (VCF, báo cáo FEM…) được
   chuyển theo phần mà tính năng đang dùng.

Optimization (menu Optimization VCF / TAS / Results) và Design là công cụ sản xuất: phải cho đúng kết quả đã đóng băng
ở cuối mỗi bước (`tests/Mcotis.Desktop.Checks/Fixtures/production-baselines.json`, `--optimization-study`,
`--column-design`); mọi khác biệt có chủ đích phải ghi vào development log và được người dùng duyệt. Quyền tối ưu do
cờ `GeneratorOrigin` lưu trong document quyết định (generator VCF / TAS), không theo tên model.

**Trạng thái (2026-10-14): M0–M3 xong.**

- **M0 — baseline và capability gate:** khoảng trống inertia ngoài mặt phẳng đã đóng ở tầng solver
  (`FrameStiffnessDemand`), template có khối lượng riêng, kết quả Optimization và Design được đóng băng trong
  `production-baselines.json`.
- **M1 — một đường phân tích:** Run FEM Analysis và mọi case của Optimization VCF / TAS giải native document qua
  `FemDocumentRun`; tên đi qua `LegacyNames`, kết quả có trạng thái theo `AnalysisInputHash`, modal chạy trong cửa sổ
  chính. Kết quả 6 thành phần và modal còn giữ trong bộ nhớ (chưa có schema output — việc của giai đoạn persistence).
- **M2 — document lifecycle của cửa sổ chính:** document giữ toàn bộ cấu hình cần để phục hồi model; mỗi lệnh edit là
  một bước của `DocumentSession`, Undo / Redo phục hồi model từ document, dirty state và dấu `*` theo document, Save
  đánh dấu đúng revision. Joint restraints là assignment tường minh, sửa được và phản lực gối fixed có `My`. Phần UI
  chưa chuyển vẫn dùng `NativeToLegacyProjection` như ranh giới chuyển tiếp; bridge này chỉ được xóa khi consumer cũ
  cuối cùng chuyển xong, không tạo model thứ hai để phân tích.
- **M3 — native generators và parametric studies:** VCF / TAS mang `GeneratorOrigin` và `GenerationKey`, validation
  quyết định Optimization có được bật; Re-apply / Detach là các bước Undo được. Hai optimization dùng chung
  `ParametricOptimization`, sinh một native document cho từng candidate và giữ nguyên production baselines.

Gate đã chạy trên working tree này: build; `Mcotis.Checks --full` (3.093 checks, gồm OpenSees static / cable / modal);
`Mcotis.Desktop.Checks --full` (gồm model history, generator flags, native parity và production baselines). Live
SAP2000 / ETABS vẫn chưa nằm trong gate này.

## 0. Định hướng đã chốt

| Nội dung | Quyết định (người dùng, 2026-10-12) |
|---|---|
| SAP2000 / CSI | Bỏ qua hoàn toàn. Không import, không export, không có giai đoạn tích hợp CSI. Code SAP hiện có chỉ để tham khảo, không mở rộng. |
| Các trang MB | Đã archive toàn bộ, không còn menu hay trang MB hoạt động. Chỉ giữ source đông băng để tham khảo; không nối vào hệ mới. |
| Hệ mới | Các project thư viện riêng trong `src/` (`Mcotis.Core`, `Mcotis.Application`, `Mcotis.Persistence`, `Mcotis.Analysis`), không phụ thuộc app desktop, WPF, SQLite hay CSI. App hiện tại là `src/Mcotis.Desktop`, vẫn chạy cho đến khi từng tính năng được chuyển sang. |

Hệ quả: mọi mục của plan cũ nói về CSI service, version adapter, Import Report và "Save gọi SAP" đều rời khỏi phạm vi.

## 1. Quyết định kiến trúc

| Nội dung | Quyết định |
|---|---|
| Model trung tâm | Một `ModelDocument` chứa toàn bộ dữ liệu kỹ thuật, **bất biến**: mỗi lần sửa tạo ra document mới |
| Identity | Mỗi object có `ObjectId` (GUID) bất biến, tồn tại qua Save/Open, Rename, Undo |
| Naming | `Name` duy nhất, chung một namespace cho Point / Frame / Cable / Shell |
| Quan hệ giữa objects | Tham chiếu bằng ID, không bằng Name |
| Editing | Mọi thay đổi đi qua `DocumentEditor` trong một command; hoặc thành công trọn vẹn, hoặc document giữ nguyên |
| Undo/Redo | Quay về đúng document trước đó — không cần thao tác đảo ngược cho từng command |
| Solver | Nhận một `ModelDocument` (bản thân nó đã là snapshot); không đọc UI, file hay CSI |
| Results | Gắn với `AnalysisInputHash`; trạng thái Not run / Running / Current / Stale / Failed |
| Save | Luôn ghi document đang mở; không đọc lại từ nguồn nào khác |
| File native | `.mcotis`: một file JSON là nguồn duy nhất, có `schemaVersion` và migration tuần tự |

**Khác với plan cũ, cần biết:**

- **Cable là object riêng** (`CableObject`), không phải một loại Frame (người dùng, 2026-10-12). Nó có section riêng
  (`CableSectionDefinition`: material + diện tích), assignment riêng (`CableAssignment`: section + modifiers) và load
  riêng (`CableTargetForce`). Section của frame không gán được cho cable và ngược lại. Plan cũ chỉ có Point/Frame/Shell.
- **Không còn Lock/Unlock** trong hệ mới. Kết quả không bị xóa khi sửa model; chúng chuyển sang *Stale*, và Undo đưa về
  *Current*.
- **File là JSON, không phải SQLite.** Lý do: document luôn được đọc và ghi trọn vẹn, nên một file văn bản đủ đáp ứng
  "một nguồn authoritative", ghi atomic, và không kéo theo thư viện native. Đổi lại sau này chỉ cần thay
  `IDocumentStore`. Kết quả phân tích (lớn, dạng bảng) sẽ có store riêng ở giai đoạn 6.

## 2. Identity và unique Name

| Quy tắc | Cách thực hiện |
|---|---|
| Chuẩn hóa | Trim hai đầu, Unicode NFC; từ chối tên rỗng, toàn khoảng trắng hoặc có ký tự điều khiển |
| So sánh | Không phân biệt hoa/thường (`OrdinalIgnoreCase`): Point `P1` và Frame `p1` không cùng tồn tại |
| Phạm vi | Point/Frame/Cable/Shell chung một scope. Material, Frame section, Cable section, Shell section, Load pattern mỗi loại một scope. Load case và Combination chung một scope (kết quả được chọn theo một tên) |
| Tên tự sinh | `P1`, `F1`, `C1` (cable), `S1`; luôn là số kế tiếp sau số lớn nhất mà dãy đã đạt tới |
| Delete | Không đổi tên object nào còn lại và không làm dãy số lùi lại (người dùng, 2026-10-12): xóa `F2` thì `F1`, `F3`, `F4` giữ nguyên và frame mới là `F5`; xóa `F5` thì frame kế tiếp là `F6`. Mỗi loại object có bộ đếm riêng, lưu trong file nên vẫn đúng sau Save/Open. Undo một lần tạo thì trả lại số đó |
| Tên gõ tay | Tên gõ tay rơi vào dãy (`F20`) đẩy dãy lên theo. Tên đã bị xóa vẫn có thể gõ tay để dùng lại; tên tự sinh thì không quay lại lấy |
| Rename | Đổi Name, giữ ID; connectivity, assignments, loads không đổi |
| Batch rename | Xét theo kết quả cuối: cho phép đổi chéo tên; có collision thì từ chối cả lô |
| Duplicate | ID mới, Name mới, kèm assignments (không kèm loads) |
| Undo delete | Khôi phục đúng ID, Name, assignments và loads |
| Nguồn gốc | `ObjectOrigin`: User / Generated / Imported, kèm `GenerationKey` để generator giữ ID khi sinh lại |

`NamingService` là nơi duy nhất quyết định tên hợp lệ, cách so sánh và tên tự sinh.

## 3. Native model

| Entity | Dữ liệu |
|---|---|
| `PointObject` | ID, Name, XYZ |
| `FrameObject` | ID, Name, hai PointId |
| `CableObject` | ID, Name, hai PointId |
| `ShellObject` | ID, Name, các PointId theo thứ tự biên |
| `MaterialDefinition` | Loại, E, ν, trọng lượng riêng, khối lượng riêng |
| `FrameSectionDefinition` | Material, shape, dimensions, A / I33 / I22 / J / As2 / As3 |
| `CableSectionDefinition` | Material, diện tích |
| `ShellSectionDefinition` | Material, loại, chiều dày |
| Assignments (tách khỏi geometry) | Restraint theo point; section, releases, local axis, modifiers theo frame; section, modifiers (area / mass / weight) theo cable; section, local axis theo shell |
| Loads | Point load, frame distributed / point load, cable target force, shell uniform load — mỗi load thuộc một pattern |
| Load case | Loại (`CaseKind`: LinearStatic / NonlinearStatic / InitialPDelta / ModalEigen), components (pattern × hệ số), initial case (case nonlinear mà nó tiếp tục từ), stiffness source (case nonlinear mà case linear / modal lấy stiffness), geometric nonlinearity (None / PDelta), controls riêng (load steps, iterations, dung sai), modal options (mass source, số mode), Run, fit cable target forces |
| Mass source | ID, Name, có tính khối lượng bản thân hay không — dữ liệu cho modal; chưa có phép tính nào đọc nó |
| Combination | Terms (case hoặc combination × hệ số), loại |
| Analysis settings | Dung sai và số vòng fit cáp, số đoạn mesh, cable sag, DOF đang bật, DOF của nút cable (mặc định UX UY UZ) |

Đơn vị cố định trong document: kN, m, t. Chuyển đổi đơn vị thuộc về UI.

`DocumentValidator` chặn: tọa độ không hữu hạn; frame hoặc cable thiếu điểm đầu hoặc dài bằng 0; shell dưới 3 điểm, lặp điểm, suy
biến, biên tự cắt (shell vênh chỉ là cảnh báo); tham chiếu không tồn tại; load đặt sai loại object; vòng lặp trong
initial case hoặc combination; tên trùng; và các quy tắc theo loại case (`LoadCaseRules`): case linear không tiếp tục
từ case khác, case modal không có load, chỉ case nonlinear / initial P-Delta mới được tiếp tục từ hoặc cho mượn
stiffness, không case nào phụ thuộc vào chính nó, combination không lấy case modal. Một frame chưa gán section vẫn là document hợp lệ — đó là việc của
**solver capability validator** (giai đoạn 4), tách riêng.

## 4. Cấu trúc solution

| Project | Trách nhiệm | Trạng thái |
|---|---|---|
| `src/Mcotis.Core` | Native model, ID, naming, editor, validator, analysis input hash | Có |
| `src/Mcotis.Application` | `DocumentSession`, commands, Undo/Redo, New/Open/Save, result state, contracts | Có |
| `src/Mcotis.Persistence` | File `.mcotis`, migration, atomic save | Có |
| `tests/Mcotis.Checks` | Checks offline của hệ mới | Có |
| `src/Mcotis.Analysis` | Immutable topology/DOF compiler; `StaticSolver` 3D cho frame và cable | Có compiler và solver dense: frame (tự chia tại điểm nằm trên nó), cable tension-only, target force, staged case, sag, chẩn đoán ổn định số theo từng DOF; shell và solver sparse chưa có |
| `src/Mcotis.Desktop` | App WPF hiện tại; TemplateBuilding workspace đã dùng các thư viện native | Native Save/Open, DOF options, linear static và result tables đã có; migration đầy đủ còn ở giai đoạn 6 |
| `tests/Mcotis.Desktop.Checks` | Checks offline của app | Có |

Core chỉ tham chiếu .NET runtime; Application chỉ tham chiếu Core; không project nào tham chiếu WPF, SQLite hay CSI.
`Mcotis.Checks` kiểm tra điều này bằng reflection.

## 5. Phân luồng ViewModel (giai đoạn 6)

Giữ như plan cũ, bỏ nhóm Import / ExternalConnections:

| Nhóm | ViewModels |
|---|---|
| Shell | `ShellViewModel` (chỉ điều hướng và trạng thái chung), `DocumentViewModel` |
| Define | Materials, FrameSections, ShellSections, LoadPatterns, LoadCases, Combinations |
| Edit | GeometryEditor, VcfSystem, RenameObjects |
| Assign | Sections, Restraints, Releases, LocalAxes, Loads |
| Analysis | AnalysisSetup, AnalysisMonitor |
| Results | ResultSelection, ResultTable, ResultDisplay |
| Display | Viewport, Selection, DisplayOptions |
| File | Recovery |

ViewModel gọi command, không sửa document trực tiếp. Panel nghe `DocumentSession.Changed` và chỉ làm mới phần mà
`DocumentChangeSet` nêu. Một `SelectionService` giữ các ID đang chọn; selection, zoom, màu không làm document dirty.
Dialog làm việc trên dữ liệu tạm: Apply = validate + commit; OK = commit rồi đóng; Cancel = bỏ.

## 6. Lộ trình

| Giai đoạn | Công việc | Điều kiện hoàn tất | Trạng thái |
|---|---|---|---|
| **0 — Baseline** | Menu MB xuống cuối sau vạch ngăn; checks của hệ mới chạy không cần SAP2000 | Hệ mới build và test trên máy không cài SAP | Xong |
| **1 — Core document** | Native model, ID, `NameIndex`, `NamingService`, validators | Unique Name; rename không làm hỏng references | Xong |
| **2 — Document lifecycle** | `DocumentSession`, New / Open / Save / Save As | Save đúng document đang mở; Open lỗi giữ nguyên model | Xong ở tầng thư viện |
| **3 — Edit commands** | Commands, Undo/Redo, change sets, revision, dirty, result state | Mọi thao tác phục hồi được; invalidation nhất quán | Xong ở tầng thư viện |
| **4 — Analysis boundary** | Shared 3D engine, planar DOF presets, snapshot từ `ModelDocument`; capability check (chặn Shell…); results gắn document ID + input hash | Numerical source gates + XZ regression + analytical/OpenSeesPy checks | Compiler và `StaticSolver` xong cho frame và cable (target force, staged case, sag, frame có nút giữa, station dọc frame), đã đối chiếu ba nguồn kể cả default model; toàn bộ app đã chạy trên engine này và solver planar đã archive. Chẩn đoán ổn định số đã có (2026-10-13): số chữ số chính xác bị mất theo từng DOF, stiffness suy biến / gần suy biến và các DOF tham gia — [solver/numerical-stability-diagnostics.md](solver/numerical-stability-diagnostics.md). Còn shell, solver sparse |
| **5 — Hệ VCF trên native model** | Generator VCF tạo/sinh lại geometry qua command, giữ ID bằng `GenerationKey`; tên theo vai trò | Đổi số bay giữ ID và assignments của object không đổi | M3 xong: VCF/TAS có `GeneratorOrigin`, stable `GenerationKey`, validation, Re-apply / Detach có Undo; đổi số bay giữ identity theo generation key. Optimization đọc flag, không đoán theo tên model |
| **6 — Desktop** | `Mcotis.Desktop`: shell, các ViewModel ở mục 5, selection chung, viewport đọc document | Tạo → lưu → mở → phân tích → xem kết quả hoàn toàn trên hệ mới | Đang làm sau M2/M3: main workspace có một `DocumentSession`; Run FEM Analysis, modal và mọi optimization candidate đã đọc native document qua `FemDocumentRun`. Undo / Redo, dirty state, explicit six-DOF restraints, fixed-support `My`, generator validation và Re-apply / Detach đã xong. Controls và viewport chưa chuyển hết nên còn đọc `NativeToLegacyProjection`; `NativeModelWindow` chưa retire; chưa chạy export trên SAP2000 / ETABS thật |
| **7 — Persistence hoàn chỉnh** | Autosave nền theo revision, recovery, results store, đọc file `.cotfem.db` cũ, default model dạng `.mcotis` | Recovery không báo thành công giả; file cũ mở được | Đang làm: `.cotfem.db` được chuyển xác định sang native, giữ restraint 6-DOF qua Save/Open và round-trip `.mcotis`; default file và autosave chính vẫn chưa đổi sang `.mcotis` |
| **8 — Release** | CI, đo hiệu năng, đóng gói; gỡ phần app cũ đã được thay | App chạy trọn vẹn trên máy không có SAP/ETABS | |

Thứ tự 4 → 6 khác plan cũ (ViewModel trước, Analysis sau): đưa solver sang trước cho phép đối chiếu số với kết quả
hiện có trước khi động vào UI. (Ghi chú 2026-10-12: default model hiện tại cho độ võng lớn nhất Pretension 22.9 mm,
SLS 243.8 mm, ULS 392.5 mm ở cả solver planar lẫn `StaticSolver`; hai con số 39.6 mm / 46.3 mm từng ghi ở đây không
được solver nào tái hiện, chưa rõ nguồn.)

Shell: geometry, assignments và persistence đã có; chưa có shell solver. Capability check ở giai đoạn 4 phải chặn
chạy model có Shell và nói rõ lý do.

## 7. Checks

`dotnet run --project tests/Mcotis.Checks -- --full` — full verification (3017 checks ngày 2026-10-13), không cần SAP2000, WPF hay database:

| Nhóm | Nội dung |
|---|---|
| Naming | Collision khác hoa/thường và giữa các loại, tên tự sinh, xóa không làm đổi dãy tên, batch rename (đổi chéo, collision), duplicate |
| References | Rename / delete / undo không để lại tham chiếu treo; xóa object đang được dùng bị từ chối |
| Validation | Từng quy tắc ở mục 3; edit thất bại không làm đổi document |
| Commands | Change set, revision, Undo/Redo, dirty state, giới hạn history |
| Result state | Hash bỏ qua tên và thứ tự dựng; current / stale / failed; snapshot không bị edit ảnh hưởng |
| Native file | Round-trip; Save đúng dữ liệu đang thấy; Open lỗi giữ nguyên model; file thiếu/thừa field bị từ chối; migration không ghi file gốc; atomic save |
| Load cases | Model mẫu của plan (NL_PRET → PDELTA_GRAVITY → LIN_WINDX / MODES_PD, các nhánh nonlinear độc lập NL_SLS / NL_ULS / NL_ALTERNATE) và run graph của nó: thứ tự, prerequisite, vòng lặp, case bị chặn; quy tắc từng loại; hash; những gì solver từ chối (`static.case.*`); schema 5 round-trip và nâng cấp file schema 3/4 |
| DOF compilation | Full 3D/plane/custom masks, deterministic mapping, support loads, failed compilation, immutable snapshot, schema-1 migration |
| Spatial static | Ma trận 12×12 kiểm bằng tích phân độc lập, lời giải giải tích, cân bằng toàn cục, các trường hợp từ chối, đối chiếu OpenSeesPy (12 model) |
| General static | Frame chia tại điểm nằm trên nó, cable tension-only, target force (một cable, hai cable tương tác, hai cable nối tiếp), staged case, sag so với lời giải kín và catenary chính xác, xoay cả kết cấu trong không gian, các trường hợp từ chối, đối chiếu OpenSeesPy (4 model có cable, 11 lời giải) |
| Stability diagnostics | Ước lượng số chữ số bị mất log10(Kii·(K⁻¹)ii) so với lời giải kín, với nghịch đảo tính độc lập và với chuyển vị của chính solver; khung ổn định 2D / 3D, cơ cấu phẳng, quay tự do trong 3D, gần suy biến (không báo ∞), suy biến, không hội tụ (không coi là mất ổn định), DOF không hoạt động, các case độc lập với nhau |
| Independence | Core, Application, Persistence và Analysis không tham chiếu WPF / SQLite / CSI |

`dotnet run --project tests/Mcotis.Desktop.Checks -- --general-solver` đối chiếu phép phân tích của app (`FemSolver` →
`StaticSolver`) với solver planar đã archive trên cùng dữ liệu đầu vào: một cable truss và default model như app chạy.
`--fem` (58 checks lời giải kín của app) và `--fem-sap` (study SAP2000 đã lưu) nay cũng chạy qua solver tổng quát.
`--fem-schema` kiểm tra configuration của app ở schema 2: nâng cấp một `.cotfem.db` schema 1 và một `.cotfem.json`
version 1 thật (ID, pattern, hệ số, revision không đổi), bốn loại case qua cả hai file, các trường hợp bị từ chối.

Còn thiếu so với danh sách của plan cũ: UX (Cancel dialog, selection đồng bộ) — thuộc giai đoạn 6.

## 8. Việc còn để ngỏ

1. **Tên object do VCF generator sinh**: giữ quy ước theo vai trò hiện tại (`TTrPt001`, `BTrPt…`, `Cable9a-2`) hay
   chuyển sang `P1` / `F1` / `C1`. Hệ mới hỗ trợ cả hai; cần chốt trước giai đoạn 5.
2. **Các lối vào SAP còn lại**: File → Read FEM Properties from SAP và nút *Get From SAP2000* trong Define
   Materials / Sections hiện giữ nguyên. Toàn bộ menu/trang MB, Study Reports, Print All Reports và Run Study /
   Cancel Study đã rời khỏi UI hoạt động.
3. **Save As và document ID**: bản sao giữ hay đổi `DocumentId` — quyết định cùng results store (giai đoạn 7).
4. **Tendon**: chưa có trong model. Khi cần sẽ là một object riêng như Cable, không phải một loại Frame.
