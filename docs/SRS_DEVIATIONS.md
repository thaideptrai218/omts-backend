# Khác biệt phía Backend so với SRS v8 (tóm tắt)

**Cập nhật:** 12/11/2026. Đây chỉ là tóm tắt các mục phía backend. Danh sách đầy đủ, gồm cả phía Web, là tài liệu chủ trong repo Web: `docs/SRS_DEVIATIONS.md` (mã ID bên dưới khớp với tài liệu đó). Phần chi tiết hành vi API nằm trong [api-behaviour-reference.md](api-behaviour-reference.md) và [operations-reference.md](operations-reference.md).

**Trạng thái** lấy theo code trên nhánh `feature/OMTS-132-nursery` ngày 12/11 (gồm farming-rules và raft-lanes đã gộp). "Đang làm" nghĩa là có thay đổi chưa commit.

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
| FARM-01 | `CageCount` chuyển từ `int` sang `decimal` (migration `20261111140000_HarvestLotFractionalCagesAndOverPlanReason`) | Đã commit (`d534d829`) |
| FARM-02, FARM-03 | Không có hao hụt, khối lượng mỗi con, tỷ lệ sống; không có thực thể kế hoạch thu hoạch (`HarvestPlan`) | Chờ quyết định (D3, D4, D5) |
| FARM-04 | Thu hoạch KHÔNG bị chặn bởi mục tiêu sản lượng chu kỳ (quyết định Owner 12/11/2026: kế hoạch chỉ là kế hoạch). Màn thu hoạch trả `targetPercent` và `overTarget`; `confirmOverPlan`/`overPlanReason` của thu hoạch được nhận nhưng bỏ qua. Trần 110%/150% chỉ còn ở kế hoạch giống và vật tư | Đã làm |
| FARM-05 | "Thu xong làn": `POST /api/rafts/{raftId}/lanes/{laneNumber}/finish-harvest`; hoàn tất nhiều làn một lần (`POST /api/rafts/{raftId}/finish-harvest`) | Đã commit (`d534d829`, `ade8a6d1`) |
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

Ghi chú 12/11/2026: ba thông báo `LANE_DAMAGED`, `LANE_OUT_OF_SERVICE`, `NURSERY_RECORDED` dùng mã chung `MINI_APP_NOTIFICATION`; chờ SRS có mã message riêng.
Ươm giống (12/11/2026, chưa có trong SRS, chờ PM; đã commit `01c73cf9`, `7099ae60`): bước bắt buộc trước thả giống mới (NUR-01); đợt ươm theo vùng, một lô giống, kho trừ lúc bắt đầu bằng phiếu Xuất dùng mục đích Nursery (NUR-02, NUR-03, luôn chặn tồn âm); số sống sót ước tính nhập lúc hoàn tất, hao hụt = vào − sống sót (NUR-04); hoàn tất chia cho bè cùng vùng và tạo một chu kỳ mỗi bè trong một giao dịch (NUR-05); đã hoàn tất thì không hủy (NUR-07); thả giống từ phần chia chặn cứng, không phiếu xuất (NUR-08); dữ liệu cũ không backfill (NUR-09); thông báo `NURSERY_RECORDED` (NUR-10); quyền dùng chung `CultivationCycles` (NUR-11); lỗi `MSG-GEN-09` kèm `reasonCode` `NURSERY_*` (NUR-12). Kế hoạch chu kỳ bỏ ngày thu hoạch dự kiến (NUR-14; cột còn trong CSDL, không đọc, không ghi). Ảnh 1-3 đã bắt buộc ở bắt đầu và hoàn tất ươm (PHO-01); ảnh lưu local, cloud bằng cấu hình (PHO-03). % tiến độ từng làn và ảnh các bước (B2, 12/11/2026, chưa có trong SRS, chờ PM): mỗi làn trong một chu kỳ có `seededPercent` và `harvestedPercent`, thả/thu cộng dồn không vượt 100 (400 `LanePercent`), trạng thái làn suy ra từ % (SRS: cả làn), hoàn tất thu hoạch đặt 100, chỉnh tay `PUT /api/rafts/{raftId}/lanes/{n}/progress` ghi lịch sử cũ sang mới và thông báo `LANE_PROGRESS_CHANGED` cho Chủ HTX khi Quản lý vùng chỉnh (dùng mã chung `MINI_APP_NOTIFICATION`); ảnh bắt buộc 1-3 cho thả giống, thu hoạch, kế hoạch nuôi (chu kỳ không từ ươm; chu kỳ từ ươm dùng ảnh hoàn tất ươm) và báo hỏng làn, chỉ với bản ghi mới (bản ghi cũ không ảnh vẫn hợp lệ); ảnh nhật ký chăm sóc tùy chọn, tối đa 3 (SRS CYCLE-04: bắt buộc ít nhất 1). Thả giống, tạo chu kỳ và báo hỏng làn chuyển từ JSON sang `multipart/form-data`. **Q21 (đã chốt 12/11/2026, B3):** chu kỳ mới chỉ được tạo khi **hoàn tất ươm**; `POST /api/rafts/{raftId}/cultivation-cycles` (tạo tay, thả giống chọn lô giống, mức 110%/150% kế hoạch, xuất kho khi thả) trả 400 MSG-GEN-09 `reasonCode` `CYCLE_REQUIRES_NURSERY` ("Chu kỳ mới được tạo khi hoàn tất ươm."). Chu kỳ đang chạy (không có `nurseryBatchId`) vẫn đọc, sửa, thu hoạch, chốt được và vẫn nhận phiên thả giống chọn lô giống theo đường cũ. Cấu hình `Cultivation:AllowManualStart` (mặc định false, chỉ host test của màn chu kỳ cũ bật). Dữ liệu demo: chu kỳ demo vẫn tạo bằng bootstrap (không qua quy tắc), thêm một đợt ươm "Đang ươm" ở vùng 1 (qua dịch vụ thật, không tồn âm) để Chủ HTX hoàn tất thử. Hợp đồng API: `FARMING_NURSERY_API_CONTRACT_2026-11-12.md` trong repo Web. Chi tiết làn/bè/thu hoạch: `SRS_CHANGE_LOG_HR_2026-11-11.md` mục G.7 và G.8 trong repo Web.

## Tích hợp chưa có nhà cung cấp

Các phần sau chạy bằng bản giả trong Development và chưa có nhà cung cấp thật: OCR đọc ảnh đồng hồ đo nước (WATER-01), Payment Notification Service (API-12), SMS, email và push. Chi tiết: [operations-reference.md](operations-reference.md#external-services-development).

## Câu hỏi mở phía backend

- Q14: System Administrator và cách cấp.
- Q18: trạng thái email trong module tài khoản trên `develop`.
- Q20: đối chiếu lại HR-03, HR-04, PAY-01 đến PAY-05 sau khi nhánh `feature/web-hr-api-gaps` được push.
- Q21 (12/11): đường tạo chu kỳ không qua ươm còn mở; có khóa cho bè mới không. Hiện thả giống qua đường đó vẫn có lô giống, 110%/150% và trừ kho.
- Q24 (12/11): thu hoạch bắt buộc gắn yêu cầu mua đã chấp nhận (HARVEST-01, đã có trong code) có đưa vào SRS không.

Các câu hỏi khác và nội dung đầy đủ nằm trong tài liệu chủ của repo Web.
