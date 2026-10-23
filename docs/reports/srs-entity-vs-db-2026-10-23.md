# Đối chiếu entity SRS với bảng database – OMTS

Ngày lập: 23/10/2026

## 1. Tóm tắt

| | Số lượng |
|---|---|
| Entity trong SRS (mục I.3.2, E01–E52) | **52** |
| Bảng trong database, nhánh `develop` | **59** |
| Khớp tên | 48 |
| Lệch tên (đổi tên khi làm INP) | 2 |
| SRS có, database đã bỏ | 2 |
| Database có, SRS chưa có | 9 |

**Công thức:** 52 entity − 2 entity đã bỏ + 9 bảng mới = **59 bảng**. Hai entity đổi tên vẫn là một bảng mỗi cái, nên không làm thay đổi số lượng.

**Nguyên nhân chính:**

1. **Mô hình dữ liệu trong SRS chưa cập nhật phần INP.** SRS vẫn dùng ERD-FULL v11 (09/10/2026). Change log ngày 22/10 đã thêm các UC INP (phiếu nhập, trả hàng, thanh toán nhà cung cấp) nhưng ghi rõ sơ đồ chưa được cập nhật. Code đã làm INP trong PR #12.
2. **SRS cố ý không mô hình hóa dữ liệu kỹ thuật.** Mục I.3.2 ghi rằng phiên đăng nhập, mã xác minh và các dữ liệu kỹ thuật khác được mô tả ở SDS.
3. **Danh sách khu làm việc của nhân viên chưa có trong entity Employee.** UC LAB-01 có nhắc đến "working zones", nhưng E03 Employee không có thuộc tính này. Code tạo bảng riêng `EmployeeWorkingZones`.

## 2. Nguồn đối chiếu

| Nguồn | Chi tiết |
|---|---|
| SRS | `Downloads\omts-srs-md\md\03-i-overall-requirements.md`, mục 3.1 và 3.2 |
| Database | `src/OMTS.Infrastructure/Persistence/Migrations/OmtsDbContextModelSnapshot.cs`, nhánh `develop` tại commit `48ae1e0` (23/10/2026) |
| Nhánh đang review | `feature/OMTS-211-env`, `feature/OMTS-212-lab` |

**Quy ước:**

- Database đặt tên bảng ở dạng số nhiều (`Account` → `Accounts`). Tôi không tính đây là lệch tên.
- Bảng lịch sử migration `__EFMigrationsHistory` không được tính.
- Báo cáo chỉ so ở mức tên bảng, không so cột.

## 3. Lệch tên (2)

| SRS | Database | Thay đổi |
|---|---|---|
| E13 OysterSeedType | `FarmingInputItems` | Mở rộng từ loại giống thành danh mục vật tư chung, có nhóm (giống, vật tư bè, …) và đơn vị tính |
| E14 SeedSupplier | `Suppliers` | Mở rộng từ nhà cung cấp giống thành nhà cung cấp chung, có loại, chứng nhận, SĐT không trùng |

Nguồn: mô tả commit của PR #12 (`48ae1e0`): "SeedSupplier → Supplier, OysterSeedType → FarmingInputItem".

## 4. SRS có, database đã bỏ (2)

| SRS | Lý do |
|---|---|
| E49 RaftMaterialRecord | Bị thay thế. Chi phí vật tư bè giờ lấy từ phiếu nhập đã hoàn tất (`PurchaseReceipts`) có dòng thuộc nhóm vật tư `RaftMaterial`. |
| E50 RaftMaterialLine | Bị thay thế bởi `PurchaseReceiptLines`. |

## 5. Database có, SRS chưa có (9)

| Bảng | Nhóm | Có cần thêm vào SRS? |
|---|---|---|
| `PurchaseReceipts` | INP – phiếu nhập | **Có** |
| `PurchaseReceiptLines` | INP – dòng phiếu nhập | **Có** |
| `PurchaseReturns` | INP – phiếu trả hàng nhà cung cấp | **Có** |
| `PurchaseReturnLines` | INP – dòng phiếu trả | **Có** |
| `SupplierPayments` | INP – thanh toán nhà cung cấp | **Có** |
| `EmployeeWorkingZones` | Nhân sự – nhân viên làm ở những khu nào | **Có** (thêm entity hoặc thuộc tính trên E03 Employee) |
| `LoginSessions` | Kỹ thuật – phiên đăng nhập | Không, mô tả ở SDS |
| `VerificationCodes` | Kỹ thuật – mã xác minh email/SĐT | Không, mô tả ở SDS |
| `PushDevices` | Kỹ thuật – thiết bị nhận thông báo push | Không, mô tả ở SDS |

