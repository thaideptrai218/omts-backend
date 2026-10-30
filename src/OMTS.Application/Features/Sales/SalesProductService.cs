using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales;

/// <summary>Minimal real product catalogue used by SALE-01; only the Owner writes it.</summary>
public sealed class SalesProductService(ISalesProductRepository products, IAccountRepository accounts, ICurrentUser user,
    RowVersionGuard versions, AuditLogService audit, IUnitOfWork uow, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).DateTime);
    public async Task<Result<IReadOnlyList<SalesProductView>>> ListAsync(bool includeUnavailable, CancellationToken ct)
    {
        var account = user.AccountId is { } id ? await accounts.GetByIdAsync(id, ct) : null;
        if (account?.SubjectType is not (SubjectType.Trader or SubjectType.CooperativeOwner)) return AppError.Forbidden;
        if (includeUnavailable && account.SubjectType != SubjectType.CooperativeOwner) return AppError.Forbidden;
        return Result<IReadOnlyList<SalesProductView>>.Success((await products.ListAsync(!includeUnavailable, Today, ct)).Select(View).ToList());
    }
    public async Task<Result<SalesProductView>> SaveAsync(Guid? id, SaveSalesProduct input, CancellationToken ct)
    {
        var account = user.AccountId is { } caller ? await accounts.GetByIdAsync(caller, ct) : null;
        if (account?.SubjectType != SubjectType.CooperativeOwner) return AppError.Forbidden;
        if (string.IsNullOrWhiteSpace(input.Code) || input.Code.Length > 50) return AppError.Invalid("Code");
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200) return AppError.Invalid("Name");
        if (input.Description?.Length > 2000) return AppError.Invalid("Description");
        var code = input.Code.Trim().ToUpperInvariant();
        if (await products.FindCodeAsync(code, id, ct) is { } duplicate) return AppError.AlreadyExists("Product", duplicate.Id);
        var row = id is { } value ? await products.GetAsync(value, ct) : new Product();
        if (row is null) return AppError.NotFound;
        if (id is not null && await versions.CheckAsync(row, input.RowVersion, ct) is { } stale) return stale;
        var before = new { row.Code, row.Name, row.Description, row.StatusId };
        row.Code = code; row.Name = input.Name.Trim(); row.Description = input.Description?.Trim();
        row.StatusId = input.InUse ? StatusIds.Usage.InUse : StatusIds.Usage.Discontinued;
        if (id is null) products.Add(row);
        audit.Record(new AuditEntry(id is null ? "Product.Created" : "Product.Updated", nameof(Product))
        { RecordId = row.Id, RecordCode = row.Code, Before = id is null ? null : before,
            After = new { row.Code, row.Name, row.Description, row.StatusId } });
        await uow.SaveChangesAsync(ct);
        return View(row);
    }
    public static bool Available(Product p, DateOnly today) =>
        p.StatusId == StatusIds.Usage.InUse && (p.ValidFrom is null || p.ValidFrom <= today) && (p.ValidTo is null || p.ValidTo >= today);
    private SalesProductView View(Product p) => new(p.Id, p.Code, p.Name, p.Description,
        p.StatusId == StatusIds.Usage.InUse, Available(p, Today), RowVersionGuard.Encode(p.RowVersion));
}
