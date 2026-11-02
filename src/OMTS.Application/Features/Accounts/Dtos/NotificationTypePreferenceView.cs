namespace OMTS.Application.Features.Accounts.Dtos;

public sealed record NotificationTypePreferenceView(string Type, string Name, bool Mandatory, bool Push);
