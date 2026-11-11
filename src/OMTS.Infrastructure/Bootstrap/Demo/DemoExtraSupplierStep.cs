using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Purchasing.Suppliers;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// Sixty more suppliers so paging, sorting and scrolling of the supplier list can be tried. Created through the supplier
/// service, so their NCC codes come from the normal sequence. Each is matched by its own phone number, so a database that
/// already holds the first five demo suppliers still gets these. Every sixth one is discontinued and the creation dates
/// are spread over a year.
/// </summary>
internal sealed class DemoExtraSupplierStep : IDemoSeedStep
{
    public const int Total = 60;

    // Only the place names are real: business names, phones (0900 0xx xxx), tax codes (99…) and emails (example.com) are made up.
    private static readonly (string Place, string Ward, string Province)[] Places =
    [
        ("Vân Đồn", "Xã Hạ Long", "Quảng Ninh"), ("Cẩm Phả", "Phường Cửa Ông", "Quảng Ninh"), ("Móng Cái", "Phường Trà Cổ", "Quảng Ninh"),
        ("Tiên Yên", "Xã Đông Ngũ", "Quảng Ninh"), ("Hạ Long", "Phường Bãi Cháy", "Quảng Ninh"), ("Cô Tô", "Xã Đồng Tiến", "Quảng Ninh"),
        ("Quảng Yên", "Phường Hà An", "Quảng Ninh"), ("Đầm Hà", "Xã Đại Bình", "Quảng Ninh"), ("Cát Bà", "Xã Trân Châu", "Hải Phòng"),
        ("Đồ Sơn", "Phường Ngọc Xuyên", "Hải Phòng"), ("Thủy Nguyên", "Xã Lập Lễ", "Hải Phòng"), ("Giao Thủy", "Xã Giao Xuân", "Nam Định"),
        ("Sầm Sơn", "Phường Trung Sơn", "Thanh Hóa"), ("Cửa Lò", "Phường Nghi Thu", "Nghệ An"), ("Cam Ranh", "Xã Cam Bình", "Khánh Hòa"),
        ("Nha Trang", "Phường Vĩnh Nguyên", "Khánh Hòa"), ("Vạn Ninh", "Xã Vạn Thạnh", "Khánh Hòa"), ("Sông Cầu", "Xã Xuân Phương", "Phú Yên"),
        ("Phú Quốc", "Xã Hàm Ninh", "Kiên Giang"), ("Long Hải", "Xã Phước Tỉnh", "Bà Rịa - Vũng Tàu"),
    ];

    private static readonly (SupplierType Type, string Label, string Company)[] Kinds =
    [
        (SupplierType.Seed, "Trại giống hàu", "Giống thủy sản"),
        (SupplierType.Material, "Cửa hàng vật tư nuôi biển", "Vật tư ngư nghiệp"),
        (SupplierType.Other, "Đại lý tổng hợp", "Thương mại dịch vụ"),
    ];

    public string Name => "extra suppliers";

    /// <summary>The phone number of the supplier at <paramref name="index"/>, also how the demo receipts refer to it.</summary>
    public static string PhoneAt(int index) => $"09000{10137 + (index * 137)}";

    private static bool IsDiscontinued(int index) => index % 6 == 5;

    private static SaveSupplierRequest RequestAt(int index)
    {
        var (place, ward, province) = Places[index % Places.Length];
        var (type, label, company) = Kinds[index % Kinds.Length];
        var hasCompany = index % 4 == 0;
        return new SaveSupplierRequest(
            $"{label} {place}", type, PhoneAt(index),
            $"Số {(index * 7 % 180) + 1}, {ward}, {place}, {province}",
            index % 5 is 0 or 2 ? $"ncc{index + 1:00}@example.com" : null,
            Province: province, Ward: ward,
            CompanyName: hasCompany ? $"Công ty TNHH {company} {place}" : null,
            TaxCode: hasCompany ? $"99{index + 1:00000000}" : null);
    }

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<SupplierService>();
        var created = 0;
        for (var index = 0; index < Total; index++)
        {
            var phone = PhoneNumbers.Normalize(PhoneAt(index));
            if (await context.Db.Suppliers.AnyAsync(s => s.PhoneNumber == phone, cancellationToken))
            {
                continue;
            }

            var request = RequestAt(index);
            var supplier = DemoSeedContext.Ensure(await service.CreateAsync(request, cancellationToken), $"supplier {request.Name}");
            if (IsDiscontinued(index))
            {
                DemoSeedContext.Ensure(await service.DiscontinueAsync(supplier.Id, cancellationToken), $"discontinuing {request.Name}");
            }

            // The service stamps today; the list needs a spread to sort and filter by creation date. The stamp interceptor
            // keeps CreatedAt on a normal save, so this is a bulk update, which only a relational provider runs (the
            // in-memory test database keeps today's date).
            if (context.Db.Database.IsRelational())
            {
                var createdAt = context.Now.AddDays(-(7 + (index * 6)));
                await context.Db.Suppliers.Where(s => s.Id == supplier.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, createdAt), cancellationToken);
            }

            created++;
        }

        return created;
    }
}
