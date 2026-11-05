using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Sales.Customers;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// SALE-04: offline customer profiles (phones 0900 000 041…045), besides the two that <see cref="DemoTraderStep"/> links to
/// trader accounts. The last two are the same buyer entered twice, ready to be merged. Matched by phone number, merged
/// profiles included, or by the Customer.Created audit row of a number changed since, so a rerun neither repeats them nor
/// undoes a merge.
/// </summary>
internal sealed class DemoCustomerStep : IDemoSeedStep
{
    private static readonly (string Name, string Phone, string Address, string? Note)[] Customers =
    [
        ("Vựa hải sản Bà Lan", "0900000041", "Chợ Hạ Long 1, TP Hạ Long, Quảng Ninh", "Lấy hàng sáng thứ Bảy"),
        ("Nhà hàng Biển Xanh", "0900000042", "Đường Bến Đoan, TP Hạ Long, Quảng Ninh", null),
        ("Anh Tùng thương lái Cẩm Phả", "0900000043", "Phường Cẩm Đông, TP Cẩm Phả, Quảng Ninh", null),
        ("Chị Hoa Cái Rồng", "0900000044", "Thị trấn Cái Rồng, Vân Đồn, Quảng Ninh", "Số chính của chị Hoa"),
        ("Hoa Cái Rồng (số cũ)", "0900000045", "Thị trấn Cái Rồng, Vân Đồn, Quảng Ninh", "Trùng với hồ sơ chị Hoa, chờ gộp"),
    ];

    public string Name => "customer profiles";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<CustomerService>();
        var created = 0;
        foreach (var (name, phone, address, note) in Customers)
        {
            if (await context.Db.Customers.AnyAsync(c => c.PhoneNumber == phone, cancellationToken)
                || await context.Db.AuditEvents.AnyAsync(e => e.Action == "Customer.Created" && e.RecordCode == phone, cancellationToken))
            {
                continue;
            }

            DemoSeedContext.Ensure(await service.CreateAsync(new SaveCustomerRequest(name, phone, address, note), cancellationToken), $"customer {name}");
            created++;
        }

        return created;
    }
}
