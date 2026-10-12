# Mcotis — kế hoạch hệ thống độc lập

Bản chỉnh ngày 2026-10-12. Thay cho bản plan 10 mục trước đó, và thay cho các quyết định về Lock/Unlock, SQLite và
"stable ID theo tên" trong [fem-database-plan.md](fem-database-plan.md) đối với hệ mới.

**Cập nhật 3D (2026-10-12):** theo yêu cầu mới, tầng analysis sẽ dùng chung model XYZ và DOF presets cho 3D/XZ/XY/YZ,
thay cho việc port nguyên solver planar. Foundation đã có: `Mcotis.Analysis.ModelCompiler`, DOF settings lưu ở schema 2,
migration schema 1 → XZ, mapping sáu DOF và diagnostics. Đã có thêm một engine tuyến tính 3D cho frame (dense, dùng để
kiểm chứng công thức): `LinearStaticSolver`, đối chiếu với lời giải giải tích, OpenSeesPy và solver planar hiện tại —
phạm vi, nguồn công thức và bằng chứng ở [analysis/spatial-static-verification.md](analysis/spatial-static-verification.md).
Engine này chưa xử lý cable, shell, frame có nút ở giữa, và chưa có UI. Chi tiết và source gates:
[analysis/foundation-status.md](analysis/foundation-status.md), [implementation plan](analysis/IMPLEMENTATION_PLAN.md),
[literature register](analysis/literature.md). P-Delta và modal vẫn là các giai đoạn sau.

## 0. Định hướng đã chốt

| Nội dung | Quyết định (người dùng, 2026-10-12) |
|---|---|
| SAP2000 / CSI | Bỏ qua hoàn toàn. Không import, không export, không có giai đoạn tích hợp CSI. Code SAP hiện có chỉ để tham khảo, không mở rộng. |
| Các trang MB | Chỉ để tham khảo. Giữ nguyên hành vi, không refactor, không nối vào hệ mới. Menu **MB** nằm cuối thanh menu, sau một vạch ngăn. |
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
| Load case | Loại, components (pattern × hệ số), initial case, Run, fit cable target forces |
| Combination | Terms (case hoặc combination × hệ số), loại |
| Analysis settings | Dung sai và số vòng fit cáp, số đoạn mesh, cable sag |

Đơn vị cố định trong document: kN, m, t. Chuyển đổi đơn vị thuộc về UI.

`DocumentValidator` chặn: tọa độ không hữu hạn; frame hoặc cable thiếu điểm đầu hoặc dài bằng 0; shell dưới 3 điểm, lặp điểm, suy
biến, biên tự cắt (shell vênh chỉ là cảnh báo); tham chiếu không tồn tại; load đặt sai loại object; vòng lặp trong
initial case hoặc combination; tên trùng. Một frame chưa gán section vẫn là document hợp lệ — đó là việc của
**solver capability validator** (giai đoạn 4), tách riêng.

## 4. Cấu trúc solution

