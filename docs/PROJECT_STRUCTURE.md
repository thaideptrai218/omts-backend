# Cấu trúc OMTS Backend

Cập nhật 31/10/2026. Giữ Clean Architecture, chia theo tầng và nghiệp vụ; mỗi kiểu dữ liệu ở một file cùng tên.

```text
omts-backend/
├── OMTS.sln
├── src/
│   ├── OMTS.Domain/
│   │   ├── Common/                     Entity, AuditableEntity, IHasStatus
│   │   ├── Constants/                  Danh mục trạng thái
│   │   ├── Enums/
│   │   └── Entities/<nhóm>/            Identity, Farming, Sales, Labor…
│   ├── OMTS.Application/
│   │   ├── Common/
│   │   │   ├── Interfaces/             Các cổng Persistence, Messaging…
│   │   │   └── Models/                 Dữ liệu trao đổi qua các cổng
│   │   └── Features/<nghiệp-vụ>/
│   │       ├── *Service.cs             Điều phối ca sử dụng
│   │       ├── Dtos/                   Request/response API
│   │       ├── Models/                 Dữ liệu tính toán nội bộ
│   │       └── Options/                Cấu hình riêng khi cần
│   ├── OMTS.Infrastructure/
│   │   ├── Persistence/
│   │   │   ├── Repositories/<nhóm>/    Triển khai truy cập dữ liệu
│   │   │   ├── Configurations/<nhóm>/  EF mapping
│   │   │   ├── Interceptors/
│   │   │   ├── Seed/
│   │   │   └── Migrations/             Do EF sinh
│   │   └── Identity, Messaging, Storage, Ocr, Zalo, Jobs
│   └── OMTS.Api/
│       ├── Controllers/<nghiệp-vụ>/    HTTP endpoints
│       └── Filters, Extensions, Services, Common
├── tests/                              Unit/integration theo nghiệp vụ
├── docs/
├── .github/
└── Dockerfile, docker-compose.yml      Cấu hình công cụ ở gốc
```

Controller chia thành `Identity`, `Organization`, `Farming`, `WaterQuality`, `Purchasing`, `Sales`, `Workforce`, `Notifications`, `Files`, `Development`.

`Features/Sales/Requests` xử lý yêu cầu mua theo lồng; `Features/Sales/Products` quản lý sản phẩm. Mỗi phần có Dtos. Workforce, Employees, Cooperatives và các nghiệp vụ khác cũng tách request/response khỏi service.

Interface ở `Common/Interfaces`; bản ghi dữ liệu trao đổi qua interface ở `Common/Models/<nhóm>`. Ví dụ `IPurchaseReceiptRepository` là cổng, `PurchaseReceiptFilter` và các số liệu tổng hợp là model.

- Namespace khớp thư mục; mỗi class/interface/record/enum ở file riêng. Partial class có thể chia file cùng kiểu/namespace khi truy vấn lớn.
- API gọi Application; Application chỉ dùng Domain và abstraction; Infrastructure triển khai interface. Không đưa EF vào API/Application.
- Không tạo repository/service rỗng để đủ sơ đồ. Cấu hình build/runtime giữ tại vị trí công cụ đọc.
- Đổi folder không đổi route HTTP, JSON nghiệp vụ, tên entity đầy đủ hay bảng DB. Tách entity sang file cùng namespace không cần migration mới.
- Giữ các migration đã sinh; chỉ sinh mới khi model DB đổi.
- `.env`, user-secrets, token, log, bin, obj không đưa vào commit.
- Không dừng API đang chạy để build; dùng BaseOutputPath khác nếu file đang bị khóa.

Kiểm tra bằng `dotnet build`, `dotnet test` và `dotnet ef migrations has-pending-model-changes` theo `CLAUDE.md`.
