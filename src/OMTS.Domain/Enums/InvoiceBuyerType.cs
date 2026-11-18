namespace OMTS.Domain.Enums;

/// <summary>
/// Who the e-invoice is made out to. Organization: a company or other body (name, address, tax code and email on the invoice).
/// Individual: a person (name and address, the personal tax number and email when given). Consumer: nothing is given and the invoice reads
/// "Người mua không cung cấp thông tin"; only a customer can be one.
/// </summary>
public enum InvoiceBuyerType
{
    Organization,
    Individual,
    Consumer,
}
