# Xác nhận nhân lực trước khi nhận yêu cầu mua

PR review ngày 03/11/2026: nhánh `develop` đã xóa các bảng scaffold cũ, gồm `LaborSettlements` mà nhánh này còn dùng để kiểm tra khóa kỳ công. Thử ghép trong worktree riêng phát hiện xung đột DTO, snapshot và thiếu entity/status trong `WorkforceRepository`; đã hủy thử ghép, giữ nguyên source đã kiểm thử. PR backend cần đồng bộ schema trước khi merge. Chưa áp dụng migration xóa bảng của develop vào DB dev, chưa bỏ kiểm tra khóa kỳ công. Mobile đã ghép bộ khung Zalo từ develop và tiếp nhận `legacyCode` để tương thích mã lỗi SRS mới.

Cập nhật ngày 06/11/2026: Chủ HTX phải lập [kế hoạch ngày làm](work-day-plans.md) và chọn tự phụ trách hoặc giao Manager. Manager nhận/từ chối trước; chỉ người phụ trách đã nhận kế hoạch được mời nhân viên. Các quy tắc nhân lực và quyết định cuối của Chủ HTX bên dưới vẫn áp dụng. Thông tin triển khai ở cuối tài liệu là ghi nhận lịch sử ngày 03/11, không phải xác nhận process hiện đang chạy.

Quyết định trực tiếp của chủ dự án ngày 31/10/2026 bổ sung bước cho SALE-03: thương lái gửi yêu cầu theo lồng → Chủ HTX mời nhân viên xác nhận ca → nhân viên đồng ý hoặc từ chối → Chủ HTX quyết định nhận/từ chối đơn. Không tự động nhận hoặc từ chối đơn khi có phản hồi.

## Quy tắc

- Chủ HTX lập kế hoạch số lồng dự kiến nhận, ngày làm, ngày nhận hàng, năng suất lồng/người/ngày và người phụ trách. Người phụ trách đã nhận kế hoạch chọn danh sách nhân viên. Mobile mặc định năng suất 20; đây là giá trị có thể chỉnh, không phải hằng số backend.
- Số người cần = làm tròn lên(số lồng kế hoạch / năng suất). 100 lồng, năng suất 20: cần ít nhất 5 người đồng ý ca.
- Tạo kế hoạch ngày làm chuyển đơn từ `AWAITING_RESPONSE` sang `AWAITING_STAFF`; sau đó người phụ trách gửi lời mời. Chưa đủ người thì API nhận đơn trả lỗi, kể cả khi bỏ qua giao diện.
- Giữ nguyên ca đã gửi để tránh thay nội dung nhân viên đã đọc/xác nhận. Có thể mời thêm người bằng cùng kế hoạch, không gửi trùng cho người đã được mời.
- Nhân viên chỉ trả lời lời mời của hồ sơ gắn với tài khoản đang gọi. Từ chối ca phải có lý do. Phản hồi cùng nội dung được thử lại mà không ghi/gửi thông báo lần nữa.
- Một nhân viên chỉ giữ một đơn trong một ngày. Chỉ mục duy nhất `(EmployeeId, ReservedWorkDate)` lọc ngày khác null chặn cả hai thao tác đồng thời. Pending invitations không giữ ca.
- Nghỉ phép đã duyệt, phân công khác trong ngày, hồ sơ nghỉ việc hoặc tài khoản không hoạt động không được tính là khả dụng; kiểm tra khi mời, khi đồng ý và trước khi nhận đơn.
- Số lồng và ngày nhận cuối cùng phải khớp kế hoạch đã gửi; ngày làm không được qua hạn. Chủ HTX quyết định sau khi đủ người, không tự động tạo đơn bán/cân hàng/hóa đơn.
- Từ chối đơn bắt buộc có lý do gửi cho thương lái. Hủy/từ chối giải phóng các ngày đang giữ và thông báo nhân viên; không xóa lịch sử phản hồi.
- `rowVersion` của parent đổi theo phản hồi nhân viên. Phản hồi, quyết định nhận, hủy cùng tranh chấp parent; SQL rollback toàn bộ thao tác khi xung đột. Lời mời cũng có rowVersion riêng.
- Dữ liệu trả cho thương lái chỉ có tổng tiến độ nhân lực; danh sách tên và lý do riêng của nhân viên chỉ Chủ HTX và Manager phụ trách đủ quyền vùng được xem.