## 6. Khớp tên (48)

| Nhóm SRS | Entity → bảng |
|---|---|
| Tổ chức, tài khoản, phân quyền (6) | Cooperative, Account, Employee, Role, RolePermission, AccessGrant |
| Khu nuôi, bè (6/8) | FarmingZone, ZoneComplianceDocument, DocumentZoneScope, Raft, RaftLayoutVersion, Lane |
| Giống, vụ nuôi, đo nước (6/8) | SeedReceiptLot, CultivationCycle, SeedingEvent, LaneSegment, CultivationLogEntry, WaterMeasurement |
| Đơn hàng, bán hàng (6) | Product, Customer, SalesOrder, SaleTransaction, PortWeighing, PortHandover |
| Thu hoạch, truy xuất (4) | HarvestEvent, TraceabilityLot, TracePublicationVersion, TraceCorrection |
| Sau bán hàng (3) | QualityClaim, SaleAdjustment, PhysicalReturn |
| Tài chính khách hàng (4) | CustomerReceipt, ReceiptAllocation, CustomerCredit, CustomerRefund |
| Lao động (8) | Contractor, SeasonalWorker, WorkAssignment, AttendanceRecord, PayRuleVersion, PayLine, LaborSettlement, LaborPayment |
| Hệ thống (5) | Notification, MessageDeliveryAttempt, AuditEvent, Status, StatusTransition |

## 7. Nhánh đang review

| Nhánh | Thay đổi bảng so với `develop` |
|---|---|
| `feature/OMTS-212-lab` (LAB) | Thêm bảng **`Payrolls`** (bảng lương theo kỳ). SRS chưa có entity này. |
| `feature/OMTS-211-env` (ENV) | Không thêm bảng mới, dùng bảng `WaterMeasurements` đã có. |

**Lưu ý khi merge:**

- Hai nhánh này tách ra trước khi PR INP được merge, nên snapshot của chúng vẫn còn 4 bảng cũ: `SeedSuppliers`, `OysterSeedTypes`, `RaftMaterialRecords`, `RaftMaterialLines`.
- Cần cập nhật hai nhánh theo `develop` và tạo lại migration trên nền `develop` trước khi merge. Nếu không, 4 bảng cũ có thể bị tạo lại.
- Sau khi merge nhánh LAB, database sẽ có **60 bảng**.

## 8. Đề xuất cập nhật SRS

1. **Đổi tên** E13 OysterSeedType → FarmingInputItem và E14 SeedSupplier → Supplier. Cập nhật mục đích, thuộc tính và quan hệ theo phạm vi vật tư chung.
2. **Bỏ** E49 RaftMaterialRecord và E50 RaftMaterialLine. Ghi rõ chi phí vật tư bè lấy từ phiếu nhập.
3. **Thêm 5 entity INP:** PurchaseReceipt, PurchaseReceiptLine, PurchaseReturn, PurchaseReturnLine, SupplierPayment.
4. **Thêm khu làm việc của nhân viên:** entity `EmployeeWorkingZone` hoặc thuộc tính trên E03 Employee, cho khớp với mô tả UC LAB-01.
5. **Thêm Payroll** khi nhánh LAB được merge.
6. **Vẽ lại ERD-FULL** (đang là v11, 52 entity, 101 quan hệ) và cập nhật số entity, số quan hệ trong mục 3.1 và 3.2.
7. **Rà các chỗ còn nhắc entity cũ:**
   - OysterSeedType: 6 chỗ ở mục I, 2 chỗ ở mục V.
   - SeedSupplier: 5 chỗ ở mục I.
   - RaftMaterialRecord: 7 chỗ ở mục I, 2 chỗ ở mục V.
   - RaftMaterialLine: 5 chỗ ở mục I, 2 chỗ ở mục V.
   - Các chỗ này gồm bảng phân loại trạng thái ở mục 3.4, quan hệ của E02 Account, và E15 SeedReceiptLot.
8. **Bổ sung nhóm trạng thái** `PURCHASE_RECEIPT` và `SUPPLIER_PAYMENT` cùng các chuyển trạng thái tương ứng vào bảng 3.4. Code đã có các nhóm này theo mô tả commit của PR #12.

Sau khi cập nhật theo các đề xuất 1–6, số entity trong SRS sẽ là 52 − 2 + 5 + 1 + 1 = **57**. Con số này bằng 60 bảng (sau khi merge LAB) trừ 3 bảng kỹ thuật.
