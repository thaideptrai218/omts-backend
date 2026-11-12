# Khác biệt phía Backend so với SRS v8 (tóm tắt)

**Cập nhật:** 11/11/2026. Đây chỉ là tóm tắt các mục phía backend. Danh sách đầy đủ, gồm cả phía Web, là tài liệu chủ trong repo Web: `docs/SRS_DEVIATIONS.md` (mã ID bên dưới khớp với tài liệu đó). Phần chi tiết hành vi API nằm trong [api-behaviour-reference.md](api-behaviour-reference.md) và [operations-reference.md](operations-reference.md).

**Trạng thái** lấy theo code trên nhánh `feature/OMTS-132-farming-rules` ngày 11/11. "Đang làm" nghĩa là có thay đổi chưa commit.

## Bảng tóm tắt

| ID | Nội dung khác SRS v8 (backend) | Trạng thái backend |
|---|---|---|
| AC-01 | Đăng nhập bằng số điện thoại hoặc tên đăng nhập; email không phải định danh. Đăng ký từ chối email (`RegistrationService`). Nhật ký 11/11 ghi module tài khoản chưa làm, cần kiểm tra lại trên `develop` (Q18) | Đã làm (đăng ký); cần xác nhận |
| AC-02 | Ba vai trò cố định (`FixedRoles`): Cooperative Owner, Zone Manager (`ZONE_MANAGER`), Accountant (`ACCOUNTANT`); quyền là ma trận tĩnh, không lưu; Owner cấp cặp vai trò–vùng; cặp không có hiệu lực thì không đăng nhập được | Đã làm |
| AC-04 | Không có actor System Administrator trong `develop` (Q14) | Chờ xác nhận |
| AC-06 | Phiên theo thiết bị, refresh xoay vòng với ân hạn 30 giây, thu hồi phiên qua Redis tùy chọn; không có trong SRS | Đã làm |
| HR-03, HR-04 | Lọc, sắp xếp, xuất và nhập .xlsx nhân viên (chỉ Chủ HTX nhập và xuất); nhập bỏ qua dòng lỗi; mã nhân viên IN HOA, duy nhất cả người đã nghỉ; CCCD không xuất file; cột Mã, Giới tính, CCCD, Ngày bắt đầu | Đã làm trên nhánh `feature/web-hr-api-gaps`, chưa push |
| CL-01 | Chưa có đăng nhập app nhân viên; nhân viên dùng tài khoản liên kết; mã QR `OMTS-ATT` ký HMAC, 60 giây; endpoint `crew-start`, `staff/*`, sửa chấm công (LABOR-04.4, .7, .8) | Đã làm trên nhánh, chưa push |
| CL-02 | Không đẩy thông báo cho nhân viên (MSG-LABOR-05, -15) | Chờ backend (app nhân viên) |
| CL-04 | Khóa kỳ lương bảo thủ (`PayPeriodLock`): mọi ngày trước ngày cuối của bảng lương Chờ chốt hoặc Đã chốt | Đã làm; chờ quyết định Q9 |
| PAY-01 | Bảng lương và thanh toán lương chỉ Chủ HTX (`[RequireCooperativeOwner]`) | Đã làm trên nhánh |
| PAY-03 | Trạng thái Đang tạo (3405), Đã hủy (3406); Tạm tính là Nháp (3401); tạo đồng bộ | Đã làm trên nhánh |
| PAY-05 | Tạm ứng (khoản Advance) trừ vào lần chốt kế tiếp; phiếu chi vào Sổ quỹ | Đã làm trên nhánh; cần xác nhận (Q4) |
| PAY-08 | Thiết lập kỳ lương và cờ tự động tạo bảng lương lưu được nhưng chưa có tác dụng (GFR-07 chưa làm) | Chờ backend |
| FARM-01 | `CageCount` chuyển từ `int` sang `decimal` (migration `20261111140000_HarvestLotFractionalCagesAndOverPlanReason`) | Đang làm (chưa commit) |
| FARM-02, FARM-03 | Không có hao hụt, khối lượng mỗi con, tỷ lệ sống; không có thực thể kế hoạch thu hoạch (`HarvestPlan`) | Chờ quyết định (D3, D4, D5) |
| FARM-04 | Cảnh báo mềm khi vượt 110% mục tiêu chu kỳ (`confirmOverPlan`, `OverPlanReason`, MSG-GEN-04); từ chối khi vượt 150% (`HarvestQuantityRules`) | Đang làm (chưa commit); ngưỡng chờ Q7 |
| FARM-05 | "Thu xong làn": `POST /api/rafts/{raftId}/lanes/{laneNumber}/finish-harvest` | Đang làm (chưa commit) |
| FARM-06 | Quy tắc tọa độ (từ chối 0,0 và hoán đổi; cảnh báo ngoài khung Việt Nam hoặc cách vùng quá 5 km) | Chờ quyết định (D9) |
| STK-01 | Sổ kho: danh mục vật tư và giống, Xuất dùng (`InputIssue`), Xuất hủy, đơn vị và giá trên phiếu nhập; tồn âm bị từ chối (MSG-INPUT-01) | Đã làm (commit `ad3b9db`) |
| STK-02 | Thả giống không đối chiếu với lô giống (GBR-04) | Chờ quyết định (Q8) |
| STK-03 | Phiếu nhập V2 (`PurchaseReceiptsV2`): không có khu vực, chiết khấu, trả trước, ảnh hóa đơn; mã PN000123 | Đã làm |
| STK-04 | Thanh toán nhà cung cấp không gắn phiếu (`SupplierPaymentsV2`, mã TT); trả hàng có mã TH, bỏ cột hoàn tiền (`PurchaseReturnsV2`) | Đã làm |
| STK-06 | Không thông báo cho Kế toán khi có phiếu mới (chỉ Chủ HTX) | Chờ backend |
| SAL-01 | Giới hạn công nợ khách và trường bắt buộc là thiết lập chung của HTX (`CustomerSettingsController`) | Đã làm (commit `b8c11848`) |
| SAL-04 | Thanh toán VietQR khoản không khớp hóa đơn chờ DEBT-02.6 (PR 7b) | Chờ backend |
| SAL-05 | Rút lại nguồn gốc (TRACE-04) chỉ Chủ HTX; MSG-TRACE-05; bản tiếng Việt của trang nguồn gốc là đề xuất | Đã làm; bản tiếng Việt chờ PM (Q15) |
| SAL-06 | Quyền xem khiếu nại: Quản lý vùng trong vùng; Kế toán không xem (PM default) | Chờ xác nhận (Q16) |
| SAL-07 | Sửa hóa đơn (SALE-07), hủy (SALE-08), trả hàng theo khiếu nại (CLAIM-03) chưa có endpoint | Chờ backend |
| WEB-06 | Thông báo lỗi nhập và xuất nhân viên, lỗi file, vượt 5000 dòng chưa có mã MSG (`messageCode` = null) | Chờ quyết định (Q15) |

Ghi chú 12/11/2026: hai thông báo `LANE_DAMAGED`, `LANE_OUT_OF_SERVICE` dùng mã chung `MINI_APP_NOTIFICATION`; chờ SRS có mã message riêng. Chi tiết làn/bè/thu hoạch: `SRS_CHANGE_LOG_HR_2026-11-11.md` mục G.7 trong repo Web.

## Tích hợp chưa có nhà cung cấp

Các phần sau chạy bằng bản giả trong Development và chưa có nhà cung cấp thật: OCR đọc ảnh đồng hồ đo nước (WATER-01), Payment Notification Service (API-12), SMS, email và push. Chi tiết: [operations-reference.md](operations-reference.md#external-services-development).

## Câu hỏi mở phía backend

- Q14: System Administrator và cách cấp.
- Q18: trạng thái email trong module tài khoản trên `develop`.
- Q20: đối chiếu lại HR-03, HR-04, PAY-01 đến PAY-05 sau khi nhánh `feature/web-hr-api-gaps` được push.

Các câu hỏi khác và nội dung đầy đủ nằm trong tài liệu chủ của repo Web.