## API

| Endpoint | Quyền / dữ liệu |
| --- | --- |
| `GET /api/purchase-requests/staffing-employees?day=YYYY-MM-DD` | Chủ HTX; thêm `purchaseRequestId` nếu gọi bởi Manager phụ trách đã nhận kế hoạch |
| `POST /api/purchase-requests/{id}/staffing` | Người phụ trách đã nhận kế hoạch; `cages`, `workDate`, `pickupDate`, `cagesPerEmployeePerDay`, `employeeIds`, `note`, `rowVersion` khớp kế hoạch |
| `GET /api/purchase-requests/{id}/staffing` | Chủ HTX hoặc Manager phụ trách; từng lời mời và phản hồi |
| `GET /api/me/purchase-shifts?from=YYYY-MM-DD&to=YYYY-MM-DD` | Nhân viên; lời mời của mình, tối đa 63 ngày lịch |
| `POST /api/me/purchase-shifts/{id}/response` | Nhân viên được mời; `approved`, `reason`, `rowVersion` |
| `POST /api/purchase-requests/{id}/response` | Chủ HTX; API cũ bổ sung điều kiện đủ nhân lực |

`PurchaseRequestView.staffing` là null trước khi gửi lời mời, sau đó chứa kế hoạch và các số đếm. Lọc danh sách bổ sung `AWAITING_STAFF`. Bốn loại thông báo mới là `PURCHASE_SHIFT_INVITED`, `PURCHASE_SHIFT_RESPONDED`, `PURCHASE_SHIFT_CONFIRMED`, `PURCHASE_SHIFT_CLOSED`, đều thuộc nhóm công việc; đây là thông báo in-app hiện có.

## Kiểm tra và triển khai

- Toàn bộ suite: 898 đạt, 105 bỏ qua do dịch vụ ngoài. Sau thay đổi cuối ở fixture, chạy riêng Sales với SQL được cấu hình: 19/19 đạt, gồm migration trên DB thử tách biệt, unique index chống trùng và rowversion SQL thật. DB thử được dọn bởi fixture.
- Mobile lint/typecheck/build đạt. Trình duyệt dùng API fixtures đi qua gửi 100 lồng → mời 5 người → 4/5 bị chặn → đủ 5/5 → Owner nhận → Trader đọc kết quả; nhánh từ chối đơn bắt buộc lý do. Chưa xác nhận trên điện thoại thật.
- Migration sinh bằng EF: `20261030203817_AddPurchaseStaffing`, đã áp dụng DB dev. EF báo không còn pending model changes.
- Backend phục vụ Mini App vẫn là bản cũ: khởi chạy bản mới ở cổng 5204 bị bộ duyệt tự động từ chối (`blocked by policy`). Chưa đổi proxy; không dừng backend, tunnel hoặc Device đang có. Cần chạy API mới và chuyển đường API trước khi thử luồng này trên Mini App.
- Kiểm tra sau migration thấy readiness của API cũ báo `status-catalog` vì bản cũ yêu cầu transition 77. Đã khôi phục riêng row 77 trên DB dev để tương thích các process cũ (không sửa đơn, lời mời hoặc dữ liệu nghiệp vụ); readiness local/public đã về 200. DB dev tạm giữ row tương thích ngoài seed mới. Chỉ dọn row này sau khi tất cả process API cũ không còn được dùng. Logic nhận đơn của source mới chỉ cho phép sau khi đủ xác nhận nhân lực, không dùng transition 77 để bỏ qua điều kiện.

Các đơn cũ đã chấp nhận được giữ nguyên. Bản API mới bắt buộc xác nhận nhân lực cho quyết định nhận mới; cập nhật DB đơn thuần chưa thay logic của các process API cũ.
