# Kế hoạch ngày làm và người phụ trách

Yêu cầu ngày 06/11/2026: Chủ HTX tạo kế hoạch thu hoạch, sửa bè hoặc công việc khác, chọn tự phụ trách hoặc giao một Manager. Manager phải đồng ý hoặc từ chối nhận kế hoạch trước khi phân công. Từ chối bắt buộc có lý do. Manager trong API là tài khoản Staff đang có quyền xem và tạo phân công trong vùng của bè; không thêm vai trò giả hoặc bỏ qua phân quyền vùng.

## Luồng và quyền

- Chủ HTX chọn bè, loại việc, ngày làm và người phụ trách. Tự phụ trách thì kế hoạch được nhận ngay. Giao Manager thì chờ phản hồi.
- Chỉ người phụ trách đã nhận kế hoạch mới được phân công hoặc gửi lời mời ca. Chủ HTX xem được tiến độ nhưng không phân công thay Manager qua API khác.
- Công việc độc lập tạo phân công thật để nhân viên xem lịch cá nhân. Kế hoạch gắn yêu cầu mua gửi lời mời trước; nhân viên phản hồi, Chủ HTX quyết định nhận đơn khi đủ người rồi API tạo phân công thật.
- Năng suất và số lồng vẫn lấy từ kế hoạch mua, ví dụ 100 lồng với 20 lồng/người/ngày cần 5 người đồng ý. Phản hồi ca gửi thông báo in-app cho Chủ HTX và Manager phụ trách, không tự quyết định nhận đơn.
- Chủ HTX chỉ sửa hoặc giao lại khi chưa có phân công đang hiệu lực và chưa gửi lời mời. Phản hồi cũ vẫn được lưu trong audit. Nhân viên khác hoặc Manager khác không xem hay nhận thay kế hoạch.
- Nhận đơn kiểm tra lại nhân viên khả dụng, quyền hiện tại của người phụ trách và trạng thái hoạt động của bè, vùng, loại việc. Hủy/từ chối giải phóng ca và hủy phân công chưa chấm công; không hủy đơn có phân công đã chấm công.

Trạng thái trả về được suy ra từ kế hoạch, đơn và phân công: `PENDING_MANAGER`, `DECLINED`, `READY_TO_ASSIGN`, `AWAITING_STAFF`, `ASSIGNED`, `CLOSED`. Không thêm hàng trạng thái vào status catalog.

## API

| Endpoint | Người gọi / dữ liệu |
| --- | --- |
| `GET /api/work-plans?from=YYYY-MM-DD&to=YYYY-MM-DD` | Chủ HTX xem tất cả, Manager xem kế hoạch của mình; tối đa 63 ngày lịch |
| `GET /api/work-plans/{id}` | Chủ HTX hoặc Manager phụ trách có quyền trong vùng |
| `GET /api/work-plans/purchase/{id}` | Kế hoạch của yêu cầu mua; trả JSON `null` cho Chủ HTX nếu chưa lập |
| `GET /api/work-plans/responsibles?raftId={id}` | Chủ HTX; chính mình và Staff đủ quyền xem/tạo phân công trong vùng |
| `POST /api/work-plans` | Chủ HTX tạo kế hoạch |
| `PUT /api/work-plans/{id}` | Chủ HTX sửa/giao lại; cần `rowVersion` |
| `POST /api/work-plans/{id}/response` | Manager phụ trách; `approved`, `reason`, `rowVersion` |
| `GET /api/work-plans/{id}/employees` | Người phụ trách đã nhận kế hoạch; danh sách nhân viên và lý do không khả dụng |
| `POST /api/work-plans/{id}/assignments` | Người phụ trách đã nhận; `employeeIds`, `rowVersion` |

Tạo/sửa nhận `raftId`, `jobTypeId`, `workDate`, `responsibleAccountId`, `clientOperationId`, `note`. Với yêu cầu mua, thêm `purchaseRequestId`, `cages`, `cagesPerEmployeePerDay`, `pickupDate`. Gửi lại cùng `clientOperationId` và nội dung trả kế hoạch đã tạo; dùng lại mã với nội dung khác bị từ chối. Mỗi yêu cầu mua chỉ có một kế hoạch.

API phân công cũ `/api/workforce/assignments` yêu cầu `workDayPlanId` và kiểm tra cùng người phụ trách, bè, loại việc, ngày làm. API staffing cũ của yêu cầu mua cũng yêu cầu kế hoạch đã được nhận. Trường `workDayPlanId` của phân công cũ trong DB được giữ nullable để đọc lịch sử.

## Dữ liệu và kiểm tra

Migration `20261106090714_AddWorkDayPlans` thêm bảng kế hoạch, liên kết nullable vào `WorkAssignments`, chỉ mục chống trùng thao tác/yêu cầu mua và constraint kiểm tra phản hồi có ngày, lý do từ chối. Migration này chưa áp dụng lên DB dev trong thay đổi này. Cần đồng bộ migration/snapshot với schema `develop` trước khi merge và triển khai.

Kiểm thử API bao gồm tự phụ trách, Manager nhận/từ chối, giao lại, phân quyền vùng, chống phân công thay và toàn bộ luồng yêu cầu mua đến phân công thực tế. Kiểm thử SQL dùng DB tạm riêng, áp dụng migration thật và kiểm tra constraint/rowversion. Chưa xác nhận luồng mới trên điện thoại và chưa triển khai backend từ commit này.

Yêu cầu mua cũ đã có lời mời nhưng chưa có kế hoạch không được nhận qua luồng mới. Cần xử lý chuyển dữ liệu hoặc đóng và lập lại theo nghiệp vụ trước khi nâng cấp; thay đổi này không tự chọn bè/loại việc/người phụ trách cho dữ liệu cũ.

Thông báo mới `WORK_PLAN_REQUESTED`, `WORK_PLAN_RESPONDED`, `WORK_PLAN_STAFF_RESPONDED` thuộc nhóm công việc, liên kết về kế hoạch bằng ID. Đây là thông báo in-app; không bổ sung push hệ điều hành trong thay đổi này.
