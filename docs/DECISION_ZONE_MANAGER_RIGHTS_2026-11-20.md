# Quyết định: quyền của Quản lý vùng (20/11/2026)

**Người chốt:** chủ dự án. **Phạm vi:** backend (`FixedRoles`, `WorkDayPlansController`, `WorkforceController` và các service phía sau). **Tình trạng:** đã sửa code, chưa build (SDK 10 so với 8 ghim), chưa chạy test. **Chờ PM:** đưa vào SRS §I.4.4.

Quyết định này **đảo một phần** hai quyết định trước: ma trận quyền 27/10/2026 (§I.4.4, BR-FARM-07) và quyết định 02/11/2026 cho Quản lý vùng bán hàng tại cảng, xem mọi đơn, khách hàng và yêu cầu mua. Từ nay bán hàng là việc của Chủ HTX.

## Ma trận Quản lý vùng (`ZONE_MANAGER`) trước và sau

| Chức năng | Trước | Sau |
|---|---|---|
| `SALES_ORDERS` | Xem, tạo, sửa | Không có |
| `SALES_INVOICES` | Xem, tạo, xuất | Không có |
| `SALES_RETURNS` | Xem, tạo | Không có |
| `CUSTOMERS` | Đủ quyền | Không có |
| `QUALITY_CLAIMS` | Xem | Không có |
| `PURCHASE_REQUESTS` | Xem | Không có |
| `LOT_TRACING`, `PROVENANCE`, `HARVEST_LOTS`, `HARVESTS` | Như cũ | Như cũ |
| `HARVEST_PLANS` | Xem | Xem (không đổi) |
| `PURCHASE_RECEIPTS`, `SUPPLIERS`, `INPUT_ISSUES`, `PRODUCT_DISPOSALS` | Như cũ | Như cũ |
| `WORK_ASSIGNMENTS`, `ATTENDANCE` | Đủ quyền theo vùng | Đủ quyền theo vùng; thêm các hành động ở dưới |
| `PAYROLL`, `PAY_RULES`, thanh toán, công nợ, sổ quỹ, báo cáo bán hàng | Không có | Không có (không đổi) |

Kế toán (`ACCOUNTANT`) không đổi. Chủ HTX không bị ảnh hưởng (quyền theo loại tài khoản, không theo ma trận).

## Hành động mới của Quản lý vùng, giới hạn theo vùng

- **Kế hoạch ngày làm** (`GET /api/work-plans/responsibles`, `POST /api/work-plans`, `PUT /api/work-plans/{id}`): trước đây chỉ Chủ HTX (`[RequireCooperativeOwner]`); nay cần `WORK_ASSIGNMENTS` Create (tạo, người phụ trách) hoặc Edit (sửa) ở vùng của bè. Quản lý vùng chỉ đặt chính mình làm người phụ trách (kế hoạch được duyệt ngay, nằm trong danh sách của mình), chỉ sửa kế hoạch mình phụ trách, và không gắn kế hoạch với yêu cầu mua của thương lái (403; yêu cầu mua là của Chủ HTX).
- **Sửa chấm công** (`GET`/`PUT /api/workforce/attendance/{employeeId}/{day}`): cần `ATTENDANCE` Edit ở mọi vùng mà ngày đó chạm tới (vùng của các giai đoạn việc trong ngày; nhân viên Staff hoặc ngày chưa có phân công: một vùng làm việc của nhân viên) và ở vùng của bè mới. Giữ nguyên cho mọi người, kể cả Chủ HTX: lý do bắt buộc (`Reason`), chặn khi ngày thuộc kỳ lương Chờ chốt hoặc Đã chốt (409 MSG-LABOR-04), nhật ký `Attendance.Corrected` có giá trị trước/sau và lý do. Đã kiểm trong code: cả ba điều này đã có sẵn, không thêm luật mới.
- **Duyệt đơn nghỉ / thiếu giờ** (`POST /api/workforce/leave-requests/{id}/review`): cần `ATTENDANCE` Edit ở một vùng làm việc của nhân viên (đơn không có vùng riêng); khi duyệt thiếu giờ thì cần quyền ở vùng của các giai đoạn việc trong ngày và vùng của bè Chủ chọn cho thợ thời vụ chưa có việc. Đọc danh sách đơn (`GET .../leave-requests`) giữ nguyên.

## Không làm / chưa làm

- **Kế hoạch thu hoạch, tiếp nhận và bàn giao lại (D3):** backend chưa có trạng thái "đã tiếp nhận" hay "bàn giao lại". Hiện có Nháp, Đã giao (Chủ HTX bấm "Giao QLV", thông báo `HARVEST_PLAN_ASSIGNED`), Đang thu (tự động khi có lô hoặc luống bỏ qua đầu tiên), Hoàn tất (tự động khi mọi dòng xong, hoặc Chủ HTX "Kết thúc"), Hủy. Quản lý vùng đã xem kế hoạch đã giao, ghi lô thu hoạch, bỏ luống và chọn nhân công cho dòng. Thêm bước tiếp nhận/bàn giao là đổi chuyển trạng thái nên cần chủ xác nhận (AGENTS.md); đề xuất ở báo cáo giao việc.
- **Một Kế toán:** backend không ép; Web cảnh báo.
- Báo cáo theo vùng cho Quản lý vùng: chưa làm trong đợt này (cần danh sách báo cáo và mã quyền từng báo cáo).

## Ảnh hưởng và rủi ro

- Web: `/api/me/permissions` của Quản lý vùng không còn các chức năng bán hàng; menu và nút phải theo đó. Màn bán tại cảng, khách hàng, yêu cầu mua, khiếu nại, trả hàng chỉ còn Chủ HTX (Kế toán giữ xem/xuất hóa đơn, đơn, khách hàng như cũ).
- Truy vết lô và truy vết ngược theo lô giống vẫn hiện hóa đơn đã bán (mã, kg) cho Quản lý vùng nhưng không còn giá, thành tiền, tên và số điện thoại thương lái (các trường này theo `SALES_INVOICES` View).
- Kế hoạch thu hoạch vẫn trả đơn đặt hàng và khách hàng đã gắn (mã, tên) cho Quản lý vùng, vì họ cần biết thu cho ai; không gắn với quyền `CUSTOMERS`.
- Màn sản phẩm: Quản lý vùng từ "người bán" thành "người xem" (vẫn thấy giá và tồn như cũ).
- Dòng mô tả vai trò trong bảng `Roles` đã seed không tự cập nhật (chỉ mô tả; quyền lấy từ `FixedRoles` lúc chạy).
- Test tích hợp bán hàng, truy vết và báo cáo dùng "Quản lý vùng của vùng" làm người bán (`SaleSetup.Manager` trong `SalesInvoiceTestKit`, khoảng 85 file) sẽ hỏng cho tới khi bộ kit đổi người bán sang Chủ HTX; chưa sửa vì không chạy được.
