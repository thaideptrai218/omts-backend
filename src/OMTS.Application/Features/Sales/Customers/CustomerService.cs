using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Customers;

/// <summary>
/// SALE-04 – customer profiles of the Zone Manager and the Owner (API filter): no zone, no discontinue (BR-SALE-04). When
/// staff save a profile no other one may have its phone number; the profiles that registration makes are not checked, and
/// are merged later. The phone number of a linked profile follows the account. <see cref="CreateAsync"/> is also the quick
/// create of SALE-05.1. A walk-in customer has no profile, so nothing here stands for one (BR-SALE-07).
/// </summary>
public sealed class CustomerService(
    ICustomerRepository customers,
    ICooperativeRepository cooperatives,
    CustomerDebtReader debts,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    internal const string ObjectName = "Customer profile";
    internal const string MergedStatus = "Merged";
    internal const string RecordType = nameof(Customer);
    private const int MaxAuditCodeLength = 100;
    private const int MaxPageSize = 100;

    /// <summary>
    /// SALE-04 step 2: by name or phone number. A search that reads as a whole number is also looked up as stored, so
    /// <c>+84…</c>, spaces and dots find it. Longer than any name, it finds nothing.
    /// </summary>
    public async Task<Paged<CustomerListItem>> SearchAsync(string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        // The upper bound keeps (page - 1) * pageSize inside int.
        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var text = DisplayNames.Normalize(search);
        if (text.Length > DisplayNames.MaxLength)
        {
            return new Paged<CustomerListItem>([], page, pageSize, 0);
        }

        var (items, total) = await customers.SearchAsync(text, PhoneNumbers.Normalize(text), page, pageSize, cancellationToken);
        return new Paged<CustomerListItem>(
            items.Select(c => new CustomerListItem(c.Id, c.Name, c.PhoneNumber, c.Address, c.AccountId is not null, RowVersionGuard.Encode(c.RowVersion))).ToList(),
            page, pageSize, total);
    }

    public async Task<Result<CustomerView>> GetAsync(Guid customerId, CancellationToken cancellationToken) =>
        await customers.GetForEditAsync(customerId, cancellationToken) is { } customer
            ? View(customer, await debts.DebtAsync(customer.Id, cancellationToken))
            : AppError.NotFound;

    public async Task<Result<CustomerSaved>> CreateAsync(SaveCustomerRequest request, CancellationToken cancellationToken)
    {
        var (values, invalid) = CustomerEntries.From(request, phoneLocked: false);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await DuplicateAsync(values.PhoneNumber!, null, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        var customer = new Customer { CooperativeId = cooperative.Id };
        Apply(customer, values);
        customers.Add(customer);
        audit.Record(new AuditEntry("Customer.Created", RecordType) { RecordId = customer.Id, RecordCode = AuditCode(customer), After = Snapshot(customer) });
        if (await SaveAsync(values.PhoneNumber!, null, cancellationToken) is { } error)
        {
            return error;
        }

        // A new profile has no invoice or payment yet.
        return Saved(customer, 0m);
    }

    /// <summary>SALE-04.2: invoices stay with the profile; a merged profile is read-only.</summary>
    public async Task<Result<CustomerSaved>> UpdateAsync(Guid customerId, SaveCustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = await customers.GetForEditAsync(customerId, cancellationToken);
        if (customer is null)
        {
            return AppError.NotFound;
        }

        if (customer.MergedIntoCustomerId is not null)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {customer.Name}", MergedStatus));
        }

        // First (NFR-09): a form read before the trader changed the number holds the old one, and is stale, not a change.
        if (await rowVersions.CheckAsync(customer, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var locked = customer.AccountId is not null;
        if (locked && !string.IsNullOrWhiteSpace(request.PhoneNumber) && PhoneNumbers.Normalize(request.PhoneNumber) != customer.PhoneNumber)
        {
            return AppError.Invalid("Phone number", "The phone number of a profile linked to a trader account is changed only by the trader.");
        }

        var (values, invalid) = CustomerEntries.From(request, locked);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (!locked && await DuplicateAsync(values.PhoneNumber!, customer.Id, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        var before = Snapshot(customer);
        Apply(customer, values);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(customer));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Customer.Updated", RecordType) { RecordId = customer.Id, RecordCode = AuditCode(customer), Before = changedBefore, After = changedAfter });
            if (await SaveAsync(values.PhoneNumber, customer.Id, cancellationToken) is { } error)
            {
                return error;
            }
        }

        return Saved(customer, await debts.DebtAsync(customer.Id, cancellationToken));
    }

    /// <summary>MSG-GEN-02 with the id of the profile to open (SALE-04.0.E2).</summary>
    private async Task<AppError?> DuplicateAsync(string phoneNumber, Guid? exceptCustomerId, CancellationToken cancellationToken) =>
        await customers.FindByPhoneAsync(phoneNumber, exceptCustomerId, cancellationToken) is { } same ? AppError.AlreadyExists(ObjectName, same.Id) : null;

    /// <summary>The second of two parallel saves with one number gets the same MSG-GEN-02 as the pre-check.</summary>
    private async Task<AppError?> SaveAsync(string? phoneNumber, Guid? exceptCustomerId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            if (phoneNumber is not null && await DuplicateAsync(phoneNumber, exceptCustomerId, cancellationToken) is { } duplicate)
            {
                return duplicate;
            }

            throw;
        }
    }

    private static void Apply(Customer customer, CustomerEntries values)
    {
        customer.Name = values.Name;
        customer.Address = values.Address;
        customer.Note = values.Note;
        if (values.PhoneNumber is not null)
        {
            customer.PhoneNumber = values.PhoneNumber;
            customer.PhoneCheckedByStaff = true;
        }
    }

    private static CustomerSaved Saved(Customer customer, decimal debt)
    {
        var saved = UserMessages.Saved(ObjectName);
        return new CustomerSaved(View(customer, debt), saved.Text, saved.Code);
    }

    /// <param name="debt">The customer debt now (BR-DEBT-02, <see cref="CustomerDebtReader"/>).</param>
    internal static CustomerView View(Customer c, decimal debt) => new(
        c.Id, c.Name, c.PhoneNumber, c.Email, c.Address, c.Note,
        c.AccountId is not null,
        c.Account is { } account ? new LinkedTraderAccount(account.Id, account.Username, account.DisplayName) : null,
        c.MergedIntoCustomerId,
        debt,
        RowVersionGuard.Encode(c.RowVersion));

    private static object Snapshot(Customer c) => new { c.Name, c.PhoneNumber, c.Address, c.Note };

    // AuditEvent.RecordCode holds 100 characters; the phone number is the business identity of a profile, when it has one.
    internal static string AuditCode(Customer c) => c.PhoneNumber ?? (c.Name.Length <= MaxAuditCodeLength ? c.Name : c.Name[..MaxAuditCodeLength]);
}
