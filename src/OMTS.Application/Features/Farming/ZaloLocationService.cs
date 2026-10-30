using OMTS.Application.Features.Farming.Dtos;
using OMTS.Application.Features.Farming.Models;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Farming;

/// <summary>FARM-04 coordinate input: resolve only the linked caller's current position, without saving a raft.</summary>
public sealed class ZaloLocationService(IZaloOpenApi zalo, IAccountRepository accounts)
{
    public async Task<Result<LocationCoordinates>> ResolveAsync(Guid accountId, ZaloLocationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ZaloAccessToken) || string.IsNullOrWhiteSpace(request.LocationToken)
            || request.ZaloAccessToken.Length > 4096 || request.LocationToken.Length > 4096)
            return AppError.Invalid("Location token");

        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account?.ZaloAccountId is null) return AppError.Forbidden;
        var identity = await zalo.GetUserIdAsync(request.ZaloAccessToken, cancellationToken);
        if (identity.Outcome == ZaloLookupOutcome.Unavailable)
            return new AppError(ErrorKind.ServiceUnavailable, UserMessages.ServiceNotResponding("Zalo"));
        if (identity.Outcome != ZaloLookupOutcome.Found || identity.Value != account.ZaloAccountId)
            return AppError.Forbidden;

        var location = await zalo.GetLocationAsync(request.ZaloAccessToken, request.LocationToken, cancellationToken);
        return location.Outcome switch
        {
            ZaloLookupOutcome.Found when location.Latitude is >= -90 and <= 90 && location.Longitude is >= -180 and <= 180 =>
                new LocationCoordinates(location.Latitude.Value, location.Longitude.Value),
            ZaloLookupOutcome.Unavailable => new AppError(ErrorKind.ServiceUnavailable, UserMessages.ServiceNotResponding("Zalo")),
            _ => AppError.Invalid("Location token"),
        };
    }
}