| Project | Trách nhiệm | Trạng thái |
|---|---|---|
| `src/Mcotis.Core` | Native model, ID, naming, editor, validator, analysis input hash | Có |
| `src/Mcotis.Application` | `DocumentSession`, commands, Undo/Redo, New/Open/Save, result state, contracts | Có |
| `src/Mcotis.Persistence` | File `.mcotis`, migration, atomic save | Có |
| `tests/Mcotis.Checks` | Checks offline của hệ mới | Có |
| `src/Mcotis.Analysis` | Immutable topology/DOF compiler; engine tuyến tính 3D cho frame | Có compiler và engine frame (dense); cable, shell, solver sparse chưa có |
| `src/Mcotis.Desktop` | App WPF hiện tại (chưa dùng các thư viện trên); sẽ chuyển dần sang shell và ViewModels mới | App hiện tại có; chuyển đổi ở giai đoạn 6 |
| `tests/COT_VCF.Checks` | Checks offline của app | Có |

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
| **4 — Analysis boundary** | Shared 3D engine, planar DOF presets, snapshot từ `ModelDocument`; capability check (chặn Shell…); results gắn document ID + input hash | Numerical source gates + XZ regression + analytical/OpenSeesPy checks | Compiler và engine frame tuyến tính 3D xong, đã đối chiếu; còn cable, shell, frame có nút giữa, solver sparse, và nối vào app |
| **5 — Hệ VCF trên native model** | Generator VCF tạo/sinh lại geometry qua command, giữ ID bằng `GenerationKey`; tên theo vai trò | Đổi số bay giữ ID và assignments của object không đổi | |
| **6 — Desktop** | `Mcotis.Desktop`: shell, các ViewModel ở mục 5, selection chung, viewport đọc document | Tạo → lưu → mở → phân tích → xem kết quả hoàn toàn trên hệ mới | |
| **7 — Persistence hoàn chỉnh** | Autosave nền theo revision, recovery, results store, đọc file `.cotfem.db` cũ, default model dạng `.mcotis` | Recovery không báo thành công giả; file cũ mở được | |
| **8 — Release** | CI, đo hiệu năng, đóng gói; gỡ phần app cũ đã được thay | App chạy trọn vẹn trên máy không có SAP/ETABS | |

Thứ tự 4 → 6 khác plan cũ (ViewModel trước, Analysis sau): đưa solver sang trước cho phép đối chiếu số với kết quả
hiện có (default model: SLS 39.6 mm, ULS 46.3 mm) trước khi động vào UI.

Shell: geometry, assignments và persistence đã có; chưa có shell solver. Capability check ở giai đoạn 4 phải chặn
chạy model có Shell và nói rõ lý do.

## 7. Checks

`dotnet run --project tests/Mcotis.Checks` — 1447 checks, không cần SAP2000, WPF hay database:

| Nhóm | Nội dung |
|---|---|
| Naming | Collision khác hoa/thường và giữa các loại, tên tự sinh, xóa không làm đổi dãy tên, batch rename (đổi chéo, collision), duplicate |
| References | Rename / delete / undo không để lại tham chiếu treo; xóa object đang được dùng bị từ chối |
| Validation | Từng quy tắc ở mục 3; edit thất bại không làm đổi document |
| Commands | Change set, revision, Undo/Redo, dirty state, giới hạn history |
| Result state | Hash bỏ qua tên và thứ tự dựng; current / stale / failed; snapshot không bị edit ảnh hưởng |
| Native file | Round-trip; Save đúng dữ liệu đang thấy; Open lỗi giữ nguyên model; file thiếu/thừa field bị từ chối; migration không ghi file gốc; atomic save |
| DOF compilation | Full 3D/plane/custom masks, deterministic mapping, support loads, failed compilation, immutable snapshot, schema-1 migration |
| Spatial static | Ma trận 12×12 kiểm bằng tích phân độc lập, lời giải giải tích, cân bằng toàn cục, các trường hợp từ chối, đối chiếu OpenSeesPy (12 model) |
| Independence | Core, Application, Persistence và Analysis không tham chiếu WPF / SQLite / CSI |

Còn thiếu so với danh sách của plan cũ: UX (Cancel dialog, selection đồng bộ) — thuộc giai đoạn 6; Analysis so với
solver — giai đoạn 4.

## 8. Việc còn để ngỏ

1. **Tên object do VCF generator sinh**: giữ quy ước theo vai trò hiện tại (`TTrPt001`, `BTrPt…`, `Cable9a-2`) hay
   chuyển sang `P1` / `F1` / `C1`. Hệ mới hỗ trợ cả hai; cần chốt trước giai đoạn 5.
2. **Các lối vào SAP/MB còn nằm ngoài menu MB**: File → Read FEM Properties from SAP, Run → Run Study / Cancel Study,
   Post-Processing → Study Reports…, nút *Get From SAP2000* trong Define Materials / Sections. Hiện giữ nguyên.
3. **Save As và document ID**: bản sao giữ hay đổi `DocumentId` — quyết định cùng results store (giai đoạn 7).
4. **Tendon**: chưa có trong model. Khi cần sẽ là một object riêng như Cable, không phải một loại Frame.
