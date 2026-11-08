using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>Customer payments as the staff see them, with the names of the people they name read in one query.</summary>
public sealed class CustomerPaymentViews(IAccountRepository accounts, IStatusCatalog statusCatalog)
{
    public async Task<CustomerPaymentDetails> ViewAsync(CustomerPayment payment, CancellationToken cancellationToken) =>
        (await ViewAsync([payment], cancellationToken))[0];

    /// <summary>The answer of a save, with MSG-GEN-05 ("Payment PT000123 saved.", "Cancellation of money paid back PT000124 saved.").</summary>
    public async Task<CustomerPaymentSaved> SavedAsync(CustomerPayment payment, decimal? debtAfter, bool cancelled, CancellationToken cancellationToken)
    {
        var what = payment.Kind == CustomerPaymentKind.PaidBack ? "money paid back" : "payment";
        var message = UserMessages.Saved(cancelled ? $"Cancellation of {what} {payment.Code}" : $"{char.ToUpperInvariant(what[0])}{what[1..]} {payment.Code}");
        return new CustomerPaymentSaved(await ViewAsync(payment, cancellationToken), debtAfter, message.Text, message.Code);
    }

    public async Task<IReadOnlyList<CustomerPaymentDetails>> ViewAsync(IReadOnlyList<CustomerPayment> payments, CancellationToken cancellationToken)
    {
        var ids = payments
            .SelectMany(p => new[] { p.CollectedByAccountId, p.RecordedByAccountId, p.ConfirmedByAccountId, p.CancelledByAccountId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        IReadOnlyDictionary<Guid, string> names = ids.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(ids, cancellationToken);
        AccountRef? Ref(Guid? id) => id is { } value ? new AccountRef(value, names.GetValueOrDefault(value) ?? string.Empty) : null;
        return payments.Select(p => new CustomerPaymentDetails(
            p.Id, p.Code, p.Customer is { } c ? new CustomerDebtCustomer(c.Id, c.Name, c.PhoneNumber) : null, p.Kind, p.Amount, p.PaymentDate, p.Method,
            p.BankReference, p.Invoice is { } i ? new CustomerPaymentInvoiceRef(i.Id, i.Code) : null, Ref(p.CollectedByAccountId), Ref(p.RecordedByAccountId), p.Note,
            p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), Ref(p.ConfirmedByAccountId), p.ConfirmedAt, Ref(p.CancelledByAccountId), p.CancelledAt, p.CancelReason,
            RowVersionGuard.Encode(p.RowVersion), p.TransferInvoiceCode, p.TransferredAt)).ToList();
    }
}
