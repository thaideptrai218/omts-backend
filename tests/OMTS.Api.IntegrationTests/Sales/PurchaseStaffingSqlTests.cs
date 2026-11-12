using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Api.IntegrationTests.Sales;

public sealed class PurchaseStaffingSqlTests
{
    [SqlServerFact]
    public async Task Migrations_apply_and_database_prevents_double_booking_and_stale_parent_updates()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var code = Guid.NewGuid().ToString("N");
        var cooperative = new Cooperative { FullName = "Staffing SQL check" };
        var employee = new Employee { Cooperative = cooperative, FullName = "Staffing SQL check" };
        var customer = new Customer { Cooperative = cooperative, Name = "Staffing SQL check" };
        var product = new Product { Code = code, Name = "Staffing SQL check" };
        var day = new DateOnly(2030, 1, 2);
        var first = new PurchaseRequest { Code = code + "A", Customer = customer, Product = product, ProductName = product.Name,
            RequestedCages = 100, PickupDate = day, PlannedCages = 100, WorkDate = day, PlannedPickupDate = day,
            CagesPerEmployeePerDay = 20, ClientOperationId = Guid.NewGuid() };
        var second = new PurchaseRequest { Code = code + "B", Customer = customer, Product = product, ProductName = product.Name,
            RequestedCages = 100, PickupDate = day, ClientOperationId = Guid.NewGuid() };
        var a = new PurchaseShiftInvitation { PurchaseRequest = first, Employee = employee };
        var b = new PurchaseShiftInvitation { PurchaseRequest = second, Employee = employee };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // Hai scope đã đọc lúc chưa ai giữ ca: unique index phải chặn lần ghi thứ hai.
        await using (var one = database.CreateContext())
        await using (var two = database.CreateContext())
        {
            var left = await one.Set<PurchaseShiftInvitation>().SingleAsync(i => i.Id == a.Id);
            var right = await two.Set<PurchaseShiftInvitation>().SingleAsync(i => i.Id == b.Id);
            left.Approved = right.Approved = true;
            left.ReservedWorkDate = right.ReservedWorkDate = day;
            left.RespondedAt = right.RespondedAt = DateTimeOffset.UtcNow;
            await one.SaveChangesAsync();
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => two.SaveChangesAsync());
            Assert.True(error.InnerException is SqlException { Number: 2601 or 2627 });
        }
        // Phản hồi nhân viên và quyết định Owner cùng sửa parent, dùng rowversion SQL thực.
        await using (var one = database.CreateContext())
        await using (var two = database.CreateContext())
        {
            var left = await one.Set<PurchaseRequest>().SingleAsync(r => r.Id == first.Id);
            var right = await two.Set<PurchaseRequest>().SingleAsync(r => r.Id == first.Id);
            left.StaffingNote = "Thay đổi đã lưu"; right.StaffingNote = "Bản đọc cũ";
            await one.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => two.SaveChangesAsync());
        }
    }
}
