namespace OMTS.Application.Features.Auth.Dtos;

/// <param name="PauseLeft">With <see cref="AuthError.TemporarilyLocked"/>: what is left of the pause, {retry_time} of MSG-ACCOUNT-06.</param>
/// <param name="RefreshReason">Set on every refused refresh; login failures have none.</param>
public sealed record AuthResult(AuthTokens? Tokens, AuthError? Error, TimeSpan? PauseLeft = null, RefreshFailureReason? RefreshReason = null)
{
    public static AuthResult Success(AuthTokens tokens) => new(tokens, null);
    public static AuthResult Failure(AuthError error) => new(null, error);
    public static AuthResult Paused(TimeSpan pauseLeft) => new(null, AuthError.TemporarilyLocked, pauseLeft);
    public static AuthResult RefreshRefused(AuthError error, RefreshFailureReason reason) => new(null, error, null, reason);
}
